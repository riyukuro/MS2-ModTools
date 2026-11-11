// The map file of the game (.xblock): <game><entitySet><entity id modelName name iterations><property name><set
// [index] value/>... Entities carry only the properties that differ from the model's defaults.
using System;
using System.Collections.Generic;
using MS2.ModKit.Xml;

namespace MS2.ModKit.MapEdit
{
    /// <summary>One set of a property: Index is null for a plain value.</summary>
    public struct XSet
    {
        public string Index, Value;
        public XSet(string index, string value) { Index = index; Value = value; }
    }

    public sealed class XProp
    {
        public string Name;
        public List<XSet> Items = new List<XSet>();

        public XProp(string name) { Name = name; }
        public XProp(string name, string value) { Name = name; Items.Add(new XSet(null, value)); }
        public XProp(string name, List<XSet> items) { Name = name; Items = items; }

        /// <summary>One value without an index.</summary>
        public bool Scalar { get { return Items.Count == 1 && Items[0].Index == null; } }
    }

    public sealed class XEntity
    {
        public string Id;           // 32 hex digits
        public string Model, Name;
        public int Iterations;
        public List<XProp> Props = new List<XProp>();

        public XProp Find(string name)
        {
            foreach (XProp p in Props) if (p.Name == name) return p;
            return null;
        }
    }

    public enum XDialect
    {
        /// <summary>Tab indent, attributes on the line of their tag.</summary>
        Tab,
        /// <summary>Two-space indent, every attribute on its own line.</summary>
        Spaced
    }

    public static class XBlock
    {
        public static XDialect DetectDialect(byte[] bytes)
        {
            byte[] mark = System.Text.Encoding.ASCII.GetBytes("<game>\r\n  <entitySet>");
            int n = Math.Min(bytes.Length, 300);
            for (int i = 0; i + mark.Length <= n; i++)
            {
                int k = 0;
                while (k < mark.Length && bytes[i + k] == mark[k]) k++;
                if (k == mark.Length) return XDialect.Spaced;
            }
            return XDialect.Tab;
        }

        public static XDialect ParseDialect(string s)
        {
            if (s == "tab") return XDialect.Tab;
            if (s == "toolbench" || s == "spaced") return XDialect.Spaced;
            throw new ModKitException("unknown dialect \"" + s + "\" (tab or toolbench)");
        }

        public static List<XEntity> Read(byte[] bytes, string label)
        {
            XmlDoc doc = XmlDoc.Parse(bytes, label);
            var res = new List<XEntity>();
            Collect(doc.Root, res, label);
            return res;
        }

        static void Collect(XmlElem e, List<XEntity> res, string label)
        {
            if (e.Name == "entity") { res.Add(Entity(e, label)); return; }
            foreach (XmlElem c in e.Children) Collect(c, res, label);
        }

        internal static XEntity Entity(XmlElem e, string label)
        {
            var x = new XEntity { Id = e.Attr("id"), Model = e.Attr("modelName"), Name = e.Attr("name") };
            string it = e.Attr("iterations");
            if (it == null || !int.TryParse(it.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out x.Iterations))
                throw new ModKitException(label + " line " + e.Line + ": entity " + x.Id + " has no whole number in iterations");
            foreach (XmlElem p in e.Children)
            {
                var xp = new XProp(p.Attr("name"));
                foreach (XmlElem s in p.Children) xp.Items.Add(new XSet(s.Attr("index"), s.Attr("value")));
                x.Props.Add(xp);
            }
            return x;
        }

        static string Indent(XDialect d) { return d == XDialect.Tab ? "\t" : "  "; }

        public static string Text(IEnumerable<XEntity> entities, XDialect dialect)
        {
            var w = new NetXmlWriter(Indent(dialect), dialect == XDialect.Spaced);
            w.Start("game");
            w.Start("entitySet");
            foreach (XEntity e in entities) WriteEntity(w, e);
            w.End();
            w.End();
            return w.Finish();
        }

        public static byte[] Bytes(IEnumerable<XEntity> entities, XDialect dialect) { return NetXmlWriter.Bytes(Text(entities, dialect)); }

        static void WriteEntity(NetXmlWriter w, XEntity e)
        {
            w.Start("entity", "id", e.Id, "modelName", e.Model, "name", e.Name, "iterations", e.Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (XProp p in e.Props) WriteProp(w, p);
            w.End();
        }

        internal static void WriteProp(NetXmlWriter w, XProp p)
        {
            w.Start("property", "name", p.Name);
            foreach (XSet s in p.Items)
            {
                if (s.Index == null) w.Empty("set", "value", s.Value);
                else w.Empty("set", "index", s.Index, "value", s.Value);
            }
            w.End();
        }

        /// <summary>The text of one entity as it stands inside a file of this dialect, from its '&lt;' to its last '&gt;'
        /// (lines after the first carry the indent of depth 2).</summary>
        public static string EntityText(XEntity e, XDialect dialect)
        {
            var w = new NetXmlWriter(Indent(dialect), dialect == XDialect.Spaced, false);
            w.Start("game"); w.Start("entitySet");
            WriteEntity(w, e);
            string t = w.Finish0();
            return t.Substring(t.IndexOf("<entity", t.IndexOf("<entitySet", StringComparison.Ordinal) + 10, StringComparison.Ordinal));
        }

        /// <summary>The text of one property inside an entity of a file of this dialect (depth 3).</summary>
        public static string PropText(XProp p, XDialect dialect)
        {
            var w = new NetXmlWriter(Indent(dialect), dialect == XDialect.Spaced, false);
            w.Start("game"); w.Start("entitySet"); w.Start("entity");
            WriteProp(w, p);
            string t = w.Finish0();
            return t.Substring(t.IndexOf("<property", StringComparison.Ordinal));
        }

        // ---- property order ----

        /// <summary>The order of the shipped files: by lower-cased name, the property "Name" last.</summary>
        public static List<string> ShippedOrder(IList<string> names)
        {
            var rest = new List<string>();
            bool hasName = false;
            foreach (string n in names) { if (n == "Name") hasName = true; else rest.Add(n); }
            Stable.Sort(rest, delegate (string a, string b) { return string.CompareOrdinal(a.ToLowerInvariant(), b.ToLowerInvariant()); });
            if (hasName) rest.Add("Name");
            return rest;
        }

        /// <summary>shipped true: ShippedOrder; false: as they are. original (entity id -> property names): an entity
        /// that is there with the same set of names takes that order.</summary>
        public static void ReorderProps(List<XEntity> entities, bool shipped, IDictionary<string, List<string>> original)
        {
            foreach (XEntity e in entities)
            {
                if (!shipped && original == null) continue;
                var names = new List<string>();
                foreach (XProp p in e.Props) names.Add(p.Name);
                List<string> o = null, order;
                if (original != null) original.TryGetValue(e.Id, out o);
                if (o != null && SameSet(o, names)) order = o;
                else if (shipped) order = ShippedOrder(names);
                else continue;
                var pos = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < order.Count; i++) if (!pos.ContainsKey(order[i])) pos[order[i]] = i;
                Stable.Sort(e.Props, delegate (XProp a, XProp b) { return pos[a.Name].CompareTo(pos[b.Name]); });
            }
        }

        static bool SameSet(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            var x = new List<string>(a); var y = new List<string>(b);
            x.Sort(StringComparer.Ordinal); y.Sort(StringComparer.Ordinal);
            for (int i = 0; i < x.Count; i++) if (x[i] != y[i]) return false;
            return true;
        }
    }
}
