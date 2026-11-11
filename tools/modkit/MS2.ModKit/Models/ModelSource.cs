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
            archive = p.Substring(0, slash);
            entry = p.Substring(slash + 1);
            return true;
        }

        static bool IsModelPath(string v)
        {
            string a, e;
            return v != null && v.TrimEnd().EndsWith(".nif", StringComparison.OrdinalIgnoreCase) && Split(v, out a, out e);
        }

        /// <summary>The entry of the client file that lists the slot models of an item id.</summary>
        public static string ItemModelEntry(long id) { return "itemmodel/" + (id / 100000).ToString(CultureInfo.InvariantCulture) + ".xml"; }

        // The first asset of each slot: a slot with one asset per gender shows the first.
        static void SlotAssets(XmlElem slots, List<string> into)
        {
            foreach (XmlElem slot in slots.Children)
            {
                if (slot.Name != "slot") continue;
                foreach (XmlElem asset in slot.Children)
                {
                    if (asset.Name != "asset") continue;
                    string v = asset.Attr("name");
                    if (!IsModelPath(v)) continue;
                    if (!into.Contains(v)) into.Add(v);
                    break;
                }
            }
        }

        /// <summary>
        /// The model files of an item as game paths, with the draft's edits: the first asset of each slot of the item's
        /// own file, else of the client's item model list; without slots, the model the item is placed or dropped as.
        /// </summary>
        public static List<string> ItemAssets(EntityDraft d)
        {
            var list = new List<string>();
            if (d == null) return list;
            var seenSlot = new List<XmlElem>();
            foreach (EntitySection s in d.Sections)
            {
                XmlElem e = s.Elem;
                if (e == null || e.Name != "asset" || e.Parent == null || e.Parent.Name != "slot") continue;
                if (seenSlot.Contains(e.Parent)) continue;
                string v = d.Get(s, "name");
                if (!IsModelPath(v)) continue;
                seenSlot.Add(e.Parent);
                if (!list.Contains(v)) list.Add(v);
            }
            if (list.Count == 0)
            {
                // a slot's assets that are not sections of their own: read the shipped elements
                foreach (EntityPart p in d.Parts)
                    if (p.Root != null) FindSlots(p.Root, list, 0);
            }
            if (list.Count == 0) ClientItemModel(d, list);
            if (list.Count == 0)
            {
                // the bundle row or the item's own file, whichever names one
                foreach (string[] field in new[] { new[] { "install", "asset" }, new[] { "drop", "dropNif" } })
                {
                    foreach (EntitySection s in d.Sections)
                    {
                        if (s.Elem == null || s.Elem.Name != field[0]) continue;
                        string v = d.Get(s, field[1]);
                        if (IsModelPath(v)) { list.Add(v); break; }
                    }
                    if (list.Count > 0) break;
                }
            }
            return list;
        }

        static void FindSlots(XmlElem e, List<string> into, int depth)
        {
            if (depth > 6) return;
            foreach (XmlElem c in e.Children)
            {
                if (c.Name == "slots") SlotAssets(c, into);
                else FindSlots(c, into, depth + 1);
            }
        }

        static void ClientItemModel(EntityDraft d, List<string> into)
        {
            DataSet data = d.Editor.Data;
            string entry = ItemModelEntry(d.Id);
            foreach (Profile p in data.Profiles)
            {
                if (p.IsServer) continue;
                string archive = p.Root + ":Xml.m2d";
                string key = "itemmodel\n" + archive + "\n" + entry;
                object o;
                if (!data.Cache.TryGetValue(key, out o))
                {
                    var rows = new Dictionary<string, XmlElem>(StringComparer.Ordinal);
                    try
                    {
                        if (data.Exists(archive))
                        {
                            M2dArchive a = data.Open(archive);
                            M2dEntry e = a.Find(entry);
                            XmlDoc doc = e == null ? null : XmlDoc.Parse(a.Read(e), archive + " " + e.Name);
                            if (doc != null && doc.Root != null)
                                foreach (XmlElem row in doc.Root.Children)
                                {
                                    string id = row.Attr("id");
                                    if (row.Name == "ItemModel" && id != null && !rows.ContainsKey(id)) rows[id] = row;
                                }
                        }
                    }
                    catch (ModKitException) { }
                    catch (IOException) { }
                    data.Cache[key] = o = rows;
                }
                XmlElem found;
                if (((Dictionary<string, XmlElem>)o).TryGetValue(d.Id.ToString(CultureInfo.InvariantCulture), out found))
                    FindSlots(found, into, 0);
                if (into.Count > 0) return;
            }
        }

        /// <summary>The file name of an NPC's .kfm ("02010001_stan.kfm") from its model field, or null when it names none.</summary>
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

    /// <summary>The .kfm file of an animated model: only the name of its .nif is read.</summary>
    public static class Kfm
    {
        public const string Magic = ";Gamebryo KFM File Version ";

        /// <summary>The .nif path a binary .kfm names (as written, e.g. ".\02010001_Stan.nif"), or null when the bytes are not one.</summary>
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
    }

    /// <summary>
    /// Reads models and textures from the Resource/Model archives of a data set. Safe to call from a background
    /// thread: one call at a time, and the archives are opened on their own (not through DataSet.Open).
    /// </summary>
    public sealed class ModelSource : IDisposable
    {
        const int KeptTextures = 24;

        readonly object gate = new object();
        readonly List<string> folders = new List<string>();        // .../Resource/Model of each root that has one
        readonly Dictionary<string, M2dArchive> open = new Dictionary<string, M2dArchive>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<M2dArchive, Dictionary<string, M2dEntry>> byName = new Dictionary<M2dArchive, Dictionary<string, M2dEntry>>();
        readonly Dictionary<string, Texture> textures = new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase);
        bool disposed;

        public ModelSource(DataSet data)
        {
            if (data == null) throw new ArgumentNullException("data");
            foreach (Profile p in Profile.All)
            {
                if (!data.Has(p)) continue;
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
                    if (e != null) return a.Read(e);
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
                    t = Dds.Decode(a.Read(e), out why);
                    if (t != null) break;
                }
                if (textures.Count >= KeptTextures) textures.Clear();
                textures[file] = t;
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

        /// <summary>Reads and joins the model files; null with the reason when there is nothing to draw.</summary>
        public Model Load(IList<string> gamePaths, out string why)
        {
            why = null;
            if (gamePaths == null || gamePaths.Count == 0) { why = "no model"; return null; }
            if (!Available) { why = "cannot show: the data has no model archives"; return null; }
            var model = new Model();
            string firstWhy = null;
            foreach (string path in gamePaths)
            {
                string reason = null;
                try
                {
                    byte[] bytes = ReadGamePath(path);
                    if (bytes == null) reason = "no file " + FileName(path);
                    else model.Add(ModelBuilder.Build(bytes));
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
            var pic = new ModelPicture { Width = width, Height = height };
            try
            {
                string why;
                Model model = Load(gamePaths, out why);
                if (model == null) { pic.Message = why; return pic; }
                string archive = null, entry;
                foreach (string p in gamePaths) if (ModelNames.Split(p, out archive, out entry)) break;
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
                textures.Clear();
            }
        }
    }
}
