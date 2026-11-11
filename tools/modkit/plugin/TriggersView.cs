// The "MS2 Triggers" view: the trigger scripts of a map. A map search (id or name, the maps of the Pick dialog),
// the list of hits, the scripts of the chosen map (one folder per map file, trigger/<xblock>/), and the chosen
// script as a tree: its states, each with its on enter actions, its conditions (their actions and the states they
// go to) and its on exit actions. The output box is the MS2 Tables view's.
//
// The script is linted as it is shown (MS2.ModKit.TriggerLint, the port of tools/trigger/lint.py with its schema and
// known issues): a node with a problem is coloured and names it, and the list under the tree has every problem of the
// script, with Go to.
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
        TriggerPacks packs;
        string packsArchive;

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
            findingBar.Controls.Add(findingsLabel);
            var findingPanel = new Panel { Dock = DockStyle.Bottom, Height = 170 };
            findingPanel.Controls.Add(findingList);
            findingPanel.Controls.Add(findingBar);

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(tree);
            right.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            right.Controls.Add(findingPanel);
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
                    IViewService views = Platform.GetService<IViewService>();
                    h = views == null ? null : views.CreateOrFindView(TablesView.ViewId) as TablesView;
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

        void OnHostBusy(bool on)
        {
            foreach (Control c in lockable) c.Enabled = !on;
            UseWaitCursor = on;
        }

        void OnHostChanged()
        {
            lookup = null;
            packs = null;
            string keep = script != null ? script.Entry : null;
            try
            {
                FillMaps(mapId);
                if (mapId != 0) ShowMap(mapId, keep);
            }
            catch (ModKitException) { ShowScript(null); }
            catch (System.IO.IOException) { ShowScript(null); }
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
            if (archive != packsArchive) packs = null;
            List<TriggerFolder> folders = archive == null ? new List<TriggerFolder>() : Triggers.FoldersOf(Session().Data, archive, id);
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
                        scriptList.Items.Add(folders.Count == 1 ? e.Substring(e.LastIndexOf('/') + 1) : e.Substring(Triggers.Folder.Length));
                    }
                scriptList.EndUpdate();
            }
            finally { filling = false; }
            int n = 0;
            foreach (TriggerFolder f in folders) n += f.Scripts.Count;
            var names = new List<string>();
            foreach (TriggerFolder f in folders) names.Add(f.XBlock);
            scriptsLabel.Text = folders.Count == 0 ? "no map file" : n + " script(s) in " + string.Join(", ", names.ToArray());
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
            ShowScript(Triggers.Read(Session().Data, archive, scriptEntries[i]));
        }

        TriggerPacks Packs()
        {
            if (packs == null && archive != null) { packs = TriggerPacks.OfArchive(Session().Data, archive); packsArchive = archive; }
            return packs;
        }

        void ShowScript(TriggerScript s)
        {
            script = s;
            treeNodes.Clear();
            findings = s == null ? new List<TriggerFinding>() : TriggerLint.Shipped.Lint(s.Entry, s.Bytes, Packs());
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
                scriptLabel.Text = s.Entry + ": " + states.Count + " state(s)";
                foreach (TriggerNode n in s.Root.Children) tree.Nodes.Add(Node(n));
                MarkFindings();
                foreach (TreeNode t in tree.Nodes) t.Expand();
                if (tree.Nodes.Count > 0) tree.Nodes[0].EnsureVisible();
            }
            finally
            {
                tree.EndUpdate();
                FillFindings();
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
            TriggerNode n = script.NodeAt(f.Line);
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
            findingsLabel.Text = script == null ? "" : findings.Count == 0 ? "lint: no problem"
                : "lint: " + errors + " error(s), " + warnings + " warning(s), " + allowed + " known issue(s)";
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

        void CollapseToStates()
        {
            tree.BeginUpdate();
            foreach (TreeNode t in tree.Nodes) t.Collapse(false);
            tree.EndUpdate();
        }
    }
}
