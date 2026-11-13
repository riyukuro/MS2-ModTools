// Enchant bonuses (Enchant.cs, PlannerEnchant.cs) on a made-up server table: three ladders of +1 to +15.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    public sealed class FakeEnchant : IDisposable
    {
        public readonly string Dir;
        public readonly DataSet Data;
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        // slot 30 rank 4 levels 50-59 (ids 1000101-115), slot 30 rank 4 levels 60-70 (1000201-215), slot 14 rank 4 levels 60-70 (1000301-315)
        static string Ladder(int first, int slot, int lo, int hi, string option)
        {
            string s = "";
            for (int g = 1; g <= 15; g++)
                s += "\t<option id=\"" + (first + g) + "\" slot=\"" + slot + "\" grade=\"" + g + "\" rank=\"4\" rate=\"" + (g * 0.01).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                    + "\" minLv=\"" + lo + "\" maxLv=\"" + hi + "\" option=\"" + option + "\" />\n";
            return s;
        }

        public static readonly string Table = Head + "<ms2>\n" + Ladder(1000100, 30, 50, 59, "27, 28") + Ladder(1000200, 30, 60, 70, "27, 28") + Ladder(1000300, 14, 60, 70, "20") + "</ms2>\n";

        public FakeEnchant()
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-enchant-" + Guid.NewGuid().ToString("N"));
            string s = Path.Combine(Dir, "server");
            Directory.CreateDirectory(s);
            M2dWriter.Create(Path.Combine(s, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                new KeyValuePair<string, byte[]>("table/itemoptionconstant.xml", Fake.Crlf(Head + "<ms2 />\n")),
            });
            M2dWriter.Create(Path.Combine(s, "Server.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                new KeyValuePair<string, byte[]>(EnchantRules.Entry, Fake.Crlf(Table)),
            });
            Data = new DataSet(null, null, s);
        }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class EnchantTests
    {
        const string S = EnchantRules.Archive;

        static Mod Empty() { return FakeGacha.ModOf("", "enchant.test"); }

        static Mod Edit(string body)
        {
            return FakeGacha.ModOf("<edit archive=\"" + S + "\" entry=\"" + EnchantRules.Entry + "\">" + body + "</edit>", "enchant.test");
        }

        static Plan PlanOf(FakeEnchant f, Mod m) { return Planner.Build(f.Data, new[] { Mod.Parse(m.ToXml(), "round trip") }, new PlanOptions()); }

        static List<string> Texts(List<PlanMessage> l) { return l.Select(m => m.ToString()).ToList(); }

        static List<string> Enchant(List<PlanMessage> l) { return Texts(l).Where(t => t.Contains("enchant")).ToList(); }

        static EnchantLadder Find(EnchantEditor ed, long slot, long lo)
        {
            return ed.Ladders.Single(l => l.Slot == slot && l.MinLv == lo && l.Rank == 4);
        }

        [Test]
        public void RulesReadAsTheServerReads()
        {
            var row = new Dictionary<string, string> { { "id", "1000101" }, { "slot", "30" }, { "grade", "1" }, { "rank", "4" }, { "rate", "0.02" }, { "minLv", "50" }, { "maxLv", "59" }, { "option", "27, 28" } };
            Func<string, string> get = n => { string v; return row.TryGetValue(n, out v) ? v : null; };
            Assert.That(EnchantRules.Check(get), Is.Empty);
            row["grade"] = ""; row["rate"] = "x"; row["option"] = "27, a"; row["rank"] = "+4 ";
            var e = new List<string>(); var w = new List<string>();
            EnchantRules.Check(get, e, w);
            Assert.That(e, Has.Some.StartsWith("grade \"\""), "an empty int stops the ingest");
            Assert.That(e, Has.Some.StartsWith("rate \"x\""));
            Assert.That(e, Has.Some.StartsWith("option has the part \" a\""));
            Assert.That(e, Has.None.StartsWith("rank"), "a sign and white space read");
            row.Remove("grade"); row["rate"] = "0.0125"; row["option"] = "27, 27, 40, 300"; row["slot"] = "99"; row["minLv"] = "60";
            List<string> all = EnchantRules.Check(get);
            Assert.That(all, Has.Some.StartsWith("grade is 0"), "a missing int is 0");
            Assert.That(all, Has.Some.Contains("finer than 0.001"));
            Assert.That(all, Has.Some.StartsWith("stat code 27 is named twice"));
            Assert.That(all, Has.Some.StartsWith("stat code 40 is no stat"));
            Assert.That(all, Has.Some.StartsWith("stat code 300 is not one the server reads"));
            Assert.That(all, Has.Some.StartsWith("slot 99 is no item type"));
            Assert.That(all, Has.Some.StartsWith("minLv 60 is above maxLv 59"));
            row["rate"] = "INF";
            Assert.That(EnchantRules.Check(get), Has.Some.Contains("no finite number"));
            Assert.That(EnchantRules.StatsText("27, 28"), Is.EqualTo("min weapon attack, max weapon attack"));
            Assert.That(EnchantRules.Uncovered(new[] { new long[] { 50, 59 }, new long[] { 62, 70 } }, 40, 80).Select(u => u[0] + "-" + u[1]), Is.EqualTo(new[] { "40-49", "60-61", "71-80" }));
        }

        [Test]
        public void LaddersAddUp()
        {
            using (var f = new FakeEnchant())
            {
                var ed = new EnchantEditor(f.Data, Empty());
                Assert.That(ed.Ladders.Count, Is.EqualTo(3));
                EnchantLadder l = Find(ed, 30, 50);
                Assert.That(l.Rows.Count, Is.EqualTo(15));
                Assert.That(ed.Total(l, 3), Is.EqualTo(0.06).Within(1e-6));
                Assert.That(ed.Total(l, 15), Is.EqualTo(1.2).Within(1e-6));
                Assert.That(ed.Missing(l), Is.Empty);
                Assert.That(ed.Find(30, 0, 55), Is.EqualTo(new[] { l }));
                Assert.That(ed.Find(0, 4, 65).Count, Is.EqualTo(2));
                Assert.That(ed.Check(l), Is.Empty);
                Assert.That(l.Label, Is.EqualTo("bludgeon (30) rank 4, level 50-59"));
                Assert.That(Enchant(PlanOf(f, Empty()).Warnings), Is.Empty);
            }
        }

        [Test]
        public void ScaleAndRatePlanClean()
        {
            using (var f = new FakeEnchant())
            {
                Mod m = Empty();
                var ed = new EnchantEditor(f.Data, m);
                EnchantLadder l = Find(ed, 30, 50);
                Assert.That(ed.Scale(new[] { l }, 1.5), Is.EqualTo(15));
                Assert.That(ed.Get(ed.Row(l, 1), "rate"), Is.EqualTo("0.015"));
                Assert.That(ed.Get(ed.Row(l, 3), "rate"), Is.EqualTo("0.045"));
                ed.SetRate(l, 15, 0.5);
                Assert.That(ed.IsChanged(l), Is.True);
                Assert.That(ed.IsChanged(Find(ed, 30, 60)), Is.False);
                Assert.Throws<ModKitException>(() => ed.Set(ed.Row(l, 1), "id", "99200000"), "the key of a shipped row");
                Plan p = PlanOf(f, m);
                Assert.That(Texts(p.Errors), Is.Empty);
                Assert.That(Enchant(p.Warnings), Is.Empty);
                string text = Fake.Text(p, S, EnchantRules.Entry);
                Assert.That(text, Does.Contain("id=\"1000115\" slot=\"30\" grade=\"15\" rank=\"4\" rate=\"0.5\""));
                Assert.That(text, Does.Contain("id=\"1000201\" slot=\"30\" grade=\"1\" rank=\"4\" rate=\"0.01\""), "another ladder as shipped");
                ed.Revert(l);
                Assert.That(ed.IsChanged(l), Is.False);
            }
        }

        [Test]
        public void ARaisedLevelCapAndALostBand()
        {
            using (var f = new FakeEnchant())
            {
                Mod m = Empty();
                var ed = new EnchantEditor(f.Data, m);
                ed.SetLadders(ed.Find(0, 4, 65), "maxLv", 80);
                Plan p = PlanOf(f, m);
                Assert.That(Texts(p.Errors), Is.Empty);
                Assert.That(Enchant(p.Warnings), Is.Empty, "a band that grows loses nothing");
                Assert.That(Fake.Text(p, S, EnchantRules.Entry), Does.Contain("minLv=\"60\" maxLv=\"80\""));

                // the band starts at 62: levels 60 and 61 lose every grade
                ed.SetLadders(new[] { Find(ed, 14, 60) }, "minLv", 62);
                Assert.That(Find(ed, 14, 62).MinLv, Is.EqualTo(62));
                List<string> w = Enchant(PlanOf(f, m).Warnings);
                Assert.That(w, Has.Some.Contains("enchant bonus of clothes (14) rank 4, level 60-61: no row for +1, +2"));
                Assert.That(w.Count, Is.EqualTo(1), string.Join(" | ", w));
                Assert.Throws<ModKitException>(() => ed.SetLadders(new[] { Find(ed, 14, 62) }, "rate", 1));
            }
        }

        [Test]
        public void ARemovedRowAndAnAddedGrade()
        {
            using (var f = new FakeEnchant())
            {
                Mod m = Empty();
                var ed = new EnchantEditor(f.Data, m);
                EnchantLadder l = Find(ed, 30, 60);
                ed.Remove(ed.Row(l, 15));
                Assert.That(ed.Missing(l), Is.EqualTo(new long[] { 15 }));
                Assert.That(ed.Check(l), Has.Some.StartsWith("no row for +15"));
                Plan p = PlanOf(f, m);
                Assert.That(Enchant(p.Warnings), Has.Some.Contains("enchant bonus of bludgeon (30) rank 4, level 60-70: no row for +15 after the mods"));

                Assert.That(ed.NextIds(null, 2, null), Is.EqualTo(new long[] { 99200000, 99200001 }));
                Assert.Throws<ModKitException>(() => ed.AddGrade(l, 15, 0.2, 1000999), "outside the range of new rows");
                Assert.Throws<ModKitException>(() => ed.AddGrade(l, 14, 0.2, 99200000), "+14 is there");
                TableRow n = ed.AddGrade(l, 15, 0.2, 99200000);
                Assert.That(ed.Get(n, "option"), Is.EqualTo("27, 28"), "the ladder's stats");
                Assert.That(ed.Row(l, 15), Is.SameAs(n));
                Assert.That(ed.Check(l), Is.Empty);
                Assert.That(EnchantEditor.AddedIds(m), Is.EqualTo(new long[] { 99200000 }));
                Assert.That(m.Ids.Any(r => r.Space == EnchantRules.Space && r.From <= 99200000 && r.To >= 99200000), Is.True, "reserved");
                p = PlanOf(f, m);
                Assert.That(Texts(p.Errors), Is.Empty);
                Assert.That(Enchant(p.Warnings), Is.Empty);
                Assert.That(Fake.Text(p, S, EnchantRules.Entry), Does.Contain("id=\"99200000\" slot=\"30\" grade=\"15\""));
                ed.Remove(n);
                Assert.That(EnchantEditor.AddedIds(m), Is.Empty);
                Assert.That(ed.NextIds(null, 1, null), Is.EqualTo(new long[] { 99200000 }), "the id is free again");
            }
        }

        [Test]
        public void ACopyForAnotherItemAndShadows()
        {
            using (var f = new FakeEnchant())
            {
                Mod m = Empty();
                var ed = new EnchantEditor(f.Data, m);
                EnchantLadder src = Find(ed, 30, 50);
                List<long> ids = ed.NextIds(null, 15, null);
                Assert.Throws<ModKitException>(() => ed.CopyLadder(src, 31, 4, 50, 59, ids.Take(3).ToList()));
                EnchantLadder c = ed.CopyLadder(src, 31, 4, 50, 59, ids);
                Assert.That(ed.IsAdded(c), Is.True);
                Assert.That(c.Rows.Select(r => ed.Int(r, "grade")), Is.EqualTo(Enumerable.Range(1, 15).Select(i => (long)i)));
                Assert.That(ed.Ladders.IndexOf(c), Is.EqualTo(ed.Ladders.IndexOf(src) + 1));
                Assert.That(ed.Check(c), Is.Empty);
                Plan p = PlanOf(f, m);
                Assert.That(Texts(p.Errors), Is.Empty);
                Assert.That(Enchant(p.Warnings), Is.Empty);
                string text = Fake.Text(p, S, EnchantRules.Entry);
                Assert.That(text, Does.Contain("id=\"99200000\" slot=\"31\" grade=\"1\" rank=\"4\" rate=\"0.01\" minLv=\"50\" maxLv=\"59\" option=\"27, 28\""));
                Assert.That(text.IndexOf("id=\"99200014\""), Is.GreaterThan(text.IndexOf("id=\"99200000\"")), "in grade order");
                Assert.That(text.IndexOf("id=\"99200000\""), Is.GreaterThan(text.IndexOf("id=\"1000115\"")).And.LessThan(text.IndexOf("id=\"1000201\"")), "after the source");

                // a copy for levels 55-65 of the same items: the 50-59 rows come first, and it hides 60-65 of the 60-70 rows
                ed.Remove(c);
                EnchantLadder o = ed.CopyLadder(src, 30, 4, 55, 65, ed.NextIds(null, 15, null));
                List<string> msgs = ed.Check(o);
                Assert.That(msgs, Has.Some.Matches(@"\+1 \(id 99200000\) is not used for level 55-59: row 1000101 .* comes first"));
                List<string> w = Enchant(PlanOf(f, m).Warnings);
                Assert.That(w, Has.Some.Contains("enchant row 99200000 (bludgeon (30) rank 4, level 55-65, +1): the server does not use it for level 55-59: enchant row 1000101"));
                Assert.That(w, Has.Some.Contains("enchant row 1000201 (bludgeon (30) rank 4, level 60-70, +1): the server does not use it for level 60-65: enchant row 99200000"));
            }
        }

        [Test]
        public void PlanStopsOnWhatTheIngestCannotRead()
        {
            using (var f = new FakeEnchant())
            {
                Mod m = Edit("<change at=\"option[id=1000101]\"><set name=\"rate\" value=\"abc\" /></change>"
                    + "<change at=\"option[id=1000102]\"><set name=\"option\" value=\"27,x\" /></change>"
                    + "<change at=\"option[id=1000103]\"><set name=\"grade\" value=\"\" /></change>"
                    + "<change at=\"option[id=1000104]\"><set name=\"rate\" value=\"0\" /></change>"
                    + "<add><xml><option id=\"1000201\" slot=\"14\" grade=\"1\" rank=\"5\" rate=\"0.01\" minLv=\"60\" maxLv=\"70\" option=\"20\" /></xml></add>");
                Plan p = PlanOf(f, m);
                List<string> e = Texts(p.Errors), w = Texts(p.Warnings);
                Assert.That(e, Has.Some.Contains("enchant row 1000101 (bludgeon (30) rank 4, level 50-59, +1): rate \"abc\""));
                Assert.That(e, Has.Some.Contains("enchant row 1000102 (bludgeon (30) rank 4, level 50-59, +2): option has the part \"x\""));
                Assert.That(e, Has.Some.Contains("enchant row 1000103 (bludgeon (30) rank 4, level 50-59, +0): grade \"\""));
                Assert.That(e, Has.Some.Contains("enchant row id 1000201 is on 2 rows"));
                Assert.That(w, Has.Some.Contains("enchant row 1000104 (bludgeon (30) rank 4, level 50-59, +4): rate is 0"));
                Assert.That(w, Has.Some.Contains("enchant bonus of bludgeon (30) rank 4, level 50-59: no row for +3 after the mods"));

                Mod gone = FakeGacha.ModOf("<edit archive=\"" + S + "\" entry=\"" + EnchantRules.Entry + "\"><delete at=\"option[id=1000101]\" /></edit>", "enchant.test");
                Assert.That(Enchant(PlanOf(f, gone).Warnings), Has.Some.Contains("level 50-59: no row for +1 after the mods"));
            }
        }

        [Test]
        public void IdSpaceOfNewRows()
        {
            long from, to;
            IdSpaces.DefaultRange(EnchantRules.Space, out from, out to);
            Assert.That(from, Is.EqualTo(EnchantRules.From));
            Assert.That(to, Is.EqualTo(EnchantRules.To));
            List<IdPlace> places = IdSpaces.GroupPlaces(EnchantRules.Space);
            Assert.That(places.Count, Is.EqualTo(1));
            Assert.That(places[0].Entry, Is.EqualTo(EnchantRules.Entry));
            using (var f = new FakeEnchant())
            {
                Mod other = FakeGacha.ModOf("<edit archive=\"" + S + "\" entry=\"" + EnchantRules.Entry + "\"><add><xml><option id=\"99200000\" slot=\"30\" grade=\"1\" rank=\"4\" rate=\"0.01\" minLv=\"71\" maxLv=\"80\" option=\"27\" /></xml></add></edit>", "other");
                var ed = new EnchantEditor(f.Data, Empty()) { Others = () => new[] { other } };
                Assert.That(ed.NextIds(new[] { other }, 1, null), Is.EqualTo(new long[] { 99200001 }));
                Assert.Throws<ModKitException>(() => ed.AddGrade(Find(ed, 30, 50), 1, 0.1, 99200000), "+1 is there");
                ed.Remove(ed.Row(Find(ed, 30, 50), 1));
                var ex = Assert.Throws<ModKitException>(() => ed.AddGrade(Find(ed, 30, 50), 1, 0.1, 99200000));
                Assert.That(ex.Message, Does.Contain("the enabled mod other adds"));
            }
        }

        [Test]
        public void RealEnchant()
        {
            string srv = RealData.Server;
            if (string.IsNullOrEmpty(srv) || !File.Exists(Path.Combine(srv, "Server.m2d"))) Assert.Ignore("MS2_SERVER_DATA is not set");
            using (var data = new DataSet(null, null, srv))
            {
                Mod m = Empty();
                var ed = new EnchantEditor(data, m);
                Assert.That(ed.Ladders, Is.Not.Empty);
                var errors = new List<string>();
                foreach (EnchantLadder l in ed.Ladders)
                    foreach (TableRow r in l.Rows)
                    {
                        var e = new List<string>();
                        TableRow row = r;
                        EnchantRules.Check(x => ed.Get(row, x), e, new List<string>());
                        errors.AddRange(e.Select(s => ed.Id(r) + ": " + s));
                    }
                Assert.That(errors, Is.Empty, "the shipped rows read as the server reads them");
                Assert.That(Enchant(Planner.Build(data, new[] { Mod.Parse(m.ToXml(), "round trip") }, new PlanOptions()).Warnings), Is.Empty);
                EnchantLadder top = ed.Ladders.OrderByDescending(l => l.MaxLv).First();
                long cap = top.MaxLv;
                List<EnchantLadder> capped = ed.Ladders.Where(l => l.MaxLv == cap).ToList();
                ed.SetLadders(capped, "maxLv", cap + 10);
                ed.Scale(new[] { top }, 2);
                EnchantLadder copy = ed.CopyLadder(top, top.Slot, top.Rank, cap + 11, cap + 20, ed.NextIds(null, top.Rows.Count, null));
                Plan p = Planner.Build(data, new[] { Mod.Parse(m.ToXml(), "round trip") }, new PlanOptions());
                Assert.That(Texts(p.Errors).Where(s => s.Contains("enchant")), Is.Empty);
                Assert.That(Enchant(p.Warnings), Is.Empty, "a raised cap and a band above it lose no level");
                TestContext.Progress.WriteLine(ed.Ladders.Count + " ladders; " + capped.Count + " capped at " + cap + " raised to " + (cap + 10) + "; " + top.Label + " at +15 "
                    + EnchantRules.Percent(ed.Total(top, 15)) + "; copied to " + copy.Label);
            }
        }
    }
}
