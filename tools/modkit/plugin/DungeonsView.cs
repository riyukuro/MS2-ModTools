// The "MS2 Dungeons" view: the dungeon rooms (table/dungeonroom.xml), in the style of the MS2 Item sets view. A find
// box (dungeon id, name or map id), the list of hits, the room's name, description and fields as one grid with a
// value column per side (the Korean client and the server keep some fields differently and ship different values),
// the checks of the room, Add / Clone / Remove, and the Plan / Apply / Revert buttons and output box of the MS2
// Tables view.
//
// An edit in the "every side" column sets the field on every side in that side's notation; an edit in a side's
// column sets that side only (see MS2.ModKit.DungeonEditor). Every edit is an op of the mod the MS2 Tables view is
// editing.
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
    public class DungeonsView : AbstractView
    {
        public const string ViewId = "MS2.Tables.DungeonsView";
        const int RowHeight = 34;
        static readonly string[] Texts = { "name", "uiDescription" };

        TablesView host;
        DungeonEditor editor;
        DungeonRoom room;
        List<string> fields = new List<string>();
        readonly List<DungeonRoom> hits = new List<DungeonRoom>();
        bool filling;

        readonly TextBox searchBox = new TextBox { Width = 180 };
        readonly Label hitsLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListBox hitList = new ListBox { Dock = DockStyle.Left, Width = 290, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = RowHeight };
        readonly Label roomLabel = new Label { AutoSize = true, Margin = new Padding(24, 8, 8, 0) };
        readonly TextBox newIdBox = new TextBox { Width = 80 };
        readonly CheckBox setOnly = new CheckBox { Text = "set fields only", AutoSize = true, Margin = new Padding(12, 6, 3, 0) };
        readonly DataGridView grid = new DataGridView { Dock = DockStyle.Fill };
        readonly ListBox checkList = new ListBox { Dock = DockStyle.Bottom, Height = 80, IntegralHeight = false, HorizontalScrollbar = true };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 130 };
        readonly ComboBox targetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();
        readonly BoldFont bold = new BoldFont();

        public DungeonsView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(Lbl("find"));
            top.Controls.Add(searchBox);
            tips.SetToolTip(searchBox, "A part of the dungeon id or of its name, or the start of a map id; empty lists every dungeon");
            top.Controls.Add(Btn("Find", "Lists the dungeons whose id, name or map ids hold the text", delegate { Find(); }));
            top.Controls.Add(hitsLabel);
            top.Controls.Add(roomLabel);

            FlowLayoutPanel applyBar = Bar();
            applyBar.Controls.Add(Lbl("Apply to"));
            targetBox.Items.Add("workspace (patched archives)");
            targetBox.Items.Add("game folder (loose files)");
            tips.SetToolTip(targetBox, "Where Apply writes the client's tables (the same choice as in the MS2 Tables view). The server's archives and the global client's always go to the workspace.");
            applyBar.Controls.Add(targetBox);
            applyBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change, with the dungeon checks for each target; writes nothing", delegate { return host.DoPlan(); }));
            applyBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change, to every target at once", delegate { return host.DoApply(); }));
            applyBar.Controls.Add(Job("Revert", "Removes what Apply wrote, on every target", delegate { return host.DoRevert(); }));

            string range = DungeonRules.From + "-" + DungeonRules.To;
            FlowLayoutPanel roomBar = Bar();
            roomBar.Controls.Add(Btn("Add", "Adds a normal dungeon for 1 to 4 players without a map under the id in the box (empty = the next free id of " + range + "), on every side", delegate { Add(); }));
            roomBar.Controls.Add(Btn("Clone", "Adds a copy of this dungeon (each side its own row, name and description) under the id in the box (empty = the next free id of " + range + ")", delegate { Clone(); }));
            roomBar.Controls.Add(Lbl("new id"));
            roomBar.Controls.Add(newIdBox);
            roomBar.Controls.Add(Btn("Remove / restore", "Removes the dungeon on every side (a new one goes away), or brings a removed shipped dungeon back", delegate { RemoveOrRestore(); }));
            roomBar.Controls.Add(Btn("Undo changes", "Drops every change the mod makes to this shipped dungeon: its fields, name and description", delegate { Undo(); }));
            roomBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same mod and Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));
            roomBar.Controls.Add(setOnly);
            tips.SetToolTip(setOnly, "Lists only the fields a side of this dungeon has a value for");
            setOnly.CheckedChanged += delegate { if (!filling && room != null) Guard(delegate { EndEdits(); ShowRoom(room); }); };

            SetupGrid(grid);
            grid.CellValueNeeded += OnValueNeeded;
            grid.CellValuePushed += OnValuePushed;
            grid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e)
            {
                if (room == null || editor == null || e.RowIndex < 0 || e.RowIndex >= fields.Count) return;
                int side = e.ColumnIndex - 2;
                string f = fields[e.RowIndex];
                if (room.IsDeleted || e.ColumnIndex == grid.ColumnCount - 1) e.CellStyle.ForeColor = SystemColors.GrayText;
                else if (side >= 0 && side < editor.Sides.Count && !IsText(f) && (room.Rows[side] == null || !DungeonRules.OnSide(f, editor.Sides[side].Kms)))
                    e.CellStyle.BackColor = SystemColors.Control;
                else if (e.ColumnIndex == 0 && HasValue(f)) e.CellStyle.Font = bold.Of(grid.Font);
            };
            grid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e)
            {
                if (room == null || room.IsDeleted || editor == null || e.RowIndex >= fields.Count) { e.Cancel = true; return; }
                int side = e.ColumnIndex - 2;
                string f = fields[e.RowIndex];
                if (e.ColumnIndex == 0 || e.ColumnIndex == grid.ColumnCount - 1 || Array.IndexOf(DungeonRules.Fixed, f) >= 0) e.Cancel = true;
                else if (side >= 0 && (IsText(f) || room.Rows[side] == null || !DungeonRules.OnSide(f, editor.Sides[side].Kms))) e.Cancel = true;
            };
            tips.SetToolTip(checkList, "The checks of the dungeon as shown; Plan checks every dungeon the mods add or change, per target, with the maps and missions of each target");

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(grid);
            right.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            right.Controls.Add(checkList);
            right.Controls.Add(roomBar);

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
                delegate { return roomBar.Height + 8 + grid.ColumnHeadersHeight + 6 * grid.RowTemplate.Height + 4; });
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

        string Key(DungeonRoom d) { return d != null && editor != null ? editor.Get(d, DungeonRules.Key) : null; }

        void Hook(TablesView h)
        {
            if (h == host) return;
            // the room and the list rows belong to the old view's session: drop them, and show the same room again
            string key = Key(room);
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
            if (h == null) { room = null; hits.Clear(); return; }
            ShowRoom(null);
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
            catch (ModKitException) { ShowRoom(null); }
            catch (System.IO.IOException) { ShowRoom(null); }
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
                Logger.Warning("MS2 Dungeons: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        DungeonEditor Editor()
        {
            EditorSession s = host.SharedSession();
            if (editor == null || editor.Mod != s.Mod || editor.Data != s.Data)
            {
                editor = new DungeonEditor(s.Data, s.Mod);
                editor.Others = delegate { return s.OtherMods(); };
                // a value column per side, between "every side" and the note
                grid.RowCount = 0;
                grid.Columns.Clear();
                grid.Columns.Add(Col("field", 170, true));
                grid.Columns.Add(Col("every side", 150, false));
                foreach (DungeonSide side in editor.Sides) grid.Columns.Add(Col(Title(side), 150, false));
                grid.Columns.Add(Col("note", 220, true));
            }
            return editor;
        }

        static string Title(DungeonSide side)
        {
            Profile p = Profile.OfArchive(side.Archive);
            return p != null ? p.Title : side.Archive;
        }

        void OnHostSaid(string text) { OutputWrap.Set(outputBox, text); }

        void OnHostBusy(bool on) { ViewLayout.Lock(this, lockable, on, outputBox); }

        // the mod was replaced or edited in another view: read the tables again and show the same room
        void OnHostChanged()
        {
            CancelEdits();
            string key = Key(room);
            editor = null;
            ShowRoom(null);
            try { FillHits(key); }
            catch (ModKitException) { ShowRoom(null); }
            catch (System.IO.IOException) { ShowRoom(null); }
        }

        // ---- the list ----

        void Find()
        {
            EndEdits();
            FillHits(Key(room));
            if (hitList.Items.Count > 0 && hitList.SelectedIndex < 0) hitList.SelectedIndex = 0;
            else if (hitList.Items.Count == 0) ShowRoom(null);
        }

        void FillHits(string keep)
        {
            DungeonEditor ed = Editor();
            hits.Clear();
            hits.AddRange(ed.Find(searchBox.Text));
            // the mod's new dungeons first, then by id
            hits.Sort(delegate(DungeonRoom a, DungeonRoom b)
            {
                if (a.IsAdded != b.IsAdded) return a.IsAdded ? -1 : 1;
                return ed.Id(a).CompareTo(ed.Id(b));
            });
            filling = true;
            DungeonRoom show = null;
            try
            {
                hitList.BeginUpdate();
                hitList.Items.Clear();
                foreach (DungeonRoom d in hits)
                {
                    hitList.Items.Add(d);
                    if (show == null && keep != null && ed.Get(d, DungeonRules.Key) == keep) { hitList.SelectedIndex = hitList.Items.Count - 1; show = d; }
                }
                hitList.EndUpdate();
            }
            finally { filling = false; }
            hitsLabel.Text = hits.Count + " dungeon(s), " + ed.Rooms.Count + " in all";
            if (show != null) ShowRoom(show);
        }

        void OpenSelected()
        {
            DungeonRoom d = hitList.SelectedItem as DungeonRoom;
            if (d == null) return;
            EndEdits();
            ShowRoom(d);
            host.SayOutside(Describe(d));
        }

        static string State(DungeonEditor ed, DungeonRoom d, string removed, string added, string changed)
        {
            return d.IsDeleted ? removed : d.IsAdded ? added : ed.IsChanged(d) ? changed : "";
        }

        void OnDrawHit(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= hitList.Items.Count || editor == null || host == null || host.IsDisposed || host.Busy) return;
            var d = hitList.Items[e.Index] as DungeonRoom;
            if (d == null) return;
            string name = editor.Text(d, "name");
            int maps = editor.Maps(d).Count;
            string min = editor.Get(d, "minUserCount"), max = editor.Get(d, "maxUserCount");
            string users = max.Length == 0 ? "" : ", " + (min.Length == 0 || min == max ? "" : min + "-") + max + (max == "1" ? " player" : " players");
            string group = editor.Get(d, DungeonRules.Group);
            bool marked = d.IsAdded || editor.IsChanged(d);
            string line1 = editor.Get(d, DungeonRules.Key) + "  " + (name.Length > 0 ? name : "(no name)");
            string line2 = (group.Length > 0 ? group + ", " : "") + (maps == 0 ? "no map" : maps + " map" + (maps == 1 ? "" : "s")) + users
                + (editor.MissingOn(d).Count > 0 ? ", one side only" : "") + State(editor, d, "  (removed)", "  (new)", "  (changed)");
            Color fore = d.IsDeleted && (e.State & DrawItemState.Selected) == 0 ? SystemColors.GrayText : e.ForeColor;
            int half = e.Bounds.Height / 2;
            var t1 = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 1, e.Bounds.Width - 8, half);
            var t2 = new Rectangle(t1.X, e.Bounds.Y + half - 1, t1.Width, half);
            TextRenderer.DrawText(e.Graphics, line1, marked ? bold.Of(e.Font) : e.Font, t1, fore, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, line2, e.Font, t2, fore == e.ForeColor && (e.State & DrawItemState.Selected) == 0 ? SystemColors.GrayText : fore, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        }

        // ---- the room ----

        static bool IsText(string f) { return Array.IndexOf(Texts, f) >= 0; }

        bool HasValue(string f)
        {
            if (IsText(f)) return editor.Text(room, f).Length > 0;
            for (int i = 0; i < editor.Sides.Count; i++) if (editor.Get(room, i, f).Length > 0) return true;
            return false;
        }

        void ShowRoom(DungeonRoom d)
        {
            CancelEdits();
            room = d;
            filling = true;
            try
            {
                grid.RowCount = 0;
                checkList.Items.Clear();
                fields = new List<string>();
                if (d == null) { roomLabel.Text = "no dungeon chosen"; return; }
                DungeonEditor ed = Editor();
                string name = ed.Text(d, "name");
                roomLabel.Text = "Dungeon " + ed.Get(d, DungeonRules.Key) + (name.Length > 0 ? " (" + name + ")" : "")
                    + State(ed, d, ", removed by the mod", ", new in the mod", ", changed by the mod");
                fields.AddRange(Texts);
                foreach (string f in ed.Fields(d)) if (!setOnly.Checked || HasValue(f)) fields.Add(f);
                grid.RowCount = fields.Count;
                foreach (string t in ed.Check(d)) checkList.Items.Add(t);
                if (checkList.Items.Count == 0) checkList.Items.Add("no problem found");
            }
            finally { filling = false; }
            grid.Invalidate();
        }

        string Describe(DungeonRoom d)
        {
            DungeonEditor ed = Editor();
            var sb = new StringBuilder();
            sb.Append("dungeon ").Append(ed.Get(d, DungeonRules.Key)).Append(State(ed, d, " (removed by the mod)", " (new in the mod)", " (changed by the mod)")).Append(':');
            for (int i = 0; i < ed.Sides.Count; i++)
            {
                DungeonSide side = ed.Sides[i];
                sb.Append("\r\n").Append(side.Archive).Append(' ').Append(side.Entry).Append(": ");
                if (d.Rows[i] == null) sb.Append("not there");
                else if (d.Rows[i].IsDeleted) sb.Append("removed");
                else sb.Append("has it");
            }
            return sb.ToString();
        }

        void Refresh(string said)
        {
            host.EditedOutside();
            DungeonRoom d = room;
            hitList.Invalidate();
            ShowRoom(d);
            if (said != null) host.SayOutside(said + "\r\n\r\n" + Describe(d));
        }

        string Note(string f)
        {
            if (f == "name") return "in every language file (string/{lang}/" + DungeonRules.NameFile + ")";
            if (f == "uiDescription") return "the text of the dungeon window, in every language file";
            if (editor.Differs(room, f)) return "the sides differ; \"every side\" sets all";
            if (f == DungeonRules.Group) return string.Join(", ", DungeonRules.GroupTypes);
            if (f == DungeonRules.Fields) return "the maps of the dungeon, the first is entered";
            if (f == "lobbyFieldID") return "the map players wait in";
            if (f == DungeonRules.Duration) return "seconds; the server keeps it as durationTick";
            if (f == DungeonRules.LimitEnter) return "1 = entries are counted; the server's playType";
            if (f == DungeonRules.ClearKill) return "the npc whose death clears it; else a trigger does";
            if (f == "cooldownType") return "nextDay or dayOfWeeks; the server only";
            if (f == "timerType") return "clock or gauge";
            if (f == "userMissions" || f == "partyMissions") return "ids of table/dungeonmission.xml";
            if (f == "roundID") return "a group of table/dungeonrounddata.xml";
            if (Array.IndexOf(DungeonRules.Fixed, f) >= 0) return "not changed here";
            if (!DungeonRules.OnSide(f, true)) return "the server only";
            if (!DungeonRules.OnSide(f, false)) return "the Korean client only";
            return "";
        }

        void OnValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (room == null || editor == null || host == null || host.Busy || e.RowIndex < 0 || e.RowIndex >= fields.Count) return;
            string f = fields[e.RowIndex];
            int side = e.ColumnIndex - 2;
            if (e.ColumnIndex == 0) e.Value = (!IsText(f) && editor.Differs(room, f) ? "≠ " : "   ") + f;
            else if (e.ColumnIndex == grid.ColumnCount - 1) e.Value = Note(f);
            else if (IsText(f)) e.Value = e.ColumnIndex == 1 ? editor.Text(room, f) : "";
            else if (e.ColumnIndex == 1) e.Value = editor.Differs(room, f) ? "" : editor.Get(room, f);
            else if (side < editor.Sides.Count) e.Value = editor.Get(room, side, f);
        }

        void OnValuePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            if (room == null || e.ColumnIndex < 1 || e.ColumnIndex >= grid.ColumnCount - 1 || e.RowIndex < 0 || e.RowIndex >= fields.Count) return;
            string f = fields[e.RowIndex];
            string v = ((e.Value as string) ?? "").Trim();
            int side = e.ColumnIndex - 2;
            Guard(delegate
            {
                DungeonEditor ed = Editor();
                if (IsText(f))
                {
                    if (v == ed.Text(room, f)) return;
                    ed.SetText(room, f, v);
                }
                else if (side < 0)
                {
                    if (ed.Differs(room, f) ? v.Length == 0 : v == ed.Get(room, f)) return;
                    ed.Set(room, f, v);
                }
                else
                {
                    if (v == ed.Get(room, side, f)) return;
                    ed.Set(room, side, f, v);
                }
                Refresh(null);
            });
        }

        // ---- add, clone, remove ----

        long NewId()
        {
            string text = newIdBox.Text.Trim();
            long v;
            if (text.Length > 0)
            {
                if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out v)) throw new ModKitException("\"" + text + "\" is not an id");
                return v;   // Add and Clone refuse an id this or another enabled mod has (DungeonEditor.Others)
            }
            List<long> free = Editor().NextIds(host.SharedSession().OtherMods(), 1, null);
            if (free.Count == 0) throw new ModKitException("no free id is left in " + DungeonRules.From + "-" + DungeonRules.To);
            return free[0];
        }

        void Add()
        {
            EndEdits();
            DungeonRoom d = Editor().Add(NewId());
            Opened(d, "dungeon " + Editor().Get(d, DungeonRules.Key) + " added: give it a name and its maps (fieldIDs). Save mod, then Plan checks it per target.");
        }

        void Clone()
        {
            if (room == null) throw new ModKitException("choose the dungeon to copy first");
            EndEdits();
            string source = Editor().Get(room, DungeonRules.Key);
            DungeonRoom d = Editor().Clone(room, NewId());
            Opened(d, "dungeon " + Editor().Get(d, DungeonRules.Key) + " added as a copy of " + source + ". Save mod, then Plan checks it per target.");
        }

        void Opened(DungeonRoom d, string said)
        {
            host.EditedOutside();
            newIdBox.Text = "";
            searchBox.Text = "";
            FillHits(Editor().Get(d, DungeonRules.Key));
            ShowRoom(d);
            host.SayOutside(said + "\r\n\r\n" + Describe(d));
        }

        void Undo()
        {
            if (room == null) throw new ModKitException("choose a dungeon first");
            EndEdits();
            DungeonEditor ed = Editor();
            ed.Revert(room);
            Refresh("dungeon " + ed.Get(room, DungeonRules.Key) + " is as shipped again");
        }

        void RemoveOrRestore()
        {
            if (room == null) throw new ModKitException("choose a dungeon first");
            EndEdits();
            DungeonEditor ed = Editor();
            string id = ed.Get(room, DungeonRules.Key);
            if (room.IsDeleted)
            {
                ed.Restore(room);
                Refresh("dungeon " + id + " is back");
                return;
            }
            bool added = room.IsAdded;
            ed.Remove(room);
            if (added)
            {
                host.EditedOutside();
                ShowRoom(null);
                FillHits(null);
                host.SayOutside("dungeon " + id + " was new in the mod and is gone again; its id is free");
            }
            else Refresh("dungeon " + id + " removed on every side (Remove / restore brings it back)");
        }
    }
}
