// The "MS2 Achievements" view: the trophies (achieve/<id>.xml of each side's Xml.m2d), in the style of the MS2
// Dungeons view. A find box (achievement id or name), the list of hits, the achievement's texts and fields as one
// grid, its grades (value, condition, reward) as another, the checks, Clone into the mod's id range of a category,
// Remove / restore, Undo changes, and the Plan / Apply / Revert buttons and output box of the MS2 Tables view.
//
// An edit sets the value on every side that has the achievement, each side in its own notation (the Korean client
// drops empty attributes and default rewards, the server and the global client write them; see
// MS2.ModKit.AchievementEditor). The condition code picks an id of the kind the condition type names, with the
// picker of the MS2 Quests view; a reward code of an item reward picks an item. Every edit is an op of the mod the
// MS2 Tables view is editing.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using Emergent.Toolbench;
using Emergent.Toolbench.UI.Workbench;
using MS2.ModKit;

namespace MS2.Tables
{
    public class AchievementsView : AbstractView
    {
        public const string ViewId = "MS2.Tables.AchievementsView";
        const int RowHeight = 34;
        static readonly string[] GradeHeads = { "grade", "condition", "code", "count", "target", "reward", "code", "value", "rank" };

        TablesView host;
        AchievementEditor editor;
        Achievement shown;
        readonly List<string> fields = new List<string>();
        List<string[]> grades = new List<string[]>();
        readonly List<Achievement> hits = new List<Achievement>();
        bool filling;

        readonly TextBox searchBox = new TextBox { Width = 180 };
        readonly Label hitsLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListBox hitList = new ListBox { Dock = DockStyle.Left, Width = 290, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = RowHeight };
        readonly Label achLabel = new Label { AutoSize = true, Margin = new Padding(24, 8, 8, 0) };
        readonly ComboBox categoryBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        readonly TextBox newIdBox = new TextBox { Width = 80 };
        readonly ComboBox tagBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        readonly ComboBox typeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190, MaxDropDownItems = 24 };
        readonly ComboBox rewardBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        readonly DataGridView fieldGrid = new DataGridView { Dock = DockStyle.Fill };
        readonly DataGridView gradeGrid = new DataGridView { Dock = DockStyle.Fill };
        readonly ListBox checkList = new ListBox { Dock = DockStyle.Bottom, Height = 80, IntegralHeight = false, HorizontalScrollbar = true };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 130 };
        readonly ComboBox targetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();
        readonly BoldFont bold = new BoldFont();

        public AchievementsView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(Lbl("find"));
            top.Controls.Add(searchBox);
            tips.SetToolTip(searchBox, "A part of the achievement id or of its name; empty lists every achievement");
            top.Controls.Add(Btn("Find", "Lists the achievements whose id or name holds the text", delegate { Find(); }));
            top.Controls.Add(hitsLabel);
            top.Controls.Add(achLabel);

            FlowLayoutPanel applyBar = Bar();
            applyBar.Controls.Add(Lbl("Apply to"));
            targetBox.Items.Add("workspace (patched archives)");
            targetBox.Items.Add("game folder (loose files)");
            tips.SetToolTip(targetBox, "Where Apply writes the client's files (the same choice as in the MS2 Tables view). The server's archives and the global client's always go to the workspace.");
            applyBar.Controls.Add(targetBox);
            applyBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change, with the achievement checks for each target (reward items and titles, condition types, grades, client and server sides); writes nothing", delegate { return host.DoPlan(); }));
            applyBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change, to every target at once", delegate { return host.DoApply(); }));
            applyBar.Controls.Add(Job("Revert", "Removes what Apply wrote, on every target", delegate { return host.DoRevert(); }));

            FlowLayoutPanel achBar = Bar();
            for (int c = 1; c <= 3; c++) categoryBox.Items.Add(AchieveRules.Categories[c]);
            categoryBox.SelectedIndex = 0;
            achBar.Controls.Add(Btn("Clone", "Adds a copy of this achievement (each side its own file, its name and descriptions in every language) under the id in the box; empty = the next free id of the category's range (" + AchieveRules.NewFrom(1) + "-" + AchieveRules.NewTo(1) + " combat, " + AchieveRules.NewFrom(2) + "-" + AchieveRules.NewTo(2) + " adventure, " + AchieveRules.NewFrom(3) + "-" + AchieveRules.NewTo(3) + " living)", delegate { Clone(); }));
            achBar.Controls.Add(Lbl("into"));
            achBar.Controls.Add(categoryBox);
            tips.SetToolTip(categoryBox, "The category a clone goes to: the second digit of its id (the client counts it there)");
            achBar.Controls.Add(Lbl("new id"));
            achBar.Controls.Add(newIdBox);
            achBar.Controls.Add(Btn("Remove / restore", "Removes the achievement on every side (a new one goes away and its id is free), or brings a removed shipped achievement back", delegate { RemoveOrRestore(); }));
            achBar.Controls.Add(Btn("Undo changes", "Drops every change the mod makes to this shipped achievement: its files, name and descriptions", delegate { Undo(); }));
            achBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same mod and Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            FlowLayoutPanel fieldBar = Bar();
            fieldBar.Controls.Add(Lbl("list"));
            fieldBar.Controls.Add(tagBox);
            tips.SetToolTip(tagBox, "The categories of table/achievecategory.xml; Set list writes the chosen one as categoryTag (the list the achievement window shows it in)");
            fieldBar.Controls.Add(Btn("Set list", "Writes the chosen category as the achievement's categoryTag on every side", delegate { SetField("categoryTag", tagBox.SelectedItem as string ?? ""); }));

            FlowLayoutPanel gradeBar = Bar();
            gradeBar.Controls.Add(Btn("Add grade", "Adds a grade after the last one: a copy of its condition with the next grade value and no reward, on every side", delegate { AddGrade(); }));
            gradeBar.Controls.Add(Btn("Remove grade", "Removes the selected grade on every side (the last grade stays)", delegate { RemoveGrade(); }));
            gradeBar.Controls.Add(Lbl("condition"));
            gradeBar.Controls.Add(typeBox);
            foreach (string t in AchieveRules.ConditionTypes) typeBox.Items.Add(t);
            tips.SetToolTip(typeBox, "The condition types the client and the server read (an unknown one drops the achievement on the client and fails the server's data ingest)");
            gradeBar.Controls.Add(Btn("Set", "Writes the chosen condition type to the selected grade", delegate { SetGradeFromBox(1, typeBox); }));
            gradeBar.Controls.Add(Lbl("reward"));
            gradeBar.Controls.Add(rewardBox);
            rewardBox.Items.Add("(none)");
            foreach (string t in AchieveRules.RewardTypes) rewardBox.Items.Add(t);
            tips.SetToolTip(rewardBox, "The reward types; (none) takes the selected grade's reward away");
            gradeBar.Controls.Add(Btn("Set ", "Writes the chosen reward type to the selected grade", delegate { SetGradeFromBox(5, rewardBox); }));

            Setup(fieldGrid);
            fieldGrid.Columns.Add(Col("field", 95, true));
            fieldGrid.Columns.Add(Col("value", 150, false));
            fieldGrid.Columns.Add(Col("note", 110, true));
            fieldGrid.CellValueNeeded += OnFieldNeeded;
            fieldGrid.CellValuePushed += OnFieldPushed;
            fieldGrid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e)
            {
                if (shown == null || editor == null || editor.IsDeleted(shown) || e.ColumnIndex != 1 || e.RowIndex >= fields.Count) e.Cancel = true;
            };
            fieldGrid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e)
            {
                if (shown == null || editor == null || e.RowIndex < 0 || e.RowIndex >= fields.Count) return;
                if (editor.IsDeleted(shown) || e.ColumnIndex == 2) e.CellStyle.ForeColor = SystemColors.GrayText;
                else if (e.ColumnIndex == 0 && FieldValue(fields[e.RowIndex]).Length > 0) e.CellStyle.Font = bold.Of(fieldGrid.Font);
            };

            Setup(gradeGrid);
            int[] widths = { 40, 120, 80, 45, 45, 60, 70, 40, 35 };
            for (int i = 0; i < GradeHeads.Length; i++) gradeGrid.Columns.Add(Col(GradeHeads[i], widths[i], false));
            gradeGrid.CellValueNeeded += delegate(object sender, DataGridViewCellValueEventArgs e)
            {
                if (shown == null || e.RowIndex < 0 || e.RowIndex >= grades.Count || e.ColumnIndex < 0) return;
                e.Value = grades[e.RowIndex][e.ColumnIndex];
            };
            gradeGrid.CellValuePushed += OnGradePushed;
            gradeGrid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e)
            {
                if (shown == null || editor == null || editor.IsDeleted(shown) || e.RowIndex >= grades.Count) e.Cancel = true;
                else if (e.ColumnIndex >= 6 && grades[e.RowIndex][5].Length == 0) e.Cancel = true;   // set the reward type first
            };
            gradeGrid.CellToolTipTextNeeded += delegate(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                switch (e.ColumnIndex)
                {
                    case 0: e.ToolTipText = "The grade value: grades rise 1, 2, 3, ...; each is reached on its own"; break;
                    case 1: e.ToolTipText = "The condition type (the box above lists them)"; break;
                    case 2: e.ToolTipText = "What the condition counts: an id or a comma list, by the type"; break;
                    case 3: e.ToolTipText = "How many the grade needs"; break;
                    case 4: e.ToolTipText = "The target of the condition, for the types that have one"; break;
                    case 5: e.ToolTipText = "The reward type; empty = no reward"; break;
                    case 6: e.ToolTipText = "The reward: an item id, a title id of table/titletag.xml, ..."; break;
                    case 7: e.ToolTipText = "The reward's count"; break;
                    case 8: e.ToolTipText = "The item reward's rank (1 = normal)"; break;
                }
            };
            gradeGrid.SelectionChanged += delegate { if (!filling) ShowGradeBoxes(); };
            Func<EditorSession> session = delegate { return host.SharedSession(); };
            IdPick.Attach(gradeGrid, delegate(int row, int column)
            {
                if (shown == null || row < 0 || row >= grades.Count) return null;
                if (column == 2) return IdLookup.QuestCodeKind(grades[row][1]);
                if (column == 6 && string.Equals(grades[row][5], "item", StringComparison.OrdinalIgnoreCase)) return IdLookup.Item;
                return null;
            }, session, delegate(int row, int column) { return column == 2; });
            tips.SetToolTip(checkList, "The checks of the achievement as shown; Plan checks every achievement the mods add or change, per target, with the items and titles of each target");

            var fieldPane = Pane("Texts and fields", fieldBar, fieldGrid);
            var gradePane = Pane("Grades (… or F4 picks an id)", gradeBar, gradeGrid);
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            table.Controls.Add(fieldPane, 0, 0);
            table.Controls.Add(gradePane, 1, 0);

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(table);
            right.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            right.Controls.Add(checkList);
            right.Controls.Add(achBar);

            Controls.Add(right);
            Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
            Controls.Add(hitList);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(outputBox);
            Controls.Add(applyBar);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, applyBar, hitList, right });
            ViewLayout.KeepLogLines(outputBox, this);
            ViewLayout.KeepHeightShare(checkList, right, 0.14, 3 * checkList.ItemHeight + 4,
                delegate { return achBar.Height + 8 + 18 + gradeBar.Height + gradeGrid.ColumnHeadersHeight + 6 * gradeGrid.RowTemplate.Height + 4; });
            ViewLayout.CentreOnFirstShow(this);
            targetBox.SelectedIndexChanged += delegate { if (!filling && host != null) host.TargetIndex = targetBox.SelectedIndex; };

            searchBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                Guard(delegate { Find(); });
            };
            hitList.SelectedIndexChanged += delegate { if (!filling) Guard(delegate { OpenSelected(); }); };
            hitList.DrawItem += OnDrawHit;
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

        static DataGridViewColumn Col(string header, int width, bool readOnly)
        {
            return ViewLayout.FitColumn(new DataGridViewTextBoxColumn { HeaderText = header, ReadOnly = readOnly, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable });
        }

        static void Setup(DataGridView g)
        {
            g.VirtualMode = true;
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.AllowUserToOrderColumns = false;
            g.RowHeadersVisible = false;
            g.MultiSelect = false;
            g.SelectionMode = DataGridViewSelectionMode.CellSelect;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            g.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            g.BackgroundColor = SystemColors.Window;
            g.DefaultCellStyle.BackColor = SystemColors.Window;
            g.DefaultCellStyle.ForeColor = SystemColors.WindowText;
        }

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

        void EndEdits() { fieldGrid.EndEdit(); gradeGrid.EndEdit(); }

        void CancelEdits() { fieldGrid.CancelEdit(); gradeGrid.CancelEdit(); }

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
            long keep = shown != null ? shown.Id : 0;
            if (host != null)
            {
                host.Changed -= OnHostChanged;
                host.Flushing -= EndEdits;
                host.BusyChanged -= OnHostBusy;
                host.Said -= OnHostSaid;
                host.TargetChanged -= OnHostTarget;
            }
            host = h;
            editor = null;
            if (h == null) { shown = null; hits.Clear(); return; }
            Show(null);
            hits.Clear();
            hitList.Items.Clear();
            h.Changed += OnHostChanged;
            h.Flushing += EndEdits;
            h.BusyChanged += OnHostBusy;
            h.Said += OnHostSaid;
            h.TargetChanged += OnHostTarget;
            OnHostTarget();
            OutputWrap.Set(outputBox, h.OutputText);
            OnHostBusy(h.Busy);
            try { FillHits(keep); }
            catch (ModKitException) { Show(null); }
            catch (System.IO.IOException) { Show(null); }
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
                Logger.Warning("MS2 Achievements: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        AchievementEditor Editor()
        {
            EditorSession s = host.SharedSession();
            if (editor == null || editor.Mod != s.Mod || editor.Data != s.Data)
            {
                editor = new AchievementEditor(s.Data, s.Mod);
                editor.Others = delegate { return s.OtherMods(); };
                filling = true;
                try
                {
                    tagBox.Items.Clear();
                    foreach (string t in editor.CategoryTags()) tagBox.Items.Add(t);
                }
                finally { filling = false; }
            }
            return editor;
        }

        void OnHostSaid(string text) { OutputWrap.Set(outputBox, text); }

        void OnHostBusy(bool on) { ViewLayout.Lock(this, lockable, on, outputBox); }

        // the mod was replaced or edited in another view: read the files again and show the same achievement
        void OnHostChanged()
        {
            CancelEdits();
            long keep = shown != null ? shown.Id : 0;
            editor = null;
            Show(null);
            try { FillHits(keep); }
            catch (ModKitException) { Show(null); }
            catch (System.IO.IOException) { Show(null); }
        }

        // ---- the list ----

        void Find()
        {
            EndEdits();
            FillHits(shown != null ? shown.Id : 0);
            if (hitList.Items.Count > 0 && hitList.SelectedIndex < 0) hitList.SelectedIndex = 0;
            else if (hitList.Items.Count == 0) Show(null);
        }

        void FillHits(long keep)
        {
            AchievementEditor ed = Editor();
            hits.Clear();
            hits.AddRange(ed.Find(searchBox.Text));
            // the mod's new achievements first, then by id
            hits.Sort(delegate(Achievement a, Achievement b)
            {
                if (a.IsAdded != b.IsAdded) return a.IsAdded ? -1 : 1;
                return a.Id.CompareTo(b.Id);
            });
            filling = true;
            Achievement show = null;
            try
            {
                hitList.BeginUpdate();
                hitList.Items.Clear();
                foreach (Achievement a in hits)
                {
                    hitList.Items.Add(a);
                    if (show == null && keep != 0 && a.Id == keep) { hitList.SelectedIndex = hitList.Items.Count - 1; show = a; }
                }
                hitList.EndUpdate();
            }
            finally { filling = false; }
            hitsLabel.Text = hits.Count + " achievement(s), " + ed.All.Count + " in all";
            if (show != null) Show(show);
        }

        void OpenSelected()
        {
            var a = hitList.SelectedItem as Achievement;
            if (a == null) return;
            EndEdits();
            Show(a);
            host.SayOutside(Describe(a));
        }

        static string State(AchievementEditor ed, Achievement a, string removed, string added, string changed)
        {
            return ed.IsDeleted(a) ? removed : a.IsAdded ? added : ed.IsChanged(a) ? changed : "";
        }

        void OnDrawHit(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= hitList.Items.Count || editor == null || host == null || host.IsDisposed || host.Busy) return;
            var a = hitList.Items[e.Index] as Achievement;
            if (a == null) return;
            string name = editor.Text(a, "name");
            int cat = AchieveRules.CategoryOf(a.Id), n = editor.Grades(a).Count;
            bool deleted = editor.IsDeleted(a), marked = a.IsAdded || editor.IsChanged(a);
            string line1 = a.Id + "  " + (name.Length > 0 ? name : "(no name)");
            string tag = editor.Get(a, "categoryTag");
            string line2 = (tag.Length > 0 ? tag : AchieveRules.TypeOf(a.Id) == 2 && cat >= 1 && cat <= 3 ? AchieveRules.Categories[cat] : "type " + AchieveRules.TypeOf(a.Id))
                + ", " + n + (n == 1 ? " grade" : " grades") + (editor.MissingOn(a).Count > 0 ? ", one side only" : "") + State(editor, a, "  (removed)", "  (new)", "  (changed)");
            Color fore = deleted && (e.State & DrawItemState.Selected) == 0 ? SystemColors.GrayText : e.ForeColor;
            int half = e.Bounds.Height / 2;
            var t1 = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 1, e.Bounds.Width - 8, half);
            var t2 = new Rectangle(t1.X, e.Bounds.Y + half - 1, t1.Width, half);
            TextRenderer.DrawText(e.Graphics, line1, marked ? bold.Of(e.Font) : e.Font, t1, fore, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, line2, e.Font, t2, fore == e.ForeColor && (e.State & DrawItemState.Selected) == 0 ? SystemColors.GrayText : fore, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        }

        // ---- the achievement ----

        static bool IsText(string f) { return Array.IndexOf(AchievementEditor.TextFields, f) >= 0; }

        string FieldValue(string f)
        {
            if (IsText(f)) return editor.Text(shown, f);
            return editor.Differs(shown).Contains(f) ? "" : editor.Get(shown, f);
        }

        void Show(Achievement a)
        {
            CancelEdits();
            shown = a;
            filling = true;
            try
            {
                fieldGrid.RowCount = 0;
                gradeGrid.RowCount = 0;
                checkList.Items.Clear();
                fields.Clear();
                grades = new List<string[]>();
                if (a == null) { achLabel.Text = "no achievement chosen"; return; }
                AchievementEditor ed = Editor();
                string name = ed.Text(a, "name");
                achLabel.Text = "Achievement " + a.Id + (name.Length > 0 ? " (" + name + ")" : "") + State(ed, a, ", removed by the mod", ", new in the mod", ", changed by the mod");
                fields.AddRange(AchievementEditor.TextFields);
                fields.AddRange(AchieveRules.Fields);
                fieldGrid.RowCount = fields.Count;
                grades = ed.Grades(a);
                gradeGrid.RowCount = grades.Count;
                foreach (string t in ed.Check(a)) checkList.Items.Add(t);
                if (checkList.Items.Count == 0) checkList.Items.Add("no problem found");
                int cat = AchieveRules.CategoryOf(a.Id);
                if (cat >= 1 && cat <= 3) categoryBox.SelectedIndex = cat - 1;
                string tag = ed.Get(a, "categoryTag");
                tagBox.SelectedIndex = tagBox.Items.IndexOf(tag);
            }
            finally { filling = false; }
            fieldGrid.Invalidate();
            gradeGrid.Invalidate();
            ShowGradeBoxes();
        }

        int SelectedGrade()
        {
            DataGridViewCell c = gradeGrid.CurrentCell;
            return c == null || c.RowIndex >= grades.Count ? -1 : c.RowIndex;
        }

        void ShowGradeBoxes()
        {
            int g = SelectedGrade();
            if (g < 0) g = grades.Count > 0 ? 0 : -1;
            bool was = filling;
            filling = true;
            try
            {
                typeBox.SelectedIndex = g < 0 ? -1 : IndexOf(typeBox, grades[g][1]);
                rewardBox.SelectedIndex = g < 0 ? -1 : grades[g][5].Length == 0 ? 0 : IndexOf(rewardBox, grades[g][5]);
            }
            finally { filling = was; }
        }

        static int IndexOf(ComboBox box, string text)
        {
            for (int i = 0; i < box.Items.Count; i++) if (string.Equals((string)box.Items[i], text, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        string Describe(Achievement a)
        {
            AchievementEditor ed = Editor();
            var sb = new StringBuilder();
            sb.Append("achievement ").Append(a.Id).Append(State(ed, a, " (removed by the mod)", " (new in the mod)", " (changed by the mod)")).Append(':');
            for (int i = 0; i < ed.Sides.Count; i++)
                sb.Append("\r\n").Append(ed.Sides[i].Archive).Append(' ').Append(a.Entry).Append(": ").Append(ed.Has(a, i) ? "has it" : ed.IsDeleted(a) ? "removed" : "not there");
            if (ed.Sides.Count > 0 && Profile.OfArchive(ed.Sides[ed.Sides.Count - 1].Archive) == Profile.Server && ed.Data.Has(Profile.GmsClient))
                sb.Append("\r\n(the global client gets the server's file)");
            return sb.ToString();
        }

        void Refresh(string said)
        {
            host.EditedOutside();
            Achievement a = shown;
            hitList.Invalidate();
            Show(a);
            if (said != null) host.SayOutside(said + "\r\n\r\n" + Describe(a));
        }

        string Note(string f)
        {
            if (f == "name") return "in every language file (string/{lang}/" + AchieveRules.NameFile + ")";
            if (IsText(f)) return "in every language file (string/{lang}/" + AchieveRules.DescFile + ")";
            if (editor.Differs(shown).Contains(f)) return "the sides differ; a value here sets all";
            switch (f)
            {
                case "icon": return "the image path the window shows";
                case "categoryTag": return "the list it is shown in (Set list above)";
                case "feature": return "shown only when the feature is on";
                case "account": return "1 = shared by the account's characters";
                case "locking": return "1 = hidden until reached";
                case "noticePercent": return "progress % that tells the player (1 = default)";
                case "standardLevel": return "the level it is meant for";
                default: return "";
            }
        }

        void OnFieldNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (shown == null || editor == null || host == null || host.Busy || e.RowIndex < 0 || e.RowIndex >= fields.Count) return;
            string f = fields[e.RowIndex];
            if (e.ColumnIndex == 0) e.Value = (!IsText(f) && editor.Differs(shown).Contains(f) ? "≠ " : "   ") + f;
            else if (e.ColumnIndex == 1) e.Value = FieldValue(f);
            else e.Value = Note(f);
        }

        void OnFieldPushed(object sender, DataGridViewCellValueEventArgs e)
        {
            if (shown == null || e.ColumnIndex != 1 || e.RowIndex < 0 || e.RowIndex >= fields.Count) return;
            string f = fields[e.RowIndex];
            string v = ((e.Value as string) ?? "").Trim();
            Guard(delegate { SetField(f, v); });
        }

        void SetField(string f, string v)
        {
            if (shown == null) throw new ModKitException("choose an achievement first");
            if (filling) return;
            AchievementEditor ed = Editor();
            if (IsText(f))
            {
                if (v == ed.Text(shown, f)) return;
                ed.SetText(shown, f, v);
            }
            else
            {
                if (ed.Differs(shown).Contains(f) ? v.Length == 0 : v == ed.Get(shown, f)) return;
                ed.Set(shown, f, v);
            }
            Refresh(null);
        }

        void OnGradePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            if (shown == null || e.RowIndex < 0 || e.RowIndex >= grades.Count || e.ColumnIndex < 0 || e.ColumnIndex >= GradeHeads.Length) return;
            int row = e.RowIndex, col = e.ColumnIndex;
            string v = ((e.Value as string) ?? "").Trim();
            Guard(delegate { SetGrade(row, col, v); });
        }

        void SetGrade(int row, int col, string v)
        {
            if (shown == null) throw new ModKitException("choose an achievement first");
            if (row < 0 || row >= grades.Count) throw new ModKitException("choose a grade first");
            if (v == grades[row][col]) return;
            Editor().SetGrade(shown, row, AchieveRules.GradeFields[col], v);
            Refresh(null);
            if (row < gradeGrid.RowCount) gradeGrid.CurrentCell = gradeGrid[col, row];
        }

        void SetGradeFromBox(int col, ComboBox box)
        {
            string v = box.SelectedItem as string ?? "";
            if (v == "(none)") v = "";
            int g = SelectedGrade();
            if (g < 0 && grades.Count == 1) g = 0;
            SetGrade(g, col, v);
        }

        void AddGrade()
        {
            if (shown == null) throw new ModKitException("choose an achievement first");
            EndEdits();
            Editor().AddGrade(shown);
            Refresh("grade added to achievement " + shown.Id + " (a copy of the last condition, no reward)");
        }

        void RemoveGrade()
        {
            if (shown == null) throw new ModKitException("choose an achievement first");
            int g = SelectedGrade();
            if (g < 0) throw new ModKitException("choose a grade first");
            EndEdits();
            string value = grades[g][0];
            Editor().RemoveGrade(shown, g);
            Refresh("grade " + value + " removed from achievement " + shown.Id);
        }

        // ---- clone, remove ----

        long NewId()
        {
            string text = newIdBox.Text.Trim();
            long v;
            if (text.Length > 0)
            {
                if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out v)) throw new ModKitException("\"" + text + "\" is not an id");
                return v;   // Clone refuses an id outside the range or one this or another enabled mod has
            }
            return Editor().NextId(categoryBox.SelectedIndex + 1);
        }

        void Clone()
        {
            if (shown == null) throw new ModKitException("choose the achievement to copy first");
            EndEdits();
            long source = shown.Id;
            Achievement a = Editor().Clone(shown, NewId());
            host.EditedOutside();
            newIdBox.Text = "";
            searchBox.Text = "";
            FillHits(a.Id);
            Show(a);
            host.SayOutside("achievement " + a.Id + " added as a copy of " + source + ": give it a name and its grades. Save mod, then Plan checks it per target.\r\n\r\n" + Describe(a));
        }

        void Undo()
        {
            if (shown == null) throw new ModKitException("choose an achievement first");
            EndEdits();
            Editor().Revert(shown);
            Refresh("achievement " + shown.Id + " is as shipped again");
        }

        void RemoveOrRestore()
        {
            if (shown == null) throw new ModKitException("choose an achievement first");
            EndEdits();
            AchievementEditor ed = Editor();
            long id = shown.Id;
            if (ed.IsDeleted(shown))
            {
                ed.Restore(shown);
                Refresh("achievement " + id + " is back");
                return;
            }
            bool added = shown.IsAdded;
            ed.Remove(shown);
            if (added)
            {
                host.EditedOutside();
                Show(null);
                FillHits(0);
                host.SayOutside("achievement " + id + " was new in the mod and is gone again; its id is free");
            }
            else Refresh("achievement " + id + " removed on every side (Remove / restore brings it back)");
        }
    }
}
