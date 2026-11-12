// Monster spawns of a map: the rows of table/mapspawntag.xml. A spawn point of a map file is only a place; the row
// with the map's code and the point's number says what appears there: `tag` names monsters by the tags of their
// NPC data (basic/@mainTags; several tags are separated by commas), `population` how many, `coolTime` the seconds
// until they come back, `difficulty`/`difficultyMin` the levels of the monsters taken, `petPopulation` and
// `petSpawnProbability` the catchable pets. The server decides the spawns from its copy; the Korean client and the
// global client ship a copy too (the same attributes; the server's rows may also carry `locale`).
//
// The editor shows the rows of one map, paired between the copies by map, point, difficulty, feature and locale, and
// writes a change to every copy that has the row (the plan mirrors the server's lines to the global client). The rows
// of a spawn point the mod adds with <spawn> in a map file are that op's own <row> children and are not shown here.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static class SpawnRules
    {
        public const string Entry = "table/mapspawntag.xml", Row = "region";
        public const string Map = "mapCode", Point = "spawnPointID", Tag = "tag", Population = "population", CoolTime = "coolTime";
        public const string Difficulty = "difficulty", DifficultyMin = "difficultyMin", PetPopulation = "petPopulation", PetProbability = "petSpawnProbability";

        /// <summary>The fields a row is found by; they are not changed (remove the row and add another).</summary>
        public static readonly string[] Fixed = { Map, Point, "feature", "locale" };
        /// <summary>The fields in the order they are shown.</summary>
        public static readonly string[] Known = { Point, Tag, Population, CoolTime, Difficulty, DifficultyMin, PetPopulation, PetProbability, "feature", "locale" };
        static readonly string[] Whole = { Point, Population, CoolTime, Difficulty, DifficultyMin, PetPopulation, PetProbability };

        /// <summary>A map id as the table writes it: eight digits.</summary>
        public static string Code(long map) { return map.ToString("D8", CultureInfo.InvariantCulture); }

        public static long MapOf(string code)
        {
            long n;
            return long.TryParse((code ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n) ? n : -1;
        }

        /// <summary>What tells two rows of a table apart.</summary>
        public static string Key(Func<string, string> get)
        {
            return MapOf(get(Map)).ToString(CultureInfo.InvariantCulture) + "\n" + (get(Point) ?? "") + "\n" + (get(Difficulty) ?? "") + "\n" + (get(DifficultyMin) ?? "")
                + "\n" + (get("feature") ?? "") + "\n" + (get("locale") ?? "");
        }

        public static List<string> Tags(string tag)
        {
            var l = new List<string>();
            foreach (string t in (tag ?? "").Split(',')) if (t.Trim().Length > 0) l.Add(t.Trim());
            return l;
        }

        /// <summary>What is wrong with a row by itself.</summary>
        public static List<string> CheckRow(Func<string, string> get)
        {
            var l = new List<string>();
            if (MapOf(get(Map)) <= 0) l.Add(Map + " \"" + get(Map) + "\" is not a map id");
            foreach (string f in Whole)
            {
                string v = get(f);
                long n;
                if (v == null) { if (f == Point || f == Population) l.Add(f + " is not set"); continue; }
                if (!long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out n)) l.Add(f + " \"" + v + "\" is not a whole number");
            }
            long pop, lo, hi;
            bool some = long.TryParse(get(Population) ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out pop) && pop > 0;
            if (some && Tags(get(Tag)).Count == 0) l.Add("no tag: nothing is spawned at the point");
            if (long.TryParse(get(Difficulty) ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out hi)
                && long.TryParse(get(DifficultyMin) ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out lo) && lo > hi)
                l.Add(DifficultyMin + " " + lo + " is above " + Difficulty + " " + hi + ": no monster is in that range");
            return l;
        }
    }

    /// <summary>The NPCs that carry each tag (basic/@mainTags of the NPC data), read once from every NPC file.</summary>
    public sealed class SpawnTags
    {
        public readonly Dictionary<string, List<long>> Npcs = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        public string Archive;
        public int Files, NpcCount;
        /// <summary>The names of the NPCs (the English ones when the data has them), for showing a tag's NPCs.</summary>
        public Dictionary<long, string> Names = new Dictionary<long, string>();

        /// <summary>The NPCs of a tag as text: how many, and the first few with their names.</summary>
        public string Describe(string tag, int max)
        {
            List<long> l = Of(tag);
            if (l.Count == 0) return "no NPC";
            var sb = new StringBuilder(l.Count + (l.Count == 1 ? " NPC: " : " NPCs: "));
            for (int i = 0; i < l.Count && i < max; i++)
            {
                string name;
                if (i > 0) sb.Append(", ");
                sb.Append(l[i].ToString(CultureInfo.InvariantCulture));
                if (Names.TryGetValue(l[i], out name) && name.Length > 0) sb.Append(' ').Append(name);
            }
            if (l.Count > max) sb.Append(", ...");
            return sb.ToString();
        }

        static readonly Regex Bundle = new Regex("<npc\\s[^>]*?\\bid=\"(\\d+)\"|\\smainTags=\"([^\"]*)\"", RegexOptions.CultureInvariant);
        static readonly Regex Own = new Regex("\\smainTags=\"([^\"]*)\"", RegexOptions.CultureInvariant);
        static readonly Regex FileId = new Regex("/(\\d+)\\.xml$", RegexOptions.CultureInvariant);

        public List<long> Of(string tag)
        {
            List<long> l;
            return Npcs.TryGetValue(tag, out l) ? l : new List<long>();
        }

        void Put(long npc, string tags)
        {
            foreach (string t in SpawnRules.Tags(XmlDoc.Unescape(tags)))
            {
                List<long> l;
                if (!Npcs.TryGetValue(t, out l)) Npcs[t] = l = new List<long>();
                if (!l.Contains(npc)) l.Add(npc);
            }
        }

        /// <summary>Reads the tags of every NPC: of the server's files (it decides the spawns), else of a client's.</summary>
        public static SpawnTags Scan(DataSet data, Action<ProgressInfo> progress)
        {
            var st = new SpawnTags();
            foreach (Profile p in new[] { Profile.Server, Profile.GmsClient, Profile.KmsClient })
            {
                if (!data.Has(p)) continue;
                string archive = p.ArchiveIn("x:Xml.m2d");
                M2dArchive a = data.Open(archive);
                bool bundles = p == Profile.KmsClient;
                var files = new List<M2dEntry>();
                foreach (M2dEntry e in a.Entries)
                    if (e.Name.StartsWith(bundles ? "npcdata/" : "npc/", StringComparison.OrdinalIgnoreCase) && e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) files.Add(e);
                if (files.Count == 0) continue;
                st.Archive = archive;
                var seen = new HashSet<long>();
                long read = 0;
                for (int i = 0; i < files.Count; i++)
                {
                    if (i % 200 == 0) ProgressUtil.Report(progress, "reading the NPC tags", files[i].Name, i, files.Count);
                    byte[] b = a.Read(files[i]);
                    // thousands of files in a row: the buffers are collected before they pile up in a 32-bit host
                    if ((read += b.Length) > 32L * 1024 * 1024) { read = 0; GC.Collect(); }
                    string text = Encoding.UTF8.GetString(b);
                    st.Files++;
                    if (bundles)
                    {
                        long id = -1;
                        foreach (Match m in Bundle.Matches(text))
                        {
                            if (m.Groups[1].Success) { id = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); seen.Add(id); }
                            else if (id >= 0) st.Put(id, m.Groups[2].Value);
                        }
                    }
                    else
                    {
                        Match f = FileId.Match(files[i].Name);
                        if (!f.Success) continue;
                        long id = long.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture);
                        seen.Add(id);
                        foreach (Match m in Own.Matches(text)) st.Put(id, m.Groups[1].Value);
                    }
                }
                st.NpcCount = seen.Count;
                try { st.Names = TableIdList.NpcNames(data, Mod.Parse("<mod id=\"read\" name=\"read\" version=\"1\" />", "read")).Names(); }
                catch (ModKitException) { }
                foreach (List<long> l in st.Npcs.Values) l.Sort();
                return st;
            }
            return st;
        }
    }

    public sealed class SpawnSide
    {
        public string Archive;
        public TableDraft Table;
    }

    /// <summary>One spawn row: its row in each copy of the table (null where that copy has none).</summary>
    public sealed class SpawnRow
    {
        public readonly TableRow[] Rows;
        internal SpawnRow(int sides) { Rows = new TableRow[sides]; }
        public bool IsAdded { get { bool any = false; foreach (TableRow r in Rows) if (r != null) { if (!r.IsAdded) return false; any = true; } return any; } }
        public bool IsDeleted { get { foreach (TableRow r in Rows) if (r != null && !r.IsDeleted) return false; return true; } }
        public bool IsChanged { get { foreach (TableRow r in Rows) if (r != null && (r.IsAdded || r.IsDeleted || r.IsChanged)) return true; return false; } }
    }

    public sealed class SpawnEditor
    {
        public readonly DataSet Data;
        public readonly Mod Mod;
        public readonly List<SpawnSide> Sides = new List<SpawnSide>();
        readonly Dictionary<long, List<SpawnRow>> byMap = new Dictionary<long, List<SpawnRow>>();
        /// <summary>The NPCs of each tag, when they were read (SpawnTags.Scan): Check then names a tag no NPC has.</summary>
        public SpawnTags Tags;

        public SpawnEditor(DataSet data, Mod mod)
        {
            Data = data;
            Mod = mod;
            foreach (string a in SideArchives(data, mod)) Sides.Add(new SpawnSide { Archive = a, Table = new TableDraft(data, mod, a, SpawnRules.Entry, null) });
            if (Sides.Count == 0) throw new ModKitException("no target of the data set has a spawn table (" + SpawnRules.Entry + ")");
            Pair();
        }

        /// <summary>The archives a row is written to: the server's and the Korean client's Xml.m2d, and the global
        /// client's when the data set has no server or the mod already has lines for its table (else the server's
        /// lines are mirrored to it by the plan).</summary>
        public static List<string> SideArchives(DataSet data, Mod mod)
        {
            var l = new List<string>();
            foreach (Profile p in new[] { Profile.Server, Profile.KmsClient, Profile.GmsClient })
            {
                if (!data.Has(p)) continue;
                string a = p.ArchiveIn("x:Xml.m2d");
                if (p == Profile.GmsClient && data.Has(Profile.Server) && !HasLines(mod, a)) continue;
                if (data.Open(a).Find(SpawnRules.Entry) != null) l.Add(a);
            }
            return l;
        }

        static bool HasLines(Mod mod, string archive)
        {
            archive = DataSet.NormalizeRef(archive);
            foreach (ModOp op in mod.Ops)
            {
                var ed = op as EditOp;
                if (ed != null && ed.Archive == archive && string.Equals(ed.Entry.Replace('\\', '/'), SpawnRules.Entry, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // a shipped row is found by what it ships with; a row of the mod by what it says now
        string KeyOf(int side, TableRow r)
        {
            TableDraft t = Sides[side].Table;
            return SpawnRules.Key(a => r.IsAdded ? t.Get(r, a) : t.GetOriginal(r, a));
        }

        void Pair()
        {
            var at = new Dictionary<string, SpawnRow>(StringComparer.Ordinal);
            for (int i = 0; i < Sides.Count; i++)
            {
                TableDraft t = Sides[i].Table;
                var taken = new HashSet<SpawnRow>();
                foreach (TableRow r in t.Rows)
                {
                    if (r.Base == null || r.Base.Name != SpawnRules.Row) continue;
                    string key = KeyOf(i, r);
                    SpawnRow s;
                    if (!at.TryGetValue(key, out s) || taken.Contains(s))
                    {
                        s = new SpawnRow(Sides.Count);
                        if (!at.ContainsKey(key)) at[key] = s;
                        long map = SpawnRules.MapOf(r.IsAdded ? t.Get(r, SpawnRules.Map) : t.GetOriginal(r, SpawnRules.Map));
                        List<SpawnRow> l;
                        if (!byMap.TryGetValue(map, out l)) byMap[map] = l = new List<SpawnRow>();
                        l.Add(s);
                    }
                    taken.Add(s);
                    s.Rows[i] = r;
                }
            }
        }

        // ---- reading ----

        /// <summary>The maps that have spawn rows, in the order of their ids.</summary>
        public List<long> Maps()
        {
            var l = new List<long>(byMap.Keys);
            l.Sort();
            return l;
        }

        /// <summary>The rows of a map, in the order of the tables (rows the mod adds last).</summary>
        public List<SpawnRow> Of(long map)
        {
            List<SpawnRow> l;
            return byMap.TryGetValue(map, out l) ? new List<SpawnRow>(l) : new List<SpawnRow>();
        }

        Dictionary<long, string> mapNames;

        /// <summary>The shipped names of the maps (empty when the data has no name file).</summary>
        public Dictionary<long, string> MapNames()
        {
            if (mapNames != null) return mapNames;
            try { mapNames = TableIdList.Maps(Data, Mod).Names(); }
            catch (ModKitException) { mapNames = new Dictionary<long, string>(); }
            return mapNames;
        }

        public string MapName(long map)
        {
            string n;
            return MapNames().TryGetValue(map, out n) ? n : "";
        }

        /// <summary>The maps whose id or name holds the text, or that have a row with a tag holding it; all when empty.</summary>
        public List<long> Find(string text)
        {
            text = (text ?? "").Trim();
            var l = new List<long>();
            foreach (long map in Maps())
            {
                bool hit = text.Length == 0 || SpawnRules.Code(map).IndexOf(text, StringComparison.Ordinal) >= 0
                    || MapName(map).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!hit)
                    foreach (SpawnRow s in byMap[map])
                        if (Get(s, SpawnRules.Tag).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
                if (hit) l.Add(map);
            }
            return l;
        }

        public int CountOf(long map)
        {
            List<SpawnRow> l;
            return byMap.TryGetValue(map, out l) ? l.Count : 0;
        }

        public bool IsChanged(long map)
        {
            List<SpawnRow> l;
            if (byMap.TryGetValue(map, out l)) foreach (SpawnRow s in l) if (s.IsChanged) return true;
            return false;
        }

        /// <summary>The field on one side ("" when that side has no row or no such attribute).</summary>
        public string Get(SpawnRow s, int side, string field)
        {
            return s.Rows[side] == null ? "" : Sides[side].Table.Get(s.Rows[side], field) ?? "";
        }

        /// <summary>The field as the first side that has the row says it (the server first).</summary>
        public string Get(SpawnRow s, string field)
        {
            for (int i = 0; i < Sides.Count; i++) if (s.Rows[i] != null) return Get(s, i, field);
            return "";
        }

        /// <summary>True when the copies that have the row say different things in the field.</summary>
        public bool Differs(SpawnRow s, string field)
        {
            string first = null;
            for (int i = 0; i < Sides.Count; i++)
            {
                if (s.Rows[i] == null) continue;
                string v = Get(s, i, field);
                if (first == null) first = v; else if (v != first) return true;
            }
            return false;
        }

        public bool IsEdited(SpawnRow s, string field)
        {
            for (int i = 0; i < Sides.Count; i++)
                if (s.Rows[i] != null && (s.Rows[i].IsAdded || Sides[i].Table.IsEdited(s.Rows[i], field))) return true;
            return false;
        }

        /// <summary>The copies that lack the row.</summary>
        public List<string> MissingOn(SpawnRow s)
        {
            var l = new List<string>();
            for (int i = 0; i < Sides.Count; i++) if (s.Rows[i] == null) l.Add(Sides[i].Archive);
            return l;
        }

        /// <summary>How many rows of the first copy name each tag.</summary>
        public Dictionary<string, int> TagUse()
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (List<SpawnRow> l in byMap.Values)
                foreach (SpawnRow s in l)
                    foreach (string t in SpawnRules.Tags(Get(s, SpawnRules.Tag)))
                    {
                        int n;
                        d.TryGetValue(t, out n);
                        d[t] = n + 1;
                    }
            return d;
        }

        /// <summary>What is wrong with a row: by itself, twice in a copy, and (when the NPC tags were read) a tag no NPC has.</summary>
        public List<string> Check(SpawnRow s)
        {
            var l = new List<string>();
            if (s.IsDeleted) return l;
            for (int i = 0; i < Sides.Count; i++)
            {
                TableRow r = s.Rows[i];
                if (r == null || r.IsDeleted) continue;
                TableDraft t = Sides[i].Table;
                foreach (string p in SpawnRules.CheckRow(a => t.Get(r, a))) if (!l.Contains(p)) l.Add(p);
                if (!r.IsAdded) continue;
                string key = SpawnRules.Key(a => t.Get(r, a));
                int same = 0;
                foreach (TableRow o in t.Rows) if (!o.IsDeleted && o.Base != null && o.Base.Name == SpawnRules.Row && SpawnRules.Key(a => t.Get(o, a)) == key) same++;
                if (same > 1)
                {
                    string twice = "point " + Get(s, SpawnRules.Point) + " has " + same + " rows for the same difficulty, feature and locale in " + Sides[i].Archive;
                    if (!l.Contains(twice)) l.Add(twice);
                }
            }
            if (Tags != null && Tags.Archive != null)
                foreach (string tag in SpawnRules.Tags(Get(s, SpawnRules.Tag)))
                    if (Tags.Of(tag).Count == 0) l.Add("no NPC of " + Tags.Archive + " has the tag \"" + tag + "\"");
            return l;
        }

        // ---- editing ----

        /// <summary>Sets a field on every copy that has the row (null or "" takes an optional attribute out).</summary>
        public void Set(SpawnRow s, string field, string value)
        {
            if (Array.IndexOf(SpawnRules.Fixed, field) >= 0)
                throw new ModKitException(field + " of a spawn row is not changed; remove the row and add one");
            if (!TableDraft.IsAttrName(field)) throw new ModKitException("\"" + field + "\" is not an attribute name");
            if (s.IsDeleted) throw new ModKitException("the row is removed; restore it first");
            value = (value ?? "").Trim();
            bool optional = field == SpawnRules.PetPopulation || field == SpawnRules.PetProbability;
            for (int i = 0; i < Sides.Count; i++)
                if (s.Rows[i] != null && !s.Rows[i].IsDeleted) Sides[i].Table.Set(s.Rows[i], field, value.Length == 0 && optional ? null : value);
        }

        static string Esc(string v) { return XmlDoc.EscapeAttr(v, '"'); }

        /// <summary>A new row on every copy, after the map's last shipped row there (at the end when the map has none).</summary>
        public SpawnRow Add(long map, long point, string tag, long population, long coolTime, long difficulty, long difficultyMin)
        {
            if (map <= 0) throw new ModKitException("a spawn row needs a map id");
            if (point < 0) throw new ModKitException("a spawn row needs the number of a spawn point of the map");
            string code = SpawnRules.Code(map);
            string xml = "<region " + SpawnRules.Map + "=\"" + code + "\" " + SpawnRules.Point + "=\"" + point.ToString(CultureInfo.InvariantCulture) + "\" "
                + SpawnRules.Difficulty + "=\"" + difficulty.ToString(CultureInfo.InvariantCulture) + "\" " + SpawnRules.DifficultyMin + "=\"" + difficultyMin.ToString(CultureInfo.InvariantCulture) + "\" "
                + SpawnRules.Tag + "=\"" + Esc((tag ?? "").Trim()) + "\" " + SpawnRules.CoolTime + "=\"" + coolTime.ToString(CultureInfo.InvariantCulture) + "\" "
                + SpawnRules.Population + "=\"" + population.ToString(CultureInfo.InvariantCulture) + "\" " + SpawnRules.PetPopulation + "=\"0\" " + SpawnRules.PetProbability + "=\"0\" />";
            string key = SpawnRules.Key(a => a == SpawnRules.Map ? code : a == SpawnRules.Point ? point.ToString(CultureInfo.InvariantCulture)
                : a == SpawnRules.Difficulty ? difficulty.ToString(CultureInfo.InvariantCulture) : a == SpawnRules.DifficultyMin ? difficultyMin.ToString(CultureInfo.InvariantCulture) : null);
            foreach (SpawnRow o in Of(map))
                for (int i = 0; i < Sides.Count; i++)
                {
                    TableRow r = o.Rows[i];
                    TableDraft t = Sides[i].Table;
                    if (r != null && !r.IsDeleted && SpawnRules.Key(a => t.Get(r, a)) == key)
                        throw new ModKitException("map " + map + " already has a row for point " + point + " at difficulty " + difficultyMin + "-" + difficulty + " in " + Sides[i].Archive);
                }
            var made = new SpawnRow(Sides.Count);
            for (int i = 0; i < Sides.Count; i++)
            {
                TableRow last = null;
                foreach (SpawnRow o in Of(map)) if (o.Rows[i] != null && !o.Rows[i].IsAdded) last = o.Rows[i];
                made.Rows[i] = Sides[i].Table.AddRow(xml, last, false);
            }
            List<SpawnRow> l;
            if (!byMap.TryGetValue(map, out l)) byMap[map] = l = new List<SpawnRow>();
            l.Add(made);
            return made;
        }

        /// <summary>A new row for another point of the same map, with the fields of source.</summary>
        public SpawnRow Clone(SpawnRow source, long point)
        {
            if (source.IsDeleted) throw new ModKitException("the row is removed; restore it before copying");
            Func<string, long> num = f => { long n; return long.TryParse(Get(source, f), NumberStyles.None, CultureInfo.InvariantCulture, out n) ? n : 0; };
            SpawnRow made = Add(SpawnRules.MapOf(Get(source, SpawnRules.Map)), point, Get(source, SpawnRules.Tag), num(SpawnRules.Population), num(SpawnRules.CoolTime),
                num(SpawnRules.Difficulty), num(SpawnRules.DifficultyMin));
            foreach (string f in new[] { SpawnRules.PetPopulation, SpawnRules.PetProbability })
                if (Get(source, f).Length > 0) Set(made, f, Get(source, f));
            return made;
        }

        /// <summary>Removes a row: a new one goes away, a shipped one is deleted in every copy and can be restored.</summary>
        public void Remove(SpawnRow s)
        {
            bool added = s.IsAdded;
            for (int i = 0; i < Sides.Count; i++)
            {
                if (s.Rows[i] == null) continue;
                bool mine = s.Rows[i].IsAdded;
                Sides[i].Table.Delete(s.Rows[i]);
                if (mine && !added) s.Rows[i] = null;
            }
            if (!added) return;
            foreach (List<SpawnRow> l in byMap.Values) l.Remove(s);
        }

        /// <summary>Takes back the removal of a shipped row.</summary>
        public void Restore(SpawnRow s)
        {
            for (int i = 0; i < Sides.Count; i++) if (s.Rows[i] != null) Sides[i].Table.Restore(s.Rows[i]);
        }

        /// <summary>Drops what the mod changes in a shipped row.</summary>
        public void Revert(SpawnRow s)
        {
            if (s.IsAdded) throw new ModKitException("the row is new; remove it instead");
            for (int i = 0; i < Sides.Count; i++)
            {
                if (s.Rows[i] == null) continue;
                Sides[i].Table.Restore(s.Rows[i]);
                Sides[i].Table.RevertCells(s.Rows[i]);
            }
        }
    }
}
