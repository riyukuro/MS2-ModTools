// A talk script names its texts as $script:N$, N being a row of the target's talk text files
// (string/<language>/scriptquest*.xml, <key id name>). After the merge of all mods, a name a mod has brought into a
// talk script (a new row, a changed or added text attribute, a whole file) whose N has no row in any language of
// that target is a warning: the game would show nothing, or the key. A name the same quest and script have as
// shipped is not looked at, so the unmodded data gives no warning; nor is a name a clone (an added row that copies
// quest X under a new id) has in the script where X has it as shipped, so the clone of a quest that speaks a text
// without a row is as quiet as its source. The same text moved to another quest or script by hand is a warning.
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
