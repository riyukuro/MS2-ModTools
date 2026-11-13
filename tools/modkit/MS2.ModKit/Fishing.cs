// Fishing: the fish, the fishing spots, the lures and the fish boxes of the server. Five tables of Server.m2d:
//
//   table/Server/fish.xml                <fish id ...>        a fish: habitat (the liquid it lives in), fishMastery, lv,
//       rank, pointCount (every how many catches it gives masteryPoint), exp, baitProp (the chance in 10000 that it
//       bites), smallSize and bigSize ("min-max"), ignoreSpotMastery, individualDropBoxID, bait, feature, locale
//   table/Server/fishingSpot.xml         <spot id ...>        the fishing of a map (id = the map id): liquidType (a comma
//       list), minMastery/maxMastery (the fish masteries the spot gives), globalFishBoxID, individualFishBoxID,
//       globalDropBoxId, individualDropBoxId, spotLevel, spotDropRank
//   table/Server/fishLure.xml            <lure ...>           a lure buff: additionalEffectCode, additionalEffectLevel,
//       catchRank/catchProp and spawnRank/spawnProp (comma lists that go in pairs), drop boxes
//   table/Server/globalFishBox.xml       <box id probability cubeRate> with <fish fishCode weight weightRate> lines
//   table/Server/individualFishBox.xml   the same
//
// How the server reads them (its data parser and ingest): an attribute written as "" counts as not written. A number
// may have spaces around it, a value of only spaces stops the ingest; a boolean is 1, 0, true or false. A comma list
// leaves out empty parts and reads every other part as a number. smallSize and bigSize are split at "-" and every
// part read as a number, the first two are min and max: a size that is not written stops the ingest. A habitat the
// server does not know (the names are lower case) reads as "all"; a liquid of a spot it does not know is left out.
// Rows with the same key are cut to one by their feature and locale (see Features.cs, the server's rule): the key of
// a lure is its additionalEffectCode alone, so of the levels of one lure the server keeps one. Of the lines of a box
// with the same fishCode the last weight counts.
//
// How the server fishes: the spot of the map gives its two boxes; each is used when its probability (in 10000) comes
// up; a line gives its fish when the fish table has it, its habitat is the liquid of the tile cast at (the tiles are
// the map's fluids near the player, whatever the spot lists; a water tile counts as seawater when the spot lists
// seawater) and its mastery is within the spot's min and max (unless ignoreSpotMastery). The fish is picked by the
// weights of the lines (when they are all 0, the first). Its size comes from smallSize min..max, smallSize max..bigSize min
// and bigSize max..twice bigSize max: a range whose min is above its max stops the catch. pointCount 0 stops it from
// the second catch on. The lures are read but not used by the server.
//
// The editor writes plain <edit> lines: a row of a table is a row of its element; a box the mod adds is one <add>
// with its lines inside, a line of a shipped box is a row of that box.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public enum FishTable { Fish, Spot, Lure, GlobalBox, IndividualBox }

    public static class FishingRules
    {
        public const string Archive = "server:Server.m2d";
        public const string FishEntry = "table/Server/fish.xml", SpotEntry = "table/Server/fishingSpot.xml", LureEntry = "table/Server/fishLure.xml";
        public const string GlobalBoxEntry = "table/Server/globalFishBox.xml", IndividualBoxEntry = "table/Server/individualFishBox.xml";
        /// <summary>The client's tables of fish and spots (the fish book and the fishing screen), in Xml.m2d.</summary>
        public const string ClientFishEntry = "table/fish.xml", ClientSpotEntry = "table/fishingspot.xml";
        public const string Line = "fish", FishCode = "fishCode", Weight = "weight";
        public const string Habitat = "habitat", Liquid = "liquidType", SmallSize = "smallSize", BigSize = "bigSize";
        public const string MinMastery = "minMastery", MaxMastery = "maxMastery", Mastery = "fishMastery", Ignore = "ignoreSpotMastery";
        public const string GlobalBox = "globalFishBoxID", IndividualBox = "individualFishBoxID", Probability = "probability";
        public const string Feature = "feature", Locale = "locale";

        /// <summary>The liquids the server knows, by their number.</summary>
        public static readonly string[] Liquids = { "none", "water", "seawater", "lava", "poison", "oil", "devilwater", "emeraldwater", "all" };
        public const int All = 8, Water = 1, Seawater = 2;

        public static readonly FishTable[] Tables = { FishTable.Fish, FishTable.Spot, FishTable.Lure, FishTable.GlobalBox, FishTable.IndividualBox };

        public static readonly string[] FishFields = { "id", "fishingBook", Habitat, Mastery, "lv", "rank", "pointCount", "masteryPoint", "exp", "fishingTime",
            "catchProp", "baitProp", SmallSize, BigSize, Ignore, "bait", "companion", "individualDropBoxID", Feature, Locale };
        public static readonly string[] SpotFields = { "id", Liquid, MinMastery, MaxMastery, GlobalBox, IndividualBox, "globalDropBoxId", "individualDropBoxId",
            "spotLevel", "spotDropRank", Feature, Locale };
        public static readonly string[] LureFields = { "additionalEffectCode", "additionalEffectLevel", "fishCode", "catchRank", "catchProp", "spawnRank", "spawnProp",
            "spawnFish", "spawnFishRate", "fishSize", "fishSizeProp", "globalDropBoxID", "globalDropRank", "globalDropFishingSpotMastery", "individualDropBoxID",
            "individualDropRank", "individualDropFishingSpotMastery", Feature, Locale };
        public static readonly string[] BoxFields = { "id", Probability, "cubeRate", Feature, Locale };
        public static readonly string[] LineFields = { FishCode, Weight, "weightRate" };

        // how the server reads a field: i int, s short, b bool, l int list (comma), d int list ("-"), z size, h habitat, q liquid list; others are text
        static readonly Dictionary<string, char> fishKinds = Kinds("id i fishingBook b habitat h fishMastery i rank s smallSize z bigSize z ignoreSpotMastery b lv i pointCount i "
            + "masteryPoint i exp i fishingTime i catchProp i baitProp i companion i bait l individualDropBoxID i");
        static readonly Dictionary<string, char> spotKinds = Kinds("id i minMastery i maxMastery i liquidType q globalFishBoxID i individualFishBoxID i globalDropBoxId i "
            + "individualDropBoxId i spotLevel i spotDropRank b");
        static readonly Dictionary<string, char> lureKinds = Kinds("additionalEffectCode i additionalEffectLevel i fishCode i catchRank l catchProp l spawnRank l spawnProp l "
            + "spawnFish l spawnFishRate l fishSize i fishSizeProp i globalDropBoxID i globalDropRank i globalDropFishingSpotMastery d individualDropBoxID i "
            + "individualDropRank i individualDropFishingSpotMastery d");
        static readonly Dictionary<string, char> boxKinds = Kinds("id i probability i cubeRate i");
        static readonly Dictionary<string, char> lineKinds = Kinds("fishCode i weight i weightRate i");

        static Dictionary<string, char> Kinds(string s)
        {
            var d = new Dictionary<string, char>(StringComparer.Ordinal);
            string[] p = s.Split(' ');
            for (int i = 0; i + 1 < p.Length; i += 2) d[p[i]] = p[i + 1][0];
            return d;
        }

        public static string Entry(FishTable t)
        {
            switch (t)
            {
                case FishTable.Fish: return FishEntry;
                case FishTable.Spot: return SpotEntry;
                case FishTable.Lure: return LureEntry;
                case FishTable.GlobalBox: return GlobalBoxEntry;
                default: return IndividualBoxEntry;
            }
        }

        /// <summary>The element of a row of the table.</summary>
        public static string Row(FishTable t)
        {
            switch (t)
            {
                case FishTable.Fish: return "fish";
                case FishTable.Spot: return "spot";
                case FishTable.Lure: return "lure";
                default: return "box";
            }
        }

        /// <summary>The attribute the server keys the rows by.</summary>
        public static string Key(FishTable t) { return t == FishTable.Lure ? "additionalEffectCode" : "id"; }
        public static bool IsBox(FishTable t) { return t == FishTable.GlobalBox || t == FishTable.IndividualBox; }

        public static string[] Fields(FishTable t)
        {
            switch (t)
            {
                case FishTable.Fish: return FishFields;
                case FishTable.Spot: return SpotFields;
                case FishTable.Lure: return LureFields;
                default: return BoxFields;
            }
        }

        /// <summary>A short name of a row of the table, for messages ("fish", "spot", "lure", "global fish box").</summary>
        public static string Noun(FishTable t)
        {
            switch (t)
            {
                case FishTable.Fish: return "fish";
                case FishTable.Spot: return "fishing spot";
                case FishTable.Lure: return "lure";
                case FishTable.GlobalBox: return "global fish box";
                default: return "individual fish box";
            }
        }

        static Dictionary<string, char> KindsOf(FishTable t)
        {
            switch (t)
            {
                case FishTable.Fish: return fishKinds;
                case FishTable.Spot: return spotKinds;
                case FishTable.Lure: return lureKinds;
                default: return boxKinds;
            }
        }

        static readonly char[] XmlSpace = { ' ', '\t', '\r', '\n' };

        /// <summary>A number as the server reads an attribute: spaces around it are fine; false when it stops the ingest.</summary>
        public static bool Int(string v, out int n)
        {
            n = 0;
            if (v == null) return true;
            return int.TryParse(v.Trim(XmlSpace), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n);
        }

        // a part of a list or a size, as int.Parse reads it
        static bool Part(string v, out int n) { return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n); }

        /// <summary>A comma list of numbers as the server reads it; null when it stops the ingest.</summary>
        public static List<int> IntList(string v) { return IntList(v, ','); }

        /// <summary>A list of numbers between the separator as the server reads it (the two drop masteries of a lure are "min-max"); null when it stops the ingest.</summary>
        public static List<int> IntList(string v, char separator)
        {
            var l = new List<int>();
            if (v == null) return l;
            foreach (string p in v.Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries))
            {
                int n;
                if (!Part(p.Trim(), out n)) return null;
                l.Add(n);
            }
            return l;
        }

        /// <summary>A size "min-max" as the server reads it; false when it stops the ingest.</summary>
        public static bool Size(string v, out int min, out int max)
        {
            min = max = 0;
            string[] p = (v ?? "").Split('-');
            if (p.Length < 2) return false;
            var n = new int[p.Length];
            for (int i = 0; i < p.Length; i++) if (!Part(p[i], out n[i])) return false;
            min = n[0];
            max = n[1];
            return true;
        }

        /// <summary>The liquid a value reads as (a name, a number or a comma list of them, as the server's enum parse takes it); -1 when it does not read.</summary>
        public static int LiquidOf(string v)
        {
            if (v == null) return -1;
            int value = 0;
            foreach (string part in v.Split(','))
            {
                string p = part.Trim();
                int at = Array.IndexOf(Liquids, p), n;
                if (at >= 0) value |= at;
                else if (p.Length > 0 && (char.IsDigit(p[0]) || p[0] == '-' || p[0] == '+') && int.TryParse(p, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) value |= n;
                else return -1;
            }
            return value;
        }

        /// <summary>The habitat of a fish as the server reads it: what it does not know is "all".</summary>
        public static int HabitatOf(string v) { int h = LiquidOf(v); return h < 0 ? All : h; }

        /// <summary>The liquids of a spot as the server reads them: the parts it does not know are left out.</summary>
        public static List<int> LiquidsOf(string v)
        {
            var l = new List<int>();
            if (v == null) return l;
            foreach (string p in v.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int n = LiquidOf(p.Trim());
                if (n >= 0) l.Add(n);
            }
            return l;
        }

        public static string LiquidName(int n) { return n >= 0 && n < Liquids.Length ? Liquids[n] : n.ToString(CultureInfo.InvariantCulture); }

        static string Q(string v) { return "\"" + v + "\""; }

        /// <summary>Why the server's ingest cannot read the value of a field (null: it can). "" counts as not written.</summary>
        public static string ValueProblem(FishTable t, string field, string value) { return Problem(KindsOf(t), field, value); }

        /// <summary>Why the server's ingest cannot read the value of a field of a box line.</summary>
        public static string LineProblem(string field, string value) { return Problem(lineKinds, field, value); }

        static string Problem(Dictionary<string, char> kinds, string field, string value)
        {
            char k;
            if (string.IsNullOrEmpty(value) || !kinds.TryGetValue(field, out k)) return null;
            int n;
            switch (k)
            {
                case 'i':
                    return Int(value, out n) ? null : field + " " + Q(value) + " is not a whole number";
                case 's':
                    short s;
                    return short.TryParse(value.Trim(XmlSpace), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out s) ? null : field + " " + Q(value) + " is not a whole number from -32768 to 32767";
                case 'b':
                    string b = value.Trim(XmlSpace);
                    return b == "1" || b == "0" || b == "true" || b == "false" ? null : field + " " + Q(value) + " is not 1, 0, true or false";
                case 'l':
                    return IntList(value) != null ? null : field + " " + Q(value) + " is not a comma list of whole numbers";
                case 'd':
                    return IntList(value, '-') != null ? null : field + " " + Q(value) + " is not a list of whole numbers between \"-\" (min-max)";
                case 'z':
                    int a, c;
                    return Size(value, out a, out c) ? null : field + " " + Q(value) + " is not a size \"min-max\" of whole numbers";
            }
            return null;
        }

        /// <summary>
        /// What is wrong with a row by itself: errors stop the server (its ingest or a catch), warnings are what it does
        /// not do as the row seems to want. get gives a field's value, null when it is not written (or written as "").
        /// </summary>
        public static void Check(FishTable t, Func<string, string> get, List<string> errors, List<string> warnings)
        {
            Func<string, string> g = f => { string v = get(f); return string.IsNullOrEmpty(v) ? null : v; };
            CheckRead(t, g, errors);
            int key;
            if (Int(g(Key(t)), out key) && key <= 0) warnings.Add(Key(t) + " is " + (g(Key(t)) == null ? "not set" : g(Key(t))) + ": the server reads it as " + key);
            switch (t)
            {
                case FishTable.Fish: CheckFish(g, errors, warnings); break;
                case FishTable.Spot: CheckSpot(g, warnings); break;
                case FishTable.Lure: CheckLure(g, errors); break;
                default: CheckBox(g, warnings); break;
            }
        }

        /// <summary>
        /// The errors of a row the server reads but does not keep (feature/locale): the parser reads every row, so a
        /// value it cannot read stops it; the ingest's own reads and the catch see only the row it keeps.
        /// </summary>
        public static void CheckRead(FishTable t, Func<string, string> get, List<string> errors)
        {
            foreach (string f in KindsOf(t).Keys)
            {
                string v = get(f);
                string p = Problem(KindsOf(t), f, string.IsNullOrEmpty(v) ? null : v);
                if (p != null) errors.Add(p);
            }
        }

        /// <summary>The errors of a line of a box the server does not keep (as CheckRead).</summary>
        public static void CheckLineRead(Func<string, string> get, List<string> errors)
        {
            foreach (string f in lineKinds.Keys)
            {
                string v = get(f);
                string p = Problem(lineKinds, f, string.IsNullOrEmpty(v) ? null : v);
                if (p != null) errors.Add(p);
            }
        }

        static void CheckFish(Func<string, string> g, List<string> errors, List<string> warnings)
        {
            int smin, smax, bmin, bmax, n;
            bool small = Size(g(SmallSize), out smin, out smax), big = Size(g(BigSize), out bmin, out bmax);
            foreach (string f in new[] { SmallSize, BigSize })
                if (g(f) == null) errors.Add(f + " is not set: the server's ingest stops (it needs a size \"min-max\")");
            if (small && smin > smax) errors.Add(SmallSize + " " + g(SmallSize) + ": the min is above the max, so a catch stops");
            if (small && big && smax > bmin) errors.Add(SmallSize + " max " + smax + " is above " + BigSize + " min " + bmin + ": a catch between the two sizes stops");
            if (big && bmin > bmax) warnings.Add(BigSize + " " + g(BigSize) + ": the min is above the max");
            if (big && bmax > int.MaxValue / 2) errors.Add(BigSize + " max " + bmax + " is too big: the server doubles it for a prize fish and the catch stops");
            if (Int(g("pointCount"), out n) && n == 0) errors.Add("pointCount is " + (g("pointCount") == null ? "not set" : "0") + ": the server divides by it from the second catch on and the catch stops");
            if (Int(g("baitProp"), out n) && n <= 0) warnings.Add("baitProp is " + (g("baitProp") == null ? "not set" : g("baitProp")) + ": the fish never bites");
            string h = g(Habitat);
            int at = h == null ? -1 : Array.IndexOf(Liquids, h.Trim());
            int liquid = HabitatOf(h);
            if (LiquidOf(h) < 0)
                warnings.Add(Habitat + " " + (h == null ? "is not set" : Q(h) + " is not a liquid the server knows (" + string.Join(", ", Liquids) + ")") + ": it reads as all");
            else if (at < 0)
                warnings.Add(Habitat + " " + Q(h) + " reads as " + LiquidName(liquid));
            if (liquid == All || liquid >= Liquids.Length)
                warnings.Add(Habitat + " " + LiquidName(liquid) + ": the server gives a fish only on a tile of its habitat's liquid, and a map's tiles are water, seawater, lava and the like, so the fish is not caught");
        }

        static void CheckSpot(Func<string, string> g, List<string> warnings)
        {
            int lo, hi;
            if (Int(g(MinMastery), out lo) && Int(g(MaxMastery), out hi) && lo > hi)
                warnings.Add(MinMastery + " " + lo + " is above " + MaxMastery + " " + hi + ": only fish with " + Ignore + " can be caught");
            string l = g(Liquid);
            if (l != null)
                foreach (string p in l.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    if (LiquidOf(p.Trim()) < 0) warnings.Add(Liquid + " " + Q(p.Trim()) + " is not a liquid the server knows: it leaves it out");
            int a, b;
            Int(g(GlobalBox), out a);
            Int(g(IndividualBox), out b);
            if (a <= 0 && b <= 0) warnings.Add("the spot names no fish box: nothing bites");
        }

        static void CheckLure(Func<string, string> g, List<string> errors)
        {
            foreach (string[] pair in new[] { new[] { "catchRank", "catchProp" }, new[] { "spawnRank", "spawnProp" } })
            {
                List<int> ranks = IntList(g(pair[0])), props = IntList(g(pair[1]));
                if (ranks != null && props != null && props.Count < ranks.Count)
                    errors.Add(pair[0] + " has " + ranks.Count + " numbers and " + pair[1] + " " + props.Count + ": the server's ingest stops (each needs its chance)");
            }
        }

        static void CheckBox(Func<string, string> g, List<string> warnings)
        {
            int p;
            if (Int(g(Probability), out p) && p <= 0) warnings.Add(Probability + " is " + (g(Probability) == null ? "not set" : g(Probability)) + ": the server uses the box only 1 time in 10000 or never");
        }

        /// <summary>What is wrong with a line of a box by itself (as Check does for rows).</summary>
        public static void CheckLine(Func<string, string> get, List<string> errors, List<string> warnings)
        {
            Func<string, string> g = f => { string v = get(f); return string.IsNullOrEmpty(v) ? null : v; };
            CheckLineRead(g, errors);
            int n;
            if (g(FishCode) == null) warnings.Add(FishCode + " is not set");
            if (Int(g(Weight), out n) && n <= 0) warnings.Add(Weight + " is " + (g(Weight) == null ? "not set" : g(Weight)) + ": the fish is picked only when no fish that can bite there has a weight (then the server takes the first of them)");
        }

        /// <summary>The fish codes of box lines that are there more than once (the server keeps the last weight).</summary>
        public static List<int> Twice(IEnumerable<string> codes)
        {
            var seen = new HashSet<int>();
            var l = new List<int>();
            foreach (string c in codes)
            {
                int n;
                if (string.IsNullOrEmpty(c) || !Int(c, out n)) continue;
                if (!seen.Add(n) && !l.Contains(n)) l.Add(n);
            }
            return l;
        }
    }

    /// <summary>A row of one of the fishing tables.</summary>
    public sealed class FishRow
    {
        public readonly FishTable Table;
        internal TableRow Row;
        internal List<FishLine> Own;        // the lines of a box the mod adds
        internal FishRow(FishTable t, TableRow r) { Table = t; Row = r; }
        public bool IsAdded { get { return Row.IsAdded; } }
        public bool IsDeleted { get { return Row.IsDeleted; } }
    }

    /// <summary>A line of a fish box: a fish and its weight.</summary>
    public sealed class FishLine
    {
        public readonly FishRow Box;
        internal TableRow Row;                                  // in a shipped box
        internal List<KeyValuePair<string, string>> Attrs;      // in a box the mod adds
        internal FishLine(FishRow box) { Box = box; }
        public bool IsAdded { get { return Attrs != null || Row.IsAdded; } }
        public bool IsDeleted { get { return Row != null && Row.IsDeleted; } }
    }

    public sealed class FishingEditor
    {
        public readonly DataSet Data;
        public readonly Mod Mod;
        readonly Dictionary<FishTable, TableDraft> drafts = new Dictionary<FishTable, TableDraft>();
        readonly Dictionary<FishTable, List<FishRow>> rows = new Dictionary<FishTable, List<FishRow>>();
        // the lines of one shipped box at a time: its draft holds the table parsed
        FishRow open;
        List<FishLine> openLines;
        TableDraft openDraft;

        public FishingEditor(DataSet data, Mod mod)
        {
            Data = data;
            Mod = mod;
            if (!data.Has(Profile.Server) || !data.Exists(FishingRules.Archive))
                throw new ModKitException("the fishing tables are server tables: the data set has no " + FishingRules.Archive);
            M2dArchive arch = data.Open(FishingRules.Archive);
            foreach (FishTable t in FishingRules.Tables)
            {
                var l = new List<FishRow>();
                rows[t] = l;
                if (arch.Find(FishingRules.Entry(t)) == null) continue;
                var d = new TableDraft(data, mod, FishingRules.Archive, FishingRules.Entry(t), null);
                drafts[t] = d;
                foreach (TableRow r in d.Rows) if (r.Base != null && r.Base.Name == FishingRules.Row(t)) l.Add(Made(t, r));
            }
        }

        static FishRow Made(FishTable t, TableRow r)
        {
            var h = new FishRow(t, r);
            if (!r.IsAdded || !FishingRules.IsBox(t)) return h;
            h.Own = new List<FishLine>();
            foreach (XmlElem c in r.Base.Children)
            {
                if (c.Name != FishingRules.Line) continue;
                var line = new FishLine(h) { Attrs = new List<KeyValuePair<string, string>>() };
                foreach (XmlAttr a in c.Attrs) line.Attrs.Add(A(a.Name, a.Value));
                h.Own.Add(line);
            }
            return h;
        }

        /// <summary>Does the server data have the table?</summary>
        public bool Has(FishTable t) { return drafts.ContainsKey(t); }

        TableDraft Draft(FishTable t)
        {
            TableDraft d;
            if (!drafts.TryGetValue(t, out d)) throw new ModKitException("the server data has no " + FishingRules.Entry(t));
            return d;
        }

        // ---- reading ----

        public List<FishRow> Rows(FishTable t) { return new List<FishRow>(rows[t]); }

        public string Get(FishRow r, string field) { return Draft(r.Table).Get(r.Row, field) ?? ""; }
        public string IdText(FishRow r) { return Get(r, FishingRules.Key(r.Table)); }
        public bool IsEdited(FishRow r, string field) { return r.IsAdded || Draft(r.Table).IsEdited(r.Row, field); }

        /// <summary>The key as the server reads it; -1 when it cannot be read.</summary>
        public long Id(FishRow r)
        {
            int n;
            string v = IdText(r);
            return FishingRules.Int(v.Length == 0 ? null : v, out n) ? n : -1;
        }

        /// <summary>The first row of the table with the key that is not removed, or null.</summary>
        public FishRow Find(FishTable t, long id)
        {
            foreach (FishRow r in rows[t]) if (!r.IsDeleted && Id(r) == id) return r;
            return null;
        }

        /// <summary>The rows whose key holds the text; for a box also a fish code of its lines; all when empty.</summary>
        public List<FishRow> Find(FishTable t, string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return Rows(t);
            var l = new List<FishRow>();
            foreach (FishRow r in rows[t])
            {
                bool hit = IdText(r).IndexOf(text, StringComparison.Ordinal) >= 0;
                if (!hit && FishingRules.IsBox(t))
                    foreach (string c in Codes(r)) if (c.Trim() == text) { hit = true; break; }
                if (hit) l.Add(r);
            }
            return l;
        }

        public bool IsChanged(FishRow r)
        {
            if (r.Row.IsAdded || r.Row.IsDeleted || r.Row.IsChanged) return true;
            string sel = r.Row.Selector;
            if (sel == null || !FishingRules.IsBox(r.Table)) return false;
            foreach (ModOp op in Mod.Ops)
            {
                var ed = op as EditOp;
                if (ed == null || ed.Archive != FishingRules.Archive || !string.Equals(ed.Entry, FishingRules.Entry(r.Table), StringComparison.OrdinalIgnoreCase)) continue;
                foreach (RowOp o in ed.Rows)
                    foreach (string at in new[] { o.At, o.In, o.After, o.Before, o.Copy })
                        if (at != null && at.StartsWith(sel, StringComparison.Ordinal) && (at.Length == sel.Length || at[sel.Length] == '/')) return true;
            }
            return false;
        }

        /// <summary>How many rows of the table the mod changes, adds or removes.</summary>
        public int ChangedCount(FishTable t)
        {
            int n = 0;
            foreach (FishRow r in rows[t]) if (IsChanged(r)) n++;
            return n;
        }

        // ---- box lines ----

        /// <summary>The lines of a box (none for one the mod removes).</summary>
        public List<FishLine> Lines(FishRow box)
        {
            if (!FishingRules.IsBox(box.Table)) throw new ModKitException("only a fish box has lines");
            if (box.Own != null) return new List<FishLine>(box.Own);
            if (box.IsDeleted) return new List<FishLine>();
            if (open != box)
            {
                var d = new TableDraft(Draft(box.Table), box.Row.Selector);
                var l = new List<FishLine>();
                foreach (TableRow r in d.Rows) if (r.Base != null && r.Base.Name == FishingRules.Line) l.Add(new FishLine(box) { Row = r });
                closed.Remove(box);
                open = box; openDraft = d; openLines = l;
            }
            return new List<FishLine>(openLines);
        }

        TableDraft DraftOf(FishLine line)
        {
            if (open != line.Box || !openLines.Contains(line)) throw new ModKitException("the line is of a box that is no longer open; choose the box again");
            return openDraft;
        }

        public string Get(FishLine line, string field)
        {
            if (line.Attrs != null)
            {
                foreach (KeyValuePair<string, string> kv in line.Attrs) if (kv.Key == field) return kv.Value;
                return "";
            }
            return DraftOf(line).Get(line.Row, field) ?? "";
        }

        public bool IsEdited(FishLine line, string field) { return line.IsAdded || DraftOf(line).IsEdited(line.Row, field); }

        // the fish codes of a shipped box the mod changes, read while it is not the open one (its draft shares the
        // box table's parsed entry, so a box costs a walk of the mod's edits, not a parse)
        readonly Dictionary<FishRow, List<string>> closed = new Dictionary<FishRow, List<string>>();

        /// <summary>The fish codes of a box's lines as the mod leaves them.</summary>
        public List<string> Codes(FishRow box)
        {
            var l = new List<string>();
            if (box.IsDeleted) return l;
            if (box.Own != null)
            {
                foreach (FishLine line in box.Own) l.Add(Get(line, FishingRules.FishCode));
                return l;
            }
            if (box == open)
            {
                foreach (FishLine line in openLines) if (!line.IsDeleted) l.Add(Get(line, FishingRules.FishCode));
                return l;
            }
            if (IsChanged(box))
            {
                List<string> c;
                if (!closed.TryGetValue(box, out c))
                {
                    c = new List<string>();
                    var d = new TableDraft(Draft(box.Table), box.Row.Selector);
                    foreach (TableRow r in d.Rows)
                        if (r.Base != null && r.Base.Name == FishingRules.Line && !r.IsDeleted) c.Add(d.Get(r, FishingRules.FishCode) ?? "");
                    closed[box] = c;
                }
                l.AddRange(c);
                return l;
            }
            foreach (XmlElem c in box.Row.Base.Children) if (c.Name == FishingRules.Line) l.Add(c.Attr(FishingRules.FishCode) ?? "");
            return l;
        }

        /// <summary>The boxes (of both tables) with a line for the fish, as the mod leaves them.</summary>
        public List<FishRow> BoxesWith(long fish)
        {
            var l = new List<FishRow>();
            foreach (FishTable t in new[] { FishTable.GlobalBox, FishTable.IndividualBox })
                foreach (FishRow b in rows[t])
                {
                    if (b.IsDeleted) continue;
                    foreach (string c in Codes(b))
                    {
                        int n;
                        if (c.Length > 0 && FishingRules.Int(c, out n) && n == fish) { l.Add(b); break; }
                    }
                }
            return l;
        }

        /// <summary>The spots that name the box, as the mod leaves them.</summary>
        public List<FishRow> SpotsUsing(FishRow box)
        {
            var l = new List<FishRow>();
            long id = Id(box);
            string field = box.Table == FishTable.GlobalBox ? FishingRules.GlobalBox : FishingRules.IndividualBox;
            foreach (FishRow s in rows[FishTable.Spot])
            {
                int n;
                string v = Get(s, field);
                if (!s.IsDeleted && v.Length > 0 && FishingRules.Int(v, out n) && n == id) l.Add(s);
            }
            return l;
        }

        // ---- checks ----

        Func<string, string> Getter(FishRow r) { return f => { string v = Get(r, f); return v.Length == 0 ? null : v; }; }

        /// <summary>What is wrong with a row: by itself, and what it names that the tables do not have. Errors first, each with "error: " or "warning: ".</summary>
        public List<string> Check(FishRow r)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            if (r.IsDeleted) return errors;
            FishingRules.Check(r.Table, Getter(r), errors, warnings);
            long id = Id(r);
            int same = 0;
            foreach (FishRow o in rows[r.Table]) if (!o.IsDeleted && id >= 0 && Id(o) == id) same++;
            if (same > 1) warnings.Add("the table has " + same + " rows with " + FishingRules.Key(r.Table) + " " + id + ": the server keeps one, by their feature and locale");
            if (r.Table == FishTable.Spot)
            {
                foreach (FishTable t in new[] { FishTable.GlobalBox, FishTable.IndividualBox })
                {
                    string field = t == FishTable.GlobalBox ? FishingRules.GlobalBox : FishingRules.IndividualBox;
                    int n;
                    string v = Get(r, field);
                    if (v.Length > 0 && FishingRules.Int(v, out n) && n > 0 && Has(t) && Find(t, n) == null) warnings.Add("there is no " + FishingRules.Noun(t) + " " + n + " in " + FishingRules.Entry(t));
                }
            }
            else if (r.Table == FishTable.Fish && id > 0 && BoxesWith(id).Count == 0)
                warnings.Add("no fish box has a line for the fish: it is not caught");
            else if (FishingRules.IsBox(r.Table))
            {
                int live = 0;
                foreach (string c in Codes(r)) live++;
                if (live == 0) warnings.Add("the box has no fish");
                foreach (int c in FishingRules.Twice(Codes(r))) warnings.Add("fish " + c + " has more than one line: the server keeps the last weight");
                if (SpotsUsing(r).Count == 0) warnings.Add("no fishing spot names the box");
            }
            var l = new List<string>();
            foreach (string e in errors) l.Add("error: " + e);
            foreach (string w in warnings) l.Add("warning: " + w);
            return l;
        }

        /// <summary>What is wrong with a line of a box, as Check(row) says it.</summary>
        public List<string> Check(FishLine line)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            if (line.IsDeleted) return errors;
            FishingRules.CheckLine(f => { string v = Get(line, f); return v.Length == 0 ? null : v; }, errors, warnings);
            int n;
            string code = Get(line, FishingRules.FishCode);
            if (code.Length > 0 && FishingRules.Int(code, out n) && Has(FishTable.Fish) && Find(FishTable.Fish, n) == null)
                warnings.Add("there is no fish " + n + " in " + FishingRules.FishEntry + ": the server leaves the line out");
            var l = new List<string>();
            foreach (string e in errors) l.Add("error: " + e);
            foreach (string w in warnings) l.Add("warning: " + w);
            return l;
        }

        // ---- editing ----

        static KeyValuePair<string, string> A(string name, string value) { return new KeyValuePair<string, string>(name, value); }

        static string Xml(string name, IEnumerable<KeyValuePair<string, string>> attrs)
        {
            var sb = new StringBuilder("<" + name);
            foreach (KeyValuePair<string, string> kv in attrs) sb.Append(' ').Append(kv.Key).Append("=\"").Append(XmlDoc.EscapeAttr(kv.Value, '"')).Append('"');
            return sb.Append(" />").ToString();
        }

        // writes a box the mod adds as one <add> with its lines inside
        void Rewrite(FishRow box)
        {
            TableDraft d = Draft(box.Table);
            RowOp add = box.Row.Add;
            var self = new List<KeyValuePair<string, string>>();
            var names = new List<string>();
            foreach (XmlAttr a in box.Row.Base.Attrs) names.Add(a.Name);
            foreach (AttrOp a in add.Attrs)
            {
                if (a.At != null) throw new ModKitException("the <add> of " + box.Row.Label + " has lines for nested rows; change it in the mod's text");
                if (!names.Contains(a.Name)) names.Add(a.Name);
            }
            foreach (string n in names)
            {
                string v = d.Get(box.Row, n);
                if (v != null) self.Add(A(n, v));
            }
            string text = Xml(FishingRules.Row(box.Table), self);
            if (box.Own.Count > 0)
            {
                var sb = new StringBuilder(text.Substring(0, text.Length - 3) + ">\n");
                foreach (FishLine line in box.Own) sb.Append('\t').Append(Xml(FishingRules.Line, line.Attrs)).Append('\n');
                text = sb.Append("</").Append(FishingRules.Row(box.Table)).Append('>').ToString();
            }
            add.Xml = text;
            add.Copy = null;
            add.Attrs.Clear();
            box.Row.Base = XmlDoc.ParseFragment(text, "the new row").Root;
        }

        /// <summary>Sets a field of a row ("" takes the attribute out). The value is written as it is given.</summary>
        public void Set(FishRow r, string field, string value)
        {
            if (!TableDraft.IsAttrName(field)) throw new ModKitException("\"" + field + "\" is not an attribute name");
            if (r.IsDeleted) throw new ModKitException("it is removed; restore it first");
            value = value ?? "";
            Draft(r.Table).Set(r.Row, field, value.Length == 0 ? null : value);
            if (r.Own != null) Rewrite(r);
        }

        /// <summary>Sets a field of a box line ("" takes the attribute out).</summary>
        public void Set(FishLine line, string field, string value)
        {
            if (!TableDraft.IsAttrName(field)) throw new ModKitException("\"" + field + "\" is not an attribute name");
            if (line.IsDeleted) throw new ModKitException("the line is removed; restore it first");
            value = value ?? "";
            if (line.Attrs == null) { DraftOf(line).Set(line.Row, field, value.Length == 0 ? null : value); return; }
            int at = line.Attrs.FindIndex(kv => kv.Key == field);
            if (value.Length == 0) { if (at >= 0) line.Attrs.RemoveAt(at); }
            else if (at >= 0) line.Attrs[at] = A(field, value);
            else line.Attrs.Add(A(field, value));
            Rewrite(line.Box);
        }

        FishLine AddLine(FishRow box, List<KeyValuePair<string, string>> attrs)
        {
            if (box.IsDeleted) throw new ModKitException("the box is removed; restore it first");
            var line = new FishLine(box);
            if (box.Own != null)
            {
                line.Attrs = attrs;
                box.Own.Add(line);
                Rewrite(box);
                return line;
            }
            Lines(box);     // opens it
            line.Row = openDraft.AddRow(Xml(FishingRules.Line, attrs));
            openLines.Add(line);
            return line;
        }

        /// <summary>A new line of a box: the fish at the weight.</summary>
        public FishLine AddFish(FishRow box, long fish, long weight)
        {
            if (!FishingRules.IsBox(box.Table)) throw new ModKitException("a fish goes into a fish box");
            if (fish <= 0) throw new ModKitException("give a fish id");
            return AddLine(box, new List<KeyValuePair<string, string>> { A(FishingRules.FishCode, fish.ToString(CultureInfo.InvariantCulture)), A(FishingRules.Weight, weight.ToString(CultureInfo.InvariantCulture)) });
        }

        /// <summary>Removes a line: a new one goes away, a shipped one is deleted and can be restored.</summary>
        public void Remove(FishLine line)
        {
            if (line.Attrs != null)
            {
                line.Box.Own.Remove(line);
                Rewrite(line.Box);
                return;
            }
            bool mine = line.Row.IsAdded;
            DraftOf(line).Delete(line.Row);
            if (mine) openLines.Remove(line);
        }

        /// <summary>Takes back the removal of a shipped line.</summary>
        public void Restore(FishLine line) { if (line.Attrs == null) DraftOf(line).Restore(line.Row); }

        /// <summary>Drops what the mod changes in a shipped line.</summary>
        public void Revert(FishLine line)
        {
            if (line.IsAdded) throw new ModKitException("the line is new; remove it instead");
            DraftOf(line).Restore(line.Row);
            DraftOf(line).RevertCells(line.Row);
        }

        /// <summary>The lowest key above every key the table has.</summary>
        public long FreeId(FishTable t)
        {
            long max = 0;
            foreach (FishRow r in rows[t]) max = Math.Max(max, Id(r));
            return max + 1;
        }

        // the fields a new row starts with: values the server reads and fishes with
        static List<KeyValuePair<string, string>> Defaults(FishTable t)
        {
            var l = new List<KeyValuePair<string, string>>();
            string s;
            switch (t)
            {
                case FishTable.Fish: s = "fishingBook 1 habitat water fishMastery 0 lv 1 rank 1 pointCount 1 masteryPoint 1 exp 0 fishingTime 15000 catchProp 10000 baitProp 5000 smallSize 20-30 bigSize 40-50"; break;
                case FishTable.Spot: s = "liquidType water minMastery 0 maxMastery 79 spotLevel 1 spotDropRank 1"; break;
                case FishTable.Lure: s = "additionalEffectLevel 1 catchRank 1 catchProp 1000"; break;
                default: s = "probability 10000 cubeRate 10000"; break;
            }
            string[] p = s.Split(' ');
            for (int i = 0; i + 1 < p.Length; i += 2) l.Add(A(p[i], p[i + 1]));
            return l;
        }

        /// <summary>A new row with the key and the fields a working row needs (a box has no line yet).</summary>
        public FishRow Add(FishTable t, long id) { return AddRow(t, id, null); }

        /// <summary>A new row with the fields (and for a box the lines) of another, as the mod leaves them, under a new key.</summary>
        public FishRow Copy(FishRow from, long id) { return AddRow(from.Table, id, from); }

        FishRow AddRow(FishTable t, long id, FishRow from)
        {
            TableDraft d = Draft(t);
            string key = FishingRules.Key(t);
            if (id <= 0 || id > int.MaxValue) throw new ModKitException("give the new " + FishingRules.Noun(t) + " a " + key + " from 1 to " + int.MaxValue);
            if (t != FishTable.Lure && Find(t, id) != null) throw new ModKitException("there is a " + FishingRules.Noun(t) + " " + id + " already");
            if (from != null && from.IsDeleted) throw new ModKitException("it is removed; restore it before copying");
            var attrs = new List<KeyValuePair<string, string>> { A(key, id.ToString(CultureInfo.InvariantCulture)) };
            if (from == null) attrs.AddRange(Defaults(t));
            else
            {
                var names = new List<string>();
                foreach (XmlAttr a in from.Row.Base.Attrs) names.Add(a.Name);
                foreach (string c in d.Columns) if (!names.Contains(c)) names.Add(c);
                foreach (string n in names)
                {
                    string v = d.Get(from.Row, n);
                    if (n != key && v != null) attrs.Add(A(n, v));
                }
            }
            var r = new FishRow(t, d.AddRow(Xml(FishingRules.Row(t), attrs)));
            if (FishingRules.IsBox(t))
            {
                r.Own = new List<FishLine>();
                if (from != null)
                    foreach (FishLine s in Lines(from))
                    {
                        if (s.IsDeleted) continue;
                        var line = new FishLine(r) { Attrs = new List<KeyValuePair<string, string>>() };
                        if (s.Attrs != null) line.Attrs.AddRange(s.Attrs);
                        else
                        {
                            TableDraft sd = DraftOf(s);
                            var names = new List<string>();
                            foreach (XmlAttr a in s.Row.Base.Attrs) names.Add(a.Name);
                            foreach (string c in sd.Columns) if (!names.Contains(c)) names.Add(c);
                            foreach (string n in names) if (sd.Get(s.Row, n) != null) line.Attrs.Add(A(n, sd.Get(s.Row, n)));
                        }
                        r.Own.Add(line);
                    }
                if (r.Own.Count > 0) Rewrite(r);
            }
            rows[t].Add(r);
            return r;
        }

        /// <summary>Removes a row: a new one goes away, a shipped one is deleted (with what the mod changed in it) and can be restored.</summary>
        public void Remove(FishRow r)
        {
            TableDraft d = Draft(r.Table);
            if (r.IsAdded)
            {
                d.Delete(r.Row);
                rows[r.Table].Remove(r);
                if (open == r) { open = null; openDraft = null; openLines = null; }
                return;
            }
            if (r.IsDeleted) return;
            if (FishingRules.IsBox(r.Table))
            {
                // the lines the mod has for the box go first: a line for a deleted element does not apply
                foreach (FishLine line in Lines(r))
                {
                    if (line.Row.IsAdded) openDraft.Delete(line.Row);
                    else { openDraft.Restore(line.Row); openDraft.RevertCells(line.Row); }
                }
                open = null; openDraft = null; openLines = null;
                closed.Remove(r);
            }
            d.RevertCells(r.Row);
            d.Delete(r.Row);
        }

        /// <summary>Takes back the removal of a shipped row.</summary>
        public void Restore(FishRow r) { Draft(r.Table).Restore(r.Row); }

        /// <summary>Drops what the mod changes in the fields of a shipped row (its lines stay).</summary>
        public void Revert(FishRow r)
        {
            if (r.IsAdded) throw new ModKitException("the row is new; remove it instead");
            Draft(r.Table).Restore(r.Row);
            Draft(r.Table).RevertCells(r.Row);
        }
    }
}
