// The "MS2 Triggers" view: the trigger scripts of a map. A map search (id or name, the maps of the Pick dialog),
// the list of hits, the scripts of the chosen map (one folder per map file, trigger/<xblock>/), and the chosen
// script as a tree: its states, each with its on enter actions, its conditions (their actions and the states they
// go to) and its on exit actions. The output box is the MS2 Tables view's.
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

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(tree);
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

        void ShowScript(TriggerScript s)
        {
            script = s;
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
                foreach (TreeNode t in tree.Nodes) t.Expand();
                if (tree.Nodes.Count > 0) tree.Nodes[0].EnsureVisible();
            }
            finally { tree.EndUpdate(); }
        }

        static TreeNode Node(TriggerNode n)
        {
            var t = new TreeNode(n.Label) { Tag = n, ToolTipText = "line " + n.Line.ToString(CultureInfo.InvariantCulture) };
            if (n.Kind == TriggerKind.State) t.NodeFont = null;
            if (n.Kind == TriggerKind.Transition) t.ForeColor = Color.RoyalBlue;
            else if (n.Kind == TriggerKind.Condition) t.ForeColor = Color.DarkGreen;
            else if (n.Kind == TriggerKind.Other) t.ForeColor = SystemColors.GrayText;
            foreach (TriggerNode c in n.Children) t.Nodes.Add(Node(c));
            return t;
        }

        void CollapseToStates()
        {
            tree.BeginUpdate();
            foreach (TreeNode t in tree.Nodes) t.Collapse(false);
            tree.EndUpdate();
        }
    }
}
