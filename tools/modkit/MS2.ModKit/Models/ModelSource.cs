// Finds the model files an item or an NPC names, reads them from the model archives of a data set and draws the
// still picture (tools/modkit/design.md). Every failure comes back as a short text; nothing here throws for bad data.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit.Models
{
    /// <summary>The model names of item and NPC drafts.</summary>
    public static class ModelNames
    {
        public const string ModelFolder = "Resource/Model/";
        public const string NpcArchive = "Npc";
        public const string TextureArchive = "Textures";
        public const string NpcModelField = "model@kfm";

        /// <summary>
        /// Splits a game path ("Data/Resource/Model/Item/1/13/x.nif", with or without "./" and "Data/") into the model
        /// archive ("Item") and the entry in it ("1/13/x.nif"). False for a path that is not under Resource/Model.
        /// </summary>
        public static bool Split(string gamePath, out string archive, out string entry)
        {
            archive = entry = null;
            if (string.IsNullOrEmpty(gamePath)) return false;
            string p = gamePath.Trim().Replace('\\', '/');
            while (p.StartsWith("./", StringComparison.Ordinal)) p = p.Substring(2);
            p = p.TrimStart('/');
            if (p.StartsWith("Data/", StringComparison.OrdinalIgnoreCase)) p = p.Substring(5);
            if (!p.StartsWith(ModelFolder, StringComparison.OrdinalIgnoreCase)) return false;
            p = p.Substring(ModelFolder.Length);
            int slash = p.IndexOf('/');
            if (slash <= 0 || slash == p.Length - 1 || p.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
            string name = p.Substring(0, slash);
            if (!PlainName(name)) return false;
            archive = name;
            entry = p.Substring(slash + 1);
            return true;
        }

        /// <summary>True for a plain file name: no drive, folder or character a file name cannot hold (an archive name comes from a mod's field).</summary>
        public static bool PlainName(string name)
        {
            if (string.IsNullOrEmpty(name) || name == "." || name == "..") return false;
            foreach (char c in name)
                if (c < ' ' || c == ':' || c == '/' || c == '\\' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|') return false;
            return true;
        }

        static bool IsModelPath(string v)
        {
            string a, e;
            return v != null && v.TrimEnd().EndsWith(".nif", StringComparison.OrdinalIgnoreCase) && Split(v, out a, out e);
        }

        /// <summary>The entry of the client file that lists the slot models of an item id.</summary>
        public static string ItemModelEntry(long id) { return "itemmodel/" + (id / 100000).ToString(CultureInfo.InvariantCulture) + ".xml"; }

        /// <summary>The skeleton file of a gender (0 male, 1 female) as a game path: worn items hang on its nodes.</summary>
        public static string SkeletonPath(int gender)
        {
            return "Data/" + ModelFolder + (gender == 1 ? "Character/female/f_body.nif" : "Character/male/m_body.nif");
        }

        static int GenderOf(string v)
        {
            if (v == null) return -1;
            v = v.Trim();
            return v == "0" ? 0 : v == "1" ? 1 : -1;
        }

        static void SlotAssets(XmlElem slots, List<ItemAsset> into, ref int slotNo)
        {
            foreach (XmlElem slot in slots.Children)
            {
                if (slot.Name != "slot") continue;
                slotNo++;
                foreach (XmlElem asset in slot.Children)
                {
                    if (asset.Name != "asset") continue;
                    string v = asset.Attr("name");
                    if (!IsModelPath(v)) continue;
                    into.Add(new ItemAsset { Path = v, Slot = slotNo, Gender = GenderOf(asset.Attr("gender")), SelfNode = asset.Attr("selfnode"), TargetNode = asset.Attr("targetnode") });
                }
            }
        }

        /// <summary>
        /// What an item draft itself says of its model, with the draft's edits: the assets of its own slots and the
        /// model it is placed or dropped as. Cheap (no file is read); ModelSource.Assets adds the client's item
        /// model list, which is read from an archive.
        /// </summary>
        public static ItemQuery Query(EntityDraft d)
        {
            var q = new ItemQuery();
            if (d == null) return q;
            q.Id = d.Id;
            List<ItemAsset> list = q.Own;
            var slots = new List<XmlElem>();
            foreach (EntitySection s in d.Sections)
            {
                XmlElem e = s.Elem;
                if (e == null || e.Name != "asset" || e.Parent == null || e.Parent.Name != "slot") continue;
                string v = d.Get(s, "name");
                if (!IsModelPath(v)) continue;
                int slot = slots.IndexOf(e.Parent);
                if (slot < 0) { slot = slots.Count; slots.Add(e.Parent); }
                list.Add(new ItemAsset { Path = v, Slot = slot, Gender = GenderOf(d.Get(s, "gender")), SelfNode = d.Get(s, "selfnode"), TargetNode = d.Get(s, "targetnode") });
            }
            if (list.Count == 0)
            {
                // a slot's assets that are not sections of their own: read the shipped elements
                int slotNo = -1;
                foreach (EntityPart p in d.Parts)
                    if (p.Root != null) FindSlots(p.Root, list, 0, ref slotNo);
            }
            // the bundle row or the item's own file, whichever names one
            foreach (string[] field in new[] { new[] { "install", "asset" }, new[] { "drop", "dropNif" } })
            {
                foreach (EntitySection s in d.Sections)
                {
                    if (s.Elem == null || s.Elem.Name != field[0]) continue;
                    string v = d.Get(s, field[1]);
                    if (IsModelPath(v)) { q.Fallback = new ItemAsset { Path = v }; break; }
                }
                if (q.Fallback != null) break;
            }
            return q;
        }

        /// <summary>
        /// One asset per slot for a gender (0 male, 1 female): the slot's asset of that gender, else its asset
        /// without a gender, else its first. A file two slots name is listed once.
        /// </summary>
        public static List<ItemAsset> Pick(IList<ItemAsset> all, int gender)
        {
            var list = new List<ItemAsset>();
            if (all == null) return list;
            var done = new List<int>();
            foreach (ItemAsset first in all)
            {
                if (done.Contains(first.Slot)) continue;
                done.Add(first.Slot);
                ItemAsset best = null, plain = null;
                foreach (ItemAsset a in all)
                {
                    if (a.Slot != first.Slot) continue;
                    if (a.Gender == gender && best == null) best = a;
                    if (a.Gender < 0 && plain == null) plain = a;
                }
                ItemAsset pick = best ?? plain ?? first;
                bool listed = false;
                foreach (ItemAsset a in list) if (a.Path == pick.Path) listed = true;
                if (!listed) list.Add(pick);
            }
            return list;
        }

        /// <summary>True when the two genders get different files: the item has a model for each.</summary>
        public static bool HasGenders(IList<ItemAsset> all)
        {
            List<ItemAsset> m = Pick(all, 0), f = Pick(all, 1);
            if (m.Count != f.Count) return true;
            for (int i = 0; i < m.Count; i++) if (m[i].Path != f[i].Path) return true;
            return false;
        }

        internal static void FindSlots(XmlElem e, List<ItemAsset> into, int depth, ref int slotNo)
        {
            if (depth > 6) return;
            foreach (XmlElem c in e.Children)
            {
                if (c.Name == "slots") SlotAssets(c, into, ref slotNo);
                else FindSlots(c, into, depth + 1, ref slotNo);
            }
        }

        /// <summary>The file name of an NPC's .kfm ("02900001_digger.kfm") from its model field, or null when it names none.</summary>
        public static string NpcKfmFile(string model)
        {
            if (model == null) return null;
            string v = model.Trim().Replace('\\', '/');
            int slash = v.LastIndexOf('/');
            if (slash >= 0) v = v.Substring(slash + 1);
            if (v.EndsWith(".kfm", StringComparison.OrdinalIgnoreCase)) v = v.Substring(0, v.Length - 4);
            return v.Length == 0 ? null : v + ".kfm";
        }

        /// <summary>The file name of an NPC draft's .kfm, with the draft's edit, or null.</summary>
        public static string NpcKfmFile(EntityDraft d)
        {
            if (d == null) return null;
            try { return NpcKfmFile(d.GetField(NpcModelField)); }
            catch (ModKitException) { return null; }
        }
    }

    /// <summary>One model file of an item slot and how the item says it is worn.</summary>
    public sealed class ItemAsset
    {
        /// <summary>The game path of the .nif.</summary>
        public string Path;
        /// <summary>The number of the slot among the item's slots.</summary>
        public int Slot;
        /// <summary>0 male, 1 female, -1 when the asset names none.</summary>
        public int Gender = -1;
        /// <summary>The node of the file and the node of the character it hangs on; null when not named.</summary>
        public string SelfNode, TargetNode;
    }

    /// <summary>What an item draft says of its model (ModelNames.Query).</summary>
    public sealed class ItemQuery
    {
        public long Id;
        /// <summary>The assets of the item's own slots; empty when its file has none.</summary>
        public readonly List<ItemAsset> Own = new List<ItemAsset>();
        /// <summary>The model the item is placed or dropped as, or null.</summary>
        public ItemAsset Fallback;
    }

    /// <summary>The .kfm file of an animated model: only the name of its .nif is read.</summary>
    public static class Kfm
    {
        public const string Magic = ";Gamebryo KFM File Version ";

        /// <summary>The .nif path a binary .kfm names (as written, e.g. ".\02900001_Digger.nif"), or null when the bytes are not one.</summary>
        public static string NifPath(byte[] b)
        {
            if (b == null || b.Length < Magic.Length + 6) return null;
            for (int i = 0; i < Magic.Length; i++) if (b[i] != Magic[i]) return null;
            int e = Magic.Length;
            while (e < b.Length && e < Magic.Length + 24 && b[e] != '\n') e++;
            if (e >= b.Length || b[e] != '\n' || b[e - 1] != 'b') return null;      // "...b": the binary form
            int at = e + 1 + 1;                                                     // the byte order flag
            if (at + 4 > b.Length) return null;
            uint n = (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);
            if (n == 0 || n > 1024 || n > (uint)(b.Length - at - 4)) return null;
            return Encoding.UTF8.GetString(b, at + 4, (int)n);
        }
    }

    /// <summary>A drawn model: the pixels, or the line to show in their place.</summary>
    public sealed class ModelPicture
    {
        public int Width, Height;
        /// <summary>0xAARRGGBB, top row first, on a clear background; null when there is no picture.</summary>
        public int[] Argb;
        /// <summary>The line to show when there is no picture ("no model", "cannot show: ..."), else null.</summary>
        public string Message;
        /// <summary>What was left out of a shown picture (a missing texture, a mesh that did not read).</summary>
        public readonly List<string> Notes = new List<string>();
        public int Triangles;
        /// <summary>True for an item whose two genders have different model files.</summary>
        public bool Genders;
    }

    /// <summary>
    /// Reads models and textures from the Resource/Model archives of a data set. Safe to call from a background
    /// thread: one call at a time, and the archives are opened on their own (not through DataSet.Open).
    /// </summary>
    public sealed class ModelSource : IDisposable
    {
        const int KeptTextures = 24;
        /// <summary>The largest side a texture is kept at: a larger one is halved until it fits (the picture is 640 px at most).</summary>
        public const int TextureSide = 1024;
        /// <summary>The most bytes of decoded textures kept between pictures.</summary>
        public const long TextureBudget = 32L << 20;
        long textureBytes;

        readonly object gate = new object();
        readonly List<string> folders = new List<string>();        // .../Resource/Model of each root that has one
        readonly Dictionary<string, M2dArchive> open = new Dictionary<string, M2dArchive>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<M2dArchive, Dictionary<string, M2dEntry>> byName = new Dictionary<M2dArchive, Dictionary<string, M2dEntry>>();
        readonly Dictionary<string, Texture> textures = new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<int, Dictionary<string, float[]>> skeletons = new Dictionary<int, Dictionary<string, float[]>>();
        readonly List<string> xmlArchives = new List<string>();     // the Xml.m2d of each client root
        readonly Dictionary<string, Dictionary<string, XmlElem>> itemModels = new Dictionary<string, Dictionary<string, XmlElem>>(StringComparer.Ordinal);
        bool disposed;

        public ModelSource(DataSet data)
        {
            if (data == null) throw new ArgumentNullException("data");
            foreach (Profile p in Profile.All)
            {
                if (!data.Has(p)) continue;
                if (!p.IsServer && data.Exists(p.Root + ":Xml.m2d"))
                {
                    string xml = data.Resolve(p.Root + ":Xml.m2d");
                    if (!xmlArchives.Contains(xml)) xmlArchives.Add(xml);
                }
                // the folder is found through an archive every client has
                foreach (string name in new[] { ModelNames.TextureArchive, "Item", ModelNames.NpcArchive })
                {
                    string a = p.Root + ":" + ModelNames.ModelFolder + name + ".m2d";
                    if (!data.Exists(a)) continue;
                    string folder = Path.GetDirectoryName(data.Resolve(a));
                    if (!folders.Contains(folder)) folders.Add(folder);
                    break;
                }
            }
        }

        /// <summary>False when no root of the data set has model archives.</summary>
        public bool Available { get { return folders.Count > 0; } }

        // The archives called name (without regard to case) in each model folder. Call under the gate.
        List<M2dArchive> Archives(string name)
        {
            var list = new List<M2dArchive>();
            if (!ModelNames.PlainName(name)) return list;
            foreach (string folder in folders)
            {
                string key = folder + "\n" + name;
                M2dArchive a;
                if (!open.TryGetValue(key, out a))
                {
                    a = null;
                    try
                    {
                        string file = Path.Combine(folder, name + ".m2d");
                        if (!File.Exists(file))
                        {
                            file = null;
                            foreach (string cand in Directory.GetFiles(folder))
                                if (string.Equals(Path.GetFileName(cand), name + ".m2d", StringComparison.OrdinalIgnoreCase)) { file = cand; break; }
                        }
                        if (file != null) a = M2dArchive.Open(file);
                    }
                    catch (ModKitException) { }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    open[key] = a;
                }
                if (a != null) list.Add(a);
            }
            return list;
        }

        static string FileName(string path)
        {
            string p = path.Replace('\\', '/');
            int slash = p.LastIndexOf('/');
            return slash < 0 ? p : p.Substring(slash + 1);
        }

        // The entry with this file name in any folder of the archive. Call under the gate.
        M2dEntry ByName(M2dArchive a, string fileName)
        {
            Dictionary<string, M2dEntry> index;
            if (!byName.TryGetValue(a, out index))
            {
                index = new Dictionary<string, M2dEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (M2dEntry e in a.Entries)
                {
                    string n = FileName(e.Name);
                    if (!index.ContainsKey(n)) index[n] = e;
                }
                byName[a] = index;
            }
            M2dEntry found;
            return index.TryGetValue(fileName, out found) ? found : null;
        }

        // The slot assets the client's item model list gives an item id. The archive is opened here, on the caller's
        // (background) thread, and the rows of a list file are kept.
        List<ItemAsset> ClientItemAssets(long id)
        {
            var list = new List<ItemAsset>();
            string entry = ModelNames.ItemModelEntry(id);
            lock (gate)
            {
                if (disposed) return list;
                foreach (string file in xmlArchives)
                {
                    string key = file + "\n" + entry;
                    Dictionary<string, XmlElem> rows;
                    if (!itemModels.TryGetValue(key, out rows))
                    {
                        rows = new Dictionary<string, XmlElem>(StringComparer.Ordinal);
                        try
                        {
                            M2dArchive a;
                            if (!open.TryGetValue(file, out a)) open[file] = a = File.Exists(file) ? M2dArchive.Open(file) : null;
                            M2dEntry e = a == null ? null : a.Find(entry);
                            XmlDoc doc = e == null ? null : XmlDoc.Parse(a.Read(e), "Xml.m2d " + e.Name);
                            if (doc != null && doc.Root != null)
                                foreach (XmlElem row in doc.Root.Children)
                                {
                                    string rowId = row.Attr("id");
                                    if (row.Name == "ItemModel" && rowId != null && !rows.ContainsKey(rowId)) rows[rowId] = row;
                                }
                        }
                        catch (ModKitException) { }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                        if (itemModels.Count >= 16) itemModels.Clear();
                        itemModels[key] = rows;
                    }
                    XmlElem found;
                    if (rows.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out found))
                    {
                        int slotNo = -1;
                        ModelNames.FindSlots(found, list, 0, ref slotNo);
                    }
                    if (list.Count > 0) break;
                }
            }
            return list;
        }

        /// <summary>
        /// Every model file of an item: the assets of its own slots, else of the client's item model list (read from
        /// its archive here: call this off the UI thread), else the model it is placed or dropped as.
        /// </summary>
        public List<ItemAsset> Assets(ItemQuery q)
        {
            if (q == null) return new List<ItemAsset>();
            if (q.Own.Count > 0) return new List<ItemAsset>(q.Own);
            List<ItemAsset> list = ClientItemAssets(q.Id);
            if (list.Count == 0 && q.Fallback != null) list.Add(q.Fallback);
            return list;
        }

        /// <summary>The bytes of a model file named by its game path, or null.</summary>
        public byte[] ReadGamePath(string gamePath)
        {
            string archive, entry;
            if (!ModelNames.Split(gamePath, out archive, out entry)) return null;
            lock (gate)
            {
                if (disposed) return null;
                foreach (M2dArchive a in Archives(archive))
                {
                    M2dEntry e = a.Find(entry);
                    if (e == null) continue;
                    if (e.Size > (ulong)ModelBuilder.MaxFileBytes) throw new NifException("the model file is too large");
                    return a.Read(e);
                }
            }
            return null;
        }

        /// <summary>The decoded texture a model names, or null. Looked for by file name in the texture archive, then in the model's own archive.</summary>
        public Texture ReadTexture(string name, string modelArchive)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string file = FileName(name);
            lock (gate)
            {
                if (disposed) return null;
                Texture t;
                if (textures.TryGetValue(file, out t)) return t;
                var places = Archives(ModelNames.TextureArchive);
                if (!string.IsNullOrEmpty(modelArchive)) places.AddRange(Archives(modelArchive));
                foreach (M2dArchive a in places)
                {
                    M2dEntry e = ByName(a, file);
                    if (e == null) continue;
                    string why;
                    if (e.Size > (ulong)ModelBuilder.MaxFileBytes) continue;
                    t = Dds.Decode(a.Read(e), out why);
                    if (t != null) break;
                }
                try { while (t != null && (t.Width > TextureSide || t.Height > TextureSide)) t = ModelRenderer.Half(t); }
                catch (OutOfMemoryException) { t = null; }
                long bytes = t == null ? 0 : 4L * t.Argb.Length;
                if (textures.Count >= KeptTextures || textureBytes + bytes > TextureBudget) { textures.Clear(); textureBytes = 0; }
                textures[file] = t;
                textureBytes += bytes;
                return t;
            }
        }

        /// <summary>The game path of the .nif an NPC's .kfm file names, or null with the reason.</summary>
        public string NpcNif(string kfmFile, out string why)
        {
            why = null;
            if (string.IsNullOrEmpty(kfmFile)) { why = "no model"; return null; }
            lock (gate)
            {
                if (disposed) { why = "no model"; return null; }
                foreach (M2dArchive a in Archives(ModelNames.NpcArchive))
                {
                    M2dEntry e = ByName(a, kfmFile);
                    if (e == null) continue;
                    string nif = Kfm.NifPath(a.Read(e));
                    if (nif == null) { why = "cannot show: " + kfmFile + " names no model file"; return null; }
                    string folder = e.Name.Replace('\\', '/');
                    int slash = folder.LastIndexOf('/');
                    folder = slash < 0 ? "" : folder.Substring(0, slash + 1);
                    string beside = folder + FileName(nif);
                    M2dEntry n = a.Find(beside) ?? ByName(a, FileName(nif));
                    if (n == null) { why = "cannot show: no file " + FileName(nif); return null; }
                    return "Data/" + ModelNames.ModelFolder + ModelNames.NpcArchive + "/" + n.Name.Replace('\\', '/');
                }
            }
            why = "cannot show: no file " + kfmFile;
            return null;
        }

        /// <summary>
        /// Where the nodes of a gender's character skeleton stand (0 male, 1 female), by node name; null when the data
        /// has no skeleton file or it does not read.
        /// </summary>
        public Dictionary<string, float[]> Skeleton(int gender)
        {
            gender = gender == 1 ? 1 : 0;
            lock (gate)
            {
                Dictionary<string, float[]> nodes;
                if (skeletons.TryGetValue(gender, out nodes)) return nodes;
            }
            Dictionary<string, float[]> read = null;
            try
            {
                byte[] bytes = ReadGamePath(ModelNames.SkeletonPath(gender));
                if (bytes != null) read = ModelBuilder.Nodes(NifFile.Read(bytes));
            }
            catch (ModKitException) { }
            catch (IOException) { }
            lock (gate) { if (!disposed) skeletons[gender] = read; }
            return read;
        }

        // Where the character node an asset hangs on stands, or null: the asset is then drawn as its file stores it.
        float[] TargetOf(ItemAsset a, int gender)
        {
            if (a == null || string.IsNullOrEmpty(a.TargetNode)) return null;
            Dictionary<string, float[]> nodes = Skeleton(a.Gender >= 0 ? a.Gender : gender);
            float[] m;
            return nodes != null && nodes.TryGetValue(a.TargetNode.Trim(), out m) ? m : null;
        }

        /// <summary>Reads and joins the model files; null with the reason when there is nothing to draw.</summary>
        public Model Load(IList<string> gamePaths, out string why)
        {
            var assets = new List<ItemAsset>();
            if (gamePaths != null) foreach (string p in gamePaths) assets.Add(new ItemAsset { Path = p });
            return Load(assets, 0, out why);
        }

        /// <summary>
        /// Reads and joins the model files of an item. A file that is not skinned and names a character node is put
        /// where that node stands in the gender's skeleton (a cap stored in the head bone's space stands upright);
        /// a skinned file already stands where the skeleton stands.
        /// </summary>
        public Model Load(IList<ItemAsset> assets, int gender, out string why)
        {
            why = null;
            if (assets == null || assets.Count == 0) { why = "no model"; return null; }
            if (!Available) { why = "cannot show: the data has no model archives"; return null; }
            var model = new Model();
            string firstWhy = null;
            foreach (ItemAsset asset in assets)
            {
                string path = asset.Path;
                string reason = null;
                try
                {
                    byte[] bytes = ReadGamePath(path);
                    if (bytes == null) reason = "no file " + FileName(path);
                    else
                    {
                        if (model.VertexCount >= ModelBuilder.MaxVertices || model.TriangleCount >= ModelBuilder.MaxTriangles) reason = ModelBuilder.TooLarge;
                        else
                        {
                            Model one = ModelBuilder.Build(bytes);
                            one.Place(TargetOf(asset, gender));
                            model.Add(one);
                        }
                    }
                }
                catch (ModKitException e) { reason = FileName(path) + ": " + e.Message; }
                catch (IOException e) { reason = FileName(path) + ": " + e.Message; }
                if (reason == null) continue;
                model.Notes.Add(reason);
                if (firstWhy == null) firstWhy = reason;
            }
            if (!model.Empty) return model;
            why = "cannot show: " + (firstWhy ?? "the model has no triangles");
            return null;
        }

        /// <summary>The still picture of the model files. Never throws for bad data: the picture carries a message instead.</summary>
        public ModelPicture Picture(IList<string> gamePaths, int width, int height, RenderOptions options)
        {
            var assets = new List<ItemAsset>();
            if (gamePaths != null) foreach (string p in gamePaths) assets.Add(new ItemAsset { Path = p });
            return Picture(assets, 0, width, height, options);
        }

        /// <summary>The still picture of an item's model files as the gender wears them (0 male, 1 female). Never throws for bad data.</summary>
        public ModelPicture Picture(IList<ItemAsset> assets, int gender, int width, int height, RenderOptions options)
        {
            var pic = new ModelPicture { Width = width, Height = height };
            try
            {
                string why;
                Model model = Load(assets, gender, out why);
                if (model == null) { pic.Message = why; return pic; }
                string archive = null, entry;
                foreach (ItemAsset p in assets) if (ModelNames.Split(p.Path, out archive, out entry)) break;
                var missing = new List<string>();
                string own = archive;
                pic.Argb = ModelRenderer.Render(model, width, height, delegate (string name)
                {
                    Texture t = ReadTexture(name, own);
                    if (t == null && !missing.Contains(name)) missing.Add(name);
                    return t;
                }, options);
                pic.Triangles = model.TriangleCount;
                pic.Notes.AddRange(model.Notes);
                foreach (string m in missing) pic.Notes.Add("no texture " + m);
            }
            catch (ModKitException e) { pic.Argb = null; pic.Message = "cannot show: " + e.Message; }
            catch (IOException e) { pic.Argb = null; pic.Message = "cannot show: " + e.Message; }
            catch (OutOfMemoryException) { pic.Argb = null; pic.Message = "cannot show: the model is too large"; }
            return pic;
        }

        /// <summary>
        /// The still picture of an item as a gender wears it (0 male, 1 female): one asset per slot. The picture says
        /// whether the other gender has another model. Never throws for bad data.
        /// </summary>
        public ModelPicture ItemPicture(ItemQuery q, int gender, int width, int height, RenderOptions options)
        {
            List<ItemAsset> all;
            try { all = Assets(q); }
            catch (ModKitException e) { return new ModelPicture { Width = width, Height = height, Message = "cannot show: " + e.Message }; }
            catch (IOException e) { return new ModelPicture { Width = width, Height = height, Message = "cannot show: " + e.Message }; }
            ModelPicture pic = Picture(ModelNames.Pick(all, gender), gender, width, height, options);
            pic.Genders = ModelNames.HasGenders(all);
            return pic;
        }

        /// <summary>The still picture of an NPC's model from the file name of its .kfm.</summary>
        public ModelPicture NpcPicture(string kfmFile, int width, int height, RenderOptions options)
        {
            string why, nif;
            try { nif = NpcNif(kfmFile, out why); }
            catch (ModKitException e) { nif = null; why = "cannot show: " + e.Message; }
            catch (IOException e) { nif = null; why = "cannot show: " + e.Message; }
            if (nif == null) return new ModelPicture { Width = width, Height = height, Message = why };
            return Picture(new[] { nif }, width, height, options);
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                foreach (M2dArchive a in open.Values) if (a != null) a.Dispose();
                open.Clear();
                byName.Clear();
                itemModels.Clear();
                textures.Clear();
                textureBytes = 0;
                skeletons.Clear();
            }
        }
    }
}
