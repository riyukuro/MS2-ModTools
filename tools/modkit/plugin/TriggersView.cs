// The "MS2 Triggers" view: the trigger scripts of a map. A map search (id or name, the maps of the Pick dialog),
// the list of hits, the scripts of the chosen map (one folder per map file, trigger/<xblock>/), and the chosen
// script as a tree: its states, each with its on enter actions, its conditions (their actions and the states they
// go to) and its on exit actions. The output box is the MS2 Tables view's.
//
// The script is linted as it is shown (MS2.ModKit.TriggerLint, the port of tools/trigger/lint.py with its schema and
// known issues): a node with a problem is coloured and names it, and the list under the tree has every problem of the
// script, with Go to.
//
// Edits (MS2.ModKit.TriggerEditor): set or remove an attribute of the chosen node, add an action, condition,
// transition, state or on enter / on exit block, remove a node, add a new script to the map's folder, revert a
// script. Each edit makes the script a whole-file entry of the mod being edited; Plan and Apply are the MS2 Tables
// view's, and the plan lints every trigger entry of a mod (an error blocks Apply unless the mod's "lint errors
// only warn" is on).
//
// The view holds no data logic: MS2.ModKit.Triggers reads the scripts. The session, the worker thread and the
// output stay in the MS2 Tables view, as for the other entity views.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Emergent.Toolbench;
using Emergent.Toolbench.UI.Workbench;
using MS2.ModKit;

namespace MS2.Tables
{
    public class TriggersView : AbstractView
    {
        public const string ViewId = "MS2.Tables.TriggersView";
        const int MaxHits = 500;

        TablesView host;
        IdLookup lookup;
        EditorSession lookupSession;
        string archive;
        long mapId;
        TriggerScript script;
        bool filling;

        readonly TextBox searchBox = new TextBox { Width = 200 };
        readonly Label hitsLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListBox mapList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        readonly ListBox scriptList = new ListBox { Dock = DockStyle.Bottom, Height = 200, IntegralHeight = false };
        readonly Label scriptsLabel = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = 20, TextAlign = ContentAlignment.BottomLeft };
        readonly Label scriptLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 8, 0) };
        readonly TreeView tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 130 };
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();
        readonly List<long> hitIds = new List<long>();
        readonly List<string> scriptEntries = new List<string>();
        readonly ListView findingList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        readonly Label findingsLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 8, 0) };
        readonly Dictionary<TriggerNode, TreeNode> treeNodes = new Dictionary<TriggerNode, TreeNode>();
        List<TriggerFinding> findings = new List<TriggerFinding>();
        // the importers of the packs are found once, as a job of the Tables view; until then a script is linted without them
        TriggerPacks importersJob;
        // the objects of the map files (Resource/Exported.m2d beside the trigger archive), read once per data set by a
        // job, through the index cache in the workspace's cache/; until then a script is linted without them
        DataSet objectsData;
        TriggerXBlocks objects;
        string objectsNote;
        readonly CheckBox strictBox = new CheckBox { Text = "strict", AutoSize = true, Margin = new Padding(8, 6, 3, 0) };
        List<TriggerFolder> folders = new List<TriggerFolder>();

        readonly ComboBox attrBox = new ComboBox { Width = 120 };
        readonly TextBox valueBox = new TextBox { Width = 130 };
        readonly ComboBox kindBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        readonly TextBox nameBox = new TextBox { Width = 120 };
        readonly TextBox goToBox = new TextBox { Width = 90 };
        readonly TextBox newScriptBox = new TextBox { Width = 120 };
        readonly CheckBox lintWarnBox = new CheckBox { Text = "lint errors only warn", AutoSize = true, Margin = new Padding(8, 6, 3, 0) };
        static readonly TriggerKind[] Kinds = { TriggerKind.Action, TriggerKind.Condition, TriggerKind.Transition, TriggerKind.State, TriggerKind.OnEnter, TriggerKind.OnExit };
        static readonly string[] KindNames = { "action", "condition", "transition", "state", "on enter", "on exit" };

        public TriggersView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(Lbl("Map id or name"));
            top.Controls.Add(searchBox);
            top.Controls.Add(Btn("Find", "Lists the maps whose id starts with the text or whose name has it", delegate { Find(); }));
            top.Controls.Add(hitsLabel);

            FlowLayoutPanel scriptBar = Bar();
            scriptBar.Controls.Add(scriptLabel);
            scriptBar.Controls.Add(Btn("Expand all", "Opens every state of the script", delegate { tree.ExpandAll(); if (tree.Nodes.Count > 0) tree.Nodes[0].EnsureVisible(); }));
            scriptBar.Controls.Add(Btn("Collapse", "Shows the states only", delegate { CollapseToStates(); }));
            scriptBar.Controls.Add(Btn("Revert script", "Takes the script out of the mod: every edit of it is gone (a script the mod added is gone too)", delegate { Revert(); }));
            FlowLayoutPanel modBar = Bar();
            modBar.Controls.Add(Lbl("New script"));
            modBar.Controls.Add(newScriptBox);
            tips.SetToolTip(newScriptBox, "The file name of a new script in the folder of the chosen script (trigger/<xblock>/<name>.xml); it starts with one state, Start");
            modBar.Controls.Add(Btn("Add script", "Adds a new script to the map's trigger folder, in the mod", delegate { NewScript(); }));
            modBar.Controls.Add(lintWarnBox);
            tips.SetToolTip(lintWarnBox, "For the mod being edited: a lint error of one of its trigger scripts is a plan warning instead of an error, and Apply goes on (trigger-lint=\"warn\" in mod.xml)");
            modBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change, with the lint of the mod's trigger scripts; writes nothing", delegate { return host.DoPlan(); }));
            modBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change (the MS2 Tables view's Apply); a lint error of a trigger script stops it", delegate { return host.DoApply(); }));
            modBar.Controls.Add(Btn("Save mod", "Saves the mod being edited, the trigger files beside mod.xml (the same Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            FlowLayoutPanel editBar = Bar();
            editBar.Controls.Add(Lbl("Attribute"));
            editBar.Controls.Add(attrBox);
            tips.SetToolTip(attrBox, "An attribute of the chosen node (pick one or type a new name)");
            editBar.Controls.Add(valueBox);
            tips.SetToolTip(valueBox, "The value; empty removes the attribute");
            editBar.Controls.Add(Btn("Set", "Sets the attribute of the chosen node (an empty value removes it)", delegate { SetAttr(); }));
            editBar.Controls.Add(Lbl("Add"));
            for (int i = 0; i < KindNames.Length; i++) kindBox.Items.Add(KindNames[i]);
            kindBox.SelectedIndex = 0;
            editBar.Controls.Add(kindBox);
            editBar.Controls.Add(nameBox);
            tips.SetToolTip(nameBox, "The name of the new action or condition, the state of a new transition, the name of a new state");
            editBar.Controls.Add(Lbl("goes to"));
            editBar.Controls.Add(goToBox);
            tips.SetToolTip(goToBox, "A new condition: the state it goes to (empty: none)");
            editBar.Controls.Add(Btn("Add", "Adds under the chosen node (or the nearest one above it that can hold it); a state goes after the last state", delegate { Add(); }));
            editBar.Controls.Add(Btn("Remove", "Removes the chosen node and all under it", delegate { Remove(); }));

            var left = new Panel { Dock = DockStyle.Left, Width = 300 };
            // docked from the last child to the first: the script list at the bottom, its label above it, the splitter, the maps
            left.Controls.Add(mapList);
            left.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            left.Controls.Add(scriptsLabel);
            left.Controls.Add(scriptList);
            tips.SetToolTip(scriptList, "The scripts of the map's trigger folders (trigger/<xblock>/), one folder per map file of the map");

            findingList.Columns.Add("level", 70);
            findingList.Columns.Add("line", 50);
            findingList.Columns.Add("code", 130);
            findingList.Columns.Add("problem", 600);
            findingList.Resize += delegate { FitFindings(); };
            findingList.DoubleClick += delegate { Guard(delegate { GoToFinding(); }); };
            findingList.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                Guard(delegate { GoToFinding(); });
            };
            FlowLayoutPanel findingBar = Bar();
            findingBar.Controls.Add(Btn("Go to", "Selects the node of the chosen problem in the tree", delegate { GoToFinding(); }));
            findingBar.Controls.Add(strictBox);
            tips.SetToolTip(strictBox, "What the Maple2 server (WIP) does not run correctly yet is an error, not a warning (lint.py --strict)");
            strictBox.CheckedChanged += delegate { if (!filling && script != null) Guard(delegate { Relint(); }); };
            findingBar.Controls.Add(findingsLabel);
            var findingPanel = new Panel { Dock = DockStyle.Bottom, Height = 170 };
            findingPanel.Controls.Add(findingList);
            findingPanel.Controls.Add(findingBar);

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(tree);
            right.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            right.Controls.Add(findingPanel);
            right.Controls.Add(editBar);
            right.Controls.Add(modBar);
            right.Controls.Add(scriptBar);

            Controls.Add(right);
            Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
            Controls.Add(left);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(outputBox);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, left, right });
            ViewLayout.KeepLogLines(outputBox, this);
            ViewLayout.KeepHeightShare(scriptList, left, 0.45, 4 * scriptList.ItemHeight + 4);
            ViewLayout.KeepHeightShare(findingPanel, right, 0.3, 120);
            ViewLayout.CentreOnFirstShow(this);

            searchBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                Guard(delegate { Find(); });
            };
            mapList.SelectedIndexChanged += delegate { if (!filling) Guard(delegate { OpenMap(); }); };
            scriptList.SelectedIndexChanged += delegate { if (!filling) Guard(delegate { OpenScript(); }); };
            tree.AfterSelect += delegate { FillAttrs(); };
            attrBox.SelectedIndexChanged += delegate { ShowValue(); };
            attrBox.Leave += delegate { ShowValue(); };
            lintWarnBox.CheckedChanged += delegate { if (!filling) Guard(delegate { SetLintWarn(); }); };
            valueBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                Guard(delegate { SetAttr(); });
            };
        }

        public override void Initialize()
        {
            base.Initialize();
            Guard(delegate { Find(); });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Hook(null);
            base.Dispose(disposing);
        }

        // ---- small builders ----

        static FlowLayoutPanel Bar()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(2) };
        }

        static Label Lbl(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(6, 8, 0, 0) }; }

        Button Btn(string text, string tip, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, UseVisualStyleBackColor = true, ForeColor = SystemColors.ControlText };
            b.Click += delegate(object sender, EventArgs e) { Guard(delegate { click(sender, e); }); };
            tips.SetToolTip(b, tip);
            return b;
        }

        Button Job(string text, string tip, Func<bool> start)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, UseVisualStyleBackColor = true, ForeColor = SystemColors.ControlText };
            b.Click += delegate { if (Guard(delegate { host.NeedHandle(); })) start(); };
            tips.SetToolTip(b, tip);
            return b;
        }

        protected override void OnEnter(EventArgs e)
        {
            TablesView.ItemsInUse = true;
            base.OnEnter(e);
        }

        // ---- the MS2 Tables view: session, worker thread, output ----

        void Hook(TablesView h)
        {
            if (h == host) return;
            if (host != null)
            {
                host.Changed -= OnHostChanged;
                host.BusyChanged -= OnHostBusy;
                host.Said -= OnHostSaid;
            }
            host = h;
            lookup = null;
            if (h == null) return;
            h.Changed += OnHostChanged;
            h.BusyChanged += OnHostBusy;
            h.Said += OnHostSaid;
            OutputWrap.Set(outputBox, h.OutputText);
            OnHostBusy(h.Busy);
        }

        bool Guard(MethodInvoker body)
        {
            try
            {
                TablesView h = TablesView.Open;
                if (h == null || h.IsDisposed)
                {
                    h = TablesView.OpenBehind();
                }
                if (h == null) { OutputWrap.Set(outputBox, "the MS2 Tables view could not be opened; this view works on the data of that view"); return false; }
                Hook(h);
            }
            catch (Exception ex)
            {
                OutputWrap.Set(outputBox, "the MS2 Tables view could not be opened: " + ex.Message);
                Logger.Warning("MS2 Triggers: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        void OnHostSaid(string text) { OutputWrap.Set(outputBox, text); }

        void OnHostBusy(bool on) { ViewLayout.Lock(this, lockable, on, outputBox); }

        void OnHostChanged()
        {
            lookup = null;
            string keep = script != null ? script.Entry : null;
            try
            {
                FillMaps(mapId);
                if (mapId != 0) ShowMap(mapId, keep);
            }
            catch (ModKitException ex) { ShowScript(null); host.SayOutside(ex.Message); }
            catch (System.IO.IOException ex) { ShowScript(null); host.SayOutside(ex.Message); }
            catch (System.IO.InvalidDataException ex) { ShowScript(null); host.SayOutside(ex.Message); }
            // a mod loaded or made: its "lint errors only warn" choice shows at once, with or without a script
            RefreshWarnBox();
        }

        void RefreshWarnBox()
        {
            filling = true;
            try { lintWarnBox.Checked = host != null && Session().Mod.TriggerLintWarn; }
            catch (ModKitException) { lintWarnBox.Checked = false; }
            finally { filling = false; }
        }

        EditorSession Session() { return host.SharedSession(); }

        IdLookup Lookup()
        {
            EditorSession s = Session();
            if (lookup == null || lookupSession != s || lookup.Mod != s.Mod) { lookup = new IdLookup(s); lookupSession = s; }
            return lookup;
        }

        // ---- maps ----

        void Find()
        {
            FillMaps(mapId);
            if (mapList.Items.Count > 0 && mapList.SelectedIndex < 0) mapList.SelectedIndex = 0;
        }

        void FillMaps(long keep)
        {
            List<EntityHit> hits = Lookup().Find(IdLookup.Map, searchBox.Text.Trim(), MaxHits);
            filling = true;
            try
            {
                mapList.BeginUpdate();
                mapList.Items.Clear();
                hitIds.Clear();
                int sel = -1;
                foreach (EntityHit h in hits)
                {
                    if (h.Id == keep) sel = hitIds.Count;
                    hitIds.Add(h.Id);
                    mapList.Items.Add(h.ToString());
                }
                mapList.SelectedIndex = sel;
                mapList.EndUpdate();
            }
            finally { filling = false; }
            hitsLabel.Text = hits.Count >= MaxHits ? MaxHits + "+ maps" : hits.Count + " map(s)";
        }

        void OpenMap()
        {
            int i = mapList.SelectedIndex;
            if (i < 0 || i >= hitIds.Count) return;
            ShowMap(hitIds[i], null);
            host.SayOutside(DescribeMap());
        }

        void ShowMap(long id, string keepEntry)
        {
            mapId = id;
            archive = Triggers.ArchiveOf(Session().Data);
            folders = archive == null ? new List<TriggerFolder>() : Triggers.FoldersOf(Session().Data, archive, id);
            TriggerEditor ed = archive == null ? null : Editor();
            if (ed != null) foreach (TriggerFolder f in folders) f.Scripts.AddRange(ed.Added(archive, f.XBlock));
            filling = true;
            int sel = -1;
            try
            {
                scriptList.BeginUpdate();
                scriptList.Items.Clear();
                scriptEntries.Clear();
                foreach (TriggerFolder f in folders)
                    foreach (string e in f.Scripts)
                    {
                        if (e == keepEntry) sel = scriptEntries.Count;
                        scriptEntries.Add(e);
                        string shown = folders.Count == 1 ? e.Substring(e.LastIndexOf('/') + 1) : e.Substring(Triggers.Folder.Length);
                        scriptList.Items.Add((ed != null && ed.IsModded(archive, e) ? "* " : "") + shown);
                    }
                scriptList.EndUpdate();
            }
            finally { filling = false; }
            int n = 0;
            foreach (TriggerFolder f in folders) n += f.Scripts.Count;
            var names = new List<string>();
            foreach (TriggerFolder f in folders) names.Add(f.XBlock);
            scriptsLabel.Text = folders.Count == 0 ? "no map file" : n + " script(s) in " + string.Join(", ", names.ToArray()) + " (* = in the mod)";
            if (sel < 0 && scriptEntries.Count > 0) sel = 0;
            if (sel >= 0) scriptList.SelectedIndex = sel;   // shows it
            else ShowScript(null);
        }

        string DescribeMap()
        {
            if (archive == null) return "no archive of the data set has trigger scripts";
            return "map " + mapId.ToString(CultureInfo.InvariantCulture) + ": " + scriptEntries.Count + " script(s) in " + archive;
        }

        // ---- the script ----

        void OpenScript()
        {
            int i = scriptList.SelectedIndex;
            if (i < 0 || i >= scriptEntries.Count) { ShowScript(null); return; }
            ShowScript(Editor().Current(archive, scriptEntries[i]));
        }

        TriggerEditor Editor() { return new TriggerEditor(Session().Data, Session().Mod); }

        /// <summary>The shipped trigger entries of the archive, kept in the data set (a mod edit does not change them).</summary>
        TriggerPacks Packs()
        {
            return archive == null ? null : TriggerPacks.Shipped(Session().Data, archive);
        }

        /// <summary>
        /// A raw script names its importers in the lint: they are found once per archive, by a job (every script is
        /// read), and the script is linted again when it is done.
        /// </summary>
        void FindImportersLater(TriggerPacks p, string format)
        {
            if (p == null || p.HasImporters || format != "raw" || importersJob == p || host == null || host.Busy) return;
            importersJob = p;
            bool started = host.RunOutside("finding the trigger scripts' imports", delegate { p.BuildImporters(); }, delegate
            {
                if (IsDisposed || host == null) return;
                if (script != null && Packs() == p) Relint();
                host.SayOutside("the imports of " + p.Names.Count + " trigger scripts are read; the lint names a library script's importers");
            });
            if (!started) importersJob = null;
        }

        /// <summary>The script shown again (the lint again), with the same node chosen.</summary>
        void Relint()
        {
            TriggerNode was = tree.SelectedNode != null ? tree.SelectedNode.Tag as TriggerNode : null;
            int line = was != null ? was.Line : 0;
            string tag = was != null && was.Elem != null ? was.Elem.Name : null;
            string name = was != null ? was.Name : null;
            ShowScript(script);
            if (was == null) return;
            foreach (KeyValuePair<TriggerNode, TreeNode> kv in treeNodes)
                if (kv.Key.Line == line && kv.Key.Name == name && (kv.Key.Elem != null ? kv.Key.Elem.Name : null) == tag) { tree.SelectedNode = kv.Value; kv.Value.EnsureVisible(); break; }
        }

        void ShowScript(TriggerScript s)
        {
            script = s;
            treeNodes.Clear();
            string format = null;
            TriggerPacks p = s == null ? null : Packs();
            findings = s == null ? new List<TriggerFinding>() : TriggerLint.Shipped.Lint(s.Entry, s.Bytes, p, LintOptions(s), out format);
            tree.BeginUpdate();
            try
            {
                tree.Nodes.Clear();
                if (s == null) { scriptLabel.Text = "no script"; return; }
                if (s.ParseError != null)
                {
                    scriptLabel.Text = s.Entry + ": does not parse";
                    tree.Nodes.Add(new TreeNode("the file does not parse: " + s.ParseError) { ForeColor = Color.Firebrick });
                    return;
                }
                List<TriggerNode> states = s.States;
                scriptLabel.Text = s.Entry + ": " + states.Count + " state(s)" + (Editor().IsModded(s.Archive, s.Entry) ? ", in mod " + Session().Mod.Id : "");
                foreach (TriggerNode n in s.Root.Children) tree.Nodes.Add(Node(n));
                MarkFindings();
                foreach (TreeNode t in tree.Nodes) t.Expand();
                if (tree.Nodes.Count > 0) tree.Nodes[0].EnsureVisible();
            }
            finally
            {
                tree.EndUpdate();
                FillFindings();
                FillAttrs();
                RefreshWarnBox();
            }
            FindImportersLater(p, format);
            if (s != null) ReadObjectsLater();
        }

        TriggerLintOptions LintOptions(TriggerScript s)
        {
            var o = new TriggerLintOptions { ScanImporters = false, Strict = strictBox.Checked };
            if (objects != null && objectsData == Session().Data)
            {
                o.XBlock = s.XBlock;
                TriggerObjects got;
                string why;
                objects.TryGet(s.XBlock, out got, out why);
                o.Objects = got;
                o.XBlockError = why;
            }
            return o;
        }

        /// <summary>The map files' objects: read once per data set by a job, and the script linted again with them.</summary>
        void ReadObjectsLater()
        {
            DataSet data = Session().Data;
            if (objectsData == data || archive == null || host == null) return;
            if (host.Busy)
            {
                // tried again on the next script shown
                objectsNote = "the map files' objects are read when the running job is done: the object ids are not checked yet";
                FillFindings();
                return;
            }
            string ex = Triggers.ExportedOf(data, archive);
            objectsData = data;
            objects = null;
            if (ex == null) { objectsNote = "no data root has " + Planner.MapArchivePath + ": the object ids are not checked"; FillFindings(); return; }
            string ws = Session().WorkspaceDir;
            string cacheDir = ws == null ? null : System.IO.Path.Combine(ws, "cache");
            // the cache stays out of every data root and the game folder
            var keep = new List<string>();
            foreach (string r in data.Roots) keep.Add(data.RootFolder(r));
            if (Session().GameDir != null) keep.Add(Session().GameDir);
            TriggerXBlocks got = null;
            string note = null;
            objectsNote = "reading the objects of the map files";
            FillFindings();
            bool started = host.RunOutside("reading the objects of the map files (" + ex + ")", delegate
            {
                ReadObjects(data, ex, cacheDir, keep, out got, out note);
            }, delegate
            {
                if (IsDisposed || host == null || objectsData != data) return;
                objects = got;
                // a read that failed is tried again on the next script shown
                if (got == null) objectsData = null;
                objectsNote = note;
                if (script != null) Relint();
                host.SayOutside(note);
            });
            if (!started) { objectsData = null; objectsNote = null; }
        }

        static void ReadObjects(DataSet data, string ex, string cacheDir, List<string> keep, out TriggerXBlocks got, out string note)
        {
            got = null;
            try
            {
                MS2.ModKit.M2d.M2dArchive a = data.Open(ex);
                string why = null;
                try { got = TriggerXBlocks.Cached(a, cacheDir, keep); }
                catch (ModKitException ex2) { got = TriggerXBlocks.OfArchive(a); why = ex2.Message + "; read without the cache. "; }
                if (got.WriteError != null) why += got.WriteError + ". ";
                int bad = got.Broken.Count;
                if (bad > 0) why += bad.ToString(CultureInfo.InvariantCulture) + " xblock(s) do not read (the lint says which). ";
                note = why + (got.FromCache ? "the map files' objects are read from the index cache " + got.CacheFile
                    : got.CacheFile != null ? "the map files' objects are read from " + ex + " and kept in the index cache " + got.CacheFile
                    : "the map files' objects are read from " + ex + (cacheDir == null ? " (no workspace folder, so no index cache)" : ""))
                    + "; the lint checks the object ids of the scripts";
            }
            catch (Exception e)
            {
                if (!(e is ModKitException) && !(e is System.IO.IOException) && !(e is System.IO.InvalidDataException) && !(e is FormatException)
                    && !(e is UnauthorizedAccessException)) throw;
                got = null;
                note = "the map files' objects could not be read (" + e.Message + "): the object ids are not checked";
            }
        }

        TreeNode Node(TriggerNode n)
        {
            var t = new TreeNode(n.Label) { Tag = n, ToolTipText = "line " + n.Line.ToString(CultureInfo.InvariantCulture) };
            treeNodes[n] = t;
            if (n.Kind == TriggerKind.Transition) t.ForeColor = Color.RoyalBlue;
            else if (n.Kind == TriggerKind.Condition) t.ForeColor = Color.DarkGreen;
            else if (n.Kind == TriggerKind.Other) t.ForeColor = SystemColors.GrayText;
            foreach (TriggerNode c in n.Children) t.Nodes.Add(Node(c));
            return t;
        }

        /// <summary>The tree node a finding is about, or null (the whole file, or a line of an imported script).</summary>
        TreeNode NodeOf(TriggerFinding f)
        {
            if (script == null || f.Line <= 0 || f.ImportedFrom != null) return null;
            TriggerNode n = script.NodeAt(f.Line, f.Tag);
            TreeNode t;
            return n != null && treeNodes.TryGetValue(n, out t) ? t : null;
        }

        // the nodes with problems: red for an error, orange for a warning, grey-brown for a known issue; the codes after
        // the label, the messages in the tooltip
        void MarkFindings()
        {
            var byNode = new Dictionary<TreeNode, List<TriggerFinding>>();
            foreach (TriggerFinding f in findings)
            {
                TreeNode t = NodeOf(f);
                if (t == null) continue;
                List<TriggerFinding> l;
                if (!byNode.TryGetValue(t, out l)) byNode[t] = l = new List<TriggerFinding>();
                l.Add(f);
            }
            foreach (KeyValuePair<TreeNode, List<TriggerFinding>> kv in byNode)
            {
                string worst = "allowed";
                var codes = new List<string>();
                var tip = new List<string> { kv.Key.ToolTipText };
                foreach (TriggerFinding f in kv.Value)
                {
                    if (f.IsError) worst = "error";
                    else if (f.Level == "warning" && worst != "error") worst = "warning";
                    if (!codes.Contains(f.Code)) codes.Add(f.Code);
                    tip.Add(f.Level + ": " + f.Message + (f.AllowReason != null ? " (known: " + f.AllowReason + ")" : ""));
                }
                kv.Key.ForeColor = LevelColour(worst);
                kv.Key.Text += "    [" + worst + ": " + string.Join(", ", codes.ToArray()) + "]";
                kv.Key.ToolTipText = string.Join("\n", tip.ToArray());
            }
        }

        static Color LevelColour(string level)
        {
            return level == "error" ? Color.Firebrick : level == "warning" ? Color.DarkOrange : Color.RosyBrown;
        }

        void FillFindings()
        {
            int errors = 0, warnings = 0, allowed = 0;
            findingList.BeginUpdate();
            try
            {
                findingList.Items.Clear();
                foreach (TriggerFinding f in findings)
                {
                    if (f.IsError) errors++; else if (f.Level == "warning") warnings++; else allowed++;
                    string msg = f.Message + (f.AllowReason != null ? " (known: " + f.AllowReason + ")" : "");
                    var it = new ListViewItem(new[] { f.Level, f.Line.ToString(CultureInfo.InvariantCulture), f.Code, msg }) { Tag = f, ForeColor = LevelColour(f.Level) };
                    findingList.Items.Add(it);
                }
            }
            finally { findingList.EndUpdate(); }
            findingsLabel.Text = script == null ? "" : (findings.Count == 0 ? "lint: no problem"
                : "lint: " + errors + " error(s), " + warnings + " warning(s), " + allowed + " known issue(s)")
                + (objects != null ? ", object ids checked" : objectsNote != null ? " (" + objectsNote + ")" : "");
            FitFindings();
        }

        void FitFindings()
        {
            int w = findingList.ClientSize.Width - findingList.Columns[0].Width - findingList.Columns[1].Width - findingList.Columns[2].Width - 4;
            if (w > 160) findingList.Columns[3].Width = w;
        }

        void GoToFinding()
        {
            TriggerFinding f = findingList.SelectedItems.Count > 0 ? findingList.SelectedItems[0].Tag as TriggerFinding : null;
            if (f == null) { host.SayOutside("choose a problem in the list first"); return; }
            TreeNode t = NodeOf(f);
            if (t == null)
            {
                host.SayOutside(f.ImportedFrom != null ? "the problem is in the imported script " + f.ImportedFrom + ", line " + f.Line
                    : "the problem is about the whole file (line " + f.Line + "): " + f.Message);
                return;
            }
            tree.SelectedNode = t;
            t.EnsureVisible();
            tree.Focus();
        }

        // ---- edits ----

        TriggerNode Chosen() { return tree.SelectedNode == null ? null : tree.SelectedNode.Tag as TriggerNode; }

        void NeedScript()
        {
            if (script == null) throw new ModKitException("choose a script first");
            if (script.ParseError != null) throw new ModKitException("the script does not parse; it cannot be edited here");
        }

        void FillAttrs()
        {
            TriggerNode n = Chosen();
            string keep = attrBox.Text;
            attrBox.Items.Clear();
            if (n == null || n.Elem == null) { valueBox.Text = ""; return; }
            string first = null;
            foreach (MS2.ModKit.Xml.XmlAttr a in n.Elem.Attrs)
            {
                attrBox.Items.Add(a.Name);
                if (first == null && a.Name != "name") first = a.Name;
            }
            attrBox.Text = n.Elem.FindAttr(keep) != null ? keep : first ?? (n.Elem.Attrs.Count > 0 ? n.Elem.Attrs[0].Name : "");
            ShowValue();
        }

        void ShowValue()
        {
            TriggerNode n = Chosen();
            string v = n == null || n.Elem == null ? null : n.Elem.Attr(attrBox.Text.Trim());
            valueBox.Text = v ?? "";
        }

        // the chosen node as child indexes from the top, to choose it again after the tree is rebuilt
        static List<int> PathOf(TreeNode t)
        {
            var l = new List<int>();
            for (; t != null; t = t.Parent) l.Insert(0, t.Index);
            return l;
        }

        void Choose(List<int> path)
        {
            TreeNodeCollection level = tree.Nodes;
            TreeNode hit = null;
            foreach (int i in path)
            {
                if (level.Count == 0) break;
                hit = level[Math.Min(i, level.Count - 1)];
                level = hit.Nodes;
            }
            if (hit == null) return;
            tree.SelectedNode = hit;
            hit.Expand();
            hit.EnsureVisible();
        }

        // after an edit: the mod pane, the script list (its * marks) and the tree follow; the node chosen before
        // (or the one at its place) is chosen again
        void Edited(TriggerScript now, List<int> path, string said)
        {
            host.EditedOutside();
            ShowMap(mapId, now.Entry);
            if (path != null) Choose(path);
            int errors = 0;
            foreach (TriggerFinding f in findings) if (f.IsError) errors++;
            host.SayOutside(said + "\r\n" + now.Entry + " is a whole-file entry of mod " + Session().Mod.Id + " now (Save mod writes it beside mod.xml)."
                + (errors > 0 ? "\r\nlint: " + errors + " error(s); Apply stops on them" + (Session().Mod.TriggerLintWarn ? " unless the mod's \"lint errors only warn\" is on (it is)" : " (or turn on \"lint errors only warn\" for the mod)") : ""));
        }

        void SetAttr()
        {
            NeedScript();
            TriggerNode n = Chosen();
            if (n == null || n.Elem == null) throw new ModKitException("choose a node in the tree first");
            string name = attrBox.Text.Trim();
            if (name.Length == 0) throw new ModKitException("name the attribute to set");
            string value = valueBox.Text;
            List<int> path = PathOf(tree.SelectedNode);
            TriggerScript now = Editor().SetAttr(script, n, name, value);
            Edited(now, path, (value.Length == 0 ? "removed " + name + " of " : "set " + name + "=\"" + value + "\" on ") + n.Label + " (line " + n.Line + ")");
        }

        // the node a new element of the kind goes under: the chosen node or the nearest one above it that can hold
        // it; an action or transition chosen on a state goes into its on enter block
        TriggerNode Target(TriggerNode n, TriggerKind kind)
        {
            if (kind == TriggerKind.State || n == null) return script.Root;
            for (TriggerNode p = n; p != null; p = p.Parent)
            {
                if (TriggerEditor.CanHold(p, kind)) return p;
                if (p.Kind == TriggerKind.State && (kind == TriggerKind.Action || kind == TriggerKind.Transition))
                    foreach (TriggerNode c in p.Children) if (c.Kind == TriggerKind.OnEnter) return c;
            }
            return null;
        }

        void Add()
        {
            NeedScript();
            TriggerKind kind = Kinds[Math.Max(0, kindBox.SelectedIndex)];
            TriggerNode n = Chosen();
            TriggerNode target = Target(n, kind);
            if (target == null || !TriggerEditor.CanHold(target, kind))
                throw new ModKitException("a new " + KindNames[kindBox.SelectedIndex] + " cannot go under " + (n == null ? "the script" : n.Label) + "; choose a "
                    + (kind == TriggerKind.Condition ? "state or a group" : kind == TriggerKind.OnEnter || kind == TriggerKind.OnExit ? "state without one" : "state, on enter, on exit or condition"));
            TreeNode tt;
            List<int> path = target.Kind == TriggerKind.Script ? null : treeNodes.TryGetValue(target, out tt) ? PathOf(tt) : null;
            string name = nameBox.Text.Trim();
            TriggerScript now = Editor().Add(script, target, kind, name, goToBox.Text.Trim());
            Edited(now, path, "added " + KindNames[kindBox.SelectedIndex] + (name.Length > 0 ? " " + name : "") + " under " + (target.Kind == TriggerKind.Script ? "the script" : target.Label));
            if (kind == TriggerKind.State) { TriggerNode st = script == null ? null : script.State(name); TreeNode t; if (st != null && treeNodes.TryGetValue(st, out t)) { tree.SelectedNode = t; t.EnsureVisible(); } }
        }

        void Remove()
        {
            NeedScript();
            TriggerNode n = Chosen();
            if (n == null) throw new ModKitException("choose the node to remove in the tree first");
            List<int> path = PathOf(tree.SelectedNode);
            TriggerScript now = Editor().Remove(script, n);
            Edited(now, path, "removed " + n.Label + " (line " + n.Line + ") and all under it");
        }

        void NewScript()
        {
            if (archive == null) throw new ModKitException("no archive of the data set has trigger scripts");
            string xblock = script != null ? script.XBlock : folders.Count > 0 ? folders[0].XBlock : null;
            if (xblock == null) throw new ModKitException("the map has no map file (xblock), so no trigger folder to add to");
            TriggerScript now = Editor().NewScript(archive, xblock, newScriptBox.Text.Trim(), "Start");
            newScriptBox.Text = "";
            Edited(now, null, "added the script " + now.Entry + " with one state, Start");
        }

        void Revert()
        {
            NeedScript();
            string entry = script.Entry;
            TriggerEditor ed = Editor();
            if (!ed.IsModded(archive, entry)) { host.SayOutside(entry + " is not in the mod; there is nothing to revert"); return; }
            ed.Revert(archive, entry);
            host.EditedOutside();
            ShowMap(mapId, entry);
            host.SayOutside(entry + ": the mod's edits are taken out" + (ed.Current(archive, entry) == null ? " (the script was the mod's own, it is gone)" : ""));
        }

        void SetLintWarn()
        {
            Session().Mod.TriggerLintWarn = lintWarnBox.Checked;
            host.EditedOutside();
            host.SayOutside("mod " + Session().Mod.Id + ": a lint error of its trigger scripts is " + (lintWarnBox.Checked ? "a plan warning; Apply goes on (trigger-lint=\"warn\")" : "a plan error; Apply stops on it"));
        }

        void CollapseToStates()
        {
            tree.BeginUpdate();
            foreach (TreeNode t in tree.Nodes) t.Collapse(false);
            tree.EndUpdate();
        }
    }
}
