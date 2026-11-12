// The plan's checks of dungeon rooms (table/dungeonroom.xml, see Dungeons.cs): every room the mods add or change
// is read as it ends up in each target and checked with DungeonRules. On top of the editor's checks the plan knows
// the final tables: a room id the plan puts into a table twice (the server's data ingest fails; the kms client
// reads the last row), fields and a lobby that are not maps of the target, missions and a round group the target
// does not have, and a new room that one target gets and another does not.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        sealed class RoomSide
        {
            internal Profile Profile;
            internal string Archive, Entry;
            internal bool Kms;
            internal XmlDoc Doc, Orig;
        }

        static string RoomKey(XmlElem e)
        {
            return (e.Attr(DungeonRules.Key) ?? "") + "\n" + (e.Attr("feature") ?? "") + "\n" + (e.Attr("locale") ?? "");
        }

        // the mod a room is blamed on: the last mod whose lines for a room table name the id, else the last with lines there
        static Mod RoomMod(Run run, string id)
        {
            Mod named = null, any = null;
            var inStep = new Regex(@"\[" + DungeonRules.Key + @"=[""']?" + Regex.Escape(id) + @"[""']?\]");
            var inRow = new Regex(@"\s" + DungeonRules.Key + @"\s*=\s*[""']" + Regex.Escape(id) + @"[""']");
            foreach (Mod m in run.Plan.Mods)
                foreach (ModOp op in m.Ops)
                {
                    var ed = op as EditOp;
                    if (ed == null || !DungeonRules.IsEntry(ed.Entry)) continue;
                    any = m;
                    foreach (RowOp r in ed.Rows)
                    {
                        bool hit = false;
                        foreach (string sel in new[] { r.At, r.Copy, r.In, r.After, r.Before }) if (sel != null && inStep.IsMatch(sel)) hit = true;
                        if (r.Xml != null && inRow.IsMatch(r.Xml)) hit = true;
                        foreach (AttrOp a in r.Attrs) if (a.Name == DungeonRules.Key && a.Value == id) hit = true;
                        if (hit) named = m;
                    }
                }
            return named ?? any;
        }

        // the ids (attribute key of the rows named row) of a target's table as the plan leaves it; null when the target has no such table
        static HashSet<string> RoomRefs(Run run, RoomSide s, string file, string row, string key)
        {
            XmlDoc d = FinalDoc(run, s.Archive, "table/" + s.Profile.Region + "/" + file) ?? FinalDoc(run, s.Archive, "table/" + file);
            if (d == null || d.Root == null) return null;
            var set = new HashSet<string>();
            foreach (XmlElem e in d.Root.Children) if (e.Name == row) set.Add(e.Attr(key) ?? "");
            return set;
        }

        static void DungeonChecks(Run run)
        {
            var sides = new List<RoomSide>();
            bool touched = false;
            foreach (Profile p in run.Data.Profiles)
            {
                string archive = p.ArchiveIn("x:Xml.m2d");
                string entry = DungeonRules.EntryOf(run.Data, archive);
                if (entry == null) continue;
                M2dArchive arch = run.Data.Open(archive);
                if (run.State(arch, archive, arch.Find(entry).Name).Final != null) touched = true;
                sides.Add(new RoomSide { Profile = p, Archive = archive, Entry = entry, Kms = p == Profile.KmsClient });
            }
            if (!touched) return;
            Plan plan = run.Plan;
            foreach (RoomSide s in sides.ToArray())
            {
                s.Orig = OrigDoc(run, s.Archive, s.Entry);
                s.Doc = FinalDoc(run, s.Archive, s.Entry);
                if (s.Orig == null || s.Orig.Root == null || s.Doc == null || s.Doc.Root == null) sides.Remove(s);
            }
            // the server before the global client: a line mirrored to the latter says a thing once
            sides.Sort((x, y) => (x.Profile == Profile.GmsClient ? 1 : 0) - (y.Profile == Profile.GmsClient ? 1 : 0));
            bool hasServer = false;
            foreach (RoomSide s in sides) if (s.Profile == Profile.Server) hasServer = true;

            var added = new List<HashSet<string>>();
            var has = new List<HashSet<string>>();
            var said = new HashSet<string>();       // a line mirrored from the server to the global client says a thing once
            foreach (RoomSide s in sides)
            {
                var mine = new HashSet<string>();
                var ids = new HashSet<string>();
                added.Add(mine);
                has.Add(ids);
                var orig = new Dictionary<string, string>();
                var origIds = new Dictionary<string, int>();
                var origHas = new HashSet<string>();
                foreach (XmlElem e in s.Orig.Root.Children)
                {
                    if (e.Name != DungeonRules.Rows) continue;
                    orig[RoomKey(e)] = RowText(e);
                    int n0;
                    // counted per id, feature and locale: rows that differ there are variants of a room
                    origIds.TryGetValue(RoomKey(e), out n0);
                    origIds[RoomKey(e)] = n0 + 1;
                    origHas.Add(e.Attr(DungeonRules.Key) ?? "");
                }
                bool mirror = hasServer && s.Profile != Profile.Server && !s.Kms;
                Dictionary<long, bool> maps = null;
                HashSet<string> missions = null, rounds = null;
                bool read = false;
                var seen = new Dictionary<string, int>();
                foreach (XmlElem row in s.Doc.Root.Children)
                {
                    if (row.Name != DungeonRules.Rows) continue;
                    string id = row.Attr(DungeonRules.Key) ?? "";
                    ids.Add(id);
                    int times;
                    seen.TryGetValue(RoomKey(row), out times);
                    seen[RoomKey(row)] = times + 1;
                    string was;
                    bool isNew = !orig.TryGetValue(RoomKey(row), out was);
                    if (!isNew && was == RowText(row)) continue;
                    Mod mod = RoomMod(run, id);
                    string label = "dungeon " + id;
                    var texts = new List<string>(DungeonRules.CheckRoom(row.Attr, s.Kms));
                    if (!read)
                    {
                        read = true;
                        maps = IdsOf(run, s.Profile, "map/", "table/fielddata.xml", "<fieldData id=\"");
                        missions = RoomRefs(run, s, "dungeonmission.xml", "DungeonMission", "id");
                        rounds = RoomRefs(run, s, "dungeonrounddata.xml", "DungeonRoundGroup", "id");
                    }
                    if (maps != null)
                        foreach (string f in new[] { DungeonRules.Fields, "lobbyFieldID" })
                            foreach (string t in (row.Attr(f) ?? "").Split(','))
                            {
                                long m;
                                if (long.TryParse(t.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out m) && m > 0 && !maps.ContainsKey(m))
                                    texts.Add(f + ": " + m + " is not a map of the " + s.Profile.Title);
                            }
                    if (missions != null)
                        foreach (string f in new[] { "userMissions", "partyMissions" })
                            foreach (string t in (row.Attr(f) ?? "").Split(','))
                                if (t.Trim().Length > 0 && t.Trim() != "0" && !missions.Contains(t.Trim()))
                                    texts.Add(f + ": " + t.Trim() + " is not a mission of table/dungeonmission.xml of the " + s.Profile.Title);
                    string round = row.Attr("roundID");
                    if (rounds != null && !string.IsNullOrEmpty(round) && round != "0" && !rounds.Contains(round))
                        texts.Add("roundID " + round + " is not a round group of table/dungeonrounddata.xml of the " + s.Profile.Title);
                    foreach (string t in texts)
                    {
                        if (mirror && said.Contains(id + "\n" + t.Replace(s.Profile.Title, ""))) continue;
                        if (s.Profile == Profile.Server) said.Add(id + "\n" + t.Replace(s.Profile.Title, ""));
                        plan.Msg(plan.Warnings, mod, s.Archive, s.Entry, label + ": " + t);
                    }
                    if (isNew && !origHas.Contains(id)) mine.Add(id);
                }
                foreach (KeyValuePair<string, int> kv in seen)
                {
                    // an id the shipped table already has that often is not this plan's doing: it does not block Apply
                    int shipped;
                    origIds.TryGetValue(kv.Key, out shipped);
                    if (kv.Value < 2 || kv.Value <= shipped && mirror) continue;
                    bool error = s.Profile == Profile.Server && kv.Value > shipped;
                    if (mirror && kv.Value > shipped && said.Contains(kv.Key + "\ntwice")) continue;
                    if (error) said.Add(kv.Key + "\ntwice");
                    string twice = kv.Key.Substring(0, kv.Key.IndexOf('\n'));
                    plan.Msg(error ? plan.Errors : plan.Warnings, RoomMod(run, twice), s.Archive, s.Entry, "dungeon " + twice + " is in the table " + kv.Value + " times"
                        + (s.Kms ? ": the client reads the last row" : s.Profile == Profile.Server ? ": the server's data ingest fails" : ""));
                }
            }

            for (int i = 0; i < sides.Count; i++)
                foreach (string id in added[i])
                    for (int k = 0; k < sides.Count; k++)
                        if (k != i && !has[k].Contains(id))
                            plan.Msg(plan.Warnings, RoomMod(run, id), sides[i].Archive, sides[i].Entry, "dungeon " + id + " is added here but not in " + sides[k].Archive
                                + ": client and server read dungeons from their own table");
        }
    }
}
