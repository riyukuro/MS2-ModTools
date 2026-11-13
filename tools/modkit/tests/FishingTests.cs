using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    /// <summary>A data set with the five fishing tables on the server, the server's features and the client's fish and spot tables. Every id is made up.</summary>
    public sealed class FakeFishing : IDisposable
    {
        public readonly string Dir;
        public readonly DataSet Data;
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        public const string Fish = Head + "<ms2>\n"
            + "\t<fish id=\"9100001\" fishingBook=\"1\" habitat=\"water\" fishMastery=\"10\" lv=\"1\" rank=\"1\" pointCount=\"5\" masteryPoint=\"1\" exp=\"10\" baitProp=\"5000\" smallSize=\"10-20\" bigSize=\"30-40\" />\n"
            + "\t<fish id=\"9100002\" fishingBook=\"1\" habitat=\"seawater\" fishMastery=\"50\" lv=\"1\" rank=\"2\" pointCount=\"5\" masteryPoint=\"1\" exp=\"10\" baitProp=\"5000\" smallSize=\"10-20\" bigSize=\"30-40\" />\n"
            + "</ms2>\n";
        public const string Spots = Head + "<ms2>\n"
            + "\t<spot id=\"9900001\" minMastery=\"0\" maxMastery=\"100\" liquidType=\"water,seawater\" globalFishBoxID=\"9200001\" individualFishBoxID=\"9300001\" spotLevel=\"1\" spotDropRank=\"1\" />\n"
            + "</ms2>\n";
        public const string Lures = Head + "<ms2>\n"
            + "\t<lure additionalEffectCode=\"9700001\" additionalEffectLevel=\"1\" catchRank=\"1,2\" catchProp=\"100,200\" />\n"
            + "</ms2>\n";
        public const string GlobalBoxes = Head + "<ms2>\n"
            + "\t<box id=\"9200001\" probability=\"10000\" cubeRate=\"10000\">\n"
            + "\t\t<fish fishCode=\"9100001\" weight=\"10\" weightRate=\"0\" />\n"
            + "\t\t<fish fishCode=\"9100002\" weight=\"5\" weightRate=\"0\" />\n"
            + "\t</box>\n</ms2>\n";
        public const string IndividualBoxes = Head + "<ms2>\n"
            + "\t<box id=\"9300001\" probability=\"5000\" cubeRate=\"10000\">\n"
            + "\t\t<fish fishCode=\"9100001\" weight=\"1\" weightRate=\"0\" />\n"
            + "\t</box>\n</ms2>\n";
        // the server's locale is NA, its type Live (level 5): Low is on, High is off
        public const string Setting = Head + "<ms2>\n\t<setting type=\"Live\" KR=\"10\" NA=\"5\" />\n</ms2>\n";
        public const string Features = Head + "<ms2>\n\t<feature name=\"Low\" KR=\"3\" NA=\"3\" />\n\t<feature name=\"High\" KR=\"9\" NA=\"9\" />\n</ms2>\n";
        public const string ClientFish = Head + "<ms2>\n"
            + "\t<fish id=\"9100001\" fishingBook=\"1\" habitat=\"water\" fishMastery=\"10\" rank=\"1\" smallSize=\"10-20\" bigSize=\"30-40\" />\n"
            + "\t<fish id=\"9100002\" fishingBook=\"1\" habitat=\"seawater\" fishMastery=\"50\" rank=\"2\" smallSize=\"10-20\" bigSize=\"30-40\" />\n"
            + "</ms2>\n";
        public const string ClientSpots = Head + "<ms2>\n\t<spot id=\"9900001\" minMastery=\"0\" maxMastery=\"100\" liquidType=\"water,seawater\" />\n</ms2>\n";

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        public FakeFishing()
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-fishing-" + Guid.NewGuid().ToString("N"));
            string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
            foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);
            M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E(FishingRules.ClientFishEntry, ClientFish), E(FishingRules.ClientSpotEntry, ClientSpots),
            });
            M2dWriter.Create(Path.Combine(g, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E(FishingRules.ClientFishEntry, ClientFish), E(FishingRules.ClientSpotEntry, ClientSpots),
            });
            M2dWriter.Create(Path.Combine(s, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E(FeatureSet.SettingTable, Setting), E(FeatureSet.FeatureTable, Features),
            });
            M2dWriter.Create(Path.Combine(s, "Server.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E(FishingRules.FishEntry, Fish), E(FishingRules.SpotEntry, Spots), E(FishingRules.LureEntry, Lures),
                E(FishingRules.GlobalBoxEntry, GlobalBoxes), E(FishingRules.IndividualBoxEntry, IndividualBoxes),
            });
            Data = new DataSet(k, g, s);
        }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class FishingTests
    {
        static Mod Empty() { return FakeRooms.ModOf("", "fishing.test"); }

        static string Body(Mod m)
        {
            string x = m.ToXml();
            int a = x.IndexOf('>', x.IndexOf("<mod", StringComparison.Ordinal)) + 1, b = x.LastIndexOf("</mod>", StringComparison.Ordinal);
            return x.Substring(a, b - a);
        }

        static Mod Reload(Mod m) { return FakeRooms.ModOf(Body(m), "fishing.test"); }
        static Plan PlanOf(FakeFishing f, Mod m) { return Planner.Build(f.Data, new[] { Reload(m) }, new PlanOptions()); }

        static List<string> Lines(IEnumerable<PlanMessage> msgs)
        {
            var l = new List<string>();
            foreach (PlanMessage m in msgs) l.Add(m.ToString());
            return l;
        }

        static List<string> With(IEnumerable<PlanMessage> msgs, string text) { return Lines(msgs).Where(s => s.Contains(text)).ToList(); }

        [Test]
        public void RulesReadValuesAsTheServerDoes()
        {
            int a, b;
            Assert.That(FishingRules.Size("10-20", out a, out b) && a == 10 && b == 20, Is.True);
            Assert.That(FishingRules.Size("10-20-30", out a, out b) && b == 20, Is.True);      // the third part is read, not used
            Assert.That(FishingRules.Size("10", out a, out b), Is.False);
            Assert.That(FishingRules.Size("10- 20", out a, out b), Is.True);                  // int.Parse takes the space
            Assert.That(FishingRules.Size("10-x", out a, out b), Is.False);
            Assert.That(FishingRules.IntList(" 1, 2,,3"), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(FishingRules.IntList("1, ,2"), Is.Null);                               // a part of only spaces stops the ingest
            Assert.That(FishingRules.LiquidOf("water,poison"), Is.EqualTo(5));
            Assert.That(FishingRules.LiquidOf(" lava "), Is.EqualTo(3));
            Assert.That(FishingRules.LiquidOf("Water"), Is.EqualTo(-1));                       // the names are lower case
            Assert.That(FishingRules.HabitatOf("Water"), Is.EqualTo(FishingRules.All));
            Assert.That(FishingRules.LiquidsOf("water,mud,lava"), Is.EqualTo(new[] { 1, 3 }));
            Assert.That(FishingRules.ValueProblem(FishTable.Fish, "rank", "40000"), Does.Contain("32767"));
            Assert.That(FishingRules.ValueProblem(FishTable.Fish, "fishingBook", "yes"), Does.Contain("1, 0, true or false"));
            Assert.That(FishingRules.ValueProblem(FishTable.Fish, "lv", " 3 "), Is.Null);
            Assert.That(FishingRules.ValueProblem(FishTable.Fish, "lv", "   "), Is.Not.Null);
            Assert.That(FishingRules.ValueProblem(FishTable.Fish, "lv", ""), Is.Null);         // "" counts as not written
            Assert.That(FishingRules.ValueProblem(FishTable.Lure, "globalDropFishingSpotMastery", "0-1189"), Is.Null);   // a "-" list
            Assert.That(FishingRules.ValueProblem(FishTable.Lure, "globalDropFishingSpotMastery", "0,1189"), Is.Not.Null);
            Assert.That(FishingRules.Twice(new[] { "1", "2", " 1", "3", "2" }), Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public void TablesAreListedWithTheirLinks()
        {
            using (var f = new FakeFishing())
            {
                var ed = new FishingEditor(f.Data, Empty());
                Assert.That(ed.Rows(FishTable.Fish).Count, Is.EqualTo(2));
                Assert.That(ed.Rows(FishTable.Lure).Count, Is.EqualTo(1));
                FishRow box = ed.Find(FishTable.GlobalBox, 9200001);
                Assert.That(ed.Codes(box), Is.EqualTo(new[] { "9100001", "9100002" }));
                Assert.That(ed.BoxesWith(9100001).Count, Is.EqualTo(2));
                Assert.That(ed.BoxesWith(9100002).Count, Is.EqualTo(1));
                Assert.That(ed.SpotsUsing(box).Count, Is.EqualTo(1));
                Assert.That(ed.Find(FishTable.GlobalBox, "9100002").Count, Is.EqualTo(1));      // through a line
                foreach (FishTable t in FishingRules.Tables)
                    foreach (FishRow r in ed.Rows(t)) Assert.That(ed.Check(r), Is.Empty, FishingRules.Noun(t) + " " + ed.IdText(r));
                foreach (FishLine l in ed.Lines(box)) Assert.That(ed.Check(l), Is.Empty);
                Assert.That(ed.FreeId(FishTable.Fish), Is.EqualTo(9100003));
                Assert.That(PlanOf(f, Empty()).Warnings, Is.Empty);
            }
        }

        [Test]
        public void SizesAndPointCountThatStopTheServerAreErrors()
        {
            using (var f = new FakeFishing())
            {
                Mod m = Empty();
                var ed = new FishingEditor(f.Data, m);
                FishRow fish = ed.Find(FishTable.Fish, 9100001);
                ed.Set(fish, "smallSize", "20-35");
                ed.Set(fish, "pointCount", "0");
                Assert.That(ed.Check(fish).Count(s => s.StartsWith("error: ", StringComparison.Ordinal)), Is.EqualTo(2));
                Plan p = PlanOf(f, m);
                Assert.That(With(p.Errors, "fish 9100001: smallSize max 35 is above bigSize min 30"), Has.Count.EqualTo(1));
                Assert.That(With(p.Errors, "fish 9100001: pointCount is 0"), Has.Count.EqualTo(1));
                ed.Set(fish, "smallSize", "");
                Assert.That(With(PlanOf(f, m).Errors, "smallSize is not set"), Has.Count.EqualTo(1));
                ed.Set(fish, "bigSize", "30-1073741824");
                Assert.That(With(PlanOf(f, m).Errors, "bigSize max 1073741824 is too big"), Has.Count.EqualTo(1));
                ed.Revert(fish);
                Assert.That(PlanOf(f, m).Errors, Is.Empty);
                Assert.That(ed.IsChanged(fish), Is.False);
            }
        }

        [Test]
        public void ALureWithFewerChancesThanRanksIsAnError()
        {
            using (var f = new FakeFishing())
            {
                Mod m = Empty();
                var ed = new FishingEditor(f.Data, m);
                ed.Set(ed.Find(FishTable.Lure, 9700001), "catchProp", "100");
                Assert.That(With(PlanOf(f, m).Errors, "lure 9700001 level 1: catchRank has 2 numbers and catchProp 1"), Has.Count.EqualTo(1));
            }
        }

        [Test]
        public void ANewBoxWithItsLinesSurvivesAReload()
        {
            using (var f = new FakeFishing())
            {
                Mod m = Empty();
                var ed = new FishingEditor(f.Data, m);
                FishRow box = ed.Add(FishTable.GlobalBox, 9200002);
                ed.AddFish(box, 9100002, 7);
                ed.AddFish(box, 9100009, 3);
                ed.Set(box, "probability", "2500");
                var again = new FishingEditor(f.Data, Reload(m));
                FishRow b = again.Find(FishTable.GlobalBox, 9200002);
                Assert.That(b, Is.Not.Null);
                Assert.That(b.IsAdded, Is.True);
                Assert.That(again.Get(b, "probability"), Is.EqualTo("2500"));
                List<FishLine> lines = again.Lines(b);
                Assert.That(lines.Count, Is.EqualTo(2));
                Assert.That(again.Get(lines[0], "weight"), Is.EqualTo("7"));
                Assert.That(again.Check(lines[1]), Has.Some.Contains("there is no fish 9100009"));
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty);
                Assert.That(With(p.Warnings, "global fish box 9200002: no fishing spot names it"), Has.Count.EqualTo(1));
                Assert.That(With(p.Warnings, "global fish box 9200002: there is no fish 9100009"), Has.Count.EqualTo(1));
                // the spot names it: the warning goes
                ed.Set(ed.Find(FishTable.Spot, 9900001), "globalFishBoxID", "9200002");
                Assert.That(With(PlanOf(f, m).Warnings, "no fishing spot names it"), Is.Empty);
                // a copy takes the lines along
                FishRow copy = ed.Copy(ed.Find(FishTable.IndividualBox, 9300001), 9300002);
                Assert.That(ed.Codes(copy), Is.EqualTo(new[] { "9100001" }));
                Assert.Throws<ModKitException>(() => ed.Add(FishTable.GlobalBox, 9200001));
            }
        }

        [Test]
        public void LinesOfAShippedBoxAreEditedInPlace()
        {
            using (var f = new FakeFishing())
            {
                Mod m = Empty();
                var ed = new FishingEditor(f.Data, m);
                FishRow box = ed.Find(FishTable.GlobalBox, 9200001);
                List<FishLine> lines = ed.Lines(box);
                ed.Set(lines[0], "weight", "40");
                ed.AddFish(box, 9100002, 1);
                Assert.That(ed.IsChanged(box), Is.True);
                var again = new FishingEditor(f.Data, Reload(m));
                FishRow b = again.Find(FishTable.GlobalBox, 9200001);
                List<FishLine> l = again.Lines(b);
                Assert.That(l.Count, Is.EqualTo(3));
                Assert.That(again.Get(l[0], "weight"), Is.EqualTo("40"));
                Assert.That(again.IsEdited(l[0], "weight"), Is.True);
                Assert.That(With(PlanOf(f, m).Warnings, "global fish box 9200001: fish 9100002 has more than one line"), Has.Count.EqualTo(1));
                // removing the box takes back its line edits first
                ed.Remove(box);
                Assert.That(box.IsDeleted, Is.True);
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty);
                Assert.That(new FishingEditor(f.Data, Reload(m)).Find(FishTable.GlobalBox, 9200001), Is.Null);
                Assert.That(With(p.Warnings, "global fish box 9200001: the mod takes it away, and fishing spot 9900001 still names it"), Has.Count.EqualTo(1));
                ed.Restore(box);
                Assert.That(ed.Lines(box).Count, Is.EqualTo(2));
                Assert.That(PlanOf(f, m).Warnings, Is.Empty);
            }
        }

        [Test]
        public void ASpotOrFishThatCannotBeCaughtIsAWarning()
        {
            using (var f = new FakeFishing())
            {
                Mod m = Empty();
                var ed = new FishingEditor(f.Data, m);
                FishRow spot = ed.Find(FishTable.Spot, 9900001);
                ed.Set(spot, "liquidType", "lava,mud");
                Plan p = PlanOf(f, m);
                Assert.That(With(p.Warnings, "fishing spot 9900001: nothing can be caught"), Has.Count.EqualTo(1));
                Assert.That(With(p.Warnings, "fishing spot 9900001: liquidType \"mud\" is not a liquid the server knows"), Has.Count.EqualTo(1));
                Assert.That(With(p.Warnings, "the Korean client's table/fishingspot.xml has liquidType \"water,seawater\""), Has.Count.EqualTo(1));
                ed.Revert(spot);
                // on a spot of water and seawater a water tile counts as seawater: a water fish is not caught there
                FishRow fish = ed.Find(FishTable.Fish, 9100001);
                ed.Set(fish, "lv", "2");
                Assert.That(With(PlanOf(f, m).Warnings, "fish 9100001: no fishing spot gives it"), Has.Count.EqualTo(1));
                ed.Set(spot, "liquidType", "water");
                Assert.That(With(PlanOf(f, m).Warnings, "fish 9100001: no fishing spot gives it"), Is.Empty);
                ed.Set(fish, "habitat", "Water");
                p = PlanOf(f, m);
                Assert.That(With(p.Warnings, "fish 9100001: habitat \"Water\" is not a liquid the server knows"), Has.Count.EqualTo(1));
                Assert.That(With(p.Warnings, "fish 9100001: habitat all: the server gives a fish only on a tile"), Has.Count.EqualTo(1));
                Assert.That(With(p.Warnings, "the global client's table/fish.xml has habitat \"water\" (the server's \"Water\")"), Has.Count.EqualTo(1));
            }
        }

        [Test]
        public void ARowTheServerDoesNotKeepIsAWarning()
        {
            using (var f = new FakeFishing())
            {
                Mod m = Empty();
                var ed = new FishingEditor(f.Data, m);
                ed.Set(ed.Find(FishTable.Fish, 9100002), "feature", "High");
                Assert.That(With(PlanOf(f, m).Warnings, "fish 9100002: its feature \"High\" are off (locale NA, server type Live, level 5)"), Has.Count.EqualTo(1));
                ed.Set(ed.Find(FishTable.Fish, 9100002), "feature", "Low");
                Assert.That(With(PlanOf(f, m).Warnings, "fish 9100002: its feature"), Is.Empty);
                // a second level of a lure: the server keys lures by their code, so it keeps one of the two
                FishRow lure = ed.Copy(ed.Find(FishTable.Lure, 9700001), 9700001);
                ed.Set(lure, "additionalEffectLevel", "2");
                Assert.That(ed.Check(lure), Has.Some.Contains("the table has 2 rows with additionalEffectCode 9700001"));
                Assert.That(With(PlanOf(f, m).Warnings, "lure 9700001 level 2: the server keeps this row and leaves out the other row with additionalEffectCode 9700001"), Has.Count.EqualTo(1));
                // a row with a feature wins over one without: now the new level is the one left out
                ed.Set(ed.Rows(FishTable.Lure)[0], "feature", "Low");
                Plan p = PlanOf(f, m);
                Assert.That(With(p.Warnings, "lure 9700001 level 2: another row with additionalEffectCode 9700001 wins"), Has.Count.EqualTo(1));
                Assert.That(With(p.Warnings, "lure 9700001 level 1: the server keeps this row"), Has.Count.EqualTo(1));
            }
        }

        [Test]
        public void ANewFishNeedsABoxAndTheClientRow()
        {
            using (var f = new FakeFishing())
            {
                Mod m = Empty();
                var ed = new FishingEditor(f.Data, m);
                FishRow fish = ed.Add(FishTable.Fish, ed.FreeId(FishTable.Fish));
                Assert.That(ed.IdText(fish), Is.EqualTo("9100003"));
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty);
                Assert.That(With(p.Warnings, "fish 9100003: no fish box has a line for it"), Has.Count.EqualTo(1));
                Assert.That(With(p.Warnings, "fish 9100003: the Korean client's table/fish.xml has no row for it"), Has.Count.EqualTo(1));
                ed.AddFish(ed.Find(FishTable.IndividualBox, 9300001), 9100003, 2);
                Assert.That(With(PlanOf(f, m).Warnings, "no fish box has a line for it"), Is.Empty);
                ed.Remove(fish);
                Assert.That(ed.Find(FishTable.Fish, 9100003), Is.Null);
                Assert.That(With(PlanOf(f, m).Warnings, "individual fish box 9300001: there is no fish 9100003"), Has.Count.EqualTo(1));
            }
        }

        [Test]
        public void RealServerFishing()
        {
            string srv = RealData.Server;
            if (string.IsNullOrEmpty(srv) || !File.Exists(Path.Combine(srv, "Server.m2d"))) Assert.Ignore("MS2_SERVER_DATA is not set");
            using (var data = new DataSet(null, null, srv))
            {
                Mod m = Empty();
                var ed = new FishingEditor(data, m);
                foreach (FishTable t in FishingRules.Tables) Assert.That(ed.Rows(t), Is.Not.Empty, FishingRules.Entry(t));
                var errors = new List<string>();
                foreach (FishTable t in FishingRules.Tables)
                    foreach (FishRow r in ed.Rows(t))
                        foreach (string s in ed.Check(r)) if (s.StartsWith("error: ", StringComparison.Ordinal)) errors.Add(FishingRules.Noun(t) + " " + ed.IdText(r) + ": " + s);
                Assert.That(errors, Is.Empty, "the shipped rows read as the server reads them");
                // a fish edited, a box copied under a free id with a spot pointed at it
                FishRow fish = ed.Rows(FishTable.Fish).First(r => ed.BoxesWith(ed.Id(r)).Count > 0);
                ed.Set(fish, "exp", "1234");
                FishRow box = ed.Rows(FishTable.GlobalBox).First(b => ed.SpotsUsing(b).Count > 0);
                long free = ed.FreeId(FishTable.GlobalBox);
                FishRow copy = ed.Copy(box, free);
                ed.Set(ed.SpotsUsing(box)[0], "globalFishBoxID", free.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Plan p = Planner.Build(data, new[] { Mod.Parse(m.ToXml(), "round trip") }, new PlanOptions());
                Assert.That(Lines(p.Errors).Where(s => s.Contains("fish")), Is.Empty);
                var again = new FishingEditor(data, Mod.Parse(m.ToXml(), "round trip"));
                Assert.That(again.Codes(again.Find(FishTable.GlobalBox, free)), Is.EqualTo(ed.Codes(box)));
                TestContext.Progress.WriteLine(string.Join(", ", FishingRules.Tables.Select(t => ed.Rows(t).Count + " " + FishingRules.Noun(t))) + "; fish " + ed.IdText(fish)
                    + " edited, box " + ed.IdText(box) + " copied to " + free + "; " + p.Warnings.Count + " warnings");
            }
        }
    }
}
