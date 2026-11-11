// Game map (.xblock) to editor map (.block) and back.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MS2.ModKit.MapEdit
{
    /// <summary>Counts of what a conversion met, with up to five examples each.</summary>
    public sealed class ConvertReport
    {
        public readonly List<string> Keys = new List<string>();
        public readonly Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.Ordinal);
        public readonly Dictionary<string, List<string>> Samples = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        public void Note(string key, string sample, int n)
        {
            if (!Counts.ContainsKey(key)) { Counts[key] = 0; Keys.Add(key); Samples[key] = new List<string>(); }
            Counts[key] += n;
            if (sample != null && Samples[key].Count < 5) Samples[key].Add(sample);
        }

        public void Note(string key, string sample) { Note(key, sample, 1); }
        public void Note(string key) { Note(key, null, 1); }

        public int Count(string key) { int n; return Counts.TryGetValue(key, out n) ? n : 0; }

        /// <summary>One line: {'key': n, ...}</summary>
        public string Line()
        {
            var sb = new StringBuilder("{");
            foreach (string k in Keys)
            {
                if (sb.Length > 1) sb.Append(", ");
                sb.Append('\'').Append(k).Append("': ").Append(Counts[k]);
            }
            return sb.Append('}').ToString();
        }
    }

    public enum PropOrder { Block, Shipped, Model }

    public sealed class ToBlockOptions
    {
        public string Name;
        public bool LayersByCategory, FoldersByCategory;
        public string LibRoot = "..\\Library";
        public IDictionary<string, string> ModelIds;
        public string ExportFolder = ".";
    }

    public static class MapConvert
    {
        static readonly string[,] LibProject =
        {
            { "maplestory2library", "maplestory2library\\maplestory2.emtproj" },
            { "standardmodellibrary", "standardmodellibrary\\standardmodellibrary.emtproj" },
            { "physxmodellibrary", "physxmodellibrary\\physxmodellibrary.emtproj" },
            { "triggerslibrary", "triggerslibrary\\triggerslibrary.emtproj" },
            { "beastmodellibrary", "beastmodellibrary\\beastmodellibrary.emtproj" },
            { "gimodellibrary", "gimodellibrary\\gimodellibrary.emtproj" },
            { "tool", "tool\\tool.emtproj" },
        };

        /// <summary>("presets", "presets thing") -> "Presets_Thing"; a library keeps its name.</summary>
        public static string CategoryLabel(string[] cat)
        {
            if (cat[0] == "library") return cat[1];
            var sb = new StringBuilder();
            bool prevCased = false;
            foreach (char c in cat[1])
            {
                bool cased = char.IsUpper(c) || char.IsLower(c);
                char t = cased ? (prevCased ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)) : c;
                prevCased = cased;
                bool keep = (t >= 'A' && t <= 'Z') || (t >= 'a' && t <= 'z') || (t >= '0' && t <= '9') || t == '_';
                if (keep) sb.Append(t);
                else if (sb.Length == 0 || sb[sb.Length - 1] != '\u0001') sb.Append('\u0001');
            }
            return sb.ToString().Replace('\u0001', '_').Trim('_');
        }

        public static string ProjectFor(string[] cat, string libRoot)
        {
            if (cat[0] == "library")
            {
                for (int i = 0; i < LibProject.GetLength(0); i++)
                    if (LibProject[i, 0] == cat[1]) return libRoot + "\\" + LibProject[i, 1];
                throw new ModKitException("model library " + cat[1] + " has no known project file");
            }
            return libRoot + "\\Presets\\" + CategoryLabel(cat) + ".emtproj";
        }

        public static BlockDoc ToBlock(IList<XEntity> ents, string name, FlatIndex index, ToBlockOptions opt, ConvertReport rep)
        {
            if (opt == null) opt = new ToBlockOptions();
            if (rep == null) rep = new ConvertReport();
            var namesById = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (XEntity e in ents) namesById[MapValues.Dashed(e.Id)] = e.Name;
            var blk = new BlockDoc(opt.Name ?? name);
            for (int i = 0; i < blk.Settings.Count; i++)
                if (blk.Settings[i].Id == BlockDoc.ExportFolderId)
                    blk.Settings[i] = new BSetting(blk.Settings[i].Id, blk.Settings[i].Type, opt.ExportFolder);
            var usedCats = new List<string[]>();
            var usedKeys = new Dictionary<string, bool>(StringComparer.Ordinal);
            var layerByCat = new Dictionary<string, BLayer>(StringComparer.Ordinal);
            var folderByCat = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (XEntity e in ents)
            {
                FlatModel m = index.Get(e.Model);
                string[] cat = m == null ? null : m.Category;
                string catKey = cat == null ? null : cat[0] + "\n" + cat[1];
                if (m == null) rep.Note("entity_model_missing_from_flats", e.Model);
                else if (catKey != null && !usedKeys.ContainsKey(catKey)) { usedKeys[catKey] = true; usedCats.Add(cat); }
                var props = new List<XProp>(e.Props);
                bool nameable = m != null && m.Nameable;
                if (nameable && !props.Exists(delegate (XProp p) { return p.Name == "Name"; }))
                    rep.Note("nameable_without_Name", name + " " + e.Id + " " + e.Model);
                // the editor makes the Name property itself for a model that can be named
                if (nameable && props.Count > 0)
                {
                    XProp last = props[props.Count - 1];
                    if (last.Name == "Name" && last.Scalar && last.Items[0].Value == e.Name)
                    {
                        props.RemoveAt(props.Count - 1);
                        rep.Note("synthetic_Name_dropped");
                    }
                }
                var outProps = new List<XProp>();
                foreach (XProp p in props)
                {
                    FlatProp fp = m == null ? null : m.Prop(p.Name);
                    string t = fp == null ? null : fp.Type;
                    if (m != null && fp == null) rep.Note("property_not_in_model", e.Model + " " + p.Name);
                    List<XSet> items = p.Items;
                    bool color = t == "Color" || t == "ColorA", eref = t == "EntityRef" || t == "AssocEntityRef";
                    if (color || eref)
                    {
                        items = new List<XSet>();
                        foreach (XSet s in p.Items)
                        {
                            string nv;
                            if (color)
                            {
                                bool exact;
                                nv = MapValues.ColorToBlock(s.Value, t == "Color" ? 3 : 4, out exact);
                                if (!exact) rep.Note("color_not_byte_exact", name + " " + e.Id + " " + p.Name + " " + s.Value);
                            }
                            else
                            {
                                bool dangling;
                                nv = MapValues.EntityRefToBlock(s.Value, namesById, out dangling);
                                if (dangling) rep.Note("entityref_dangling", name + " " + e.Id + " " + p.Name + " " + s.Value);
                            }
                            items.Add(new XSet(s.Index, nv));
                        }
                    }
                    outProps.Add(new XProp(p.Name, items));
                }
                var be = new BEntity { Id = MapValues.Dashed(e.Id), Model = e.Model, Name = e.Name, Iterations = e.Iterations, Props = outProps };
                string mid;
                if (opt.ModelIds != null && e.Model != null && opt.ModelIds.TryGetValue(e.Model, out mid)) be.ModelId = mid;
                if (opt.FoldersByCategory && cat != null)
                {
                    string fid;
                    if (!folderByCat.TryGetValue(catKey, out fid))
                    {
                        fid = MapValues.UrlUuid5("ms2-folder:" + blk.Name + ":" + CategoryLabel(cat));
                        folderByCat[catKey] = fid;
                        blk.Folders.Add(new BFolder(fid, CategoryLabel(cat), MapValues.ZeroGuid));
                    }
                    be.Folder = fid;
                }
                if (opt.LayersByCategory && cat != null)
                {
                    BLayer lay;
                    if (!layerByCat.TryGetValue(catKey, out lay))
                    {
                        lay = new BLayer { Name = CategoryLabel(cat), Path = blk.Name + "." + CategoryLabel(cat) + ".layer" };
                        layerByCat[catKey] = lay;
                        blk.Layers.Add(lay);
                    }
                    lay.Entities.Add(be);
                }
                else blk.Entities.Add(be);
            }
            foreach (string[] c in usedCats) blk.References.Add(ProjectFor(c, opt.LibRoot));
            Comparison<BEntity> byId = delegate (BEntity a, BEntity b) { return string.CompareOrdinal(a.Id, b.Id); };
            foreach (BLayer l in blk.Layers) Stable.Sort(l.Entities, byId);
            Stable.Sort(blk.Entities, byId);
            rep.Note("entities", null, ents.Count);
            return blk;
        }

        /// <summary>By id, entities with the same id staying in their order.</summary>
        public static void SortById(List<XEntity> list)
        {
            Stable.Sort(list, delegate (XEntity a, XEntity b) { return string.CompareOrdinal(a.Id, b.Id); });
        }

        static XEntity ToXEntity(BEntity be, FlatIndex index)
        {
            FlatModel m = index.Get(be.Model);
            var props = new List<XProp>();
            bool hasName = false;
            foreach (XProp p in be.Props)
            {
                if (p.Name == "Name") hasName = true;
                FlatProp fp = m == null ? null : m.Prop(p.Name);
                string t = fp == null ? null : fp.Type;
                List<XSet> items = p.Items;
                if (t == "Color" || t == "ColorA" || t == "EntityRef" || t == "AssocEntityRef")
                {
                    items = new List<XSet>();
                    foreach (XSet s in p.Items)
                        items.Add(new XSet(s.Index, t == "Color" ? MapValues.ColorToXBlock(s.Value, 3)
                            : t == "ColorA" ? MapValues.ColorToXBlock(s.Value, 4) : MapValues.EntityRefToXBlock(s.Value)));
                }
                props.Add(new XProp(p.Name, items));
            }
            if (!hasName && m != null && m.Nameable) props.Add(new XProp("Name", be.Name));
            return new XEntity { Id = MapValues.Undashed(be.Id), Model = be.Model, Name = be.Name, Iterations = be.Iterations, Props = props };
        }

        static void Order(XEntity xe, FlatIndex index, PropOrder order)
        {
            if (order == PropOrder.Block) return;
            var names = new List<string>();
            foreach (XProp p in xe.Props) names.Add(p.Name);
            List<string> want;
            if (order == PropOrder.Shipped) want = XBlock.ShippedOrder(names);
            else
            {
                FlatModel m = index.Get(xe.Model);
                var pos = new Dictionary<string, int>(StringComparer.Ordinal);
                if (m != null) for (int i = 0; i < m.Props.Count; i++) pos[m.Props[i].Name ?? ""] = i;
                want = new List<string>();
                bool hasName = false;
                foreach (string n in names) { if (n == "Name") hasName = true; else want.Add(n); }
                Stable.Sort(want, delegate (string a, string b)
                {
                    int x, y;
                    if (!pos.TryGetValue(a, out x)) x = 1 << 30;
                    if (!pos.TryGetValue(b, out y)) y = 1 << 30;
                    return x.CompareTo(y);
                });
                if (hasName) want.Add("Name");
            }
            var rank = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < want.Count; i++) rank[want[i]] = i;
            Stable.Sort(xe.Props, delegate (XProp a, XProp b) { return rank[a.Name].CompareTo(rank[b.Name]); });
        }

        /// <summary>What the editor's export gives: one game map for the block, one more for each layer marked to
        /// be exported on its own. Key: file name, value: the entities, sorted by id.</summary>
        public static List<KeyValuePair<string, List<XEntity>>> ToXBlocks(BlockDoc block, FlatIndex index, PropOrder order)
        {
            var res = new List<KeyValuePair<string, List<XEntity>>>();
            Emit(res, index, order, block.Name, block.Entities, block.Layers, block.Name);
            return res;
        }

        static void Collect(List<KeyValuePair<string, List<XEntity>>> res, FlatIndex index, PropOrder order,
            IList<BEntity> ents, IList<BLayer> children, List<BEntity> acc, Dictionary<string, int> at)
        {
            foreach (BEntity e in ents)
            {
                int i;
                if (at.TryGetValue(e.Id ?? "", out i)) acc[i] = e;      // the same id twice: the later one, at the first place
                else { at[e.Id ?? ""] = acc.Count; acc.Add(e); }
            }
            foreach (BLayer ch in children)
            {
                if (ch.ExportHint) Emit(res, index, order, ch.Name, ch.Entities, ch.Layers, ch.Path);
                else Collect(res, index, order, ch.Entities, ch.Layers, acc, at);
            }
        }

        static void Emit(List<KeyValuePair<string, List<XEntity>>> res, FlatIndex index, PropOrder order,
            string name, IList<BEntity> ents, IList<BLayer> children, string path)
        {
            var acc = new List<BEntity>();
            Collect(res, index, order, ents, children, acc, new Dictionary<string, int>(StringComparer.Ordinal));
            var list = new List<XEntity>();
            foreach (BEntity e in acc)
            {
                XEntity xe = ToXEntity(e, index);
                Order(xe, index, order);
                list.Add(xe);
            }
            Stable.Sort(list, delegate (XEntity a, XEntity b) { return string.CompareOrdinal(a.Id, b.Id); });
            string p = string.IsNullOrEmpty(path) ? name : path;
            int cut = Math.Max(p.LastIndexOf('/'), p.LastIndexOf('\\'));
            p = p.Substring(cut + 1);
            int dot = p.LastIndexOf('.');
            if (dot > 0) p = p.Substring(0, dot);
            res.Add(new KeyValuePair<string, List<XEntity>>(p + ".xblock", list));
        }
    }
}
