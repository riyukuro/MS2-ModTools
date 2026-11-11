// The "MS2 Quests" view: one quest at a time, in the style of the MS2 Items and MS2 NPCs views. A search box (id or
// name), the list of hits, and three grids that share the whole view: the fields and texts (level requirement, start
// and complete NPC, reward exp and meso, the texts of every language), the conditions (type, code, count), and the
// item lists of the complete reward and of the accept reward. Clone to new id (with the talk script and the rows of
// the tables that list the quest, when the box is ticked), and its own Plan / Apply / Revert buttons and output box.
//
// A value set here is written to every place of the quest at once: the kms-client's table row and quest file, the
// quest files of the server and the gms-client, and the text rows of every target.
//
// The view holds no data logic and no mod of its own. MS2.ModKit.QuestEditor turns what is done here into operations
// of the mod the MS2 Tables view is editing; the session, the worker thread (Plan / Apply / Revert), the output text
// and the close question stay in that view (TablesView, "for the other views of the plugin").
using System;
using System.Text;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Emergent.Toolbench;
using Emergent.Toolbench.UI.Workbench;
using MS2.ModKit;

namespace MS2.Tables
{
    public class QuestsView : AbstractView
    {
        public const string ViewId = "MS2.Tables.QuestsView";
        const int MaxHits = 500;

        static readonly string[][] Fields =
        {
            new[] { "require@level", "Level requirement" },
            new[] { "start@npc", "Start NPC" },
            new[] { "complete@npc", "Complete NPC" },
            new[] { "completeReward@exp", "Reward exp" },
            new[] { "completeReward@money", "Reward meso" },
        };

        static readonly string[][] TextNames =
        {
            new[] { "name", "Name" },
            new[] { "desc", "Description" },
            new[] { "manual", "Goals ('|' between conditions)" },
            new[] { "complete", "Completion text" },
        };

        /// <summary>One line of a grid: a heading, or cells with the shipped cells to compare with.</summary>
        sealed class Line
        {
            internal string Heading;
            internal string[] Cells;
            internal string[] Shipped;          // null: nothing to compare with (a new quest)
            internal bool[] Editable;
            internal string Field, Language;    // the fields grid
            internal long Talk;                 // the fields grid: the text id of a talk text line
            internal string Reward, List;       // the rewards grid: the reward element and its list
            internal int Index;                 // rewards and conditions
            internal bool Changed
            {
                get
                {
                    if (Shipped == null || Heading != null) return false;
                    for (int i = 0; i < Cells.Length; i++)
                        if (Editable[i] && (Cells[i] ?? "") != (Shipped[i] ?? "")) return true;
                    return false;
                }
            }
        }

        TablesView host;
        QuestEditor editor, shippedEditor;
        QuestDraft draft, shipped;
        bool filling;

        readonly List<Line> fieldLines = new List<Line>(), conditionLines = new List<Line>(), rewardLines = new List<Line>();
        readonly TextBox searchBox = new TextBox { Width = 200 };
        readonly ComboBox languageBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };
        readonly Label hitsLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListBox hitList = new ListBox { Dock = DockStyle.Left, Width = 280, IntegralHeight = false };
        readonly Label questLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 8, 0) };
        readonly TextBox cloneIdBox = new TextBox { Width = 80 };
        readonly CheckBox listedBox = new CheckBox { Text = "with talk script and list rows", AutoSize = true, Checked = true, Margin = new Padding(4, 6, 3, 0) };
        readonly CheckBox allLanguagesBox = new CheckBox { Text = "texts to every language", AutoSize = true, Margin = new Padding(8, 6, 3, 0) };
        readonly DataGridView fieldGrid = new DataGridView { Dock = DockStyle.Fill };
        readonly DataGridView conditionGrid = new DataGridView { Dock = DockStyle.Fill };
        readonly DataGridView rewardGrid = new DataGridView { Dock = DockStyle.Fill };
        readonly ComboBox listBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        readonly TextBox itemBox = new TextBox { Width = 80 };
        readonly TextBox rankBox = new TextBox { Width = 30, Text = "1" };
        readonly TextBox countBox = new TextBox { Width = 50, Text = "1" };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Bottom, Height = 130 };
        readonly ComboBox targetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();

        public QuestsView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(Lbl("Quest id or name"));
            top.Controls.Add(searchBox);
            top.Controls.Add(Btn("Find", "Lists the quests whose id or name contains the text; the quests the mod adds come first. The list shows the shipped names", delegate { Find(); }));
            top.Controls.Add(Lbl("Names in"));
            top.Controls.Add(languageBox);
            tips.SetToolTip(languageBox, "The language folder (string/<language>) whose quest names are searched and shown in the list");
            top.Controls.Add(hitsLabel);

            FlowLayoutPanel applyBar = Bar();
            applyBar.Controls.Add(Lbl("Apply to"));
            targetBox.Items.Add("workspace (patched archives)");
            targetBox.Items.Add("game folder (loose files)");
            tips.SetToolTip(targetBox, "Where Apply writes the client's tables (the same choice as in the MS2 Tables view): patched copies of the archives in the workspace folder, or plain files under Data/ of the game folder. The archives of the server and of the global client (gms-client) always go to the workspace.");
            applyBar.Controls.Add(targetBox);
            applyBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change, entry by entry for each target (kms-client, gms-client, server); writes nothing", delegate { return host.DoPlan(); }));
            applyBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change, to every target at once", delegate { return host.DoApply(); }));
            applyBar.Controls.Add(Job("Revert", "Removes what Apply wrote, on every target", delegate { return host.DoRevert(); }));

            FlowLayoutPanel questBar = Bar();
            questBar.Controls.Add(questLabel);
            questBar.Controls.Add(Btn("Clone to new id", "Adds a new quest that starts as a copy of this one as shipped (edits the mod makes to this quest are not copied), under the id in the box (empty = the next free id after this quest). The copy has the table row, the quest file and the text rows of every language of the source, on every target", delegate { Clone(); }));
            questBar.Controls.Add(cloneIdBox);
            questBar.Controls.Add(listedBox);
            tips.SetToolTip(listedBox, "Ticked: the clone also gets a copy of the source quest's talk script (what the NPC says when it offers the quest) and of its rows in the tables that list quests: the fields of the quest, the special type name, the server's script conditions and script functions. The talk texts themselves are shared with the source quest. Unticked: only the table row, the quest file and the text rows");
            questBar.Controls.Add(Btn("Own talk texts", "Gives the chosen new quest talk texts of its own: every text its copied talk script names is copied under a new text id (the quest id followed by 001, 002, ...) in every language, and the script names the new ids. The texts can then be edited in the grid without changing what the source quest says", delegate { OwnTalk(true); }));
            questBar.Controls.Add(Btn("Shared talk texts", "Takes the chosen new quest's own talk texts out of the mod again: it speaks the source quest's lines, as right after the clone", delegate { OwnTalk(false); }));
            questBar.Controls.Add(Btn("Remove clone", "Takes the chosen new quest out of the mod again, with every row and file the clone added", delegate { RemoveClone(); }));
            questBar.Controls.Add(allLanguagesBox);
            tips.SetToolTip(allLanguagesBox, "Ticked: a text typed in the grid is written to the quest's row in every language. Unticked: to the language of the grid line only, on every target that has it");
            questBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same mod and the same Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            Setup(fieldGrid, fieldLines, new[] { "field", "value", "shipped value", "where" }, new[] { 130, 150, 110, 100 }, SetField);
            Setup(conditionGrid, conditionLines, new[] { "#", "type", "code", "count", "shipped", "code is", "where" }, new[] { 28, 80, 70, 50, 90, 90, 80 }, SetCondition);
            Setup(rewardGrid, rewardLines, new[] { "#", "item id", "rank", "count", "shipped", "where" }, new[] { 28, 90, 40, 60, 110, 100 }, SetReward);

            FlowLayoutPanel rewardBar = Bar();
            rewardBar.Controls.Add(Lbl("List"));
            rewardBar.Controls.Add(listBox);
            tips.SetToolTip(listBox, "The reward list the buttons beside it work on; \"accept:\" lists are what the quest gives when it is accepted, the others when it is completed. v: the kms-client's table (what the Korean client shows). essentialItem and globalEssentialItem: the quest files (the server gives globalEssentialItem when it is not empty, else essentialItem). The two job lists: per job");
            rewardBar.Controls.Add(Lbl("item"));
            rewardBar.Controls.Add(itemBox);
            rewardBar.Controls.Add(Lbl("rank"));
            rewardBar.Controls.Add(rankBox);
            rewardBar.Controls.Add(Lbl("count"));
            rewardBar.Controls.Add(countBox);
            rewardBar.Controls.Add(Btn("Add row", "Adds the item in the boxes to the chosen list, on every target that has that list", delegate { AddReward(); }));
            rewardBar.Controls.Add(Btn("Remove row", "Removes the selected row of the grid from its list, on every target that has that row", delegate { RemoveReward(); }));
            rewardBar.Controls.Add(Btn("Same on every target", "Writes the rows of the chosen list to v, essentialItem and globalEssentialItem on every target, so the client shows what the server gives. The two job lists and the accept reward stay as they are", delegate { SameEverywhere(); }));

            // the three grids share the whole area: fields and texts at the left, conditions over rewards at the right
            var conditionPane = Pane("Conditions (count: empty or 0 = once; a code that is another table's id is read-only)", null, conditionGrid);
            var rewardPane = Pane("Reward items", rewardBar, rewardGrid);
            var fieldPane = Pane("Fields and texts", null, fieldGrid);
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
            table.Controls.Add(fieldPane, 0, 0);
            table.SetRowSpan(fieldPane, 2);
            table.Controls.Add(conditionPane, 1, 0);
            table.Controls.Add(rewardPane, 1, 1);

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(table);
            right.Controls.Add(questBar);

            Controls.Add(right);
            Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
            Controls.Add(hitList);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(outputBox);
            Controls.Add(applyBar);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, applyBar, hitList, right });
            ViewLayout.KeepLogLines(outputBox, this);
            ViewLayout.CentreOnFirstShow(this);
            targetBox.SelectedIndexChanged += delegate { if (!filling && host != null) host.TargetIndex = targetBox.SelectedIndex; };

            searchBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                Guard(delegate { Find(); });
            };
            hitList.SelectedIndexChanged += delegate { if (!filling) Guard(delegate { OpenSelected(); }); };
            languageBox.SelectedIndexChanged += delegate
            {
                if (filling) return;
                Guard(delegate { Editor().Language = (string)languageBox.SelectedItem; Find(); });
            };
        }

        public override void Initialize()
        {
            base.Initialize();
            Guard(delegate { Find(); });
        }

        readonly BoldFont bold = new BoldFont();   // the heading lines of the grids

        protected override void Dispose(bool disposing)
        {
            if (disposing) { Hook(null); bold.Dispose(); }
            base.Dispose(disposing);
        }

        // ---- small builders ----

        static FlowLayoutPanel Bar()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(2) };
        }

        static Label Lbl(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(6, 8, 0, 0) }; }

        static Panel Pane(string caption, Control bar, DataGridView grid)
        {
            var p = new Panel { Dock = DockStyle.Fill, Margin = new Padding(1) };
            p.Controls.Add(grid);
            if (bar != null) p.Controls.Add(bar);
            p.Controls.Add(new Label { Text = caption, Dock = DockStyle.Top, Height = 18, Padding = new Padding(4, 2, 0, 0), Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) });
            return p;
        }

        Button Btn(string text, string tip, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, UseVisualStyleBackColor = true, ForeColor = SystemColors.ControlText };
            b.Click += delegate(object sender, EventArgs e) { Guard(delegate { click(sender, e); }); };
            tips.SetToolTip(b, tip);
            return b;
        }

        /// <summary>A button for Plan, Apply or Revert: the command starts a job of the Tables view and returns at once.</summary>
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

        /// <summary>A grid over a list of lines: the cells a line marks editable are typed into, and set(line, column, text) writes them.</summary>
        void Setup(DataGridView grid, List<Line> lines, string[] heads, int[] widths, Action<Line, int, string> set)
        {
            grid.VirtualMode = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToOrderColumns = false;
            grid.RowHeadersVisible = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            grid.BackgroundColor = SystemColors.AppWorkspace;
            grid.DefaultCellStyle.BackColor = SystemColors.Window;
            grid.DefaultCellStyle.ForeColor = SystemColors.WindowText;
            for (int i = 0; i < heads.Length; i++)
                grid.Columns.Add(ViewLayout.FitColumn(new DataGridViewTextBoxColumn { HeaderText = heads[i], Width = widths[i], SortMode = DataGridViewColumnSortMode.NotSortable }));
            grid.CellValueNeeded += delegate(object sender, DataGridViewCellValueEventArgs e)
            {
                Line l = At(lines, e.RowIndex);
                if (l == null) return;
                if (l.Heading != null) e.Value = "";   // a heading is drawn over the whole line (RowPostPaint)
                else if (e.ColumnIndex == 0) e.Value = (l.Changed ? "* " : "") + l.Cells[0];
                else e.Value = l.Cells[e.ColumnIndex];
            };
            grid.CellToolTipTextNeeded += delegate(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
            {
                Line l = At(lines, e.RowIndex);
                if (l == null || e.ColumnIndex < 0) return;
                e.ToolTipText = l.Heading ?? l.Cells[e.ColumnIndex] ?? "";
                if (grid == conditionGrid && l.Heading == null && e.ColumnIndex == 1 && editor != null)
                    e.ToolTipText = "The types the shipped quests use: " + string.Join(", ", editor.ConditionTypes().ToArray()) + ". A new type must take the same kind of code as the old one";
            };
            grid.RowPostPaint += delegate(object sender, DataGridViewRowPostPaintEventArgs e)
            {
                Line l = At(lines, e.RowIndex);
                if (l == null || l.Heading == null) return;
                var r = new Rectangle(e.RowBounds.Left, e.RowBounds.Top, Math.Min(e.RowBounds.Width, grid.Columns.GetColumnsWidth(DataGridViewElementStates.Visible)) - 1, e.RowBounds.Height - 1);
                e.Graphics.FillRectangle(SystemBrushes.Control, r);
                TextRenderer.DrawText(e.Graphics, l.Heading, bold.Of(grid.Font), r, SystemColors.ControlText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            grid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e)
            {
                Line l = At(lines, e.RowIndex);
                if (l == null || l.Heading != null || !l.Editable[e.ColumnIndex]) e.Cancel = true;
            };
            grid.CellValuePushed += delegate(object sender, DataGridViewCellValueEventArgs e)
            {
                Line l = At(lines, e.RowIndex);
                if (l == null || l.Heading != null || !l.Editable[e.ColumnIndex] || draft == null) return;
                string value = e.Value == null ? "" : e.Value.ToString();
                if (value == (l.Cells[e.ColumnIndex] ?? "")) return;
                int column = e.ColumnIndex;
                Guard(delegate { set(l, column, value); });
            };
            grid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e)
            {
                Line l = At(lines, e.RowIndex);
                if (l == null) return;
                if (l.Heading != null)
                {
                    e.CellStyle.BackColor = SystemColors.Control;
                    e.CellStyle.ForeColor = SystemColors.ControlText;
                    e.CellStyle.Font = bold.Of(grid.Font);
                }
                else if (!l.Editable[e.ColumnIndex]) e.CellStyle.ForeColor = e.ColumnIndex == 0 ? SystemColors.WindowText : Color.Gray;
                else if (l.Changed) e.CellStyle.BackColor = Color.FromArgb(255, 236, 170);
                else if (draft != null && draft.IsNew) e.CellStyle.BackColor = Color.FromArgb(214, 240, 214);
            };
        }

        static Line At(List<Line> lines, int index) { return index >= 0 && index < lines.Count ? lines[index] : null; }

        void EndEdits() { fieldGrid.EndEdit(); conditionGrid.EndEdit(); rewardGrid.EndEdit(); }
        void CancelEdits() { fieldGrid.CancelEdit(); conditionGrid.CancelEdit(); rewardGrid.CancelEdit(); }

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

        // ---- the MS2 Tables view: session, mod, worker thread, output, close question ----

        void Hook(TablesView h)
        {
            if (h == host) return;
            if (host != null)
            {
                host.Changed -= OnHostChanged;
                host.Flushing -= OnHostFlushing;
                host.BusyChanged -= OnHostBusy;
                host.Said -= OnHostSaid;
                host.TargetChanged -= OnHostTarget;
            }
            host = h;
            editor = shippedEditor = null;
            if (h == null) return;
            h.Changed += OnHostChanged;
            h.Flushing += OnHostFlushing;
            h.BusyChanged += OnHostBusy;
            h.Said += OnHostSaid;
            h.TargetChanged += OnHostTarget;
            OnHostTarget();
            OutputWrap.Set(outputBox, h.OutputText);
            OnHostBusy(h.Busy);
        }

        /// <summary>Runs one command of this view; what goes wrong is shown in the output of both views.</summary>
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
                if (h == null) { OutputWrap.Set(outputBox, "the MS2 Tables view could not be opened; the quest editor works on the mod of that view"); return false; }
                Hook(h);
            }
            catch (Exception ex)
            {
                OutputWrap.Set(outputBox, "the MS2 Tables view could not be opened: " + ex.Message);
                Logger.Warning("MS2 Quests: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        QuestEditor Editor()
        {
            EditorSession s = host.SharedSession();
            if (editor == null || editor.Mod != s.Mod || editor.Data != s.Data)
            {
                string language = editor != null ? editor.Language : null;
                if (shippedEditor == null || shippedEditor.Data != s.Data) shippedEditor = new QuestEditor(s.Data, new Mod { Id = "shipped", Name = "shipped" });
                editor = s.Quests();
                var languages = new List<string>();
                foreach (string side in editor.Sides())
                    foreach (string l in editor.LanguagesIn(side))
                        if (!languages.Contains(l)) languages.Add(l);
                if (language != null && languages.Contains(language)) editor.Language = language;
                else if (!languages.Contains(editor.Language) && languages.Count > 0) editor.Language = languages[0];
                shippedEditor.Language = editor.Language;
                filling = true;
                languageBox.Items.Clear();
                foreach (string l in languages) languageBox.Items.Add(l);
                languageBox.SelectedItem = editor.Language;
                filling = false;
            }
            return editor;
        }

        void OnHostSaid(string text) { OutputWrap.Set(outputBox, text); }

        void OnHostBusy(bool on)
        {
            foreach (Control c in lockable) c.Enabled = !on;
            UseWaitCursor = on;
        }

        void OnHostFlushing() { EndEdits(); }

        void OnHostChanged()
        {
            CancelEdits();
            long id = draft != null ? draft.Id : 0;
            ShowQuest(null);
            try
            {
                FillHits(id);
                if (id != 0 && SelectedHit() != null) ShowQuest(Editor().Open(id));
            }
            catch (ModKitException) { ShowQuest(null); }
            catch (System.IO.IOException) { ShowQuest(null); }
        }

        /// <summary>After an edit: the quest is read again (rows may have come or gone) and the Tables view is told.</summary>
        void Edited(string said)
        {
            long id = draft.Id;
            host.EditedOutside();
            ShowQuest(Editor().Open(id));
            host.SayOutside(said);
        }

        // ---- search ----

        void Find()
        {
            EndEdits();
            FillHits(draft != null ? draft.Id : 0);
            if (hitList.Items.Count > 0 && hitList.SelectedIndex < 0) hitList.SelectedIndex = 0;
        }

        void FillHits(long keep)
        {
            QuestEditor ed = Editor();
            if (!ed.HasData)
            {
                hitList.Items.Clear();
                hitsLabel.Text = "no data folder has quest data";
                return;
            }
            List<QuestHit> hits = ed.Find(searchBox.Text, MaxHits);
            filling = true;
            try
            {
                hitList.BeginUpdate();
                hitList.Items.Clear();
                foreach (QuestHit h in hits)
                {
                    hitList.Items.Add(h);
                    if (h.Id == keep) hitList.SelectedIndex = hitList.Items.Count - 1;
                }
                hitList.EndUpdate();
            }
            finally { filling = false; }
            hitsLabel.Text = hits.Count + (hits.Count >= MaxHits ? "+" : "") + " quest(s)";
        }

        QuestHit SelectedHit() { return hitList.SelectedItem as QuestHit; }

        void OpenSelected()
        {
            QuestHit h = SelectedHit();
            if (h == null) return;
            EndEdits();
            ShowQuest(Editor().Open(h.Id));
            host.SayOutside(Describe(draft));
        }

        /// <summary>Where the quest is, per target; for a new quest also every place its id is registered.</summary>
        string Describe(QuestDraft d)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("quest ").Append(d.Id).Append(d.IsNew ? " (new in mod " + editor.Mod.Id + ")" : "").Append(": ").Append(d.Places.Count).Append(" place(s)");
            foreach (QuestPlace p in d.Places)
                sb.Append("\r\n  ").Append(p.Kind == QuestPlace.Text ? "text  " : p.Kind == QuestPlace.Table ? "table " : "file  ").Append(p.Label)
                  .Append(p.Kind == QuestPlace.Text ? "  (" + p.Language + ")" : "").Append(p.IsNew ? "  (new)" : "");
            if (d.IsNew)
            {
                sb.Append("\r\n\r\nA new quest id is written to:");
                foreach (string l in editor.Registration(d.Id)) sb.Append("\r\n  ").Append(l);
                int own = editor.OwnTalkTextCount(d.Id);
                sb.Append(own > 0
                    ? "\r\nThe talk script rows are copies, and " + own + " talk text(s) are this quest's own (edit them under Talk texts)."
                    : editor.ListedRows(d.Id).Count > 0
                    ? "\r\nThe talk script rows are copies; the talk texts they name are shared with the source quest (Own talk texts makes copies to edit)."
                    : "\r\nThe talk script and the tables that list quests have no row for this quest (none copied, or the source had none); add them in the MS2 Tables view.");
            }
            return sb.ToString();
        }

        // ---- the quest ----

        QuestDraft Draft()
        {
            if (draft == null) throw new ModKitException("choose a quest in the list first");
            return draft;
        }

        static string Short(QuestPlace p)
        {
            return p.Target + (p.Kind == QuestPlace.Table ? " table" : p.Kind == QuestPlace.File ? " file" : "") + (p.Feature.Length > 0 ? " [" + p.Feature + "]" : "");
        }

        /// <summary>"3 place(s)", or what each place of the list holds for the row when they do not agree ("-": no such row).</summary>
        static string RewardWhere(QuestReward w)
        {
            if (!w.Differs) return w.Places + " place(s)";
            var sb = new StringBuilder("differs: ");
            for (int i = 0; i < w.In.Count; i++)
                sb.Append(i > 0 ? "; " : "").Append(Short(w.In[i])).Append(" = ").Append(w.Values[i] ?? "-");
            return sb.ToString();
        }

        /// <summary>"3 place(s)", or each place with its value when they do not agree.</summary>
        static string Where(QuestDraft d, string field)
        {
            List<QuestPlace> places = d.PlacesOf(QuestPlace.Table);
            places.AddRange(d.PlacesOf(QuestPlace.File));
            if (!d.Differs(field)) return places.Count + " place(s)";
            var parts = new List<string>();
            foreach (QuestPlace p in places) parts.Add(Short(p) + " = " + (d.Get(p, field) ?? "-"));
            return "differs: " + string.Join("; ", parts.ToArray());
        }

        static string TextOf(QuestDraft d, string language, string field, out int rows, out bool differs)
        {
            string first = null;
            rows = 0; differs = false;
            if (d == null) return null;
            foreach (QuestPlace p in d.PlacesOf(QuestPlace.Text))
            {
                if (!string.Equals(p.Language, language, StringComparison.OrdinalIgnoreCase)) continue;
                string v = d.Text(p, field) ?? "";
                if (rows++ == 0) first = v; else if (v != first) differs = true;
            }
            return first;
        }

        const string AcceptPrefix = "accept: ";
        static string ListName(string reward, string list) { return (reward == QuestDraft.AcceptReward ? AcceptPrefix : "") + list; }

        static string ItemText(QuestReward w) { return w.Code + " x" + w.Count + " (rank " + w.Rank + ")"; }

        void ShowQuest(QuestDraft d)
        {
            CancelEdits();
            fieldGrid.RowCount = conditionGrid.RowCount = rewardGrid.RowCount = 0;
            fieldLines.Clear(); conditionLines.Clear(); rewardLines.Clear();
            draft = d;
            shipped = null;
            string keepList = listBox.SelectedItem as string;
            listBox.Items.Clear();
            if (d == null)
            {
                questLabel.Text = "(no quest)";
                cloneIdBox.Text = "";
                return;
            }
            if (!d.IsNew)
            {
                try { shippedEditor.Language = editor.Language; shipped = shippedEditor.Open(d.Id); }
                catch (ModKitException) { }
            }

            fieldLines.Add(new Line { Heading = "Fields (written to every place of the quest)" });
            foreach (string[] f in Fields)
                fieldLines.Add(new Line
                {
                    Field = f[0],
                    Cells = new[] { f[1], d.Get(f[0]) ?? "", shipped != null ? shipped.Get(f[0]) ?? "" : "", Where(d, f[0]) },
                    Shipped = shipped != null ? new[] { null, shipped.Get(f[0]) ?? "", null, null } : null,
                    Editable = new[] { false, true, false, false },
                });
            foreach (string language in d.Languages())
            {
                fieldLines.Add(new Line { Heading = "Texts: " + language });
                foreach (string[] t in TextNames)
                {
                    int rows, n; bool differs, x;
                    string v = TextOf(d, language, t[0], out rows, out differs) ?? "", was = TextOf(shipped, language, t[0], out n, out x);
                    fieldLines.Add(new Line
                    {
                        Field = t[0], Language = language,
                        Cells = new[] { t[1], v, was ?? "", rows + " row(s)" + (differs ? ", the targets differ" : "") },
                        Shipped = shipped != null ? new[] { null, was ?? "", null, null } : null,
                        Editable = new[] { false, true, false, false },
                    });
                }
            }

            List<QuestTalkLine> talk = editor.TalkLines(d.Id);
            if (talk.Count > 0)
            {
                bool own = talk.Exists(delegate(QuestTalkLine t) { return t.Own; });
                foreach (string language in editor.TalkLanguages())
                {
                    fieldLines.Add(new Line { Heading = "Talk texts: " + language + (!d.IsNew ? " (shipped: read only)" : own ? " (this quest's own)" : " (the source quest's: read only)") });
                    foreach (QuestTalkLine t in talk)
                    {
                        int rows; bool differs;
                        string v = editor.TalkText(t.TextId, language, out rows, out differs);
                        fieldLines.Add(new Line
                        {
                            Talk = t.TextId, Language = language,
                            Cells = new[] { t.Script + " " + t.Element + " " + t.TextId, v ?? "", !d.IsNew ? v ?? "" : "", rows == 0 ? "no row" : rows + " row(s)" + (differs ? ", they differ" : "") + (t.Own ? "" : d.IsNew ? ", shared" : "") },
                            Editable = new[] { false, t.Own && rows > 0, false, false },
                        });
                    }
                }
            }

            List<QuestCondition> conditions = d.Conditions(), wasConditions = shipped != null ? shipped.Conditions() : null;
            foreach (QuestCondition c in conditions)
            {
                QuestCondition was = wasConditions != null && c.Index < wasConditions.Count ? wasConditions[c.Index] : null;
                string table = QuestDraft.CodeTable(c.Type);
                conditionLines.Add(new Line
                {
                    Index = c.Index,
                    Cells = new[]
                    {
                        (c.Index + 1).ToString(CultureInfo.InvariantCulture), c.Type, c.Code, c.Value,
                        was != null ? was.Type + " " + was.Code + " x" + (was.Value.Length > 0 ? was.Value : "1") : shipped != null ? "(new row)" : "",
                        table == null ? "free text" : table + " id, read-only",
                        c.Places + " place(s)" + (c.Differs ? ", they differ" : ""),
                    },
                    Shipped = shipped != null ? new[] { null, was != null ? was.Type : "", was != null ? was.Code : "", was != null ? was.Value : "", null, null, null } : null,
                    Editable = new[] { false, true, table == null, true, false, false, false },
                });
            }

            foreach (string reward in new[] { QuestDraft.CompleteReward, QuestDraft.AcceptReward })
                foreach (string list in QuestDraft.RewardLists)
                {
                    if (!d.HasList(list)) continue;
                    bool accept = reward == QuestDraft.AcceptReward;
                    List<QuestReward> rows = d.Rewards(reward, list), wasRows = shipped != null ? shipped.Rewards(reward, list) : null;
                    // the accept reward has the table's v list and the files' essentialItem list; another list shows when it has rows
                    if (accept && list != "v" && list != "essentialItem" && rows.Count == 0) continue;
                    listBox.Items.Add(ListName(reward, list));
                    rewardLines.Add(new Line { Heading = (accept ? "On accept: " : "On complete: ") + list + (list == "v" ? "  (kms-client table)" : "  (quest files)"), Reward = reward, List = list, Index = -1 });
                    QuestReward[] wasOf = QuestDraft.PairShipped(wasRows, rows);
                    foreach (QuestReward w in rows)
                    {
                        QuestReward was = wasOf[w.Index];
                        rewardLines.Add(new Line
                        {
                            Reward = reward, List = list, Index = w.Index,
                            Cells = new[] { (w.Index + 1).ToString(CultureInfo.InvariantCulture), w.Code, w.Rank, w.Count, was != null ? ItemText(was) : shipped != null ? "(new row)" : "", RewardWhere(w) },
                            Shipped = shipped != null ? new[] { null, was != null ? was.Code : "", was != null ? was.Rank : "", was != null ? was.Count : "", null, null } : null,
                            Editable = new[] { false, true, true, true, false, false },
                        });
                    }
                }
            if (keepList != null && listBox.Items.Contains(keepList)) listBox.SelectedItem = keepList;
            else if (listBox.Items.Count > 0) listBox.SelectedIndex = 0;

            fieldGrid.RowCount = fieldLines.Count;
            conditionGrid.RowCount = conditionLines.Count;
            rewardGrid.RowCount = rewardLines.Count;
            questLabel.Text = "Quest " + d.Id.ToString(CultureInfo.InvariantCulture) + (d.IsNew ? " (new)" : "") + "  " + d.Name;
            cloneIdBox.Text = "";
        }

        static long Number(string text, string what)
        {
            text = (text ?? "").Trim();
            long v;
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out v)) throw new ModKitException("\"" + text + "\" is not a number (" + what + ")");
            return v;
        }

        void SetField(Line l, int column, string value)
        {
            QuestDraft d = Draft();
            if (l.Talk != 0)
            {
                int n = Editor().SetTalkText(d.Id, l.Talk, allLanguagesBox.Checked ? null : l.Language, value);
                Edited("talk text " + l.Talk + " set in " + n + " row(s)" + (allLanguagesBox.Checked ? " (every language)" : " (" + l.Language + ")"));
                return;
            }
            if (l.Language != null)
            {
                int n = d.SetText(allLanguagesBox.Checked ? null : l.Language, l.Field, value);
                Edited(l.Field + " text set in " + n + " row(s)" + (allLanguagesBox.Checked ? " (every language)" : " (" + l.Language + ")"));
                FillHits(d.Id);
                return;
            }
            long v = Number(value, l.Cells[0]);
            int places;
            if (l.Field == "require@level")
            {
                if (v > int.MaxValue) throw new ModKitException("the level is too large");
                places = d.SetLevel((int)v);
            }
            else if (l.Field == "start@npc") places = d.SetStartNpc(v);
            else if (l.Field == "complete@npc") places = d.SetCompleteNpc(v);
            else if (l.Field == "completeReward@exp") places = d.SetExp(v);
            else places = d.SetMeso(v);
            Edited(l.Cells[0] + " (" + l.Field + ") = " + v + " in " + places + " place(s); Plan lists them per target");
        }

        void SetCondition(Line l, int column, string value)
        {
            QuestDraft d = Draft();
            if (column == 1)
            {
                int places = d.SetConditionType(l.Index, value);
                Edited("condition " + (l.Index + 1) + " type = " + value.Trim() + " in " + places + " place(s)");
                return;
            }
            if (column == 2)
            {
                int places = d.SetConditionCode(l.Index, value);
                Edited("condition " + (l.Index + 1) + " code = " + value.Trim() + " in " + places + " place(s)");
                return;
            }
            long v = Number(value, "count");
            int n = d.SetConditionCount(l.Index, v);
            Edited("condition " + (l.Index + 1) + " count = " + v + " in " + n + " place(s)");
        }

        void SetReward(Line l, int column, string value)
        {
            var it = new QuestItem { Code = l.Cells[1], Rank = l.Cells[2], Count = l.Cells[3] };
            Number(value, column == 1 ? "item id" : column == 2 ? "rank" : "count");
            value = value.Trim();
            if (column == 1) it.Code = value; else if (column == 2) it.Rank = value; else it.Count = value;
            int n = Draft().SetReward(l.Reward, l.List, l.Index, it);
            Edited(ListName(l.Reward, l.List) + " row " + (l.Index + 1) + " = item " + it.Code + " rank " + it.Rank + " count " + it.Count + " in " + n + " place(s)");
        }

        string ChosenList(out string reward)
        {
            string list = listBox.SelectedItem as string;
            if (list == null) throw new ModKitException("choose a reward list in the box first");
            bool accept = list.StartsWith(AcceptPrefix, StringComparison.Ordinal);
            reward = accept ? QuestDraft.AcceptReward : QuestDraft.CompleteReward;
            return accept ? list.Substring(AcceptPrefix.Length) : list;
        }

        void AddReward()
        {
            QuestDraft d = Draft();
            EndEdits();
            string reward, list = ChosenList(out reward);
            Number(itemBox.Text, "item id"); Number(rankBox.Text, "rank"); Number(countBox.Text, "count");
            var it = new QuestItem { Code = itemBox.Text.Trim(), Rank = rankBox.Text.Trim(), Count = countBox.Text.Trim() };
            int n = d.AddReward(reward, list, it);
            Edited("item " + it.Code + " x" + it.Count + " added to " + ListName(reward, list) + " in " + n + " place(s)");
        }

        void RemoveReward()
        {
            QuestDraft d = Draft();
            rewardGrid.CancelEdit();
            Line l = rewardGrid.CurrentCell == null ? null : At(rewardLines, rewardGrid.CurrentCell.RowIndex);
            if (l == null || l.Heading != null) throw new ModKitException("select a row of a reward list in the grid first");
            int n = d.RemoveReward(l.Reward, l.List, l.Index);
            Edited(ListName(l.Reward, l.List) + " row " + (l.Index + 1) + " (item " + l.Cells[1] + ") removed in " + n + " place(s)");
        }

        void SameEverywhere()
        {
            QuestDraft d = Draft();
            EndEdits();
            string reward, list = ChosenList(out reward);
            if (reward != QuestDraft.CompleteReward) throw new ModKitException("\"Same on every target\" works on the complete reward; choose one of its lists");
            var items = new List<QuestItem>();
            foreach (QuestReward w in d.Rewards(list)) items.Add(new QuestItem { Code = w.Code, Rank = w.Rank, Count = w.Count });
            int n = d.SetRewardItems(items);
            Edited("the " + items.Count + " row(s) of " + list + " written to v, essentialItem and globalEssentialItem: " + n + " list(s) on the targets");
        }

        // ---- clone ----

        void Clone()
        {
            QuestDraft d = Draft();
            EndEdits();
            QuestEditor ed = Editor();
            string text = cloneIdBox.Text.Trim();
            long id = text.Length == 0 ? ed.NextFreeId(d.Id + 1) : Number(text, "id");
            long source = d.Id;
            int lines = ed.ModLines(source);
            QuestDraft made = ed.Clone(source, id, listedBox.Checked);
            host.EditedOutside();
            FillHits(id);
            ShowQuest(made);
            // a mod folder that could not be read when the id was checked: its reserved ids are not known
            string warn = "";
            foreach (string w in host.SharedSession().ModWarnings) warn += "warning: " + w + "\r\n";
            host.SayOutside(warn + "quest " + id + " added as a copy of quest " + source
                + (lines > 0 ? " as shipped: the " + lines + " line(s) this mod has on quest " + source + " are not in the copy" : "") + ". Save mod, then Plan shows it per target.\r\n\r\n" + Describe(made));
        }

        void OwnTalk(bool own)
        {
            QuestDraft d = Draft();
            if (!d.IsNew) throw new ModKitException("quest " + d.Id + " is a shipped quest; only a quest the mod adds gets talk texts of its own (clone it first)");
            EndEdits();
            QuestEditor ed = Editor();
            int n = own ? ed.OwnTalkTexts(d.Id) : ed.ShareTalkTexts(d.Id);
            if (n == 0)
            {
                host.SayOutside(!own ? "quest " + d.Id + " has no talk texts of its own"
                    : ed.OwnTalkTextCount(d.Id) > 0 ? "quest " + d.Id + " has its own talk texts already"
                    : ed.TalkLines(d.Id).Count == 0 ? "quest " + d.Id + " has no talk script row (the clone was made without it, or the source has none)"
                    : "the talk script of quest " + d.Id + " names no text the data has a row for");
                return;
            }
            Edited(own ? n + " talk text(s) copied for quest " + d.Id + "; edit them under Talk texts. Save mod, then Plan shows the rows per target."
                : n + " own talk text(s) of quest " + d.Id + " taken out of the mod; it speaks the source quest's lines again");
        }

        void RemoveClone()
        {
            QuestDraft d = Draft();
            if (!d.IsNew) throw new ModKitException("quest " + d.Id + " is a shipped quest; only a quest the mod adds can be removed");
            CancelEdits();
            Editor().RemoveClone(d.Id);
            ShowQuest(null);
            host.EditedOutside();
            FillHits(0);
            host.SayOutside("quest " + d.Id + " taken out of the mod");
        }
    }
}
