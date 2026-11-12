// The "MS2 Global drops" view: the server's global drop tables (see MS2.ModKit.GlobalDropEditor), in the style of the
// MS2 Spawns view. A choice between the global drop boxes and the item groups, a find box (id, comment or item id),
// the list, the lines of the chosen box (one per item group: monster levels, counts and their chances) or group (one
// per item: levels, count range, weight, rarity) as a grid, the checks, Add / Clone / Remove for lines and for whole
// boxes and groups, and the Plan / Apply / Revert buttons and output box of the MS2 Tables view.
//
// Every edit is an op of the mod the MS2 Tables view is editing. The tables are read when the view is first shown.
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
    public class GlobalDropsView : AbstractView
    {
        public const string ViewId = "MS2.Tables.GlobalDropsView";
        const int RowHeight = 34;

        sealed class ItemInfo
        {
            internal string Name, Icon;
        }

        TablesView host;
        GlobalDropEditor editor;
        ItemEditor items;
        readonly Dictionary<long, ItemInfo> itemInfo = new Dictionary<long, ItemInfo>();
        bool groupMode;             // the list shows item groups
        long shown = -1;            // the id of the box or group shown
        bool shownGroup;
        bool colsGroup;             // the grid has the columns of an item group
        DropHolder holder;
        List<DropLine> rows = new List<DropLine>();
        string[] cols = GlobalDropRules.LinkFields;
        bool filling, loaded;

        readonly ComboBox kindBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        readonly TextBox searchBox = new TextBox { Width = 160 };
        readonly Label hitsLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListBox hitList = new ListBox { Dock = DockStyle.Left, Width = 290, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = RowHeight };
        readonly Label holderLabel = new Label { AutoSize = true, Margin = new Padding(24, 8, 8, 0) };
        readonly TextBox lineBox = new TextBox { Width = 90 };
        readonly TextBox idBox = new TextBox { Width = 90 };
        readonly TextBox commentBox = new TextBox { Width = 180 };
        readonly DataGridView grid = new DataGridView { Dock = DockStyle.Fill };
        readonly ListBox checkList = new ListBox { Dock = DockStyle.Bottom, Height = 80, IntegralHeight = false, HorizontalScrollbar = true };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 130 };
        readonly ComboBox targetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();
        readonly BoldFont bold = new BoldFont();

        // the grid of a group starts with the item's icon and name; the grid of a box ends with what the group holds
        int Lead { get { return colsGroup ? 2 : 0; } }

        public GlobalDropsView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(Lbl("show"));
            kindBox.Items.Add("drop boxes");
            kindBox.Items.Add("item groups");
            kindBox.SelectedIndex = 0;
            tips.SetToolTip(kindBox, "A global drop box is what an NPC names in globalDropBoxId; each of its lines draws from an item group");
            top.Controls.Add(kindBox);
            top.Controls.Add(Lbl("find"));
            top.Controls.Add(searchBox);
            tips.SetToolTip(searchBox, "A part of the id or the comment, or of an item id (a box is found through the groups of its lines); empty lists all");
            top.Controls.Add(Btn("Find", "Lists the boxes or groups whose id, comment or items hold the text", delegate { Find(); }));
            top.Controls.Add(hitsLabel);
            top.Controls.Add(holderLabel);

            FlowLayoutPanel applyBar = Bar();
            applyBar.Controls.Add(Lbl("Apply to"));
            targetBox.Items.Add("workspace (patched archives)");
            targetBox.Items.Add("game folder (loose files)");
            tips.SetToolTip(targetBox, "Where Apply writes the client's tables (the same choice as in the MS2 Tables view). The server's archives and the global client's always go to the workspace.");
            applyBar.Controls.Add(targetBox);
            applyBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change, with the drop checks; writes nothing", delegate { return host.DoPlan(); }));
            applyBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change, to every target at once", delegate { return host.DoApply(); }));
            applyBar.Controls.Add(Job("Revert", "Removes what Apply wrote, on every target", delegate { return host.DoRevert(); }));

            FlowLayoutPanel rowBar = Bar();
            rowBar.Controls.Add(Btn("Add line", "Adds a line to what is shown: to a group the item whose id is in the box, to a drop box a line for the item group whose id is in the box", delegate { AddLine(); }));
            rowBar.Controls.Add(lineBox);
            tips.SetToolTip(lineBox, "For Add line: an item id (a group is shown) or the id of an item group (a drop box is shown)");
            rowBar.Controls.Add(Btn("Clone line", "Adds a copy of the selected line", delegate { CloneLine(); }));
            rowBar.Controls.Add(Btn("Remove / restore line", "Removes the selected line (a new one goes away), or brings a removed shipped line back", delegate { RemoveOrRestoreLine(); }));
            rowBar.Controls.Add(Btn("Undo changes", "Drops every change the mod makes to the selected shipped line", delegate { Undo(); }));
            rowBar.Controls.Add(Btn("Open group", "Shows the item group of the selected line of a drop box", delegate { OpenGroup(); }));
            rowBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same mod and Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            FlowLayoutPanel holderBar = Bar();
            holderBar.Controls.Add(Btn("New", "Adds an empty drop box or item group (what the list shows) with the id and comment in the boxes; no id = the next free one", delegate { AddHolder(false); }));
            holderBar.Controls.Add(Btn("Copy", "Adds a copy of what is shown, with its lines, under the id and comment in the boxes; no id = the next free one", delegate { AddHolder(true); }));
            holderBar.Controls.Add(Lbl("id"));
            holderBar.Controls.Add(idBox);
            holderBar.Controls.Add(Lbl("comment"));
            holderBar.Controls.Add(commentBox);
            tips.SetToolTip(commentBox, "A note for people; the game does not read it");
            holderBar.Controls.Add(Btn("Set comment", "Gives what is shown the comment in the box", delegate { SetComment(); }));
            holderBar.Controls.Add(Btn("Remove / restore", "Removes the drop box or item group shown (a new one goes away), or brings a removed shipped one back", delegate { RemoveOrRestoreHolder(); }));

            SetupGrid(grid);
            grid.RowTemplate.Height = RowHeight;
            grid.CellValueNeeded += OnValueNeeded;
            grid.CellValuePushed += OnValuePushed;
            grid.CellPainting += OnPainting;
            grid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e)
            {
                if (editor == null || host == null || host.Busy || e.RowIndex < 0 || e.RowIndex >= rows.Count) return;
                DropLine r = rows[e.RowIndex];
                int f = e.ColumnIndex - Lead;
                if (r.IsDeleted) e.CellStyle.ForeColor = SystemColors.GrayText;
                else if (f < 0 || f >= cols.Length) e.CellStyle.BackColor = SystemColors.Control;
                else if (editor.IsEdited(r, cols[f])) e.CellStyle.Font = bold.Of(grid.Font);
            };
            grid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e)
            {
                int f = e.ColumnIndex - Lead;
                if (editor == null || e.RowIndex >= rows.Count || rows[e.RowIndex].IsDeleted || f < 0 || f >= cols.Length) e.Cancel = true;
            };
            tips.SetToolTip(checkList, "The checks of what the mod adds or changes here; Plan checks every box and group the mods add or change");

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(grid);
            right.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            right.Controls.Add(checkList);
            right.Controls.Add(rowBar);
            right.Controls.Add(holderBar);

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
                delegate { return rowBar.Height + holderBar.Height + 8 + grid.ColumnHeadersHeight + 5 * grid.RowTemplate.Height + 4; });
            ViewLayout.CentreOnFirstShow(this);
            targetBox.SelectedIndexChanged += delegate { if (!filling && host != null) host.TargetIndex = targetBox.SelectedIndex; };
            kindBox.SelectedIndexChanged += delegate
            {
                if (filling) return;
                Guard(delegate { groupMode = kindBox.SelectedIndex == 1; Find(); });
            };

            searchBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                Guard(delegate { Find(); });
            };
            hitList.SelectedIndexChanged += delegate { if (!filling) Guard(delegate { OpenSelected(); }); };
            hitList.DrawItem += OnDrawHit;
            SetColumns();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible || loaded || !IsHandleCreated) return;
            loaded = true;
            BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) Guard(delegate { Find(); }); });
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
            return ViewLayout.FitColumn(new DataGridViewTextBoxColumn { HeaderText = header, ReadOnly = readOnly, Width = width, FillWeight = width, SortMode = DataGridViewColumnSortMode.NotSortable });
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

        void EndEdits() { grid.EndEdit(); }

        void CancelEdits() { grid.CancelEdit(); }

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
            Forget();
            if (h == null) { rows.Clear(); return; }
            Show(null);
            hitList.Items.Clear();
            h.Changed += OnHostChanged;
            h.Flushing += EndEdits;
            h.BusyChanged += OnHostBusy;
            h.Said += OnHostSaid;
            h.TargetChanged += OnHostTarget;
            OnHostTarget();
            OutputWrap.Set(outputBox, h.OutputText);
            OnHostBusy(h.Busy);
            if (loaded) Reload();
        }

        void Forget()
        {
            editor = null;
            items = null;
            itemInfo.Clear();
            holder = null;
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
                Logger.Warning("MS2 Global drops: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        GlobalDropEditor Editor()
        {
            EditorSession s = host.SharedSession();
            if (editor == null || editor.Mod != s.Mod || editor.Data != s.Data)
            {
                Forget();
                editor = new GlobalDropEditor(s.Data, s.Mod);
            }
            return editor;
        }

        void OnHostSaid(string text) { OutputWrap.Set(outputBox, text); }

        void OnHostBusy(bool on) { ViewLayout.Lock(this, lockable, on, outputBox); }

        // the mod was replaced or edited in another view: read the tables again (when this view is looked at) and show the same box or group
        void OnHostChanged()
        {
            CancelEdits();
            Forget();
            if (!Visible) { ClearGrid(); hitList.Items.Clear(); loaded = false; return; }
            Reload();
        }

        void Reload()
        {
            try { FillHits(); }
            catch (ModKitException) { Show(null); }
            catch (System.IO.IOException) { Show(null); }
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
            info = new ItemInfo { Name = "" };
            // a running job (Plan, Apply) reads the same data on its own thread: the paint waits for its end
            if (host == null || host.Busy || id <= 0) return info;
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
            catch (OutOfMemoryException) { return info; }      // not kept: the next paint asks again
            catch (ModKitException) { info.Name = "(no such item)"; }
            catch (System.IO.IOException) { return info; }
            itemInfo[id] = info;
            return info;
        }

        long ItemOf(DropLine r)
        {
            long id;
            return long.TryParse(editor.Get(r, GlobalDropRules.Item), NumberStyles.None, CultureInfo.InvariantCulture, out id) ? id : -1;
        }

        // ---- the list ----

        DropHolder Lookup(bool group, long id)
        {
            if (id < 0) return null;
            GlobalDropEditor ed = Editor();
            DropHolder h = group ? ed.Group(id) : ed.Box(id);
            if (h != null) return h;
            foreach (DropHolder o in group ? ed.Groups() : ed.Boxes()) if (ed.Id(o) == id) return o;     // a removed one
            return null;
        }

        void Find()
        {
            EndEdits();
            loaded = true;
            FillHits();
            if (hitList.Items.Count > 0 && hitList.SelectedIndex < 0) hitList.SelectedIndex = 0;
            else if (hitList.Items.Count == 0) Show(null);
        }

        void FillHits()
        {
            GlobalDropEditor ed = Editor();
            List<DropHolder> hits = groupMode ? ed.FindGroups(searchBox.Text) : ed.FindBoxes(searchBox.Text);
            var changed = new Dictionary<DropHolder, bool>();
            foreach (DropHolder h in hits) changed[h] = ed.IsChanged(h);
            // what the mod changes first, then by id
            hits.Sort(delegate(DropHolder a, DropHolder b)
            {
                return changed[a] != changed[b] ? (changed[a] ? -1 : 1) : ed.Id(a).CompareTo(ed.Id(b));
            });
            DropHolder keep = shownGroup == groupMode ? Lookup(shownGroup, shown) : null;
            filling = true;
            bool found = false;
            try
            {
                if (kindBox.SelectedIndex != (groupMode ? 1 : 0)) kindBox.SelectedIndex = groupMode ? 1 : 0;
                hitList.BeginUpdate();
                hitList.Items.Clear();
                foreach (DropHolder h in hits)
                {
                    hitList.Items.Add(h);
                    if (h == keep) { hitList.SelectedIndex = hitList.Items.Count - 1; found = true; }
                }
                hitList.EndUpdate();
            }
            finally { filling = false; }
            hitsLabel.Text = hits.Count + (groupMode ? " group(s), " + ed.Groups().Count : " box(es), " + ed.Boxes().Count) + " in all";
            Show(found ? keep : null);
        }

        void OpenSelected()
        {
            var h = hitList.SelectedItem as DropHolder;
            if (h == null) return;
            EndEdits();
            Show(h);
            host.SayOutside(Describe());
        }

        void OnDrawHit(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= hitList.Items.Count || editor == null || host == null || host.IsDisposed || host.Busy) return;
            var h = hitList.Items[e.Index] as DropHolder;
            if (h == null) return;
            string comment = editor.Get(h, GlobalDropRules.Comment);
            int n = editor.LineCount(h);
            bool marked = h.IsAdded || h.IsDeleted || editor.IsChanged(h);
            string line1 = editor.IdText(h) + "  " + (comment.Length > 0 ? comment : "(no comment)");
            string line2 = n + (h.IsGroup ? (n == 1 ? " item" : " items") : (n == 1 ? " line" : " lines")) + (h.IsDeleted ? "  (removed)" : h.IsAdded ? "  (new)" : marked ? "  (changed)" : "");
            int half = e.Bounds.Height / 2;
            var t1 = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 1, e.Bounds.Width - 8, half);
            var t2 = new Rectangle(t1.X, e.Bounds.Y + half - 1, t1.Width, half);
            TextRenderer.DrawText(e.Graphics, line1, marked ? bold.Of(e.Font) : e.Font, t1, e.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, line2, e.Font, t2, (e.State & DrawItemState.Selected) == 0 ? SystemColors.GrayText : e.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        }

        // ---- the lines ----

        void SetColumns()
        {
            grid.Columns.Clear();
            colsGroup = shownGroup;
            grid.RowTemplate.Height = colsGroup ? RowHeight : 22;
            cols = colsGroup ? GlobalDropRules.ItemFields : GlobalDropRules.LinkFields;
            if (colsGroup)
            {
                grid.Columns.Add(Col("", RowHeight + 4, true));
                grid.Columns[0].MinimumWidth = RowHeight + 4;
                grid.Columns.Add(Col("item", 190, true));
            }
            foreach (string c in cols)
                grid.Columns.Add(Col(c == GlobalDropRules.Link ? "group" : c == GlobalDropRules.Chance ? "dropCountProbability" : c,
                    c == GlobalDropRules.Chance || c == GlobalDropRules.Count ? 130 : c == "mapDependency" ? 110 : 75, false));
            if (!colsGroup) grid.Columns.Add(Col("the group holds", 260, true));
        }

        DropLine Current()
        {
            int i = grid.CurrentCell == null ? -1 : grid.CurrentCell.RowIndex;
            return i >= 0 && i < rows.Count ? rows[i] : null;
        }

        void ClearGrid()
        {
            filling = true;
            try { grid.RowCount = 0; rows = new List<DropLine>(); checkList.Items.Clear(); }
            finally { filling = false; }
        }

        void Show(DropHolder h)
        {
            CancelEdits();
            holder = h;
            filling = true;
            try
            {
                grid.RowCount = 0;
                checkList.Items.Clear();
                rows = new List<DropLine>();
                if (h == null || host == null) { holderLabel.Text = groupMode ? "no item group chosen" : "no drop box chosen"; return; }
                GlobalDropEditor ed = Editor();
                shown = ed.Id(h);
                shownGroup = h.IsGroup;
                if (colsGroup != shownGroup || grid.Columns.Count == 0) SetColumns();
                rows = ed.Lines(h);
                string comment = ed.Get(h, GlobalDropRules.Comment);
                bool changed = ed.IsChanged(h);
                holderLabel.Text = (h.IsGroup ? "Item group " : "Global drop box ") + ed.IdText(h) + (comment.Length > 0 ? " (" + comment + ")" : "") + ": "
                    + (h.IsDeleted ? "removed by the mod" : rows.Count + (h.IsGroup ? (rows.Count == 1 ? " item" : " items") : (rows.Count == 1 ? " line" : " lines"))
                        + (h.IsAdded ? ", new in the mod" : changed ? ", changed by the mod" : ""));
                grid.RowCount = rows.Count;
                // shipped lines are left alone: what the mod adds or changes is checked
                if (changed)
                {
                    foreach (string t in ed.Check(h)) checkList.Items.Add(t);
                    for (int i = 0; i < rows.Count; i++)
                        if (rows[i].IsChanged)
                            foreach (string t in ed.Check(rows[i]))
                                checkList.Items.Add((h.IsGroup ? "item " + ed.Get(rows[i], GlobalDropRules.Item) : "line " + (i + 1)) + ": " + t);
                }
                if (checkList.Items.Count == 0) checkList.Items.Add(changed ? "no problem found in what the mod adds or changes here" : "the mod changes nothing here");
            }
            finally { filling = false; }
            grid.Invalidate();
        }

        string Describe()
        {
            GlobalDropEditor ed = Editor();
            if (holder == null) return "";
            var sb = new StringBuilder();
            if (holder.IsGroup)
            {
                sb.Append("item group ").Append(ed.IdText(holder)).Append(" in ").Append(GlobalDropRules.Archive).Append(' ').Append(GlobalDropRules.SetEntry);
                List<DropHolder> used = ed.BoxesUsing(shown);
                sb.Append("\r\nDrop boxes with a line for it: ");
                if (used.Count == 0) sb.Append("none");
                for (int i = 0; i < used.Count && i < 12; i++) sb.Append(i > 0 ? ", " : "").Append(ed.IdText(used[i]));
                if (used.Count > 12) sb.Append(" and ").Append(used.Count - 12).Append(" more");
                sb.Append("\r\nA line is one item the group can give: for monsters of minLevel to maxLevel, minCount to maxCount pieces, of the rarity in grade. The weights say how often an item is drawn against the others; a group without weights draws evenly.");
            }
            else
            {
                sb.Append("global drop box ").Append(ed.IdText(holder)).Append(" in ").Append(GlobalDropRules.Archive).Append(' ').Append(GlobalDropRules.BoxEntry);
                sb.Append("\r\nAn NPC drops from the box its dropiteminfo names in globalDropBoxId (MS2 NPCs view). A line draws from one item group for monsters of minLevel to maxLevel: dropCount lists how many items, dropCountProbability the chance of each count in 1/10000.");
            }
            return sb.ToString();
        }

        void Refresh(string said)
        {
            host.EditedOutside();
            int at = grid.CurrentCell == null ? -1 : grid.CurrentCell.RowIndex, col = grid.CurrentCell == null ? 0 : grid.CurrentCell.ColumnIndex;
            if (editor == null) Reload();       // the tables were read again: the same box or group by its id
            else Show(holder);
            hitList.Invalidate();
            if (at >= 0 && at < grid.RowCount && col < grid.ColumnCount) grid.CurrentCell = grid[col, at];
            if (said != null) host.SayOutside(said);
        }

        string Holds(DropLine r)
        {
            var sb = new StringBuilder();
            foreach (long g in editor.GroupsOf(r))
            {
                DropHolder h = editor.Group(g);
                if (sb.Length > 0) sb.Append("; ");
                if (h == null) { sb.Append("no group ").Append(g); continue; }
                string comment = editor.Get(h, GlobalDropRules.Comment);
                int n = editor.LineCount(h);
                sb.Append(comment.Length > 0 ? comment + ": " : "").Append(n).Append(n == 1 ? " item" : " items");
            }
            return sb.ToString();
        }

        void OnValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (editor == null || host == null || host.Busy || e.RowIndex < 0 || e.RowIndex >= rows.Count) return;
            DropLine r = rows[e.RowIndex];
            int f = e.ColumnIndex - Lead;
            if (f >= 0 && f < cols.Length) e.Value = editor.Get(r, cols[f]);
            else if (colsGroup) e.Value = e.ColumnIndex == 1 ? Info(ItemOf(r)).Name : "";
            else e.Value = Holds(r);
        }

        void OnPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (!colsGroup || e.ColumnIndex != 0 || e.RowIndex < 0 || editor == null || host == null || host.IsDisposed || host.Busy) return;
            e.Paint(e.ClipBounds, DataGridViewPaintParts.All);
            if (e.RowIndex < rows.Count)
            {
                IconCache cache = Cache();
                int size = e.CellBounds.Height - 4;
                var box = new Rectangle(e.CellBounds.X + 2, e.CellBounds.Y + 2, size, size);
                IconCache.Draw(e.Graphics, box, cache != null ? cache.Get(Info(ItemOf(rows[e.RowIndex])).Icon, grid) : null);
            }
            e.Handled = true;
        }

        void OnValuePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            int f = e.ColumnIndex - Lead;
            if (f < 0 || f >= cols.Length || e.RowIndex < 0 || e.RowIndex >= rows.Count) return;
            DropLine r = rows[e.RowIndex];
            string field = cols[f];
            string v = ((e.Value as string) ?? "").Trim();
            Guard(delegate
            {
                GlobalDropEditor ed = Editor();
                if (ed != editor || !rows.Contains(r)) throw new ModKitException("the tables were read again; choose the line again");
                if (v == ed.Get(r, field)) return;
                ed.Set(r, field, v);
                Refresh(null);
            });
        }

        // ---- add, clone, remove ----

        static long Number(string text, string what)
        {
            long v;
            if (!long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out v)) throw new ModKitException(text.Trim().Length == 0 ? "give " + what + " first" : "\"" + text.Trim() + "\" is not " + what);
            return v;
        }

        DropHolder Shown()
        {
            Editor();
            if (holder == null) holder = Lookup(shownGroup, shown);
            if (holder == null) throw new ModKitException("choose a drop box or an item group first");
            return holder;
        }

        void Select(DropLine made)
        {
            int at = rows.IndexOf(made);
            if (at >= 0) grid.CurrentCell = grid[Math.Min(Lead, grid.ColumnCount - 1), at];
        }

        void AddLine()
        {
            EndEdits();
            DropHolder h = Shown();
            GlobalDropEditor ed = Editor();
            DropLine made;
            string said;
            if (h.IsGroup)
            {
                long item = Number(lineBox.Text, "an item id");
                made = ed.AddItem(h, item);
                said = "item " + item + " added to group " + ed.IdText(h) + ", for every monster level, one piece: set the counts, weight and grade as needed. Save mod, then Plan checks that the server has the item.";
            }
            else
            {
                long group = Number(lineBox.Text, "the id of an item group");
                made = ed.AddLink(h, group);
                said = "line for item group " + group + " added to drop box " + ed.IdText(h) + ": one item at a chance of 1 in 10 (dropCount 0,1 with dropCountProbability 9000,1000), for every monster level.";
            }
            lineBox.Text = "";
            Refresh(said);
            Select(made);
        }

        void CloneLine()
        {
            DropLine r = Current();
            if (r == null) throw new ModKitException("select the line to copy first");
            EndEdits();
            DropLine made = Editor().Clone(r);
            Refresh("line added as a copy of the selected one");
            Select(made);
        }

        void Undo()
        {
            DropLine r = Current();
            if (r == null) throw new ModKitException("select a line first");
            EndEdits();
            Editor().Revert(r);
            Refresh("the line is as shipped again");
        }

        void RemoveOrRestoreLine()
        {
            DropLine r = Current();
            if (r == null) throw new ModKitException("select a line first");
            EndEdits();
            GlobalDropEditor ed = Editor();
            if (r.IsDeleted) { ed.Restore(r); Refresh("the line is back"); return; }
            bool added = r.IsAdded;
            ed.Remove(r);
            Refresh(added ? "the line was new in the mod and is gone again" : "the line is removed (Remove / restore line brings it back)");
        }

        void OpenGroup()
        {
            DropLine r = Current();
            if (r == null || r.Holder.IsGroup) throw new ModKitException("select a line of a drop box first");
            EndEdits();
            GlobalDropEditor ed = Editor();
            List<long> ids = ed.GroupsOf(r);
            if (ids.Count == 0) throw new ModKitException("the line names no item group");
            DropHolder g = ed.Group(ids[0]);
            if (g == null) throw new ModKitException("there is no item group " + ids[0] + "; add it with New while the list shows item groups");
            Go(g);
            host.SayOutside(Describe());
        }

        // shows a box or group, with the list on its kind and the find box empty
        void Go(DropHolder h)
        {
            groupMode = h.IsGroup;
            shownGroup = h.IsGroup;
            shown = Editor().Id(h);
            searchBox.Text = "";
            FillHits();
        }

        void AddHolder(bool copy)
        {
            EndEdits();
            GlobalDropEditor ed = Editor();
            DropHolder from = copy ? Shown() : null;
            if (from != null && from.IsDeleted) throw new ModKitException("it is removed; restore it before copying");
            bool group = copy ? from.IsGroup : groupMode;
            long id = idBox.Text.Trim().Length > 0 ? Number(idBox.Text, "an id") : ed.FreeId(group);
            DropHolder made = copy ? ed.Clone(from, id, commentBox.Text) : group ? ed.AddGroup(id, commentBox.Text) : ed.AddBox(id, commentBox.Text);
            string fromId = copy ? ed.IdText(from) : "";
            host.EditedOutside();
            idBox.Text = "";
            commentBox.Text = "";
            Go(Lookup(group, id) ?? made);
            host.SayOutside((group ? "item group " : "global drop box ") + id + (copy ? " added as a copy of " + fromId : " added, empty: add its lines with Add line")
                + (group ? ". Nothing drops from it until a drop box has a line for it." : ". An NPC drops from it once its globalDropBoxId names it (MS2 NPCs view).") + " Save mod, then Plan checks it.");
        }

        void SetComment()
        {
            EndEdits();
            DropHolder h = Shown();
            Editor().SetComment(h, commentBox.Text);
            commentBox.Text = "";
            Refresh("comment set");
        }

        void RemoveOrRestoreHolder()
        {
            EndEdits();
            DropHolder h = Shown();
            GlobalDropEditor ed = Editor();
            string what = (h.IsGroup ? "item group " : "global drop box ") + ed.IdText(h);
            if (h.IsDeleted)
            {
                ed.Restore(h);
                Refresh(what + " is back");
                return;
            }
            bool added = h.IsAdded;
            int used = h.IsGroup ? ed.BoxesUsing(ed.Id(h)).Count : 0;
            ed.Remove(h);
            host.EditedOutside();
            if (added) { shown = -1; holder = null; }
            Reload();
            host.SayOutside(added ? what + " was new in the mod and is gone again"
                : what + " is removed with its lines (Remove / restore brings it back)" + (used > 0 ? "; " + used + " drop box(es) still have a line for it: Plan names them" : ""));
        }
    }
}
