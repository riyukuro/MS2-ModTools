// Field rows for the maps our client has but cannot open: the client loads a map only when its table/fielddata.xml
// row has an environment with an xblock name (CFieldData::LoadFieldData). Where that row is empty or missing and the
// source client has the map's own file map/<8-digit id>.xml (an <ms2><environment ...> file, the global client's
// form), this builds the compact row our table has and writes it into a mod.
//
// The compact form is the one the rows of our table have: one <environment> without feature or locale, the elements
// in the source's order, only those the client reads (CFieldData::vfunc_1: xblock, property, drop, split, camera,
// cashCall, indoor; CFieldDataCli::vfunc_1: ui, bgm, hdr, camera, shadow, fov, optimize). An attribute is left out
// when it is empty, when it has the reader's default (the value our rows leave out), or when our rows never carry it;
// an element left with no attribute is left out. The bool attributes the client reads with XmlGetAttr(bool) are
// written true/false, and the two return-map ids lose their leading zeros (xblock@name keeps them).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public enum FieldRowKind
    {
        Fill,       // our row is there with no environment that has an xblock name: the environment goes into it
        Add,        // we have no row: a new row after the row with the next lower id
        Several,    // the source has several environments for our locale: not filled
        NoMap,      // our client has no xblock file of the map: not filled
        NoXBlock,   // the source environment has no xblock name: not filled
        Error,      // the source file does not parse
    }

    public sealed class FieldRow
    {
        public FieldRowKind Kind;
        public int Id;
        public string File;         // the source entry, map/<8-digit id>.xml
        public string XBlock;       // the source environment's xblock name
        public string Feature;      // the source environment's feature ("" for none); not written
        public bool? FeatureOn;     // our client has the feature on (null: no feature, or our feature tables unread)
        public int FeatureLevel;
        public int Environments;    // environments of the source file for our locale
        public string Xml;          // Fill: the <environment>; Add: the whole <fieldData>
        public int Attrs;           // attributes in the compact environment
        public string At;           // Fill: fieldData[id=N]
        public string After;        // Add: fieldData[id=P], the row it goes after (null: before the first row)
        public string Before;       // Add with no lower row: the first row
        public string Error;
        public bool? OnServer;      // the server's pack has the map's file (null: no server pack given)
        /// <summary>A row Put writes: Fill or Add.</summary>
        public bool Ticked { get { return Kind == FieldRowKind.Fill || Kind == FieldRowKind.Add; } }
        /// <summary>Ticked by default: a row Put writes for a map the server has (the maps its ingest marks closed),
        /// except maps behind the Develop feature (the client's dev and test maps).</summary>
        public bool Default { get { return Ticked && OnServer != false && !string.Equals(Feature, DevelopFeature, StringComparison.Ordinal); } }
        public const string DevelopFeature = "Develop";
        public override string ToString() { return Kind + " " + Id + (Feature != "" ? " feature " + Feature : ""); }
    }

    public sealed class FieldRows
    {
        public const string Entry = "table/fielddata.xml";
        public const string MapFolder = "map/";

        // the reader's default per attribute of each element the client reads: an attribute with that value is left out
        static readonly Dictionary<string, Dictionary<string, string>> Defaults = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            { "xblock", D() },
            { "property", D("popupMenu", "1", "gemSystem", "1", "additionalUseDisable", "1", "autoRevivalTime", "0", "bigCity", "0",
                "capacity", "0", "checkClimb", "1", "checkFly", "0", "checkPartyObserve", "0", "cubeSkillIntervalTime", "0",
                "deathPenalty", "1", "doNotRevivalHere", "0", "enterAdminLevel", "0", "enterMaxLevel", "0", "enterMinLevel", "0",
                "enterreturnid", "0", "exploreType", "0", "homeReturnable", "1", "infinityMeratRevival", "1", "limitContent", "0",
                "limitMove", "0", "mapSearch", "1", "mapType", "0", "minimapInvisible", "0", "onlyDarkTomb", "0", "pkMode", "0",
                "revivalreturnid", "0", "webFinder", "0") },
            { "drop", D() },
            { "indoor", D("type", "0") },
            { "optimize", D("fastglow", "1", "fastshimmer", "1") },
            { "cashCall", D("RecallOtherUserProhibit", "0", "cashCallMarketProhibit", "0", "cashCallMedicProhibit", "0",
                "cashTaxiNotDeparture", "0", "cashTaxiNotDestination", "0") },
            { "fov", D("value", "40") },
            { "split", D("globalSplit", "0") },
            { "bgm", D("bossParam", "0", "bossValue", "0", "id", "0", "param", "0", "value", "0") },
            { "hdr", D("bloomscale", "2", "glowscale", "0.1", "middlegray", "0.51", "offset", "30", "threshold", "0.27") },
            { "camera", D("distance", "1100", "hangle", "45", "vangle", "30", "zoomInHeightOffset", "95", "zoomOutHeightOffset", "60") },
            { "shadow", D("intensity", "0.3", "simpleshadow", "0.5") },
            { "ui", D("applyCameraOption", "1", "autoFindTaxiStation", "1", "cameraExtraDistance", "0", "effectLOD", "1", "fallDamage", "1",
                "fieldName", "1", "globalNameTag", "0", "hscroll", "1", "instancingGrid", "0", "useEPSkill", "1", "useEffLight", "0",
                "useOptimalHide", "1", "usePet", "1", "useRidee", "1", "vscroll", "1") },
        };

        // attributes our rows never carry
        static readonly HashSet<string> Never = new HashSet<string>(StringComparer.Ordinal)
        {
            "drop@dropRank", "drop@globalDropBoxID", "property@useTimeEvent", "property@requireQuest", "ui@showHitEffect",
        };

        // read with XmlGetAttr(bool): written true/false
        static readonly HashSet<string> Bools = new HashSet<string>(StringComparer.Ordinal)
        {
            "ui@isNoSkipFrame", "camera@useFileSetting", "property@popupMenu", "property@gemSystem",
        };

        // map ids: written without leading zeros
        static readonly HashSet<string> Ids = new HashSet<string>(StringComparer.Ordinal) { "property@enterreturnid", "property@revivalreturnid" };

        static Dictionary<string, string> D(params string[] kv)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        public readonly string Target, Source, Locale;
        public readonly FeatureSet Features;
        public readonly List<FieldRow> Rows = new List<FieldRow>();
        public int Open;            // source maps whose row of ours is open already
        public int OursOnly;        // our closed rows the source has no file for
        public readonly List<int> NoSource = new List<int>();

        FieldRows(string target, string source, string locale, FeatureSet features)
        {
            Target = target; Source = source; Locale = locale; Features = features;
        }

        /// <summary>The value an attribute has in the compact row; null when it is left out.</summary>
        public static string CompactValue(string element, string attr, string value)
        {
            Dictionary<string, string> d;
            if (!Defaults.TryGetValue(element, out d) || string.IsNullOrEmpty(value)) return null;
            string key = element + "@" + attr, def;
            if (Never.Contains(key)) return null;
            if (Ids.Contains(key) && value.Length > 1 && value[0] == '0')
            {
                bool digits = true;
                foreach (char c in value) if (c < '0' || c > '9') { digits = false; break; }
                if (digits) { value = value.TrimStart('0'); if (value.Length == 0) value = "0"; }
            }
            if (d.TryGetValue(attr, out def) && def == value) return null;
            if (Bools.Contains(key)) value = value == "1" ? "true" : value == "0" ? "false" : value;
            return value;
        }

        /// <summary>
        /// The compact &lt;environment&gt; of a source environment, its child lines indented by one tab under an
        /// unindented first line, "\n" between lines; attrs is the number of attributes it has.
        /// </summary>
        public static string Compact(XmlElem env, out int attrs)
        {
            attrs = 0;
            var sb = new StringBuilder();
            sb.Append("<environment>");
            foreach (XmlElem e in env.Children)
            {
                if (!Defaults.ContainsKey(e.Name)) continue;
                var line = new StringBuilder();
                int n = 0;
                foreach (XmlAttr a in e.Attrs)
                {
                    string v = CompactValue(e.Name, a.Name, a.Value);
                    if (v == null) continue;
                    line.Append(' ').Append(a.Name).Append("=\"").Append(XmlDoc.EscapeAttr(v, '"')).Append('"');
                    n++;
                }
                if (n == 0) continue;
                attrs += n;
                sb.Append("\n\t<").Append(e.Name).Append(line).Append(" />");
            }
            sb.Append("\n</environment>");
            return sb.ToString();
        }

        static string Indent(string fragment) { return fragment.Replace("\n", "\n\t"); }

        /// <summary>The 8-digit file name of a map id.</summary>
        public static string FileOf(int id) { return MapFolder + id.ToString("00000000", CultureInfo.InvariantCulture) + ".xml"; }

        static bool IdOf(string name, out int id)
        {
            id = 0;
            string n = name.Replace('\\', '/');
            if (!n.StartsWith(MapFolder, StringComparison.OrdinalIgnoreCase) || !n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return false;
            string s = n.Substring(MapFolder.Length, n.Length - MapFolder.Length - 4);
            if (s.Length == 0 || s.IndexOf('/') >= 0) return false;
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out id);
        }

        /// <summary>
        /// Scans the data set: our table/fielddata.xml (target, e.g. client:Xml.m2d) against the source archive's
        /// map/*.xml. The xblock files are looked for in &lt;target root&gt;:Resource/Exported.m2d; the feature notes
        /// come from the target's feature tables for locale and server type. server (when the data set has it) is
        /// the server's pack: a map it has no map/*.xml for is not ticked by default.
        /// </summary>
        public static FieldRows Scan(DataSet data, string target, string source, string server = "server:Xml.m2d",
            string locale = "KR", string serverType = FeatureSet.DefaultServerType)
        {
            M2dArchive ta = data.Open(target), sa = data.Open(source);
            var maps = new SortedDictionary<int, Func<byte[]>>();
            foreach (M2dEntry e in sa.Entries)
            {
                int id;
                if (!IdOf(e.Name, out id)) continue;
                M2dEntry k = e;
                maps[id] = delegate { return sa.Read(k); };
            }
            M2dEntry fe = ta.Find(Entry);
            if (fe == null) throw new ModKitException(target + " has no " + Entry);
            byte[] table = ta.Read(fe);
            M2dEntry se = ta.Find(FeatureSet.SettingTable), fte = ta.Find(FeatureSet.FeatureTable);
            FeatureSet features = se != null && fte != null ? FeatureSet.Read(ta.Read(se), ta.Read(fte), locale, serverType) : null;
            string norm = DataSet.NormalizeRef(target);
            int colon = norm.IndexOf(':');
            string mapRef = (colon > 0 ? norm.Substring(0, colon) : norm) + ":" + Planner.MapArchivePath;
            HashSet<string> xblocks = null;
            if (data.Exists(mapRef))
            {
                xblocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (M2dEntry e in data.Open(mapRef).Entries) xblocks.Add(e.Name.Replace('\\', '/'));
            }
            HashSet<int> onServer = null;
            if (!string.IsNullOrEmpty(server) && data.Exists(server))
            {
                onServer = new HashSet<int>();
                foreach (M2dEntry e in data.Open(server).Entries) { int id; if (IdOf(e.Name, out id)) onServer.Add(id); }
            }
            FieldRows fr = Scan(table, maps, xblocks, features, target, source, locale);
            if (onServer != null) foreach (FieldRow r in fr.Rows) r.OnServer = onServer.Contains(r.Id);
            return fr;
        }

        /// <summary>
        /// Scans our table's bytes against the source map files by id (read on demand). xblocks holds the xblock
        /// entries of our client ("xblock/&lt;name&gt;.xblock"); null when unknown, then no row is NoMap.
        /// </summary>
        public static FieldRows Scan(byte[] fielddata, IDictionary<int, Func<byte[]>> maps, ICollection<string> xblocks, FeatureSet features,
            string target, string source, string locale)
        {
            var fr = new FieldRows(target, source, locale, features);
            XmlDoc doc = XmlDoc.Parse(fielddata, target + " " + Entry);
            if (doc.Root == null) throw new ModKitException(Entry + " has no root element");
            // our rows by id, in id order: whether each is open
            var ours = new SortedDictionary<int, bool>();
            foreach (XmlElem row in doc.Root.Children)
            {
                int id;
                if (row.Name != "fieldData" || !int.TryParse(row.Attr("id") ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out id)) continue;
                bool open = false;
                foreach (XmlElem env in row.Children)
                {
                    if (env.Name != "environment") continue;
                    foreach (XmlElem x in env.Children)
                        if (x.Name == "xblock" && !string.IsNullOrEmpty(x.Attr("name"))) open = true;
                }
                bool was;
                ours[id] = (ours.TryGetValue(id, out was) && was) || open;
            }
            var ids = new List<int>(ours.Keys);
            var keys = new List<int>(maps.Keys);
            keys.Sort();
            foreach (int id in keys)
            {
                bool open, have = ours.TryGetValue(id, out open);
                if (have && open) { fr.Open++; continue; }
                var r = new FieldRow { Id = id, File = FileOf(id), Feature = "" };
                fr.Rows.Add(r);
                XmlDoc src;
                try { src = XmlDoc.Parse(maps[id](), source + " " + r.File); }
                catch (Exception ex) { r.Kind = FieldRowKind.Error; r.Error = ex.Message; continue; }
                var envs = new List<XmlElem>();
                if (src.Root != null)
                    foreach (XmlElem e in src.Root.Children)
                    {
                        if (e.Name != "environment") continue;
                        string loc = e.Attr("locale") ?? "";
                        if (loc.Length == 0 || string.Equals(loc, locale, StringComparison.OrdinalIgnoreCase)) envs.Add(e);
                    }
                r.Environments = envs.Count;
                if (envs.Count == 0) { r.Kind = FieldRowKind.Error; r.Error = "no environment for locale " + locale; continue; }
                if (envs.Count > 1) { r.Kind = FieldRowKind.Several; continue; }
                XmlElem env = envs[0];
                r.Feature = env.Attr("feature") ?? "";
                if (r.Feature.Length > 0 && features != null) { r.FeatureOn = features.IsOn(r.Feature); r.FeatureLevel = features.LevelOf(r.Feature); }
                foreach (XmlElem x in env.Children) if (x.Name == "xblock" && !string.IsNullOrEmpty(x.Attr("name"))) { r.XBlock = x.Attr("name"); break; }
                int attrs;
                string compact = Compact(env, out attrs);
                r.Attrs = attrs;
                if (r.XBlock == null) { r.Kind = FieldRowKind.NoXBlock; continue; }
                if (xblocks != null && !xblocks.Contains("xblock/" + r.XBlock + ".xblock")) { r.Kind = FieldRowKind.NoMap; continue; }
                string key = id.ToString(CultureInfo.InvariantCulture);
                if (have)
                {
                    r.Kind = FieldRowKind.Fill;
                    r.At = "fieldData[id=" + key + "]";
                    r.Xml = compact;
                }
                else
                {
                    r.Kind = FieldRowKind.Add;
                    int at = ids.BinarySearch(id);
                    at = at < 0 ? ~at : at;
                    if (at > 0) r.After = "fieldData[id=" + ids[at - 1].ToString(CultureInfo.InvariantCulture) + "]";
                    else if (ids.Count > 0) r.Before = "fieldData[id=" + ids[0].ToString(CultureInfo.InvariantCulture) + "]";
                    r.Xml = "<fieldData id=\"" + key + "\">\n\t" + Indent(compact) + "\n</fieldData>";
                }
            }
            foreach (KeyValuePair<int, bool> o in ours)
                if (!o.Value && !maps.ContainsKey(o.Key)) fr.NoSource.Add(o.Key);
            fr.OursOnly = fr.NoSource.Count;
            return fr;
        }

        static bool SameArchive(string a, string b) { return string.Equals(DataSet.NormalizeRef(a), DataSet.NormalizeRef(b), StringComparison.OrdinalIgnoreCase); }

        EditOp EditOf(Mod mod, bool create)
        {
            foreach (ModOp op in mod.Ops)
            {
                var ed = op as EditOp;
                if (ed != null && SameArchive(ed.Archive, Target) && string.Equals(ed.Entry.Replace('\\', '/'), Entry, StringComparison.OrdinalIgnoreCase)) return ed;
            }
            if (!create) return null;
            var n = new EditOp { Archive = Target, Entry = Entry };
            mod.Ops.Add(n);
            return n;
        }

        static string Norm(string xml) { return (xml ?? "").Replace("\r\n", "\n").Trim(); }

        // the add op the mod has for a row as Put wrote it
        static bool Is(RowOp op, FieldRow r)
        {
            if (op.Kind != RowOpKind.Add || op.Copy != null || op.Attrs.Count > 0 || Norm(op.Xml) != Norm(r.Xml)) return false;
            if (r.Kind == FieldRowKind.Fill) return op.In == r.At && op.After == null && op.Before == null;
            return op.In == null && op.After == r.After && op.Before == r.Before;
        }

        /// <summary>
        /// Writes the rows (Fill and Add only) into the mod as add ops of table/fielddata.xml, sync="false". A row the
        /// mod has already is not written again. Returns the number of rows written.
        /// </summary>
        public int Put(Mod mod, IEnumerable<FieldRow> rows)
        {
            int n = 0;
            foreach (FieldRow r in rows)
            {
                if (!r.Ticked || r.Xml == null) continue;
                EditOp ed = EditOf(mod, false);
                bool there = false;
                if (ed != null) foreach (RowOp op in ed.Rows) if (Is(op, r)) { there = true; break; }
                if (there) continue;
                var add = new RowOp { Kind = RowOpKind.Add, Xml = r.Xml, Sync = false };
                if (r.Kind == FieldRowKind.Fill) add.In = r.At;
                else { add.After = r.After; add.Before = r.Before; }
                EditOf(mod, true).Rows.Add(add);
                n++;
            }
            return n;
        }

        /// <summary>Takes out the ops Put wrote for the rows; an edit left with no row op goes. Returns the number taken out.</summary>
        public int Take(Mod mod, IEnumerable<FieldRow> rows)
        {
            var list = new List<FieldRow>(rows);
            EditOp ed = EditOf(mod, false);
            if (ed == null) return 0;
            int n = ed.Rows.RemoveAll(delegate(RowOp op)
            {
                foreach (FieldRow r in list) if (r.Xml != null && Is(op, r)) return true;
                return false;
            });
            if (ed.Rows.Count == 0) mod.Ops.Remove(ed);
            return n;
        }

        /// <summary>How many of the rows the mod has as Put wrote them.</summary>
        public int InMod(Mod mod, IEnumerable<FieldRow> rows)
        {
            EditOp ed = EditOf(mod, false);
            if (ed == null) return 0;
            int n = 0;
            foreach (FieldRow r in rows)
            {
                if (r.Xml == null) continue;
                foreach (RowOp op in ed.Rows) if (Is(op, r)) { n++; break; }
            }
            return n;
        }

        static string N(int n) { return n.ToString("N0", CultureInfo.InvariantCulture); }

        /// <summary>
        /// What the scan found, as the view and the command line say it: the counts by kind, the rows ticked by
        /// default, our closed rows with no source file (up to 60 ids) and one line per source file that does not
        /// parse. Lines are joined by nl.
        /// </summary>
        public string Summary(string nl)
        {
            var kinds = new SortedDictionary<FieldRowKind, int>();
            int def = 0;
            foreach (FieldRow r in Rows)
            {
                int n;
                kinds.TryGetValue(r.Kind, out n);
                kinds[r.Kind] = n + 1;
                if (r.Default) def++;
            }
            var parts = new List<string>();
            foreach (KeyValuePair<FieldRowKind, int> k in kinds) parts.Add(k.Key + " " + N(k.Value));
            var b = new StringBuilder();
            b.Append("field row scan of ").Append(Target).Append(' ').Append(Entry).Append(" from ").Append(Source).Append(' ').Append(MapFolder).Append("*.xml (").Append(Locale).Append("): ")
             .Append(N(Open)).Append(" maps open already; ").Append(parts.Count == 0 ? "nothing to list" : string.Join(", ", parts.ToArray()))
             .Append("; ").Append(N(def)).Append(" ticked by default (the server has the map, not a Develop map)");
            if (NoSource.Count > 0)
            {
                b.Append(nl).Append(N(NoSource.Count)).Append(" closed rows of ours have no source file: ");
                for (int i = 0; i < NoSource.Count && i < 60; i++) { if (i > 0) b.Append(' '); b.Append(NoSource[i].ToString(CultureInfo.InvariantCulture)); }
                if (NoSource.Count > 60) b.Append(" ... (").Append(N(NoSource.Count - 60)).Append(" more)");
            }
            foreach (FieldRow r in Rows)
                if (r.Kind == FieldRowKind.Error) b.Append(nl).Append("  ").Append(r.File ?? r.Id.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(r.Error);
            return b.ToString();
        }
    }
}
