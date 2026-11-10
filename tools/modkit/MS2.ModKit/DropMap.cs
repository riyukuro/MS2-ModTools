// The field map of drop boxes between the nested layout (<dropBox>/<group>/<v>) and the flat layout (one
// <individualDropBox> per item row). Read from Maps/drop.dropmap.xml (embedded); see the comment there.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public sealed class DropScore
    {
        public string NestedSide, FlatSide, Common, Comparable, Agree, PctAll, PctComparable;
    }

    public sealed class DropField
    {
        /// <summary>0 = box, 1 = group, 2 = item row.</summary>
        public int Level;
        public string Nested, Flat;
        /// <summary>The values are booleans: nested 1/0, flat True/False.</summary>
        public bool Bool;
        public readonly List<DropScore> Scores = new List<DropScore>();

        public string ToFlat(string v)
        {
            if (!Bool || v == null) return v;
            if (v == "1") return "True";
            if (v == "0") return "False";
            return v;
        }

        public string ToNested(string v)
        {
            if (!Bool || v == null) return v;
            if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) return "1";
            if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)) return "0";
            return v;
        }
    }

    public sealed class DropMap
    {
        public readonly List<DropField> Fields = new List<DropField>();
        /// <summary>Nested fields without a flat counterpart, by level.</summary>
        public readonly List<KeyValuePair<int, string>> Unmapped = new List<KeyValuePair<int, string>>();

        public const string BoxNested = "dropBox", GroupNested = "group", RowNested = "v", RowFlat = "individualDropBox";

        public DropField ByNested(int level, string name)
        {
            foreach (DropField f in Fields) if (f.Level == level && f.Nested == name) return f;
            return null;
        }

        public DropField ByFlat(string name)
        {
            foreach (DropField f in Fields) if (f.Flat == name) return f;
            return null;
        }

        public static DropMap Parse(byte[] bytes, string label)
        {
            XmlDoc doc = XmlDoc.Parse(bytes, label);
            if (doc.Root == null || doc.Root.Name != "dropmap") throw new ModKitException(label + ": the root element must be <dropmap>");
            var m = new DropMap();
            foreach (XmlElem e in doc.Root.Children)
            {
                int level;
                if (!int.TryParse(e.Attr("level") ?? "", out level) || level < 0 || level > 2)
                    throw new ModKitException(label + " line " + e.Line + ": level must be 0, 1 or 2");
                string nested = e.Attr("nested");
                if (string.IsNullOrEmpty(nested)) throw new ModKitException(label + " line " + e.Line + ": nested=\"\" is missing");
                if (e.Name == "unmapped") { m.Unmapped.Add(new KeyValuePair<int, string>(level, nested)); continue; }
                if (e.Name != "field") throw new ModKitException(label + " line " + e.Line + ": <" + e.Name + "> is not known here (field, unmapped)");
                var f = new DropField { Level = level, Nested = nested, Flat = e.Attr("flat"), Bool = e.Attr("convert") == "bool" };
                if (string.IsNullOrEmpty(f.Flat)) throw new ModKitException(label + " line " + e.Line + ": flat=\"\" is missing");
                foreach (XmlElem s in e.Children)
                    if (s.Name == "score")
                        f.Scores.Add(new DropScore
                        {
                            NestedSide = s.Attr("nested"), FlatSide = s.Attr("flat"), Common = s.Attr("common"), Comparable = s.Attr("comparable"),
                            Agree = s.Attr("agree"), PctAll = s.Attr("pctAll"), PctComparable = s.Attr("pctComparable"),
                        });
                m.Fields.Add(f);
            }
            return m;
        }

        static readonly object gate = new object();
        static DropMap dflt;

        /// <summary>The map embedded in the library.</summary>
        public static DropMap Default
        {
            get
            {
                lock (gate)
                {
                    if (dflt != null) return dflt;
                    Assembly asm = typeof(DropMap).Assembly;
                    using (Stream s = asm.GetManifestResourceStream("MS2.ModKit.drop.dropmap.xml"))
                    {
                        if (s == null) throw new ModKitException("the drop field map is not embedded in the library");
                        var buf = new byte[s.Length];
                        int got = 0;
                        while (got < buf.Length) { int r = s.Read(buf, got, buf.Length - got); if (r <= 0) break; got += r; }
                        return dflt = Parse(buf, "drop.dropmap.xml");
                    }
                }
            }
        }
    }
}
