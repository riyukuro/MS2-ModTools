// The checks of an NPC's talk, the same in the Dialogue view (TalkEditor.Problems) and in the plan (PlannerNpcTalk.cs):
//   - a jump (goto, gotoFail, gotoConditionTalkID; a comma list of step ids) to an id no step of the NPC has: error
//   - two steps with one key (kind, id, feature, locale): error
//   - a script no step jumps to that is no random pick (randomPick="1"): unreachable, a warning. Select, monologue and
//     job steps are where the client starts, as are the random picks
//   - a buttonSet the client does not show (it returns a non-zero buttonSet as it is; the values it computes itself and
//     the ones the shipped talks use are known): warning
//   - a $script:N$ with no row in a language the target has talk texts in: warning
// Shipped talks have some of these (the Korean client's file has jumps to ids no step has); a problem the shipped
// talk of the NPC on that target has too is not the mod's doing. The plan leaves those out, the view shows them dim.
using System;
using System.Collections.Generic;
using System.Globalization;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public sealed class TalkProblem
    {
        public bool Error;
        public string Archive = "", Text = "";
        /// <summary>The shipped talk of the NPC on that target has the same problem.</summary>
        public bool Shipped;

        public override string ToString() { return (Error ? "error: " : "warning: ") + Text; }
    }

    public static class TalkCheck
    {
        public static readonly string[] Jumps = { "goto", "gotoFail", "gotoConditionTalkID" };
        /// <summary>The button sets the client knows: the ones it computes and the ones the shipped talks set.</summary>
        public static readonly int[] Buttons = { 1, 3, 4, 5, 6, 7, 8, 9, 16, 17, 19 };

        /// <summary>The step ids a jump value names.</summary>
        public static List<string> Targets(string value)
        {
            var l = new List<string>();
            if (value == null) return l;
            foreach (string s in value.Split(',')) { string t = s.Trim(); if (t.Length > 0) l.Add(t); }
            return l;
        }

        /// <summary>
        /// The problems of the steps under root (an &lt;npc&gt; or a talk file's root), each an error flag and a text.
        /// texts: the text ids each language of the target has (null: the texts are not checked).
        /// </summary>
        public static List<KeyValuePair<bool, string>> Of(XmlElem root, IDictionary<string, HashSet<long>> texts)
        {
            var l = new List<KeyValuePair<bool, string>>();
            if (root == null) return l;
            var ids = new HashSet<string>();
            var keys = new HashSet<string>();
            var targets = new HashSet<string>();
            foreach (XmlElem s in root.Children)
            {
                if (!TalkEditor.IsStep(s)) continue;
                ids.Add(s.Attr("id") ?? "");
                string key = new TalkStep(s).Key;
                if (!keys.Add(key)) l.Add(new KeyValuePair<bool, string>(true, "two steps are " + key));
            }
            foreach (XmlElem s in root.Children)
            {
                if (!TalkEditor.IsStep(s)) continue;
                Walk(root, s, ids, targets, texts, l);
            }
            foreach (XmlElem s in root.Children)
            {
                if (s.Name != "script" || s.Attr("randomPick") == "1" || targets.Contains(s.Attr("id") ?? "")) continue;
                l.Add(new KeyValuePair<bool, string>(false, new TalkStep(s).Key + " is reached from no step: no jump names it and it is no random pick"));
            }
            return l;
        }

        static void Walk(XmlElem root, XmlElem e, HashSet<string> ids, HashSet<string> targets, IDictionary<string, HashSet<long>> texts, List<KeyValuePair<bool, string>> into)
        {
            string at = null;
            foreach (string j in Jumps)
                foreach (string t in Targets(e.Attr(j)))
                {
                    targets.Add(t);
                    if (ids.Contains(t)) continue;
                    if (at == null) at = TalkPath.Of(root, e).ToString();
                    into.Add(new KeyValuePair<bool, string>(true, at + ": " + j + " names step " + t + ", which the NPC has not"));
                }
            string bs = e.Attr("buttonSet");
            if (bs != null)
            {
                int v;
                if (!int.TryParse(bs, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v) || (v != 0 && Array.IndexOf(Buttons, v) < 0))
                {
                    if (at == null) at = TalkPath.Of(root, e).ToString();
                    into.Add(new KeyValuePair<bool, string>(false, at + ": buttonSet " + bs + " is none the client shows (known: " + string.Join(", ", Array.ConvertAll(Buttons, b => b.ToString(CultureInfo.InvariantCulture))) + ")"));
                }
            }
            long id;
            if (texts != null && TalkEditor.TryRef(e.Attr("text"), out id))
            {
                var langs = new List<string>(texts.Keys);
                langs.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string lang in langs)
                {
                    if (texts[lang].Contains(id)) continue;
                    if (at == null) at = TalkPath.Of(root, e).ToString();
                    into.Add(new KeyValuePair<bool, string>(false, at + ": talk text " + id.ToString(CultureInfo.InvariantCulture) + " has no " + lang + " row"));
                }
            }
            foreach (XmlElem c in e.Children) Walk(root, c, ids, targets, texts, into);
        }

        /// <summary>The problems of now that shipped does not have (both lists as Of gives them).</summary>
        public static List<KeyValuePair<bool, string>> New(List<KeyValuePair<bool, string>> now, List<KeyValuePair<bool, string>> shipped)
        {
            var had = new HashSet<string>();
            foreach (KeyValuePair<bool, string> p in shipped) had.Add(p.Value);
            return now.FindAll(p => !had.Contains(p.Value));
        }
    }

    public sealed partial class TalkEditor
    {
        /// <summary>The text ids each language of a target has rows for, the mod's rows in.</summary>
        public Dictionary<string, HashSet<long>> TextIds(string archive)
        {
            var d = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
            foreach (TextDoc doc in NpcTextDocs(archive))
            {
                HashSet<long> set;
                if (!d.TryGetValue(doc.Lang, out set)) d[doc.Lang] = set = new HashSet<long>();
                set.UnionWith(doc.Rows.Keys);
                EditOp ed = EditOf(archive, doc.Name, false);
                if (ed != null) foreach (RowOp r in ed.Rows) { long v = AddedId(r); if (v >= 0) set.Add(v); }
            }
            return d;
        }

        /// <summary>The steps of the NPC as the target ships them (the &lt;npc&gt; or the file's root); null when it ships none.</summary>
        public XmlElem ShippedRoot(string archive, long npc)
        {
            CombinedIndex ix = Index(archive);
            if (ix.Entries.Count > 0)
            {
                KeyValuePair<string, XmlElem> s;
                return ix.Npcs.TryGetValue(npc, out s) ? s.Value : null;
            }
            string n;
            byte[] b = files.Shipped(archive, FileEntry(npc), out n);
            if (b == null) return null;
            try { return XmlDoc.Parse(b, archive + " " + n).Root; }
            catch (ModKitException) { return null; }
        }

        /// <summary>The problems of the NPC's talk on every side, the ones the shipped talk has too marked Shipped;
        /// last a warning per pair of sides whose steps differ in text.</summary>
        public List<TalkProblem> Problems(NpcTalk t) { return Problems(t, TextIds); }

        List<TalkProblem> Problems(NpcTalk t, Func<string, Dictionary<string, HashSet<long>>> textIds)
        {
            var l = new List<TalkProblem>();
            foreach (TalkSide s in t.Sides)
            {
                Dictionary<string, HashSet<long>> texts = textIds(s.Archive);
                var had = new HashSet<string>();
                XmlElem shipped = s.InMod ? ShippedRoot(s.Archive, t.NpcId) : s.Root;
                if (shipped != null)
                    foreach (KeyValuePair<bool, string> p in TalkCheck.Of(shipped, texts)) had.Add(p.Value);
                foreach (KeyValuePair<bool, string> p in TalkCheck.Of(s.Root, texts))
                    l.Add(new TalkProblem { Error = p.Key, Archive = s.Archive, Text = p.Value, Shipped = had.Contains(p.Value) });
            }
            foreach (string key in t.Keys)
            {
                TalkSide first = null;
                foreach (TalkSide s in t.Sides)
                {
                    TalkStep st = s.Step(key);
                    if (st == null) continue;
                    if (first == null) { first = s; continue; }
                    if (!SameStep(first.Step(key).Elem, st.Elem))
                        l.Add(new TalkProblem { Archive = s.Archive, Text = key + " differs from the " + first.Profile.Name + "'s", Shipped = !first.InMod && !s.InMod });
                }
            }
            return l;
        }

        // the same step on two sides: the same names, attributes and children, whatever the spacing
        static bool SameStep(XmlElem a, XmlElem b)
        {
            if (a.Name != b.Name || a.Attrs.Count != b.Attrs.Count || a.Children.Count != b.Children.Count) return false;
            foreach (XmlAttr x in a.Attrs) if (b.Attr(x.Name) != x.Value) return false;
            for (int i = 0; i < a.Children.Count; i++) if (!SameStep(a.Children[i], b.Children[i])) return false;
            return true;
        }
    }
}
