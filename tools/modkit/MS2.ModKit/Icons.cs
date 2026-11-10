// Item icons and npc portraits: which image an item or an npc shows, and its bytes from the image archive.
//
//   item   property@slotIcon (the bundle row and the per-id file both have it). The client's rule: empty -> the
//          item's own id, item/icon/<id>.png; a value that does not start with '.' (a number or a short name) ->
//          item/icon/<value>.png; else the value is a path under ./Data/Resource/Image/.
//   npc    @portrait on the bundle row, basic@portrait in the per-id file: a path under ./Data/Resource/Image/.
//
// The images are PNG files in Resource/Image.m2d of a data root. Not every root has one (a global client may ship
// without it), so the image is read from the first root that has the entry, the profile's own root first.
using System;
using System.Collections.Generic;
using System.Globalization;
using MS2.ModKit.M2d;

namespace MS2.ModKit
{
    public static class Icons
    {
        public const string ImageArchive = "Resource/Image.m2d";
        public const string GamePrefix = "./Data/Resource/Image/";
        public const string ItemIconField = "property@slotIcon";
        public const string NpcPortraitField = "@portrait", NpcPortraitFileField = "basic@portrait";

        /// <summary>The image path the client builds for an item from its slotIcon value (null or "" for none).</summary>
        public static string ItemIconPath(long id, string slotIcon)
        {
            string v = (slotIcon ?? "").Trim();
            if (v.Length == 0) return GamePrefix + "item/icon/" + id.ToString(CultureInfo.InvariantCulture) + ".png";
            if (v[0] != '.') return GamePrefix + "item/icon/" + v + ".png";
            return v;
        }

        /// <summary>The entry of Resource/Image.m2d a game path names, or null when the path is not under ./Data/Resource/Image/.</summary>
        public static string ImageEntry(string gamePath)
        {
            if (string.IsNullOrEmpty(gamePath)) return null;
            string p = gamePath.Trim().Replace('\\', '/');
            if (p.StartsWith("./", StringComparison.Ordinal)) p = p.Substring(2);
            else if (p.StartsWith("/", StringComparison.Ordinal)) p = p.Substring(1);
            const string under = "Data/Resource/Image/";
            if (!p.StartsWith(under, StringComparison.OrdinalIgnoreCase)) return null;
            p = p.Substring(under.Length);
            return p.Length == 0 ? null : p;
        }

        /// <summary>The image entry of an item's icon, from its slotIcon value.</summary>
        public static string ItemIconEntry(long id, string slotIcon) { return ImageEntry(ItemIconPath(id, slotIcon)); }

        /// <summary>
        /// The icon entry of the item as a profile has it, with the mod's edits: the profile's own row or file when the
        /// editor reads it, else the item's field as GetField gives it. Null when the item has no icon path.
        /// </summary>
        public static string IconEntry(this ItemDraft d, Profile p)
        {
            string v = null;
            bool found = false;
            if (p != null)
                foreach (EntitySection s in d.Sections)
                {
                    if (DataSet.RootOf(s.Part.Archive) != p.Root || !s.Chain.StartsWith("environment[", StringComparison.Ordinal)) continue;
                    if (s.Part.Kind != d.Editor.Entity && s.Part.Kind != EntityEditor.FileKind) continue;
                    if (EntityDraft.BelowEnv(s.Chain) != FieldMapEntry.ElemOf(ItemIconField)) continue;
                    v = d.Get(s, FieldMapEntry.AttrOf(ItemIconField));
                    found = true;
                    break;
                }
            if (!found) v = d.GetField(ItemIconField);
            return ItemIconEntry(d.Id, v);
        }

        /// <summary>The icon entry of the item: the kms-client row's, else the per-id file's.</summary>
        public static string IconEntry(this ItemDraft d) { return d.IconEntry(null); }

        /// <summary>The portrait entry of an npc (its environment's portrait, else the per-id file's basic@portrait), with the mod's edits; null when it has none.</summary>
        public static string PortraitEntry(this EntityDraft d)
        {
            string v = d.GetField(NpcPortraitField);
            return string.IsNullOrEmpty(v) ? null : ImageEntry(v);
        }
    }

    /// <summary>
    /// Reads images from the Resource/Image.m2d archives of a data set. Safe to call from a background thread: one
    /// read at a time, and the archives are opened on their own (not through DataSet.Open).
    /// </summary>
    public sealed class ImageSource : IDisposable
    {
        readonly object gate = new object();
        readonly List<string> files = new List<string>();
        readonly Dictionary<string, M2dArchive> open = new Dictionary<string, M2dArchive>();
        readonly List<string> roots = new List<string>();

        /// <param name="first">The profile whose own image archive is looked in first (null: kms-client, gms-client, server).</param>
        public ImageSource(DataSet data, Profile first)
        {
            if (data == null) throw new ArgumentNullException("data");
            var order = new List<Profile>();
            if (first != null) order.Add(first);
            foreach (Profile p in Profile.All) if (!order.Contains(p)) order.Add(p);
            foreach (Profile p in order)
            {
                string a = p.Root + ":" + Icons.ImageArchive;
                if (!data.Has(p) || !data.Exists(a)) continue;
                files.Add(data.Resolve(a));
                roots.Add(p.Root);
            }
        }

        /// <summary>The data roots that have an image archive, in the order they are read.</summary>
        public IList<string> Roots { get { return roots.AsReadOnly(); } }

        /// <summary>False when no root of the data set has an image archive.</summary>
        public bool Available { get { return files.Count > 0; } }

        M2dArchive Archive(string file)
        {
            M2dArchive a;
            if (!open.TryGetValue(file, out a)) open[file] = a = M2dArchive.Open(file);
            return a;
        }

        /// <summary>The bytes of the entry from the first archive that has it, or null.</summary>
        public byte[] Read(string entry)
        {
            if (string.IsNullOrEmpty(entry)) return null;
            lock (gate)
            {
                foreach (string f in files)
                {
                    M2dArchive a = Archive(f);
                    M2dEntry e = a.Find(entry);
                    if (e != null) return a.Read(e);
                }
            }
            return null;
        }

        /// <summary>True when an archive has the entry.</summary>
        public bool Has(string entry)
        {
            if (string.IsNullOrEmpty(entry)) return false;
            lock (gate)
            {
                foreach (string f in files) if (Archive(f).Find(entry) != null) return true;
            }
            return false;
        }

        public void Dispose()
        {
            lock (gate)
            {
                foreach (M2dArchive a in open.Values) a.Dispose();
                open.Clear();
            }
        }
    }
}
