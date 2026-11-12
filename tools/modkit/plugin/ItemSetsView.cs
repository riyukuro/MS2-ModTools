// The "MS2 Item sets" view: the item sets (table/setiteminfo.xml) with their set bonuses, in the style of the MS2
// Recipes view. A find box (set id, name or item id), the list of hits with the first item's icon, the set's name
// and flags as a form, its items as a grid with icons and names, its bonuses (one per number of items worn) with the
// values of the chosen bonus, the checks of the set, Add / Clone / Remove, and the Plan / Apply / Revert buttons and
// output box of the MS2 Tables view.
//
// A set is one thing on every side that ships the table, though the sides keep the bonuses differently: the Korean
// client as <part> rows of the set, the server in table/setitemoption.xml (see MS2.ModKit.ItemSetEditor). Every edit
// is an op of the mod the MS2 Tables view is editing.
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
    public class ItemSetsView : AbstractView
    {
        public const string ViewId = "MS2.Tables.ItemSetsView";
        const int IconRow = 36;

        // the form's rows: the name, then attributes of the set row
        static readonly string[] Fields = { "name", "showEffectIfItsSetItemMotion", "isDisableTooltip" };

        sealed class ItemInfo
        {
            internal string Name, Icon;
            internal bool Missing;
        }

        TablesView host;
        ItemSetEditor editor;
        ItemEditor items;
        ItemSet set;
        List<long> members = new List<long>();
        List<SetPart> parts = new List<SetPart>();
        SetPart part;
        readonly List<string> detail = new List<string>();
        readonly List<ItemSet> hits = new List<ItemSet>();
        readonly Dictionary<long, ItemInfo> itemInfo = new Dictionary<long, ItemInfo>();
        bool filling;

        readonly TextBox searchBox = new TextBox { Width = 180 };
        readonly Label hitsLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListBox hitList = new ListBox { Dock = DockStyle.Left, Width = 290, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = IconRow };
        readonly Label setLabel = new Label { AutoSize = true, Margin = new Padding(24, 8, 8, 0) };
        readonly TextBox newIdBox = new TextBox { Width = 80 };
        readonly TextBox countBox = new TextBox { Width = 30 };
        readonly DataGridView formGrid = new DataGridView { Dock = DockStyle.Top };
        readonly DataGridView memberGrid = new DataGridView { Dock = DockStyle.Fill };
        readonly DataGridView partGrid = new DataGridView { Dock = DockStyle.Top, Height = 120 };
        readonly DataGridView detailGrid = new DataGridView { Dock = DockStyle.Fill };
        readonly ListBox checkList = new ListBox { Dock = DockStyle.Bottom, Height = 80, IntegralHeight = false, HorizontalScrollbar = true };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 130 };
        readonly ComboBox targetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();
        readonly BoldFont bold = new BoldFont();

        public ItemSetsView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            detail.Add(ItemSetRules.Count);
            detail.AddRange(ItemSetRules.Stats);
            detail.Add(ItemSetRules.Effects);
            detail.AddRange(ItemSetRules.Texts);

            FlowLayoutPanel top = Bar();
            top.Controls.Add(Lbl("find"));
            top.Controls.Add(searchBox);
            tips.SetToolTip(searchBox, "A part of the set id or of the set's name, or the start of an item id; empty lists every set");
            top.Controls.Add(Btn("Find", "Lists the sets whose id, name or item ids hold the text", delegate { Find(); }));
            top.Controls.Add(hitsLabel);
            // the set's label is here, where it has room: the buttons of the bar below keep their place
            top.Controls.Add(setLabel);

            FlowLayoutPanel applyBar = Bar();
            applyBar.Controls.Add(Lbl("Apply to"));
            targetBox.Items.Add("workspace (patched archives)");
            targetBox.Items.Add("game folder (loose files)");
            tips.SetToolTip(targetBox, "Where Apply writes the client's tables (the same choice as in the MS2 Tables view). The server's archives and the global client's always go to the workspace.");
            applyBar.Controls.Add(targetBox);
            applyBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change, with the set checks for each target; writes nothing", delegate { return host.DoPlan(); }));
            applyBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change, to every target at once", delegate { return host.DoApply(); }));
            applyBar.Controls.Add(Job("Revert", "Removes what Apply wrote, on every target", delegate { return host.DoRevert(); }));

            string range = ItemSetRules.From + "-" + ItemSetRules.To;
            FlowLayoutPanel setBar = Bar();
            setBar.Controls.Add(Btn("Add", "Adds a set without items and bonuses under the id in the box (empty = the next free id of " + range + "), on every side", delegate { Add(); }));
            setBar.Controls.Add(Btn("Clone", "Adds a copy of this set (items, bonuses, name) under the id in the box (empty = the next free id of " + range + "), with bonuses of its own", delegate { Clone(); }));
            setBar.Controls.Add(Lbl("new id"));
            setBar.Controls.Add(newIdBox);
            setBar.Controls.Add(Btn("Remove / restore", "Removes the set on every side (a new one goes away), or brings a removed shipped set back", delegate { RemoveOrRestore(); }));
            setBar.Controls.Add(Btn("Undo changes", "Drops every change the mod makes to this shipped set: its items, bonuses and name", delegate { Undo(); }));
            setBar.Controls.Add(Btn("Add bonus", "Adds a bonus for the number of worn items in the box, on every side", delegate { AddPart(); }));
            setBar.Controls.Add(Lbl("for"));
            setBar.Controls.Add(countBox);
            tips.SetToolTip(countBox, "The number of the set's items worn at which the new bonus applies");
            setBar.Controls.Add(Btn("Remove bonus", "Removes the chosen bonus on every side", delegate { RemovePart(); }));
            setBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same mod and Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            SetupGrid(formGrid);
            formGrid.Columns.Add(Col("field", 170, true));
            formGrid.Columns.Add(Col("value", 150, false));
            formGrid.CellValueNeeded += OnFormValueNeeded;
            formGrid.CellValuePushed += OnFormValuePushed;
            formGrid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e)
            {
                if (set == null) return;
                if (set.IsDeleted) e.CellStyle.ForeColor = SystemColors.GrayText;
            };
            formGrid.CellToolTipTextNeeded += delegate(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
            {
                if (set != null && editor != null && e.RowIndex >= 0 && e.RowIndex < Fields.Length) e.ToolTipText = Means(Fields[e.RowIndex]);
            };
            formGrid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e) { if (set == null || set.IsDeleted) e.Cancel = true; };
            formGrid.Height = formGrid.ColumnHeadersHeight + Fields.Length * formGrid.RowTemplate.Height + 4;

            SetupGrid(memberGrid);
            memberGrid.RowTemplate.Height = IconRow;
            memberGrid.Columns.Add(Col("", IconRow + 4, true));
            memberGrid.Columns.Add(Col("item", 80, false));
            memberGrid.Columns.Add(Col("name", 180, true));
            memberGrid.Columns[0].MinimumWidth = IconRow + 4;
            memberGrid.CellValueNeeded += OnMemberValueNeeded;
            memberGrid.CellValuePushed += OnMemberValuePushed;
            memberGrid.CellPainting += OnMemberPainting;
            memberGrid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e)
            {
                if (set == null) return;
                if (set.IsDeleted) e.CellStyle.ForeColor = SystemColors.GrayText;
                else if (e.ColumnIndex == 2 && e.Value as string == "(no such item)") e.CellStyle.ForeColor = Color.Firebrick;
                else if (e.ColumnIndex == 2 && e.RowIndex >= members.Count) e.CellStyle.ForeColor = SystemColors.GrayText;
            };
            memberGrid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e) { if (set == null || set.IsDeleted || e.ColumnIndex != 1) e.Cancel = true; };
            IdPick.Attach(memberGrid, delegate(int row, int column)
            {
                return set != null && !set.IsDeleted && column == 1 ? IdLookup.Item : null;
            }, delegate { return host.SharedSession(); });
            tips.SetToolTip(memberGrid, "The items of the set. Type an id in the last row to add an item; empty an id to take the item out.");

            SetupGrid(partGrid);
            partGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            partGrid.ReadOnly = true;
            partGrid.Columns.Add(Col("items worn", 80, true));
            partGrid.Columns.Add(Col("bonus", 200, true));
            partGrid.Columns[0].FillWeight = 14;
            partGrid.Columns[1].FillWeight = 86;
            partGrid.CellValueNeeded += delegate(object sender, DataGridViewCellValueEventArgs e)
            {
                if (set == null || editor == null || e.RowIndex < 0 || e.RowIndex >= parts.Count) return;
                SetPart p = parts[e.RowIndex];
                if (e.ColumnIndex == 0) e.Value = (editor.MissingOn(set, p).Count > 0 || AnyDiffers(p) ? "≠ " : "") + p.Count;
                else { string t = editor.Summary(p); e.Value = t.Length == 0 ? "(nothing yet)" : t; }
            };
            partGrid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e) { if (set != null && set.IsDeleted) e.CellStyle.ForeColor = SystemColors.GrayText; };
            partGrid.SelectionChanged += delegate
            {
                if (filling || set == null) return;
                int i = partGrid.CurrentCell != null ? partGrid.CurrentCell.RowIndex : -1;
                SetPart p = i >= 0 && i < parts.Count ? parts[i] : null;
                if (p == part) return;
                detailGrid.EndEdit();
                part = p;
                detailGrid.RowCount = part == null ? 0 : detail.Count;
                detailGrid.Invalidate();
            };

            SetupGrid(detailGrid);
            detailGrid.Columns.Add(Col("of the chosen bonus", 190, true));
            detailGrid.Columns.Add(Col("value", 90, false));
            detailGrid.Columns.Add(Col("note", 190, true));
            detailGrid.CellValueNeeded += OnDetailValueNeeded;
            detailGrid.CellValuePushed += OnDetailValuePushed;
            detailGrid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e)
            {
                if (set == null || part == null || editor == null || e.RowIndex < 0 || e.RowIndex >= detail.Count) return;
                if (set.IsDeleted || e.ColumnIndex == 2) e.CellStyle.ForeColor = SystemColors.GrayText;
                else if (editor.Get(part, detail[e.RowIndex]).Length > 0) e.CellStyle.Font = bold.Of(detailGrid.Font);
            };
            detailGrid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e) { if (set == null || set.IsDeleted || part == null) e.Cancel = true; };
            tips.SetToolTip(checkList, "The checks of the set as shown; Plan checks every set the mods add or change, per target, with the items and effects of each target");

            var bonus = new Panel { Dock = DockStyle.Fill };
            bonus.Controls.Add(detailGrid);
            bonus.Controls.Add(new Splitter { Dock = DockStyle.Top, Height = 4 });
            bonus.Controls.Add(partGrid);

            // the name and flags above the items, beside the bonuses
            var about = new Panel { Dock = DockStyle.Left, Width = 340 };
            about.Controls.Add(memberGrid);
            about.Controls.Add(formGrid);

            var middle = new Panel { Dock = DockStyle.Fill };
            middle.Controls.Add(bonus);
            middle.Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
            middle.Controls.Add(about);

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(middle);
            right.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            right.Controls.Add(checkList);
            right.Controls.Add(setBar);

            Controls.Add(right);
            Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
            Controls.Add(hitList);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(outputBox);
            Controls.Add(applyBar);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, applyBar, hitList, right });
            // the output box keeps two lines; the checks get 12 % of the height, the bonus list 28 % of the middle;
            // in a low window the bonus values keep their header and four rows
            ViewLayout.KeepLogLines(outputBox, this);
            ViewLayout.KeepHeightShare(checkList, right, 0.12, 3 * checkList.ItemHeight + 4,
                delegate { return setBar.Height + 8 + partGrid.Height + detailGrid.ColumnHeadersHeight + 4 * detailGrid.RowTemplate.Height + 4; });
            ViewLayout.KeepHeightShare(partGrid, bonus, 0.28, partGrid.ColumnHeadersHeight + 4 * partGrid.RowTemplate.Height + 4,
                delegate { return 4 + detailGrid.ColumnHeadersHeight + 4 * detailGrid.RowTemplate.Height + 4; });
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
            g.BackgroundColor = SystemColors.Window;
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

        void EndEdits() { formGrid.EndEdit(); memberGrid.EndEdit(); detailGrid.EndEdit(); }

        void CancelEdits() { formGrid.CancelEdit(); memberGrid.CancelEdit(); detailGrid.CancelEdit(); }

        protected override void OnEnter(EventArgs e)
        {
            TablesView.ItemsInUse = true;
            // an item may have been added or renamed in the Items view meanwhile
            itemInfo.Clear();
            items = null;
            hitList.Invalidate();
            memberGrid.Invalidate();
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

        string Key(ItemSet s) { return s != null && editor != null ? editor.Get(s, "id") : null; }

        void Hook(TablesView h)
        {
            if (h == host) return;
            // the set and the list rows belong to the old view's session: drop them, and show the same set again
            string key = Key(set);
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
            items = null;
            itemInfo.Clear();
            if (h == null) { set = null; part = null; hits.Clear(); return; }
            ShowSet(null);
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
            try { FillHits(key); }
            catch (ModKitException) { ShowSet(null); }
            catch (System.IO.IOException) { ShowSet(null); }
        }

        // painting reads only what is shown already: never a new session of a closed MS2 Tables view
        bool CanPaint { get { return host != null && !host.IsDisposed && editor != null && set != null; } }

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
                Logger.Warning("MS2 Item sets: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        ItemSetEditor Editor()
        {
            EditorSession s = host.SharedSession();
            if (editor == null || editor.Mod != s.Mod || editor.Data != s.Data)
            {
                editor = new ItemSetEditor(s.Data, s.Mod);
                editor.Others = delegate { return s.OtherMods(); };
                itemInfo.Clear();
            }
            return editor;
        }

        void OnHostSaid(string text) { OutputWrap.Set(outputBox, text); }

        void OnHostBusy(bool on)
        {
            ViewLayout.Lock(this, lockable, on, outputBox);
            if (!on) Invalidate(true);      // the names and icons a paint could not read during the job
        }

        // the mod was replaced or edited in another view: read the tables again and show the same set
        void OnHostChanged()
        {
            CancelEdits();
            string key = Key(set);
            editor = null;
            items = null;
            itemInfo.Clear();
            ShowSet(null);
            try { FillHits(key); }
            catch (ModKitException) { ShowSet(null); }
            catch (System.IO.IOException) { ShowSet(null); }
        }

        // ---- items: name and icon ----

        IconCache Cache()
        {
            if (host == null || host.IsDisposed) return null;
            try { return IconCache.For(host.SharedSession().Data); }
            catch (ModKitException) { return null; }
            catch (System.IO.IOException) { return null; }
        }

        ItemInfo Info(long id)
        {
            ItemInfo info;
            if (itemInfo.TryGetValue(id, out info)) return info;
            info = new ItemInfo();
            // a running job (Plan, Apply) reads the same data on its own thread: the paint waits for its end
            if (host == null || host.Busy) { info.Name = ""; return info; }
            try
            {
                EditorSession s = host.SharedSession();
                if (items == null || items.Mod != s.Mod || items.Data != s.Data) items = s.Items();
                items.Look(id, delegate(EntityDraft e)
                {
                    var d = (ItemDraft)e;
                    info.Name = d.Name;
                    info.Icon = d.IconEntry();
                });
            }
            catch (OutOfMemoryException) { info.Name = ""; return info; }      // not kept: the next paint asks again
            catch (ModKitException) { info.Missing = true; info.Name = "(no such item)"; }
            catch (System.IO.IOException) { info.Name = ""; }
            itemInfo[id] = info;
            return info;
        }

        // ---- the list ----

        void Find()
        {
            EndEdits();
            FillHits(Key(set));
            if (hitList.Items.Count > 0 && hitList.SelectedIndex < 0) hitList.SelectedIndex = 0;
            else if (hitList.Items.Count == 0) ShowSet(null);
        }

        void FillHits(string keep)
        {
            ItemSetEditor ed = Editor();
            hits.Clear();
            hits.AddRange(ed.Find(searchBox.Text));
            // the mod's new sets first, then by id
            hits.Sort(delegate(ItemSet a, ItemSet b)
            {
                if (a.IsAdded != b.IsAdded) return a.IsAdded ? -1 : 1;
                return ed.Id(a).CompareTo(ed.Id(b));
            });
            filling = true;
            ItemSet show = null;
            try
            {
                hitList.BeginUpdate();
                hitList.Items.Clear();
                foreach (ItemSet s in hits)
                {
                    hitList.Items.Add(s);
                    if (show == null && keep != null && ed.Get(s, "id") == keep) { hitList.SelectedIndex = hitList.Items.Count - 1; show = s; }
                }
                hitList.EndUpdate();
            }
            finally { filling = false; }
            hitsLabel.Text = hits.Count + " set(s), " + ed.Sets.Count + " in all";
            if (show != null) ShowSet(show);
        }

        void OpenSelected()
        {
            ItemSet s = hitList.SelectedItem as ItemSet;
            if (s == null) return;
            EndEdits();
            ShowSet(s);
            host.SayOutside(Describe(s));
        }

        static string State(ItemSetEditor ed, ItemSet s, string removed, string added, string changed)
        {
            return s.IsDeleted ? removed : s.IsAdded ? added : ed.IsChanged(s) ? changed : "";
        }

        void OnDrawHit(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= hitList.Items.Count || editor == null || host == null || host.IsDisposed) return;
            var s = hitList.Items[e.Index] as ItemSet;
            if (s == null) return;
            int size = e.Bounds.Height - 4;
            var box = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 2, size, size);
            List<long> ids = editor.Members(s);
            IconCache cache = Cache();
            ItemInfo info = ids.Count > 0 ? Info(ids[0]) : null;
            IconCache.Draw(e.Graphics, box, cache != null && info != null ? cache.Get(info.Icon, hitList) : null);
            string name = editor.Name(s);
            var counts = new StringBuilder();
            foreach (SetPart p in editor.Parts(s)) counts.Append(counts.Length > 0 ? "/" : "").Append(p.Count);
            bool marked = s.IsAdded || editor.IsChanged(s);
            string line1 = editor.Get(s, "id") + "  " + (name.Length > 0 ? name : "(no name)");
            string line2 = ids.Count + " item" + (ids.Count == 1 ? "" : "s") + (counts.Length > 0 ? ", bonus at " + counts : ", no bonus")
                + State(editor, s, "  (removed)", "  (new)", "  (changed)");
            Color fore = s.IsDeleted && (e.State & DrawItemState.Selected) == 0 ? SystemColors.GrayText : e.ForeColor;
            int half = e.Bounds.Height / 2;
            var t1 = new Rectangle(box.Right + 6, e.Bounds.Y + 1, e.Bounds.Width - box.Width - 10, half);
            var t2 = new Rectangle(t1.X, e.Bounds.Y + half - 1, t1.Width, half);
            TextRenderer.DrawText(e.Graphics, line1, marked ? bold.Of(e.Font) : e.Font, t1, fore, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, line2, e.Font, t2, fore == e.ForeColor && (e.State & DrawItemState.Selected) == 0 ? SystemColors.GrayText : fore, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        }

        // ---- the set ----

        void ShowSet(ItemSet s) { ShowSet(s, null); }

        /// <summary>Shows a set; keepCount is the bonus to choose again (else the first).</summary>
        void ShowSet(ItemSet s, string keepCount)
        {
            CancelEdits();
            set = s;
            part = null;
            filling = true;
            try
            {
                formGrid.RowCount = 0;
                memberGrid.RowCount = 0;
                partGrid.RowCount = 0;
                detailGrid.RowCount = 0;
                checkList.Items.Clear();
                members = new List<long>();
                parts = new List<SetPart>();
                if (s == null) { setLabel.Text = "no set chosen"; return; }
                ItemSetEditor ed = Editor();
                string name = ed.Name(s);
                setLabel.Text = "Set " + ed.Get(s, "id") + (name.Length > 0 ? " (" + name + ")" : "")
                    + State(ed, s, ", removed by the mod", ", new in the mod", ", changed by the mod");
                members = ed.Members(s);
                parts = ed.Parts(s);
                formGrid.RowCount = Fields.Length;
                memberGrid.RowCount = members.Count + (s.IsDeleted ? 0 : 1);
                partGrid.RowCount = parts.Count;
                int at = parts.Count > 0 ? 0 : -1;
                for (int i = 0; i < parts.Count; i++) if (keepCount != null && parts[i].Count == keepCount) at = i;
                if (at >= 0)
                {
                    part = parts[at];
                    partGrid.CurrentCell = partGrid.Rows[at].Cells[0];
                    detailGrid.RowCount = detail.Count;
                }
                foreach (string t in Checks(s)) checkList.Items.Add(t);
                if (checkList.Items.Count == 0) checkList.Items.Add("no problem found");
            }
            finally { filling = false; }
            formGrid.Invalidate();
            memberGrid.Invalidate();
            partGrid.Invalidate();
            detailGrid.Invalidate();
        }

        /// <summary>The editor's checks and the items no target of the mod has (the plan names the target).</summary>
        List<string> Checks(ItemSet s)
        {
            ItemSetEditor ed = Editor();
            List<string> l = ed.Check(s);
            // a shipped set the mod leaves alone is not the mod's problem (as in the plan)
            if (s.IsDeleted || !(s.IsAdded || ed.IsChanged(s))) return l;
            foreach (long id in members) if (Info(id).Missing) l.Add("item " + id + " is not an item");
            return l;
        }

        string Describe(ItemSet s)
        {
            ItemSetEditor ed = Editor();
            var sb = new StringBuilder();
            sb.Append("set ").Append(ed.Get(s, "id")).Append(State(ed, s, " (removed by the mod)", " (new in the mod)", " (changed by the mod)")).Append(':');
            for (int i = 0; i < ed.Sides.Count; i++)
            {
                ItemSetSide side = ed.Sides[i];
                sb.Append("\r\n").Append(side.Archive).Append(": ");
                if (s.Rows[i] == null) sb.Append("not there");
                else if (s.Rows[i].IsDeleted) sb.Append("removed");
                else if (side.Kms) sb.Append("has it, the bonuses in the set's own <part> rows");
                else sb.Append("has it, the bonuses in option ").Append(side.Info.Get(s.Rows[i], "optionID")).Append(" of ").Append(ItemSetRules.OptionEntry);
            }
            return sb.ToString();
        }

        void Refresh(string said)
        {
            host.EditedOutside();
            ItemSet s = set;
            string count = part != null ? part.Count : null;
            hitList.Invalidate();
            ShowSet(s, count);
            if (said != null) host.SayOutside(said + "\r\n\r\n" + Describe(s));
        }

        // ---- the form ----

        string Means(string f)
        {
            return f == "name" ? "The set's name in every language file (string/{lang}/" + ItemSetRules.NameFile + ")"
                : (f == "isDisableTooltip" ? "1 = the item tooltips do not list the set" : "1 = the set's effect shows only in the items' set motion")
                    + (editor.Differs(set, f) ? "; the sides differ, an edit sets every side" : "");
        }

        void OnFormValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (set == null || editor == null || e.RowIndex < 0 || e.RowIndex >= Fields.Length) return;
            string f = Fields[e.RowIndex];
            bool name = f == "name";
            if (e.ColumnIndex == 0) e.Value = (!name && editor.Differs(set, f) ? "≠ " : "   ") + f;
            else e.Value = name ? editor.Name(set) : editor.Get(set, f);
        }

        void OnFormValuePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            if (set == null || e.ColumnIndex != 1 || e.RowIndex < 0 || e.RowIndex >= Fields.Length) return;
            string f = Fields[e.RowIndex];
            string v = ((e.Value as string) ?? "").Trim();
            Guard(delegate
            {
                ItemSetEditor ed = Editor();
                if (f == "name")
                {
                    if (v == ed.Name(set)) return;
                    ed.SetName(set, v);
                }
                else
                {
                    if (v == (ed.Get(set, f) ?? "") && !ed.Differs(set, f)) return;
                    ed.Set(set, f, v.Length == 0 ? null : v);
                }
                Refresh(null);
            });
        }

        // ---- the items ----

        void OnMemberValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (set == null || editor == null || e.RowIndex < 0) return;
            if (e.RowIndex >= members.Count)
            {
                if (e.ColumnIndex == 2) e.Value = "(type an item id here to add it)";
                return;
            }
            long id = members[e.RowIndex];
            if (e.ColumnIndex == 1) e.Value = id.ToString(CultureInfo.InvariantCulture);
            else if (e.ColumnIndex == 2) e.Value = Info(id).Name;
        }

        void OnMemberPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.ColumnIndex != 0 || e.RowIndex < 0 || !CanPaint) return;
            e.Paint(e.ClipBounds, DataGridViewPaintParts.All);
            if (e.RowIndex < members.Count)
            {
                IconCache cache = Cache();
                int size = e.CellBounds.Height - 4;
                var box = new Rectangle(e.CellBounds.X + 2, e.CellBounds.Y + 2, size, size);
                IconCache.Draw(e.Graphics, box, cache != null ? cache.Get(Info(members[e.RowIndex]).Icon, memberGrid) : null);
            }
            e.Handled = true;
        }

        void OnMemberValuePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            if (set == null || e.ColumnIndex != 1 || e.RowIndex < 0) return;
            string v = ((e.Value as string) ?? "").Trim();
            int row = e.RowIndex;
            Guard(delegate
            {
                ItemSetEditor ed = Editor();
                var l = new List<long>(ed.Members(set));
                if (v.Length == 0 || v == "0")
                {
                    if (row >= l.Count) return;
                    l.RemoveAt(row);
                }
                else
                {
                    long id;
                    if (!long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out id)) throw new ModKitException("\"" + v + "\" is not an item id");
                    if (row < l.Count && l[row] == id && !ed.Differs(set, "itemIDs")) return;
                    int at = l.IndexOf(id);
                    if (at >= 0 && at != row) throw new ModKitException("item " + id + " is in the set already");
                    if (row < l.Count) l[row] = id; else l.Add(id);
                }
                ed.SetMembers(set, l);
                Refresh(null);
            });
        }

        // ---- the bonuses ----

        bool AnyDiffers(SetPart p)
        {
            foreach (string f in detail) if (f != ItemSetRules.Count && editor.Differs(p, f)) return true;
            return false;
        }

        string Note(string f)
        {
            if (editor.Differs(part, f)) return "the sides differ; an edit sets every side";
            if (f == ItemSetRules.Car) return "the Korean client does not read it";
            if (f == ItemSetRules.Count) return "the bonus applies from this many of the set's items";
            if (f == ItemSetRules.Effects) return "additional effects, e.g. 70000001:1, 70000002:3";
            if (f == "sgi_boss" || f == "sgi_elite") return "the server keeps one of the two";
            return "";
        }

        void OnDetailValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (set == null || part == null || editor == null || e.RowIndex < 0 || e.RowIndex >= detail.Count) return;
            string f = detail[e.RowIndex];
            switch (e.ColumnIndex)
            {
                case 0: e.Value = (editor.Differs(part, f) ? "≠ " : "   ") + ItemSetRules.Title(f) + (ItemSetRules.Title(f) == f ? "" : "  [" + f + "]"); break;
                case 1: e.Value = f == ItemSetRules.Count ? part.Count : editor.Get(part, f); break;
                case 2: e.Value = Note(f); break;
            }
        }

        void OnDetailValuePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            if (set == null || part == null || e.ColumnIndex != 1 || e.RowIndex < 0 || e.RowIndex >= detail.Count) return;
            string f = detail[e.RowIndex];
            string v = ((e.Value as string) ?? "").Trim();
            Guard(delegate
            {
                ItemSetEditor ed = Editor();
                string now = f == ItemSetRules.Count ? part.Count : ed.Get(part, f);
                if (v == now && !ed.Differs(part, f)) return;
                ed.Set(set, part, f, v);
                if (f == ItemSetRules.Count) { host.EditedOutside(); hitList.Invalidate(); ShowSet(set, v.TrimStart('0')); return; }
                Refresh(null);
            });
        }

        void AddPart()
        {
            if (set == null) throw new ModKitException("choose a set first");
            EndEdits();
            string text = countBox.Text.Trim();
            int n;
            if (text.Length == 0)
            {
                // the next count after the highest bonus
                n = 2;
                foreach (SetPart p in parts) { int c; if (int.TryParse(p.Count, NumberStyles.None, CultureInfo.InvariantCulture, out c) && c >= n) n = c + 1; }
            }
            else if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out n)) throw new ModKitException("\"" + text + "\" is not a number of items");
            Editor().AddPart(set, n);
            countBox.Text = "";
            host.EditedOutside();
            hitList.Invalidate();
            ItemSet s = set;
            ShowSet(s, n.ToString(CultureInfo.InvariantCulture));
            host.SayOutside("bonus for " + n + " items added; give it values below\r\n\r\n" + Describe(s));
        }

        void RemovePart()
        {
            if (set == null || part == null) throw new ModKitException("choose a bonus of a set first");
            EndEdits();
            string count = part.Count;
            Editor().RemovePart(set, part);
            part = null;
            Refresh("bonus for " + count + " items removed");
        }

        // ---- add, clone, remove ----

        long NewId()
        {
            string text = newIdBox.Text.Trim();
            long v;
            if (text.Length > 0)
            {
                if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out v)) throw new ModKitException("\"" + text + "\" is not an id");
                return v;   // Add and Clone refuse an id this or another enabled mod has (ItemSetEditor.Others)
            }
            List<long> free = Editor().NextIds(host.SharedSession().OtherMods(), 1, null);
            if (free.Count == 0) throw new ModKitException("no free id is left in " + ItemSetRules.From + "-" + ItemSetRules.To);
            return free[0];
        }

        void Add()
        {
            EndEdits();
            ItemSet s = Editor().Add(NewId());
            Opened(s, "set " + Editor().Get(s, "id") + " added, empty: give it a name, items and bonuses. Save mod, then Plan checks it per target.");
        }

        void Clone()
        {
            if (set == null) throw new ModKitException("choose the set to copy first");
            EndEdits();
            string source = Editor().Get(set, "id");
            ItemSet s = Editor().Clone(set, NewId());
            Opened(s, "set " + Editor().Get(s, "id") + " added as a copy of " + source + ", with bonuses of its own. Save mod, then Plan checks it per target.");
        }

        void Opened(ItemSet s, string said)
        {
            host.EditedOutside();
            newIdBox.Text = "";
            searchBox.Text = "";
            FillHits(Editor().Get(s, "id"));
            ShowSet(s);
            host.SayOutside(said + "\r\n\r\n" + Describe(s));
        }

        void Undo()
        {
            if (set == null) throw new ModKitException("choose a set first");
            EndEdits();
            ItemSetEditor ed = Editor();
            ed.Revert(set);
            Refresh("set " + ed.Get(set, "id") + " is as shipped again");
        }

        void RemoveOrRestore()
        {
            if (set == null) throw new ModKitException("choose a set first");
            EndEdits();
            ItemSetEditor ed = Editor();
            string id = ed.Get(set, "id");
            if (set.IsDeleted)
            {
                ed.Restore(set);
                Refresh("set " + id + " is back");
                return;
            }
            bool added = set.IsAdded;
            ed.Remove(set);
            if (added)
            {
                host.EditedOutside();
                ShowSet(null);
                FillHits(null);
                host.SayOutside("set " + id + " was new in the mod and is gone again; its id is free");
            }
            else Refresh("set " + id + " removed on every side (Remove / restore brings it back)");
        }
    }
}
