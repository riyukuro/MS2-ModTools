// What the Dialogue view shows of a talk beside the steps: the flow from the steps the client starts on along the
// jumps, the NPCs the mod has a talk for, and the problems of many NPCs with the text ids read once per target.
//
// The flow starts at the select, monologue and job steps and at the random picks (the steps the client starts on,
// as TalkCheck counts them); below a step come the steps its jumps name, in the order of the jumps inside it. A jump
// names a step id: the script with that id, else the first step with it. A step the flow has shown already is shown
// again without its jumps; a jump to an id no step has is dangling; the scripts no jump names and that are no random
// pick come last, unreachable.
using System;
using System.Globalization;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>One line of a talk's flow.</summary>
    public sealed class TalkFlowLine
    {
        public int Depth;
        /// <summary>The step's key (TalkStep.Key); null for a dangling jump.</summary>
        public string Key;
        /// <summary>The jump that leads here ("goto", "gotoFail", "gotoConditionTalkID"; "" at the top), and the id it names.</summary>
        public string Via = "", Target = "";
        /// <summary>The page and choice the jump is on ("content 1 / distractor 2"; "" for a jump on the step).</summary>
        public string From = "";
        public bool Dangling, Unreachable, Again;

        public override string ToString()
        {
            string what = Dangling ? "step " + Target + " (no such step)" : Key + (Again ? " (again)" : "") + (Unreachable ? " (reached from no step)" : "");
            return new string(' ', Depth * 2) + (Via.Length > 0 ? Via + (From.Length > 0 ? " at " + From : "") + " -> " : "") + what;
        }
    }

    public static class TalkFlow
    {
        /// <summary>
        /// The button set the client's talk window shows for a page (a &lt;content&gt; of a step of an NPC talk file),
        /// as the number and why. The client's rule is GetTalkButtonSet (src/ms2/commonfiles/talkscript.cpp, @0x140857990)
        /// with the flags UITalkManager::FindTalkScript opens the step with: a select step with 8 (always 5, its buttonSet
        /// is not read), a job step with 1, a script step by its id. A monologue's pages are balloons (no window, 0). Where
        /// the NPC's type group (its data, not the talk) decides, the text says so. Null for an element that is no page.
        /// </summary>
        public static string Buttons(XmlElem page)
        {
            XmlElem step = page == null ? null : page.Parent;
            if (step == null || page.Name != "content" || !TalkEditor.IsStep(step)) return null;
            if (step.Name == "select") return "5 (select step)";
            if (step.Name == "monologue") return "0 (balloon)";
            int set;
            if (int.TryParse(page.Attr("buttonSet") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out set) && set != 0)
                return set.ToString(CultureInfo.InvariantCulture) + " (buttonSet)";
            bool choices = false;
            foreach (XmlElem c in page.Children) if (c.Name == "distractor") { choices = true; break; }
            XmlElem last = null;
            foreach (XmlElem c in step.Children) if (c.Name == "content") last = c;
            bool isLast = page == last;
            if (step.Name == "job")
            {
                string rest = isLast ? "the NPC's type group (last page)" : "4 (not the last page)";
                return choices ? "9 (choices) if the NPC's type group is 3, else " + rest : rest;
            }
            // a script: gotoConditionTalkID makes no page the last
            if (isLast && (step.Attr("gotoConditionTalkID") ?? "").Length > 0) isLast = false;
            if (choices) return "9 (choices)";
            return isLast ? "3 (last page)" : "4 (not the last page)";
        }

        /// <summary>The flow of the steps under root (an &lt;npc&gt; or a talk file's root).</summary>
        public static List<TalkFlowLine> Of(XmlElem root)
        {
            var l = new List<TalkFlowLine>();
            if (root == null) return l;
            var steps = new List<XmlElem>();
            foreach (XmlElem s in root.Children) if (TalkEditor.IsStep(s)) steps.Add(s);
            var shown = new HashSet<XmlElem>();
            var named = new HashSet<string>();
            foreach (XmlElem s in steps) CollectTargets(s, named);
            foreach (XmlElem s in steps)
            {
                bool entry = s.Name != "script" || s.Attr("randomPick") == "1";
                if (entry) Visit(root, steps, s, 0, "", "", "", shown, l);
            }
            // the scripts a jump names but no entry reaches (a loop of scripts) come next, then the unreachable ones
            foreach (XmlElem s in steps)
                if (!shown.Contains(s) && named.Contains(s.Attr("id") ?? "")) Visit(root, steps, s, 0, "", "", "", shown, l);
            foreach (XmlElem s in steps)
                if (!shown.Contains(s))
                {
                    int at = l.Count;
                    Visit(root, steps, s, 0, "", "", "", shown, l);
                    l[at].Unreachable = true;
                }
            return l;
        }

        static void CollectTargets(XmlElem e, HashSet<string> into)
        {
            foreach (string j in TalkCheck.Jumps) foreach (string t in TalkCheck.Targets(e.Attr(j))) into.Add(t);
            foreach (XmlElem c in e.Children) CollectTargets(c, into);
        }

        /// <summary>The step a jump to the id goes to: the script with the id, else the first step with it; null when none.</summary>
        public static XmlElem Target(IList<XmlElem> steps, string id)
        {
            XmlElem any = null;
            foreach (XmlElem s in steps)
            {
                if ((s.Attr("id") ?? "") != id) continue;
                if (s.Name == "script") return s;
                if (any == null) any = s;
            }
            return any;
        }

        static void Visit(XmlElem root, List<XmlElem> steps, XmlElem s, int depth, string via, string target, string from, HashSet<XmlElem> shown, List<TalkFlowLine> l)
        {
            var line = new TalkFlowLine { Depth = depth, Key = new TalkStep(s).Key, Via = via, Target = target, From = from };
            l.Add(line);
            if (!shown.Add(s)) { line.Again = true; return; }
            Jumps(root, steps, s, s, depth + 1, shown, l);
        }

        static void Jumps(XmlElem root, List<XmlElem> steps, XmlElem step, XmlElem e, int depth, HashSet<XmlElem> shown, List<TalkFlowLine> l)
        {
            string from = null;
            foreach (string j in TalkCheck.Jumps)
                foreach (string t in TalkCheck.Targets(e.Attr(j)))
                {
                    if (from == null) from = e == step ? "" : Place(root, e);
                    XmlElem to = Target(steps, t);
                    if (to == null) l.Add(new TalkFlowLine { Depth = depth, Via = j, Target = t, From = from, Dangling = true });
                    else Visit(root, steps, to, depth, j, t, from, shown, l);
                }
            foreach (XmlElem c in e.Children) Jumps(root, steps, step, c, depth, shown, l);
        }

        // the place of an element under its step, as TalkPath words it, without the step
        static string Place(XmlElem root, XmlElem e)
        {
            TalkPath p = TalkPath.Of(root, e);
            if (p == null) return "";
            string s = p.ToString();
            int cut = s.IndexOf(" / ", StringComparison.Ordinal);
            return cut < 0 ? "" : s.Substring(cut + 3);
        }
    }

    public sealed partial class TalkEditor
    {
        static readonly Regex TalkFileName = new Regex(@"^script/npc/(\d+)\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The NPCs whose talk the mod changes, adds or deletes on some target.</summary>
        public HashSet<long> ModNpcIds()
        {
            var set = new HashSet<long>();
            foreach (ModOp o in Mod.Ops)
            {
                long id;
                var e = o as EntryOp;
                if (e != null)
                {
                    Match m = TalkFileName.Match(e.Name.Replace('\\', '/'));
                    if (m.Success && TryId(m.Groups[1].Value, out id)) set.Add(id);
                }
                var ed = o as EditOp;
                if (ed != null && IsCombined(ed.Entry)) foreach (RowOp r in ed.Rows) if ((id = NpcOfRow(r)) >= 0) set.Add(id);
            }
            return set;
        }

        /// <summary>
        /// Problems with the text ids of each target read once into texts, for a list of many NPCs. A target's ids are
        /// kept under a stamp of the text rows the mod adds there, so a text edit (a new or dropped own text) reads them
        /// anew and the ids read before are let go.
        /// </summary>
        public List<TalkProblem> Problems(NpcTalk t, IDictionary<string, Dictionary<string, HashSet<long>>> texts)
        {
            return Problems(t, delegate(string archive)
            {
                string key = archive + "\n" + ModTextStamp(archive);
                Dictionary<string, HashSet<long>> d;
                if (texts.TryGetValue(key, out d)) return d;
                foreach (string k in new List<string>(texts.Keys))
                    if (k == archive || k.StartsWith(archive + "\n", StringComparison.Ordinal)) texts.Remove(k);
                texts[key] = d = TextIds(archive);
                return d;
            });
        }

        // what TextIds reads of the mod on a target: the ids its rows add to the talk text files, in order
        string ModTextStamp(string archive)
        {
            int rows = 0;
            long h = 17;
            foreach (ModOp o in Mod.Ops)
            {
                var ed = o as EditOp;
                if (ed == null || ed.Archive != archive || !TextFile.IsMatch(ed.Entry.Replace('\\', '/'))) continue;
                h = h * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(ed.Entry);
                foreach (RowOp r in ed.Rows) { h = h * 31 + AddedId(r); rows++; }
            }
            return rows.ToString(CultureInfo.InvariantCulture) + ":" + h.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The next step id no step of the talk has (on any side), in the id class of near: the client picks other
        /// buttons for ids of 300 and more, of 100 to 199 and the rest, so a new step keeps the class (10 to 99 for
        /// the rest). -1 when the class is full.
        /// </summary>
        public static long NextStepId(NpcTalk t, long near)
        {
            long from = near >= 300 ? 300 : near >= 100 && near < 200 ? 100 : 10, to = near >= 300 ? long.MaxValue : near >= 100 && near < 200 ? 199 : 99;
            var used = new HashSet<long>();
            foreach (TalkSide s in t.Sides)
                foreach (TalkStep st in s.Steps) { long id; if (TryId(st.Id, out id)) used.Add(id); }
            for (long id = from; id <= to; id++) if (!used.Contains(id)) return id;
            return -1;
        }

        /// <summary>The first line a step says in a language: its first content's text; "" when it has none.</summary>
        public string FirstLine(long npc, TalkStep s, string language)
        {
            foreach (XmlElem c in s.Elem.Children)
            {
                if (c.Name != "content") continue;
                return Say(npc, c.Attr("text"), language) ?? (c.Attr("text") ?? "");
            }
            return "";
        }

        /// <summary>True when every side that has the step has it the same (the attributes and children, whatever the spacing).</summary>
        public static bool SameOnEverySide(NpcTalk t, string key)
        {
            XmlElem first = null;
            foreach (TalkSide s in t.Sides)
            {
                TalkStep st = s.Step(key);
                if (st == null) continue;
                if (first == null) first = st.Elem;
                else if (!SameStep(first, st.Elem)) return false;
            }
            return true;
        }
    }
}
