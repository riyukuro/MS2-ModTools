// The model listing of a game: one .flat file per model (name, id, mixins, every property with its type and default).
// Built from the flat/ entries of the game's Resource/Exported archive, from a folder, or read from an index file.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit.MapEdit
{
    public sealed class FlatMixin { public string Name, Type; public decimal Id; }

    public sealed class FlatBehavior { public string Name, Type, Source; public decimal Id; }

    public sealed class FlatProp
    {
        public string Name, Type, Source;
        public decimal Id;
        /// <summary>The default: null when the model gives none; one XSet with a null index for a plain value.</summary>
        public List<XSet> Default;

        public bool DefaultIsScalar { get { return Default != null && Default.Count == 1 && Default[0].Index == null; } }
    }

    public sealed class FlatModel
    {
        public string Name, File;
        public decimal Id;
        public readonly List<string> Traits = new List<string>();
        public readonly List<FlatMixin> Mixins = new List<FlatMixin>();
        public readonly List<FlatProp> Props = new List<FlatProp>();
        public readonly List<FlatBehavior> Behaviors = new List<FlatBehavior>();

        Dictionary<string, FlatProp> byName;

        public FlatProp Prop(string name)
        {
            if (byName == null)
            {
                byName = new Dictionary<string, FlatProp>(StringComparer.Ordinal);
                foreach (FlatProp p in Props) byName[p.Name ?? ""] = p;
            }
            FlatProp r;
            return byName.TryGetValue(name, out r) ? r : null;
        }

        public bool Nameable
        {
            get { foreach (FlatMixin m in Mixins) if (m.Name == "Nameable") return true; return false; }
        }

        /// <summary>The two folders the file stands in: ("library", name) or ("presets", name).</summary>
        public string[] Category
        {
            get
            {
                string[] parts = File.Split('/');
                return parts.Length < 3 ? null : new[] { parts[1], parts[2] };
            }
        }
    }

    public sealed class FlatError { public string File, Error, Other; }

    public sealed class FlatIndex
    {
        readonly Dictionary<string, FlatModel> models = new Dictionary<string, FlatModel>(StringComparer.Ordinal);
        readonly List<string> order = new List<string>();
        public readonly List<FlatError> Errors = new List<FlatError>();

        public int Count { get { return order.Count; } }
        public IList<string> Names { get { return order.AsReadOnly(); } }

        public FlatModel Get(string name)
        {
            FlatModel m;
            return name != null && models.TryGetValue(name, out m) ? m : null;
        }

        void Add(FlatModel m)
        {
            string key = m.Name ?? "";
            FlatModel old;
            if (models.TryGetValue(key, out old)) Errors.Add(new FlatError { File = m.File, Error = "duplicate model name", Other = old.File });
            else order.Add(key);
            models[key] = m;        // as the script this replaces: the later file wins
        }

        static decimal Int(XmlElem e, string attr)
        {
            string s = e.Attr(attr);
            decimal d;
            if (s == null || !decimal.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out d))
                throw new ModKitException("<" + e.Name + "> has no whole number in " + attr);
            return d;
        }

        public static FlatModel ParseFlat(byte[] bytes, string file)
        {
            XmlElem root = XmlDoc.Parse(bytes, file).Root;
            var m = new FlatModel { Name = root.Attr("name"), Id = Int(root, "id"), File = file };
            foreach (XmlElem c in root.Children)
            {
                switch (c.Name)
                {
                    case "trait": m.Traits.Add(c.Attr("value")); break;
                    case "mixin": m.Mixins.Add(new FlatMixin { Name = c.Attr("name"), Id = Int(c, "id"), Type = c.Attr("type") }); break;
                    case "property":
                        var p = new FlatProp { Name = c.Attr("name"), Type = c.Attr("type"), Id = Int(c, "id"), Source = c.Attr("source") };
                        foreach (XmlElem s in c.Children)
                            if (s.Name == "set")
                            {
                                if (p.Default == null) p.Default = new List<XSet>();
                                p.Default.Add(new XSet(s.Attr("index"), s.Attr("value")));
                            }
                        m.Props.Add(p);
                        break;
                    case "behavior": m.Behaviors.Add(new FlatBehavior { Name = c.Attr("name"), Id = Int(c, "id"), Type = c.Attr("type"), Source = c.Attr("source") }); break;
                }
            }
            return m;
        }

        /// <summary>files: name relative to the folder that holds flat/ ("flat/.../x.flat") and its bytes.</summary>
        public static FlatIndex FromFlats(IEnumerable<KeyValuePair<string, byte[]>> files)
        {
            var list = new List<KeyValuePair<string, byte[]>>(files);
            list.Sort(delegate (KeyValuePair<string, byte[]> a, KeyValuePair<string, byte[]> b) { return string.CompareOrdinal(a.Key, b.Key); });
            var ix = new FlatIndex();
            foreach (KeyValuePair<string, byte[]> f in list)
            {
                FlatModel m;
                try { m = ParseFlat(f.Value, f.Key); }
                catch (ModKitException x) { ix.Errors.Add(new FlatError { File = f.Key, Error = x.Message }); continue; }
                ix.Add(m);
            }
            return ix;
        }

        public static FlatIndex FromArchive(M2dArchive arch)
        {
            var files = new List<KeyValuePair<string, byte[]>>();
            foreach (M2dEntry e in arch.Entries)
                if (e.Name.StartsWith("flat/", StringComparison.OrdinalIgnoreCase) && e.Name.EndsWith(".flat", StringComparison.OrdinalIgnoreCase))
                    files.Add(new KeyValuePair<string, byte[]>(e.Name, arch.Read(e)));
            return FromFlats(files);
        }

        public static FlatIndex FromDirectory(string dir)
        {
            string flat = Path.Combine(dir, "flat");
            if (!Directory.Exists(flat)) throw new ModKitException(dir + " has no flat folder");
            var files = new List<KeyValuePair<string, byte[]>>();
            string root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string f in WbProject.FilesOf(flat, ".flat", true))
                files.Add(new KeyValuePair<string, byte[]>(Path.GetFullPath(f).Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes(f)));
            return FromFlats(files);
        }

        /// <summary>An index file, a folder that holds flat/, or an archive with flat/ entries.</summary>
        public static FlatIndex Load(string path)
        {
            if (Directory.Exists(path)) return FromDirectory(path);
            if (!File.Exists(path)) throw new ModKitException("no model index at " + path + " (an index file, a folder with flat/, or the Exported archive)");
            if (path.EndsWith(".m2d", StringComparison.OrdinalIgnoreCase))
                using (M2dArchive a = M2dArchive.Open(path)) return FromArchive(a);
            return FromJson(File.ReadAllText(path, new UTF8Encoding(false)), path);
        }

        // ---- the index file ----

        static string S(object o) { return o as string; }
        static decimal D(object o) { return o is decimal ? (decimal)o : 0; }

        public static FlatIndex FromJson(string text, string label)
        {
            var top = Json.Parse(text, label) as JsonObj;
            var ms = top == null ? null : top.Get("models") as JsonObj;
            if (ms == null) throw new ModKitException(label + ": not a model index (no \"models\")");
            var ix = new FlatIndex();
            foreach (string key in ms.Keys)
            {
                var o = ms.Map[key] as JsonObj;
                if (o == null) continue;
                var m = new FlatModel { Name = S(o.Get("name")), Id = D(o.Get("id")), File = S(o.Get("file")) };
                foreach (object t in (o.Get("traits") as List<object>) ?? new List<object>()) m.Traits.Add(S(t));
                foreach (object x in (o.Get("mixins") as List<object>) ?? new List<object>())
                {
                    var l = (List<object>)x;
                    m.Mixins.Add(new FlatMixin { Name = S(l[0]), Id = D(l[1]), Type = S(l[2]) });
                }
                foreach (object x in (o.Get("props") as List<object>) ?? new List<object>())
                {
                    var po = (JsonObj)x;
                    var p = new FlatProp { Name = S(po.Get("name")), Type = S(po.Get("type")), Id = D(po.Get("id")), Source = S(po.Get("source")) };
                    if (po.Map.ContainsKey("default"))
                    {
                        object d = po.Get("default");
                        p.Default = new List<XSet>();
                        var dl = d as List<object>;
                        if (dl == null) p.Default.Add(new XSet(null, S(d)));
                        else foreach (object it in dl) { var pair = (List<object>)it; p.Default.Add(new XSet(S(pair[0]), S(pair[1]))); }
                    }
                    m.Props.Add(p);
                }
                foreach (object x in (o.Get("behaviors") as List<object>) ?? new List<object>())
                {
                    var l = (List<object>)x;
                    m.Behaviors.Add(new FlatBehavior { Name = S(l[0]), Id = D(l[1]), Type = S(l[2]), Source = S(l[3]) });
                }
                ix.models[key] = m;
                ix.order.Add(key);
            }
            foreach (object x in (top.Get("errors") as List<object>) ?? new List<object>())
            {
                var eo = x as JsonObj;
                if (eo != null) ix.Errors.Add(new FlatError { File = S(eo.Get("file")), Error = S(eo.Get("error")), Other = S(eo.Get("other")) });
            }
            return ix;
        }

        /// <summary>A file of the form {"name": "value", ...}.</summary>
        public static List<KeyValuePair<string, string>> ReadStringMap(string text, string label)
        {
            var o = Json.Parse(text, label) as JsonObj;
            if (o == null) throw new ModKitException(label + ": not a list of name and value");
            object inner;
            if (o.Map.TryGetValue("models", out inner) && inner is JsonObj) o = (JsonObj)inner;     // the known-ids file keeps its list under "models"
            var res = new List<KeyValuePair<string, string>>();
            foreach (string k in o.Keys) res.Add(new KeyValuePair<string, string>(k, S(o.Map[k])));
            return res;
        }

        static void Q(StringBuilder sb, string s) { if (s == null) sb.Append("null"); else Json.Quote(sb, s); }
        static void N(StringBuilder sb, decimal d) { sb.Append(d.ToString(CultureInfo.InvariantCulture)); }

        /// <summary>The index file, in the form the script this replaces writes (one line).</summary>
        public string ToJson()
        {
            var sb = new StringBuilder(1 << 20);
            sb.Append("{\"models\": {");
            bool first = true;
            foreach (string key in order)
            {
                FlatModel m = models[key];
                if (!first) sb.Append(", ");
                first = false;
                Q(sb, key); sb.Append(": {\"name\": "); Q(sb, m.Name);
                sb.Append(", \"id\": "); N(sb, m.Id);
                sb.Append(", \"file\": "); Q(sb, m.File);
                sb.Append(", \"traits\": [");
                for (int i = 0; i < m.Traits.Count; i++) { if (i > 0) sb.Append(", "); Q(sb, m.Traits[i]); }
                sb.Append("], \"mixins\": [");
                for (int i = 0; i < m.Mixins.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append('['); Q(sb, m.Mixins[i].Name); sb.Append(", "); N(sb, m.Mixins[i].Id); sb.Append(", "); Q(sb, m.Mixins[i].Type); sb.Append(']');
                }
                sb.Append("], \"props\": [");
                for (int i = 0; i < m.Props.Count; i++)
                {
                    FlatProp p = m.Props[i];
                    if (i > 0) sb.Append(", ");
                    sb.Append("{\"name\": "); Q(sb, p.Name); sb.Append(", \"type\": "); Q(sb, p.Type);
                    sb.Append(", \"id\": "); N(sb, p.Id); sb.Append(", \"source\": "); Q(sb, p.Source);
                    if (p.Default != null)
                    {
                        sb.Append(", \"default\": ");
                        if (p.DefaultIsScalar) Q(sb, p.Default[0].Value);
                        else
                        {
                            sb.Append('[');
                            for (int k = 0; k < p.Default.Count; k++)
                            {
                                if (k > 0) sb.Append(", ");
                                sb.Append('['); Q(sb, p.Default[k].Index); sb.Append(", "); Q(sb, p.Default[k].Value); sb.Append(']');
                            }
                            sb.Append(']');
                        }
                    }
                    sb.Append('}');
                }
                sb.Append("], \"behaviors\": [");
                for (int i = 0; i < m.Behaviors.Count; i++)
                {
                    FlatBehavior b = m.Behaviors[i];
                    if (i > 0) sb.Append(", ");
                    sb.Append('['); Q(sb, b.Name); sb.Append(", "); N(sb, b.Id); sb.Append(", "); Q(sb, b.Type); sb.Append(", "); Q(sb, b.Source); sb.Append(']');
                }
                sb.Append("]}");
            }
            sb.Append("}, \"errors\": [");
            for (int i = 0; i < Errors.Count; i++)
            {
                FlatError e = Errors[i];
                if (i > 0) sb.Append(", ");
                sb.Append("{\"file\": "); Q(sb, e.File); sb.Append(", \"error\": "); Q(sb, e.Error);
                if (e.Other != null) { sb.Append(", \"other\": "); Q(sb, e.Other); }
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }
}
