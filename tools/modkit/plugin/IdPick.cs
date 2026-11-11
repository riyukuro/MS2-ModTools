// The id pickers: a small "…" button at the right of a grid cell that holds an npc, item, skill, effect, quest, shop
// or drop box id, opened by a click on it or by F4, and one shared dialog that searches that kind of id by id or name
// (the same Find the views' own search lists use, the mod's new ids first). The picked id goes through the grid's
// normal edit, so the view sets it exactly as if it had been typed.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using MS2.ModKit;

namespace MS2.Tables
{
    internal static class IdPick
    {
        /// <summary>The kind of id (IdLookup.Npc, ...) an editable cell holds, or null: no picker on that cell.</summary>
        internal delegate string KindOf(int row, int column);

        const int ButtonWidth = 18;

        static EditorSession lookupSession;
        static Mod lookupMod;
        static IdLookup lookup;

        // one lookup while the session and its mod stay the same: the editors behind it read their names once
        static IdLookup Lookup(EditorSession s)
        {
            if (lookup == null || lookupSession != s || lookupMod != s.Mod)
            {
                lookup = new IdLookup(s);
                lookupSession = s;
                lookupMod = s.Mod;
            }
            return lookup;
        }

        internal static string Hint(string kind) { return "F4 or the … button: pick " + Article(kind) + " " + kind + " by id or name"; }

        static string Article(string kind) { return kind == IdLookup.Item ? "an" : kind == IdLookup.Effect ? "an" : "a"; }

        /// <summary>Puts the picker on the cells of a grid that kindOf names; session gives the session to search in.</summary>
        internal static void Attach(DataGridView grid, KindOf kindOf, Func<EditorSession> session)
        {
            grid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e)
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0 || kindOf(e.RowIndex, e.ColumnIndex) == null) return;
                Padding p = e.CellStyle.Padding;
                e.CellStyle.Padding = new Padding(p.Left, p.Top, Math.Max(p.Right, ButtonWidth), p.Bottom);
            };
            grid.CellPainting += delegate(object sender, DataGridViewCellPaintingEventArgs e)
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0 || kindOf(e.RowIndex, e.ColumnIndex) == null) return;
                if (!e.Handled) { e.Paint(e.ClipBounds, DataGridViewPaintParts.All); e.Handled = true; }
                Rectangle b = ButtonOf(e.CellBounds);
                ButtonRenderer.DrawButton(e.Graphics, b, "…", grid.Font, false, System.Windows.Forms.VisualStyles.PushButtonState.Normal);
            };
            grid.CellToolTipTextNeeded += delegate(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                string kind = kindOf(e.RowIndex, e.ColumnIndex);
                if (kind == null) return;
                string hint = "(" + Hint(kind) + ")";
                e.ToolTipText = string.IsNullOrEmpty(e.ToolTipText) ? hint : e.ToolTipText + "  " + hint;
            };
            grid.CellMouseClick += delegate(object sender, DataGridViewCellMouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left || e.RowIndex < 0 || e.ColumnIndex < 0) return;
                string kind = kindOf(e.RowIndex, e.ColumnIndex);
                if (kind == null) return;
                Rectangle cell = grid.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
                if (e.X < cell.Width - ButtonWidth - 2) return;
                Open(grid, e.RowIndex, e.ColumnIndex, kind, session);
            };
            grid.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.F4 || e.Modifiers != Keys.None || grid.CurrentCell == null) return;
                string kind = kindOf(grid.CurrentCell.RowIndex, grid.CurrentCell.ColumnIndex);
                if (kind == null) return;
                e.Handled = true;
                Open(grid, grid.CurrentCell.RowIndex, grid.CurrentCell.ColumnIndex, kind, session);
            };
            // F4 while the cell is being typed in: the editing box has the keys
            grid.EditingControlShowing += delegate(object sender, DataGridViewEditingControlShowingEventArgs e)
            {
                e.Control.KeyDown -= OnEditKey;
                e.Control.KeyDown += OnEditKey;
                e.Control.Tag = new object[] { kindOf, session };
            };
        }

        static void OnEditKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.F4 || e.Modifiers != Keys.None) return;
            var c = sender as Control;
            var grid = c != null ? c.Parent as DataGridView ?? (c.Parent != null ? c.Parent.Parent as DataGridView : null) : null;
            var tag = c != null ? c.Tag as object[] : null;
            if (grid == null || tag == null || grid.CurrentCell == null) return;
            var kindOf = (KindOf)tag[0];
            string kind = kindOf(grid.CurrentCell.RowIndex, grid.CurrentCell.ColumnIndex);
            if (kind == null) return;
            e.Handled = true;
            int row = grid.CurrentCell.RowIndex, column = grid.CurrentCell.ColumnIndex;
            string typed = c.Text;
            grid.CancelEdit();
            grid.EndEdit();
            Open(grid, row, column, kind, (Func<EditorSession>)tag[1], typed);
        }

        static Rectangle ButtonOf(Rectangle cell)
        {
            return new Rectangle(cell.Right - ButtonWidth - 1, cell.Top + 1, ButtonWidth, cell.Height - 3);
        }

        static void Open(DataGridView grid, int row, int column, string kind, Func<EditorSession> session)
        {
            object v = grid[column, row].Value;
            Open(grid, row, column, kind, session, v == null ? "" : v.ToString());
        }

        static void Open(DataGridView grid, int row, int column, string kind, Func<EditorSession> session, string current)
        {
            IdLookup l;
            try { l = Lookup(session()); }
            catch (ModKitException ex) { MessageBox.Show(grid.FindForm(), ex.Message, "Pick " + Article(kind) + " " + kind, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            string picked;
            using (var d = new PickDialog(l, kind, current))
            {
                if (d.ShowDialog(grid.FindForm()) != DialogResult.OK) return;
                picked = d.Picked;
            }
            if (picked == null || row >= grid.RowCount) return;
            // through the grid's own edit: the view's CellValuePushed sets it, as a typed id
            grid.CurrentCell = grid[column, row];
            if (!grid.BeginEdit(true) || grid.EditingControl == null) return;
            grid.EditingControl.Text = picked;
            grid.EndEdit();
        }

        /// <summary>A "Pick…" button for a text box that holds an id of a kind.</summary>
        internal static Button ForBox(TextBox box, string kind, Func<EditorSession> session, ToolTip tips)
        {
            var b = new Button { Text = "Pick…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(2, 2, 6, 2) };
            tips.SetToolTip(b, "Pick " + Article(kind) + " " + kind + " by id or name for the box at the left (F4 in the box does the same)");
            EventHandler open = delegate
            {
                IdLookup l;
                try { l = Lookup(session()); }
                catch (ModKitException ex) { MessageBox.Show(box.FindForm(), ex.Message, "Pick " + Article(kind) + " " + kind, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                using (var d = new PickDialog(l, kind, box.Text))
                    if (d.ShowDialog(box.FindForm()) == DialogResult.OK && d.Picked != null) box.Text = d.Picked;
            };
            b.Click += open;
            box.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.F4 || e.Modifiers != Keys.None) return;
                e.Handled = true;
                open(box, EventArgs.Empty);
            };
            return b;
        }

        /// <summary>The shared pick dialog: a search box, the hits (id, name, new), OK / Cancel.</summary>
        sealed class PickDialog : Form
        {
            const int Max = 300;
            readonly IdLookup lookup;
            readonly string kind;
            readonly long current;
            readonly TextBox searchBox = new TextBox { Dock = DockStyle.Fill };
            readonly ListView list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
            readonly Label status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            readonly Button ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
            readonly Timer typing = new Timer { Interval = 250 };

            internal string Picked { get; private set; }

            internal PickDialog(IdLookup lookup, string kind, string currentText)
            {
                this.lookup = lookup;
                this.kind = kind;
                // a list of ids (a quest code "43,44"): the first one is shown chosen
                string first = (currentText ?? "").Split(',')[0].Trim();
                long id;
                current = long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out id) ? id : 0;

                Text = "Pick " + Article(kind) + " " + kind;
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.Sizable;
                MinimizeBox = MaximizeBox = false;
                ShowInTaskbar = false;
                Font = SystemFonts.MessageBoxFont;
                ClientSize = new Size(520, 440);
                MinimumSize = new Size(360, 280);

                // shops and drop boxes have no names: the column says what the mod does with the id
                bool byId = kind == IdLookup.Shop || kind == IdLookup.DropBox;
                list.Columns.Add("id", 100);
                list.Columns.Add(byId ? "in the mod" : "name", 330);
                list.Columns.Add("", 60);
                list.Resize += delegate { FitColumns(); };

                var top = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, RowCount = 1, AutoSize = true, Padding = new Padding(6, 6, 6, 2) };
                top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                top.Controls.Add(new Label { Text = byId ? "Id" : "Id or name", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 4, 0) }, 0, 0);
                top.Controls.Add(searchBox, 1, 0);
                var find = new Button { Text = "Find", AutoSize = true };
                find.Click += delegate { Search(); };
                top.Controls.Add(find, 2, 0);

                var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, ColumnCount = 3, RowCount = 1, AutoSize = true, Padding = new Padding(6, 2, 6, 6) };
                bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
                bottom.Controls.Add(status, 0, 0);
                bottom.Controls.Add(ok, 1, 0);
                bottom.Controls.Add(cancel, 2, 0);

                var middle = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 2, 6, 2) };
                middle.Controls.Add(list);
                Controls.Add(middle);
                Controls.Add(top);
                Controls.Add(bottom);
                AcceptButton = ok;
                CancelButton = cancel;

                typing.Tick += delegate { typing.Stop(); Search(); };
                searchBox.TextChanged += delegate { typing.Stop(); typing.Start(); };
                searchBox.KeyDown += delegate(object sender, KeyEventArgs e)
                {
                    // the arrows move in the list while the search box keeps the keys for typing
                    if ((e.KeyCode == Keys.Down || e.KeyCode == Keys.Up) && list.Items.Count > 0)
                    {
                        int i = list.SelectedIndices.Count > 0 ? list.SelectedIndices[0] : -1;
                        i = Math.Max(0, Math.Min(list.Items.Count - 1, i + (e.KeyCode == Keys.Down ? 1 : -1)));
                        list.Items[i].Selected = true;
                        list.EnsureVisible(i);
                        e.Handled = true;
                    }
                };
                list.SelectedIndexChanged += delegate { ok.Enabled = list.SelectedItems.Count > 0; };
                list.DoubleClick += delegate { if (list.SelectedItems.Count > 0) { DialogResult = DialogResult.OK; Close(); } };
                FormClosing += delegate(object sender, FormClosingEventArgs e)
                {
                    if (DialogResult == DialogResult.OK) Picked = list.SelectedItems.Count > 0 ? (string)list.SelectedItems[0].Tag : null;
                };
                Shown += delegate { searchBox.Focus(); };
                Search();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) typing.Dispose();
                base.Dispose(disposing);
            }

            void FitColumns()
            {
                int w = list.ClientSize.Width - list.Columns[0].Width - list.Columns[2].Width - 4;
                if (w > 80) list.Columns[1].Width = w;
            }

            void Search()
            {
                List<EntityHit> hits;
                try { hits = lookup.Find(kind, searchBox.Text, Max); }
                catch (ModKitException ex) { status.Text = ex.Message; return; }
                catch (System.IO.IOException ex) { status.Text = ex.Message; return; }
                bool empty = searchBox.Text.Trim().Length == 0;
                // on the first list the current id is shown even when it is not among the first ids
                if (empty && current != 0 && !hits.Exists(delegate(EntityHit h) { return h.Id == current; }))
                {
                    List<EntityHit> c = lookup.Find(kind, current.ToString(CultureInfo.InvariantCulture), 20);
                    EntityHit same = c.Find(delegate(EntityHit h) { return h.Id == current; });
                    if (same != null) hits.Insert(0, same);
                }
                list.BeginUpdate();
                list.Items.Clear();
                ListViewItem chosen = null;
                foreach (EntityHit h in hits)
                {
                    string id = h.Id.ToString(CultureInfo.InvariantCulture);
                    var it = new ListViewItem(new[] { id, h.Name ?? "", h.Added ? "new" : "" }) { Tag = id };
                    if (h.Added) it.BackColor = Color.FromArgb(214, 240, 214);
                    list.Items.Add(it);
                    if (h.Id == current && chosen == null) chosen = it;
                }
                if (chosen == null && list.Items.Count > 0 && !empty) chosen = list.Items[0];
                if (chosen != null) { chosen.Selected = true; chosen.EnsureVisible(); }
                list.EndUpdate();
                ok.Enabled = list.SelectedItems.Count > 0;
                FitColumns();
                if (hits.Count == 0) status.Text = empty ? "No " + kind + " ids in the data folders" : "No " + kind + " has an id starting with or a name containing \"" + searchBox.Text.Trim() + "\"";
                else status.Text = hits.Count + (hits.Count >= Max ? "+ (the first " + Max + "; type to narrow)" : "") + " shown" + (kind == IdLookup.Shop || kind == IdLookup.DropBox ? "; searched by id" : "; by id or name");
            }
        }
    }
}
