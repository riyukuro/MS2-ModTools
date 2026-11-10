// The "MS2 Skills" view: one player skill or additional effect at a time. A Skills / Effects switch, a search box (id
// or name), the list of hits, the icon, a level picker, and one grid with the mapped fields of the chosen level: the
// level's own fields, then each motion and each attack of the level. The "mod value" column is what the mod sets (a
// number in neutral units, or *k for the shipped value times k); the other columns show the shipped value of each
// target. Below: clone to a new id, a job skill tree row, the level's tooltip, and Plan / Apply / Revert with a
// two-line output box.
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
            internal string Text;
            public override string ToString() { return Text; }
        }

        TablesView host;
        SkillEditor editor;
        SkillInfo info;         // the shipped skill (for a clone of the mod: its source)
        long shownId;           // the id the grid edits
        int level = 1;
        readonly List<GridRow> rows = new List<GridRow>();
        bool filling;

        readonly ComboBox kindBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
        readonly TextBox searchBox = new TextBox { Width = 200 };
        readonly Label hitsLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListBox hitList = new ListBox { Dock = DockStyle.Left, Width = 280, IntegralHeight = false };
        readonly Label skillLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 8, 0) };
        readonly ComboBox levelBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };
        readonly TextBox cloneIdBox = new TextBox { Width = 80 };
        readonly TextBox cloneLevelsBox = new TextBox { Width = 50 };
        readonly TextBox cloneNameBox = new TextBox { Width = 150 };
        readonly TextBox cloneOwnBox = new TextBox { Width = 150 };
        readonly TextBox jobBox = new TextBox { Width = 40 };
        readonly TextBox replaceBox = new TextBox { Width = 80 };
        readonly TextBox positionBox = new TextBox { Width = 50 };
        readonly TextBox tierBox = new TextBox { Width = 30, Text = "1" };
        readonly TextBox kmsParamBox = new TextBox { Width = 110 };
        readonly TextBox gmsTextBox = new TextBox { Width = 320 };
        readonly FlowLayoutPanel jobBar;
        readonly DataGridView grid = new DataGridView { Dock = DockStyle.Fill };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Bottom, Height = 130 };
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
            tips.SetToolTip(levelBox, "The level the grid shows and edits");
            skillBar.Controls.Add(Btn("Clone to new id", "Adds a new skill or effect that starts as a copy of this one on each target that has it, under the id in the box (the mod's reserved range: skills 78000000-78099999, effects 77100000-77199999). levels: the levels kept (empty = all, \"1-3\"); name: empty = the source's; own effects: \"source=new, ...\" effects cloned with it and pointed at", delegate { Clone(); }));
            skillBar.Controls.Add(cloneIdBox);
            skillBar.Controls.Add(Lbl("levels"));
            skillBar.Controls.Add(cloneLevelsBox);
            skillBar.Controls.Add(Lbl("name"));
            skillBar.Controls.Add(cloneNameBox);
            skillBar.Controls.Add(Lbl("own effects"));
            skillBar.Controls.Add(cloneOwnBox);
            skillBar.Controls.Add(Btn("Remove clone", "Takes the chosen new skill or effect out of the mod again", delegate { RemoveClone(); }));

            jobBar = Bar();
            jobBar.Controls.Add(Lbl("Job"));
            jobBar.Controls.Add(jobBox);
            tips.SetToolTip(jobBox, "The job number of the job skill tree (as the job table writes it, e.g. 10)");
            jobBar.Controls.Add(Lbl("replace skill"));
            jobBar.Controls.Add(replaceBox);
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
            tips.SetToolTip(gmsTextBox, "The description text of the level on the gms-client and the server (written to every language they have)");
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
            grid.BackgroundColor = SystemColors.AppWorkspace;
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

            var bars = new Panel { Dock = DockStyle.Fill };
            bars.Controls.Add(textBar);
            bars.Controls.Add(jobBar);
            bars.Controls.Add(skillBar);
            var iconPad = new Panel { Dock = DockStyle.Left, Width = 72, Padding = new Padding(4, 4, 4, 4) };
            iconPad.Controls.Add(iconBox);
            var head = new Panel { Dock = DockStyle.Top, Height = 104 };
            head.Controls.Add(bars);
            head.Controls.Add(iconPad);
            tips.SetToolTip(iconBox, "The skill's icon (ui@icon), read from Resource/Image.m2d of your data; a box with a cross: no data folder has that image");

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(grid);
            right.Controls.Add(head);
            bool fitting = false;
            EventHandler fitHead = delegate
            {
                if (fitting) return;
                fitting = true;
                try
                {
                    int want = Math.Max(72, skillBar.Height + (jobBar.Visible ? jobBar.Height : 0) + textBar.Height);
                    int room = right.ClientSize.Height - (grid.ColumnHeadersHeight + 4 * grid.RowTemplate.Height + 4);
                    if (room > 0) want = Math.Min(want, Math.Max(72, room));
                    if (head.Height != want) head.Height = want;
                }
                finally { fitting = false; }
            };
            right.Resize += fitHead;
            bars.Layout += delegate { fitHead(bars, EventArgs.Empty); };

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
            kindBox.SelectedIndexChanged += delegate
            {
                if (filling) return;
                jobBar.Visible = Kind == SkillKind.Skill;
                Guard(delegate { ShowSkill(0, null); Find(); });
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
                    IViewService views = Platform.GetService<IViewService>();
                    h = views == null ? null : views.CreateOrFindView(TablesView.ViewId) as TablesView;
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
            return editor;
        }

        void OnHostSaid(string text) { outputBox.Text = text; }

        void OnHostBusy(bool on)
        {
            foreach (Control c in lockable) c.Enabled = !on;
            UseWaitCursor = on;
        }

        void OnHostFlushing() { grid.EndEdit(); }

        void OnHostChanged()
        {
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
            host.EditedOutside();
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
                hits.Add(new Hit { Id = op.Id, Text = t + "  " + (op.Name ?? "") + "  (new, clone of " + op.Clone + ")" });
            }
            foreach (KeyValuePair<long, string> kv in ed.Find(Kind, text, MaxHits))
                hits.Add(new Hit { Id = kv.Key, Text = kv.Key.ToString(CultureInfo.InvariantCulture) + "  " + kv.Value + (ed.Op(Kind, kv.Key, false) != null ? "  *" : "") });
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
            hitsLabel.Text = hits.Count + (hits.Count >= MaxHits ? "+" : "") + (Kind == SkillKind.Skill ? " skill(s)" : " effect(s)") + "; * = the mod changes it";
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
            IconCache cache = null;
            string entry = i == null ? null : Icons.ImageEntry(i.Icon);
            if (host != null && entry != null)
            {
                try { cache = IconCache.For(host.SharedSession().Data); }
                catch (ModKitException) { }
                catch (System.IO.IOException) { }
            }
            iconBox.Show(cache, entry);
            FillRows();
        }

        SkillValues Values(SkillSideInfo s, GridRow r)
        {
            SkillValues v;
            if (!s.Levels.TryGetValue(level, out v)) return null;
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
            if (shownId == 0 || editor == null) return;
            kmsParamBox.Text = gmsTextBox.Text = "";
            SkillOp mine = editor.Op(Kind, shownId, false);
            if (mine != null)
                foreach (SkillText t in mine.Texts)
                    if (SkillLevels.Has(t.Levels, level)) { kmsParamBox.Text = t.KmsParam ?? ""; gmsTextBox.Text = t.GmsText ?? ""; }
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
                e.Value = v == null ? "(no such level/motion/attack)" : v.Values.TryGetValue(r.Field, out t) ? t : "-";
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
                e.CellStyle.Font = new Font(grid.Font, FontStyle.Bold);
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
            editor.Clone(Kind, info.Id, newId, cloneLevelsBox.Text.Trim(), cloneNameBox.Text.Trim(), own);
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
            editor.SetTooltip(Kind, shownId, level, kmsParamBox.Text.Trim(), gmsTextBox.Text.Trim());
            Edited();
            host.SayOutside((Kind == SkillKind.Skill ? "skill " : "effect ") + shownId + " level " + level + ": tooltip set in the mod");
        }
    }
}
