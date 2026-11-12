// Item sets (set bonuses): the items of a set and what a character gets for wearing 2, 3, ... of them.
//
// The KMS client reads one table, table/setiteminfo.xml (SetItemTable::LoadSets, SetItemInfo::Load,
// SetItemOptionPart::Load):
//   <set id itemIDs showEffectIfItsSetItemMotion isDisableTooltip>     optionID is in the file and is not read
//     <part count additionalEffectID additionalEffectLevel animationPrefix setEffect groundAttribute
//           str dex int luk hp asp msp atp evp cap ndd pap map par mar pen
//           finalAdditionalDamage sgi_elite sgi_boss lddincrease />
//   The whole table fails to load when two sets have one id, a set has id 0, two parts of a set have one count,
//   a part has count 0, or the effect ids and effect levels of a part differ in number.
// The server and the global client read two tables:
//   table/setiteminfo.xml    <set id itemIDs optionID showEffectIfItsSetItemMotion isDisableTooltip feature locale />
//   table/setitemoption.xml  <option id> <part count additionalEffectID additionalEffectLevel animationPrefix
//                            setEffect groundAttribute {stat}_value_base {stat}_rate_base sgi_target
//                            sgi_boss_target /> </option>
//   A set takes the parts of the option its optionID names (several sets can name one option); the server's data
//   ingest fails on a set whose option is missing.
// The set's name is the row <key id name /> of string/{lang}/setitemname.xml on every side.
//
// The editor shows a bonus in the KMS notation and writes each side its own: a value X is X_value_base on the server,
// the rates (pen, finalAdditionalDamage, lddincrease, sgi_elite, sgi_boss) are thousandths there ("0.02" for 20),
// sgi_elite is sgi_rate_base with sgi_target="3" and sgi_boss is sgi_boss_rate_base with sgi_boss_target="4"
// (or sgi_rate_base with sgi_target="4").
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>The fields of a set bonus and how each side writes them; the checks the editor and the plan share.</summary>
    public static class ItemSetRules
    {
        public const string InfoEntry = "table/setiteminfo.xml", OptionEntry = "table/setitemoption.xml", NameFile = "setitemname.xml";
        public const string Rows = "set", OptionRows = "option", PartRows = "part";
        public const string Space = "itemset";
        /// <summary>The set ids of mods: no shipped set uses this band.</summary>
        public const long From = 99000000, To = 99009999;
        public const string Count = "count", Effects = "effects", Car = "car";

        /// <summary>The bonuses the KMS client reads on a part, in its order.</summary>
        public static readonly string[] KmsStats =
        {
            "str", "dex", "int", "luk", "hp", "asp", "msp", "atp", "evp", "cap", "ndd", "pap", "map", "par", "mar", "pen",
            "finalAdditionalDamage", "sgi_elite", "sgi_boss", "lddincrease",
        };
        /// <summary>Every bonus of the shipped tables: the KMS client's and car, which only the server reads.</summary>
        public static readonly string[] Stats;
        public static readonly string[] Texts = { "animationPrefix", "setEffect", "groundAttribute" };

        static ItemSetRules()
        {
            var l = new List<string>(KmsStats);
            l.Insert(10, Car);
            Stats = l.ToArray();
        }

        public static bool IsStat(string field) { return Array.IndexOf(Stats, field) >= 0; }

        /// <summary>The server's attribute of a rate bonus (null for a value bonus, sgi_elite and sgi_boss).</summary>
        static string RateAttr(string field)
        {
            switch (field)
            {
                case "pen": return "pen_rate_base";
                case "lddincrease": return "lddincrease_rate_base";
                case "finalAdditionalDamage": return "finaladditionaldamage_rate_base";
                default: return null;
            }
        }

        /// <summary>What a bonus is called in the editor.</summary>
        public static string Title(string field)
        {
            switch (field)
            {
                case "str": return "strength";
                case "dex": return "dexterity";
                case "int": return "intelligence";
                case "luk": return "luck";
                case "hp": return "health";
                case "asp": return "attack speed";
                case "msp": return "movement speed";
                case "atp": return "accuracy";
                case "evp": return "evasion";
                case "cap": return "critical rate";
                case "car": return "critical damage (server only)";
                case "ndd": return "defense";
                case "pap": return "physical attack";
                case "map": return "magic attack";
                case "par": return "physical resistance";
                case "mar": return "magic resistance";
                case "pen": return "piercing, 1/1000";
                case "finalAdditionalDamage": return "total damage, 1/1000";
                case "sgi_elite": return "damage to elites, 1/1000";
                case "sgi_boss": return "damage to bosses, 1/1000";
                case "lddincrease": return "ranged damage, 1/1000";
                case Effects: return "effects (id:level, ...)";
                case Count: return "items worn";
                default: return field;
            }
        }

        static string Scale(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            decimal d;
            if (!decimal.TryParse(v, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out d)) return v;
            return d == 0 ? "" : (d * 1000m).ToString("0.####", CultureInfo.InvariantCulture);
        }

        static string Unscale(string field, string v)
        {
            decimal d;
            if (!decimal.TryParse(v, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out d))
                throw new ModKitException(field + " \"" + v + "\" is not a number");
            return (d / 1000m).ToString("0.#######", CultureInfo.InvariantCulture);
        }

        static string Plain(string v) { return string.IsNullOrEmpty(v) || v == "0" ? "" : v; }

        static List<string> Tokens(string v)
        {
            var l = new List<string>();
            if (v == null) return l;
            foreach (string t in v.Split(',')) if (t.Trim().Length > 0) l.Add(t.Trim());
            return l;
        }

        /// <summary>
        /// A field of a part in the editor's notation, read from one side's row: "" for none. get gives the row's
        /// attributes; kms says whose notation the row is in.
        /// </summary>
        public static string Read(Func<string, string> get, bool kms, string field)
        {
            if (field == Count) return get(Count) ?? "";
            if (field == Effects)
            {
                List<string> ids = Tokens(get("additionalEffectID")), lv = Tokens(get("additionalEffectLevel"));
                bool none = true;
                foreach (string id in ids) if (id != "0") none = false;
                if (none) return "";
                var sb = new StringBuilder();
                for (int i = 0; i < ids.Count; i++)
                    sb.Append(i > 0 ? "," : "").Append(ids[i]).Append(':').Append(i < lv.Count ? lv[i] : "?");
                return sb.ToString();
            }
            if (!IsStat(field)) return get(field) ?? "";
            if (kms) return Plain(get(field));
            string rate = RateAttr(field);
            if (rate != null) return Scale(get(rate));
            if (field == "sgi_elite") return get("sgi_target") == "3" ? Scale(get("sgi_rate_base")) : "";
            if (field == "sgi_boss")
            {
                string b = Scale(get("sgi_boss_rate_base"));
                if (b.Length > 0) return b;
                return get("sgi_target") == "4" ? Scale(get("sgi_rate_base")) : "";
            }
            return Plain(get(field + "_value_base"));
        }

        /// <summary>Writes a field of a part to one side's row in that side's notation ("" for none); set(name, null) removes an attribute.</summary>
        public static void Write(Func<string, string> get, Action<string, string> set, bool kms, string field, string value)
        {
            value = (value ?? "").Trim();
            if (field == Count) { set(Count, value); return; }
            if (field == Effects)
            {
                var ids = new List<string>();
                var lv = new List<string>();
                foreach (string t in Tokens(value))
                {
                    int c = t.IndexOf(':');
                    string id = (c < 0 ? t : t.Substring(0, c)).Trim(), level = c < 0 ? "1" : t.Substring(c + 1).Trim();
                    long n;
                    if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n <= 0 ||
                        !long.TryParse(level, NumberStyles.None, CultureInfo.InvariantCulture, out n))
                        throw new ModKitException("\"" + t + "\" is not an effect \"id:level\"");
                    ids.Add(id);
                    lv.Add(level);
                }
                if (ids.Count == 0)
                {
                    // a shipped part without an effect: the kms table has neither attribute, the server's has 0 and 1
                    set("additionalEffectID", kms ? null : "0");
                    set("additionalEffectLevel", kms ? null : "1");
                }
                else
                {
                    set("additionalEffectID", string.Join(",", ids.ToArray()));
                    set("additionalEffectLevel", string.Join(",", lv.ToArray()));
                }
                return;
            }
            if (!IsStat(field)) { set(field, value.Length == 0 ? null : value); return; }
            if (value == "0") value = "";
            if (kms)
            {
                // the client reads no car on a part: it is written only to a row that has one
                if (field == Car && get(Car) == null) return;
                set(field, value.Length == 0 ? null : value);
                return;
            }
            string rate = RateAttr(field);
            if (rate != null) { set(rate, value.Length == 0 ? null : Unscale(field, value)); return; }
            if (field == "sgi_elite")
            {
                if (value.Length == 0)
                {
                    if (get("sgi_target") == "3") { set("sgi_rate_base", null); set("sgi_target", null); }
                    return;
                }
                string v = Unscale(field, value);
                if (get("sgi_target") == "4")
                {
                    // the boss bonus sits in the slot elites need: it moves to its own attributes
                    set("sgi_boss_rate_base", get("sgi_rate_base"));
                    set("sgi_boss_target", "4");
                }
                set("sgi_rate_base", v);
                set("sgi_target", "3");
                return;
            }
            if (field == "sgi_boss")
            {
                bool shared = get("sgi_target") == "4" && Scale(get("sgi_boss_rate_base")).Length == 0;
                if (value.Length == 0)
                {
                    if (shared) { set("sgi_rate_base", null); set("sgi_target", null); }
                    else { set("sgi_boss_rate_base", null); set("sgi_boss_target", null); }
                }
                else if (shared) set("sgi_rate_base", Unscale(field, value));
                else { set("sgi_boss_rate_base", Unscale(field, value)); set("sgi_boss_target", "4"); }
                return;
            }
            set(field + "_value_base", value.Length == 0 ? null : value);
        }

        public const string TableFails = ": the kms client fails to load the whole set table";

        /// <summary>The problems of one part row of one side (members: the number of items of the set, -1 when unknown).</summary>
        public static List<string> CheckPart(Func<string, string> get, bool kms, int members)
        {
            var l = new List<string>();
            string cs = get(Count) ?? "";
            int count;
            string label = "bonus for " + (cs.Length == 0 ? "?" : cs) + " items";
            if (!int.TryParse(cs, NumberStyles.None, CultureInfo.InvariantCulture, out count) || count <= 0)
                l.Add(label + ": count \"" + cs + "\" is not a number above 0" + (kms ? TableFails : ""));
            else if (members >= 0 && count > members)
                l.Add(label + ": the set has " + members + " item" + (members == 1 ? "" : "s") + ", the bonus can never be active");
            List<string> ids = Tokens(get("additionalEffectID")), lv = Tokens(get("additionalEffectLevel"));
            if (ids.Count != lv.Count)
                l.Add(label + ": " + ids.Count + " effect id" + (ids.Count == 1 ? "" : "s") + " and " + lv.Count + " effect level" + (lv.Count == 1 ? "" : "s") + (kms ? TableFails : ""));
            foreach (string t in ids)
            {
                long n;
                if (!long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out n)) l.Add(label + ": effect id \"" + t + "\" is not a number");
            }
            foreach (string f in Stats)
            {
                string v = Read(get, kms, f);
                decimal d;
                if (v.Length > 0 && !decimal.TryParse(v, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out d))
                    l.Add(label + ": " + f + " \"" + v + "\" is not a number");
                else if (v.Length > 0 && kms && v.IndexOf('.') >= 0)
                    l.Add(label + ": " + f + " \"" + v + "\" is not a whole number (the kms client reads whole numbers)");
            }
            if (kms && Plain(get(Car)).Length > 0)
                l.Add(label + ": car=\"" + get(Car) + "\" is not a bonus the kms client reads on a set");
            if (!kms)
            {
                string bt = get("sgi_boss_target");
                if (!string.IsNullOrEmpty(bt) && bt != "0" && bt != "4")
                    l.Add(label + ": sgi_boss_target \"" + bt + "\" is not 0 or 4, the values the server takes");
                string st = get("sgi_target");
                if (Scale(get("sgi_rate_base")).Length > 0 && st != "3" && st != "4")
                    l.Add(label + ": sgi_target \"" + st + "\" is not 3 (elites) or 4 (bosses)");
            }
            return l;
        }

        /// <summary>The item ids of an itemIDs list; false when a token is not an id.</summary>
        public static bool TryMembers(string itemIds, out List<long> ids)
        {
            ids = new List<long>();
            foreach (string t in Tokens(itemIds))
            {
                long n;
                if (!long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n <= 0 || n > int.MaxValue) return false;
                ids.Add(n);
            }
            return true;
        }

        /// <summary>The problems of a set row and its parts on one side.</summary>
        public static List<string> CheckSet(Func<string, string> get, List<Func<string, string>> parts, bool kms)
        {
            var l = new List<string>();
            List<long> ids;
            int members = -1;
            if (!TryMembers(get("itemIDs"), out ids)) l.Add("itemIDs \"" + get("itemIDs") + "\" is not a list of item ids");
            else
            {
                members = ids.Count;
                if (ids.Count == 0) l.Add("the set has no items (itemIDs)");
                var seen = new HashSet<long>();
                foreach (long id in ids) if (!seen.Add(id)) l.Add("item " + id + " is in the set twice");
            }
            var counts = new HashSet<string>();
            foreach (Func<string, string> p in parts)
            {
                l.AddRange(CheckPart(p, kms, members));
                string c = p(Count) ?? "";
                if (!counts.Add(c)) l.Add("two bonuses for " + c + " items" + (kms ? TableFails : ""));
            }
            return l;
        }
    }

    /// <summary>One side of the set editor: the tables of one archive.</summary>
    public sealed class ItemSetSide
    {
        public string Archive;
        /// <summary>True for the kms client: the parts are children of the set row. Else they are in Options.</summary>
        public bool Kms;
        public TableDraft Info;
        /// <summary>table/setitemoption.xml (null for the kms client, and when the archive has none).</summary>
        public TableDraft Options;
        internal Dictionary<TableRow, TableDraft> Nested = new Dictionary<TableRow, TableDraft>();
        internal Dictionary<string, TableRow> OptionById;

        internal TableRow OptionRow(string id)
        {
            if (Options == null) return null;
            if (OptionById == null)
            {
                OptionById = new Dictionary<string, TableRow>();
                foreach (TableRow r in Options.Rows)
                    if (r.Base.Name == ItemSetRules.OptionRows && !r.IsDeleted) OptionById[Options.Get(r, "id") ?? ""] = r;     // the last row of an id wins
            }
            TableRow row;
            return OptionById.TryGetValue(id ?? "", out row) ? row : null;
        }
    }

    /// <summary>A set as the editor shows it: its row on every side (null where a side has none).</summary>
    public sealed class ItemSet
    {
        public readonly TableRow[] Rows;
        internal ItemSet(int sides) { Rows = new TableRow[sides]; }

        public TableRow Shown { get { foreach (TableRow r in Rows) if (r != null) return r; return null; } }
        public bool IsAdded { get { TableRow r = Shown; return r != null && r.IsAdded; } }
        public bool IsDeleted { get { foreach (TableRow r in Rows) if (r != null && !r.IsDeleted) return false; return true; } }
    }

    /// <summary>One bonus of a set (the part of one count) on every side; it is good until the next change of the set.</summary>
    public sealed class SetPart
    {
        public string Count;
        internal ItemSetEditor.PartRef[] Refs;
    }

    /// <summary>
    /// The set editor of one mod: the set tables of every side that ships them (kms client, server; the global
    /// client when there is no server), read through TableDraft and written to every side at once.
    /// </summary>
    public sealed class ItemSetEditor
    {
        public readonly DataSet Data;
        public readonly Mod Mod;
        public readonly List<ItemSetSide> Sides = new List<ItemSetSide>();
        public readonly List<ItemSet> Sets = new List<ItemSet>();
        /// <summary>The other enabled mods, read when a new id is checked or asked for (null: none).</summary>
        public Func<IEnumerable<Mod>> Others;

        internal sealed class PartRef
        {
            internal int Side;
            internal TableDraft HDraft;     // the holder: the kms set row or the server's option row
            internal TableRow HRow;
            internal int Index;             // among the holder's children
            internal XmlElem Elem;          // the part where the holder has no draft of its own
            internal TableDraft Draft;      // the holder's parts as rows, once the mod changes one
            internal TableRow Row;

            internal string Raw(string a) { return Row != null ? Draft.Get(Row, a) : Elem.Attr(a); }
        }

        sealed class Bag
        {
            internal string Name;
            internal readonly List<KeyValuePair<string, string>> Attrs = new List<KeyValuePair<string, string>>();

            internal string Get(string a) { foreach (KeyValuePair<string, string> kv in Attrs) if (kv.Key == a) return kv.Value; return null; }

            internal void Set(string a, string v)
            {
                for (int i = 0; i < Attrs.Count; i++)
                    if (Attrs[i].Key == a)
                    {
                        if (v == null) Attrs.RemoveAt(i); else Attrs[i] = new KeyValuePair<string, string>(a, v);
                        return;
                    }
                if (v != null) Attrs.Add(new KeyValuePair<string, string>(a, v));
            }

            internal string Xml()
            {
                var sb = new StringBuilder("<" + Name);
                foreach (KeyValuePair<string, string> kv in Attrs) sb.Append(' ').Append(kv.Key).Append("=\"").Append(Esc(kv.Value)).Append('"');
                return sb.Append(" />").ToString();
            }
        }

        static string Esc(string v) { return v.Replace("&", "&amp;").Replace("<", "&lt;").Replace("\"", "&quot;"); }

        public ItemSetEditor(DataSet data, Mod mod)
        {
            Data = data;
            Mod = mod;
            foreach (string a in SideArchives(data, mod))
            {
                var s = new ItemSetSide { Archive = a, Kms = Profile.OfArchive(a) == Profile.KmsClient, Info = new TableDraft(data, mod, a, ItemSetRules.InfoEntry, null) };
                if (!s.Kms && data.Open(a).Find(ItemSetRules.OptionEntry) != null) s.Options = new TableDraft(data, mod, a, ItemSetRules.OptionEntry, null);
                // the kms table holds the parts in its own notation: its lines are for it alone (the server's lines are
                // mirrored to the global client as any other)
                s.Info.NoMirror = s.Kms;
                Sides.Add(s);
            }
            if (Sides.Count == 0) throw new ModKitException("no target of the data set has " + ItemSetRules.InfoEntry);
            foreach (ItemSetSide s in Sides)
            {
                Touched(s, s.Kms ? s.Info : s.Options);
            }
            Pair();
        }

        /// <summary>The archives a set is written to: the kms client's and the server's Xml.m2d, and the global
        /// client's when the data set has no server (else the server's lines are planned for the global client too)
        /// or when the mod has lines of its own there.</summary>
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
                if (arch.Find(ItemSetRules.InfoEntry) != null) l.Add(a);
            }
            return l;
        }

        static bool IsEntry(EditOp ed, string entry) { return string.Equals(ed.Entry.Replace('\\', '/'), entry, StringComparison.OrdinalIgnoreCase); }

        static bool HasLines(Mod mod, string archive)
        {
            if (mod == null) return false;
            foreach (ModOp op in mod.Ops)
            {
                var ed = op as EditOp;
                if (ed != null && string.Equals(ed.Archive, archive, StringComparison.OrdinalIgnoreCase)
                    && (IsEntry(ed, ItemSetRules.InfoEntry) || IsEntry(ed, ItemSetRules.OptionEntry))) return true;
            }
            return false;
        }

        // the holders the mod already has part lines for get their part drafts now, so that the lines show
        void Touched(ItemSetSide side, TableDraft d)
        {
            if (d == null || d.OtherOps == 0) return;
            var rowOf = new Dictionary<XmlElem, TableRow>();
            foreach (TableRow r in d.Rows) if (r.Original != null) rowOf[r.Original] = r;
            foreach (ModOp op in Mod.Ops)
            {
                var ed = op as EditOp;
                if (ed == null || ed.Archive != d.Archive || !IsEntry(ed, d.Entry)) continue;
                foreach (RowOp r in ed.Rows)
                {
                    XmlElem holder = null;
                    foreach (string sel in new[] { r.At, r.After, r.Before, r.Copy })
                    {
                        XmlElem e = Resolve(d, sel);
                        if (e != null && e.Parent != null && e.Parent != d.Doc.Root) holder = Top(d, e);
                    }
                    XmlElem into = Resolve(d, r.In);
                    if (into != null && into != d.Doc.Root && r.After == null && r.Before == null) holder = Top(d, into);
                    TableRow row;
                    if (holder != null && rowOf.TryGetValue(holder, out row) && !side.Nested.ContainsKey(row)) NestedOf(side, d, row);
                }
            }
        }

        static XmlElem Resolve(TableDraft d, string selector)
        {
            if (string.IsNullOrEmpty(selector)) return null;
            string error;
            try { return RowPath.Parse(selector).TryResolve(d.Doc.Root, out error); }
            catch (ModKitException) { return null; }
        }

        static XmlElem Top(TableDraft d, XmlElem e)
        {
            while (e != null && e.Parent != d.Doc.Root) e = e.Parent;
            return e;
        }

        TableDraft NestedOf(ItemSetSide side, TableDraft d, TableRow holder)
        {
            TableDraft n;
            if (side.Nested.TryGetValue(holder, out n)) return n;
            n = new TableDraft(Data, Mod, d.Archive, d.Entry, holder.Selector);
            n.NoMirror = d.NoMirror;
            side.Nested[holder] = n;
            return n;
        }

        void Pair()
        {
            Sets.Clear();
            // rows that share an id on one side pair in their order: the n-th with the n-th
            var byId = new Dictionary<string, List<ItemSet>>();
            for (int i = 0; i < Sides.Count; i++)
            {
                var seen = new Dictionary<string, int>();
                foreach (TableRow r in Sides[i].Info.Rows)
                {
                    string k = Sides[i].Info.Get(r, "id");
                    if (r.Base.Name != ItemSetRules.Rows || string.IsNullOrEmpty(k)) continue;
                    List<ItemSet> list;
                    if (!byId.TryGetValue(k, out list)) byId[k] = list = new List<ItemSet>();
                    int n;
                    seen.TryGetValue(k, out n);
                    seen[k] = n + 1;
                    if (n >= list.Count)
                    {
                        var made = new ItemSet(Sides.Count);
                        list.Add(made);
                        Sets.Add(made);
                    }
                    list[n].Rows[i] = r;
                }
            }
        }

        // ---- reading ----

        /// <summary>A value of the set row (id, itemIDs, showEffectIfItsSetItemMotion, isDisableTooltip, ...) on the first side that has the set.</summary>
        public string Get(ItemSet s, string field)
        {
            for (int i = 0; i < Sides.Count; i++) if (s.Rows[i] != null) return Sides[i].Info.Get(s.Rows[i], field);
            return null;
        }

        public long Id(ItemSet s) { long id; return long.TryParse(Get(s, "id"), NumberStyles.None, CultureInfo.InvariantCulture, out id) ? id : 0; }

        /// <summary>True when the sides that have the set give the field of the set row different values.</summary>
        public bool Differs(ItemSet s, string field)
        {
            bool any = false;
            string first = null;
            for (int i = 0; i < Sides.Count; i++)
            {
                if (s.Rows[i] == null) continue;
                string v = Sides[i].Info.Get(s.Rows[i], field) ?? "";
                if (!any) { first = v; any = true; }
                else if (v != first) return true;
            }
            return false;
        }

        /// <summary>The items of the set (of the first side that has it); tokens that are no ids are left out.</summary>
        public List<long> Members(ItemSet s)
        {
            var l = new List<long>();
            foreach (string t in (Get(s, "itemIDs") ?? "").Split(','))
            {
                long n;
                if (long.TryParse(t.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n)) l.Add(n);
            }
            return l;
        }

        /// <summary>The sides (archive names) that do not have the set.</summary>
        public List<string> MissingOn(ItemSet s)
        {
            var l = new List<string>();
            for (int i = 0; i < Sides.Count; i++) if (s.Rows[i] == null) l.Add(Sides[i].Archive);
            return l;
        }

        bool Holder(int i, ItemSet s, out TableDraft d, out TableRow r)
        {
            d = null; r = null;
            TableRow info = s.Rows[i];
            if (info == null) return false;
            ItemSetSide side = Sides[i];
            if (side.Kms) { d = side.Info; r = info; return true; }
            d = side.Options;
            r = side.OptionRow(side.Info.Get(info, "optionID"));
            return r != null;
        }

        List<PartRef> SideParts(int i, ItemSet s)
        {
            var l = new List<PartRef>();
            TableDraft hd;
            TableRow hr;
            if (!Holder(i, s, out hd, out hr)) return l;
            TableDraft nd;
            if (!hr.IsAdded && Sides[i].Nested.TryGetValue(hr, out nd))
            {
                foreach (TableRow row in nd.Rows)
                    if (row.Base.Name == ItemSetRules.PartRows && !row.IsDeleted)
                        l.Add(new PartRef { Side = i, HDraft = hd, HRow = hr, Draft = nd, Row = row });
                return l;
            }
            for (int k = 0; k < hr.Base.Children.Count; k++)
                if (hr.Base.Children[k].Name == ItemSetRules.PartRows)
                    l.Add(new PartRef { Side = i, HDraft = hd, HRow = hr, Index = k, Elem = hr.Base.Children[k] });
            return l;
        }

        /// <summary>The bonuses of a set, lowest count first; the parts of one count on the sides are one bonus.</summary>
        public List<SetPart> Parts(ItemSet s)
        {
            var all = new List<SetPart>();
            var byCount = new Dictionary<string, List<SetPart>>();
            for (int i = 0; i < Sides.Count; i++)
            {
                var seen = new Dictionary<string, int>();
                foreach (PartRef r in SideParts(i, s))
                {
                    string c = r.Raw(ItemSetRules.Count) ?? "";
                    List<SetPart> list;
                    if (!byCount.TryGetValue(c, out list)) byCount[c] = list = new List<SetPart>();
                    int n;
                    seen.TryGetValue(c, out n);
                    seen[c] = n + 1;
                    if (n >= list.Count)
                    {
                        var made = new SetPart { Count = c, Refs = new PartRef[Sides.Count] };
                        list.Add(made);
                        all.Add(made);
                    }
                    list[n].Refs[i] = r;
                }
            }
            // a stable sort by count
            var sorted = new List<SetPart>();
            foreach (SetPart p in all)
            {
                int at = sorted.Count;
                while (at > 0 && Num(sorted[at - 1].Count) > Num(p.Count)) at--;
                sorted.Insert(at, p);
            }
            return sorted;
        }

        static long Num(string v) { long n; return long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out n) ? n : long.MaxValue; }

        /// <summary>A field of a bonus in the editor's notation (ItemSetRules.Stats, Texts, Effects, Count), of the first side that has it.</summary>
        public string Get(SetPart p, string field)
        {
            foreach (PartRef r in p.Refs) if (r != null) return ItemSetRules.Read(r.Raw, Sides[r.Side].Kms, field);
            return "";
        }

        /// <summary>True when the sides give the field of the bonus different values (car: among the sides that read it).</summary>
        public bool Differs(SetPart p, string field)
        {
            bool any = false;
            string first = null;
            foreach (PartRef r in p.Refs)
            {
                if (r == null || (field == ItemSetRules.Car && Sides[r.Side].Kms)) continue;
                string v = ItemSetRules.Read(r.Raw, Sides[r.Side].Kms, field);
                if (!any) { first = v; any = true; }
                else if (v != first) return true;
            }
            return false;
        }

        /// <summary>The sides (archive names) whose set has no bonus of this count though it has the set's parts.</summary>
        public List<string> MissingOn(ItemSet s, SetPart p)
        {
            var l = new List<string>();
            TableDraft hd;
            TableRow hr;
            for (int i = 0; i < Sides.Count; i++) if (p.Refs[i] == null && Holder(i, s, out hd, out hr)) l.Add(Sides[i].Archive);
            return l;
        }

        /// <summary>The bonuses as one line: "2: str 9, hp 100; 4: effect 1:1".</summary>
        public string Summary(SetPart p)
        {
            var sb = new StringBuilder();
            foreach (string f in ItemSetRules.Stats)
            {
                string v = Get(p, f);
                if (v.Length > 0) sb.Append(sb.Length > 0 ? ", " : "").Append(f).Append(' ').Append(v);
            }
            string e = Get(p, ItemSetRules.Effects);
            if (e.Length > 0) sb.Append(sb.Length > 0 ? ", " : "").Append("effect ").Append(e);
            foreach (string f in ItemSetRules.Texts)
            {
                string v = Get(p, f);
                if (v.Length > 0) sb.Append(sb.Length > 0 ? ", " : "").Append(f).Append(' ').Append(v);
            }
            return sb.ToString();
        }

        /// <summary>True when the mod changes the set: its row, a bonus or its name.</summary>
        public bool IsChanged(ItemSet s)
        {
            for (int i = 0; i < Sides.Count; i++)
            {
                if (s.Rows[i] == null) continue;
                if (s.Rows[i].IsChanged || s.Rows[i].IsDeleted) return true;
                TableDraft hd, nd;
                TableRow hr;
                if (!Holder(i, s, out hd, out hr)) continue;
                if (hr.IsChanged || (hr.IsAdded && !s.Rows[i].IsAdded)) return true;
                if (Sides[i].Nested.TryGetValue(hr, out nd))
                    foreach (TableRow r in nd.Rows) if (r.IsAdded || r.IsDeleted || r.IsChanged) return true;
            }
            string id = Get(s, "id") ?? "";
            foreach (NameTable n in names.Values)
            {
                TableRow r;
                if (n != null && n.ById.TryGetValue(id, out r) && (r.IsAdded || r.IsChanged)) return true;
            }
            return false;
        }

        /// <summary>Sets whose id, name or an item id holds text.</summary>
        public List<ItemSet> Find(string text)
        {
            text = (text ?? "").Trim();
            var l = new List<ItemSet>();
            foreach (ItemSet s in Sets)
            {
                if (text.Length > 0 && (Get(s, "id") ?? "").IndexOf(text, StringComparison.Ordinal) < 0
                    && Name(s).IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0
                    && ("," + (Get(s, "itemIDs") ?? "")).IndexOf("," + text, StringComparison.Ordinal) < 0) continue;
                l.Add(s);
            }
            return l;
        }

        // ---- names ----

        sealed class NameTable
        {
            internal TableDraft Draft;
            internal readonly Dictionary<string, TableRow> ById = new Dictionary<string, TableRow>();
        }

        readonly Dictionary<string, NameTable> names = new Dictionary<string, NameTable>();
        readonly Dictionary<string, List<string>> langs = new Dictionary<string, List<string>>();

        /// <summary>The language folders of an archive that hold string/{lang}/setitemname.xml.</summary>
        public List<string> Languages(string archive)
        {
            List<string> list;
            if (langs.TryGetValue(archive, out list)) return list;
            list = new List<string>();
            var rx = new Regex("^string/([^/]+)/" + Regex.Escape(ItemSetRules.NameFile) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (M2dEntry e in Data.Open(archive).Entries)
            {
                Match m = rx.Match(e.Name.Replace('\\', '/'));
                if (m.Success && !list.Contains(m.Groups[1].Value)) list.Add(m.Groups[1].Value);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            langs[archive] = list;
            return list;
        }

        NameTable Names(string archive, string lang)
        {
            string key = archive + "\n" + lang;
            NameTable n;
            if (names.TryGetValue(key, out n)) return n;
            try
            {
                n = new NameTable { Draft = new TableDraft(Data, Mod, archive, "string/" + lang + "/" + ItemSetRules.NameFile, null) };
                foreach (TableRow r in n.Draft.Rows)
                    if (r.Base.Name == "key" && !r.IsDeleted) n.ById[n.Draft.Get(r, "id") ?? ""] = r;
            }
            catch (ModKitException) { n = null; }
            names[key] = n;
            return n;
        }

        /// <summary>The set's name: the English one of the first side that has a name file (else its first language); "" for none.</summary>
        public string Name(ItemSet s)
        {
            string id = Get(s, "id") ?? "";
            foreach (ItemSetSide side in Sides)
            {
                List<string> l = Languages(side.Archive);
                if (l.Count == 0) continue;
                NameTable n = Names(side.Archive, l.Contains("en") ? "en" : l[0]);
                TableRow r;
                if (n != null && n.ById.TryGetValue(id, out r)) return n.Draft.Get(r, "name") ?? "";
            }
            return "";
        }

        /// <summary>Names the set in every language of every side (a new row where a name file has none).</summary>
        public void SetName(ItemSet s, string name)
        {
            name = name ?? "";
            string id = Get(s, "id") ?? "";
            for (int i = 0; i < Sides.Count; i++)
            {
                if (s.Rows[i] == null) continue;
                foreach (string lang in Languages(Sides[i].Archive))
                {
                    NameTable n = Names(Sides[i].Archive, lang);
                    if (n == null) continue;
                    TableRow r;
                    if (n.ById.TryGetValue(id, out r))
                    {
                        if (r.IsAdded && name.Length == 0) { n.Draft.Delete(r); n.ById.Remove(id); }
                        else n.Draft.Set(r, "name", name);
                    }
                    else if (name.Length > 0)
                        n.ById[id] = n.Draft.AddRow("<key id=\"" + Esc(id) + "\" name=\"" + Esc(name) + "\" />");
                }
            }
        }

        // ---- checks ----

        /// <summary>The other sets of a side that take their bonuses from the option this set names.</summary>
        public List<ItemSet> SharesOption(ItemSet s, int side)
        {
            var l = new List<ItemSet>();
            if (Sides[side].Kms || s.Rows[side] == null) return l;
            string o = Sides[side].Info.Get(s.Rows[side], "optionID") ?? "";
            foreach (ItemSet other in Sets)
                if (other != s && other.Rows[side] != null && !other.Rows[side].IsDeleted && (Sides[side].Info.Get(other.Rows[side], "optionID") ?? "") == o) l.Add(other);
            return l;
        }

        /// <summary>The problems of the set as shown (the plan checks the final tables the same way, and items and effects).</summary>
        public List<string> Check(ItemSet s)
        {
            var l = new List<string>();
            if (s.IsDeleted) return l;
            var from = new Dictionary<string, List<string>>();
            var order = new List<string>();
            int have = 0;
            for (int i = 0; i < Sides.Count; i++)
            {
                TableRow row = s.Rows[i];
                if (row == null) continue;
                have++;
                TableDraft info = Sides[i].Info;
                var parts = new List<Func<string, string>>();
                foreach (PartRef r in SideParts(i, s)) parts.Add(r.Raw);
                var found = ItemSetRules.CheckSet(a => info.Get(row, a), parts, Sides[i].Kms);
                TableDraft hd;
                TableRow hr;
                if (!Sides[i].Kms && !Holder(i, s, out hd, out hr))
                    found.Add("optionID \"" + info.Get(row, "optionID") + "\" has no <option> row in " + ItemSetRules.OptionEntry + ": the server's data ingest fails");
                List<ItemSet> shared = SharesOption(s, i);
                if (shared.Count > 0)
                {
                    var ids = new List<string>();
                    foreach (ItemSet o in shared) { if (ids.Count == 5) { ids.Add("..."); break; } ids.Add(Get(o, "id")); }
                    found.Add("the bonuses (option " + info.Get(row, "optionID") + ") are also those of set " + string.Join(", ", ids.ToArray()) + ": a change of a bonus changes them too");
                }
                foreach (string t in found)
                {
                    List<string> where;
                    if (!from.TryGetValue(t, out where)) { from[t] = where = new List<string>(); order.Add(t); }
                    where.Add(Sides[i].Archive);
                }
            }
            foreach (string t in order) l.Add(from[t].Count == have ? t : from[t].Count == 1 && have == 1 ? t : string.Join(", ", from[t].ToArray()) + ": " + t);

            if (Differs(s, "itemIDs")) l.Add("the items (itemIDs) differ between the sides; the first side's are shown");
            foreach (SetPart p in Parts(s))
            {
                List<string> miss = MissingOn(s, p);
                if (miss.Count > 0) l.Add("bonus for " + p.Count + " items: not on " + string.Join(", ", miss.ToArray()));
                var diff = new List<string>();
                foreach (string f in ItemSetRules.Stats) if (Differs(p, f)) diff.Add(f);
                if (Differs(p, ItemSetRules.Effects)) diff.Add("effects");
                foreach (string f in ItemSetRules.Texts) if (Differs(p, f)) diff.Add(f);
                if (diff.Count > 0) l.Add("bonus for " + p.Count + " items: " + string.Join(", ", diff.ToArray()) + " differ" + (diff.Count == 1 ? "s" : "") + " between the sides; the first side's value is shown");
            }
            if (s.IsAdded)
            {
                long id = Id(s);
                if (id < ItemSetRules.From || id > ItemSetRules.To)
                    l.Add("new id " + id + " is outside of " + ItemSetRules.From + "-" + ItemSetRules.To + ", the range of new sets");
                if (Name(s).Length == 0) l.Add("the set has no name (string/{lang}/" + ItemSetRules.NameFile + ")");
            }
            List<string> missing = MissingOn(s);
            if (missing.Count > 0)
            {
                var haveOn = new List<string>();
                for (int i = 0; i < Sides.Count; i++) if (s.Rows[i] != null) haveOn.Add(Sides[i].Archive);
                l.Add("only on " + string.Join(", ", haveOn.ToArray()) + "; not on " + string.Join(", ", missing.ToArray()));
            }
            return l;
        }

        // ---- editing ----

        void NotDeleted(ItemSet s)
        {
            for (int i = 0; i < Sides.Count; i++)
                if (s.Rows[i] != null && s.Rows[i].IsDeleted)
                    throw new ModKitException("set " + Get(s, "id") + " is removed on " + Sides[i].Archive + "; restore it before a change");
        }

        /// <summary>Sets a value of the set row on every side that has the set (null removes the attribute).</summary>
        public void Set(ItemSet s, string field, string value)
        {
            if (field == "id" || field == "optionID" || field == "feature" || field == "locale")
                throw new ModKitException(field + " of a set is not changed here; " + (s.IsAdded ? "remove the new set and add it with the other id" : "clone the set for a new one"));
            NotDeleted(s);
            for (int i = 0; i < Sides.Count; i++)
                if (s.Rows[i] != null) Sides[i].Info.Set(s.Rows[i], field, value);
        }

        /// <summary>Sets the items of the set.</summary>
        public void SetMembers(ItemSet s, IEnumerable<long> ids)
        {
            var l = new List<string>();
            foreach (long id in ids)
            {
                if (id <= 0 || id > int.MaxValue) throw new ModKitException("item id " + id + " is not an item");
                string t = id.ToString(CultureInfo.InvariantCulture);
                if (l.Contains(t)) throw new ModKitException("item " + id + " is in the set already");
                l.Add(t);
            }
            Set(s, "itemIDs", string.Join(",", l.ToArray()));
        }

        static List<Bag> Bags(XmlElem holder)
        {
            var l = new List<Bag>();
            foreach (XmlElem c in holder.Children)
            {
                var b = new Bag { Name = c.Name };
                foreach (XmlAttr a in c.Attrs) b.Attrs.Add(new KeyValuePair<string, string>(a.Name, a.Value));
                l.Add(b);
            }
            return l;
        }

        static bool IsCell(AttrOp a) { return string.IsNullOrEmpty(a.At) && a.Kind != AttrOpKind.Text; }

        // a row the mod adds holds its parts in its own text: the text is written again with the change
        static void Rewrite(TableDraft d, TableRow holder, List<Bag> children)
        {
            foreach (AttrOp a in holder.Add.Attrs)
                if (!IsCell(a)) throw new ModKitException("the <add> of " + holder.Label + " has lines for nested rows or text; change its bonuses in the mod's text");
            var self = new Bag { Name = holder.Base.Name };
            foreach (XmlAttr a in holder.Base.Attrs) self.Set(a.Name, d.Get(holder, a.Name));
            foreach (AttrOp a in holder.Add.Attrs) self.Set(a.Name, d.Get(holder, a.Name));
            string text = self.Xml();
            if (children.Count > 0)
            {
                var sb = new StringBuilder(text.Substring(0, text.Length - 3) + ">\n");
                foreach (Bag b in children) sb.Append('\t').Append(b.Xml()).Append('\n');
                text = sb.Append("</").Append(self.Name).Append('>').ToString();
            }
            holder.Add.Xml = text;
            holder.Add.Copy = null;
            holder.Add.Attrs.Clear();
            holder.Base = XmlDoc.ParseFragment(text, "the new row").Root;
        }

        void Mutate(PartRef r, Action<Func<string, string>, Action<string, string>> change)
        {
            if (r.HRow.IsAdded)
            {
                List<Bag> bags = Bags(r.HRow.Base);
                change(bags[r.Index].Get, bags[r.Index].Set);
                Rewrite(r.HDraft, r.HRow, bags);
                return;
            }
            TableDraft nd = r.Draft;
            TableRow row = r.Row;
            if (row == null)
            {
                nd = NestedOf(Sides[r.Side], r.HDraft, r.HRow);
                row = nd.Rows[r.Index];
            }
            change(a => nd.Get(row, a), (a, v) => nd.Set(row, a, v));
        }

        /// <summary>Sets a field of a bonus (ItemSetRules.Stats, Texts, Effects, Count; "" for none) on every side that has the bonus.</summary>
        public void Set(ItemSet s, SetPart p, string field, string value)
        {
            NotDeleted(s);
            value = (value ?? "").Trim();
            if (field == ItemSetRules.Count)
            {
                int n;
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n <= 0) throw new ModKitException("\"" + value + "\" is not a number of items above 0");
                value = n.ToString(CultureInfo.InvariantCulture);
                foreach (SetPart o in Parts(s)) if (o.Count == value && o.Count != p.Count) throw new ModKitException("the set has a bonus for " + value + " items already");
            }
            else if (ItemSetRules.IsStat(field) && value.Length > 0)
            {
                long n;
                if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) throw new ModKitException(field + " \"" + value + "\" is not a whole number");
            }
            foreach (PartRef r in p.Refs)
            {
                if (r == null) continue;
                bool kms = Sides[r.Side].Kms;
                if (ItemSetRules.Read(r.Raw, kms, field) == value) continue;
                Mutate(r, (get, set) => ItemSetRules.Write(get, set, kms, field, value));
            }
        }

        /// <summary>
        /// A bonus for a count of worn items on every side that has the set's parts and no bonus of that count yet.
        /// A side that lacks a bonus the others have gets it with their values.
        /// </summary>
        public void AddPart(ItemSet s, int count)
        {
            NotDeleted(s);
            if (count <= 0) throw new ModKitException(count + " is not a number of items above 0");
            string c = count.ToString(CultureInfo.InvariantCulture);
            SetPart had = null;
            foreach (SetPart p in Parts(s)) if (p.Count == c) had = p;
            bool any = false;
            for (int i = 0; i < Sides.Count; i++)
            {
                TableDraft hd;
                TableRow hr;
                if ((had != null && had.Refs[i] != null) || !Holder(i, s, out hd, out hr)) continue;
                bool kms = Sides[i].Kms;
                var b = new Bag { Name = ItemSetRules.PartRows };
                b.Set(ItemSetRules.Count, c);
                ItemSetRules.Write(b.Get, b.Set, kms, ItemSetRules.Effects, had == null ? "" : Get(had, ItemSetRules.Effects));
                if (had != null)
                {
                    foreach (string f in ItemSetRules.Stats) ItemSetRules.Write(b.Get, b.Set, kms, f, Get(had, f));
                    foreach (string f in ItemSetRules.Texts) ItemSetRules.Write(b.Get, b.Set, kms, f, Get(had, f));
                }
                if (hr.IsAdded)
                {
                    List<Bag> bags = Bags(hr.Base);
                    bags.Add(b);
                    Rewrite(hd, hr, bags);
                }
                else NestedOf(Sides[i], hd, hr).AddRow(b.Xml());
                any = true;
            }
            if (!any) throw new ModKitException(had != null ? "the set has a bonus for " + c + " items already" : "no side has a row for the set's bonuses (see the checks)");
        }

        /// <summary>Removes a bonus on every side.</summary>
        public void RemovePart(ItemSet s, SetPart p)
        {
            NotDeleted(s);
            foreach (PartRef r in p.Refs)
            {
                if (r == null) continue;
                if (r.HRow.IsAdded)
                {
                    List<Bag> bags = Bags(r.HRow.Base);
                    bags.RemoveAt(r.Index);
                    Rewrite(r.HDraft, r.HRow, bags);
                }
                else if (r.Row != null) r.Draft.Delete(r.Row);
                else
                {
                    TableDraft nd = NestedOf(Sides[r.Side], r.HDraft, r.HRow);
                    nd.Delete(nd.Rows[r.Index]);
                }
            }
        }

        List<Mod> OtherMods()
        {
            var l = new List<Mod>();
            IEnumerable<Mod> mods = Others == null ? null : Others();
            if (mods != null) foreach (Mod m in mods) if (m != null && m != Mod && m.Id != Mod.Id) l.Add(m);
            return l;
        }

        void CheckNewId(long id)
        {
            if (id < ItemSetRules.From || id > ItemSetRules.To)
                throw new ModKitException("new set ids are " + ItemSetRules.From + " to " + ItemSetRules.To + "; " + id + " is outside");
            string t = id.ToString(CultureInfo.InvariantCulture);
            foreach (ItemSet o in Sets) if (Id(o) == id) throw new ModKitException("set " + id + " is there already");
            foreach (ItemSetSide side in Sides)
                if (side.OptionRow(t) != null) throw new ModKitException(side.Archive + " has an option " + id + " already");
            foreach (Mod m in OtherMods())
            {
                if (AddedIds(m).Contains(id)) throw new ModKitException("the enabled mod " + m.Id + " adds a set " + id + " already");
                foreach (IdRange r in m.Ids)
                    if (r.Space == ItemSetRules.Space && id >= r.From && id <= r.To)
                        throw new ModKitException("the set id " + id + " is reserved by the enabled mod " + m.Id);
            }
        }

        ItemSet Make(long newId, ItemSet source)
        {
            CheckNewId(newId);
            string id = newId.ToString(CultureInfo.InvariantCulture);
            List<SetPart> parts = source == null ? new List<SetPart>() : Parts(source);
            var made = new ItemSet(Sides.Count);
            for (int i = 0; i < Sides.Count; i++)
            {
                ItemSetSide side = Sides[i];
                var row = new Bag { Name = ItemSetRules.Rows };
                TableRow from = source == null ? null : source.Rows[i] ?? source.Shown;
                TableDraft fromDraft = source == null ? null : Sides[Array.IndexOf(source.Rows, from)].Info;
                row.Set("id", id);
                row.Set("itemIDs", from == null ? "" : fromDraft.Get(from, "itemIDs") ?? "");
                if (from != null)
                    foreach (string c in fromDraft.Columns)
                    {
                        // a set copied to a side that did not have it takes the attributes every side reads
                        if (c == "id" || c == "itemIDs" || c == "optionID") continue;
                        if (source.Rows[i] == null && c != "showEffectIfItsSetItemMotion" && c != "isDisableTooltip") continue;
                        string v = fromDraft.Get(from, c);
                        if (v != null) row.Set(c, v);
                    }
                if (!side.Kms || (source != null && source.Rows[i] != null && side.Info.Get(source.Rows[i], "optionID") != null)) row.Set("optionID", id);

                var bags = new List<Bag>();
                foreach (SetPart p in parts)
                {
                    var b = new Bag { Name = ItemSetRules.PartRows };
                    foreach (string f in new[] { ItemSetRules.Count, ItemSetRules.Effects }) ItemSetRules.Write(b.Get, b.Set, side.Kms, f, Get(p, f));
                    foreach (string f in ItemSetRules.Stats) ItemSetRules.Write(b.Get, b.Set, side.Kms, f, Get(p, f));
                    foreach (string f in ItemSetRules.Texts) ItemSetRules.Write(b.Get, b.Set, side.Kms, f, Get(p, f));
                    bags.Add(b);
                }
                made.Rows[i] = side.Info.AddRow(row.Xml());
                if (side.Kms) { if (bags.Count > 0) Rewrite(side.Info, made.Rows[i], bags); }
                else if (side.Options != null)
                {
                    TableRow opt = side.Options.AddRow("<" + ItemSetRules.OptionRows + " id=\"" + id + "\" />");
                    if (bags.Count > 0) Rewrite(side.Options, opt, bags);
                    side.OptionById = null;
                }
            }
            IdSpaces.Reserve(Mod, ItemSetRules.Space, newId);
            Sets.Add(made);
            return made;
        }

        /// <summary>A new set without items and bonuses on every side (on the server with an option of its id).</summary>
        public ItemSet Add(long newId) { return Make(newId, null); }

        /// <summary>A new set that copies source as it is shown (items, bonuses, name), on every side, with the new id and bonuses of its own.</summary>
        public ItemSet Clone(ItemSet source, long newId)
        {
            if (source.IsDeleted) throw new ModKitException("set " + Get(source, "id") + " is removed; restore it before copying");
            ItemSet made = Make(newId, source);
            string name = Name(source);
            if (name.Length > 0) SetName(made, name);
            return made;
        }

        void DropPartLines(int i, ItemSet s)
        {
            TableDraft hd, nd;
            TableRow hr;
            if (!Holder(i, s, out hd, out hr) || hr.IsAdded || !Sides[i].Nested.TryGetValue(hr, out nd)) return;
            foreach (TableRow r in nd.Rows.ToArray())
            {
                if (r.IsAdded) { nd.Delete(r); continue; }
                nd.Restore(r);
                nd.RevertCells(r);
            }
        }

        /// <summary>
        /// Removes a set: a new one goes away with its option and its name (its id is free again); a shipped one is
        /// deleted on every side. The server's option row and the name rows of a shipped set stay (nothing reads them
        /// without the set).
        /// </summary>
        public void Remove(ItemSet s)
        {
            bool added = s.IsAdded;
            long id = Id(s);
            if (added) SetName(s, "");
            for (int i = 0; i < Sides.Count; i++)
            {
                if (s.Rows[i] == null) continue;
                ItemSetSide side = Sides[i];
                if (side.Kms) DropPartLines(i, s);      // lines for the parts of a deleted row would not find them
                else if (s.Rows[i].IsAdded)
                {
                    TableDraft hd;
                    TableRow hr;
                    if (Holder(i, s, out hd, out hr) && hr.IsAdded && SharesOption(s, i).Count == 0) { hd.Delete(hr); side.OptionById = null; }
                }
                side.Info.Delete(s.Rows[i]);
            }
            if (added)
            {
                Sets.Remove(s);
                bool still = false;
                foreach (ItemSet o in Sets) if (Id(o) == id) still = true;
                if (!still) IdSpaces.Unreserve(Mod, ItemSetRules.Space, id);
            }
        }

        /// <summary>Takes back the removal of a shipped set.</summary>
        public void Restore(ItemSet s)
        {
            for (int i = 0; i < Sides.Count; i++) if (s.Rows[i] != null) Sides[i].Info.Restore(s.Rows[i]);
        }

        /// <summary>Drops every change the mod makes to a shipped set: its row, its bonuses and its name.</summary>
        public void Revert(ItemSet s)
        {
            if (s.IsAdded) throw new ModKitException("set " + Get(s, "id") + " is new; remove it instead");
            string id = Get(s, "id") ?? "";
            for (int i = 0; i < Sides.Count; i++)
            {
                if (s.Rows[i] == null) continue;
                DropPartLines(i, s);
                Sides[i].Info.Restore(s.Rows[i]);
                Sides[i].Info.RevertCells(s.Rows[i]);
            }
            foreach (NameTable n in names.Values)
            {
                TableRow r;
                if (n == null || !n.ById.TryGetValue(id, out r)) continue;
                if (r.IsAdded) { n.Draft.Delete(r); n.ById.Remove(id); }
                else n.Draft.RevertCells(r);
            }
        }

        /// <summary>The set ids a mod's &lt;add&gt; rows of the set table give (the id it sets, else the id of its row text).</summary>
        public static List<long> AddedIds(Mod mod)
        {
            var l = new List<long>();
            var inRow = new Regex(@"^\s*<\w+[^>]*?\sid\s*=\s*[""'](\d+)[""']");
            foreach (ModOp op in mod.Ops)
            {
                var ed = op as EditOp;
                if (ed == null || !IsEntry(ed, ItemSetRules.InfoEntry)) continue;
                foreach (RowOp r in ed.Rows)
                {
                    if (r.Kind != RowOpKind.Add) continue;
                    string id = null;
                    foreach (AttrOp a in r.Attrs) if (a.Kind == AttrOpKind.Set && a.Name == "id" && a.At == null) id = a.Value;
                    if (id == null && r.Xml != null) { Match mt = inRow.Match(r.Xml); if (mt.Success) id = mt.Groups[1].Value; }
                    long n;
                    if (id != null && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out n)) l.Add(n);
                }
            }
            return l;
        }

        /// <summary>The lowest free ids of the range of new sets: not shipped on any side and not taken by the mods.</summary>
        public List<long> NextIds(IEnumerable<Mod> mods, int count, Action<ProgressInfo> progress)
        {
            var all = new List<Mod>(mods ?? new Mod[0]);
            if (!all.Contains(Mod)) all.Add(Mod);
            var taken = new HashSet<long>();
            foreach (ItemSet s in Sets) taken.Add(Id(s));
            foreach (Mod m in all) if (m != Mod) foreach (long id in AddedIds(m)) taken.Add(id);
            List<long> free = IdSpaces.NextFreeGroupIds(Data, all, ItemSetRules.Space, count + taken.Count, progress);
            free.RemoveAll(taken.Contains);
            if (free.Count > count) free.RemoveRange(count, free.Count - count);
            return free;
        }
    }
}
