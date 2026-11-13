// Gacha capsules (table/gacha_info.xml): what a capsule item gives and what an item won from it is worth when it is
// dismantled. The client and the server read the same table, each from its own Xml.m2d, so a capsule row is edited as
// one thing that is written to every side that ships the table (the server's lines are planned for the global client
// too, see PlannerMirror).
//
// A row (<randomBox>):
//   randomBoxID           the gacha id; a capsule item opens it by the first part of its OpenGachaBox function
//                         parameter ("gachaId,..."), item/**/{id}.xml on the server
//   individualDropBoxID   the drop box the server rolls for each capsule opened (table/Server/individualItemDrop_Final.xml):
//                         every group of the box gives its drop count of rows, picked by weight
//   coinItemID, coinItemAmount  what dismantling an item won from the capsule gives (the server keeps the gacha id
//                         on the item) when the item has no item_break entry: the server pays an item's break
//                         ingredients first, and the coin only when there are none; amount 0 gives nothing. The client reads a missing amount as 1, the server as 0
//   shopID                the capsule shop the client's gacha window opens
//   randomBoxGroup, ui, seasonNumberOnIcon, randomBoxSeasonKR/CN/NA/JP, saleEndDate (yyyy-MM-dd-HH-mm-ss)
//                         how the client's gacha window shows the capsule
//   paymentIconTag        client rows only: the item whose icon shows the price
//   randomBoxGroupDesc    on the client rows, but the client never reads it (nor does the server)
//   feature, locale       rows with one id for other features or locales
// The server's ingest reads randomBoxGroup, the seasons, seasonNumberOnIcon and ui as bytes and the ids and the amount
// as ints (an empty value is 0); a value it cannot read stops the ingest.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>The checks of one gacha row, shared by the editor (values as shown) and the plan (final rows).</summary>
    public static class GachaRules
    {
        public const string Entry = "table/gacha_info.xml", Rows = "randomBox", Key = "randomBoxID";
        public const string Space = "gacha";
        /// <summary>The gacha ids of mods: no shipped capsule uses this band (shipped ids have 7 digits).</summary>
        public const long From = 99100000, To = 99109999;
        public const string DropBox = "individualDropBoxID", Coin = "coinItemID", Amount = "coinItemAmount", Shop = "shopID";
        public const string SaleEnd = "saleEndDate", Group = "randomBoxGroup", Ui = "ui";
        /// <summary>The item function that opens a capsule.</summary>
        public const string Function = "OpenGachaBox";

        /// <summary>Fields the server reads as a byte (0 to 255).</summary>
        public static readonly string[] Bytes = { Group, "randomBoxSeasonKR", "randomBoxSeasonCN", "randomBoxSeasonNA", "randomBoxSeasonJP", "seasonNumberOnIcon", Ui };
        /// <summary>Fields the server reads as an int.</summary>
        public static readonly string[] Ints = { Key, DropBox, Shop, Coin, Amount };
        /// <summary>Fields only the client's rows have; edits of them go to the client sides only.</summary>
        public static readonly string[] ClientOnly = { "paymentIconTag", "randomBoxGroupDesc" };
        /// <summary>The fields in the order the shipped rows have them (feature and locale after).</summary>
        public static readonly string[] Fields =
        {
            Key, Group, "randomBoxSeasonKR", "randomBoxSeasonCN", "randomBoxSeasonNA", "randomBoxSeasonJP", "seasonNumberOnIcon",
            SaleEnd, Ui, "paymentIconTag", DropBox, Shop, Coin, Amount, "randomBoxGroupDesc", "feature", "locale",
        };

        public static bool IsClientOnly(string field) { return Array.IndexOf(ClientOnly, field) >= 0; }

        /// <summary>What a field does, for the view.</summary>
        public static string Means(string field)
        {
            switch (field)
            {
                case Key: return "the gacha id; a capsule item opens it (OpenGachaBox \"id,...\")";
                case Group: return "the capsule group of the gacha window (1 to 4 shipped)";
                case "randomBoxSeasonKR": case "randomBoxSeasonCN": case "randomBoxSeasonNA": case "randomBoxSeasonJP": return "the season the window shows for that region";
                case "seasonNumberOnIcon": return "1: the icon shows the season number";
                case SaleEnd: return "the end of sale the window shows, yyyy-MM-dd-HH-mm-ss";
                case Ui: return "the window layout (1 to 4 shipped)";
                case "paymentIconTag": return "client only: the item whose icon shows the price";
                case DropBox: return "the drop box rolled for each capsule (Shops and Drops edits it)";
                case Shop: return "the capsule shop the window opens";
                case Coin: return "the item dismantling a won item gives (when the item has no break entry)";
                case Amount: return "how many coins; 0 gives none (the client shows 1 when empty, the server reads 0)";
                case "randomBoxGroupDesc": return "never read: neither the client nor the server reads it";
                case "feature": return "the row is for this feature only";
                case "locale": return "the row is for this locale only";
                default: return "";
            }
        }

        // a number as text to compare (sign and blanks allowed; empty is 0)
        static bool ReadInt(string v, out long n)
        {
            n = 0;
            v = (v ?? "").Trim();
            if (v.Length == 0) return true;
            int i;
            if (!int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out i)) return false;
            n = i;
            return true;
        }

        /// <summary>The value the server reads; false when it cannot read it. Its ingest drops an attribute that is
        /// exactly empty (0); any other text goes to XmlConvert: an int may have a sign and blanks around it, a byte
        /// blanks only, and a value of blanks alone is not read.</summary>
        public static bool ServerValue(string field, string v, out long n)
        {
            n = 0;
            if (string.IsNullOrEmpty(v)) return true;
            const NumberStyles blanks = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
            if (Array.IndexOf(Bytes, field) >= 0)
            {
                byte b;
                if (!byte.TryParse(v, blanks, CultureInfo.InvariantCulture, out b)) return false;
                n = b;
                return true;
            }
            int i;
            if (!int.TryParse(v, blanks | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out i)) return false;
            n = i;
            return true;
        }

        /// <summary>The value a side reads, as text to compare: numbers as numbers, an empty amount as 1 on a client
        /// and 0 on the server.</summary>
        public static string Effective(string field, string v, bool client)
        {
            string t = (v ?? "").Trim();
            if (field == Amount && t.Length == 0) return client ? "1" : "0";
            if (Array.IndexOf(Bytes, field) >= 0 || Array.IndexOf(Ints, field) >= 0)
            {
                long n;
                return ReadInt(t, out n) ? n.ToString(CultureInfo.InvariantCulture) : t;
            }
            return t;
        }

        static readonly Regex Date = new Regex(@"^\d{4}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2}$");

        /// <summary>
        /// The problems of a row: errors (what the server's ingest cannot read) and warnings (what makes the capsule
        /// give or refund nothing). get gives the row's values.
        /// </summary>
        public static void Check(Func<string, string> get, List<string> errors, List<string> warnings)
        {
            foreach (string f in Bytes)
            {
                string v = get(f);
                long n;
                if (!ServerValue(f, v, out n)) errors.Add(f + " \"" + v + "\" is not a number of 0 to 255 the server reads (no sign, not blank): its ingest stops on it");
            }
            foreach (string f in Ints)
            {
                string v = get(f);
                long n;
                if (!ServerValue(f, v, out n)) errors.Add(f + " \"" + v + "\" is not a whole number the server reads (an int, not blank): its ingest stops on it");
            }
            long box, coin, amount, id;
            if (ServerValue(Key, get(Key), out id) && id <= 0) warnings.Add(Key + " is " + id + ": no capsule can open it");
            if (ServerValue(DropBox, get(DropBox), out box) && box <= 0) warnings.Add(DropBox + " is " + box + ": the capsule gives nothing and is not used up (the server refuses to open it)");
            bool hasCoin = ServerValue(Coin, get(Coin), out coin) && coin > 0;
            if (ServerValue(Amount, get(Amount), out amount))
            {
                if (hasCoin && amount <= 0) warnings.Add(Amount + " is " + (string.IsNullOrEmpty((get(Amount) ?? "").Trim()) ? "empty (the server reads 0)" : amount.ToString(CultureInfo.InvariantCulture)) + ": dismantling a won item without a break entry gives no coin " + coin);
                if (!hasCoin && amount > 0) warnings.Add(Coin + " is 0: dismantling a won item without a break entry gives nothing, whatever " + Amount + " says");
            }
            string d = (get(SaleEnd) ?? "").Trim();
            if (d.Length > 0 && !Date.IsMatch(d)) warnings.Add(SaleEnd + " \"" + d + "\" is not yyyy-MM-dd-HH-mm-ss");
        }

        /// <summary>The problems as one list, errors first (for the editor).</summary>
        public static List<string> Check(Func<string, string> get)
        {
            var e = new List<string>();
            var w = new List<string>();
            Check(get, e, w);
            e.AddRange(w);
            return e;
        }
    }

    /// <summary>A capsule item: an item whose function is OpenGachaBox, and the gacha id its parameter names.</summary>
    public sealed class GachaCapsule
    {
        public long Item;
        /// <summary>The function's parameter as written.</summary>
        public string Parameter;
        /// <summary>The gacha id (the first part); 0 when the parameter cannot be read.</summary>
        public long Gacha;
        /// <summary>Why the server cannot open it (a part that is not a number); null: none.</summary>
        public string Problem;
        /// <summary>The entry the item is read from.</summary>
        public string Entry;
    }

    /// <summary>Finds the capsule items of the server's item files.</summary>
    public static class GachaCapsules
    {
        static readonly byte[] Needle = Encoding.ASCII.GetBytes(GachaRules.Function);
        static readonly Regex Tag = new Regex(@"<function\b[^>]*>", RegexOptions.IgnoreCase);
        static readonly Regex Name = new Regex(@"\bname\s*=\s*[""']" + GachaRules.Function + @"[""']");
        static readonly Regex Param = new Regex(@"\bparameter\s*=\s*""([^""]*)""|\bparameter\s*=\s*'([^']*)'");
        static readonly Regex FileId = new Regex(@"(?:^|/)(\d+)\.xml$", RegexOptions.IgnoreCase);

        /// <summary>True for an entry that holds one item of the server (item/**/{id}.xml).</summary>
        public static bool IsItemEntry(string name)
        {
            string n = name.Replace('\\', '/');
            return n.StartsWith("item/", StringComparison.OrdinalIgnoreCase) && FileId.IsMatch(n);
        }

        /// <summary>The capsules of one item file (none when the bytes do not name the function).</summary>
        public static List<GachaCapsule> Read(string entry, byte[] bytes)
        {
            var l = new List<GachaCapsule>();
            if (bytes == null || IndexOf(bytes, Needle) < 0) return l;
            Match fm = FileId.Match(entry.Replace('\\', '/'));
            long item;
            if (!fm.Success || !long.TryParse(fm.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out item)) return l;
            string text = Encoding.UTF8.GetString(bytes);
            foreach (Match m in Tag.Matches(text))
            {
                if (!Name.IsMatch(m.Value)) continue;
                Match p = Param.Match(m.Value);
                string param = p.Success ? XmlUnescape(p.Groups[1].Success ? p.Groups[1].Value : p.Groups[2].Value) : "";
                l.Add(Parse(item, param, entry));
            }
            return l;
        }

        /// <summary>A capsule as the server reads its parameter: every comma part must be a whole number.</summary>
        public static GachaCapsule Parse(long item, string param, string entry)
        {
            var c = new GachaCapsule { Item = item, Parameter = param, Entry = entry };
            string[] parts = (param ?? "").Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                int n;
                if (!int.TryParse(parts[i].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n))
                {
                    c.Problem = "its OpenGachaBox parameter \"" + param + "\" has the part \"" + parts[i] + "\", which is not a whole number: the server fails when it is opened";
                    c.Gacha = 0;
                    return c;
                }
                if (i == 0) c.Gacha = n;
            }
            return c;
        }

        /// <summary>The capsules of every item entry of an archive; read gives the bytes of an entry (null: none).</summary>
        public static List<GachaCapsule> Scan(M2dArchive arch, Func<M2dEntry, byte[]> read)
        {
            var l = new List<GachaCapsule>();
            foreach (M2dEntry e in arch.Entries)
                if (IsItemEntry(e.Name)) l.AddRange(Read(e.Name, read(e)));
            return l;
        }

        /// <summary>The capsules by gacha id.</summary>
        public static Dictionary<long, List<GachaCapsule>> ByGacha(IEnumerable<GachaCapsule> capsules)
        {
            var d = new Dictionary<long, List<GachaCapsule>>();
            foreach (GachaCapsule c in capsules)
            {
                if (c.Problem != null) continue;
                List<GachaCapsule> l;
                if (!d.TryGetValue(c.Gacha, out l)) d[c.Gacha] = l = new List<GachaCapsule>();
                l.Add(c);
            }
            return d;
        }

        static string XmlUnescape(string v)
        {
            return v.Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
        }

        static int IndexOf(byte[] hay, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= hay.Length; i++)
            {
                if (hay[i] != needle[0]) continue;
                int k = 1;
                while (k < needle.Length && hay[i + k] == needle[k]) k++;
                if (k == needle.Length) return i;
            }
            return -1;
        }
    }

    /// <summary>The odds of one row of a drop box group.</summary>
    public sealed class GachaOddsRow
    {
        public string Item;
        public long Weight, Min, Max;
        /// <summary>The share of the group's weight (0 to 100); a row of weight 0 or less never drops.</summary>
        public double Percent;
    }

    /// <summary>The odds of one group of a drop box.</summary>
    public sealed class GachaOddsGroup
    {
        public string Group;
        public string DropCount;
        public long TotalWeight;
        /// <summary>The server picks by the job weights (properJobWeight/imProperJobWeight) for this group, not by weight.</summary>
        public bool JobWeights;
        /// <summary>isApplySmartGenderDrop: the other gender's items are left out of the roll.</summary>
        public bool SmartGender;
        /// <summary>A smart group whose rows have no weight and no job weight: the server gives one of the rows for
        /// the player's job, each as likely, once (whatever the drop count).</summary>
        public bool Uniform;
        /// <summary>dropGroupMinLevel: players below this level get nothing from the group (null when not set).</summary>
        public string MinLevel;
        public readonly List<GachaOddsRow> Rows = new List<GachaOddsRow>();
    }

    public static class GachaOdds
    {
        static long Num(string v)
        {
            long n;
            return long.TryParse((v ?? "").Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        static string Of(List<KeyValuePair<string, string>> attrs, string name)
        {
            foreach (KeyValuePair<string, string> kv in attrs) if (kv.Key == name) return kv.Value;
            return null;
        }

        /// <summary>
        /// The odds of a drop box on one side (as GroupEditor shows it): per group, each row's share of the group's
        /// weight. The server rolls each group's drop count, then picks that many rows by weight (a row can come up
        /// again). A group with a smartDropRate and job weights is picked by the job weights instead; one with a
        /// smartDropRate and no weight on any row gives one row for the player's job, each as likely (Uniform: the
        /// percent is of all the rows, the server picks among those for the job).
        /// </summary>
        public static List<GachaOddsGroup> Of(GroupSideView side)
        {
            var l = new List<GachaOddsGroup>();
            var byGroup = new Dictionary<string, GachaOddsGroup>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<KeyValuePair<string, string>>> g in side.Groups)
            {
                var og = new GachaOddsGroup
                {
                    Group = g.Key,
                    DropCount = Of(g.Value, "dropCount"),
                    SmartGender = (Of(g.Value, "isApplySmartGenderDrop") ?? "").Trim() == "1",
                    MinLevel = Num(Of(g.Value, "dropGroupMinLevel")) > 0 ? Of(g.Value, "dropGroupMinLevel").Trim() : null,
                };
                og.JobWeights = Num(Of(g.Value, "smartDropRate")) > 0;
                byGroup[g.Key] = og;
                l.Add(og);
            }
            var any = new Dictionary<string, bool>(StringComparer.Ordinal);
            var smart = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (GachaOddsGroup og in l) if (og.JobWeights) smart[og.Group] = true;
            foreach (GroupRowView r in side.Rows)
            {
                GachaOddsGroup og;
                if (!byGroup.TryGetValue(r.Group ?? "", out og))
                {
                    og = new GachaOddsGroup { Group = r.Group ?? "" };
                    byGroup[og.Group] = og;
                    l.Add(og);
                }
                var row = new GachaOddsRow { Item = r.Get("itemID") ?? r.Item, Weight = Num(r.Get("weight")), Min = Num(r.Get("minCount")), Max = Num(r.Get("maxCount")) };
                og.Rows.Add(row);
                if (Num(r.Get("properJobWeight")) != 0 || Num(r.Get("imProperJobWeight")) != 0) any[og.Group] = true;
            }
            foreach (GachaOddsGroup og in l)
            {
                // a smart group whose rows have no job weight is rolled by the plain weight, and one whose rows have no
                // weight at all by one even pick of the rows for the player's job
                if (og.JobWeights && !any.ContainsKey(og.Group)) og.JobWeights = false;
                if (smart.ContainsKey(og.Group) && !og.JobWeights && og.Rows.Count > 0 && og.Rows.TrueForAll(r => r.Weight == 0))
                {
                    og.Uniform = true;
                    foreach (GachaOddsRow r in og.Rows) r.Percent = 100.0 / og.Rows.Count;
                    continue;
                }
                foreach (GachaOddsRow r in og.Rows) if (r.Weight > 0) og.TotalWeight += r.Weight;
                foreach (GachaOddsRow r in og.Rows) r.Percent = og.TotalWeight > 0 && r.Weight > 0 ? 100.0 * r.Weight / og.TotalWeight : 0;
            }
            return l;
        }
    }

    /// <summary>A gacha capsule row as the editor shows it: its row on every side that has it (null where a side has none).</summary>
    public sealed class Gacha
    {
        public readonly TableRow[] Rows;
        internal Gacha(int sides) { Rows = new TableRow[sides]; }

        public TableRow Shown { get { foreach (TableRow r in Rows) if (r != null) return r; return null; } }
        public bool IsAdded { get { TableRow r = Shown; return r != null && r.IsAdded; } }
        public bool IsDeleted { get { foreach (TableRow r in Rows) if (r != null && !r.IsDeleted) return false; return true; } }
        public bool IsChanged { get { foreach (TableRow r in Rows) if (r != null && r.IsChanged) return true; return false; } }
    }

    /// <summary>
    /// The gacha editor of one mod: gacha_info.xml of every side that ships it (kms client, server; the global client
    /// when there is no server), read through TableDraft and written to every side at once (the client-only fields to
    /// the client sides).
    /// </summary>
    public sealed class GachaEditor
    {
        public readonly DataSet Data;
        public readonly Mod Mod;
        public readonly List<TableDraft> Sides = new List<TableDraft>();
        public readonly List<Gacha> Gachas = new List<Gacha>();

        public GachaEditor(DataSet data, Mod mod)
        {
            Data = data;
            Mod = mod;
            foreach (string a in SideArchives(data, mod)) Sides.Add(new TableDraft(data, mod, a, GachaRules.Entry, null));
            if (Sides.Count == 0) throw new ModKitException("no target of the data set has " + GachaRules.Entry);
            Pair();
        }

        /// <summary>The archives a capsule is written to: as for recipes (RecipeEditor.SideArchives).</summary>
        public static List<string> SideArchives(DataSet data, Mod mod)
        {
            var l = new List<string>();
            foreach (Profile p in new[] { Profile.KmsClient, Profile.Server, Profile.GmsClient })
            {
                if (!data.Has(p)) continue;
                string a = p.ArchiveIn("x:Xml.m2d");
                if (p == Profile.GmsClient && data.Has(Profile.Server) && !HasLines(mod, a)) continue;
                M2dArchive arch;
                try { arch = data.Open(a); }
                catch (ModKitException) { continue; }
                if (arch.Find(GachaRules.Entry) != null) l.Add(a);
            }
            return l;
        }

        static bool HasLines(Mod mod, string archive)
        {
            if (mod == null) return false;
            foreach (ModOp op in mod.Ops)
            {
                var ed = op as EditOp;
                if (ed != null && string.Equals(ed.Archive, archive, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ed.Entry.Replace('\\', '/'), GachaRules.Entry, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>True for a side of the server (the other sides are clients).</summary>
        public bool IsServer(int side) { return DataSet.RootOf(Sides[side].Archive) == Profile.Server.Root; }

        string KeyOf(TableDraft s, TableRow r)
        {
            return (s.Get(r, GachaRules.Key) ?? "").Trim() + "\n" + (s.Get(r, "feature") ?? "") + "\n" + (s.Get(r, "locale") ?? "");
        }

        void Pair()
        {
            Gachas.Clear();
            var byKey = new Dictionary<string, List<Gacha>>();
            for (int i = 0; i < Sides.Count; i++)
            {
                var seen = new Dictionary<string, int>();
                foreach (TableRow r in Sides[i].Rows)
                {
                    if (r.Base.Name != GachaRules.Rows || string.IsNullOrEmpty(Sides[i].Get(r, GachaRules.Key))) continue;
                    string k = KeyOf(Sides[i], r);
                    List<Gacha> list;
                    if (!byKey.TryGetValue(k, out list)) byKey[k] = list = new List<Gacha>();
                    int n;
                    seen.TryGetValue(k, out n);
                    seen[k] = n + 1;
                    if (n >= list.Count)
                    {
                        var made = new Gacha(Sides.Count);
                        list.Add(made);
                        Gachas.Add(made);
                    }
                    list[n].Rows[i] = r;
                }
            }
        }

        // ---- reading ----

        public long Id(Gacha g) { long id; return long.TryParse((Get(g, GachaRules.Key) ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id) ? id : 0; }

        // the sides a field lives on: client-only fields on the clients
        bool On(int side, string field) { return !(GachaRules.IsClientOnly(field) && IsServer(side)); }

        /// <summary>The value of the first side that has the capsule (and the field).</summary>
        public string Get(Gacha g, string field)
        {
            for (int i = 0; i < Sides.Count; i++) if (g.Rows[i] != null && On(i, field)) return Sides[i].Get(g.Rows[i], field);
            return null;
        }

        /// <summary>The value of one side's row (null where the side has no row).</summary>
        public string GetOn(Gacha g, int side, string field)
        {
            return g.Rows[side] == null ? null : Sides[side].Get(g.Rows[side], field);
        }

        /// <summary>The shipped value of the first side that has the capsule (null for a capsule the mod adds).</summary>
        public string Shipped(Gacha g, string field)
        {
            if (g.IsAdded) return null;
            for (int i = 0; i < Sides.Count; i++) if (g.Rows[i] != null && On(i, field)) return Sides[i].GetOriginal(g.Rows[i], field);
            return null;
        }

        public string Key(Gacha g) { return (Get(g, GachaRules.Key) ?? "").Trim() + "\n" + (Get(g, "feature") ?? "") + "\n" + (Get(g, "locale") ?? ""); }

        /// <summary>True when the sides that have the capsule read the field differently (an empty amount is 1 on a
        /// client and 0 on the server).</summary>
        public bool Differs(Gacha g, string field)
        {
            bool any = false;
            string first = null;
            for (int i = 0; i < Sides.Count; i++)
            {
                if (g.Rows[i] == null || !On(i, field)) continue;
                string v = GachaRules.Effective(field, Sides[i].Get(g.Rows[i], field), !IsServer(i));
                if (!any) { first = v; any = true; }
                else if (v != first) return true;
            }
            return false;
        }

        public List<string> MissingOn(Gacha g)
        {
            var l = new List<string>();
            for (int i = 0; i < Sides.Count; i++) if (g.Rows[i] == null) l.Add(Sides[i].Archive);
            return l;
        }

        List<string> Have(Gacha g)
        {
            var l = new List<string>();
            for (int i = 0; i < Sides.Count; i++) if (g.Rows[i] != null) l.Add(Sides[i].Archive);
            return l;
        }

        /// <summary>Capsules whose id, drop box, coin item or shop holds text.</summary>
        public List<Gacha> Find(string text)
        {
            text = (text ?? "").Trim();
            var l = new List<Gacha>();
            foreach (Gacha g in Gachas)
            {
                if (text.Length > 0)
                {
                    bool hit = false;
                    foreach (string f in new[] { GachaRules.Key, GachaRules.DropBox, GachaRules.Coin, GachaRules.Shop })
                        if ((Get(g, f) ?? "").IndexOf(text, StringComparison.Ordinal) >= 0) hit = true;
                    if (!hit) continue;
                }
                l.Add(g);
            }
            return l;
        }

        /// <summary>The server's value of a field (the row of the server side, else the shown value).</summary>
        public string ServerGet(Gacha g, string field)
        {
            for (int i = 0; i < Sides.Count; i++) if (g.Rows[i] != null && IsServer(i)) return Sides[i].Get(g.Rows[i], field);
            return Get(g, field);
        }

        /// <summary>The problems of the capsule as shown (the plan checks the final rows the same way, and more).</summary>
        public List<string> Check(Gacha g)
        {
            var l = new List<string>();
            if (g.IsDeleted) return l;
            // the server's reading decides what a capsule gives
            List<string> rules = GachaRules.Check(f => ServerGet(g, f));
            if (g.IsAdded || g.IsChanged) l.AddRange(rules);
            if (g.IsAdded)
            {
                long id = Id(g);
                if (id < GachaRules.From || id > GachaRules.To)
                    l.Add("new id " + id + " is outside of " + GachaRules.From + "-" + GachaRules.To + ", the range of new capsules");
            }
            List<string> missing = MissingOn(g);
            if (missing.Count > 0) l.Add("only on " + string.Join(", ", Have(g).ToArray()) + "; not on " + string.Join(", ", missing.ToArray()));
            foreach (string f in GachaRules.Fields)
                if (Differs(g, f)) l.Add("the sides read " + f + " differently: the client shows one value and the server uses another");
            return l;
        }

        // ---- editing ----

        /// <summary>Sets a field on every side that has the capsule (a client-only field on the clients; null removes the attribute).</summary>
        public void Set(Gacha g, string field, string value)
        {
            if (field == GachaRules.Key || field == "feature" || field == "locale")
            {
                if (!g.IsAdded) throw new ModKitException(field + " of a shipped capsule is its key; clone the capsule for a new one");
                if (field == GachaRules.Key) CheckNewId(ParseId(value), g);
            }
            bool anySide = false;
            for (int i = 0; i < Sides.Count; i++)
            {
                if (g.Rows[i] == null) continue;
                if (g.Rows[i].IsDeleted) throw new ModKitException("capsule " + Get(g, GachaRules.Key) + " is removed on " + Sides[i].Archive + "; restore it before a change");
                if (On(i, field)) anySide = true;
            }
            if (!anySide) throw new ModKitException(field + " is a field of the client's rows, and no client side has this capsule");
            long old = Id(g);
            for (int i = 0; i < Sides.Count; i++)
                if (g.Rows[i] != null && On(i, field)) Sides[i].Set(g.Rows[i], field, value);
            if (field == GachaRules.Key)
            {
                long now = Id(g);
                if (now != old)
                {
                    bool still = false;
                    foreach (Gacha o in Gachas) if (o != g && o.IsAdded && Id(o) == old) still = true;
                    if (!still) IdSpaces.Unreserve(Mod, GachaRules.Space, old);
                    IdSpaces.Reserve(Mod, GachaRules.Space, now);
                }
            }
        }

        static long ParseId(string v)
        {
            long id;
            if (!long.TryParse((v ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0) throw new ModKitException("\"" + v + "\" is not a gacha id");
            return id;
        }

        /// <summary>The other enabled mods, read when a new id is checked or asked for (null: none).</summary>
        public Func<IEnumerable<Mod>> Others;

        List<Mod> OtherMods()
        {
            var l = new List<Mod>();
            IEnumerable<Mod> mods = Others == null ? null : Others();
            if (mods != null) foreach (Mod m in mods) if (m != null && m != Mod && m.Id != Mod.Id) l.Add(m);
            return l;
        }

        void CheckNewId(long id, Gacha self)
        {
            if (id < GachaRules.From || id > GachaRules.To)
                throw new ModKitException("new gacha ids are " + GachaRules.From + " to " + GachaRules.To + "; " + id + " is outside");
            foreach (Gacha o in Gachas)
                if (o != self && Id(o) == id) throw new ModKitException("capsule " + id + " is there already");
            foreach (Mod m in OtherMods())
            {
                if (AddedIds(m).Contains(id)) throw new ModKitException("the enabled mod " + m.Id + " adds a capsule " + id + " already");
                foreach (IdRange r in m.Ids)
                    if (r.Space == GachaRules.Space && id >= r.From && id <= r.To)
                        throw new ModKitException("the gacha id " + id + " is reserved by the enabled mod " + m.Id);
            }
        }

        /// <summary>A new capsule that copies source (its shown values), on every side, with the new id.</summary>
        public Gacha Clone(Gacha source, long newId)
        {
            CheckNewId(newId, null);
            if (source.IsDeleted) throw new ModKitException("capsule " + Get(source, GachaRules.Key) + " is removed; restore it before copying");
            var made = new Gacha(Sides.Count);
            for (int i = 0; i < Sides.Count; i++)
            {
                TableRow from = source.Rows[i];
                if (from != null && !from.IsDeleted) made.Rows[i] = Sides[i].Clone(from);
                else
                {
                    // a side without the row: the values of the first side that has it, without the fields it does not hold
                    int s = Array.IndexOf(source.Rows, source.Shown);
                    made.Rows[i] = Sides[i].AddRow(RowXml(Sides[s], source.Shown, f => On(i, f)));
                }
                Sides[i].Set(made.Rows[i], GachaRules.Key, newId.ToString(CultureInfo.InvariantCulture));
            }
            IdSpaces.Reserve(Mod, GachaRules.Space, newId);
            Gachas.Add(made);
            return made;
        }

        /// <summary>A new capsule on every side: it rolls the drop box and refunds coinAmount of coin per dismantled item.</summary>
        public Gacha Add(long newId, long dropBox, long coin, long coinAmount)
        {
            CheckNewId(newId, null);
            var made = new Gacha(Sides.Count);
            for (int i = 0; i < Sides.Count; i++)
            {
                var sb = new StringBuilder("<" + GachaRules.Rows);
                foreach (KeyValuePair<string, string> kv in NewRow(newId, dropBox, coin, coinAmount))
                    if (On(i, kv.Key)) sb.Append(' ').Append(kv.Key).Append("=\"").Append(XmlEscape(kv.Value)).Append('"');
                sb.Append(" />");
                made.Rows[i] = Sides[i].AddRow(sb.ToString());
            }
            IdSpaces.Reserve(Mod, GachaRules.Space, newId);
            Gachas.Add(made);
            return made;
        }

        static List<KeyValuePair<string, string>> NewRow(long id, long box, long coin, long amount)
        {
            var l = new List<KeyValuePair<string, string>>();
            Action<string, string> a = (k, v) => l.Add(new KeyValuePair<string, string>(k, v));
            Func<long, string> n = v => v.ToString(CultureInfo.InvariantCulture);
            a(GachaRules.Key, n(id)); a(GachaRules.Group, "1");
            a("randomBoxSeasonKR", "1"); a("randomBoxSeasonCN", ""); a("randomBoxSeasonNA", ""); a("randomBoxSeasonJP", "");
            a("seasonNumberOnIcon", "1"); a(GachaRules.SaleEnd, "2099-12-31-23-59-59"); a(GachaRules.Ui, "1");
            a("paymentIconTag", "");
            a(GachaRules.DropBox, n(box)); a(GachaRules.Shop, "0"); a(GachaRules.Coin, n(coin)); a(GachaRules.Amount, n(amount));
            return l;
        }

        static string RowXml(TableDraft s, TableRow r, Func<string, bool> keep)
        {
            var sb = new StringBuilder("<" + r.Base.Name);
            foreach (string c in s.Columns)
            {
                string v = s.Get(r, c);
                if (v != null && keep(c)) sb.Append(' ').Append(c).Append("=\"").Append(XmlEscape(v)).Append('"');
            }
            return sb.Append(" />").ToString();
        }

        static string XmlEscape(string v)
        {
            return v.Replace("&", "&amp;").Replace("<", "&lt;").Replace("\"", "&quot;");
        }

        /// <summary>Removes a capsule: a new one goes away (and its id is free again), a shipped one is deleted on every side.</summary>
        public void Remove(Gacha g)
        {
            bool added = g.IsAdded;
            long id = Id(g);
            for (int i = 0; i < Sides.Count; i++) if (g.Rows[i] != null) Sides[i].Delete(g.Rows[i]);
            if (added)
            {
                Gachas.Remove(g);
                bool still = false;
                foreach (Gacha o in Gachas) if (Id(o) == id) still = true;
                if (!still) IdSpaces.Unreserve(Mod, GachaRules.Space, id);
            }
        }

        public void Restore(Gacha g)
        {
            for (int i = 0; i < Sides.Count; i++) if (g.Rows[i] != null) Sides[i].Restore(g.Rows[i]);
        }

        /// <summary>The gacha ids a mod's &lt;add&gt; rows of the table give.</summary>
        public static List<long> AddedIds(Mod mod)
        {
            var l = new List<long>();
            var inRow = new Regex(@"\s" + GachaRules.Key + @"\s*=\s*[""'](\d+)[""']");
            foreach (ModOp op in mod.Ops)
            {
                var ed = op as EditOp;
                if (ed == null || !string.Equals(ed.Entry.Replace('\\', '/'), GachaRules.Entry, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (RowOp r in ed.Rows)
                {
                    if (r.Kind != RowOpKind.Add) continue;
                    string id = null;
                    foreach (AttrOp a in r.Attrs) if (a.Kind == AttrOpKind.Set && a.Name == GachaRules.Key && a.At == null) id = a.Value;
                    if (id == null && r.Xml != null) { Match mt = inRow.Match(r.Xml); if (mt.Success) id = mt.Groups[1].Value; }
                    long n;
                    if (id != null && long.TryParse(id.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n) && !l.Contains(n)) l.Add(n);
                }
            }
            return l;
        }

        /// <summary>The lowest free ids of the range of new capsules: not shipped on any side and not taken by the mods.</summary>
        public List<long> NextIds(IEnumerable<Mod> mods, int count, Action<ProgressInfo> progress)
        {
            var all = new List<Mod>(mods ?? new Mod[0]);
            if (!all.Contains(Mod)) all.Add(Mod);
            var taken = new HashSet<long>();
            foreach (Gacha g in Gachas) taken.Add(Id(g));
            foreach (Mod m in all) if (m != Mod) foreach (long id in AddedIds(m)) taken.Add(id);
            List<long> free = IdSpaces.NextFreeGroupIds(Data, all, GachaRules.Space, count + taken.Count, progress);
            free.RemoveAll(taken.Contains);
            if (free.Count > count) free.RemoveRange(count, free.Count - count);
            return free;
        }

        // ---- what the capsules open ----

        /// <summary>The capsule items of the server's item files as the mod leaves them (empty without a server).
        /// Slow (every item file is read): run it outside the UI thread.</summary>
        public List<GachaCapsule> Capsules()
        {
            var l = new List<GachaCapsule>();
            string a = Profile.Server.ArchiveIn("x:Xml.m2d");
            if (!Data.Has(Profile.Server) || !Data.Exists(a)) return l;
            M2dArchive arch = Data.Open(a);
            // the mod's item files of the server are its replaced or added entries; the rest as shipped
            var own = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Mod != null)
                foreach (ModOp op in Mod.Ops)
                {
                    var en = op as EntryOp;
                    if (en == null || en.Name == null || !string.Equals(DataSet.NormalizeRef(en.Archive), DataSet.NormalizeRef(a), StringComparison.OrdinalIgnoreCase)) continue;
                    string n = en.Name.Replace('\\', '/');
                    if (!GachaCapsules.IsItemEntry(n)) continue;
                    if (en.Delete) { gone.Add(n); own.Remove(n); continue; }
                    byte[] b = ModBytes(en, arch);
                    if (b != null) { own[n] = b; gone.Remove(n); }
                }
            foreach (M2dEntry e in arch.Entries)
            {
                string n = e.Name.Replace('\\', '/');
                if (!GachaCapsules.IsItemEntry(n) || gone.Contains(n) || own.ContainsKey(n)) continue;
                l.AddRange(GachaCapsules.Read(n, arch.Read(e)));
            }
            foreach (KeyValuePair<string, byte[]> kv in own) l.AddRange(GachaCapsules.Read(kv.Key, kv.Value));
            return l;
        }

        // the bytes of the mod's entry op (its unsaved file, its file in the folder, or the copied entry); null: none
        byte[] ModBytes(EntryOp op, M2dArchive arch)
        {
            if (op.File != null)
            {
                byte[] b;
                if (Mod.Files.TryGetValue(op.File.Replace('\\', '/'), out b)) return b;
                string full = Mod.Dir == null ? null : Mod.PathIn(Mod.Dir, op.File);
                try { return full != null && File.Exists(full) ? File.ReadAllBytes(full) : null; }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
            }
            M2dEntry src = op.Copy == null ? null : arch.Find(op.Copy);
            return src == null ? null : arch.Read(src);
        }

        GroupEditor groups;     // read once: it holds the drop tables as the mod leaves them

        /// <summary>The drop box odds of a capsule on the server as the mod leaves it (null: no such box, or no server).
        /// The first call reads the drop tables.</summary>
        public List<GachaOddsGroup> Odds(Gacha g, out string why)
        {
            why = null;
            long box;
            if (!GachaRules.ServerValue(GachaRules.DropBox, ServerGet(g, GachaRules.DropBox), out box) || box <= 0) { why = "the capsule names no drop box"; return null; }
            if (!Data.Has(Profile.Server)) { why = "drop boxes are server data and the data set has no server"; return null; }
            GroupView v;
            try
            {
                if (groups == null) groups = new GroupEditor(Data, Mod);
                v = groups.Open(GroupKind.DropBox, box);
            }
            catch (ModKitException x) { why = x.Message; return null; }
            GroupSideView side = v.Side(Planner.ServerTables);
            if (side == null || !side.Present) { why = "the server has no drop box " + box; return null; }
            return GachaOdds.Of(side);
        }
    }
}
