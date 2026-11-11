// A talk script names its texts as $script:N$, N being a row of the target's talk text files
// (string/<language>/scriptquest*.xml, <key id name>). After the merge of all mods, a name a mod has brought into a
// talk script (a new row, a changed or added text attribute, a whole file) whose N has no row in any language of
// that target is a warning: the game would show nothing, or the key. A name the same quest and script have as
// shipped is not looked at, so the unmodded data gives no warning; nor is a name a clone (an added row that copies
// quest X under a new id) has in the script where X has it as shipped, so the clone of a quest that speaks a text
// without a row is as quiet as its source. The same text moved to another quest or script by hand is a warning.
//
// The mirror (PlannerMirror.cs) of a line about talk texts keeps to what the other side has: a text row that copies
// a row the other side does not ship is not added there, and a script row keeps the name the other side ships at a
// place where the line would write a name that has no row there, when the shipped name has no row there either
// (the other client never had a text for it; its copy of a clone says what its copy of the source says). Both are
// notes. A name without a row at a place where the other side ships a text stays, and is the warning above.
using System;
using System.Collections.Generic;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        // every $script:N$ under a row: "quest id \n script id \n N" (the same name twice in one script counts once)
        static void TalkNames(XmlElem e, string quest, string script, List<string> into, List<long> ids, HashSet<string> seen)
        {
            if (e.Name == "quest" && quest == null) quest = e.Attr("id") ?? "";
            else if (e.Name == "script") script = e.Attr("id") ?? "";
            long id;
            if (quest != null && QuestEditor.TryTalkRef(e.Attr("text"), out id))
            {
                string key = quest + "\n" + (script ?? "") + "\n" + id;
                if (seen.Add(key)) { into.Add(key); ids.Add(id); }
            }
            foreach (XmlElem c in e.Children) TalkNames(c, quest, script, into, ids, seen);
        }

        // the text ids a target has a row for after the plan, over every language
        static HashSet<long> TalkTextIds(Run run, string archive)
        {
            var set = new HashSet<long>();
            M2dArchive arch = run.Data.Open(archive);
            var docs = new List<byte[]>();
            foreach (M2dEntry e in arch.Entries)
                if (QuestEditor.TalkRx.IsMatch(e.Name.Replace('\\', '/'))) docs.Add(PlannedBytes(run, archive, arch, e));
            foreach (EntryState st in run.Order)
                if (st.Archive == archive && !st.Existed && QuestEditor.TalkRx.IsMatch(st.Name.Replace('\\', '/'))) docs.Add(PlannedBytes(st));
            foreach (byte[] b in docs)
            {
                if (b == null) continue;
                XmlDoc doc;
                try { doc = XmlDoc.Parse(b, archive + " talk texts"); }
                catch (ModKitException) { continue; }
                if (doc.Root == null) continue;
                foreach (XmlElem k in doc.Root.Children)
                {
                    long id;
                    if (k.Name == "key" && QuestEditor.TryId(k.Attr("id") ?? "", out id)) set.Add(id);
                }
            }
            return set;
        }

        // the mod that sets the name: the highest one with an edit line of a talk script holding it
        static Mod TalkNameMod(Run run, long id)
        {
            string name = "$script:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "$";
            for (int i = run.Plan.Mods.Count - 1; i >= 0; i--)
                foreach (ModOp op in run.Plan.Mods[i].Ops)
                {
                    var ed = op as EditOp;
                    if (ed == null || !QuestEditor.IsTalkScript(ed.Entry)) continue;
                    foreach (RowOp r in ed.Rows)
                        foreach (AttrOp a in r.Attrs) if (a.Value == name) return run.Plan.Mods[i];
                }
            return null;
        }

        // the clones in the talk scripts of all mods: new quest id -> the quest id its row copies. Not per target:
        // a quest has one id everywhere, and the rows the plan mirrors to the other client have no line of their own
        static Dictionary<string, string> TalkClones(Run run)
        {
            var d = new Dictionary<string, string>();
            foreach (Mod m in run.Plan.Mods)
                foreach (ModOp op in m.Ops)
                {
                    var ed = op as EditOp;
                    if (ed == null || !QuestEditor.IsTalkScript(ed.Entry)) continue;
                    foreach (RowOp r in ed.Rows)
                    {
                        const string head = "quest[id=";
                        if (r.Kind != RowOpKind.Add || r.Copy == null || !r.Copy.StartsWith(head, StringComparison.Ordinal) || !r.Copy.EndsWith("]", StringComparison.Ordinal)) continue;
                        AttrOp id = r.Attrs.Find(delegate(AttrOp a) { return a.Name == "id" && string.IsNullOrEmpty(a.At) && a.Value != null; });
                        if (id != null) d[id.Value] = r.Copy.Substring(head.Length, r.Copy.Length - head.Length - 1);
                    }
                }
            return d;
        }

        // the name as its quest's source has it: the quest id replaced by the id of the quest it was copied from;
        // null when the quest is not a clone
        static string TalkSourceKey(Dictionary<string, string> clones, string key)
        {
            int cut = key.IndexOf('\n');
            string quest = key.Substring(0, cut), from;
            if (!clones.TryGetValue(quest, out from)) return null;
            return from + key.Substring(cut);
        }

        // ---- the mirror ----

        static readonly System.Text.RegularExpressions.Regex KeyIdRx = new System.Text.RegularExpressions.Regex("^<key\\b[^>]*?\\sid=\"(\\d+)\"", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        static bool AddedTalkId(RowOp r, out long id)
        {
            id = 0;
            if (r.Kind != RowOpKind.Add) return false;
            AttrOp a = r.Attrs.FindLast(delegate(AttrOp x) { return x.Kind == AttrOpKind.Set && x.Name == "id" && string.IsNullOrEmpty(x.At); });
            if (a != null) return QuestEditor.TryId(a.Value ?? "", out id);
            if (r.Xml == null) return false;
            System.Text.RegularExpressions.Match m = KeyIdRx.Match(r.Xml.TrimStart());
            return m.Success && QuestEditor.TryId(m.Groups[1].Value, out id);
        }

        // the root of an entry as the other side has it before the edits; null when it has none or it does not parse
        static XmlElem MirrorRoot(Run run, M2dArchive arch, string archive, string entry)
        {
            EntryState st;
            byte[] b = null;
            if (run.States.TryGetValue(Key(archive, entry), out st)) b = st.Base;
            if (b == null) { M2dEntry e = arch.Find(entry); if (e != null) b = arch.Read(e); }
            if (b == null) return null;
            try { return XmlDoc.Parse(b, archive + " " + entry).Root; }
            catch (ModKitException) { return null; }
        }

        // the text ids the archive ships a row for, kept with the data set
        static HashSet<long> ShippedTalkIds(Run run, M2dArchive arch, string archive)
        {
            string key = "talk text ids\n" + archive;
            object o;
            if (run.Data.Cache.TryGetValue(key, out o)) return (HashSet<long>)o;
            var set = new HashSet<long>();
            foreach (M2dEntry e in arch.Entries)
            {
                if (!QuestEditor.TalkRx.IsMatch(e.Name.Replace('\\', '/'))) continue;
                XmlDoc doc;
                try { doc = XmlDoc.Parse(arch.Read(e), archive + " " + e.Name); }
                catch (ModKitException) { continue; }
                if (doc.Root == null) continue;
                foreach (XmlElem k in doc.Root.Children)
                {
                    long id;
                    if (k.Name == "key" && QuestEditor.TryId(k.Attr("id") ?? "", out id)) set.Add(id);
                }
            }
            run.Data.Cache[key] = set;
            return set;
        }

        // the text ids the other side has a row for once the mods are in: the ones it ships, the ones a mod adds
        // there itself, and the ones the mirror brings (a row in a file it ships, copied from a row it ships)
        static HashSet<long> MirrorTalkIds(Run run, M2dArchive arch, string archive)
        {
            HashSet<long> set;
            if (run.MirrorTalk.TryGetValue(archive, out set)) return set;
            set = new HashSet<long>(ShippedTalkIds(run, arch, archive));
            Profile to = Profile.OfArchive(archive);
            var roots = new Dictionary<string, XmlElem>(StringComparer.OrdinalIgnoreCase);
            foreach (Mod m in run.Plan.Mods)
                foreach (ModOp op in m.Ops)
                {
                    var ed = op as EditOp;
                    if (ed == null || !QuestEditor.TalkRx.IsMatch(ed.Entry.Replace('\\', '/'))) continue;
                    bool own = op.Archive == archive;
                    if (!own)
                    {
                        Profile from = Profile.OfArchive(op.Archive);
                        if (from == null || to == null || !from.Mirrors(to) || to.ArchiveIn(op.Archive) != archive) continue;
                    }
                    XmlElem root = null;
                    if (!own && !roots.TryGetValue(ed.Entry, out root)) roots[ed.Entry] = root = MirrorRoot(run, arch, archive, ed.Entry);
                    if (!own && root == null) continue;
                    foreach (RowOp r in ed.Rows)
                    {
                        long id;
                        string err;
                        if (!AddedTalkId(r, out id)) continue;
                        if (own || r.Copy == null || RowPath.Parse(r.Copy).TryResolve(root, out err) != null) set.Add(id);
                    }
                }
            run.MirrorTalk[archive] = set;
            return set;
        }

        static RowOp CopyOf(RowOp r)
        {
            var n = new RowOp { Kind = r.Kind, At = r.At, Copy = r.Copy, Xml = r.Xml, After = r.After, Before = r.Before, In = r.In, Sync = r.Sync, Line = r.Line,
                Grouped = r.Grouped, FromIndent = r.FromIndent, NoNewId = r.NoNewId };
            n.Attrs.AddRange(r.Attrs);
            return n;
        }

        /// <summary>A mirrored line of a talk text file or a talk script, cut to what the other side has (see the head of this file).</summary>
        static EditOp MirrorTalk(Run run, MirrorOp mo, M2dArchive arch, EditOp ed)
        {
            bool texts = QuestEditor.TalkRx.IsMatch(ed.Entry.Replace('\\', '/'));
            if (!texts && !QuestEditor.IsTalkScript(ed.Entry)) return ed;
            XmlElem root = MirrorRoot(run, arch, ed.Archive, ed.Entry);
            if (root == null) return ed;
            var rows = new List<RowOp>();
            int rowsOut = 0, namesOut = 0;
            HashSet<long> have = null;
            foreach (RowOp r in ed.Rows)
            {
                string err;
                if (texts)
                {
                    if (r.Kind == RowOpKind.Add && r.Copy != null && RowPath.Parse(r.Copy).TryResolve(root, out err) == null) rowsOut++;
                    else rows.Add(r);
                    continue;
                }
                string at = r.Kind == RowOpKind.Add ? r.Copy : r.Kind == RowOpKind.Change ? r.At : null;
                XmlElem row = string.IsNullOrEmpty(at) ? null : RowPath.Parse(at).TryResolve(root, out err);
                RowOp nr = r;
                if (row != null)
                    foreach (AttrOp a in r.Attrs)
                    {
                        long to, was;
                        if (a.Kind != AttrOpKind.Set || a.Name != "text" || string.IsNullOrEmpty(a.At) || !QuestEditor.TryTalkRef(a.Value, out to)) continue;
                        XmlElem el = RowPath.Parse(a.At).TryResolve(row, out err);
                        if (el == null || !QuestEditor.TryTalkRef(el.Attr("text"), out was) || was == to) continue;
                        if (have == null) have = MirrorTalkIds(run, arch, ed.Archive);
                        if (have.Contains(to) || have.Contains(was)) continue;
                        if (nr == r) nr = CopyOf(r);
                        nr.Attrs.Remove(a);
                        namesOut++;
                    }
                rows.Add(nr);
            }
            if (rowsOut == 0 && namesOut == 0) return ed;
            string name = mo.To.Name;
            if (rowsOut > 0)
                run.Plan.Msg(run.Plan.Notes, mo.Mod, ed.Archive, ed.Entry, "not mirrored from " + mo.From + ": " + rowsOut + " new text row(s) copy rows the " + name + " does not ship; it gets no row for them");
            if (namesOut > 0)
                run.Plan.Msg(run.Plan.Notes, mo.Mod, ed.Archive, ed.Entry, "not mirrored from " + mo.From + ": " + namesOut + " talk text name(s) stay as the " + name +
                    " ships them; it has no row for the new text nor for the one it names there now");
            var cut = new EditOp { Archive = ed.Archive, Line = ed.Line, Entry = ed.Entry };
            cut.Rows.AddRange(rows);
            return cut;
        }

        static void TalkNameCheck(Run run)
        {
            var texts = new Dictionary<string, HashSet<long>>();
            Dictionary<string, string> clones = null;
            foreach (EntryState st in run.Order)
            {
                if (st.DeletedBy != null || st.Final == null || !QuestEditor.IsTalkScript(st.Name)) continue;
                if (st.Existed && M2dWriter.BytesEqual(st.Final, st.Original)) continue;
                XmlDoc now, was = null;
                try
                {
                    now = XmlDoc.Parse(st.Final, st.Archive + " " + st.Name);
                    if (st.Existed) was = XmlDoc.Parse(st.Original, st.Archive + " " + st.Name);
                }
                catch (ModKitException) { continue; }
                if (now.Root == null) continue;
                var names = new List<string>();
                var ids = new List<long>();
                var shipped = new List<string>();
                var shippedIds = new List<long>();
                var shippedSet = new HashSet<string>();
                TalkNames(now.Root, null, null, names, ids, new HashSet<string>());
                if (was != null && was.Root != null) TalkNames(was.Root, null, null, shipped, shippedIds, shippedSet);
                for (int i = 0; i < names.Count; i++)
                {
                    if (shippedSet.Contains(names[i])) continue;       // as shipped: not a mod's doing
                    if (shippedSet.Count > 0)
                    {
                        if (clones == null) clones = TalkClones(run);
                        string src = TalkSourceKey(clones, names[i]);
                        if (src != null && shippedSet.Contains(src)) continue;      // a clone that says what its source says
                    }
                    HashSet<long> have;
                    if (!texts.TryGetValue(st.Archive, out have))
                    {
                        try { have = TalkTextIds(run, st.Archive); }
                        catch (ModKitException) { have = null; }
                        texts[st.Archive] = have;
                    }
                    if (have == null || have.Contains(ids[i])) continue;
                    string[] at = names[i].Split('\n');
                    Profile target = Profile.OfArchive(st.Archive);
                    run.Plan.Msg(run.Plan.Warnings, TalkNameMod(run, ids[i]), st.Archive, st.Name,
                        "quest " + at[0] + (at[1].Length > 0 ? ", script " + at[1] : "") + " names talk text " + at[2] + ", and " + (target != null ? "the " + target.Name : "this target") +
                        " has no row for it in any language (string/<language>/scriptquest*.xml); the game has no text to show there");
                }
            }
        }
    }
}
