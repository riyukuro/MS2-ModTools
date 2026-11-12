// The "MS2 Spawns" view: what spawns at the spawn points of a map (table/mapspawntag.xml), in the style of the MS2
// Dungeons view. A find box (map id, map name or tag), the list of maps with rows, the rows of the chosen map as a
// grid (one line per spawn row: point, tag, population, cool time, difficulty, pets), the checks of the map's rows,
// Add / Clone / Remove, and the Plan / Apply / Revert buttons and output box of the MS2 Tables view.
//
// An edit sets the field on every copy of the table that has the row (see MS2.ModKit.SpawnEditor). Every edit is an
// op of the mod the MS2 Tables view is editing. The tables are read when the view is first shown, not at startup.
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
    public class SpawnsView : AbstractView
    {
        public const string ViewId = "MS2.Tables.SpawnsView";
        const int RowHeight = 34;
        static readonly string[] Cols = SpawnRules.Known;

        TablesView host;
        SpawnEditor editor;
        long map = -1;
        List<SpawnRow> rows = new List<SpawnRow>();
        bool filling, loaded;
        SpawnTags tags;      // read by "Read NPC tags", kept while the data set stays
        DataSet tagsOf;

        readonly TextBox searchBox = new TextBox { Width = 180 };
        readonly Label hitsLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListBox hitList = new ListBox { Dock = DockStyle.Left, Width = 290, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = RowHeight };
        readonly Label mapLabel = new Label { AutoSize = true, Margin = new Padding(24, 8, 8, 0) };
        readonly TextBox pointBox = new TextBox { Width = 60 };
        readonly TextBox mapBox = new TextBox { Width = 80 };
        readonly DataGridView grid = new DataGridView { Dock = DockStyle.Fill };
        readonly ListBox checkList = new ListBox { Dock = DockStyle.Bottom, Height = 80, IntegralHeight = false, HorizontalScrollbar = true };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 130 };
        readonly ComboBox targetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();
        readonly BoldFont bold = new BoldFont();

        public SpawnsView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(Lbl("find"));
            top.Controls.Add(searchBox);
            tips.SetToolTip(searchBox, "A part of the map id, of the map's name or of a tag; empty lists every map with spawn rows");
            top.Controls.Add(Btn("Find", "Lists the maps whose id, name or spawn tags hold the text", delegate { Find(); }));
            top.Controls.Add(hitsLabel);
            top.Controls.Add(mapLabel);

            FlowLayoutPanel applyBar = Bar();
            applyBar.Controls.Add(Lbl("Apply to"));
            targetBox.Items.Add("workspace (patched archives)");
            targetBox.Items.Add("game folder (loose files)");
            tips.SetToolTip(targetBox, "Where Apply writes the client's tables (the same choice as in the MS2 Tables view). The server's archives and the global client's always go to the workspace.");
            applyBar.Controls.Add(targetBox);
            applyBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change, with the spawn checks for each target; writes nothing", delegate { return host.DoPlan(); }));
            applyBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change, to every target at once", delegate { return host.DoApply(); }));
            applyBar.Controls.Add(Job("Revert", "Removes what Apply wrote, on every target", delegate { return host.DoRevert(); }));

            FlowLayoutPanel rowBar = Bar();
            rowBar.Controls.Add(Btn("Add", "Adds a row for the spawn point in the box to this map (or to the map in the map box), on every copy of the table: one monster of difficulty 1, no tag yet", delegate { Add(); }));
            rowBar.Controls.Add(Btn("Clone", "Adds a copy of the selected row for the spawn point in the box", delegate { Clone(); }));
            rowBar.Controls.Add(Lbl("point"));
            rowBar.Controls.Add(pointBox);
            tips.SetToolTip(pointBox, "The number of a spawn point of the map (SpawnPointID of the point in the map file; the MS2 Maps view lists them)");
            rowBar.Controls.Add(Lbl("map"));
            rowBar.Controls.Add(mapBox);
            tips.SetToolTip(mapBox, "For Add: the id of a map that has no spawn rows yet; empty = the map shown");
            rowBar.Controls.Add(Btn("Remove / restore", "Removes the selected row on every copy (a new one goes away), or brings a removed shipped row back", delegate { RemoveOrRestore(); }));
            rowBar.Controls.Add(Btn("Undo changes", "Drops every change the mod makes to the selected shipped row", delegate { Undo(); }));
            rowBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same mod and Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));
            rowBar.Controls.Add(Job("Read NPC tags", "Reads the tags of every NPC once: the rows then show the NPCs of their tags, and the checks name a tag no NPC has", delegate { return ReadTags(); }));

            SetupGrid(grid);
            foreach (string c in Cols) grid.Columns.Add(Col(c == SpawnRules.Point ? "point" : c == SpawnRules.PetProbability ? "petProbability" : c, c == SpawnRules.Tag ? 170 : 70, Array.IndexOf(SpawnRules.Fixed, c) >= 0));
            grid.Columns.Add(Col("copies", 110, true));
            grid.Columns.Add(Col("spawns", 260, true));
            grid.CellValueNeeded += OnValueNeeded;
            grid.CellValuePushed += OnValuePushed;
            grid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e)
            {
                if (editor == null || e.RowIndex < 0 || e.RowIndex >= rows.Count) return;
                SpawnRow r = rows[e.RowIndex];
                if (r.IsDeleted || e.ColumnIndex >= Cols.Length) e.CellStyle.ForeColor = SystemColors.GrayText;
                else if (Array.IndexOf(SpawnRules.Fixed, Cols[e.ColumnIndex]) >= 0) e.CellStyle.BackColor = SystemColors.Control;
                else if (editor.IsEdited(r, Cols[e.ColumnIndex])) e.CellStyle.Font = bold.Of(grid.Font);
            };
            grid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e)
            {
                if (editor == null || e.RowIndex >= rows.Count || rows[e.RowIndex].IsDeleted || e.ColumnIndex >= Cols.Length
                    || Array.IndexOf(SpawnRules.Fixed, Cols[e.ColumnIndex]) >= 0) e.Cancel = true;
            };
            tips.SetToolTip(checkList, "The checks of the rows of this map that the mod adds or changes; Plan checks every row the mods add or change, per target");

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(grid);
            right.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            right.Controls.Add(checkList);
            right.Controls.Add(rowBar);

            Controls.Add(right);
            Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
            Controls.Add(hitList);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(outputBox);
            Controls.Add(applyBar);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, applyBar, hitList, right });
            // the output box keeps two lines; the checks get 14 % of the height; in a low window the grid keeps six rows
            ViewLayout.KeepLogLines(outputBox, this);
            ViewLayout.KeepHeightShare(checkList, right, 0.14, 3 * checkList.ItemHeight + 4,
                delegate { return rowBar.Height + 8 + grid.ColumnHeadersHeight + 6 * grid.RowTemplate.Height + 4; });
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

        // the two tables are big (thousands of rows each): they are read when the view is first looked at
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
            long keep = map;
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
            if (h == null) { map = -1; rows.Clear(); return; }
            ShowMap(-1);
            hitList.Items.Clear();
            h.Changed += OnHostChanged;
            h.Flushing += EndEdits;
            h.BusyChanged += OnHostBusy;
            h.Said += OnHostSaid;
            h.TargetChanged += OnHostTarget;
            OnHostTarget();
            OutputWrap.Set(outputBox, h.OutputText);
            OnHostBusy(h.Busy);
            if (!loaded) return;
            try { FillHits(keep); }
            catch (ModKitException) { ShowMap(-1); }
            catch (System.IO.IOException) { ShowMap(-1); }
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
                Logger.Warning("MS2 Spawns: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        SpawnEditor Editor()
        {
            EditorSession s = host.SharedSession();
            if (editor == null || editor.Mod != s.Mod || editor.Data != s.Data)
            {
                editor = new SpawnEditor(s.Data, s.Mod);
                if (tagsOf == s.Data) editor.Tags = tags;
            }
            return editor;
        }

        static string Title(string archive)
        {
            Profile p = Profile.OfArchive(archive);
            return p != null ? p.Title : archive;
        }

        void OnHostSaid(string text) { OutputWrap.Set(outputBox, text); }

        void OnHostBusy(bool on) { ViewLayout.Lock(this, lockable, on, outputBox); }

        // the mod was replaced or edited in another view: read the tables again (when this view is looked at) and show the same map
        void OnHostChanged()
        {
            CancelEdits();
            long keep = map;
            editor = null;
            ShowMap(-1);
            if (!Visible) { hitList.Items.Clear(); loaded = false; map = keep; return; }
            try { FillHits(keep); }
            catch (ModKitException) { ShowMap(-1); }
            catch (System.IO.IOException) { ShowMap(-1); }
        }

        bool ReadTags()
        {
            SpawnTags read = null;
            DataSet data = host.SharedSession().Data;
            return host.RunOutside("NPC tags", delegate { read = SpawnTags.Scan(data, null); }, delegate
            {
                if (read == null) return;
                tags = read;
                tagsOf = data;
                if (editor != null && editor.Data == data) editor.Tags = read;
                ShowMap(map);
                host.SayOutside(read.Archive == null ? "the data set has no NPC data to read tags from"
                    : read.Npcs.Count + " tags of " + read.NpcCount + " NPCs read from " + read.Archive + "; the rows show the NPCs of their tags");
            });
        }

        // ---- the list ----

        void Find()
        {
            EndEdits();
            loaded = true;
            FillHits(map);
            if (hitList.Items.Count > 0 && hitList.SelectedIndex < 0) hitList.SelectedIndex = 0;
            else if (hitList.Items.Count == 0) ShowMap(-1);
        }

        void FillHits(long keep)
        {
            SpawnEditor ed = Editor();
            List<long> hits = ed.Find(searchBox.Text);
            // the maps the mod changes first, then by id
            hits.Sort(delegate(long a, long b)
            {
                bool ca = ed.IsChanged(a), cb = ed.IsChanged(b);
                return ca != cb ? (ca ? -1 : 1) : a.CompareTo(b);
            });
            filling = true;
            long show = -1;
            try
            {
                hitList.BeginUpdate();
                hitList.Items.Clear();
                foreach (long m in hits)
                {
                    hitList.Items.Add(m);
                    if (m == keep) { hitList.SelectedIndex = hitList.Items.Count - 1; show = m; }
                }
                hitList.EndUpdate();
            }
            finally { filling = false; }
            hitsLabel.Text = hits.Count + " map(s), " + ed.Maps().Count + " in all";
            if (show >= 0) ShowMap(show);
        }

        void OpenSelected()
        {
            if (!(hitList.SelectedItem is long)) return;
            EndEdits();
            ShowMap((long)hitList.SelectedItem);
            host.SayOutside(Describe());
        }

        void OnDrawHit(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= hitList.Items.Count || editor == null || host == null || host.IsDisposed || host.Busy) return;
            if (!(hitList.Items[e.Index] is long)) return;
            long m = (long)hitList.Items[e.Index];
            string name = editor.MapName(m);
            int n = editor.CountOf(m);
            bool marked = editor.IsChanged(m);
            string line1 = m == SpawnRules.NoMap ? "rows without a map id" : m.ToString(CultureInfo.InvariantCulture) + "  " + (name.Length > 0 ? name : "(no name)");
            string line2 = n + (n == 1 ? " row" : " rows") + (marked ? "  (changed)" : "");
            int half = e.Bounds.Height / 2;
            var t1 = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 1, e.Bounds.Width - 8, half);
            var t2 = new Rectangle(t1.X, e.Bounds.Y + half - 1, t1.Width, half);
            TextRenderer.DrawText(e.Graphics, line1, marked ? bold.Of(e.Font) : e.Font, t1, e.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, line2, e.Font, t2, (e.State & DrawItemState.Selected) == 0 ? SystemColors.GrayText : e.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        }

        // ---- the map's rows ----

        SpawnRow Current()
        {
            int i = grid.CurrentCell == null ? -1 : grid.CurrentCell.RowIndex;
            return i >= 0 && i < rows.Count ? rows[i] : null;
        }

        void ShowMap(long m)
        {
            CancelEdits();
            map = m;
            filling = true;
            try
            {
                grid.RowCount = 0;
                checkList.Items.Clear();
                rows = new List<SpawnRow>();
                if (m < 0 || host == null) { mapLabel.Text = "no map chosen"; return; }
                SpawnEditor ed = Editor();
                string name = ed.MapName(m);
                rows = ed.Of(m);
                mapLabel.Text = (m == SpawnRules.NoMap ? "Rows whose mapCode is not a map id" : "Map " + m + (name.Length > 0 ? " (" + name + ")" : "")) + ": " + rows.Count + (rows.Count == 1 ? " spawn row" : " spawn rows")
                    + (ed.IsChanged(m) ? ", changed by the mod" : "");
                grid.RowCount = rows.Count;
                // shipped rows are left alone (many shipped tags are chests and events, not NPCs): the rows the mod adds or changes are checked
                foreach (SpawnRow r in rows)
                    if (r.IsAdded || r.IsChanged)
                        foreach (string t in ed.Check(r)) checkList.Items.Add("point " + ed.Get(r, SpawnRules.Point) + ": " + t);
                if (checkList.Items.Count == 0) checkList.Items.Add(ed.Tags == null ? "no problem found in the rows the mod adds or changes (Read NPC tags also checks their tags)" : "no problem found in the rows the mod adds or changes");
            }
            finally { filling = false; }
            grid.Invalidate();
        }

        string Describe()
        {
            SpawnEditor ed = Editor();
            var sb = new StringBuilder();
            sb.Append("map ").Append(map).Append(": ").Append(rows.Count).Append(" spawn row(s) in ").Append(SpawnRules.Entry).Append(" of ");
            for (int i = 0; i < ed.Sides.Count; i++) sb.Append(i > 0 ? ", " : "").Append(ed.Sides[i].Archive);
            sb.Append("\r\nA row says what appears at one spawn point of the map: monsters whose NPC data carries the tag, population at a time, back after coolTime seconds.");
            return sb.ToString();
        }

        void Refresh(string said)
        {
            host.EditedOutside();
            hitList.Invalidate();
            int at = grid.CurrentCell == null ? -1 : grid.CurrentCell.RowIndex, col = grid.CurrentCell == null ? 0 : grid.CurrentCell.ColumnIndex;
            ShowMap(map);
            if (at >= 0 && at < grid.RowCount) grid.CurrentCell = grid[col, at];
            if (said != null) host.SayOutside(said);
        }

        string Copies(SpawnRow r)
        {
            if (r.IsDeleted) return "removed";
            var sb = new StringBuilder();
            List<string> missing = editor.MissingOn(r);
            if (r.IsAdded) sb.Append("new");
            else if (missing.Count == 0) sb.Append("all");
            else
                for (int i = 0; i < editor.Sides.Count; i++)
                    if (r.Rows[i] != null) sb.Append(sb.Length > 0 ? ", " : "").Append(Title(editor.Sides[i].Archive)).Append(" only");
            foreach (string f in Cols)
                if (editor.Differs(r, f))
                    for (int i = 1; i < editor.Sides.Count; i++)
                        if (r.Rows[i] != null && editor.Get(r, i, f) != editor.Get(r, f))
                            sb.Append("; ≠ ").Append(Title(editor.Sides[i].Archive)).Append(' ').Append(f).Append(' ').Append(editor.Get(r, i, f));
            return sb.ToString();
        }

        string Spawns(SpawnRow r)
        {
            List<string> tags = SpawnRules.Tags(editor.Get(r, SpawnRules.Tag));
            if (tags.Count == 0) return "nothing";
            if (editor.Tags == null || editor.Tags.Archive == null) return "";
            var sb = new StringBuilder();
            foreach (string t in tags)
            {
                if (sb.Length > 0) sb.Append("; ");
                if (tags.Count > 1) sb.Append(t).Append(": ");
                sb.Append(editor.Tags.Describe(t, 3));
            }
            return sb.ToString();
        }

        void OnValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (editor == null || host == null || host.Busy || e.RowIndex < 0 || e.RowIndex >= rows.Count) return;
            SpawnRow r = rows[e.RowIndex];
            if (e.ColumnIndex < Cols.Length) e.Value = editor.Get(r, Cols[e.ColumnIndex]);
            else if (e.ColumnIndex == Cols.Length) e.Value = Copies(r);
            else e.Value = Spawns(r);
        }

        void OnValuePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.ColumnIndex < 0 || e.ColumnIndex >= Cols.Length || e.RowIndex < 0 || e.RowIndex >= rows.Count) return;
            SpawnRow r = rows[e.RowIndex];
            string f = Cols[e.ColumnIndex];
            string v = ((e.Value as string) ?? "").Trim();
            Guard(delegate
            {
                SpawnEditor ed = Editor();
                if (v == ed.Get(r, f) && !ed.Differs(r, f)) return;
                ed.Set(r, f, v);
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

        void Added(long m, SpawnRow made, string said)
        {
            host.EditedOutside();
            pointBox.Text = "";
            mapBox.Text = "";
            if (m != map || hitList.SelectedIndex < 0) { searchBox.Text = ""; FillHits(m); }
            ShowMap(m);
            hitList.Invalidate();
            int at = rows.IndexOf(made);
            if (at >= 0) grid.CurrentCell = grid[1, at];
            host.SayOutside(said);
        }

        void Add()
        {
            EndEdits();
            long m = mapBox.Text.Trim().Length > 0 ? Number(mapBox.Text, "a map id") : map;
            if (m < 0) throw new ModKitException("choose a map first, or give a map id in the map box");
            long point = Number(pointBox.Text, "the number of a spawn point");
            SpawnRow made = Editor().Add(m, point, "", 1, 0, 1, 1);
            Added(m, made, "row for point " + point + " of map " + m + " added: give it a tag (what spawns) and a population. Save mod, then Plan checks it per target.");
        }

        void Clone()
        {
            SpawnRow r = Current();
            if (r == null) throw new ModKitException("select the row to copy first");
            EndEdits();
            long point = Number(pointBox.Text, "the number of a spawn point");
            SpawnRow made = Editor().Clone(r, point);
            Added(map, made, "row for point " + point + " added as a copy of the row of point " + Editor().Get(r, SpawnRules.Point) + ". Save mod, then Plan checks it per target.");
        }

        void Undo()
        {
            SpawnRow r = Current();
            if (r == null) throw new ModKitException("select a row first");
            EndEdits();
            Editor().Revert(r);
            Refresh("the row of point " + Editor().Get(r, SpawnRules.Point) + " is as shipped again");
        }

        void RemoveOrRestore()
        {
            SpawnRow r = Current();
            if (r == null) throw new ModKitException("select a row first");
            EndEdits();
            SpawnEditor ed = Editor();
            string point = ed.Get(r, SpawnRules.Point);
            if (r.IsDeleted) { ed.Restore(r); Refresh("the row of point " + point + " is back"); return; }
            bool added = r.IsAdded;
            ed.Remove(r);
            Refresh(added ? "the row of point " + point + " was new in the mod and is gone again" : "the row of point " + point + " removed on every copy (Remove / restore brings it back)");
        }
    }
}
