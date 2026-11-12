// The plan's checks of the global drop tables (see GlobalDrops.cs): every box and item group the mods add or
// change is read as the server ends up with it. A line that is as shipped is not looked at. On top of the editor's
// checks the plan knows the final tables: an id a table then has twice, a box line for a group the final group
// table does not have (also a shipped line, when the mods take a shipped group out), an item the server does not
// have (the mods' new items count), and a new group no box names.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        // the mod a drop line is blamed on: the last mod with lines for the table, else for the other drop table
        static Mod GlobalDropMod(Run run, string entry)
        {
            Mod own = null, any = null;
            foreach (Mod m in run.Plan.Mods)
                foreach (ModOp op in m.Ops)
                {
                    var ed = op as EditOp;
                    if (ed == null || !(GlobalDropRules.IsBoxEntry(ed.Entry) || GlobalDropRules.IsSetEntry(ed.Entry))) continue;
                    any = m;
                    if (string.Equals(ed.Entry.Replace('\\', '/'), entry, StringComparison.OrdinalIgnoreCase)) own = m;
                }
            return own ?? any;
        }

        static string DropHolderText(XmlElem h)
        {
            var sb = new StringBuilder(RowText(h));
            foreach (XmlElem c in h.Children) if (c.Name == GlobalDropRules.Line) sb.Append("line\n").Append(RowText(c));
            return sb.ToString();
        }

        static void GlobalDropChecks(Run run)
        {
            if (GlobalDropMod(run, GlobalDropRules.BoxEntry) == null) return;
            const string archive = GlobalDropRules.Archive;
            if (!run.Data.Exists(archive)) return;
            Plan plan = run.Plan;
            var said = new HashSet<string>();
            XmlDoc boxes = FinalDoc(run, archive, GlobalDropRules.BoxEntry), sets = FinalDoc(run, archive, GlobalDropRules.SetEntry);

            // the groups the server ends up with, and the groups a box names
            HashSet<string> groups = null;
            if (sets != null && sets.Root != null)
            {
                groups = new HashSet<string>();
                foreach (XmlElem g in sets.Root.Children)
                    if (g.Name == GlobalDropRules.Holder) groups.Add((g.Attr(GlobalDropRules.GroupId) ?? "").Trim());
            }
            HashSet<long> linked = null;
            if (boxes != null && boxes.Root != null)
            {
                linked = new HashSet<long>();
                foreach (XmlElem b in boxes.Root.Children)
                    if (b.Name == GlobalDropRules.Holder)
                        foreach (XmlElem v in b.Children)
                        {
                            List<long> ids = v.Name == GlobalDropRules.Line ? GlobalDropRules.List(v.Attr(GlobalDropRules.Link)) : null;
                            if (ids != null) foreach (long id in ids) linked.Add(id);
                        }
            }
            HashSet<long> items = null;
            bool itemsRead = false;

            // a shipped group the mods take out: every box line that still names it, the shipped ones too
            XmlDoc setsWere = OrigDoc(run, archive, GlobalDropRules.SetEntry);
            if (groups != null && setsWere != null && setsWere.Root != null && boxes != null && boxes.Root != null)
            {
                var lost = new HashSet<long>();
                foreach (XmlElem g in setsWere.Root.Children)
                {
                    long id;
                    string text = (g.Attr(GlobalDropRules.GroupId) ?? "").Trim();
                    if (g.Name == GlobalDropRules.Holder && !groups.Contains(text) && GlobalDropRules.Whole(text, out id)) lost.Add(id);
                }
                if (lost.Count > 0)
                {
                    Mod by = GlobalDropMod(run, GlobalDropRules.SetEntry);
                    foreach (XmlElem b in boxes.Root.Children)
                    {
                        if (b.Name != GlobalDropRules.Holder) continue;
                        string id = (b.Attr(GlobalDropRules.BoxId) ?? "").Trim();
                        int at = 0;
                        foreach (XmlElem v in b.Children)
                        {
                            if (v.Name != GlobalDropRules.Line) continue;
                            at++;
                            List<long> ids = GlobalDropRules.List(v.Attr(GlobalDropRules.Link));
                            if (ids == null) continue;
                            foreach (long g in ids)
                                if (lost.Contains(g))
                                    Say(plan, said, plan.Errors, by, archive, GlobalDropRules.BoxEntry, "global drop box " + (id.Length > 0 ? id : "\"\"") + ", line " + at + " (group " + v.Attr(GlobalDropRules.Link)
                                        + "): " + GlobalDropRules.SetEntry + " ends up with no item group " + g + ", so the line drops nothing");
                        }
                    }
                }
            }

            foreach (bool isSet in new[] { false, true })
            {
                string entry = isSet ? GlobalDropRules.SetEntry : GlobalDropRules.BoxEntry, idField = isSet ? GlobalDropRules.GroupId : GlobalDropRules.BoxId;
                XmlDoc doc = isSet ? sets : boxes, orig = OrigDoc(run, archive, entry);
                if (doc == null || doc.Root == null || orig == null || orig.Root == null) continue;
                M2dArchive arch = run.Data.Open(archive);
                if (run.State(arch, archive, entry).Final == null) continue;
                Mod mod = GlobalDropMod(run, entry);
                string kind = isSet ? "item group " : "global drop box ";

                var shipped = new Dictionary<string, List<XmlElem>>();
                foreach (XmlElem e in orig.Root.Children)
                {
                    if (e.Name != GlobalDropRules.Holder) continue;
                    string id = (e.Attr(idField) ?? "").Trim();
                    List<XmlElem> l;
                    if (!shipped.TryGetValue(id, out l)) shipped[id] = l = new List<XmlElem>();
                    l.Add(e);
                }
                var count = new Dictionary<string, int>();
                foreach (XmlElem e in doc.Root.Children)
                {
                    if (e.Name != GlobalDropRules.Holder) continue;
                    string id = (e.Attr(idField) ?? "").Trim();
                    int n;
                    count.TryGetValue(id, out n);
                    count[id] = n + 1;
                }
                var twice = new HashSet<string>();
                foreach (XmlElem e in doc.Root.Children)
                {
                    if (e.Name != GlobalDropRules.Holder) continue;
                    string id = (e.Attr(idField) ?? "").Trim(), what = kind + (id.Length > 0 ? id : "\"\"");
                    List<XmlElem> was;
                    bool isNew = !shipped.TryGetValue(id, out was);
                    int n0 = isNew ? 0 : was.Count;
                    if (count[id] > Math.Max(n0, 1) && twice.Add(id))
                        Say(plan, said, plan.Errors, mod, archive, entry, what + ": the table ends up with " + count[id] + " with this id");
                    var lineTexts = new HashSet<string>();
                    bool asShipped = false, wasAbove = false;
                    string text = DropHolderText(e);
                    if (!isNew)
                        foreach (XmlElem o in was)
                        {
                            if (DropHolderText(o) == text) asShipped = true;
                            foreach (XmlElem c in o.Children)
                            {
                                if (c.Name != GlobalDropRules.Line) continue;
                                lineTexts.Add(RowText(c));
                                long w0;
                                if (GlobalDropRules.Whole(c.Attr(GlobalDropRules.Weight), out w0) && w0 > 0) wasAbove = true;
                            }
                        }
                    if (asShipped) continue;
                    long num;
                    if (!GlobalDropRules.Whole(id, out num) || num <= 0)
                        Say(plan, said, plan.Errors, mod, archive, entry, what + ": " + idField + " is not an id");

                    int lines = 0, at = 0;
                    bool anyWeight = false, above = false, touched = false;
                    foreach (XmlElem v in e.Children)
                    {
                        if (v.Name != GlobalDropRules.Line) continue;
                        lines++;
                        at++;
                        long w;
                        if (v.Attr(GlobalDropRules.Weight) != null)
                        {
                            anyWeight = true;
                            if (GlobalDropRules.Whole(v.Attr(GlobalDropRules.Weight), out w) && w > 0) above = true;
                        }
                        if (lineTexts.Contains(RowText(v))) continue;
                        touched = true;
                        if (isSet)
                        {
                            string line = what + ", item " + (v.Attr(GlobalDropRules.Item) ?? "?");
                            foreach (string problem in GlobalDropRules.CheckItem(v.Attr))
                                Say(plan, said, plan.Warnings, mod, archive, entry, line + ": " + problem);
                            long item;
                            if (GlobalDropRules.Whole(v.Attr(GlobalDropRules.Item), out item) && item > 0)
                            {
                                if (!itemsRead) { items = Items(run, Profile.Server); itemsRead = true; }
                                if (items != null && !items.Contains(item))
                                    Say(plan, said, plan.Errors, mod, archive, entry, line + ": the server has no item " + item);
                            }
                        }
                        else
                        {
                            string line = what + ", line " + at + " (group " + (v.Attr(GlobalDropRules.Link) ?? "?") + ")";
                            foreach (string problem in GlobalDropRules.CheckLink(v.Attr))
                                Say(plan, said, plan.Warnings, mod, archive, entry, line + ": " + problem);
                            List<long> ids = GlobalDropRules.List(v.Attr(GlobalDropRules.Link));
                            if (ids != null && groups != null)
                                foreach (long g in ids)
                                    if (!groups.Contains(g.ToString(CultureInfo.InvariantCulture)))
                                        Say(plan, said, plan.Errors, mod, archive, entry, line + ": " + GlobalDropRules.SetEntry + " ends up with no item group " + g + ", so the line drops nothing");
                        }
                    }
                    if (lines == 0)
                        Say(plan, said, plan.Warnings, mod, archive, entry, what + (isSet ? ": the group has no item" : ": the box has no line, so it drops nothing"));
                    if (isSet && anyWeight && !above && (isNew || touched || wasAbove))
                        Say(plan, said, plan.Warnings, mod, archive, entry, what + ": every weight of the group is 0");
                    if (isSet && isNew && lines > 0 && linked != null && num > 0 && !linked.Contains(num))
                        Say(plan, said, plan.Warnings, mod, archive, entry, what + ": no global drop box has a line for the group, so nothing drops from it");
                }
            }
        }
    }
}
