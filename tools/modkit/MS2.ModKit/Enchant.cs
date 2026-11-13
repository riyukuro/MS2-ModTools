// Enchant bonuses (table/Server/enchantOptionTable.xml in the server's Server.m2d): the stats an item gains at each
// enchant level. The table is the server's only: the client has no copy and shows what the server sends. Success
// rates, fodder and costs are fixed in the server's code; this table is what a mod can change about enchanting.
//
// A row (<option>):
//   id        the row's key (shipped ids have 7 digits: slot, grade, rank, level); nothing else looks it up
//   slot      the item type: the second two digits of the item id (id / 100000 % 100), e.g. 30 bludgeon, 14 clothes
//   grade     the enchant level the row is for, 1 to 15
//   rank      the item's rarity, 1 (normal) to 6
//   minLv, maxLv  the item levels the row is for
//   rate      what the level adds, as a share of each listed stat: 0.016 is 1.6 %. The client is sent it in steps
//             of 0.001
//   option    the stats, by BasicAttribute code, comma separated ("27, 28" weapon attack, "20" defense)
// The server picks for a level the first row in file order with the item's slot, rank and that grade whose level
// range holds the item's level; an item at +N has the rates of grades 1 to N added up. A level no row matches adds
// nothing. The ingest reads the ids, slot, grade, rank and levels as ints and the rate as a float: an empty value or
// text stops it (a missing attribute is 0); a stat part that is not a whole number stops it too, and a duplicate id.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>The checks of one enchant row, shared by the editor (values as shown) and the plan (final rows).</summary>
    public static class EnchantRules
    {
        public const string Archive = Planner.ServerTables;
        public const string Entry = "table/Server/enchantOptionTable.xml", Rows = "option", Key = "id";
        public const string Space = "enchant";
        /// <summary>The ids of new rows: no shipped row uses this band (shipped ids have 7 digits).</summary>
        public const long From = 99200000, To = 99299999;
        public const int MaxGrade = 15, MaxRank = 6;
        public const string Slot = "slot", Grade = "grade", Rank = "rank", Rate = "rate", MinLv = "minLv", MaxLv = "maxLv", Stats = "option";

        /// <summary>Fields the server reads as an int.</summary>
        public static readonly string[] Ints = { Key, Slot, Grade, Rank, MinLv, MaxLv };
        /// <summary>The fields in the order the shipped rows have them.</summary>
        public static readonly string[] Fields = { Key, Slot, Grade, Rank, Rate, MinLv, MaxLv, Stats };

        /// <summary>What a field does, for the view.</summary>
        public static string Means(string field)
        {
            switch (field)
            {
                case Key: return "the row's key; nothing else looks it up";
                case Slot: return "the item type (the 2nd and 3rd digits of the item id)";
                case Grade: return "the enchant level the row is for (1 to 15)";
                case Rank: return "the item's rarity (1 normal to 6)";
                case Rate: return "what this level adds, a share of each stat: 0.016 is 1.6 % (steps of 0.001)";
                case MinLv: return "the lowest item level the row is for";
                case MaxLv: return "the highest item level the row is for";
                case Stats: return "the stats it raises, by code, comma separated (27, 28 weapon attack; 20 defense)";
                default: return "";
            }
        }

        static readonly Dictionary<int, string> Slots = new Dictionary<int, string>
        {
            { 12, "earring" }, { 13, "hat" }, { 14, "clothes" }, { 15, "pants" }, { 16, "gloves" }, { 17, "shoes" },
            { 18, "cape" }, { 19, "necklace" }, { 20, "ring" }, { 21, "belt" }, { 22, "overall" },
            { 30, "bludgeon" }, { 31, "dagger" }, { 32, "longsword" }, { 33, "scepter" }, { 34, "throwing star" },
            { 40, "spellbook" }, { 41, "shield" },
            { 50, "greatsword" }, { 51, "bow" }, { 52, "staff" }, { 53, "cannon" }, { 54, "blade" }, { 55, "knuckle" }, { 56, "orb" },
        };

        /// <summary>The item type of a slot ("bludgeon"); null for a number that is no item type of gear.</summary>
        public static string SlotName(long slot)
        {
            string n;
            return slot >= int.MinValue && slot <= int.MaxValue && Slots.TryGetValue((int)slot, out n) ? n : null;
        }

        /// <summary>The slots of gear, lowest first.</summary>
        public static List<int> AllSlots() { var l = new List<int>(Slots.Keys); l.Sort(); return l; }

        static readonly string[] StatNames =
        {
            "strength", "dexterity", "intelligence", "luck", "health", "hp regen", "hp regen interval", "spirit",
            "spirit regen", "spirit regen interval", "stamina", "stamina regen", "stamina regen interval", "attack speed",
            "movement speed", "accuracy", "evasion", "critical rate", "critical damage", "critical evasion", "defense",
            "perfect guard", "jump height", "physical attack", "magic attack", "physical resistance", "magic resistance",
            "min weapon attack", "max weapon attack", "damage", "unknown", "piercing", "mount speed", "bonus attack", "pet bonus attack",
        };

        /// <summary>The highest stat code the server names.</summary>
        public static int MaxStat { get { return StatNames.Length - 1; } }

        /// <summary>The name of a stat code ("min weapon attack"); null for a code the server has no name for.</summary>
        public static string StatName(long code) { return code >= 0 && code < StatNames.Length ? StatNames[code] : null; }

        const NumberStyles IntStyle = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowLeadingSign;
        const NumberStyles FloatStyle = IntStyle | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

        /// <summary>The int the server reads (a missing attribute is 0); false for a value it cannot read, an empty one too.</summary>
        public static bool ServerInt(string v, out long n)
        {
            n = 0;
            if (v == null) return true;
            int i;
            if (!int.TryParse(v, IntStyle, CultureInfo.InvariantCulture, out i)) return false;
            n = i;
            return true;
        }

        /// <summary>The rate the server reads (a missing attribute is 0); false for a value it cannot read.</summary>
        public static bool ServerRate(string v, out double r)
        {
            r = 0;
            if (v == null) return true;
            string t = v.Trim();
            if (t == "INF") { r = double.PositiveInfinity; return true; }
            if (t == "-INF") { r = double.NegativeInfinity; return true; }
            float f;
            if (!float.TryParse(t, FloatStyle, CultureInfo.InvariantCulture, out f)) return false;
            r = f;
            return true;
        }

        /// <summary>The stat codes of an option value (none for a missing or empty one); false with the part the
        /// server cannot read.</summary>
        public static bool ServerStats(string v, out List<long> codes, out string bad)
        {
            codes = new List<long>();
            bad = null;
            if (v == null) return true;
            foreach (string p in v.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int n;
                if (!int.TryParse(p.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) { bad = p; return false; }
                codes.Add(n);
            }
            return true;
        }

        /// <summary>A rate as the table writes it ("0.016").</summary>
        public static string FormatRate(double r) { return r.ToString("0.###", CultureInfo.InvariantCulture); }

        /// <summary>A rate as a share ("1.6 %").</summary>
        public static string Percent(double r) { return (r * 100).ToString("0.#", CultureInfo.InvariantCulture) + " %"; }

        /// <summary>The stats of an option value by name ("min weapon attack, max weapon attack").</summary>
        public static string StatsText(string v)
        {
            List<long> codes;
            string bad;
            if (!ServerStats(v, out codes, out bad)) return "(cannot be read)";
            var l = new List<string>();
            foreach (long c in codes) l.Add(StatName(c) ?? "code " + c);
            return l.Count == 0 ? "(none)" : string.Join(", ", l.ToArray());
        }

        /// <summary>
        /// The problems of a row: errors (what stops the server's ingest) and warnings (what makes a row add nothing or
        /// something else than it looks). get gives the row's values.
        /// </summary>
        public static void Check(Func<string, string> get, List<string> errors, List<string> warnings)
        {
            var n = new Dictionary<string, long>();
            foreach (string f in Ints)
            {
                string v = get(f);
                long x;
                if (!ServerInt(v, out x)) errors.Add(f + " \"" + v + "\" is not a whole number the server reads (an int): its ingest stops on it");
                else n[f] = x;
            }
            string rv = get(Rate);
            double rate;
            bool hasRate = ServerRate(rv, out rate);
            if (!hasRate) errors.Add(Rate + " \"" + rv + "\" is not a number the server reads: its ingest stops on it");
            List<long> codes;
            string bad;
            bool hasStats = ServerStats(get(Stats), out codes, out bad);
            if (!hasStats) errors.Add(Stats + " has the part \"" + bad + "\", which is not a whole number: the server's ingest stops on it");

            long v2;
            if (n.TryGetValue(Key, out v2) && v2 <= 0) warnings.Add(Key + " is " + v2 + ": keep ids above 0");
            if (n.TryGetValue(Grade, out v2) && (v2 < 1 || v2 > MaxGrade)) warnings.Add(Grade + " is " + v2 + ": enchant levels are 1 to " + MaxGrade + ", the server never looks it up");
            if (n.TryGetValue(Rank, out v2) && (v2 < 1 || v2 > MaxRank)) warnings.Add(Rank + " is " + v2 + ": items have rarity 1 to " + MaxRank + ", no item uses the row");
            if (n.TryGetValue(Slot, out v2) && SlotName(v2) == null) warnings.Add(Slot + " " + v2 + " is no item type of gear: no item uses the row");
            long lo, hi;
            if (n.TryGetValue(MinLv, out lo) && n.TryGetValue(MaxLv, out hi) && lo > hi) warnings.Add(MinLv + " " + lo + " is above " + MaxLv + " " + hi + ": no item level matches the row");
            if (hasRate)
            {
                if (double.IsNaN(rate) || double.IsInfinity(rate)) warnings.Add(Rate + " \"" + rv + "\" is no finite number");
                else if (rate == 0) warnings.Add(Rate + " is 0: the level adds nothing");
                else if (rate < 0) warnings.Add(Rate + " is " + FormatRate(rate) + ": the level takes " + Percent(-rate) + " away");
                else if (Math.Abs(rate * 1000 - Math.Round(rate * 1000)) > 1e-4)
                    warnings.Add(Rate + " " + rv.Trim() + " is finer than 0.001: the client is sent it in steps of 0.001");
            }
            if (hasStats)
            {
                if (codes.Count == 0) warnings.Add(Stats + " names no stat: the level adds nothing");
                var seen = new HashSet<long>();
                foreach (long c in codes)
                {
                    if (c < 0 || c > 255) warnings.Add("stat code " + c + " is not one the server reads (0 to 255): it is left out");
                    else if (StatName(c) == null) warnings.Add("stat code " + c + " is no stat of the server: it raises nothing a player sees");
                    if (!seen.Add(c)) warnings.Add("stat code " + c + " is named twice");
                }
            }
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

        /// <summary>The value of an int field (0 when it cannot be read).</summary>
        public static long IntOf(string v) { long n; return ServerInt(v, out n) ? n : 0; }

        /// <summary>The rate (0 when it cannot be read or is not finite).</summary>
        public static double RateOf(string v)
        {
            double r;
            return ServerRate(v, out r) && !double.IsNaN(r) && !double.IsInfinity(r) ? r : 0;
        }

        /// <summary>The parts of [lo, hi] that the ranges cover, as merged [from, to] pairs.</summary>
        public static List<long[]> Covered(IEnumerable<long[]> ranges, long lo, long hi)
        {
            var l = new List<long[]>();
            foreach (long[] r in ranges)
            {
                long a = Math.Max(lo, r[0]), b = Math.Min(hi, r[1]);
                if (a <= b) l.Add(new[] { a, b });
            }
            l.Sort((x, y) => x[0].CompareTo(y[0]));
            var m = new List<long[]>();
            foreach (long[] r in l)
            {
                if (m.Count > 0 && r[0] <= m[m.Count - 1][1] + 1) m[m.Count - 1][1] = Math.Max(m[m.Count - 1][1], r[1]);
                else m.Add(new[] { r[0], r[1] });
            }
            return m;
        }

        /// <summary>The parts of [lo, hi] the ranges leave out.</summary>
        public static List<long[]> Uncovered(IEnumerable<long[]> ranges, long lo, long hi)
        {
            var l = new List<long[]>();
            long at = lo;
            foreach (long[] c in Covered(ranges, lo, hi))
            {
                if (c[0] > at) l.Add(new[] { at, c[0] - 1 });
                at = c[1] + 1;
            }
            if (at <= hi) l.Add(new[] { at, hi });
            return l;
        }

        public static string Spans(List<long[]> spans)
        {
            var l = new List<string>();
            foreach (long[] s in spans) l.Add(s[0] == s[1] ? s[0].ToString(CultureInfo.InvariantCulture) : s[0] + "-" + s[1]);
            return string.Join(", ", l.ToArray());
        }

        public static string Grades(IEnumerable<long> grades)
        {
            var l = new List<string>();
            foreach (long g in grades) l.Add("+" + g);
            return string.Join(", ", l.ToArray());
        }

        /// <summary>"bludgeon (30) rank 4, levels 60-70".</summary>
        public static string LadderLabel(long slot, long rank, long minLv, long maxLv)
        {
            return (SlotName(slot) ?? "slot") + " (" + slot + ") rank " + rank + ", level " + (minLv == maxLv ? minLv.ToString(CultureInfo.InvariantCulture) : minLv + "-" + maxLv);
        }

        /// <summary>
        /// Rows the server never reaches for some item levels: for each row in file order (rows), the levels an
        /// earlier row with the same slot, grade and rank covers. Gives (row index, earlier row index, levels lost).
        /// </summary>
        public static List<Shadow> Shadows(IList<Func<string, string>> rows)
        {
            var l = new List<Shadow>();
            var byKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < rows.Count; i++)
            {
                Func<string, string> g = rows[i];
                string k = IntOf(g(Slot)) + "\n" + IntOf(g(Grade)) + "\n" + IntOf(g(Rank));
                long lo = IntOf(g(MinLv)), hi = IntOf(g(MaxLv));
                List<int> before;
                if (!byKey.TryGetValue(k, out before)) byKey[k] = before = new List<int>();
                if (lo <= hi)
                {
                    var ranges = new List<long[]>();
                    int first = -1;
                    foreach (int j in before)
                    {
                        long a = IntOf(rows[j](MinLv)), b = IntOf(rows[j](MaxLv));
                        if (a > b || b < lo || a > hi) continue;
                        if (first < 0) first = j;
                        ranges.Add(new[] { a, b });
                    }
                    if (first >= 0)
                    {
                        List<long[]> lost = Covered(ranges, lo, hi);
                        l.Add(new Shadow { Row = i, By = first, Levels = lost, All = lost.Count == 1 && lost[0][0] == lo && lost[0][1] == hi });
                    }
                }
                before.Add(i);
            }
            return l;
        }
    }

    /// <summary>A row the server does not reach for some item levels: an earlier row with its slot, grade and rank holds them.</summary>
    public sealed class Shadow
    {
        public int Row, By;
        public List<long[]> Levels;
        /// <summary>True when the row is never used.</summary>
        public bool All;
    }

    /// <summary>The rows of one slot, rank and level range: the bonus of each enchant level of those items.</summary>
    public sealed class EnchantLadder
    {
        public long Slot, Rank, MinLv, MaxLv;
        public readonly List<TableRow> Rows = new List<TableRow>();
        public string Label { get { return EnchantRules.LadderLabel(Slot, Rank, MinLv, MaxLv); } }
    }

    /// <summary>
    /// The enchant bonus editor of one mod: the server's enchantOptionTable.xml through a TableDraft, its rows grouped
    /// into ladders (one per slot, rank and level range) as the data set ships them.
    /// </summary>
    public sealed class EnchantEditor
    {
        public readonly DataSet Data;
        public readonly Mod Mod;
        public readonly TableDraft Draft;
        public readonly List<EnchantLadder> Ladders = new List<EnchantLadder>();

        public EnchantEditor(DataSet data, Mod mod)
        {
            Data = data;
            Mod = mod;
            if (!data.Has(Profile.Server)) throw new ModKitException("enchant bonuses are server data, and the data set has no server");
            Draft = new TableDraft(data, mod, EnchantRules.Archive, EnchantRules.Entry, null);
            var byKey = new Dictionary<string, EnchantLadder>(StringComparer.Ordinal);
            foreach (TableRow r in Draft.Rows)
            {
                if (r.Base.Name != EnchantRules.Rows) continue;
                long s = Int(r, EnchantRules.Slot), k = Int(r, EnchantRules.Rank), lo = Int(r, EnchantRules.MinLv), hi = Int(r, EnchantRules.MaxLv);
                string key = s + "\n" + k + "\n" + lo + "\n" + hi;
                EnchantLadder l;
                if (!byKey.TryGetValue(key, out l))
                {
                    byKey[key] = l = new EnchantLadder { Slot = s, Rank = k, MinLv = lo, MaxLv = hi };
                    Ladders.Add(l);
                }
                l.Rows.Add(r);
            }
            foreach (EnchantLadder l in Ladders) SortRows(l);
        }

        void SortRows(EnchantLadder l)
        {
            var at = new Dictionary<TableRow, int>();
            for (int i = 0; i < l.Rows.Count; i++) at[l.Rows[i]] = i;
            l.Rows.Sort((a, b) => { int c = Int(a, EnchantRules.Grade).CompareTo(Int(b, EnchantRules.Grade)); return c != 0 ? c : at[a].CompareTo(at[b]); });
        }

        // ---- reading ----

        public string Get(TableRow r, string field) { return Draft.Get(r, field); }
        public string Shipped(TableRow r, string field) { return r.IsAdded ? null : Draft.GetOriginal(r, field); }
        public long Int(TableRow r, string field) { return EnchantRules.IntOf(Draft.Get(r, field)); }
        public double RateOf(TableRow r) { return EnchantRules.RateOf(Draft.Get(r, EnchantRules.Rate)); }
        public long Id(TableRow r) { return Int(r, EnchantRules.Key); }

        /// <summary>The row the server uses for a grade of the ladder (the first one that is not removed); null: none.</summary>
        public TableRow Row(EnchantLadder l, long grade)
        {
            foreach (TableRow r in l.Rows) if (!r.IsDeleted && Int(r, EnchantRules.Grade) == grade) return r;
            return null;
        }

        /// <summary>The bonus of an item of the ladder at +grade: the rates of grades 1 to grade added up.</summary>
        public double Total(EnchantLadder l, long grade)
        {
            double t = 0;
            for (long g = 1; g <= grade; g++) { TableRow r = Row(l, g); if (r != null) t += RateOf(r); }
            return t;
        }

        /// <summary>The grades 1 to 15 the ladder has no row for.</summary>
        public List<long> Missing(EnchantLadder l)
        {
            var m = new List<long>();
            for (long g = 1; g <= EnchantRules.MaxGrade; g++) if (Row(l, g) == null) m.Add(g);
            return m;
        }

        public bool IsChanged(EnchantLadder l) { foreach (TableRow r in l.Rows) if (r.IsAdded || r.IsChanged || r.IsDeleted) return true; return false; }
        public bool IsAdded(EnchantLadder l) { foreach (TableRow r in l.Rows) if (!r.IsAdded) return false; return l.Rows.Count > 0; }

        /// <summary>Ladders by slot (0: any), rank (0: any) and an item level they cover (0: any).</summary>
        public List<EnchantLadder> Find(long slot, long rank, long level)
        {
            var l = new List<EnchantLadder>();
            foreach (EnchantLadder d in Ladders)
            {
                if (slot != 0 && d.Slot != slot) continue;
                if (rank != 0 && d.Rank != rank) continue;
                if (level != 0 && (level < d.MinLv || level > d.MaxLv)) continue;
                l.Add(d);
            }
            return l;
        }

        // the place of each row in the file the plan writes: an added row beside its anchor, else at the end
        List<TableRow> FileOrder()
        {
            var at = new Dictionary<string, int>(StringComparer.Ordinal);
            var keyed = new List<KeyValuePair<double, TableRow>>();
            int n = 0;
            foreach (TableRow r in Draft.Rows)
                if (!r.IsAdded) { at[r.Selector] = n; keyed.Add(new KeyValuePair<double, TableRow>(n, r)); n++; }
            int k = 0;
            foreach (TableRow r in Draft.Rows)
            {
                if (!r.IsAdded) continue;
                k++;
                int i;
                double p = n + k;
                if (r.Add.After != null && at.TryGetValue(r.Add.After, out i)) p = i + 0.5 + k * 1e-6;
                else if (r.Add.Before != null && at.TryGetValue(r.Add.Before, out i)) p = i - 0.5 + k * 1e-6;
                keyed.Add(new KeyValuePair<double, TableRow>(p, r));
            }
            var order = new List<KeyValuePair<double, TableRow>>(keyed);
            // a stable sort by place
            var idx = new Dictionary<TableRow, int>();
            for (int i = 0; i < order.Count; i++) idx[order[i].Value] = i;
            order.Sort((a, b) => { int c = a.Key.CompareTo(b.Key); return c != 0 ? c : idx[a.Value].CompareTo(idx[b.Value]); });
            var l = new List<TableRow>();
            foreach (KeyValuePair<double, TableRow> kv in order) if (!kv.Value.IsDeleted && kv.Value.Base.Name == EnchantRules.Rows) l.Add(kv.Value);
            return l;
        }

        /// <summary>The problems of a ladder as shown (the plan checks the final rows the same way, and more).</summary>
        public List<string> Check(EnchantLadder l)
        {
            var msgs = new List<string>();
            var mine = new HashSet<TableRow>(l.Rows);
            foreach (TableRow r in l.Rows)
            {
                if (r.IsDeleted || !(r.IsAdded || r.IsChanged)) continue;
                TableRow row = r;
                foreach (string t in EnchantRules.Check(f => Draft.Get(row, f))) msgs.Add("+" + Int(r, EnchantRules.Grade) + " (id " + Get(r, EnchantRules.Key) + "): " + t);
                if (r.IsAdded)
                {
                    long id = Id(r);
                    if (id < EnchantRules.From || id > EnchantRules.To)
                        msgs.Add("new id " + id + " is outside of " + EnchantRules.From + "-" + EnchantRules.To + ", the range of new enchant rows");
                }
            }
            if (!IsChanged(l)) return msgs;
            List<long> missing = Missing(l);
            if (missing.Count > 0 && missing.Count < EnchantRules.MaxGrade) msgs.Add("no row for " + EnchantRules.Grades(missing) + ": those levels add nothing");
            var ids = new Dictionary<long, int>();
            List<TableRow> order = FileOrder();
            foreach (TableRow r in order) { long id = Id(r); int c; ids[id] = ids.TryGetValue(id, out c) ? c + 1 : 1; }
            foreach (TableRow r in l.Rows)
            {
                int c;
                if (!r.IsDeleted && (r.IsAdded || r.IsChanged) && ids.TryGetValue(Id(r), out c) && c > 1) msgs.Add("id " + Id(r) + " is on " + c + " rows: the server's ingest stops on it");
            }
            var gets = new List<Func<string, string>>();
            foreach (TableRow r in order) { TableRow row = r; gets.Add(f => Draft.Get(row, f)); }
            foreach (Shadow s in EnchantRules.Shadows(gets))
            {
                TableRow r = order[s.Row], by = order[s.By];
                if (!mine.Contains(r) && !mine.Contains(by)) continue;
                msgs.Add("+" + Int(r, EnchantRules.Grade) + " (id " + Id(r) + ") " + (s.All ? "is never used" : "is not used for level " + EnchantRules.Spans(s.Levels))
                    + ": row " + Id(by) + " (" + EnchantRules.LadderLabel(Int(by, EnchantRules.Slot), Int(by, EnchantRules.Rank), Int(by, EnchantRules.MinLv), Int(by, EnchantRules.MaxLv)) + ") comes first");
            }
            return msgs;
        }

        // ---- editing ----

        /// <summary>Sets a field of one row (null removes the attribute). The id of a shipped row is its key.</summary>
        public void Set(TableRow r, string field, string value)
        {
            if (field == EnchantRules.Key)
            {
                if (!r.IsAdded) throw new ModKitException("the id of a shipped row is its key; add a row for a new one");
                long old = Id(r), now = ParseId(value);
                if (now != old) CheckNewId(now, r);
                Draft.Set(r, field, value);
                if (now != old)
                {
                    bool still = false;
                    foreach (TableRow o in Draft.Rows) if (o != r && o.IsAdded && Id(o) == old) still = true;
                    if (!still) IdSpaces.Unreserve(Mod, EnchantRules.Space, old);
                    IdSpaces.Reserve(Mod, EnchantRules.Space, now);
                }
                return;
            }
            if (r.IsDeleted) throw new ModKitException("row " + Get(r, EnchantRules.Key) + " is removed; restore it before a change");
            Draft.Set(r, field, value);
            if (field == EnchantRules.Grade)
                foreach (EnchantLadder l in Ladders) if (l.Rows.Contains(r)) SortRows(l);
        }

        /// <summary>Sets the rate of a grade of the ladder.</summary>
        public void SetRate(EnchantLadder l, long grade, double rate)
        {
            TableRow r = Row(l, grade);
            if (r == null) throw new ModKitException(l.Label + " has no row for +" + grade + "; add it first");
            Set(r, EnchantRules.Rate, EnchantRules.FormatRate(rate));
        }

        /// <summary>
        /// Sets slot, rank, minLv or maxLv on every row of the ladders (a ladder keeps its rows): what moves the bonus
        /// to other items, e.g. maxLv 80 for a raised level cap.
        /// </summary>
        public void SetLadders(IEnumerable<EnchantLadder> ladders, string field, long value)
        {
            if (field != EnchantRules.Slot && field != EnchantRules.Rank && field != EnchantRules.MinLv && field != EnchantRules.MaxLv)
                throw new ModKitException(field + " is no field of a whole ladder (slot, rank, minLv, maxLv)");
            string v = value.ToString(CultureInfo.InvariantCulture);
            foreach (EnchantLadder l in ladders)
            {
                foreach (TableRow r in l.Rows) if (!r.IsDeleted) Draft.Set(r, field, v);
                if (field == EnchantRules.Slot) l.Slot = value;
                else if (field == EnchantRules.Rank) l.Rank = value;
                else if (field == EnchantRules.MinLv) l.MinLv = value;
                else l.MaxLv = value;
            }
        }

        /// <summary>Sets the stats (the option field) of every row of the ladders.</summary>
        public void SetStats(IEnumerable<EnchantLadder> ladders, string option)
        {
            foreach (EnchantLadder l in ladders) foreach (TableRow r in l.Rows) if (!r.IsDeleted) Draft.Set(r, EnchantRules.Stats, option);
        }

        /// <summary>Multiplies the rate of every row of the ladders by factor, to 0.001 (a rate it cannot read stays).
        /// Gives the number of rows changed.</summary>
        public int Scale(IEnumerable<EnchantLadder> ladders, double factor)
        {
            if (double.IsNaN(factor) || double.IsInfinity(factor) || factor < 0) throw new ModKitException("the factor must be a number of 0 or more");
            int n = 0;
            foreach (EnchantLadder l in ladders)
                foreach (TableRow r in l.Rows)
                {
                    if (r.IsDeleted) continue;
                    double rate;
                    if (!EnchantRules.ServerRate(Get(r, EnchantRules.Rate), out rate) || double.IsNaN(rate) || double.IsInfinity(rate)) continue;
                    string now = EnchantRules.FormatRate(Math.Round(rate * factor, 3, MidpointRounding.AwayFromZero));
                    if (now == Get(r, EnchantRules.Rate)) continue;
                    Draft.Set(r, EnchantRules.Rate, now);
                    n++;
                }
            return n;
        }

        static long ParseId(string v)
        {
            long id;
            if (!long.TryParse((v ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0) throw new ModKitException("\"" + v + "\" is not a row id");
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

        void CheckNewId(long id, TableRow self)
        {
            if (id < EnchantRules.From || id > EnchantRules.To)
                throw new ModKitException("new enchant row ids are " + EnchantRules.From + " to " + EnchantRules.To + "; " + id + " is outside");
            foreach (TableRow o in Draft.Rows)
                if (o != self && !o.IsDeleted && Id(o) == id) throw new ModKitException("row " + id + " is there already");
            foreach (Mod m in OtherMods())
            {
                if (AddedIds(m).Contains(id)) throw new ModKitException("the enabled mod " + m.Id + " adds an enchant row " + id + " already");
                foreach (IdRange r in m.Ids)
                    if (r.Space == EnchantRules.Space && id >= r.From && id <= r.To)
                        throw new ModKitException("the enchant row id " + id + " is reserved by the enabled mod " + m.Id);
            }
        }

        // the last shipped row of the ladder (where new rows go after); null when it has none
        static TableRow Anchor(EnchantLadder l)
        {
            TableRow a = null;
            foreach (TableRow r in l.Rows) if (!r.IsAdded) a = r;
            return a;
        }

        string RowXml(long id, long slot, long grade, long rank, string rate, long lo, long hi, string option)
        {
            Func<long, string> n = v => v.ToString(CultureInfo.InvariantCulture);
            return "<" + EnchantRules.Rows + " id=\"" + n(id) + "\" slot=\"" + n(slot) + "\" grade=\"" + n(grade) + "\" rank=\"" + n(rank)
                + "\" rate=\"" + Esc(rate) + "\" minLv=\"" + n(lo) + "\" maxLv=\"" + n(hi) + "\" option=\"" + Esc(option) + "\" />";
        }

        static string Esc(string v) { return (v ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace("\"", "&quot;"); }

        /// <summary>A row for a grade the ladder lacks, with the ladder's stats and the rate given, placed after the ladder's rows.</summary>
        public TableRow AddGrade(EnchantLadder l, long grade, double rate, long newId)
        {
            if (grade < 1 || grade > EnchantRules.MaxGrade) throw new ModKitException("enchant levels are 1 to " + EnchantRules.MaxGrade);
            if (Row(l, grade) != null) throw new ModKitException(l.Label + " has a row for +" + grade + " already");
            CheckNewId(newId, null);
            string option = "";
            foreach (TableRow r in l.Rows) if (!r.IsDeleted) { option = Get(r, EnchantRules.Stats) ?? ""; break; }
            TableRow made = Draft.AddRow(RowXml(newId, l.Slot, grade, l.Rank, EnchantRules.FormatRate(rate), l.MinLv, l.MaxLv, option), Anchor(l), false);
            IdSpaces.Reserve(Mod, EnchantRules.Space, newId);
            l.Rows.Add(made);
            SortRows(l);
            return made;
        }

        /// <summary>
        /// A new ladder that copies source (its rows as shown) for another slot, rank or level range, with new ids
        /// (one per row of source, in grade order), placed after source's rows.
        /// </summary>
        public EnchantLadder CopyLadder(EnchantLadder source, long slot, long rank, long minLv, long maxLv, IList<long> newIds)
        {
            var rows = new List<TableRow>();
            foreach (TableRow r in source.Rows) if (!r.IsDeleted) rows.Add(r);
            if (rows.Count == 0) throw new ModKitException(source.Label + " has no row left to copy");
            if (newIds == null || newIds.Count < rows.Count) throw new ModKitException("the copy needs " + rows.Count + " new ids");
            var seen = new HashSet<long>();
            for (int i = 0; i < rows.Count; i++)
            {
                CheckNewId(newIds[i], null);
                if (!seen.Add(newIds[i])) throw new ModKitException("the id " + newIds[i] + " is given twice");
            }
            var made = new EnchantLadder { Slot = slot, Rank = rank, MinLv = minLv, MaxLv = maxLv };
            TableRow anchor = Anchor(source);
            // rows added after one anchor keep the order they are added in
            for (int i = 0; i < rows.Count; i++)
            {
                TableRow r = rows[i];
                string xml = RowXml(newIds[i], slot, Int(r, EnchantRules.Grade), rank, Get(r, EnchantRules.Rate) ?? "0", minLv, maxLv, Get(r, EnchantRules.Stats) ?? "");
                made.Rows.Add(Draft.AddRow(xml, anchor, false));
                IdSpaces.Reserve(Mod, EnchantRules.Space, newIds[i]);
            }
            SortRows(made);
            Ladders.Insert(Ladders.IndexOf(source) + 1, made);
            return made;
        }

        /// <summary>Removes a row: a new one goes away (and its id is free again), a shipped one is deleted.</summary>
        public void Remove(TableRow r)
        {
            bool added = r.IsAdded;
            long id = Id(r);
            Draft.Delete(r);
            if (!added) return;
            foreach (EnchantLadder l in Ladders) l.Rows.Remove(r);
            Ladders.RemoveAll(l => l.Rows.Count == 0);
            bool still = false;
            foreach (TableRow o in Draft.Rows) if (o.IsAdded && Id(o) == id) still = true;
            if (!still) IdSpaces.Unreserve(Mod, EnchantRules.Space, id);
        }

        /// <summary>Removes every row of a ladder.</summary>
        public void Remove(EnchantLadder l) { foreach (TableRow r in l.Rows.ToArray()) Remove(r); }

        public void Restore(TableRow r) { Draft.Restore(r); }
        public void Restore(EnchantLadder l) { foreach (TableRow r in l.Rows) Draft.Restore(r); }

        /// <summary>Drops every change of the mod to the ladder's shipped rows and removes its new rows.</summary>
        public void Revert(EnchantLadder l)
        {
            foreach (TableRow r in l.Rows.ToArray())
            {
                if (r.IsAdded) { Remove(r); continue; }
                Draft.Restore(r);
                Draft.RevertCells(r);
            }
            if (l.Rows.Count > 0)
            {
                TableRow f = l.Rows[0];
                l.Slot = Int(f, EnchantRules.Slot); l.Rank = Int(f, EnchantRules.Rank); l.MinLv = Int(f, EnchantRules.MinLv); l.MaxLv = Int(f, EnchantRules.MaxLv);
                SortRows(l);
            }
        }

        /// <summary>The row ids a mod's &lt;add&gt; rows of the table give.</summary>
        public static List<long> AddedIds(Mod mod)
        {
            var l = new List<long>();
            var inRow = new Regex(@"\s" + EnchantRules.Key + @"\s*=\s*[""'](\d+)[""']");
            foreach (ModOp op in mod.Ops)
            {
                var ed = op as EditOp;
                if (ed == null || !string.Equals(ed.Entry.Replace('\\', '/'), EnchantRules.Entry, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (RowOp r in ed.Rows)
                {
                    if (r.Kind != RowOpKind.Add) continue;
                    string id = null;
                    foreach (AttrOp a in r.Attrs) if (a.Kind == AttrOpKind.Set && a.Name == EnchantRules.Key && a.At == null) id = a.Value;
                    if (id == null && r.Xml != null) { Match mt = inRow.Match(r.Xml); if (mt.Success) id = mt.Groups[1].Value; }
                    long n;
                    if (id != null && long.TryParse(id.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n) && !l.Contains(n)) l.Add(n);
                }
            }
            return l;
        }

        /// <summary>The lowest free ids of the range of new rows: not in the table and not taken by the mods.</summary>
        public List<long> NextIds(IEnumerable<Mod> mods, int count, Action<ProgressInfo> progress)
        {
            var all = new List<Mod>(mods ?? new Mod[0]);
            if (!all.Contains(Mod)) all.Add(Mod);
            var taken = new HashSet<long>();
            foreach (TableRow r in Draft.Rows) taken.Add(Id(r));
            foreach (Mod m in all) if (m != Mod) foreach (long id in AddedIds(m)) taken.Add(id);
            List<long> free = IdSpaces.NextFreeGroupIds(Data, all, EnchantRules.Space, count + taken.Count, progress);
            free.RemoveAll(taken.Contains);
            if (free.Count > count) free.RemoveRange(count, free.Count - count);
            return free;
        }
    }
}
