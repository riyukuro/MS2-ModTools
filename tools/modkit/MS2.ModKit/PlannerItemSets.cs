// The plan's checks of item sets (table/setiteminfo.xml and the server's table/setitemoption.xml, see ItemSets.cs):
// every set the mods add or change (its row, or the bonuses it takes) is read as it ends up in each target and
// checked with ItemSetRules. On top of the editor's checks the plan knows the final tables: a set id that is there
// twice in the kms table (the client then loads no set at all), an optionID without an option row (the server's
// ingest fails), items and effects that the target does not have, a new set that one target gets and another does
// not, and a new set whose items or bonuses differ between the kms client and the server.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        sealed class SetSide
        {
            internal Profile Profile;
            internal string Archive;
            internal bool Kms;
            internal XmlDoc Info, InfoOrig, Opt, OptOrig;
        }

        static string SetKey(XmlElem e)
        {
            return (e.Attr("id") ?? "") + "\n" + (e.Attr("feature") ?? "") + "\n" + (e.Attr("locale") ?? "");
        }

        static Dictionary<string, XmlElem> SetOptions(XmlDoc d)
        {
            var m = new Dictionary<string, XmlElem>();
            if (d == null || d.Root == null) return m;
            foreach (XmlElem e in d.Root.Children)
                if (e.Name == ItemSetRules.OptionRows) m[e.Attr("id") ?? ""] = e;       // the last row of an id wins, as in the server's parser
            return m;
        }

        static List<XmlElem> SetParts(XmlElem holder)
        {
            var l = new List<XmlElem>();
            if (holder != null) foreach (XmlElem c in holder.Children) if (c.Name == ItemSetRules.PartRows) l.Add(c);
            return l;
        }

        // a set as one text: its row and the parts it takes
        static string SetText(XmlElem row, XmlElem holder)
        {
            var sb = new StringBuilder(RowText(row));
            if (holder == null) return sb.Append("(no option)").ToString();
            foreach (XmlElem p in SetParts(holder)) sb.Append("part\n").Append(RowText(p));
            return sb.ToString();
        }

        static XmlDoc OrigDoc(Run run, string archive, string entry)
        {
            try
            {
                M2dArchive arch = run.Data.Open(archive);
                M2dEntry e = arch.Find(entry);
                if (e == null) return null;
                EntryState st = run.State(arch, archive, e.Name);
                return XmlDoc.Parse(st.Original ?? arch.Read(e), archive + " " + e.Name);
            }
            catch (ModKitException) { return null; }
        }

        // the mod a set is blamed on: the last mod whose lines for the set tables name the id, else the last with lines there
        static Mod SetMod(Run run, string id)
        {
            Mod named = null, any = null;
            var inStep = new Regex(@"\[id=[""']?" + Regex.Escape(id) + @"[""']?\]");
            var inRow = new Regex(@"\sid\s*=\s*[""']" + Regex.Escape(id) + @"[""']");
            foreach (Mod m in run.Plan.Mods)
                foreach (ModOp op in m.Ops)
                {
                    var ed = op as EditOp;
                    if (ed == null) continue;
                    string entry = ed.Entry.Replace('\\', '/');
                    if (!string.Equals(entry, ItemSetRules.InfoEntry, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(entry, ItemSetRules.OptionEntry, StringComparison.OrdinalIgnoreCase)) continue;
                    any = m;
                    foreach (RowOp r in ed.Rows)
                    {
                        bool hit = false;
                        foreach (string sel in new[] { r.At, r.Copy, r.In, r.After, r.Before }) if (sel != null && inStep.IsMatch(sel)) hit = true;
                        if (r.Xml != null && inRow.IsMatch(r.Xml)) hit = true;
                        foreach (AttrOp a in r.Attrs) if (a.Name == "id" && a.Value == id) hit = true;
                        if (hit) named = m;
                    }
                }
            return named ?? any;
        }

        /// <summary>The additional-effect ids of a target, as shipped plus those the mods add (null when they cannot be read).</summary>
        static HashSet<long> SetEffects(Run run, Profile p)
        {
            string key = "setcheck-effects\n" + p.Root;
            object o;
            if (run.Data.Cache.TryGetValue(key, out o) && ((KeyValuePair<int, HashSet<long>>)o).Key == run.Plan.GetHashCode()) return ((KeyValuePair<int, HashSet<long>>)o).Value;
            var place = p == Profile.KmsClient
                ? new IdPlace { Archive = p.ArchiveIn("x:Xml.m2d"), Entry = "additional/*.xml", Rows = "additional", Key = "id" }
                : new IdPlace { Archive = p.ArchiveIn("x:Xml.m2d"), Entry = "additionaleffect/{id}.xml" };
            var set = new HashSet<long>();
            try { foreach (long id in IdSpaces.UsedIds(run.Data, place, run.Options.Progress)) set.Add(id); }
            catch (ModKitException) { return null; }
            foreach (NewId n in run.Plan.NewIds)
            {
                string text;
                long id;
                if (IdSpaces.Match(place, n, out text) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id)) set.Add(id);
            }
            run.Data.Cache[key] = new KeyValuePair<int, HashSet<long>>(run.Plan.GetHashCode(), set);      // one per target: an earlier plan's set goes
            return set;
        }

        static void ItemSetChecks(Run run)
        {
            // first which targets ship the table and whether any plan touches it; the tables are read only then
            var sides = new List<SetSide>();
            bool touched = false;
            foreach (Profile p in run.Data.Profiles)
            {
                string archive = p.ArchiveIn("x:Xml.m2d");
                M2dArchive arch;
                try { arch = run.Data.Open(archive); }
                catch (ModKitException) { continue; }
                M2dEntry e = arch.Find(ItemSetRules.InfoEntry);
                if (e == null) continue;
                if (run.State(arch, archive, e.Name).Final != null) touched = true;
                M2dEntry oe = arch.Find(ItemSetRules.OptionEntry);
                if (oe != null && run.State(arch, archive, oe.Name).Final != null) touched = true;
                sides.Add(new SetSide { Profile = p, Archive = archive, Kms = p == Profile.KmsClient });
            }
            if (!touched) return;
            Plan plan = run.Plan;
            foreach (SetSide s in sides.ToArray())
            {
                s.InfoOrig = OrigDoc(run, s.Archive, ItemSetRules.InfoEntry);
                s.Info = FinalDoc(run, s.Archive, ItemSetRules.InfoEntry);
                if (s.InfoOrig == null || s.Info == null || s.Info.Root == null) { sides.Remove(s); continue; }
                if (s.Kms) continue;
                s.OptOrig = OrigDoc(run, s.Archive, ItemSetRules.OptionEntry);
                s.Opt = FinalDoc(run, s.Archive, ItemSetRules.OptionEntry);
            }

            var reserved = new List<IdRange>();
            foreach (Mod m in plan.Mods) foreach (IdRange r in m.Ids) if (r.Space == ItemSetRules.Space) reserved.Add(r);

            // the new sets of each side, for the comparison between the sides: id -> row and holder
            var added = new List<Dictionary<string, KeyValuePair<XmlElem, XmlElem>>>();
            var has = new List<HashSet<string>>();
            // the server's sets without an option row: a line mirrored to the gms client gives one message
            int server = -1;
            var missing = new HashSet<string>();
            for (int i = 0; i < sides.Count; i++)
            {
                if (sides[i].Profile != Profile.Server) continue;
                server = i;
                Dictionary<string, XmlElem> so = SetOptions(sides[i].Opt);
                foreach (XmlElem e in sides[i].Info.Root.Children)
                    if (e.Name == ItemSetRules.Rows && !so.ContainsKey(e.Attr("optionID") ?? "")) missing.Add(e.Attr("id") ?? "");
            }
            foreach (SetSide s in sides)
            {
                var mine = new Dictionary<string, KeyValuePair<XmlElem, XmlElem>>();
                var ids = new HashSet<string>();
                added.Add(mine);
                has.Add(ids);
                Dictionary<string, XmlElem> opts = SetOptions(s.Opt), optsOrig = SetOptions(s.OptOrig);
                var orig = new Dictionary<string, string>();
                var origIds = new HashSet<string>();
                var seenOrig = new Dictionary<string, int>();
                foreach (XmlElem e in s.InfoOrig.Root.Children)
                {
                    if (e.Name != ItemSetRules.Rows) continue;
                    int was0;
                    seenOrig.TryGetValue(e.Attr("id") ?? "", out was0);
                    seenOrig[e.Attr("id") ?? ""] = was0 + 1;
                    XmlElem h = e;
                    if (!s.Kms) optsOrig.TryGetValue(e.Attr("optionID") ?? "", out h);
                    orig[SetKey(e)] = SetText(e, h);
                    origIds.Add(e.Attr("id") ?? "");
                }
                HashSet<long> items = null, effects = null;
                bool read = false;
                var seen = new Dictionary<string, int>();
                foreach (XmlElem row in s.Info.Root.Children)
                {
                    if (row.Name != ItemSetRules.Rows) continue;
                    string id = row.Attr("id") ?? "";
                    ids.Add(id);
                    int times;
                    seen.TryGetValue(id, out times);
                    seen[id] = times + 1;
                    XmlElem holder = row;
                    if (!s.Kms) opts.TryGetValue(row.Attr("optionID") ?? "", out holder);
                    string was;
                    bool isNew = !orig.TryGetValue(SetKey(row), out was);
                    if (!isNew && was == SetText(row, holder)) continue;
                    Mod mod = SetMod(run, id);
                    string label = "set " + id;

                    long n;
                    if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n <= 0)
                        plan.Msg(s.Kms ? plan.Errors : plan.Warnings, mod, s.Archive, ItemSetRules.InfoEntry, label + ": id \"" + id + "\" is not a number above 0" + (s.Kms ? ItemSetRules.TableFails : ""));
                    var parts = new List<Func<string, string>>();
                    foreach (XmlElem p in SetParts(holder)) parts.Add(p.Attr);
                    foreach (string t in ItemSetRules.CheckSet(row.Attr, parts, s.Kms))
                        plan.Msg(s.Kms && t.EndsWith(ItemSetRules.TableFails, StringComparison.Ordinal) ? plan.Errors : plan.Warnings, mod, s.Archive, ItemSetRules.InfoEntry, label + ": " + t);
                    // the server's mirror on the gms client says it once, on the server
                    if (!s.Kms && holder == null && !(s.Profile != Profile.Server && server >= 0 && missing.Contains(id)))
                        plan.Msg(s.Profile == Profile.Server ? plan.Errors : plan.Warnings, mod, s.Archive, ItemSetRules.InfoEntry, label + ": optionID \"" + row.Attr("optionID") + "\" has no <option> row in " + ItemSetRules.OptionEntry
                            + (s.Profile == Profile.Server ? ": the server's data ingest fails" : ""));

                    if (!read)
                    {
                        read = true;
                        items = Items(run, s.Profile);
                        effects = SetEffects(run, s.Profile);
                    }
                    List<long> members;
                    if (items != null && ItemSetRules.TryMembers(row.Attr("itemIDs"), out members))
                        foreach (long it in members)
                            if (!items.Contains(it)) plan.Msg(plan.Warnings, mod, s.Archive, ItemSetRules.InfoEntry, label + ": item " + it + " is not an item of the " + s.Profile.Title);
                    if (effects != null)
                        foreach (XmlElem p in SetParts(holder))
                            foreach (string t in (p.Attr("additionalEffectID") ?? "").Split(','))
                            {
                                long ef;
                                if (long.TryParse(t.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ef) && ef > 0 && !effects.Contains(ef))
                                    plan.Msg(plan.Warnings, mod, s.Archive, s.Kms ? ItemSetRules.InfoEntry : ItemSetRules.OptionEntry,
                                        label + ": bonus for " + p.Attr("count") + " items: effect " + ef + " is not an additional effect of the " + s.Profile.Title);
                            }
                    if (isNew && !origIds.Contains(id)) mine[id] = new KeyValuePair<XmlElem, XmlElem>(row, holder);
                }
                if (s.Kms)
                    foreach (KeyValuePair<string, int> kv in seen)
                    {
                        // an id the shipped table already has that often is not this plan's doing: it does not block Apply
                        int shipped;
                        seenOrig.TryGetValue(kv.Key, out shipped);
                        if (kv.Value > 1)
                            plan.Msg(kv.Value > shipped ? plan.Errors : plan.Warnings, SetMod(run, kv.Key), s.Archive, ItemSetRules.InfoEntry, "set " + kv.Key + " is in the table " + kv.Value + " times" + ItemSetRules.TableFails);
                    }
            }

            for (int i = 0; i < sides.Count; i++)
                foreach (KeyValuePair<string, KeyValuePair<XmlElem, XmlElem>> kv in added[i])
                {
                    SetSide s = sides[i];
                    Mod mod = SetMod(run, kv.Key);
                    string label = "set " + kv.Key;
                    long n;
                    bool covered = false;
                    if (long.TryParse(kv.Key, NumberStyles.None, CultureInfo.InvariantCulture, out n))
                        foreach (IdRange r in reserved) if (n >= r.From && n <= r.To) covered = true;
                    for (int k = 0; k < sides.Count; k++)
                    {
                        if (k == i) continue;
                        SetSide o = sides[k];
                        if (!has[k].Contains(kv.Key))
                        {
                            if (!covered && !(!s.Kms && i != server && server >= 0 && added[server].ContainsKey(kv.Key)))
                                plan.Msg(plan.Warnings, mod, s.Archive, ItemSetRules.InfoEntry, label + " is added here but not in " + o.Archive + ": client and server read sets from their own table");
                            continue;
                        }
                        // one message per pair, from the side that comes first
                        KeyValuePair<XmlElem, XmlElem> other;
                        if (k < i || !added[k].TryGetValue(kv.Key, out other) || s.Kms == o.Kms) continue;
                        string diff = SetDiff(kv.Value, s.Kms, other, o.Kms);
                        if (diff != null)
                            plan.Msg(plan.Warnings, mod, null, ItemSetRules.InfoEntry, label + ": " + diff + " between " + s.Archive + " and " + o.Archive);
                    }
                }
        }

        // what differs between a new set of the kms client and of the server, in the editor's notation; null for nothing
        static string SetDiff(KeyValuePair<XmlElem, XmlElem> a, bool aKms, KeyValuePair<XmlElem, XmlElem> b, bool bKms)
        {
            var l = new List<string>();
            if ((a.Key.Attr("itemIDs") ?? "") != (b.Key.Attr("itemIDs") ?? "")) l.Add("the items differ");
            var pa = new Dictionary<string, XmlElem>();
            var pb = new Dictionary<string, XmlElem>();
            foreach (XmlElem p in SetParts(a.Value)) pa[p.Attr("count") ?? ""] = p;
            foreach (XmlElem p in SetParts(b.Value)) pb[p.Attr("count") ?? ""] = p;
            foreach (KeyValuePair<string, XmlElem> kv in pb)
                if (!pa.ContainsKey(kv.Key)) l.Add("the bonus for " + kv.Key + " items is on one side only");
            foreach (KeyValuePair<string, XmlElem> kv in pa)
            {
                XmlElem o;
                if (!pb.TryGetValue(kv.Key, out o)) { l.Add("the bonus for " + kv.Key + " items is on one side only"); continue; }
                var fields = new List<string>(ItemSetRules.KmsStats);
                fields.Add(ItemSetRules.Effects);
                fields.AddRange(ItemSetRules.Texts);
                var d = new List<string>();
                foreach (string f in fields)
                    if (ItemSetRules.Read(kv.Value.Attr, aKms, f) != ItemSetRules.Read(o.Attr, bKms, f)) d.Add(f);
                if (d.Count > 0) l.Add("the bonus for " + kv.Key + " items differs in " + string.Join(", ", d.ToArray()));
            }
            return l.Count == 0 ? null : string.Join("; ", l.ToArray());
        }
    }
}
