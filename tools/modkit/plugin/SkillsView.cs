// The "MS2 Skills" view: one player skill or additional effect at a time. A Skills / Effects switch, a search box (id
// or name), the list of hits, the icon, a level picker, and one grid with the mapped fields of the chosen level: the
// level's own fields, then each motion and each attack of the level. The "mod value" column is what the mod sets (a
// number in neutral units, or *k for the shipped value times k); the other columns show the shipped value of each
// target. Under it a read-only table of all levels at once (one target's values, the cells the mod changes marked;
// a click picks that level). Above: clone to a new id, a job skill tree row, the level's tooltip; Plan / Apply /
// Revert with a two-line output box. Names are shown in one language ("Names in"; default: each target's own).
//
// The view holds no data logic and no mod of its own. MS2.ModKit.SkillEditor turns what is done here into <skill>,
// <effect> and <jobskill> operations of the mod the MS2 Tables view is editing; the session, the worker thread, the
// output text and the close question stay in that view (TablesView).
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
    public class SkillsView : AbstractView
    {
        public const string ViewId = "MS2.Tables.SkillsView";
        const int MaxHits = 500;
        static readonly string[] SideNames = { "kms-client", "gms-client", "server" };

        sealed class GridRow
        {
            internal string Heading;
            internal SkillScope Scope;
            internal int Motion, Attack;    // 1-based
            internal string Field;
        }

        sealed class Hit
        {
            internal long Id;
            internal string Text, Base;     // Base: the text without the "*" mark
            internal bool New, Marked;
            public override string ToString() { return Text; }
        }

        TablesView host;
        SkillEditor editor;
        SkillInfo info;         // the shipped skill (for a clone of the mod: its source)
        long shownId;           // the id the grid edits
        int level = 1;
        readonly List<GridRow> rows = new List<GridRow>();
        readonly List<GridRow> allCols = new List<GridRow>();     // the columns of the all-levels table
        readonly List<int> allLevels = new List<int>();           // its rows
        string nameLanguage;    // null = each target's own
        bool filling;

        readonly ComboBox kindBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
        readonly TextBox searchBox = new TextBox { Width = 200 };
        readonly Label hitsLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListBox hitList = new ListBox { Dock = DockStyle.Left, Width = 260, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 36 };
        readonly Dictionary<long, string> iconOf = new Dictionary<long, string>();     // id -> image entry (null: none), of the listed rows
        readonly ComboBox namesBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        readonly Label skillLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 8, 0) };
        readonly ComboBox levelBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };
        readonly TextBox cloneIdBox = new TextBox { Width = 80 };
        readonly TextBox cloneLevelsBox = new TextBox { Width = 50 };
        readonly TextBox cloneNameBox = new TextBox { Width = 110 };
        readonly TextBox cloneOwnBox = new TextBox { Width = 110 };
        readonly CheckBox cloneGroupBox = new CheckBox { Text = "own cooldown group", AutoSize = true, Margin = new Padding(6, 6, 0, 0) };
        readonly TextBox jobBox = new TextBox { Width = 40 };
        readonly TextBox replaceBox = new TextBox { Width = 80 };
        readonly TextBox positionBox = new TextBox { Width = 50 };
        readonly TextBox tierBox = new TextBox { Width = 30, Text = "1" };
        readonly TextBox kmsParamBox = new TextBox { Width = 110 };
        readonly TextBox gmsTextBox = new TextBox { Width = 280 };
        readonly ComboBox langBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 50, Text = "en" };
        readonly FlowLayoutPanel jobBar = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
        readonly DataGridView grid = new DataGridView { Dock = DockStyle.Top, Height = 200 };
        readonly DataGridView allGrid = new DataGridView { Dock = DockStyle.Fill };
        readonly ComboBox allSideBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 130 };
        readonly ComboBox targetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        readonly ToolTip tips = new ToolTip();
        readonly IconBox iconBox = new IconBox { Dock = DockStyle.Left, Width = 64, Margin = new Padding(0) };
        readonly List<Control> lockable = new List<Control>();

        public SkillsView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            kindBox.Items.Add("Skills");
            kindBox.Items.Add("Effects");
            kindBox.SelectedIndex = 0;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(kindBox);
            tips.SetToolTip(kindBox, "Skills: the player skills (skilldata on the kms-client, skill/ on the gms-client and server). Effects: the additional effects they apply (additional / additionaleffect)");
            top.Controls.Add(Lbl("id or name"));
            top.Controls.Add(searchBox);
            top.Controls.Add(Btn("Find", "Lists the ids that contain the text or whose name contains it; the skills and effects the mod adds come first", delegate { Find(); }));
            top.Controls.Add(Lbl("Names in"));
            top.Controls.Add(namesBox);
            namesBox.Items.Add("own language");
            namesBox.SelectedIndex = 0;
            tips.SetToolTip(namesBox, "The language of the names in the list and above the grid. own language: Korean (kr) on the kms-client, English (en) on the gms-client and the server. A target without names in the chosen language uses its own; the line on the right says which language each target gave");
            top.Controls.Add(hitsLabel);

            FlowLayoutPanel applyBar = Bar();
            applyBar.Controls.Add(Lbl("Apply to"));
            targetBox.Items.Add("workspace (patched archives)");
            targetBox.Items.Add("game folder (loose files)");
            tips.SetToolTip(targetBox, "Where Apply writes the client's tables (the same choice as in the MS2 Tables view)");
            applyBar.Controls.Add(targetBox);
            applyBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change on every target, with the warnings (missing levels, motions or attacks, unknown ids, tooltips); writes nothing", delegate { return host.DoPlan(); }));
            applyBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change, to every target at once", delegate { return host.DoApply(); }));
            applyBar.Controls.Add(Job("Revert", "Removes what Apply wrote, on every target", delegate { return host.DoRevert(); }));
            applyBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same mod and Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            FlowLayoutPanel skillBar = Bar();
            skillBar.Controls.Add(skillLabel);
            skillBar.Controls.Add(Lbl("Level"));
            skillBar.Controls.Add(levelBox);
            tips.SetToolTip(levelBox, "The level the grid shows and edits (a click on a row of the all-levels table picks it too)");
            skillBar.Controls.Add(jobBar);

            FlowLayoutPanel cloneBar = Bar();
            cloneBar.Controls.Add(Btn("Clone to new id", "Adds a new skill or effect that starts as a copy of this one on each target that has it, under the id in the box (the mod's reserved range: skills 78000000-78099999, effects 77100000-77199999). levels: the levels kept (empty = all, \"1-3\"); name: empty = the source's; own effects: \"source=new, ...\" effects cloned with it and pointed at", delegate { Clone(); }));
            cloneBar.Controls.Add(cloneIdBox);
            cloneBar.Controls.Add(Lbl("levels"));
            cloneBar.Controls.Add(cloneLevelsBox);
            cloneBar.Controls.Add(Lbl("name"));
            cloneBar.Controls.Add(cloneNameBox);
            cloneBar.Controls.Add(Lbl("own effects"));
            cloneBar.Controls.Add(cloneOwnBox);
            cloneBar.Controls.Add(cloneGroupBox);
            tips.SetToolTip(cloneGroupBox, "Skills only. Ticked: the clone gets its own cooldown group (cooldownGroupID = the new id). Not ticked: it keeps the source's group, so using one starts the other's cooldown too; Plan warns when that group is not 0");
            cloneBar.Controls.Add(Btn("Where used", "Lists the npcs and items that name this skill or effect (npc skills, item skill and additional effects), the mod's first, with Go to", delegate
            {
                if (shownId == 0) throw new ModKitException("choose a skill or an effect first");
                WhereUsedJump.Show(this, host, Kind == SkillKind.Effect ? IdLookup.Effect : IdLookup.Skill, shownId);
            }));
            cloneBar.Controls.Add(Btn("Remove clone", "Takes the chosen new skill or effect out of the mod again", delegate { RemoveClone(); }));

            jobBar.Controls.Add(Lbl("Job"));
            jobBar.Controls.Add(jobBox);
            tips.SetToolTip(jobBox, "The job number of the job skill tree (as the job table writes it, e.g. 10)");
            jobBar.Controls.Add(Lbl("replace skill"));
            jobBar.Controls.Add(replaceBox);
            jobBar.Controls.Add(IdPick.ForBox(replaceBox, IdLookup.Skill, delegate { return host.SharedSession(); }, tips));
            jobBar.Controls.Add(Lbl("or position x,y"));
            jobBar.Controls.Add(positionBox);
            jobBar.Controls.Add(Lbl("tier"));
            jobBar.Controls.Add(tierBox);
            jobBar.Controls.Add(Btn("Set job row", "Puts this skill in the job's skill tree on every target: in the slot of the skill in \"replace skill\", or as a new row at the position (a warning when the position is taken in that tier). The tree's grid size is not known; check the position in game", delegate { JobRow(); }));

            FlowLayoutPanel textBar = Bar();
            textBar.Controls.Add(Lbl("Tooltip of the level: kms param"));
            textBar.Controls.Add(kmsParamBox);
            tips.SetToolTip(kmsParamBox, "The kms-client's stringParam of the level: the numbers its description fills in, comma separated");
            textBar.Controls.Add(Lbl("gms text"));
            textBar.Controls.Add(gmsTextBox);
            tips.SetToolTip(gmsTextBox, "The description text of the level on the gms-client and the server, written to the one language in \"lang\"");
            textBar.Controls.Add(Lbl("lang"));
            textBar.Controls.Add(langBox);
            foreach (string l in Profile.GmsClient.Languages) langBox.Items.Add(l);
            tips.SetToolTip(langBox, "The language the gms text goes to (en if empty); the other languages keep their text");
            textBar.Controls.Add(Btn("Set tooltip", "Writes the level's tooltip into the mod; both boxes empty = the mod leaves it", delegate { SetTooltip(); }));

            grid.VirtualMode = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToOrderColumns = false;
            grid.RowHeadersVisible = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            grid.BackgroundColor = allGrid.BackgroundColor = SystemColors.Window;      // the room under the last row reads as the table, not as an empty block
            grid.DefaultCellStyle.BackColor = SystemColors.Window;
            grid.DefaultCellStyle.ForeColor = SystemColors.WindowText;
            grid.Columns.Add(ViewLayout.FitColumn(new DataGridViewTextBoxColumn { HeaderText = "field", ReadOnly = true, Width = 220, SortMode = DataGridViewColumnSortMode.NotSortable }));
            grid.Columns.Add(ViewLayout.FitColumn(new DataGridViewTextBoxColumn { HeaderText = "mod value", Width = 140, SortMode = DataGridViewColumnSortMode.NotSortable }));
            foreach (string s in SideNames)
                grid.Columns.Add(ViewLayout.FitColumn(new DataGridViewTextBoxColumn { HeaderText = s + " (shipped)", ReadOnly = true, Width = 140, SortMode = DataGridViewColumnSortMode.NotSortable }));
            grid.CellValueNeeded += OnCellValueNeeded;
            grid.CellValuePushed += OnCellValuePushed;
            grid.CellFormatting += OnCellFormatting;
            grid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e)
            {
                GridRow r = RowAt(e.RowIndex);
                if (r == null || r.Heading != null || shownId == 0) e.Cancel = true;
            };

            allGrid.ReadOnly = true;
            allGrid.AllowUserToAddRows = false;
            allGrid.AllowUserToDeleteRows = false;
            allGrid.AllowUserToOrderColumns = false;
            allGrid.AllowUserToResizeRows = false;
            allGrid.RowHeadersVisible = false;
            allGrid.MultiSelect = false;
            allGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            allGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            allGrid.DefaultCellStyle.BackColor = SystemColors.Window;
            allGrid.DefaultCellStyle.ForeColor = SystemColors.WindowText;
            allGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(204, 228, 247);   // the level shown above, light so the marks stay visible
            allGrid.DefaultCellStyle.SelectionForeColor = SystemColors.WindowText;
            allGrid.CellClick += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (filling || e.RowIndex < 0 || e.RowIndex >= allLevels.Count || !levelBox.Items.Contains(allLevels[e.RowIndex])) return;
                levelBox.SelectedItem = allLevels[e.RowIndex];
            };
            foreach (string sn in SideNames) allSideBox.Items.Add(sn);
            allSideBox.SelectedIndex = 0;
            allSideBox.SelectedIndexChanged += delegate { if (!filling) FillAll(); };
            tips.SetToolTip(allSideBox, "The target whose values the all-levels table shows");
            FlowLayoutPanel allBar = Bar();
            allBar.Controls.Add(Lbl("All levels: values of"));
            allBar.Controls.Add(allSideBox);
            allBar.Controls.Add(Lbl("marked = changed by the mod; (d) = default, left out; a click picks the level"));
            tips.SetToolTip(allBar, "A marked cell shows the result of the mod's value, its tip gives the shipped and the mod value. (d): the target leaves the attribute out and the client's default applies");
            var allPanel = new Panel { Dock = DockStyle.Fill };
            allPanel.Controls.Add(allGrid);
            allPanel.Controls.Add(allBar);

            var bars = new Panel { Dock = DockStyle.Fill };
            bars.Controls.Add(textBar);
            bars.Controls.Add(cloneBar);
            bars.Controls.Add(skillBar);
            var iconPad = new Panel { Dock = DockStyle.Left, Width = 72, Padding = new Padding(4, 4, 4, 4) };
            iconPad.Controls.Add(iconBox);
            var head = new Panel { Dock = DockStyle.Top, Height = 104 };
            head.Controls.Add(bars);
            head.Controls.Add(iconPad);
            tips.SetToolTip(iconBox, "The skill's icon (ui@icon), read from Resource/Image.m2d of your data; an empty box: no data folder has that image");

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(allPanel);
            right.Controls.Add(new Splitter { Dock = DockStyle.Top, Height = 4 });
            right.Controls.Add(grid);
            right.Controls.Add(head);
            bool fitting = false;
            EventHandler fitHead = delegate
            {
                if (fitting) return;
                fitting = true;
                try
                {
                    int want = Math.Max(72, skillBar.Height + cloneBar.Height + textBar.Height);
                    int room = right.ClientSize.Height - 2 * (grid.ColumnHeadersHeight + 4 * grid.RowTemplate.Height + 4) - allBar.Height;
                    if (room > 0) want = Math.Min(want, Math.Max(72, room));
                    if (head.Height != want) head.Height = want;
                    FitGrid(right.ClientSize.Height - head.Height - allBar.Height);
                }
                finally { fitting = false; }
            };
            right.Resize += fitHead;
            bars.Layout += delegate { fitHead(bars, EventArgs.Empty); };
            fitGridNow = delegate { fitHead(right, EventArgs.Empty); };

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
            hitList.DrawItem += OnDrawHit;
            hitList.SelectedIndexChanged += delegate { if (!filling) Guard(delegate { OpenSelected(); }); };
            kindBox.SelectedIndexChanged += delegate
            {
                if (filling) return;
                jobBar.Visible = Kind == SkillKind.Skill;
                cloneIdBox.Text = cloneLevelsBox.Text = cloneNameBox.Text = cloneOwnBox.Text = "";
                cloneGroupBox.Checked = false;
                cloneGroupBox.Enabled = Kind == SkillKind.Skill;
                Guard(delegate { ShowSkill(0, null); FillLanguages(); Find(); });
            };
            namesBox.SelectedIndexChanged += delegate
            {
                if (filling) return;
                nameLanguage = namesBox.SelectedIndex <= 0 ? null : (string)namesBox.SelectedItem;
                Guard(delegate { Find(); if (shownId != 0) Open(shownId); });
            };
            levelBox.SelectedIndexChanged += delegate
            {
                if (filling || levelBox.SelectedItem == null) return;
                grid.EndEdit();
                level = (int)levelBox.SelectedItem;
                FillRows();
            };
        }

        SkillKind Kind { get { return kindBox.SelectedIndex == 1 ? SkillKind.Effect : SkillKind.Skill; } }

        MethodInvoker fitGridNow;

        public override void Initialize()
        {
            base.Initialize();
            Guard(delegate { FillLanguages(); Find(); });
        }

        /// <summary>
        /// The per-level grid as high as its rows, at most 3/5 of the room or, when the all-levels table has few levels,
        /// all the room that table does not need (the all-levels table gets the rest).
        /// </summary>
        void FitGrid(int room)
        {
            int want = grid.ColumnHeadersHeight + Math.Max(rows.Count, 4) * grid.RowTemplate.Height + 3;
            int allNeed = allGrid.ColumnHeadersHeight + (Math.Max(levelBox.Items.Count, 1) + 1) * allGrid.RowTemplate.Height + SystemInformation.HorizontalScrollBarHeight + 3;
            int max = Math.Max(grid.ColumnHeadersHeight + 4 * grid.RowTemplate.Height + 3, Math.Max(room * 3 / 5, room - allNeed));
            want = Math.Min(want, max);
            if (grid.Height != want) grid.Height = want;
        }

        /// <summary>The "Names in" choices: own language, then every language a target has names in.</summary>
        void FillLanguages()
        {
            SkillEditor ed = Editor();
            bool was = filling;
            filling = true;
            try
            {
                string keep = nameLanguage;
                namesBox.Items.Clear();
                namesBox.Items.Add("own language");
                namesBox.SelectedIndex = 0;
                foreach (string l in ed.NameLanguages(Kind))
                {
                    namesBox.Items.Add(l);
                    if (l == keep) namesBox.SelectedIndex = namesBox.Items.Count - 1;
                }
                nameLanguage = namesBox.SelectedIndex <= 0 ? null : keep;
            }
            finally { filling = was; }
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
                if (Guard(delegate { grid.EndEdit(); host.NeedHandle(); })) start();
            };
            tips.SetToolTip(b, tip);
            return b;
        }

        protected override void OnEnter(EventArgs e)
        {
            TablesView.ItemsInUse = true;
            base.OnEnter(e);
        }

        // ---- the MS2 Tables view ----

        void OnHostTarget()
        {
            bool was = filling;
            filling = true;
            targetBox.SelectedIndex = host.TargetIndex;
            filling = was;
        }

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
            editor = null;
            if (h == null) return;
            h.Changed += OnHostChanged;
            h.Flushing += OnHostFlushing;
            h.BusyChanged += OnHostBusy;
            h.Said += OnHostSaid;
            h.TargetChanged += OnHostTarget;
            OnHostTarget();
            outputBox.Text = h.OutputText;
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
                if (h == null) { outputBox.Text = "the MS2 Tables view could not be opened; the skill editor works on the mod of that view"; return false; }
                Hook(h);
            }
            catch (Exception ex)
            {
                outputBox.Text = "the MS2 Tables view could not be opened: " + ex.Message;
                Logger.Warning("MS2 Skills: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        SkillEditor Editor()
        {
            EditorSession s = host.SharedSession();
            if (editor == null || editor.Mod != s.Mod || editor.Data != s.Data) editor = s.Skills();
            editor.NameLanguage = nameLanguage;
            return editor;
        }

        void OnHostSaid(string text) { outputBox.Text = text; }

        void OnHostBusy(bool on) { ViewLayout.Lock(this, lockable, on, outputBox); }

        void OnHostFlushing() { grid.EndEdit(); }

        void OnHostChanged()
        {
            iconOf.Clear();
            grid.CancelEdit();
            long id = shownId;
            try
            {
                FillHits(id);
                if (id != 0) Open(id); else ShowSkill(0, null);
            }
            catch (ModKitException) { ShowSkill(0, null); }
            catch (System.IO.IOException) { ShowSkill(0, null); }
        }

        void Edited()
        {
            grid.Invalidate();
            FillAll();
            RefreshMarks();
            host.EditedOutside();
        }

        /// <summary>The "*" marks of the listed shipped ids, as the mod is now.</summary>
        void RefreshMarks()
        {
            bool was = filling;
            filling = true;
            try
            {
                int sel = hitList.SelectedIndex;
                for (int i = 0; i < hitList.Items.Count; i++)
                {
                    var h = hitList.Items[i] as Hit;
                    if (h == null || h.New) continue;
                    bool m = editor.Op(Kind, h.Id, false) != null;
                    if (m == h.Marked) continue;
                    h.Marked = m;
                    h.Text = h.Base + (m ? "  *" : "");
                    hitList.Items[i] = h;
                }
                if (hitList.SelectedIndex != sel) hitList.SelectedIndex = sel;
            }
            finally { filling = was; }
        }

        // ---- search ----

        void Find()
        {
            grid.EndEdit();
            FillHits(shownId);
            if (hitList.Items.Count > 0 && hitList.SelectedIndex < 0) hitList.SelectedIndex = 0;
        }

        void FillHits(long keep)
        {
            SkillEditor ed = Editor();
            string text = searchBox.Text.Trim();
            var hits = new List<Hit>();
            foreach (SkillOp op in ed.Mod.Skills)
            {
                if (op.Kind != Kind || !op.IsClone) continue;
                string t = op.Id.ToString(CultureInfo.InvariantCulture);
                if (text.Length > 0 && t.IndexOf(text, StringComparison.Ordinal) < 0 && (op.Name ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                hits.Add(new Hit { Id = op.Id, New = true, Text = t + "  " + (op.Name ?? "") + "  (new, clone of " + op.Clone + ")" });
            }
            foreach (KeyValuePair<long, string> kv in ed.Find(Kind, text, MaxHits))
            {
                var h = new Hit { Id = kv.Key, Base = kv.Key.ToString(CultureInfo.InvariantCulture) + "  " + kv.Value, Marked = ed.Op(Kind, kv.Key, false) != null };
                h.Text = h.Base + (h.Marked ? "  *" : "");
                hits.Add(h);
            }
            filling = true;
            try
            {
                hitList.BeginUpdate();
                hitList.Items.Clear();
                foreach (Hit h in hits)
                {
                    hitList.Items.Add(h);
                    if (h.Id == keep) hitList.SelectedIndex = hitList.Items.Count - 1;
                }
                hitList.EndUpdate();
            }
            finally { filling = false; }
            hitsLabel.Text = hits.Count + (hits.Count >= MaxHits ? "+" : "") + (Kind == SkillKind.Skill ? " skill(s)" : " effect(s)") + "; * = the mod changes it" +
                (string.IsNullOrEmpty(ed.NamesUsed) ? "" : "; names: " + ed.NamesUsed);
        }

        // ---- icons ----

        IconCache Cache()
        {
            if (host == null) return null;
            try { return IconCache.For(host.SharedSession().Data); }
            catch (ModKitException) { return null; }
            catch (System.IO.IOException) { return null; }
        }

        string IconEntryOf(long id)
        {
            string entry;
            if (iconOf.TryGetValue(id, out entry)) return entry;
            entry = null;
            try { entry = Editor().IconEntry(Kind, id); }
            catch (ModKitException) { }
            catch (System.IO.IOException) { }
            iconOf[id] = entry;
            return entry;
        }

        // a row of the list: the icon (a blank box when there is none), then the text
        void OnDrawHit(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= hitList.Items.Count) return;
            var h = hitList.Items[e.Index] as Hit;
            if (h == null) return;
            int size = e.Bounds.Height - 4;
            var box = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 2, size, size);
            IconCache cache = Cache();
            string entry = cache != null ? IconEntryOf(h.Id) : null;
            IconCache.Draw(e.Graphics, box, cache != null ? cache.Get(entry, hitList) : null);
            var text = new Rectangle(box.Right + 6, e.Bounds.Y, e.Bounds.Width - box.Width - 10, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, h.ToString(), e.Font, text, e.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        }

        void OpenSelected()
        {
            var h = hitList.SelectedItem as Hit;
            if (h == null) return;
            grid.EndEdit();
            Open(h.Id);
            host.SayOutside(Describe());
        }

        void Open(long id)
        {
            SkillEditor ed = Editor();
            SkillOp op = ed.Op(Kind, id, false);
            long source = op != null && op.IsClone ? op.Clone : id;
            ShowSkill(id, ed.Open(Kind, source));
        }

        string Describe()
        {
            if (shownId == 0) return "";
            var sb = new StringBuilder();
            SkillOp op = editor.Op(Kind, shownId, false);
            sb.Append(Kind == SkillKind.Skill ? "skill " : "effect ").Append(shownId);
            if (op != null && op.IsClone) sb.Append(" (new in mod ").Append(editor.Mod.Id).Append(", clone of ").Append(op.Clone).Append(')');
            sb.Append(':');
            if (info == null) { sb.Append(" no target ships it"); return sb.ToString(); }
            foreach (SkillSideInfo s in info.Sides)
            {
                sb.Append(" ").Append(s.Side).Append(' ').Append(s.Entry).Append(" (").Append(s.Levels.Count).Append(" levels");
                if (s.Variants.Count > 0) sb.Append(", ").Append(s.Variants.Count).Append(" in variants");
                sb.Append(");");
            }
            return sb.ToString();
        }

        // ---- the skill ----

        void ShowSkill(long id, SkillInfo i)
        {
            grid.CancelEdit();
            shownId = i == null && (editor == null || editor.Op(Kind, id, false) == null) ? 0 : id;
            info = i;
            filling = true;
            levelBox.Items.Clear();
            var levels = new SortedDictionary<int, bool>();
            if (i != null) foreach (SkillSideInfo s in i.Sides) foreach (int l in s.Levels.Keys) levels[l] = true;
            foreach (int l in levels.Keys) levelBox.Items.Add(l);
            if (levels.Count > 0) { if (!levels.ContainsKey(level)) foreach (int l in levels.Keys) { level = l; break; } levelBox.SelectedItem = level; }
            filling = false;
            if (shownId == 0) skillLabel.Text = "(nothing chosen)";
            else
            {
                SkillOp op = editor.Op(Kind, shownId, false);
                string name = op != null && op.Name != null ? op.Name : i != null ? i.Name : null;
                skillLabel.Text = shownId + (name != null ? "  " + name : "") + (op != null && op.IsClone ? "  (clone of " + op.Clone + ")" : "");
            }
            string entry = i == null ? null : Icons.ImageEntry(i.Icon);
            iconBox.Show(entry != null ? Cache() : null, entry);
            FillRows();
        }

        SkillValues Values(SkillSideInfo s, GridRow r) { return Values(s, r, level); }

        static SkillValues Values(SkillSideInfo s, GridRow r, int lv)
        {
            SkillValues v;
            if (s == null || !s.Levels.TryGetValue(lv, out v)) return null;
            if (r.Scope == SkillScope.Level) return v;
            if (r.Motion > v.Children.Count) return null;
            v = v.Children[r.Motion - 1];
            if (r.Scope == SkillScope.Motion) return v;
            return r.Attack > v.Children.Count ? null : v.Children[r.Attack - 1];
        }

        void FillRows()
        {
            grid.CancelEdit();
            grid.RowCount = 0;
            rows.Clear();
            FillAll();
            if (shownId == 0 || editor == null) { if (fitGridNow != null) fitGridNow(); return; }
            kmsParamBox.Text = gmsTextBox.Text = "";
            langBox.Text = "en";
            SkillOp mine = editor.Op(Kind, shownId, false);
            if (mine != null)
                foreach (SkillText t in mine.Texts)
                    if (SkillLevels.Has(t.Levels, level)) { kmsParamBox.Text = t.KmsParam ?? ""; gmsTextBox.Text = t.GmsText ?? ""; langBox.Text = t.Lang; }
            int motions = 0;
            var attacks = new Dictionary<int, int>();
            if (info != null)
                foreach (SkillSideInfo s in info.Sides)
                {
                    SkillValues v;
                    if (!s.Levels.TryGetValue(level, out v)) continue;
                    motions = Math.Max(motions, v.Children.Count);
                    for (int m = 0; m < v.Children.Count; m++)
                    {
                        int a;
                        attacks.TryGetValue(m + 1, out a);
                        attacks[m + 1] = Math.Max(a, v.Children[m].Children.Count);
                    }
                }
            rows.Add(new GridRow { Heading = "Level " + level });
            foreach (string f in editor.Fields(Kind, SkillScope.Level)) rows.Add(new GridRow { Scope = SkillScope.Level, Field = f });
            string[] mf = editor.Fields(Kind, SkillScope.Motion), af = editor.Fields(Kind, SkillScope.Attack);
            for (int m = 1; m <= motions; m++)
            {
                if (mf.Length > 0 || af.Length > 0) rows.Add(new GridRow { Heading = "Motion " + m });
                foreach (string f in mf) rows.Add(new GridRow { Scope = SkillScope.Motion, Motion = m, Field = f });
                int an;
                attacks.TryGetValue(m, out an);
                for (int a = 1; a <= an; a++)
                {
                    if (af.Length == 0) break;
                    rows.Add(new GridRow { Heading = "Motion " + m + " attack " + a });
                    foreach (string f in af) rows.Add(new GridRow { Scope = SkillScope.Attack, Motion = m, Attack = a, Field = f });
                }
            }
            grid.RowCount = rows.Count;
            grid.Invalidate();
            if (fitGridNow != null) fitGridNow();
        }

        // ---- all levels ----

        static string ColumnName(GridRow c)
        {
            if (c.Scope == SkillScope.Level) return c.Field;
            if (c.Scope == SkillScope.Motion) return "m" + c.Motion + " " + c.Field;
            return "m" + c.Motion + " a" + c.Attack + " " + c.Field;
        }

        /// <summary>The mod's value applied to a shipped one: "*k" times the shipped number, else the value itself.</summary>
        static string Result(string shipped, string mod)
        {
            if (!mod.StartsWith("*", StringComparison.Ordinal)) return mod;
            double k, v;
            if (shipped == null || !double.TryParse(mod.Substring(1), NumberStyles.Float, CultureInfo.InvariantCulture, out k)
                || !double.TryParse(shipped, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return mod;
            return (v * k).ToString("0.####", CultureInfo.InvariantCulture);
        }

        /// <summary>The read-only table of every level: rows = levels, columns = the mapped fields of each motion and attack.</summary>
        void FillAll()
        {
            bool was = filling;
            filling = true;
            try
            {
                allGrid.Rows.Clear();
                allGrid.Columns.Clear();
                allCols.Clear();
                allLevels.Clear();
                if (shownId == 0 || editor == null || info == null) return;
                SkillSideInfo side = info.Sides.Find(x => x.Side == SideNames[Math.Max(0, allSideBox.SelectedIndex)]);
                int motions = 0;
                var attacks = new Dictionary<int, int>();
                var levels = new SortedDictionary<int, bool>();
                foreach (SkillSideInfo s in info.Sides)
                    foreach (KeyValuePair<int, SkillValues> kv in s.Levels)
                    {
                        levels[kv.Key] = true;
                        motions = Math.Max(motions, kv.Value.Children.Count);
                        for (int m = 0; m < kv.Value.Children.Count; m++)
                        {
                            int a;
                            attacks.TryGetValue(m + 1, out a);
                            attacks[m + 1] = Math.Max(a, kv.Value.Children[m].Children.Count);
                        }
                    }
                foreach (string f in editor.Fields(Kind, SkillScope.Level)) allCols.Add(new GridRow { Scope = SkillScope.Level, Field = f });
                for (int m = 1; m <= motions; m++)
                {
                    foreach (string f in editor.Fields(Kind, SkillScope.Motion)) allCols.Add(new GridRow { Scope = SkillScope.Motion, Motion = m, Field = f });
                    int an;
                    attacks.TryGetValue(m, out an);
                    for (int a = 1; a <= an; a++)
                        foreach (string f in editor.Fields(Kind, SkillScope.Attack)) allCols.Add(new GridRow { Scope = SkillScope.Attack, Motion = m, Attack = a, Field = f });
                }
                allGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "level", MinimumWidth = 44, FillWeight = 60, SortMode = DataGridViewColumnSortMode.NotSortable });
                foreach (GridRow c in allCols)
                {
                    SkillField f = editor.Field(Kind, c.Field);
                    allGrid.Columns.Add(new DataGridViewTextBoxColumn
                    {
                        HeaderText = ColumnName(c) + (f != null && f.Unit.Length > 0 ? " (" + f.Unit + ")" : ""),
                        MinimumWidth = 64, SortMode = DataGridViewColumnSortMode.NotSortable,
                    });
                }
                Color mark = Color.FromArgb(255, 236, 170);
                int current = -1;
                foreach (int lv in levels.Keys)
                {
                    int ri = allGrid.Rows.Add();
                    DataGridViewRow row = allGrid.Rows[ri];
                    allLevels.Add(lv);
                    if (lv == level) current = ri;
                    row.Cells[0].Value = lv.ToString(CultureInfo.InvariantCulture);
                    for (int i = 0; i < allCols.Count; i++)
                    {
                        GridRow c = allCols[i];
                        DataGridViewCell cell = row.Cells[i + 1];
                        SkillValues v = Values(side, c, lv);
                        string mod = editor.Get(Kind, shownId, lv, c.Motion, c.Attack, c.Field);
                        if (v == null) { cell.Value = ""; continue; }
                        string t;
                        string shipped = v.Values.TryGetValue(c.Field, out t) ? t : null;
                        bool def = v.Defaulted.Contains(c.Field);
                        if (mod == null) { cell.Value = shipped == null ? "-" : shipped + (def ? " (d)" : ""); continue; }
                        cell.Value = Result(shipped, mod);
                        cell.Style.BackColor = mark;
                        cell.Style.SelectionBackColor = Color.FromArgb(240, 205, 110);
                        cell.ToolTipText = "shipped " + (shipped ?? "-") + (def ? " (default)" : "") + ", mod " + mod;
                    }
                }
                if (current >= 0) { allGrid.ClearSelection(); allGrid.Rows[current].Selected = true; }
            }
            finally { filling = was; }
        }

        GridRow RowAt(int i) { return i >= 0 && i < rows.Count ? rows[i] : null; }

        void OnCellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            GridRow r = RowAt(e.RowIndex);
            if (r == null) return;
            if (r.Heading != null) { e.Value = e.ColumnIndex == 0 ? r.Heading : ""; return; }
            if (e.ColumnIndex == 0)
            {
                SkillField f = editor.Field(Kind, r.Field);
                e.Value = "  " + r.Field + (f != null && f.Unit.Length > 0 ? " (" + f.Unit + ")" : "");
                return;
            }
            if (e.ColumnIndex == 1) { e.Value = editor.Get(Kind, shownId, level, r.Motion, r.Attack, r.Field) ?? ""; return; }
            string side = SideNames[e.ColumnIndex - 2];
            e.Value = "";
            if (info == null) return;
            foreach (SkillSideInfo s in info.Sides)
            {
                if (s.Side != side) continue;
                SkillValues v = Values(s, r);
                string t;
                e.Value = v == null ? "(no such level/motion/attack)" : !v.Values.TryGetValue(r.Field, out t) ? "-" : v.Defaulted.Contains(r.Field) ? t + " (default)" : t;
            }
        }

        void OnCellValuePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            GridRow r = RowAt(e.RowIndex);
            if (r == null || r.Heading != null || e.ColumnIndex != 1) return;
            string v = e.Value == null ? "" : e.Value.ToString();
            Guard(delegate
            {
                editor.Set(Kind, shownId, level, r.Motion, r.Attack, r.Field, v);
                Edited();
            });
        }

        void OnCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            GridRow r = RowAt(e.RowIndex);
            if (r == null) return;
            if (r.Heading != null)
            {
                e.CellStyle.BackColor = SystemColors.Control;
                e.CellStyle.Font = bold.Of(grid.Font);
            }
            else if (e.ColumnIndex == 1 && e.Value is string && ((string)e.Value).Length > 0)
                e.CellStyle.BackColor = Color.LightYellow;
        }

        // ---- commands ----

        static long ParseId(string text, string what)
        {
            long id;
            if (!long.TryParse((text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id)) throw new ModKitException(what + " \"" + text + "\" is not an id");
            return id;
        }

        void Clone()
        {
            if (shownId == 0 || info == null) throw new ModKitException("choose a shipped " + (Kind == SkillKind.Skill ? "skill" : "effect") + " to clone first");
            long newId = ParseId(cloneIdBox.Text, "new id");
            var own = new List<KeyValuePair<long, long>>();
            foreach (string part in cloneOwnBox.Text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = part.Split('=');
                if (kv.Length != 2) throw new ModKitException("own effects: \"" + part.Trim() + "\" is not source=new");
                own.Add(new KeyValuePair<long, long>(ParseId(kv[0], "own effect"), ParseId(kv[1], "own effect")));
            }
            editor.Clone(Kind, info.Id, newId, cloneLevelsBox.Text.Trim(), cloneNameBox.Text.Trim(), own, Kind == SkillKind.Skill && cloneGroupBox.Checked ? "own" : null);
            Edited();
            FillHits(newId);
            Open(newId);
            host.SayOutside(Describe() + "\r\nPlan shows the rows and files the clone writes on each target.");
        }

        void RemoveClone()
        {
            SkillOp op = shownId == 0 ? null : editor.Op(Kind, shownId, false);
            if (op == null || !op.IsClone) throw new ModKitException("the chosen id is not a clone of this mod");
            editor.Mod.Skills.Remove(op);
            editor.Mod.JobSkills.RemoveAll(j => j.Skill == op.Id);
            Edited();
            ShowSkill(0, null);
            FillHits(0);
            WhereUsedJump.SayLeftBehind(host, (op.Kind == SkillKind.Effect ? "effect " : "skill ") + op.Id + " taken out of the mod",
                op.Kind == SkillKind.Effect ? IdLookup.Effect : IdLookup.Skill, op.Id);
        }

        void JobRow()
        {
            if (shownId == 0) throw new ModKitException("choose a skill first");
            int job = (int)ParseId(jobBox.Text, "job");
            long replace = replaceBox.Text.Trim().Length == 0 ? -1 : ParseId(replaceBox.Text, "replace skill");
            int tier = tierBox.Text.Trim().Length == 0 ? 1 : (int)ParseId(tierBox.Text, "tier");
            JobSkillOp j = editor.JobRow(job, shownId, replace, positionBox.Text.Trim(), tier);
            Edited();
            host.SayOutside("job " + j.Job + ": skill " + j.Skill + (j.Replace >= 0 ? " takes the slot of " + j.Replace : " at " + j.Position + " tier " + j.Tier) + "; Plan shows the rows on each target");
        }

        void SetTooltip()
        {
            if (shownId == 0) throw new ModKitException("choose a skill or effect first");
            string lang = langBox.Text.Trim();
            editor.SetTooltip(Kind, shownId, level, kmsParamBox.Text.Trim(), gmsTextBox.Text.Trim(), lang.Length == 0 ? null : lang);
            Edited();
            host.SayOutside((Kind == SkillKind.Skill ? "skill " : "effect ") + shownId + " level " + level + ": tooltip set in the mod");
        }
    }
}
