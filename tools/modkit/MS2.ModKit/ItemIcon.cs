// An item's own icon from a PNG file: the image becomes a new entry of Resource/Image.m2d of each client data root
// that has the archive (the kms client and the global client read their own), item/icon/mod/<id>.png, held as a
// whole-file entry of the mod (files/<root>/item/icon/mod/<id>.png), and the item's property@slotIcon names it on
// both sides (the bundle row and the per-id files): ./Data/Resource/Image/item/icon/mod/<id>.png. The server does
// not draw icons, so its image archive gets nothing. Take puts the shipped slotIcon back and drops the entries.
//
// The shipped item icons are 60x60 RGBA PNG files; the plan warns about another size (PlannerIcons.cs).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MS2.ModKit
{
    /// <summary>The size and kind of a PNG image as its IHDR chunk says.</summary>
    public sealed class PngInfo
    {
        public int Width, Height, BitDepth, ColorType;

        static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>The header of the PNG bytes, or null with the reason when they are not a PNG image.</summary>
        public static PngInfo Read(byte[] b, out string problem)
        {
            problem = null;
            if (b == null || b.Length < 8 + 8 + 13 + 4) { problem = "not a PNG image (too short)"; return null; }
            for (int i = 0; i < Signature.Length; i++)
                if (b[i] != Signature[i]) { problem = "not a PNG image (no PNG signature)"; return null; }
            if (Be(b, 8) != 13 || b[12] != 'I' || b[13] != 'H' || b[14] != 'D' || b[15] != 'R') { problem = "not a PNG image (no IHDR chunk first)"; return null; }
            var p = new PngInfo { Width = (int)Math.Min(Be(b, 16), int.MaxValue), Height = (int)Math.Min(Be(b, 20), int.MaxValue), BitDepth = b[24], ColorType = b[25] };
            if (p.Width == 0 || p.Height == 0) { problem = "a PNG image of size " + p.Width + "x" + p.Height; return null; }
            return p;
        }

        static uint Be(byte[] b, int at) { return (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]); }

        public override string ToString() { return Width.ToString(CultureInfo.InvariantCulture) + "x" + Height.ToString(CultureInfo.InvariantCulture); }
    }

    public static class ItemIcon
    {
        /// <summary>The size of the shipped item icons.</summary>
        public const int Size = 60;
        /// <summary>The folder of Resource/Image.m2d the mods' own item icons go to.</summary>
        public const string Folder = "item/icon/mod/";
        /// <summary>The largest icon file taken (the shipped 60x60 icons have a few kilobytes).</summary>
        public const int MaxBytes = 4 << 20;
        /// <summary>The most pixels a view decodes for a preview (a PNG header may claim a huge size).</summary>
        public const long MaxPixels = 4096L * 4096L;

        /// <summary>The image entry of the item's own icon: item/icon/mod/&lt;id&gt;.png.</summary>
        public static string EntryOf(long id) { return Folder + id.ToString(CultureInfo.InvariantCulture) + ".png"; }

        /// <summary>The slotIcon value that names the item's own icon.</summary>
        public static string SlotIconOf(long id) { return Icons.GamePrefix + EntryOf(id); }

        /// <summary>The image archives of the client roots the data set has: client:Resource/Image.m2d, gms-client:Resource/Image.m2d.</summary>
        public static List<string> ClientArchives(DataSet data)
        {
            var l = new List<string>();
            foreach (Profile p in Profile.All)
            {
                if (p.IsServer || !data.Has(p)) continue;
                string a = p.Root + ":" + Icons.ImageArchive;
                if (data.Exists(a)) l.Add(a);
            }
            return l;
        }

        static bool IsImageArchive(string archive)
        {
            if (archive == null) return false;
            int colon = archive.IndexOf(':');
            string path = (colon >= 0 ? archive.Substring(colon + 1) : archive).Replace('\\', '/');
            return string.Equals(path, Icons.ImageArchive, StringComparison.OrdinalIgnoreCase);
        }

        static IEnumerable<EntryOp> Ops(Mod mod, string entry)
        {
            foreach (ModOp o in mod.Ops)
            {
                var e = o as EntryOp;
                if (e != null && IsImageArchive(e.Archive) && string.Equals((e.Name ?? "").Replace('\\', '/'), entry, StringComparison.OrdinalIgnoreCase)) yield return e;
            }
        }

        /// <summary>True when the mod gives the item its own icon (the entry is in the mod and slotIcon names it).</summary>
        public static bool HasOwn(ItemDraft d)
        {
            if (!string.Equals(d.IconEntry(), EntryOf(d.Id), StringComparison.OrdinalIgnoreCase)) return false;
            foreach (EntryOp o in Ops(d.Editor.Mod, EntryOf(d.Id))) return true;
            return false;
        }

        /// <summary>
        /// Gives the item the PNG image as its own icon: the image entry in each client image archive, slotIcon on both
        /// sides. Returns the archives written. Throws when the bytes are not a PNG image or no client root has an image
        /// archive.
        /// </summary>
        public static List<string> Set(ItemDraft d, byte[] png)
        {
            string problem;
            if (PngInfo.Read(png, out problem) == null) throw new ModKitException("item " + d.Id + ": the icon is " + problem);
            if (png.Length > MaxBytes) throw new ModKitException("item " + d.Id + ": the icon file has " + png.Length + " bytes, more than " + MaxBytes + " (the shipped icons have a few kilobytes)");
            ItemEditor ed = d.Editor;
            List<string> archives = ClientArchives(ed.Data);
            if (archives.Count == 0) throw new ModKitException("no client data folder has " + Icons.ImageArchive + ": the icon has nowhere to go");
            // slotIcon first: when the item has none to set (or a side refuses the value) no image entry is left in the mod
            if (d.SetBoth(Icons.ItemIconField, SlotIconOf(d.Id)) == 0)
                throw new ModKitException("item " + d.Id + " has no " + Icons.ItemIconField + " to set");
            var trig = new TriggerEditor(ed.Data, ed.Mod);
            string entry = EntryOf(d.Id);
            foreach (string a in archives) trig.Put(a, entry, png);
            return archives;
        }

        /// <summary>Takes the item's own icon out of the mod: slotIcon as shipped again (for a new item: as the item it copies), the image entries gone.</summary>
        public static void Take(ItemDraft d)
        {
            Mod mod = d.Editor.Mod;
            string entry = EntryOf(d.Id);
            foreach (EntryOp o in new List<EntryOp>(Ops(mod, entry)))
            {
                mod.Ops.Remove(o);
                if (o.File != null) mod.Files.Remove(o.File.Replace('\\', '/'));
            }
            if (string.Equals(d.IconEntry(), entry, StringComparison.OrdinalIgnoreCase))
            {
                // each section back to its own original value (the two sides may name different icons as shipped)
                foreach (EntitySection s in d.Sections.ToArray())
                {
                    if (!s.Chain.StartsWith("environment[", StringComparison.Ordinal)) continue;
                    if (s.Part.Kind != d.Editor.Entity && s.Part.Kind != EntityEditor.FileKind) continue;
                    if (EntityDraft.BelowEnv(s.Chain) != FieldMapEntry.ElemOf(Icons.ItemIconField)) continue;
                    string attr = FieldMapEntry.AttrOf(Icons.ItemIconField);
                    if (d.Get(s, attr) != d.GetOriginal(s, attr)) d.Set(s, attr, d.GetOriginal(s, attr));
                }
            }
        }

        /// <summary>
        /// The bytes of an image entry the mod holds (any image archive; its unsaved file first, else its file in the mod
        /// folder), or null when the mod has none or the file cannot be read.
        /// </summary>
        public static byte[] ModImage(Mod mod, string entry)
        {
            if (mod == null || string.IsNullOrEmpty(entry)) return null;
            foreach (EntryOp o in Ops(mod, entry.Replace('\\', '/')))
            {
                if (o.Delete || o.File == null) continue;
                byte[] b;
                if (mod.Files.TryGetValue(o.File.Replace('\\', '/'), out b)) return b;
                string full = Mod.PathIn(mod.Dir, o.File);
                try { if (full != null && File.Exists(full)) return File.ReadAllBytes(full); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }
    }
}
