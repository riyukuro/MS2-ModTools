// The plan's checks of gacha capsules (table/gacha_info.xml, see Gacha.cs). Every capsule row the mods add or change
// is read as the server's ingest reads it (GachaRules): a value it cannot read is an error. Of the rows of one id the
// server keeps one, by the feature and locale it runs. A kept row is checked against the plan's drop boxes, items and
// clients; a removed row against the capsule items that still open it and the items won from it, which refund its coin
// when they are dismantled. A capsule item the mods change is checked for its OpenGachaBox parameter.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        const string GachaServerArchive = "server:Xml.m2d";

        static string GachaKeyOf(XmlElem e)
        {
            return (e.Attr(GachaRules.Key) ?? "").Trim() + "\n" + (e.Attr("feature") ?? "") + "\n" + (e.Attr("locale") ?? "");
        }

        static long GachaIdOf(XmlElem e)
        {
            long n;
            return long.TryParse((e.Attr(GachaRules.Key) ?? "").Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n) ? n : long.MinValue;
        }

        // the mod a gacha row is blamed on: the last mod whose edit of the table names the id, else the last that edits it
        static Mod GachaMod(Run run, string archive, string id)
        {
            Mod named = null, any = null;
            var inStep = new Regex(@"\[" + GachaRules.Key + @"=[""']?" + Regex.Escape(id) + @"[""']?\]");
            var inRow = new Regex(@"\s" + GachaRules.Key + @"\s*=\s*[""']" + Regex.Escape(id) + @"[""']");
            foreach (Mod m in run.Plan.Mods)
                foreach (ModOp op in m.Ops)
                {
                    var ed = op as EditOp;
                    var en = op as EntryOp;
                    if (en != null && string.Equals((en.Name ?? "").Replace('\\', '/'), GachaRules.Entry, StringComparison.OrdinalIgnoreCase)
                        && (archive == null || string.Equals(DataSet.NormalizeRef(en.Archive), DataSet.NormalizeRef(archive), StringComparison.OrdinalIgnoreCase))) any = m;
                    if (ed == null || !string.Equals(ed.Entry.Replace('\\', '/'), GachaRules.Entry, StringComparison.OrdinalIgnoreCase)) continue;
                    any = m;
                    foreach (RowOp r in ed.Rows)
                    {
                        bool hit = r.At != null && inStep.IsMatch(r.At) || r.Copy != null && inStep.IsMatch(r.Copy)
                            || r.Xml != null && inRow.IsMatch(r.Xml);
                        foreach (AttrOp a in r.Attrs) if (a.Name == GachaRules.Key && (a.Value ?? "").Trim() == id) hit = true;
                        if (hit) named = m;
                    }
                }
            return named ?? any;
        }

        sealed class GachaSide
        {
            internal Profile Profile;
            internal string Archive;
            internal List<XmlElem> Rows = new List<XmlElem>();
            internal readonly HashSet<XmlElem> Changed = new HashSet<XmlElem>();
            internal readonly Dictionary<string, XmlElem> Shipped = new Dictionary<string, XmlElem>(StringComparer.Ordinal);
            internal bool Touched;
            internal bool IsServer { get { return Profile == Profile.Server; } }
        }

        static GachaSide GachaSideOf(Run run, Profile p)
        {
            string archive = p.ArchiveIn("x:Xml.m2d");
            if (!run.Data.Has(p) || !run.Data.Exists(archive)) return null;
            M2dArchive arch;
            try { arch = run.Data.Open(archive); }
            catch (ModKitException) { return null; }
            M2dEntry e = arch.Find(GachaRules.Entry);
            EntryState st;
            if (e == null && !run.States.TryGetValue(archive + "\n" + GachaRules.Entry, out st)) return null;
            var s = new GachaSide { Profile = p, Archive = archive, Touched = LevelMod(run, archive, GachaRules.Entry) != null };
            XmlDoc fin = FinalDoc(run, archive, GachaRules.Entry);
            if (fin != null && fin.Root != null) foreach (XmlElem r in fin.Root.Children) if (r.Name == GachaRules.Rows) s.Rows.Add(r);
            var then = new Dictionary<string, int>(StringComparer.Ordinal);
            XmlDoc was = OrigDoc(run, archive, GachaRules.Entry);
            if (was != null && was.Root != null)
                foreach (XmlElem r in was.Root.Children)
                {
                    if (r.Name != GachaRules.Rows) continue;
                    int k;
                    then[r.OuterText] = then.TryGetValue(r.OuterText, out k) ? k + 1 : 1;
                    if (!s.Shipped.ContainsKey(GachaKeyOf(r))) s.Shipped[GachaKeyOf(r)] = r;
                }
            if (s.Touched)
                foreach (XmlElem r in s.Rows)
                {
                    int left;
                    if (then.TryGetValue(r.OuterText, out left) && left > 0) then[r.OuterText] = left - 1;
                    else s.Changed.Add(r);
                }
            return s;
        }

        // the row of each id a side keeps by the features (the first of an id when they are not known)
        static Dictionary<long, XmlElem> GachaKept(GachaSide s, FeatureSet set)
        {
            var best = new Dictionary<long, XmlElem>();
            if (s == null) return best;
            foreach (XmlElem e in s.Rows)
            {
                long id = GachaIdOf(e);
                if (id == long.MinValue) continue;
                string f = e.Attr("feature") ?? "", l = e.Attr("locale") ?? "";
                XmlElem b;
                if (set == null) { if (!best.ContainsKey(id)) best[id] = e; continue; }
                if (!set.Allows(f, l)) continue;
                if (!best.TryGetValue(id, out b) || set.SecondWins(b.Attr("feature") ?? "", b.Attr("locale") ?? "", f, l)) best[id] = e;
            }
            return best;
        }

        // the server's item entries the plan changes, adds or deletes
        static List<EntryState> GachaChangedItems(Run run)
        {
            var l = new List<EntryState>();
            foreach (EntryState st in run.Order)
            {
                if (!string.Equals(DataSet.NormalizeRef(st.Archive), DataSet.NormalizeRef(GachaServerArchive), StringComparison.OrdinalIgnoreCase)) continue;
                if (!GachaCapsules.IsItemEntry(st.Name)) continue;
                if (st.DeletedBy != null ? st.Existed : st.Final != null && (!st.Existed || !M2dWriter.BytesEqual(st.Final, st.Original))) l.Add(st);
            }
            return l;
        }

        // every capsule item of the server as the plan leaves it
        static List<GachaCapsule> GachaAllCapsules(Run run)
        {
            var l = new List<GachaCapsule>();
            M2dArchive arch;
            try { arch = run.Data.Open(GachaServerArchive); }
            catch (ModKitException) { return l; }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (M2dEntry e in arch.Entries)
            {
                if (!GachaCapsules.IsItemEntry(e.Name)) continue;
                seen.Add(e.Name);
                l.AddRange(GachaCapsules.Read(e.Name, PlannedBytes(run, GachaServerArchive, arch, e)));
            }
            foreach (EntryState st in run.Order)
            {
                if (!string.Equals(DataSet.NormalizeRef(st.Archive), DataSet.NormalizeRef(GachaServerArchive), StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Contains(st.Name) || !GachaCapsules.IsItemEntry(st.Name) || st.DeletedBy != null) continue;
                l.AddRange(GachaCapsules.Read(st.Name, st.Final ?? PlannedBytes(st)));
            }
            return l;
        }

        // the groups of a server drop box that have rows: -1 when the box is not there
        static int GachaBoxGroups(XmlDoc drops, string box)
        {
            if (drops == null || drops.Root == null) return -1;
            List<XmlElem> boxes = Children(drops.Root, DropMap.BoxNested, "dropBoxID", box);
            if (boxes.Count == 0) return -1;
            int n = 0;
            foreach (XmlElem b in boxes)
                foreach (XmlElem g in Children(b, DropMap.GroupNested, null, null))
                    if (Children(g, DropMap.RowNested, null, null).Count > 0) n++;
            return n;
        }

        static string GachaLabel(XmlElem e)
        {
            string id = (e.Attr(GachaRules.Key) ?? "").Trim();
            string f = e.Attr("feature") ?? "", l = e.Attr("locale") ?? "";
            return "capsule " + (id.Length == 0 ? "(no " + GachaRules.Key + ")" : id)
                + (f.Length > 0 || l.Length > 0 ? " (" + (f.Length > 0 ? "feature " + f : "") + (f.Length > 0 && l.Length > 0 ? ", " : "") + (l.Length > 0 ? "locale " + l : "") + ")" : "");
        }

        static void GachaChecks(Run run)
        {
            Plan plan = run.Plan;
            GachaSide server = GachaSideOf(run, Profile.Server);
            var clients = new List<GachaSide>();
            foreach (Profile p in new[] { Profile.KmsClient, Profile.GmsClient })
            {
                GachaSide c = GachaSideOf(run, p);
                if (c != null) clients.Add(c);
            }
            List<EntryState> items = run.Data.Has(Profile.Server) ? GachaChangedItems(run) : new List<EntryState>();
            bool touched = server != null && server.Touched;
            foreach (GachaSide c in clients) touched |= c.Touched;
            if (!touched && items.Count == 0) return;

            FeatureSet set = server != null ? FishFeatures(run) : null;
            Dictionary<long, XmlElem> kept = GachaKept(server, set);
            List<GachaCapsule> capsules = server != null && touched ? GachaAllCapsules(run) : null;
            Dictionary<long, List<GachaCapsule>> opened = capsules == null ? null : GachaCapsules.ByGacha(capsules);
            XmlDoc drops = null;
            HashSet<string> boxes = null;
            HashSet<long> serverItems = null;
            bool boxesRead = false, itemsRead = false;

            // the rows the server reads (else the first client's, when the data set has no server)
            GachaSide main = server ?? (clients.Count > 0 ? clients[0] : null);
            if (main != null && main.Touched)
            {
                foreach (XmlElem e in main.Changed)
                {
                    string id = (e.Attr(GachaRules.Key) ?? "").Trim();
                    Mod mod = GachaMod(run, main.Archive, id);
                    string label = GachaLabel(e);
                    var errors = new List<string>();
                    var warnings = new List<string>();
                    XmlElem el = e;
                    Func<string, string> get = f => el.Attr(f);
                    long gid = GachaIdOf(e);
                    XmlElem k;
                    bool isKept = !main.IsServer || gid != long.MinValue && kept.TryGetValue(gid, out k) && k == e;
                    if (isKept) GachaRules.Check(get, errors, warnings);
                    else GachaRules.Check(get, errors, new List<string>());
                    if (!isKept && gid != long.MinValue)
                    {
                        string f = e.Attr("feature") ?? "", l = e.Attr("locale") ?? "";
                        warnings.Add(set != null && !set.Allows(f, l)
                            ? "its feature \"" + f + "\"" + (l.Length > 0 ? " and locale \"" + l + "\"" : "") + " are off (" + set.Text + "): the server leaves it out"
                            : "another row with " + GachaRules.Key + " " + gid + " wins by its feature and locale" + (set != null ? " (" + set.Text + ")" : "") + ": the server leaves this one out");
                    }
                    if (isKept && main.IsServer)
                    {
                        long box;
                        if (GachaRules.ServerValue(GachaRules.DropBox, e.Attr(GachaRules.DropBox), out box) && box > 0)
                        {
                            if (!boxesRead) { boxesRead = true; drops = FinalDoc(run, ServerTables, ServerDropEntry); boxes = BoxIdsAfter(run, Profile.Server); }
                            string b = box.ToString(CultureInfo.InvariantCulture);
                            int groups = GachaBoxGroups(drops, b);
                            if (boxes != null && !boxes.Contains(b) || drops != null && groups < 0)
                                warnings.Add(GachaRules.DropBox + " " + b + " is not a drop box of the server after the mods: opening the capsule fails and it is not used up");
                            else if (groups == 0)
                                warnings.Add("drop box " + b + " has no group with rows: opening the capsule fails and it is not used up");
                        }
                        long coin, amount;
                        if (GachaRules.ServerValue(GachaRules.Coin, e.Attr(GachaRules.Coin), out coin) && coin > 0
                            && GachaRules.ServerValue(GachaRules.Amount, e.Attr(GachaRules.Amount), out amount) && amount > 0)
                        {
                            if (!itemsRead) { itemsRead = true; serverItems = Items(run, Profile.Server); }
                            if (serverItems != null && !serverItems.Contains(coin))
                                warnings.Add(GachaRules.Coin + " " + coin + " is not an item of the " + Profile.Server.Title + ": dismantling a won item gives nothing");
                        }
                        if (opened != null && !main.Shipped.ContainsKey(GachaKeyOf(e)) && gid != long.MinValue && !opened.ContainsKey(gid))
                            warnings.Add("no capsule item opens it (no server item has the function " + GachaRules.Function + " with the parameter \"" + gid + ",...\"): players cannot get it");
                        foreach (GachaSide c in clients) GachaClientCheck(e, c, set, warnings);
                    }
                    foreach (string t in errors) plan.Msg(plan.Errors, mod, main.Archive, GachaRules.Entry, label + ": " + t);
                    foreach (string t in warnings) plan.Msg(plan.Warnings, mod, main.Archive, GachaRules.Entry, label + ": " + t);
                }

                // shipped ids the plan leaves without a row the server keeps
                if (main.IsServer)
                {
                    var done = new HashSet<long>();
                    foreach (KeyValuePair<string, XmlElem> kv in main.Shipped)
                    {
                        long gid = GachaIdOf(kv.Value);
                        if (gid == long.MinValue || kept.ContainsKey(gid) || !done.Add(gid)) continue;
                        // an id whose shipped row the server did not keep either is not the mods' doing
                        bool wasKept = set == null || set.Allows(kv.Value.Attr("feature") ?? "", kv.Value.Attr("locale") ?? "");
                        if (!wasKept) continue;
                        string id = gid.ToString(CultureInfo.InvariantCulture);
                        Mod mod = GachaMod(run, main.Archive, id);
                        string what = "capsule " + id + " is removed";
                        long coin, amount;
                        if (GachaRules.ServerValue(GachaRules.Coin, kv.Value.Attr(GachaRules.Coin), out coin) && coin > 0
                            && GachaRules.ServerValue(GachaRules.Amount, kv.Value.Attr(GachaRules.Amount), out amount) && amount > 0)
                            plan.Msg(plan.Warnings, mod, main.Archive, GachaRules.Entry, what + ": items players won from it no longer give coin " + coin + " when they are dismantled (the server looks the capsule up then)");
                        List<GachaCapsule> by;
                        if (opened != null && opened.TryGetValue(gid, out by))
                            plan.Msg(plan.Warnings, mod, main.Archive, GachaRules.Entry, what + ", and item " + GachaItems(by) + " still opens it: opening fails");
                    }
                }
            }

            // client rows the mods change for a capsule the server does not have
            if (server != null)
                foreach (GachaSide c in clients)
                {
                    if (!c.Touched) continue;
                    foreach (XmlElem e in c.Changed)
                    {
                        long gid = GachaIdOf(e);
                        if (gid == long.MinValue || kept.ContainsKey(gid)) continue;
                        string id = (e.Attr(GachaRules.Key) ?? "").Trim();
                        plan.Msg(plan.Warnings, GachaMod(run, c.Archive, id), c.Archive, GachaRules.Entry, GachaLabel(e) + ": the server has no capsule " + id + ": the client shows it, but opening it fails");
                    }
                }

            // capsule items the mods change
            foreach (EntryState st in items)
            {
                if (st.DeletedBy != null) continue;
                Mod mod = LevelMod(run, GachaServerArchive, st.Name);
                foreach (GachaCapsule c in GachaCapsules.Read(st.Name, st.Final))
                {
                    if (c.Problem != null) { plan.Msg(plan.Errors, mod, st.Archive, st.Name, "item " + c.Item + ": " + c.Problem); continue; }
                    if (server == null) continue;
                    if (!kept.ContainsKey(c.Gacha))
                        plan.Msg(plan.Warnings, mod, st.Archive, st.Name, "item " + c.Item + " opens capsule " + c.Gacha + ", which the server has no row of " + GachaRules.Entry + " for after the mods: opening fails");
                }
            }
        }

        static string GachaItems(List<GachaCapsule> by)
        {
            var ids = new List<string>();
            foreach (GachaCapsule c in by) if (ids.Count < 5) ids.Add(c.Item.ToString(CultureInfo.InvariantCulture));
            return string.Join(", ", ids.ToArray()) + (by.Count > 5 ? " and " + (by.Count - 5) + " more" : "");
        }

        // a client's row of a capsule the server keeps: there, and read the same (the client-only fields aside)
        static void GachaClientCheck(XmlElem e, GachaSide c, FeatureSet set, List<string> warnings)
        {
            long gid = GachaIdOf(e);
            string diff = null;
            bool any = false;
            foreach (XmlElem r in c.Rows)
            {
                if (GachaIdOf(r) != gid) continue;
                // the client resolves the features of its own: compare only rows of the same feature and locale
                if ((r.Attr("feature") ?? "") != (e.Attr("feature") ?? "") || (r.Attr("locale") ?? "") != (e.Attr("locale") ?? "")) continue;
                any = true;
                var l = new List<string>();
                foreach (string f in GachaRules.Fields)
                {
                    if (GachaRules.IsClientOnly(f) || f == "feature" || f == "locale") continue;
                    string a = GachaRules.Effective(f, e.Attr(f), false), b = GachaRules.Effective(f, r.Attr(f), true);
                    if (a != b) l.Add(f + " \"" + b + "\" (the server's \"" + a + "\")");
                }
                if (l.Count == 0) return;
                if (diff == null) diff = string.Join(", ", l.ToArray());
            }
            if (!any) warnings.Add("the " + c.Profile.Title + " has no row for it in its " + GachaRules.Entry + ": its gacha window does not show the capsule (change the client table too)");
            else warnings.Add("the " + c.Profile.Title + " reads it differently: " + diff);
        }
    }
}
