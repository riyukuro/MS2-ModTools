// The texts of NPC talk: a <content> or <distractor> names its line by text="$script:<text id>$", and the line is a
// <key id name> row of string/<lang>/scriptnpc*.xml on each target (a text id may have a row per feature; the
// Korean client writes the ids without leading zeros, so they are read as numbers).
//
// A shipped text may be spoken by other NPCs too, so the first edit of a line gives it a text id of its own: the NPC
// id followed by 001..999, the first one no target ships and no enabled mod adds or reserves. The rows of the old
// id are copied under the new id in every language of every side (a language without a row gets a new row in the
// file that holds the NPC's other lines), the edited language gets the new text, and the place names the new id on
// every side. The id is recorded as an <ids space="npctalk"> range of the mod. Later edits change those rows.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>A talk text in one language: the text of its first row, the number of rows and whether they differ.</summary>
    public sealed class TalkText
    {
        public long TextId;
        public string Language = "", Text = "";
        public int Rows;
        public bool Differs;
        /// <summary>The rows are the mod's own (a text id of this NPC's own range).</summary>
        public bool Own;
    }

    public sealed partial class TalkEditor
    {
        public const string TextIdSpace = "npctalk";
        public const int MaxOwnTexts = 999;
        const string TextEntries = "string/*/scriptnpc*.xml";
        static readonly Regex TextFile = new Regex(@"^string/([^/]+)/scriptnpc[^/]*\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // ids are taken in the quest talk files too: one text id should not name two lines
        static readonly Regex AnyTalkFile = new Regex(@"^string/[^/]+/script(npc|quest)[^/]*\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex TextRef = new Regex(@"^\$script:(\d+)\$$", RegexOptions.CultureInvariant);

        /// <summary>The other enabled mods (null: none known); the text ids they add or reserve are not taken.</summary>
        public Func<IEnumerable<Mod>> Others;

        public static string RefOf(long textId) { return "$script:" + textId.ToString(CultureInfo.InvariantCulture) + "$"; }

        public static bool TryRef(string value, out long textId)
        {
            textId = 0;
            Match m = value == null ? Match.Empty : TextRef.Match(value);
            return m.Success && TryId(m.Groups[1].Value, out textId);
        }

        sealed class TextDoc
        {
            internal string Name, Lang;
            internal XmlDoc Doc;
            internal readonly Dictionary<long, List<XmlElem>> Rows = new Dictionary<long, List<XmlElem>>();
        }

        static IEnumerable<KeyValuePair<long, XmlElem>> KeyRows(XmlDoc doc)
        {
            if (doc.Root == null) yield break;
            foreach (XmlElem c in doc.Root.Children)
            {
                long id;
                if (c.Name == "key" && TryId(c.Attr("id"), out id)) yield return new KeyValuePair<long, XmlElem>(id, c);
            }
        }

        // the NPC talk text files of an archive (scriptnpc*), rows by text id, kept with the data set
        List<TextDoc> NpcTextDocs(string archive)
        {
            string key = "npctalk.text\n" + archive;
            object o;
            if (Data.Cache.TryGetValue(key, out o)) return (List<TextDoc>)o;
            var list = new List<TextDoc>();
            M2dArchive arch = Data.Open(archive);
            foreach (M2dEntry e in arch.Entries)
            {
                string n = e.Name.Replace('\\', '/');
                if (!TextFile.IsMatch(n)) continue;
                XmlDoc doc;
                try { doc = XmlDoc.Parse(arch.Read(e), archive + " " + e.Name); }
                catch (ModKitException) { continue; }
                if (doc.Root == null) continue;
                var td = new TextDoc { Name = e.Name, Lang = n.Split('/')[1], Doc = doc };
                foreach (KeyValuePair<long, XmlElem> r in KeyRows(doc))
                {
                    List<XmlElem> l;
                    if (!td.Rows.TryGetValue(r.Key, out l)) td.Rows[r.Key] = l = new List<XmlElem>();
                    l.Add(r.Value);
                }
                list.Add(td);
            }
            list.Sort(delegate(TextDoc a, TextDoc b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            Data.Cache[key] = list;
            return list;
        }

        // the text ids of the quest talk files of an archive (scriptquest*): only the ids are kept, the files are big
        HashSet<long> QuestTextIds(string archive)
        {
            string key = "npctalk.questids\n" + archive;
            object o;
            if (Data.Cache.TryGetValue(key, out o)) return (HashSet<long>)o;
            var set = new HashSet<long>();
            M2dArchive arch = Data.Open(archive);
            foreach (M2dEntry e in arch.Entries)
            {
                string n = e.Name.Replace('\\', '/');
                if (!AnyTalkFile.IsMatch(n) || TextFile.IsMatch(n)) continue;
                try { foreach (KeyValuePair<long, XmlElem> r in KeyRows(XmlDoc.Parse(arch.Read(e), archive + " " + e.Name))) set.Add(r.Key); }
                catch (ModKitException) { }
            }
            Data.Cache[key] = set;
            return set;
        }

        /// <summary>The languages the targets have NPC talk text files in.</summary>
        public List<string> TextLanguages()
        {
            var l = new List<string>();
            foreach (KeyValuePair<string, bool> a in Archives())
                foreach (TextDoc d in NpcTextDocs(a.Key)) if (!l.Contains(d.Lang)) l.Add(d.Lang);
            l.Sort(StringComparer.OrdinalIgnoreCase);
            return l;
        }

        // ---- the mod's rows ----

        static bool PlainSet(AttrOp a, string name) { return a.Kind == AttrOpKind.Set && string.IsNullOrEmpty(a.At) && a.Name == name; }

        static string LastSet(RowOp r, string name)
        {
            for (int i = r.Attrs.Count - 1; i >= 0; i--) if (PlainSet(r.Attrs[i], name)) return r.Attrs[i].Value;
            return null;
        }

        static XmlElem XmlRoot(RowOp r)
        {
            try { return XmlDoc.ParseFragment(r.Xml.Trim(), "the row").Root; }
            catch (ModKitException) { return null; }
        }

        // the text id a row the mod adds gives its key: a set of id on a copy, else the id of the literal row
        static long AddedId(RowOp r)
        {
            long id;
            if (r.Kind != RowOpKind.Add) return -1;
            string v = LastSet(r, "id");
            if (v == null && r.Copy == null && r.Xml != null) { XmlElem e = XmlRoot(r); v = e == null ? null : e.Attr("id"); }
            return v != null && TryId(v, out id) ? id : -1;
        }

        sealed class TextRow
        {
            internal string Archive, Entry, Lang;
            internal XmlElem Source;    // the shipped row (copied from, for an add)
            internal RowOp Add;         // the mod's row, or null for a shipped one
            internal string Text
            {
                get
                {
                    if (Add == null) return Source.Attr("name") ?? "";
                    string v = LastSet(Add, "name");
                    if (v != null) return v;
                    if (Source != null) return Source.Attr("name") ?? "";
                    XmlElem e = XmlRoot(Add);
                    return e == null ? "" : e.Attr("name") ?? "";
                }
            }
        }

        List<TextRow> TextRows(long textId)
        {
            var l = new List<TextRow>();
            foreach (KeyValuePair<string, bool> a in Archives())
                foreach (TextDoc d in NpcTextDocs(a.Key))
                {
                    EditOp ed = EditOf(a.Key, d.Name, false);
                    if (ed != null)
                        foreach (RowOp r in ed.Rows)
                        {
                            if (AddedId(r) != textId) continue;
                            XmlElem src = null;
                            string err;
                            if (r.Copy != null && (src = RowPath.Parse(r.Copy).TryResolve(d.Doc.Root, out err)) == null) continue;
                            l.Add(new TextRow { Archive = a.Key, Entry = d.Name, Lang = d.Lang, Source = src, Add = r });
                        }
                    List<XmlElem> rows;
                    if (d.Rows.TryGetValue(textId, out rows))
                        foreach (XmlElem r in rows) l.Add(new TextRow { Archive = a.Key, Entry = d.Name, Lang = d.Lang, Source = r });
                }
            return l;
        }

        static bool InOwnRange(long npc, long textId) { return textId > npc * 1000 && textId <= npc * 1000 + MaxOwnTexts; }

        /// <summary>A talk text in one language over every target; null when the language has no row.</summary>
        public TalkText Text(long npc, long textId, string language)
        {
            TalkText t = null;
            foreach (TextRow r in TextRows(textId))
            {
                if (!string.Equals(r.Lang, language, StringComparison.OrdinalIgnoreCase)) continue;
                string s = r.Text;
                if (t == null) t = new TalkText { TextId = textId, Language = r.Lang, Text = s, Own = r.Add != null && InOwnRange(npc, textId) };
                else if (s != t.Text) t.Differs = true;
                t.Rows++;
            }
            return t;
        }

        /// <summary>The text a value names ($script:N$) in a language, else the value itself when it names no text id.</summary>
        public string Say(long npc, string value, string language)
        {
            long id;
            if (!TryRef(value, out id)) return value ?? "";
            TalkText t = Text(npc, id, language);
            return t == null ? null : t.Text;
        }

        HashSet<long> TextIdsUsed(long from, long to)
        {
            var used = new HashSet<long>();
            foreach (KeyValuePair<string, bool> a in Archives())
            {
                foreach (TextDoc d in NpcTextDocs(a.Key))
                    foreach (long id in d.Rows.Keys) if (id >= from && id <= to) used.Add(id);
                foreach (long id in QuestTextIds(a.Key)) if (id >= from && id <= to) used.Add(id);
            }
            var mods = new List<Mod> { Mod };
            if (Others != null) mods.AddRange(Others());
            foreach (Mod m in mods)
            {
                foreach (IdRange r in m.Ids)
                    if (r.Space == TextIdSpace || r.Space == QuestEditor.TalkIdSpace)
                        for (long v = Math.Max(r.From, from); v <= Math.Min(r.To, to); v++) used.Add(v);
                foreach (ModOp op in m.Ops)
                {
                    var ed = op as EditOp;
                    if (ed == null || !AnyTalkFile.IsMatch(ed.Entry.Replace('\\', '/'))) continue;
                    foreach (RowOp r in ed.Rows) { long v = AddedId(r); if (v >= 0) used.Add(v); }
                }
            }
            return used;
        }

        // where a new row of a language goes on a side: the file that holds the NPC's other lines, else scriptnpc.xml,
        // else scriptnpcnam.xml, else the first scriptnpc file of the language
        TextDoc HomeOf(TalkSide side, string lang)
        {
            var named = new HashSet<long>();
            Walk(side.Root, named);
            TextDoc first = null, plain = null, nam = null;
            foreach (TextDoc d in NpcTextDocs(side.Archive))
            {
                if (!string.Equals(d.Lang, lang, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (long id in named) if (d.Rows.ContainsKey(id)) return d;
                string file = d.Name.Replace('\\', '/');
                file = file.Substring(file.LastIndexOf('/') + 1).ToLowerInvariant();
                if (file == "scriptnpc.xml") plain = d;
                if (file == "scriptnpcnam.xml") nam = d;
                if (first == null) first = d;
            }
            return plain ?? nam ?? first;
        }

        static void Walk(XmlElem e, HashSet<long> into)
        {
            long id;
            if (TryRef(e.Attr("text"), out id)) into.Add(id);
            foreach (XmlElem c in e.Children) Walk(c, into);
        }

        static void SetName(RowOp r, string value)
        {
            if (r.Copy != null)
            {
                r.Attrs.RemoveAll(a => PlainSet(a, "name"));
                r.Attrs.Add(new AttrOp { Kind = AttrOpKind.Set, Name = "name", Value = value });
                return;
            }
            XmlDoc d = XmlDoc.ParseFragment(r.Xml.Trim(), "the row");
            var ed = new XmlEditor(d);
            ed.SetAttr(d.Root, "name", value);
            r.Xml = ed.Apply();
        }

        /// <summary>
        /// Sets the text of a line (the text="" of a content or distractor) in one language (null: every language).
        /// A line that names a text of the NPC's own has those rows changed; any other line first gets a text id of
        /// its own (see the top of this file). Sides without the place are named in missed.
        /// </summary>
        public NpcTalk SetText(NpcTalk t, TalkPath at, string language, string value, List<string> missed)
        {
            if (value == null) throw new ArgumentNullException("value");
            if (t == null || t.Sides.Count == 0) throw new ModKitException("the NPC has no talk to edit");
            long npc = t.NpcId;
            var sides = new List<KeyValuePair<TalkSide, long>>();
            foreach (TalkSide s in t.Sides)
            {
                XmlElem e = at.Resolve(s);
                if (e == null) { if (missed != null) missed.Add(s.Archive); continue; }
                if (e.Name != "content" && e.Name != "distractor") throw new ModKitException("<" + e.Name + "> has no text line; a content or a distractor has");
                long id;
                sides.Add(new KeyValuePair<TalkSide, long>(s, TryRef(e.Attr("text"), out id) ? id : -1));
            }
            if (sides.Count == 0) throw new ModKitException("no target has " + at);

            long own = sides[0].Value;
            foreach (KeyValuePair<TalkSide, long> s in sides) if (s.Value != own) own = -1;
            if (own >= 0 && InOwnRange(npc, own))
            {
                List<TextRow> rows = TextRows(own).FindAll(r => r.Add != null);
                if (rows.Count > 0)
                {
                    foreach (TextRow r in rows)
                        if (language == null || string.Equals(r.Lang, language, StringComparison.OrdinalIgnoreCase)) SetName(r.Add, value);
                    return Open(npc);
                }
            }

            // a text id of the NPC's own: first make sure there is one, so nothing is added when they run out
            long first = npc * 1000 + 1, last = npc * 1000 + MaxOwnTexts, next = first;
            HashSet<long> used = TextIdsUsed(first, last);
            while (next <= last && used.Contains(next)) next++;
            if (next > last) throw new ModKitException("NPC " + npc + " has no free talk text id left (" + first + " to " + last + " are taken)");

            var targets = new List<string>();
            foreach (KeyValuePair<TalkSide, long> s in sides)
            {
                TalkSide side = s.Key;
                if (targets.Contains(side.Archive)) continue;
                targets.Add(side.Archive);
                var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (s.Value >= 0)
                    foreach (TextRow r in TextRows(s.Value))
                    {
                        if (r.Archive != side.Archive) continue;
                        bool mine = language == null || string.Equals(r.Lang, language, StringComparison.OrdinalIgnoreCase);
                        RowOp add;
                        if (r.Source != null && (r.Add == null || r.Add.Copy != null))
                        {
                            add = new RowOp { Kind = RowOpKind.Add, Copy = RowPath.Canonical(r.Source) };
                            add.Attrs.Add(new AttrOp { Kind = AttrOpKind.Set, Name = "id", Value = next.ToString(CultureInfo.InvariantCulture) });
                            string text = r.Text;
                            if (mine) text = value;
                            if (text != (r.Source.Attr("name") ?? "")) add.Attrs.Add(new AttrOp { Kind = AttrOpKind.Set, Name = "name", Value = text });
                        }
                        else
                        {
                            add = new RowOp { Kind = RowOpKind.Add, Xml = NewRowXml(next, mine ? value : r.Text) };
                        }
                        EditOf(side.Archive, r.Entry, true).Rows.Add(add);
                        done.Add(r.Lang);
                    }
                var langs = new List<string>();
                foreach (TextDoc d in NpcTextDocs(side.Archive)) if (!done.Contains(d.Lang) && !langs.Contains(d.Lang)) langs.Add(d.Lang);
                foreach (string lang in langs)
                {
                    TextDoc home = HomeOf(side, lang);
                    if (home != null) EditOf(side.Archive, home.Name, true).Rows.Add(new RowOp { Kind = RowOpKind.Add, Xml = NewRowXml(next, value) });
                }
            }
            IdSpaces.ReserveText(Mod, TextIdSpace, TextEntries, next, targets);
            return SetAttr(t, at, "text", RefOf(next), null);
        }

        static string NewRowXml(long id, string text)
        {
            return "<key id=\"" + id.ToString(CultureInfo.InvariantCulture) + "\" name=\"" + XmlDoc.EscapeAttr(text ?? "", '"') + "\" />";
        }
    }
}
