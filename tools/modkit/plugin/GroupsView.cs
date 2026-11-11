// The "MS2 Shops and Drops" view: one shop or one drop box at a time, in the style of the MS2 NPCs view. A switch
// between shops and drop boxes, a search box (id), the list of hits, the target to show (a shop is on the server only;
// a drop box is on the server's final file, the Korean client's final file and the flat splits of the server and the
// global client), the header fields as a form, the rows as a grid (add, remove, edit), Clone / New / Discard, and the
// Plan / Apply / Revert buttons and output box of the MS2 Tables view.
//
// What the view shows is the group as the mod leaves it (MS2.ModKit.GroupEditor plans the group's op and reads the
// result back). The view holds no data logic and no mod of its own: every edit is an op of the mod the MS2 Tables
// view is editing, and the session, the worker thread and the output stay in that view.
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
    public class GroupsView : AbstractView
    {
        public const string ViewId = "MS2.Tables.GroupsView";
        const int MaxHits = 500;

        sealed class FormRow
        {
            internal string Heading;
            internal string Group;   // drop boxes: a field of this <group>; null: a header field
            internal string Field;
            internal int Header;     // shops: which shop_game_info row
        }

        TablesView host;
        GroupEditor editor, shippedEditor;
        DataSet shippedData;
        GroupView view, shippedView;
        GroupSideView side;
        readonly List<FormRow> form = new List<FormRow>();
        readonly List<GroupRowView> gridRows = new List<GroupRowView>();
        readonly List<string> columns = new List<string>();
        int removedFrom;   // grid rows from here on are rows the mod removes (shown grey)
        bool filling;

        readonly ComboBox kindBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        readonly TextBox searchBox = new TextBox { Width = 160 };
        readonly Label hitsLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListBox hitList = new ListBox { Dock = DockStyle.Left, Width = 170, IntegralHeight = false };
        readonly Label groupLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 8, 0) };
        readonly ComboBox sideBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
        readonly TextBox cloneIdBox = new TextBox { Width = 90 };
        readonly TextBox fieldNameBox = new TextBox { Width = 130 };
        readonly TextBox fieldValueBox = new TextBox { Width = 130 };
        readonly TextBox fieldGroupBox = new TextBox { Width = 50 };
        readonly TextBox rowGroupBox = new TextBox { Width = 50 };
        readonly TextBox rowAttrsBox = new TextBox { Width = 220 };
        readonly DataGridView formGrid = new DataGridView { Dock = DockStyle.Top, Height = 170 };
        readonly DataGridView rowGrid = new DataGridView { Dock = DockStyle.Fill };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 130 };
        readonly ComboBox targetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();

        public GroupsView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            kindBox.Items.Add("Shops");
            kindBox.Items.Add("Drop boxes");
            kindBox.SelectedIndex = 0;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(kindBox);
            tips.SetToolTip(kindBox, "Shops (the server's shop tables) or drop boxes (the server's and the clients' drop tables)");
            top.Controls.Add(Lbl("id"));
            top.Controls.Add(searchBox);
            top.Controls.Add(Btn("Find", "Lists the shops or boxes whose id starts with the text; the ones the mod adds come first", delegate { Find(); }));
            top.Controls.Add(hitsLabel);

            FlowLayoutPanel applyBar = Bar();
            applyBar.Controls.Add(Lbl("Apply to"));
            targetBox.Items.Add("workspace (patched archives)");
            targetBox.Items.Add("game folder (loose files)");
            tips.SetToolTip(targetBox, "Where Apply writes the client's tables (the same choice as in the MS2 Tables view). The server's archives and the global client's always go to the workspace.");
            applyBar.Controls.Add(targetBox);
            applyBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change, entry by entry for each target; writes nothing", delegate { return host.DoPlan(); }));
            applyBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change, to every target at once", delegate { return host.DoApply(); }));
            applyBar.Controls.Add(Job("Revert", "Removes what Apply wrote, on every target (per-character shop state in a game database is not undone)", delegate { return host.DoRevert(); }));

            FlowLayoutPanel groupBar = Bar();
            groupBar.Controls.Add(groupLabel);
            groupBar.Controls.Add(Lbl("Show"));
            groupBar.Controls.Add(sideBox);
            tips.SetToolTip(sideBox, "The target whose rows are shown. An edit is written to every target where the row is found; Plan warns for a target that does not have it");
            groupBar.Controls.Add(Btn("Clone to new id", "Adds a copy of this shop or box under the id in the box (empty = the next free id of the mod's reserved range)", delegate { Clone(); }));
            groupBar.Controls.Add(cloneIdBox);
            groupBar.Controls.Add(Btn("New", "Adds an empty shop or box under the id in the box (empty = the next free reserved id); add rows to it below", delegate { New(); }));
            groupBar.Controls.Add(Btn("Discard", "Takes the mod's changes to this shop or box out again (a new or cloned one goes away)", delegate { Discard(); }));
            groupBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same mod and Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            FlowLayoutPanel fieldBar = Bar();
            fieldBar.Controls.Add(Lbl("Field"));
            fieldBar.Controls.Add(fieldNameBox);
            fieldBar.Controls.Add(Lbl("="));
            fieldBar.Controls.Add(fieldValueBox);
            fieldBar.Controls.Add(Lbl("of group"));
            fieldBar.Controls.Add(fieldGroupBox);
            tips.SetToolTip(fieldGroupBox, "Drop boxes: the dropGroupID whose field is set; empty = a field of the box itself");
            fieldBar.Controls.Add(Btn("Set field", "Sets (or adds) a header field: the shop's header row, the box's <dropBox> or one of its groups. An empty value removes the field", delegate { SetFieldFromBar(); }));

            FlowLayoutPanel rowBar = Bar();
            rowBar.Controls.Add(Lbl("New row"));
            rowBar.Controls.Add(rowAttrsBox);
            tips.SetToolTip(rowAttrsBox, "The new row's fields as name=value pairs separated by spaces, for example  id=5001 price=10 paymentType=5  (shops) or  itemID=5001 weight=10 minCount=1 maxCount=1  (drop boxes)");
            rowBar.Controls.Add(Lbl("in group"));
            rowBar.Controls.Add(rowGroupBox);
            rowBar.Controls.Add(Btn("Add row", "Adds the row (a shop: the planner gives it the next free sn unless sn= is given; a box: into the group in the box, made if missing)", delegate { AddRow(); }));
            rowBar.Controls.Add(Btn("Remove / restore row", "Removes the chosen row, or brings a removed (grey) row back", delegate { RemoveOrRestore(); }));

            SetupGrid(formGrid);
            formGrid.Columns.Add(Col("field", 260, true));
            formGrid.Columns.Add(Col("value", 220, false));
            formGrid.Columns.Add(Col("shipped value", 220, true));
            formGrid.CellValueNeeded += OnFormValueNeeded;
            formGrid.CellValuePushed += OnFormValuePushed;
            formGrid.CellFormatting += OnFormFormatting;
            formGrid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e)
            {
                FormRow r = FormAt(e.RowIndex);
                if (r == null || r.Heading != null) e.Cancel = true;
            };

            SetupGrid(rowGrid);
            rowGrid.CellValueNeeded += OnRowValueNeeded;
            rowGrid.CellValuePushed += OnRowValuePushed;
            rowGrid.CellFormatting += OnRowFormatting;
            rowGrid.CellPainting += OnRowPainting;
            rowGrid.RowTemplate.Height = IconRow;
            rowGrid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e)
            {
                if (e.RowIndex >= removedFrom || e.ColumnIndex < 2) e.Cancel = true;
            };

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(rowGrid);
            right.Controls.Add(rowBar);
            right.Controls.Add(new Splitter { Dock = DockStyle.Top, Height = 4 });
            right.Controls.Add(formGrid);
            right.Controls.Add(fieldBar);
            right.Controls.Add(groupBar);

            Controls.Add(right);
            Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
            Controls.Add(hitList);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(outputBox);
            Controls.Add(applyBar);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, applyBar, hitList, right });
            // the output box keeps two lines (it scrolls); the header form gets 30 % of the height beside the rows, so
            // the rows with icons get the larger share (both splitters set a new share when dragged). In a low window
            // the header form still shows its column header and four rows, and the rows below a header and three rows.
            ViewLayout.KeepLogLines(outputBox, this);
            ViewLayout.KeepHeightShare(formGrid, right, 0.3, formGrid.ColumnHeadersHeight + 4 * formGrid.RowTemplate.Height + 4,
                delegate { return groupBar.Height + fieldBar.Height + rowBar.Height + 4 + rowGrid.ColumnHeadersHeight + 3 * IconRow + 4; });
            ViewLayout.CentreOnFirstShow(this);
            targetBox.SelectedIndexChanged += delegate { if (!filling && host != null) host.TargetIndex = targetBox.SelectedIndex; };

            searchBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                Guard(delegate { Find(); });
            };
            kindBox.SelectedIndexChanged += delegate { if (!filling) Guard(delegate { ShowGroup(null); Find(); }); };
            hitList.SelectedIndexChanged += delegate { if (!filling) Guard(delegate { OpenSelected(); }); };
            sideBox.SelectedIndexChanged += delegate { if (!filling) Guard(delegate { ShowSide(sideBox.SelectedIndex); }); };
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

        // ---- the entry point for the NPC view ----

        /// <summary>Shows a shop or drop box by id (the NPC view's "Open shop" / "Open drop box").</summary>
        public void OpenGroup(GroupKind kind, long id)
        {
            Guard(delegate
            {
                filling = true;
                kindBox.SelectedIndex = kind == GroupKind.Shop ? 0 : 1;
                searchBox.Text = id.ToString(CultureInfo.InvariantCulture);
                filling = false;
                FillHits(id);
                GroupView v = Editor().Open(kind, id);
                ShowGroup(v);
                host.SayOutside(Describe(v));
            });
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

        static void SetupGrid(DataGridView g)
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
            g.BackgroundColor = SystemColors.AppWorkspace;
            g.DefaultCellStyle.BackColor = SystemColors.Window;
            g.DefaultCellStyle.ForeColor = SystemColors.WindowText;
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

        void EndEdits() { formGrid.EndEdit(); rowGrid.EndEdit(); }

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
            if (h == null) return;
            h.Changed += OnHostChanged;
            h.Flushing += EndEdits;
            h.BusyChanged += OnHostBusy;
            h.Said += OnHostSaid;
            h.TargetChanged += OnHostTarget;
            OnHostTarget();
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
                if (h == null) { OutputWrap.Set(outputBox, "the MS2 Tables view could not be opened; this view works on the mod of that view"); return false; }
                Hook(h);
            }
            catch (Exception ex)
            {
                OutputWrap.Set(outputBox, "the MS2 Tables view could not be opened: " + ex.Message);
                Logger.Warning("MS2 Shops and Drops: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        GroupEditor Editor()
        {
            EditorSession s = host.SharedSession();
            if (editor == null || editor.Mod != s.Mod || editor.Data != s.Data) editor = s.Groups();
            return editor;
        }

        /// <summary>An editor over the same data with an empty mod: the shipped header of a shipped group.</summary>
        GroupEditor Shipped()
        {
            GroupEditor ed = Editor();
            if (shippedEditor == null || shippedData != ed.Data)
            {
                shippedEditor = new GroupEditor(ed.Data, Mod.Parse("<mod id=\"shipped\" name=\"shipped\" version=\"1\"></mod>", "shipped"));
                shippedEditor.Maps = ed.Maps;
                shippedData = ed.Data;
            }
            return shippedEditor;
        }

        void OnHostSaid(string text) { OutputWrap.Set(outputBox, text); }

        void OnHostBusy(bool on)
        {
            foreach (Control c in lockable) c.Enabled = !on;
            UseWaitCursor = on;
        }

        void OnHostChanged()
        {
            iconOfItem.Clear();
            formGrid.CancelEdit();
            rowGrid.CancelEdit();
            GroupKind kind = Kind();
            long id = view != null ? view.Id : 0;
            ShowGroup(null);
            try
            {
                FillHits(id);
                if (id != 0 && Editor().Exists(kind, id)) ShowGroup(Editor().Open(kind, id));
            }
            catch (ModKitException) { ShowGroup(null); }
            catch (System.IO.IOException) { ShowGroup(null); }
        }

        GroupKind Kind() { return kindBox.SelectedIndex == 1 ? GroupKind.DropBox : GroupKind.Shop; }

        // ---- search ----

        void Find()
        {
            EndEdits();
            FillHits(view != null ? view.Id : 0);
            if (hitList.Items.Count > 0 && hitList.SelectedIndex < 0) hitList.SelectedIndex = 0;
        }

        void FillHits(long keep)
        {
            List<EntityHit> hits = Editor().Find(Kind(), searchBox.Text.Trim(), MaxHits);
            filling = true;
            try
            {
                hitList.BeginUpdate();
                hitList.Items.Clear();
                foreach (EntityHit h in hits)
                {
                    hitList.Items.Add(h);
                    if (h.Id == keep) hitList.SelectedIndex = hitList.Items.Count - 1;
                }
                hitList.EndUpdate();
            }
            finally { filling = false; }
            hitsLabel.Text = hits.Count + (hits.Count >= MaxHits ? "+" : "") + (Kind() == GroupKind.Shop ? " shop(s)" : " drop box(es)");
        }

        void OpenSelected()
        {
            EntityHit h = hitList.SelectedItem as EntityHit;
            if (h == null) return;
            EndEdits();
            GroupView v = Editor().Open(Kind(), h.Id);
            ShowGroup(v);
            host.SayOutside(Describe(v));
        }

        /// <summary>Where the group is per target, and the plan's lines about it.</summary>
        static string Describe(GroupView v)
        {
            var sb = new StringBuilder();
            sb.Append(v.Noun).Append(' ').Append(v.Id.ToString(CultureInfo.InvariantCulture)).Append(v.IsNew ? " (new in the mod)" : v.Op != null ? " (changed by the mod)" : "").Append(':');
            foreach (GroupSideView s in v.Sides)
            {
                sb.Append("\r\n").Append(s.Target).Append(" (").Append(s.Title).Append("): ");
                if (!s.Present) { sb.Append("not there"); continue; }
                sb.Append(s.Rows.Count).Append(" row(s)");
                if (s.Removed.Count > 0) sb.Append(", ").Append(s.Removed.Count).Append(" removed");
                sb.Append(" in ").Append(string.Join(", ", s.Entries.ToArray()));
            }
            if (v.Lines.Count > 0)
            {
                sb.Append("\r\n\r\nPlan, for this ").Append(v.Noun).Append(':');
                foreach (string l in v.Lines) sb.Append("\r\n  ").Append(l);
            }
            return sb.ToString();
        }

        // ---- the group ----

        void ShowGroup(GroupView v)
        {
            formGrid.CancelEdit();
            rowGrid.CancelEdit();
            view = v;
            shippedView = null;
            if (v != null && !v.IsNew)
            {
                try { shippedView = Shipped().Open(v.Kind, v.Id); }
                catch (ModKitException) { shippedView = null; }
            }
            filling = true;
            int keep = sideBox.SelectedIndex;
            sideBox.Items.Clear();
            if (v != null)
                foreach (GroupSideView s in v.Sides) sideBox.Items.Add(s.Target + ": " + s.Title + (s.Present ? "" : " (not there)"));
            if (sideBox.Items.Count > 0) sideBox.SelectedIndex = keep >= 0 && keep < sideBox.Items.Count ? keep : 0;
            filling = false;
            groupLabel.Text = v == null ? "(nothing chosen)" : v.Noun + " " + v.Id.ToString(CultureInfo.InvariantCulture) + (v.IsNew ? " (new)" : v.Op != null ? " (changed)" : "");
            ShowSide(sideBox.SelectedIndex);
        }

        void ShowSide(int index)
        {
            formGrid.CancelEdit();
            rowGrid.CancelEdit();
            side = view != null && index >= 0 && index < view.Sides.Count ? view.Sides[index] : null;
            FillForm();
            FillRows();
        }

        GroupSideView ShippedSide()
        {
            return shippedView == null || side == null ? null : shippedView.Side(side.Archive);
        }

        void FillForm()
        {
            form.Clear();
            formGrid.RowCount = 0;
            if (side == null) return;
            if (side.Flat)
                form.Add(new FormRow { Heading = "a flat split has no header; set box and group fields on a nested target (they are written to every target)" });
            for (int i = 0; i < side.Headers.Count; i++)
            {
                form.Add(new FormRow { Heading = view.Kind == GroupKind.Shop ? "shop header" + (side.Headers.Count > 1 ? " row " + (i + 1) : "") : "box" });
                foreach (KeyValuePair<string, string> kv in side.Headers[i]) form.Add(new FormRow { Field = kv.Key, Header = i });
            }
            foreach (KeyValuePair<string, List<KeyValuePair<string, string>>> g in side.Groups)
            {
                form.Add(new FormRow { Heading = "group " + g.Key });
                foreach (KeyValuePair<string, string> kv in g.Value) form.Add(new FormRow { Group = g.Key, Field = kv.Key });
            }
            formGrid.RowCount = form.Count;
            formGrid.Invalidate();
        }

        static string Lookup(List<KeyValuePair<string, string>> attrs, string name)
        {
            if (attrs == null) return null;
            foreach (KeyValuePair<string, string> kv in attrs) if (kv.Key == name) return kv.Value;
            return null;
        }

        static List<KeyValuePair<string, string>> GroupAttrs(GroupSideView s, string group)
        {
            if (s == null) return null;
            foreach (KeyValuePair<string, List<KeyValuePair<string, string>>> g in s.Groups) if (g.Key == group) return g.Value;
            return null;
        }

        string FormValue(FormRow r, GroupSideView s)
        {
            if (s == null) return null;
            if (r.Group != null) return Lookup(GroupAttrs(s, r.Group), r.Field);
            return r.Header < s.Headers.Count ? Lookup(s.Headers[r.Header], r.Field) : null;
        }

        FormRow FormAt(int i) { return i >= 0 && i < form.Count ? form[i] : null; }

        void OnFormValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            FormRow r = FormAt(e.RowIndex);
            if (r == null) return;
            if (r.Heading != null) { e.Value = e.ColumnIndex == 0 ? r.Heading : ""; return; }
            string now = FormValue(r, side);
            string was = shippedView == null ? null : FormValue(r, ShippedSide());
            if (e.ColumnIndex == 0) e.Value = (shippedView != null && now != was ? "* " : "   ") + r.Field;
            else if (e.ColumnIndex == 1) e.Value = now;
            else e.Value = was;
        }

        void OnFormFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            FormRow r = FormAt(e.RowIndex);
            if (r == null) return;
            if (r.Heading != null)
            {
                e.CellStyle.BackColor = SystemColors.Control;
                e.CellStyle.ForeColor = SystemColors.ControlText;
                e.CellStyle.Font = bold.Of(formGrid.Font);
            }
            else if (e.ColumnIndex == 2) e.CellStyle.ForeColor = Color.Gray;
            else if (view != null && view.IsNew) e.CellStyle.BackColor = Color.FromArgb(214, 240, 214);
            else if (shippedView != null && FormValue(r, side) != FormValue(r, ShippedSide())) e.CellStyle.BackColor = Color.FromArgb(255, 236, 170);
        }

        void OnFormValuePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            FormRow r = FormAt(e.RowIndex);
            if (r == null || r.Heading != null || e.ColumnIndex != 1 || view == null) return;
            string value = e.Value == null ? "" : e.Value.ToString();
            if (value == (FormValue(r, side) ?? "")) return;
            string group = r.Group, field = r.Field;
            Guard(delegate { SetField(group, field, value); });
        }

        void SetFieldFromBar()
        {
            if (view == null) throw new ModKitException("choose a shop or a drop box first");
            string name = fieldNameBox.Text.Trim();
            if (name.Length == 0) throw new ModKitException("type the field's name");
            string group = fieldGroupBox.Text.Trim();
            if (group.Length > 0 && view.Kind == GroupKind.Shop) throw new ModKitException("a shop has no groups; leave \"of group\" empty");
            SetField(group.Length == 0 ? null : group, name, fieldValueBox.Text);
        }

        void SetField(string group, string name, string value)
        {
            GroupView v = view;
            string set = value.Length == 0 ? null : value;
            if (group != null) Editor().SetGroupField(v.Id, group, name, set);
            else Editor().SetHeader(v.Kind, v.Id, name, set);
            Reopen((group != null ? "group " + group + " " : "") + name + (set == null ? " removed" : " = " + set) + "; Plan shows it per target");
        }

        // ---- rows ----

        void FillRows()
        {
            rowGrid.RowCount = 0;
            rowGrid.Columns.Clear();
            gridRows.Clear();
            columns.Clear();
            removedFrom = 0;
            if (side == null) return;
            gridRows.AddRange(side.Rows);
            removedFrom = gridRows.Count;
            gridRows.AddRange(side.Removed);
            var seen = new Dictionary<string, bool>();
            foreach (GroupRowView r in gridRows)
                foreach (KeyValuePair<string, string> kv in r.Fields)
                    if (!seen.ContainsKey(kv.Key)) { seen[kv.Key] = true; columns.Add(kv.Key); }
            rowGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            DataGridViewColumn first = Col("row", 230 + IconRow, true);
            first.DefaultCellStyle.Padding = new Padding(IconRow + 2, 0, 0, 0);   // the item's icon is drawn at the left of the cell
            rowGrid.Columns.Add(first);
            rowGrid.Columns.Add(Col(side.Flat ? "split" : "entry", 170, true));
            foreach (string c in columns) rowGrid.Columns.Add(Col(c, Math.Max(60, Math.Min(160, c.Length * 9)), false));
            rowGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            rowGrid.RowCount = gridRows.Count;
            rowGrid.Invalidate();
        }

        // ---- the item icon of each row ----

        const int IconRow = 34;   // row height; the icon is 2 pixels smaller
        ItemEditor items;
        readonly Dictionary<string, string> iconOfItem = new Dictionary<string, string>();

        /// <summary>The item a row hands out: shops name it in id, nested drop boxes in itemID, flat ones in item.</summary>
        static long RowItem(GroupRowView r, GroupKind kind)
        {
            string v = kind == GroupKind.Shop ? r.Get("id") : (r.Get("itemID") ?? r.Get("item") ?? r.Item);
            long id;
            return v != null && long.TryParse(v.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out id) && id > 0 ? id : 0;
        }

        IconCache Cache()
        {
            if (host == null) return null;
            try { return IconCache.For(host.SharedSession().Data); }
            catch (ModKitException) { return null; }
            catch (System.IO.IOException) { return null; }
        }

        /// <summary>The icon entry of the item as the mod has it (null: no such item or no icon).</summary>
        string IconOfItem(long id)
        {
            string key = id.ToString(System.Globalization.CultureInfo.InvariantCulture), entry;
            if (iconOfItem.TryGetValue(key, out entry)) return entry;
            entry = null;
            try
            {
                EditorSession s = host.SharedSession();
                if (items == null || items.Mod != s.Mod || items.Data != s.Data) { items = s.Items(); iconOfItem.Clear(); }
                entry = items.Open(id).IconEntry();   // no such item: ModKitException, the plain placeholder
            }
            catch (ModKitException) { }
            catch (System.IO.IOException) { }
            iconOfItem[key] = entry;
            return entry;
        }

        void OnRowPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.ColumnIndex != 0 || e.RowIndex < 0 || view == null) return;
            GroupRowView r = RowAt(e.RowIndex);
            if (r == null) return;
            e.Paint(e.ClipBounds, DataGridViewPaintParts.All);
            long id = RowItem(r, view.Kind);
            IconCache cache = Cache();
            string entry = id != 0 && cache != null ? IconOfItem(id) : null;
            int size = e.CellBounds.Height - 4;
            var box = new Rectangle(e.CellBounds.X + 2, e.CellBounds.Y + 2, size, size);
            Image img = cache != null ? cache.Get(entry, rowGrid) : null;
            IconCache.Draw(e.Graphics, box, img);
            e.Handled = true;
        }

        GroupRowView RowAt(int i) { return i >= 0 && i < gridRows.Count ? gridRows[i] : null; }

        void OnRowValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            GroupRowView r = RowAt(e.RowIndex);
            if (r == null || view == null) return;
            if (e.ColumnIndex == 0) e.Value = (e.RowIndex >= removedFrom ? "(removed) " : r.IsChanged ? "* " : "") + r.Describe(view.Kind);
            else if (e.ColumnIndex == 1) e.Value = r.Entry;
            else e.Value = r.Get(columns[e.ColumnIndex - 2]);
        }

        void OnRowFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            GroupRowView r = RowAt(e.RowIndex);
            if (r == null || view == null) return;
            if (e.RowIndex >= removedFrom) { e.CellStyle.ForeColor = Color.Gray; return; }
            if (e.ColumnIndex < 2) { e.CellStyle.BackColor = SystemColors.Control; return; }
            string c = columns[e.ColumnIndex - 2];
            if (r.IsAdded || view.IsNew) e.CellStyle.BackColor = Color.FromArgb(214, 240, 214);
            else if (r.Before != null && r.GetBefore(c) != r.Get(c)) e.CellStyle.BackColor = Color.FromArgb(255, 236, 170);
        }

        void OnRowValuePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            GroupRowView r = RowAt(e.RowIndex);
            if (r == null || view == null || e.RowIndex >= removedFrom || e.ColumnIndex < 2) return;
            string name = columns[e.ColumnIndex - 2];
            string value = e.Value == null ? "" : e.Value.ToString();
            if (value == (r.Get(name) ?? "")) return;
            GroupView v = view;
            GroupSideView s = side;
            Guard(delegate
            {
                Editor().SetRowField(v, s, r, name, value.Length == 0 ? null : value);
                Reopen(r.Describe(v.Kind) + ": " + name + (value.Length == 0 ? " removed" : " = " + value) + "; written to every target where the row is found");
            });
        }

        static List<KeyValuePair<string, string>> ParseAttrs(string text)
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (string part in text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) throw new ModKitException("\"" + part + "\" is not name=value");
                list.Add(new KeyValuePair<string, string>(part.Substring(0, eq), part.Substring(eq + 1)));
            }
            if (list.Count == 0) throw new ModKitException("type the new row's fields as name=value pairs");
            return list;
        }

        void AddRow()
        {
            if (view == null) throw new ModKitException("choose a shop or a drop box first");
            EndEdits();
            List<KeyValuePair<string, string>> attrs = ParseAttrs(rowAttrsBox.Text);
            string group = rowGroupBox.Text.Trim();
            if (view.Kind == GroupKind.DropBox)
            {
                if (group.Length == 0) throw new ModKitException("type the dropGroupID of the group the row goes into");
                bool known = false;
                foreach (GroupSideView s in view.Sides)
                    if (GroupAttrs(s, group) != null) known = true;
                foreach (GroupSideView s in view.Sides)
                    foreach (GroupRowView r in s.Rows) if (r.Group == group) known = true;
                if (known) Editor().AddRow(view.Kind, view.Id, group, attrs);
                else Editor().AddGroup(view.Id, group, null, new IList<KeyValuePair<string, string>>[] { attrs });
            }
            else Editor().AddRow(view.Kind, view.Id, null, attrs);
            rowAttrsBox.Text = "";
            Reopen("row added; Plan shows it per target");
        }

        void RemoveOrRestore()
        {
            if (view == null) throw new ModKitException("choose a shop or a drop box first");
            int i = rowGrid.CurrentCell == null ? -1 : rowGrid.CurrentCell.RowIndex;
            GroupRowView r = RowAt(i);
            if (r == null) throw new ModKitException("choose a row in the grid first");
            EndEdits();
            if (i >= removedFrom) { Editor().Restore(view, r); Reopen(r.Describe(view.Kind) + " comes back as shipped"); }
            else { Editor().RemoveRow(view, r); Reopen(r.Describe(view.Kind) + " removed on every target where it is found"); }
        }

        // ---- clone, new, discard ----

        long IdFromBox()
        {
            string text = cloneIdBox.Text.Trim();
            if (text.Length == 0) return 0;
            long v;
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out v)) throw new ModKitException("\"" + text + "\" is not an id");
            return v;
        }

        void Clone()
        {
            if (view == null) throw new ModKitException("choose the shop or drop box to copy first");
            EndEdits();
            long source = view.Id;
            long id = Editor().Clone(view.Kind, source, IdFromBox());
            Opened(view.Kind, id, view.Noun + " " + id + " added as a copy of " + source + ". Save mod, then Plan shows it per target.");
        }

        void New()
        {
            EndEdits();
            GroupKind kind = Kind();
            long id = Editor().New(kind, IdFromBox(), null);
            Opened(kind, id, (kind == GroupKind.Shop ? "shop " : "drop box ") + id + " added, empty. Add its rows below; Save mod, then Plan shows it per target.");
        }

        void Discard()
        {
            if (view == null) throw new ModKitException("choose a shop or a drop box first");
            GroupView v = view;
            Editor().Discard(v.Kind, v.Id);
            host.EditedOutside();
            ShowGroup(null);
            FillHits(Editor().Exists(v.Kind, v.Id) ? v.Id : 0);
            if (Editor().Exists(v.Kind, v.Id)) ShowGroup(Editor().Open(v.Kind, v.Id));
            host.SayOutside(v.Noun + " " + v.Id + ": the mod's changes are taken out");
        }

        void Opened(GroupKind kind, long id, string said)
        {
            host.EditedOutside();
            cloneIdBox.Text = "";
            FillHits(id);
            GroupView v = Editor().Open(kind, id);
            ShowGroup(v);
            host.SayOutside(said + "\r\n\r\n" + Describe(v));
        }

        /// <summary>After an edit: reads the group back as the mod now leaves it.</summary>
        void Reopen(string said)
        {
            host.EditedOutside();
            GroupView v = Editor().Open(view.Kind, view.Id);
            ShowGroup(v);
            host.SayOutside(said + "\r\n\r\n" + Describe(v));
        }
    }
}
