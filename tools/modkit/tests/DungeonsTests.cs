using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    /// <summary>A kms client, a global client and a server with a few made-up dungeon rooms.</summary>
    public sealed class FakeRooms : IDisposable
    {
        public readonly string Dir;
        public readonly DataSet Data;
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";
        public const string SrvEntry = "table/na/dungeonroom.xml";

        public const string Kms = Head + "<ms2>\n" +
            "\t<dungeonRoom dungeonRoomID=\"9100001\" groupType=\"1\" fieldIDs=\"7000001,7000002\" lobbyFieldID=\"7000003\" dungeonLevel=\"50\" minUserCount=\"1\" maxUserCount=\"4\" durationSeconds=\"900\" limitEnter=\"1\" clearKillNpc=\"66000001\" cooldownValue=\"5\" userMissions=\"31,32\" extraRewardTicketTableID=\"3\" />\n" +
            "\t<dungeonRoom dungeonRoomID=\"9100002\" groupType=\"2\" fieldIDs=\"7000002\" minUserCount=\"4\" maxUserCount=\"10\" gearScore=\"1500\" />\n" +
            "\t<dungeonRoom dungeonRoomID=\"9100004\" groupType=\"9\" fieldIDs=\"7000001\" minUserCount=\"1\" maxUserCount=\"1\" />\n" +
            "</ms2>\n";

        public const string Srv = Head + "<ms2>\n" +
            "\t<dungeonRoom dungeonRoomID=\"9100001\" groupType=\"normal\" fieldIDs=\"07000001,07000002\" lobbyFieldID=\"7000003\" dungeonLevel=\"50\" minUserCount=\"1\" maxUserCount=\"4\" durationTick=\"900000\" playType=\"limitEnter\" clearType=\"monster\" clearValue=\"66000001\" cooldownType=\"nextDay\" cooldownValue=\"5\" userMissions=\"31,32\" />\n" +
            "\t<dungeonRoom dungeonRoomID=\"9100002\" groupType=\"raid\" fieldIDs=\"07000002\" minUserCount=\"4\" maxUserCount=\"10\" gearScore=\"1800\" playType=\"limitReward\" clearType=\"trigger\" cooldownType=\"dayOfWeeks\" />\n" +
            "\t<dungeonRoom dungeonRoomID=\"9100003\" groupType=\"event\" fieldIDs=\"07000001\" minUserCount=\"1\" maxUserCount=\"4\" playType=\"limitReward\" clearType=\"trigger\" cooldownType=\"nextDay\" />\n" +
            "</ms2>\n";

        static string Keys(params string[] kv)
        {
            var sb = new StringBuilder(Head + "<ms2>\n");
            for (int i = 0; i < kv.Length; i += 2) sb.Append("\t<key id=\"" + kv[i] + "\" name=\"" + kv[i + 1] + "\" />\n");
            return sb.Append("</ms2>\n").ToString();
        }

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        public FakeRooms() : this(null) { }

        /// <summary>srv: another shipped room table of the server and the global client.</summary>
        public FakeRooms(string srv) : this(srv, false) { }

        /// <summary>badLang: the Korean client also has a language folder whose name file is no xml.</summary>
        public FakeRooms(string srv, bool badLang)
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-rooms-" + Guid.NewGuid().ToString("N"));
            string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
            foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);
            const string missions = Head + "<ms2>\n\t<DungeonMission id=\"31\" />\n\t<DungeonMission id=\"32\" />\n</ms2>\n";
            const string roundGroups = Head + "<ms2>\n\t<DungeonRoundGroup id=\"5\" />\n</ms2>\n";
            var kl = new List<KeyValuePair<string, byte[]>>
            {
                E(DungeonRules.KmsEntry, Kms),
                E("table/fielddata.xml", Head + "<ms2>\n\t<fieldData id=\"7000001\" />\n\t<fieldData id=\"7000002\" />\n\t<fieldData id=\"7000003\" />\n</ms2>\n"),
                E("table/dungeonmission.xml", missions), E("table/dungeonrounddata.xml", roundGroups),
                E("string/kr/" + DungeonRules.NameFile, Keys("9100001", "Zinc Cellar")),
                E("string/en/" + DungeonRules.NameFile, Keys("9100001", "Tin Cellar", "9100001", "Tin Cellar B")),
            };
            if (badLang) kl.Add(E("string/jp/" + DungeonRules.NameFile, "<ms2><key id="));
            M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, kl);
            foreach (string d in new[] { g, s })
            {
                var l = new List<KeyValuePair<string, byte[]>>
                {
                    E(SrvEntry, srv ?? Srv), E("table/dungeonmission.xml", missions), E("table/dungeonrounddata.xml", roundGroups),
                    E("string/en/" + DungeonRules.NameFile, Keys("9100001", "Tin Cellar", "9100002", "Clay Loft")),
                };
                foreach (long i in new long[] { 7000001, 7000002, 7000003 }) l.Add(E("map/" + i.ToString("00000000") + ".xml", Head + "<ms2>\n\t<environment />\n</ms2>\n"));
                M2dWriter.Create(Path.Combine(d, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, l);
            }
            Data = new DataSet(k, g, s);
        }

        public static Mod ModOf(string body, string id = "rooms.test")
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
    public class DungeonsTests
    {
        const string K = "client:Xml.m2d", G = "gms-client:Xml.m2d", S = "server:Xml.m2d";

        static Mod Empty() { return FakeRooms.ModOf(""); }

        static DungeonRoom Find(DungeonEditor ed, string id)
        {
            foreach (DungeonRoom d in ed.Rooms) if (ed.Get(d, DungeonRules.Key) == id) return d;
            return null;
        }

        static string Body(Mod m)
        {
            string x = m.ToXml();
            int a = x.IndexOf('>', x.IndexOf("<mod", StringComparison.Ordinal)) + 1, b = x.LastIndexOf("</mod>", StringComparison.Ordinal);
            return x.Substring(a, b - a);
        }

        static Mod Reload(Mod m) { return FakeRooms.ModOf(Body(m)); }

        static Plan PlanOf(FakeRooms f, Mod m) { return Planner.Build(f.Data, new[] { Reload(m) }, new PlanOptions()); }

        static List<string> Lines(IEnumerable<PlanMessage> msgs)
        {
            var l = new List<string>();
            foreach (PlanMessage m in msgs) l.Add(m.ToString());
            return l;
        }

        static string Row(Plan p, string archive, string entry, string id)
        {
            foreach (string line in Fake.Text(p, archive, entry).Split('\n'))
                if (line.Contains("dungeonRoomID=\"" + id + "\"")) return line.Trim();
            return null;
        }

        [Test]
        public void RulesReadAndWriteBothNotations()
        {
            var row = new Dictionary<string, string>();
            Func<string, string> get = a => { string v; return row.TryGetValue(a, out v) ? v : null; };
            Action<string, string> set = (a, v) => { if (v == null) row.Remove(a); else row[a] = v; };

            DungeonRules.Write(get, set, true, DungeonRules.Group, "chaosRaid");
            DungeonRules.Write(get, set, true, DungeonRules.Fields, "07000001, 7000002");
            DungeonRules.Write(get, set, true, DungeonRules.Duration, "600");
            DungeonRules.Write(get, set, true, DungeonRules.LimitEnter, "1");
            DungeonRules.Write(get, set, true, DungeonRules.ClearKill, "66000002");
            DungeonRules.Write(get, set, true, "cooldownType", "nextDay");
            Assert.That(row["groupType"], Is.EqualTo("3"));
            Assert.That(row["fieldIDs"], Is.EqualTo("7000001,7000002"));
            Assert.That(row["durationSeconds"], Is.EqualTo("600"));
            Assert.That(row["limitEnter"], Is.EqualTo("1"));
            Assert.That(row["clearKillNpc"], Is.EqualTo("66000002"));
            Assert.That(row.ContainsKey("cooldownType"), Is.False);
            Assert.That(DungeonRules.Read(get, true, DungeonRules.Group), Is.EqualTo("chaosRaid"));

            row.Clear();
            row["playType"] = "limitReward";
            row["clearType"] = "trigger";
            DungeonRules.Write(get, set, false, DungeonRules.Group, "3");
            DungeonRules.Write(get, set, false, DungeonRules.Fields, "7000001,7000002");
            DungeonRules.Write(get, set, false, DungeonRules.Duration, "600");
            DungeonRules.Write(get, set, false, DungeonRules.LimitEnter, "1");
            DungeonRules.Write(get, set, false, DungeonRules.ClearKill, "66000002");
            DungeonRules.Write(get, set, false, "extraRewardTicketTableID", "3");
            Assert.That(row["groupType"], Is.EqualTo("chaosRaid"));
            Assert.That(row["fieldIDs"], Is.EqualTo("07000001,07000002"));
            Assert.That(row["durationTick"], Is.EqualTo("600000"));
            Assert.That(row["playType"], Is.EqualTo("limitEnter"));
            Assert.That(row["clearType"], Is.EqualTo("monster"));
            Assert.That(row["clearValue"], Is.EqualTo("66000002"));
            Assert.That(row.ContainsKey("extraRewardTicketTableID"), Is.False);
            Assert.That(row.ContainsKey("durationSeconds"), Is.False);
            Assert.That(DungeonRules.Read(get, false, DungeonRules.Fields), Is.EqualTo("7000001,7000002"));
            Assert.That(DungeonRules.Read(get, false, DungeonRules.Duration), Is.EqualTo("600"));
            Assert.That(DungeonRules.Read(get, false, DungeonRules.LimitEnter), Is.EqualTo("1"));
            Assert.That(DungeonRules.Read(get, false, DungeonRules.ClearKill), Is.EqualTo("66000002"));

            DungeonRules.Write(get, set, false, DungeonRules.LimitEnter, "");
            DungeonRules.Write(get, set, false, DungeonRules.ClearKill, "");
            DungeonRules.Write(get, set, false, DungeonRules.Duration, "");
            Assert.That(row["playType"], Is.EqualTo("limitReward"));
            Assert.That(row["clearType"], Is.EqualTo("trigger"));
            Assert.That(row.ContainsKey("clearValue"), Is.False);
            Assert.That(row.ContainsKey("durationTick"), Is.False);
        }

        [Test]
        public void PairsTheSidesAndShowsWhereTheyDiffer()
        {
            using (var f = new FakeRooms())
            {
                var ed = new DungeonEditor(f.Data, Empty());
                Assert.That(ed.Sides.Count, Is.EqualTo(2));       // kms client and server; the global client follows the server
                Assert.That(ed.Rooms.Count, Is.EqualTo(4));
                DungeonRoom a = Find(ed, "9100001"), b = Find(ed, "9100002");
                Assert.That(ed.Get(a, DungeonRules.Group), Is.EqualTo("normal"));
                Assert.That(ed.Get(a, 1, DungeonRules.Fields), Is.EqualTo("7000001,7000002"));
                Assert.That(ed.Get(a, 1, DungeonRules.Duration), Is.EqualTo("900"));
                Assert.That(ed.Get(a, "cooldownType"), Is.EqualTo("nextDay"));
                Assert.That(ed.Get(a, "extraRewardTicketTableID"), Is.EqualTo("3"));
                foreach (string fld in ed.Fields(a)) Assert.That(ed.Differs(a, fld), Is.False, fld);
                Assert.That(ed.Fields(a), Has.No.Member("durationTick").And.No.Member("playType").And.No.Member("clearValue"));
                Assert.That(ed.Differs(b, "gearScore"), Is.True);
                Assert.That(ed.Maps(a), Is.EqualTo(new long[] { 7000001, 7000002 }));
                Assert.That(ed.MissingOn(Find(ed, "9100003")), Is.EqualTo(new[] { K }));
                Assert.That(ed.MissingOn(Find(ed, "9100004")), Is.EqualTo(new[] { S }));
                Assert.That(ed.Text(a, "name"), Is.EqualTo("Tin Cellar"));
                Assert.That(ed.Find("Clay").Count, Is.EqualTo(1));
                Assert.That(ed.Find("7000002").Count, Is.EqualTo(2));
                Assert.That(ed.Check(a), Is.Empty);
                Assert.That(ed.IsChanged(a), Is.False);
            }
        }

        [Test]
        public void SetsAFieldOnEverySideInItsNotation()
        {
            using (var f = new FakeRooms())
            {
                Mod mod = Empty();
                var ed = new DungeonEditor(f.Data, mod);
                DungeonRoom b = Find(ed, "9100002");
                ed.Set(b, DungeonRules.Group, "chaosRaid");
                ed.Set(b, DungeonRules.Fields, "7000001,7000003");
                ed.Set(b, DungeonRules.Duration, "1200");
                ed.Set(b, DungeonRules.LimitEnter, "1");
                ed.Set(b, DungeonRules.ClearKill, "66000005");
                ed.Set(b, "cooldownType", "nextDay");
                ed.Set(b, 1, "gearScore", "2000");
                ed.SetText(b, "name", "Clay Attic");
                Assert.That(ed.IsChanged(b), Is.True);
                Assert.That(ed.Get(b, 0, "gearScore"), Is.EqualTo("1500"));
                Assert.Throws<ModKitException>(() => ed.Set(b, DungeonRules.Key, "5"));
                Assert.Throws<ModKitException>(() => ed.Set(b, "playType", "limitEnter"));
                Assert.Throws<ModKitException>(() => ed.Set(b, 0, "cooldownType", "nextDay"));

                Plan p = PlanOf(f, mod);
                Assert.That(Lines(p.Errors), Is.Empty);
                Assert.That(Row(p, K, DungeonRules.KmsEntry, "9100002"), Is.EqualTo(
                    "<dungeonRoom dungeonRoomID=\"9100002\" groupType=\"3\" fieldIDs=\"7000001,7000003\" minUserCount=\"4\" maxUserCount=\"10\" gearScore=\"1500\" durationSeconds=\"1200\" limitEnter=\"1\" clearKillNpc=\"66000005\" />"));
                Assert.That(Row(p, S, FakeRooms.SrvEntry, "9100002"), Is.EqualTo(
                    "<dungeonRoom dungeonRoomID=\"9100002\" groupType=\"chaosRaid\" fieldIDs=\"07000001,07000003\" minUserCount=\"4\" maxUserCount=\"10\" gearScore=\"2000\" playType=\"limitEnter\" clearType=\"monster\" cooldownType=\"nextDay\" durationTick=\"1200000\" clearValue=\"66000005\" />"));
                Assert.That(Row(p, G, FakeRooms.SrvEntry, "9100002"), Is.EqualTo(Row(p, S, FakeRooms.SrvEntry, "9100002")));
                Assert.That(Fake.Text(p, K, "string/kr/" + DungeonRules.NameFile), Does.Contain("<key id=\"9100002\" name=\"Clay Attic\" />"));
                Assert.That(Fake.Text(p, S, "string/en/" + DungeonRules.NameFile), Does.Contain("<key id=\"9100002\" name=\"Clay Attic\" />"));

                // the saved mod opens to the same rooms; a revert leaves nothing
                var again = new DungeonEditor(f.Data, Reload(mod));
                Assert.That(again.Get(Find(again, "9100002"), 1, "gearScore"), Is.EqualTo("2000"));
                Assert.That(again.Text(Find(again, "9100002"), "name"), Is.EqualTo("Clay Attic"));
                ed.Revert(b);
                Assert.That(ed.IsChanged(b), Is.False);
                Assert.That(PlanOf(f, mod).Changes, Is.Empty);
            }
        }

        [Test]
        public void AddsAndClonesRoomsOnEverySide()
        {
            using (var f = new FakeRooms())
            {
                Mod mod = Empty();
                var ed = new DungeonEditor(f.Data, mod);
                Assert.That(ed.NextIds(null, 2, null), Is.EqualTo(new long[] { 98000000, 98000001 }));
                Assert.Throws<ModKitException>(() => ed.Add(9100001));
                Assert.Throws<ModKitException>(() => ed.Add(5));

                DungeonRoom made = ed.Add(98000000);
                ed.Set(made, DungeonRules.Fields, "7000001");
                ed.SetText(made, "name", "Zinc Vault");
                Assert.That(ed.Check(made), Is.Empty);
                // a clone: each side its own row; a side without the source takes the other side's fields in its notation
                DungeonRoom copy = ed.Clone(Find(ed, "9100001"), 98000001);
                DungeonRoom fromServer = ed.Clone(Find(ed, "9100003"), 98000002);
                Assert.That(ed.Text(copy, "name"), Is.EqualTo("Tin Cellar"));
                Assert.That(ed.NextIds(null, 1, null), Is.EqualTo(new long[] { 98000003 }));
                Assert.Throws<ModKitException>(() => ed.Clone(made, 98000001));

                Plan p = PlanOf(f, mod);
                Assert.That(Lines(p.Errors), Is.Empty);
                Assert.That(Lines(p.Warnings), Is.Empty);
                Assert.That(Row(p, K, DungeonRules.KmsEntry, "98000000"), Is.EqualTo(
                    "<dungeonRoom dungeonRoomID=\"98000000\" groupType=\"1\" dungeonLevel=\"1\" minUserCount=\"1\" maxUserCount=\"4\" limitPlayerLevel=\"1\" timerType=\"clock\" fieldIDs=\"7000001\" />"));
                Assert.That(Row(p, S, FakeRooms.SrvEntry, "98000000"), Is.EqualTo(
                    "<dungeonRoom dungeonRoomID=\"98000000\" groupType=\"normal\" dungeonLevel=\"1\" minUserCount=\"1\" maxUserCount=\"4\" limitPlayerLevel=\"1\" timerType=\"clock\" cooldownType=\"nextDay\" playType=\"limitReward\" clearType=\"trigger\" fieldIDs=\"07000001\" />"));
                Assert.That(Row(p, K, DungeonRules.KmsEntry, "98000001"), Is.EqualTo(Row(p, K, DungeonRules.KmsEntry, "9100001").Replace("9100001", "98000001")));
                Assert.That(Row(p, S, FakeRooms.SrvEntry, "98000001"), Is.EqualTo(Row(p, S, FakeRooms.SrvEntry, "9100001").Replace("9100001", "98000001")));
                Assert.That(Row(p, K, DungeonRules.KmsEntry, "98000002"), Is.EqualTo(
                    "<dungeonRoom dungeonRoomID=\"98000002\" groupType=\"11\" fieldIDs=\"7000001\" minUserCount=\"1\" maxUserCount=\"4\" />"));
                Assert.That(Row(p, G, FakeRooms.SrvEntry, "98000001"), Is.Not.Null);
                Assert.That(Fake.Text(p, K, "string/en/" + DungeonRules.NameFile), Does.Contain("<key id=\"98000001\" name=\"Tin Cellar\" />"));

                // another mod cannot take the ids; removing a new room frees its id and its texts
                Mod other = FakeRooms.ModOf("", "rooms.other");
                var ed2 = new DungeonEditor(f.Data, other) { Others = () => new[] { Reload(mod) } };
                Assert.Throws<ModKitException>(() => ed2.Add(98000001));
                ed.Remove(copy);
                ed.Remove(fromServer);
                ed.Remove(made);
                Assert.That(PlanOf(f, mod).Changes, Is.Empty);
                Assert.That(mod.Ids, Is.Empty);
            }
        }

        [Test]
        public void RemoveOfARoomAddedOnOneSideOnly_KeepsTheShippedRowRestorable()
        {
            using (var f = new FakeRooms())
            {
                // 9100003 ships on the server only; the mod adds it to the Korean client by hand
                Mod mod = FakeRooms.ModOf("<edit archive=\"" + K + "\" entry=\"" + DungeonRules.KmsEntry + "\">\n" +
                    "<add><xml><dungeonRoom dungeonRoomID=\"9100003\" groupType=\"1\" fieldIDs=\"7000001\" minUserCount=\"1\" maxUserCount=\"4\" /></xml></add>\n</edit>");
                var ed = new DungeonEditor(f.Data, mod);
                DungeonRoom d = Find(ed, "9100003");
                Assert.That(d.IsAdded, Is.False);
                ed.Remove(d);
                Assert.That(ed.Rooms, Does.Contain(d));
                Assert.That(d.IsDeleted, Is.True);
                Assert.That(Body(mod), Does.Not.Contain("groupType=\"1\""));
                ed.Restore(d);
                Assert.That(d.IsDeleted, Is.False);
                Assert.That(PlanOf(f, mod).Changes, Is.Empty);
            }
        }

        [Test]
        public void CloneCopiesTheTextOfEachLanguage()
        {
            using (var f = new FakeRooms())
            {
                Mod mod = Empty();
                var ed = new DungeonEditor(f.Data, mod);
                ed.Clone(Find(ed, "9100001"), 98000001);
                Plan p = PlanOf(f, mod);
                Assert.That(Fake.Text(p, K, "string/kr/" + DungeonRules.NameFile), Does.Contain("<key id=\"98000001\" name=\"Zinc Cellar\" />"));
                Assert.That(Fake.Text(p, K, "string/en/" + DungeonRules.NameFile), Does.Contain("<key id=\"98000001\" name=\"Tin Cellar\" />"));
                Assert.That(Fake.Text(p, S, "string/en/" + DungeonRules.NameFile), Does.Contain("<key id=\"98000001\" name=\"Tin Cellar\" />"));
            }
        }

        [Test]
        public void ALanguageFileThatCannotBeRead_IsReported()
        {
            using (var f = new FakeRooms(null, true))
            {
                Mod mod = Empty();
                var ed = new DungeonEditor(f.Data, mod);
                DungeonRoom d = ed.Add(98000001);
                ed.SetText(d, "name", "Brass Attic");
                Assert.That(ed.TextProblems, Has.Count.EqualTo(1));
                Assert.That(ed.TextProblems[0], Does.Contain("string/jp/").And.Contain("names are not written there"));
                Assert.That(ed.Check(d), Has.Some.Contains("string/jp/"));
                Assert.That(Fake.Text(PlanOf(f, mod), K, "string/en/" + DungeonRules.NameFile), Does.Contain("Brass Attic"));
            }
        }

        [Test]
        public void RowsOfOneIdWithAnotherFeature_AreNotDuplicates()
        {
            string srv = FakeRooms.Srv.Replace("\t<dungeonRoom dungeonRoomID=\"9100003\"",
                "\t<dungeonRoom dungeonRoomID=\"9100002\" feature=\"Tinfoil\" groupType=\"raid\" fieldIDs=\"07000002\" minUserCount=\"4\" maxUserCount=\"10\" playType=\"limitReward\" clearType=\"trigger\" cooldownType=\"dayOfWeeks\" />\n\t<dungeonRoom dungeonRoomID=\"9100003\"");
            using (var f = new FakeRooms(srv))
            {
                Mod mod = Empty();
                var ed = new DungeonEditor(f.Data, mod);
                foreach (DungeonRoom d in ed.Rooms)
                    if (ed.Get(d, DungeonRules.Key) == "9100002")
                    {
                        Assert.That(ed.Check(d), Has.None.Contains("times"));
                        ed.Set(d, "gearScore", "1700");
                    }
                Plan p = PlanOf(f, mod);
                Assert.That(Lines(p.Errors), Is.Empty);
                Assert.That(Lines(p.Warnings), Has.None.Contains("times"));
            }
        }

        [Test]
        public void RemovesAndRestoresAShippedRoom()
        {
            using (var f = new FakeRooms())
            {
                Mod mod = Empty();
                var ed = new DungeonEditor(f.Data, mod);
                DungeonRoom a = Find(ed, "9100001");
                ed.Remove(a);
                Assert.That(a.IsDeleted, Is.True);
                Assert.Throws<ModKitException>(() => ed.Set(a, "gearScore", "1"));
                Plan p = PlanOf(f, mod);
                Assert.That(Row(p, K, DungeonRules.KmsEntry, "9100001"), Is.Null);
                Assert.That(Row(p, S, FakeRooms.SrvEntry, "9100001"), Is.Null);
                ed.Restore(a);
                Assert.That(PlanOf(f, mod).Changes, Is.Empty);
            }
        }

        [Test]
        public void EditorChecksARoom()
        {
            using (var f = new FakeRooms())
            {
                var ed = new DungeonEditor(f.Data, Empty());
                DungeonRoom b = Find(ed, "9100002");
                ed.Set(b, DungeonRules.Fields, "");
                ed.Set(b, "minUserCount", "12");
                ed.Set(b, DungeonRules.Group, "castle");
                ed.Set(b, "timerType", "sand");
                string all = string.Join("\n", ed.Check(b).ToArray());
                Assert.That(all, Does.Contain("no field"));
                Assert.That(all, Does.Contain("minUserCount 12 is above maxUserCount 10"));
                Assert.That(all, Does.Contain("groupType \"castle\" is not a group type of the kms client"));
                Assert.That(all, Does.Contain("groupType \"castle\" is not a group type of the server"));
                Assert.That(all, Does.Contain("timerType \"sand\" is not clock or gauge"));
                Assert.That(string.Join("\n", ed.Check(Find(ed, "9100003")).ToArray()), Does.Contain("not in " + K));
            }
        }

        [Test]
        public void PlanChecksTheFinalTables()
        {
            using (var f = new FakeRooms())
            {
                Mod mod = FakeRooms.ModOf(
                    "<edit archive=\"" + S + "\" entry=\"" + FakeRooms.SrvEntry + "\">\n" +
                    "<add><xml><dungeonRoom dungeonRoomID=\"9100002\" groupType=\"raid\" fieldIDs=\"07000002\" minUserCount=\"1\" maxUserCount=\"4\" /></xml></add>\n" +
                    "<add><xml><dungeonRoom dungeonRoomID=\"98000005\" groupType=\"normal\" fieldIDs=\"07000009\" lobbyFieldID=\"7000008\" minUserCount=\"1\" maxUserCount=\"4\" userMissions=\"31,39\" roundID=\"6\" /></xml></add>\n" +
                    "</edit>\n");
                Plan p = PlanOf(f, mod);
                List<string> e = Lines(p.Errors), w = Lines(p.Warnings);
                Assert.That(e.Count, Is.EqualTo(1), string.Join("\n", e.ToArray()));
                Assert.That(e[0], Does.Contain("dungeon 9100002 is in the table 2 times: the server's data ingest fails"));
                Assert.That(w, Has.Some.Contains("dungeon 98000005: fieldIDs: 7000009 is not a map of the"));
                Assert.That(w, Has.Some.Contains("dungeon 98000005: lobbyFieldID: 7000008 is not a map of the"));
                Assert.That(w, Has.Some.Contains("dungeon 98000005: userMissions: 39 is not a mission"));
                Assert.That(w, Has.Some.Contains("dungeon 98000005: roundID 6 is not a round group"));
                Assert.That(w, Has.Some.Contains("dungeon 98000005 is added here but not in " + K));
                // the line mirrored to the global client says each thing once
                Assert.That(w.FindAll(t => t.Contains("userMissions: 39")).Count, Is.EqualTo(1));
            }
        }

        [Test]
        public void AShippedDoubleIdDoesNotBlockApply()
        {
            string twice = FakeRooms.Srv.Replace("</ms2>", "\t<dungeonRoom dungeonRoomID=\"9100077\" fieldIDs=\"07000001\" />\n\t<dungeonRoom dungeonRoomID=\"9100077\" fieldIDs=\"07000002\" />\n</ms2>");
            using (var f = new FakeRooms(twice))
            {
                Mod mod = Empty();
                var ed = new DungeonEditor(f.Data, mod);
                ed.Set(Find(ed, "9100002"), "gearScore", "1600");
                Plan p = PlanOf(f, mod);
                Assert.That(Lines(p.Errors), Is.Empty);
                Assert.That(Lines(p.Warnings), Has.Some.Contains("dungeon 9100077 is in the table 2 times"));
            }
        }
    }
}
