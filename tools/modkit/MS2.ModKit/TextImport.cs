// Text import: fills the text of our string files that is still Korean from another client's string files (the
// global client's string/en), as row edits of a mod.
//
// A file of ours (string/<lang>/<name>.xml) is matched to the source file of the same name, or to a family of
// split files (Families: the global client splits quest scripts, quest descriptions, npc scripts, skill names and
// the skill and additional descriptions over several files). Rows are keyed by id (questID when there is no id),
// leading zeros dropped. Our row takes the source row with the same feature and locale, else the source rows of
// the key when they all give the attribute the same text; else the attribute is "ambiguous" and left alone. A level
// of ours (the additional descriptions have one row per level) must be the source row's level too.
//
// Per text attribute (every attribute but the keys and NotText):
//   - ours has Hangul, the source has none, is not empty and is not a placeholder (SCRIPTQUEST_..._NAME): Fill,
//     when both have the same {0} slots: the global client writes some descriptions per level with the numbers in,
//     where ours has one text with {0} for them ("form": left alone);
//   - ours has no Hangul: never filled; when the source text is plainly different (word overlap under 15%) it is
//     listed (Plain) so the user may take it row by row;
//   - the mod sets the attribute already: kept as the mod has it.
//
// Put writes Change row ops with set attributes into the mod (sync="false": the field maps do not carry them to
// the other client); Take removes the ops Put wrote (the attributes whose value is still the source text).
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public enum TextKind { Fill, Plain }

    /// <summary>One attribute of one of our rows and the source text for it.</summary>
    public sealed class TextRow
    {
        public TextKind Kind;
        public string File;         // our file name, e.g. itemname.xml
        public string Entry;        // string/<lang>/<file>
        public string Id;           // as our row has it
        public string Feature, Locale;
        public string Attr;
        public string Ours, Source;
        public string At;           // selector of our row (RowPath.Canonical)
        public double Overlap;      // Plain: the word overlap (0..1)
        public override string ToString() { return Entry + " " + At + " @" + Attr + ": " + Ours + " -> " + Source; }
    }

    /// <summary>What the scan found in one of our files.</summary>
    public sealed class TextFileStat
    {
        public string File;
        public readonly List<string> SourceFiles = new List<string>();
        public int Keys;            // keys of ours
        public int Has;             // of them, keys the source has
        public int Fill, Plain, Near, Same, Korean, Ambiguous, Kept, Placeholder;
        public int Form;            // ours has other {n} slots than the source text
        public int Missing;         // the source row has no text for the attribute
        public int SourceOnly;      // keys only the source has
        public string Error;        // the file (or a source file) does not parse
    }

    public sealed class TextImport
    {
        /// <summary>Our file (no .xml) -> the prefix of the source's split files.</summary>
        public static readonly Dictionary<string, string> Families = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "questdescription_final", "questdescription_" },
            { "scriptquest", "scriptquest_" },
            { "scriptnpc", "scriptnpc" },
            { "stringskilldescription", "korskilldescription" },
            { "stringadditionaldescription", "koradditionaldescription" },
            { "skillname", "skillname" },
        };

        /// <summary>Attributes that are keys, references or flags, not text.</summary>
        public static readonly HashSet<string> NotText = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "questID", "level", "feature", "locale", "isTutorial", "regionID", "class", "npc", "grade", "type",
            "contentsid", "specialNotifyIcon",
        };

        public const double PlainOverlap = 0.15;

        static readonly Regex Hangul = new Regex("[가-힣]", RegexOptions.CultureInvariant);
        static readonly Regex Placeholder = new Regex("^[A-Z][A-Z0-9]*(_[A-Z0-9]+)+$", RegexOptions.CultureInvariant);
        static readonly Regex Word = new Regex("[a-z0-9]+", RegexOptions.CultureInvariant);
        static readonly Regex Slot = new Regex("\\{[0-9]+\\}", RegexOptions.CultureInvariant);
        static readonly Regex Space = new Regex("\\s+", RegexOptions.CultureInvariant);

        public readonly string Target, TargetLang, Source, SourceLang;
        public readonly List<TextFileStat> Files = new List<TextFileStat>();
        public readonly List<TextRow> Rows = new List<TextRow>();

        TextImport(string target, string targetLang, string source, string sourceLang)
        {
            Target = target; TargetLang = targetLang; Source = source; SourceLang = sourceLang;
        }

        public static bool IsKorean(string s) { return s != null && Hangul.IsMatch(s); }
        public static bool IsPlaceholder(string s) { return s != null && Placeholder.IsMatch(s.Trim()); }

        public static string Key(string id)
        {
            if (id == null) return null;
            string k = id.Trim().TrimStart('0');
            return k.Length == 0 ? "0" : k;
        }

        /// <summary>The source files for our file: the family's split files, else the file of the same name.</summary>
        public static List<string> SourceFilesOf(string ourFile, ICollection<string> sourceFiles)
        {
            var l = new List<string>();
            string b = ourFile.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? ourFile.Substring(0, ourFile.Length - 4) : ourFile;
            string prefix;
            if (Families.TryGetValue(b, out prefix))
            {
                foreach (string f in sourceFiles) if (f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) l.Add(f);
            }
            else
                foreach (string f in sourceFiles) if (string.Equals(f, ourFile, StringComparison.OrdinalIgnoreCase)) l.Add(f);
            l.Sort(StringComparer.OrdinalIgnoreCase);
            return l;
        }

        /// <summary>Word overlap of two texts (Jaccard over lower-case words), 1 when neither has a word.</summary>
        public static double Overlap(string a, string b)
        {
            var wa = Words(a); var wb = Words(b);
            if (wa.Count == 0 && wb.Count == 0) return 1;
            int both = 0;
            foreach (string w in wa) if (wb.Contains(w)) both++;
            int any = wa.Count + wb.Count - both;
            return any == 0 ? 1 : (double)both / any;
        }

        static HashSet<string> Words(string s)
        {
            var h = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Word.Matches((s ?? "").ToLowerInvariant())) h.Add(m.Value);
            return h;
        }

        /// <summary>The {n} slots of a text, sorted, as one string ("{0}{1}").</summary>
        public static string Slots(string s)
        {
            var l = new List<string>();
            foreach (Match m in Slot.Matches(s ?? "")) if (!l.Contains(m.Value)) l.Add(m.Value);
            l.Sort(StringComparer.Ordinal);
            return string.Concat(l.ToArray());
        }

        static string Norm(string s) { return Space.Replace(s ?? "", " ").Trim().ToLowerInvariant(); }

        static string FileOf(string entry) { return entry.Substring(entry.LastIndexOf('/') + 1); }

        static string Folder(string lang) { return "string/" + lang + "/"; }

        /// <summary>Scans the data set: our archive and language against the source's.</summary>
        public static TextImport Scan(DataSet data, Mod mod, string target, string targetLang, string source, string sourceLang)
        {
            M2dArchive ta = data.Open(target), sa = data.Open(source);
            var ours = new SortedDictionary<string, Func<byte[]>>(StringComparer.OrdinalIgnoreCase);
            var theirs = new Dictionary<string, Func<byte[]>>(StringComparer.OrdinalIgnoreCase);
            string tf = Folder(targetLang), sf = Folder(sourceLang);
            foreach (M2dEntry e in ta.Entries)
            {
                string n = e.Name.Replace('\\', '/');
                if (!n.StartsWith(tf, StringComparison.OrdinalIgnoreCase) || n.IndexOf('/', tf.Length) >= 0 || !n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                M2dEntry k = e;
                ours[FileOf(n)] = delegate { return ta.Read(k); };
            }
            foreach (M2dEntry e in sa.Entries)
            {
                string n = e.Name.Replace('\\', '/');
                if (!n.StartsWith(sf, StringComparison.OrdinalIgnoreCase) || n.IndexOf('/', sf.Length) >= 0 || !n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                M2dEntry k = e;
                theirs[FileOf(n)] = delegate { return sa.Read(k); };
            }
            return Scan(ours, theirs, mod, target, targetLang, source, sourceLang);
        }

        /// <summary>Scans files given by name (our files and the source's, the bytes read on demand).</summary>
        public static TextImport Scan(IDictionary<string, Func<byte[]>> ours, IDictionary<string, Func<byte[]>> theirs, Mod mod,
            string target, string targetLang, string source, string sourceLang)
        {
            var ti = new TextImport(target, targetLang, source, sourceLang);
            var names = new List<string>(ours.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            var parsed = new Dictionary<string, XmlDoc>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in names)
            {
                var st = new TextFileStat { File = file };
                ti.Files.Add(st);
                st.SourceFiles.AddRange(SourceFilesOf(file, theirs.Keys));
                string entry = Folder(targetLang) + file;
                XmlDoc doc;
                try { doc = XmlDoc.Parse(ours[file](), target + " " + entry); }
                catch (Exception ex) { st.Error = ex.Message; continue; }
                if (doc.Root == null) continue;
                // the source rows of the family, by key
                var src = new Dictionary<string, List<XmlElem>>(StringComparer.Ordinal);
                foreach (string sfName in st.SourceFiles)
                {
                    XmlDoc sd;
                    if (!parsed.TryGetValue(sfName, out sd))
                    {
                        try { sd = XmlDoc.Parse(theirs[sfName](), source + " " + Folder(sourceLang) + sfName); }
                        catch (Exception ex) { st.Error = sfName + ": " + ex.Message; sd = null; }
                        parsed[sfName] = sd;
                    }
                    if (sd == null || sd.Root == null) continue;
                    foreach (XmlElem r in sd.Root.Children)
                    {
                        string k = Key(r.Attr("id") ?? r.Attr("questID"));
                        if (k == null) continue;
                        List<XmlElem> l;
                        if (!src.TryGetValue(k, out l)) src[k] = l = new List<XmlElem>(1);
                        l.Add(r);
                    }
                }
                Dictionary<string, string> modSets = ModSets(mod, target, entry);
                var ourKeys = new HashSet<string>(StringComparer.Ordinal);
                var hasKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (XmlElem r in doc.Root.Children)
                {
                    string id = r.Attr("id") ?? r.Attr("questID");
                    string k = Key(id);
                    if (k == null) continue;
                    ourKeys.Add(k);
                    List<XmlElem> cands;
                    if (!src.TryGetValue(k, out cands)) continue;
                    hasKeys.Add(k);
                    string feature = r.Attr("feature") ?? "", locale = r.Attr("locale") ?? "", level = r.Attr("level");
                    if (level != null)
                    {
                        cands = cands.FindAll(delegate(XmlElem c) { return (c.Attr("level") ?? "") == level; });
                        if (cands.Count == 0) continue;
                    }
                    XmlElem exact = null;
                    foreach (XmlElem c in cands)
                        if ((c.Attr("feature") ?? "") == feature && (c.Attr("locale") ?? "") == locale) { exact = c; break; }
                    string at = null;
                    foreach (XmlAttr a in r.Attrs)
                    {
                        if (NotText.Contains(a.Name) || a.Value.Length == 0) continue;
                        string theirsText;
                        if (exact != null) theirsText = exact.Attr(a.Name);
                        else if (!Agreed(cands, a.Name, out theirsText)) { st.Ambiguous++; continue; }
                        if (string.IsNullOrEmpty(theirsText)) { st.Missing++; continue; }
                        bool korean = IsKorean(a.Value);
                        if (Norm(a.Value) == Norm(theirsText)) { st.Same++; continue; }
                        if (IsKorean(theirsText)) { if (korean) st.Korean++; continue; }
                        if (IsPlaceholder(theirsText)) { st.Placeholder++; continue; }
                        if (at == null) at = RowPath.Canonical(r);
                        if (modSets.ContainsKey(at + "\n" + a.Name)) { st.Kept++; continue; }
                        var row = new TextRow { File = file, Entry = entry, Id = id, Feature = feature, Locale = locale, Attr = a.Name, Ours = a.Value, Source = theirsText, At = at };
                        if (korean && Slots(a.Value) != Slots(theirsText)) { st.Form++; continue; }
                        if (korean) { row.Kind = TextKind.Fill; st.Fill++; ti.Rows.Add(row); continue; }
                        row.Overlap = Overlap(a.Value, theirsText);
                        if (row.Overlap < PlainOverlap) { row.Kind = TextKind.Plain; st.Plain++; ti.Rows.Add(row); }
                        else st.Near++;
                    }
                }
                st.Keys = ourKeys.Count;
                st.Has = hasKeys.Count;
                foreach (string k in src.Keys) if (!ourKeys.Contains(k)) st.SourceOnly++;
            }
            return ti;
        }

        // the source rows of a key give the attribute one text (rows without it do not count)
        static bool Agreed(List<XmlElem> cands, string attr, out string text)
        {
            text = null;
            foreach (XmlElem c in cands)
            {
                string v = c.Attr(attr);
                if (string.IsNullOrEmpty(v)) continue;
                if (text == null) text = v;
                else if (Norm(text) != Norm(v)) { text = null; return false; }
            }
            return true;
        }

        // the attributes the mod sets in the entry: "selector\nattribute" -> value
        static Dictionary<string, string> ModSets(Mod mod, string archive, string entry)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (mod == null) return d;
            foreach (ModOp op in mod.Ops)
            {
                var ed = op as EditOp;
                if (ed == null || !SameArchive(ed.Archive, archive) || !string.Equals(ed.Entry.Replace('\\', '/'), entry, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (RowOp r in ed.Rows)
                {
                    if (r.Kind != RowOpKind.Change) continue;
                    foreach (AttrOp a in r.Attrs)
                        if (a.At == null && a.Kind != AttrOpKind.Text) d[(r.At ?? "") + "\n" + a.Name] = a.Kind == AttrOpKind.Set ? a.Value : null;
                }
            }
            return d;
        }

        static bool SameArchive(string a, string b) { return string.Equals(DataSet.NormalizeRef(a), DataSet.NormalizeRef(b), StringComparison.OrdinalIgnoreCase); }

        EditOp EditOf(Mod mod, string entry, bool create)
        {
            foreach (ModOp op in mod.Ops)
            {
                var ed = op as EditOp;
                if (ed != null && SameArchive(ed.Archive, Target) && string.Equals(ed.Entry.Replace('\\', '/'), entry, StringComparison.OrdinalIgnoreCase)) return ed;
            }
            if (!create) return null;
            var n = new EditOp { Archive = Target, Entry = entry };
            mod.Ops.Add(n);
            return n;
        }

        /// <summary>
        /// Writes the rows into the mod: for each a Change op of its row (sync="false") with the attribute set to the
        /// source text. An attribute the mod sets already is left as it is. Returns the number of attributes set.
        /// </summary>
        public int Put(Mod mod, IEnumerable<TextRow> rows)
        {
            int n = 0;
            var byEntry = new Dictionary<string, Dictionary<string, RowOp>>(StringComparer.OrdinalIgnoreCase);
            foreach (TextRow t in rows)
            {
                Dictionary<string, RowOp> changes;
                if (!byEntry.TryGetValue(t.Entry, out changes))
                {
                    byEntry[t.Entry] = changes = new Dictionary<string, RowOp>(StringComparer.Ordinal);
                    EditOp have = EditOf(mod, t.Entry, false);
                    if (have != null)
                        foreach (RowOp r in have.Rows)
                            if (r.Kind == RowOpKind.Change && !changes.ContainsKey(r.At ?? "")) changes[r.At ?? ""] = r;
                }
                RowOp op;
                if (!changes.TryGetValue(t.At, out op))
                {
                    op = new RowOp { Kind = RowOpKind.Change, At = t.At, Sync = false };
                    EditOf(mod, t.Entry, true).Rows.Add(op);
                    changes[t.At] = op;
                }
                bool set = false;
                foreach (AttrOp a in op.Attrs) if (a.At == null && a.Name == t.Attr) { set = true; break; }
                if (set) continue;
                op.Attrs.Add(new AttrOp { Kind = AttrOpKind.Set, Name = t.Attr, Value = t.Source, Sync = false });
                n++;
            }
            return n;
        }

        /// <summary>
        /// Takes the rows out of the mod: the attribute sets whose value is still the source text. A row op left with
        /// nothing goes, and an edit of an entry left with no row op. Returns the number of attributes taken out.
        /// </summary>
        public int Take(Mod mod, IEnumerable<TextRow> rows)
        {
            int n = 0;
            var want = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (TextRow t in rows) want[t.Entry.ToLowerInvariant() + "\n" + t.At + "\n" + t.Attr] = t.Source;
            for (int i = mod.Ops.Count - 1; i >= 0; i--)
            {
                var ed = mod.Ops[i] as EditOp;
                if (ed == null || !SameArchive(ed.Archive, Target)) continue;
                string entry = ed.Entry.Replace('\\', '/').ToLowerInvariant();
                for (int j = ed.Rows.Count - 1; j >= 0; j--)
                {
                    RowOp r = ed.Rows[j];
                    if (r.Kind != RowOpKind.Change) continue;
                    int before = r.Attrs.Count;
                    r.Attrs.RemoveAll(delegate(AttrOp a)
                    {
                        string v;
                        return a.At == null && a.Kind == AttrOpKind.Set && want.TryGetValue(entry + "\n" + (r.At ?? "") + "\n" + a.Name, out v) && v == a.Value;
                    });
                    n += before - r.Attrs.Count;
                    if (before > 0 && r.Attrs.Count == 0) ed.Rows.RemoveAt(j);
                }
                if (ed.Rows.Count == 0) mod.Ops.RemoveAt(i);
            }
            return n;
        }

        /// <summary>How many of the rows the mod has as Put wrote them (the attribute set to the source text).</summary>
        public int InMod(Mod mod, IEnumerable<TextRow> rows)
        {
            var sets = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (TextRow t in rows)
            {
                Dictionary<string, string> d;
                if (!sets.TryGetValue(t.Entry, out d)) sets[t.Entry] = d = ModSets(mod, Target, t.Entry);
                string v;
                if (d.TryGetValue(t.At + "\n" + t.Attr, out v) && v == t.Source) n++;
            }
            return n;
        }
    }
}
