using System;
using System.Collections.Generic;
using System.IO;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    /// <summary>A kms client, a global client and a server with a few made-up spawn rows and tagged NPCs.</summary>
    public sealed class FakeSpawns : IDisposable
    {
        public readonly string Dir;
        public readonly DataSet Data;
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        public static string Row(string map, string point, string tag, string pop, string extra = "", string diff = "1")
        {
            return "\t<region mapCode=\"" + map + "\" " + extra + "spawnPointID=\"" + point + "\" difficulty=\"" + diff + "\" difficultyMin=\"1\" tag=\"" + tag
                + "\" coolTime=\"30\" population=\"" + pop + "\" petPopulation=\"0\" petSpawnProbability=\"0\" />\n";
        }

        public static readonly string Kms = Head + "<ms2>\n" + Row("07100001", "10", "moss", "3") + Row("07100001", "11", "moss,reed", "2")
            + Row("07100002", "10", "reed", "1") + Row("07100003", "5", "flint", "4") + "</ms2>\n";
        public static readonly string Srv = Head + "<ms2>\n" + Row("07100001", "10", "moss", "3") + Row("07100001", "11", "moss,reed", "5")
            + Row("07100001", "11", "moss", "1", "locale=\"NA\" ") + Row("07100002", "10", "reed", "1") + Row("07100004", "1", "husk", "6") + "</ms2>\n";

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        public FakeSpawns() : this(true) { }

        public FakeSpawns(bool server) : this(server, Kms, Srv) { }

        /// <summary>With these spawn tables: kms for the Korean client, srv for the global client and the server.</summary>
        public FakeSpawns(bool server, string kms, string srv)
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-spawns-" + Guid.NewGuid().ToString("N"));
            string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
            foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);
            M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E(SpawnRules.Entry, kms),
                E("table/fielddata.xml", Head + "<ms2>\n\t<fieldData id=\"7100001\" />\n\t<fieldData id=\"7100002\" />\n\t<fieldData id=\"7100003\" />\n\t<fieldData id=\"7100009\" />\n</ms2>\n"),
                E("npcdata/npc_0.xml", Head + "<ms2>\n\t<npc id=\"66100001\">\n\t\t<environment>\n\t\t\t<basic mainTags=\"moss,reed\" subTags=\"\" />\n\t\t</environment>\n\t</npc>\n"
                    + "\t<npc id=\"66100002\">\n\t\t<environment>\n\t\t\t<basic mainTags=\"moss\" />\n\t\t</environment>\n\t</npc>\n</ms2>\n"),
            });
            foreach (string d in new[] { g, s })
            {
                var l = new List<KeyValuePair<string, byte[]>>
                {
                    E(SpawnRules.Entry, srv),
                    E("npc/66/10/66100001.xml", Head + "<ms2>\n\t<environment>\n\t\t<basic mainTags=\"moss,reed\" />\n\t</environment>\n</ms2>\n"),
                    E("npc/66/10/66100002.xml", Head + "<ms2>\n\t<environment>\n\t\t<basic mainTags=\"moss\" />\n\t</environment>\n</ms2>\n"),
                    E("npc/66/10/66100003.xml", Head + "<ms2>\n\t<environment>\n\t\t<basic mainTags=\"husk\" />\n\t</environment>\n</ms2>\n"),
                };
                foreach (long i in new long[] { 7100001, 7100002, 7100004, 7100009 }) l.Add(E("map/" + i.ToString("00000000") + ".xml", Head + "<ms2>\n\t<environment />\n</ms2>\n"));
                M2dWriter.Create(Path.Combine(d, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, l);
            }
            Data = server ? new DataSet(k, g, s) : new DataSet(k, g, null);
        }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class SpawnsTests
    {
        const string K = "client:Xml.m2d", G = "gms-client:Xml.m2d", S = "server:Xml.m2d";

        static Mod Empty() { return FakeRooms.ModOf("", "spawns.test"); }

        static string Body(Mod m)
        {
            string x = m.ToXml();
            int a = x.IndexOf('>', x.IndexOf("<mod", StringComparison.Ordinal)) + 1, b = x.LastIndexOf("</mod>", StringComparison.Ordinal);
            return x.Substring(a, b - a);
        }

        static Mod Reload(Mod m) { return FakeRooms.ModOf(Body(m), "spawns.test"); }
        static Plan PlanOf(FakeSpawns f, Mod m) { return Planner.Build(f.Data, new[] { Reload(m) }, new PlanOptions()); }

        static List<string> Lines(IEnumerable<PlanMessage> msgs)
        {
            var l = new List<string>();
            foreach (PlanMessage m in msgs) l.Add(m.ToString());
            return l;
        }

        static SpawnRow Find(SpawnEditor ed, long map, string point, string locale = "")
        {
            foreach (SpawnRow s in ed.Of(map)) if (ed.Get(s, SpawnRules.Point) == point && ed.Get(s, "locale") == locale) return s;
            return null;
        }

        [Test]
        public void RowsArePairedByMapPointAndLocale()
        {
            using (var f = new FakeSpawns())
            {
                var ed = new SpawnEditor(f.Data, Empty());
                Assert.That(ed.Sides.Count, Is.EqualTo(2));
                Assert.That(ed.Sides[0].Archive, Is.EqualTo(S));
                Assert.That(ed.Maps(), Is.EqualTo(new long[] { 7100001, 7100002, 7100003, 7100004 }));
                Assert.That(ed.CountOf(7100001), Is.EqualTo(3));
                SpawnRow both = Find(ed, 7100001, "11"), na = Find(ed, 7100001, "11", "NA");
                Assert.That(ed.MissingOn(both), Is.Empty);
                Assert.That(ed.Differs(both, SpawnRules.Population), Is.True);
                Assert.That(ed.Differs(both, SpawnRules.Tag), Is.False);
                Assert.That(ed.Get(both, SpawnRules.Population), Is.EqualTo("5"));
                Assert.That(ed.MissingOn(na), Is.EqualTo(new[] { K }));
                Assert.That(ed.MissingOn(Find(ed, 7100003, "5")), Is.EqualTo(new[] { S }));
                Assert.That(ed.TagUse()["moss"], Is.EqualTo(3));
            }
        }

        [Test]
        public void SetWritesEveryCopyThatHasTheRow()
        {
            using (var f = new FakeSpawns())
            {
                Mod mod = Empty();
                var ed = new SpawnEditor(f.Data, mod);
                SpawnRow r = Find(ed, 7100001, "11");
                ed.Set(r, SpawnRules.Tag, "reed");
                ed.Set(r, SpawnRules.Population, "7");
                Assert.That(ed.IsEdited(r, SpawnRules.Tag), Is.True);
                Assert.That(ed.IsChanged(7100001), Is.True);
                Assert.That(ed.IsChanged(7100002), Is.False);
                Assert.Throws<ModKitException>(() => ed.Set(r, SpawnRules.Point, "12"));
                Plan p = PlanOf(f, mod);
                Assert.That(Lines(p.Errors), Is.Empty);
                foreach (string a in new[] { K, S, G })
                {
                    string t = Fake.Text(p, a, SpawnRules.Entry);
                    Assert.That(t, Does.Contain("spawnPointID=\"11\" difficulty=\"1\" difficultyMin=\"1\" tag=\"reed\" coolTime=\"30\" population=\"7\""), a);
                    Assert.That(t, Does.Contain("spawnPointID=\"10\" difficulty=\"1\" difficultyMin=\"1\" tag=\"moss\""), a);
                }
                // the row of the other locale is not touched
                Assert.That(Fake.Text(p, S, SpawnRules.Entry), Does.Contain("locale=\"NA\" spawnPointID=\"11\" difficulty=\"1\" difficultyMin=\"1\" tag=\"moss\" coolTime=\"30\" population=\"1\""));
                // the mod reads back as the same edits
                var again = new SpawnEditor(f.Data, Reload(mod));
                Assert.That(again.Get(Find(again, 7100001, "11"), 1, SpawnRules.Population), Is.EqualTo("7"));
                again.Revert(Find(again, 7100001, "11"));
                Assert.That(again.IsChanged(7100001), Is.False);
            }
        }

        [Test]
        public void AddGoesAfterTheMapsLastRowOnEveryCopy()
        {
            using (var f = new FakeSpawns())
            {
                Mod mod = Empty();
                var ed = new SpawnEditor(f.Data, mod);
                SpawnRow made = ed.Add(7100001, 12, "moss", 4, 60, 2, 1);
                Assert.That(made.IsAdded, Is.True);
                Assert.That(ed.CountOf(7100001), Is.EqualTo(4));
                Assert.Throws<ModKitException>(() => ed.Add(7100001, 12, "reed", 1, 0, 2, 1));
                Assert.Throws<ModKitException>(() => ed.Add(7100001, 10, "reed", 1, 0, 1, 1));
                SpawnRow copy = ed.Clone(Find(ed, 7100002, "10"), 20);
                Assert.That(ed.Get(copy, SpawnRules.Tag), Is.EqualTo("reed"));
                Plan p = PlanOf(f, mod);
                Assert.That(Lines(p.Errors), Is.Empty);
                Assert.That(Lines(p.Warnings), Is.Empty);
                foreach (string a in new[] { K, S, G })
                {
                    string t = Fake.Text(p, a, SpawnRules.Entry);
                    int at = t.IndexOf("mapCode=\"07100001\" spawnPointID=\"12\" difficulty=\"2\" difficultyMin=\"1\" tag=\"moss\" coolTime=\"60\" population=\"4\"", StringComparison.Ordinal);
                    Assert.That(at, Is.GreaterThan(0), a);
                    Assert.That(at, Is.LessThan(t.IndexOf("mapCode=\"07100002\"", StringComparison.Ordinal)), a);
                    Assert.That(t, Does.Contain("mapCode=\"07100002\" spawnPointID=\"20\""), a);
                }
                var again = new SpawnEditor(f.Data, Reload(mod));
                SpawnRow back = Find(again, 7100001, "12");
                Assert.That(back.IsAdded, Is.True);
                Assert.That(again.MissingOn(back), Is.Empty);
                again.Remove(back);
                Assert.That(again.CountOf(7100001), Is.EqualTo(3));
            }
        }

        [Test]
        public void AddForAMapWithoutRows()
        {
            using (var f = new FakeSpawns())
            {
                Mod mod = Empty();
                var ed = new SpawnEditor(f.Data, mod);
                ed.Add(7100009, 1, "husk", 2, 10, 1, 1);
                Assert.That(ed.Maps(), Does.Contain(7100009L));
                Plan p = PlanOf(f, mod);
                Assert.That(Lines(p.Errors), Is.Empty);
                Assert.That(Lines(p.Warnings), Is.Empty);
                Assert.That(Fake.Text(p, S, SpawnRules.Entry), Does.Contain("mapCode=\"07100009\" spawnPointID=\"1\""));
            }
        }

        [Test]
        public void RemoveAndRestoreAShippedRow()
        {
            using (var f = new FakeSpawns())
            {
                Mod mod = Empty();
                var ed = new SpawnEditor(f.Data, mod);
                SpawnRow r = Find(ed, 7100002, "10");
                ed.Remove(r);
                Assert.That(r.IsDeleted, Is.True);
                Assert.That(ed.CountOf(7100002), Is.EqualTo(1));
                Plan p = PlanOf(f, mod);
                foreach (string a in new[] { K, S, G }) Assert.That(Fake.Text(p, a, SpawnRules.Entry), Does.Not.Contain("mapCode=\"07100002\""), a);
                var again = new SpawnEditor(f.Data, Reload(mod));
                SpawnRow back = Find(again, 7100002, "10");
                Assert.That(back.IsDeleted, Is.True);
                again.Restore(back);
                Assert.That(again.IsChanged(7100002), Is.False);
            }
        }

        [Test]
        public void WithoutAServerTheGlobalClientIsASide()
        {
            using (var f = new FakeSpawns(false))
            {
                Mod mod = Empty();
                var ed = new SpawnEditor(f.Data, mod);
                Assert.That(ed.Sides.Count, Is.EqualTo(2));
                Assert.That(ed.Sides[1].Archive, Is.EqualTo(G));
                ed.Set(Find(ed, 7100001, "10"), SpawnRules.CoolTime, "5");
                Plan p = PlanOf(f, mod);
                Assert.That(Fake.Text(p, G, SpawnRules.Entry), Does.Contain("spawnPointID=\"10\" difficulty=\"1\" difficultyMin=\"1\" tag=\"moss\" coolTime=\"5\""));
            }
        }

        [Test]
        public void TagsAreReadFromTheNpcData()
        {
            using (var f = new FakeSpawns())
            {
                SpawnTags t = SpawnTags.Scan(f.Data, null);
                Assert.That(t.Archive, Is.EqualTo(S));
                Assert.That(t.NpcCount, Is.EqualTo(3));
                Assert.That(t.Of("moss"), Is.EqualTo(new long[] { 66100001, 66100002 }));
                Assert.That(t.Of("husk"), Is.EqualTo(new long[] { 66100003 }));
                Assert.That(t.Of("flint"), Is.Empty);
            }
            using (var f = new FakeSpawns(false))
            {
                // the global client has per-id files like the server
                Assert.That(SpawnTags.Scan(f.Data, null).Archive, Is.EqualTo(G));
            }
        }

        [Test]
        public void CheckNamesBadNumbersEmptyTagsAndUnknownTags()
        {
            using (var f = new FakeSpawns())
            {
                var ed = new SpawnEditor(f.Data, Empty());
                SpawnRow r = Find(ed, 7100001, "10");
                Assert.That(ed.Check(r), Is.Empty);
                ed.Set(r, SpawnRules.Population, "many");
                ed.Set(r, SpawnRules.DifficultyMin, "3");
                Assert.That(ed.Check(r), Has.Some.Contains("population \"many\" is not a whole number"));
                Assert.That(ed.Check(r), Has.Some.Contains("difficultyMin 3 is above difficulty 1"));
                ed.Revert(r);
                ed.Set(r, SpawnRules.Tag, "");
                Assert.That(ed.Check(r), Has.Some.Contains("no tag"));
                ed.Set(r, SpawnRules.Tag, "moss,flint");
                Assert.That(ed.Check(r), Is.Empty);
                ed.Tags = SpawnTags.Scan(f.Data, null);
                List<string> l = ed.Check(r);
                Assert.That(l.Count, Is.EqualTo(1));
                Assert.That(l[0], Does.Contain("has the tag \"flint\""));
            }
        }

        [Test]
        public void PlanChecksChangedRows()
        {
            using (var f = new FakeSpawns())
            {
                Mod mod = FakeRooms.ModOf(
                    "<edit archive=\"server:Xml.m2d\" entry=\"table/mapspawntag.xml\">\n" +
                    "  <change at=\"region[mapCode=07100002]\"><set name=\"population\" value=\"x\" /></change>\n" +
                    "  <add><xml><region mapCode=\"07100001\" spawnPointID=\"10\" difficulty=\"1\" difficultyMin=\"1\" tag=\"moss\" coolTime=\"0\" population=\"1\" /></xml></add>\n" +
                    "  <add><xml><region mapCode=\"07100077\" spawnPointID=\"1\" difficulty=\"1\" difficultyMin=\"1\" tag=\"\" coolTime=\"0\" population=\"1\" /></xml></add>\n" +
                    "</edit>", "spawns.test");
                Plan p = Planner.Build(f.Data, new[] { mod }, new PlanOptions());
                List<string> e = Lines(p.Errors), w = Lines(p.Warnings);
                Assert.That(e, Has.Exactly(1).Contains("map 7100001 point 10: the table ends up with 2 rows"));
                Assert.That(w, Has.Exactly(1).Contains("map 7100002 point 10: population \"x\" is not a whole number"));
                Assert.That(w, Has.Exactly(1).Contains("map 7100077 point 1: no tag"));
                Assert.That(w, Has.Exactly(1).Contains("this target has no map 7100077"));
            }
        }

        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";
        static string Table(params string[] rows) { return Head + "<ms2>\n" + string.Join("", rows) + "</ms2>\n"; }

        [Test]
        public void RowsWithOneKeyArePairedInOrder()
        {
            // both copies ship two rows for map 7100001 point 11 with the same difficulty, feature and locale
            string two = Table(FakeSpawns.Row("07100001", "10", "moss", "3"), FakeSpawns.Row("07100001", "11", "moss", "2"), FakeSpawns.Row("07100001", "11", "reed", "4"));
            using (var f = new FakeSpawns(true, two, two))
            {
                var mod = Empty();
                var ed = new SpawnEditor(f.Data, mod);
                List<SpawnRow> rows = ed.Of(7100001);
                Assert.That(rows.Count, Is.EqualTo(3));
                foreach (SpawnRow r in rows) Assert.That(ed.MissingOn(r), Is.Empty);
                Assert.That(ed.Get(rows[2], 0, SpawnRules.Tag), Is.EqualTo("reed"));
                Assert.That(ed.Get(rows[2], 1, SpawnRules.Tag), Is.EqualTo("reed"));
                ed.Set(rows[2], SpawnRules.Population, "9");

                // the edit is on the second row of the key in both copies, and stays there after a reload
                var again = new SpawnEditor(f.Data, Reload(mod));
                rows = again.Of(7100001);
                Assert.That(rows.Count, Is.EqualTo(3));
                for (int side = 0; side < 2; side++)
                {
                    Assert.That(again.Get(rows[1], side, SpawnRules.Population), Is.EqualTo("2"));
                    Assert.That(again.Get(rows[2], side, SpawnRules.Population), Is.EqualTo("9"));
                    Assert.That(again.Get(rows[2], side, SpawnRules.Tag), Is.EqualTo("reed"));
                }
                Plan p = PlanOf(f, mod);
                Assert.That(Lines(p.Errors), Is.Empty, Fake.Dump(p));
                Assert.That(Lines(p.Warnings), Has.None.Contains("spawn row"), Fake.Dump(p));
            }
        }

        [Test]
        public void PlanLeavesUntouchedRowsWithOneKeyAlone()
        {
            // the first of two shipped rows with one key has a population and no tag; the mod changes another row
            string two = Table(FakeSpawns.Row("07100001", "11", "", "2"), FakeSpawns.Row("07100001", "11", "reed", "4"), FakeSpawns.Row("07100002", "10", "reed", "1"));
            using (var f = new FakeSpawns(true, two, two))
            {
                var mod = Empty();
                var ed = new SpawnEditor(f.Data, mod);
                ed.Set(ed.Of(7100002)[0], SpawnRules.Population, "3");
                Plan p = PlanOf(f, mod);
                Assert.That(Lines(p.Errors), Is.Empty, Fake.Dump(p));
                Assert.That(Lines(p.Warnings), Has.None.Contains("point 11"), Fake.Dump(p));
            }
        }

        [Test]
        public void RowsWithoutAMapIdAreListedAndEdited()
        {
            string odd = Table(FakeSpawns.Row("07100001", "10", "moss", "3"), FakeSpawns.Row("abc", "4", "moss", "1"));
            using (var f = new FakeSpawns(true, odd, odd))
            {
                var mod = Empty();
                var ed = new SpawnEditor(f.Data, mod);
                Assert.That(ed.Maps(), Is.EqualTo(new long[] { SpawnRules.NoMap, 7100001 }));
                List<SpawnRow> rows = ed.Of(SpawnRules.NoMap);
                Assert.That(rows.Count, Is.EqualTo(1));
                Assert.That(ed.MissingOn(rows[0]), Is.Empty);
                Assert.That(ed.Check(rows[0]), Has.Exactly(1).Contains("is not a map id"));
                ed.Set(rows[0], SpawnRules.Population, "2");
                var again = new SpawnEditor(f.Data, Reload(mod));
                Assert.That(again.Get(again.Of(SpawnRules.NoMap)[0], SpawnRules.Population), Is.EqualTo("2"));
                Assert.That(again.IsChanged(SpawnRules.NoMap), Is.True);
            }
        }

        [Test]
        public void CloneKeepsEveryAttributeOfEachCopy()
        {
            string srv = Table(FakeSpawns.Row("07100001", "11", "moss", "5"), FakeSpawns.Row("07100001", "11", "moss", "1", "locale=\"NA\" feature=\"Fog\" hue=\"3\" "));
            string kms = Table(FakeSpawns.Row("07100001", "11", "moss", "2"));
            using (var f = new FakeSpawns(true, kms, srv))
            {
                var mod = Empty();
                var ed = new SpawnEditor(f.Data, mod);
                SpawnRow made = ed.Clone(Find(ed, 7100001, "11", "NA"), 20);
                Assert.That(ed.Get(made, "locale"), Is.EqualTo("NA"));
                Assert.That(ed.Get(made, "feature"), Is.EqualTo("Fog"));
                Assert.That(ed.Get(made, "hue"), Is.EqualTo("3"));
                Assert.That(ed.Get(made, SpawnRules.Point), Is.EqualTo("20"));
                // the Korean client has no such row, so it gets none
                Assert.That(ed.MissingOn(made), Is.EqualTo(new[] { K }));

                // a row both copies have is copied with each copy's own values
                SpawnRow both = ed.Clone(Find(ed, 7100001, "11"), 21);
                Assert.That(ed.Get(both, 0, SpawnRules.Population), Is.EqualTo("5"));
                Assert.That(ed.Get(both, 1, SpawnRules.Population), Is.EqualTo("2"));
                Assert.Throws<ModKitException>(() => ed.Clone(Find(ed, 7100001, "11"), 21));

                var again = new SpawnEditor(f.Data, Reload(mod));
                SpawnRow back = Find(again, 7100001, "20", "NA");
                Assert.That(back, Is.Not.Null);
                Assert.That(again.Get(back, "hue"), Is.EqualTo("3"));
                Assert.That(Lines(PlanOf(f, mod).Errors), Is.Empty);
            }
        }

        [Test]
        public void AChangedDifficultyKeepsTheRowAShippedOne()
        {
            // map 7100055 is no map of any target
            string t = Table(FakeSpawns.Row("07100055", "1", "moss", "1"), FakeSpawns.Row("07100001", "10", "moss", "3"), FakeSpawns.Row("07100001", "10", "moss", "3", "", "4"));
            using (var f = new FakeSpawns(true, t, t))
            {
                var mod = Empty();
                var ed = new SpawnEditor(f.Data, mod);
                ed.Set(ed.Of(7100055)[0], SpawnRules.Difficulty, "3");
                Plan p = PlanOf(f, mod);
                Assert.That(Lines(p.Errors), Is.Empty, Fake.Dump(p));
                Assert.That(Lines(p.Warnings), Has.None.Contains("has no map"), Fake.Dump(p));
                var again = new SpawnEditor(f.Data, Reload(mod));
                Assert.That(again.Of(7100055).Count, Is.EqualTo(1));
                Assert.That(again.Of(7100055)[0].IsAdded, Is.False);
                Assert.That(again.MissingOn(again.Of(7100055)[0]), Is.Empty);

                // moving a shipped row onto another row's difficulty range is said by the editor and is a plan error
                SpawnRow low = ed.Of(7100001)[0];
                Assert.That(ed.Check(low), Is.Empty);
                ed.Set(low, SpawnRules.Difficulty, "4");
                Assert.That(ed.Check(low), Has.Exactly(2).Contains("point 10 has 2 rows for the same difficulty"));
                Assert.That(Lines(PlanOf(f, mod).Errors), Has.Some.Contains("map 7100001 point 10: the table ends up with 2 rows"));
            }
        }
    }
}
