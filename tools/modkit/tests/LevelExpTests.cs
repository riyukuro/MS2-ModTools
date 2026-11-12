// LevelExp on made-up tables: a table/nextexp.xml of four levels in the kms client, the global client and the
// server's pack, and a monsterExpTable.xml of three levels in the server's Server.m2d (made-up values).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    sealed class FakeLevels : IDisposable
    {
        public readonly string Dir;
        public readonly DataSet Data;

        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        public static string Next(params long[] values)
        {
            string s = Head + "<ms2>\n";
            for (int i = 0; i < values.Length; i++) s += "\t<exp level=\"" + (i + 1) + "\" value=\"" + values[i] + "\" locale=\"\" />\n";
            return s + "</ms2>\n";
        }

        public const string Monster = Head + "<ms2>\n" +
            "\t<exp level=\"1\" common=\"10\" dungeonElite=\"30\" elite=\"20\" boss=\"50\" dungeonBoss=\"70\" />\n" +
            "\t<exp level=\"2\" common=\"12\" dungeonElite=\"33\" elite=\"22\" boss=\"55\" dungeonBoss=\"77\" />\n" +
            "\t<exp level=\"3\" common=\"15\" dungeonElite=\"35\" elite=\"25\" boss=\"60\" dungeonBoss=\"80\" />\n" +
            "</ms2>\n";

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        public FakeLevels(bool gms = true, string gmsTable = null)
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-levels-" + Guid.NewGuid().ToString("N"));
            string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
            foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);
            string table = Next(100, 250, 400, 900);
            M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>> { E("table/nextexp.xml", table) });
            if (gms) M2dWriter.Create(Path.Combine(g, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>> { E("table/nextexp.xml", gmsTable ?? table) });
            M2dWriter.Create(Path.Combine(s, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>> { E("table/nextexp.xml", table) });
            M2dWriter.Create(Path.Combine(s, "Server.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>> { E("table/Server/monsterExpTable.xml", Monster) });
            Data = new DataSet(k, gms ? g : null, s);
        }

        public static Mod NewMod() { return Mod.Parse("<mod id=\"levels.test\" name=\"t\" version=\"1\" />", "test mod"); }

        public Plan Plan(Mod m) { return Planner.Build(Data, new[] { Mod.Parse(m.ToXml(), "round trip") }, new PlanOptions()); }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class LevelExpTests
    {
        static readonly LevelTable Next = LevelTable.NextExp, Monster = LevelTable.MonsterExp;
        const string K = "client:Xml.m2d", G = "gms-client:Xml.m2d", S = "server:Xml.m2d";

        static List<string> Texts(List<PlanMessage> l, string part) { return l.Select(m => m.Text).Where(t => t.Contains(part)).ToList(); }

        static IEnumerable<EditOp> Edits(Mod m) { return m.Ops.OfType<EditOp>(); }

        [Test]
        public void Rows_AreTheShippedOnes()
        {
            using (var f = new FakeLevels())
            {
                var lx = new LevelExp(f.Data, FakeLevels.NewMod());
                Assert.That(lx.Sides(Next), Is.EqualTo(new[] { K, G, S }));
                Assert.That(lx.Sides(Monster), Is.EqualTo(new[] { "server:Server.m2d" }));
                Assert.That(lx.Levels(Next), Is.EqualTo(new[] { 1, 2, 3, 4 }));
                Assert.That(lx.GetShipped(Next, 2, "value"), Is.EqualTo("250"));
                Assert.That(lx.Get(Monster, 3, "dungeonBoss"), Is.EqualTo("80"));
                Assert.That(lx.Get(Next, 9, "value"), Is.Null);
                Assert.That(lx.EditedCount(Next), Is.EqualTo(0));
                Assert.That(lx.Differ(Next), Is.Empty);
            }
            using (var f = new FakeLevels(gms: false))
                Assert.That(new LevelExp(f.Data, FakeLevels.NewMod()).Sides(Next), Is.EqualTo(new[] { K, S }));
        }

        [Test]
        public void Set_WritesEverySide_AndTheShippedValueTakesTheLineOut()
        {
            using (var f = new FakeLevels())
            {
                Mod m = FakeLevels.NewMod();
                var lx = new LevelExp(f.Data, m);
                Assert.That(lx.Set(Next, 2, "value", " 300 "), Is.EqualTo(3));
                foreach (string a in new[] { K, G, S }) Assert.That(lx.Get(Next, a, 2, "value"), Is.EqualTo("300"), a);
                Assert.That(lx.IsEdited(Next, 2), Is.True);
                Assert.That(lx.IsEdited(Next, 1), Is.False);
                Assert.That(Edits(m).Select(e => e.Archive), Is.EqualTo(new[] { K, G, S }));
                Assert.That(Edits(m).All(e => !e.Mirror), Is.True, "each side has its own lines");
                Assert.That(Edits(m).First().Rows.Single().At, Is.EqualTo("exp[level=2]"));
                Assert.That(lx.Set(Next, 2, "value", "300"), Is.EqualTo(0), "no change");
                // round trip: the saved mod reads the same values
                var back = new LevelExp(f.Data, Mod.Parse(m.ToXml(), "round trip"));
                Assert.That(back.Get(Next, G, 2, "value"), Is.EqualTo("300"));
                Assert.That(lx.Set(Next, 2, "value", "250"), Is.EqualTo(3));
                Assert.That(m.Ops, Is.Empty, "the shipped value again: no line left");
            }
        }

        [Test]
        public void Set_RefusesWhatIsNotAWholeNumberOf0OrMore()
        {
            using (var f = new FakeLevels())
            {
                Mod m = FakeLevels.NewMod();
                var lx = new LevelExp(f.Data, m);
                Assert.That(Assert.Throws<ModKitException>(() => lx.Set(Next, 2, "value", "1.5")).Message, Does.Contain("not a whole number"));
                Assert.That(Assert.Throws<ModKitException>(() => lx.Set(Next, 2, "value", "-4")).Message, Does.Contain("below 0"));
                Assert.That(Assert.Throws<ModKitException>(() => lx.Set(Next, 2, "level", "4")).Message, Does.Contain("no field"));
                Assert.That(Assert.Throws<ModKitException>(() => lx.Set(Next, 7, "value", "4")).Message, Does.Contain("no row of level 7"));
                Assert.That(m.Ops, Is.Empty);
                Assert.That(lx.Set(Next, 1, "value", "+0120"), Is.EqualTo(3));
                Assert.That(lx.Get(Next, S, 1, "value"), Is.EqualTo("120"), "written as the number");
            }
        }

        [Test]
        public void MonsterExp_IsTheServersOnly()
        {
            using (var f = new FakeLevels())
            {
                Mod m = FakeLevels.NewMod();
                var lx = new LevelExp(f.Data, m);
                Assert.That(lx.Set(Monster, 1, "boss", "500"), Is.EqualTo(1));
                Assert.That(Edits(m).Single().Archive, Is.EqualTo("server:Server.m2d"));
                Assert.That(Edits(m).Single().Entry, Is.EqualTo("table/Server/monsterExpTable.xml"));
                Assert.That(lx.Get(Monster, 1, "elite"), Is.EqualTo("20"));
                Plan p = f.Plan(m);
                Assert.That(p.Errors, Is.Empty, string.Join("\n", p.Errors));
                Assert.That(Texts(p.Warnings, "monster exp"), Is.Empty);
            }
        }

        [Test]
        public void Scale_MultipliesTheShippedValuesOfARange()
        {
            Assert.That(LevelExp.Scaled(250, 150), Is.EqualTo(375));
            Assert.That(LevelExp.Scaled(5, 50), Is.EqualTo(3), "half away from zero");
            Assert.That(LevelExp.Scaled(1, 10), Is.EqualTo(1), "above 0 stays at least 1");
            Assert.That(LevelExp.Scaled(0, 500), Is.EqualTo(0));
            Assert.That(LevelExp.Scaled(long.MaxValue / 2, 400), Is.EqualTo(long.MaxValue));
            using (var f = new FakeLevels())
            {
                Mod m = FakeLevels.NewMod();
                var lx = new LevelExp(f.Data, m);
                Assert.That(lx.Scale(Next, 3, 2, null, 200), Is.EqualTo(2), "from and to either way round");
                Assert.That(lx.Levels(Next).Select(l => lx.Get(Next, l, "value")), Is.EqualTo(new[] { "100", "500", "800", "900" }));
                Assert.That(lx.Scale(Next, 2, 3, null, 200), Is.EqualTo(0), "of the shipped values: twice is the same");
                Assert.That(lx.Scale(Monster, 1, 3, "common", 50), Is.EqualTo(3));
                Assert.That(lx.Get(Monster, 3, "common"), Is.EqualTo("8"));
                Assert.That(lx.Get(Monster, 3, "boss"), Is.EqualTo("60"), "one field only");
                Assert.That(lx.Scale(Monster, 1, 1, null, 100), Is.EqualTo(1), "100%: back as shipped");
                Assert.That(lx.Get(Monster, 1, "common"), Is.EqualTo("10"));
                Assert.Throws<ModKitException>(() => lx.Scale(Next, 1, 4, null, 0));
                Assert.Throws<ModKitException>(() => lx.Scale(Next, 1, 4, "boss", 120));
            }
        }

        [Test]
        public void Take_PutsTheShippedValuesBack()
        {
            using (var f = new FakeLevels())
            {
                Mod m = FakeLevels.NewMod();
                var lx = new LevelExp(f.Data, m);
                lx.Scale(Next, 1, 4, null, 300);
                lx.Set(Monster, 2, "elite", "99");
                Assert.That(lx.EditedCount(Next), Is.EqualTo(4));
                Assert.That(lx.Take(Next, 3), Is.True);
                Assert.That(lx.Take(Next, 3), Is.False);
                Assert.That(lx.Get(Next, G, 3, "value"), Is.EqualTo("400"));
                Assert.That(lx.TakeAll(Next), Is.EqualTo(3));
                Assert.That(Edits(m).Single().Archive, Is.EqualTo("server:Server.m2d"), "the other table keeps its lines");
            }
        }

        [Test]
        public void Plan_IsCleanForAnEditOfEverySide()
        {
            using (var f = new FakeLevels())
            {
                Mod m = FakeLevels.NewMod();
                new LevelExp(f.Data, m).Scale(Next, 1, 4, null, 80);
                Plan p = f.Plan(m);
                Assert.That(p.Errors, Is.Empty, string.Join("\n", p.Errors));
                Assert.That(Texts(p.Warnings, "nextexp"), Is.Empty, string.Join("\n", p.Warnings));
            }
        }

        static Mod Hand(string archive, string lines)
        {
            return Mod.Parse("<mod id=\"levels.hand\" name=\"t\" version=\"1\">\n<edit archive=\"" + archive + "\" entry=\"table/nextexp.xml\">\n" + lines + "</edit>\n</mod>\n", "hand-written mod");
        }

        [Test]
        public void Plan_ChecksTheValues()
        {
            using (var f = new FakeLevels())
            {
                // one side only: the bar and the level-up disagree; a value that is no number; 0; a fall
                Mod m = Hand(K, "<change at=\"exp[level=1]\"><set name=\"value\" value=\"lots\" /></change>\n" +
                    "<change at=\"exp[level=2]\"><set name=\"value\" value=\"0\" /></change>\n" +
                    "<change at=\"exp[level=4]\"><set name=\"value\" value=\"300\" /></change>\n");
                Plan p = f.Plan(m);
                Assert.That(Texts(p.Errors, "nextexp level 1 value \"lots\" is not a whole number"), Has.Count.EqualTo(1), string.Join("\n", p.Errors));
                Assert.That(Texts(p.Warnings, "level 2 needs 0 experience"), Has.Count.EqualTo(1), string.Join("\n", p.Warnings));
                Assert.That(Texts(p.Warnings, "level 4 needs 300, less than level 3 (400)"), Has.Count.EqualTo(1), string.Join("\n", p.Warnings));
                Assert.That(Texts(p.Warnings, "nextexp level 4: client:Xml.m2d has 300, gms-client:Xml.m2d has 900"), Has.Count.EqualTo(1), string.Join("\n", p.Warnings));
                Assert.That(Texts(p.Warnings, "nextexp level 4: client:Xml.m2d has 300, server:Xml.m2d has 900"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Warnings, "level 3"), Is.Empty.Or.All.Contains("less than level 3"), "level 3 is left as shipped");
            }
        }

        [Test]
        public void Plan_LevelsTheClientDoesNotRead_AndALostRow()
        {
            using (var f = new FakeLevels())
            {
                Mod m = Hand(K, "<add><xml><exp level=\"100\" value=\"5000\" locale=\"\" /></xml></add>\n<delete at=\"exp[level=3]\" />\n");
                Plan p = f.Plan(m);
                Assert.That(p.Errors, Is.Empty, string.Join("\n", p.Errors));
                Assert.That(Texts(p.Warnings, "nextexp level 100: the client reads levels 1..99 only"), Has.Count.EqualTo(1), string.Join("\n", p.Warnings));
                Assert.That(Texts(p.Warnings, "nextexp level 3 has no row after the mods: the client's experience bar never fills"), Has.Count.EqualTo(1), string.Join("\n", p.Warnings));
                // the server reads any level: a level 100 there raises nothing
                Plan s = f.Plan(Hand(S, "<add><xml><exp level=\"100\" value=\"5000\" locale=\"\" /></xml></add>\n"));
                Assert.That(Texts(s.Warnings, "reads levels"), Is.Empty);
            }
        }

        [Test]
        public void Plan_SidesThatShippedDifferentValuesRaiseNothing()
        {
            using (var f = new FakeLevels(gmsTable: FakeLevels.Next(100, 260, 400, 900)))
            {
                var lx = new LevelExp(f.Data, FakeLevels.NewMod());
                Assert.That(lx.Differ(Next), Is.EqualTo(new[] { 2 }), "as shipped");
                Mod m = Hand(K, "<change at=\"exp[level=1]\"><set name=\"value\" value=\"110\" /></change>\n");
                Plan p = f.Plan(m);
                Assert.That(Texts(p.Warnings, "nextexp level 2:"), Is.Empty, "the global client shipped its own value");
                Assert.That(Texts(p.Warnings, "nextexp level 1:"), Has.Count.EqualTo(2), string.Join("\n", p.Warnings));
            }
        }

        [Test]
        public void Scale_ScalesEachSideFromItsOwnShippedValue()
        {
            using (var f = new FakeLevels(gmsTable: FakeLevels.Next(100, 300, 400, 900)))
            {
                Mod m = FakeLevels.NewMod();
                var lx = new LevelExp(f.Data, m);
                Assert.That(lx.Scale(Next, 2, 2, null, 200), Is.EqualTo(1));
                Assert.That(lx.Get(Next, K, 2, "value"), Is.EqualTo("500"));
                Assert.That(lx.Get(Next, G, 2, "value"), Is.EqualTo("600"));
                Assert.That(lx.Get(Next, S, 2, "value"), Is.EqualTo("500"));
                Plan p = f.Plan(m);
                Assert.That(Texts(p.Warnings, "nextexp level 2:"), Is.Empty, "the sides shipped different values: nothing to raise");
                Assert.That(lx.Scale(Next, 2, 2, null, 100), Is.EqualTo(1));
                Assert.That(lx.Get(Next, G, 2, "value"), Is.EqualTo("300"), "100% puts each side's shipped value back");
                Assert.That(m.Ops, Is.Empty);
            }
        }

        [Test]
        public void ALevelWithRowsByLocale_IsRefusedAndRaised()
        {
            string table = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ms2>\n\t<exp level=\"1\" value=\"100\" locale=\"\" />\n\t<exp level=\"2\" value=\"250\" locale=\"KR\" />\n" +
                "\t<exp level=\"2\" value=\"260\" locale=\"CN\" />\n\t<exp level=\"3\" value=\"400\" locale=\"\" />\n</ms2>\n";
            using (var f = new FakeLevels(gmsTable: table))
            {
                Mod m = FakeLevels.NewMod();
                var lx = new LevelExp(f.Data, m);
                Assert.That(Assert.Throws<ModKitException>(() => lx.Set(Next, 2, "value", "300")).Message, Does.Contain("has 2 rows on " + G));
                Assert.That(Assert.Throws<ModKitException>(() => lx.Scale(Next, 1, 3, null, 200)).Message, Does.Contain("has 2 rows"));
                Assert.That(m.Ops, Is.Empty, "a refused level leaves no line on any side");
                lx.Set(Next, 3, "value", "500");
                Plan p = f.Plan(m);
                Assert.That(Texts(p.Warnings, "nextexp level 2 has 2 rows"), Has.Count.EqualTo(1), string.Join("\n", p.Warnings));
            }
        }
    }
}
