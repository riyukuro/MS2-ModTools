// What an editor front end needs between its settings and the patcher: the four folders, the mod being edited,
// and Plan / Apply / Revert over the whole mods folder with their results as text. The Toolbench plugin
// (tools/modkit/plugin) is a set of controls over this class and TableDraft.
//
// The mods folder is the set that gets applied: every subfolder with a mod.xml (see ModFolder). The mod being
// edited is planned from memory and saved into the mods folder before an apply, so the workspace state names a
// folder for every mod and a later revert of one mod can load the others again.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MS2.ModKit.M2d;

namespace MS2.ModKit
{
    public sealed class EditorSession : IDisposable
    {
        public readonly DataSet Data;
        public readonly string ModsDir, WorkspaceDir;
        /// <summary>The mod that takes the edits. Never null.</summary>
        public Mod Mod { get; private set; }

        /// <param name="clientData">folder with the client's Xml.m2d; null or "" when not set</param>
        /// <param name="serverData">folder with the server's Xml.m2d; null or "" when not set</param>
        /// <param name="modsDir">folder of mod folders; needed to save and apply</param>
        /// <param name="workspaceDir">folder the patched copies go to; needed to apply and revert</param>
        public EditorSession(string clientData, string serverData, string modsDir, string workspaceDir)
        {
            if (string.IsNullOrEmpty(clientData) && string.IsNullOrEmpty(serverData))
                throw new ModKitException("neither the client data folder nor the server data folder is set");
            Data = new DataSet(clientData, serverData);
            ModsDir = string.IsNullOrEmpty(modsDir) ? null : Path.GetFullPath(modsDir);
            WorkspaceDir = string.IsNullOrEmpty(workspaceDir) ? null : Path.GetFullPath(workspaceDir);
            NewMod("my.mod");
        }

        public void Dispose() { Data.Dispose(); }

        // ---- the mod being edited ----

        /// <summary>Starts an empty mod. The id is checked the way a manifest's is.</summary>
        public Mod NewMod(string id)
        {
            Mod = Mod.Parse("<mod id=\"" + Xml.XmlDoc.EscapeAttr(id ?? "", '"') + "\" name=\"\" version=\"1\"/>", "the new mod");
            return Mod;
        }

        /// <summary>Makes a mod that is in memory already the one being edited (a front end keeps its mod when its folders change).</summary>
        public void Adopt(Mod mod)
        {
            if (mod == null) throw new ArgumentNullException("mod");
            Mod = mod;
        }

        /// <summary>Loads a mod: a path to its folder or mod.xml, or the name of a folder under the mods folder.</summary>
        public Mod LoadMod(string pathOrName)
        {
            string p = pathOrName;
            if (!Directory.Exists(p) && !File.Exists(p) && ModsDir != null) p = Path.Combine(ModsDir, pathOrName);
            Mod = Mod.Load(p);
            return Mod;
        }

        /// <summary>Saves the mod to &lt;mods folder&gt;/&lt;id&gt;/mod.xml and returns the folder.</summary>
        public string SaveMod()
        {
            if (ModsDir == null) throw new ModKitException("the mods folder is not set");
            if (Mod.Ops.Count == 0) throw new ModKitException("mod " + Mod.Id + " holds no change yet; there is nothing to save");
            string dir = ModFolder.DirOf(ModsDir, Mod.Id);
            if ((Mod.Dir == null || !SamePath(Mod.Dir, dir)) && File.Exists(Path.Combine(dir, "mod.xml")))
                throw new ModKitException(dir + " already holds a mod; give this one another id");
            Mod.Save(dir);
            return dir;
        }

        static bool SamePath(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
        }

        // ---- what there is to edit ----

        public List<string> Archives() { return Data.ListArchives(); }

        /// <summary>Entry names of an archive that contain the filter text (any case), at most max of them.</summary>
        public List<string> Entries(string archive, string filter, int max)
        {
            var list = new List<string>();
            filter = (filter ?? "").Trim().Replace('\\', '/');
            foreach (M2dEntry e in Data.Open(archive).Entries)
            {
                if (filter.Length > 0 && e.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                list.Add(e.Name);
                if (list.Count >= max) break;
            }
            return list;
        }

        public TableDraft OpenTable(string archive, string entry, string parentSelector)
        {
            return new TableDraft(Data, Mod, archive, entry, parentSelector);
        }

        /// <summary>The same table in the other data root ("client:Xml.m2d" for "server:Xml.m2d"), or null when it is not there.</summary>
        public string OtherSide(string archive, string entry)
        {
            archive = DataSet.NormalizeRef(archive);
            foreach (string root in Data.Roots)
            {
                if (root == DataSet.RootOf(archive)) continue;
                string other = root + ":" + DataSet.PathOf(archive);
                if (Data.Exists(other) && Data.Open(other).Find(entry) != null) return other;
            }
            return null;
        }

        // ---- plan, apply, revert ----

        /// <summary>The mods that an apply would use: the mods folder, with the mod being edited as it is in memory.</summary>
        public List<Mod> ActiveMods() { return ModFolder.LoadAllWith(ModsDir, Mod); }

        public Plan Plan() { return Planner.Build(Data, ActiveMods(), null); }

        /// <summary>Saves the mod being edited (if it holds anything) and writes patched copies of every archive the mods touch into the workspace.</summary>
        public ApplyResult Apply()
        {
            if (WorkspaceDir == null) throw new ModKitException("the workspace folder is not set");
            if (ModsDir == null) throw new ModKitException("the mods folder is not set");
            if (Mod.Ops.Count > 0) SaveMod();
            List<Mod> mods = ModFolder.LoadAll(ModsDir);
            if (mods.Count == 0) throw new ModKitException("no mod in " + ModsDir + " and no pending change; there is nothing to apply");
            return new Workspace(WorkspaceDir).Apply(Data, mods, null);
        }

        /// <summary>Removes every patched copy from the workspace. The mods folder is left as it is.</summary>
        public ApplyResult Revert()
        {
            if (WorkspaceDir == null) throw new ModKitException("the workspace folder is not set");
            return new Workspace(WorkspaceDir).Revert(Data, null, null);
        }

        // ---- results as text ----

        public static string Report(Plan p)
        {
            var sb = new StringBuilder();
            sb.Append(p.Mods.Count).Append(" mod(s), ").Append(p.Changes.Count).Append(" change(s), ")
              .Append(p.Errors.Count).Append(" error(s), ").Append(p.Conflicts.Count).Append(" conflict(s), ")
              .Append(p.Warnings.Count).Append(" warning(s)\r\n");
            foreach (PlanMessage m in p.Errors) sb.Append("error: ").Append(m).Append("\r\n");
            foreach (PlanMessage m in p.Conflicts) sb.Append("conflict: ").Append(m).Append("\r\n");
            foreach (PlanMessage m in p.Warnings) sb.Append("warning: ").Append(m).Append("\r\n");
            foreach (PlanMessage m in p.Notes) sb.Append("note: ").Append(m).Append("\r\n");
            foreach (PlanChange c in p.Changes) sb.Append("  ").Append(c).Append("\r\n");
            return sb.ToString();
        }

        public static string Report(ApplyResult r)
        {
            var sb = new StringBuilder();
            if (r.Plan != null) sb.Append(Report(r.Plan));
            if (!r.Applied) return sb.Append("nothing written: ").Append(r.Reason).Append("\r\n").ToString();
            foreach (ApplyOutput o in r.Outputs)
                sb.Append("wrote ").Append(o.Path).Append(" (").Append(o.Archive).Append(": ")
                  .Append(o.Copied ? "copied as it is" : o.Replaced + " replaced, " + o.Added + " added, " + o.Deleted + " deleted, " + o.Kept + " kept")
                  .Append(")\r\n");
            foreach (string f in r.Removed) sb.Append("removed ").Append(f).Append("\r\n");
            if (r.Outputs.Count == 0 && r.Removed.Count == 0) sb.Append("nothing to write or remove\r\n");
            return sb.ToString();
        }
    }
}
