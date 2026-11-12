// The plan's checks of the Meret Market (item rows of the server's table/Server/shop_merat_custom.xml, see
// MeretMarket.cs), on the rows as the mods leave them: the rows the mods add and the rows whose text they change.
// Errors: a value the server's ingest cannot read (a number, a boolean, a job code, a time), an id two rows use (the
// items and their bundles are one id space: the ingest stops), an item the game server leaves out (itemID not in its
// items), and a sale nobody can buy (no job, a payment type the server does not take, a price below 0, minLevel above
// maxLevel, a sale that ends before it starts). Warnings: a tab that is not in meratmarketcategory.xml, a sale that
// has ended or not started, a grade outside 1 to 6, no quantity, a tag the shop does not show, and bundles (the game
// server drops them).
using System;
using System.Collections.Generic;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        static void MeretChecks(Run run)
        {
            Plan plan = run.Plan;
            const string archive = MeretMarket.Archive, entry = MeretMarket.Entry;
            if (!run.Data.Exists(archive)) return;
            Mod mod = LevelMod(run, archive, entry);
            if (mod == null) return;
            XmlDoc fin = FinalDoc(run, archive, entry), was = OrigDoc(run, archive, entry);
            if (fin == null || fin.Root == null) return;
            var then = new HashSet<string>(StringComparer.Ordinal);
            if (was != null && was.Root != null)
                foreach (XmlElem e in was.Root.Children)
                    if (e.Name == MeretMarket.Row) then.Add(e.OuterText);
            // the ids of every row and bundle: the ingest adds them all to one dictionary
            var count = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (XmlElem e in fin.Root.Children)
            {
                if (e.Name != MeretMarket.Row) continue;
                MeretCount(count, e.Attr(MeretMarket.Key));
                foreach (XmlElem c in e.Children) if (c.Name == MeretMarket.Child) MeretCount(count, c.Attr(MeretMarket.Key));
            }
            HashSet<long> items = null;
            HashSet<int> tabs = null;
            bool read = false;
            DateTime now = DateTime.Now;
            foreach (XmlElem e in fin.Root.Children)
            {
                if (e.Name != MeretMarket.Row || then.Contains(e.OuterText)) continue;   // as shipped
                if (!read)
                {
                    read = true;
                    if (run.Data.Exists(Profile.Server.ArchiveIn("x:Xml.m2d"))) items = Items(run, Profile.Server);
                    if (run.Data.Exists(MeretMarket.TabArchive)) tabs = MeretMarket.TabIdsOf(FinalDoc(run, MeretMarket.TabArchive, MeretMarket.TabEntry));
                }
                string id = (e.Attr(MeretMarket.Key) ?? "").Trim();
                string what = "Meret Market item " + (id.Length == 0 ? "(no id)" : id) + ": ";
                var errors = new List<string>();
                var warnings = new List<string>();
                HashSet<long> known = items;
                HashSet<int> tabSet = tabs;
                MeretMarket.Check(e, now, known == null ? (Func<long, bool>)null : known.Contains, tabSet == null ? (Func<int, bool>)null : tabSet.Contains, errors, warnings);
                int n;
                if (count.TryGetValue(id, out n) && n > 1) errors.Add("id " + id + " is used by " + n + " rows (items and bundles are one id space): the server's ingest stops");
                foreach (XmlElem c in e.Children)
                {
                    if (c.Name != MeretMarket.Child) continue;
                    string cid = (c.Attr(MeretMarket.Key) ?? "").Trim();
                    if (count.TryGetValue(cid, out n) && n > 1) errors.Add("bundle " + cid + ": id " + cid + " is used by " + n + " rows (items and bundles are one id space): the server's ingest stops");
                }
                foreach (string s in errors) plan.Msg(plan.Errors, mod, archive, entry, what + s);
                foreach (string s in warnings) plan.Msg(plan.Warnings, mod, archive, entry, what + s);
            }
        }

        static void MeretCount(Dictionary<string, int> d, string id)
        {
            id = (id ?? "").Trim();
            int n;
            d[id] = d.TryGetValue(id, out n) ? n + 1 : 1;
        }
    }
}
