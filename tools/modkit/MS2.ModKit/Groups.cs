// Group entities: one id, many rows. A shop is a header row (shop_game_info) and stock rows (shop_game, one <item>
// per row); a drop box is groups of item rows. A mod keeps row operations on a group, not a copy of it, so that it
// still applies where the sides ship different rows:
//
//   <shop id="..."> <header><set .../></header> <row sn="3"><set .../></row> <add><xml><item .../></xml></add> <remove sn="4"/> </shop>
//   <shop id="..." new="true"> <xml><shop categoryID="..."/></xml> <add>...</add> </shop>
//   <shop id="..." clone="..."> overrides as above </shop>
//   <dropbox id="..."> <row group="1" item="..." n="1"><set .../></row> <group id="1"><set .../></group>
//                      <add group="1"><xml><v itemID="..."/></xml></add> <add><xml><group dropGroupID="2">...</group></xml></add>
//                      <remove group="1" item="..." n="1"/> <remove group="2"/> </dropbox>
//   <dropbox id="..." new="true" split="table/individualitemdrop.xml"> <xml><dropBox>...</dropBox></xml> </dropbox>
//   <dropbox id="..." clone="..."> overrides </dropbox>
//
// Row identity: a stock row is its sn (with feature and locale when variants share an sn); a drop row is
// (group, item, the n-th row of that pair in the box). Rows that share an item are never merged.
// The planner (PlannerGroups.cs) applies each op on every target where its row is found.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public enum GroupKind { Shop, DropBox }

    public enum GroupRowKind { Set, Add, Remove, GroupSet }

    public sealed class GroupRowOp
    {
        public GroupRowKind Kind;
        /// <summary>Shop rows: the sn; null on an add = the next free sn.</summary>
        public string Sn;
        /// <summary>Shop rows: tell apart the variants of one sn; null = not given.</summary>
        public string Feature, Locale;
        /// <summary>Drop rows: the dropGroupID and the itemID of the row; Item null on a group op or a whole-group remove.</summary>
        public string Group, Item;
        /// <summary>Drop rows: the n-th row of (group, item) in the box, from 1.</summary>
        public int N = 1;
        public readonly List<AttrOp> Attrs = new List<AttrOp>();
        /// <summary>Add: the new element (an &lt;item&gt; of a shop; a &lt;v&gt; or a whole &lt;group&gt; of a drop box).</summary>
        public string Xml;
        public int Line;

        public string Describe(GroupKind kind)
        {
            if (kind == GroupKind.Shop)
                return "sn " + (Sn ?? "(new)") + (Feature != null ? " feature \"" + Feature + "\"" : "") + (Locale != null ? " locale \"" + Locale + "\"" : "");
            if (Item == null) return "group " + Group;
            return "group " + Group + " item " + Item + (N > 1 ? " (" + Ord(N) + " row of that item)" : "");
        }

        internal static string Ord(int n)
        {
            int t = n % 100;
            string s = t >= 11 && t <= 13 ? "th" : n % 10 == 1 ? "st" : n % 10 == 2 ? "nd" : n % 10 == 3 ? "rd" : "th";
            return n.ToString(CultureInfo.InvariantCulture) + s;
        }
    }

    public sealed class GroupOp
    {
        public GroupKind Kind;
        public long Id;
        /// <summary>The group is written whole from Xml and the add rows (a new shop or box).</summary>
        public bool New;
        /// <summary>The id of the group this one is a copy of; -1 = none.</summary>
        public long Clone = -1;
        /// <summary>Drop boxes: the flat split file a new or cloned box goes into; null = the planner's default.</summary>
        public string Split;
        /// <summary>Shops: the header row (shop_game_info) of a new shop; drop boxes: the &lt;dropBox&gt; element of a new box.</summary>
        public string Xml;
        /// <summary>Changes of the header: the shop_game_info row(s) of a shop, the &lt;dropBox&gt; element of a box.</summary>
        public readonly List<AttrOp> Header = new List<AttrOp>();
        /// <summary>Shops: the header variant the changes are for; null = every row of the shop.</summary>
        public string HeaderFeature, HeaderLocale;
        public readonly List<GroupRowOp> Rows = new List<GroupRowOp>();
        public int Line;

        public bool IsClone { get { return Clone >= 0; } }
        public string Noun { get { return Kind == GroupKind.Shop ? "shop" : "drop box"; } }
        public string Tag { get { return Kind == GroupKind.Shop ? "shop" : "dropbox"; } }
        public override string ToString() { return Noun + " " + Id.ToString(CultureInfo.InvariantCulture); }
    }

    public sealed partial class Mod
    {
        /// <summary>The group op of a kind and id, or null.</summary>
        public GroupOp FindGroup(GroupKind kind, long id)
        {
            foreach (GroupOp g in Groups) if (g.Kind == kind && g.Id == id) return g;
            return null;
        }

        sealed partial class Reader
        {
            internal GroupOp ReadGroup(XmlElem e, GroupKind kind)
            {
                bool shop = kind == GroupKind.Shop;
                if (shop) Only(e, "id", "new", "clone");
                else Only(e, "id", "new", "clone", "split");
                var g = new GroupOp { Kind = kind, Id = Num(e, "id"), New = Flag(e, "new", false), Line = e.Line };
                if (e.Attr("clone") != null) g.Clone = Num(e, "clone");
                if (g.New && g.IsClone) throw Bad(e, "<" + e.Name + "> takes new=\"true\" or clone=\"...\", not both");
                if (g.IsClone && g.Clone == g.Id) throw Bad(e, "<" + e.Name + "> cannot be a clone of itself");
                g.Split = e.Attr("split");
                if (g.Split != null)
                {
                    g.Split = g.Split.Replace('\\', '/');
                    if (!g.New && !g.IsClone) throw Bad(e, "split=\"...\" is for a new or cloned drop box; the rows of a shipped box stay in the file that holds them");
                }
                foreach (XmlElem c in e.Children)
                {
                    switch (c.Name)
                    {
                        case "xml":
                            if (!g.New) throw Bad(c, "<xml> directly in <" + e.Name + "> is the " + (shop ? "header row of a new shop" : "whole new box") + "; it needs new=\"true\"");
                            if (g.Xml != null) throw Bad(c, "a second <xml>");
                            g.Xml = XmlText(c);
                            break;
                        case "header":
                            if (shop) Only(c, "feature", "locale"); else Only(c);
                            g.HeaderFeature = c.Attr("feature"); g.HeaderLocale = c.Attr("locale");
                            g.Header.AddRange(ReadSets(c));
                            if (g.Header.Count == 0) throw Bad(c, "<header> without <set> or <unset>");
                            break;
                        case "row":
                        case "group":
                        case "remove":
                        case "add":
                            g.Rows.Add(ReadGroupRow(c, kind));
                            break;
                        default: throw Bad(c, "unknown element <" + c.Name + "> in <" + e.Name + "> (known: " + (g.New ? "xml, " : "") + "header, row, " + (shop ? "" : "group, ") + "add, remove)");
                    }
                }
                if (g.New && g.Xml == null && !shop) throw Bad(e, "a new drop box needs <xml> with its <dropBox> element");
                if (g.New && g.Xml == null && shop) throw Bad(e, "a new shop needs <xml> with its header row, for example <shop categoryID=\"...\" />");
                if (!g.New && !g.IsClone && g.Header.Count == 0 && g.Rows.Count == 0) throw Bad(e, "<" + e.Name + "> changes nothing");
                return g;
            }

            string XmlText(XmlElem s)
            {
                string x;
                if (s.Children.Count > 0)
                {
                    if (s.Children.Count != 1) throw Bad(s, "<xml> holds exactly one element");
                    x = s.Children[0].OuterText;
                }
                else x = (s.InnerText ?? "").Trim();
                if (x.Length == 0) throw Bad(s, "<xml> is empty");
                return x;
            }

            List<AttrOp> ReadSets(XmlElem c)
            {
                var list = new List<AttrOp>();
                foreach (XmlElem s in c.Children)
                {
                    var a = new AttrOp { Line = s.Line };
                    switch (s.Name)
                    {
                        case "set": Only(s, "name", "value"); a.Kind = AttrOpKind.Set; a.Name = Need(s, "name"); a.Value = s.Attr("value"); if (a.Value == null) throw Bad(s, "<set> needs value=\"...\" (it may be empty)"); break;
                        case "unset": Only(s, "name"); a.Kind = AttrOpKind.Unset; a.Name = Need(s, "name"); break;
                        case "xml": continue;
                        default: throw Bad(s, "unknown element <" + s.Name + "> in <" + c.Name + "> (known: set, unset)");
                    }
                    list.Add(a);
                }
                return list;
            }

            GroupRowOp ReadGroupRow(XmlElem c, GroupKind kind)
            {
                bool shop = kind == GroupKind.Shop;
                var r = new GroupRowOp { Line = c.Line };
                switch (c.Name)
                {
                    case "row": r.Kind = GroupRowKind.Set; break;
                    case "group": if (shop) throw Bad(c, "a shop has no groups"); r.Kind = GroupRowKind.GroupSet; break;
                    case "add": r.Kind = GroupRowKind.Add; break;
                    default: r.Kind = GroupRowKind.Remove; break;
                }
                if (shop)
                {
                    Only(c, "sn", "feature", "locale");
                    r.Sn = c.Attr("sn"); r.Feature = c.Attr("feature"); r.Locale = c.Attr("locale");
                    if (r.Sn == null && r.Kind != GroupRowKind.Add) throw Bad(c, "<" + c.Name + "> needs sn=\"...\"");
                    if (r.Sn != null) Num(c, "sn");
                    if (r.Kind == GroupRowKind.Add && (r.Feature != null || r.Locale != null)) throw Bad(c, "feature and locale of a new row go into its <xml>");
                }
                else if (r.Kind == GroupRowKind.GroupSet)
                {
                    Only(c, "id");
                    r.Group = Need(c, "id");
                }
                else if (r.Kind == GroupRowKind.Add)
                {
                    Only(c, "group");
                    r.Group = c.Attr("group");
                }
                else
                {
                    Only(c, "group", "item", "n");
                    r.Group = Need(c, "group"); r.Item = c.Attr("item");
                    if (c.Attr("n") != null) { long n = Num(c, "n"); if (n < 1 || n > 100000) throw Bad(c, "n=\"" + n + "\" counts from 1"); r.N = (int)n; }
                    if (r.Item == null && r.Kind == GroupRowKind.Set) throw Bad(c, "<row> needs item=\"...\"; use <group id=\"...\"> to change the group's own attributes");
                    if (r.Item == null && c.Attr("n") != null) throw Bad(c, "n=\"...\" counts the rows of one item; give item=\"...\" too");
                }
                foreach (XmlElem s in c.Children)
                    if (s.Name == "xml")
                    {
                        if (r.Kind != GroupRowKind.Add) throw Bad(s, "<xml> belongs in <add>");
                        if (r.Xml != null) throw Bad(s, "a second <xml> in one <add>");
                        r.Xml = XmlText(s);
                    }
                r.Attrs.AddRange(ReadSets(c));
                if (r.Kind == GroupRowKind.Add && r.Xml == null) throw Bad(c, "<add> needs an <xml> child with the new row");
                if (r.Kind == GroupRowKind.Remove && c.Children.Count > 0) throw Bad(c, "<remove> has no child elements");
                if ((r.Kind == GroupRowKind.Set || r.Kind == GroupRowKind.GroupSet) && r.Attrs.Count == 0) throw Bad(c, "<" + c.Name + "> without <set> or <unset>");
                return r;
            }
        }

        static void WriteSets(StringBuilder sb, string indent, List<AttrOp> attrs)
        {
            foreach (AttrOp a in attrs)
            {
                sb.Append(indent).Append(a.Kind == AttrOpKind.Unset ? "<unset" : "<set");
                A(sb, "name", a.Name);
                if (a.Kind != AttrOpKind.Unset) A(sb, "value", a.Value);
                sb.Append(" />\n");
            }
        }

        void WriteXml(StringBuilder sb, string indent, string xml)
        {
            if (xml.IndexOf("]]>", StringComparison.Ordinal) >= 0) throw new ModKitException("mod " + Id + ": literal XML with \"]]>\" cannot be saved");
            sb.Append(indent).Append("<xml><![CDATA[").Append(xml).Append("]]></xml>\n");
        }

        void WriteGroup(StringBuilder sb, GroupOp g)
        {
            sb.Append("  <").Append(g.Tag); A(sb, "id", g.Id.ToString(CultureInfo.InvariantCulture));
            if (g.New) A(sb, "new", "true");
            if (g.IsClone) A(sb, "clone", g.Clone.ToString(CultureInfo.InvariantCulture));
            A(sb, "split", g.Split);
            sb.Append(">\n");
            if (g.Xml != null) WriteXml(sb, "    ", g.Xml);
            if (g.Header.Count > 0)
            {
                sb.Append("    <header"); A(sb, "feature", g.HeaderFeature); A(sb, "locale", g.HeaderLocale); sb.Append(">\n");
                WriteSets(sb, "      ", g.Header);
                sb.Append("    </header>\n");
            }
            foreach (GroupRowOp r in g.Rows)
            {
                string tag = r.Kind == GroupRowKind.Set ? "row" : r.Kind == GroupRowKind.GroupSet ? "group" : r.Kind == GroupRowKind.Add ? "add" : "remove";
                sb.Append("    <").Append(tag);
                if (g.Kind == GroupKind.Shop) { A(sb, "sn", r.Sn); A(sb, "feature", r.Feature); A(sb, "locale", r.Locale); }
                else if (r.Kind == GroupRowKind.GroupSet) A(sb, "id", r.Group);
                else
                {
                    A(sb, "group", r.Group); A(sb, "item", r.Item);
                    if (r.Item != null && r.Kind != GroupRowKind.Add && r.N != 1) A(sb, "n", r.N.ToString(CultureInfo.InvariantCulture));
                }
                if (r.Xml == null && r.Attrs.Count == 0) { sb.Append(" />\n"); continue; }
                sb.Append(">\n");
                if (r.Xml != null) WriteXml(sb, "      ", r.Xml);
                WriteSets(sb, "      ", r.Attrs);
                sb.Append("    </").Append(tag).Append(">\n");
            }
            sb.Append("  </").Append(g.Tag).Append(">\n");
        }
    }
}
