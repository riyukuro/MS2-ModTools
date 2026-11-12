// The Meret Market (the cash shop) as a mod edit.
//
// The server's Server.m2d has table/Server/shop_merat_custom.xml: one <item> row per thing on sale, written for the
// server (the client's own shop_merat.xml is of an older shop and is not read). The server's ingest
// (ParseMeretCustomShop, ServerTableMapper.cs) reads every row, and the game server shows each item whose itemID is in
// its items under the tab of its tabID (a tab of table/na/meratmarketcategory.xml in Xml.m2d). The row:
//
// - id: the sale's id. The ids of the items and of their bundles are one space: an id used twice stops the ingest.
// - tabID, banner, bannerTag (0 none, 1 pink gift, 2 blue gift), saleTag (0 none, 1 new, 2 hot, 3 event, 4 sale,
//   5 special), showSaleTime, promoName, promoSaleStartTime, promoSaleEndTime: how the shop shows it.
// - itemID, grade, quantity, bonusQuantity, durationDay: what a purchase gives (quantity + bonusQuantity of the item,
//   at the grade; durationDay above 0: an item that expires after that many days).
// - paymentType (0 meso, 1 meret, 2 red meret; any other: nobody can pay), price, salePrice (0: the price).
// - saleStartTime, saleEndTime: yyyy-MM-dd-HH-mm-ss, empty for no limit. Outside them a purchase fails.
// - jobRequire: the job codes that may buy, a comma list (empty: nobody); minLevel, maxLevel (0: no limit);
//   achieveID (above 0: a player who has that trophy may not buy), achieveGrade, noRestock, pcCafe, giftable.
// - <additionalQuantity id quantity bonusQuantity price salePrice /> children: bundles of the same item (more of it
//   for another price). A bundle takes its item's tab, item, grade, payment, jobs, levels, restock, trophy, pcCafe and
//   giftable; it has its own id, quantity, bonusQuantity, price, salePrice, durationDay, banner, bannerTag, saleTag,
//   showSaleTime and promoName. Its sale and promo times are its item's, or its own only when the item's are empty.
//
// The ingest reads every attribute of a row and of a bundle as a number or a boolean (true/false/1/0) as written: a
// number may have spaces around it, a time may not, and a value of only spaces is not a number or a boolean. A value it
// cannot read stops it, as do a job code of an item that is not a job and a time it uses that is not
// yyyy-MM-dd-HH-mm-ss (a bundle's job codes are only read as numbers). An exactly empty attribute reads as 0 (or no
// limit); empty parts of a job list are skipped.
// The edits refuse such values; the plan's checks (PlannerMeret.cs) catch them in rows edited by hand.
//
// A shipped item is edited with a <change> of its row (item[id=N]) with set attributes; a shipped bundle's attributes
// are set lines under the row (at="additionalQuantity[id=M]"). Setting the shipped value back takes the line out. A
// bundle added to a shipped item is an <add in="item[id=N]">; a shipped bundle or item removed is a <delete>. A new
// item (New, Copy) is an <add> of the row's text (bundles and all), rewritten on each edit; Take of a new item takes
// it out of the mod.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>One bundle (an additionalQuantity child) of a Meret Market item.</summary>
    public sealed class MeretBundle
    {
        public int Id;
        /// <summary>The mod's add of the bundle under a shipped item (null: shipped, or in a new item's text).</summary>
        public RowOp Added;
        /// <summary>The bundle is in the text of an item the mod adds.</summary>
        public bool InNewItem;
        /// <summary>The bundle's attributes: as shipped (a shipped bundle), else as the mod writes them.</summary>
        public readonly List<KeyValuePair<string, string>> Attrs = new List<KeyValuePair<string, string>>();

        public bool IsShipped { get { return Added == null && !InNewItem; } }
        /// <summary>The bundle's path under its item's row.</summary>
        public string At { get { return MeretMarket.Child + "[id=" + Attr("id") + "]"; } }

        public string Attr(string name)
        {
            foreach (KeyValuePair<string, string> kv in Attrs) if (kv.Key == name) return kv.Value;
            return null;
        }
    }

    /// <summary>One Meret Market item: a shipped row or a row the mod adds.</summary>
    public sealed class MeretItem
    {
        public int Id;
        /// <summary>The shipped row's path (null: a row the mod adds).</summary>
        public string At;
        /// <summary>The mod's add of the row (null: a shipped row).</summary>
        public RowOp Added;
        /// <summary>The rows of the id in the file (items and bundles), with the mod's adds.</summary>
        public int Rows = 1;
        public readonly List<KeyValuePair<string, string>> Shipped = new List<KeyValuePair<string, string>>();
        public readonly List<MeretBundle> ShippedBundles = new List<MeretBundle>();

        public bool IsAdded { get { return Added != null; } }

        public string ShippedAttr(string name)
        {
            foreach (KeyValuePair<string, string> kv in Shipped) if (kv.Key == name) return kv.Value;
            return null;
        }
    }

    public sealed class MeretMarket
    {
        public const string Archive = "server:Server.m2d";
        public const string Entry = "table/Server/shop_merat_custom.xml";
        public const string Row = "item", Key = "id", Child = "additionalQuantity";
        public const string TabArchive = "server:Xml.m2d", TabEntry = "table/na/meratmarketcategory.xml";
        public const string TimeFormat = "yyyy-MM-dd-HH-mm-ss";

        /// <summary>An item's attributes, in the order the shipped rows write them.</summary>
        public static readonly string[] Fields = ("id tabID banner bannerTag itemID grade quantity bonusQuantity durationDay saleTag paymentType price salePrice " +
            "saleStartTime saleEndTime jobRequire noRestock minLevel maxLevel achieveID achieveGrade pcCafe giftable showSaleTime promoName promoSaleStartTime promoSaleEndTime").Split(' ');
        /// <summary>The attributes of a bundle the server reads as the bundle's own.</summary>
        public static readonly string[] BundleFields = { "id", "quantity", "bonusQuantity", "price", "salePrice", "durationDay", "banner", "bannerTag", "saleTag", "showSaleTime", "promoName" };
        /// <summary>The times of a bundle the server reads only when its item's is empty.</summary>
        public static readonly string[] BundleTimes = { "saleStartTime", "saleEndTime", "promoSaleStartTime", "promoSaleEndTime" };
        /// <summary>The job codes the server knows (jobRequire), and their names.</summary>
        public static readonly int[] JobCodes = { 1, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 999 };
        public static readonly string[] JobNames = { "Beginner", "Knight", "Berserker", "Wizard", "Priest", "Archer", "Heavy Gunner", "Thief", "Assassin", "Rune Blade", "Striker", "Soul Binder", "GM" };
        public static readonly string[] Payments = { "meso", "meret", "red meret" };
        public static readonly string[] SaleTags = { "none", "new", "hot", "event", "sale", "special" };
        public static readonly string[] BannerTags = { "none", "pink gift", "blue gift" };

        static readonly Dictionary<string, char> Kinds = MakeKinds();

        static Dictionary<string, char> MakeKinds()
        {
            // i int, b byte, l long, h short, o bool, t time, j jobs, s text
            var k = new Dictionary<string, char>(StringComparer.Ordinal);
            foreach (string f in "id tabID bannerTag itemID grade quantity bonusQuantity durationDay paymentType achieveID".Split(' ')) k[f] = 'i';
            foreach (string f in "saleTag achieveGrade".Split(' ')) k[f] = 'b';
            foreach (string f in "price salePrice".Split(' ')) k[f] = 'l';
            foreach (string f in "minLevel maxLevel".Split(' ')) k[f] = 'h';
            foreach (string f in "noRestock pcCafe giftable showSaleTime".Split(' ')) k[f] = 'o';
            foreach (string f in "saleStartTime saleEndTime promoSaleStartTime promoSaleEndTime".Split(' ')) k[f] = 't';
            k["jobRequire"] = 'j';
            foreach (string f in "banner promoName".Split(' ')) k[f] = 's';
            return k;
        }

        public readonly DataSet Data;
        public readonly Mod Mod;
        List<MeretItem> shipped;
        Dictionary<int, int> shippedIds;   // rows by id, items and bundles
        bool has;

        public MeretMarket(DataSet data, Mod mod) { Data = data; Mod = mod; }

        /// <summary>Whether the data set has the server's shop table.</summary>
        public bool Has { get { Read(); return has; } }

        void Read()
        {
            if (shipped != null) return;
            shipped = new List<MeretItem>();
            shippedIds = new Dictionary<int, int>();
            byte[] b = Data.Exists(Archive) ? ShippedBytes(Archive, Entry) : null;
            if (b == null) return;
            XmlDoc doc = XmlDoc.Parse(b, Archive + " " + Entry);
            if (doc.Root == null) return;
            has = true;
            foreach (XmlElem e in doc.Root.Children)
            {
                int id;
                if (e.Name != Row || !TryId(e.Attr(Key), out id)) continue;
                Count(shippedIds, id);
                var it = new MeretItem { Id = id, At = RowPath.Canonical(e) };
                foreach (XmlAttr a in e.Attrs) it.Shipped.Add(new KeyValuePair<string, string>(a.Name, a.Value));
                foreach (XmlElem c in e.Children)
                {
                    int cid;
                    if (c.Name != Child || !TryId(c.Attr(Key), out cid)) continue;
                    Count(shippedIds, cid);
                    var bu = new MeretBundle { Id = cid };
                    foreach (XmlAttr a in c.Attrs) bu.Attrs.Add(new KeyValuePair<string, string>(a.Name, a.Value));
                    it.ShippedBundles.Add(bu);
                }
                shipped.Add(it);
            }
        }

        static void Count(Dictionary<int, int> d, int id)
        {
            int n;
            d[id] = d.TryGetValue(id, out n) ? n + 1 : 1;
        }

        byte[] ShippedBytes(string archive, string entry)
        {
            var trig = new TriggerEditor(Data, Mod);
            EntryOp op = trig.OpOf(archive, entry);
            if (op != null) return trig.ModBytes(op);
            M2dArchive a = Data.Open(archive);
            M2dEntry e = a.Find(entry);
            return e == null ? null : a.Read(e);
        }

        static bool TryId(string s, out int id)
        {
            return int.TryParse((s ?? "").Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out id);
        }

        static string Inv(long v) { return v.ToString(CultureInfo.InvariantCulture); }

        /// <summary>The tab ids of the shop's categories (every tab and sub tab), or null when the file is not there.</summary>
        public HashSet<int> TabIds()
        {
            byte[] b = Data.Exists(TabArchive) ? ShippedBytes(TabArchive, TabEntry) : null;
            if (b == null) return null;
            return TabIdsOf(XmlDoc.Parse(b, TabArchive + " " + TabEntry));
        }

        public static HashSet<int> TabIdsOf(XmlDoc d)
        {
            var set = new HashSet<int>();
            if (d == null || d.Root == null) return null;
            var stack = new List<XmlElem> { d.Root };
            while (stack.Count > 0)
            {
                XmlElem e = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                int id;
                if (e.Name == "tab" && TryId(e.Attr("id"), out id)) set.Add(id);
                stack.AddRange(e.Children);
            }
            return set;
        }

        // ---- the items ----

        // the mod's added item rows
        List<KeyValuePair<RowOp, XmlElem>> AddedRows()
        {
            var l = new List<KeyValuePair<RowOp, XmlElem>>();
            EditOp ed = Edit(false);
            if (ed == null) return l;
            foreach (RowOp r in ed.Rows)
            {
                if (r.Kind != RowOpKind.Add || r.Xml == null || r.Copy != null || !string.IsNullOrEmpty(r.In) || r.After != null || r.Before != null) continue;
                XmlElem e = AddedElem(r);
                if (e != null && e.Name == Row) l.Add(new KeyValuePair<RowOp, XmlElem>(r, e));
            }
            return l;
        }

        static XmlElem AddedElem(RowOp r)
        {
            try { return XmlDoc.ParseFragment(r.Xml.Trim(), "added row").Root; }
            catch (ModKitException) { return null; }
        }

        /// <summary>The items, shipped and added, by id (the items the mod removes too: see IsRemoved).</summary>
        public List<MeretItem> Items()
        {
            Read();
            var ids = new Dictionary<int, int>(shippedIds);
            var l = new List<MeretItem>(shipped);
            foreach (KeyValuePair<RowOp, XmlElem> kv in AddedRows())
            {
                int id;
                if (!TryId(kv.Value.Attr(Key), out id)) continue;
                Count(ids, id);
                foreach (XmlElem c in kv.Value.Children) { int cid; if (c.Name == Child && TryId(c.Attr(Key), out cid)) Count(ids, cid); }
                l.Add(new MeretItem { Id = id, Added = kv.Key });
            }
            foreach (RowOp r in BundleAdds(null))
            {
                XmlElem c = AddedElem(r);
                int cid;
                if (c != null && TryId(c.Attr(Key), out cid)) Count(ids, cid);
            }
            foreach (MeretItem it in l) it.Rows = ids[it.Id];
            l.Sort(delegate (MeretItem a, MeretItem b) { return a.Id != b.Id ? a.Id.CompareTo(b.Id) : (a.IsAdded ? 1 : 0).CompareTo(b.IsAdded ? 1 : 0); });
            return l;
        }

        /// <summary>The item of an id (the shipped one first), or null.</summary>
        public MeretItem Find(int id)
        {
            foreach (MeretItem it in Items()) if (it.Id == id) return it;
            return null;
        }

        /// <summary>Whether an item or a bundle, shipped or of the mod, has the id.</summary>
        public bool IdUsed(int id)
        {
            Read();
            if (shippedIds.ContainsKey(id)) return true;
            foreach (KeyValuePair<RowOp, XmlElem> kv in AddedRows())
            {
                int n;
                if (TryId(kv.Value.Attr(Key), out n) && n == id) return true;
                foreach (XmlElem c in kv.Value.Children) if (c.Name == Child && TryId(c.Attr(Key), out n) && n == id) return true;
            }
            foreach (RowOp r in BundleAdds(null))
            {
                XmlElem c = AddedElem(r);
                int n;
                if (c != null && TryId(c.Attr(Key), out n) && n == id) return true;
            }
            return false;
        }

        /// <summary>The next id above every item and bundle id, from the given one.</summary>
        public int FreeId(int from)
        {
            int id = Math.Max(1, from);
            while (IdUsed(id)) id++;
            return id;
        }

        // ---- item attributes ----

        /// <summary>An attribute of the item's row with the mod (null: the row has none).</summary>
        public string Get(MeretItem it, string attr)
        {
            if (it.IsAdded)
            {
                XmlElem e = AddedElem(it.Added);
                return e == null ? null : e.Attr(attr);
            }
            string v;
            return SetLine(it, "", attr, out v) ? v : it.ShippedAttr(attr);
        }

        // the mod's last line for an attribute under the row (at: "" for the row itself); false: none
        bool SetLine(MeretItem it, string at, string attr, out string value)
        {
            value = null;
            RowOp op = Change(it, false);
            if (op == null) return false;
            for (int i = op.Attrs.Count - 1; i >= 0; i--)
                if (op.Attrs[i].Name == attr && SameUnder(op.Attrs[i].At, at))
                {
                    if (op.Attrs[i].Kind == AttrOpKind.Set) { value = op.Attrs[i].Value ?? ""; return true; }
                    if (op.Attrs[i].Kind == AttrOpKind.Unset) return true;
                }
            return false;
        }

        static bool SameUnder(string at, string want)
        {
            if (string.IsNullOrEmpty(at) || string.IsNullOrEmpty(want)) return string.IsNullOrEmpty(at) && string.IsNullOrEmpty(want);
            return Same(at, want);
        }

        public bool IsEdited(MeretItem it)
        {
            if (it.IsAdded) return true;
            return Change(it, false) != null || Deleted(it.At) != null || BundleAdds(it).Count > 0 || BundleDeletes(it).Count > 0;
        }

        /// <summary>Whether the mod removes the shipped item.</summary>
        public bool IsRemoved(MeretItem it) { return !it.IsAdded && Deleted(it.At) != null; }

        public int EditedCount()
        {
            int n = 0;
            foreach (MeretItem it in Items()) if (IsEdited(it)) n++;
            return n;
        }

        void Single(MeretItem it)
        {
            if (it.Rows > 1) throw new ModKitException("id " + it.Id + " is used by " + it.Rows + " rows (items and bundles): the server's ingest stops on it; edit them by hand");
            if (IsRemoved(it)) throw new ModKitException("item " + it.Id + " is removed by the mod: take the item back first");
        }

        /// <summary>Why the server's ingest cannot read a value of an attribute (null: it reads it).</summary>
        public static string FieldProblem(string attr, string value) { return FieldProblem(attr, value, false); }

        /// <summary>Why the server's ingest cannot read a value of an attribute of an item or (bundle) of a bundle
        /// (null: it reads it). The value is checked as written: the ingest does not trim times.</summary>
        public static string FieldProblem(string attr, string value, bool bundle)
        {
            char k;
            if (!Kinds.TryGetValue(attr, out k)) return null;
            value = value ?? "";
            if (value.Length == 0)
                return attr == "id" ? "the id is empty" : null;      // an empty attribute reads as 0
            string v = value.Trim(XmlSpace);                           // numbers and booleans may have spaces around
            if (v.Length == 0 && k != 's' && k != 't' && k != 'j') return attr + " \"" + value + "\" is only spaces, not a value (the server's ingest stops)";
            long n;
            bool num = long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n);
            switch (k)
            {
                case 'i': if (!num || n < int.MinValue || n > int.MaxValue) return attr + " \"" + value + "\" is not a whole number (the server's ingest stops)"; break;
                case 'h': if (!num || n < short.MinValue || n > short.MaxValue) return attr + " \"" + value + "\" is not a whole number from " + short.MinValue + " to " + short.MaxValue + " (the server's ingest stops)"; break;
                case 'b': if (!num || n < 0 || n > 255) return attr + " \"" + value + "\" is not a whole number from 0 to 255 (the server's ingest stops)"; break;
                case 'l': if (!num) return attr + " \"" + value + "\" is not a whole number (the server's ingest stops)"; break;
                case 'o': if (v != "0" && v != "1" && v != "true" && v != "false") return attr + " \"" + value + "\" is not 0, 1, true or false (the server's ingest stops)"; break;
                case 't': if (ParseTime(value) == null) return attr + " \"" + value + "\" is not a time as " + TimeFormat + " (the server's ingest stops)"; break;
                case 'j': return JobProblem(value, !bundle);
            }
            return null;
        }

        static readonly char[] XmlSpace = { ' ', '\t', '\r', '\n' };

        // The ingest splits the list at commas, skips empty parts and reads each part as a number (spaces around it are
        // fine, a part of only spaces is not); the job codes are checked only for the item's own list.
        static string JobProblem(string value, bool codes)
        {
            foreach (string part in (value ?? "").Split(','))
            {
                if (part.Length == 0) continue;
                int n;
                string p = part.Trim();
                if (!int.TryParse(p, NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out n)) return "jobRequire part \"" + part + "\" is not a whole number (the server's ingest stops)";
                if (codes && n != 0 && Array.IndexOf(JobCodes, n) < 0) return "jobRequire " + n + " is not a job code (" + string.Join(", ", Array.ConvertAll(JobCodes, Inv2)) + "): the server's ingest stops";
            }
            return null;
        }

        static string Inv2(int v) { return v.ToString(CultureInfo.InvariantCulture); }

        /// <summary>The job codes of a jobRequire value in words ("nobody" when none).</summary>
        public static string JobsText(string value)
        {
            var names = new List<string>();
            foreach (string part in (value ?? "").Split(','))
            {
                int n;
                if (!int.TryParse(part.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n) || n == 0) continue;
                int i = Array.IndexOf(JobCodes, n);
                names.Add(i < 0 ? "?" + n : JobNames[i]);
            }
            if (names.Count == 0) return "nobody";
            return names.Count >= JobCodes.Length - 1 ? "every job" : string.Join(", ", names.ToArray());
        }

        /// <summary>Sets an attribute of the item (the shipped value takes the line out). Returns whether anything changed.</summary>
        public bool SetField(MeretItem it, string attr, string value)
        {
            if (Array.IndexOf(Fields, attr) < 0) throw new ModKitException("an item has no attribute \"" + attr + "\" the server reads");
            value = (value ?? "").Trim();
            string bad = FieldProblem(attr, value);
            if (bad != null) throw new ModKitException("item " + it.Id + ": " + bad);
            Single(it);
            if (attr == "id")
            {
                int id;
                TryId(value, out id);
                if (id != it.Id && IdUsed(id)) throw new ModKitException("id " + id + " is in use (the ids of the items and their bundles are one space)");
                if (!it.IsAdded) throw new ModKitException("a shipped item keeps its id: copy it to a new id instead");
            }
            if (Get(it, attr) == value) return false;
            if (it.IsAdded)
            {
                ItemText t = ItemText.Of(it.Added);
                t.Set(t.Attrs, attr, value);
                it.Added.Xml = t.ToString();
                return true;
            }
            SetUnder(it, "", attr, value, it.ShippedAttr(attr));
            return true;
        }

        // a set line under the shipped item's row; the shipped value takes it out
        void SetUnder(MeretItem it, string at, string attr, string value, string shippedValue)
        {
            RowOp op = Change(it, false);
            if (op != null) op.Attrs.RemoveAll(x => x.Name == attr && SameUnder(x.At, at));
            if (value == shippedValue)
            {
                if (op != null && op.Attrs.Count == 0) Drop(op);
                return;
            }
            if (op == null) op = Change(it, true);
            op.Attrs.Add(new AttrOp { Kind = AttrOpKind.Set, Name = attr, Value = value, At = at.Length == 0 ? null : at });
        }

        // ---- bundles ----

        /// <summary>The item's bundles with the mod (the ones it removes left out).</summary>
        public List<MeretBundle> Bundles(MeretItem it)
        {
            var l = new List<MeretBundle>();
            if (it.IsAdded)
            {
                foreach (List<KeyValuePair<string, string>> c in ItemText.Of(it.Added).Children)
                {
                    var b = new MeretBundle { InNewItem = true };
                    b.Attrs.AddRange(c);
                    TryId(b.Attr(Key), out b.Id);
                    l.Add(b);
                }
                return l;
            }
            List<RowOp> dels = BundleDeletes(it);
            foreach (MeretBundle b in it.ShippedBundles)
            {
                bool gone = false;
                foreach (RowOp d in dels) if (Same(d.At, it.At + "/" + b.At)) gone = true;
                if (!gone) l.Add(b);
            }
            foreach (RowOp r in BundleAdds(it))
            {
                XmlElem c = AddedElem(r);
                if (c == null) continue;
                var b = new MeretBundle { Added = r };
                foreach (XmlAttr a in c.Attrs) b.Attrs.Add(new KeyValuePair<string, string>(a.Name, a.Value));
                TryId(b.Attr(Key), out b.Id);
                l.Add(b);
            }
            return l;
        }

        /// <summary>A bundle's attribute with the mod (null: none).</summary>
        public string BundleGet(MeretItem it, MeretBundle b, string attr)
        {
            if (!b.IsShipped) return b.Attr(attr);
            string v;
            return SetLine(it, b.At, attr, out v) ? v : b.Attr(attr);
        }

        MeretBundle FindBundle(MeretItem it, int id)
        {
            foreach (MeretBundle b in Bundles(it)) if (b.Id == id) return b;
            throw new ModKitException("item " + it.Id + " has no bundle " + id);
        }

        static void CheckBundleValue(MeretItem it, string attr, string value)
        {
            if (Array.IndexOf(BundleFields, attr) < 0) throw new ModKitException("a bundle has no attribute \"" + attr + "\" of its own (it takes the others from its item)");
            string bad = FieldProblem(attr, value, true);
            if (bad != null) throw new ModKitException("item " + it.Id + " bundle: " + bad);
        }

        /// <summary>Adds a bundle of the item: quantity and bonus of the item for the price (salePrice 0: the price).</summary>
        public MeretBundle AddBundle(MeretItem it, int id, string quantity, string bonusQuantity, string price, string salePrice)
        {
            Single(it);
            if (id <= 0) throw new ModKitException("the bundle id must be above 0");
            if (IdUsed(id)) throw new ModKitException("id " + id + " is in use (the ids of the items and their bundles are one space)");
            var attrs = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("id", Inv(id)) };
            string[] names = { "quantity", "bonusQuantity", "price", "salePrice" };
            string[] values = { quantity, bonusQuantity, price, salePrice };
            for (int i = 0; i < names.Length; i++)
            {
                string v = (values[i] ?? "").Trim();
                if (v.Length == 0) v = "0";
                CheckBundleValue(it, names[i], v);
                attrs.Add(new KeyValuePair<string, string>(names[i], v));
            }
            if (it.IsAdded)
            {
                ItemText t = ItemText.Of(it.Added);
                t.Children.Add(attrs);
                it.Added.Xml = t.ToString();
            }
            else Edit(true).Rows.Add(new RowOp { Kind = RowOpKind.Add, In = it.At, Xml = ElemText(Child, attrs) });
            return FindBundle(Find(it.Id) ?? it, id);
        }

        /// <summary>Sets a bundle's attribute. Returns whether anything changed.</summary>
        public bool SetBundle(MeretItem it, int bundleId, string attr, string value)
        {
            value = (value ?? "").Trim();
            CheckBundleValue(it, attr, value);
            Single(it);
            if (attr == "id") throw new ModKitException("a bundle keeps its id: remove it and add another");
            MeretBundle b = FindBundle(it, bundleId);
            if (BundleGet(it, b, attr) == value) return false;
            if (it.IsAdded)
            {
                ItemText t = ItemText.Of(it.Added);
                foreach (List<KeyValuePair<string, string>> c in t.Children)
                    if (ItemText.Value(c, Key) == b.Attr(Key)) { t.Set(c, attr, value); break; }
                it.Added.Xml = t.ToString();
            }
            else if (b.Added != null)
            {
                var c = new List<KeyValuePair<string, string>>(b.Attrs);
                ItemText.SetIn(c, attr, value);
                b.Added.Xml = ElemText(Child, c);
            }
            else SetUnder(it, b.At, attr, value, b.Attr(attr));
            return true;
        }

        /// <summary>Removes a bundle (a shipped one: a delete line; one of the mod: taken out).</summary>
        public void RemoveBundle(MeretItem it, int bundleId)
        {
            Single(it);
            MeretBundle b = FindBundle(it, bundleId);
            if (it.IsAdded)
            {
                ItemText t = ItemText.Of(it.Added);
                t.Children.RemoveAll(c => ItemText.Value(c, Key) == b.Attr(Key));
                it.Added.Xml = t.ToString();
                return;
            }
            if (b.Added != null) { Drop(b.Added); return; }
            RowOp op = Change(it, false);
            if (op != null)
            {
                op.Attrs.RemoveAll(x => SameUnder(x.At, b.At));
                if (op.Attrs.Count == 0) Drop(op);
            }
            Edit(true).Rows.Add(new RowOp { Kind = RowOpKind.Delete, At = it.At + "/" + b.At });
        }

        // the bundle adds under a shipped item (null: under any row)
        List<RowOp> BundleAdds(MeretItem it)
        {
            var l = new List<RowOp>();
            EditOp ed = Edit(false);
            if (ed == null) return l;
            foreach (RowOp r in ed.Rows)
            {
                if (r.Kind != RowOpKind.Add || r.Xml == null || r.Copy != null || string.IsNullOrEmpty(r.In)) continue;
                if (it != null && (it.IsAdded || !Same(r.In, it.At))) continue;
                XmlElem c = AddedElem(r);
                if (c != null && c.Name == Child) l.Add(r);
            }
            return l;
        }

        List<RowOp> BundleDeletes(MeretItem it)
        {
            var l = new List<RowOp>();
            EditOp ed = Edit(false);
            if (ed == null || it.IsAdded) return l;
            foreach (RowOp r in ed.Rows)
            {
                if (r.Kind != RowOpKind.Delete) continue;
                foreach (MeretBundle b in it.ShippedBundles) if (Same(r.At, it.At + "/" + b.At)) { l.Add(r); break; }
            }
            return l;
        }

        RowOp Deleted(string at)
        {
            EditOp ed = Edit(false);
            if (ed == null) return null;
            foreach (RowOp r in ed.Rows) if (r.Kind == RowOpKind.Delete && Same(r.At, at)) return r;
            return null;
        }

        // ---- new items ----

        static string ElemText(string name, IList<KeyValuePair<string, string>> attrs)
        {
            var sb = new StringBuilder("<" + name);
            foreach (KeyValuePair<string, string> kv in attrs) sb.Append(' ').Append(kv.Key).Append("=\"").Append(XmlDoc.EscapeAttr(kv.Value ?? "", '"')).Append('"');
            return sb.Append(" />").ToString();
        }

        // a new item's row: its attributes and its bundles' attributes, written back as one text
        sealed class ItemText
        {
            public readonly List<KeyValuePair<string, string>> Attrs = new List<KeyValuePair<string, string>>();
            public readonly List<List<KeyValuePair<string, string>>> Children = new List<List<KeyValuePair<string, string>>>();

            public static ItemText Of(RowOp r)
            {
                XmlElem e = AddedElem(r);
                if (e == null) throw new ModKitException("the mod's new item row is not XML: edit it by hand");
                var t = new ItemText();
                foreach (XmlAttr a in e.Attrs) t.Attrs.Add(new KeyValuePair<string, string>(a.Name, a.Value));
                foreach (XmlElem c in e.Children)
                {
                    if (c.Name != Child) throw new ModKitException("the mod's new item row has a <" + c.Name + "> child: edit it by hand");
                    var l = new List<KeyValuePair<string, string>>();
                    foreach (XmlAttr a in c.Attrs) l.Add(new KeyValuePair<string, string>(a.Name, a.Value));
                    t.Children.Add(l);
                }
                return t;
            }

            public static string Value(List<KeyValuePair<string, string>> l, string name)
            {
                foreach (KeyValuePair<string, string> kv in l) if (kv.Key == name) return kv.Value;
                return null;
            }

            public static void SetIn(List<KeyValuePair<string, string>> l, string name, string value)
            {
                for (int i = 0; i < l.Count; i++)
                    if (l[i].Key == name) { l[i] = new KeyValuePair<string, string>(name, value); return; }
                l.Add(new KeyValuePair<string, string>(name, value));
            }

            public void Set(List<KeyValuePair<string, string>> l, string name, string value) { SetIn(l, name, value); }

            public override string ToString()
            {
                string head = ElemText(Row, Attrs);
                if (Children.Count == 0) return head;
                var sb = new StringBuilder(head.Substring(0, head.Length - 3)).Append('>');
                foreach (List<KeyValuePair<string, string>> c in Children) sb.Append("\r\n\t").Append(ElemText(Child, c));
                return sb.Append("\r\n</").Append(Row).Append('>').ToString();
            }
        }

        void NewId(int id)
        {
            if (id <= 0) throw new ModKitException("the item id must be above 0");
            if (IdUsed(id)) throw new ModKitException("id " + id + " is in use (the ids of the items and their bundles are one space)");
        }

        /// <summary>
        /// Adds an item on sale: one of itemId at grade 1 for 0 meret, in the tab given, for every job, with no time
        /// limit. Set its price and the rest after.
        /// </summary>
        public MeretItem New(int id, string itemId, string tabId)
        {
            NewId(id);
            itemId = (itemId ?? "").Trim();
            tabId = (tabId ?? "").Trim();
            int n;
            if (!int.TryParse(itemId, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n <= 0) throw new ModKitException("the item id \"" + itemId + "\" is not a whole number above 0");
            if (FieldProblem("tabID", tabId) != null || tabId.Length == 0) throw new ModKitException("the tab \"" + tabId + "\" is not a whole number");
            var t = new ItemText();
            foreach (string f in Fields)
            {
                string v = f == "id" ? Inv(id) : f == "itemID" ? itemId : f == "tabID" ? tabId
                    : f == "grade" || f == "quantity" || f == "paymentType" ? "1"
                    : f == "jobRequire" ? string.Join(",", Array.ConvertAll(Array.FindAll(JobCodes, j => j != 999), Inv2))
                    : Kinds[f] == 't' || Kinds[f] == 's' ? "" : "0";
                t.Attrs.Add(new KeyValuePair<string, string>(f, v));
            }
            return AddRow(t);
        }

        /// <summary>Adds a copy of an item (as the mod has it, bundles left out) under a new id.</summary>
        public MeretItem Copy(int fromId, int id)
        {
            MeretItem from = Find(fromId);
            if (from == null) throw new ModKitException("there is no Meret Market item " + fromId);
            NewId(id);
            var t = new ItemText();
            List<KeyValuePair<string, string>> source = from.IsAdded ? ItemText.Of(from.Added).Attrs : from.Shipped;
            foreach (KeyValuePair<string, string> kv in source)
                t.Attrs.Add(new KeyValuePair<string, string>(kv.Key, kv.Key == Key ? Inv(id) : Get(from, kv.Key) ?? kv.Value));
            return AddRow(t);
        }

        MeretItem AddRow(ItemText t)
        {
            var op = new RowOp { Kind = RowOpKind.Add, Xml = t.ToString() };
            Edit(true).Rows.Add(op);
            foreach (MeretItem it in Items()) if (it.Added == op) return it;
            throw new ModKitException("the new item could not be read back");
        }

        // ---- removing, taking edits out ----

        /// <summary>Takes the item off sale: a shipped one with a delete line (its other lines go), a new one out of the mod.</summary>
        public void Remove(MeretItem it)
        {
            if (it.IsAdded) { Drop(it.Added); return; }
            if (it.Rows > 1) throw new ModKitException("id " + it.Id + " is used by " + it.Rows + " rows: edit them by hand");
            Take(it);
            Edit(true).Rows.Add(new RowOp { Kind = RowOpKind.Delete, At = it.At });
        }

        /// <summary>Takes the mod's lines of an item out (a new item: takes it out of the mod). Returns whether it had any.</summary>
        public bool Take(MeretItem it)
        {
            if (it.IsAdded)
            {
                EditOp e = Edit(false);
                if (e == null || !e.Rows.Contains(it.Added)) return false;
                Drop(it.Added);
                return true;
            }
            var ops = new List<RowOp>();
            RowOp op = Change(it, false);
            if (op != null) ops.Add(op);
            op = Deleted(it.At);
            if (op != null) ops.Add(op);
            ops.AddRange(BundleAdds(it));
            ops.AddRange(BundleDeletes(it));
            foreach (RowOp o in ops) Drop(o);
            return ops.Count > 0;
        }

        public int TakeAll()
        {
            int n = 0;
            foreach (MeretItem it in Items()) if (Take(it)) n++;
            return n;
        }

        // ---- checks (the plan's, on a row as the mods leave it) ----

        /// <summary>A sale time as the server reads it, as written (null: not one; the server does not trim it).</summary>
        public static DateTime? ParseTime(string text)
        {
            DateTime t;
            return DateTime.TryParseExact(text ?? "", TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out t) ? t : (DateTime?)null;
        }

        public static string FormatTime(DateTime t) { return t.ToString(TimeFormat, CultureInfo.InvariantCulture); }

        static long Num(string v)
        {
            long n;
            return long.TryParse((v ?? "").Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        /// <summary>
        /// The problems of an item row (and its bundles) as the server reads it. errors: the ingest stops, the game
        /// server leaves the item out, or nobody can buy it; warnings: it will not sell as it reads. itemKnown is null
        /// when the server's items are not known, tabKnown when the tabs are not.
        /// </summary>
        public static void Check(XmlElem item, DateTime now, Func<long, bool> itemKnown, Func<int, bool> tabKnown, List<string> errors, List<string> warnings)
        {
            foreach (XmlAttr a in item.Attrs)
            {
                string bad = FieldProblem(a.Name, a.Value);
                if (bad != null) errors.Add(bad);
            }
            if (item.Attr("id") == null) errors.Add("the row has no id (the server's ingest reads 0)");
            Func<string, string> at = delegate (string n) { return (item.Attr(n) ?? "").Trim(); };
            long itemId = Num(at("itemID"));
            bool leftOut = itemId <= 0 || itemKnown != null && !itemKnown(itemId);
            if (itemId <= 0) errors.Add("itemID \"" + at("itemID") + "\" is not an item: the game server leaves the sale out");
            else if (leftOut) errors.Add("item " + itemId + " is not in the server's items: the game server leaves the sale out");
            long tab = Num(at("tabID"));
            if (tabKnown != null && tab != 1 && (tab < int.MinValue || tab > int.MaxValue || !tabKnown((int)tab)))
                warnings.Add("tabID " + at("tabID") + " is not a tab of " + TabEntry + ": the shop shows the item in no tab");
            if (JobProblem(at("jobRequire"), true) == null && JobsText(at("jobRequire")) == "nobody") errors.Add("jobRequire names no job: nobody can buy it");
            long pay = Num(at("paymentType"));
            if (pay < 0 || pay > 2) errors.Add("paymentType " + pay + " is not 0 (meso), 1 (meret) or 2 (red meret): nobody can pay for it");
            long price = Num(at("price")), sale = Num(at("salePrice"));
            if ((sale > 0 ? sale : price) < 0) errors.Add("the price is below 0: the server refuses the purchase");
            long grade = Num(at("grade"));
            if (grade < 1 || grade > 6) warnings.Add("grade " + grade + " is not 1 (normal) to 6");
            if (Num(at("quantity")) + Num(at("bonusQuantity")) <= 0) warnings.Add("quantity and bonusQuantity give no item");
            long bt = Num(at("bannerTag")), st = Num(at("saleTag"));
            if (bt < 0 || bt >= BannerTags.Length) warnings.Add("bannerTag " + bt + " is not 0 to " + (BannerTags.Length - 1) + ": the shop shows none");
            if (st < 0 || st >= SaleTags.Length) warnings.Add("saleTag " + st + " is not 0 to " + (SaleTags.Length - 1) + ": the shop shows none");
            long lo = Num(at("minLevel")), hi = Num(at("maxLevel"));
            if (lo > 0 && hi > 0 && lo > hi) errors.Add("minLevel " + lo + " is above maxLevel " + hi + ": nobody can buy it");
            DateTime? start = ParseTime(at("saleStartTime")), end = ParseTime(at("saleEndTime"));
            if (end != null && end.Value < now) warnings.Add("the sale ended " + FormatTime(end.Value) + ": the server refuses the purchase");
            if (start != null && start.Value > now) warnings.Add("the sale starts " + FormatTime(start.Value) + ": the server refuses the purchase before");
            if (start != null && end != null && end.Value <= start.Value) errors.Add("the sale ends at or before its start: nobody can buy it");
            int bundles = 0;
            foreach (XmlElem c in item.Children)
            {
                if (c.Name != Child) continue;
                bundles++;
                string what = "bundle " + (c.Attr("id") ?? "(no id)") + ": ";
                foreach (XmlAttr a in c.Attrs)
                {
                    // a bundle's time is read only when its item's is empty
                    if (Array.IndexOf(BundleTimes, a.Name) >= 0 && (item.Attr(a.Name) ?? "").Length > 0) continue;
                    string bad = FieldProblem(a.Name, a.Value, true);
                    if (bad != null) errors.Add(what + bad);
                }
                long bp = Num(c.Attr("price")), bs = Num(c.Attr("salePrice"));
                long cbt = Num(c.Attr("bannerTag")), cst = Num(c.Attr("saleTag"));
                if (cbt < 0 || cbt >= BannerTags.Length) warnings.Add(what + "bannerTag " + cbt + " is not 0 to " + (BannerTags.Length - 1) + ": the shop shows none");
                if (cst < 0 || cst >= SaleTags.Length) warnings.Add(what + "saleTag " + cst + " is not 0 to " + (SaleTags.Length - 1) + ": the shop shows none");
                if ((bs > 0 ? bs : bp) < 0) errors.Add(what + "the price is below 0: the server refuses the purchase");
                if (Num(c.Attr("quantity")) + Num(c.Attr("bonusQuantity")) <= 0) warnings.Add(what + "quantity and bonusQuantity give no item");
            }
            // The game server puts each bundle under its sale (any order) and gives the sale's item; a bundle whose
            // sale it leaves out is dropped with it.
            if (bundles > 0 && leftOut)
                errors.Add(bundles + " bundle" + (bundles == 1 ? "" : "s") + ": the sale is left out, so the game server drops " + (bundles == 1 ? "it" : "them") + " too");
        }

        // ---- the mod's lines ----

        EditOp Edit(bool create)
        {
            foreach (ModOp o in Mod.Ops)
            {
                var ed = o as EditOp;
                if (ed != null && string.Equals(DataSet.NormalizeRef(ed.Archive), DataSet.NormalizeRef(Archive), StringComparison.OrdinalIgnoreCase)
                    && string.Equals((ed.Entry ?? "").Replace('\\', '/'), Entry, StringComparison.OrdinalIgnoreCase)) return ed;
            }
            if (!create) return null;
            var n = new EditOp { Archive = Archive, Entry = Entry, Mirror = false };
            Mod.Ops.Add(n);
            return n;
        }

        RowOp Change(MeretItem it, bool create)
        {
            if (it.IsAdded) return null;
            EditOp ed = Edit(create);
            if (ed == null) return null;
            foreach (RowOp o in ed.Rows)
                if (o.Kind == RowOpKind.Change && Same(o.At, it.At)) return o;
            if (!create) return null;
            var op = new RowOp { Kind = RowOpKind.Change, At = it.At };
            ed.Rows.Add(op);
            return op;
        }

        static bool Same(string at, string want)
        {
            if ((at ?? "") == (want ?? "")) return true;
            try { return RowPath.Parse(at ?? "").ToString() == RowPath.Parse(want ?? "").ToString(); }
            catch (ModKitException) { return false; }
        }

        void Drop(RowOp op)
        {
            EditOp ed = Edit(false);
            if (ed == null || !ed.Rows.Remove(op)) return;
            if (ed.Rows.Count == 0) Mod.Ops.Remove(ed);
        }
    }
}
