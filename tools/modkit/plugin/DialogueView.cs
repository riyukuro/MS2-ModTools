// The "MS2 Dialogue" view: the talk scripts of the NPCs (script/npc) and their talk texts, step by step, in the style
// of the MS2 Recipes view. A find box (NPC id or name) and a "changed by the mod" filter; the list of NPCs with a talk
// (id, name, steps, the targets that have it, problems); the steps of the chosen NPC (kind, id, first line, a mark
// when the targets have the step differently) beside the flow from the steps the client starts on along the jumps;
// the lines of the chosen step (its pages, choices and events) with the text in the chosen language, the button
// set, voice, picture, jumps and the other attributes, all editable; the problems of the talk; and the quest talk
// of the quests the NPC starts or completes, read only, with a jump to the MS2 Quests view, which owns it.
//
// The talk is one thing on every target that has it (the Korean client's combined file and the global client's and
// the server's files, see MS2.ModKit.TalkEditor); every edit is an op of the mod the MS2 Tables view is editing.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using Emergent.Toolbench;
using Emergent.Toolbench.UI.Workbench;
using MS2.ModKit;
using MS2.ModKit.Xml;

namespace MS2.Tables
{
    public class DialogueView : AbstractView
    {
        public const string ViewId = "MS2.Tables.DialogueView";

        // the attribute columns of the line grid after line, text and text id; "more" holds the rest
        static readonly string[] AttrColumns = { "buttonSet", "voiceID", "illust", "goto", "gotoFail" };
        const int TextCol = 1, FirstAttrCol = 3, MoreCol = FirstAttrCol + 5, ButtonsCol = MoreCol + 1;

        sealed class NpcInfo
        {
            internal int Steps;
            internal string Sides = "";
            internal int Problems, Shipped;
            internal bool Changed;
        }

        sealed class Line
        {
            internal TalkPath Path;
            internal int Depth;
            internal string Label = "";
            internal string Name = "";   // the element: script, content, distractor, event, ...
        }

        TablesView host;
        TalkEditor editor;
        Dictionary<long, string> names;
        readonly Dictionary<long, NpcInfo> infos = new Dictionary<long, NpcInfo>();
        Dictionary<string, Dictionary<string, HashSet<long>>> textIds = new Dictionary<string, Dictionary<string, HashSet<long>>>();
        readonly List<long> hits = new List<long>();
        NpcTalk talk;
        // the cell edit typed but not yet in the mod (see NpcTalkCellEdit.cs)
        readonly TalkEditQueue typed = new TalkEditQueue();
        // what the last edit said, without the NPC's summary
        string saidLast;
        readonly List<string> stepKeys = new List<string>();
        readonly List<Line> lines = new List<Line>();
        List<TalkProblem> problems = new List<TalkProblem>();
        readonly List<UseHit> questHits = new List<UseHit>();
        bool filling;

        readonly TextBox searchBox = new TextBox { Width = 180 };
        readonly CheckBox changedBox = new CheckBox { Text = "changed by the mod", AutoSize = true, Margin = new Padding(8, 6, 3, 0) };
        readonly ComboBox langBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
        readonly Label hitsLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        int talkCount;
        readonly DataGridView npcGrid = new DataGridView { Dock = DockStyle.Left, Width = 420 };
        readonly Label npcLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 8, 0) };
        readonly ComboBox kindBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        readonly TextBox stepIdBox = new TextBox { Width = 60 };
        readonly DataGridView stepGrid = new DataGridView { Dock = DockStyle.Fill };
        readonly TreeView flowTree = new TreeView { Dock = DockStyle.Bottom, Height = 200, HideSelection = false, ShowRootLines = true };
        readonly Panel stepPane = new Panel { Dock = DockStyle.Left, Width = 360 };
        readonly Label stepLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 8, 0) };
        readonly DataGridView lineGrid = new DataGridView { Dock = DockStyle.Fill };
        readonly ListView problemList = new ListView { Dock = DockStyle.Bottom, Height = 110, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.None, MultiSelect = false };
        readonly ListView questList = new ListView { Dock = DockStyle.Bottom, Height = 110, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.None, MultiSelect = false };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 130 };
        readonly ComboBox targetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        readonly ContextMenuStrip pickMenu = new ContextMenuStrip();
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();
        readonly BoldFont bold = new BoldFont();

        public DialogueView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(Lbl("find"));
            top.Controls.Add(searchBox);
            tips.SetToolTip(searchBox, "The start of an NPC id or a part of its name; empty lists every NPC with a talk");
            top.Controls.Add(Btn("Find", "Lists the NPCs with a talk whose id starts with the text or whose name holds it", delegate { Find(); }));
            top.Controls.Add(changedBox);
            tips.SetToolTip(changedBox, "Only the NPCs whose talk the mod changes on some target");
            top.Controls.Add(Lbl("text in"));
            top.Controls.Add(langBox);
            tips.SetToolTip(langBox, "The language the lines are shown and edited in (the talk text files of every target in that language)");
            top.Controls.Add(hitsLabel);

            FlowLayoutPanel applyBar = Bar();
            applyBar.Controls.Add(Lbl("Apply to"));
            targetBox.Items.Add("workspace (patched archives)");
            targetBox.Items.Add("game folder (loose files)");
            tips.SetToolTip(targetBox, "Where Apply writes the client's files (the same choice as in the MS2 Tables view). The server's archives and the global client's always go to the workspace.");
            applyBar.Controls.Add(targetBox);
            applyBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change, with the talk checks for each target; writes nothing", delegate { return host.DoPlan(); }));
            applyBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change, to every target at once", delegate { return host.DoApply(); }));
            applyBar.Controls.Add(Job("Revert", "Removes what Apply wrote, on every target", delegate { return host.DoRevert(); }));

            SetupGrid(npcGrid);
            npcGrid.Columns.Add(Col("id", 60));
            npcGrid.Columns.Add(Col("name", 80));
            npcGrid.Columns.Add(Col("steps", 35));
            npcGrid.Columns.Add(Col("targets", 55));
            npcGrid.Columns.Add(Col("problems", 45));
            npcGrid.ReadOnly = true;
            npcGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            npcGrid.CellValueNeeded += OnNpcValueNeeded;
            npcGrid.CellFormatting += OnNpcFormatting;
            npcGrid.SelectionChanged += delegate { if (!filling) Guard(delegate { OpenSelected(); }); };

            // the NPC's bar: the label goes last, its text changes with the NPC
            FlowLayoutPanel npcBar = Bar();
            foreach (string k in TalkEditor.StepKinds) kindBox.Items.Add(k);
            kindBox.SelectedIndex = 1;
            npcBar.Controls.Add(kindBox);
            tips.SetToolTip(kindBox, "The kind of a new step: select, monologue and job steps are where the client starts; a script is reached by a jump or a random pick");
            npcBar.Controls.Add(Lbl("id"));
            npcBar.Controls.Add(stepIdBox);
            tips.SetToolTip(stepIdBox, "The id of a new step; empty = the next free id in the id class of the chosen step (10-99, 100-199, 300 and more: the client shows other buttons per class)");
            npcBar.Controls.Add(Btn("Add step", "Adds a step of the kind with one empty page under the id, on every target that has the talk", delegate { AddStep(); }));
            npcBar.Controls.Add(Btn("Remove step", "Removes the chosen step on every target", delegate { RemoveStep(); }));
            npcBar.Controls.Add(Btn("Revert NPC", "Takes the NPC's talk out of the mod on every target (its own talk texts stay in the mod)", delegate { RevertNpc(); }));
            npcBar.Controls.Add(Btn("Quest talk", "Lists the quests the NPC starts or completes with their talk lines (read only here: the MS2 Quests view edits them); double-click one to open it there", delegate { QuestTalk(); }));
            npcBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same mod and Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));
            npcBar.Controls.Add(npcLabel);

            SetupGrid(stepGrid);
            stepGrid.Columns.Add(Col("", 20));
            stepGrid.Columns.Add(Col("step", 70));
            stepGrid.Columns.Add(Col("first line", 130));
            stepGrid.ReadOnly = true;
            stepGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            stepGrid.CellValueNeeded += OnStepValueNeeded;
            stepGrid.CellFormatting += OnStepFormatting;
            stepGrid.SelectionChanged += delegate { if (!filling) Guard(delegate { ShowStep(); }); };
            tips.SetToolTip(stepGrid, "The steps of the talk; ≠ marks a step the targets have differently (the lines show the first target's)");
            tips.SetToolTip(flowTree, "The flow from the steps the client starts on along the jumps; red: a jump to no step or a script nothing reaches. Click a line to show its step.");
            flowTree.AfterSelect += delegate(object sender, TreeViewEventArgs e) { if (!filling) Guard(delegate { SelectStep(e.Node == null ? null : e.Node.Tag as string); }); };
            stepPane.Controls.Add(stepGrid);
            stepPane.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            stepPane.Controls.Add(flowTree);

            FlowLayoutPanel lineBar = Bar();
            lineBar.Controls.Add(Btn("Add page", "Adds an empty page (content) after the last page of the step, on every target", delegate { AddPage(); }));
            lineBar.Controls.Add(Btn("Add choice", "Adds an empty choice (distractor) to the chosen page, on every target", delegate { AddChoice(); }));
            lineBar.Controls.Add(Btn("Remove line", "Removes the chosen page, choice or event on every target", delegate { RemoveLine(); }));
            lineBar.Controls.Add(Btn("Move up", "Moves the chosen page, choice or event one place up on every target", delegate { MoveLine(-1); }));
            lineBar.Controls.Add(Btn("Move down", "Moves the chosen page, choice or event one place down on every target", delegate { MoveLine(1); }));
            lineBar.Controls.Add(Btn("Pick step", "Picks the step the chosen goto or gotoFail cell jumps to, or a new script step (F4 in the cell does the same)", delegate { PickStep(); }));
            lineBar.Controls.Add(stepLabel);

            SetupGrid(lineGrid);
            lineGrid.Columns.Add(Col("line", 70));
            lineGrid.Columns.Add(Col("text", 160));
            lineGrid.Columns.Add(Col("text id", 75));
            foreach (string a in AttrColumns) lineGrid.Columns.Add(Col(a, a == "voiceID" ? 55 : a == "gotoFail" ? 50 : a == "goto" ? 40 : 45));
            lineGrid.Columns.Add(Col("more attributes", 60));
            lineGrid.Columns.Add(Col("buttons", 60));
            lineGrid.Columns[ButtonsCol].ReadOnly = true;
            lineGrid.Columns[ButtonsCol].ToolTipText = "The button set the client shows for the page (GetTalkButtonSet), read-only: a buttonSet attribute, else what the step kind, the choices and the last page make";
            lineGrid.Columns[0].ReadOnly = true;
            lineGrid.Columns[2].ReadOnly = true;
            lineGrid.CellValueNeeded += OnLineValueNeeded;
            lineGrid.CellValuePushed += OnLineValuePushed;
            lineGrid.CellFormatting += OnLineFormatting;
            lineGrid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e)
            {
                Line l = LineAt(e.RowIndex);
                if (l == null || (e.ColumnIndex == TextCol && l.Name != "content" && l.Name != "distractor")) e.Cancel = true;
            };
            lineGrid.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.F4 || !IsJumpCell(lineGrid.CurrentCell)) return;
                e.Handled = true;
                Guard(delegate { PickStep(); });
            };
            tips.SetToolTip(lineGrid, "The lines of the step: the text in the chosen language (an edit gives the line a text of the NPC's own on its first edit), and the attributes; \"more attributes\" holds the rest as name=value; name=value");

            problemList.Columns.Add("problem", 600);
            questList.Columns.Add("quest talk", 600);
            questList.ItemActivate += delegate { Guard(delegate { OpenQuest(); }); };
            tips.SetToolTip(problemList, "The problems of the talk on each target; grey: the shipped talk has them too (the plan leaves those out)");

            var linePane = new Panel { Dock = DockStyle.Fill };
            linePane.Controls.Add(lineGrid);
            linePane.Controls.Add(lineBar);

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(linePane);
            right.Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
            right.Controls.Add(stepPane);
            right.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            right.Controls.Add(questList);
            right.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            right.Controls.Add(problemList);
            right.Controls.Add(npcBar);

            Controls.Add(right);
            Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
            Controls.Add(npcGrid);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(outputBox);
            Controls.Add(applyBar);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, applyBar, npcGrid, right });
            ViewLayout.KeepLogLines(outputBox, this);
            ViewLayout.KeepHeightShare(problemList, right, 0.16, 3 * Font.Height + 8);
            ViewLayout.KeepHeightShare(questList, right, 0.14, 3 * Font.Height + 8);
            ViewLayout.KeepHeightShare(flowTree, stepPane, 0.45, 4 * Font.Height + 8);
            KeepWidthShare(npcGrid, this, 0.30, 320);
            KeepWidthShare(stepPane, right, 0.30, 240);
            ViewLayout.CentreOnFirstShow(this);
            Resize += delegate { FitListColumns(); };
            // the list fills on the first show when the view was made before the data was there
            VisibleChanged += delegate { if (Visible && hits.Count == 0 && !filling) Guard(delegate { Find(); }); };
            targetBox.SelectedIndexChanged += delegate { if (!filling && host != null) host.TargetIndex = targetBox.SelectedIndex; };
            langBox.SelectedIndexChanged += delegate { if (!filling) Guard(delegate { stepGrid.Invalidate(); ShowStep(); }); };
            changedBox.CheckedChanged += delegate { if (!filling) Guard(delegate { Find(); }); };
            searchBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                Guard(delegate { Find(); });
            };
        }

        public override void Initialize()
        {
            base.Initialize();
            Guard(delegate { Find(); });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Hook(null);
                bold.Dispose();
                tips.Dispose();
                pickMenu.Dispose();
                Font f = outputBox.Font;
                outputBox.Font = null;
                f.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---- small builders ----

        static FlowLayoutPanel Bar()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(2) };
        }

        static Label Lbl(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(6, 8, 0, 0) }; }

        static DataGridViewColumn Col(string header, int width)
        {
            return ViewLayout.FitColumn(new DataGridViewTextBoxColumn { HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable });
        }

        static void SetupGrid(DataGridView g)
        {
            g.VirtualMode = true;
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.AllowUserToOrderColumns = false;
            g.AllowUserToResizeRows = false;
            g.RowHeadersVisible = false;
            g.MultiSelect = false;
            g.SelectionMode = DataGridViewSelectionMode.CellSelect;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            g.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            g.BackgroundColor = SystemColors.Window;
            g.DefaultCellStyle.BackColor = SystemColors.Window;
            g.DefaultCellStyle.ForeColor = SystemColors.WindowText;
        }

        // a pane docked left keeps a share of its container's width, never under min pixels nor leaving less than min
        static void KeepWidthShare(Control pane, Control container, double share, int min)
        {
            bool adjusting = false;
            EventHandler fit = delegate
            {
                int w = container.ClientSize.Width;
                if (w <= 0 || adjusting) return;
                adjusting = true;
                pane.Width = Math.Max(Math.Min((int)Math.Round(w * share), w - min), Math.Min(min, w / 2));
                adjusting = false;
            };
            container.Resize += fit;
            pane.Resize += delegate
            {
                // a dragged splitter sets a new share
                if (adjusting || container.ClientSize.Width <= 0) return;
                share = (double)pane.Width / container.ClientSize.Width;
            };
            fit(null, EventArgs.Empty);
        }

        void FitListColumns()
        {
            problemList.Columns[0].Width = Math.Max(200, problemList.ClientSize.Width - 4);
            questList.Columns[0].Width = Math.Max(200, questList.ClientSize.Width - 4);
        }

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
            b.Click += delegate
            {
                if (Guard(delegate { EndEdits(); host.NeedHandle(); })) start();
            };
            tips.SetToolTip(b, tip);
            return b;
        }

        /// <summary>Ends the cell edit and puts it into the mod now, on the NPC it was typed for.</summary>
        void EndEdits()
        {
            lineGrid.EndEdit();
            CommitTyped();
        }

        void CommitTyped()
        {
            TalkCellEdit e = typed.Take();
            if (e == null) return;
            var missed = new List<string>();
            string said;
            NpcTalk t = Editor().Commit(talk, e, missed, out said);
            if (t != null) Edited(t, said, missed);
        }

        // the save, plan or apply of the Tables view: the edit is reported like any other, the mod is read after it; a
        // held edit that fails stops the save, plan or apply with its error (the Tables view's Guard says it), so the
        // lost edit is not hidden behind "saved ..."
        void OnHostFlushing()
        {
            try { EndEdits(); }
            catch (ModKitException ex) { throw new ModKitException("the edit typed in the Dialogue view did not go in, nothing was saved, planned or applied: " + ex.Message); }
        }

        protected override void OnEnter(EventArgs e)
        {
            TablesView.ItemsInUse = true;
            base.OnEnter(e);
        }

        void OnHostTarget()
        {
            bool was = filling;
            filling = true;
            targetBox.SelectedIndex = host.TargetIndex;
            filling = was;
        }

        // ---- the MS2 Tables view: session, mod, worker thread, output ----

        void Hook(TablesView h)
        {
            if (h == host) return;
            long keep = talk != null ? talk.NpcId : -1;
            if (host != null)
            {
                host.Changed -= OnHostChanged;
                host.Flushing -= OnHostFlushing;
                host.BusyChanged -= OnHostBusy;
                host.Said -= OnHostSaid;
                host.TargetChanged -= OnHostTarget;
            }
            host = h;
            Forget();
            if (h == null) { talk = null; hits.Clear(); return; }
            ShowTalk(null);
            h.Changed += OnHostChanged;
            h.Flushing += OnHostFlushing;
            h.BusyChanged += OnHostBusy;
            h.Said += OnHostSaid;
            h.TargetChanged += OnHostTarget;
            OnHostTarget();
            OutputWrap.Set(outputBox, h.OutputText);
            OnHostBusy(h.Busy);
            try { FillHits(keep); }
            catch (ModKitException) { ShowTalk(null); }
            catch (System.IO.IOException) { ShowTalk(null); }
        }

        void Forget()
        {
            // the session or its mod was replaced: an edit typed for the one before has no place to go
            TalkCellEdit lost = typed.Take();
            if (lost != null && host != null) host.SayOutside("the edit typed at NPC " + lost.NpcId + ", " + lost.Path + " was not kept: the mod changed in the MS2 Tables view before it was committed");
            editor = null;
            names = null;
            infos.Clear();
            textIds = new Dictionary<string, Dictionary<string, HashSet<long>>>();
        }

        bool Guard(MethodInvoker body)
        {
            try
            {
                TablesView h = TablesView.Open;
                if (h == null || h.IsDisposed) h = TablesView.OpenBehind();
                if (h == null) { OutputWrap.Set(outputBox, "the MS2 Tables view could not be opened; this view works on the mod of that view"); return false; }
                Hook(h);
            }
            catch (Exception ex)
            {
                OutputWrap.Set(outputBox, "the MS2 Tables view could not be opened: " + ex.Message);
                Logger.Warning("MS2 Dialogue: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        TalkEditor Editor()
        {
            EditorSession s = host.SharedSession();
            if (editor == null || editor.Mod != s.Mod || editor.Data != s.Data)
            {
                editor = s.Talks();
                infos.Clear();
                textIds = new Dictionary<string, Dictionary<string, HashSet<long>>>();
                FillLanguages();
            }
            return editor;
        }

        void FillLanguages()
        {
            string was = langBox.SelectedItem as string;
            bool f = filling;
            filling = true;
            langBox.Items.Clear();
            foreach (string l in editor.TextLanguages()) langBox.Items.Add(l);
            int at = was != null ? langBox.Items.IndexOf(was) : -1;
            if (at < 0) at = langBox.Items.IndexOf("en");
            if (at < 0 && langBox.Items.Count > 0) at = 0;
            langBox.SelectedIndex = at;
            filling = f;
        }

        string Language { get { return langBox.SelectedItem as string; } }

        void OnHostSaid(string text) { OutputWrap.Set(outputBox, text); }

        void OnHostBusy(bool on) { ViewLayout.Lock(this, lockable, on, outputBox); }

        // the mod was replaced or edited in another view: read again and show the same NPC
        void OnHostChanged()
        {
            lineGrid.CancelEdit();
            long keep = talk != null ? talk.NpcId : -1;
            Forget();
            ShowTalk(null);
            try { FillHits(keep); }
            catch (ModKitException) { ShowTalk(null); }
            catch (System.IO.IOException) { ShowTalk(null); }
        }

        // ---- the NPC list ----

        string NameOf(long id)
        {
            if (names == null)
            {
                names = new Dictionary<long, string>();
                foreach (EntityHit h in host.SharedSession().Npcs().Find("", int.MaxValue)) if (!names.ContainsKey(h.Id)) names[h.Id] = h.Name ?? "";
            }
            string n;
            return names.TryGetValue(id, out n) ? n : "";
        }

        void Find()
        {
            EndEdits();
            FillHits(talk != null ? talk.NpcId : -1);
            if (hits.Count > 0 && talk == null) SelectNpc(hits[0]);
            else if (hits.Count == 0) ShowTalk(null);
        }

        void FillHits(long keep)
        {
            TalkEditor ed = Editor();
            string text = searchBox.Text.Trim();
            HashSet<long> mine = ed.ModNpcIds();
            hits.Clear();
            List<long> all = ed.NpcIds();
            foreach (long id in all)
            {
                if (changedBox.Checked && !mine.Contains(id)) continue;
                if (text.Length > 0 && !id.ToString(CultureInfo.InvariantCulture).StartsWith(text, StringComparison.Ordinal)
                    && NameOf(id).IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                hits.Add(id);
            }
            // the mod's NPCs first, then by id
            hits.Sort(delegate(long a, long b)
            {
                bool ma = mine.Contains(a), mb = mine.Contains(b);
                return ma != mb ? (ma ? -1 : 1) : a.CompareTo(b);
            });
            filling = true;
            try
            {
                npcGrid.RowCount = 0;
                npcGrid.RowCount = hits.Count;
                npcGrid.ClearSelection();
            }
            finally { filling = false; }
            talkCount = all.Count;
            ShowCount(mine);
            int at = keep >= 0 ? hits.IndexOf(keep) : -1;
            if (at >= 0) SelectNpc(keep);
            else ShowTalk(null);
        }

        // the header's counts: the hits, the NPCs with a talk (read with the hits) and the NPCs the mod changes now
        void ShowCount(HashSet<long> mine)
        {
            hitsLabel.Text = hits.Count + " NPC(s), " + talkCount + " with a talk, " + mine.Count + " changed by the mod";
        }

        void SelectNpc(long id)
        {
            int at = hits.IndexOf(id);
            if (at < 0) return;
            filling = true;
            try { WhereUsedJump.SelectCell(npcGrid, 0, at); }
            finally { filling = false; }
            ShowTalk(Editor().Open(id));
        }

        NpcInfo Info(long id)
        {
            NpcInfo i;
            if (infos.TryGetValue(id, out i)) return i;
            i = new NpcInfo();
            TalkEditor ed = Editor();
            NpcTalk t = ed.Open(id);
            i.Steps = t.Keys.Count;
            var sides = new List<string>();
            foreach (TalkSide s in t.Sides) { sides.Add(s.Profile.Name); if (s.InMod) i.Changed = true; }
            i.Sides = string.Join(", ", sides.ToArray());
            foreach (TalkProblem p in ed.Problems(t, textIds)) { if (p.Shipped) i.Shipped++; else i.Problems++; }
            infos[id] = i;
            return i;
        }

        void OnNpcValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (host == null || host.IsDisposed || e.RowIndex < 0 || e.RowIndex >= hits.Count) return;
            long id = hits[e.RowIndex];
            try
            {
                switch (e.ColumnIndex)
                {
                    case 0: e.Value = id.ToString(CultureInfo.InvariantCulture); break;
                    case 1: e.Value = NameOf(id); break;
                    case 2: e.Value = Info(id).Steps.ToString(CultureInfo.InvariantCulture); break;
                    case 3: e.Value = Info(id).Sides; break;
                    case 4: NpcInfo i = Info(id); e.Value = i.Problems + (i.Shipped > 0 ? " (+" + i.Shipped + " shipped)" : ""); break;
                }
            }
            catch (ModKitException ex) { e.Value = "(" + ex.Message + ")"; }
            catch (System.IO.IOException ex) { e.Value = "(" + ex.Message + ")"; }
        }

        void OnNpcFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= hits.Count || !infos.ContainsKey(hits[e.RowIndex])) return;
            NpcInfo i = infos[hits[e.RowIndex]];
            if (i.Changed) e.CellStyle.Font = bold.Of(npcGrid.Font);
            if (e.ColumnIndex == 4 && i.Problems > 0) e.CellStyle.ForeColor = Color.Firebrick;
        }

        void OpenSelected()
        {
            if (npcGrid.CurrentCell == null) return;
            int r = npcGrid.CurrentCell.RowIndex;
            if (r < 0 || r >= hits.Count) return;
            long npc = hits[r];
            if (talk != null && talk.NpcId == npc) return;
            // the edit typed for the NPC open now goes to that NPC, before the other one opens
            saidLast = null;
            EndEdits();
            ShowTalk(Editor().Open(npc));
            host.SayOutside((saidLast != null ? saidLast + "\r\n\r\n" : "") + Describe());
        }

        // ---- the talk ----

        void ShowTalk(NpcTalk t)
        {
            lineGrid.CancelEdit();
            string keepStep = StepChosen();
            talk = t;
            stepKeys.Clear();
            questHits.Clear();
            questList.Items.Clear();
            filling = true;
            try
            {
                stepGrid.RowCount = 0;
                flowTree.Nodes.Clear();
                problemList.Items.Clear();
                if (t == null)
                {
                    npcLabel.Text = "no NPC chosen";
                    ShowLines(null);
                    return;
                }
                TalkEditor ed = Editor();
                stepKeys.AddRange(t.Keys);
                stepGrid.RowCount = stepKeys.Count;
                var sides = new List<string>();
                bool inMod = false;
                foreach (TalkSide s in t.Sides) { sides.Add(s.Profile.Name); inMod |= s.InMod; }
                npcLabel.Text = "NPC " + t.NpcId + " " + NameOf(t.NpcId) + " (" + string.Join(", ", sides.ToArray()) + ")" + (inMod ? ", changed by the mod" : "");
                FillFlow(t);
                problems = ed.Problems(t, textIds);
                foreach (TalkProblem p in problems)
                {
                    TalkSide s = t.Side(p.Archive);
                    var it = new ListViewItem((s != null ? s.Profile.Name : p.Archive) + ": " + p);
                    it.ForeColor = p.Shipped ? SystemColors.GrayText : p.Error ? Color.Firebrick : SystemColors.WindowText;
                    problemList.Items.Add(it);
                }
                if (problemList.Items.Count == 0) problemList.Items.Add("no problem found");
                questList.Items.Add(new ListViewItem("Quest talk: lists the quests NPC " + t.NpcId + " starts or completes") { ForeColor = SystemColors.GrayText });
            }
            finally { filling = false; }
            FitListColumns();
            int at = keepStep != null ? stepKeys.IndexOf(keepStep) : -1;
            SelectStep(at >= 0 ? keepStep : stepKeys.Count > 0 ? stepKeys[0] : null);
        }

        void FillFlow(NpcTalk t)
        {
            TalkSide first = t.Sides.Count > 0 ? t.Sides[0] : null;
            if (first == null) return;
            var parents = new List<TreeNode>();
            foreach (TalkFlowLine f in TalkFlow.Of(first.Root))
            {
                string text = f.ToString().TrimStart();
                var n = new TreeNode(text) { Tag = f.Key };
                if (f.Dangling || f.Unreachable) n.ForeColor = Color.Firebrick;
                else if (f.Again) n.ForeColor = SystemColors.GrayText;
                while (parents.Count > f.Depth) parents.RemoveAt(parents.Count - 1);
                if (parents.Count == 0) flowTree.Nodes.Add(n);
                else parents[parents.Count - 1].Nodes.Add(n);
                parents.Add(n);
            }
            flowTree.ExpandAll();
            if (flowTree.Nodes.Count > 0) flowTree.Nodes[0].EnsureVisible();
        }

        string StepChosen()
        {
            if (stepGrid.CurrentCell == null) return null;
            int r = stepGrid.CurrentCell.RowIndex;
            return r >= 0 && r < stepKeys.Count ? stepKeys[r] : null;
        }

        void SelectStep(string key)
        {
            int at = key == null ? -1 : stepKeys.IndexOf(key);
            if (at >= 0)
            {
                bool f = filling;
                filling = true;
                try { WhereUsedJump.SelectCell(stepGrid, 1, at); }
                finally { filling = f; }
            }
            ShowStep();
        }

        void OnStepValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (talk == null || e.RowIndex < 0 || e.RowIndex >= stepKeys.Count || editor == null) return;
            string key = stepKeys[e.RowIndex];
            switch (e.ColumnIndex)
            {
                case 0: e.Value = TalkEditor.SameOnEverySide(talk, key) ? "" : "≠"; break;
                case 1: e.Value = key; break;
                case 2:
                    TalkStep s = FirstStep(key);
                    try { e.Value = s == null ? "" : editor.FirstLine(talk.NpcId, s, Language); }
                    catch (ModKitException ex) { e.Value = "(" + ex.Message + ")"; }
                    break;
            }
        }

        void OnStepFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (talk == null || e.RowIndex < 0 || e.RowIndex >= stepKeys.Count) return;
            if (e.ColumnIndex == 0) e.CellStyle.ForeColor = Color.Firebrick;
            string key = stepKeys[e.RowIndex];
            if (problems.Exists(p => !p.Shipped && p.Text.StartsWith(key, StringComparison.Ordinal) && (p.Text.Length == key.Length || p.Text[key.Length] == ' ' || p.Text[key.Length] == ':')))
                e.CellStyle.BackColor = Color.MistyRose;
        }

        TalkStep FirstStep(string key)
        {
            foreach (TalkSide s in talk.Sides) { TalkStep st = s.Step(key); if (st != null) return st; }
            return null;
        }

        string Describe()
        {
            if (talk == null) return "";
            var sb = new StringBuilder();
            sb.Append("NPC ").Append(talk.NpcId).Append(' ').Append(NameOf(talk.NpcId)).Append(": ").Append(stepKeys.Count).Append(" step(s)");
            foreach (TalkSide s in talk.Sides)
                sb.Append("\r\n").Append(s.Profile.Name).Append(": ").Append(s.Entry).Append(s.Combined ? " (" + TalkEditor.Selector(talk.NpcId) + ")" : "")
                  .Append(s.InMod ? s.Shipped ? ", changed by the mod" : ", added by the mod" : ", as shipped");
            int errors = problems.FindAll(p => !p.Shipped && p.Error).Count, warnings = problems.FindAll(p => !p.Shipped && !p.Error).Count;
            sb.Append("\r\n").Append(errors).Append(" error(s), ").Append(warnings).Append(" warning(s) of the mod's doing; ").Append(problems.Count - errors - warnings).Append(" the shipped talk has too");
            return sb.ToString();
        }

        // ---- the lines of a step ----

        void ShowStep()
        {
            ShowLines(StepChosen());
        }

        void ShowLines(string key)
        {
            lineGrid.CancelEdit();
            lines.Clear();
            lineGrid.RowCount = 0;
            if (talk == null || key == null)
            {
                stepLabel.Text = talk == null ? "" : "no step chosen";
                return;
            }
            TalkStep s = FirstStep(key);
            if (s == null) { stepLabel.Text = "no target has " + key; return; }
            var p = new TalkPath(key);
            lines.Add(new Line { Path = p, Label = key, Name = s.Kind });
            AddLines(s.Elem, p, 1);
            lineGrid.RowCount = lines.Count;
            var missing = new List<string>();
            foreach (TalkSide side in talk.Sides) if (side.Step(key) == null) missing.Add(side.Profile.Name);
            stepLabel.Text = key + (missing.Count > 0 ? ", not on the " + string.Join(", ", missing.ToArray()) : "")
                + (TalkEditor.SameOnEverySide(talk, key) ? "" : ", differs between the targets (shown: the " + FirstSide(key).Profile.Name + "'s)");
            lineGrid.Invalidate();
        }

        void AddLines(XmlElem e, TalkPath p, int depth)
        {
            var count = new Dictionary<string, int>();
            foreach (XmlElem c in e.Children)
            {
                int n;
                count.TryGetValue(c.Name, out n);
                count[c.Name] = n + 1;
                TalkPath cp = p.Then(c.Name, n);
                string label = c.Name == "content" ? "page " + (n + 1) : c.Name == "distractor" ? "choice " + (n + 1) : c.Name + " " + (n + 1);
                lines.Add(new Line { Path = cp, Depth = depth, Label = new string(' ', depth * 2) + label, Name = c.Name });
                AddLines(c, cp, depth + 1);
            }
        }

        TalkSide FirstSide(string key)
        {
            foreach (TalkSide s in talk.Sides) if (s.Step(key) != null) return s;
            return null;
        }

        Line LineAt(int row) { return row >= 0 && row < lines.Count && talk != null ? lines[row] : null; }

        XmlElem ElemOf(Line l)
        {
            foreach (TalkSide s in talk.Sides) { XmlElem e = l.Path.Resolve(s); if (e != null) return e; }
            return null;
        }

        static bool Named(string attr, bool step)
        {
            if (attr == "text" || Array.IndexOf(AttrColumns, attr) >= 0) return true;
            return step && (attr == "id" || attr == "feature" || attr == "locale");
        }

        static string More(XmlElem e, bool step)
        {
            var l = new List<string>();
            foreach (XmlAttr a in e.Attrs) if (!Named(a.Name, step)) l.Add(a.Name + "=" + a.Value);
            return string.Join("; ", l.ToArray());
        }

        void OnLineValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            Line l = LineAt(e.RowIndex);
            if (l == null || editor == null) return;
            XmlElem x = ElemOf(l);
            if (x == null) return;
            bool step = l.Path.Below.Count == 0;
            try
            {
                if (e.ColumnIndex == 0) e.Value = l.Label;
                else if (e.ColumnIndex == TextCol) e.Value = x.Attr("text") == null ? "" : editor.Say(talk.NpcId, x.Attr("text"), Language) ?? "(no " + Language + " row) " + x.Attr("text");
                else if (e.ColumnIndex == 2)
                {
                    long id;
                    e.Value = TalkEditor.TryRef(x.Attr("text"), out id) ? id.ToString(CultureInfo.InvariantCulture) : "";
                }
                else if (e.ColumnIndex >= FirstAttrCol && e.ColumnIndex < MoreCol) e.Value = x.Attr(AttrColumns[e.ColumnIndex - FirstAttrCol]) ?? "";
                else if (e.ColumnIndex == MoreCol) e.Value = More(x, step);
                else if (e.ColumnIndex == ButtonsCol) e.Value = TalkFlow.Buttons(x) ?? "";
            }
            catch (ModKitException ex) { e.Value = "(" + ex.Message + ")"; }
        }

        void OnLineFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            Line l = LineAt(e.RowIndex);
            if (l == null) return;
            if (l.Path.Below.Count == 0) e.CellStyle.Font = bold.Of(lineGrid.Font);
            if (e.ColumnIndex == TextCol)
            {
                XmlElem x = ElemOf(l);
                long id;
                if (x != null && TalkEditor.TryRef(x.Attr("text"), out id) && editor != null && Language != null)
                {
                    TalkText t = editor.Text(talk.NpcId, id, Language);
                    if (t == null) e.CellStyle.ForeColor = Color.Firebrick;
                    else if (t.Differs) e.CellStyle.BackColor = Color.LightYellow;
                }
            }
            if (e.ColumnIndex == 0 || e.ColumnIndex == 2) e.CellStyle.ForeColor = SystemColors.GrayText;
        }

        void OnLineValuePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            Line l = LineAt(e.RowIndex);
            if (l == null) return;
            TalkCellEdit edit = Typed(l, e.ColumnIndex, e.Value == null ? "" : e.Value.ToString());
            if (edit == null) return;
            // a second edit commits the first: one edit is held at a time
            if (typed.Pending) Guard(CommitTyped);
            typed.Post(edit);
            // the grid is still busy with the cell and the edit rebuilds the lines: commit it after the grid is done,
            // unless an NPC change, save, plan or apply commits it first (EndEdits)
            BeginInvoke((MethodInvoker)delegate { Guard(CommitTyped); });
        }

        /// <summary>The edit a value typed into a cell of the line makes, bound to the open NPC; null for none.</summary>
        TalkCellEdit Typed(Line l, int col, string v)
        {
            XmlElem x = ElemOf(l);
            if (x == null) return null;
            var edit = new TalkCellEdit { NpcId = talk.NpcId, Path = l.Path, Language = Language };
            if (col == TextCol)
            {
                // the cell of a line with no row in the language shows the id behind a note; the note is not the text
                string note = "(no " + Language + " row) ";
                if (v.StartsWith(note, StringComparison.Ordinal)) v = v.Substring(note.Length);
                if (x.Attr("text") != null && v == x.Attr("text") && editor.Say(talk.NpcId, v, Language) == null) return null;
                edit.Text = v;
            }
            else if (col >= FirstAttrCol && col < MoreCol)
            {
                v = v.Trim();
                edit.Attrs.Add(new KeyValuePair<string, string>(AttrColumns[col - FirstAttrCol], v.Length == 0 ? null : v));
            }
            else if (col == MoreCol)
            {
                bool step = l.Path.Below.Count == 0;
                var want = new Dictionary<string, string>();
                var order = new List<string>();
                foreach (string part in v.Split(';'))
                {
                    string p = part.Trim();
                    if (p.Length == 0) continue;
                    int eq = p.IndexOf('=');
                    if (eq <= 0) throw new ModKitException("\"" + p + "\" is no name=value");
                    string name = p.Substring(0, eq).Trim(), value = p.Substring(eq + 1).Trim();
                    if (Named(name, step)) throw new ModKitException(name + " has a column of its own" + (step && (name == "id" || name == "feature" || name == "locale") ? " (the step's key: add a new step instead)" : ""));
                    if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_.-]*$")) throw new ModKitException("\"" + name + "\" is no attribute name");
                    want[name] = value;
                    order.Add(name);
                }
                foreach (XmlAttr a in x.Attrs)
                    if (!Named(a.Name, step) && !want.ContainsKey(a.Name)) edit.Attrs.Add(new KeyValuePair<string, string>(a.Name, null));
                foreach (string name in order) edit.Attrs.Add(new KeyValuePair<string, string>(name, want[name]));
            }
            else return null;
            return edit;
        }

        void Edited(NpcTalk t, string said, List<string> missed)
        {
            infos.Remove(t.NpcId);
            host.EditedOutside();
            ShowCount(Editor().ModNpcIds());
            var sb = new StringBuilder(said);
            if (missed != null && missed.Count > 0) sb.Append("\r\nnot changed on: ").Append(string.Join("; ", missed.ToArray())).Append(" (a side named alone has no such place)");
            if (talk == null || talk.NpcId != t.NpcId)
            {
                // an edit typed for the NPC open before: the NPC being opened stays on screen
                npcGrid.Invalidate();
                host.SayOutside(saidLast = sb.ToString());
                return;
            }
            // EditedOutside tells the other views; this one shows the talk as it is now
            string step = StepChosen();
            int row = lineGrid.CurrentCell != null ? lineGrid.CurrentCell.RowIndex : -1, col = lineGrid.CurrentCell != null ? lineGrid.CurrentCell.ColumnIndex : 0;
            if (hits.IndexOf(t.NpcId) < 0) FillHits(t.NpcId);
            npcGrid.Invalidate();
            ShowTalk(t);
            if (step != null) SelectStep(step);
            if (row >= 0 && row < lineGrid.RowCount) WhereUsedJump.SelectCell(lineGrid, col, row);
            saidLast = sb.ToString();
            sb.Append("\r\n\r\n").Append(Describe());
            host.SayOutside(sb.ToString());
        }

        NpcTalk Talk()
        {
            if (talk == null) throw new ModKitException("choose an NPC first");
            return talk;
        }

        Line ChosenLine()
        {
            return lineGrid.CurrentCell == null ? null : LineAt(lineGrid.CurrentCell.RowIndex);
        }

        void AddStep()
        {
            NpcTalk t = Talk();
            EndEdits();
            string kind = kindBox.SelectedItem as string ?? "script";
            string id = stepIdBox.Text.Trim();
            if (id.Length == 0)
            {
                string at = StepChosen();
                TalkStep s = at == null ? null : FirstStep(at);
                long near, n;
                if (s == null || !long.TryParse(s.Id, NumberStyles.None, CultureInfo.InvariantCulture, out near)) near = 10;
                n = TalkEditor.NextStepId(t, near);
                if (n < 0) throw new ModKitException("the id class of step " + near + " is full; type an id");
                id = n.ToString(CultureInfo.InvariantCulture);
            }
            var missed = new List<string>();
            t = Editor().AddStep(t, kind, id, missed);
            stepIdBox.Text = "";
            Edited(t, kind + " " + id + " added", missed);
            SelectStep(TalkStep.KeyOf(kind, id, "", ""));
        }

        void RemoveStep()
        {
            string key = StepChosen();
            if (key == null) throw new ModKitException("choose a step first");
            EndEdits();
            var missed = new List<string>();
            Edited(Editor().Remove(Talk(), new TalkPath(key), missed), key + " removed", missed);
        }

        void RevertNpc()
        {
            NpcTalk t = Talk();
            EndEdits();
            Editor().Revert(t.NpcId);
            NpcTalk now = Editor().Open(t.NpcId);
            if (now.Sides.Count == 0)
            {
                infos.Remove(t.NpcId);
                host.EditedOutside();
                FillHits(-1);
                host.SayOutside("NPC " + t.NpcId + " talk taken out of the mod; no target ships one");
                return;
            }
            Edited(now, "NPC " + t.NpcId + " talk taken out of the mod on every target", null);
        }

        void AddPage()
        {
            string key = StepChosen();
            if (key == null) throw new ModKitException("choose a step first");
            EndEdits();
            TalkStep s = FirstStep(key);
            int pages = 0;
            foreach (XmlElem c in s.Elem.Children) if (c.Name == "content") pages++;
            var missed = new List<string>();
            NpcTalk t = Editor().Insert(Talk(), new TalkPath(key), pages > 0 ? new KeyValuePair<string, int>("content", pages - 1) : (KeyValuePair<string, int>?)null, "<content text=\"\" />", missed);
            Edited(t, key + ": page " + (pages + 1) + " added", missed);
        }

        void AddChoice()
        {
            Line l = ChosenLine();
            while (l != null && l.Name != "content")
            {
                // a choice's page: the path without its last part
                if (l.Path.Below.Count < 2) { l = null; break; }
                var up = new TalkPath(l.Path.Step);
                for (int i = 0; i < l.Path.Below.Count - 1; i++) up = up.Then(l.Path.Below[i].Key, l.Path.Below[i].Value);
                l = lines.Find(x => x.Path.ToString() == up.ToString());
            }
            if (l == null) throw new ModKitException("choose a page (or a choice of it) first");
            EndEdits();
            XmlElem page = ElemOf(l);
            int n = 0, lastDistractor = -1;
            foreach (XmlElem c in page.Children) if (c.Name == "distractor") lastDistractor = n++;
            var missed = new List<string>();
            NpcTalk t = Editor().Insert(Talk(), l.Path, lastDistractor >= 0 ? new KeyValuePair<string, int>("distractor", lastDistractor) : (KeyValuePair<string, int>?)null, "<distractor text=\"\" goto=\"\" />", missed);
            Edited(t, l.Path + ": choice " + (n + 1) + " added", missed);
        }

        void RemoveLine()
        {
            Line l = ChosenLine();
            if (l == null || l.Path.Below.Count == 0) throw new ModKitException("choose a page, choice or event first (Remove step removes a step)");
            EndEdits();
            var missed = new List<string>();
            Edited(Editor().Remove(Talk(), l.Path, missed), l.Path + " removed", missed);
        }

        void MoveLine(int by)
        {
            Line l = ChosenLine();
            if (l == null || l.Path.Below.Count == 0) throw new ModKitException("choose a page, choice or event first (a step does not move)");
            EndEdits();
            int col = lineGrid.CurrentCell != null ? lineGrid.CurrentCell.ColumnIndex : 0;
            var to = new TalkPath(l.Path.Step);
            for (int i = 0; i < l.Path.Below.Count; i++)
            {
                KeyValuePair<string, int> b = l.Path.Below[i];
                to = to.Then(b.Key, i == l.Path.Below.Count - 1 ? b.Value + by : b.Value);
            }
            var missed = new List<string>();
            Edited(Editor().Move(Talk(), l.Path, by, missed), l.Path + " moved " + (by < 0 ? "up" : "down"), missed);
            // the moved line keeps the selection
            string want = to.ToString();
            for (int r = 0; r < lineGrid.RowCount; r++)
            {
                Line x = LineAt(r);
                if (x != null && x.Path.ToString() == want) { WhereUsedJump.SelectCell(lineGrid, col, r); break; }
            }
        }

        bool IsJumpCell(DataGridViewCell c)
        {
            if (c == null || LineAt(c.RowIndex) == null) return false;
            int a = c.ColumnIndex - FirstAttrCol;
            return a >= 0 && a < AttrColumns.Length && (AttrColumns[a] == "goto" || AttrColumns[a] == "gotoFail");
        }

        void PickStep()
        {
            DataGridViewCell cell = lineGrid.CurrentCell;
            if (!IsJumpCell(cell)) throw new ModKitException("choose a goto or gotoFail cell first");
            EndEdits();
            Line l = LineAt(cell.RowIndex);
            string attr = AttrColumns[cell.ColumnIndex - FirstAttrCol];
            NpcTalk t = Talk();
            pickMenu.Items.Clear();
            foreach (string key in stepKeys)
            {
                TalkStep s = FirstStep(key);
                string first = editor.FirstLine(t.NpcId, s, Language);
                if (first.Length > 50) first = first.Substring(0, 50) + "…";
                string id = s.Id;
                pickMenu.Items.Add(key + (first.Length > 0 ? "  " + first : ""), null, delegate
                {
                    Guard(delegate
                    {
                        var edit = new TalkCellEdit { NpcId = t.NpcId, Path = l.Path, Language = Language };
                        edit.Attrs.Add(new KeyValuePair<string, string>(attr, id));
                        typed.Post(edit);
                        CommitTyped();
                    });
                });
            }
            long near;
            TalkStep at = FirstStep(l.Path.Step);
            if (at == null || !long.TryParse(at.Id, NumberStyles.None, CultureInfo.InvariantCulture, out near)) near = 10;
            long n = TalkEditor.NextStepId(t, near);
            if (n >= 0)
            {
                pickMenu.Items.Add(new ToolStripSeparator());
                pickMenu.Items.Add("new script " + n + " (with one empty page)", null, delegate
                {
                    Guard(delegate
                    {
                        var missed = new List<string>();
                        NpcTalk now = Editor().AddStep(talk != null && talk.NpcId == t.NpcId ? talk : Editor().Open(t.NpcId), "script", n.ToString(CultureInfo.InvariantCulture), missed);
                        string said = "script " + n + " added";
                        // the step is in the mod now: the view and the Tables view are told even when the jump fails
                        try
                        {
                            now = Editor().SetAttr(now, l.Path, attr, n.ToString(CultureInfo.InvariantCulture), missed);
                            said += "; " + l.Path + ": " + attr + " = " + n;
                        }
                        catch (ModKitException ex) { said += "; " + l.Path + ": " + attr + " not set: " + ex.Message; }
                        Edited(now, said, missed);
                    });
                });
            }
            Rectangle r = lineGrid.GetCellDisplayRectangle(cell.ColumnIndex, cell.RowIndex, false);
            pickMenu.Show(lineGrid, new Point(r.Left, r.Bottom));
        }

        // ---- quest talk, read only ----

        void QuestTalk()
        {
            NpcTalk t = Talk();
            EditorSession session = host.SharedSession();
            long npc = t.NpcId;
            List<UseHit> found = null;
            var talkLines = new Dictionary<long, List<QuestTalkLine>>();
            host.RunOutside("Quests of NPC " + npc.ToString(CultureInfo.InvariantCulture), delegate
            {
                found = new WhereUsed(session).Of(IdLookup.Npc, npc, 500).FindAll(h => h.Kind == IdLookup.Quest && (h.Field == "start@npc" || h.Field == "complete@npc"));
                QuestEditor q = session.Quests();
                foreach (UseHit h in found) if (!talkLines.ContainsKey(h.Id)) talkLines[h.Id] = q.HasData ? q.TalkLines(h.Id) : new List<QuestTalkLine>();
            }, delegate
            {
                if (IsDisposed || talk == null || talk.NpcId != npc) return;
                questHits.Clear();
                questList.Items.Clear();
                foreach (UseHit h in found)
                {
                    questHits.Add(h);
                    var head = new ListViewItem("quest " + h.Id + "  " + h.Name + "  (" + (h.Field == "start@npc" ? "starts here" : "completes here") + ")") { Tag = h };
                    head.Font = bold.Of(questList.Font);
                    questList.Items.Add(head);
                    foreach (QuestTalkLine ql in talkLines[h.Id])
                        questList.Items.Add(new ListViewItem("    script " + ql.Script + " " + ql.Element + ": " + (ql.Text ?? "$script:" + ql.TextId + "$")) { Tag = h, ForeColor = SystemColors.GrayText });
                }
                if (found.Count == 0) questList.Items.Add("no quest starts or completes at NPC " + npc);
                FitListColumns();
                host.SayOutside(found.Count + " quest(s) start or complete at NPC " + npc + "; their talk is read only here, double-click one to edit it in the MS2 Quests view");
            });
        }

        void OpenQuest()
        {
            if (questList.SelectedItems.Count == 0) return;
            var h = questList.SelectedItems[0].Tag as UseHit;
            if (h == null) return;
            WhereUsedJump.Jump(h, talk != null ? talk.NpcId : 0);
        }
    }
}
