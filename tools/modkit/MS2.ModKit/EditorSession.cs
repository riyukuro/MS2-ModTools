// What an editor front end needs between its settings and the patcher: the four folders, the mod being edited,
// and Plan / Apply / Revert over the whole mods folder with their results as text. The Toolbench plugin
// (tools/modkit/plugin) is a set of controls over this class and TableDraft.
//
// The mods folder holds the mods; the ones that are enabled there get applied, in the order of its list (see
// ModList). The mod being edited always takes part: it is planned from memory and saved into the mods folder
// before an apply, so the state names a folder for every mod and a later revert of one mod can load the others.
// An apply writes patched archives into the workspace, or (ApplyTarget.LooseFiles) the client's entries as
// plain files into a game folder (see LooseTarget).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MS2.ModKit.M2d;

namespace MS2.ModKit
{
    public enum ApplyTarget
    {
        /// <summary>Patched copies of the archives in the workspace folder.</summary>
        Workspace,
        /// <summary>Plain files under the Data folder of a game folder (client only).</summary>
        LooseFiles
    }

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
            : this(clientData, null, serverData, modsDir, workspaceDir) { }

        /// <summary>A session over any of the three profiles (see Profile.cs): the Korean client's, the global client's and the server's data folder.</summary>
        /// <param name="kmsClientData">folder with the Korean client's Xml.m2d (the "client" root); null or "" when not set</param>
        /// <param name="gmsClientData">folder with the global client's Xml.m2d (the "gms-client" root); null or "" when not set</param>
        /// <param name="serverData">folder with the server's Xml.m2d; null or "" when not set</param>
        public EditorSession(string kmsClientData, string gmsClientData, string serverData, string modsDir, string workspaceDir)
        {
            if (string.IsNullOrEmpty(kmsClientData) && string.IsNullOrEmpty(gmsClientData) && string.IsNullOrEmpty(serverData))
                throw new ModKitException("no data folder is set (Korean client, global client or server)");
            Data = new DataSet(kmsClientData, gmsClientData, serverData);
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

        /// <summary>The item editor over the data and the mod being edited (see Items.cs). Ask again after the mod is replaced.</summary>
        public ItemEditor Items()
        {
            var e = new ItemEditor(Data, Mod);
            if (Maps != null) e.Maps = Maps;
            return e;
        }

        /// <summary>The sync report of one id of an entity that has a field map ("item"), over the enabled mods and the mod being edited; plan = null: the data as it is.</summary>
        public SyncReport Sync(string entity, long id, Plan plan)
        {
            EntityMap em = (Maps ?? FieldMaps.Default).Entity(entity);
            if (em == null) throw new ModKitException("no field map for \"" + entity + "\"");
            return SyncReport.ForId(Data, plan, em, id);
        }

        /// <summary>The rows of other tables that belong to an id (the ref lines of the entity's field map), in the data as shipped.</summary>
        public RefList Refs(string entity, long id, int maxPerRule)
        {
            EntityMap em = (Maps ?? FieldMaps.Default).Entity(entity);
            if (em == null) throw new ModKitException("no field map for \"" + entity + "\"");
            return ServerRefs.Find(Data, em, id, maxPerRule);
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

        /// <summary>Where an apply writes the client's part. The server's part always goes to the workspace as patched archives.</summary>
        public ApplyTarget Target = ApplyTarget.Workspace;

        /// <summary>The game folder (the one that holds Data) that loose files are written into; needed for ApplyTarget.LooseFiles.</summary>
        public string GameDir;

        /// <summary>Field maps for the two-sided sync; null = the maps inside the library.</summary>
        public FieldMaps Maps;

        /// <summary>true: a plan writes nothing to the other side of a mapped field (it warns instead).</summary>
        public bool NoSync;

        /// <summary>What follows an apply or a revert on the server's side: a target folder and a command, both off until set.</summary>
        public readonly ServerStep Server = new ServerStep();

        /// <summary>The mods folder as a list with order and enabled states.</summary>
        public ModList ModList() { return MS2.ModKit.ModList.Load(ModsDir); }

        /// <summary>The mods that a plan or an apply uses: the enabled mods in list order, with the mod being edited as it is in memory.</summary>
        public List<Mod> ActiveMods() { return ModList().LoadEnabled(Mod); }

        LooseTarget Loose()
        {
            if (string.IsNullOrEmpty(GameDir)) throw new ModKitException("the game folder is not set (it is where the loose files go)");
            return new LooseTarget(GameDir);
        }

        // the roots whose archives go to the workspace when the client is served by loose files
        List<string> OtherRoots()
        {
            var roots = new List<string>();
            foreach (string r in Data.Roots) if (r != LooseTarget.Root) roots.Add(r);
            return roots;
        }

        static bool HasOtherRoot(Plan p)
        {
            foreach (PlanArchive a in p.Archives) if (!LooseTarget.Serves(a.Archive)) return true;
            return false;
        }

        public Plan Plan() { return Plan(null); }

        public Plan Plan(Action<ProgressInfo> progress)
        {
            Plan p = Planner.Build(Data, ActiveMods(), new PlanOptions { Progress = progress, Maps = Maps, NoSync = NoSync });
            if (Target == ApplyTarget.LooseFiles)
            {
                Loose().Check(p);
                if (WorkspaceDir == null && HasOtherRoot(p))
                    p.Msg(p.Warnings, null, null, null, "the workspace folder is not set: the archives of the server and the global client would not be written");
            }
            foreach (string line in Server.Describe(p, WorkspaceDir)) p.Msg(p.Notes, null, null, null, line);
            return p;
        }

        public ApplyResult Apply() { return Apply(null); }

        /// <summary>
        /// Saves the mod being edited (if it holds anything) and writes what the enabled mods change.
        /// Target Workspace: patched copies of every touched archive go into the workspace.
        /// Target LooseFiles: the client's entries go into the game folder as plain files, the archives of the
        /// other roots into the workspace. What the other target left behind from an earlier apply is removed.
        /// </summary>
        public ApplyResult Apply(Action<ProgressInfo> progress)
        {
            ApplyResult r = ApplyOnly(progress);
            if (r.Applied) ServerFollowUp(r);
            return r;
        }

        // the server's archives go to the workspace under <workspace>/<root>/
        void ServerFollowUp(ApplyResult r)
        {
            if (!Server.Enabled) return;
            bool changed = false;
            foreach (ApplyOutput o in r.Outputs) if (!LooseTarget.Serves(o.Archive) && DataSet.RootOf(DataSet.NormalizeRef(o.Archive)) != Profile.GmsClient.Root) changed = true;
            if (WorkspaceDir != null)
                foreach (string root in OtherRoots())
                {
                    if (root == Profile.GmsClient.Root) continue;
                    string under = Path.Combine(WorkspaceDir, root) + Path.DirectorySeparatorChar;
                    foreach (string f in r.Removed) if (f.StartsWith(under, StringComparison.OrdinalIgnoreCase)) changed = true;
                }
            r.Server = Server.Run(Data, WorkspaceDir, changed);
        }

        ApplyResult ApplyOnly(Action<ProgressInfo> progress)
        {
            if (ModsDir == null) throw new ModKitException("the mods folder is not set");
            if (Target == ApplyTarget.Workspace && WorkspaceDir == null) throw new ModKitException("the workspace folder is not set");
            LooseTarget loose = Target == ApplyTarget.LooseFiles ? Loose() : null;
            if (Mod.Ops.Count > 0) SaveMod();
            List<Mod> mods = ActiveMods();
            if (mods.Count == 0) throw new ModKitException("no enabled mod in " + ModsDir + " and no pending change; there is nothing to apply");
            var options = new ApplyOptions { Progress = progress, Maps = Maps, NoSync = NoSync };

            if (loose == null)
            {
                ApplyResult r = new Workspace(WorkspaceDir).Apply(Data, mods, options);
                if (r.Applied && !string.IsNullOrEmpty(GameDir) && Directory.Exists(Path.Combine(GameDir, "Data")))
                {
                    var old = new LooseTarget(GameDir);
                    if (old.ReadState() != null) Merge(r, old.Revert());
                }
                return r;
            }

            Plan plan = Planner.Build(Data, mods, new PlanOptions { Progress = progress, Maps = Maps, NoSync = NoSync });
            ApplyResult res = loose.Apply(mods, plan, options);
            if (!res.Applied) return res;
            if (WorkspaceDir != null)
            {
                var ws = new Workspace(WorkspaceDir);
                if (HasOtherRoot(plan) || ws.ReadState() != null)
                {
                    options.Roots = OtherRoots();
                    Merge(res, ws.Apply(Data, mods, plan, options));
                }
            }
            else if (HasOtherRoot(plan))
                plan.Msg(plan.Warnings, null, null, null, "the workspace folder is not set: the archives of the server and the global client were not written");
            return res;
        }

        static void Merge(ApplyResult into, ApplyResult from)
        {
            into.Outputs.AddRange(from.Outputs);
            into.Removed.AddRange(from.Removed);
            into.Written.AddRange(from.Written);
            into.Left.AddRange(from.Left);
        }

        /// <summary>Removes what an apply wrote: the loose files of the record in the game folder and every patched copy in the workspace. The mods folder is left as it is.</summary>
        public ApplyResult Revert()
        {
            var res = new ApplyResult { Applied = true };
            bool any = false;
            if (!string.IsNullOrEmpty(GameDir) && Directory.Exists(Path.Combine(GameDir, "Data")))
            {
                var loose = new LooseTarget(GameDir);
                if (loose.ReadState() != null) { Merge(res, loose.Revert()); any = true; }
            }
            if (WorkspaceDir != null)
            {
                var ws = new Workspace(WorkspaceDir);
                if (ws.ReadState() != null) { Merge(res, ws.Revert(Data, null, null)); any = true; }
            }
            if (!any)
                throw new ModKitException("nothing is applied: no " + Workspace.StateFile + " in the workspace" + (string.IsNullOrEmpty(GameDir) ? "" : " and no " + LooseTarget.StateFile + " in the game folder"));
            ServerFollowUp(res);
            return res;
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

        /// <summary>The targets of the session's data, one line per target with what the plan does there, then Report(p).</summary>
        public string Describe(Plan p)
        {
            var sb = new StringBuilder(Targets.Line(Data)).Append("\r\n");
            foreach (string l in Targets.PlanLines(p, Data)) sb.Append("  ").Append(l).Append("\r\n");
            return sb.Append(Report(p)).ToString();
        }

        /// <summary>The targets of the session's data, what the apply or revert wrote or removed on each, then the plan and the server step.</summary>
        public string Describe(ApplyResult r)
        {
            var sb = new StringBuilder(Targets.Line(Data)).Append("\r\n");
            if (r.Plan != null) foreach (string l in Targets.PlanLines(r.Plan, Data)) sb.Append("  plan, ").Append(l).Append("\r\n");
            if (!r.Applied) return sb.Append(Report(r)).ToString();
            foreach (string l in Targets.ResultLines(r, Data, WorkspaceDir)) sb.Append("  ").Append(l).Append("\r\n");
            if (r.Plan != null) sb.Append(Report(r.Plan));
            sb.Append(ServerStep.Report(r.Server));
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
            foreach (string f in r.Written) sb.Append("wrote ").Append(f).Append("\r\n");
            foreach (string f in r.Removed) sb.Append("removed ").Append(f).Append("\r\n");
            foreach (string f in r.Left) sb.Append("left alone, changed since it was written: ").Append(f).Append("\r\n");
            if (r.Outputs.Count == 0 && r.Removed.Count == 0 && r.Written.Count == 0 && r.Left.Count == 0) sb.Append("nothing to write or remove\r\n");
            sb.Append(ServerStep.Report(r.Server));
            return sb.ToString();
        }
    }
}
