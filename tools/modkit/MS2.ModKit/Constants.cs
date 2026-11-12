// The game constants as a mod edit: the <v key value locale feature> rows of table/constants.xml.
//
// Four copies: the server's Server.m2d (table/Server/constants.xml, the server's own keys and the shared ones), the
// server's Xml.m2d, the kms client's and the global client's Xml.m2d (table/constants.xml). A row is one key for one
// locale and one feature ("" for the row without one). The client shows its own copy's values (prices, limits,
// cooldowns) and the server decides with its copies, so an edit is written to every copy that has the row, as one
// edit block per copy, not mirrored (each side gets its own lines). Setting the shipped value back takes the line out.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>One copy of the constants table: an archive and the entry in it.</summary>
    public sealed class ConstSide
    {
        public string Archive, Entry;
        public bool IsServer;

        public override string ToString() { return Archive + " " + Entry; }

        public static readonly ConstSide ServerTable = new ConstSide { Archive = "server:Server.m2d", Entry = "table/Server/constants.xml", IsServer = true };
        public static readonly ConstSide ServerXml = new ConstSide { Archive = EntityEditor.ServerXml, Entry = "table/constants.xml", IsServer = true };
        public static readonly ConstSide KmsXml = new ConstSide { Archive = EntityEditor.ClientXml, Entry = "table/constants.xml" };
        public static readonly ConstSide GmsXml = new ConstSide { Archive = EntityEditor.GmsXml, Entry = "table/constants.xml" };

        public static readonly ConstSide[] All = { ServerTable, ServerXml, KmsXml, GmsXml };
    }

    /// <summary>The name of one row: key, locale and feature ("" for none).</summary>
    public struct ConstId : IComparable<ConstId>, IEquatable<ConstId>
    {
        public readonly string Key, Locale, Feature;

        public ConstId(string key, string locale = "", string feature = "") { Key = key ?? ""; Locale = locale ?? ""; Feature = feature ?? ""; }

        /// <summary>The row path of the id: v[key=K][feature=F][locale=L] (an empty test matches the row without the attribute).</summary>
        public string At
        {
            get
            {
                var s = new RowStep { Name = "v" };
                s.Tests.Add(new KeyValuePair<string, string>("key", Key));
                s.Tests.Add(new KeyValuePair<string, string>("feature", Feature));
                s.Tests.Add(new KeyValuePair<string, string>("locale", Locale));
                return s.ToString();
            }
        }

        public override string ToString()
        {
            return Key + (Locale.Length > 0 ? " (locale " + Locale + ")" : "") + (Feature.Length > 0 ? " (feature " + Feature + ")" : "");
        }

        public int CompareTo(ConstId o)
        {
            int c = string.Compare(Key, o.Key, StringComparison.OrdinalIgnoreCase);
            if (c == 0) c = string.CompareOrdinal(Key, o.Key);
            if (c == 0) c = string.CompareOrdinal(Locale, o.Locale);
            return c != 0 ? c : string.CompareOrdinal(Feature, o.Feature);
        }

        public bool Equals(ConstId o) { return Key == o.Key && Locale == o.Locale && Feature == o.Feature; }
        public override bool Equals(object o) { return o is ConstId && Equals((ConstId)o); }
        public override int GetHashCode() { return (Key.GetHashCode() * 31 + Locale.GetHashCode()) * 31 + Feature.GetHashCode(); }

        public static ConstId Of(XmlElem e) { return new ConstId(e.Attr("key"), e.Attr("locale"), e.Attr("feature")); }

        /// <summary>The id as the server ingest tells ids apart: the key trimmed and in upper case, locale, feature.</summary>
        public string Folded { get { return Key.Trim().ToUpperInvariant() + "\n" + Locale + "\n" + Feature; } }
    }

    public sealed class Constants
    {
        public readonly DataSet Data;
        public readonly Mod Mod;
        readonly Dictionary<ConstSide, Dictionary<ConstId, string>> rows = new Dictionary<ConstSide, Dictionary<ConstId, string>>();
        readonly Dictionary<ConstSide, Dictionary<ConstId, int>> counts = new Dictionary<ConstSide, Dictionary<ConstId, int>>();
        // the mod's whole-entry line each copy was read with (Shipped), to tell when another view replaced it
        readonly Dictionary<ConstSide, EntryOp> readWith = new Dictionary<ConstSide, EntryOp>();
        // the mod's row lines by copy and id, built on first use and dropped on every change (Forget)
        readonly Dictionary<ConstSide, Dictionary<ConstId, RowOp>> opIndex = new Dictionary<ConstSide, Dictionary<ConstId, RowOp>>();
        List<ConstId> ids;

        public Constants(DataSet data, Mod mod) { Data = data; Mod = mod; }

        /// <summary>The copies the data set has.</summary>
        public List<ConstSide> Sides()
        {
            var l = new List<ConstSide>();
            foreach (ConstSide s in ConstSide.All) if (Rows(s).Count > 0) l.Add(s);
            return l;
        }

        /// <summary>
        /// The shipped values of one copy by id (empty when the copy is not there). Of an id with more than one row
        /// the last row's value: the server ingest reads the rows in order and keeps the last.
        /// </summary>
        public Dictionary<ConstId, string> Rows(ConstSide side)
        {
            Dictionary<ConstId, string> d;
            if (rows.TryGetValue(side, out d)) return d;
            d = new Dictionary<ConstId, string>();
            var c = new Dictionary<ConstId, int>();
            rows[side] = d;
            counts[side] = c;
            byte[] b = Data.Exists(side.Archive) ? Shipped(side) : null;
            if (b == null) return d;
            XmlDoc doc = XmlDoc.Parse(b, side.ToString());
            if (doc.Root == null) return d;
            var same = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (XmlElem e in doc.Root.Children)
            {
                if (e.Name != "v" || string.IsNullOrEmpty(e.Attr("key"))) continue;
                ConstId id = ConstId.Of(e);
                d[id] = e.Attr("value") ?? "";
                int n;
                string k = id.Folded;
                same[k] = same.TryGetValue(k, out n) ? n + 1 : 1;
            }
            foreach (ConstId id in d.Keys) c[id] = same[id.Folded];
            return d;
        }

        /// <summary>
        /// How many rows of one copy the server takes for the id: the rows of the id and of the ids whose key
        /// differs only by case or spaces (the ingest matches keys that way). 0: the copy has no row.
        /// </summary>
        public int RowCount(ConstSide side, ConstId id)
        {
            Rows(side);
            int n;
            return counts[side].TryGetValue(id, out n) ? n : 0;
        }

        /// <summary>Every id of every copy, by key.</summary>
        public List<ConstId> Ids()
        {
            if (ids != null) return ids;
            var s = new HashSet<ConstId>();
            foreach (ConstSide side in ConstSide.All) foreach (ConstId id in Rows(side).Keys) s.Add(id);
            ids = new List<ConstId>(s);
            ids.Sort();
            return ids;
        }

        byte[] Shipped(ConstSide side)
        {
            var trig = new TriggerEditor(Data, Mod);
            EntryOp op = trig.OpOf(side.Archive, side.Entry);
            readWith[side] = op;
            if (op != null) return trig.ModBytes(op);
            M2dArchive a = Data.Open(side.Archive);
            M2dEntry e = a.Find(side.Entry);
            return e == null ? null : a.Read(e);
        }

        /// <summary>
        /// True when the mod now adds or replaces a copy's entry otherwise than when the copy was read (another view
        /// changed it): the shipped values are out of date and a new Constants should read them.
        /// </summary>
        public bool Stale()
        {
            if (Mod == null) return false;
            var trig = new TriggerEditor(Data, Mod);
            foreach (KeyValuePair<ConstSide, EntryOp> kv in readWith)
                if (!ReferenceEquals(trig.OpOf(kv.Key.Archive, kv.Key.Entry), kv.Value)) return true;
            return false;
        }

        /// <summary>Drops what is known of the mod's row lines: call it when the mod may have changed elsewhere.</summary>
        public void Forget() { opIndex.Clear(); }

        // ---- values ----

        /// <summary>The copies that have the row.</summary>
        public List<ConstSide> SidesOf(ConstId id)
        {
            var l = new List<ConstSide>();
            foreach (ConstSide s in ConstSide.All) if (Rows(s).ContainsKey(id)) l.Add(s);
            return l;
        }

        public string GetShipped(ConstSide side, ConstId id)
        {
            string v;
            return Rows(side).TryGetValue(id, out v) ? v : null;
        }

        /// <summary>The shipped value on the first copy that has the row (null: none has it).</summary>
        public string GetShipped(ConstId id)
        {
            foreach (ConstSide s in ConstSide.All) { string v = GetShipped(s, id); if (v != null) return v; }
            return null;
        }

        /// <summary>The value of a row on one copy with the mod (null: the copy has no such row).</summary>
        public string Get(ConstSide side, ConstId id)
        {
            string was = GetShipped(side, id);
            if (was == null) return null;
            RowOp op = Change(side, id, false);
            if (op != null)
                for (int i = op.Attrs.Count - 1; i >= 0; i--)
                    if (op.Attrs[i].Name == "value" && string.IsNullOrEmpty(op.Attrs[i].At))
                        return op.Attrs[i].Kind == AttrOpKind.Set ? op.Attrs[i].Value ?? "" : "";
            return was;
        }

        /// <summary>The value with the mod on the first copy that has the row (null: none has it).</summary>
        public string Get(ConstId id)
        {
            foreach (ConstSide s in ConstSide.All) { string v = Get(s, id); if (v != null) return v; }
            return null;
        }

        public bool IsEdited(ConstId id)
        {
            foreach (ConstSide s in SidesOf(id)) if (Get(s, id) != GetShipped(s, id)) return true;
            return false;
        }

        public int EditedCount()
        {
            int n = 0;
            foreach (ConstId id in Ids()) if (IsEdited(id)) n++;
            return n;
        }

        /// <summary>True when the copies that have the row hold different values with the mod.</summary>
        public bool Differs(ConstId id)
        {
            string first = null;
            foreach (ConstSide s in SidesOf(id))
            {
                string v = Get(s, id);
                if (first == null) first = v;
                else if (!Alike(id, first, v)) return true;
            }
            return false;
        }

        /// <summary>True when the copies that have the row shipped different values.</summary>
        public bool ShippedDiffers(ConstId id)
        {
            string first = null;
            foreach (ConstSide s in SidesOf(id))
            {
                string v = GetShipped(s, id);
                if (first == null) first = v;
                else if (!Alike(id, first, v)) return true;
            }
            return false;
        }

        // ---- kinds ----

        static readonly Regex IntRx = new Regex(@"^\s*-?\d+\s*$"), FloatRx = new Regex(@"^\s*-?(\d+\.\d*|\.\d+|\d+)([eE][-+]?\d+)?[fF]?\s*$");
        static Dictionary<string, string> serverKinds;

        /// <summary>What a value looks like: int, float, bool, list (has a comma) or text.</summary>
        public static string KindOf(string v)
        {
            v = v ?? "";
            if (IntRx.IsMatch(v)) return "int";
            if (FloatRx.IsMatch(v)) return "float";
            string t = v.Trim();
            if (string.Equals(t, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "false", StringComparison.OrdinalIgnoreCase)) return "bool";
            if (v.IndexOf(',') >= 0) return "list";
            return "text";
        }

        /// <summary>
        /// The type the server ingest reads a key as: int, long, byte, float or bool; null when it reads the key as
        /// text, a time or a vector, or does not read it. Keys match trimmed and without regard to case, as there.
        /// </summary>
        public static string ServerKind(string key)
        {
            if (serverKinds == null)
            {
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string list in ConstantKinds.Lists)
                {
                    string kind = list[0] == 'i' ? "int" : list[0] == 'l' ? "long" : list[0] == 'b' ? "byte" : list[0] == 'f' ? "float" : "bool";
                    foreach (string k in list.Substring(2).Split(',')) if (k.Length > 0) d[k] = kind;
                }
                serverKinds = d;
            }
            string s;
            return serverKinds.TryGetValue((key ?? "").Trim(), out s) ? s : null;
        }

        /// <summary>
        /// The kind the readers take a constant as: the server ingest's type when it reads the key, else what the
        /// shipped value looks like (int, float, bool, list or text).
        /// </summary>
        public static string KindOf(ConstId id, string shipped)
        {
            return ServerKind(id.Key) ?? KindOf(shipped);
        }

        static bool Truth(string v, out bool b)
        {
            string t = (v ?? "").Trim();
            if (t == "1" || string.Equals(t, "true", StringComparison.OrdinalIgnoreCase)) { b = true; return true; }
            if (t == "0" || string.Equals(t, "false", StringComparison.OrdinalIgnoreCase)) { b = false; return true; }
            b = false;
            return false;
        }

        static bool Number(string v, out double d)
        {
            d = 0;
            string t = (v ?? "").Trim();
            if (!FloatRx.IsMatch(t)) return false;
            return double.TryParse(t.TrimEnd('f', 'F'), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }

        /// <summary>Two values the game reads alike: the same number, the same truth value, or the same text.</summary>
        public static bool Alike(string a, string b)
        {
            if (a == b) return true;
            if (a == null || b == null) return false;
            double x, y;
            if (Number(a, out x) && Number(b, out y)) return x == y;
            string ka = KindOf(a);
            if (ka == "bool" && KindOf(b) == "bool") return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
            return false;
        }

        /// <summary>Two values of a constant the game reads alike, by the constant's kind (a truth value: 1 is true).</summary>
        public static bool Alike(ConstId id, string a, string b)
        {
            if (a == b) return true;
            if (a == null || b == null) return false;
            bool x, y;
            if (ServerKind(id.Key) == "bool" && Truth(a, out x) && Truth(b, out y)) return x == y;
            return Alike(a, b);
        }

        /// <summary>
        /// Why a value is refused for a row that shipped a value (null: the readers take it). A key the server
        /// ingest reads is checked against the ingest's type; another key against what its shipped value looks like.
        /// </summary>
        public static string Problem(ConstId id, string shipped, string value)
        {
            string v = "constant " + id + " \"" + (value ?? "").Trim() + "\"";
            string t = (value ?? "").Trim();
            double d;
            bool b;
            string server = ServerKind(id.Key);
            if (server != null)
            {
                switch (server)
                {
                    case "int":
                    case "long":
                    case "byte":
                        long n;
                        if (!IntRx.IsMatch(t) || !long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n))
                            return v + " is not a whole number: the server reads the key as one (" + server + ") and takes 0 for anything else";
                        long lo = server == "byte" ? 0 : server == "int" ? int.MinValue : long.MinValue, hi = server == "byte" ? 255 : server == "int" ? int.MaxValue : long.MaxValue;
                        if (n < lo || n > hi) return v + " is out of range: the server reads the key as " + server + " (" + lo + " to " + hi + ")";
                        return null;
                    case "float":
                        return Number(t, out d) ? null : v + " is not a number: the server reads the key as one and takes 0 for anything else";
                    default:
                        return Truth(t, out b) ? null : v + " is not a truth value: the server reads the key as true, false, 1 or 0 and takes false for anything else";
                }
            }
            string want = KindOf(shipped), got = KindOf(value);
            switch (want)
            {
                case "int":
                case "float": return Number(t, out d) ? null : v + " is not a number (the shipped value is " + shipped + ")";
                case "bool": return got == "bool" ? null : v + " is not true or false (the shipped value is " + shipped + ")";
                default: return null;
            }
        }

        /// <summary>
        /// A value the readers may take otherwise than meant (null: none): a fraction for a key the server does not
        /// read whose shipped value is a whole number (the client may read it as one).
        /// </summary>
        public static string Doubt(ConstId id, string shipped, string value)
        {
            if (ServerKind(id.Key) != null || KindOf(shipped) != "int") return null;
            string t = (value ?? "").Trim();
            double d;
            if (IntRx.IsMatch(t) || !Number(t, out d)) return null;
            return "constant " + id + " \"" + t + "\" is a fraction and the shipped value " + shipped.Trim() + " is whole; the server does not read the key, so check the client takes a fraction there";
        }

        // ---- edits ----

        /// <summary>
        /// Sets the value of a row on every copy that has it (side null) or on one copy. The shipped value of a copy
        /// takes its line out. Returns the copies that changed.
        /// </summary>
        public int Set(ConstId id, string value, ConstSide side = null)
        {
            if (Mod == null) throw new ModKitException("no mod is being edited");
            value = value ?? "";
            string kind = KindOf(id, GetShipped(id));
            if (kind == "int" || kind == "long" || kind == "byte" || kind == "float" || kind == "bool") value = value.Trim();
            List<ConstSide> sides = side == null ? SidesOf(id) : new List<ConstSide> { side };
            int n = 0, had = 0;
            foreach (ConstSide s in sides)
            {
                string was = GetShipped(s, id);
                if (was == null) continue;
                int rowsOf = RowCount(s, id);
                if (rowsOf > 1)
                    throw new ModKitException(s.Archive + " " + s.Entry + " has " + rowsOf + " rows of constant " + id + " (keys that differ only by case or spaces count as the same): "
                        + "the server reads the last one; take the extra rows out by hand first");
                string bad = Problem(id, was, value);
                if (bad != null) throw new ModKitException(bad);
            }
            foreach (ConstSide s in sides)
            {
                string was = GetShipped(s, id);
                if (was == null) continue;
                had++;
                if (SetOne(s, id, was, value)) n++;
            }
            if (had == 0) throw new ModKitException(side == null ? "no copy has the constant " + id : side.Archive + " has no constant " + id);
            return n;
        }

        bool SetOne(ConstSide side, ConstId id, string shipped, string value)
        {
            if (Get(side, id) == value) return false;
            RowOp op = Change(side, id, false);
            if (op != null) op.Attrs.RemoveAll(x => x.Name == "value" && string.IsNullOrEmpty(x.At));
            if (value == shipped)
            {
                if (op != null && op.Attrs.Count == 0) Drop(side, op);
                return true;
            }
            if (op == null) op = Change(side, id, true);
            op.Attrs.Add(new AttrOp { Kind = AttrOpKind.Set, Name = "value", Value = value });
            return true;
        }

        /// <summary>Takes the mod's lines of a row out on every copy. Returns whether it had one.</summary>
        public bool Take(ConstId id)
        {
            if (Mod == null) throw new ModKitException("no mod is being edited");
            bool any = false;
            foreach (ConstSide s in ConstSide.All)
            {
                RowOp op = Change(s, id, false);
                if (op == null) continue;
                Drop(s, op);
                any = true;
            }
            return any;
        }

        /// <summary>Takes the mod's lines of every row out. Returns the rows that had one.</summary>
        public int TakeAll()
        {
            int n = 0;
            foreach (ConstId id in Ids()) if (Take(id)) n++;
            return n;
        }

        // ---- the mod's lines ----

        EditOp Edit(ConstSide side, bool create)
        {
            if (Mod == null) return null;
            foreach (ModOp o in Mod.Ops)
            {
                var ed = o as EditOp;
                if (ed != null && SameArchive(ed.Archive, side.Archive) && string.Equals((ed.Entry ?? "").Replace('\\', '/'), side.Entry, StringComparison.OrdinalIgnoreCase)) return ed;
            }
            if (!create) return null;
            var n = new EditOp { Archive = side.Archive, Entry = side.Entry, Mirror = false };
            Mod.Ops.Add(n);
            return n;
        }

        RowOp Change(ConstSide side, ConstId id, bool create)
        {
            if (!create)
            {
                Dictionary<ConstId, RowOp> ix;
                if (!opIndex.TryGetValue(side, out ix))
                {
                    ix = new Dictionary<ConstId, RowOp>();
                    EditOp e = Edit(side, false);
                    if (e != null)
                        foreach (RowOp o in e.Rows)
                        {
                            ConstId at;
                            if (o.Kind == RowOpKind.Change && IdOf(o.At, out at) && !ix.ContainsKey(at)) ix[at] = o;
                        }
                    opIndex[side] = ix;
                }
                RowOp found;
                return ix.TryGetValue(id, out found) ? found : null;
            }
            RowOp had = Change(side, id, false);
            if (had != null) return had;
            EditOp ed = Edit(side, true);
            var op = new RowOp { Kind = RowOpKind.Change, At = id.At };
            ed.Rows.Add(op);
            opIndex.Remove(side);
            return op;
        }

        // the id a row op's path names: the tests key, and locale and feature with "" for none, in any order
        static bool IdOf(string at, out ConstId id)
        {
            id = default(ConstId);
            RowPath p;
            try { p = RowPath.Parse(at ?? ""); }
            catch (ModKitException) { return false; }
            if (p.Steps.Count != 1 || p.Steps[0].Name != "v" || p.Steps[0].Nth != 0) return false;
            string key = null, locale = "", feature = "";
            foreach (KeyValuePair<string, string> t in p.Steps[0].Tests)
            {
                if (t.Key == "key") key = t.Value;
                else if (t.Key == "locale") locale = t.Value;
                else if (t.Key == "feature") feature = t.Value;
                else return false;
            }
            if (key == null) return false;
            id = new ConstId(key, locale, feature);
            return true;
        }

        void Drop(ConstSide side, RowOp op)
        {
            EditOp ed = Edit(side, false);
            opIndex.Remove(side);
            if (ed == null || !ed.Rows.Remove(op)) return;
            if (ed.Rows.Count == 0) Mod.Ops.Remove(ed);
        }

        static bool SameArchive(string a, string b) { return string.Equals(DataSet.NormalizeRef(a), DataSet.NormalizeRef(b), StringComparison.OrdinalIgnoreCase); }
    }
}
