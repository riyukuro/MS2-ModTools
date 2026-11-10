// The mods folder as a list the user arranges: which mods take part in a plan or an apply, and in what order
// (a later mod wins where two set the same thing).
//   <mods folder>/modkit-mods.xml      <mods><mod folder="a.first" enabled="true"/>...</mods>
// The file lives in the mods folder, not in a mod, so a mod folder can be copied to someone else as it is.
// A mod folder the file does not name yet is enabled and goes to the end, in the order of the folder names.
// Folders whose name starts with '.' or '_' are not mods (as in ModFolder).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public sealed class ModListItem
    {
        /// <summary>Name of the mod's folder under the mods folder.</summary>
        public string Folder;
        public string Id = "", Name = "", Version = "";
        public bool Enabled = true;
        /// <summary>Why the mod.xml could not be read; null when it could.</summary>
        public string Error;

        public override string ToString() { return Error != null ? Folder + " (unreadable)" : Id == Folder ? Id : Id + " (" + Folder + ")"; }
    }

    public sealed class ModList
    {
        public const string FileName = "modkit-mods.xml";

        public readonly string Dir;
        public readonly List<ModListItem> Items = new List<ModListItem>();

        ModList(string dir) { Dir = dir; }

        /// <summary>Reads the list of a mods folder. A missing folder (or null) gives an empty list.</summary>
        public static ModList Load(string modsDir)
        {
            var list = new ModList(string.IsNullOrEmpty(modsDir) ? null : Path.GetFullPath(modsDir));
            if (list.Dir == null || !Directory.Exists(list.Dir)) return list;

            var folders = new List<string>();
            string[] dirs = Directory.GetDirectories(list.Dir);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (string d in dirs)
            {
                string name = Path.GetFileName(d);
                if (name.StartsWith(".", StringComparison.Ordinal) || name.StartsWith("_", StringComparison.Ordinal)) continue;
                if (File.Exists(Path.Combine(d, "mod.xml"))) folders.Add(name);
            }

            string f = Path.Combine(list.Dir, FileName);
            if (File.Exists(f))
            {
                XmlDoc doc = XmlDoc.Parse(File.ReadAllBytes(f), f);
                if (doc.Root == null || doc.Root.Name != "mods") throw new ModKitException(f + ": not a mod list");
                foreach (XmlElem e in doc.Root.Children)
                {
                    if (e.Name != "mod") continue;
                    string folder = e.Attr("folder");
                    int at = folders.FindIndex(delegate(string n) { return string.Equals(n, folder, StringComparison.OrdinalIgnoreCase); });
                    if (at < 0) continue;       // the folder is gone
                    list.Items.Add(new ModListItem { Folder = folders[at], Enabled = e.Attr("enabled") != "false" });
                    folders.RemoveAt(at);
                }
            }
            foreach (string name in folders) list.Items.Add(new ModListItem { Folder = name });

            foreach (ModListItem it in list.Items)
            {
                try
                {
                    Mod m = Mod.Load(Path.Combine(list.Dir, it.Folder));
                    it.Id = m.Id; it.Name = m.Name ?? ""; it.Version = m.Version ?? "";
                }
                catch (ModKitException x) { it.Error = x.Message; }
            }
            return list;
        }

        /// <summary>Writes the order and the enabled states to the file in the mods folder.</summary>
        public void Save()
        {
            if (Dir == null) throw new ModKitException("the mods folder is not set");
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<mods version=\"1\">\n");
            foreach (ModListItem it in Items)
                sb.Append("  <mod folder=\"").Append(XmlDoc.EscapeAttr(it.Folder, '"')).Append("\" enabled=\"").Append(it.Enabled ? "true" : "false").Append("\"/>\n");
            sb.Append("</mods>\n");
            Directory.CreateDirectory(Dir);
            string f = Path.Combine(Dir, FileName);
            File.WriteAllBytes(f + ".tmp", new UTF8Encoding(false).GetBytes(sb.ToString()));
            if (File.Exists(f)) File.Delete(f);
            File.Move(f + ".tmp", f);
        }

        /// <summary>Moves an item one place up (delta -1) or down (+1); returns its new index.</summary>
        public int Move(int index, int delta)
        {
            int to = index + delta;
            if (index < 0 || index >= Items.Count || to < 0 || to >= Items.Count) return index;
            ModListItem it = Items[index];
            Items.RemoveAt(index);
            Items.Insert(to, it);
            return to;
        }

        public ModListItem FindId(string id)
        {
            foreach (ModListItem it in Items) if (it.Error == null && it.Id == id) return it;
            return null;
        }

        /// <summary>
        /// The mods a plan or an apply uses: the enabled ones in list order. The mod being edited always takes
        /// part, as it is in memory: at its place in the list when its id is there (enabled or not), else at
        /// the end. An edited mod without any operation is left out.
        /// </summary>
        public List<Mod> LoadEnabled(Mod current)
        {
            var mods = new List<Mod>();
            bool seen = false;
            foreach (ModListItem it in Items)
            {
                if (current != null && it.Error == null && it.Id == current.Id)
                {
                    if (!seen && current.Ops.Count > 0) mods.Add(current);
                    seen = true;
                    continue;
                }
                if (!it.Enabled) continue;
                if (it.Error != null) throw new ModKitException("mod folder " + it.Folder + " is enabled but cannot be read: " + it.Error);
                mods.Add(Mod.Load(Path.Combine(Dir, it.Folder)));
            }
            if (current != null && !seen && current.Ops.Count > 0) mods.Add(current);
            return mods;
        }
    }
}
