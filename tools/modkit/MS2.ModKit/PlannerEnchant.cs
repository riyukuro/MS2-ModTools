// The plan's checks of enchant bonuses (table/Server/enchantOptionTable.xml, see Enchant.cs). Every row the mods add
// or change is read as the server's ingest reads it (EnchantRules): a value it cannot read, and an id on two rows, are
// errors. The final table is then looked at as the server looks it up: a row an earlier row of its slot, grade and rank
// hides, and item levels the mods leave without a row for an enchant level (the shipped levels, and those of the rows
// the mods add or change), are warnings.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        // the mod an enchant row is blamed on: the last mod whose edit of the table names the id, else the last that edits it
        static Mod EnchantMod(Run run, string id)
        {
            Mod named = null;
            var inStep = new Regex(@"\[" + EnchantRules.Key + @"=[""']?" + Regex.Escape(id) + @"[""']?\]");
            var inRow = new Regex(@"\s" + EnchantRules.Key + @"\s*=\s*[""']" + Regex.Escape(id) + @"[""']");
            foreach (Mod m in run.Plan.Mods)
                foreach (ModOp op in m.Ops)
                {
                    var ed = op as EditOp;
                    if (ed == null || !string.Equals(ed.Entry.Replace('\\', '/'), EnchantRules.Entry, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(DataSet.NormalizeRef(ed.Archive), DataSet.NormalizeRef(EnchantRules.Archive), StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (RowOp r in ed.Rows)
                    {
                        bool hit = r.At != null && inStep.IsMatch(r.At) || r.Copy != null && inStep.IsMatch(r.Copy)
                            || r.Xml != null && inRow.IsMatch(r.Xml);
                        foreach (AttrOp a in r.Attrs) if (a.Name == EnchantRules.Key && (a.Value ?? "").Trim() == id) hit = true;
                        if (hit) named = m;
                    }
                }
            return named ?? LevelMod(run, EnchantRules.Archive, EnchantRules.Entry);
        }

        static string EnchantLabel(XmlElem e)
        {
            Func<string, long> n = f => EnchantRules.IntOf(e.Attr(f));
            return "enchant row " + (e.Attr(EnchantRules.Key) ?? "(no id)").Trim() + " (" + EnchantRules.LadderLabel(n(EnchantRules.Slot), n(EnchantRules.Rank), n(EnchantRules.MinLv), n(EnchantRules.MaxLv)) + ", +" + n(EnchantRules.Grade) + ")";
        }

        static void EnchantChecks(Run run)
        {
            Mod level = LevelMod(run, EnchantRules.Archive, EnchantRules.Entry);
            if (level == null) return;
            Plan plan = run.Plan;
            string archive = EnchantRules.Archive, entry = EnchantRules.Entry;
            XmlDoc fin = FinalDoc(run, archive, entry);
            if (fin == null || fin.Root == null)
            {
                plan.Msg(plan.Errors, level, archive, entry, "the mods leave the server without " + entry + ": its ingest stops");
                return;
            }
            var rows = new List<XmlElem>();
            foreach (XmlElem r in fin.Root.Children) if (r.Name == EnchantRules.Rows) rows.Add(r);
            var was = new List<XmlElem>();
            XmlDoc orig = OrigDoc(run, archive, entry);
            if (orig != null && orig.Root != null) foreach (XmlElem r in orig.Root.Children) if (r.Name == EnchantRules.Rows) was.Add(r);

            var then = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (XmlElem r in was) { int k; then[r.OuterText] = then.TryGetValue(r.OuterText, out k) ? k + 1 : 1; }
            var changed = new HashSet<XmlElem>();
            foreach (XmlElem r in rows)
            {
                int left;
                if (then.TryGetValue(r.OuterText, out left) && left > 0) then[r.OuterText] = left - 1;
                else changed.Add(r);
            }

            Func<XmlElem, string> idOf = e => (e.Attr(EnchantRules.Key) ?? "").Trim();
            foreach (XmlElem e in rows)
            {
                if (!changed.Contains(e)) continue;
                var errors = new List<string>();
                var warnings = new List<string>();
                XmlElem el = e;
                EnchantRules.Check(f => el.Attr(f), errors, warnings);
                Mod mod = EnchantMod(run, idOf(e));
                string label = EnchantLabel(e);
                foreach (string t in errors) plan.Msg(plan.Errors, mod, archive, entry, label + ": " + t);
                foreach (string t in warnings) plan.Msg(plan.Warnings, mod, archive, entry, label + ": " + t);
            }

            // an id on two rows stops the ingest
            var byId = new Dictionary<long, List<XmlElem>>();
            foreach (XmlElem e in rows)
            {
                long id;
                if (!EnchantRules.ServerInt(e.Attr(EnchantRules.Key), out id)) continue;
                List<XmlElem> l;
                if (!byId.TryGetValue(id, out l)) byId[id] = l = new List<XmlElem>();
                l.Add(e);
            }
            foreach (KeyValuePair<long, List<XmlElem>> kv in byId)
            {
                if (kv.Value.Count < 2) continue;
                bool mine = false;
                foreach (XmlElem e in kv.Value) if (changed.Contains(e)) mine = true;
                if (!mine) continue;
                string id = kv.Key.ToString(CultureInfo.InvariantCulture);
                plan.Msg(plan.Errors, EnchantMod(run, id), archive, entry, "enchant row id " + id + " is on " + kv.Value.Count + " rows: the server's ingest stops on it");
            }

            // rows the server never reaches for some levels
            var gets = new List<Func<string, string>>();
            foreach (XmlElem e in rows) { XmlElem el = e; gets.Add(f => el.Attr(f)); }
            foreach (Shadow s in EnchantRules.Shadows(gets))
            {
                XmlElem r = rows[s.Row], by = rows[s.By];
                if (!changed.Contains(r) && !changed.Contains(by)) continue;
                plan.Msg(plan.Warnings, EnchantMod(run, idOf(changed.Contains(r) ? r : by)), archive, entry,
                    EnchantLabel(r) + ": " + (s.All ? "the server never uses it" : "the server does not use it for level " + EnchantRules.Spans(s.Levels))
                    + ": " + EnchantLabel(by) + " comes first in the table");
            }

            // levels left without a row: the shipped rows' levels and those of the rows the mods add or change
            var finalRanges = new Dictionary<string, List<long[]>>(StringComparer.Ordinal);
            foreach (XmlElem e in rows)
            {
                string k = EnchantKey(e, -1);
                List<long[]> l;
                if (!finalRanges.TryGetValue(k, out l)) finalRanges[k] = l = new List<long[]>();
                l.Add(new[] { EnchantRules.IntOf(e.Attr(EnchantRules.MinLv)), EnchantRules.IntOf(e.Attr(EnchantRules.MaxLv)) });
            }
            // (slot, rank, levels) -> grades left without a row, and a row id to blame
            var lost = new SortedDictionary<string, SortedDictionary<long, bool>>(StringComparer.Ordinal);
            var blame = new Dictionary<string, string>(StringComparer.Ordinal);
            var bands = new List<KeyValuePair<XmlElem, bool>>();
            foreach (XmlElem e in was) bands.Add(new KeyValuePair<XmlElem, bool>(e, true));
            foreach (XmlElem e in rows) if (changed.Contains(e)) bands.Add(new KeyValuePair<XmlElem, bool>(e, false));
            var done = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<XmlElem, bool> b in bands)
            {
                XmlElem e = b.Key;
                long slot = EnchantRules.IntOf(e.Attr(EnchantRules.Slot)), rank = EnchantRules.IntOf(e.Attr(EnchantRules.Rank));
                long lo = EnchantRules.IntOf(e.Attr(EnchantRules.MinLv)), hi = EnchantRules.IntOf(e.Attr(EnchantRules.MaxLv));
                if (lo > hi) continue;
                // a ladder the mods add is checked for every grade; a shipped row for its own
                long g0 = EnchantRules.IntOf(e.Attr(EnchantRules.Grade));
                long from = b.Value ? g0 : 1, to = b.Value ? g0 : EnchantRules.MaxGrade;
                for (long g = from; g <= to; g++)
                {
                    if (g < 1 || g > EnchantRules.MaxGrade) continue;
                    string seen = slot + "\n" + rank + "\n" + lo + "\n" + hi + "\n" + g;
                    if (!done.Add(seen)) continue;
                    List<long[]> have;
                    finalRanges.TryGetValue(slot + "\n" + g + "\n" + rank, out have);
                    foreach (long[] u in EnchantRules.Uncovered(have ?? new List<long[]>(), lo, hi))
                    {
                        string k = slot.ToString("D6", CultureInfo.InvariantCulture) + "\n" + rank.ToString("D3", CultureInfo.InvariantCulture) + "\n" + u[0].ToString("D6", CultureInfo.InvariantCulture) + "\n" + u[1].ToString("D6", CultureInfo.InvariantCulture);
                        SortedDictionary<long, bool> gs;
                        if (!lost.TryGetValue(k, out gs)) lost[k] = gs = new SortedDictionary<long, bool>();
                        gs[g] = true;
                        if (!blame.ContainsKey(k)) blame[k] = idOf(e);
                    }
                }
            }
            foreach (KeyValuePair<string, SortedDictionary<long, bool>> kv in lost)
            {
                string[] p = kv.Key.Split('\n');
                long slot = long.Parse(p[0], CultureInfo.InvariantCulture), rank = long.Parse(p[1], CultureInfo.InvariantCulture);
                long lo = long.Parse(p[2], CultureInfo.InvariantCulture), hi = long.Parse(p[3], CultureInfo.InvariantCulture);
                plan.Msg(plan.Warnings, EnchantMod(run, blame[kv.Key]), archive, entry,
                    "enchant bonus of " + EnchantRules.LadderLabel(slot, rank, lo, hi) + ": no row for " + EnchantRules.Grades(kv.Value.Keys) + " after the mods, so those levels add nothing");
            }
        }

        // slot, grade, rank of a row (grade: the row's own when g is -1)
        static string EnchantKey(XmlElem e, long g)
        {
            return EnchantRules.IntOf(e.Attr(EnchantRules.Slot)) + "\n" + (g < 0 ? EnchantRules.IntOf(e.Attr(EnchantRules.Grade)) : g) + "\n" + EnchantRules.IntOf(e.Attr(EnchantRules.Rank));
        }
    }
}
