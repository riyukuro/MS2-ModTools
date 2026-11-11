// Which map file does the game load for a map? The map table of a target's Xml.m2d says it: one row per map id
// (rows of table/fielddata.xml, or one file map/<id>.xml), with one <environment> per feature, each naming a map
// file (<xblock name="..."/>, in any letter case). A map may so have several map files; the features that are on
// decide which one is loaded.
//
// A mod that changes a map file no row names changes nothing in the game: a warning, with the file(s) the table
// names for the map id when there is such a row. A mod that changes one of several files of a map: nothing when it
// is the file the target loads under its features (Features.cs), a warning naming the loaded file when it is
// another one, and a note when the target's features cannot be known.
// The table is read after the plan's own changes to it, so a mod that brings a new map with its row is not warned.
using System;
using System.Collections.Generic;
using System.Globalization;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        public const string MapTableBundle = "table/fielddata.xml", MapTableFolder = "map/";

        sealed class MapFileUse
        {
            internal long MapId;
            internal string File, Feature, Locale;      // File as the table writes it
        }

        sealed class MapTable
        {
            /// <summary>lower-case map file name -> the rows that name it</summary>
            internal readonly Dictionary<string, List<MapFileUse>> ByFile = new Dictionary<string, List<MapFileUse>>(StringComparer.Ordinal);
            internal readonly Dictionary<long, List<MapFileUse>> ByMap = new Dictionary<long, List<MapFileUse>>();

            internal void Add(long mapId, XmlElem row)
            {
                foreach (XmlElem env in row.Children)
                {
                    if (env.Name != "environment") continue;
                    foreach (XmlElem x in env.Children)
                    {
                        string file = x.Name == "xblock" ? x.Attr("name") : null;
                        if (string.IsNullOrEmpty(file)) continue;
                        var use = new MapFileUse { MapId = mapId, File = file, Feature = env.Attr("feature") ?? "", Locale = env.Attr("locale") ?? "" };
                        List<MapFileUse> l;
                        string key = file.ToLowerInvariant();
                        if (!ByFile.TryGetValue(key, out l)) ByFile[key] = l = new List<MapFileUse>();
                        l.Add(use);
                        if (!ByMap.TryGetValue(mapId, out l)) ByMap[mapId] = l = new List<MapFileUse>();
                        l.Add(use);
                    }
                }
            }
        }

        // the bytes of an entry as the plan has them so far; null = deleted by a mod
        static byte[] PlannedBytes(Run run, string ar, M2dArchive arch, M2dEntry e)
        {
            EntryState st;
            if (!run.States.TryGetValue(ar + "\n" + e.Name.ToLowerInvariant(), out st)) return arch.Read(e);
            return PlannedBytes(st);
        }

        static byte[] PlannedBytes(EntryState st)
        {
            if (st.DeletedBy != null) return null;
            if (st.Ed != null && !st.Ed.IsEmpty)
            {
                try { return st.Ed.ApplyBytes(); }
                catch (ModKitException) { }
            }
            return st.Base;
        }

        static void ReadMapRows(MapTable table, string name, byte[] bytes, string label)
        {
            if (bytes == null) return;
            XmlDoc doc;
            try { doc = XmlDoc.Parse(bytes, label); }
            catch (ModKitException) { return; }
            if (doc.Root == null) return;
            if (name.StartsWith(MapTableFolder, StringComparison.Ordinal))
            {
                long id = FileNumber(name);
                if (id >= 0) table.Add(id, doc.Root);
                return;
            }
            foreach (XmlElem row in doc.Root.Children)
            {
                long id;
                if (row.Name == "fieldData" && long.TryParse(row.Attr("id") ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out id)) table.Add(id, row);
            }
        }

        /// <summary>The map table of a target after the plan so far; null when the target has none.</summary>
        static MapTable MapTableOf(Run run, MapRun mr, Profile target)
        {
            MapTable table;
            if (mr.Tables.TryGetValue(target.Root, out table)) return table;
            mr.Tables[target.Root] = null;
            string ar = target.Root + ":Xml.m2d";
            if (!run.Data.Exists(ar)) return null;
            M2dArchive arch;
            try { arch = run.Data.Open(ar); } catch (ModKitException) { return null; }
            table = new MapTable();
            bool found = false;
            foreach (M2dEntry e in arch.Entries)
            {
                string n = e.Name.ToLowerInvariant();
                if (!n.EndsWith(".xml", StringComparison.Ordinal)) continue;
                if (n != MapTableBundle && !n.StartsWith(MapTableFolder, StringComparison.Ordinal)) continue;
                found = true;
                ReadMapRows(table, n, PlannedBytes(run, ar, arch, e), ar + " " + e.Name);
            }
            foreach (EntryState st in run.Order)        // entries a mod of this plan adds
            {
                if (st.Archive != ar || st.Existed) continue;
                string n = st.Name.ToLowerInvariant();
                if (n != MapTableBundle && !(n.StartsWith(MapTableFolder, StringComparison.Ordinal) && n.EndsWith(".xml", StringComparison.Ordinal))) continue;
                found = true;
                ReadMapRows(table, n, PlannedBytes(st), ar + " " + st.Name);
            }
            if (!found) return null;
            mr.Tables[target.Root] = table;
            return table;
        }

        // an entry of a target's Xml.m2d as the plan has it so far; null when there is none
        static byte[] PlannedEntry(Run run, string ar, M2dArchive arch, string name)
        {
            M2dEntry e = arch.Find(name);
            if (e != null) return PlannedBytes(run, ar, arch, e);
            EntryState st;
            return run.States.TryGetValue(ar + "\n" + name, out st) ? PlannedBytes(st) : null;
        }

        /// <summary>The features of a target after the plan so far; null when they cannot be known.</summary>
        static FeatureSet FeaturesOf(Run run, MapRun mr, Profile target)
        {
            FeatureSet set;
            if (mr.Features.TryGetValue(target.Root, out set)) return set;
            mr.Features[target.Root] = null;
            string ar = target.Root + ":Xml.m2d";
            if (!run.Data.Exists(ar)) return null;
            M2dArchive arch;
            try { arch = run.Data.Open(ar); } catch (ModKitException) { return null; }
            string locale, type;
            run.Data.FeatureOption(target, out locale, out type);
            set = FeatureSet.Read(PlannedEntry(run, ar, arch, FeatureSet.SettingTable), PlannedEntry(run, ar, arch, FeatureSet.FeatureTable), locale, type, target.IsServer);
            mr.Features[target.Root] = set;
            return set;
        }

        // the row of a map the game takes under a feature set; null when it takes none
        static MapFileUse LoadedFile(FeatureSet set, List<MapFileUse> ofMap)
        {
            MapFileUse best = null;
            foreach (MapFileUse u in ofMap)
            {
                if (!set.Allows(u.Feature, u.Locale)) continue;
                if (best == null || set.SecondWins(best.Feature, best.Locale, u.Feature, u.Locale)) best = u;
            }
            return best;
        }

        static string FilesText(List<MapFileUse> uses)
        {
            var parts = new List<string>();
            foreach (MapFileUse u in uses)
            {
                string p = u.File + (u.Feature.Length > 0 ? " (feature " + u.Feature : " (no feature") + (u.Locale.Length > 0 ? ", locale " + u.Locale : "") + ")";
                if (!parts.Contains(p)) parts.Add(p);
            }
            return string.Join(", ", parts.ToArray());
        }

        /// <summary>Once per map file and target: is the file one the game loads?</summary>
        static void MapFileCheck(Run run, MapRun mr, Mod mod, MapOp mop, MapState ms)
        {
            MapTable table = MapTableOf(run, mr, ms.Target);
            if (table == null) return;
            Plan plan = run.Plan;
            string at = " (" + ms.Target.Name + ", mod.xml line " + mop.Line + ")";
            List<MapFileUse> uses;
            if (!table.ByFile.TryGetValue(mop.XBlock.ToLowerInvariant(), out uses))
            {
                List<MapFileUse> ofMap;
                string hint = ms.MapId >= 0 && table.ByMap.TryGetValue(ms.MapId, out ofMap)
                    ? "; for map " + D(ms.MapId) + " the map table names " + FilesText(ofMap)
                    : "";
                plan.Msg(plan.Warnings, mod, ms.ArchiveRef, ms.St.Name, "no map of this target's map table uses this map file, so the game does not load it" + hint + at);
                return;
            }
            var seen = new Dictionary<long, bool>();
            foreach (MapFileUse u in uses)
            {
                if (seen.ContainsKey(u.MapId)) continue;
                seen[u.MapId] = true;
                List<MapFileUse> ofMap = table.ByMap[u.MapId];
                bool other = false;
                foreach (MapFileUse o in ofMap) if (!string.Equals(o.File, u.File, StringComparison.OrdinalIgnoreCase)) other = true;
                if (!other) continue;
                FeatureSet set = FeaturesOf(run, mr, ms.Target);
                if (set != null)
                {
                    MapFileUse loaded = LoadedFile(set, ofMap);
                    if (loaded != null && string.Equals(loaded.File, u.File, StringComparison.OrdinalIgnoreCase)) continue;
                    plan.Msg(plan.Warnings, mod, ms.ArchiveRef, ms.St.Name, "with this target's features (" + set.Text + ") the " + (set.ServerRule ? "server" : "game") + " loads "
                        + (loaded != null ? loaded.File : "no map file") + " for map " + D(u.MapId) + ", not this map file; the map table names " + FilesText(ofMap) + at);
                }
                else
                    plan.Msg(plan.Notes, mod, ms.ArchiveRef, ms.St.Name, "map " + D(u.MapId) + " has more than one map file in the map table: " + FilesText(ofMap) + ". The features that are on decide which one the game loads; this mod changes only this one" + at);
            }
        }
    }
}
