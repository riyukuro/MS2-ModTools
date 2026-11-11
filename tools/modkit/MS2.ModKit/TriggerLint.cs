// The lint of the trigger scripts: a port of tools/trigger/lint.py and the ingest normalisation it runs first
// (tools/trigger/normalize.py), with the same findings (level, code, line, message) for the same script.
// The schema (tools/trigger/schema.json) and the allowlist of known shipped problems (tools/trigger/known-issues.json)
// are the committed data files, embedded as they are.
//
// A shipped script (raw form: CamelCase or Korean names, `!Name`, <import>) is normalised the way the ingest does
// it (snake_case names, renamed arguments, merged imports), then checked: unknown action or condition, unknown,
// bad or missing argument, a transition to no state, an unreachable state, a group condition without <group>,
// what the server ignores. With TriggerLintOptions.Objects (lint.py --xblock) the object ids are checked against the
// map's xblock (TriggerObjects); with Strict (lint.py --strict) what Maple2 does not run correctly yet is an error.
// Levels: error, warning, allowed (a finding the allowlist names; it does not count).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MS2.ModKit.MapEdit;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public sealed class TriggerFinding
    {
        /// <summary>The script (trigger/&lt;xblock&gt;/&lt;file&gt;.xml) and the line; 0 for the whole file.</summary>
        public string Entry;
        public int Line;
        /// <summary>The element's name the finding is about (several elements can share a line); null when none.</summary>
        public string Tag;
        /// <summary>error, warning or allowed.</summary>
        public string Level;
        public string Code, Message;
        /// <summary>The allowlist's reason when the level is allowed.</summary>
        public string AllowReason;
        /// <summary>The imported script the line is in, when it is not this one; else null.</summary>
        public string ImportedFrom;

        public bool IsError { get { return Level == "error"; } }

        public override string ToString()
        {
            return Entry + ":" + Line.ToString(CultureInfo.InvariantCulture) + ": " + Level + ": [" + Code + "] " + Message;
        }
    }

    /// <summary>
    /// The scripts an &lt;import&gt; is resolved against, in archive order (the order the ingest searches), and the
    /// scripts that import each one.
    /// </summary>
    public sealed class TriggerPacks
    {
        readonly List<string> names;
        readonly HashSet<string> exact;
        readonly Func<string, byte[]> read;
        // an overlay: the packs it is made from, and the entries it changes (null bytes: deleted)
        readonly TriggerPacks under;
        readonly Dictionary<string, byte[]> changed;
        volatile Dictionary<string, List<string>> importers;

        public TriggerPacks(IEnumerable<string> names, Func<string, byte[]> read)
        {
            this.names = new List<string>(names);
            exact = new HashSet<string>(this.names, StringComparer.Ordinal);
            this.read = read;
        }

        public IList<string> Names { get { return names; } }

        /// <summary>
        /// The trigger entries of an archive as shipped, kept in the data set (a mod does not change the shipped
        /// archive), so the importers are found once per archive.
        /// </summary>
        public static TriggerPacks Shipped(DataSet data, string archive)
        {
            string key = "trigger-packs\n" + archive;
            object o;
            lock (data.Cache)
                if (data.Cache.TryGetValue(key, out o)) return (TriggerPacks)o;
            TriggerPacks p = OfArchive(data, archive);
            lock (data.Cache)
            {
                if (data.Cache.TryGetValue(key, out o)) return (TriggerPacks)o;
                data.Cache[key] = p;
            }
            return p;
        }

        /// <summary>
        /// These packs with the entries a plan changes: bytes for an entry put or added (a new one goes after the
        /// others, as the archive writer appends it), null for one deleted.
        /// </summary>
        public TriggerPacks Overlay(IDictionary<string, byte[]> changes)
        {
            if (changes == null || changes.Count == 0) return this;
            var ch = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, byte[]> c in changes) ch[c.Key] = c.Value;
            var l = new List<string>();
            foreach (string n in names) { byte[] b; if (!ch.TryGetValue(n, out b) || b != null) l.Add(n); }
            var news = new List<string>();
            foreach (KeyValuePair<string, byte[]> c in ch) if (c.Value != null && !exact.Contains(c.Key)) news.Add(c.Key);
            news.Sort(StringComparer.Ordinal);
            l.AddRange(news);
            return new TriggerPacks(l, delegate(string n) { byte[] b; return ch.TryGetValue(n, out b) ? b : Read(n); }, this, ch);
        }

        TriggerPacks(List<string> names, Func<string, byte[]> read, TriggerPacks under, Dictionary<string, byte[]> changed)
            : this(names, read)
        {
            this.under = under;
            this.changed = changed;
        }

        /// <summary>The importers are known (BuildImporters or an ImportersOf has run).</summary>
        public bool HasImporters { get { return importers != null; } }

        /// <summary>Reads every script once for its &lt;import&gt;s (slow on a whole archive: run it off the UI thread).</summary>
        public void BuildImporters()
        {
            if (importers != null) return;
            Dictionary<string, List<string>> tg;
            if (under == null) tg = ScanImports(names);
            else
            {
                // the shipped importers, without what the changed entries imported there, and those entries read again
                under.BuildImporters();
                tg = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, List<string>> kv in under.importers)
                {
                    if (!exact.Contains(kv.Key)) continue;
                    var l = new List<string>();
                    foreach (string n in kv.Value) if (!changed.ContainsKey(n)) l.Add(n);
                    if (l.Count > 0) tg[kv.Key] = l;
                }
                var again = new List<string>();
                foreach (string n in names) if (changed.ContainsKey(n)) again.Add(n);
                // in the order of the packs, as a scan of all of them has it
                var at = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < names.Count; i++) at[names[i]] = i;
                foreach (KeyValuePair<string, List<string>> kv in ScanImports(again))
                {
                    List<string> l;
                    if (!tg.TryGetValue(kv.Key, out l)) tg[kv.Key] = l = new List<string>();
                    l.AddRange(kv.Value);
                    l.Sort(delegate(string x, string y) { return at[x].CompareTo(at[y]); });
                }
            }
            importers = tg;
        }

        Dictionary<string, List<string>> ScanImports(IEnumerable<string> which)
        {
            var tg = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string n in which)
            {
                byte[] b = Read(n);
                if (b == null) continue;
                string text = Encoding.UTF8.GetString(b);
                if (text.IndexOf("<import", StringComparison.Ordinal) < 0) continue;
                text = CommentRe.Replace(text, "");
                foreach (Match m in ImportRe.Matches(text))
                {
                    string hit = Resolve(TriggerNormalizer.ImportPackPath(m.Groups[1].Value));
                    if (hit == null) continue;
                    List<string> l;
                    if (!tg.TryGetValue(hit, out l)) tg[hit] = l = new List<string>();
                    l.Add(n);
                }
            }
            return tg;
        }

        /// <summary>The entries trigger/... of an archive.</summary>
        public static TriggerPacks OfArchive(DataSet data, string archive)
        {
            M2d.M2dArchive a = data.Open(archive);
            var l = new List<string>();
            foreach (M2d.M2dEntry e in a.Entries) if (e.Name.StartsWith(Triggers.Folder, StringComparison.Ordinal)) l.Add(e.Name);
            return new TriggerPacks(l, delegate(string n) { M2d.M2dEntry e = a.Find(n); return e == null ? null : a.Read(e); });
        }

        /// <summary>
        /// The files trigger/&lt;folder&gt;/&lt;file&gt; under a folder, in the order of its pack-order.txt (one entry name a
        /// line) when it has one, else sorted.
        /// </summary>
        public static TriggerPacks OfFolder(string root)
        {
            var l = new List<string>();
            string order = Path.Combine(root, "pack-order.txt");
            string tdir = Path.Combine(root, "trigger");
            if (File.Exists(order))
            {
                foreach (string line in File.ReadAllLines(order, Encoding.UTF8))
                    if (line.Trim().StartsWith(Triggers.Folder, StringComparison.Ordinal)) l.Add(line.Trim());
            }
            else if (Directory.Exists(tdir))
            {
                var folders = new List<string>();
                foreach (string d in Directory.GetDirectories(tdir)) folders.Add(Path.GetFileName(d));
                folders.Sort(StringComparer.Ordinal);
                foreach (string f in folders)
                {
                    var files = new List<string>();
                    foreach (string p in Directory.GetFiles(Path.Combine(tdir, f))) files.Add(Path.GetFileName(p));
                    files.Sort(StringComparer.Ordinal);
                    foreach (string n in files) l.Add(Triggers.Folder + f + "/" + n);
                }
            }
            return new TriggerPacks(l, delegate(string n)
            {
                string p = Path.Combine(root, n.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(p) ? File.ReadAllBytes(p) : null;
            });
        }

        public byte[] Read(string name)
        {
            try { return read(name); }
            catch (IOException) { return null; }
            catch (ModKitException) { return null; }
            catch (InvalidDataException) { return null; }
        }

        /// <summary>The ingest's lookup of an imported pack path: exact, else the first entry ending in /&lt;file&gt; or equal, ignoring case.</summary>
        public string Resolve(string packPath)
        {
            if (packPath == null) return null;
            if (exact.Contains(packPath)) return packPath;
            string b = "/" + packPath.Substring(packPath.LastIndexOf('/') + 1).ToLowerInvariant();
            string pl = packPath.ToLowerInvariant();
            foreach (string n in names)
            {
                string nl = n.ToLowerInvariant();
                if (nl.EndsWith(b, StringComparison.Ordinal) || nl == pl) return n;
            }
            return null;
        }

        static readonly Regex CommentRe = new Regex("<!--.*?-->", RegexOptions.Singleline);
        static readonly Regex ImportRe = new Regex("<import\\b[^>]*\\bpath=\"([^\"]*)\"");

        /// <summary>The scripts whose &lt;import&gt; names the entry (comments left out).</summary>
        public List<string> ImportersOf(string name)
        {
            BuildImporters();
            List<string> r;
            return importers.TryGetValue(name, out r) ? r : new List<string>();
        }
    }

    /// <summary>An element of a script as the lint sees it: attributes in order, the source facts beside them.</summary>
    internal sealed class LintEl
    {
        public string Tag;
        public LintEl Parent;
        public int Line;
        public readonly List<string> Keys = new List<string>();
        public readonly List<string> Vals = new List<string>();
        public readonly List<LintEl> Kids = new List<LintEl>();
        /// <summary>The name or state before normalisation, "new=old;.." of the renamed arguments, the imported file.</summary>
        public string RawName, RawState, RawArgs, Src, File, Splitter;
        public bool IngestFail;

        public string Get(string k)
        {
            int i = Keys.IndexOf(k);
            return i < 0 ? null : Vals[i];
        }

        public void Set(string k, string v)
        {
            int i = Keys.IndexOf(k);
            if (i < 0) { Keys.Add(k); Vals.Add(v); } else Vals[i] = v;
        }

        public string Pop(string k)
        {
            int i = Keys.IndexOf(k);
            if (i < 0) return null;
            string v = Vals[i];
            Keys.RemoveAt(i); Vals.RemoveAt(i);
            return v;
        }

        /// <summary>This element and all below it, in document order (ElementTree iter).</summary>
        public List<LintEl> Iter(string tag)
        {
            var l = new List<LintEl>();
            Walk(this, tag, l);
            return l;
        }

        static void Walk(LintEl e, string tag, List<LintEl> l)
        {
            if (tag == null || e.Tag == tag) l.Add(e);
            foreach (LintEl c in e.Kids) Walk(c, tag, l);
        }

        /// <summary>The first element below this one with the tag (ElementTree find(".//tag")).</summary>
        public LintEl FindDeep(string tag)
        {
            foreach (LintEl c in Kids)
            {
                if (c.Tag == tag) return c;
                LintEl d = c.FindDeep(tag);
                if (d != null) return d;
            }
            return null;
        }

        public LintEl Child(string tag)
        {
            foreach (LintEl c in Kids) if (c.Tag == tag) return c;
            return null;
        }

        public string NameBefore { get { return RawName ?? Get("name"); } }

        public static LintEl From(XmlElem e, LintEl parent)
        {
            var n = new LintEl { Tag = e.Name, Parent = parent, Line = e.Line };
            foreach (XmlAttr a in e.Attrs) n.Set(a.Name, a.Value);
            foreach (XmlElem c in e.Children) n.Kids.Add(From(c, n));
            return n;
        }
    }

    /// <summary>The schema: actions, conditions and enums of the server, the ingest's renames.</summary>
    public sealed class TriggerSchema
    {
        internal JsonObj Actions, Conditions, Enums, ActionOverrides, ConditionOverrides, TrAction, TrCond;
        internal readonly HashSet<string> Features = new HashSet<string>(StringComparer.Ordinal);

        public static TriggerSchema Parse(string json)
        {
            var root = Json.Parse(json, "trigger schema") as JsonObj;
            if (root == null) throw new ModKitException("trigger schema: not an object");
            var s = new TriggerSchema();
            s.Actions = O(root.Get("actions"));
            s.Conditions = O(root.Get("conditions"));
            s.Enums = O(root.Get("enums")) ?? new JsonObj();
            JsonObj ing = O(root.Get("ingest")) ?? new JsonObj();
            JsonObj ov = O(ing.Get("overrides")) ?? new JsonObj();
            s.ActionOverrides = O(ov.Get("action")) ?? new JsonObj();
            s.ConditionOverrides = O(ov.Get("condition")) ?? new JsonObj();
            JsonObj tr = O(ing.Get("translate")) ?? new JsonObj();
            s.TrAction = O(tr.Get("ActionLookup")) ?? new JsonObj();
            s.TrCond = O(tr.Get("ConditionLookup")) ?? new JsonObj();
            var fe = ing.Get("features_enabled") as List<object>;
            if (fe != null) foreach (object f in fe) if (f is string) s.Features.Add((string)f);
            if (s.Actions == null || s.Conditions == null) throw new ModKitException("trigger schema: no actions or conditions");
            return s;
        }

        internal static JsonObj O(object o) { return o as JsonObj; }
        internal static string S(JsonObj o, string k) { return o == null ? null : o.Get(k) as string; }
        internal static bool True(JsonObj o, string k)
        {
            object v = o == null ? null : o.Get(k);
            if (v == null) return false;
            if (v is bool) return (bool)v;
            if (v is string) return ((string)v).Length > 0;
            if (v is decimal) return (decimal)v != 0;
            if (v is JsonObj) return ((JsonObj)v).Keys.Count > 0;
            var l = v as List<object>;
            return l == null || l.Count > 0;
        }

        internal static List<JsonObj> Args(JsonObj entry)
        {
            var l = new List<JsonObj>();
            var a = entry.Get("args") as List<object>;
            if (a != null) foreach (object o in a) if (o is JsonObj) l.Add((JsonObj)o);
            return l;
        }
    }

    /// <summary>The ingest's renames (TriggerMapper), as normalize.py repeats them.</summary>
    public sealed class TriggerNormalizer
    {
        readonly TriggerSchema schema;

        internal TriggerNormalizer(TriggerSchema schema) { this.schema = schema; }

        static readonly string[,] SnakeFix =
        {
            { "NPC", "Npc" }, { "NPc", "Npc" }, { "PVP", "Pvp" }, { "ID", "Id" }, { "PC", "Pc" }, { "UI", "Ui" },
            { "Setpc", "SetPc" }, { "UnSet", "Unset" }, { "Emotionloop", "EmotionLoop" },
        };

        /// <summary>TriggerTranslate.ToSnakeCase; null for an empty name (the C# code throws).</summary>
        public static string ToSnakeCase(string text)
        {
            text = text.Replace(" ", "");
            for (int i = 0; i < SnakeFix.GetLength(0); i++) text = text.Replace(SnakeFix[i, 0], SnakeFix[i, 1]);
            if (text.Length == 0) return null;
            var sb = new StringBuilder();
            sb.Append(char.ToLowerInvariant(text[0]));
            for (int i = 1; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsUpper(c)) sb.Append('_').Append(char.ToLowerInvariant(c));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static readonly string[,] SubStart =
        {
            { "1st", "First" }, { "2nd", "Second" }, { "3rd", "Third" }, { "4th", "Fourth" }, { "5th", "Fifth" },
            { "6th", "Sixth" }, { "7th", "Seventh" },
        };

        /// <summary>TriggerMapper.FixClassName (state@name, transition@state).</summary>
        public static string FixClassName(string name)
        {
            if (name == null) return null;
            if (name.Trim().Length == 0) return "State";
            switch (name)
            {
                case "None": return "StateNone";
                case "True": return "StateTrue";
                case "False": return "StateFalse";
                case "del": return "StateDelete";
            }
            name = name.Replace("-", "To").Replace(" ", "_").Replace(".", "_");
            for (int i = 0; i < SubStart.GetLength(0); i++)
                if (name.StartsWith(SubStart[i, 0], StringComparison.Ordinal)) { name = name.Replace(SubStart[i, 0], SubStart[i, 1]); break; }
            var prefix = new StringBuilder();
            while (name.Length > 0 && !char.IsLetter(name[0]))
            {
                if (name[0] != '_') prefix.Append(name[0]);
                name = name.Substring(1);
            }
            if (prefix.Length == 0) return name;
            if (name.Length == 0) return "State" + prefix;
            return !char.IsLetter(name[name.Length - 1]) ? name + "_" + prefix : name + prefix;
        }

        /// <summary>TriggerMapper.Translate; null when the name is empty.</summary>
        static string Translate(string name, JsonObj table)
        {
            var sb = new StringBuilder();
            foreach (string p in name.Split('_', ' '))
            {
                string t = table.Get(p) as string;
                sb.Append(t ?? p);
            }
            return ToSnakeCase(sb.ToString());
        }

        /// <summary>TriggerMapper.ExtractImportPaths for one path attribute; null if blank.</summary>
        public static string ImportPackPath(string raw)
        {
            if (raw == null || raw.Trim().Length == 0) return null;
            string path = raw.Replace('\\', '/');
            if (path.StartsWith("./", StringComparison.Ordinal)) path = path.Substring(2);
            string tp = path.Replace("Data/Xml/Trigger/", "trigger/");
            if (!tp.StartsWith("trigger/", StringComparison.Ordinal) && tp.IndexOf('/') < 0) tp = "trigger/" + tp;
            return tp;
        }

        internal struct Issue { public string Level; public int Line; public string Message; }

        internal void MergeImports(LintEl doc, TriggerPacks packs, List<Issue> issues)
        {
            var names = new List<string>();
            var states = new Dictionary<string, LintEl>(StringComparer.Ordinal);
            foreach (LintEl im in doc.Iter("import"))
            {
                string pp = ImportPackPath(im.Get("path"));
                if (pp == null || packs == null) continue;
                string name = packs.Resolve(pp);
                if (name == null)
                {
                    issues.Add(new Issue { Level = "error", Line = im.Line, Message = "import " + Py.Repr(im.Get("path")) + " does not resolve (pack path " + Py.Repr(pp) + "); its states are silently left out" });
                    continue;
                }
                LintEl idoc;
                string why = Read(packs, name, out idoc);
                if (idoc == null)
                {
                    issues.Add(new Issue { Level = "error", Line = im.Line, Message = "import " + Py.Repr(im.Get("path")) + ": " + name + " " + why });
                    continue;
                }
                foreach (LintEl st in idoc.Iter("state"))
                {
                    string sn = st.Get("name");
                    if (string.IsNullOrEmpty(sn)) continue;
                    st.File = name;
                    if (!states.ContainsKey(sn)) names.Add(sn);
                    states[sn] = st;   // later imports win
                }
            }
            var existing = new HashSet<string>(StringComparer.Ordinal);
            foreach (LintEl st in doc.Iter("state")) existing.Add(st.Get("name") ?? "\0");
            foreach (string sn in names)
                if (!existing.Contains(sn)) { LintEl st = states[sn]; st.Parent = doc; doc.Kids.Add(st); }
            foreach (LintEl p in doc.Iter(null)) p.Kids.RemoveAll(delegate(LintEl c) { return c.Tag == "import"; });
        }

        /// <summary>
        /// Parses as lint.py does: expat's error and line (x) when expat does not read the text; else the XmlDoc (d),
        /// or XmlDoc's message when only it fails (or the bytes do not decode).
        /// </summary>
        internal static string ParseLikeExpat(byte[] bytes, string label, out XmlDoc d, out ExpatError x)
        {
            d = null; x = null;
            try
            {
                x = ExpatCheck.Check(XmlDoc.DecodeText(bytes, label));
                if (x != null) return null;
                d = XmlDoc.Parse(bytes, label);
                return d.Root == null ? "empty document" : null;
            }
            catch (ModKitException e) { d = null; return e.Message; }
        }

        internal static string Read(TriggerPacks packs, string name, out LintEl doc)
        {
            doc = null;
            byte[] b = packs.Read(name);
            if (b == null) return "parse error: cannot be read";
            XmlDoc d;
            ExpatError x;
            string why = ParseLikeExpat(b, name, out d, out x);
            if (x != null) return "parse error line " + x.Line + ": " + x.Message;
            if (why != null) return "parse error: " + why;
            doc = LintEl.From(d.Root, null);
            return null;
        }

        void RenameArgs(LintEl el, JsonObj names)
        {
            var renamed = new List<string>();
            foreach (string k in new List<string>(el.Keys))
            {
                if (k == "name") continue;
                string sk = ToSnakeCase(k);
                if (sk == null) continue;
                string nw = names == null ? null : names.Get(sk) as string;
                if (nw == null)
                {
                    if (k != sk) nw = sk;
                    else continue;
                }
                string v = el.Pop(k);
                el.Set(nw, v);    // the last one wins when two map to one name
                renamed.Add(nw + "=" + k);
            }
            if (renamed.Count > 0) el.RawArgs = string.Join(";", renamed.ToArray());
        }

        internal void Normalize(LintEl doc, List<Issue> issues)
        {
            // the nodes of a feature that is not on are left out
            foreach (LintEl p in doc.Iter(null))
                p.Kids.RemoveAll(delegate(LintEl c)
                {
                    if (c.Tag != "state" && c.Tag != "action" && c.Tag != "condition") return false;
                    string f = c.Get("feature");
                    return !string.IsNullOrEmpty(f) && !schema.Features.Contains(f);
                });
            foreach (LintEl st in doc.Iter("state"))
                if (st.Get("name") != null) { st.RawName = st.Get("name"); st.Set("name", FixClassName(st.RawName)); }
            foreach (LintEl tr in doc.Iter("transition"))
                if (tr.Get("state") != null) { tr.RawState = tr.Get("state"); tr.Set("state", FixClassName(tr.RawState)); }
            foreach (LintEl el in doc.Iter("action"))
            {
                string raw = el.Get("name");
                if (raw == null) continue;
                el.RawName = raw;
                string name = Translate(raw, schema.TrAction) ?? raw;
                el.Set("name", name);
                JsonObj ov = TriggerSchema.O(schema.ActionOverrides.Get(name));
                if (ov == null) continue;
                string splitter = TriggerSchema.S(ov, "splitter");
                if (ov.Map.ContainsKey("splitter"))
                {
                    string sp = el.Get(splitter);
                    string key = sp;
                    JsonObj types = TriggerSchema.O(ov.Get("types"));
                    if (key == null && types != null && types.Keys.Count > 0) key = types.Get(types.Keys[0]) as string;
                    JsonObj lookup = TriggerSchema.O(ov.Get("lookup"));
                    JsonObj sub = key == null || lookup == null ? null : TriggerSchema.O(lookup.Get(key));
                    if (sub == null)
                    {
                        var keys = new List<string>(lookup == null ? new List<string>() : lookup.Keys);
                        keys.Sort(StringComparer.Ordinal);
                        issues.Add(new Issue { Level = "error", Line = el.Line, Message = "action " + Py.Repr(raw) + ": " + splitter + "=" + Py.Repr(sp)
                            + " has no ingest mapping (FunctionLookup keys " + Py.Repr(keys) + "); the ingest throws on this script" });
                        el.IngestFail = true;
                        continue;
                    }
                    el.Splitter = SplitterNewName(splitter, TriggerSchema.O(sub.Get("names")));
                    ov = sub;
                    el.Set("name", TriggerSchema.S(ov, "name"));
                }
                RenameArgs(el, TriggerSchema.O(ov.Get("names")));
            }
            foreach (LintEl el in doc.Iter("condition"))
            {
                string raw = el.Get("name");
                if (raw == null) continue;
                el.RawName = raw;
                if (raw.StartsWith("!", StringComparison.Ordinal)) el.Set("negate", "true");
                string name = Translate(raw.TrimStart('!'), schema.TrCond) ?? raw;
                el.Set("name", name);
                JsonObj ov = TriggerSchema.O(schema.ConditionOverrides.Get(name));
                if (ov == null) continue;
                string on = TriggerSchema.S(ov, "name");
                if (on != name) el.Set("name", on);
                RenameArgs(el, TriggerSchema.O(ov.Get("names")));
            }
        }

        static string SplitterNewName(string attr, JsonObj names)
        {
            string sk = ToSnakeCase(attr ?? "");
            if (sk == null) return attr;
            string n = names == null ? null : names.Get(sk) as string;
            return n ?? sk;
        }
    }

    /// <summary>Python's repr and str of the values lint.py writes into its messages.</summary>
    internal static class Py
    {
        public static string Repr(string s)
        {
            if (s == null) return "None";
            char q = s.IndexOf('\'') >= 0 && s.IndexOf('"') < 0 ? '"' : '\'';
            var sb = new StringBuilder();
            sb.Append(q);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == q || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[i + 1]); i++; }
                else if (!Printable(c))
                {
                    if (c < 0x100) sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    else sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                }
                else sb.Append(c);
            }
            sb.Append(q);
            return sb.ToString();
        }

        static bool Printable(char c)
        {
            if (c == ' ') return true;
            switch (char.GetUnicodeCategory(c))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.Surrogate:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.SpaceSeparator:
                    return false;
                default: return true;
            }
        }

        public static string Repr(List<string> l)
        {
            var parts = new List<string>();
            foreach (string s in l) parts.Add(Repr(s));
            return "[" + string.Join(", ", parts.ToArray()) + "]";
        }

        public static string Str(object v)
        {
            if (v == null) return "None";
            if (v is bool) return (bool)v ? "True" : "False";
            if (v is decimal) return ((decimal)v).ToString(CultureInfo.InvariantCulture);
            return v.ToString();
        }

        static readonly Regex IntRe = new Regex("^\\s*[+-]?\\d+\\s*$");
        static readonly Regex FloatRe = new Regex("^\\s*[+-]?(\\d[\\d,]*\\.?\\d*|\\.\\d+)([eE][+-]?\\d+)?\\s*$");

        public static bool IsInt(string v) { return v != null && IntRe.IsMatch(v); }

        /// <summary>int.TryParse (NumberStyles.Integer): whitespace, sign, digits, the Int32 range.</summary>
        public static bool DotnetInt(string v)
        {
            int n;
            return DotnetInt(v, out n);
        }

        public static bool DotnetInt(string v, out int value)
        {
            value = 0;
            if (!IsInt(v)) return false;
            long n;
            if (!long.TryParse(v.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n) || n < int.MinValue || n > int.MaxValue) return false;
            value = (int)n;
            return true;
        }

        public static bool DotnetFloat(string v)
        {
            if (v == null) return false;
            switch (v.Trim().ToLowerInvariant())
            {
                case "nan": case "infinity": case "-infinity": case "+infinity": case "∞": case "-∞": return true;
            }
            return FloatRe.IsMatch(v);
        }

        /// <summary>fnmatch.fnmatch on POSIX: case-sensitive, '*' matches '/' too.</summary>
        public static bool FnMatch(string name, string pattern)
        {
            var sb = new StringBuilder("^");
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (c == '*') sb.Append(".*");
                else if (c == '?') sb.Append('.');
                else if (c == '[')
                {
                    int j = i + 1;
                    if (j < pattern.Length && pattern[j] == '!') j++;
                    if (j < pattern.Length && pattern[j] == ']') j++;
                    while (j < pattern.Length && pattern[j] != ']') j++;
                    if (j >= pattern.Length) { sb.Append("\\["); continue; }
                    string set = pattern.Substring(i + 1, j - i - 1).Replace("\\", "\\\\");
                    if (set.StartsWith("!", StringComparison.Ordinal)) set = "^" + set.Substring(1);
                    else if (set.StartsWith("^", StringComparison.Ordinal)) set = "\\" + set;
                    sb.Append('[').Append(set).Append(']');
                    i = j;
                }
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append("$");
            return Regex.IsMatch(name, sb.ToString(), RegexOptions.Singleline);
        }
    }

    /// <summary>One entry of known-issues.json.</summary>
    public sealed class TriggerAllow
    {
        public string Code, Path, Match, Level, Reason;
    }

    /// <summary>What TriggerLint.Lint checks beyond the file (lint.py --xblock, --strict).</summary>
    public sealed class TriggerLintOptions
    {
        /// <summary>Look for the files that import this one (lint.py does).</summary>
        public bool ScanImporters = true;
        /// <summary>The xblock asked for (a name, auto's map, a path); null: no object id check.</summary>
        public string XBlock;
        /// <summary>The objects of XBlock; null with XBlock set gives xblock-missing.</summary>
        public TriggerObjects Objects;
        /// <summary>maple2-gap warnings are errors (before known-issues).</summary>
        public bool Strict;
    }

    public sealed class TriggerLint
    {
        const string SchemaResource = "MS2.ModKit.trigger.schema.json";
        const string AllowResource = "MS2.ModKit.trigger.known-issues.json";
        static readonly object Gate = new object();
        static TriggerLint shipped;

        readonly TriggerSchema schema;
        readonly TriggerNormalizer norm;
        public readonly List<TriggerAllow> Allow;

        public TriggerSchema Schema { get { return schema; } }

        public TriggerLint(TriggerSchema schema, List<TriggerAllow> allow)
        {
            this.schema = schema;
            norm = new TriggerNormalizer(schema);
            Allow = allow ?? new List<TriggerAllow>();
        }

        /// <summary>The lint with the committed schema and allowlist.</summary>
        public static TriggerLint Shipped
        {
            get
            {
                lock (Gate)
                {
                    if (shipped == null) shipped = new TriggerLint(TriggerSchema.Parse(Resource(SchemaResource)), ParseAllow(Resource(AllowResource)));
                    return shipped;
                }
            }
        }

        static string Resource(string name)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) throw new ModKitException("the library has no resource " + name);
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        public static List<TriggerAllow> ParseAllow(string json)
        {
            var l = new List<TriggerAllow>();
            if (string.IsNullOrEmpty(json)) return l;
            var root = Json.Parse(json, "trigger allowlist") as JsonObj;
            var entries = root == null ? null : root.Get("entries") as List<object>;
            if (entries == null) return l;
            foreach (object o in entries)
            {
                var e = o as JsonObj;
                if (e == null) continue;
                l.Add(new TriggerAllow
                {
                    Code = TriggerSchema.S(e, "code"), Path = TriggerSchema.S(e, "path"), Match = TriggerSchema.S(e, "match"),
                    Level = TriggerSchema.S(e, "level"), Reason = TriggerSchema.S(e, "reason"),
                });
            }
            return l;
        }

        /// <summary>The allowlist entry that names the finding, or null. The path is matched below trigger/.</summary>
        public TriggerAllow Allowed(TriggerFinding f)
        {
            string rel = f.Entry ?? "";
            if (rel.StartsWith(Triggers.Folder, StringComparison.Ordinal)) rel = rel.Substring(Triggers.Folder.Length);
            foreach (TriggerAllow a in Allow)
            {
                if (!string.IsNullOrEmpty(a.Code) && a.Code != f.Code) continue;
                if (!string.IsNullOrEmpty(a.Path) && !(Py.FnMatch(rel, a.Path) || Py.FnMatch(rel, "*/" + a.Path))) continue;
                if (!string.IsNullOrEmpty(a.Match) && f.Message.IndexOf(a.Match, StringComparison.Ordinal) < 0) continue;
                if (!string.IsNullOrEmpty(a.Level) && a.Level != f.Level) continue;
                return a;
            }
            return null;
        }

        /// <summary>
        /// The findings of one script, the allowlist applied. The entry is its name (trigger/&lt;xblock&gt;/&lt;file&gt;.xml);
        /// packs resolves its imports and names its importers (null: none, as lint.py without a trigger/ root).
        /// </summary>
        public List<TriggerFinding> Lint(string entry, byte[] bytes, TriggerPacks packs)
        {
            string fmt;
            return Lint(entry, bytes, packs, out fmt);
        }

        public List<TriggerFinding> Lint(string entry, byte[] bytes, TriggerPacks packs, out string format)
        {
            return Lint(entry, bytes, packs, true, out format);
        }

        /// <summary>
        /// As Lint; with scanImporters false the importers are used only when the packs know them already
        /// (TriggerPacks.HasImporters), so the call does not read the whole archive.
        /// </summary>
        public List<TriggerFinding> Lint(string entry, byte[] bytes, TriggerPacks packs, bool scanImporters, out string format)
        {
            return Lint(entry, bytes, packs, new TriggerLintOptions { ScanImporters = scanImporters }, out format);
        }

        /// <summary>As Lint, with the object id check and the strict levels of the options.</summary>
        public List<TriggerFinding> Lint(string entry, byte[] bytes, TriggerPacks packs, TriggerLintOptions options, out string format)
        {
            options = options ?? new TriggerLintOptions();
            List<TriggerFinding> l = new Run(this, entry, packs, options).File(bytes, out format);
            foreach (TriggerFinding f in l)
            {
                if (options.Strict && f.Code == "maple2-gap" && f.Level == "warning") f.Level = "error";
                TriggerAllow a = Allowed(f);
                if (a != null) { f.Level = "allowed"; f.AllowReason = a.Reason ?? "?"; }
            }
            return l;
        }

        // ---- one file (Linter.lint_file) ----

        sealed class Run
        {
            readonly TriggerLint lt;
            readonly TriggerSchema schema;
            readonly string entry;
            readonly TriggerPacks packs;
            readonly List<TriggerFinding> outl = new List<TriggerFinding>();
            List<string> importers = new List<string>();
            Dictionary<string, LintEl> states;

            readonly bool scanImporters;
            readonly TriggerLintOptions opt;
            TriggerObjects idx;

            public Run(TriggerLint lt, string entry, TriggerPacks packs, TriggerLintOptions opt)
            {
                this.lt = lt; schema = lt.schema; this.entry = entry; this.packs = packs; this.opt = opt; scanImporters = opt.ScanImporters;
            }

            void Add(string level, string code, LintEl el, string msg) { Add(level, code, el == null ? 0 : el.Line, el == null ? null : el.Src, msg, el == null ? null : el.Tag); }

            void Add(string level, string code, int line, string src, string msg, string tag = null)
            {
                if (code == "maple2-gap" && !msg.StartsWith("Maple2 server (WIP) currently", StringComparison.Ordinal) && !msg.StartsWith("Maple2’s parser", StringComparison.Ordinal))
                    msg = "Maple2 server (WIP) currently " + char.ToLowerInvariant(msg[0]) + msg.Substring(1);
                outl.Add(new TriggerFinding { Entry = entry, Line = line, Level = level, Code = code, ImportedFrom = src, Tag = tag, Message = msg + (src != null ? " [in imported " + src + "]" : "") });
            }

            static bool IsRaw(LintEl doc)
            {
                if (doc.FindDeep("import") != null) return true;
                foreach (LintEl el in doc.Iter(null))
                {
                    if (el.Tag != "action" && el.Tag != "condition") continue;
                    string n = el.Get("name") ?? "";
                    if (n.StartsWith("!", StringComparison.Ordinal)) return true;
                    foreach (char c in n) if (char.IsUpper(c) || c > 127) return true;
                }
                return false;
            }

            public List<TriggerFinding> File(byte[] bytes, out string fmt)
            {
                fmt = null;
                XmlDoc d;
                ExpatError x;
                string why = TriggerNormalizer.ParseLikeExpat(bytes, entry, out d, out x);
                if (x != null) { Add("error", "parse", x.Line, null, "XML parse error: " + x.Message); return outl; }
                if (why != null)
                {
                    var lm = System.Text.RegularExpressions.Regex.Match(why, @" line (\d+): ");
                    Add("error", "parse", lm.Success ? int.Parse(lm.Groups[1].Value) : 0, null, "XML parse error: " + why);
                    return outl;
                }
                if (d.Root == null) { Add("error", "parse", 0, null, "empty document"); return outl; }
                LintEl doc = LintEl.From(d.Root, null);
                fmt = IsRaw(doc) ? "raw" : "server";
                var issues = new List<TriggerNormalizer.Issue>();
                if (fmt == "raw")
                {
                    if (packs != null && (scanImporters || packs.HasImporters)) importers = packs.ImportersOf(entry);
                    if (packs == null && doc.FindDeep("import") != null)
                        Add("warning", "import-root", 0, null, "cannot find the trigger/ root for <import>: pass --root");
                    lt.norm.MergeImports(doc, packs, issues);
                    foreach (LintEl st in doc.Iter("state"))
                        if (st.File != null) foreach (LintEl el in st.Iter(null)) el.Src = st.File;
                    lt.norm.Normalize(doc, issues);
                }
                else if (doc.FindDeep("import") != null)
                    Add("warning", "import-ignored", doc.FindDeep("import"), "TriggerStorage does not read <import>; only the ingest merges imports");
                foreach (TriggerNormalizer.Issue i in issues)
                    Add(i.Level, i.Message.Contains("import") ? "import" : "ingest", i.Line, null, i.Message);

                if (doc.Tag != "ms2") { Add("error", "root", doc, "root element is <" + doc.Tag + ">, the server needs <ms2>"); return outl; }

                if (opt.XBlock != null)
                {
                    idx = opt.Objects;
                    if (idx == null) Add("warning", "xblock-missing", 0, null, "xblock " + Py.Repr(opt.XBlock) + " is not in the index; object IDs not checked");
                }

                states = new Dictionary<string, LintEl>(StringComparer.Ordinal);
                var order = new List<string>();
                foreach (LintEl ch in doc.Kids)
                {
                    if (ch.Tag != "state") { Add("warning", "ignored-node", ch, "<" + ch.Tag + "> under <ms2> is ignored by the server"); continue; }
                    string n = ch.Get("name");
                    if (string.IsNullOrEmpty(n)) { Add("warning", "state-noname", ch, "state without a name is skipped"); continue; }
                    if (states.ContainsKey(n)) { Add("error", "state-dup", ch, "duplicate state " + Py.Repr(ch.NameBefore)); continue; }
                    states[n] = ch;
                    order.Add(n);
                }
                if (order.Count == 0) Add("warning", "no-states", doc, "script has no states");

                var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                foreach (string n in order) edges[n] = new HashSet<string>(StringComparer.Ordinal);
                foreach (string n in order)
                {
                    LintEl st = states[n];
                    bool imported = st.Src != null;
                    foreach (LintEl ch in st.Kids)
                    {
                        if (ch.Tag == "onEnter")
                        {
                            foreach (LintEl c in ch.Kids)
                            {
                                if (c.Tag == "action") CheckAction(c, edges[n], imported);
                                else if (c.Tag == "transition") CheckTransition(c, edges[n]);
                                else Add("warning", "ignored-node", c, "<" + c.Tag + "> in onEnter is ignored");
                            }
                        }
                        else if (ch.Tag == "onExit")
                        {
                            foreach (LintEl c in ch.Kids)
                            {
                                if (c.Tag == "action") CheckAction(c, edges[n], imported);
                                else Add("warning", "ignored-node", c, "<" + c.Tag + "> in onExit is ignored");
                            }
                        }
                        else if (ch.Tag == "condition") CheckCondition(ch, edges[n], imported);
                        else Add("warning", "ignored-node", ch, "<" + ch.Tag + "> in a state is ignored");
                    }
                }

                // reachable from the first state?
                if (order.Count > 0)
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal) { order[0] };
                    var todo = new List<string> { order[0] };
                    while (todo.Count > 0)
                    {
                        string cur = todo[todo.Count - 1];
                        todo.RemoveAt(todo.Count - 1);
                        foreach (string t in edges[cur])
                            if (t != null && edges.ContainsKey(t) && seen.Add(t)) todo.Add(t);
                    }
                    LintEl first = states[order[0]];
                    foreach (string n in order)
                        if (!seen.Contains(n) && states[n].Src == null)
                            Add("warning", "unreachable", states[n], "state " + Py.Repr(states[n].NameBefore) + " is never entered from " + Py.Repr(first.RawName ?? order[0]));
                }

                // the script is loaded by a TriggerModel of the map?
                if (idx != null)
                {
                    string b = entry.Replace('\\', '/');
                    b = b.Substring(b.LastIndexOf('/') + 1);
                    int dot = b.LastIndexOf('.');
                    if (dot > 0) b = b.Substring(0, dot);
                    b = b.ToLowerInvariant();
                    if (!idx.LoadsScript(b))
                        Add("warning", "no-trigger-model", 0, null, "no TriggerModel in xblock " + idx.Name + " names " + b + ".xml; the script is never loaded");
                }
                return outl;
            }

            void CheckTransition(LintEl tr, HashSet<string> edges)
            {
                string t = tr.Get("state");
                if (t == null) { Add("warning", "transition-nostate", tr, "transition without a state attribute"); return; }
                edges.Add(t);
                if (states.ContainsKey(t)) return;
                string raw = tr.RawState ?? t;
                if (importers.Count > 0)
                {
                    Add("warning", "lib-transition", tr, "transition to state " + Py.Repr(raw) + ", not in this file; it is imported by "
                        + importers.Count.ToString(CultureInfo.InvariantCulture) + " script(s) (e.g. " + importers[0] + "), which must define it");
                    return;
                }
                Add("error", "bad-transition", tr, "transition to state " + Py.Repr(raw) + ", which does not exist" + (raw == t ? "" : " (normalised " + Py.Repr(t) + ")"));
                Add("warning", "maple2-gap", tr, "Maple2 server (WIP) currently stays in its current state when the target is missing; actions in a condition that holds run again every tick");
            }

            void CheckAction(LintEl el, HashSet<string> edges, bool imported)
            {
                if (el.IngestFail) return;
                string name = el.Get("name");
                if ((el.NameBefore ?? "").ToLowerInvariant() == "dungeonvariable")
                {
                    Add("warning", "maple2-gap", el, "DungeonVariable has no ingest mapping or server action; it does nothing");
                    return;
                }
                JsonObj e = string.IsNullOrEmpty(name) ? null : TriggerSchema.O(schema.Actions.Get(name));
                if (e == null)
                {
                    Add("error", "unknown-action", el, "unknown action " + Py.Repr(el.NameBefore) + (el.NameBefore == name ? "" : " (normalised " + Py.Repr(name) + ")") + "; the server skips it");
                    return;
                }
                CheckArgs(el, e, "action", imported);
                if (name != "set_skip" && name != "set_scene_skip") return;
                foreach (string arg in new[] { "state", "action" })
                {
                    string v = el.Get(arg);
                    if (string.IsNullOrEmpty(v)) continue;
                    string fixedName = TriggerNormalizer.FixClassName(v);
                    if (fixedName != v)
                        Add("warning", "maple2-gap", el, name + " " + arg + "=" + Py.Repr(v) + " is not normalised by the ingest; the server looks up that exact state name");
                    if (states.ContainsKey(v)) edges.Add(v);
                    else if (states.ContainsKey(fixedName)) edges.Add(fixedName);
                    else if (arg == "state" && !imported) Add("warning", "skip-state-missing", el, name + " state=" + Py.Repr(v) + " does not exist");
                }
            }

            void CheckCondition(LintEl el, HashSet<string> edges, bool imported)
            {
                string name = el.Get("name");
                JsonObj e = string.IsNullOrEmpty(name) ? null : TriggerSchema.O(schema.Conditions.Get(name));
                string rawName = el.NameBefore;
                if ((rawName ?? "").ToLowerInvariant() == "dungeonvariable")
                {
                    Add("warning", "maple2-gap", el, "DungeonVariable has no ingest mapping or server condition; it does nothing");
                    if (e == null) return;
                }
                if (e == null)
                {
                    Add("error", "unknown-condition", el, "unknown condition " + Py.Repr(rawName) + (rawName == name ? "" : " (normalised " + Py.Repr(name) + ")")
                        + "; the server skips it with its transition and actions");
                    return;
                }
                CheckArgs(el, e, "condition", imported);
                string cls = TriggerSchema.S(e, "class");
                if (cls == "NpcHp" && el.Get("compare") != null) Add("warning", "maple2-gap", el, "CheckNpcHp compare is ignored; the server always uses higher");
                if (cls == "UserValue" && el.Get("operator") != null) Add("warning", "maple2-gap", el, "UserValue operator is ignored; the server always uses the default comparison");
                if (cls == "NpcDamage" && (el.Get("damageRate") != null || el.Get("damage_rate") != null)) Add("warning", "maple2-gap", el, "npc_damage damageRate is never read by the server");
                if (cls == "WeddingHallState" && (el.Get("hallState") != null || el.Get("hall_state") != null)) Add("warning", "maple2-gap", el, "wedding_hall_state hallState is never read by the server");
                if (TriggerSchema.True(e, "group"))
                {
                    LintEl g = el.Child("group");
                    if (g == null) { Add("error", "group-missing", el, Py.Str(rawName) + " without <group> is skipped by the server"); return; }
                    if (!imported)
                        Add("warning", "maple2-gap", el, Py.Str(rawName) + ": the server never reads the inner conditions; it reads transition/action inside <group> ("
                            + (name == "all_of" ? "always true" : "never true") + ")");
                    foreach (LintEl c in g.Kids)
                    {
                        if (c.Tag == "condition")
                        {
                            JsonObj ce = TriggerSchema.O(schema.Conditions.Get(c.Get("name") ?? ""));
                            if (ce == null) Add("error", "unknown-condition", c, "unknown condition " + Py.Repr(c.NameBefore) + " in group");
                            else CheckArgs(c, ce, "condition", imported);
                        }
                        else if (c.Tag == "transition") CheckTransition(c, edges);
                        else if (c.Tag == "action") CheckAction(c, edges, imported);
                    }
                    foreach (LintEl c in el.Kids)
                    {
                        if (c.Tag == "transition")
                        {
                            // read by no one; the edge counts so that reachability shows what the author meant
                            edges.Add(c.Get("state"));
                            if (c.Get("state") == null || !states.ContainsKey(c.Get("state"))) CheckTransition(c, new HashSet<string>());
                        }
                        else if (c.Tag == "action") CheckAction(c, edges, imported);
                    }
                    return;
                }
                foreach (LintEl c in el.Kids)
                {
                    if (c.Tag == "transition") CheckTransition(c, edges);
                    else if (c.Tag == "action") CheckAction(c, edges, imported);
                    else Add("warning", "ignored-node", c, "<" + c.Tag + "> in condition " + Py.Str(rawName) + " is ignored");
                }
            }

            void CheckArgs(LintEl el, JsonObj e, string kind, bool imported)
            {
                var args = new Dictionary<string, JsonObj>(StringComparer.Ordinal);
                foreach (JsonObj a in TriggerSchema.Args(e)) { string n = TriggerSchema.S(a, "name"); if (n != null) args[n] = a; }
                var rawargs = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string p in (el.RawArgs ?? "").Split(';'))
                {
                    int eq = p.IndexOf('=');
                    if (eq >= 0) rawargs[p.Substring(0, eq)] = p.Substring(eq + 1);
                }
                string what = kind + " " + Py.Str(el.NameBefore);
                JsonObj compare = TriggerSchema.O(e.Get("compare"));
                for (int i = 0; i < el.Keys.Count; i++)
                {
                    string k = el.Keys[i], v = el.Vals[i];
                    if (k == "name" || k == "feature" || k == el.Splitter) continue;   // the splitter only picks the server action
                    if (kind == "condition" && compare != null && TriggerSchema.S(compare, "op") == k) continue;
                    if (k == "negate" && kind == "condition")
                    {
                        if (!TriggerSchema.True(e, "negatable") && !imported)
                            Add("warning", "maple2-gap", el, what + ": negation is ignored (the server condition has no negate argument)");
                        continue;
                    }
                    string an;
                    if (!rawargs.TryGetValue(k, out an)) an = k;
                    JsonObj a;
                    if (!args.TryGetValue(k, out a))
                    {
                        if (!imported) Add("warning", "maple2-gap", el, "Maple2’s parser has no mapping for " + what + " argument " + Py.Repr(an));
                        continue;
                    }
                    if (imported) continue;
                    string t = TriggerSchema.S(a, "type");
                    string label = what + " " + an + "=" + Py.Repr(v);
                    if (t == "int" && v != "" && !Py.DotnetInt(v))
                        Add("warning", "maple2-gap", el, label + ": Maple2’s parser rejects this integer value; reads as 0");
                    else if (t == "float" && v != "" && !Py.DotnetFloat(v))
                        Add("warning", "maple2-gap", el, label + ": Maple2’s parser rejects this number; reads as 0");
                    else if (t == "bool" && Array.IndexOf(new[] { "", "1", "0", "true", "false" }, v.Trim().ToLowerInvariant()) < 0)
                        Add("warning", "maple2-gap", el, label + ": Maple2’s parser rejects this boolean; reads as false");
                    else if (t == "vector3" && v != "")
                    {
                        string[] p = v.Split(',');
                        bool bad = p.Length != 3;
                        foreach (string x in p) if (!Py.DotnetFloat(x)) bad = true;
                        if (bad) Add("warning", "maple2-gap", el, label + ": Maple2’s parser rejects this vector; reads as 0,0,0");
                    }
                    else if (t == "int_list")
                    {
                        List<string> dropped;
                        string err = IntArray(v, out dropped);
                        if (err != null) Add("warning", "maple2-gap", el, label + ": Maple2’s parser rejects this value (" + err + ")");
                        else if (dropped.Count > 0) Add("warning", "int-list-dropped", el, label + ": parts " + Py.Repr(dropped) + " are dropped");
                    }
                    else if (t == "enum" && v != "")
                    {
                        string en = TriggerSchema.S(a, "enum");
                        JsonObj names = TriggerSchema.O(en == null ? null : schema.Enums.Get(en)) ?? new JsonObj();
                        if (!EnumParse(v, names))
                        {
                            if (TriggerSchema.S(a, "on_bad") == "throw")
                                Add("warning", "maple2-gap", el, label + ": Maple2’s parser rejects this enum value (Enum.Parse<" + Py.Str(en) + "> throws)");
                            else
                                Add("warning", "maple2-gap", el, label + ": Maple2’s parser rejects this " + Py.Str(en) + "; reads as " + Py.Str(a.Get("default")));
                        }
                    }
                    else if (t == "banner" && Array.IndexOf(new[] { "", "1", "3", "4", "5", "6", "7" }, v) < 0)
                        Add("warning", "bad-banner", el, label + ": only 1,3,4,5,6,7 are mapped; reads as Text");
                }
                if (!imported)
                    foreach (JsonObj a in TriggerSchema.Args(e))
                    {
                        string n = TriggerSchema.S(a, "name");
                        if (TriggerSchema.True(a, "required") && string.IsNullOrEmpty(el.Get(n)))
                            Add("warning", "missing-arg", el, what + ": required argument " + Py.Repr(n) + " is missing; reads as " + Py.Str(a.Get("default")));
                    }
                if (idx != null) CheckRefs(el, kind, args, rawargs, what);
            }

            // ref (schema) -> the xblock kinds the server's lookup can find
            static readonly string[] SpawnKinds = { "npc_spawn", "event_npc_spawn", "pc_spawn", "region_spawn" };

            static string[] RefKinds(string kind, string name, string arg, string reff)
            {
                if (kind == "action" && name == "spawn_monster" && arg == "spawn_ids") return new[] { "event_npc_spawn" };
                switch (reff)
                {
                    case "spawn": return SpawnKinds;
                    default: return new[] { reff };
                }
            }

            /// <summary>The object ids of the element against the map's xblock (Linter.check_refs).</summary>
            void CheckRefs(LintEl el, string kind, Dictionary<string, JsonObj> args, Dictionary<string, string> rawargs, string what)
            {
                string name = el.Get("name");
                foreach (KeyValuePair<string, JsonObj> ka in args)   // first-seen order, last definition (a dict of lint.py)
                {
                    JsonObj a = ka.Value;
                    string k = ka.Key, reff = TriggerSchema.S(a, "ref"), v = k == null ? null : el.Get(k);
                    if (string.IsNullOrEmpty(reff) || string.IsNullOrEmpty(v)) continue;
                    // set_dialogue: spawn_id is an object id only with type 1
                    if (kind == "action" && name == "set_dialogue" && k == "spawn_id" && (el.Get("type") ?? "").Trim() != "1") continue;
                    var ids = new List<int>();
                    if (TriggerSchema.S(a, "type") == "int_list") IntIds(v, ids);
                    else { int n; if (Py.DotnetInt(v, out n)) ids.Add(n); }
                    string[] kinds = RefKinds(kind, name, k, reff);
                    var missing = new List<int>();
                    var wrong = new List<KeyValuePair<int, List<string>>>();
                    foreach (int i in ids)
                    {
                        if (i <= 0) continue;
                        bool found = false;
                        foreach (string kd in kinds) if (idx.Has(kd, i)) { found = true; break; }
                        if (found) continue;
                        List<string> other = idx.KindsOf(i);
                        if (other.Count > 0) wrong.Add(new KeyValuePair<int, List<string>>(i, other));
                        else missing.Add(i);
                    }
                    string an;
                    if (!rawargs.TryGetValue(k, out an)) an = k;
                    if (reff == "breakable")
                    {
                        var nif = new List<int>();
                        foreach (KeyValuePair<int, List<string>> w in wrong) if (w.Value.Contains("breakable_nif")) nif.Add(w.Key);
                        if (nif.Count > 0)
                        {
                            wrong.RemoveAll(w => nif.Contains(w.Key));
                            Add("warning", "maple2-gap", el, what + " " + an + ": id(s) " + Compact(nif) + " are MS2BreakableNIF objects, which the "
                                + "ingest does not export (case commented out in MapEntityMapper), so the server finds nothing");
                        }
                    }
                    if (missing.Count > 0)
                        Add("warning", "ref-missing", el, what + " " + an + ": " + string.Join("/", kinds) + " id(s) " + Compact(missing) + " not in xblock " + idx.Name);
                    if (wrong.Count > 0)
                    {
                        var wk = new List<string>();
                        var wi = new List<int>();
                        foreach (KeyValuePair<int, List<string>> w in wrong) { wi.Add(w.Key); foreach (string x in w.Value) if (!wk.Contains(x)) wk.Add(x); }
                        wk.Sort(string.CompareOrdinal);
                        Add("warning", "ref-kind", el, what + " " + an + ": id(s) " + Compact(wi) + " are " + string.Join("/", wk.ToArray()) + ", not " + string.Join("/", kinds));
                    }
                }
            }

            static string Compact(List<int> l)
            {
                var s = new List<int>(new HashSet<int>(l));
                s.Sort();
                var parts = new List<string>();
                foreach (int i in s)
                {
                    if (parts.Count == 8) break;
                    parts.Add(i.ToString(CultureInfo.InvariantCulture));
                }
                return string.Join(",", parts.ToArray()) + (s.Count > 8 ? ",...(" + s.Count.ToString(CultureInfo.InvariantCulture) + ")" : "");
            }

            const int RangeLimit = 100000;

            static void Range(int a, int b, List<int> ids)
            {
                if (a > b) { int t = a; a = b; b = t; }
                long end = (long)b - a > RangeLimit ? (long)a + RangeLimit - 1 : b;
                for (long i = a; i <= end; i++) ids.Add((int)i);
            }

            /// <summary>The ids of TriggerFunctionMapping.ParseIntArray (int_array of lint.py); "all" is -1.</summary>
            static void IntIds(string v, List<int> ids)
            {
                if (string.IsNullOrEmpty(v)) return;
                if (v == "all") { ids.Add(-1); return; }
                int x, y;
                if (v.IndexOf(',') >= 0)
                {
                    foreach (string part in v.Split(','))
                    {
                        if (part.IndexOf('-') >= 0)
                        {
                            string[] rp = part.Split('-');
                            if (rp.Length == 2 && Py.DotnetInt(rp[0], out x) && Py.DotnetInt(rp[1], out y)) Range(x, y, ids);
                        }
                        else if (Py.DotnetInt(part, out x)) ids.Add(x);
                    }
                    return;
                }
                if (v.IndexOf('-') >= 0)
                {
                    string[] rp = v.Split('-');
                    if (rp.Length == 2 && Py.DotnetInt(rp[0], out x) && Py.DotnetInt(rp[1], out y)) { Range(x, y, ids); return; }
                }
                if (Py.DotnetInt(v, out x)) ids.Add(x);
            }

            /// <summary>TriggerFunctionMapping.ParseIntArray: why it throws (null if it does not), and the parts it drops.</summary>
            static string IntArray(string v, out List<string> dropped)
            {
                dropped = new List<string>();
                if (string.IsNullOrEmpty(v) || v == "all") return null;
                if (v.IndexOf(',') >= 0)
                {
                    foreach (string part in v.Split(','))
                    {
                        if (part.IndexOf('-') >= 0)
                        {
                            string[] rp = part.Split('-');
                            if (!(rp.Length == 2 && Py.DotnetInt(rp[0]) && Py.DotnetInt(rp[1]))) dropped.Add(part);
                        }
                        else if (!Py.DotnetInt(part)) dropped.Add(part);
                    }
                    return null;
                }
                if (v.IndexOf('-') >= 0)
                {
                    string[] rp = v.Split('-');
                    if (rp.Length == 2 && Py.DotnetInt(rp[0]) && Py.DotnetInt(rp[1])) return null;
                }
                return Py.DotnetInt(v) ? null : "int.Parse(" + Py.Repr(v) + ") throws";
            }

            /// <summary>Enum.Parse(type, v, ignoreCase: true) succeeds?</summary>
            static bool EnumParse(string v, JsonObj names)
            {
                string s = v.Trim();
                if (Py.IsInt(s)) return true;
                var low = new HashSet<string>(StringComparer.Ordinal);
                foreach (string k in names.Keys) low.Add(k.ToLowerInvariant());
                if (s.Length == 0) return false;
                foreach (string p in s.Split(',')) if (!low.Contains(p.Trim().ToLowerInvariant())) return false;
                return true;
            }
        }
    }
}
