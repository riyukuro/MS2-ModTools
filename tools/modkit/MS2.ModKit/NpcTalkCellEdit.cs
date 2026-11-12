// An edit typed into a cell of the Dialogue view, bound to the NPC and the place it was typed at. The grid hands the
// value over while it is still busy with the cell, so the view commits the edit a moment later; by then another NPC
// may be open, or a save, plan or apply may be about to read the mod. The edit carries its NPC, so it lands on that
// NPC's talk whatever is open, and the view commits it (Take, then Commit) before it opens another NPC or lets the
// mod be read.
using System.Collections.Generic;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public sealed class TalkCellEdit
    {
        public long NpcId;
        public TalkPath Path;
        /// <summary>The new text of the line in Language; null when the edit is no text edit.</summary>
        public string Text, Language;
        /// <summary>The attributes to set, in order (a null value removes the attribute).</summary>
        public readonly List<KeyValuePair<string, string>> Attrs = new List<KeyValuePair<string, string>>();
    }

    /// <summary>The one edit of the Dialogue view's cells that is typed but not yet in the mod.</summary>
    public sealed class TalkEditQueue
    {
        TalkCellEdit pending;

        public bool Pending { get { return pending != null; } }

        /// <summary>Holds the edit; one held before is committed first by the caller (Take).</summary>
        public void Post(TalkCellEdit e) { pending = e; }

        /// <summary>The held edit, once: a second Take (the posted commit after an early one) gives null.</summary>
        public TalkCellEdit Take()
        {
            TalkCellEdit e = pending;
            pending = null;
            return e;
        }
    }

    public sealed partial class TalkEditor
    {
        /// <summary>
        /// Puts a cell edit into the mod, on its own NPC's talk: current when that is the NPC, else the talk opened anew.
        /// Null when the edit changes nothing (the value is what the place has); the talk after the edit otherwise.
        /// </summary>
        public NpcTalk Commit(NpcTalk current, TalkCellEdit e, List<string> missed, out string said)
        {
            said = null;
            if (e == null) return null;
            NpcTalk t = current != null && current.NpcId == e.NpcId ? current : Open(e.NpcId);
            XmlElem x = Shown(t, e.Path);
            if (x == null) throw new ModKitException("NPC " + e.NpcId + " has no " + e.Path + " any more");
            var changes = new List<string>();
            if (e.Text != null)
            {
                string was = x.Attr("text") == null ? "" : Say(t.NpcId, x.Attr("text"), e.Language);
                if (e.Text != was)
                {
                    if (e.Language == null) throw new ModKitException("no target has talk text files: no language to write the line in");
                    t = SetText(t, e.Path, e.Language, e.Text, missed);
                    changes.Add("the " + e.Language + " text set");
                }
            }
            // the attributes go in at once: a bad one (a name XML has no place for) leaves the mod as it was
            var set = new List<KeyValuePair<string, string>>();
            XmlElem shown = Shown(t, e.Path);
            foreach (KeyValuePair<string, string> a in e.Attrs)
            {
                if (shown != null && (shown.Attr(a.Key) ?? "") == (a.Value ?? "")) continue;
                set.Add(a);
                changes.Add(a.Key + (a.Value == null ? " removed" : " = " + a.Value));
            }
            if (set.Count > 0) t = SetAttrs(t, e.Path, set, missed);
            if (changes.Count == 0) return null;
            said = "NPC " + t.NpcId + ", " + e.Path + ": " + string.Join(", ", changes.ToArray());
            return t;
        }

        /// <summary>The place on the first side that has it (the side the Dialogue view shows); null when none has.</summary>
        public static XmlElem Shown(NpcTalk t, TalkPath p)
        {
            foreach (TalkSide s in t.Sides) { XmlElem e = p.Resolve(s); if (e != null) return e; }
            return null;
        }
    }
}
