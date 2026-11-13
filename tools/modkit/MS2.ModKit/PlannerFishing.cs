// The plan's checks of the fishing tables of the server (see Fishing.cs), on the rows as the mods leave them: the rows
// the mods add and the rows whose text they change, in table/Server/fish.xml, fishingSpot.xml, fishLure.xml,
// globalFishBox.xml and individualFishBox.xml.
// Errors: what the server's ingest cannot read (a number, a boolean, a list, a size that is not written, a lure whose
// chances are fewer than its ranks) and what stops a catch (sizes out of order, pointCount 0, a bigSize too big).
// Warnings: a row the server does not keep (its feature or locale loses to another row with the key, or is off), a
// fish box a spot names that is not there, a line for a fish that is not there, a fish no box has, a fish no spot gives
// (its habitat or mastery), a spot where nothing can be caught, a box no spot names, a fish code twice in a box, and a
// fish or spot that the client's table/fish.xml or table/fishingspot.xml does not have or has with other values (the
// client shows its own rows in the fish book and on the fishing screen).
using System;
using System.Collections.Generic;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        sealed class FishTab
        {
            public FishTable Table;
            public Mod Mod;
            public List<XmlElem> Rows = new List<XmlElem>();
            public HashSet<XmlElem> Changed = new HashSet<XmlElem>();
            public Dictionary<long, XmlElem> Kept = new Dictionary<long, XmlElem>();
            public HashSet<long> Shipped = new HashSet<long>();
        }

        static void FishingChecks(Run run)
        {
            Plan plan = run.Plan;
            const string archive = FishingRules.Archive;
            if (!run.Data.Exists(archive)) return;
            var tabs = new Dictionary<FishTable, FishTab>();
            bool any = false;
            foreach (FishTable t in FishingRules.Tables)
            {
                var tab = new FishTab { Table = t, Mod = LevelMod(run, archive, FishingRules.Entry(t)) };
                tabs[t] = tab;
                any |= tab.Mod != null;
            }
            if (!any) return;
            FeatureSet set = FishFeatures(run);
            foreach (FishTab tab in tabs.Values)
            {
                string entry = FishingRules.Entry(tab.Table), row = FishingRules.Row(tab.Table);
                XmlDoc fin = FinalDoc(run, archive, entry);
                if (fin == null || fin.Root == null) continue;
                foreach (XmlElem e in fin.Root.Children) if (e.Name == row) tab.Rows.Add(e);
                if (tab.Mod != null)
                {
                    // the shipped rows' texts, as many times as they are shipped: a row the mod adds as a copy is not one of them
                    var then = new Dictionary<string, int>(StringComparer.Ordinal);
                    XmlDoc was = OrigDoc(run, archive, entry);
                    if (was != null && was.Root != null)
                        foreach (XmlElem e in was.Root.Children)
                            if (e.Name == row) { int k; then[e.OuterText] = then.TryGetValue(e.OuterText, out k) ? k + 1 : 1; tab.Shipped.Add(FishKey(e, tab.Table)); }
                    foreach (XmlElem e in tab.Rows)
                    {
                        int left;
                        if (then.TryGetValue(e.OuterText, out left) && left > 0) then[e.OuterText] = left - 1;
                        else tab.Changed.Add(e);
                    }
                }
                // the row the server keeps of each key
                var best = new Dictionary<long, XmlElem>();
                foreach (XmlElem e in tab.Rows)
                {
                    long key = FishKey(e, tab.Table);
                    if (key < 0) continue;
                    string f = e.Attr(FishingRules.Feature) ?? "", l = e.Attr(FishingRules.Locale) ?? "";
                    XmlElem b;
                    if (set == null) { if (!best.ContainsKey(key)) best[key] = e; continue; }
                    if (!set.Allows(f, l)) continue;
                    if (!best.TryGetValue(key, out b) || set.SecondWins(b.Attr(FishingRules.Feature) ?? "", b.Attr(FishingRules.Locale) ?? "", f, l)) best[key] = e;
                }
                tab.Kept = best;
            }

            FishTab fish = tabs[FishTable.Fish], spots = tabs[FishTable.Spot];
            XmlDoc[] clientFish = FishClient(run, FishingRules.ClientFishEntry), clientSpots = FishClient(run, FishingRules.ClientSpotEntry);
            foreach (FishTab tab in tabs.Values)
            {
                if (tab.Mod == null) continue;
                string entry = FishingRules.Entry(tab.Table);
                foreach (XmlElem e in tab.Changed)
                {
                    long key = FishKey(e, tab.Table);
                    string id = (e.Attr(FishingRules.Key(tab.Table)) ?? "").Trim();
                    string what = FishingRules.Noun(tab.Table) + " " + (id.Length == 0 ? "(no " + FishingRules.Key(tab.Table) + ")" : id) + ": ";
                    if (tab.Table == FishTable.Lure) what = "lure " + (id.Length == 0 ? "(no additionalEffectCode)" : id) + " level " + ((e.Attr("additionalEffectLevel") ?? "").Trim()) + ": ";
                    var errors = new List<string>();
                    var warnings = new List<string>();
                    XmlElem el = e;
                    XmlElem kept;
                    bool isKept = key >= 0 && tab.Kept.TryGetValue(key, out kept) && kept == e;
                    // the parser reads every row; the ingest's splits and indexes and the catch see only the kept one
                    Func<string, string> get = f => { string v = el.Attr(f); return string.IsNullOrEmpty(v) ? null : v; };
                    if (isKept) FishingRules.Check(tab.Table, get, errors, warnings);
                    else FishingRules.CheckRead(tab.Table, get, errors);
                    if (key >= 0 && !isKept)
                    {
                        string f = e.Attr(FishingRules.Feature) ?? "", l = e.Attr(FishingRules.Locale) ?? "";
                        string why = set != null && !set.Allows(f, l)
                            ? "its feature \"" + f + "\"" + (l.Length > 0 ? " and locale \"" + l + "\"" : "") + " are off (" + set.Text + "): the server leaves it out"
                            : "another row with " + FishingRules.Key(tab.Table) + " " + key + " wins by its feature and locale" + (set != null ? " (" + set.Text + ")" : "") + ": the server leaves this one out";
                        if (tab.Table == FishTable.Lure) why += " (the server keys a lure by additionalEffectCode alone, so of its levels it keeps one)";
                        warnings.Add(why);
                    }
                    if (isKept)
                    {
                        int others = 0;
                        foreach (XmlElem o in tab.Rows) if (o != e && FishKey(o, tab.Table) == key) others++;
                        if (others > 0)
                            warnings.Add("the server keeps this row and leaves out the " + (others == 1 ? "other row" : others + " other rows") + " with " + FishingRules.Key(tab.Table) + " " + key
                                + (tab.Table == FishTable.Lure ? " (it keys a lure by additionalEffectCode alone, so of its levels it keeps one)" : ""));
                        if (tab.Table == FishTable.Fish) FishOfFish(e, key, tabs, warnings);
                        else if (tab.Table == FishTable.Spot) FishOfSpot(e, tabs, warnings);
                        else if (FishingRules.IsBox(tab.Table)) FishOfBox(e, tab.Table, tabs, warnings);
                        if (tab.Table == FishTable.Fish) FishClientCheck(e, key, clientFish, "table/fish.xml", new[] { "fishingBook", FishingRules.Habitat, FishingRules.Mastery, "rank", FishingRules.SmallSize, FishingRules.BigSize }, warnings);
                        if (tab.Table == FishTable.Spot) FishClientCheck(e, key, clientSpots, "table/fishingspot.xml", new[] { FishingRules.MinMastery, FishingRules.MaxMastery, FishingRules.Liquid }, warnings);
                    }
                    if (FishingRules.IsBox(tab.Table))
                    {
                        int n = 0;
                        foreach (XmlElem c in e.Children)
                        {
                            if (c.Name != FishingRules.Line) continue;
                            n++;
                            XmlElem cl = c;
                            var le = new List<string>();
                            var lw = new List<string>();
                            Func<string, string> lget = f => { string v = cl.Attr(f); return string.IsNullOrEmpty(v) ? null : v; };
                            if (isKept) FishingRules.CheckLine(lget, le, lw);
                            else FishingRules.CheckLineRead(lget, le);
                            string code = (c.Attr(FishingRules.FishCode) ?? "").Trim();
                            foreach (string s in le) errors.Add("line " + n + " (fish " + code + "): " + s);
                            foreach (string s in lw) warnings.Add("line " + n + " (fish " + code + "): " + s);
                        }
                    }
                    foreach (string s in errors) plan.Msg(plan.Errors, tab.Mod, archive, entry, what + s);
                    foreach (string s in warnings) plan.Msg(plan.Warnings, tab.Mod, archive, entry, what + s);
                }
            }
            FishGoneBoxes(plan, tabs);
        }

        // a shipped box the mods take away that a spot they leave as shipped still names
        static void FishGoneBoxes(Plan plan, Dictionary<FishTable, FishTab> tabs)
        {
            FishTab spots = tabs[FishTable.Spot];
            foreach (FishTable t in new[] { FishTable.GlobalBox, FishTable.IndividualBox })
            {
                FishTab tab = tabs[t];
                if (tab.Mod == null) continue;
                string field = t == FishTable.GlobalBox ? FishingRules.GlobalBox : FishingRules.IndividualBox;
                foreach (XmlElem s in spots.Kept.Values)
                {
                    if (spots.Changed.Contains(s)) continue;        // its own check says so
                    long n = FishInt(s, field);
                    if (n > 0 && tab.Shipped.Contains(n) && !tab.Kept.ContainsKey(n))
                        plan.Msg(plan.Warnings, tab.Mod, FishingRules.Archive, FishingRules.Entry(t), FishingRules.Noun(t) + " " + n + ": the mod takes it away, and fishing spot "
                            + (s.Attr("id") ?? "").Trim() + " still names it in " + field + ": nothing of the box bites there");
                }
            }
        }

        // the key of a row as the ingest reads it ("" and a missing one read as 0); -1 when it cannot be read
        static long FishKey(XmlElem e, FishTable t)
        {
            string v = e.Attr(FishingRules.Key(t));
            int n;
            return FishingRules.Int(string.IsNullOrEmpty(v) ? null : v, out n) ? n : -1;
        }

        static int FishInt(XmlElem e, string field)
        {
            string v = e.Attr(field);
            int n;
            return FishingRules.Int(string.IsNullOrEmpty(v) ? null : v, out n) ? n : 0;
        }

        static bool FishBool(XmlElem e, string field)
        {
            string v = (e.Attr(field) ?? "").Trim();
            return v == "1" || v == "true";
        }

        // the features of the server after the plan so far; null when they cannot be known
        static FeatureSet FishFeatures(Run run)
        {
            string ar = Profile.Server.Root + ":Xml.m2d";
            if (!run.Data.Exists(ar)) return null;
            try
            {
                M2dArchive arch = run.Data.Open(ar);
                string locale, type;
                run.Data.FeatureOption(Profile.Server, out locale, out type);
                return FeatureSet.Read(PlannedEntry(run, ar, arch, FeatureSet.SettingTable), PlannedEntry(run, ar, arch, FeatureSet.FeatureTable), locale, type, true);
            }
            catch (ModKitException) { return null; }
        }

        // the client tables (Korean, then global) as the plan leaves them; null for a client the data set does not have
        static XmlDoc[] FishClient(Run run, string entry)
        {
            var l = new XmlDoc[2];
            Profile[] ps = { Profile.KmsClient, Profile.GmsClient };
            for (int i = 0; i < 2; i++)
            {
                string ar = ps[i].ArchiveIn("x:Xml.m2d");
                if (run.Data.Has(ps[i]) && run.Data.Exists(ar)) l[i] = FinalDoc(run, ar, entry);
            }
            return l;
        }

        static void FishClientCheck(XmlElem e, long key, XmlDoc[] client, string entry, string[] fields, List<string> warnings)
        {
            string[] names = { "Korean", "global" };
            for (int i = 0; i < client.Length; i++)
            {
                XmlDoc d = client[i];
                if (d == null || d.Root == null) continue;
                XmlElem same = null;
                string diff = null;
                foreach (XmlElem c in d.Root.Children)
                {
                    if (c.Name != e.Name || FishKey(c, FishTable.Fish) != key) continue;
                    var l = new List<string>();
                    foreach (string f in fields)
                    {
                        string a = (e.Attr(f) ?? "").Trim(), b = (c.Attr(f) ?? "").Trim();
                        if (a != b) l.Add(f + " \"" + b + "\" (the server's \"" + a + "\")");
                    }
                    if (l.Count == 0) { same = c; break; }
                    if (diff == null) diff = string.Join(", ", l.ToArray());
                }
                if (same != null) continue;
                if (diff == null) warnings.Add("the " + names[i] + " client's " + entry + " has no row for it: the client does not show it (change the client table too)");
                else warnings.Add("the " + names[i] + " client's " + entry + " has " + diff + ": the client shows its own values (change the client table too)");
            }
        }

        // the fish codes of a box's lines, as the ingest reads them
        static List<long> FishCodes(XmlElem box)
        {
            var l = new List<long>();
            foreach (XmlElem c in box.Children) if (c.Name == FishingRules.Line) l.Add(FishInt(c, FishingRules.FishCode));
            return l;
        }

        // does the spot turn the map's water tiles into seawater (it lists seawater)
        static bool FishSeaSpot(XmlElem spot)
        {
            return FishingRules.LiquidsOf(spot.Attr(FishingRules.Liquid)).Contains(FishingRules.Seawater);
        }

        // can the spot give the fish, as far as the tables tell: the tiles are the map's fluids near the player (the
        // spot's list does not choose them), so its habitat only rules it out when it is all, or water on a spot that
        // lists seawater (each water tile counts as seawater there); then its masteries
        static bool FishGives(XmlElem spot, XmlElem fish)
        {
            int h = FishingRules.HabitatOf(string.IsNullOrEmpty(fish.Attr(FishingRules.Habitat)) ? null : fish.Attr(FishingRules.Habitat));
            if (h == FishingRules.All || (h == FishingRules.Water && FishSeaSpot(spot))) return false;
            if (FishBool(fish, FishingRules.Ignore)) return true;
            int m = FishInt(fish, FishingRules.Mastery);
            return m >= FishInt(spot, FishingRules.MinMastery) && m <= FishInt(spot, FishingRules.MaxMastery);
        }

        static List<XmlElem> FishBoxesOf(XmlElem spot, Dictionary<FishTable, FishTab> tabs)
        {
            var l = new List<XmlElem>();
            XmlElem b;
            if (tabs[FishTable.GlobalBox].Kept.TryGetValue(FishInt(spot, FishingRules.GlobalBox), out b)) l.Add(b);
            if (tabs[FishTable.IndividualBox].Kept.TryGetValue(FishInt(spot, FishingRules.IndividualBox), out b)) l.Add(b);
            return l;
        }

        static void FishOfFish(XmlElem e, long key, Dictionary<FishTable, FishTab> tabs, List<string> warnings)
        {
            var boxes = new List<XmlElem>();
            foreach (FishTable t in new[] { FishTable.GlobalBox, FishTable.IndividualBox })
                foreach (XmlElem b in tabs[t].Kept.Values) if (FishCodes(b).Contains(key)) boxes.Add(b);
            if (boxes.Count == 0) { warnings.Add("no fish box has a line for it: it is not caught"); return; }
            foreach (XmlElem s in tabs[FishTable.Spot].Kept.Values)
            {
                bool names = false;
                foreach (XmlElem b in FishBoxesOf(s, tabs)) if (boxes.Contains(b)) names = true;
                if (names && FishGives(s, e)) return;
            }
            warnings.Add("no fishing spot gives it: the spots that name its boxes give other masteries than its " + FishInt(e, FishingRules.Mastery)
                + (FishingRules.HabitatOf(string.IsNullOrEmpty(e.Attr(FishingRules.Habitat)) ? null : e.Attr(FishingRules.Habitat)) == FishingRules.Water ? ", or list seawater (their water tiles count as seawater, so a water fish does not bite)" : ""));
        }

        static void FishOfSpot(XmlElem e, Dictionary<FishTable, FishTab> tabs, List<string> warnings)
        {
            foreach (FishTable t in new[] { FishTable.GlobalBox, FishTable.IndividualBox })
            {
                int n = FishInt(e, t == FishTable.GlobalBox ? FishingRules.GlobalBox : FishingRules.IndividualBox);
                if (n > 0 && !tabs[t].Kept.ContainsKey(n)) warnings.Add("there is no " + FishingRules.Noun(t) + " " + n + " in " + FishingRules.Entry(t));
            }
            Dictionary<long, XmlElem> fish = tabs[FishTable.Fish].Kept;
            foreach (XmlElem b in FishBoxesOf(e, tabs))
                foreach (long c in FishCodes(b))
                {
                    XmlElem f;
                    if (fish.TryGetValue(c, out f) && FishGives(e, f)) return;
                }
            if (FishBoxesOf(e, tabs).Count > 0) warnings.Add("nothing can be caught: no fish of its boxes bites at its masteries"
                + (FishSeaSpot(e) ? " (it lists seawater, so the map's water tiles count as seawater and a water fish does not bite)" : ""));
        }

        static void FishOfBox(XmlElem e, FishTable t, Dictionary<FishTable, FishTab> tabs, List<string> warnings)
        {
            long id = FishKey(e, t);
            string field = t == FishTable.GlobalBox ? FishingRules.GlobalBox : FishingRules.IndividualBox;
            bool named = false;
            foreach (XmlElem s in tabs[FishTable.Spot].Kept.Values) if (FishInt(s, field) == id) { named = true; break; }
            if (!named) warnings.Add("no fishing spot names it in " + field);
            var codes = new List<string>();
            foreach (XmlElem c in e.Children) if (c.Name == FishingRules.Line) codes.Add(c.Attr(FishingRules.FishCode));
            if (codes.Count == 0) warnings.Add("it has no fish");
            foreach (int c in FishingRules.Twice(codes)) warnings.Add("fish " + c + " has more than one line: the server keeps the last weight");
            Dictionary<long, XmlElem> fish = tabs[FishTable.Fish].Kept;
            foreach (long c in FishCodes(e))
                if (c > 0 && !fish.ContainsKey(c)) warnings.Add("there is no fish " + c + " in " + FishingRules.FishEntry + ": the server leaves the line out");
        }
    }
}
