// The rows of other tables that belong to one id of an entity: the "ref" lines of its field map name the tables
// and the attribute that holds the key. Used to show an item's option and drop rows, which live in tables that
// only the server has. The data as shipped is read; a mod's own changes to those tables are not looked at.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>One "ref" line of a field map.</summary>
    public sealed class RefRule
    {
        public string Name, Archive, Entries, Attribute;
        /// <summary>The field of the server file that holds the key (element@attribute); null = the id itself.</summary>
        public string Key;
        /// <summary>Evidence: of how many ids with a key the key was found in these tables.</summary>
        public int Of, Hit;
    }

    public sealed class RefHit
    {
        public RefRule Rule;
        public string Archive, Entry, Selector, KeyValue;
        public int Line;
        /// <summary>The row's attributes as text.</summary>
        public string Row;
    }

    public sealed class RefList
    {
        public readonly List<RefHit> Hits = new List<RefHit>();
        public readonly List<string> Notes = new List<string>();

        public string ToText()
        {
            var sb = new StringBuilder();
            RefRule last = null;
            foreach (RefHit h in Hits)
            {
                if (h.Rule != last) sb.Append(h.Rule.Name).Append(" (").Append(h.Rule.Attribute).Append(" = ").Append(h.KeyValue).Append("):\r\n");
                last = h.Rule;
                sb.Append("  ").Append(h.Archive).Append(' ').Append(h.Entry).Append("  ").Append(h.Selector).Append("\r\n      ").Append(h.Row).Append("\r\n");
            }
            foreach (string n in Notes) sb.Append("note: ").Append(n).Append("\r\n");
            if (Hits.Count == 0) sb.Append("no row of another table refers to it\r\n");
            return sb.ToString();
        }
    }

    public static class ServerRefs
    {
        /// <summary>The rows that the ref lines of the map find for an id. maxPerRule limits the rows listed for one line.</summary>
        public static RefList Find(DataSet data, EntityMap em, long id, int maxPerRule)
        {
            if (em == null) throw new ArgumentNullException("em");
            var list = new RefList();
            if (em.Refs.Count == 0) { list.Notes.Add("the field map of \"" + em.Name + "\" has no ref line"); return list; }
            string idText = id.ToString(CultureInfo.InvariantCulture);
            XmlElem under = null;
            bool looked = false;
            foreach (RefRule rule in em.Refs)
            {
                string key = idText;
                if (rule.Key != null)
                {
                    if (!looked)
                    {
                        looked = true;
                        under = ServerUnder(data, em, id);
                        if (under == null) list.Notes.Add("the server has no entry of this id, so the keys it would hold are not known");
                    }
                    if (under == null) continue;
                    key = EntityMap.ValueAt(under, rule.Key);
                    if (string.IsNullOrEmpty(key) || key == "0") continue;
                }
                if (!data.Exists(rule.Archive)) { Note(list, rule.Archive + " is not in the data set"); continue; }
                M2dArchive arch = data.Open(rule.Archive);
                Regex rx = IdSpaces.EntryRegex(rule.Entries);
                byte[] needle = Encoding.ASCII.GetBytes(rule.Attribute + "=\"" + key + "\"");
                int n = 0, more = 0;
                foreach (M2dEntry e in arch.Entries)
                {
                    if (!rx.IsMatch(e.Name)) continue;
                    byte[] bytes = arch.Read(e);
                    if (IndexOf(bytes, needle) < 0) continue;
                    XmlDoc doc = XmlDoc.Parse(bytes, rule.Archive + " " + e.Name);
                    if (doc.Root == null) continue;
                    var found = new List<XmlElem>();
                    Collect(doc.Root, rule.Attribute, key, found);
                    foreach (XmlElem x in found)
                    {
                        if (n >= maxPerRule) { more++; continue; }
                        n++;
                        list.Hits.Add(new RefHit { Rule = rule, Archive = rule.Archive, Entry = e.Name, Selector = RowPath.Canonical(x), KeyValue = key, Line = x.Line, Row = RowText(x) });
                    }
                }
                if (more > 0) list.Notes.Add(rule.Name + ": " + more + " more row(s) are not listed");
            }
            return list;
        }

        static void Note(RefList l, string text) { if (!l.Notes.Contains(text)) l.Notes.Add(text); }

        static XmlElem ServerUnder(DataSet data, EntityMap em, long id)
        {
            if (!data.Exists(em.Server.Archive)) return null;
            M2dArchive arch = data.Open(em.Server.Archive);
            var names = new List<string>();
            foreach (M2dEntry e in arch.Entries) names.Add(e.Name);
            string entry = em.Server.EntryOf(id, names);
            if (entry == null) return null;
            XmlDoc doc = XmlDoc.Parse(arch.Read(arch.Find(entry)), em.Server.Archive + " " + entry);
            List<XmlElem> u = em.UnderElems(false, doc.Root, id);
            return u.Count > 0 ? u[0] : null;
        }

        static void Collect(XmlElem e, string attribute, string key, List<XmlElem> found)
        {
            if (e.Attr(attribute) == key) found.Add(e);
            foreach (XmlElem c in e.Children) Collect(c, attribute, key, found);
        }

        static string RowText(XmlElem e)
        {
            var sb = new StringBuilder();
            foreach (XmlAttr a in e.Attrs)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(a.Name).Append("=\"").Append(a.Value).Append('"');
            }
            if (e.Children.Count > 0) sb.Append("  (").Append(e.Children.Count).Append(" children)");
            return sb.ToString();
        }

        static int IndexOf(byte[] hay, byte[] needle)
        {
            int end = hay.Length - needle.Length;
            for (int i = 0; i <= end; i++)
            {
                if (hay[i] != needle[0]) continue;
                int k = 1;
                while (k < needle.Length && hay[i + k] == needle[k]) k++;
                if (k == needle.Length) return i;
            }
            return -1;
        }
    }
}
