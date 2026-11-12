// Item stat options: the constant (fixed base stats) and static (fixed bonus lines) option rows an item names, per
// option code and grade (the item's rarity), and the random option lines (a pick of stats per grade).
//
// Who reads what:
//   client:Xml.m2d  table/itemoptionconstant.xml   <option code><rank grade? createType?><v name value/></rank>
//     (kms client)  CItemOptionTable::Init (@0x1405f4720) loads constant, random and variation; there is no static
//                   table. ItemOptionConstant::Load (@0x1405d2a90): a rank without grade is grade 0, createType 3 is
//                   Lua values (luaIndex), else <v name value> as int with value 0 dropped. FindConstantOption
//                   (@0x1405f5ee0) takes the rank with the highest grade at or below the item's grade. An item names
//                   its row by <option constantID> in itemdata/NNN.xml.
//   server:Xml.m2d  itemoption/constant/itemoptionconstant_*.xml and itemoption/option/static/itemoptionstatic_*.xml:
//     (and the      flat rows <option code grade X_value_base X_rate_base ...>. The server's ingest (Maple2.File.Ingest
//     global client) TableMapper, ItemOptionParser) reads the files ConstantFiles and StaticFiles name and keys rows by
//                   code and grade (a second row of one code and grade stops the ingest); the item names its rows by
//                   <option constant static> in item/<a>/<bb>/<id>.xml and the server looks a row up by code and the
//                   item's rarity, exact grade. A static row with optionNumPick empty or "0,0" is skipped.
//
//   client:Xml.m2d  table/itemoptionrandom.xml   <option code statGroup levelGroupID pickCount isRarePickOne?><v name statID weight?/>
//     (kms client)  ItemOptionRandom::Load (@0x1405d3d00): no grades; a <v> whose name is no stat or special is
//                   dropped, weight defaults to 1, statID names a row of table/itemoptionvariation.xml (the line's
//                   range is that row's first..last <v stat>, GetVariationRange @0x1405f63e0). GetPickCount
//                   (@0x1405d5fe0): with isRarePickOne a grade 2 item gets 1 line and a grade 1 item none, else
//                   pickCount. An item names its row by <option randomID>.
//   server:Xml.m2d  itemoption/option/random/itemoptionrandom_*.xml: the static layout (RandomFiles); the item names
//                   its rows by <option random>. The server picks a count in optionNumPick (capped at the stats
//                   listed) and takes that many of the row's stats, each as likely; it ignores the kms weights.
//
// The two layouts share the codes but not always the item's pointer: a kms item's constantID is often its pick id
// (optionID on the server), so the editor keeps the items naming a code per side.
//
// An edit is typed into one side; it is written to another side too where that side had the same value there (the
// field-map rule), else the plan warns that the sides differ. A kms row is replaced whole (a delete of the shipped
// row and an add of the new text); a flat row is edited cell by cell, and a grade row the code lacks is added.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public enum OptionKind { Constant, Static, Random }

    /// <summary>The names, files and value rules of the option tables.</summary>
    public static class ItemOptionRules
    {
        public const string Space = "itemoption";
        /// <summary>The option codes of mods: shipped codes end at 156010201.</summary>
        public const long From = 900100000, To = 900199999;
        public const string KmsEntry = "table/itemoptionconstant.xml", KmsRandomEntry = "table/itemoptionrandom.xml", KmsVariationEntry = "table/itemoptionvariation.xml";
        public const string ConstantDir = "itemoption/constant/", StaticDir = "itemoption/option/static/", RandomDir = "itemoption/option/random/";
        public const string ConstantPrefix = ConstantDir + "itemoptionconstant_", StaticPrefix = StaticDir + "itemoptionstatic_", RandomPrefix = RandomDir + "itemoptionrandom_";
        public const string ValueSuffix = "_value_base", RateSuffix = "_rate_base", Pick = "optionNumPick";
        public const int MaxGrade = 6;

        /// <summary>The constant files the server reads (ItemOptionParser).</summary>
        public static readonly string[] ConstantFiles = { "equip", "equip_fighter", "equip_pet", "equip_s2", "equipmanual", "etc", "gemstone", "mergematerial", "skin" };
        /// <summary>The static files the server reads (ItemOptionParser); 55 and 56 ship but are not read.</summary>
        public static readonly string[] StaticFiles =
        {
            "12", "13", "14", "15", "16", "17", "18", "19", "20", "21", "22", "30", "31", "32", "33", "34", "40", "41",
            "50", "51", "52", "53", "54", "armormanual", "mergematerial", "petequipment",
        };

        /// <summary>The random files the server reads (ItemOptionParser); itemoptionstatic_mergematerial.xml in the folder is not read.</summary>
        public static readonly string[] RandomFiles =
        {
            "12", "13", "14", "15", "16", "17", "18", "19", "20", "21", "22", "30", "31", "32", "33", "34", "40", "41",
            "50", "51", "52", "53", "54", "54_pvp", "55", "56", "accmanual", "armormanual", "pet_60", "weaponmanual",
        };

        public static readonly OptionKind[] Kinds = { OptionKind.Constant, OptionKind.Static, OptionKind.Random };

        public static string Dir(OptionKind k) { return k == OptionKind.Constant ? ConstantDir : k == OptionKind.Static ? StaticDir : RandomDir; }

        public static string KindName(OptionKind k) { return k == OptionKind.Constant ? "constant" : k == OptionKind.Static ? "static" : "random"; }

        /// <summary>The kms table of a kind; null for static (the kms client has none).</summary>
        public static string KmsEntryOf(OptionKind k) { return k == OptionKind.Constant ? KmsEntry : k == OptionKind.Random ? KmsRandomEntry : null; }

        /// <summary>True for a kms option table, with its kind.</summary>
        public static bool IsKmsEntry(string entry, out OptionKind kind)
        {
            string e = entry.Replace('\\', '/').ToLowerInvariant();
            kind = e == KmsRandomEntry ? OptionKind.Random : OptionKind.Constant;
            return e == KmsEntry || e == KmsRandomEntry;
        }

        /// <summary>The flat stats a kms random name stands for: X_value_base and X_rate_base.</summary>
        public static bool IsFlatOf(string kmsName, string flatName)
        {
            return string.Equals(flatName, kmsName + ValueSuffix, StringComparison.OrdinalIgnoreCase) || string.Equals(flatName, kmsName + RateSuffix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The kms name of a flat stat of either suffix ("nddincrease_rate_base" -> "nddincrease"); null for another name.</summary>
        public static string BaseName(string flatName)
        {
            string k = KmsName(flatName);
            if (k != null) return k;
            if (flatName == null || !flatName.EndsWith(RateSuffix, StringComparison.Ordinal) || flatName.Length == RateSuffix.Length) return null;
            return flatName.Substring(0, flatName.Length - RateSuffix.Length);
        }

        /// <summary>The lines a kms random row gives an item of a grade (GetPickCount): with isRarePickOne 1 at grade 2 and 0 at grade 1, else pickCount.</summary>
        public static int KmsPickCount(string pickCount, string rarePickOne, int grade)
        {
            if (Flag(rarePickOne))
            {
                if (grade == 2) return 1;
                if (grade == 1) return 0;
            }
            uint n;
            return uint.TryParse((pickCount ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n) && n <= int.MaxValue ? (int)n : 0;
        }

        // TinyXml's bool: "1" or "true" (any case)
        public static bool Flag(string v) { v = (v ?? "").Trim(); return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase); }

        /// <summary>The attributes of a kms random row the editor sets, and of its &lt;v&gt; lines.</summary>
        public static readonly string[] KmsRandomAttrs = { "pickCount", "isRarePickOne", "statGroup", "levelGroupID" };

        /// <summary>Why a value does not fit an attribute of a kms random row or line; null when it does ("" removes it).</summary>
        public static string CheckKmsRandom(string attr, string value)
        {
            if (string.IsNullOrEmpty(value) || value.Trim().Length == 0) return null;
            string v = value.Trim();
            uint n;
            switch (attr)
            {
                case "isRarePickOne":
                    // TinyXml's bool, as Flag reads it
                    return v == "0" || v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)
                        ? null : attr + ": \"" + value + "\" is not 0, 1, true or false";
                case "levelGroupID":
                    return IsInt(v) ? null : attr + ": \"" + value + "\" is no whole number";
                case "pickCount": case "statGroup": case "statID": case "weight": case "probVer":
                    if (!uint.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out n)) return attr + ": \"" + value + "\" is no whole number of 0 or more (the client reads an unsigned int)";
                    if (attr == "weight" && n == 0) return "weight: 0 never picks the line (leave it empty for 1)";
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>The kind of a flat option file, by its folder; false for another entry.</summary>
        public static bool IsFlatEntry(string entry, out OptionKind kind)
        {
            string e = entry.Replace('\\', '/').ToLowerInvariant();
            kind = OptionKind.Constant;
            if (!e.EndsWith(".xml", StringComparison.Ordinal)) return false;
            if (e.StartsWith(ConstantDir, StringComparison.Ordinal) && e.IndexOf('/', ConstantDir.Length) < 0) return true;
            kind = OptionKind.Random;
            if (e.StartsWith(RandomDir, StringComparison.Ordinal) && e.IndexOf('/', RandomDir.Length) < 0) return true;
            kind = OptionKind.Static;
            return e.StartsWith(StaticDir, StringComparison.Ordinal) && e.IndexOf('/', StaticDir.Length) < 0;
        }

        /// <summary>True when the server's ingest reads the flat file.</summary>
        public static bool ServerReads(OptionKind kind, string entry)
        {
            string e = entry.Replace('\\', '/').ToLowerInvariant();
            string prefix = kind == OptionKind.Constant ? ConstantPrefix : kind == OptionKind.Static ? StaticPrefix : RandomPrefix;
            foreach (string f in kind == OptionKind.Constant ? ConstantFiles : kind == OptionKind.Static ? StaticFiles : RandomFiles)
                if (e == prefix + f + ".xml") return true;
            return false;
        }

        /// <summary>The flat name of a kms stat ("mar" -> "mar_value_base").</summary>
        public static string FlatName(string kmsName) { return kmsName + ValueSuffix; }

        /// <summary>The kms name of a flat stat ("mar_value_base" -> "mar"); null for a name kms has no &lt;v&gt; for.</summary>
        public static string KmsName(string flatName)
        {
            if (flatName == null || !flatName.EndsWith(ValueSuffix, StringComparison.Ordinal) || flatName.Length == ValueSuffix.Length) return null;
            return flatName.Substring(0, flatName.Length - ValueSuffix.Length);
        }

        /// <summary>True for the attributes of a flat row that are no stat cells (the row's key).</summary>
        public static bool IsKey(string attr) { return attr == "code" || attr == "grade"; }

        /// <summary>A value as the sides compare it: trimmed, and "0" (no stat on either layout) as "".</summary>
        public static string Normal(string v)
        {
            if (v == null) return "";
            v = v.Trim();
            if (v == "0" || v == "0,0") return "";
            return v;
        }

        static bool IsInt(string s)
        {
            long n;
            return long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n) && n >= int.MinValue && n <= int.MaxValue;
        }

        static bool IsFloat(string s)
        {
            double d;
            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !double.IsNaN(d) && !double.IsInfinity(d);
        }

        static bool IsPair(string s, bool ints, out double lo, out double hi)
        {
            lo = hi = 0;
            string[] p = s.Split(',');
            if (p.Length != 2) return false;
            if (ints ? !(IsInt(p[0]) && IsInt(p[1])) : !(IsFloat(p[0]) && IsFloat(p[1]))) return false;
            lo = double.Parse(p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
            hi = double.Parse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>
        /// Why a value does not fit a cell; null when it does. An empty value removes the stat and always fits.
        /// kms: an int. Flat constant: X_value_base an int, X_rate_base a number. Flat static and random: "v" or "min,max" (ints for
        /// a value, numbers for a rate). optionNumPick: "min,max", ints, 0 &lt;= min &lt;= max. Other attributes are text.
        /// </summary>
        public static string CheckValue(OptionKind kind, bool kms, string stat, string value)
        {
            if (string.IsNullOrEmpty(value) || value.Trim().Length == 0) return null;
            string v = value.Trim();
            double lo, hi;
            if (kms) return IsInt(v) ? null : stat + ": \"" + value + "\" is no whole number (the client reads an int)";
            if (stat == Pick)
            {
                if (!IsPair(v, true, out lo, out hi)) return Pick + ": \"" + value + "\" is not \"min,max\" (whole numbers)";
                if (lo < 0 || lo > hi) return Pick + ": \"" + value + "\": min must be 0 or more and not above max";
                return null;
            }
            bool isValue = stat.EndsWith(ValueSuffix, StringComparison.Ordinal), isRate = stat.EndsWith(RateSuffix, StringComparison.Ordinal);
            if (!isValue && !isRate) return null;
            if (kind == OptionKind.Constant)
            {
                if (isValue) return IsInt(v) ? null : stat + ": \"" + value + "\" is no whole number";
                return IsFloat(v) ? null : stat + ": \"" + value + "\" is no number";
            }
            if (isValue ? IsInt(v) : IsFloat(v)) return null;
            if (IsPair(v, isValue, out lo, out hi)) return lo <= hi ? null : stat + ": \"" + value + "\": min is above max";
            return stat + ": \"" + value + "\" is not a " + (isValue ? "whole number" : "number") + " or \"min,max\"";
        }
    }

    /// <summary>A side of the editor: the kms option table, or the flat rows of one kind in one archive.</summary>
    public sealed class OptionSide
    {
        public string Archive;
        public bool Kms;
        public OptionKind Kind;
        /// <summary>"kms client", "server", "global client".</summary>
        public string Title;

        public string Key { get { return Archive + "\n" + ItemOptionRules.KindName(Kind); } }

        public override string ToString() { return Title; }
    }

    /// <summary>What one cell shows: the value of a stat at a grade on a side, and where it comes from.</summary>
    public sealed class OptionCell
    {
        /// <summary>The value as the side has it after the mod; "" for none.</summary>
        public string Value = "";
        /// <summary>The value of the shipped data; "" for none (and for a code the mod adds).</summary>
        public string Shipped = "";
        /// <summary>Where the value comes from: "rank grade 3 (grades 3-4)", "row grade 2 in itemoptionconstant_equip.xml".</summary>
        public string Source = "";
        /// <summary>Why the cell cannot be edited; null when it can.</summary>
        public string ReadOnly;
        /// <summary>The grades a kms rank serves (for a flat cell its own grade).</summary>
        public readonly List<int> Grades = new List<int>();
        public bool Edited { get { return ItemOptionRules.Normal(Value) != ItemOptionRules.Normal(Shipped); } }
    }

    /// <summary>One line of the code picker.</summary>
    public sealed class OptionCodeInfo
    {
        public long Code;
        public OptionKind Kind;
        /// <summary>The items naming the code, on any side (as shipped, and the mod's items).</summary>
        public readonly List<long> Items = new List<long>();
        /// <summary>The grades with a row on some side.</summary>
        public readonly SortedDictionary<int, bool> Grades = new SortedDictionary<int, bool>();
        /// <summary>The sides with rows, by title.</summary>
        public readonly List<string> Sides = new List<string>();
        /// <summary>The flat files the code's rows are in (entry names, without the folder).</summary>
        public readonly List<string> Files = new List<string>();
        public bool Changed, Added;

        public string GradeText
        {
            get
            {
                var l = new List<string>();
                foreach (int g in Grades.Keys) l.Add(g == 0 ? "any" : g.ToString(CultureInfo.InvariantCulture));
                return string.Join(",", l.ToArray());
            }
        }
    }

    /// <summary>A kms &lt;option&gt; row as a tree the editor can change and write out again.</summary>
    sealed class OptNode
    {
        internal string Name;
        internal readonly List<KeyValuePair<string, string>> Attrs = new List<KeyValuePair<string, string>>();
        internal readonly List<OptNode> Children = new List<OptNode>();
        /// <summary>The element's text for one with text content (kept as it is).</summary>
        internal string Raw;

        internal string Attr(string n)
        {
            foreach (KeyValuePair<string, string> a in Attrs) if (a.Key == n) return a.Value;
            return null;
        }

        internal void SetAttr(string n, string v)
        {
            for (int i = 0; i < Attrs.Count; i++)
                if (Attrs[i].Key == n) { Attrs[i] = new KeyValuePair<string, string>(n, v); return; }
            Attrs.Add(new KeyValuePair<string, string>(n, v));
        }

        internal static OptNode Of(XmlElem e)
        {
            var n = new OptNode { Name = e.Name };
            foreach (XmlAttr a in e.Attrs) n.Attrs.Add(new KeyValuePair<string, string>(a.Name, a.Value));
            if (e.Children.Count == 0 && !e.SelfClosing && e.InnerText.Trim().Length > 0) n.Raw = e.OuterText;
            foreach (XmlElem c in e.Children) n.Children.Add(Of(c));
            return n;
        }

        internal OptNode Copy()
        {
            var n = new OptNode { Name = Name, Raw = Raw };
            n.Attrs.AddRange(Attrs);
            foreach (OptNode c in Children) n.Children.Add(c.Copy());
            return n;
        }

        internal bool Same(OptNode o)
        {
            if (o == null || Name != o.Name || Raw != o.Raw || Attrs.Count != o.Attrs.Count || Children.Count != o.Children.Count) return false;
            for (int i = 0; i < Attrs.Count; i++) if (Attrs[i].Key != o.Attrs[i].Key || Attrs[i].Value != o.Attrs[i].Value) return false;
            for (int i = 0; i < Children.Count; i++) if (!Children[i].Same(o.Children[i])) return false;
            return true;
        }

        internal void Write(StringBuilder sb, string indent, string nl)
        {
            sb.Append(indent);
            if (Raw != null) { sb.Append(Raw).Append(nl); return; }
            sb.Append('<').Append(Name);
            foreach (KeyValuePair<string, string> a in Attrs) sb.Append(' ').Append(a.Key).Append("=\"").Append(XmlDoc.EscapeAttr(a.Value, '"')).Append('"');
            if (Children.Count == 0) { sb.Append(" />").Append(nl); return; }
            sb.Append('>').Append(nl);
            foreach (OptNode c in Children) c.Write(sb, indent + "\t", nl);
            sb.Append(indent).Append("</").Append(Name).Append('>').Append(nl);
        }

        internal string Text()
        {
            var sb = new StringBuilder();
            Write(sb, "", "\r\n");
            return sb.ToString().TrimEnd();
        }

        // ---- ranks ----

        internal static int GradeOf(OptNode rank)
        {
            int g;
            return int.TryParse(rank.Attr("grade") ?? "", NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out g) ? g : 0;
        }

        internal static bool IsLua(OptNode rank) { return (rank.Attr("createType") ?? "").Trim() == "3"; }

        internal List<OptNode> Ranks()
        {
            var l = new List<OptNode>();
            foreach (OptNode c in Children) if (c.Name == "rank") l.Add(c);
            return l;
        }

        /// <summary>The rank the client takes for an item of grade g (FindConstantOption): the highest grade at or below g.</summary>
        internal OptNode RankFor(int g)
        {
            OptNode best = null;
            int bestGrade = int.MinValue;
            foreach (OptNode r in Ranks())
            {
                int rg = GradeOf(r);
                if (rg <= g && rg >= bestGrade) { best = r; bestGrade = rg; }
            }
            return best;
        }

        /// <summary>The grades 1..MaxGrade a rank serves.</summary>
        internal List<int> Served(OptNode rank)
        {
            var l = new List<int>();
            for (int g = 1; g <= ItemOptionRules.MaxGrade; g++) if (RankFor(g) == rank) l.Add(g);
            return l;
        }

        internal static string V(OptNode rank, string name)
        {
            if (rank == null) return "";
            foreach (OptNode v in rank.Children) if (v.Name == "v" && v.Attr("name") == name) return v.Attr("value") ?? "";
            return "";
        }
    }

    /// <summary>A &lt;v&gt; line of a kms random row: a stat the client may pick.</summary>
    public sealed class KmsRandomLine
    {
        public string Name = "", StatId = "", Weight = "";
        /// <summary>The range the client rolls (the statID's row of the variation table); HasRange false when it has no row.</summary>
        public string First = "", Last = "";
        public bool HasRange;
        public string RangeText { get { return !HasRange ? "" : First == Last ? First : First + ".." + Last; } }
    }

    /// <summary>A kms random row as the editor shows it.</summary>
    public sealed class KmsRandomRow
    {
        public long Code;
        /// <summary>The row's attributes but code, in file order (pickCount, isRarePickOne, statGroup, levelGroupID, copyID ...).</summary>
        public readonly List<KeyValuePair<string, string>> Attrs = new List<KeyValuePair<string, string>>();
        public readonly List<KmsRandomLine> Lines = new List<KmsRandomLine>();
        /// <summary>True when the mod changed or added the row.</summary>
        public bool Edited;

        public string Attr(string name)
        {
            foreach (KeyValuePair<string, string> a in Attrs) if (a.Key == name) return a.Value;
            return "";
        }

        /// <summary>The lines an item of a grade gets (GetPickCount).</summary>
        public int PickCount(int grade) { return ItemOptionRules.KmsPickCount(Attr("pickCount"), Attr("isRarePickOne"), grade); }
    }

    public sealed class ItemOptionEditor
    {
        public readonly DataSet Data;
        public readonly Mod Mod;
        /// <summary>The other mods (for the new code check): reserved ranges and added codes.</summary>
        public Func<IEnumerable<Mod>> Others;
        /// <summary>The flat archives: the server's, and the global client's when the data set has no server or the mod has lines there.</summary>
        public readonly List<string> FlatArchives = new List<string>();
        /// <summary>The kms client's archive with the option table; null when the data set has none.</summary>
        public readonly string KmsArchive;
        /// <summary>True when the kms archive has the constant table, and the random table.</summary>
        public readonly bool KmsConstant, KmsRandom;

        readonly Dictionary<string, TableDraft> drafts = new Dictionary<string, TableDraft>(StringComparer.OrdinalIgnoreCase);
        // code -> rows of one flat draft (built at first use, dropped at each write)
        readonly Dictionary<TableDraft, Dictionary<string, List<TableRow>>> byCode = new Dictionary<TableDraft, Dictionary<string, List<TableRow>>>();
        ShippedIndex index;

        public ItemOptionEditor(DataSet data, Mod mod)
        {
            if (mod == null) throw new ArgumentNullException("mod");
            Data = data;
            Mod = mod;
            Others = delegate { return new Mod[0]; };
            if (data.Has(Profile.KmsClient))
            {
                string a = Profile.KmsClient.ArchiveIn("x:Xml.m2d");
                M2dArchive arch = TryOpen(a);
                if (arch != null)
                {
                    KmsConstant = arch.Find(ItemOptionRules.KmsEntry) != null;
                    KmsRandom = arch.Find(ItemOptionRules.KmsRandomEntry) != null;
                    if (KmsConstant || KmsRandom) KmsArchive = a;
                }
            }
            foreach (Profile p in new[] { Profile.Server, Profile.GmsClient })
            {
                if (!data.Has(p)) continue;
                string a = p.ArchiveIn("x:Xml.m2d");
                if (p == Profile.GmsClient && data.Has(Profile.Server) && !HasLines(mod, a)) continue;
                M2dArchive arch = TryOpen(a);
                if (arch == null) continue;
                bool any = false;
                foreach (M2dEntry e in arch.Entries) { OptionKind k; if (ItemOptionRules.IsFlatEntry(e.Name, out k)) { any = true; break; } }
                if (any) FlatArchives.Add(a);
            }
            if (KmsArchive == null && FlatArchives.Count == 0) throw new ModKitException("no target of the data set has item option tables");
        }

        M2dArchive TryOpen(string a)
        {
            try { return Data.Open(a); }
            catch (ModKitException) { return null; }
        }

        static bool HasLines(Mod mod, string archive)
        {
            foreach (ModOp op in mod.Ops)
            {
                var ed = op as EditOp;
                OptionKind k;
                if (ed != null && string.Equals(ed.Archive, archive, StringComparison.OrdinalIgnoreCase) && ItemOptionRules.IsFlatEntry(ed.Entry, out k)) return true;
            }
            return false;
        }

        public static string TitleOf(string archive)
        {
            switch (Profile.OfArchive(archive) == null ? "" : Profile.OfArchive(archive).Root)
            {
                case "client": return "kms client";
                case "server": return "server";
                default: return Profile.OfArchive(archive) == null ? archive : Profile.OfArchive(archive).Title;
            }
        }

        /// <summary>The sides of a kind: the kms table (constant and random; kms has no static), then the flat archives.</summary>
        public List<OptionSide> Sides(OptionKind kind)
        {
            var l = new List<OptionSide>();
            if (kind == OptionKind.Constant ? KmsConstant : kind == OptionKind.Random && KmsRandom) l.Add(new OptionSide { Archive = KmsArchive, Kms = true, Kind = kind, Title = "kms client" });
            foreach (string a in FlatArchives) l.Add(new OptionSide { Archive = a, Kind = kind, Title = TitleOf(a) });
            return l;
        }

        // ---- the shipped index ----

        sealed class ShippedIndex
        {
            // side key -> code -> entries the code's rows are in (kms: the table)
            internal readonly Dictionary<string, Dictionary<long, List<string>>> Codes = new Dictionary<string, Dictionary<long, List<string>>>();
            // side key -> code -> grades with a row (kms: rank grades, 0 for a rank without one)
            internal readonly Dictionary<string, Dictionary<long, SortedDictionary<int, bool>>> Grades = new Dictionary<string, Dictionary<long, SortedDictionary<int, bool>>>();
            // side key -> stat names the side's files use (flat: attribute names; kms constant: X_value_base for
            // <v name=X>; kms random: the <v name> itself, one name stands for X_value_base and X_rate_base)
            internal readonly Dictionary<string, SortedDictionary<string, bool>> Stats = new Dictionary<string, SortedDictionary<string, bool>>();
            // side key -> code -> items naming it as shipped
            internal readonly Dictionary<string, Dictionary<long, List<long>>> Items = new Dictionary<string, Dictionary<long, List<long>>>();
            // side key -> item -> the code it names as shipped
            internal readonly Dictionary<string, Dictionary<long, long>> ItemCode = new Dictionary<string, Dictionary<long, long>>();
            internal readonly List<string> Skipped = new List<string>();
        }

        static T Get<K, T>(Dictionary<K, T> d, K key) where T : new()
        {
            T v;
            if (!d.TryGetValue(key, out v)) d[key] = v = new T();
            return v;
        }

        ShippedIndex Index()
        {
            if (index != null) return index;
            string key = "itemoptions\n" + (KmsArchive ?? "") + "\n" + string.Join("\n", FlatArchives.ToArray());
            object o;
            if (Data.Cache.TryGetValue(key, out o)) return index = (ShippedIndex)o;
            var ix = new ShippedIndex();
            if (KmsConstant)
            {
                string side = KmsArchive + "\nconstant";
                M2dArchive a = Data.Open(KmsArchive);
                XmlDoc d = XmlDoc.Parse(a.Read(a.Find(ItemOptionRules.KmsEntry)), KmsArchive + " " + ItemOptionRules.KmsEntry);
                Dictionary<long, List<string>> codes = Get(ix.Codes, side);
                Dictionary<long, SortedDictionary<int, bool>> grades = Get(ix.Grades, side);
                SortedDictionary<string, bool> stats = Get(ix.Stats, side);
                foreach (XmlElem row in d.Root.Children)
                {
                    long code;
                    if (row.Name != "option" || !TryCode(row.Attr("code"), out code)) continue;
                    Get(codes, code).Add(ItemOptionRules.KmsEntry);
                    foreach (XmlElem r in row.Children)
                    {
                        if (r.Name != "rank") continue;
                        int g;
                        if (!int.TryParse(r.Attr("grade") ?? "0", NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out g)) g = 0;
                        Get(grades, code)[g] = true;
                        foreach (XmlElem v in r.Children) if (v.Name == "v" && v.Attr("name") != null) stats[ItemOptionRules.FlatName(v.Attr("name"))] = true;
                    }
                }
            }
            if (KmsRandom)
            {
                // no grades: a row serves every grade
                string side = KmsArchive + "\nrandom";
                M2dArchive a = Data.Open(KmsArchive);
                XmlDoc d = XmlDoc.Parse(a.Read(a.Find(ItemOptionRules.KmsRandomEntry)), KmsArchive + " " + ItemOptionRules.KmsRandomEntry);
                Dictionary<long, List<string>> codes = Get(ix.Codes, side);
                SortedDictionary<string, bool> stats = Get(ix.Stats, side);
                if (d.Root != null)
                    foreach (XmlElem row in d.Root.Children)
                    {
                        long code;
                        if (row.Name != "option" || !TryCode(row.Attr("code"), out code)) continue;
                        Get(codes, code).Add(ItemOptionRules.KmsRandomEntry);
                        foreach (XmlElem v in row.Children) if (v.Name == "v" && v.Attr("name") != null) stats[v.Attr("name")] = true;
                    }
            }
            foreach (string archive in FlatArchives)
            {
                M2dArchive a = Data.Open(archive);
                foreach (M2dEntry e in a.Entries)
                {
                    OptionKind k;
                    if (!ItemOptionRules.IsFlatEntry(e.Name, out k)) continue;
                    string side = archive + "\n" + ItemOptionRules.KindName(k);
                    XmlDoc d;
                    try { d = XmlDoc.Parse(a.Read(e), archive + " " + e.Name); }
                    catch (ModKitException ex) { ix.Skipped.Add(archive + " " + e.Name + ": " + ex.Message); continue; }
                    if (d.Root == null) continue;
                    Dictionary<long, List<string>> codes = Get(ix.Codes, side);
                    Dictionary<long, SortedDictionary<int, bool>> grades = Get(ix.Grades, side);
                    SortedDictionary<string, bool> stats = Get(ix.Stats, side);
                    foreach (XmlElem row in d.Root.Children)
                    {
                        long code;
                        int g;
                        if (row.Name != "option" || !TryCode(row.Attr("code"), out code)) continue;
                        List<string> files = Get(codes, code);
                        if (!files.Contains(e.Name)) files.Add(e.Name);
                        if (int.TryParse(row.Attr("grade") ?? "", NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out g)) Get(grades, code)[g] = true;
                        foreach (XmlAttr at in row.Attrs) if (!ItemOptionRules.IsKey(at.Name)) stats[at.Name] = true;
                    }
                }
            }
            // the items: kms constantID and randomID, flat constant, static and random
            var items = new ItemEditor(Data, new Mod { Id = "itemoptions-scan" });
            Action<long, XmlElem> visit = delegate(long id, XmlElem root)
            {
                bool bundle = root.Name == "item";
                foreach (XmlElem opt in Options(root))
                {
                    if (bundle)
                    {
                        if (KmsConstant) Name(ix, KmsArchive + "\nconstant", opt.Attr("constantID"), id);
                        if (KmsRandom) Name(ix, KmsArchive + "\nrandom", opt.Attr("randomID"), id);
                    }
                    else if (items.FileXml != null && FlatArchives.Contains(items.FileXml))
                    {
                        // the per-id item files are read from one archive (the server's): its pointers count for that
                        // archive's option files only, not for another flat side's
                        Name(ix, items.FileXml + "\nconstant", opt.Attr("constant"), id);
                        Name(ix, items.FileXml + "\nstatic", opt.Attr("static"), id);
                        Name(ix, items.FileXml + "\nrandom", opt.Attr("random"), id);
                    }
                }
            };
            items.ScanShipped(visit, delegate(string file, Exception ex) { ix.Skipped.Add(file + ": " + ex.Message); });
            Data.Cache[key] = ix;
            return index = ix;
        }

        static void Name(ShippedIndex ix, string side, string code, long item)
        {
            long c;
            if (!TryCode(code, out c) || c == 0) return;
            Dictionary<long, long> had = Get(ix.ItemCode, side);
            if (had.ContainsKey(item)) return;
            had[item] = c;
            Get(Get(ix.Items, side), c).Add(item);
        }

        static IEnumerable<XmlElem> Options(XmlElem e)
        {
            foreach (XmlElem c in e.Children)
            {
                if (c.Name == "option") yield return c;
                else foreach (XmlElem d in Options(c)) yield return d;
            }
        }

        internal static bool TryCode(string s, out long code)
        {
            code = 0;
            return s != null && long.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out code);
        }

        /// <summary>The files skipped while the shipped data was read (a parse error), for the view to say.</summary>
        public List<string> Skipped { get { return Index().Skipped; } }

        /// <summary>The stat names a side's shipped files use (flat attribute names; kms constant as X_value_base; kms random the &lt;v name&gt;), sorted.</summary>
        public List<string> StatNames(OptionSide side)
        {
            SortedDictionary<string, bool> s;
            var l = new List<string>();
            if (Index().Stats.TryGetValue(side.Key, out s)) l.AddRange(s.Keys);
            if (!side.Kms && !l.Contains(ItemOptionRules.Pick) && side.Kind != OptionKind.Constant) l.Add(ItemOptionRules.Pick);
            return l;
        }

        // ---- drafts ----

        TableDraft Draft(string archive, string entry)
        {
            string k = archive + "\n" + entry;
            TableDraft d;
            if (!drafts.TryGetValue(k, out d))
            {
                drafts[k] = d = new TableDraft(Data, Mod, archive, entry, null);
                // the global client a side of its own: the editor keeps each side as it is shown, Plan mirrors nothing
                d.NoMirror = SidesApart;
            }
            return d;
        }

        /// <summary>True when the server and the global client are both sides: their edits carry mirror="false".</summary>
        public bool SidesApart { get { return FlatArchives.Count > 1; } }

        TableDraft KmsDraft(OptionKind kind)
        {
            if (kind == OptionKind.Constant ? !KmsConstant : kind != OptionKind.Random || !KmsRandom) return null;
            return Draft(KmsArchive, ItemOptionRules.KmsEntryOf(kind));
        }

        Dictionary<string, List<TableRow>> RowsByCode(TableDraft d)
        {
            Dictionary<string, List<TableRow>> m;
            if (byCode.TryGetValue(d, out m)) return m;
            m = new Dictionary<string, List<TableRow>>(StringComparer.Ordinal);
            foreach (TableRow r in d.Rows)
            {
                if (r.Base.Name != "option") continue;
                string c = d.Get(r, "code");
                if (c == null) continue;
                Get(m, c.Trim()).Add(r);
            }
            byCode[d] = m;
            return m;
        }

        void Changed(TableDraft d) { byCode.Remove(d); }

        static string CodeText(long code) { return code.ToString(CultureInfo.InvariantCulture); }

        /// <summary>The flat entries of a side that hold the code: the shipped files, then files where the mod adds it.</summary>
        List<string> EntriesOf(OptionSide side, long code)
        {
            var l = new List<string>();
            Dictionary<long, List<string>> codes;
            List<string> files;
            if (Index().Codes.TryGetValue(side.Key, out codes) && codes.TryGetValue(code, out files)) l.AddRange(files);
            foreach (string e in ModEntries(side))
                if (!l.Contains(e))
                {
                    List<TableRow> rows;
                    if (RowsByCode(Draft(side.Archive, e)).TryGetValue(CodeText(code), out rows) && rows.Count > 0) l.Add(e);
                }
            return l;
        }

        /// <summary>The entries of a side the mod has edits for.</summary>
        List<string> ModEntries(OptionSide side)
        {
            var l = new List<string>();
            foreach (ModOp op in Mod.Ops)
            {
                var ed = op as EditOp;
                if (ed == null || !string.Equals(ed.Archive, side.Archive, StringComparison.OrdinalIgnoreCase)) continue;
                OptionKind k;
                string entry = ed.Entry.Replace('\\', '/');
                if (side.Kms ? string.Equals(entry, ItemOptionRules.KmsEntryOf(side.Kind), StringComparison.OrdinalIgnoreCase)
                    : ItemOptionRules.IsFlatEntry(entry, out k) && k == side.Kind)
                {
                    M2dEntry me = Data.Open(side.Archive).Find(entry);
                    string name = me == null ? entry : me.Name;
                    if (!l.Contains(name)) l.Add(name);
                }
            }
            return l;
        }

        // the kms rows of a code: the shipped one (maybe deleted) and the mod's replacement
        void KmsRows(OptionKind kind, long code, out TableRow shipped, out TableRow added)
        {
            shipped = added = null;
            TableDraft d = KmsDraft(kind);
            if (d == null) return;
            List<TableRow> rows;
            if (!RowsByCode(d).TryGetValue(CodeText(code), out rows)) return;
            foreach (TableRow r in rows)
            {
                if (r.IsAdded) added = r;
                else if (shipped == null) shipped = r;
            }
        }

        OptNode KmsNode(TableRow r)
        {
            if (r == null) return null;
            // the row as its add or change leaves it: every attribute op, on the row and under it (a hand edit of a
            // <v> or a <rank>), the way the plan applies them
            RowOp op = r.Add ?? r.Change;
            if (op == null || op.Attrs.Count == 0) return OptNode.Of(r.Base);
            XmlDoc f = XmlDoc.ParseFragment(r.Base.OuterText, "the kms option row");
            var ed = new XmlEditor(f);
            foreach (AttrOp a in op.Attrs)
            {
                XmlElem el = f.Root;
                if (!string.IsNullOrEmpty(a.At))
                {
                    string error;
                    try { el = RowPath.Parse(a.At).TryResolve(f.Root, out error); }
                    catch (ModKitException) { el = null; }
                }
                if (el == null) continue;
                if (a.Kind == AttrOpKind.Set) ed.SetAttr(el, a.Name, a.Value);
                else if (a.Kind == AttrOpKind.Unset) ed.RemoveAttr(el, a.Name);
                else ed.SetText(el, a.Value);
            }
            return OptNode.Of(XmlDoc.ParseFragment(ed.Apply(), "the kms option row").Root);
        }

        /// <summary>The kms row of a code as the mod leaves it; null when the side has none.</summary>
        OptNode KmsNow(OptionKind kind, long code)
        {
            TableRow shipped, added;
            KmsRows(kind, code, out shipped, out added);
            if (added != null) return KmsNode(added);
            if (shipped != null && !shipped.IsDeleted) return KmsNode(shipped);
            return null;
        }

        OptNode KmsShipped(OptionKind kind, long code)
        {
            TableRow shipped, added;
            KmsRows(kind, code, out shipped, out added);
            return shipped == null ? null : OptNode.Of(shipped.Original);
        }

        // ---- reading ----

        /// <summary>True when a side has the code (shipped or added by the mod).</summary>
        public bool Has(OptionSide side, long code)
        {
            if (side.Kms) return KmsNow(side.Kind, code) != null;
            return EntriesOf(side, code).Count > 0;
        }

        /// <summary>The flat row of a code and grade on a side, and the file it is in; null when there is none.</summary>
        TableRow FlatRow(OptionSide side, long code, int grade, out TableDraft draft)
        {
            draft = null;
            string g = grade.ToString(CultureInfo.InvariantCulture);
            // a row in a file the server reads first: a grade in two files is edited where it counts
            List<string> entries = EntriesOf(side, code);
            var ordered = new List<string>();
            foreach (string e in entries) if (ItemOptionRules.ServerReads(side.Kind, e)) ordered.Add(e);
            foreach (string e in entries) if (!ItemOptionRules.ServerReads(side.Kind, e)) ordered.Add(e);
            foreach (string e in ordered)
            {
                TableDraft d = Draft(side.Archive, e);
                List<TableRow> rows;
                if (!RowsByCode(d).TryGetValue(CodeText(code), out rows)) continue;
                foreach (TableRow r in rows)
                    if (!r.IsDeleted && (d.Get(r, "grade") ?? "").Trim() == g) { draft = d; return r; }
            }
            return null;
        }

        /// <summary>The cell of a stat at a grade on a side. Flat stat names are used for every side (kms "mar" is "mar_value_base").</summary>
        public OptionCell Cell(OptionSide side, long code, int grade, string stat)
        {
            var c = new OptionCell();
            if (side.Kms && side.Kind == OptionKind.Random)
            {
                // no grades: the row's pick count at the grade, and a line's range; both are edited in the random detail
                OptNode now = KmsNow(side.Kind, code), was = KmsShipped(side.Kind, code);
                c.Shipped = RandomCell(was, grade, stat);
                if (now == null) { c.ReadOnly = "the kms client has no row " + CodeText(code); return c; }
                for (int g = 1; g <= ItemOptionRules.MaxGrade; g++) c.Grades.Add(g);
                c.Value = RandomCell(now, grade, stat);
                c.Source = stat == ItemOptionRules.Pick ? "pickCount " + (now.Attr("pickCount") ?? "") + (ItemOptionRules.KmsPickCount("1", now.Attr("isRarePickOne"), 1) == 0 ? ", isRarePickOne (grade 1: 0, grade 2: 1)" : "")
                    : c.Value.Length == 0 ? "no line of the stat" : "the line's statID range (every grade)";
                c.ReadOnly = "a kms random row has no grades: edit it in the random detail";
                return c;
            }
            if (side.Kms)
            {
                string name = ItemOptionRules.KmsName(stat);
                if (name == null) { c.ReadOnly = "the kms client has no " + stat; return c; }
                OptNode now = KmsNow(side.Kind, code), was = KmsShipped(side.Kind, code);
                if (was != null) c.Shipped = OptNode.V(was.RankFor(grade), name);
                if (now == null) { c.ReadOnly = "the kms client has no row " + CodeText(code); return c; }
                OptNode rank = now.RankFor(grade);
                if (rank == null) { c.Source = "no rank at or below grade " + grade; return c; }
                c.Grades.AddRange(now.Served(rank));
                c.Value = OptNode.V(rank, name);
                c.Source = (rank.Attr("grade") == null ? "rank without grade" : "rank grade " + rank.Attr("grade")) + " (grades " + Span(c.Grades) + ")";
                if (OptNode.IsLua(rank)) c.ReadOnly = "the rank's values come from Lua (createType 3)";
                return c;
            }
            TableDraft d;
            TableRow r = FlatRow(side, code, grade, out d);
            c.Grades.Add(grade);
            if (r == null) { c.Source = "no row of grade " + grade; return c; }
            c.Value = d.Get(r, stat) ?? "";
            c.Shipped = r.Original != null ? (r.Original.Attr(stat) ?? "") : "";
            c.Source = "row grade " + grade + " in " + Short(d.Entry) + (ItemOptionRules.ServerReads(side.Kind, d.Entry) ? "" : " (the server does not read this file)");
            return c;
        }

        // a kms random row in the grade grid: the lines an item of the grade gets, or the range of the stat's line
        string RandomCell(OptNode row, int grade, string stat)
        {
            if (row == null) return "";
            if (stat == ItemOptionRules.Pick) return ItemOptionRules.KmsPickCount(row.Attr("pickCount"), row.Attr("isRarePickOne"), grade).ToString(CultureInfo.InvariantCulture);
            foreach (OptNode v in row.Children)
            {
                if (v.Name != "v" || v.Attr("name") == null || !ItemOptionRules.IsFlatOf(v.Attr("name"), stat)) continue;
                string first, last;
                string id = (v.Attr("statID") ?? "").Trim();
                return VariationRange(id, out first, out last) ? (first == last ? first : first + ".." + last) : "statID " + id;
            }
            return "";
        }

        static string Short(string entry) { int i = entry.LastIndexOf('/'); return i < 0 ? entry : entry.Substring(i + 1); }

        static string Span(List<int> grades)
        {
            if (grades.Count == 0) return "none";
            if (grades.Count == 1) return grades[0].ToString(CultureInfo.InvariantCulture);
            bool run = true;
            for (int i = 1; i < grades.Count; i++) if (grades[i] != grades[i - 1] + 1) run = false;
            if (run) return grades[0].ToString(CultureInfo.InvariantCulture) + "-" + grades[grades.Count - 1].ToString(CultureInfo.InvariantCulture);
            var l = new List<string>();
            foreach (int g in grades) l.Add(g.ToString(CultureInfo.InvariantCulture));
            return string.Join(",", l.ToArray());
        }

        /// <summary>The stats a code shows on the sides of its kind (rows of the grid), in the order of the first side, then the rest sorted.</summary>
        public List<string> Stats(OptionKind kind, long code)
        {
            var l = new List<string>();
            var rest = new SortedDictionary<string, bool>(StringComparer.Ordinal);
            if (kind != OptionKind.Constant) l.Add(ItemOptionRules.Pick);
            var kmsLines = new List<string>();
            foreach (OptionSide s in Sides(kind))
            {
                if (s.Kms && kind == OptionKind.Random)
                {
                    foreach (OptNode node in new[] { KmsNow(kind, code), KmsShipped(kind, code) })
                        if (node != null)
                            foreach (OptNode v in node.Children)
                                if (v.Name == "v" && v.Attr("name") != null && !kmsLines.Contains(v.Attr("name"))) kmsLines.Add(v.Attr("name"));
                    continue;
                }
                if (s.Kms)
                {
                    OptNode n = KmsNow(kind, code), w = KmsShipped(kind, code);
                    foreach (OptNode node in new[] { n, w })
                        if (node != null)
                            foreach (OptNode r in node.Ranks())
                                foreach (OptNode v in r.Children)
                                    if (v.Name == "v" && v.Attr("name") != null) Add(l, rest, ItemOptionRules.FlatName(v.Attr("name")));
                    continue;
                }
                foreach (string e in EntriesOf(s, code))
                {
                    TableDraft d = Draft(s.Archive, e);
                    List<TableRow> rows;
                    if (!RowsByCode(d).TryGetValue(CodeText(code), out rows)) continue;
                    foreach (TableRow r in rows)
                    {
                        foreach (XmlAttr a in r.Base.Attrs) if (!ItemOptionRules.IsKey(a.Name)) Add(l, rest, a.Name);
                        foreach (RowOp op in new[] { r.Add ?? r.Change, r.Own })
                            if (op != null) foreach (AttrOp a in op.Attrs) if (string.IsNullOrEmpty(a.At) && a.Kind == AttrOpKind.Set && !ItemOptionRules.IsKey(a.Name)) Add(l, rest, a.Name);
                    }
                }
            }
            // a kms random line no flat side has a stat for shows as X_value_base
            foreach (string name in kmsLines)
            {
                bool have = false;
                foreach (string f in rest.Keys) if (ItemOptionRules.IsFlatOf(name, f)) have = true;
                if (!have) rest[ItemOptionRules.FlatName(name)] = true;
            }
            l.AddRange(rest.Keys);
            return l;
        }

        static void Add(List<string> l, SortedDictionary<string, bool> rest, string n) { if (!l.Contains(n)) rest[n] = true; }

        /// <summary>The grades a code shows: 1..5, and 6 when a side has a row of grade 6.</summary>
        public List<int> GradesOf(OptionKind kind, long code)
        {
            var l = new List<int> { 1, 2, 3, 4, 5 };
            foreach (OptionSide s in Sides(kind))
                if (!s.Kms) { TableDraft d; if (FlatRow(s, code, 6, out d) != null) { l.Add(6); break; } }
            return l;
        }

        /// <summary>True when the sides that have the code give the cell different values.</summary>
        public bool Differs(OptionKind kind, long code, int grade, string stat)
        {
            string first = null;
            foreach (OptionSide s in Sides(kind))
            {
                if (!Has(s, code)) continue;
                // a kms random row has no values to compare (statID ranges and weights; the planner checks its pick count)
                if (s.Kms && (kind == OptionKind.Random || ItemOptionRules.KmsName(stat) == null)) continue;
                string v = ItemOptionRules.Normal(Cell(s, code, grade, stat).Value);
                if (first == null) first = v;
                else if (v != first) return true;
            }
            return false;
        }

        // ---- the picker ----

        /// <summary>The codes of a kind on any side, as shipped and with the mod's own codes, lowest first.</summary>
        public List<OptionCodeInfo> Codes(OptionKind kind)
        {
            var all = new SortedDictionary<long, OptionCodeInfo>();
            ShippedIndex ix = Index();
            foreach (OptionSide s in Sides(kind))
            {
                Dictionary<long, List<string>> codes;
                if (ix.Codes.TryGetValue(s.Key, out codes))
                    foreach (KeyValuePair<long, List<string>> kv in codes) Info(all, kind, kv.Key, s, kv.Value);
                Dictionary<long, List<long>> items;
                if (ix.Items.TryGetValue(s.Key, out items))
                    foreach (KeyValuePair<long, List<long>> kv in items)
                    {
                        OptionCodeInfo info = Info(all, kind, kv.Key, null, null);
                        foreach (long id in kv.Value) if (!info.Items.Contains(id)) info.Items.Add(id);
                    }
                Dictionary<long, SortedDictionary<int, bool>> grades;
                if (ix.Grades.TryGetValue(s.Key, out grades))
                    foreach (KeyValuePair<long, SortedDictionary<int, bool>> kv in grades)
                        foreach (int g in kv.Value.Keys) all[kv.Key].Grades[g] = true;
                // the mod's rows
                foreach (string e in ModEntries(s))
                {
                    TableDraft d = Draft(s.Archive, e);
                    foreach (KeyValuePair<string, List<TableRow>> kv in RowsByCode(d))
                    {
                        long code;
                        if (!TryCode(kv.Key, out code)) continue;
                        bool edited = false, added = false;
                        foreach (TableRow r in kv.Value)
                        {
                            if (r.IsAdded || r.IsChanged || r.IsDeleted) edited = true;
                            if (r.IsAdded) added = true;
                        }
                        if (!edited) continue;
                        OptionCodeInfo info = Info(all, kind, code, s, new List<string> { e });
                        info.Changed = true;
                        if (added && !ShippedAnywhere(kind, code)) info.Added = true;
                    }
                }
            }
            // the grades of the codes the mod changed, as the mod leaves them
            foreach (OptionCodeInfo info in all.Values)
            {
                if (!info.Changed) continue;
                info.Grades.Clear();
                foreach (OptionSide s in Sides(kind))
                {
                    if (s.Kms && kind == OptionKind.Random) continue;
                    if (s.Kms)
                    {
                        OptNode now = KmsNow(kind, info.Code);
                        if (now != null) foreach (OptNode r in now.Ranks()) info.Grades[OptNode.GradeOf(r)] = true;
                        continue;
                    }
                    TableDraft d;
                    for (int g = 1; g <= ItemOptionRules.MaxGrade; g++) if (FlatRow(s, info.Code, g, out d) != null) info.Grades[g] = true;
                }
            }
            // the mod's items: their pointers as the mod leaves them (an item the mod points elsewhere leaves its shipped code)
            var moved = new Dictionary<long, List<long>>();
            foreach (KeyValuePair<long, long> kv in ModPointers(kind))
            {
                List<long> to;
                if (!moved.TryGetValue(kv.Key, out to)) moved[kv.Key] = to = new List<long>();
                to.Add(kv.Value);
                OptionCodeInfo info = Info(all, kind, kv.Value, null, null);
                if (!info.Items.Contains(kv.Key)) info.Items.Add(kv.Key);
            }
            if (moved.Count > 0)
                foreach (OptionCodeInfo info in all.Values)
                    info.Items.RemoveAll(delegate(long id) { List<long> to; return moved.TryGetValue(id, out to) && !to.Contains(info.Code); });
            return new List<OptionCodeInfo>(all.Values);
        }

        bool ShippedAnywhere(OptionKind kind, long code)
        {
            foreach (OptionSide s in Sides(kind))
            {
                Dictionary<long, List<string>> codes;
                if (Index().Codes.TryGetValue(s.Key, out codes) && codes.ContainsKey(code)) return true;
            }
            return false;
        }

        static OptionCodeInfo Info(SortedDictionary<long, OptionCodeInfo> all, OptionKind kind, long code, OptionSide side, List<string> files)
        {
            OptionCodeInfo info;
            if (!all.TryGetValue(code, out info)) all[code] = info = new OptionCodeInfo { Code = code, Kind = kind };
            if (side != null && !info.Sides.Contains(side.Title)) info.Sides.Add(side.Title);
            if (files != null && side != null && !side.Kms)
                foreach (string f in files) if (!info.Files.Contains(Short(f))) info.Files.Add(Short(f));
            return info;
        }

        // the items the mod adds or edits, with the code of the kind each names (on any side)
        List<KeyValuePair<long, long>> ModPointers(OptionKind kind)
        {
            var l = new List<KeyValuePair<long, long>>();
            var items = new ItemEditor(Data, Mod);
            var ids = new List<long>(items.AddedIds());
            var seen = new HashSet<long>(ids);
            foreach (long id in items.TouchedIds()) if (seen.Add(id)) ids.Add(id);
            // one item open at a time, its file let go after (a fork can touch thousands)
            foreach (long id in ids)
            {
                try { OneItem(items, id, delegate (ItemDraft d) { foreach (long c in Pointers(d, kind)) l.Add(new KeyValuePair<long, long>(id, c)); }); }
                catch (ModKitException) { }
            }
            return l;
        }

        // the kms item's pointer first, then the item file's
        static string[] PointerFields(OptionKind kind)
        {
            return kind == OptionKind.Constant ? new[] { "constantID", "constant" } : kind == OptionKind.Random ? new[] { "randomID", "random" } : new[] { "static" };
        }

        static bool IsKmsPointer(string field) { return field == "constantID" || field == "randomID"; }

        static List<long> Pointers(ItemDraft d, OptionKind kind)
        {
            var l = new List<long>();
            foreach (EntitySection s in d.Sections)
            {
                if (s.Elem == null || s.Elem.Name != "option" || s.Part.Kind == "option") continue;
                foreach (string f in PointerFields(kind))
                {
                    long c;
                    if (TryCode(d.Get(s, f), out c) && c != 0 && !l.Contains(c)) l.Add(c);
                }
            }
            return l;
        }

        /// <summary>The codes of a kind an item names (on any side, as the mod leaves it).</summary>
        public List<long> CodesOf(long itemId, OptionKind kind)
        {
            var items = new ItemEditor(Data, Mod);
            return Pointers(items.Open(itemId), kind);
        }

        /// <summary>The items naming a code of a kind, on any side: shipped (unless the mod repoints them) and the mod's.</summary>
        public List<long> ItemsOf(OptionKind kind, long code)
        {
            var l = new List<long>();
            var mine = new Dictionary<long, List<long>>();
            foreach (KeyValuePair<long, long> kv in ModPointers(kind)) Get(mine, kv.Key).Add(kv.Value);
            foreach (OptionSide s in Sides(kind))
            {
                Dictionary<long, List<long>> items;
                List<long> ids;
                if (Index().Items.TryGetValue(s.Key, out items) && items.TryGetValue(code, out ids))
                    foreach (long id in ids) if (!mine.ContainsKey(id) && !l.Contains(id)) l.Add(id);
            }
            foreach (KeyValuePair<long, List<long>> kv in mine) if (kv.Value.Contains(code) && !l.Contains(kv.Key)) l.Add(kv.Key);
            l.Sort();
            return l;
        }

        // ---- writing ----

        /// <summary>
        /// Sets a stat of a code at a grade on a side ("" or null removes it) and on each other side of the kind that
        /// had the same value there. A kms cell edits the rank that serves the grade (Cell().Grades says which grades).
        /// Returns what was written, one line per side.
        /// </summary>
        public List<string> Set(OptionSide side, long code, int grade, string stat, string value)
        {
            value = value == null ? "" : value.Trim();
            if (grade < 1 || grade > ItemOptionRules.MaxGrade) throw new ModKitException("grade " + grade + " is outside 1.." + ItemOptionRules.MaxGrade);
            if (ItemOptionRules.IsKey(stat) || !TableDraft.IsAttrName(stat)) throw new ModKitException("\"" + stat + "\" is no stat");
            if (side.Kms && side.Kind == OptionKind.Random) throw new ModKitException("a kms random row has no grades: set its pick count and lines (SetKmsRandom, SetKmsRandomLine)");
            string bad = ItemOptionRules.CheckValue(side.Kind, side.Kms, stat, value);
            if (bad != null) throw new ModKitException(bad);
            if (value.Length > 0 && !StatNames(side).Contains(stat))
                throw new ModKitException("the " + side.Title + " " + ItemOptionRules.KindName(side.Kind) + " files use no stat " + stat + (side.Kms ? " (the client names a stat X as X_value_base here)" : ""));
            OptionCell before = Cell(side, code, grade, stat);
            if (before.ReadOnly != null && Has(side, code)) throw new ModKitException(before.ReadOnly);
            if (!Has(side, code)) throw new ModKitException("the " + side.Title + " has no option " + CodeText(code) + " (" + ItemOptionRules.KindName(side.Kind) + ")");
            var said = new List<string>();
            if (ItemOptionRules.Normal(before.Value) == ItemOptionRules.Normal(value)) return said;
            // the other sides that agreed before, with the grades to write there
            var mirror = new List<KeyValuePair<OptionSide, List<int>>>();
            foreach (OptionSide o in Sides(side.Kind))
            {
                if (o.Key == side.Key || !Has(o, code)) continue;
                if (o.Kms && (o.Kind == OptionKind.Random || ItemOptionRules.KmsName(stat) == null)) continue;
                if (!o.Kms && value.Length > 0 && !StatNames(o).Contains(stat)) continue;
                var gs = new List<int>();
                if (o.Kms)
                {
                    // one kms rank: written only when every grade it serves that this side has is among the grades written
                    OptionCell oc = Cell(o, code, grade, stat);
                    if (oc.ReadOnly != null || ItemOptionRules.Normal(oc.Value) != ItemOptionRules.Normal(before.Value)) continue;
                    bool inside = true;
                    foreach (int g in oc.Grades) { TableDraft dd; if (!before.Grades.Contains(g) && (side.Kms || FlatRow(side, code, g, out dd) != null)) inside = false; }
                    if (oc.Grades.Count == 0 || !inside) continue;
                    gs.Add(grade);
                }
                else
                    foreach (int g in before.Grades)
                    {
                        OptionCell mine = Cell(side, code, g, stat), oc = Cell(o, code, g, stat);
                        TableDraft dd;
                        if (FlatRow(o, code, g, out dd) != null && ItemOptionRules.Normal(oc.Value) == ItemOptionRules.Normal(mine.Value)) gs.Add(g);
                    }
                if (gs.Count > 0) mirror.Add(new KeyValuePair<OptionSide, List<int>>(o, gs));
            }
            said.Add(Write(side, code, new List<int> { grade }, stat, value));
            foreach (KeyValuePair<OptionSide, List<int>> m in mirror) said.Add(Write(m.Key, code, m.Value, stat, value) + " (it had the same value)");
            return said;
        }

        string Write(OptionSide side, long code, List<int> grades, string stat, string value)
        {
            if (side.Kms)
            {
                string name = ItemOptionRules.KmsName(stat);
                OptNode n = KmsNow(side.Kind, code).Copy();
                OptNode rank = n.RankFor(grades[0]);
                if (rank == null)
                {
                    if (value.Length == 0) return side.Title + ": nothing to remove";
                    rank = new OptNode { Name = "rank" };
                    rank.SetAttr("grade", grades[0].ToString(CultureInfo.InvariantCulture));
                    int at = 0;
                    for (int i = 0; i < n.Children.Count; i++) if (n.Children[i].Name == "rank") { at = i; break; } else at = i + 1;
                    n.Children.Insert(at, rank);
                }
                if (OptNode.IsLua(rank)) throw new ModKitException("the rank's values come from Lua (createType 3)");
                List<int> served = n.Served(rank);
                int vi = rank.Children.FindIndex(v => v.Name == "v" && v.Attr("name") == name);
                if (value.Length == 0 || ItemOptionRules.Normal(value).Length == 0) { if (vi >= 0) rank.Children.RemoveAt(vi); }
                else if (vi >= 0) rank.Children[vi].SetAttr("value", value);
                else
                {
                    var v = new OptNode { Name = "v" };
                    v.SetAttr("name", name);
                    v.SetAttr("value", value);
                    rank.Children.Add(v);
                }
                ReplaceKms(side.Kind, code, n);
                return side.Title + ": " + (rank.Attr("grade") == null ? "rank without grade" : "rank grade " + rank.Attr("grade")) + " (grades " + Span(served) + ") " + name + (value.Length == 0 ? " removed" : " = " + value);
            }
            var done = new List<string>();
            foreach (int g in grades)
            {
                TableDraft d;
                TableRow r = FlatRow(side, code, g, out d);
                if (r == null)
                {
                    if (value.Length == 0) continue;
                    r = AddGradeRow(side, code, g, out d);
                }
                d.Set(r, stat, value.Length == 0 ? null : value);
                // an added row left with its key only goes again
                if (r.IsAdded)
                {
                    bool empty = true;
                    foreach (string c in d.Columns) if (!ItemOptionRules.IsKey(c) && d.Get(r, c) != null) empty = false;
                    if (empty) d.Delete(r);
                }
                Changed(d);
                done.Add(g.ToString(CultureInfo.InvariantCulture));
            }
            return side.Title + ": " + ItemOptionRules.KindName(side.Kind) + " grade " + string.Join(",", done.ToArray()) + " " + stat + (value.Length == 0 ? " removed" : " = " + value);
        }

        // a grade row the code lacks on a flat side: in a file of the code that the server reads (the first one; else
        // the first file of the code), right after the code's row of the nearest lower grade there (else before the
        // nearest higher one; at the end of the file when the code has no shipped row in it)
        TableRow AddGradeRow(OptionSide side, long code, int grade, out TableDraft d)
        {
            List<string> files = EntriesOf(side, code);
            string entry = files.Find(e => ItemOptionRules.ServerReads(side.Kind, e)) ?? files[0];
            d = Draft(side.Archive, entry);
            TableRow below = null, above = null;
            int bg = int.MinValue, ag = int.MaxValue;
            List<TableRow> rows;
            if (RowsByCode(d).TryGetValue(CodeText(code), out rows))
                foreach (TableRow r in rows)
                {
                    int rg;
                    if (r.IsAdded || r.IsDeleted || !int.TryParse((d.Get(r, "grade") ?? "").Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out rg)) continue;
                    if (rg < grade && rg > bg) { below = r; bg = rg; }
                    if (rg > grade && rg < ag) { above = r; ag = rg; }
                }
            string xml = "<option code=\"" + CodeText(code) + "\" grade=\"" + grade.ToString(CultureInfo.InvariantCulture) + "\" />";
            return below != null ? d.AddRow(xml, below, false) : d.AddRow(xml, above, true);
        }

        // the kms row of a code becomes n: the shipped row is deleted and n added, or the shipped row restored when n is it
        void ReplaceKms(OptionKind kind, long code, OptNode n)
        {
            TableDraft d = KmsDraft(kind);
            TableRow shipped, added;
            KmsRows(kind, code, out shipped, out added);
            if (added != null) d.Delete(added);
            if (shipped != null && OptNode.Of(shipped.Original).Same(n)) d.Restore(shipped);
            else
            {
                if (shipped != null) d.Delete(shipped);
                d.AddRow(n.Text());
            }
            Changed(d);
        }

        /// <summary>
        /// Takes back the mod's edits of a code on every side of the kind (its own rows: a code the mod adds goes away).
        /// A code of the mod's own (a fork) also gives its items their old pointers back and frees its reservation; when
        /// an item has no old pointer to go back to, nothing is reverted and the message says which item.
        /// </summary>
        public int Revert(OptionKind kind, long code)
        {
            if (ShippedAnywhere(kind, code)) return RevertRows(kind, code);
            // the items first: every pointer has an old value to go back to, or nothing is written. One item is open
            // at a time (a fork can move thousands): a first pass checks, a second sets
            var items = new ItemEditor(Data, Mod);
            var ids = new List<long>();
            var seen = new HashSet<long>();
            foreach (KeyValuePair<long, long> kv in ModPointers(kind))
                if (kv.Value == code && seen.Add(kv.Key)) ids.Add(kv.Key);
            foreach (long id in ids)
                OneItem(items, id, delegate (ItemDraft d) { RevertPointers(d, kind, code, false); });
            var undo = new ModUndo(Mod);
            try
            {
                int n = RevertRows(kind, code);
                foreach (long id in ids) OneItem(items, id, delegate (ItemDraft d) { n += RevertPointers(d, kind, code, true); });
                // the reservation goes only with something of the code: a code the revert finds nothing of keeps it
                if (n > 0 && Mod.Ids.Exists(r => r.Space == ItemOptionRules.Space && code >= r.From && code <= r.To)) { IdSpaces.Unreserve(Mod, ItemOptionRules.Space, code); n++; }
                return n;
            }
            catch
            {
                undo.Restore();
                Forget();
                throw;
            }
        }

        // the item's pointers at a code of the mod's own: checked (each has an old value), or set back to it
        int RevertPointers(ItemDraft d, OptionKind kind, long code, bool set)
        {
            int n = 0;
            foreach (EntitySection s in d.Sections.ToArray())
            {
                if (s.Elem == null || s.Elem.Name != "option" || s.Part.Kind == "option") continue;
                foreach (string f in PointerFields(kind))
                {
                    if ((d.Get(s, f) ?? "").Trim() != CodeText(code)) continue;
                    if ((d.GetOriginal(s, f) ?? "").Trim() == CodeText(code))
                        throw new ModKitException("item " + d.Id.ToString(CultureInfo.InvariantCulture) + " names option " + CodeText(code) + " in its own data (" + f + "): point it at another code first; nothing was reverted");
                    if (set) { d.Set(s, f, d.GetOriginal(s, f), false); n++; }
                }
            }
            return n;
        }

        // opens one item, hands it over, and lets go of its file again unless it was read before: the edits are in
        // the mod, so the parsed file is not needed after
        void OneItem(ItemEditor items, long id, Action<ItemDraft> act)
        {
            string key = items.FileDocKey(id);
            bool had = key != null && Data.Cache.ContainsKey(key);
            try { act(items.Open(id)); }
            finally { if (key != null && !had) Data.Cache.Remove(key); }
        }

        int RevertRows(OptionKind kind, long code)
        {
            int n = 0;
            foreach (OptionSide s in Sides(kind))
            {
                if (s.Kms)
                {
                    TableRow shipped, added;
                    TableDraft kd = KmsDraft(kind);
                    KmsRows(kind, code, out shipped, out added);
                    if (added != null) { kd.Delete(added); n++; }
                    if (shipped != null && shipped.IsDeleted) { kd.Restore(shipped); n++; }
                    if (kd != null) Changed(kd);
                    continue;
                }
                foreach (string e in EntriesOf(s, code))
                {
                    TableDraft d = Draft(s.Archive, e);
                    List<TableRow> rows;
                    if (!RowsByCode(d).TryGetValue(CodeText(code), out rows)) continue;
                    foreach (TableRow r in rows.ToArray())
                    {
                        if (r.IsAdded) { d.Delete(r); n++; continue; }
                        if (r.IsDeleted) { d.Restore(r); n++; }
                        if (r.IsChanged)
                        {
                            d.RevertCells(r);
                            n++;
                        }
                    }
                    Changed(d);
                }
            }
            return n;
        }

        // ---- the kms random row (no grades: its attributes and <v> lines) ----

        /// <summary>The range the client rolls for a statID: the first and last &lt;v stat&gt; of its row in the kms
        /// table/itemoptionvariation.xml (as shipped; the editor does not edit that table). False when there is no row.</summary>
        public bool VariationRange(string statId, out string first, out string last)
        {
            first = last = "";
            string[] r;
            if (statId == null || !Variation().TryGetValue(statId.Trim(), out r)) return false;
            first = r[0]; last = r[1];
            return true;
        }

        Dictionary<string, string[]> Variation()
        {
            string key = "itemoptionvariation\n" + (KmsArchive ?? "");
            object o;
            if (Data.Cache.TryGetValue(key, out o)) return (Dictionary<string, string[]>)o;
            var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
            M2dArchive a = KmsArchive == null ? null : TryOpen(KmsArchive);
            M2dEntry e = a == null ? null : a.Find(ItemOptionRules.KmsVariationEntry);
            if (e != null)
            {
                try
                {
                    XmlDoc d = XmlDoc.Parse(a.Read(e), KmsArchive + " " + ItemOptionRules.KmsVariationEntry);
                    if (d.Root != null)
                        foreach (XmlElem row in d.Root.Children)
                        {
                            string id = (row.Attr("id") ?? "").Trim();
                            if (row.Name != "option" || id.Length == 0 || map.ContainsKey(id)) continue;
                            string f = null, l = null;
                            foreach (XmlElem v in row.Children)
                            {
                                if (v.Name != "v" || v.Attr("stat") == null) continue;
                                if (f == null) f = v.Attr("stat").Trim();
                                l = v.Attr("stat").Trim();
                            }
                            map[id] = new[] { f ?? "", l ?? "" };
                        }
                }
                catch (ModKitException) { }
            }
            Data.Cache[key] = map;
            return map;
        }

        /// <summary>The kms random row of a code as the mod leaves it; null when the kms table has none.</summary>
        public KmsRandomRow KmsRandomOf(long code)
        {
            OptNode n = KmsRandom ? KmsNow(OptionKind.Random, code) : null;
            if (n == null) return null;
            var row = new KmsRandomRow { Code = code };
            foreach (KeyValuePair<string, string> a in n.Attrs) if (a.Key != "code") row.Attrs.Add(a);
            foreach (OptNode v in n.Children)
            {
                if (v.Name != "v") continue;
                var line = new KmsRandomLine { Name = v.Attr("name") ?? "", StatId = (v.Attr("statID") ?? "").Trim(), Weight = (v.Attr("weight") ?? "").Trim() };
                line.HasRange = VariationRange(line.StatId, out line.First, out line.Last);
                row.Lines.Add(line);
            }
            OptNode was = KmsShipped(OptionKind.Random, code);
            row.Edited = was == null || !was.Same(n);
            return row;
        }

        OptNode RandomForEdit(long code)
        {
            OptNode n = KmsRandom ? KmsNow(OptionKind.Random, code) : null;
            if (n == null) throw new ModKitException("the kms client has no option " + CodeText(code) + " (random)");
            return n.Copy();
        }

        static OptNode LineAt(OptNode row, int index)
        {
            int i = 0;
            foreach (OptNode v in row.Children) if (v.Name == "v" && i++ == index) return v;
            throw new ModKitException("the row has no line " + (index + 1).ToString(CultureInfo.InvariantCulture));
        }

        static void SetOrRemove(OptNode n, string attr, string value)
        {
            if (value.Length > 0) { n.SetAttr(attr, value); return; }
            n.Attrs.RemoveAll(a => a.Key == attr);
        }

        /// <summary>Sets pickCount, isRarePickOne, statGroup or levelGroupID of a kms random row ("" removes the attribute).</summary>
        public string SetKmsRandom(long code, string attr, string value)
        {
            value = (value ?? "").Trim();
            if (Array.IndexOf(ItemOptionRules.KmsRandomAttrs, attr) < 0) throw new ModKitException("\"" + attr + "\" is no attribute of a kms random row the editor sets");
            string bad = ItemOptionRules.CheckKmsRandom(attr, value);
            if (bad != null) throw new ModKitException(bad);
            OptNode n = RandomForEdit(code);
            if ((n.Attr(attr) ?? "") == value) return "kms client: nothing to change";
            SetOrRemove(n, attr, value);
            ReplaceKms(OptionKind.Random, code, n);
            return "kms client: random " + CodeText(code) + " " + attr + (value.Length == 0 ? " removed" : " = " + value);
        }

        /// <summary>Sets statID or weight of the line at an index of a kms random row ("" removes weight: the client takes 1).</summary>
        public string SetKmsRandomLine(long code, int index, string attr, string value)
        {
            value = (value ?? "").Trim();
            if (attr != "statID" && attr != "weight") throw new ModKitException("\"" + attr + "\" is no attribute of a kms random line the editor sets");
            if (attr == "statID" && value.Length == 0) throw new ModKitException("a line needs a statID (remove the line instead)");
            string bad = ItemOptionRules.CheckKmsRandom(attr, value);
            if (bad != null) throw new ModKitException(bad);
            OptNode n = RandomForEdit(code);
            OptNode v = LineAt(n, index);
            if ((v.Attr(attr) ?? "") == value) return "kms client: nothing to change";
            SetOrRemove(v, attr, value);
            ReplaceKms(OptionKind.Random, code, n);
            return "kms client: random " + CodeText(code) + " line " + (v.Attr("name") ?? "") + " " + attr + (value.Length == 0 ? " removed" : " = " + value);
        }

        /// <summary>Adds a line to a kms random row: a stat name the shipped table uses, a statID, and a weight ("" for 1).</summary>
        public string AddKmsRandomLine(long code, string name, string statId, string weight)
        {
            name = (name ?? "").Trim(); statId = (statId ?? "").Trim(); weight = (weight ?? "").Trim();
            // the client drops a line whose name is no stat: only names the shipped table uses are taken
            string known = StatNames(new OptionSide { Archive = KmsArchive ?? "", Kms = true, Kind = OptionKind.Random }).Find(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
            if (known == null) throw new ModKitException("the kms random table uses no stat name \"" + name + "\"");
            if (statId.Length == 0) throw new ModKitException("a line needs a statID");
            string bad = ItemOptionRules.CheckKmsRandom("statID", statId) ?? ItemOptionRules.CheckKmsRandom("weight", weight);
            if (bad != null) throw new ModKitException(bad);
            OptNode n = RandomForEdit(code);
            var v = new OptNode { Name = "v" };
            v.SetAttr("name", known);
            v.SetAttr("statID", statId);
            if (weight.Length > 0) v.SetAttr("weight", weight);
            int at = n.Children.FindLastIndex(c => c.Name == "v") + 1;
            n.Children.Insert(at, v);
            ReplaceKms(OptionKind.Random, code, n);
            return "kms client: random " + CodeText(code) + " line " + known + " added (statID " + statId + ")";
        }

        /// <summary>Removes the line at an index of a kms random row.</summary>
        public string RemoveKmsRandomLine(long code, int index)
        {
            OptNode n = RandomForEdit(code);
            OptNode v = LineAt(n, index);
            n.Children.Remove(v);
            ReplaceKms(OptionKind.Random, code, n);
            return "kms client: random " + CodeText(code) + " line " + (v.Attr("name") ?? "") + " removed";
        }

        // ---- a code of the mod's own ----

        /// <summary>
        /// Checks a new code for a fork: a number in the mods' band, no side ships it, the mod has no rows of it, and no
        /// other mod reserves or adds it. Null when it is free; why not otherwise.
        /// </summary>
        public string CheckNewCode(long code)
        {
            if (code < ItemOptionRules.From || code > ItemOptionRules.To)
                return "option codes of mods are " + ItemOptionRules.From + "-" + ItemOptionRules.To;
            foreach (OptionKind k in ItemOptionRules.Kinds)
                foreach (OptionSide s in Sides(k))
                    if (Has(s, code)) return "the " + s.Title + " already has option " + CodeText(code) + " (" + ItemOptionRules.KindName(k) + ")";
            foreach (Mod m in Others())
            {
                if (m == null || m.Id == Mod.Id) continue;
                foreach (IdRange r in m.Ids)
                    if (r.Space == ItemOptionRules.Space && code >= r.From && code <= r.To) return "mod " + m.Id + " reserves option codes " + r.From + "-" + r.To;
                if (AddsCode(m, code)) return "mod " + m.Id + " adds option " + CodeText(code);
            }
            if (AddsCode(Mod, code)) return "this mod already has rows of option " + CodeText(code);
            return null;
        }

        /// <summary>The lowest free codes of the band, not shipped and not taken by this or the other mods.</summary>
        public List<long> NextCodes(int count)
        {
            var mods = new List<Mod> { Mod };
            foreach (Mod m in Others()) if (m != null && m.Id != Mod.Id) mods.Add(m);
            var l = new List<long>();
            foreach (long c in IdSpaces.NextFreeGroupIds(Data, mods, ItemOptionRules.Space, count + 16, null))
            {
                if (l.Count == count) break;
                if (CheckNewCode(c) == null) l.Add(c);
            }
            return l;
        }

        /// <summary>
        /// Copies a code's rows to a new code on every side of the kind that has it (into the same files), reserves the
        /// new code and points the given items at it where they name the old code: the kms constantID (randomID) when
        /// the kms table has the old code, the item file's constant (static, random) when a flat side has it. All or nothing: an
        /// item that cannot be opened stops the fork before anything is written, and a failure while writing takes
        /// every row, pointer and reservation of the fork back. Returns the number of items changed.
        /// </summary>
        public int Fork(OptionKind kind, long code, long newCode, IList<long> itemIds)
        {
            string bad = CheckNewCode(newCode);
            if (bad != null) throw new ModKitException(bad);
            var sides = new List<OptionSide>();
            bool kmsHas = false, flatHas = false;
            foreach (OptionSide s in Sides(kind))
            {
                if (!Has(s, code)) continue;
                sides.Add(s);
                if (s.Kms) kmsHas = true; else flatHas = true;
            }
            if (sides.Count == 0) throw new ModKitException("no side has option " + CodeText(code) + " (" + ItemOptionRules.KindName(kind) + ")");
            // the items first: every one opens, or nothing is written. One item is open at a time, so that thousands
            // of items do not stay parsed (a 32-bit host): a first pass checks, a second sets the pointers
            var items = itemIds == null ? null : new ItemEditor(Data, Mod);
            if (itemIds != null)
                foreach (long id in itemIds)
                {
                    try { OneItem(items, id, delegate (ItemDraft d) { }); }
                    catch (ModKitException ex) { throw new ModKitException("item " + id.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message + "; nothing was forked"); }
                }
            var undo = new ModUndo(Mod);
            try
            {
                foreach (OptionSide s in sides)
                {
                    if (s.Kms)
                    {
                        OptNode n = KmsNow(kind, code).Copy();
                        n.SetAttr("code", CodeText(newCode));
                        KmsDraft(kind).AddRow(n.Text());
                        Changed(KmsDraft(kind));
                        continue;
                    }
                    foreach (string e in EntriesOf(s, code))
                    {
                        TableDraft d = Draft(s.Archive, e);
                        List<TableRow> rows;
                        if (!RowsByCode(d).TryGetValue(CodeText(code), out rows)) continue;
                        foreach (TableRow r in rows.ToArray())
                        {
                            if (r.IsDeleted) continue;
                            TableRow c = d.Clone(r);
                            d.Set(c, "code", CodeText(newCode));
                        }
                        Changed(d);
                    }
                }
                IdSpaces.Reserve(Mod, ItemOptionRules.Space, newCode);
                int changed = 0;
                if (itemIds != null)
                    foreach (long id in itemIds)
                        OneItem(items, id, delegate (ItemDraft d)
                        {
                            bool hit = false;
                            foreach (EntitySection s in d.Sections.ToArray())
                            {
                                if (s.Elem == null || s.Elem.Name != "option" || s.Part.Kind == "option") continue;
                                foreach (string f in PointerFields(kind))
                                {
                                    // a pointer is moved only on a layout that gets the new code
                                    if (!(IsKmsPointer(f) ? kmsHas : flatHas)) continue;
                                    if ((d.Get(s, f) ?? "").Trim() == CodeText(code)) { d.Set(s, f, CodeText(newCode), false); hit = true; }
                                }
                            }
                            if (hit) changed++;
                        });
                return changed;
            }
            catch
            {
                undo.Restore();
                Forget();
                throw;
            }
        }

        // the drafts read the mod's ops: after the ops are put back they are read again
        void Forget()
        {
            drafts.Clear();
            byCode.Clear();
        }

        /// <summary>
        /// Drops the parsed flat files (a view calls it when it leaves a code); they are read again when needed. A kms
        /// table (constant, random) is one file every code reads, so it stays.
        /// </summary>
        public void DropDrafts()
        {
            foreach (string k in new List<string>(drafts.Keys))
            {
                OptionKind kind;
                int cut = k.IndexOf('\n');
                if (KmsArchive != null && string.Equals(k.Substring(0, cut), KmsArchive, StringComparison.OrdinalIgnoreCase) && ItemOptionRules.IsKmsEntry(k.Substring(cut + 1), out kind)) continue;
                byCode.Remove(drafts[k]);
                drafts.Remove(k);
            }
        }

        /// <summary>The number of parsed option files the editor holds (a kms table counts as one).</summary>
        public int DraftCount { get { return drafts.Count; } }

        /// <summary>True when an op of the mod adds a row of the option code to an option table (kms or flat).</summary>
        internal static bool AddsCode(Mod m, long code)
        {
            string c = CodeText(code);
            foreach (ModOp op in m.Ops)
            {
                var ed = op as EditOp;
                OptionKind k;
                if (ed == null) continue;
                string entry = ed.Entry.Replace('\\', '/');
                if (!ItemOptionRules.IsKmsEntry(entry, out k) && !ItemOptionRules.IsFlatEntry(entry, out k)) continue;
                foreach (RowOp r in ed.Rows)
                {
                    if (r.Kind == RowOpKind.Delete) continue;
                    string set = null;
                    foreach (AttrOp a in r.Attrs) if (string.IsNullOrEmpty(a.At) && a.Name == "code" && a.Kind == AttrOpKind.Set) set = a.Value;
                    if (set != null) { if (set.Trim() == c) return true; continue; }
                    if (r.Kind != RowOpKind.Add || r.Xml == null) continue;
                    try
                    {
                        XmlDoc f = XmlDoc.ParseFragment(r.Xml.Trim(), "a row of mod " + m.Id);
                        if (f.Root != null && (f.Root.Attr("code") ?? "").Trim() == c) return true;
                    }
                    catch (ModKitException) { }
                }
            }
            return false;
        }
    }

    /// <summary>
    /// The ops, id ranges and unsaved files of a mod as they are now, to put back after an edit of many steps fails
    /// part way. The same op objects are put back with their old contents.
    /// </summary>
    public sealed class ModUndo
    {
        readonly Mod mod;
        readonly List<ModOp> ops;
        readonly List<IdRange> ids;
        readonly Dictionary<string, byte[]> files;
        readonly List<Action> restore = new List<Action>();

        public ModUndo(Mod m)
        {
            mod = m;
            ops = new List<ModOp>(m.Ops);
            ids = new List<IdRange>(m.Ids);
            files = new Dictionary<string, byte[]>(m.Files, StringComparer.Ordinal);
            foreach (IdRange r in m.Ids)
            {
                IdRange q = r;
                long from = q.From, to = q.To;
                var places = new List<IdPlace>(q.Places);
                restore.Add(delegate { q.From = from; q.To = to; q.Places.Clear(); q.Places.AddRange(places); });
            }
            foreach (ModOp op in m.Ops)
            {
                var en = op as EntryOp;
                if (en != null)
                {
                    string name = en.Name, copy = en.Copy, file = en.File;
                    bool del = en.Delete;
                    restore.Add(delegate { en.Name = name; en.Copy = copy; en.File = file; en.Delete = del; });
                    continue;
                }
                var ed = op as EditOp;
                if (ed == null) continue;
                var rows = new List<RowOp>(ed.Rows);
                restore.Add(delegate { ed.Rows.Clear(); ed.Rows.AddRange(rows); });
                foreach (RowOp r in ed.Rows)
                {
                    RowOp row = r;
                    string at = r.At, cp = r.Copy, xml = r.Xml, after = r.After, before = r.Before, inn = r.In;
                    bool sync = r.Sync;
                    var attrs = new List<AttrOp>(r.Attrs);
                    var vals = new List<string>();
                    foreach (AttrOp a in r.Attrs) vals.Add(a.Value);
                    restore.Add(delegate
                    {
                        row.At = at; row.Copy = cp; row.Xml = xml; row.After = after; row.Before = before; row.In = inn; row.Sync = sync;
                        row.Attrs.Clear(); row.Attrs.AddRange(attrs);
                        for (int i = 0; i < attrs.Count; i++) attrs[i].Value = vals[i];
                    });
                }
            }
        }

        public void Restore()
        {
            mod.Ops.Clear(); mod.Ops.AddRange(ops);
            mod.Ids.Clear(); mod.Ids.AddRange(ids);
            mod.Files.Clear();
            foreach (KeyValuePair<string, byte[]> kv in files) mod.Files[kv.Key] = kv.Value;
            foreach (Action a in restore) a();
        }
    }
}
