// Constants on made-up tables: a table/constants.xml in the kms client, the global client and the server's pack, and
// a table/Server/constants.xml in the server's Server.m2d (made-up keys and values).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    sealed class FakeConstants : IDisposable
    {
        public readonly string Dir;
        public readonly DataSet Data;

        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        public const string Shared =
            "\t<v key=\"TestPrice\" value=\"100\" />\n" +
            "\t<v key=\"TestRate\" value=\"0.5\" />\n" +
            "\t<v key=\"TestFlag\" value=\"true\" />\n" +
            "\t<v key=\"TestList\" value=\"1,2,3\" />\n" +
            "\t<v key=\"TestFee\" value=\"10\" locale=\"KR\" />\n" +
            "\t<v key=\"TestFee\" value=\"20\" locale=\"CN\" />\n";

        public static string Table(string rows) { return Head + "<ms2>\n" + rows + "</ms2>\n"; }

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        public FakeConstants()
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-constants-" + Guid.NewGuid().ToString("N"));
            string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
            foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);
            string client = Table(Shared + "\t<v key=\"TestSplit\" value=\"5\" />\n");
            M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>> { E("table/constants.xml", client) });
            M2dWriter.Create(Path.Combine(g, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>> { E("table/constants.xml", client) });
            M2dWriter.Create(Path.Combine(s, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>> { E("table/constants.xml", client) });
            M2dWriter.Create(Path.Combine(s, "Server.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E("table/Server/constants.xml", Table(Shared + "\t<v key=\"TestSplit\" value=\"7\" />\n\t<v key=\"TestServerOnly\" value=\"3\" />\n")),
            });
            Data = new DataSet(k, g, s);
        }

        public static Mod NewMod() { return Mod.Parse("<mod id=\"constants.test\" name=\"t\" version=\"1\" />", "test mod"); }

        public Plan Plan(Mod m) { return Planner.Build(Data, new[] { Mod.Parse(m.ToXml(), "round trip") }, new PlanOptions()); }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class ConstantsTests
    {
        static readonly ConstSide ST = ConstSide.ServerTable, SX = ConstSide.ServerXml, K = ConstSide.KmsXml, G = ConstSide.GmsXml;
        static readonly ConstId Price = new ConstId("TestPrice"), Rate = new ConstId("TestRate"), Flag = new ConstId("TestFlag"), List = new ConstId("TestList"),
            FeeKr = new ConstId("TestFee", "KR"), FeeCn = new ConstId("TestFee", "CN"), Split = new ConstId("TestSplit"), ServerOnly = new ConstId("TestServerOnly");

        static List<string> Texts(List<PlanMessage> l, string part) { return l.Select(m => m.Text).Where(t => t.Contains(part)).ToList(); }

        static IEnumerable<EditOp> Edits(Mod m) { return m.Ops.OfType<EditOp>(); }

        [Test]
        public void Rows_AreTheShippedOnes()
        {
            using (var f = new FakeConstants())
            {
                var c = new Constants(f.Data, FakeConstants.NewMod());
                Assert.That(c.Sides(), Is.EqualTo(new[] { ST, SX, K, G }));
                Assert.That(c.Ids().Count, Is.EqualTo(8));
                Assert.That(c.Ids().First().Key, Is.EqualTo("TestFee"));
                Assert.That(c.Get(FeeCn), Is.EqualTo("20"));
                Assert.That(c.Get(K, ServerOnly), Is.Null);
                Assert.That(c.SidesOf(ServerOnly), Is.EqualTo(new[] { ST }));
                Assert.That(c.ShippedDiffers(Split), Is.True);
                Assert.That(c.Differs(Price), Is.False);
                Assert.That(c.EditedCount(), Is.EqualTo(0));
            }
        }

        [Test]
        public void Set_WritesEveryCopy_AndTheShippedValueTakesTheLineOut()
        {
            using (var f = new FakeConstants())
            {
                Mod m = FakeConstants.NewMod();
                var c = new Constants(f.Data, m);
                Assert.That(c.Set(Price, " 250 "), Is.EqualTo(4));
                foreach (ConstSide s in ConstSide.All) Assert.That(c.Get(s, Price), Is.EqualTo("250"), s.ToString());
                Assert.That(c.IsEdited(Price), Is.True);
                Assert.That(Edits(m).Count(), Is.EqualTo(4));
                Assert.That(Edits(m).All(e => !e.Mirror), Is.True, "each copy has its own lines");
                Assert.That(Edits(m).First().Entry, Is.EqualTo("table/Server/constants.xml"));
                Assert.That(Edits(m).First().Rows.Single().At, Is.EqualTo(Price.At));
                var back = new Constants(f.Data, Mod.Parse(m.ToXml(), "round trip"));
                Assert.That(back.Get(G, Price), Is.EqualTo("250"));
                Assert.That(c.Set(Price, "250"), Is.EqualTo(0), "no change");
                Assert.That(c.Set(Price, "100"), Is.EqualTo(4));
                Assert.That(m.Ops, Is.Empty, "the shipped value again: no line left");
            }
        }

        [Test]
        public void Set_OfALocaleRow_LeavesTheOtherLocale()
        {
            using (var f = new FakeConstants())
            {
                Mod m = FakeConstants.NewMod();
                var c = new Constants(f.Data, m);
                c.Set(FeeKr, "15");
                Assert.That(c.Get(K, FeeKr), Is.EqualTo("15"));
                Assert.That(c.Get(K, FeeCn), Is.EqualTo("20"));
                Plan p = f.Plan(m);
                Assert.That(p.Errors, Is.Empty);
                Assert.That(p.Warnings, Is.Empty, string.Join("\n", p.Warnings.Select(x => x.Text)));
            }
        }

        [Test]
        public void Set_RefusesAValueOfAnotherKind()
        {
            using (var f = new FakeConstants())
            {
                var c = new Constants(f.Data, FakeConstants.NewMod());
                Assert.That(Assert.Throws<ModKitException>(() => c.Set(Price, "1.5")).Message, Does.Contain("not a whole number"));
                Assert.That(Assert.Throws<ModKitException>(() => c.Set(Rate, "half")).Message, Does.Contain("not a number"));
                Assert.That(Assert.Throws<ModKitException>(() => c.Set(Flag, "yes")).Message, Does.Contain("not true or false"));
                Assert.That(Assert.Throws<ModKitException>(() => c.Set(new ConstId("NoSuchKey"), "1")).Message, Does.Contain("no copy has"));
                Assert.That(c.Set(Rate, "2"), Is.EqualTo(4), "a whole number is a number");
                Assert.That(c.Set(Flag, "False"), Is.EqualTo(4));
                Assert.That(c.Set(List, "4,5"), Is.EqualTo(4));
                Assert.That(c.Mod.Ops.Count, Is.EqualTo(4), "none of the refused values left a line");
            }
        }

        [Test]
        public void Set_OnOneCopy_RaisesTheSidesWarning()
        {
            using (var f = new FakeConstants())
            {
                Mod m = FakeConstants.NewMod();
                var c = new Constants(f.Data, m);
                Assert.That(c.Set(Price, "300", K), Is.EqualTo(1));
                Assert.That(c.Differs(Price), Is.True);
                Plan p = f.Plan(m);
                Assert.That(Texts(p.Warnings, "the client shows one value and the server uses another"), Is.Not.Empty);
                Assert.That(Texts(p.Warnings, "the two clients disagree"), Has.Count.EqualTo(1));
                Assert.That(Assert.Throws<ModKitException>(() => c.Set(ServerOnly, "4", K)).Message, Does.Contain("has no constant"));
            }
        }

        [Test]
        public void Plan_IsCleanForAnEditOfEveryCopy_AndCopiesThatShippedDifferentRaiseNothing()
        {
            using (var f = new FakeConstants())
            {
                Mod m = FakeConstants.NewMod();
                var c = new Constants(f.Data, m);
                c.Set(Price, "400");
                c.Set(Rate, "0.75");
                c.Set(Split, "9", ST);
                c.Set(ServerOnly, "6");
                Plan p = f.Plan(m);
                Assert.That(p.Errors, Is.Empty, string.Join("\n", p.Errors.Select(x => x.Text)));
                Assert.That(p.Warnings, Is.Empty, string.Join("\n", p.Warnings.Select(x => x.Text)));
            }
        }

        [Test]
        public void Plan_ChecksHandWrittenLines()
        {
            using (var f = new FakeConstants())
            {
                Mod m = Mod.Parse("<mod id=\"constants.hand\" name=\"t\" version=\"1\">" +
                    "<edit archive=\"server:Server.m2d\" entry=\"table/Server/constants.xml\" sync=\"false\">" +
                    "<change at=\"v[key=TestPrice]\"><set name=\"value\" value=\"cheap\"/></change>" +
                    "<add><xml><v key=\"TestMadeUp\" value=\"1\"/></xml></add>" +
                    "<delete at=\"v[key=TestServerOnly]\"/>" +
                    "</edit></mod>", "hand");
                Plan p = f.Plan(m);
                Assert.That(Texts(p.Errors, "\"cheap\" is not a whole number"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Warnings, "TestMadeUp is added, and no copy"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Warnings, "TestServerOnly has no row after the mods"), Has.Count.EqualTo(1));
                var c = new Constants(f.Data, m);
                Assert.That(c.Get(ST, Price), Is.EqualTo("cheap"), "a hand-written v[key=K] line is the row's line");
                Assert.That(c.Take(Price), Is.True);
            }
        }

        [Test]
        public void Take_PutsTheShippedValuesBack()
        {
            using (var f = new FakeConstants())
            {
                Mod m = FakeConstants.NewMod();
                var c = new Constants(f.Data, m);
                c.Set(Price, "1");
                c.Set(FeeKr, "2");
                Assert.That(c.Take(Price), Is.True);
                Assert.That(c.Take(Price), Is.False);
                Assert.That(c.Get(G, Price), Is.EqualTo("100"));
                Assert.That(c.EditedCount(), Is.EqualTo(1));
                Assert.That(c.TakeAll(), Is.EqualTo(1));
                Assert.That(m.Ops, Is.Empty);
            }
        }

        [Test]
        public void Kinds()
        {
            Assert.That(Constants.KindOf("12"), Is.EqualTo("int"));
            Assert.That(Constants.KindOf("-0.25"), Is.EqualTo("float"));
            Assert.That(Constants.KindOf("TRUE"), Is.EqualTo("bool"));
            Assert.That(Constants.KindOf("1,2"), Is.EqualTo("list"));
            Assert.That(Constants.KindOf("abc"), Is.EqualTo("text"));
            Assert.That(Constants.Alike("2.0", "2"), Is.True);
            Assert.That(Constants.Alike("True", "true"), Is.True);
            Assert.That(Constants.Alike("a", "A"), Is.False);
        }
    }
}
