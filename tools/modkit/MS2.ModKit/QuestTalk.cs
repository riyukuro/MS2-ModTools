// The talk texts of a quest: what its talk script says.
//
// A <quest id> row of script/quest/questscript*.xml names its lines by text="$script:<text id>$" on its <content>
// and <distractor> elements. The texts are <key id name> rows of string/<lang>/scriptquest*.xml, keyed by the text
// id; a text id may have a row per feature, and a language may lack a row.
//
// A cloned quest copies the script row, so it speaks the source's lines. OwnTalkTexts gives the clone text rows of
// its own: every text the copied script names gets a new text id (<new quest id>001 onward, skipping an id the data
// or the mod already has), the text rows of the source are copied under the new id in every language of every
// target, and the copied script row names the new ids. The new ids are recorded as id ranges of the mod, each with
// the targets its text has rows on.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>One line of a quest's talk script: a text id the script names, and its text.</summary>
    public sealed class QuestTalkLine
    {
        public long TextId;
        /// <summary>The id of the script that names the text first, and the element that names it (content, distractor).</summary>
        public string Script = "", Element = "";
        /// <summary>True when the text rows are rows the mod adds for this quest (they can be edited).</summary>
        public bool Own;
        /// <summary>The text in the editor's language, else in the first language that has a row; null without any row.</summary>
        public string Text;
        public string Language;
        /// <summary>The number of text rows, over every language and target.</summary>
        public int Rows;
        public readonly List<string> Languages = new List<string>();
    }

    /// <summary>A talk text in one language and for one feature: its rows over the targets.</summary>
    public sealed class QuestTalkText
    {
        public long TextId;
        public string Language = "", Feature = "";
        /// <summary>The text of the first row, the number of rows and whether they differ.</summary>
        public string Text = "";
        public int Rows;
        public bool Differs;
        /// <summary>The text of the row the first row is a copy of (a quest's own text), else the row's own text.</summary>
        public string Source = "";
        public bool Own;
    }

    public sealed partial class QuestEditor
    {
        public const string TalkIdSpace = "questtalk";
        /// <summary>The most texts one quest can own: its text ids are the quest id followed by 001..999.</summary>
        public const int MaxOwnTalkTexts = 999;
        const string TalkEntries = "string/*/scriptquest*.xml";
        static readonly Regex TalkRx = new Regex(@"^string/([^/]+)/scriptquest[^/]*\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex TalkRefRx = new Regex(@"^\$script:(\d+)\$$", RegexOptions.CultureInvariant);

        static string TalkRefOf(long textId) { return "$script:" + S(textId) + "$"; }

        static bool TryTalkRef(string text, out long textId)
        {
            textId = 0;
            Match m = text == null ? Match.Empty : TalkRefRx.Match(text);
            return m.Success && TryId(m.Groups[1].Value, out textId);
        }

        // every scriptquest file of an archive, in every language, with its rows by text id
        List<StringDoc> TalkStrings(string archive)
        {
            string key = "questtalk\n" + archive;
            object o;
            if (Data.Cache.TryGetValue(key, out o)) return (List<StringDoc>)o;
            var list = new List<StringDoc>();
            M2d.M2dArchive arch = Data.Open(archive);
            foreach (M2d.M2dEntry e in arch.Entries)
            {
                Match m = TalkRx.Match(e.Name.Replace('\\', '/'));
                if (!m.Success) continue;
                XmlDoc doc = XmlDoc.Parse(arch.Read(e), archive + " " + e.Name);
                if (doc.Root == null) continue;
                var sd = new StringDoc { Name = e.Name, Lang = m.Groups[1].Value, Doc = doc };
                foreach (XmlElem c in doc.Root.Children)
                {
                    long id;
                    if (c.Name != "key" || !TryId(c.Attr("id"), out id)) continue;
                    List<XmlElem> l;
                    if (!sd.Rows.TryGetValue(id, out l)) sd.Rows[id] = l = new List<XmlElem>();
                    l.Add(c);
                }
                list.Add(sd);
            }
            list.Sort(delegate(StringDoc a, StringDoc b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            Data.Cache[key] = list;
            return list;
        }

        List<string> TalkLanguagesIn(string archive)
        {
            var l = new List<string>();
            foreach (StringDoc d in TalkStrings(archive)) if (!l.Contains(d.Lang)) l.Add(d.Lang);
            return l;
        }

        // a place of a talk script row that names a text
        sealed class TalkRef
        {
            internal RowOp Op;          // the mod's copy of the script row; null for a shipped row
            internal XmlElem Elem;
            internal string At;         // Elem from the row
            internal long TextId;       // what the quest names there now
        }

        static bool IsTalkScript(string entry) { return SpecOf(entry) == Lists[0]; }

        void TalkWalk(XmlElem row, XmlElem e, RowOp op, List<TalkRef> into)
        {
            long id;
            if (e != row && TryTalkRef(e.Attr("text"), out id))
            {
                var r = new TalkRef { Op = op, Elem = e, At = RowPath.Canonical(e).Substring(RowPath.Canonical(row).Length + 1), TextId = id };
                if (op != null)
                    foreach (AttrOp a in op.Attrs)
                    {
                        long now;
                        if (a.Kind == AttrOpKind.Set && a.Name == "text" && a.At == r.At && TryTalkRef(a.Value, out now)) r.TextId = now;
                    }
                into.Add(r);
            }
            foreach (XmlElem c in e.Children) TalkWalk(row, c, op, into);
        }

        List<TalkRef> TalkRefs(long id)
        {
            var l = new List<TalkRef>();
            if (AddedIds().Contains(id))
            {
                foreach (KeyValuePair<EditOp, RowOp> kv in ListedOps(id))
                {
                    if (!IsTalkScript(kv.Key.Entry)) continue;
                    DocInfo doc = Doc(kv.Key.Archive, kv.Key.Entry);
                    if (doc == null) continue;
                    string err;
                    XmlElem row = RowPath.Parse(kv.Value.Copy).TryResolve(doc.Doc.Root, out err);
                    if (row != null) TalkWalk(row, row, kv.Value, l);
                }
                return l;
            }
            foreach (string archive in Sides())
                foreach (ListDoc ld in ListDocs(archive))
                {
                    List<XmlElem> rows;
                    if (!IsTalkScript(ld.Name) || !ld.Rows.TryGetValue(id, out rows)) continue;
                    foreach (XmlElem row in rows) TalkWalk(row, row, null, l);
                }
            return l;
        }

        // a text row: a shipped one, or the mod's copy of one under a new text id
        sealed class TalkRow
        {
            internal string Archive, Entry, Lang;
            internal XmlElem Source;
            internal RowOp Add;
            internal string Text { get { return (Add != null ? KeyValue(Add, "name") : null) ?? Source.Attr("name") ?? ""; } }
            internal string Feature { get { return Source.Attr("feature") ?? ""; } }
        }

        List<TalkRow> TalkRows(long textId)
        {
            var l = new List<TalkRow>();
            foreach (string archive in Sides())
                foreach (StringDoc sd in TalkStrings(archive))
                {
                    foreach (EditOp ed in Edits(archive, sd.Name))
                        foreach (RowOp r in ed.Rows)
                        {
                            long v;
                            if (r.Kind != RowOpKind.Add || r.Copy == null || !TryId(KeyValue(r, "id"), out v) || v != textId) continue;
                            string err;
                            XmlElem src = RowPath.Parse(r.Copy).TryResolve(sd.Doc.Root, out err);
                            if (src != null) l.Add(new TalkRow { Archive = archive, Entry = sd.Name, Lang = sd.Lang, Source = src, Add = r });
                        }
                    List<XmlElem> rows;
                    if (sd.Rows.TryGetValue(textId, out rows))
                        foreach (XmlElem r in rows) l.Add(new TalkRow { Archive = archive, Entry = sd.Name, Lang = sd.Lang, Source = r });
                }
            return l;
        }

        // the text ids that are taken: the ones the data ships, the ones this mod and the other enabled mods add
        // rows for, and the ones they reserve
        HashSet<long> TalkIdsUsed(long from, long to)
        {
            var used = new HashSet<long>();
            foreach (string archive in Sides())
                foreach (StringDoc sd in TalkStrings(archive))
                    foreach (long id in sd.Rows.Keys) used.Add(id);
            var mods = new List<Mod> { Mod };
            mods.AddRange(OtherMods());
            foreach (Mod m in mods)
            {
                foreach (IdRange r in m.Ids)
                    if (r.Space == TalkIdSpace)
                        for (long v = Math.Max(r.From, from); v <= Math.Min(r.To, to); v++) used.Add(v);
                foreach (ModOp op in m.Ops)
                {
                    var ed = op as EditOp;
                    if (ed == null || !TalkRx.IsMatch(ed.Entry.Replace('\\', '/'))) continue;
                    foreach (RowOp r in ed.Rows)
                    {
                        long v;
                        if (r.Kind == RowOpKind.Add && TryId(KeyValue(r, "id"), out v)) used.Add(v);
                    }
                }
            }
            return used;
        }

        // a quest's own text ids are the quest id followed by 001..999
        static bool InOwnRange(long id, long textId) { return textId > id * 1000 && textId <= id * 1000 + MaxOwnTalkTexts; }

        /// <summary>
        /// The lines of a quest's talk script, in the order the script names them; a text id named twice is one
        /// line. Empty for a quest without a talk script (and for a clone made without it).
        /// </summary>
        public List<QuestTalkLine> TalkLines(long id)
        {
            var l = new List<QuestTalkLine>();
            var seen = new HashSet<long>();
            foreach (TalkRef r in TalkRefs(id))
            {
                if (!seen.Add(r.TextId)) continue;
                var line = new QuestTalkLine { TextId = r.TextId, Element = r.Elem.Name };
                for (XmlElem e = r.Elem; e != null; e = e.Parent)
                    if (e.Name == "script") { line.Script = e.Attr("id") ?? ""; break; }
                foreach (TalkRow t in TalkRows(r.TextId))
                {
                    line.Rows++;
                    if (t.Add != null && InOwnRange(id, r.TextId)) line.Own = true;
                    if (!line.Languages.Contains(t.Lang)) line.Languages.Add(t.Lang);
                    bool mine = string.Equals(t.Lang, Language, StringComparison.OrdinalIgnoreCase);
                    if (line.Text == null || (mine && !string.Equals(line.Language, Language, StringComparison.OrdinalIgnoreCase))) { line.Text = t.Text; line.Language = t.Lang; }
                }
                l.Add(line);
            }
            return l;
        }

        /// <summary>
        /// A talk text in one language, one entry per feature its rows have (in file order; "" = no feature).
        /// Empty when the language has no row.
        /// </summary>
        public List<QuestTalkText> TalkTexts(long textId, string language)
        {
            var l = new List<QuestTalkText>();
            foreach (TalkRow t in TalkRows(textId))
            {
                if (!string.Equals(t.Lang, language, StringComparison.OrdinalIgnoreCase)) continue;
                string feature = t.Feature;
                QuestTalkText e = l.Find(delegate(QuestTalkText x) { return x.Feature == feature; });
                if (e == null) l.Add(e = new QuestTalkText { TextId = textId, Language = t.Lang, Feature = feature, Text = t.Text, Source = t.Source.Attr("name") ?? "", Own = t.Add != null });
                else if (t.Text != e.Text) e.Differs = true;
                e.Rows++;
            }
            return l;
        }

        /// <summary>The languages the data has talk text files in, over the editor's targets.</summary>
        public List<string> TalkLanguages()
        {
            var l = new List<string>();
            foreach (string a in Sides())
                foreach (string lang in TalkLanguagesIn(a)) if (!l.Contains(lang)) l.Add(lang);
            return l;
        }

        /// <summary>The number of talk texts a new quest has rows of its own for.</summary>
        public int OwnTalkTextCount(long id)
        {
            int n = 0;
            foreach (QuestTalkLine t in TalkLines(id)) if (t.Own) n++;
            return n;
        }

        /// <summary>
        /// Gives a new quest talk texts of its own: every text its copied talk script names and the data has a row
        /// for is copied under a new text id, in every language of every target, and the script names the new id.
        /// Returns the number of texts copied (0: the quest has no talk script, or its texts are its own already).
        /// </summary>
        public int OwnTalkTexts(long id)
        {
            if (!AddedIds().Contains(id)) throw new ModKitException("quest " + id + " is a shipped quest; only a quest the mod adds gets talk texts of its own");
            // first what to copy and under which ids, so that nothing is added when the ids run out
            List<TalkRef> refs = TalkRefs(id);
            var map = new Dictionary<long, long>();
            var copies = new List<KeyValuePair<long, List<TalkRow>>>();
            HashSet<long> used = null;
            long first = id * 1000 + 1, last = id * 1000 + MaxOwnTalkTexts, next = id * 1000;
            foreach (TalkRef r in refs)
            {
                if (map.ContainsKey(r.TextId)) continue;
                List<TalkRow> rows = TalkRows(r.TextId);
                if (rows.Count == 0 || rows.Exists(delegate(TalkRow t) { return t.Add != null; })) continue;
                if (used == null) used = TalkIdsUsed(first, last);
                do
                {
                    next++;
                    if (next > last) throw new ModKitException("quest " + id + " has more than " + MaxOwnTalkTexts + " talk texts, or the text ids " + first + " to " + last + " are taken; nothing was copied");
                } while (used.Contains(next));
                map[r.TextId] = next;
                copies.Add(new KeyValuePair<long, List<TalkRow>>(next, rows));
            }
            foreach (KeyValuePair<long, List<TalkRow>> c in copies)
            {
                var targets = new List<string>();
                foreach (TalkRow t in c.Value)
                {
                    var add = new RowOp { Kind = RowOpKind.Add, Copy = RowPath.Canonical(t.Source) };
                    add.Attrs.Add(new AttrOp { Kind = AttrOpKind.Set, Name = "id", Value = S(c.Key) });
                    Edit(t.Archive, t.Entry).Rows.Add(add);
                    if (!targets.Contains(t.Archive)) targets.Add(t.Archive);
                }
                ReserveTalk(c.Key, targets);
            }
            foreach (TalkRef r in refs)
            {
                long to;
                if (!map.TryGetValue(r.TextId, out to)) continue;
                r.Op.Attrs.RemoveAll(delegate(AttrOp a) { return a.Kind == AttrOpKind.Set && a.Name == "text" && a.At == r.At; });
                r.Op.Attrs.Add(new AttrOp { Kind = AttrOpKind.Set, Name = "text", Value = TalkRefOf(to), At = r.At });
            }
            return map.Count;
        }

        // records a new text id with the targets it has rows on: a text the kms-client lacks is reserved on the
        // server alone, so the plan does not miss it there
        void ReserveTalk(long textId, List<string> targets)
        {
            foreach (IdRange r in Mod.Ids)
            {
                if (r.Space != TalkIdSpace || r.Places.Count != targets.Count) continue;
                bool same = true;
                for (int i = 0; i < targets.Count; i++) if (r.Places[i].Archive != targets[i]) same = false;
                if (!same) continue;
                if (textId >= r.From && textId <= r.To) return;
                if (r.To + 1 == textId) { r.To = textId; return; }
                if (r.From - 1 == textId) { r.From = textId; return; }
            }
            var made = new IdRange { Space = TalkIdSpace, From = textId, To = textId };
            foreach (string a in targets) made.Places.Add(new IdPlace { Archive = a, Entry = TalkEntries, Rows = "key", Key = "id" });
            Mod.Ids.Add(made);
        }

        /// <summary>
        /// Sets one of a new quest's own talk texts, in the rows of one language (null: every language) and of one
        /// feature ("" = the rows without one; null: every row) on every target. Returns the number of rows.
        /// </summary>
        public int SetTalkText(long id, long textId, string language, string value, string feature = null)
        {
            if (value == null) throw new ArgumentNullException("value");
            QuestTalkLine line = TalkLines(id).Find(delegate(QuestTalkLine t) { return t.TextId == textId; });
            if (line == null) throw new ModKitException("the talk script of quest " + id + " names no text " + textId);
            if (!line.Own) throw new ModKitException("text " + textId + " is a shipped talk text that other quests may speak too; give quest " + id + " talk texts of its own first");
            int n = 0;
            foreach (TalkRow t in TalkRows(textId))
            {
                if (t.Add == null) continue;
                if (language != null && !string.Equals(t.Lang, language, StringComparison.OrdinalIgnoreCase)) continue;
                if (feature != null && t.Feature != feature) continue;
                t.Add.Attrs.RemoveAll(delegate(AttrOp a) { return a.Kind == AttrOpKind.Set && Plain(a, "name"); });
                if ((t.Source.Attr("name") ?? "") != value) t.Add.Attrs.Add(new AttrOp { Kind = AttrOpKind.Set, Name = "name", Value = value });
                n++;
            }
            return n;
        }

        /// <summary>
        /// Takes a new quest's own talk texts out of the mod again: the quest speaks the source's lines as it did
        /// right after the clone. Returns the number of texts taken out. Read from the mod alone; a text name set
        /// by hand that is not one of the quest's own ids (the quest id followed by 001..999) is left as it is.
        /// </summary>
        public int ShareTalkTexts(long id)
        {
            var ids = new HashSet<long>();
            foreach (KeyValuePair<EditOp, RowOp> kv in ListedOps(id))
            {
                if (!IsTalkScript(kv.Key.Entry)) continue;
                kv.Value.Attrs.RemoveAll(delegate(AttrOp a)
                {
                    long t;
                    if (a.Kind != AttrOpKind.Set || a.Name != "text" || string.IsNullOrEmpty(a.At) || !TryTalkRef(a.Value, out t) || !InOwnRange(id, t)) return false;
                    ids.Add(t);
                    return true;
                });
            }
            if (ids.Count == 0) return 0;
            foreach (ModOp op in Mod.Ops.ToArray())
            {
                var ed = op as EditOp;
                if (ed == null || !TalkRx.IsMatch(ed.Entry.Replace('\\', '/'))) continue;
                ed.Rows.RemoveAll(delegate(RowOp r) { long v; return r.Kind == RowOpKind.Add && r.Copy != null && TryId(KeyValue(r, "id"), out v) && ids.Contains(v); });
                if (ed.Rows.Count == 0) Mod.Ops.Remove(ed);
            }
            foreach (long t in ids) IdSpaces.Unreserve(Mod, TalkIdSpace, t);
            return ids.Count;
        }
    }
}
