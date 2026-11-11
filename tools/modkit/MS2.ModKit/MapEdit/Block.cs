// The editor's map document (.block, with .layer files next to it): the same entities as the game's .xblock plus
// what only the editor needs (model references, tool settings, layers, folders).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MS2.ModKit.Xml;

namespace MS2.ModKit.MapEdit
{
    public sealed class BEntity
    {
        public string Id;               // dashed
        public string Model, ModelId, Name, Folder, Tags;
        public int Iterations;
        public bool Visible = true, Locked;
        public List<XProp> Props = new List<XProp>();
    }

    public sealed class BLayer
    {
        public string Name, Path;       // Path: file name next to the block
        public List<BEntity> Entities = new List<BEntity>();
        public bool ExportHint;
        public readonly List<BLayer> Layers = new List<BLayer>();
    }

    public struct BSetting
    {
        public string Id, Type, Value;
        public BSetting(string id, string type, string value) { Id = id; Type = type; Value = value; }
    }

    public struct BFolder
    {
        public string Id, Name, Parent;
        public BFolder(string id, string name, string parent) { Id = id; Name = name; Parent = parent; }
    }

    public sealed class BlockDoc
    {
        public const string ExportHintId = "Emergent.Toolbench.WorldBuilder.PerContentLayerExportHint";
        public const string ExportFolderId = "Emergent.WorldBuilder.ExportFolder";

        public string Name;
        public List<string> References = new List<string>();
        public List<BSetting> Settings = DefaultSettings();
        public readonly List<BLayer> Layers = new List<BLayer>();
        public List<BEntity> Entities = new List<BEntity>();
        public readonly List<BFolder> Folders = new List<BFolder>();

        public BlockDoc(string name) { Name = name; }

        public static List<BSetting> DefaultSettings()
        {
            return new List<BSetting>
            {
                new BSetting("Emergent.WorldBuilder.CustomShaders.CustomShaderDirectory", "String", ""),
                new BSetting(ExportFolderId, "String", "."),
                new BSetting("Emergent.WorldBuilder.Exporter", "String", "XmlBlockExporter"),
                new BSetting("Emergent.WorldBuilder.Grid.ScalePrecision", "Float", "0.1"),
                new BSetting("Emergent.WorldBuilder.Grid.ScalePrecisionEnabled", "Boolean", "False"),
                new BSetting("Emergent.WorldBuilder.Grid.TranslationPrecision", "Float", "1"),
                new BSetting("Emergent.WorldBuilder.Grid.TranslationPrecisionEnabled", "Boolean", "True"),
                new BSetting("Emergent.WorldBuilder.SnapPoints.SnapPointStickinessRadius", "Float", "1"),
                new BSetting("Emergent.WorldBuilder.World.Scale", "Float", "100"),
            };
        }

        // ---- writing ----

        static void WSettings(NetXmlWriter w, IList<BSetting> settings)
        {
            w.Start("tool-settings", "version", "1");
            foreach (BSetting s in settings)
            {
                w.Start("setting", "id", s.Id, "property-type", s.Type);
                w.Leaf("value", s.Value);
                w.End();
            }
            w.End();
        }

        static void WEntities(NetXmlWriter w, IList<BEntity> entities)
        {
            w.Start("entities", "version", "1");
            foreach (BEntity e in entities)
            {
                var a = new List<string> { "id", e.Id, "model-name", e.Model };
                if (!string.IsNullOrEmpty(e.ModelId)) { a.Add("model-id"); a.Add(e.ModelId); }
                a.Add("name"); a.Add(e.Name);
                a.Add("iterations"); a.Add(e.Iterations.ToString(CultureInfo.InvariantCulture));
                a.Add("visible"); a.Add(e.Visible ? "true" : "false");
                a.Add("locked"); a.Add(e.Locked ? "true" : "false");
                if (!string.IsNullOrEmpty(e.Folder)) { a.Add("folder"); a.Add(e.Folder); }
                if (!string.IsNullOrEmpty(e.Tags)) { a.Add("tags"); a.Add(e.Tags); }
                w.Start("entity", a.ToArray());
                w.Start("properties");
                foreach (XProp p in e.Props) XBlock.WriteProp(w, p);
                w.End();
                w.End();
            }
            w.End();
        }

        static void WLayers(NetXmlWriter w, IList<BLayer> layers)
        {
            w.Start("layers", "version", "1");
            foreach (BLayer l in layers) w.Leaf("layer", l.Path);
            w.End();
        }

        public static string LayerText(BLayer layer)
        {
            var w = new NetXmlWriter("  ", true);
            w.Start("layer", "version", "3", "name", layer.Name);
            var st = new List<BSetting>();
            if (layer.ExportHint) st.Add(new BSetting(ExportHintId, "Boolean", "True"));
            WSettings(w, st);
            WLayers(w, layer.Layers);
            WEntities(w, layer.Entities);
            w.End();
            return w.Finish();
        }

        public string Text()
        {
            var w = new NetXmlWriter("  ", true);
            w.Start("block", "version", "3", "name", Name);
            w.Start("model-references", "version", "1");
            foreach (string r in References) w.Leaf("reference", r);
            w.End();
            WSettings(w, Settings);
            WLayers(w, Layers);
            WEntities(w, Entities);
            w.Empty("selections", "version", "1");
            w.Start("entity-folders", "version", "1");
            foreach (BFolder f in Folders) w.Empty("folder", "id", f.Id, "name", f.Name, "folder", f.Parent);
            w.End();
            w.End();
            return w.Finish();
        }

        static void WriteLayerTree(BLayer layer, string dir)
        {
            foreach (BLayer sub in layer.Layers) WriteLayerTree(sub, dir);
            File.WriteAllBytes(System.IO.Path.Combine(dir, layer.Path), NetXmlWriter.Bytes(LayerText(layer)));
        }

        /// <summary>Writes the block and its layer files next to it.</summary>
        public void Write(string path)
        {
            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            foreach (BLayer l in Layers) WriteLayerTree(l, dir);
            File.WriteAllBytes(path, NetXmlWriter.Bytes(Text()));
        }

        // ---- reading ----

        static XmlElem Child(XmlElem e, string name)
        {
            foreach (XmlElem c in e.Children) if (c.Name == name) return c;
            return null;
        }

        static string TextOf(XmlElem e)
        {
            if (e == null) return "";
            if (e.Children.Count == 0) return e.InnerText ?? "";
            // text before the first child, as the script this replaces reads it
            return "";
        }

        static List<BEntity> ReadEntities(XmlElem xe, string label)
        {
            var res = new List<BEntity>();
            if (xe == null) return res;
            foreach (XmlElem e in xe.Children)
            {
                if (e.Name != "entity") continue;
                var b = new BEntity
                {
                    Id = e.Attr("id"), Model = e.Attr("model-name"), ModelId = e.Attr("model-id"), Name = e.Attr("name"),
                    Visible = (e.Attr("visible") ?? "true") == "true", Locked = (e.Attr("locked") ?? "false") == "true",
                    Folder = e.Attr("folder"), Tags = e.Attr("tags")
                };
                if (!int.TryParse((e.Attr("iterations") ?? "0").Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out b.Iterations))
                    throw new ModKitException(label + " line " + e.Line + ": entity " + b.Id + " has no whole number in iterations");
                XmlElem xp = Child(e, "properties");
                if (xp != null)
                    foreach (XmlElem p in xp.Children)
                    {
                        if (p.Name != "property") continue;
                        var prop = new XProp(p.Attr("name"));
                        foreach (XmlElem s in p.Children) if (s.Name == "set") prop.Items.Add(new XSet(s.Attr("index"), s.Attr("value")));
                        b.Props.Add(prop);
                    }
                res.Add(b);
            }
            return res;
        }

        static List<BSetting> ReadSettings(XmlElem root)
        {
            // one value per id, the last one, at the place of the first
            var res = new List<BSetting>();
            XmlElem ts = Child(root, "tool-settings");
            if (ts == null) return res;
            foreach (XmlElem s in ts.Children)
            {
                if (s.Name != "setting") continue;
                var v = new BSetting(s.Attr("id"), s.Attr("property-type"), TextOf(Child(s, "value")));
                int at = res.FindIndex(delegate (BSetting x) { return x.Id == v.Id; });
                if (at >= 0) res[at] = v; else res.Add(v);
            }
            return res;
        }

        static BLayer ReadLayer(string path, string dir)
        {
            string file = System.IO.Path.Combine(dir, path);
            XmlElem root = XmlDoc.Parse(File.ReadAllBytes(file), file).Root;
            var l = new BLayer { Name = root.Attr("name"), Path = path };
            foreach (BSetting s in ReadSettings(root))
                if (s.Id == ExportHintId) l.ExportHint = s.Value.ToLowerInvariant() == "true";
            l.Entities = ReadEntities(Child(root, "entities"), file);
            XmlElem xl = Child(root, "layers");
            if (xl != null)
                foreach (XmlElem x in xl.Children) if (x.Name == "layer") l.Layers.Add(ReadLayer(TextOf(x), dir));
            return l;
        }

        public static BlockDoc Read(string path)
        {
            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            XmlElem root = XmlDoc.Parse(File.ReadAllBytes(path), path).Root;
            var b = new BlockDoc(root.Attr("name"));
            XmlElem refs = Child(root, "model-references");
            if (refs == null) throw new ModKitException(path + ": no <model-references>");
            foreach (XmlElem r in refs.Children) if (r.Name == "reference") b.References.Add(TextOf(r));
            b.Settings = ReadSettings(root);
            XmlElem xl = Child(root, "layers");
            if (xl != null)
                foreach (XmlElem x in xl.Children) if (x.Name == "layer") b.Layers.Add(ReadLayer(TextOf(x), dir));
            b.Entities = ReadEntities(Child(root, "entities"), path);
            XmlElem xf = Child(root, "entity-folders");
            if (xf != null)
                foreach (XmlElem f in xf.Children) if (f.Name == "folder") b.Folders.Add(new BFolder(f.Attr("id"), f.Attr("name"), f.Attr("folder")));
            return b;
        }
    }
}
