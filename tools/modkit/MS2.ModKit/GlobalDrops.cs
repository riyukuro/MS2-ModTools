// Global drops: what every monster of a kind drops on top of its own drop boxes. Two server tables (Server.m2d):
//
//   table/Server/globalDropItemBox_Final.xml   <dropBox dropBoxID comment> with <v> lines, each a link to an item
//       group: dropGroupIDs (the group), minLevel/maxLevel (the monster levels the line is for), dropCount and
//       dropCountProbability (two comma lists of the same length: how many items, and the chance of each count),
//       isOwnerDrop, mapTypeCondition, continentCondition
//   table/Server/globalDropItemSet_Final.xml   <dropBox dropGroupID comment> with <v> lines, each an item:
//       itemID, minLevel/maxLevel, minCount/maxCount, weight, grade (the item's rarity), uiItemRank,
//       mapDependency, constraintsQuest, locale, assistBonus
//
// An NPC names its global box in dropiteminfo/@globalDropBoxId (the NPCs view). The individual drop boxes of an
// NPC or an item box are another table; the Shops and Drops view edits those.
//
// The editor writes plain <edit> lines: a line of a shipped box or group is a row of that element, a box or group
// the mod adds is one <add> with its lines inside.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static class GlobalDropRules
    {
        public const string Archive = "server:Server.m2d";
        public const string BoxEntry = "table/Server/globalDropItemBox_Final.xml", SetEntry = "table/Server/globalDropItemSet_Final.xml";
        public const string Holder = "dropBox", Line = "v", BoxId = "dropBoxID", GroupId = "dropGroupID", Comment = "comment";
        public const string Link = "dropGroupIDs", MinLevel = "minLevel", MaxLevel = "maxLevel", Count = "dropCount", Chance = "dropCountProbability";
        public const string Item = "itemID", MinCount = "minCount", MaxCount = "maxCount", Weight = "weight", Grade = "grade", UiRank = "uiItemRank";
        /// <summary>The rarities an item can have.</summary>
        public const int MinGrade = 1, MaxGrade = 6;

        /// <summary>The fields of a box line (a link to a group), in the order they are shown.</summary>
        public static readonly string[] LinkFields = { Link, MinLevel, MaxLevel, Count, Chance, "isOwnerDrop", "mapTypeCondition", "continentCondition", "reference1" };
        /// <summary>The fields of a group line (an item), in the order they are shown.</summary>
        public static readonly string[] ItemFields = { Item, MinLevel, MaxLevel, MinCount, MaxCount, Weight, Grade, UiRank, "mapDependency", "constraintsQuest", "locale", "assistBonus" };

        public static bool IsBoxEntry(string entry) { return Same(entry, BoxEntry); }
        public static bool IsSetEntry(string entry) { return Same(entry, SetEntry); }
        static bool Same(string a, string b) { return string.Equals((a ?? "").Replace('\\', '/'), b, StringComparison.OrdinalIgnoreCase); }

        internal static bool Whole(string v, out long n)
        {
            return long.TryParse((v ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n);
        }

        /// <summary>The numbers of a comma list; null when a part is no whole number.</summary>
        public static List<long> List(string v)
        {
            var l = new List<long>();
            if (v == null || v.Trim().Length == 0) return l;
            foreach (string t in v.Split(','))
            {
                long n;
                if (!Whole(t, out n)) return null;
                l.Add(n);
            }
            return l;
        }

        static void Numbers(List<string> l, Func<string, string> get, params string[] fields)
        {
            foreach (string f in fields)
            {
                long n;
                string v = get(f);
                if (v != null && !Whole(v, out n)) l.Add(f + " \"" + v + "\" is not a whole number");
            }
        }

        static void Range(List<string> l, Func<string, string> get, string lo, string hi, string what)
        {
            long a, b;
            if (Whole(get(lo), out a) && Whole(get(hi), out b) && a > b) l.Add(lo + " " + a + " is above " + hi + " " + b + ": " + what);
        }

        /// <summary>What is wrong with a box line by itself.</summary>
        public static List<string> CheckLink(Func<string, string> get)
        {
            var l = new List<string>();
            List<long> groups = List(get(Link));
            if (groups == null) l.Add(Link + " \"" + get(Link) + "\" is not a list of group ids");
            else if (groups.Count == 0) l.Add(Link + " is not set: the line drops nothing");
            Numbers(l, get, MinLevel, MaxLevel, "isOwnerDrop", "mapTypeCondition", "continentCondition");
            Range(l, get, MinLevel, MaxLevel, "no monster level is in that range");
            List<long> counts = List(get(Count)), chances = List(get(Chance));
            if (counts == null) l.Add(Count + " \"" + get(Count) + "\" is not a list of whole numbers");
            if (chances == null) l.Add(Chance + " \"" + get(Chance) + "\" is not a list of whole numbers");
            if (counts != null && chances != null)
            {
                if (counts.Count == 0) l.Add(Count + " is not set: the line drops nothing");
                else if (counts.Count != chances.Count)
                    l.Add(Count + " has " + counts.Count + (counts.Count == 1 ? " number" : " numbers") + " and " + Chance + " has " + chances.Count + ": each count needs its chance");
                else
                {
                    long sum = 0, some = 0;
                    for (int i = 0; i < counts.Count; i++) { sum += chances[i]; if (counts[i] > 0) some += chances[i]; }
                    if (sum == 0) l.Add(Chance + " is 0 for every count");
                    else if (some == 0) l.Add("every count with a chance is 0: the line drops nothing");
                }
            }
            return l;
        }

        /// <summary>What is wrong with a group line by itself.</summary>
        public static List<string> CheckItem(Func<string, string> get)
        {
            var l = new List<string>();
            long n;
            if (get(Item) == null) l.Add(Item + " is not set");
            else if (!Whole(get(Item), out n) || n <= 0) l.Add(Item + " \"" + get(Item) + "\" is not an item id");
            Numbers(l, get, MinLevel, MaxLevel, MinCount, MaxCount, Weight, Grade, UiRank, "constraintsQuest", "assistBonus");
            Range(l, get, MinLevel, MaxLevel, "no monster level is in that range");
            Range(l, get, MinCount, MaxCount, "the count range is empty");
            foreach (string f in new[] { MinCount, MaxCount })
                if (Whole(get(f), out n) && n == 0) l.Add(f + " is 0");
            foreach (string f in new[] { Grade, UiRank })
                if (Whole(get(f), out n) && (n < MinGrade || n > MaxGrade)) l.Add(f + " " + n + " is not a rarity (" + MinGrade + " to " + MaxGrade + ")");
            return l;
        }
    }

    /// <summary>A global drop box (its lines link to groups) or an item group (its lines are items).</summary>
    public sealed class DropHolder
    {
        public readonly bool IsGroup;
        internal TableRow Row;
        internal List<DropLine> Own;        // the lines of a holder the mod adds
        internal DropHolder(bool group, TableRow row) { IsGroup = group; Row = row; }
        public bool IsAdded { get { return Row.IsAdded; } }
        public bool IsDeleted { get { return Row.IsDeleted; } }
    }

    /// <summary>One line of a box (a link to a group) or of a group (an item).</summary>
    public sealed class DropLine
    {
        public readonly DropHolder Holder;
        internal TableRow Row;                                      // in a shipped holder
        internal List<KeyValuePair<string, string>> Attrs;          // in a holder the mod adds
        internal DropLine(DropHolder h) { Holder = h; }
        public bool IsAdded { get { return Attrs != null || Row.IsAdded; } }
        public bool IsDeleted { get { return Row != null && Row.IsDeleted; } }
        public bool IsChanged { get { return IsAdded || Row.IsDeleted || Row.IsChanged; } }
    }

    public sealed class GlobalDropEditor
    {
        public readonly DataSet Data;
        public readonly Mod Mod;
        public readonly TableDraft BoxTable, SetTable;
        readonly List<DropHolder> boxes = new List<DropHolder>(), groups = new List<DropHolder>();
        // the lines of one shipped box and one shipped group at a time: each draft holds its table parsed
        DropHolder openBox, openGroup;
        List<DropLine> openBoxLines, openGroupLines;
        TableDraft openBoxDraft, openGroupDraft;

        public GlobalDropEditor(DataSet data, Mod mod)
        {
            Data = data;
            Mod = mod;
            if (!data.Has(Profile.Server) || !data.Exists(GlobalDropRules.Archive))
                throw new ModKitException("the global drop tables are server tables: the data set has no " + GlobalDropRules.Archive);
            BoxTable = new TableDraft(data, mod, GlobalDropRules.Archive, GlobalDropRules.BoxEntry, null);
            SetTable = new TableDraft(data, mod, GlobalDropRules.Archive, GlobalDropRules.SetEntry, null);
            foreach (TableRow r in BoxTable.Rows) if (r.Base != null && r.Base.Name == GlobalDropRules.Holder) boxes.Add(Made(false, r));
            foreach (TableRow r in SetTable.Rows) if (r.Base != null && r.Base.Name == GlobalDropRules.Holder) groups.Add(Made(true, r));
        }

        static DropHolder Made(bool group, TableRow r)
        {
            var h = new DropHolder(group, r);
            if (!r.IsAdded) return h;
            h.Own = new List<DropLine>();
            foreach (XmlElem c in r.Base.Children)
            {
                if (c.Name != GlobalDropRules.Line) continue;
                var line = new DropLine(h) { Attrs = new List<KeyValuePair<string, string>>() };
                foreach (XmlAttr a in c.Attrs) line.Attrs.Add(new KeyValuePair<string, string>(a.Name, a.Value));
                h.Own.Add(line);
            }
            return h;
        }

        TableDraft TableOf(DropHolder h) { return h.IsGroup ? SetTable : BoxTable; }
        static string IdField(DropHolder h) { return h.IsGroup ? GlobalDropRules.GroupId : GlobalDropRules.BoxId; }

        // ---- reading ----

        public List<DropHolder> Boxes() { return new List<DropHolder>(boxes); }
        public List<DropHolder> Groups() { return new List<DropHolder>(groups); }

        public string Get(DropHolder h, string field) { return TableOf(h).Get(h.Row, field) ?? ""; }
        public string IdText(DropHolder h) { return Get(h, IdField(h)); }

        public long Id(DropHolder h)
        {
            long n;
            return GlobalDropRules.Whole(IdText(h), out n) ? n : -1;
        }

        public bool IsChanged(DropHolder h)
        {
            if (h.Row.IsAdded || h.Row.IsDeleted || h.Row.IsChanged) return true;
            string sel = h.Row.Selector;
            if (sel == null) return false;
            foreach (ModOp op in Mod.Ops)
            {
                var ed = op as EditOp;
                if (ed == null || ed.Archive != TableOf(h).Archive || !string.Equals(ed.Entry, TableOf(h).Entry, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (RowOp r in ed.Rows)
                    foreach (string at in new[] { r.At, r.In, r.After, r.Before, r.Copy })
                        if (at != null && at.StartsWith(sel, StringComparison.Ordinal) && (at.Length == sel.Length || at[sel.Length] == '/')) return true;
            }
            return false;
        }

        /// <summary>The box with the id (the first that is not removed), or null.</summary>
        public DropHolder Box(long id) { return ById(boxes, id); }
        /// <summary>The group with the id (the first that is not removed), or null.</summary>
        public DropHolder Group(long id) { return ById(groups, id); }

        DropHolder ById(List<DropHolder> list, long id)
        {
            foreach (DropHolder h in list) if (!h.IsDeleted && Id(h) == id) return h;
            return null;
        }

        /// <summary>The lines of a box or group (none for one the mod removes).</summary>
        public List<DropLine> Lines(DropHolder h)
        {
            if (h.Own != null) return new List<DropLine>(h.Own);
            if (h.IsDeleted) return new List<DropLine>();
            if ((h.IsGroup ? openGroup : openBox) != h)
            {
                var d = new TableDraft(Data, Mod, TableOf(h).Archive, TableOf(h).Entry, h.Row.Selector);
                var l = new List<DropLine>();
                foreach (TableRow r in d.Rows) if (r.Base != null && r.Base.Name == GlobalDropRules.Line) l.Add(new DropLine(h) { Row = r });
                closed.Remove(h);
                if (h.IsGroup) { openGroup = h; openGroupDraft = d; openGroupLines = l; }
                else { openBox = h; openBoxDraft = d; openBoxLines = l; }
            }
            return new List<DropLine>(h.IsGroup ? openGroupLines : openBoxLines);
        }

        TableDraft DraftOf(DropLine line)
        {
            DropHolder h = line.Holder;
            if ((h.IsGroup ? openGroup : openBox) != h || !(h.IsGroup ? openGroupLines : openBoxLines).Contains(line))
                throw new ModKitException("the line is of a box or group that is no longer open; choose it again");
            return h.IsGroup ? openGroupDraft : openBoxDraft;
        }

        public string Get(DropLine line, string field)
        {
            if (line.Attrs != null)
            {
                foreach (KeyValuePair<string, string> kv in line.Attrs) if (kv.Key == field) return kv.Value;
                return "";
            }
            return DraftOf(line).Get(line.Row, field) ?? "";
        }

        public bool IsEdited(DropLine line, string field)
        {
            return line.IsAdded || DraftOf(line).IsEdited(line.Row, field);
        }

        /// <summary>The group ids a box line names (ids that are no numbers are left out).</summary>
        public List<long> GroupsOf(DropLine link)
        {
            var l = new List<long>();
            foreach (string t in Get(link, GlobalDropRules.Link).Split(','))
            {
                long n;
                if (GlobalDropRules.Whole(t, out n)) l.Add(n);
            }
            return l;
        }

        // The lines of a shipped holder the mod changes, read while it is not the open one: the link, item and weight
        // of each line the mod leaves in it. Read once after the holder was open (its lines only change while it is).
        readonly Dictionary<DropHolder, List<string[]>> closed = new Dictionary<DropHolder, List<string[]>>();
        static readonly string[] ClosedFields = { GlobalDropRules.Link, GlobalDropRules.Item, GlobalDropRules.Weight };

        List<string[]> Closed(DropHolder h)
        {
            List<string[]> l;
            if (closed.TryGetValue(h, out l)) return l;
            l = new List<string[]>();
            var d = new TableDraft(Data, Mod, TableOf(h).Archive, TableOf(h).Entry, h.Row.Selector);
            foreach (TableRow r in d.Rows)
            {
                if (r.Base == null || r.Base.Name != GlobalDropRules.Line || r.IsDeleted) continue;
                var v = new string[ClosedFields.Length];
                for (int i = 0; i < v.Length; i++) v[i] = d.Get(r, ClosedFields[i]);
                l.Add(v);
            }
            closed[h] = l;
            return l;
        }

        // the attribute values of a holder's lines as the mod leaves them, without opening it (field: one of ClosedFields)
        IEnumerable<string> Values(DropHolder h, string field)
        {
            if (h.IsDeleted) yield break;
            if (h.Own != null)
            {
                foreach (DropLine l in h.Own)
                    foreach (KeyValuePair<string, string> kv in l.Attrs) if (kv.Key == field) yield return kv.Value;
                yield break;
            }
            if (h == openBox || h == openGroup)
            {
                foreach (DropLine l in h.IsGroup ? openGroupLines : openBoxLines)
                    if (!l.IsDeleted && Has(l, field)) yield return Get(l, field);
                yield break;
            }
            if (IsChanged(h))
            {
                int at = Array.IndexOf(ClosedFields, field);
                foreach (string[] v in Closed(h)) if (v[at] != null) yield return v[at];
                yield break;
            }
            foreach (XmlElem c in h.Row.Base.Children)
                if (c.Name == GlobalDropRules.Line && c.Attr(field) != null) yield return c.Attr(field);
        }

        /// <summary>The boxes with a line for the group, as the mod leaves them.</summary>
        public List<DropHolder> BoxesUsing(long group)
        {
            var l = new List<DropHolder>();
            foreach (DropHolder b in boxes)
            {
                if (b.IsDeleted) continue;
                bool hit = false;
                foreach (string v in Values(b, GlobalDropRules.Link))
                    foreach (string t in v.Split(','))
                    {
                        long n;
                        if (GlobalDropRules.Whole(t, out n) && n == group) hit = true;
                    }
                if (hit) l.Add(b);
            }
            return l;
        }

        /// <summary>The boxes whose id or comment holds the text, or that link a group with an item id holding it; all when empty.</summary>
        public List<DropHolder> FindBoxes(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return Boxes();
            var hitGroups = new HashSet<long>();
            foreach (DropHolder g in FindGroups(text)) hitGroups.Add(Id(g));
            var l = new List<DropHolder>();
            foreach (DropHolder b in boxes)
            {
                bool hit = IdText(b).IndexOf(text, StringComparison.Ordinal) >= 0 || Get(b, GlobalDropRules.Comment).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!hit)
                    foreach (string v in Values(b, GlobalDropRules.Link))
                    {
                        List<long> ids = GlobalDropRules.List(v);
                        if (ids != null) foreach (long id in ids) if (hitGroups.Contains(id)) hit = true;
                    }
                if (hit) l.Add(b);
            }
            return l;
        }

        /// <summary>The groups whose id or comment holds the text, or that have an item id holding it; all when empty.</summary>
        public List<DropHolder> FindGroups(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return Groups();
            var l = new List<DropHolder>();
            foreach (DropHolder g in groups)
            {
                bool hit = IdText(g).IndexOf(text, StringComparison.Ordinal) >= 0 || Get(g, GlobalDropRules.Comment).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!hit) foreach (string v in Values(g, GlobalDropRules.Item)) if (v.IndexOf(text, StringComparison.Ordinal) >= 0) { hit = true; break; }
                if (hit) l.Add(g);
            }
            return l;
        }

        /// <summary>How many lines (items of a group) the holder has as the mod leaves it: removed lines do not count.</summary>
        public int LineCount(DropHolder h)
        {
            if (h.Own != null) return h.Own.Count;
            if (h == openBox || h == openGroup)
            {
                int left = 0;
                foreach (DropLine l in h.IsGroup ? openGroupLines : openBoxLines) if (!l.IsDeleted) left++;
                return left;
            }
            if (!h.IsDeleted && IsChanged(h)) return Closed(h).Count;
            int n = 0;
            foreach (XmlElem c in h.Row.Base.Children) if (c.Name == GlobalDropRules.Line) n++;
            return n;
        }

        /// <summary>What is wrong with a line: by itself, and a box line that names a group the table does not have.</summary>
        public List<string> Check(DropLine line)
        {
            var l = new List<string>();
            if (line.IsDeleted) return l;
            Func<string, string> get = f => { string v = Get(line, f); return v.Length == 0 && !Has(line, f) ? null : v; };
            if (line.Holder.IsGroup) return GlobalDropRules.CheckItem(get);
            l.AddRange(GlobalDropRules.CheckLink(get));
            foreach (long g in GroupsOf(line))
                if (Group(g) == null) l.Add("there is no item group " + g + " in " + GlobalDropRules.SetEntry);
            return l;
        }

        bool Has(DropLine line, string field)
        {
            if (line.Attrs != null)
            {
                foreach (KeyValuePair<string, string> kv in line.Attrs) if (kv.Key == field) return true;
                return false;
            }
            return DraftOf(line).Get(line.Row, field) != null;
        }

        /// <summary>What is wrong with a box or group as a whole.</summary>
        public List<string> Check(DropHolder h)
        {
            var l = new List<string>();
            if (h.IsDeleted) return l;
            long id = Id(h);
            if (id <= 0) l.Add(IdField(h) + " \"" + IdText(h) + "\" is not an id");
            int same = 0;
            foreach (DropHolder o in h.IsGroup ? groups : boxes) if (!o.IsDeleted && IdText(o) == IdText(h)) same++;
            if (same > 1) l.Add("the table has " + same + (h.IsGroup ? " groups" : " boxes") + " with the id " + IdText(h));
            List<DropLine> lines = Lines(h);
            int live = 0;
            foreach (DropLine line in lines) if (!line.IsDeleted) live++;
            if (live == 0) l.Add(h.IsGroup ? "the group has no item" : "the box has no line: it drops nothing");
            if (h.IsGroup && live > 0)
            {
                // weights: a group either leaves them out or gives some item one above 0
                bool any = false, above = false;
                foreach (DropLine line in lines)
                {
                    long w;
                    if (line.IsDeleted || !Has(line, GlobalDropRules.Weight)) continue;
                    any = true;
                    if (GlobalDropRules.Whole(Get(line, GlobalDropRules.Weight), out w) && w > 0) above = true;
                }
                if (any && !above) l.Add("every weight of the group is 0");
                if (h.IsAdded && BoxesUsing(id).Count == 0) l.Add("no box has a line for the group yet: nothing drops from it");
            }
            return l;
        }

        // ---- editing ----

        static string Esc(string v) { return XmlDoc.EscapeAttr(v, '"'); }

        static string Xml(string name, IEnumerable<KeyValuePair<string, string>> attrs)
        {
            var sb = new StringBuilder("<" + name);
            foreach (KeyValuePair<string, string> kv in attrs) sb.Append(' ').Append(kv.Key).Append("=\"").Append(Esc(kv.Value)).Append('"');
            return sb.Append(" />").ToString();
        }

        static KeyValuePair<string, string> A(string name, string value) { return new KeyValuePair<string, string>(name, value); }

        // writes a holder the mod adds as one <add> with its lines inside
        void Rewrite(DropHolder h)
        {
            TableDraft d = TableOf(h);
            RowOp add = h.Row.Add;
            var self = new List<KeyValuePair<string, string>>();
            var names = new List<string>();
            foreach (XmlAttr a in h.Row.Base.Attrs) names.Add(a.Name);
            foreach (AttrOp a in add.Attrs)
            {
                if (a.At != null) throw new ModKitException("the <add> of " + h.Row.Label + " has lines for nested rows; change it in the mod's text");
                if (!names.Contains(a.Name)) names.Add(a.Name);
            }
            foreach (string n in names)
            {
                string v = d.Get(h.Row, n);
                if (v != null) self.Add(A(n, v));
            }
            string text = Xml(GlobalDropRules.Holder, self);
            if (h.Own.Count > 0)
            {
                var sb = new StringBuilder(text.Substring(0, text.Length - 3) + ">\n");
                foreach (DropLine line in h.Own) sb.Append('\t').Append(Xml(GlobalDropRules.Line, line.Attrs)).Append('\n');
                text = sb.Append("</").Append(GlobalDropRules.Holder).Append('>').ToString();
            }
            add.Xml = text;
            add.Copy = null;
            add.Attrs.Clear();
            h.Row.Base = XmlDoc.ParseFragment(text, "the new row").Root;
        }

        /// <summary>Sets the comment of a box or group.</summary>
        public void SetComment(DropHolder h, string value)
        {
            if (h.IsDeleted) throw new ModKitException("it is removed; restore it first");
            value = (value ?? "").Trim();
            TableOf(h).Set(h.Row, GlobalDropRules.Comment, value.Length == 0 ? null : value);   // none = no attribute
            if (h.Own != null) Rewrite(h);
        }

        /// <summary>Sets a field of a line ("" takes the attribute out).</summary>
        public void Set(DropLine line, string field, string value)
        {
            if (!TableDraft.IsAttrName(field)) throw new ModKitException("\"" + field + "\" is not an attribute name");
            if (line.IsDeleted) throw new ModKitException("the line is removed; restore it first");
            value = (value ?? "").Trim();
            if (line.Attrs == null) { DraftOf(line).Set(line.Row, field, value.Length == 0 ? null : value); return; }
            int at = line.Attrs.FindIndex(kv => kv.Key == field);
            if (value.Length == 0) { if (at >= 0) line.Attrs.RemoveAt(at); }
            else if (at >= 0) line.Attrs[at] = A(field, value);
            else line.Attrs.Add(A(field, value));
            Rewrite(line.Holder);
        }

        DropLine AddLine(DropHolder h, List<KeyValuePair<string, string>> attrs)
        {
            if (h.IsDeleted) throw new ModKitException("it is removed; restore it first");
            var line = new DropLine(h);
            if (h.Own != null)
            {
                line.Attrs = attrs;
                h.Own.Add(line);
                Rewrite(h);
                return line;
            }
            Lines(h);       // opens it
            line.Row = (h.IsGroup ? openGroupDraft : openBoxDraft).AddRow(Xml(GlobalDropRules.Line, attrs));
            (h.IsGroup ? openGroupLines : openBoxLines).Add(line);
            return line;
        }

        /// <summary>A new line of a box for an item group: one item at a chance of 10 in 100 (dropCount 0,1 at 9000,1000), for every monster level.</summary>
        public DropLine AddLink(DropHolder box, long group)
        {
            if (box.IsGroup) throw new ModKitException("a line for a group goes into a box");
            if (group <= 0) throw new ModKitException("give the id of an item group");
            return AddLine(box, new List<KeyValuePair<string, string>> {
                A(GlobalDropRules.Link, group.ToString(CultureInfo.InvariantCulture)), A(GlobalDropRules.MinLevel, "1"),
                A(GlobalDropRules.Count, "0,1"), A(GlobalDropRules.Chance, "9000,1000") });
        }

        /// <summary>A new item of a group, for every monster level.</summary>
        public DropLine AddItem(DropHolder group, long item)
        {
            if (!group.IsGroup) throw new ModKitException("an item goes into an item group");
            if (item <= 0) throw new ModKitException("give an item id");
            return AddLine(group, new List<KeyValuePair<string, string>> { A(GlobalDropRules.Item, item.ToString(CultureInfo.InvariantCulture)), A(GlobalDropRules.MinLevel, "1") });
        }

        /// <summary>A copy of a line in its own box or group.</summary>
        public DropLine Clone(DropLine line)
        {
            if (line.IsDeleted) throw new ModKitException("the line is removed; restore it before copying");
            var attrs = new List<KeyValuePair<string, string>>();
            if (line.Attrs != null) attrs.AddRange(line.Attrs);
            else
            {
                TableDraft d = DraftOf(line);
                var names = new List<string>();
                foreach (XmlAttr a in line.Row.Base.Attrs) names.Add(a.Name);
                foreach (string c in d.Columns) if (!names.Contains(c)) names.Add(c);
                foreach (string n in names) if (d.Get(line.Row, n) != null) attrs.Add(A(n, d.Get(line.Row, n)));
            }
            return AddLine(line.Holder, attrs);
        }

        /// <summary>Removes a line: a new one goes away, a shipped one is deleted and can be restored.</summary>
        public void Remove(DropLine line)
        {
            if (line.Attrs != null)
            {
                line.Holder.Own.Remove(line);
                Rewrite(line.Holder);
                return;
            }
            bool mine = line.Row.IsAdded;
            DraftOf(line).Delete(line.Row);
            if (mine) (line.Holder.IsGroup ? openGroupLines : openBoxLines).Remove(line);
        }

        /// <summary>Takes back the removal of a shipped line.</summary>
        public void Restore(DropLine line)
        {
            if (line.Attrs == null) DraftOf(line).Restore(line.Row);
        }

        /// <summary>Drops what the mod changes in a shipped line.</summary>
        public void Revert(DropLine line)
        {
            if (line.IsAdded) throw new ModKitException("the line is new; remove it instead");
            DraftOf(line).Restore(line.Row);
            DraftOf(line).RevertCells(line.Row);
        }

        /// <summary>The lowest id above every id a table has.</summary>
        public long FreeId(bool group)
        {
            long max = 0;
            foreach (DropHolder h in group ? groups : boxes) max = Math.Max(max, Id(h));
            return max + 1;
        }

        DropHolder AddHolder(bool group, long id, string comment, DropHolder from)
        {
            string what = group ? "item group" : "global drop box";
            if (id <= 0) throw new ModKitException("give the new " + what + " an id above 0");
            if (ById(group ? groups : boxes, id) != null) throw new ModKitException("there is a " + what + " " + id + " already");
            if (from != null && from.IsDeleted) throw new ModKitException("it is removed; restore it before copying");
            List<DropLine> source = from == null ? new List<DropLine>() : Lines(from);
            TableDraft d = group ? SetTable : BoxTable;
            var attrs = new List<KeyValuePair<string, string>> { A(group ? GlobalDropRules.GroupId : GlobalDropRules.BoxId, id.ToString(CultureInfo.InvariantCulture)) };
            comment = (comment ?? "").Trim();
            if (comment.Length > 0) attrs.Add(A(GlobalDropRules.Comment, comment));
            var h = new DropHolder(group, d.AddRow(Xml(GlobalDropRules.Holder, attrs))) { Own = new List<DropLine>() };
            foreach (DropLine s in source)
            {
                if (s.IsDeleted) continue;
                var line = new DropLine(h) { Attrs = new List<KeyValuePair<string, string>>() };
                if (s.Attrs != null) line.Attrs.AddRange(s.Attrs);
                else
                {
                    TableDraft sd = DraftOf(s);
                    var names = new List<string>();
                    foreach (XmlAttr a in s.Row.Base.Attrs) names.Add(a.Name);
                    foreach (string c in sd.Columns) if (!names.Contains(c)) names.Add(c);
                    foreach (string n in names) if (sd.Get(s.Row, n) != null) line.Attrs.Add(A(n, sd.Get(s.Row, n)));
                }
                h.Own.Add(line);
            }
            if (h.Own.Count > 0) Rewrite(h);
            (group ? groups : boxes).Add(h);
            return h;
        }

        /// <summary>A new, empty global drop box.</summary>
        public DropHolder AddBox(long id, string comment) { return AddHolder(false, id, comment, null); }
        /// <summary>A new, empty item group.</summary>
        public DropHolder AddGroup(long id, string comment) { return AddHolder(true, id, comment, null); }

        /// <summary>A new box or group with the lines of another, as the mod leaves them.</summary>
        public DropHolder Clone(DropHolder from, long id, string comment) { return AddHolder(from.IsGroup, id, comment, from); }

        /// <summary>Removes a box or group: a new one goes away, a shipped one is deleted (with what the mod changed in it) and can be restored.</summary>
        public void Remove(DropHolder h)
        {
            if (h.IsAdded)
            {
                TableOf(h).Delete(h.Row);
                (h.IsGroup ? groups : boxes).Remove(h);
                return;
            }
            if (h.IsDeleted) return;
            // the lines the mod has for the element go first: a line for a deleted element does not apply
            foreach (DropLine line in Lines(h))
            {
                if (line.Row.IsAdded) DraftOf(line).Delete(line.Row);
                else { DraftOf(line).Restore(line.Row); DraftOf(line).RevertCells(line.Row); }
            }
            if (h.IsGroup) { openGroup = null; openGroupDraft = null; openGroupLines = null; }
            else { openBox = null; openBoxDraft = null; openBoxLines = null; }
            TableOf(h).Delete(h.Row);
        }

        /// <summary>Takes back the removal of a shipped box or group.</summary>
        public void Restore(DropHolder h) { TableOf(h).Restore(h.Row); }
    }
}
