// Reserved id ranges. A mod declares <ids space="npc" from="29000000" to="29000999"> with the places the id
// lives in (entry names or rows, on the client and on the server). A new id must be free in EVERY place.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static class IdSpaces
    {
        /// <summary>Entry name pattern to regex: * = a run without '/', ** = any run, {id} = the digits of the id.</summary>
        public static Regex EntryRegex(string pattern)
        {
            var sb = new StringBuilder("^");
            string p = pattern.Replace('\\', '/');
            for (int i = 0; i < p.Length; i++)
            {
                if (p[i] == '*')
                {
                    if (i + 1 < p.Length && p[i + 1] == '*') { sb.Append(".*"); i++; }
                    else sb.Append("[^/]*");
                }
                else if (string.CompareOrdinal(p, i, "{id}", 0, 4) == 0) { sb.Append("(?<id>\\d+)"); i += 3; }
                else sb.Append(Regex.Escape(p[i].ToString()));
            }
            return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        static string PlaceKey(IdPlace p) { return p.Archive + "\n" + p.Entry.ToLowerInvariant() + "\n" + (p.Rows ?? "\u0001") + "\n" + p.Key; }

        public static string Describe(IdPlace p)
        {
            return p.Archive + " " + p.Entry + (p.Rows != null ? " rows " + p.Rows + " @" + p.Key : "");
        }

        static bool TryId(string s, out long id)
        {
            return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out id);
        }

        /// <summary>The ids a place holds in the original data. Scanned once per data set.</summary>
        public static HashSet<long> UsedIds(DataSet data, IdPlace place, Action<ProgressInfo> progress)
        {
            string key = "ids\n" + PlaceKey(place);
            object o;
            if (data.Cache.TryGetValue(key, out o)) return (HashSet<long>)o;
            var used = new HashSet<long>();
            M2dArchive arch = data.Open(place.Archive);
            Regex rx = EntryRegex(place.Entry);
            if (place.Rows == null && rx.GetGroupNumbers().Length < 2)
                throw new ModKitException("id place " + Describe(place) + ": the entry pattern needs {id} when there is no rows selector");
            RowPath rows = place.Rows == null ? null : RowPath.Parse(place.Rows);
            int n = 0;
            foreach (M2dEntry e in arch.Entries)
            {
                n++;
                Match m = rx.Match(e.Name);
                if (!m.Success) continue;
                long id;
                if (rows == null)
                {
                    if (TryId(m.Groups["id"].Value, out id)) used.Add(id);
                    continue;
                }
                ProgressUtil.Report(progress, "ids", e.Name, n, arch.Count);
                XmlDoc doc;
                try { doc = XmlDoc.Parse(arch.Read(e), place.Archive + " " + e.Name); }
                catch (ModKitException) { continue; }
                if (doc.Root == null) continue;
                foreach (XmlElem row in rows.ResolveAll(doc.Root))
                    if (TryId(row.Attr(place.Key) ?? "", out id)) used.Add(id);
            }
            data.Cache[key] = used;
            return used;
        }

        /// <summary>The id a new row or entry takes in a place, or false when the place is not about it.</summary>
        internal static bool Match(IdPlace place, NewId n, out string idText)
        {
            idText = null;
            if (place.Archive != n.Archive) return false;
            Match m = EntryRegex(place.Entry).Match(n.Entry);
            if (!m.Success) return false;
            if (place.Rows == null)
            {
                if (n.NamePath != null) return false;
                idText = m.Groups["id"].Value;
                return true;
            }
            if (n.NamePath == null) return false;
            RowPath rows = RowPath.Parse(place.Rows);
            string[] names = n.NamePath.Split('/');
            if (names.Length != rows.Steps.Count) return false;
            for (int i = 0; i < names.Length; i++)
                if (rows.Steps[i].Name != "*" && rows.Steps[i].Name != names[i]) return false;
            foreach (KeyValuePair<string, string> t in rows.Steps[names.Length - 1].Tests)
            {
                string v;
                n.Attrs.TryGetValue(t.Key, out v);
                if ((v ?? "") != t.Value) return false;
            }
            string id;
            if (!n.Attrs.TryGetValue(place.Key, out id)) id = "";
            idText = id;
            return true;
        }

        sealed class Taken { internal Mod Mod; internal IdRange Range; internal readonly List<int> Places = new List<int>(); }

        internal static void Check(DataSet data, Plan plan, Action<ProgressInfo> progress)
        {
            // ranges of one space must not overlap between mods
            for (int a = 0; a < plan.Mods.Count; a++)
                foreach (IdRange ra in plan.Mods[a].Ids)
                {
                    if (ra.From > ra.To) plan.Msg(plan.Errors, plan.Mods[a], null, null, "ids space \"" + ra.Space + "\": from " + ra.From + " is above to " + ra.To);
                    for (int b = a + 1; b < plan.Mods.Count; b++)
                        foreach (IdRange rb in plan.Mods[b].Ids)
                            if (ra.Space == rb.Space && ra.From <= rb.To && rb.From <= ra.To)
                                plan.Msg(plan.Conflicts, plan.Mods[b], null, null, "ids space \"" + rb.Space + "\": the range " + rb.From + "-" + rb.To + " overlaps " + ra.From + "-" + ra.To + " of mod " + plan.Mods[a].Id);
                }

            var owner = new Dictionary<string, Mod>();          // place + id -> the mod that adds it
            var taken = new Dictionary<string, Taken>();        // mod + space + id -> the places it was added in
            foreach (NewId n in plan.NewIds)
            {
                foreach (IdRange r in n.Mod.Ids)
                {
                    for (int pi = 0; pi < r.Places.Count; pi++)
                    {
                        IdPlace place = r.Places[pi];
                        string text;
                        if (!Match(place, n, out text)) continue;
                        long id;
                        if (!TryId(text, out id))
                        {
                            plan.Msg(plan.Warnings, n.Mod, n.Archive, n.Entry, "new " + (n.NamePath ?? "entry") + ": \"" + text + "\" is not a number, the ids of space \"" + r.Space + "\" are not checked for it");
                            continue;
                        }
                        bool inRange = id >= r.From && id <= r.To;
                        if (!inRange)
                        {
                            bool other = false;
                            foreach (IdRange r2 in n.Mod.Ids) if (r2 != r && r2.Space == r.Space && id >= r2.From && id <= r2.To) other = true;
                            if (other) continue;
                            plan.Msg(plan.Warnings, n.Mod, n.Archive, n.Entry, "new id " + id + " is outside of the range " + r.From + "-" + r.To + " the mod reserves for \"" + r.Space + "\"");
                        }
                        foreach (IdPlace q in r.Places)
                        {
                            HashSet<long> used;
                            try { used = UsedIds(data, q, progress); }
                            catch (ModKitException x) { plan.Msg(plan.Errors, n.Mod, q.Archive, null, "ids space \"" + r.Space + "\": " + x.Message); continue; }
                            if (!used.Contains(id)) continue;
                            plan.Msg(r.Strict ? plan.Errors : plan.Warnings, n.Mod, n.Archive, n.Entry,
                                "new id " + id + " of space \"" + r.Space + "\" is already used in the original data (" + Describe(q) + ")");
                        }
                        string ok = PlaceKey(place) + "\n" + id;
                        Mod first;
                        if (owner.TryGetValue(ok, out first) && first != n.Mod)
                            plan.Msg(plan.Conflicts, n.Mod, n.Archive, n.Entry, "new id " + id + " of space \"" + r.Space + "\" is also added by mod " + first.Id);
                        else owner[ok] = n.Mod;
                        string tk = n.Mod.Id + "\n" + r.Space + "\n" + r.From + "\n" + id;
                        Taken t;
                        if (!taken.TryGetValue(tk, out t)) taken[tk] = t = new Taken { Mod = n.Mod, Range = r };
                        if (!t.Places.Contains(pi)) t.Places.Add(pi);
                    }
                }
            }
            foreach (KeyValuePair<string, Taken> kv in taken)
            {
                Taken t = kv.Value;
                if (t.Places.Count == t.Range.Places.Count) continue;
                string id = kv.Key.Substring(kv.Key.LastIndexOf('\n') + 1);
                var missing = new List<string>();
                for (int i = 0; i < t.Range.Places.Count; i++) if (!t.Places.Contains(i)) missing.Add(Describe(t.Range.Places[i]));
                plan.Msg(plan.Warnings, t.Mod, null, null, "id " + id + " of space \"" + t.Range.Space + "\" is added in " + t.Places.Count + " of " + t.Range.Places.Count + " places; nothing is added in " + string.Join("; ", missing.ToArray()));
            }
        }

        /// <summary>The ids of a mod's range that are free in the original data and not added by the mod itself, lowest first.</summary>
        public static List<long> NextFree(DataSet data, Mod mod, string space, int count, Action<ProgressInfo> progress)
        {
            var ranges = new List<IdRange>();
            foreach (IdRange r in mod.Ids) if (r.Space == space) ranges.Add(r);
            if (ranges.Count == 0) throw new ModKitException("mod " + mod.Id + " reserves no ids for the space \"" + space + "\"");
            Plan plan = Planner.Build(data, new[] { mod }, new PlanOptions { Progress = progress });
            var free = new List<long>();
            foreach (IdRange r in ranges)
            {
                var busy = new HashSet<long>();
                foreach (IdPlace p in r.Places) busy.UnionWith(UsedIds(data, p, progress));
                foreach (NewId n in plan.NewIds)
                    foreach (IdPlace p in r.Places)
                    {
                        string text; long id;
                        if (Match(p, n, out text) && TryId(text, out id)) busy.Add(id);
                    }
                for (long id = r.From; id <= r.To && free.Count < count; id++)
                    if (!busy.Contains(id)) free.Add(id);
            }
            return free;
        }
    }
}
