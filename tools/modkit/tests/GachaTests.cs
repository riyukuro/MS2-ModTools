// Gacha capsules (Gacha.cs, PlannerGacha.cs) on made-up tables: a kms client, a global client and a server with
// capsule items and a drop box.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    public sealed class FakeGacha : IDisposable
    {
        public readonly string Dir;
        public readonly DataSet Data;
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        static string Row(string id, string box, string coin, string amount, string more)
        {
            return "\t<randomBox randomBoxID=\"" + id + "\" randomBoxGroup=\"1\" randomBoxSeasonKR=\"1\" randomBoxSeasonCN=\"\" randomBoxSeasonNA=\"\" randomBoxSeasonJP=\"\" seasonNumberOnIcon=\"1\" " +
                "saleEndDate=\"2030-01-01-00-00-00\" ui=\"1\"" + more + " individualDropBoxID=\"" + box + "\" shopID=\"0\" coinItemID=\"" + coin + "\"" + (amount == null ? "" : " coinItemAmount=\"" + amount + "\"") + " />\n";
        }

        public static string Table(bool client)
        {
            string tag = client ? " paymentIconTag=\"5009\"" : "";
            return Head + "<ms2>\n" +
                Row("1000001", "7001", "5009", client ? null : "1", tag + (client ? " randomBoxGroupDesc=\"made up\"" : "")) +
                Row("1000002", "7002", "5009", "3", tag) +
                Row("1000003", "7001", "5009", "", tag) +
                "</ms2>\n";
        }

        public const string Drops = Head + "<ms2>\n" +
            "  <dropBox dropBoxID=\"7001\">\n" +
            "    <group dropGroupID=\"1\" dropCount=\"1\" dropCountProbability=\"100\">\n" +
            "      <v itemID=\"5001\" minCount=\"1\" maxCount=\"1\" weight=\"30\" />\n" +
            "      <v itemID=\"5002\" minCount=\"1\" maxCount=\"2\" weight=\"10\" />\n" +
            "      <v itemID=\"5003\" minCount=\"1\" maxCount=\"1\" weight=\"0\" />\n" +
            "    </group>\n" +
            "    <group dropGroupID=\"2\" smartDropRate=\"100\" isApplySmartGenderDrop=\"1\">\n" +
            "      <v itemID=\"5004\" weight=\"5\" properJobWeight=\"10\" />\n" +
            "    </group>\n" +
            "  </dropBox>\n" +
            "  <dropBox dropBoxID=\"7002\">\n" +
            "    <group dropGroupID=\"1\" dropCount=\"1\">\n" +
            "      <v itemID=\"5002\" weight=\"1\" />\n" +
            "    </group>\n" +
            "  </dropBox>\n" +
            "  <dropBox dropBoxID=\"7003\" />\n" +
            "</ms2>\n";

        static string ItemFile(long id, string function)
        {
            return Head + "<ms2>\n\t<environment>\n\t\t<basic itemID=\"" + id + "\" />\n" + (function == null ? "" : "\t\t<function name=\"OpenGachaBox\" parameter=\"" + function + "\" />\n") + "\t</environment>\n</ms2>\n";
        }

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        public FakeGacha()
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-gacha-" + Guid.NewGuid().ToString("N"));
            string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
            foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);
            M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E(GachaRules.Entry, Table(true)),
                E("itemdata/005.xml", Head + "<ms2>\n\t<item id=\"5001\" />\n\t<item id=\"5009\" />\n</ms2>\n"),
            });
            foreach (string d in new[] { g, s })
            {
                var l = new List<KeyValuePair<string, byte[]>> { E(GachaRules.Entry, Table(false)) };
                foreach (long i in new long[] { 5001, 5002, 5003, 5004, 5009 }) l.Add(E("item/00/00/" + i.ToString("00000000") + ".xml", ItemFile(i, null)));
                l.Add(E("item/00/00/00005101.xml", ItemFile(5101, "1000001,5009")));
                l.Add(E("item/00/00/00005102.xml", ItemFile(5102, "1000002")));
                l.Add(E("item/00/00/00005103.xml", ItemFile(5103, "1000003,5009")));
                M2dWriter.Create(Path.Combine(d, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, l);
            }
            M2dWriter.Create(Path.Combine(s, "Server.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>> { E("table/Server/individualItemDrop_Final.xml", Drops) });
            Data = new DataSet(k, g, s);
        }

        public static Mod ModOf(string body, string id = "gacha.test")
        {
            Mod m = Mod.Parse("<mod id=\"" + id + "\" name=\"t\" version=\"1\">\n" + body + "\n</mod>", "test mod");
            return Mod.Parse(m.ToXml(), "round trip");
        }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class GachaTests
    {
        const string K = "client:Xml.m2d", G = "gms-client:Xml.m2d", S = "server:Xml.m2d";

        static Mod Empty() { return FakeGacha.ModOf(""); }

        static Gacha Find(GachaEditor ed, long id)
        {
            foreach (Gacha g in ed.Gachas) if (ed.Id(g) == id) return g;
            return null;
        }

        static Plan PlanOf(FakeGacha f, Mod m) { return Planner.Build(f.Data, new[] { Mod.Parse(m.ToXml(), "round trip") }, new PlanOptions()); }

        static List<string> Texts(List<PlanMessage> l) { return l.Select(m => m.ToString()).ToList(); }

        static Mod Edit(string archive, string body, string id = "gacha.test")
        {
            return FakeGacha.ModOf("<edit archive=\"" + archive + "\" entry=\"" + GachaRules.Entry + "\">" + body + "</edit>", id);
        }

        [Test]
        public void RulesReadAsTheServerReads()
        {
            var row = new Dictionary<string, string> { { "randomBoxID", "1000001" }, { "individualDropBoxID", "7001" }, { "coinItemID", "5009" }, { "coinItemAmount", "1" }, { "ui", "1" } };
            Func<string, string> get = n => { string v; return row.TryGetValue(n, out v) ? v : null; };
            Assert.That(GachaRules.Check(get), Is.Empty);
            row["ui"] = "256";
            row["randomBoxGroup"] = "x";
            row["shopID"] = "2147483648";
            var e = new List<string>(); var w = new List<string>();
            GachaRules.Check(get, e, w);
            Assert.That(e, Has.Some.StartsWith("ui \"256\""));
            Assert.That(e, Has.Some.StartsWith("randomBoxGroup \"x\""));
            Assert.That(e, Has.Some.StartsWith("shopID \"2147483648\""));
            Assert.That(w, Is.Empty);
            row["ui"] = " 4 "; row["randomBoxGroup"] = ""; row["shopID"] = "-1";
            Assert.That(GachaRules.Check(get), Is.Empty, "spaces, empty (0) and a sign read");
            row["coinItemAmount"] = "";
            Assert.That(GachaRules.Check(get), Has.Some.Contains("empty (the server reads 0)"));
            row["coinItemAmount"] = "2"; row["coinItemID"] = "0";
            Assert.That(GachaRules.Check(get), Has.Some.StartsWith("coinItemID is 0"));
            row["coinItemID"] = "5009"; row["individualDropBoxID"] = "0"; row["saleEndDate"] = "2030-01-01";
            List<string> all = GachaRules.Check(get);
            Assert.That(all, Has.Some.StartsWith("individualDropBoxID is 0"));
            Assert.That(all, Has.Some.StartsWith("saleEndDate \"2030-01-01\""));
            Assert.That(GachaRules.Effective("coinItemAmount", "", true), Is.EqualTo("1"));
            Assert.That(GachaRules.Effective("coinItemAmount", null, false), Is.EqualTo("0"));
            Assert.That(GachaRules.Effective("ui", "01", false), Is.EqualTo("1"));
        }

        [Test]
        public void CapsuleItemsAreRead()
        {
            byte[] b = System.Text.Encoding.UTF8.GetBytes("<ms2><environment feature=\"A\"><function name=\"OpenGachaBox\" parameter=\"1000001,5009\" /></environment>"
                + "<environment feature=\"B\"><function parameter='1000002' name='OpenGachaBox'/></environment><function name=\"ChatEmoticonAdd\" parameter=\"1\" /></ms2>");
            List<GachaCapsule> l = GachaCapsules.Read("item/00/00/00005101.xml", b);
            Assert.That(l.Select(c => c.Gacha), Is.EqualTo(new long[] { 1000001, 1000002 }));
            Assert.That(l[0].Item, Is.EqualTo(5101));
            Assert.That(GachaCapsules.Read("item/00/00/00005101.xml", System.Text.Encoding.UTF8.GetBytes("<ms2 />")), Is.Empty);
            GachaCapsule bad = GachaCapsules.Parse(5101, "1000001,x", "e");
            Assert.That(bad.Problem, Does.Contain("\"x\""));
            Assert.That(bad.Gacha, Is.EqualTo(0));
            Assert.That(GachaCapsules.IsItemEntry("item/00/00/00005101.xml"), Is.True);
            Assert.That(GachaCapsules.IsItemEntry("itemdata/005.xml"), Is.False);
        }

        [Test]
        public void SidesPairAndReadEmptyAmountsPerSide()
        {
            using (var f = new FakeGacha())
            {
                Assert.That(GachaEditor.SideArchives(f.Data, null), Is.EqualTo(new[] { K, S }), "the server's lines are planned for the global client");
                var ed = new GachaEditor(f.Data, Empty());
                Assert.That(ed.Gachas.Count, Is.EqualTo(3));
                Gacha a = Find(ed, 1000001), c = Find(ed, 1000003);
                Assert.That(a.Rows[0], Is.Not.Null); Assert.That(a.Rows[1], Is.Not.Null);
                Assert.That(ed.Differs(a, GachaRules.Amount), Is.False, "the client reads its missing amount as 1, the server has 1");
                Assert.That(ed.Differs(c, GachaRules.Amount), Is.True, "empty: 1 on the client, 0 on the server");
                Assert.That(ed.Check(c), Has.Some.Contains("read coinItemAmount differently"));
                Assert.That(ed.Get(a, "randomBoxGroupDesc"), Is.EqualTo("made up"), "a client-only field is read from the client");
                Assert.That(ed.ServerGet(a, GachaRules.Amount), Is.EqualTo("1"));
                Assert.That(ed.Find("7002").Select(ed.Id), Is.EqualTo(new long[] { 1000002 }));
                Assert.That(ed.Check(a), Is.Empty);
            }
        }

        [Test]
        public void EditsGoToEverySideAndPlanClean()
        {
            using (var f = new FakeGacha())
            {
                Mod m = Empty();
                var ed = new GachaEditor(f.Data, m);
                Gacha a = Find(ed, 1000001);
                ed.Set(a, GachaRules.Amount, "5");
                ed.Set(a, "paymentIconTag", "5001");
                Assert.Throws<ModKitException>(() => ed.Set(a, GachaRules.Key, "99100001"), "the key of a shipped capsule");
                Plan p = PlanOf(f, m);
                Assert.That(Texts(p.Errors), Is.Empty);
                Assert.That(Texts(p.Warnings).Where(t => t.Contains("capsule")), Is.Empty);
                Assert.That(Fake.Text(p, K, GachaRules.Entry), Does.Contain("paymentIconTag=\"5001\""));
                Assert.That(Fake.Text(p, S, GachaRules.Entry), Does.Contain("coinItemAmount=\"5\""));
                Assert.That(Fake.Text(p, S, GachaRules.Entry), Does.Not.Contain("paymentIconTag"), "a client-only field stays off the server");
                Assert.That(Fake.Text(p, G, GachaRules.Entry), Does.Contain("coinItemAmount=\"5\""), "mirrored to the global client");
            }
        }

        [Test]
        public void ANewCapsuleNeedsAnItemThatOpensIt()
        {
            using (var f = new FakeGacha())
            {
                Mod m = Empty();
                var ed = new GachaEditor(f.Data, m);
                Assert.That(ed.NextIds(null, 2, null), Is.EqualTo(new long[] { 99100000, 99100001 }));
                Assert.Throws<ModKitException>(() => ed.Add(1000009, 7001, 5009, 1), "outside the band of new capsules");
                Gacha n = ed.Add(99100000, 7002, 5009, 2);
                Assert.Throws<ModKitException>(() => ed.Add(99100000, 7002, 5009, 2));
                Assert.That(ed.Check(n), Is.Empty);
                Assert.That(GachaEditor.AddedIds(m), Is.EqualTo(new long[] { 99100000 }));
                Assert.That(m.Ids.Any(r => r.Space == GachaRules.Space && r.From <= 99100000 && r.To >= 99100000), Is.True, "reserved");
                Plan p = PlanOf(f, m);
                Assert.That(Texts(p.Errors), Is.Empty);
                Assert.That(Texts(p.Warnings), Has.Some.Contains("capsule 99100000: no capsule item opens it"));

                // an item that opens it: the warning goes
                string item = "<ms2><environment><function name=\"OpenGachaBox\" parameter=\"99100000,5009\" /></environment></ms2>";
                m.Files["item/00/00/00005199.xml"] = System.Text.Encoding.UTF8.GetBytes(item);
                Mod m2 = Mod.Parse(m.ToXml().Replace("</mod>", "<entry archive=\"" + S + "\" name=\"item/00/00/00005199.xml\" file=\"item/00/00/00005199.xml\" /></mod>"), "t");
                m2.Files["item/00/00/00005199.xml"] = System.Text.Encoding.UTF8.GetBytes(item);
                Plan p2 = Planner.Build(f.Data, new[] { m2 }, new PlanOptions());
                Assert.That(Texts(p2.Warnings), Has.None.Contains("no capsule item opens it"));
                Assert.That(new GachaEditor(f.Data, m2).Capsules().Any(c => c.Item == 5199 && c.Gacha == 99100000), Is.True, "the editor reads the mod's item file");

                // cloned: the copy keeps the client-only fields on the client
                Gacha c2 = ed.Clone(Find(ed, 1000001), 99100001);
                Assert.That(ed.GetOn(c2, 0, "randomBoxGroupDesc"), Is.EqualTo("made up"));
                Assert.That(ed.GetOn(c2, 1, "randomBoxGroupDesc"), Is.Null);
                ed.Remove(c2);
                Assert.That(GachaEditor.AddedIds(m), Is.EqualTo(new long[] { 99100000 }));
            }
        }

        [Test]
        public void PlanReadsWhatTheServerCannot()
        {
            using (var f = new FakeGacha())
            {
                Mod m = Edit(S, "<change at=\"randomBox[randomBoxID=1000002]\"><set name=\"ui\" value=\"300\" /><set name=\"individualDropBoxID\" value=\"7999\" /></change>"
                    + "<change at=\"randomBox[randomBoxID=1000003]\"><set name=\"individualDropBoxID\" value=\"7003\" /><set name=\"coinItemID\" value=\"5999\" /><set name=\"coinItemAmount\" value=\"1\" /></change>");
                Plan p = PlanOf(f, m);
                List<string> e = Texts(p.Errors), w = Texts(p.Warnings);
                Assert.That(e, Has.Some.Contains("capsule 1000002: ui \"300\""));
                Assert.That(w, Has.Some.Contains("capsule 1000002: individualDropBoxID 7999 is not a drop box"));
                Assert.That(w, Has.Some.Contains("capsule 1000003: drop box 7003 has no group with rows"));
                Assert.That(w, Has.Some.Contains("capsule 1000003: coinItemID 5999 is not an item"));
                Assert.That(w, Has.Some.Matches("capsule 1000003: the .* client reads it differently: .*coinItemID \"5009\" \\(the server's \"5999\"\\)"));
            }
        }

        [Test]
        public void ARemovedCapsuleStillOpenedAndRefunded()
        {
            using (var f = new FakeGacha())
            {
                Mod m = Empty();
                var ed = new GachaEditor(f.Data, m);
                ed.Remove(Find(ed, 1000002));
                Plan p = PlanOf(f, m);
                List<string> w = Texts(p.Warnings);
                Assert.That(w, Has.Some.Contains("capsule 1000002 is removed: items players won from it no longer give coin 5009"));
                Assert.That(w, Has.Some.Contains("capsule 1000002 is removed, and item 5102 still opens it"));
                Assert.That(Texts(p.Errors), Is.Empty);
                ed.Restore(Find(ed, 1000002));
                Assert.That(Texts(PlanOf(f, m).Warnings).Where(t => t.Contains("capsule")), Is.Empty);
            }
        }

        [Test]
        public void AChangedCapsuleItemIsChecked()
        {
            using (var f = new FakeGacha())
            {
                string bad = "<ms2><environment><function name=\"OpenGachaBox\" parameter=\"1000001,abc\" /></environment></ms2>";
                string none = "<ms2><environment><function name=\"OpenGachaBox\" parameter=\"1000777\" /></environment></ms2>";
                Mod m = FakeGacha.ModOf("<entry archive=\"" + S + "\" name=\"item/00/00/00005101.xml\" file=\"a.xml\" /><entry archive=\"" + S + "\" name=\"item/00/00/00005102.xml\" file=\"b.xml\" />");
                m.Files["a.xml"] = System.Text.Encoding.UTF8.GetBytes(bad);
                m.Files["b.xml"] = System.Text.Encoding.UTF8.GetBytes(none);
                Plan p = Planner.Build(f.Data, new[] { m }, new PlanOptions());
                Assert.That(Texts(p.Errors), Has.Some.Contains("item 5101: its OpenGachaBox parameter \"1000001,abc\""));
                Assert.That(Texts(p.Warnings), Has.Some.Contains("item 5102 opens capsule 1000777"));
            }
        }

        [Test]
        public void OddsPerGroup()
        {
            using (var f = new FakeGacha())
            {
                var ed = new GachaEditor(f.Data, Empty());
                string why;
                List<GachaOddsGroup> odds = ed.Odds(Find(ed, 1000001), out why);
                Assert.That(why, Is.Null);
                Assert.That(odds.Count, Is.EqualTo(2));
                Assert.That(odds[0].TotalWeight, Is.EqualTo(40));
                Assert.That(odds[0].Rows.Select(r => r.Percent), Is.EqualTo(new double[] { 75, 25, 0 }));
                Assert.That(odds[0].JobWeights, Is.False);
                Assert.That(odds[1].JobWeights, Is.True, "smartDropRate and a job weight");
                Assert.That(odds[1].SmartGender, Is.True);
                Gacha n = ed.Add(99100000, 0, 5009, 1);
                Assert.That(ed.Odds(n, out why), Is.Null);
                Assert.That(why, Does.Contain("no drop box"));
            }
        }

        [Test]
        public void RealGacha()
        {
            string srv = RealData.Server, cli = RealData.Client;
            if (string.IsNullOrEmpty(srv) || !File.Exists(Path.Combine(srv, "Xml.m2d"))) Assert.Ignore("MS2_SERVER_DATA is not set");
            using (var data = new DataSet(string.IsNullOrEmpty(cli) || !File.Exists(Path.Combine(cli, "Xml.m2d")) ? null : cli, null, srv))
            {
                Mod m = Empty();
                var ed = new GachaEditor(data, m);
                Assert.That(ed.Gachas, Is.Not.Empty);
                List<GachaCapsule> caps = ed.Capsules();
                Assert.That(caps, Is.Not.Empty);
                Assert.That(caps.Where(c => c.Problem != null), Is.Empty, "the shipped capsule items read");
                var errors = new List<string>();
                foreach (Gacha g in ed.Gachas)
                {
                    var e = new List<string>();
                    GachaRules.Check(x => ed.ServerGet(g, x), e, new List<string>());
                    errors.AddRange(e.Select(s => ed.Id(g) + ": " + s));
                }
                Assert.That(errors, Is.Empty, "the shipped rows read as the server reads them");
                Gacha opened = ed.Gachas.First(g => g.Rows.Any(r => r != null) && caps.Any(c => c.Gacha == ed.Id(g)) && ed.ServerGet(g, GachaRules.DropBox) != "0");
                string why;
                List<GachaOddsGroup> odds = ed.Odds(opened, out why);
                Assert.That(odds, Is.Not.Null, why);
                ed.Set(opened, GachaRules.Amount, "7");
                long free = ed.NextIds(null, 1, null)[0];
                ed.Clone(opened, free);
                Plan p = Planner.Build(data, new[] { Mod.Parse(m.ToXml(), "round trip") }, new PlanOptions());
                Assert.That(Texts(p.Errors).Where(s => s.Contains("capsule")), Is.Empty);
                Assert.That(Texts(p.Warnings), Has.Some.Contains("capsule " + free + ": no capsule item opens it"));
                TestContext.Progress.WriteLine(ed.Gachas.Count + " capsules, " + caps.Count + " capsule items; " + ed.Id(opened) + " has " + odds.Count + " groups; copied to " + free + "; "
                    + p.Warnings.Count + " warnings: " + string.Join(" | ", Texts(p.Warnings).Where(s => s.Contains("capsule")).Take(6)));
            }
        }
    }
}
