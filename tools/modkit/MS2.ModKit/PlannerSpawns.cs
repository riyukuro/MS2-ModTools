// The plan's checks of spawn rows (table/mapspawntag.xml, see Spawns.cs): every row the mods add or change is read
// as it ends up in each target and checked with SpawnRules. On top of the editor's checks the plan knows the final
// tables: a row a table then has twice (same map, point, difficulty, feature and locale) and a map the target
// does not have.
using System;
using System.Collections.Generic;
using System.Globalization;
using MS2.ModKit.M2d;
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
            foreach (Profile p in profiles)
            {
                string archive = p.ArchiveIn("x:Xml.m2d");
                if (!run.Data.Exists(archive)) continue;
                M2dArchive arch = run.Data.Open(archive);
                M2dEntry entry = arch.Find(SpawnRules.Entry);
                if (entry == null || run.State(arch, archive, entry.Name).Final == null) continue;
                XmlDoc orig = OrigDoc(run, archive, entry.Name), doc = FinalDoc(run, archive, entry.Name);
                if (orig == null || orig.Root == null || doc == null || doc.Root == null) continue;

                var shipped = new Dictionary<string, string>();
                var shippedCount = new Dictionary<string, int>();
                foreach (XmlElem e in orig.Root.Children)
                {
                    if (e.Name != SpawnRules.Row) continue;
                    string k = SpawnRules.Key(e.Attr);
                    shipped[k] = RowText(e);
                    int n;
                    shippedCount.TryGetValue(k, out n);
                    shippedCount[k] = n + 1;
                }
                var count = new Dictionary<string, int>();
                foreach (XmlElem e in doc.Root.Children)
                {
                    if (e.Name != SpawnRules.Row) continue;
                    string k = SpawnRules.Key(e.Attr);
                    int n;
                    count.TryGetValue(k, out n);
                    count[k] = n + 1;
                }
                Dictionary<long, bool> maps = IdsOf(run, p, "map/", "table/fielddata.xml", "<fieldData id=\"");
                var twice = new HashSet<string>();
                foreach (XmlElem e in doc.Root.Children)
                {
                    if (e.Name != SpawnRules.Row) continue;
                    string k = SpawnRules.Key(e.Attr), was;
                    bool isNew = !shipped.TryGetValue(k, out was);
                    int n0;
                    shippedCount.TryGetValue(k, out n0);
                    if (!isNew && was == RowText(e) && count[k] <= n0) continue;      // as shipped
                    long map = SpawnRules.MapOf(e.Attr(SpawnRules.Map));
                    string what = "spawn row of map " + (map > 0 ? map.ToString(CultureInfo.InvariantCulture) : "\"" + e.Attr(SpawnRules.Map) + "\"") + " point " + (e.Attr(SpawnRules.Point) ?? "?");
                    if (count[k] > Math.Max(n0, 1) && twice.Add(k))
                        Say(plan, said, plan.Errors, mod, archive, entry.Name, what + ": the table ends up with " + count[k] + " rows for the same difficulty, feature and locale");
                    if (!isNew && was == RowText(e)) continue;
                    foreach (string problem in SpawnRules.CheckRow(e.Attr))
                        Say(plan, said, plan.Warnings, mod, archive, entry.Name, what + ": " + problem);
                    if (isNew && map > 0 && maps != null && !maps.ContainsKey(map))
                        Say(plan, said, plan.Warnings, mod, archive, entry.Name, what + ": this target has no map " + map);
                }
            }
        }

        static void Say(Plan plan, HashSet<string> said, List<PlanMessage> to, Mod mod, string archive, string entry, string text)
        {
            if (said.Add(text)) plan.Msg(to, mod, archive, entry, text);
        }
    }
}
