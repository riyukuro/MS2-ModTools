// Table schemas derived from the shipped data: for every table the element paths, attribute names, the value
// types seen and a few sample values. The patcher uses them to WARN about an attribute or a type the game data
// never has; it does not refuse. A schema file holds sample values of the game data: it is a local cache and
// must not be committed. schema-notes.xml (names only, no values) can add what the data does not show.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    [Flags]
    public enum ValueTypes { None = 0, Empty = 1, Int = 2, Float = 4, List = 8, Text = 16 }

    public sealed class AttrSchema
    {
        public string Name;
        public long Count;
        public ValueTypes Types;
        public bool HasRange;
        public double Min, Max;
        public readonly List<string> Samples = new List<string>();

        public const int MaxSamples = 8;

        internal void Add(string value)
        {
            Count++;
            ValueTypes t = SchemaSet.Classify(value);
            Types |= t;
            if (t == ValueTypes.Int || t == ValueTypes.Float)
            {
                double d = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (!HasRange) { HasRange = true; Min = Max = d; }
                else { if (d < Min) Min = d; if (d > Max) Max = d; }
            }
            if (Samples.Count < MaxSamples && value.Length <= 80 && !Samples.Contains(value)) Samples.Add(value);
        }

        /// <summary>"int 0..1", "int, float -1.5..300", "text" ...</summary>
        public string TypeText
        {
            get
            {
                var parts = new List<string>();
                foreach (ValueTypes t in new[] { ValueTypes.Empty, ValueTypes.Int, ValueTypes.Float, ValueTypes.List, ValueTypes.Text })
                    if ((Types & t) != 0) parts.Add(t.ToString().ToLowerInvariant());
                string s = string.Join(", ", parts.ToArray());
                if (HasRange) s += " " + Min.ToString("R", CultureInfo.InvariantCulture) + ".." + Max.ToString("R", CultureInfo.InvariantCulture);
                return s;
            }
        }
    }

    public sealed class ElemSchema
    {
        public string Path;         // element names from the root element, joined by '/'
        public long Count;
        public bool HasText;
        public readonly List<AttrSchema> Attrs = new List<AttrSchema>();
        internal readonly Dictionary<string, AttrSchema> ByName = new Dictionary<string, AttrSchema>(StringComparer.Ordinal);
        internal readonly Dictionary<string, ElemSchema> Kids = new Dictionary<string, ElemSchema>(StringComparer.Ordinal);

        public AttrSchema Attr(string name)
        {
            AttrSchema a;
            return ByName.TryGetValue(name, out a) ? a : null;
        }

        internal AttrSchema Need(string name)
        {
            AttrSchema a;
            if (!ByName.TryGetValue(name, out a)) { a = new AttrSchema { Name = name }; ByName[name] = a; Attrs.Add(a); }
            return a;
        }
    }

    public sealed class TableSchema
    {
        public string Archive;
        public string Name;         // entry name with its numbers replaced by '#': "npc/#.xml"
        public int Files;
        public string Root = "";    // name of the root element
        public string Row = "";     // name of the most frequent child of the root
        public string Key = "";     // "id" when every row has it
        public string Variants = "";    // of "feature", "locale": the ones rows carry
        public readonly List<ElemSchema> Elements = new List<ElemSchema>();
        internal readonly Dictionary<string, ElemSchema> ByPath = new Dictionary<string, ElemSchema>(StringComparer.Ordinal);

        public ElemSchema Element(string path)
        {
            ElemSchema e;
            return ByPath.TryGetValue(path, out e) ? e : null;
        }

        internal ElemSchema Need(string path)
        {
            ElemSchema e;
            if (!ByPath.TryGetValue(path, out e)) { e = new ElemSchema { Path = path }; ByPath[path] = e; Elements.Add(e); }
            return e;
        }

        internal void Finish()
        {
            ElemSchema best = null, root = null;
            foreach (ElemSchema e in Elements)
            {
                int depth = e.Path.Split('/').Length;
                if (depth == 1 && (root == null || e.Count > root.Count)) root = e;
            }
            if (root == null) return;
            Root = root.Path;
            foreach (ElemSchema e in Elements)
                if (e.Path.StartsWith(Root + "/", StringComparison.Ordinal) && e.Path.IndexOf('/', Root.Length + 1) < 0 && (best == null || e.Count > best.Count)) best = e;
            if (best == null) return;
            Row = best.Path.Substring(Root.Length + 1);
            AttrSchema id = best.Attr("id");
            Key = id != null && id.Count == best.Count ? "id" : "";
            var v = new List<string>();
            foreach (string n in new[] { "feature", "locale" }) if (best.Attr(n) != null) v.Add(n);
            Variants = string.Join(",", v.ToArray());
        }
    }

    public sealed class ArchiveSchema
    {
        public string Archive;
        public long Size, MTime;    // of the .m2d the schema was built from
        public int Entries, Skipped;
        public bool Stale;          // set by Load when the archive on disk is not the one the schema was built from
        public readonly List<TableSchema> Tables = new List<TableSchema>();
        internal readonly Dictionary<string, TableSchema> ByName = new Dictionary<string, TableSchema>(StringComparer.Ordinal);

        public TableSchema Table(string name)
        {
            TableSchema t;
            return ByName.TryGetValue(name, out t) ? t : null;
        }

        internal TableSchema Need(string name)
        {
            TableSchema t;
            if (!ByName.TryGetValue(name, out t)) { t = new TableSchema { Archive = Archive, Name = name }; ByName[name] = t; Tables.Add(t); }
            return t;
        }
    }

    /// <summary>A fact the shipped data does not show: an attribute a loader reads. Names only.</summary>
    public sealed class SchemaNote
    {
        public string Archive;      // "client:Xml.m2d" or "*"
        public string Table, Element, Attr;
        public string Source;       // "client" (the loader in src/ms2/client reads it) or "maple2" (unverified)
        public string Ref = "";     // where: a source path or a class name
        public string Use = "";     // what the reader does with the value, in a few words; may be empty
    }

    public sealed class SchemaSet
    {
        public readonly List<ArchiveSchema> Archives = new List<ArchiveSchema>();
        public readonly List<SchemaNote> Notes = new List<SchemaNote>();

        public ArchiveSchema Archive(string archive)
        {
            foreach (ArchiveSchema a in Archives) if (a.Archive == archive) return a;
            return null;
        }

        // ---- names and types ----

        /// <summary>"npc/21000001.xml" -> "npc/#.xml". A number counts when it stands between '/', '_', '.' or the ends.</summary>
        public static string TableOf(string entry)
        {
            string s = entry.ToLowerInvariant();
            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] >= '0' && s[i] <= '9')
                {
                    int j = i;
                    while (j < s.Length && s[j] >= '0' && s[j] <= '9') j++;
                    bool left = i == 0 || s[i - 1] == '/' || s[i - 1] == '_';
                    bool right = j == s.Length || s[j] == '/' || s[j] == '_' || s[j] == '.';
                    if (left && right) sb.Append('#'); else sb.Append(s, i, j - i);
                    i = j;
                }
                else sb.Append(s[i++]);
            }
            return sb.ToString();
        }

        static bool IsInt(string v, int from, int to)
        {
            if (from < to && v[from] == '-') from++;
            if (to - from < 1 || to - from > 18) return false;
            for (int i = from; i < to; i++) if (v[i] < '0' || v[i] > '9') return false;
            return true;
        }

        static bool IsFloat(string v)
        {
            foreach (char c in v)
                if (!((c >= '0' && c <= '9') || c == '.' || c == '-' || c == '+' || c == 'e' || c == 'E')) return false;
            double d;
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }

        public static ValueTypes Classify(string v)
        {
            if (v.Length == 0) return ValueTypes.Empty;
            if (IsInt(v, 0, v.Length)) return ValueTypes.Int;
            if (IsFloat(v)) return ValueTypes.Float;
            if (v.IndexOf(',') >= 0)
            {
                foreach (string part in v.Split(','))
                {
                    string p = part.Trim();
                    if (p.Length > 0 && !IsFloat(p)) return ValueTypes.Text;
                }
                return ValueTypes.List;
            }
            return ValueTypes.Text;
        }

        // ---- build ----

        /// <summary>Reads every .xml entry of an archive and derives its tables. Replaces an older schema of that archive.</summary>
        public ArchiveSchema Build(DataSet data, string archive, Action<ProgressInfo> progress)
        {
            archive = DataSet.NormalizeRef(archive);
            M2dArchive arch = data.Open(archive);
            var fi = new FileInfo(arch.Path);
            var sch = new ArchiveSchema { Archive = archive, Size = fi.Length, MTime = fi.LastWriteTimeUtc.Ticks };
            int n = 0;
            foreach (M2dEntry e in arch.Entries)
            {
                ProgressUtil.Report(progress, "schema", e.Name, ++n, arch.Count);
                if (!e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                XmlDoc doc;
                try { doc = XmlDoc.Parse(arch.Read(e), e.Name); }
                catch (ModKitException) { sch.Skipped++; continue; }
                if (doc.Root == null) { sch.Skipped++; continue; }
                sch.Entries++;
                TableSchema t = sch.Need(TableOf(e.Name));
                t.Files++;
                ElemSchema root;
                // the root of a table is looked up by path; children through the parent's own map
                root = t.Need(doc.Root.Name);
                Walk(t, root, doc.Root);
            }
            foreach (TableSchema t in sch.Tables) t.Finish();
            ArchiveSchema old = Archive(archive);
            if (old != null) Archives.Remove(old);
            Archives.Add(sch);
            return sch;
        }

        /// <summary>
        /// The schema of one table of an archive (every entry whose TableOf is the name), scanned once and kept
        /// for as long as the data set; null when the archive has no such table.
        /// </summary>
        public static TableSchema ScanTable(DataSet data, string archive, string table)
        {
            archive = DataSet.NormalizeRef(archive);
            string key = "tableschema\n" + archive + "\n" + table;
            object o;
            if (data.Cache.TryGetValue(key, out o)) return (TableSchema)o;
            TableSchema t = null;
            M2dArchive arch = data.Open(archive);
            foreach (M2dEntry e in arch.Entries)
            {
                if (!e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || TableOf(e.Name) != table) continue;
                XmlDoc doc;
                try { doc = XmlDoc.Parse(arch.Read(e), e.Name); }
                catch (ModKitException) { continue; }
                if (doc.Root == null) continue;
                if (t == null) t = new TableSchema { Archive = archive, Name = table };
                t.Files++;
                Walk(t, t.Need(doc.Root.Name), doc.Root);
            }
            if (t != null) t.Finish();
            data.Cache[key] = t;
            return t;
        }

        static void Walk(TableSchema t, ElemSchema s, XmlElem e)
        {
            s.Count++;
            foreach (XmlAttr a in e.Attrs) s.Need(a.Name).Add(a.Value);
            if (e.Children.Count == 0)
            {
                if (!s.HasText && !e.SelfClosing) { string x = e.InnerText; if (x != null && x.Trim().Length > 0) s.HasText = true; }
                return;
            }
            foreach (XmlElem c in e.Children)
            {
                ElemSchema k;
                if (!s.Kids.TryGetValue(c.Name, out k)) { k = t.Need(s.Path + "/" + c.Name); s.Kids[c.Name] = k; }
                Walk(t, k, c);
            }
        }

        // ---- cache files ----

        public static string FileNameOf(string archive)
        {
            var sb = new StringBuilder();
            foreach (char c in archive) sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_');
            return sb.Append(".schema.xml").ToString();
        }

        static void A(StringBuilder sb, string name, string value)
        {
            sb.Append(' ').Append(name).Append("=\"").Append(XmlDoc.EscapeAttr(value, '"')).Append('"');
        }

        public static string ToXml(ArchiveSchema s)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
            sb.Append("<!-- Derived from game data, holds sample values: local cache, do not commit or share. -->\n");
            sb.Append("<schema"); A(sb, "archive", s.Archive); A(sb, "size", s.Size.ToString(inv)); A(sb, "mtime", s.MTime.ToString(inv));
            A(sb, "entries", s.Entries.ToString(inv)); A(sb, "skipped", s.Skipped.ToString(inv)); sb.Append(">\n");
            foreach (TableSchema t in s.Tables)
            {
                sb.Append(" <table"); A(sb, "name", t.Name); A(sb, "files", t.Files.ToString(inv)); A(sb, "root", t.Root); A(sb, "row", t.Row);
                A(sb, "key", t.Key); A(sb, "variants", t.Variants); sb.Append(">\n");
                foreach (ElemSchema e in t.Elements)
                {
                    sb.Append("  <element"); A(sb, "path", e.Path); A(sb, "count", e.Count.ToString(inv)); if (e.HasText) A(sb, "text", "1");
                    if (e.Attrs.Count == 0) { sb.Append("/>\n"); continue; }
                    sb.Append(">\n");
                    foreach (AttrSchema a in e.Attrs)
                    {
                        sb.Append("   <attr"); A(sb, "name", a.Name); A(sb, "count", a.Count.ToString(inv)); A(sb, "types", ((int)a.Types).ToString(inv));
                        if (a.HasRange) { A(sb, "min", a.Min.ToString("R", inv)); A(sb, "max", a.Max.ToString("R", inv)); }
                        if (a.Samples.Count == 0) { sb.Append("/>\n"); continue; }
                        sb.Append('>');
                        foreach (string v in a.Samples) { sb.Append("<s"); A(sb, "v", v); sb.Append("/>"); }
                        sb.Append("</attr>\n");
                    }
                    sb.Append("  </element>\n");
                }
                sb.Append(" </table>\n");
            }
            return sb.Append("</schema>\n").ToString();
        }

        public static ArchiveSchema FromXml(byte[] bytes, string label)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            XmlDoc doc = XmlDoc.Parse(bytes, label);
            XmlElem r = doc.Root;
            if (r == null || r.Name != "schema") throw new ModKitException(label + ": not a schema file");
            try
            {
                var s = new ArchiveSchema
                {
                    Archive = r.Attr("archive"), Size = long.Parse(r.Attr("size"), inv), MTime = long.Parse(r.Attr("mtime"), inv),
                    Entries = int.Parse(r.Attr("entries"), inv), Skipped = int.Parse(r.Attr("skipped") ?? "0", inv),
                };
                foreach (XmlElem te in r.Children)
                {
                    TableSchema t = s.Need(te.Attr("name"));
                    t.Files = int.Parse(te.Attr("files"), inv);
                    t.Root = te.Attr("root") ?? ""; t.Row = te.Attr("row") ?? ""; t.Key = te.Attr("key") ?? ""; t.Variants = te.Attr("variants") ?? "";
                    foreach (XmlElem ee in te.Children)
                    {
                        ElemSchema e = t.Need(ee.Attr("path"));
                        e.Count = long.Parse(ee.Attr("count"), inv);
                        e.HasText = ee.Attr("text") == "1";
                        foreach (XmlElem ae in ee.Children)
                        {
                            AttrSchema a = e.Need(ae.Attr("name"));
                            a.Count = long.Parse(ae.Attr("count"), inv);
                            a.Types = (ValueTypes)int.Parse(ae.Attr("types"), inv);
                            if (ae.Attr("min") != null)
                            {
                                a.HasRange = true;
                                a.Min = double.Parse(ae.Attr("min"), NumberStyles.Float, inv);
                                a.Max = double.Parse(ae.Attr("max"), NumberStyles.Float, inv);
                            }
                            foreach (XmlElem se in ae.Children) a.Samples.Add(se.Attr("v") ?? "");
                        }
                    }
                }
                return s;
            }
            catch (FormatException x) { throw new ModKitException(label + ": damaged schema file (" + x.Message + ")", x); }
            catch (ArgumentNullException x) { throw new ModKitException(label + ": damaged schema file (an attribute is missing)", x); }
        }

        /// <summary>Writes the schema of every archive into the folder (one file each).</summary>
        public void Save(string dir)
        {
            Directory.CreateDirectory(dir);
            foreach (ArchiveSchema s in Archives)
                File.WriteAllBytes(Path.Combine(dir, FileNameOf(s.Archive)), new UTF8Encoding(false).GetBytes(ToXml(s)));
        }

        /// <summary>Loads the cached schemas of a folder. With a data set, a schema whose archive has changed on disk is marked Stale.</summary>
        public static SchemaSet Load(string dir, DataSet data)
        {
            var set = new SchemaSet();
            if (!Directory.Exists(dir)) return set;
            string[] files = Directory.GetFiles(dir, "*.schema.xml");
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string f in files)
            {
                ArchiveSchema s = FromXml(File.ReadAllBytes(f), f);
                if (data != null && data.Exists(s.Archive))
                {
                    var fi = new FileInfo(data.Resolve(s.Archive));
                    s.Stale = fi.Length != s.Size || fi.LastWriteTimeUtc.Ticks != s.MTime;
                }
                set.Archives.Add(s);
            }
            return set;
        }

        /// <summary>
        /// The schemas a plan is checked against: the cache of a folder plus a notes file. The command line and the
        /// Toolbench views both come through here. A notes file that is null or not there adds nothing.
        /// </summary>
        public static SchemaSet Open(string dir, DataSet data, string notesFile)
        {
            SchemaSet s = Load(dir, data);
            if (!string.IsNullOrEmpty(notesFile) && File.Exists(notesFile)) s.LoadNotes(notesFile);
            return s;
        }

        // ---- notes ----

        /// <summary>Reads a notes file: &lt;notes&gt;&lt;attr archive table element name source ref/&gt;...&lt;/notes&gt;.</summary>
        public void LoadNotes(string path)
        {
            XmlDoc doc = XmlDoc.Parse(File.ReadAllBytes(path), path);
            if (doc.Root == null || doc.Root.Name != "notes") throw new ModKitException(path + ": the root element must be <notes>");
            foreach (XmlElem e in doc.Root.Children)
            {
                if (e.Name != "attr") throw new ModKitException(path + " line " + e.Line + ": unknown element <" + e.Name + ">");
                var n = new SchemaNote
                {
                    Archive = e.Attr("archive") ?? "*", Table = e.Attr("table"), Element = e.Attr("element"), Attr = e.Attr("name"),
                    Source = e.Attr("source"), Ref = e.Attr("ref") ?? "", Use = e.Attr("use") ?? "",
                };
                if (n.Table == null || n.Element == null || n.Attr == null) throw new ModKitException(path + " line " + e.Line + ": <attr> needs table, element and name");
                if (n.Source != "client" && n.Source != "maple2") throw new ModKitException(path + " line " + e.Line + ": source must be \"client\" or \"maple2\"");
                if (n.Archive != "*") n.Archive = DataSet.NormalizeRef(n.Archive);
                Notes.Add(n);
            }
        }

        SchemaNote FindNote(string archive, string table, string element, string attr)
        {
            SchemaNote found = null;
            foreach (SchemaNote n in Notes)
                if ((n.Archive == "*" || n.Archive == archive) && n.Table == table && n.Element == element && n.Attr == attr)
                    if (found == null || n.Source == "client") found = n;       // the client loader outranks Maple2
            return found;
        }

        // ---- check of a plan ----

        static bool Fits(ValueTypes seen, ValueTypes t)
        {
            if ((seen & t) != 0) return true;
            if (t == ValueTypes.Empty) return false;
            if ((seen & ValueTypes.Text) != 0) return true;
            if (t == ValueTypes.Int && (seen & ValueTypes.Float) != 0) return true;
            if ((t == ValueTypes.Int || t == ValueTypes.Float) && (seen & ValueTypes.List) != 0) return true;
            return false;
        }

        internal void Check(Plan plan)
        {
            var noSchema = new Dictionary<string, bool>();
            foreach (SchemaProbe p in plan.Probes)
            {
                ArchiveSchema a = Archive(p.Archive);
                if (a == null)
                {
                    if (!noSchema.ContainsKey(p.Archive))
                        plan.Msg(plan.Notes, null, p.Archive, null, "no schema is cached for this archive, new values are not checked (modkit schema build)");
                    noSchema[p.Archive] = true;
                    continue;
                }
                if (a.Stale && !noSchema.ContainsKey(p.Archive))
                {
                    plan.Msg(plan.Notes, null, p.Archive, null, "the cached schema was built from another version of this archive (modkit schema build)");
                    noSchema[p.Archive] = false;
                }
                string tn = TableOf(p.Entry);
                TableSchema t = a.Table(tn);
                if (t == null) continue;        // an entry the original data does not have: nothing to compare with
                ElemSchema e = t.Element(p.ElementPath);
                if (e == null)
                {
                    plan.Msg(plan.Warnings, p.Mod, p.Archive, p.Entry, "the shipped " + tn + " has no element " + p.ElementPath);
                    continue;
                }
                if (p.Attr == null) continue;
                AttrSchema at = e.Attr(p.Attr);
                if (at == null)
                {
                    SchemaNote note = FindNote(p.Archive, tn, p.ElementPath, p.Attr);
                    if (note != null && note.Source == "client")
                        plan.Msg(plan.Notes, p.Mod, p.Archive, p.Entry, "attribute " + p.Attr + " of " + p.ElementPath + " is not in the shipped " + tn + "; the client loader reads it (" + note.Ref + ")" + (note.Use == "" ? "" : ": " + note.Use));
                    else if (note != null)
                        plan.Msg(plan.Warnings, p.Mod, p.Archive, p.Entry, "attribute " + p.Attr + " of " + p.ElementPath + " is not in the shipped " + tn + "; known from Maple2 only, unverified (" + note.Ref + ")");
                    else
                        plan.Msg(plan.Warnings, p.Mod, p.Archive, p.Entry, "attribute " + p.Attr + " is never set on " + p.ElementPath + " in the shipped " + tn);
                    continue;
                }
                ValueTypes vt = Classify(p.Value);
                if (Fits(at.Types, vt)) continue;
                string ex = at.Samples.Count == 0 ? "" : ", for example \"" + at.Samples[0] + "\"";
                plan.Msg(plan.Warnings, p.Mod, p.Archive, p.Entry, p.ElementPath + " @" + p.Attr + "=\"" + p.Value + "\" is " + vt.ToString().ToLowerInvariant()
                    + "; the shipped " + tn + " only has " + at.TypeText + ex);
            }
        }
    }
}
