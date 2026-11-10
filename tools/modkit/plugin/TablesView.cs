// The "MS2 Tables" view: one XML entry of a data archive as a grid (a row per child element, a column per
// attribute), the pending edits as the mod.xml they will become, and Plan / Apply / Revert.
//
// The view holds no data logic. MS2.ModKit.EditorSession owns the folders and the mod being edited, and
// MS2.ModKit.TableDraft turns cell edits into mod.xml operations; this file only shows them. The grid is in virtual
// mode (it asks the draft for each cell), so a table of some ten thousand rows opens at once.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Emergent.Toolbench;
using Emergent.Toolbench.UI.Workbench;
using MS2.ModKit;

namespace MS2.Tables
{
    public class TablesView : AbstractView
    {
        public const string ViewId = "MS2.Tables.View";
        const int MaxEntries = 2000;

        EditorSession session;
        TableDraft draft;
        readonly List<string> columns = new List<string>();     // the draft's columns, then the ones added here
        bool filling;

        readonly ComboBox archiveBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        readonly TextBox filterBox = new TextBox { Width = 170 };
        readonly TextBox parentBox = new TextBox { Width = 150 };
        readonly ListBox entryList = new ListBox { Dock = DockStyle.Left, Width = 250, IntegralHeight = false };
        readonly DataGridView grid = new DataGridView { Dock = DockStyle.Fill };
        readonly TextBox attrBox = new TextBox { Width = 110 };
        readonly Label tableLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly TextBox modIdBox = new TextBox { Width = 130 };
        readonly ComboBox modsBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 150 };
        readonly TextBox pendingBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill };

        public TablesView()
        {
            Font mono = new Font(FontFamily.GenericMonospace, 8.25f);
            pendingBox.Font = mono;
            outputBox.Font = mono;
            pendingBox.BackColor = outputBox.BackColor = SystemColors.Window;
            pendingBox.ForeColor = outputBox.ForeColor = SystemColors.WindowText;

            // top: what to open
            FlowLayoutPanel top = Bar(DockStyle.Top);
            top.Controls.Add(Lbl("Archive"));
            top.Controls.Add(archiveBox);
            top.Controls.Add(Lbl("Entry filter"));
            top.Controls.Add(filterBox);
            top.Controls.Add(Lbl("Rows under"));
            top.Controls.Add(parentBox);
            top.Controls.Add(Btn("Open", "Opens the selected entry. \"Rows under\" is a selector of the element whose children are the rows; empty = the root element.", delegate { OpenSelected(); }));
            top.Controls.Add(Btn("Reload settings", "Reads the MS2 Mods settings again and lists the archives", delegate { Reload(); }));

            // above the grid: row tools
            FlowLayoutPanel rowBar = Bar(DockStyle.Top);
            rowBar.Controls.Add(Btn("Clone row", "Adds a row that starts as a copy of the selected row", delegate { CloneRow(); }));
            rowBar.Controls.Add(Btn("Delete / restore row", "Deletes the selected row, or takes its delete back", delegate { DeleteRow(); }));
            rowBar.Controls.Add(Btn("Remove attribute", "Removes the attribute of the selected cell from its row", delegate { RemoveAttribute(); }));
            rowBar.Controls.Add(attrBox);
            rowBar.Controls.Add(Btn("Add column", "Adds a column for an attribute no row has yet", delegate { AddColumn(); }));
            rowBar.Controls.Add(Btn("Copy edits to other side", "Writes this table's edits into the mod for the same table of the other data root (client / server) too", delegate { CopyToOtherSide(); }));
            rowBar.Controls.Add(tableLabel);

            // bottom: the mod, the commands, the two text panes
            FlowLayoutPanel modBar = Bar(DockStyle.Top);
            modBar.Controls.Add(Lbl("Mod id"));
            modBar.Controls.Add(modIdBox);
            modBar.Controls.Add(Btn("New mod", "Starts an empty mod with this id (the pending edits are dropped)", delegate { NewMod(); }));
            modBar.Controls.Add(Btn("Save mod", "Saves the pending edits as <mods folder>/<mod id>/mod.xml", delegate { SaveMod(); }));
            modBar.Controls.Add(modsBox);
            modBar.Controls.Add(Btn("Load mod", "Loads a mod folder (a name under the mods folder, or a path) into the grid", delegate { LoadMod(); }));
            modBar.Controls.Add(Btn("Plan", "Shows what the mods would change, with conflicts and warnings; writes nothing", delegate { DoPlan(); }));
            modBar.Controls.Add(Btn("Apply", "Saves the mod and writes patched copies into the workspace folder", delegate { DoApply(); }));
            modBar.Controls.Add(Btn("Revert", "Removes the patched copies from the workspace folder", delegate { DoRevert(); }));

            var texts = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
            texts.Panel1.Controls.Add(pendingBox);
            texts.Panel1.Controls.Add(Caption("Pending changes (mod.xml)"));
            texts.Panel2.Controls.Add(outputBox);
            texts.Panel2.Controls.Add(Caption("Output"));
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 260 };
            bottom.Controls.Add(texts);
            bottom.Controls.Add(modBar);

            grid.VirtualMode = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToOrderColumns = false;
            grid.RowHeadersVisible = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            grid.BackgroundColor = SystemColors.AppWorkspace;
            grid.DefaultCellStyle.BackColor = SystemColors.Window;
            grid.DefaultCellStyle.ForeColor = SystemColors.WindowText;
            grid.CellValueNeeded += OnCellValueNeeded;
            grid.CellValuePushed += OnCellValuePushed;
            grid.CellFormatting += OnCellFormatting;

            // docking goes from the last control added to the first: the bars take the full width, the grid what is left
            Controls.Add(grid);
            Controls.Add(rowBar);
            Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
            Controls.Add(entryList);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(bottom);
            Controls.Add(top);

            archiveBox.SelectedIndexChanged += delegate { if (!filling) FillEntries(); };
            filterBox.TextChanged += delegate { FillEntries(); };
            filterBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                if (entryList.Items.Count > 0 && entryList.SelectedIndex < 0) entryList.SelectedIndex = 0;
                OpenSelected();
            };
            parentBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                OpenSelected();
            };
            entryList.DoubleClick += delegate { OpenSelected(); };
            entryList.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.Handled = true; OpenSelected(); } };
        }

        public override void Initialize()
        {
            base.Initialize();
            Reload();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && session != null) { session.Dispose(); session = null; }
            base.Dispose(disposing);
        }

        // ---- small builders ----

        static FlowLayoutPanel Bar(DockStyle dock)
        {
            return new FlowLayoutPanel { Dock = dock, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(2) };
        }

        static Label Lbl(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(6, 8, 0, 0) }; }

        static Label Caption(string text) { return new Label { Text = text, Dock = DockStyle.Top, Height = 16 }; }

        readonly ToolTip tips = new ToolTip();

        Button Btn(string text, string tip, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, UseVisualStyleBackColor = true, ForeColor = SystemColors.ControlText };
            b.Click += delegate(object sender, EventArgs e) { Guard(delegate { click(sender, e); }); };
            tips.SetToolTip(b, tip);
            return b;
        }

        // ---- output ----

        void Say(string text)
        {
            outputBox.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        /// <summary>Runs one command of the view; what goes wrong is shown in the output pane and logged.</summary>
        bool Guard(MethodInvoker body)
        {
            Cursor old = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                body();
                return true;
            }
            catch (ModKitException ex)
            {
                Say(ex.Message);
                Logger.Warning("MS2 Tables: " + ex.Message);
            }
            catch (IOException ex)
            {
                Say(ex.Message);
                Logger.Warning("MS2 Tables: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                Say(ex.Message);
                Logger.Warning("MS2 Tables: " + ex.Message);
            }
            finally
            {
                Cursor.Current = old;
            }
            return false;
        }

        EditorSession Session()
        {
            if (session == null) throw new ModKitException("no data folder is set. Set the folders under Toolbench > Settings > MS2 Mods, then press Reload settings.");
            return session;
        }

        // ---- settings, archives, entries ----

        void Reload()
        {
            Guard(delegate
            {
                Mod keep = session != null ? session.Mod : null;
                if (session != null) { session.Dispose(); session = null; }
                ShowTable(null);
                archiveBox.Items.Clear();
                entryList.Items.Clear();
                TablesSettings s = TablesSettings.Load();
                session = new EditorSession(s.ClientData, s.ServerData, s.ModsDir, s.Workspace);
                if (keep != null && keep.Ops.Count > 0) session.Adopt(keep);
                filling = true;
                foreach (string a in session.Archives()) archiveBox.Items.Add(a);
                filling = false;
                FillMods();
                ShowMod();
                Say("client data: " + Shown(s.ClientData) + "\nserver data: " + Shown(s.ServerData) + "\nmods folder: " + Shown(s.ModsDir)
                    + "\nworkspace:   " + Shown(s.Workspace) + "\n" + archiveBox.Items.Count + " archive(s)");
                if (archiveBox.Items.Count > 0) archiveBox.SelectedIndex = PreferredArchive();
            });
        }

        static string Shown(string folder) { return string.IsNullOrEmpty(folder) ? "(not set)" : folder; }

        int PreferredArchive()
        {
            for (int i = 0; i < archiveBox.Items.Count; i++)
                if (((string)archiveBox.Items[i]).EndsWith(":Xml.m2d", StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        void FillEntries()
        {
            if (session == null || archiveBox.SelectedItem == null) return;
            Guard(delegate
            {
                List<string> names = session.Entries((string)archiveBox.SelectedItem, filterBox.Text, MaxEntries);
                entryList.BeginUpdate();
                entryList.Items.Clear();
                foreach (string n in names) entryList.Items.Add(n);
                entryList.EndUpdate();
            });
        }

        void FillMods()
        {
            modsBox.Items.Clear();
            if (session == null || session.ModsDir == null || !Directory.Exists(session.ModsDir)) return;
            string[] dirs = Directory.GetDirectories(session.ModsDir);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (string d in dirs)
                if (File.Exists(Path.Combine(d, "mod.xml"))) modsBox.Items.Add(Path.GetFileName(d));
        }

        // ---- the table ----

        void OpenSelected()
        {
            Guard(delegate
            {
                if (archiveBox.SelectedItem == null) { Session(); throw new ModKitException("no archive in the data folders"); }
                if (entryList.SelectedItem == null) throw new ModKitException("select an entry in the list first");
                ShowTable(Session().OpenTable((string)archiveBox.SelectedItem, (string)entryList.SelectedItem, parentBox.Text.Trim()));
                var sb = new System.Text.StringBuilder();
                sb.Append(draft.Archive).Append(' ').Append(draft.Entry).Append(": ").Append(draft.Rows.Count).Append(" row(s) under <")
                  .Append(draft.Parent.Name).Append(">, ").Append(draft.Columns.Count).Append(" attribute(s)");
                if (draft.OtherOps > 0) sb.Append("\n").Append(draft.OtherOps).Append(" operation(s) of the mod on this entry have no row here; they are kept as they are");
                foreach (string p in draft.Problems) sb.Append("\nproblem: ").Append(p);
                Say(sb.ToString());
            });
        }

        void ReopenTable()
        {
            if (draft == null) return;
            TableDraft old = draft;
            ShowTable(null);
            ShowTable(Session().OpenTable(old.Archive, old.Entry, old.ParentSelector));
        }

        void ShowTable(TableDraft d)
        {
            grid.CancelEdit();
            grid.RowCount = 0;
            grid.Columns.Clear();
            columns.Clear();
            draft = d;
            if (d == null) { tableLabel.Text = ""; return; }
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "row", ReadOnly = true, Width = 190, Frozen = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            foreach (string c in d.Columns) AddGridColumn(c);
            grid.RowCount = d.Rows.Count;
            tableLabel.Text = d.Archive + "  " + d.Entry + (d.ParentSelector.Length > 0 ? "  " + d.ParentSelector : "");
        }

        void AddGridColumn(string name)
        {
            columns.Add(name);
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, Width = 90, SortMode = DataGridViewColumnSortMode.NotSortable });
        }

        void SyncColumns()
        {
            foreach (string c in draft.Columns) if (!columns.Contains(c)) AddGridColumn(c);
        }

        TableRow RowAt(int index) { return draft != null && index >= 0 && index < draft.Rows.Count ? draft.Rows[index] : null; }

        TableRow CurrentRow()
        {
            if (draft == null) throw new ModKitException("open a table first");
            TableRow r = grid.CurrentCell != null ? RowAt(grid.CurrentCell.RowIndex) : null;
            if (r == null) throw new ModKitException("select a row first");
            return r;
        }

        void OnCellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            TableRow r = RowAt(e.RowIndex);
            if (r == null) return;
            if (e.ColumnIndex == 0) e.Value = (r.IsDeleted ? "(deleted) " : r.IsChanged ? "* " : "") + r.Label;
            else if (e.ColumnIndex <= columns.Count) e.Value = draft.Get(r, columns[e.ColumnIndex - 1]);
        }

        void OnCellValuePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            TableRow r = RowAt(e.RowIndex);
            if (r == null || e.ColumnIndex < 1 || e.ColumnIndex > columns.Count) return;
            string column = columns[e.ColumnIndex - 1];
            string value = e.Value == null ? null : e.Value.ToString();
            // an emptied cell is an empty value; a cell of a missing attribute that stays empty stays missing
            if (value == null && draft.Get(r, column) != null) value = "";
            if (value == draft.Get(r, column)) return;
            Guard(delegate { draft.Set(r, column, value); });
            Edited(e.RowIndex);
        }

        void OnCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            TableRow r = RowAt(e.RowIndex);
            if (r == null) return;
            if (r.IsDeleted)
            {
                e.CellStyle.ForeColor = Color.Gray;
                e.CellStyle.Font = new Font(grid.Font, FontStyle.Strikeout);
            }
            else if (r.IsAdded) e.CellStyle.BackColor = Color.FromArgb(214, 240, 214);
            if (!r.IsDeleted && e.ColumnIndex >= 1 && e.ColumnIndex <= columns.Count && draft.IsEdited(r, columns[e.ColumnIndex - 1]))
                e.CellStyle.BackColor = Color.FromArgb(255, 236, 170);
        }

        /// <summary>After any edit: the pending pane and the row on screen follow the mod.</summary>
        void Edited(int rowIndex)
        {
            SyncColumns();
            if (rowIndex >= 0 && rowIndex < grid.RowCount) grid.InvalidateRow(rowIndex); else grid.Invalidate();
            ShowMod();
        }

        void ShowMod()
        {
            if (session == null) { pendingBox.Text = ""; return; }
            modIdBox.Text = session.Mod.Id;
            pendingBox.Text = session.Mod.Ops.Count == 0
                ? "(mod " + session.Mod.Id + " holds no change yet)"
                : session.Mod.ToXml().Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        void CloneRow()
        {
            TableRow r = CurrentRow();
            grid.EndEdit();
            draft.Clone(r);
            grid.RowCount = draft.Rows.Count;
            Edited(-1);
            int col = grid.CurrentCell != null ? grid.CurrentCell.ColumnIndex : 0;
            grid.CurrentCell = grid.Rows[grid.RowCount - 1].Cells[col];
        }

        void DeleteRow()
        {
            TableRow r = CurrentRow();
            grid.CancelEdit();
            if (r.IsDeleted) draft.Restore(r); else draft.Delete(r);
            grid.RowCount = draft.Rows.Count;
            Edited(-1);
        }

        void RemoveAttribute()
        {
            TableRow r = CurrentRow();
            int c = grid.CurrentCell.ColumnIndex;
            if (c < 1 || c > columns.Count) throw new ModKitException("select a cell of an attribute column first");
            grid.CancelEdit();
            draft.Set(r, columns[c - 1], null);
            Edited(grid.CurrentCell.RowIndex);
        }

        void AddColumn()
        {
            if (draft == null) throw new ModKitException("open a table first");
            string name = attrBox.Text.Trim();
            if (name.Length == 0) throw new ModKitException("type the attribute's name into the box left of Add column");
            if (!TableDraft.IsAttrName(name)) throw new ModKitException("\"" + name + "\" is not an XML attribute name");
            if (columns.Contains(name)) throw new ModKitException("there is a column " + name + " already");
            AddGridColumn(name);
            attrBox.Text = "";
        }

        void CopyToOtherSide()
        {
            if (draft == null) throw new ModKitException("open a table first");
            grid.EndEdit();
            string other = Session().OtherSide(draft.Archive, draft.Entry);
            if (other == null) throw new ModKitException("no other data root holds " + draft.Entry + " in an archive of this name");
            int n = draft.CopyEditsTo(other);
            ShowMod();
            Say(n + " operation(s) on " + draft.Entry + " written for " + other + " too");
        }

        // ---- the mod ----

        void NewMod()
        {
            EditorSession s = Session();
            grid.CancelEdit();
            if (s.Mod.Ops.Count > 0 && MessageBox.Show(this, "Drop the pending changes of mod " + s.Mod.Id + "?", "MS2 Tables",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            s.NewMod(modIdBox.Text.Trim());
            ReopenTable();
            ShowMod();
            Say("new mod " + s.Mod.Id);
        }

        void SaveMod()
        {
            grid.EndEdit();
            string dir = Session().SaveMod();
            FillMods();
            Say("saved " + Path.Combine(dir, "mod.xml"));
        }

        void LoadMod()
        {
            EditorSession s = Session();
            string name = modsBox.Text.Trim();
            if (name.Length == 0) throw new ModKitException("pick a mod of the mods folder in the box left of Load mod, or type the path of a mod folder");
            grid.CancelEdit();
            s.LoadMod(name);
            ReopenTable();
            ShowMod();
            Say("loaded mod " + s.Mod.Id + " from " + s.Mod.Dir + ": " + s.Mod.Ops.Count + " operation block(s)");
        }

        // ---- plan, apply, revert (also behind the MS2 Mods menu) ----

        public bool DoPlan()
        {
            return Guard(delegate
            {
                grid.EndEdit();
                Plan p = Session().Plan();
                Say(EditorSession.Report(p));
                Logger.Info("MS2 Tables: plan: " + p.Changes.Count + " change(s), " + p.Errors.Count + " error(s), " + p.Conflicts.Count + " conflict(s)");
            });
        }

        public bool DoApply()
        {
            bool applied = false;
            Guard(delegate
            {
                grid.EndEdit();
                ApplyResult r = Session().Apply();
                FillMods();
                Say(EditorSession.Report(r));
                applied = r.Applied;
                if (r.Applied) Logger.Info("MS2 Tables: applied " + r.Plan.Mods.Count + " mod(s) to " + session.WorkspaceDir);
                else Logger.Warning("MS2 Tables: nothing applied: " + r.Reason);
            });
            return applied;
        }

        public bool DoRevert()
        {
            return Guard(delegate
            {
                ApplyResult r = Session().Revert();
                Say(EditorSession.Report(r));
                Logger.Info("MS2 Tables: removed " + r.Removed.Count + " file(s) from " + session.WorkspaceDir);
            });
        }
    }
}
