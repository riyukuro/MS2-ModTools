// The "MS2 Fishing" view: the server's fishing tables (table/Server/fish.xml, fishingSpot.xml, fishLure.xml,
// globalFishBox.xml and individualFishBox.xml in the server's Server.m2d, MS2.ModKit.FishingEditor) as a mod edit.
//
// The table box chooses the table; the list on the left shows its rows with the mod (an edited one in blue, a new one
// in green, a removed one in grey), filtered by the find box (a key, or for a box a fish id of its lines). On the
// right: the row's fields (field, value with the mod, what the value means or what is wrong with it); choosing a
// field puts its value in the box, Set writes it. For a fish box its fish lines follow (fish, weight, weightRate),
// added, set, removed or put back. Under them the row's checks. New adds a row with the fields a working row needs,
// Copy a copy of the chosen one (a box with its lines), Remove / restore deletes a shipped row or takes a new one out,
// Undo puts a shipped row's fields back. Plan, Apply and Save mod are the MS2 Tables view's.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Emergent.Toolbench;
using Emergent.Toolbench.UI.Workbench;
using MS2.ModKit;

namespace MS2.Tables
{
    public class FishingView : AbstractView
    {
        public const string ViewId = "MS2.Tables.FishingView";

        TablesView host;
        FishingEditor ed;          // for the session's data and mod
        bool read;                 // ed has read the tables (on the worker thread)
        bool filling;
        FishTable table = FishTable.Fish;
        FishRow shown;             // the row the right side shows
        string wantKey;            // the key text of the row to show again once the tables are read again
        List<FishLine> lines = new List<FishLine>();

        static readonly string[] TableNames = { "fish", "fishing spots", "lures", "global fish boxes", "individual fish boxes" };

        readonly ComboBox tableBox = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly TextBox findBox = new TextBox { Width = 110 };
        readonly Label sumLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListView rowList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, ShowItemToolTips = true };
        readonly ListView fieldList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, ShowItemToolTips = true };
        readonly ListView lineList = new ListView { Dock = DockStyle.Bottom, Height = 140, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        readonly ListBox checkList = new ListBox { Dock = DockStyle.Bottom, Height = 90, HorizontalScrollbar = true, IntegralHeight = false };
        readonly Label rowLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 0, 0), Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
        readonly Label infoLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 0, 0) };
        readonly Label fieldLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 0, 0), Text = "(choose a field)" };
        readonly TextBox valueBox = new TextBox { Width = 260 };
        readonly TextBox fishBox = new TextBox { Width = 80 };
        readonly TextBox weightBox = new TextBox { Width = 60, Text = "10" };
        readonly TextBox rateBox = new TextBox { Width = 60 };
        readonly TextBox newIdBox = new TextBox { Width = 80 };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 110 };
        readonly FlowLayoutPanel lineBar;
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();

        static readonly Dictionary<string, string> FieldTips = new Dictionary<string, string>
        {
            { "fishingBook", "true/false (1/0): the fish shows in the fishing book" },
            { FishingRules.Habitat, "Where the fish lives: none, water, seawater, lava, poison, oil, devilwater, emeraldwater or all (empty or another word: all); a spot's water gives fish of water and of all" },
            { FishingRules.Mastery, "The fishing mastery a fish needs: the server gives it only at spots whose maxMastery is at least this (unless ignoreSpotMastery)" },
            { "rank", "The fish's rank (1 common .. 4 legendary): the lure's catchRank and spawnRank choose by it" },
            { "pointCount", "The size points of the fish: 1, or 2 for a fish with a big size" },
            { FishingRules.SmallSize, "The size range in cm, as min-max (the server reads both numbers: one alone stops it)" },
            { FishingRules.BigSize, "The size range in cm of a big catch, as min-max" },
            { FishingRules.Ignore, "true/false (1/0): the fish bites at a spot whatever its mastery" },
            { "bait", "The bait item ids the fish takes, a comma list" },
            { FishingRules.Liquid, "The liquids of the spot's tiles, a comma list of none, water, seawater, lava, poison, oil, devilwater, emeraldwater, all" },
            { FishingRules.MinMastery, "The lowest fishing mastery the spot gives" },
            { FishingRules.MaxMastery, "The highest fishing mastery the spot gives; a fish needs fishMastery at most this" },
            { FishingRules.GlobalBox, "The global fish box (globalFishBox.xml) whose fish bite at the spot" },
            { FishingRules.IndividualBox, "The individual fish box (individualFishBox.xml) whose fish bite at the spot" },
            { "additionalEffectCode", "The buff (additional effect) the lure is: the server reads the lure while the player has it" },
            { "additionalEffectLevel", "The buff's level this row is for" },
            { "catchRank", "The fish ranks the lure helps, a comma list; catchProp gives each a chance (one number for each rank)" },
            { "catchProp", "The chance in 1/10000 for each rank of catchRank, a comma list" },
            { "spawnRank", "The fish ranks the lure brings, a comma list; spawnProp gives each a chance" },
            { "spawnProp", "The chance in 1/10000 for each rank of spawnRank, a comma list" },
            { "globalDropFishingSpotMastery", "The spot mastery range, as min-max" },
            { "individualDropFishingSpotMastery", "The spot mastery range, as min-max" },
            { FishingRules.Probability, "The chance in 1/10000 that the box gives a fish at all" },
            { FishingRules.Feature, "The feature the row needs (table/Server/feature.xml in Xml.m2d): a row of an off feature is left out" },
            { FishingRules.Locale, "The locale the row is for (empty: any): a row with the server's locale wins over one without" },
        };

        public FishingView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(Btn("Read", "Reads the server's fishing tables again", delegate { Reload(); }));
            top.Controls.Add(Lbl("Table"));
            tableBox.Items.AddRange(TableNames);
            tableBox.SelectedIndex = 0;
            top.Controls.Add(tableBox);
            top.Controls.Add(Lbl("Find"));
            top.Controls.Add(findBox);
            tips.SetToolTip(findBox, "A key, or part of one; for a fish box also a fish id of its lines");
            top.Controls.Add(sumLabel);

            FlowLayoutPanel rowBar = Bar();
            rowBar.Controls.Add(Lbl("New id"));
            rowBar.Controls.Add(newIdBox);
            tips.SetToolTip(newIdBox, "The new row's key (a lure's additionalEffectCode: a lure may have a row for each level); empty: the next free one");
            rowBar.Controls.Add(Btn("New", "Adds a row of the table with the fields a working row needs (a box has no fish yet: add its lines)", delegate { NewRow(false); }));
            rowBar.Controls.Add(Btn("Copy", "Adds a copy of the chosen row, as the mod leaves it, under the new id (a box with its lines)", delegate { NewRow(true); }));
            rowBar.Controls.Add(Btn("Remove / restore", "Removes the chosen row (a shipped one: a delete line in the mod), or puts a removed shipped row back", delegate { RemoveOrRestore(); }));
            rowBar.Controls.Add(Btn("Undo", "Puts the chosen shipped row's fields back as shipped (a box's lines stay)", delegate { Undo(); }));

            FlowLayoutPanel modBar = Bar();
            modBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change, with the checks of the fishing tables; writes nothing", delegate { return host.DoPlan(); }));
            modBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change (the MS2 Tables view's Apply)", delegate { return host.DoApply(); }));
            modBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            rowList.Columns.Add("key", 110, HorizontalAlignment.Right);
            rowList.Columns.Add("about", 260);
            rowList.Columns.Add("in mod", 60);

            fieldList.Columns.Add("field", 170);
            fieldList.Columns.Add("value", 160);
            fieldList.Columns.Add("means", 260);

            lineList.Columns.Add("line", 40, HorizontalAlignment.Right);
            lineList.Columns.Add("fish", 80, HorizontalAlignment.Right);
            lineList.Columns.Add("weight", 60, HorizontalAlignment.Right);
            lineList.Columns.Add("weightRate", 70, HorizontalAlignment.Right);
            lineList.Columns.Add("the fish", 200);
            lineList.Columns.Add("in mod", 60);

            var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(2) };
            head.Controls.Add(rowLabel);
            head.Controls.Add(infoLabel);

            FlowLayoutPanel setBar = Bar();
            setBar.Dock = DockStyle.Bottom;
            setBar.Controls.Add(fieldLabel);
            setBar.Controls.Add(valueBox);
            tips.SetToolTip(valueBox, "The chosen field's value (empty: the attribute is taken out)");
            setBar.Controls.Add(Btn("Set", "Writes the value of the chosen field into the mod", delegate { SetField(); }));

            lineBar = Bar();
            lineBar.Dock = DockStyle.Bottom;
            lineBar.Controls.Add(Lbl("Fish"));
            lineBar.Controls.Add(fishBox);
            tips.SetToolTip(fishBox, "The fish id of the line (a fish of fish.xml)");
            lineBar.Controls.Add(Lbl("weight"));
            lineBar.Controls.Add(weightBox);
            tips.SetToolTip(weightBox, "How often the fish is drawn against the box's other fish");
            lineBar.Controls.Add(Lbl("weightRate"));
            lineBar.Controls.Add(rateBox);
            tips.SetToolTip(rateBox, "Read by the server's ingest with the weight (empty: none)");
            lineBar.Controls.Add(Btn("Add fish", "Adds a line for the fish at the weight", delegate { EditLine('a'); }));
            lineBar.Controls.Add(Btn("Set line", "Sets the chosen line's fish, weight and weightRate", delegate { EditLine('s'); }));
            lineBar.Controls.Add(Btn("Remove / restore line", "Removes the chosen line (a shipped one: a delete line in the mod), or puts a removed shipped line back", delegate { EditLine('r'); }));
            lineBar.Controls.Add(Btn("Undo line", "Puts the chosen shipped line back as shipped", delegate { EditLine('u'); }));

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(fieldList);
            right.Controls.Add(setBar);
            right.Controls.Add(lineList);
            right.Controls.Add(lineBar);
            right.Controls.Add(checkList);
            right.Controls.Add(head);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
            split.Panel1.Controls.Add(rowList);
            split.Panel2.Controls.Add(right);

            Controls.Add(split);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(outputBox);
            Controls.Add(modBar);
            Controls.Add(rowBar);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, rowBar, modBar, split });
            ViewLayout.KeepLogLines(outputBox, this);
            ViewLayout.CentreOnFirstShow(this);
            bool sized = false;
            split.SizeChanged += delegate
            {
                if (sized || split.Width < 200) return;
                sized = true;
                split.SplitterDistance = Math.Max(100, split.Width * 40 / 100);
            };

            tableBox.SelectedIndexChanged += delegate
            {
                if (filling) return;
                table = FishingRules.Tables[tableBox.SelectedIndex];
                shown = null;
                Guard(delegate { ShowRows(); });
            };
            findBox.TextChanged += delegate { if (!filling && read) Guard(delegate { ShowRows(); }); };
            rowList.SelectedIndexChanged += delegate
            {
                if (filling || rowList.SelectedItems.Count == 0) return;
                shown = (FishRow)rowList.SelectedItems[0].Tag;
                Guard(delegate { ShowRow(); });
            };
            fieldList.SelectedIndexChanged += delegate
            {
                if (filling || fieldList.SelectedItems.Count == 0) return;
                ListViewItem it = fieldList.SelectedItems[0];
                fieldLabel.Text = it.Text;
                valueBox.Text = it.SubItems[1].Text;
            };
            lineList.SelectedIndexChanged += delegate
            {
                if (filling || lineList.SelectedItems.Count == 0) return;
                ListViewItem it = lineList.SelectedItems[0];
                fishBox.Text = it.SubItems[1].Text;
                weightBox.Text = it.SubItems[2].Text;
                rateBox.Text = it.SubItems[3].Text;
            };
            VisibleChanged += delegate { if (Visible && !read && TablesView.Open != null) Guard(delegate { ReadTables(); }); };
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
                Logger.Warning("MS2 Fishing: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        void OnHostSaid(string text) { OutputWrap.Set(outputBox, text); }

        void OnHostBusy(bool on) { ViewLayout.Lock(this, lockable, on, outputBox); }

        /// <summary>Another session or mod (or an edit of another view): read the tables again, as the mod now has them.</summary>
        void OnHostChanged()
        {
            if (shown != null && ed != null) wantKey = KeyText(shown);
            ed = null;
            read = false;
            shown = null;
            if (Visible && !host.Busy) ReadTables();
        }

        // ---- the rows ----

        void Reload()
        {
            ed = null;
            read = false;
            shown = null;
            ReadTables();
        }

        /// <summary>Reads the fishing tables on the worker thread (once per session and mod), then shows them.</summary>
        void ReadTables()
        {
            EditorSession s = host.SharedSession();
            if (s.Mod == null) { sumLabel.Text = "no mod is being edited"; return; }
            if (ed != null && ed.Data == s.Data && ed.Mod == s.Mod && read) { ShowRows(); return; }
            FishingEditor made = null;
            string failed = null;
            host.RunOutside("fishing tables", delegate
            {
                try { made = new FishingEditor(s.Data, s.Mod); }
                catch (ModKitException ex) { failed = ex.Message; }
            }, delegate
            {
                if (failed != null) { sumLabel.Text = failed; rowList.Items.Clear(); return; }
                ed = made;
                read = true;
                if (wantKey != null && ed.Has(table))
                    foreach (FishRow r in ed.Rows(table)) if (KeyText(r) == wantKey) { shown = r; break; }
                wantKey = null;
                ShowRows();
            });
        }

        FishingEditor Editor()
        {
            if (ed == null || !read) throw new ModKitException("the fishing tables are being read: try again");
            if (host.SharedSession().Mod == null) throw new ModKitException("no mod is being edited");
            return ed;
        }

        static string Num(string v, string what)
        {
            return v.Length == 0 ? "" : what + " " + v;
        }

        string About(FishRow r)
        {
            if (r.IsDeleted) return "removed by the mod";
            switch (r.Table)
            {
                case FishTable.Fish:
                    return "habitat " + FishingRules.LiquidName(FishingRules.HabitatOf(ed.Get(r, FishingRules.Habitat))) + ", mastery " + ed.Get(r, FishingRules.Mastery)
                        + ", rank " + ed.Get(r, "rank") + ", " + ed.BoxesWith(ed.Id(r)).Count + " box(es)";
                case FishTable.Spot:
                    return ed.Get(r, FishingRules.Liquid) + ", mastery " + ed.Get(r, FishingRules.MinMastery) + "-" + ed.Get(r, FishingRules.MaxMastery)
                        + ", " + Join(Num(ed.Get(r, FishingRules.GlobalBox), "global box"), Num(ed.Get(r, FishingRules.IndividualBox), "individual box"));
                case FishTable.Lure:
                    return Join(Num(ed.Get(r, "catchRank"), "catch rank"), Num(ed.Get(r, "spawnRank"), "spawn rank"));
                default:
                    int n = ed.Codes(r).Count;
                    return n + (n == 1 ? " fish" : " fish lines") + ", probability " + ed.Get(r, FishingRules.Probability) + ", " + ed.SpotsUsing(r).Count + " spot(s)";
            }
        }

        static string Join(string a, string b) { return a.Length == 0 ? b : b.Length == 0 ? a : a + ", " + b; }

        string KeyText(FishRow r)
        {
            string k = ed.IdText(r);
            if (r.Table == FishTable.Lure) k += " lv " + ed.Get(r, "additionalEffectLevel");
            string f = ed.Get(r, FishingRules.Feature), l = ed.Get(r, FishingRules.Locale);
            if (f.Length > 0 || l.Length > 0) k += " (" + Join(f, l) + ")";
            return k;
        }

        void ShowRows()
        {
            if (ed == null || !read) return;
            filling = true;
            try
            {
                tableBox.SelectedIndex = Array.IndexOf(FishingRules.Tables, table);
                rowList.BeginUpdate();
                rowList.Items.Clear();
                if (!ed.Has(table))
                {
                    sumLabel.Text = "the server data has no " + FishingRules.Entry(table);
                    shown = null;
                    return;
                }
                List<FishRow> all = ed.Rows(table), hits = ed.Find(table, findBox.Text);
                // the rows the mod changes first, each part in the table's order
                var order = new List<FishRow>();
                foreach (FishRow r in hits) if (ed.IsChanged(r)) order.Add(r);
                int changed = order.Count;
                foreach (FishRow r in hits) if (!ed.IsChanged(r)) order.Add(r);
                if (shown != null && !all.Contains(shown)) shown = null;
                foreach (FishRow r in order)
                {
                    var it = new ListViewItem(KeyText(r)) { Tag = r };
                    it.SubItems.Add(About(r));
                    bool ch = ed.IsChanged(r);
                    it.SubItems.Add(r.IsAdded ? "new" : r.IsDeleted ? "removed" : ch ? "yes" : "");
                    if (r.IsAdded) it.ForeColor = Color.DarkGreen;
                    else if (r.IsDeleted) it.ForeColor = SystemColors.GrayText;
                    else if (ch) it.ForeColor = Color.MediumBlue;
                    rowList.Items.Add(it);
                    if (r == shown) it.Selected = true;
                }
                sumLabel.Text = FishingRules.Entry(table) + ": " + all.Count + " rows, " + ed.ChangedCount(table) + " in the mod"
                    + (findBox.Text.Trim().Length > 0 ? "; " + hits.Count + " found (" + changed + " in the mod)" : "");
                if (shown == null && order.Count > 0) { shown = order[0]; rowList.Items[0].Selected = true; }
            }
            finally { rowList.EndUpdate(); filling = false; }
            ShowRow();
        }

        // what a field's value means, in words, or what is wrong with it
        string Means(FishRow r, string field, string v)
        {
            if (v.Length == 0) return "";
            string bad = FishingRules.ValueProblem(r.Table, field, v);
            if (bad != null) return bad;
            int n;
            switch (field)
            {
                case FishingRules.Habitat: return FishingRules.LiquidOf(v) < 0 ? "all (the server reads another word as all)" : "";
                case FishingRules.GlobalBox:
                case FishingRules.IndividualBox:
                    if (r.Table != FishTable.Spot || !FishingRules.Int(v, out n) || n <= 0) return "";
                    FishTable t = field == FishingRules.GlobalBox ? FishTable.GlobalBox : FishTable.IndividualBox;
                    if (!ed.Has(t)) return "";
                    FishRow box = ed.Find(t, n);
                    return box == null ? "no such box" : ed.Codes(box).Count + " fish lines";
                case "fishCode":
                    if (r.Table != FishTable.Lure) return "";
                    return FishText(v);
                case "fishingBook":
                case FishingRules.Ignore:
                case "spotDropRank":
                    bool on;
                    return TryBool(v, out on) ? (on ? "yes" : "no") : "";
            }
            return "";
        }

        static bool TryBool(string v, out bool on)
        {
            string t = v.Trim();
            on = t == "1" || string.Equals(t, "true", StringComparison.OrdinalIgnoreCase);
            return on || t == "0" || string.Equals(t, "false", StringComparison.OrdinalIgnoreCase);
        }

        string FishText(string code)
        {
            int n;
            if (!FishingRules.Int(code, out n) || n <= 0) return "";
            if (!ed.Has(FishTable.Fish)) return "";
            FishRow f = ed.Find(FishTable.Fish, n);
            if (f == null) return "no such fish: the server leaves the line out";
            return "habitat " + FishingRules.LiquidName(FishingRules.HabitatOf(ed.Get(f, FishingRules.Habitat))) + ", mastery " + ed.Get(f, FishingRules.Mastery) + ", rank " + ed.Get(f, "rank");
        }

        void ShowRow()
        {
            FishRow r = ed == null || !read ? null : shown;
            filling = true;
            try
            {
                fieldList.BeginUpdate();
                lineList.BeginUpdate();
                int keep = fieldList.SelectedIndices.Count > 0 ? fieldList.SelectedIndices[0] : -1;
                int keepLine = lineList.SelectedIndices.Count > 0 ? lineList.SelectedIndices[0] : -1;
                fieldList.Items.Clear();
                lineList.Items.Clear();
                checkList.Items.Clear();
                lines = new List<FishLine>();
                bool box = FishingRules.IsBox(table);
                lineList.Visible = box;
                lineBar.Visible = box;
                if (r == null) { rowLabel.Text = "no row chosen"; infoLabel.Text = ""; return; }
                bool changed = ed.IsChanged(r);
                rowLabel.Text = FishingRules.Noun(r.Table) + " " + KeyText(r) + (r.IsAdded ? " (new)" : r.IsDeleted ? " (removed by the mod)" : changed ? " (in the mod)" : "");
                infoLabel.Text = Info(r);
                foreach (string f in FishingRules.Fields(r.Table))
                {
                    string v = ed.Get(r, f);
                    var it = new ListViewItem(f);
                    it.SubItems.Add(v);
                    it.SubItems.Add(Means(r, f, v));
                    string tip;
                    if (FieldTips.TryGetValue(f, out tip)) it.ToolTipText = tip;
                    if (v.Length > 0 && FishingRules.ValueProblem(r.Table, f, v) != null) it.ForeColor = Color.DarkOrange;
                    else if (!r.IsAdded && ed.IsEdited(r, f)) it.ForeColor = Color.MediumBlue;
                    fieldList.Items.Add(it);
                }
                if (keep >= 0 && keep < fieldList.Items.Count) fieldList.Items[keep].Selected = true;
                foreach (string t in ed.Check(r)) checkList.Items.Add(t);
                if (box)
                {
                    lines = ed.Lines(r);    // last: it opens the box
                    for (int i = 0; i < lines.Count; i++)
                    {
                        FishLine line = lines[i];
                        string code = ed.Get(line, FishingRules.FishCode);
                        var it = new ListViewItem((i + 1).ToString(CultureInfo.InvariantCulture));
                        it.SubItems.Add(code);
                        it.SubItems.Add(ed.Get(line, FishingRules.Weight));
                        it.SubItems.Add(ed.Get(line, "weightRate"));
                        it.SubItems.Add(line.IsDeleted ? "" : FishText(code));
                        bool edited = false;
                        foreach (string f in FishingRules.LineFields) if (!line.IsAdded && ed.IsEdited(line, f)) edited = true;
                        it.SubItems.Add(line.IsDeleted ? "removed" : line.IsAdded ? "new" : edited ? "yes" : "");
                        if (line.IsDeleted) it.ForeColor = SystemColors.GrayText;
                        else if (line.IsAdded) it.ForeColor = Color.DarkGreen;
                        else if (edited) it.ForeColor = Color.MediumBlue;
                        lineList.Items.Add(it);
                        if (line.IsDeleted || !(line.IsAdded || edited)) continue;
                        foreach (string t in ed.Check(line)) checkList.Items.Add("line " + (i + 1) + " (fish " + code + "): " + t);
                    }
                    if (keepLine >= 0 && keepLine < lineList.Items.Count) lineList.Items[keepLine].Selected = true;
                }
                if (checkList.Items.Count == 0) checkList.Items.Add(r.IsDeleted ? "removed by the mod" : "no problem found in the row");
            }
            finally { fieldList.EndUpdate(); lineList.EndUpdate(); filling = false; }
        }

        // the row's links, in words
        string Info(FishRow r)
        {
            if (r.IsDeleted) return "";
            long id = ed.Id(r);
            switch (r.Table)
            {
                case FishTable.Fish:
                    var names = new List<string>();
                    foreach (FishRow b in ed.BoxesWith(id)) names.Add((b.Table == FishTable.GlobalBox ? "global " : "individual ") + ed.IdText(b));
                    return names.Count == 0 ? "no fish box has a line for it" : "in fish boxes " + Short(names);
                case FishTable.Spot:
                    return "a spot is named by the fishing tiles of a map (its liquid and mastery choose the fish of its boxes)";
                case FishTable.Lure:
                    return "read while the player has buff " + ed.IdText(r) + " at level " + ed.Get(r, "additionalEffectLevel");
                default:
                    var spots = new List<string>();
                    foreach (FishRow s in ed.SpotsUsing(r)) spots.Add(ed.IdText(s));
                    return spots.Count == 0 ? "no fishing spot names it" : "named by fishing spots " + Short(spots);
            }
        }

        static string Short(List<string> l)
        {
            if (l.Count <= 8) return string.Join(", ", l.ToArray());
            return string.Join(", ", l.GetRange(0, 8).ToArray()) + " and " + (l.Count - 8) + " more";
        }

        void Edited(string said)
        {
            host.EditedOutside();
            ShowRows();
            if (said != null) host.SayOutside(said);
        }

        FishRow Shown()
        {
            Editor();
            if (shown == null) throw new ModKitException("choose a row in the list");
            return shown;
        }

        static long Number(string text, string what)
        {
            long v;
            string t = text.Trim();
            if (!long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out v)) throw new ModKitException(t.Length == 0 ? "give " + what + " first" : "\"" + t + "\" is not " + what);
            return v;
        }

        string RowName(FishRow r) { return FishingRules.Noun(r.Table) + " " + KeyText(r); }

        void SetField()
        {
            FishRow r = Shown();
            if (fieldList.SelectedItems.Count == 0) throw new ModKitException("choose a field in the list");
            string f = fieldList.SelectedItems[0].Text, v = valueBox.Text.Trim();
            if (v == ed.Get(r, f)) { host.SayOutside(RowName(r) + ": " + f + " as it was"); return; }
            ed.Set(r, f, v);
            Edited(RowName(r) + ": " + f + (v.Length == 0 ? " taken out" : " = " + v));
        }

        FishLine ChosenLine()
        {
            int i = lineList.SelectedIndices.Count > 0 ? lineList.SelectedIndices[0] : -1;
            if (i < 0 || i >= lines.Count) throw new ModKitException("choose a line of the box");
            return lines[i];
        }

        void EditLine(char what)
        {
            FishRow box = Shown();
            if (!FishingRules.IsBox(box.Table)) throw new ModKitException("only a fish box has lines");
            switch (what)
            {
                case 'a':
                {
                    long fish = Number(fishBox.Text, "a fish id");
                    long weight = Number(weightBox.Text, "a weight");
                    FishLine made = ed.AddFish(box, fish, weight);
                    if (rateBox.Text.Trim().Length > 0) ed.Set(made, "weightRate", rateBox.Text.Trim());
                    Edited(RowName(box) + ": fish " + fish + " added at weight " + weight + (ed.Has(FishTable.Fish) && ed.Find(FishTable.Fish, fish) == null ? " (there is no fish " + fish + ": add it in the fish table)" : ""));
                    return;
                }
                case 's':
                {
                    FishLine line = ChosenLine();
                    var set = new List<string>();
                    string[] fields = { FishingRules.FishCode, FishingRules.Weight, "weightRate" };
                    TextBox[] boxes = { fishBox, weightBox, rateBox };
                    for (int i = 0; i < fields.Length; i++)
                    {
                        string v = boxes[i].Text.Trim();
                        if (v == ed.Get(line, fields[i])) continue;
                        ed.Set(line, fields[i], v);
                        set.Add(fields[i]);
                    }
                    Edited(RowName(box) + ": " + (set.Count == 0 ? "the line is as it was" : "line " + string.Join(", ", set.ToArray()) + " set"));
                    return;
                }
                case 'r':
                {
                    FishLine line = ChosenLine();
                    if (line.IsDeleted) { ed.Restore(line); Edited(RowName(box) + ": the line is back"); return; }
                    bool added = line.IsAdded;
                    ed.Remove(line);
                    Edited(RowName(box) + ": " + (added ? "the line was new in the mod and is gone again" : "the line is removed (Remove / restore line brings it back)"));
                    return;
                }
                case 'u':
                {
                    FishLine line = ChosenLine();
                    ed.Revert(line);
                    Edited(RowName(box) + ": the line is as shipped again");
                    return;
                }
            }
        }

        void NewRow(bool copy)
        {
            FishingEditor e = Editor();
            if (!e.Has(table)) throw new ModKitException("the server data has no " + FishingRules.Entry(table));
            FishRow from = copy ? Shown() : null;
            long id = newIdBox.Text.Trim().Length > 0 ? Number(newIdBox.Text, "an id") : copy && table == FishTable.Lure ? e.Id(from) : e.FreeId(table);
            FishRow made = copy ? e.Copy(from, id) : e.Add(table, id);
            string fromText = copy ? KeyText(from) : "";
            newIdBox.Text = "";
            findBox.Text = "";
            shown = made;
            string then;
            switch (table)
            {
                case FishTable.Fish: then = ". It is caught once a fish box has a line for it, and the clients' table/fish.xml need a row for it: Plan names what is missing."; break;
                case FishTable.Spot: then = ". A map's fishing tiles name the spot; set its boxes and liquid."; break;
                case FishTable.Lure: then = ". Set its level and ranks: Plan says which row of the code the server keeps."; break;
                default: then = copy ? ". A fishing spot names it in " + (table == FishTable.GlobalBox ? FishingRules.GlobalBox : FishingRules.IndividualBox) + "." : ", with no fish: add its lines with Add fish, and name it in a fishing spot."; break;
            }
            Edited(FishingRules.Noun(table) + " " + id + " added" + (copy ? " as a copy of " + fromText : "") + then + " Save mod, then Plan checks it.");
        }

        void RemoveOrRestore()
        {
            FishRow r = Shown();
            string what = RowName(r);
            if (r.IsDeleted) { ed.Restore(r); Edited(what + " is back"); return; }
            bool added = r.IsAdded;
            ed.Remove(r);
            if (added) shown = null;
            Edited(added ? what + " was new in the mod and is gone again" : what + " is removed (Remove / restore brings it back); Plan names what still uses it");
        }

        void Undo()
        {
            FishRow r = Shown();
            ed.Revert(r);
            Edited(RowName(r) + ": its fields are as shipped again" + (FishingRules.IsBox(r.Table) ? " (its lines stay as the mod has them)" : ""));
        }
    }
}
