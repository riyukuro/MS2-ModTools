// The plan's checks of spawn rows (table/mapspawntag.xml, see Spawns.cs): every row the mods add or change is read
// as it ends up in each target and checked with SpawnRules. On top of the editor's checks the plan knows the final
// tables: a row a table then has twice (same map, point, difficulty, feature and locale), a map the target does
// not have, a new row whose map has no spawn point with that number (the map files as the plan leaves them), and
// a tag a row did not ship with that no NPC carries (the NPC data as the plan leaves it). A shipped row is found
// by map, point, feature and locale, so one the mod moves to another difficulty range is a changed row, not a new one.
using System;
using System.Collections.Generic;
using System.Globalization;
using MS2.ModKit.M2d;
using MS2.ModKit.MapEdit;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        static bool IsSpawnEntry(string entry)
        {
            return string.Equals((entry ?? "").Replace('\\', '/'), SpawnRules.Entry, StringComparison.OrdinalIgnoreCase);
        }

        // the mod a spawn row is blamed on: the last mod with lines for the spawn table
        static Mod SpawnMod(Run run)
        {
            Mod any = null;
            foreach (Mod m in run.Plan.Mods)
                foreach (ModOp op in m.Ops)
                {
                    var ed = op as EditOp;
                    if (ed != null && IsSpawnEntry(ed.Entry)) any = m;
                }
            return any;
        }

        static void SpawnChecks(Run run)
        {
            Mod mod = SpawnMod(run);
            if (mod == null) return;        // rows of a map file's <spawn> are checked with the map
            Plan plan = run.Plan;
            var profiles = new List<Profile>(run.Data.Profiles);
            // the server before the global client: a line mirrored to the latter says a thing once
            profiles.Sort((x, y) => (x == Profile.GmsClient ? 1 : 0) - (y == Profile.GmsClient ? 1 : 0));
            var said = new HashSet<string>();
            var flats = new Dictionary<string, FlatIndex>();
            SpawnTags tags = null;
            HashSet<string> modTags = null;
            foreach (Profile p in profiles)
            {
                string archive = p.ArchiveIn("x:Xml.m2d");
                if (!run.Data.Exists(archive)) continue;
                M2dArchive arch = run.Data.Open(archive);
                M2dEntry entry = arch.Find(SpawnRules.Entry);
                if (entry == null || run.State(arch, archive, entry.Name).Final == null) continue;
                XmlDoc orig = OrigDoc(run, archive, entry.Name), doc = FinalDoc(run, archive, entry.Name);
                if (orig == null || orig.Root == null || doc == null || doc.Root == null) continue;

                // a row is found by its id (SpawnRules.Id); a table that ships an id twice has a text per row
                var shipped = new Dictionary<string, List<string>>();
                var shippedCount = new Dictionary<string, int>();
                foreach (XmlElem e in orig.Root.Children)
                {
                    if (e.Name != SpawnRules.Row) continue;
                    string id = SpawnRules.Id(e.Attr), k = SpawnRules.Key(e.Attr);
                    List<string> texts;
                    if (!shipped.TryGetValue(id, out texts)) shipped[id] = texts = new List<string>();
                    texts.Add(RowText(e));
                    int n;
                    shippedCount.TryGetValue(k, out n);
                    shippedCount[k] = n + 1;
                }
                var count = new Dictionary<string, int>();
                var idCount = new Dictionary<string, int>();
                foreach (XmlElem e in doc.Root.Children)
                {
                    if (e.Name != SpawnRules.Row) continue;
                    string k = SpawnRules.Key(e.Attr), id = SpawnRules.Id(e.Attr);
                    int n;
                    count.TryGetValue(k, out n);
                    count[k] = n + 1;
                    idCount.TryGetValue(id, out n);
                    idCount[id] = n + 1;
                }
                Dictionary<long, bool> maps = IdsOf(run, p, "map/", "table/fielddata.xml", "<fieldData id=\"");
                var twice = new HashSet<string>();
                foreach (XmlElem e in doc.Root.Children)
                {
                    if (e.Name != SpawnRules.Row) continue;
                    string k = SpawnRules.Key(e.Attr);
                    List<string> was;
                    bool isNew = !shipped.TryGetValue(SpawnRules.Id(e.Attr), out was);
                    bool asShipped = !isNew && was.Contains(RowText(e));
                    int n0;
                    shippedCount.TryGetValue(k, out n0);
                    if (asShipped && count[k] <= n0) continue;
                    long map = SpawnRules.MapOf(e.Attr(SpawnRules.Map));
                    string what = "spawn row of map " + (map > 0 ? map.ToString(CultureInfo.InvariantCulture) : "\"" + e.Attr(SpawnRules.Map) + "\"") + " point " + (e.Attr(SpawnRules.Point) ?? "?");
                    if (count[k] > Math.Max(n0, 1) && twice.Add(k))
                        Say(plan, said, plan.Errors, mod, archive, entry.Name, what + ": the table ends up with " + count[k] + " rows for the same difficulty, feature and locale");
                    if (asShipped) continue;
                    foreach (string problem in SpawnRules.CheckRow(e.Attr))
                        Say(plan, said, plan.Warnings, mod, archive, entry.Name, what + ": " + problem);
                    // a row the mod adds next to shipped ones of its point (another difficulty range) is an added row too
                    bool added = isNew || idCount[SpawnRules.Id(e.Attr)] > was.Count;
                    if (added && map > 0 && maps != null && !maps.ContainsKey(map))
                        Say(plan, said, plan.Warnings, mod, archive, entry.Name, what + ": this target has no map " + map);
                    long point;
                    if (isNew && map > 0 && long.TryParse(e.Attr(SpawnRules.Point) ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out point))
                    {
                        string where;
                        bool unsure;
                        HashSet<long> points = PointsOf(run, p, map, flats, out where, out unsure);
                        if (points != null && !unsure && !points.Contains(point))
                            Say(plan, said, plan.Errors, mod, archive, entry.Name, what + ": the map has no spawn point " + point + " (" + where + "), so nothing spawns; add the point in the Maps view first");
                    }
                    // a tag the row did not ship with must be one an NPC carries
                    var had = new List<string>();
                    string rowId = SpawnRules.Id(e.Attr);
                    if (!isNew)
                        foreach (XmlElem o in orig.Root.Children)
                            if (o.Name == SpawnRules.Row && SpawnRules.Id(o.Attr) == rowId) had.AddRange(SpawnRules.Tags(o.Attr(SpawnRules.Tag)));
                    foreach (string t in SpawnRules.Tags(e.Attr(SpawnRules.Tag)))
                    {
                        if (had.Contains(t)) continue;
                        if (tags == null) { tags = ShippedTags(run); modTags = ModTags(run, tags); }
                        if (tags.Archive == null) break;
                        if (tags.Of(t).Count == 0 && !modTags.Contains(t))
                            Say(plan, said, plan.Errors, mod, archive, entry.Name, what + ": no NPC of " + tags.Archive + " has the tag \"" + t + "\", so nothing spawns");
                    }
                }
            }
        }

        // the tags of the shipped NPC data, read once per data set
        static SpawnTags ShippedTags(Run run)
        {
            var t = run.Data.SpawnTagsCache as SpawnTags;
            if (t == null) run.Data.SpawnTagsCache = t = SpawnTags.Scan(run.Data, null, false);
            return t;
        }

        // the tags in the NPC files the plan adds or changes
        static HashSet<string> ModTags(Run run, SpawnTags shipped)
        {
            var set = new HashSet<string>();
            if (shipped.Archive == null) return set;
            foreach (EntryState st in run.States.Values)
            {
                if (st.Archive != shipped.Archive || st.DeletedBy != null || (st.Final == null && st.Base == st.Original)) continue;
                string n = st.Name.ToLowerInvariant();
                if (!n.StartsWith("npc/", StringComparison.Ordinal) && !n.StartsWith("npcdata/", StringComparison.Ordinal)) continue;
                byte[] b = st.Final ?? st.Base;
                if (b != null) foreach (string t in SpawnTags.TagsIn(b)) set.Add(t);
            }
            return set;
        }

        // The spawn point numbers of every map file of a map, as the plan leaves them, in the target's own map archive
        // (else in the first one the data set has). null = no map file to look at; unsure = an entity of a model
        // the listing does not have may be a spawn point.
        static HashSet<long> PointsOf(Run run, Profile p, long map, Dictionary<string, FlatIndex> flats, out string where, out bool unsure)
        {
            where = null;
            unsure = false;
            string ar = p.Root + ":" + MapArchivePath;
            if (!run.Data.Exists(ar))
            {
                ar = null;
                foreach (Profile o in run.Data.Profiles)
                    if (ar == null && run.Data.Exists(o.Root + ":" + MapArchivePath)) ar = o.Root + ":" + MapArchivePath;
                if (ar == null) return null;
            }
            M2dArchive arch = run.Data.Open(ar);
            FlatIndex flat;
            if (!flats.TryGetValue(ar, out flat))
            {
                try { flat = FlatIndex.FromArchiveOnDemand(arch); }
                catch (ModKitException) { flat = null; }
                flats[ar] = flat;
            }
            string prefix = "xblock/" + map.ToString("D8", CultureInfo.InvariantCulture);
            HashSet<long> points = null;
            foreach (M2dEntry e in arch.Entries)
            {
                string n = e.Name.ToLowerInvariant();
                if (!n.StartsWith(prefix, StringComparison.Ordinal) || !n.EndsWith(".xblock", StringComparison.Ordinal)) continue;
                char next = n[prefix.Length];
                if (next >= '0' && next <= '9') continue;
                XmlDoc doc = FinalDoc(run, ar, e.Name);
                XmlElem set = doc == null || doc.Root == null ? null : EntitySet(doc);
                if (set == null) { unsure = true; continue; }
                if (points == null) points = new HashSet<long>();
                where = where == null ? ar + " " + e.Name : where + ", " + e.Name;
                foreach (XmlElem x in set.Children)
                {
                    if (x.Name != "entity") continue;
                    MapEnt me = ReadEnt(x, flat);
                    if (me.Spawn >= 0) points.Add(me.Spawn);
                    if (flat == null || (me.NoModel && me.SpawnDefault)) unsure = true;
                }
            }
            return points;
        }

        static void Say(Plan plan, HashSet<string> said, List<PlanMessage> to, Mod mod, string archive, string entry, string text)
        {
            if (said.Add(text)) plan.Msg(to, mod, archive, entry, text);
        }
    }
}
