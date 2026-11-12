// The plan's checks of item stat options (ItemOptions.cs). Run when a plan changes an option file or an item file:
//   - flat option rows (server and global client, itemoption/constant, itemoption/option/static and .../random): a
//     code and grade twice among the files the server reads (its ingest stops), a stat no shipped file of the kind
//     uses, a value the server cannot read (errors); a static row the server skips (optionNumPick empty or "0,0"),
//     a static or random pick count above the stats listed, a row in a file the server does not read (warnings);
//   - kms option rows (table/itemoptionconstant.xml): a code twice, a value that is no whole number (errors);
//   - kms random rows (table/itemoptionrandom.xml): a code twice, an attribute the client cannot read, a weight of 0
//     (errors); a pickCount above the row's lines, a statID with no row in table/itemoptionvariation.xml (warnings);
//   - the kms client and the server giving a stat of a code different values at a grade where they agreed (warning);
//   - an item file whose option names a constant, static or random code that no file the server reads has (error);
//   - a kms item row (itemdata/*.xml) whose constantID or randomID the mod changed to a code the kms table does not
//     have (error);
//   - a grade the items of a code could be at with no row: the server takes an item's rarity from where the item
//     is made (drops, shops, rewards) and looks the row up at that exact grade, so a code that loses a grade row it
//     shipped with, or an item moved to a code without a grade its old code had, leaves those items without the
//     stats at that grade (warning).
using System;
using System.Collections.Generic;
using System.Globalization;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        // the rows of one kind of one flat archive, as shipped and as the plan leaves them
        sealed class OptionFlat
        {
            internal string Archive;
            internal OptionKind Kind;
            // code\ngrade -> entries the row is in
            internal readonly Dictionary<string, List<string>> Final = new Dictionary<string, List<string>>(), Original = new Dictionary<string, List<string>>();
            internal readonly Dictionary<string, XmlElem> FinalRow = new Dictionary<string, XmlElem>(), OriginalRow = new Dictionary<string, XmlElem>();
            internal readonly HashSet<string> Stats = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> Codes = new HashSet<string>(StringComparer.Ordinal);
            // rows of the touched files that are new or differ from the shipped row: key -> entry
            internal readonly List<KeyValuePair<string, string>> Edited = new List<KeyValuePair<string, string>>();
        }

        static List<string> OptList(Dictionary<string, List<string>> d, string key)
        {
            List<string> l;
            if (!d.TryGetValue(key, out l)) d[key] = l = new List<string>();
            return l;
        }

        static string OptKey(XmlElem e) { return (e.Attr("code") ?? "").Trim() + "\n" + (e.Attr("grade") ?? "").Trim(); }

        static string OptLabel(string key) { int i = key.IndexOf('\n'); return "option " + key.Substring(0, i) + " grade " + key.Substring(i + 1); }

        static XmlDoc OriginalDoc(Run run, M2dArchive arch, string archive, M2dEntry e)
        {
            EntryState st = run.State(arch, archive, e.Name);
            try { return XmlDoc.Parse(st.Original ?? arch.Read(e), archive + " " + e.Name); }
            catch (ModKitException) { return null; }
        }

        static void ItemOptionChecks(Run run)
        {
            var touchedFlat = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // archive\nentry
            var flatArchives = new List<string>();
            var itemFiles = new List<EntryState>();
            var kmsItemFiles = new List<EntryState>();
            bool kmsTouched = false, kmsRandomTouched = false;
            foreach (EntryState st in run.Order)
            {
                if (st.Final == null && st.DeletedBy == null) continue;
                string n = st.Name.Replace('\\', '/').ToLowerInvariant();
                OptionKind k;
                if (ItemOptionRules.IsFlatEntry(n, out k))
                {
                    touchedFlat.Add(st.Archive + "\n" + n);
                    if (!flatArchives.Contains(st.Archive)) flatArchives.Add(st.Archive);
                }
                else if (n == ItemOptionRules.KmsEntry) kmsTouched = true;
                else if (n == ItemOptionRules.KmsRandomEntry) kmsRandomTouched = true;
                else if (n.StartsWith("item/", StringComparison.Ordinal) && st.Final != null) itemFiles.Add(st);
                else if (n.StartsWith("itemdata/", StringComparison.Ordinal) && st.Final != null) kmsItemFiles.Add(st);
            }
            if (touchedFlat.Count == 0 && !kmsTouched && !kmsRandomTouched && itemFiles.Count == 0 && kmsItemFiles.Count == 0) return;
            Plan plan = run.Plan;
            foreach (EntryState st in itemFiles) if (!flatArchives.Contains(st.Archive)) flatArchives.Add(st.Archive);

            // the flat rows of each archive and kind
            var flats = new Dictionary<string, OptionFlat>(StringComparer.OrdinalIgnoreCase);
            foreach (string archive in flatArchives)
            {
                M2dArchive arch;
                try { arch = run.Data.Open(archive); }
                catch (ModKitException) { continue; }
                foreach (M2dEntry e in arch.Entries)
                {
                    OptionKind k;
                    string n = e.Name.Replace('\\', '/').ToLowerInvariant();
                    if (!ItemOptionRules.IsFlatEntry(n, out k)) continue;
                    string fk = archive + "\n" + ItemOptionRules.KindName(k);
                    OptionFlat f;
                    if (!flats.TryGetValue(fk, out f)) flats[fk] = f = new OptionFlat { Archive = archive, Kind = k };
                    bool touched = touchedFlat.Contains(archive + "\n" + n), read = ItemOptionRules.ServerReads(k, n);
                    XmlDoc orig = OriginalDoc(run, arch, archive, e);
                    XmlDoc fin = touched ? FinalDoc(run, archive, e.Name) : orig;
                    var origRows = new Dictionary<string, XmlElem>();
                    if (orig != null && orig.Root != null)
                        foreach (XmlElem r in orig.Root.Children)
                        {
                            if (r.Name != "option") continue;
                            foreach (XmlAttr a in r.Attrs) if (!ItemOptionRules.IsKey(a.Name)) f.Stats.Add(a.Name);
                            origRows[OptKey(r)] = r;
                            if (!read) continue;
                            OptList(f.Original, OptKey(r)).Add(e.Name);
                            f.OriginalRow[OptKey(r)] = r;
                        }
                    if (fin == null || fin.Root == null) continue;
                    foreach (XmlElem r in fin.Root.Children)
                    {
                        if (r.Name != "option") continue;
                        string key = OptKey(r);
                        if (read)
                        {
                            OptList(f.Final, key).Add(e.Name);
                            f.FinalRow[key] = r;
                            f.Codes.Add((r.Attr("code") ?? "").Trim());
                        }
                        XmlElem had;
                        if (touched && (!origRows.TryGetValue(key, out had) || RowText(had) != RowText(r)))
                        {
                            f.Edited.Add(new KeyValuePair<string, string>(key, e.Name));
                            if (!read && run.Data.Has(Profile.Server) && Profile.Server.ArchiveIn("x:Xml.m2d") == archive)
                                plan.Msg(plan.Warnings, EntryMod(run, archive, e.Name), archive, e.Name, OptLabel(key) + ": the server does not read this file");
                        }
                    }
                }
            }

            foreach (OptionFlat f in flats.Values)
            {
                foreach (KeyValuePair<string, List<string>> kv in f.Final)
                {
                    List<string> was;
                    int before = f.Original.TryGetValue(kv.Key, out was) ? was.Count : 0;
                    if (kv.Value.Count > 1 && kv.Value.Count > before)
                        plan.Msg(plan.Errors, EntryMod(run, f.Archive, kv.Value[kv.Value.Count - 1]), f.Archive, kv.Value[kv.Value.Count - 1],
                            OptLabel(kv.Key) + " is in " + string.Join(" and ", kv.Value.ToArray()) + ": the server's ingest stops at a second row of one code and grade");
                }
                foreach (KeyValuePair<string, string> ed in f.Edited)
                {
                    XmlElem row = null;
                    Dictionary<string, XmlElem> rows = OptRowsOf(run, f.Archive, ed.Value);
                    if (!rows.TryGetValue(ed.Key, out row)) continue;
                    Mod mod = EntryMod(run, f.Archive, ed.Value);
                    string label = OptLabel(ed.Key);
                    int stats = 0;
                    foreach (XmlAttr a in row.Attrs)
                    {
                        if (ItemOptionRules.IsKey(a.Name)) continue;
                        bool stat = a.Name.EndsWith(ItemOptionRules.ValueSuffix, StringComparison.Ordinal) || a.Name.EndsWith(ItemOptionRules.RateSuffix, StringComparison.Ordinal);
                        if (stat && !f.Stats.Contains(a.Name))
                            plan.Msg(plan.Errors, mod, f.Archive, ed.Value, label + ": no shipped " + ItemOptionRules.KindName(f.Kind) + " file has the stat " + a.Name);
                        string bad = ItemOptionRules.CheckValue(f.Kind, false, a.Name, a.Value);
                        if (bad != null) plan.Msg(plan.Errors, mod, f.Archive, ed.Value, label + ": " + bad);
                        if (stat && ItemOptionRules.Normal(a.Value).Length > 0) stats++;
                    }
                    if (f.Kind == OptionKind.Constant) continue;
                    string pick = ItemOptionRules.Normal(row.Attr(ItemOptionRules.Pick));
                    if (pick.Length == 0)
                    {
                        // a random row that picks nothing is how the shipped rows of the low grades are
                        if (f.Kind == OptionKind.Static) plan.Msg(plan.Warnings, mod, f.Archive, ed.Value, label + ": optionNumPick is empty or 0,0, so the server skips the row");
                    }
                    else
                    {
                        string[] p = pick.Split(',');
                        int hi;
                        if (p.Length == 2 && int.TryParse(p[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out hi) && hi > stats)
                            plan.Msg(plan.Warnings, mod, f.Archive, ed.Value, label + ": optionNumPick picks up to " + hi + " lines but the row lists " + stats + " stats");
                    }
                }
            }

            // the kms table
            Dictionary<string, XmlElem> kmsFinal = null, kmsOrig = null;
            string kmsArchive = run.Data.Has(Profile.KmsClient) ? Profile.KmsClient.ArchiveIn("x:Xml.m2d") : null;
            if (kmsArchive != null)
            {
                M2dArchive arch = null;
                try { arch = run.Data.Open(kmsArchive); }
                catch (ModKitException) { }
                M2dEntry e = arch == null ? null : arch.Find(ItemOptionRules.KmsEntry);
                if (e != null && (kmsTouched || touchedFlat.Count > 0 || kmsItemFiles.Count > 0))
                {
                    XmlDoc orig = OriginalDoc(run, arch, kmsArchive, e);
                    XmlDoc fin = kmsTouched ? FinalDoc(run, kmsArchive, e.Name) : orig;
                    kmsOrig = KmsRows(orig, null);
                    var twice = new List<string>();
                    kmsFinal = KmsRows(fin, twice);
                    Mod mod = EntryMod(run, kmsArchive, e.Name);
                    if (kmsTouched)
                    {
                        var origTwice = new List<string>();
                        KmsRows(orig, origTwice);
                        foreach (string c in twice)
                            if (!origTwice.Contains(c)) plan.Msg(plan.Errors, mod, kmsArchive, e.Name, "option " + c + " is in the table twice");
                        foreach (KeyValuePair<string, XmlElem> kv in kmsFinal)
                        {
                            XmlElem had;
                            if (kmsOrig.TryGetValue(kv.Key, out had) && had.OuterText == kv.Value.OuterText) continue;
                            foreach (XmlElem rank in kv.Value.Children)
                                foreach (XmlElem v in rank.Children)
                                {
                                    if (v.Name != "v") continue;
                                    string bad = ItemOptionRules.CheckValue(OptionKind.Constant, true, v.Attr("name") ?? "?", v.Attr("value"));
                                    if (bad != null) plan.Msg(plan.Errors, mod, kmsArchive, e.Name, "option " + kv.Key + ": " + bad);
                                }
                        }
                    }
                }
            }

            // the kms random table
            Dictionary<string, XmlElem> kmsRandomFinal = null;
            if (kmsArchive != null)
            {
                M2dArchive arch = null;
                try { arch = run.Data.Open(kmsArchive); }
                catch (ModKitException) { }
                M2dEntry e = arch == null ? null : arch.Find(ItemOptionRules.KmsRandomEntry);
                if (e != null && (kmsRandomTouched || kmsItemFiles.Count > 0))
                {
                    XmlDoc orig = OriginalDoc(run, arch, kmsArchive, e);
                    XmlDoc fin = kmsRandomTouched ? FinalDoc(run, kmsArchive, e.Name) : orig;
                    var twice = new List<string>();
                    kmsRandomFinal = KmsRows(fin, twice);
                    if (kmsRandomTouched)
                    {
                        Mod mod = EntryMod(run, kmsArchive, e.Name);
                        var origTwice = new List<string>();
                        Dictionary<string, XmlElem> kmsRandomOrig = KmsRows(orig, origTwice);
                        foreach (string c in twice)
                            if (!origTwice.Contains(c)) plan.Msg(plan.Errors, mod, kmsArchive, e.Name, "random option " + c + " is in the table twice");
                        HashSet<string> variation = null;
                        foreach (KeyValuePair<string, XmlElem> kv in kmsRandomFinal)
                        {
                            XmlElem had;
                            if (kmsRandomOrig.TryGetValue(kv.Key, out had) && had.OuterText == kv.Value.OuterText) continue;
                            string label = "random option " + kv.Key + ": ";
                            foreach (XmlAttr a in kv.Value.Attrs)
                            {
                                string bad = ItemOptionRules.CheckKmsRandom(a.Name, a.Value);
                                if (bad != null) plan.Msg(plan.Errors, mod, kmsArchive, e.Name, label + bad);
                            }
                            if (variation == null) variation = VariationIds(run, kmsArchive);
                            int lines = 0;
                            foreach (XmlElem v in kv.Value.Children)
                            {
                                if (v.Name != "v") continue;
                                lines++;
                                string name = v.Attr("name") ?? "?";
                                foreach (string at in new[] { "statID", "weight" })
                                {
                                    string bad = ItemOptionRules.CheckKmsRandom(at, v.Attr(at));
                                    if (bad != null) plan.Msg(plan.Errors, mod, kmsArchive, e.Name, label + "line " + name + ": " + bad);
                                }
                                string id = (v.Attr("statID") ?? "").Trim();
                                if (variation != null && ItemOptionRules.CheckKmsRandom("statID", id) == null && !variation.Contains(id))
                                    plan.Msg(plan.Warnings, mod, kmsArchive, e.Name, label + "line " + name + ": statID " + (id.Length == 0 ? "(none)" : id) + " has no row in "
                                        + ItemOptionRules.KmsVariationEntry + ", so the client has no range for the line");
                            }
                            int pick = ItemOptionRules.KmsPickCount(kv.Value.Attr("pickCount"), null, 0);
                            if (pick > lines)
                                plan.Msg(plan.Warnings, mod, kmsArchive, e.Name, label + "pickCount picks " + pick + " lines but the row lists " + lines);
                        }
                    }
                }
            }

            // kms and server side by side, for the codes the plan changes
            OptionFlat server;
            string serverArchive = run.Data.Has(Profile.Server) ? Profile.Server.ArchiveIn("x:Xml.m2d") : null;
            if (kmsFinal != null && serverArchive != null && flats.TryGetValue(serverArchive + "\nconstant", out server))
            {
                var codes = new HashSet<string>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> ed in server.Edited) codes.Add(ed.Key.Substring(0, ed.Key.IndexOf('\n')));
                foreach (KeyValuePair<string, XmlElem> kv in kmsFinal)
                {
                    XmlElem had;
                    if (!kmsOrig.TryGetValue(kv.Key, out had) || had.OuterText != kv.Value.OuterText) codes.Add(kv.Key);
                }
                foreach (string code in codes)
                {
                    XmlElem kf, ko;
                    if (!kmsFinal.TryGetValue(code, out kf)) continue;
                    kmsOrig.TryGetValue(code, out ko);
                    var differ = new List<string>();
                    for (int g = 1; g <= ItemOptionRules.MaxGrade; g++)
                    {
                        string key = code + "\n" + g.ToString(CultureInfo.InvariantCulture);
                        XmlElem sf, so;
                        if (!server.FinalRow.TryGetValue(key, out sf)) continue;
                        server.OriginalRow.TryGetValue(key, out so);
                        XmlElem rf = KmsRank(kf, g), ro = KmsRank(ko, g);
                        if (rf == null || (rf.Attr("createType") ?? "").Trim() == "3") continue;
                        var names = new HashSet<string>(StringComparer.Ordinal);
                        foreach (XmlElem v in rf.Children) if (v.Name == "v" && v.Attr("name") != null) names.Add(v.Attr("name"));
                        foreach (XmlAttr a in sf.Attrs) { string kn = ItemOptionRules.KmsName(a.Name); if (kn != null) names.Add(kn); }
                        foreach (string nm in names)
                        {
                            string fv = ItemOptionRules.Normal(sf.Attr(ItemOptionRules.FlatName(nm))), kv2 = ItemOptionRules.Normal(KmsV(rf, nm));
                            if (fv == kv2) continue;
                            bool agreed = so != null && ro != null && ItemOptionRules.Normal(so.Attr(ItemOptionRules.FlatName(nm))) == ItemOptionRules.Normal(KmsV(ro, nm));
                            if (agreed || so == null && ro == null) differ.Add("grade " + g + " " + nm + " (kms client " + (kv2.Length == 0 ? "none" : kv2) + ", server " + (fv.Length == 0 ? "none" : fv) + ")");
                        }
                    }
                    if (differ.Count > 0)
                        plan.Msg(plan.Warnings, EntryMod(run, kmsArchive, ItemOptionRules.KmsEntry) ?? FirstEditor(run, server, code), null, null,
                            "option " + code + ": the kms client shows other stats than the server applies: " + string.Join("; ", differ.ToArray()));
                }
            }

            // a code that loses a grade row it shipped with (a code the plan leaves with no row at all is the
            // item check's error)
            foreach (OptionFlat f in flats.Values)
                foreach (string key in f.Original.Keys)
                {
                    string code = key.Substring(0, key.IndexOf('\n'));
                    if (f.Final.ContainsKey(key) || !f.Codes.Contains(code)) continue;
                    plan.Msg(plan.Warnings, FirstEditor(run, f, code), f.Archive, null, OptLabel(key) + ": the code ships a row at this grade and the mod leaves none, so its items at this rarity get no "
                        + ItemOptionRules.KindName(f.Kind) + " stats");
                }

            // the items' pointers
            foreach (EntryState st in itemFiles)
            {
                XmlDoc d = FinalDoc(run, st.Archive, st.Name);
                if (d == null || d.Root == null) continue;
                XmlDoc o0 = ShippedDoc(run, st.Archive, st.Name);
                var oldOpts = o0 == null || o0.Root == null ? new List<XmlElem>() : new List<XmlElem>(OptionElems(o0.Root));
                Mod mod = EntryMod(run, st.Archive, st.Name.Replace('\\', '/'));
                foreach (OptionKind k in ItemOptionRules.Kinds)
                {
                    OptionFlat f;
                    if (!flats.TryGetValue(st.Archive + "\n" + ItemOptionRules.KindName(k), out f)) continue;
                    string field = ItemOptionRules.KindName(k);
                    int i = 0;
                    foreach (XmlElem o in OptionElems(d.Root))
                    {
                        string c = (o.Attr(field) ?? "").Trim();
                        string was = i < oldOpts.Count ? (oldOpts[i].Attr(field) ?? "").Trim() : "";
                        i++;
                        if (c.Length == 0 || c == "0") continue;
                        if (!f.Codes.Contains(c))
                        {
                            plan.Msg(plan.Errors, mod, st.Archive, st.Name,
                                "the item names " + ItemOptionRules.KindName(k) + " option " + c + " but no " + ItemOptionRules.KindName(k) + " option file the server reads has it");
                            continue;
                        }
                        if (was.Length == 0 || was == "0" || was == c) continue;
                        var lost = new List<string>();
                        for (int g = 0; g <= ItemOptionRules.MaxGrade; g++)
                        {
                            string gs = g.ToString(CultureInfo.InvariantCulture);
                            if (f.Final.ContainsKey(was + "\n" + gs) && !f.Final.ContainsKey(c + "\n" + gs)) lost.Add(gs);
                        }
                        if (lost.Count > 0)
                            plan.Msg(plan.Warnings, mod, st.Archive, st.Name, "the item moves from " + ItemOptionRules.KindName(k) + " option " + was + " to " + c + ", which has no row at grade "
                                + string.Join(",", lost.ToArray()) + ": made at that rarity, the item gets no " + ItemOptionRules.KindName(k) + " stats");
                    }
                }
            }

            // the kms items' constantID and randomID: an item that named a row of the kms table and now names a code
            // the table lacks
            foreach (OptionKind kind in new[] { OptionKind.Constant, OptionKind.Random })
            {
                Dictionary<string, XmlElem> final = kind == OptionKind.Constant ? kmsFinal : kmsRandomFinal;
                string field = kind == OptionKind.Constant ? "constantID" : "randomID";
                if (final == null) continue;
                foreach (EntryState st in kmsItemFiles)
                {
                    XmlDoc d = FinalDoc(run, st.Archive, st.Name);
                    if (d == null || d.Root == null) continue;
                    // item id -> its pointers, option by option
                    var shipped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    XmlDoc o0 = ShippedDoc(run, st.Archive, st.Name);
                    if (o0 != null && o0.Root != null)
                        foreach (XmlElem it in o0.Root.Children)
                        {
                            string iid = (it.Attr("id") ?? "").Trim();
                            if (shipped.ContainsKey(iid)) continue;
                            var l = new List<string>();
                            foreach (XmlElem o in OptionElems(it)) l.Add((o.Attr(field) ?? "").Trim());
                            shipped[iid] = l;
                        }
                    foreach (XmlElem it in d.Root.Children)
                    {
                        string iid = (it.Attr("id") ?? "").Trim();
                        List<string> olds;
                        if (!shipped.TryGetValue(iid, out olds)) continue;
                        int i = 0;
                        foreach (XmlElem o in OptionElems(it))
                        {
                            string was = i < olds.Count ? olds[i] : "";
                            i++;
                            string c = (o.Attr(field) ?? "").Trim();
                            if (!final.ContainsKey(was) || c.Length == 0 || c == "0" || c == was || final.ContainsKey(c)) continue;
                            plan.Msg(plan.Errors, EntryMod(run, st.Archive, st.Name.Replace('\\', '/')), st.Archive, st.Name,
                                "item " + iid + " names " + ItemOptionRules.KindName(kind) + " option " + c + " (" + field + ", was " + was + ") but the kms table " + ItemOptionRules.KmsEntryOf(kind) + " has no row of it");
                        }
                    }
                }
            }
        }

        // the ids of the kms variation table as the plan leaves it; null when the archive has none
        static HashSet<string> VariationIds(Run run, string kmsArchive)
        {
            XmlDoc d = FinalDoc(run, kmsArchive, ItemOptionRules.KmsVariationEntry);
            if (d == null || d.Root == null) return null;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlElem r in d.Root.Children) if (r.Name == "option" && r.Attr("id") != null) ids.Add(r.Attr("id").Trim());
            return ids;
        }

        // an entry as shipped (null when the archive has no such entry: a new file)
        static XmlDoc ShippedDoc(Run run, string archive, string entry)
        {
            M2dArchive arch;
            try { arch = run.Data.Open(archive); }
            catch (ModKitException) { return null; }
            M2dEntry e = arch.Find(entry);
            return e == null ? null : OriginalDoc(run, arch, archive, e);
        }

        static Mod FirstEditor(Run run, OptionFlat f, string code)
        {
            foreach (KeyValuePair<string, string> ed in f.Edited)
                if (ed.Key.StartsWith(code + "\n", StringComparison.Ordinal)) return EntryMod(run, f.Archive, ed.Value);
            return null;
        }

        static IEnumerable<XmlElem> OptionElems(XmlElem e)
        {
            foreach (XmlElem c in e.Children)
            {
                if (c.Name == "option") yield return c;
                else foreach (XmlElem d in OptionElems(c)) yield return d;
            }
        }

        static Dictionary<string, XmlElem> OptRowsOf(Run run, string archive, string entry)
        {
            var m = new Dictionary<string, XmlElem>();
            XmlDoc d = FinalDoc(run, archive, entry);
            if (d == null || d.Root == null) return m;
            foreach (XmlElem r in d.Root.Children) if (r.Name == "option") m[OptKey(r)] = r;
            return m;
        }

        static Dictionary<string, XmlElem> KmsRows(XmlDoc d, List<string> twice)
        {
            var m = new Dictionary<string, XmlElem>(StringComparer.Ordinal);
            if (d == null || d.Root == null) return m;
            foreach (XmlElem r in d.Root.Children)
            {
                if (r.Name != "option") continue;
                string c = (r.Attr("code") ?? "").Trim();
                if (m.ContainsKey(c)) { if (twice != null && !twice.Contains(c)) twice.Add(c); continue; }
                m[c] = r;
            }
            return m;
        }

        // the rank the client takes for grade g: the highest grade at or below g (a rank without grade is 0)
        static XmlElem KmsRank(XmlElem option, int g)
        {
            if (option == null) return null;
            XmlElem best = null;
            int bestGrade = int.MinValue;
            foreach (XmlElem r in option.Children)
            {
                if (r.Name != "rank") continue;
                int rg;
                if (!int.TryParse(r.Attr("grade") ?? "0", NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out rg)) rg = 0;
                if (rg <= g && rg >= bestGrade) { best = r; bestGrade = rg; }
            }
            return best;
        }

        static string KmsV(XmlElem rank, string name)
        {
            if (rank == null) return "";
            foreach (XmlElem v in rank.Children) if (v.Name == "v" && v.Attr("name") == name) return v.Attr("value") ?? "";
            return "";
        }
    }
}
