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

        public FakeConstants() : this("") { }

        // extra: rows every copy has after the shared ones
        public FakeConstants(string extra)
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-constants-" + Guid.NewGuid().ToString("N"));
            string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
            foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);
            string client = Table(Shared + extra + "\t<v key=\"TestSplit\" value=\"5\" />\n");
            M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>> { E("table/constants.xml", client) });
            M2dWriter.Create(Path.Combine(g, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>> { E("table/constants.xml", client) });
            M2dWriter.Create(Path.Combine(s, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>> { E("table/constants.xml", client) });
            M2dWriter.Create(Path.Combine(s, "Server.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E("table/Server/constants.xml", Table(Shared + extra + "\t<v key=\"TestSplit\" value=\"7\" />\n\t<v key=\"TestServerOnly\" value=\"3\" />\n")),
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
                Assert.That(p.Warnings.Select(x => x.Text), Has.All.Contain("runs with locale NA"), "only the copies whose game runs another locale");
                Assert.That(p.Warnings, Has.Count.EqualTo(3));
            }
        }

        [Test]
        public void Set_RefusesAValueOfAnotherKind()
        {
            using (var f = new FakeConstants())
            {
                var c = new Constants(f.Data, FakeConstants.NewMod());
                Assert.That(Assert.Throws<ModKitException>(() => c.Set(Price, "cheap")).Message, Does.Contain("not a number"));
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
                Assert.That(Texts(p.Errors, "\"cheap\" is not a number"), Has.Count.EqualTo(1));
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

        // made-up values on keys the server ingest reads: NPCColorScale float, EnableSkillJumpDown bool,
        // DamageDistance int, hitPerDeadCount byte
        const string ServerKeys =
            "\t<v key=\"NPCColorScale\" value=\"1\" />\n" +
            "\t<v key=\"EnableSkillJumpDown\" value=\"0\" />\n" +
            "\t<v key=\"DamageDistance\" value=\"3\" />\n" +
            "\t<v key=\"hitPerDeadCount\" value=\"1\" />\n";
        static readonly ConstId Scale = new ConstId("NPCColorScale"), Jump = new ConstId("EnableSkillJumpDown"), Dist = new ConstId("DamageDistance"), Hits = new ConstId("hitPerDeadCount");

        [Test]
        public void Kinds_OfAKeyTheServerReads_AreTheIngestsTypes()
        {
            Assert.That(Constants.KindOf(Scale, "1"), Is.EqualTo("float"));
            Assert.That(Constants.KindOf(Jump, "0"), Is.EqualTo("bool"));
            Assert.That(Constants.KindOf(new ConstId(" npccolorscale "), "1"), Is.EqualTo("float"), "keys match trimmed and case-insensitive");
            Assert.That(Constants.KindOf(Price, "100"), Is.EqualTo("int"), "an unknown key: the shipped shape");
            Assert.That(Constants.Problem(Scale, "1", "1.5"), Is.Null);
            Assert.That(Constants.Problem(Scale, "1", "2.5f"), Is.Null, "the ingest strips a trailing f");
            Assert.That(Constants.Problem(Scale, "1", "1.5F"), Does.Contain("not a number the server reads"), "it strips F only next to a lowercase f, so the server gets 0");
            Assert.That(Constants.Problem(Jump, "0", "true"), Is.Null);
            Assert.That(Constants.Problem(Jump, "0", "1"), Is.Null);
            Assert.That(Constants.Problem(Jump, "0", "2"), Does.Contain("not a truth value"));
            Assert.That(Constants.Problem(Dist, "3", "1.5"), Does.Contain("not a whole number"));
            Assert.That(Constants.Problem(Hits, "1", "300"), Does.Contain("out of range"));
            Assert.That(Constants.Problem(Hits, "1", "-1"), Does.Contain("out of range"));
            Assert.That(Constants.Alike(Jump, "1", "true"), Is.True);
            Assert.That(Constants.Alike(Price, "1", "true"), Is.False);
            Assert.That(Constants.Problem(Price, "100", "1.5"), Is.Null, "an unknown whole key widens to a number");
            Assert.That(Constants.Doubt(Price, "100", "1.5"), Does.Contain("is a fraction"));
            Assert.That(Constants.Doubt(Scale, "1", "1.5"), Is.Null, "the server reads it as a float");
            Assert.That(Constants.Problem(Flag, "true", "1"), Does.Contain("not true or false"), "an unknown bool keeps to true and false");
        }

        [Test]
        public void Set_OfServerKeys_FollowsTheIngest_AndAFractionOfAnUnknownWholeKeyIsAWarning()
        {
            using (var f = new FakeConstants(ServerKeys))
            {
                Mod m = FakeConstants.NewMod();
                var c = new Constants(f.Data, m);
                Assert.That(c.Set(Scale, "1.5"), Is.EqualTo(4));
                Assert.That(c.Set(Jump, "true"), Is.EqualTo(4));
                Assert.That(Assert.Throws<ModKitException>(() => c.Set(Jump, "2")).Message, Does.Contain("not a truth value"));
                Assert.That(Assert.Throws<ModKitException>(() => c.Set(Dist, "1.5")).Message, Does.Contain("not a whole number"));
                Assert.That(c.Set(Price, "1.5"), Is.EqualTo(4));
                Plan p = f.Plan(m);
                Assert.That(p.Errors, Is.Empty, string.Join("\n", p.Errors.Select(x => x.Text)));
                Assert.That(Texts(p.Warnings, "TestPrice \"1.5\" is a fraction"), Has.Count.EqualTo(4));
                Assert.That(Texts(p.Warnings, "EnableSkillJumpDown"), Is.Empty, "true and 0 read alike only where they differ");
            }
        }

        [Test]
        public void DuplicateRows_ShowTheLast_AreRefused_AndNewOnesAreFlagged()
        {
            const string dups = "\t<v key=\"TestTwice\" value=\"1\" />\n\t<v key=\"TestTwice\" value=\"2\" />\n" +
                "\t<v key=\"TestCase\" value=\"1\" />\n\t<v key=\" testcase\" value=\"2\" />\n";
            using (var f = new FakeConstants(dups))
            {
                Mod m = FakeConstants.NewMod();
                var c = new Constants(f.Data, m);
                var twice = new ConstId("TestTwice");
                Assert.That(c.Get(ST, twice), Is.EqualTo("2"), "the server keeps the last row");
                Assert.That(c.RowCount(ST, twice), Is.EqualTo(2));
                Assert.That(c.RowCount(K, new ConstId("TestCase")), Is.EqualTo(2), "keys that differ by case or spaces count as one");
                Assert.That(c.RowCount(K, Price), Is.EqualTo(1));
                Assert.That(Assert.Throws<ModKitException>(() => c.Set(twice, "3")).Message, Does.Contain("has 2 rows"));
                Assert.That(Assert.Throws<ModKitException>(() => c.Set(new ConstId("TestCase"), "3")).Message, Does.Contain("has 2 rows"));
                Assert.That(m.Ops, Is.Empty);
                Assert.That(f.Plan(m).Warnings, Is.Empty, "rows that shipped twice raise nothing by themselves");
            }
            using (var f = new FakeConstants())
            {
                Mod m = Mod.Parse("<mod id=\"constants.dup\" name=\"t\" version=\"1\">" +
                    "<edit archive=\"server:Server.m2d\" entry=\"table/Server/constants.xml\" sync=\"false\">" +
                    "<add><xml><v key=\"testprice\" value=\"5\"/></xml></add>" +
                    "</edit></mod>", "hand");
                Plan p = f.Plan(m);
                Assert.That(Texts(p.Warnings, "has 2 rows after the mods"), Has.Count.EqualTo(1), string.Join("\n", p.Warnings.Select(x => x.Text)));
            }
        }

        [Test]
        public void Plan_FlagsATakenOutValue_AndAChangeOfAnotherLocale()
        {
            using (var f = new FakeConstants())
            {
                Mod m = Mod.Parse("<mod id=\"constants.unset\" name=\"t\" version=\"1\">" +
                    "<edit archive=\"server:Server.m2d\" entry=\"table/Server/constants.xml\" sync=\"false\">" +
                    "<change at=\"v[key=TestPrice]\"><unset name=\"value\"/></change>" +
                    "</edit></mod>", "hand");
                Assert.That(Texts(f.Plan(m).Warnings, "TestPrice has no value after the mods"), Has.Count.EqualTo(1));
            }
            using (var f = new FakeConstants())
            {
                Mod m = FakeConstants.NewMod();
                var c = new Constants(f.Data, m);
                c.Set(FeeCn, "25");
                Plan p = f.Plan(m);
                Assert.That(Texts(p.Warnings, "runs with locale KR"), Is.Not.Empty, string.Join("\n", p.Warnings.Select(x => x.Text)));
            }
        }

        const string Picked =
            "\t<v key=\"TestOpt\" value=\"1\" />\n" +
            "\t<v key=\"TestOpt\" value=\"2\" locale=\"KR\" />\n" +
            "\t<v key=\"TestCap\" value=\"5\" />\n" +
            "\t<v key=\"TestCap\" value=\"9\" feature=\"TestFeat\" />\n" +
            "\t<v key=\"TestDup\" value=\"1\" feature=\"TestFeat\" />\n" +
            "\t<v key=\"TestDup\" value=\"2\" feature=\"TestFeat\" />\n" +
            "\t<v key=\"TestTwice\" value=\"1\" />\n" +
            "\t<v key=\"TestTwice\" value=\"2\" />\n";

        [Test]
        public void Plan_WarnsWhenTheGameReadsAnotherRowOfTheKey()
        {
            using (var f = new FakeConstants(Picked))
            {
                Mod m = FakeConstants.NewMod();
                var c = new Constants(f.Data, m);
                var opt = new ConstId("TestOpt");
                Assert.That(c.Shadow(K, opt), Does.Contain("locale KR"), "the KR game reads its own row");
                Assert.That(c.Shadow(ST, opt), Is.Null, "an NA game has no row of its locale: it reads the one without");
                Assert.That(c.Shadow(ST, new ConstId("TestCap")), Does.Contain("feature TestFeat"));
                Assert.That(c.Shadow(ST, new ConstId("TestCap", "", "TestFeat")), Is.Null);
                c.Set(opt, "3");
                c.Set(new ConstId("TestCap"), "6");
                Plan p = f.Plan(m);
                Assert.That(p.Errors, Is.Empty);
                Assert.That(Texts(p.Warnings, "TestOpt is changed, but this copy also has a row of the key for its game's locale KR"), Has.Count.EqualTo(1), string.Join("\n", p.Warnings.Select(x => x.Text)));
                Assert.That(Texts(p.Warnings, "TestCap is changed, but this copy also has a row of the key with feature TestFeat"), Has.Count.EqualTo(4));
            }
        }

        [Test]
        public void Rows_OfOneId_AreTheOneTheParserKeeps()
        {
            using (var f = new FakeConstants(Picked))
            {
                var c = new Constants(f.Data, FakeConstants.NewMod());
                Assert.That(c.GetShipped(ST, new ConstId("TestDup", "", "TestFeat")), Is.EqualTo("1"), "rows with a feature: the first");
                Assert.That(c.GetShipped(ST, new ConstId("TestTwice")), Is.EqualTo("2"), "rows without one: the last");
            }
        }

        // ConstantKinds against the server's ConstantsTable record (skipped unless MS2_SERVER_SOURCE names a server
        // source tree): every int, long, byte, float and bool property is listed with its type, and nothing else.
        [Test]
        public void Kinds_AreTheServerSourcesProperties()
        {
            string dir = Environment.GetEnvironmentVariable("MS2_SERVER_SOURCE");
            if (string.IsNullOrEmpty(dir)) Assert.Ignore("MS2_SERVER_SOURCE is not set");
            string file = Path.Combine(dir, "Maple2.Model", "Metadata", "ServerTable", "ConstantsTable.cs");
            if (!File.Exists(file)) Assert.Ignore("no " + file);
            string text = File.ReadAllText(file);
            int at = text.IndexOf("record ConstantsTable(", StringComparison.Ordinal), end = text.IndexOf(") : ServerTable", at < 0 ? 0 : at, StringComparison.Ordinal);
            Assert.That(at >= 0 && end > at, Is.True, "the ConstantsTable record");
            var want = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var rx = new System.Text.RegularExpressions.Regex(@"^\s*(int|long|byte|float|bool)\s+(\w+)\s*=", System.Text.RegularExpressions.RegexOptions.Multiline);
            foreach (System.Text.RegularExpressions.Match x in rx.Matches(text.Substring(at, end - at))) want[x.Groups[2].Value] = x.Groups[1].Value;
            Assert.That(want.Count, Is.GreaterThan(100));
            var have = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in Constants.ServerKinds()) have[kv.Key] = kv.Value;
            var diff = new List<string>();
            foreach (KeyValuePair<string, string> kv in want)
            {
                string h;
                if (!have.TryGetValue(kv.Key, out h)) diff.Add("missing " + kv.Value + " " + kv.Key);
                else if (h != kv.Value) diff.Add(kv.Key + ": " + h + ", the server reads " + kv.Value);
            }
            foreach (string k in have.Keys) if (!want.ContainsKey(k)) diff.Add("extra " + k);
            Assert.That(diff, Is.Empty, string.Join("\n", diff));
        }

        [Test]
        public void Stale_WhenAnotherViewReplacesTheEntry_AndTheLinesFollowSetAndTake()
        {
            using (var f = new FakeConstants())
            {
                Mod m = FakeConstants.NewMod();
                var c = new Constants(f.Data, m);
                c.Set(Price, "1");
                c.Set(Rate, "0.25");
                Assert.That(c.Take(Price), Is.True);
                Assert.That(c.Get(ST, Rate), Is.EqualTo("0.25"));
                Assert.That(c.Get(ST, Price), Is.EqualTo("100"));
                c.Set(Price, "2");
                Assert.That(c.Get(K, Price), Is.EqualTo("2"));
                Assert.That(c.EditedCount(), Is.EqualTo(2));
                Assert.That(c.Stale(), Is.False);
                m.Ops.Add(new EntryOp { Archive = ST.Archive, Name = ST.Entry, Delete = true });
                Assert.That(c.Stale(), Is.True);
            }
        }
    }
}
