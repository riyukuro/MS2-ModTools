// The "MS2 Levels" view: the experience tables by level (MS2.ModKit.LevelExp) as a mod edit.
//
// - nextexp (table/nextexp.xml): the experience from a level to the next one. The kms client, the global client and
//   the server's pack each have a copy; an edit goes to every copy, so the client's experience bar and the server's
//   level-up agree. The client reads the levels 1..99 only.
// - monster exp (table/Server/monsterExpTable.xml of the server's Server.m2d): the experience a monster of a level
//   gives, per kind (common, elite, boss, dungeonElite, dungeonBoss). Server only.
//
// The list shows each level with the mod's values (an edited level in blue, a level whose sides differ in orange),
// the curve below draws the chosen field with the mod (blue) over the shipped one (grey). Set writes one value,
// Scale multiplies the shipped values of a range of levels by a percentage, Take puts the shipped values back. Plan,
// Apply and Save mod are the MS2 Tables view's.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using Emergent.Toolbench;
using Emergent.Toolbench.UI.Workbench;
using MS2.ModKit;

namespace MS2.Tables
{
    public class LevelsView : AbstractView
    {
        public const string ViewId = "MS2.Tables.LevelsView";

        TablesView host;
        LevelExp lx;               // for the session's data and mod
        bool read;                 // lx has read the table's archives (on the worker thread)
        LevelTable readTable;
        bool filling;

        readonly ComboBox tableBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        readonly Label sidesLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListView rowList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        readonly TextBox levelBox = new TextBox { Width = 40 };
        readonly ComboBox fieldBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        readonly TextBox valueBox = new TextBox { Width = 110 };
        readonly TextBox fromBox = new TextBox { Width = 40, Text = "1" };
        readonly TextBox toBox = new TextBox { Width = 40, Text = "99" };
        readonly ComboBox scaleFieldBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        readonly TextBox percentBox = new TextBox { Width = 50, Text = "100" };
        readonly CurvePanel curve = new CurvePanel { Dock = DockStyle.Bottom, Height = 170 };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 110 };
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();

        const string AllFields = "(all)";

        public LevelsView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            foreach (LevelTable t in LevelTable.All) tableBox.Items.Add(t.Name);
            tableBox.SelectedIndex = 0;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(Lbl("Table"));
            top.Controls.Add(tableBox);
            tips.SetToolTip(tableBox, "nextexp: the experience from a level to the next (every client and the server); monster exp: what a monster of a level gives, per kind (the server's Server.m2d)");
            top.Controls.Add(Btn("Read", "Reads the table from the data folders again", delegate { Reload(); }));
            top.Controls.Add(sidesLabel);

            FlowLayoutPanel editBar = Bar();
            editBar.Controls.Add(Lbl("Level"));
            editBar.Controls.Add(levelBox);
            editBar.Controls.Add(fieldBox);
            tips.SetToolTip(fieldBox, "The field to set; the curve draws it");
            editBar.Controls.Add(valueBox);
            tips.SetToolTip(valueBox, "A whole number of 0 or more; the shipped value takes the level's line out of the mod. Enter sets it");
            editBar.Controls.Add(Btn("Set", "Sets the value of the level on every side that has the table", delegate { SetValue(); }));
            editBar.Controls.Add(Btn("Take level", "Puts the shipped values of the level back on every side", delegate { TakeLevel(); }));
            editBar.Controls.Add(Btn("Take all", "Puts the shipped values of every level of the table back", delegate { TakeAll(); }));

            FlowLayoutPanel scaleBar = Bar();
            scaleBar.Controls.Add(Lbl("Scale levels"));
            scaleBar.Controls.Add(fromBox);
            scaleBar.Controls.Add(Lbl("to"));
            scaleBar.Controls.Add(toBox);
            scaleBar.Controls.Add(scaleFieldBox);
            tips.SetToolTip(scaleFieldBox, "The field to scale, or every field of the table");
            scaleBar.Controls.Add(Lbl("by"));
            scaleBar.Controls.Add(percentBox);
            scaleBar.Controls.Add(Lbl("%"));
            scaleBar.Controls.Add(Btn("Scale", "Sets the levels' values to the shipped values times the percentage (50: half the experience, 200: twice), rounded; 100 puts the shipped values back", delegate { ScaleRange(); }));

            FlowLayoutPanel modBar = Bar();
            modBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change, with the checks of the level tables; writes nothing", delegate { return host.DoPlan(); }));
            modBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change (the MS2 Tables view's Apply)", delegate { return host.DoApply(); }));
            modBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            rowList.ShowItemToolTips = true;
            tips.SetToolTip(curve, "The chosen field by level: with the mod (blue) and as shipped (grey)");

            var body = new Panel { Dock = DockStyle.Fill };
            body.Controls.Add(rowList);
            body.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            body.Controls.Add(curve);

            Controls.Add(body);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(outputBox);
            Controls.Add(modBar);
            Controls.Add(scaleBar);
            Controls.Add(editBar);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, editBar, scaleBar, modBar, body });
            ViewLayout.KeepLogLines(outputBox, this);
            ViewLayout.KeepHeightShare(curve, body, 0.35, 80);
            ViewLayout.CentreOnFirstShow(this);

            tableBox.SelectedIndexChanged += delegate { if (!filling) Guard(delegate { ShowTable(); }); };
            fieldBox.SelectedIndexChanged += delegate { if (!filling) { ShowValue(); DrawCurve(); } };
            rowList.SelectedIndexChanged += delegate
            {
                if (filling || rowList.SelectedItems.Count == 0) return;
                levelBox.Text = ((int)rowList.SelectedItems[0].Tag).ToString(CultureInfo.InvariantCulture);
                ShowValue();
                DrawCurve();
            };
            levelBox.TextChanged += delegate { if (!filling) ShowValue(); };
            valueBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                Guard(delegate { SetValue(); });
            };
            VisibleChanged += delegate { if (Visible && !read && TablesView.Open != null) Guard(delegate { ShowTable(); }); };
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
            b.Click += delegate { if (Guard(delegate { host.NeedHandle(); })) start(); };
            tips.SetToolTip(b, tip);
            return b;
        }

        protected override void OnEnter(EventArgs e)
        {
            TablesView.ItemsInUse = true;
            base.OnEnter(e);
        }

        // ---- the MS2 Tables view: session, worker thread, output ----

        void Hook(TablesView h)
        {
            if (h == host) return;
            if (host != null)
            {
                host.Changed -= OnHostChanged;
                host.BusyChanged -= OnHostBusy;
                host.Said -= OnHostSaid;
            }
            host = h;
            if (h == null) return;
            h.Changed += OnHostChanged;
            h.BusyChanged += OnHostBusy;
            h.Said += OnHostSaid;
            OutputWrap.Set(outputBox, h.OutputText);
            OnHostBusy(h.Busy);
        }

        bool Guard(MethodInvoker body)
        {
            try
            {
                TablesView h = TablesView.Open;
                if (h == null || h.IsDisposed) h = TablesView.OpenBehind();
                if (h == null) { OutputWrap.Set(outputBox, "the MS2 Tables view could not be opened; this view works on the data of that view"); return false; }
                Hook(h);
            }
            catch (Exception ex)
            {
                OutputWrap.Set(outputBox, "the MS2 Tables view could not be opened: " + ex.Message);
                Logger.Warning("MS2 Levels: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        void OnHostSaid(string text) { OutputWrap.Set(outputBox, text); }

        void OnHostBusy(bool on) { ViewLayout.Lock(this, lockable, on, outputBox); }

        /// <summary>Another session or mod (or an edit of another view): show the table again.</summary>
        void OnHostChanged()
        {
            EditorSession s = host.SharedSession();
            if (lx != null && (lx.Data != s.Data || lx.Mod != s.Mod)) { lx = null; read = false; }
            if (Visible) { if (read) ShowRows(); else if (!host.Busy) ShowTable(); }
        }

        // ---- the table ----

        LevelTable Table() { return LevelTable.All[Math.Max(0, tableBox.SelectedIndex)]; }

        void Reload()
        {
            lx = null;
            read = false;
            ShowTable();
        }

        /// <summary>Reads the table's archives on the worker thread (once per session and mod), then shows it.</summary>
        void ShowTable()
        {
            EditorSession s = host.SharedSession();
            if (s.Mod == null) { sidesLabel.Text = "no mod is being edited"; return; }
            LevelTable t = Table();
            FillFields(t);
            if (lx == null || lx.Data != s.Data || lx.Mod != s.Mod) { lx = new LevelExp(s.Data, s.Mod); read = false; }
            if (read && readTable == t) { ShowRows(); return; }
            LevelExp reading = lx;
            host.RunOutside("level table", delegate { reading.Levels(t); }, delegate
            {
                if (reading != lx) return;
                read = true;
                readTable = t;
                ShowRows();
            });
        }

        void FillFields(LevelTable t)
        {
            if (fieldBox.Items.Count == t.Fields.Length && (string)fieldBox.Items[0] == t.Fields[0]) return;
            filling = true;
            try
            {
                fieldBox.Items.Clear();
                scaleFieldBox.Items.Clear();
                scaleFieldBox.Items.Add(AllFields);
                foreach (string f in t.Fields) { fieldBox.Items.Add(f); scaleFieldBox.Items.Add(f); }
                fieldBox.SelectedIndex = 0;
                scaleFieldBox.SelectedIndex = 0;
                rowList.Columns.Clear();
                rowList.Columns.Add("level", 50, HorizontalAlignment.Right);
                foreach (string f in t.Fields) rowList.Columns.Add(f, t.Fields.Length == 1 ? 120 : 95, HorizontalAlignment.Right);
                if (t.Fields.Length == 1) { rowList.Columns.Add("shipped", 120, HorizontalAlignment.Right); rowList.Columns.Add("change", 70, HorizontalAlignment.Right); }
                rowList.Columns.Add("sides", 60, HorizontalAlignment.Left);
                rowList.Columns.Add("in mod", 60, HorizontalAlignment.Left);
            }
            finally { filling = false; }
        }

        static string Change(string now, string was)
        {
            long? a = LevelExp.Number(now), b = LevelExp.Number(was);
            if (a == null || b == null || b.Value == 0 || a == b) return "";
            return (a.Value * 100.0 / b.Value).ToString("0.#", CultureInfo.InvariantCulture) + "%";
        }

        void ShowRows()
        {
            if (lx == null || !read) return;
            LevelTable t = Table();
            if (readTable != t) { ShowTable(); return; }
            List<string> sides = lx.Sides(t);
            var differ = new HashSet<int>(lx.Differ(t));
            int keep = rowList.SelectedItems.Count > 0 ? (int)rowList.SelectedItems[0].Tag : -1;
            filling = true;
            try
            {
                rowList.BeginUpdate();
                rowList.Items.Clear();
                foreach (int level in lx.Levels(t))
                {
                    var it = new ListViewItem(level.ToString(CultureInfo.InvariantCulture)) { Tag = level };
                    var tip = new List<string>();
                    foreach (string f in t.Fields)
                    {
                        string now = lx.Get(t, level, f) ?? "", was = lx.GetShipped(t, level, f) ?? "";
                        it.SubItems.Add(now);
                        if (now != was) tip.Add(f + ": " + was + " shipped");
                    }
                    if (t.Fields.Length == 1)
                    {
                        string was = lx.GetShipped(t, level, t.Fields[0]) ?? "";
                        it.SubItems.Add(was);
                        it.SubItems.Add(Change(lx.Get(t, level, t.Fields[0]), was));
                    }
                    bool edited = lx.IsEdited(t, level), differs = differ.Contains(level);
                    it.SubItems.Add(differs ? "differ" : "");
                    it.SubItems.Add(edited ? "yes" : "");
                    if (differs)
                    {
                        it.ForeColor = Color.DarkOrange;
                        foreach (string a in sides) tip.Add(a + ": " + (lx.Get(t, a, level, t.Fields[0]) ?? "no row"));
                    }
                    else if (edited) it.ForeColor = Color.MediumBlue;
                    if (t.ClientMax > 0 && (level < 1 || level > t.ClientMax)) { it.ForeColor = SystemColors.GrayText; tip.Add("the client reads levels 1.." + t.ClientMax + " only"); }
                    it.ToolTipText = string.Join("\n", tip.ToArray());
                    rowList.Items.Add(it);
                    if (level == keep) it.Selected = true;
                }
            }
            finally { rowList.EndUpdate(); filling = false; }
            sidesLabel.Text = (sides.Count == 0 ? "no data folder has " + t.Entry : string.Join(", ", sides.ToArray())) + "; " + lx.Levels(t).Count + " levels, " + lx.EditedCount(t) + " edited"
                + (differ.Count > 0 ? ", " + differ.Count + " differ between the sides" : "");
            ShowValue();
            DrawCurve();
        }

        int? LevelOf(TextBox box)
        {
            int n;
            return int.TryParse(box.Text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n) ? n : (int?)null;
        }

        string Field() { return fieldBox.SelectedItem as string ?? Table().Fields[0]; }

        void ShowValue()
        {
            if (lx == null || !read) return;
            int? level = LevelOf(levelBox);
            string v = level == null ? null : lx.Get(Table(), level.Value, Field());
            filling = true;
            try { if (v != null) valueBox.Text = v; }
            finally { filling = false; }
        }

        void DrawCurve()
        {
            if (lx == null || !read) { curve.Set(null, null, null, -1); return; }
            LevelTable t = Table();
            string f = Field();
            var levels = lx.Levels(t);
            var now = new List<long?>();
            var was = new List<long?>();
            foreach (int l in levels) { now.Add(LevelExp.Number(lx.Get(t, l, f))); was.Add(LevelExp.Number(lx.GetShipped(t, l, f))); }
            int? sel = LevelOf(levelBox);
            curve.Set(levels, now, was, sel == null ? -1 : levels.IndexOf(sel.Value));
            curve.Title = t.Name + " " + f;
        }

        void NeedRead()
        {
            if (host.SharedSession().Mod == null) throw new ModKitException("no mod is being edited");
            if (lx == null || !read || readTable != Table()) throw new ModKitException("the table is being read: try again");
        }

        void SetValue()
        {
            NeedRead();
            LevelTable t = Table();
            int? level = LevelOf(levelBox);
            if (level == null) throw new ModKitException("give the level");
            string f = Field();
            int n = lx.Set(t, level.Value, f, valueBox.Text);
            host.EditedOutside();
            ShowRows();
            host.SayOutside(t.Name + " level " + level.Value + " " + f + " = " + lx.Get(t, level.Value, f) + (n == 0 ? ": as it was" : " on " + n + " side" + (n == 1 ? "" : "s")) + (lx.IsEdited(t, level.Value) ? "" : " (as shipped: no line in the mod)"));
        }

        void ScaleRange()
        {
            NeedRead();
            LevelTable t = Table();
            int? from = LevelOf(fromBox), to = LevelOf(toBox);
            if (from == null || to == null) throw new ModKitException("give the levels to scale (from and to)");
            double percent;
            if (!double.TryParse(percentBox.Text.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out percent)) throw new ModKitException("give the percentage as a number (50, 150, 12.5)");
            string f = scaleFieldBox.SelectedItem as string;
            if (f == AllFields) f = null;
            int n = lx.Scale(t, from.Value, to.Value, f, percent);
            host.EditedOutside();
            ShowRows();
            host.SayOutside(t.Name + " levels " + Math.Min(from.Value, to.Value) + ".." + Math.Max(from.Value, to.Value) + " " + (f ?? "every field") + " at " + percent.ToString(CultureInfo.InvariantCulture) + "% of the shipped values: " + n + " levels changed; " + lx.EditedCount(t) + " edited in all");
        }

        void TakeLevel()
        {
            NeedRead();
            LevelTable t = Table();
            int? level = LevelOf(levelBox);
            if (level == null) throw new ModKitException("give the level");
            bool had = lx.Take(t, level.Value);
            host.EditedOutside();
            ShowRows();
            host.SayOutside(t.Name + " level " + level.Value + ": " + (had ? "the shipped values again" : "the mod does not change it"));
        }

        void TakeAll()
        {
            NeedRead();
            LevelTable t = Table();
            int n = lx.TakeAll(t);
            host.EditedOutside();
            ShowRows();
            host.SayOutside(t.Name + ": the shipped values again (" + n + " levels taken out of the mod)");
        }

        /// <summary>The curve of one field by level: the mod's values over the shipped ones.</summary>
        sealed class CurvePanel : Panel
        {
            List<int> levels;
            List<long?> now, was;
            int selected = -1;
            public string Title = "";

            public CurvePanel() { DoubleBuffered = true; BackColor = SystemColors.Window; ResizeRedraw = true; }

            public void Set(List<int> levels, List<long?> now, List<long?> was, int selected)
            {
                this.levels = levels; this.now = now; this.was = was; this.selected = selected;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (levels == null || levels.Count < 2) return;
                long max = 1;
                for (int i = 0; i < levels.Count; i++)
                {
                    if (now[i] != null && now[i].Value > max) max = now[i].Value;
                    if (was[i] != null && was[i].Value > max) max = was[i].Value;
                }
                var area = new Rectangle(50, 18, Math.Max(10, ClientSize.Width - 60), Math.Max(10, ClientSize.Height - 36));
                using (var axis = new Pen(SystemColors.ControlDark))
                {
                    g.DrawLine(axis, area.Left, area.Bottom, area.Right, area.Bottom);
                    g.DrawLine(axis, area.Left, area.Top, area.Left, area.Bottom);
                }
                using (var text = new SolidBrush(SystemColors.WindowText))
                {
                    g.DrawString(Title + " (max " + max.ToString("N0", CultureInfo.InvariantCulture) + ")", Font, text, area.Left, 2);
                    g.DrawString(levels[0].ToString(CultureInfo.InvariantCulture), Font, text, area.Left, area.Bottom + 2);
                    string last = levels[levels.Count - 1].ToString(CultureInfo.InvariantCulture);
                    g.DrawString(last, Font, text, area.Right - g.MeasureString(last, Font).Width, area.Bottom + 2);
                }
                Func<int, float> x = i => area.Left + (float)i * area.Width / (levels.Count - 1);
                Func<long, float> y = v => area.Bottom - (float)((double)v / max * area.Height);
                if (selected >= 0)
                    using (var p = new Pen(Color.Silver) { DashStyle = DashStyle.Dot })
                        g.DrawLine(p, x(selected), area.Top, x(selected), area.Bottom);
                Line(g, was, x, y, Color.DarkGray, 1.5f);
                Line(g, now, x, y, Color.MediumBlue, 1.5f);
            }

            static void Line(Graphics g, List<long?> values, Func<int, float> x, Func<long, float> y, Color c, float width)
            {
                using (var p = new Pen(c, width))
                {
                    PointF? last = null;
                    for (int i = 0; i < values.Count; i++)
                    {
                        if (values[i] == null) { last = null; continue; }
                        var pt = new PointF(x(i), y(values[i].Value));
                        if (last != null) g.DrawLine(p, last.Value, pt);
                        last = pt;
                    }
                }
            }
        }
    }
}
