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
            savedPrint = null;
            return Mod;
        }

        /// <summary>Makes a mod that is in memory already the one being edited (a front end keeps its mod when its folders change).</summary>
        public void Adopt(Mod mod)
        {
            if (mod == null) throw new ArgumentNullException("mod");
            Mod = mod;
            savedPrint = mod.Dir != null ? mod.XmlPrint() : null;
        }

        /// <summary>Loads a mod: a path to its folder or mod.xml, or the name of a folder under the mods folder.</summary>
        public Mod LoadMod(string pathOrName)
        {
            string p = pathOrName;
            if (!Directory.Exists(p) && !File.Exists(p) && ModsDir != null) p = Path.Combine(ModsDir, pathOrName);
            Mod = Mod.Load(p);
            savedPrint = Mod.XmlPrint();
            return Mod;
        }

        /// <summary>Saves the mod as SaveMod() does, but first checks the id a front end shows in its id box: a typed
        /// id that is not this mod's is refused (New mod starts a mod under a new id; Save never renames).</summary>
        public string SaveMod(string idBox)
        {
            string typed = (idBox ?? "").Trim();
            if (typed != Mod.Id)
                throw new ModKitException("the id box differs from this mod's id " + Mod.Id + "; use New mod to save under a new id");
            return SaveMod();
        }

        /// <summary>Saves the mod to the folder under the mods folder it was loaded from (else &lt;mods
        /// folder&gt;/&lt;id&gt;) as mod.xml and returns the folder.</summary>
        public string SaveMod()
        {
            if (ModsDir == null) throw new ModKitException("the mods folder is not set");
            // a mod emptied after it was saved (everything taken out of it) is saved empty over its own folder
            if (Mod.IsEmpty && !SavedHere()) throw new ModKitException("mod " + Mod.Id + " holds no change yet; there is nothing to save");
            string dir = SaveDir();
            if ((Mod.Dir == null || !SamePath(Mod.Dir, dir)) && File.Exists(Path.Combine(dir, "mod.xml")))
                throw new ModKitException(dir + " already holds a mod; give this one another id");
            // an empty mod as it was loaded is not written: the save would take away the files under its files/
            // folder that no op names (put there by hand, say)
            if (Mod.IsEmpty && !EmptiedHere()) return dir;
            Mod.Save(dir);
            savedPrint = Mod.XmlPrint();
            return dir;
        }

        // a print of the mod's text (Mod.XmlPrint) when it was loaded, adopted or saved; null for a new mod
        string savedPrint;

        /// <summary>The mod being edited was loaded from (or saved to) its folder under the mods folder and has been
        /// changed since: an empty one was emptied in this session.</summary>
        public bool EmptiedHere() { return SavedHere() && Mod.XmlPrint() != savedPrint; }

        /// <summary>The mod being edited was loaded from, or saved to, its folder under the mods folder.</summary>
        public bool SavedHere()
        {
            return ModsDir != null && Mod.Dir != null && SamePath(Mod.Dir, SaveDir());
        }

        // where Save writes: the folder the mod was loaded from or saved to, when it sits right under the mods folder
        // and its mod.xml has this id (the folder's name may differ from the id: a save there keeps the folder, so
        // Apply and the mod list read the same mod.xml); else <mods folder>/<id> (a new mod, or one given a new id)
        string SaveDir()
        {
            string byId = ModFolder.DirOf(ModsDir, Mod.Id);
            if (Mod.Dir == null || SamePath(Mod.Dir, byId)) return byId;
            string parent = Path.GetDirectoryName(Path.GetFullPath(Mod.Dir).TrimEnd('/', '\\'));
            if (parent == null || !SamePath(parent, ModsDir) || !File.Exists(Path.Combine(Mod.Dir, "mod.xml"))) return byId;
            try { return Mod.LoadHead(Mod.Dir).Id == Mod.Id ? Mod.Dir : byId; }
            catch (ModKitException) { return byId; }
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

        /// <summary>The npc editor over the data and the mod being edited (see Npcs.cs). Ask again after the mod is replaced.</summary>
        public NpcEditor Npcs()
        {
            var e = new NpcEditor(Data, Mod);
            if (Maps != null) e.Maps = Maps;
            return e;
        }

        /// <summary>The quest editor over the data and the mod being edited (see Quests.cs). Ask again after the mod is replaced.</summary>
        public QuestEditor Quests()
        {
            var ed = new QuestEditor(Data, Mod);
            // the ids the other enabled mods reserve are not offered for a new quest; read when an id is asked for
            ed.Others = delegate { return OtherMods(); };
            return ed;
        }

        /// <summary>
        /// What the last look at the other mods could not read, one line per mod folder: the ids such a mod
        /// reserves are not known, so a new id may clash with it (the plan would say so).
        /// </summary>
        public readonly List<string> ModWarnings = new List<string>();

        /// <summary>
        /// The other enabled mods of the mods folder, each read on its own: one that cannot be read is left out and
        /// named in ModWarnings, the others still count.
        /// </summary>
        public List<Mod> OtherMods()
        {
            ModWarnings.Clear();
            var others = new List<Mod>();
            if (ModsDir == null || !Directory.Exists(ModsDir)) return others;
            ModList list;
            try { list = ModList(); }
            catch (ModKitException ex) { ModWarnings.Add("the mod list cannot be read, so the ids other mods reserve are not known: " + ex.Message); return others; }
            foreach (ModListItem it in list.Items)
            {
                if (!it.Enabled) continue;
                if (it.Error == null && Mod != null && it.Id == Mod.Id) continue;
                string error = it.Error;
                if (error == null)
                    try { others.Add(Mod.Load(Path.Combine(list.Dir, it.Folder))); }
                    catch (ModKitException ex) { error = ex.Message; }
                if (error != null) ModWarnings.Add("mod folder " + it.Folder + " cannot be read, so the ids it reserves are not known: " + error);
            }
            return others;
        }

        /// <summary>The skill and effect editor over the data and the mod being edited (see SkillEditor.cs). Ask again after the mod is replaced.</summary>
        public SkillEditor Skills() { return new SkillEditor(Data, Mod); }

        /// <summary>The shop and drop box editor over the data and the mod being edited (see GroupEditor.cs). Ask again after the mod is replaced.</summary>
        public GroupEditor Groups()
        {
            var e = new GroupEditor(Data, Mod);
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
        public RefList Refs(string entity, long id, int maxPerRule) { return Refs(entity, id, maxPerRule, null); }

        /// <summary>As Refs, in the data as the plan leaves it (plan null: as shipped; see Plan()).</summary>
        public RefList Refs(string entity, long id, int maxPerRule, Plan plan)
        {
            EntityMap em = (Maps ?? FieldMaps.Default).Entity(entity);
            if (em == null) throw new ModKitException("no field map for \"" + entity + "\"");
            return ServerRefs.Find(Data, em, id, maxPerRule, plan);
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

        /// <summary>true: an apply goes on although mods conflict, and the mod of the higher priority wins.</summary>
        public bool AllowConflicts;

        /// <summary>The mods folder as a list with order and enabled states.</summary>
        public ModList ModList() { return MS2.ModKit.ModList.Load(ModsDir); }

        /// <summary>The mods that a plan or an apply uses: the enabled mods in rising priority (the first of the list last), with the mod being edited as it is in memory.</summary>
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

        /// <summary>
        /// The schema cache folder ("modkit schema build" writes it). Null or "" = no schema check. When set, Plan and
        /// Apply check new values the way the command line does: a warning for an attribute or element the shipped
        /// table never has, a note where the notes file says the client reads it.
        /// </summary>
        public string SchemaDir;
        /// <summary>The notes file read with the schema cache (schema-notes.xml); null or missing = no notes.</summary>
        public string SchemaNotes;

        SchemaSet Schemas() { return string.IsNullOrEmpty(SchemaDir) ? null : SchemaSet.Open(SchemaDir, Data, SchemaNotes); }

        PlanOptions PlanOpt(Action<ProgressInfo> progress) { return new PlanOptions { Schemas = Schemas(), Progress = progress, Maps = Maps, NoSync = NoSync }; }

        public Plan Plan() { return Plan(null); }

        public Plan Plan(Action<ProgressInfo> progress)
        {
            Plan p = Planner.Build(Data, ActiveMods(), PlanOpt(progress));
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
        /// Saves the mod being edited (if it holds anything, or if it was emptied after it was saved) and writes what
        /// the enabled mods change. With no mod left, an earlier apply is undone: the copies and loose files go.
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
            // an empty mod is saved only when it was emptied here: one loaded empty is left as it is on disk
            if (!Mod.IsEmpty || EmptiedHere()) SaveMod();
            List<Mod> mods = ActiveMods();
            // no mod takes part: an earlier apply is undone by applying nothing (as the plan shows), else there is
            // nothing to do
            if (mods.Count == 0 && !AppliedBefore(loose))
                throw new ModKitException(Mod.IsEmpty && SavedHere()
                    ? "mod " + Mod.Id + " is empty and no other mod is enabled in " + ModsDir + "; there is nothing to apply"
                    : "no enabled mod in " + ModsDir + " and no pending change; there is nothing to apply");
            var options = new ApplyOptions { Schemas = Schemas(), Progress = progress, Maps = Maps, NoSync = NoSync, AllowConflicts = AllowConflicts };
            ApplyResult done = ApplyMods(loose, mods, options, progress);
            done.Undone = done.Applied && mods.Count == 0;
            return done;
        }

        ApplyResult ApplyMods(LooseTarget loose, List<Mod> mods, ApplyOptions options, Action<ProgressInfo> progress)
        {

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

            Plan plan = Planner.Build(Data, mods, PlanOpt(progress));
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

        // an earlier apply that left something to take away (an apply of no mod leaves an empty state behind)
        bool AppliedBefore(LooseTarget loose)
        {
            if (WorkspaceDir != null && Holds(new Workspace(WorkspaceDir).ReadState())) return true;
            if (loose != null) return Holds(loose.ReadState());
            return !string.IsNullOrEmpty(GameDir) && Directory.Exists(Path.Combine(GameDir, "Data")) && Holds(new LooseTarget(GameDir).ReadState());
        }

        static bool Holds(WorkspaceState st) { return st != null && (st.Mods.Count > 0 || st.Archives.Count > 0); }
        static bool Holds(LooseState st) { return st != null && (st.Mods.Count > 0 || st.Files.Count > 0 || st.Dirs.Count > 0); }

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

        public static string Report(Plan p) { return Report(p, true); }

        // the priority line and one line per conflict
        static void Conflicts(StringBuilder sb, Plan p)
        {
            if (p.Conflicts.Count > 0 && p.Mods.Count > 1) sb.Append(ModOrder.Lead).Append(ModOrder.Text(p.Mods)).Append("\r\n");
            foreach (string l in p.LeftOut()) sb.Append(l).Append("\r\n");
            foreach (PlanMessage m in p.Conflicts) sb.Append(m.ConflictLine()).Append("\r\n");
        }

        static void Totals(StringBuilder sb, Plan p)
        {
            sb.Append(p.Mods.Count).Append(" mod(s), ").Append(p.Changes.Count).Append(" change(s), ")
              .Append(p.Errors.Count).Append(" error(s), ").Append(p.Conflicts.Count).Append(" conflict(s), ")
              .Append(p.Warnings.Count).Append(" warning(s)\r\n");
        }

        static void Messages(StringBuilder sb, Plan p)
        {
            foreach (PlanMessage m in p.Errors) sb.Append("error: ").Append(m).Append("\r\n");
            foreach (PlanMessage m in p.Warnings) sb.Append("warning: ").Append(m).Append("\r\n");
        }

        static void Rest(StringBuilder sb, Plan p)
        {
            foreach (PlanMessage m in p.Notes) sb.Append("note: ").Append(m).Append("\r\n");
            foreach (PlanChange c in p.Changes) sb.Append("  ").Append(c).Append("\r\n");
        }

        /// <summary>
        /// The most a view's text of a plan or apply holds (chars). A mod with 200k changes would make a text of
        /// hundreds of MB, more than the 32-bit host can hold in one string: past this the lines are counted, not shown.
        /// </summary>
        public const int ViewChars = 200000;

        // the messages, notes and changes while they fit in ViewChars, then how many lines are left out
        static void Capped(StringBuilder sb, Plan p, bool messages, bool rest)
        {
            int limit = sb.Length + ViewChars, total = 0, shown = 0;
            var line = new StringBuilder();
            Action<string, object> add = delegate(string lead, object o)
            {
                total++;
                if (shown < total - 1) return;
                line.Length = 0;
                line.Append(lead).Append(o).Append("\r\n");
                if (sb.Length + line.Length > limit) return;
                sb.Append(line);
                shown++;
            };
            if (messages)
            {
                foreach (PlanMessage m in p.Errors) add("error: ", m);
                foreach (PlanMessage m in p.Warnings) add("warning: ", m);
            }
            if (rest)
            {
                foreach (PlanMessage m in p.Notes) add("note: ", m);
                foreach (PlanChange c in p.Changes) add("  ", c);
            }
            if (shown < total)
                sb.Append("... ").Append(N(total - shown)).Append(" more lines of ").Append(N(total))
                  .Append(" not shown here; modkit validate or diff lists them all\r\n");
        }

        static string N(int n) { return n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture); }

        static string Report(Plan p, bool conflicts)
        {
            var sb = new StringBuilder();
            Totals(sb, p);
            if (conflicts) Conflicts(sb, p);
            Messages(sb, p);
            Rest(sb, p);
            return sb.ToString();
        }

        /// <summary>
        /// What a view shows for a plan, with what must be read at the top, since the output box of a view is two
        /// lines high: the conflicts with their winners, the counts, the errors and the warnings. Then the targets of
        /// the session's data with what the plan does there, then the notes and the changes.
        /// </summary>
        public string Describe(Plan p)
        {
            var sb = new StringBuilder();
            Conflicts(sb, p);
            Totals(sb, p);
            Capped(sb, p, true, false);
            sb.Append(Targets.Line(Data)).Append("\r\n");
            foreach (string l in Targets.PlanLines(p, Data)) sb.Append("  ").Append(l).Append("\r\n");
            Capped(sb, p, false, true);
            return sb.ToString();
        }

        /// <summary>The targets of the session's data, what the apply or revert wrote or removed on each, then the plan and the server step. A refused apply: the reason, then the plan.</summary>
        public string Describe(ApplyResult r)
        {
            // a refused apply says so first, then why: the plan as a view shows it
            if (!r.Applied) return "nothing written: " + r.Reason + "\r\n" + (r.Plan != null ? Describe(r.Plan) : "");
            var sb = new StringBuilder();
            if (r.Undone) sb.Append(UndoneLine(r)).Append("\r\n");
            sb.Append(Targets.Line(Data)).Append("\r\n");
            if (r.Plan != null) foreach (string l in Targets.PlanLines(r.Plan, Data)) sb.Append("  plan, ").Append(l).Append("\r\n");
            foreach (string l in Targets.ResultLines(r, Data, WorkspaceDir)) sb.Append("  ").Append(l).Append("\r\n");
            if (r.Plan != null) { Totals(sb, r.Plan); Conflicts(sb, r.Plan); Capped(sb, r.Plan, true, true); }
            sb.Append(ServerStep.Report(r.Server));
            return sb.ToString();
        }

        static string UndoneLine(ApplyResult r)
        {
            return "no mod is enabled: this apply took away what the earlier apply wrote (" + r.Removed.Count + " file(s) removed)";
        }

        public static string Report(ApplyResult r)
        {
            var sb = new StringBuilder();
            if (r.Plan != null) sb.Append(Report(r.Plan));
            if (!r.Applied) return sb.Append("nothing written: ").Append(r.Reason).Append("\r\n").ToString();
            if (r.Undone) sb.Append(UndoneLine(r)).Append("\r\n");
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
