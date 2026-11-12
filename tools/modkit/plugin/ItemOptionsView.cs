// The "MS2 Item options" view: the stat lines of items, by option code (MS2.ModKit.ItemOptionEditor). Round one has
// the constant options (fixed base stats) and the static options (fixed bonus lines); the random lines come later.
//
// Screen 1, the code picker: a kind (constant or static), a find box (a part of the code, or an item id: the codes
// that item names first), and the codes with the items naming them, the grades with rows, the sides and files.
// Screen 2, the grade grid of the chosen code: a stat per row, a grade (rarity 1 to 5, or 6) per column, on the side
// chosen above (the kms client's table, which the client's tooltip shows, or the flat rows the server applies). A
// cell where the sides give different values is marked; its tooltip shows every side's value and where it comes from
// (a kms rank serves every grade from its own up to the next rank). An edit is written on the other side too where
// that side had the same value; otherwise one side only, and Plan warns that the sides differ. Below: the items
// naming the code, and Fork, which copies the code's rows to a new code of the mods' band and points the chosen
// items at it (through the item files, as the Items view writes them) after a confirm.
// Every edit is an op of the mod the MS2 Tables view is editing.
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
    public class ItemOptionsView : AbstractView
    {
        public const string ViewId = "MS2.Tables.ItemOptionsView";
        const string AddStatRow = "(type a stat to add)";
        const int NamedItems = 60;   // each name opens the item

        TablesView host;
        ItemOptionEditor editor;
        ItemEditor items;
        long code;   // 0 = none chosen
        readonly List<OptionCodeInfo> hits = new List<OptionCodeInfo>();
        readonly List<string> stats = new List<string>();
        readonly List<string> extraStats = new List<string>();
        readonly List<int> grades = new List<int>();
        readonly List<long> codeItems = new List<long>();
        readonly Dictionary<long, string> itemNames = new Dictionary<long, string>();
        long firstItem;   // the item the find box named, if any
        bool filling;

        readonly ComboBox kindBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
        readonly TextBox searchBox = new TextBox { Width = 160 };
        readonly Label hitsLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly DataGridView codeGrid = new DataGridView { Dock = DockStyle.Left, Width = 470 };
        readonly Label codeLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 8, 0) };
        readonly ComboBox sideBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        readonly TextBox newCodeBox = new TextBox { Width = 90 };
        readonly DataGridView statGrid = new DataGridView { Dock = DockStyle.Fill };
        readonly ListBox itemList = new ListBox { Dock = DockStyle.Bottom, Height = 110, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended, HorizontalScrollbar = true };
        readonly Label itemsLabel = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = 20, TextAlign = ContentAlignment.MiddleLeft };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 130 };
        readonly ComboBox targetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        // screen 3, the random detail: the kms client's row of the code (its attributes and stat lines) beside what the server rolls
        readonly Panel detail = new Panel { Dock = DockStyle.Bottom, Height = 200, Visible = false };
        readonly Splitter detailSplit = new Splitter { Dock = DockStyle.Bottom, Height = 4, Visible = false };
        readonly Label detailLabel = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 34 };
        readonly DataGridView lineGrid = new DataGridView { Dock = DockStyle.Fill };
        readonly Dictionary<string, TextBox> attrBoxes = new Dictionary<string, TextBox>();
        readonly TextBox addName = new TextBox { Width = 50 }, addId = new TextBox { Width = 62 }, addWeight = new TextBox { Width = 30 };
        int detailGrade;
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();
        readonly BoldFont bold = new BoldFont();
        static readonly Color DiffBack = Color.FromArgb(255, 236, 200);

        public ItemOptionsView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            kindBox.Items.Add("constant (fixed base stats)");
            kindBox.Items.Add("static (fixed bonus lines)");
            kindBox.Items.Add("random (rolled bonus lines)");
            kindBox.SelectedIndex = 0;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(kindBox);
            tips.SetToolTip(kindBox, "The option table: constant = the item's fixed base stats; static = its fixed bonus lines (server and global client only: the kms client has no static table); random = the lines rolled when the item drops");
            top.Controls.Add(Lbl("find"));
            top.Controls.Add(searchBox);
            tips.SetToolTip(searchBox, "A part of an option code, or an item id (the codes that item names come first); empty lists every code of the table");
            top.Controls.Add(Btn("Find", "Lists the codes of the table whose number holds the text, and the codes the item of that id names", delegate { Find(); }));
            top.Controls.Add(hitsLabel);

            FlowLayoutPanel applyBar = Bar();
            applyBar.Controls.Add(Lbl("Apply to"));
            targetBox.Items.Add("workspace (patched archives)");
            targetBox.Items.Add("game folder (loose files)");
            tips.SetToolTip(targetBox, "Where Apply writes the client's tables (the same choice as in the MS2 Tables view). The server's archives and the global client's always go to the workspace.");
            applyBar.Controls.Add(targetBox);
            applyBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change, with the option checks for each target; writes nothing", delegate { return host.DoPlan(); }));
            applyBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change, to every target at once", delegate { return host.DoApply(); }));
            applyBar.Controls.Add(Job("Revert", "Removes what Apply wrote, on every target", delegate { return host.DoRevert(); }));

            FlowLayoutPanel codeBar = Bar();
            codeBar.Controls.Add(Lbl("side"));
            codeBar.Controls.Add(sideBox);
            tips.SetToolTip(sideBox, "The side the grid shows and edits: the kms client's table (what the client's tooltip shows) or the flat rows of the server (what it applies; the global client gets the same lines)");
            codeBar.Controls.Add(Btn("Revert code", "Takes back the mod's edits of this code on every side (a code the mod added goes away)", delegate { RevertCode(); }));
            codeBar.Controls.Add(Btn("Fork", "Copies this code's rows on every side to a new code (the box, empty = the next free code of " + ItemOptionRules.From + "-" + ItemOptionRules.To + ") and points the items chosen below at it (none chosen = every item listed)", delegate { Fork(); }));
            codeBar.Controls.Add(Lbl("new code"));
            codeBar.Controls.Add(newCodeBox);
            codeBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same mod and Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));
            codeBar.Controls.Add(codeLabel);

            SetupGrid(codeGrid);
            codeGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            codeGrid.ReadOnly = true;
            codeGrid.Columns.Add(Col("code", 125, true));
            codeGrid.Columns.Add(Col("items", 60, true));
            codeGrid.Columns.Add(Col("grades", 70, true));
            codeGrid.Columns.Add(Col("sides", 110, true));
            codeGrid.Columns.Add(Col("file", 110, true));
            codeGrid.CellValueNeeded += OnCodeValueNeeded;
            codeGrid.CellFormatting += OnCodeFormatting;
            codeGrid.SelectionChanged += delegate { if (!filling) Guard(delegate { OpenSelected(); }); };

            SetupGrid(statGrid);
            statGrid.CellValueNeeded += OnStatValueNeeded;
            statGrid.CellValuePushed += OnStatValuePushed;
            statGrid.CellFormatting += OnStatFormatting;
            statGrid.CellToolTipTextNeeded += OnStatToolTip;
            statGrid.CellBeginEdit += OnStatBeginEdit;
            tips.SetToolTip(itemList, "The items naming the code (shipped, and the mod's own); Fork points the ones chosen here at the new code");

            SetupGrid(lineGrid);
            lineGrid.VirtualMode = false;
            lineGrid.Columns.Add(Col("kms line", 80, true));
            lineGrid.Columns.Add(Col("statID", 80, false));
            lineGrid.Columns.Add(Col("weight", 60, false));
            lineGrid.Columns.Add(Col("client range", 110, true));
            lineGrid.Columns.Add(Col("server min,max", 190, true));
            lineGrid.Columns[0].ToolTipText = "The stat of the line in the kms client's table/itemoptionrandom.xml";
            lineGrid.Columns[1].ToolTipText = "The row of table/itemoptionvariation.xml whose values are the range the client shows for the line";
            lineGrid.Columns[2].ToolTipText = "The kms client's weight of the line (empty = 1). The server ignores weights: it picks among the stats of its own row, each as likely";
            lineGrid.Columns[3].ToolTipText = "First..last value of the statID's row in table/itemoptionvariation.xml (read-only)";
            lineGrid.Columns[4].ToolTipText = "What the server rolls for the stat at the grade of the grid's current column: edit it in the grid above, on the server side";
            lineGrid.CellEndEdit += OnLineEndEdit;
            lineGrid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e)
            {
                if (e.ColumnIndex == 0 || e.ColumnIndex >= 3) { e.CellStyle.BackColor = SystemColors.Control; e.CellStyle.ForeColor = e.ColumnIndex == 0 ? SystemColors.WindowText : SystemColors.GrayText; }
            };
            FlowLayoutPanel attrBar = Bar();
            foreach (string a in ItemOptionRules.KmsRandomAttrs)
            {
                string name = a;
                var box = new TextBox { Width = name == "isRarePickOne" ? 22 : 34 };
                attrBoxes[name] = box;
                attrBar.Controls.Add(Lbl(name));
                attrBar.Controls.Add(box);
                box.Leave += delegate { if (!filling) Guard(delegate { SetAttr(name, box); }); };
                box.KeyDown += delegate(object sender, KeyEventArgs e)
                {
                    if (e.KeyCode != Keys.Enter) return;
                    e.Handled = e.SuppressKeyPress = true;
                    Guard(delegate { SetAttr(name, box); });
                };
            }
            tips.SetToolTip(attrBoxes["pickCount"], "kms client: how many of the lines an item gets");
            tips.SetToolTip(attrBoxes["isRarePickOne"], "kms client: 1 = a normal item gets no line, a rare one gets one, the grades above get pickCount; empty or 0 = every grade gets pickCount");
            tips.SetToolTip(attrBoxes["statGroup"], "kms client: the stat group of the row");
            tips.SetToolTip(attrBoxes["levelGroupID"], "kms client: the level group of the row");
            attrBar.Controls.Add(Lbl("  new line"));
            attrBar.Controls.Add(addName);
            tips.SetToolTip(addName, "The stat of the new line, a name the shipped table uses (str, pen, ...)");
            attrBar.Controls.Add(Lbl("statID"));
            attrBar.Controls.Add(addId);
            attrBar.Controls.Add(Lbl("weight"));
            attrBar.Controls.Add(addWeight);
            attrBar.Controls.Add(Btn("Add", "Adds the line to the kms client's row of the code", delegate { AddLine(); }));
            attrBar.Controls.Add(Btn("Remove", "Removes the chosen line from the kms client's row of the code", delegate { RemoveLine(); }));
            tips.SetToolTip(detailLabel, "The server rolls each line between the min,max of its own row and ignores the kms weights; the kms client shows the range of the line's statID");
            detail.Controls.Add(lineGrid);
            detail.Controls.Add(attrBar);
            detail.Controls.Add(detailLabel);

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(statGrid);
            right.Controls.Add(detailSplit);
            right.Controls.Add(detail);
            right.Resize += delegate { SizeDetail(right); };
            detail.VisibleChanged += delegate { SizeDetail(right); };
            right.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            right.Controls.Add(itemList);
            right.Controls.Add(itemsLabel);
            right.Controls.Add(codeBar);

            Controls.Add(right);
            Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
            Controls.Add(codeGrid);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(outputBox);
            Controls.Add(applyBar);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, applyBar, codeGrid, right });
            ViewLayout.KeepLogLines(outputBox, this);
            ViewLayout.KeepHeightShare(itemList, right, 0.22, 3 * itemList.ItemHeight + 4,
                delegate { return codeBar.Height + itemsLabel.Height + 8 + statGrid.ColumnHeadersHeight + 4 * statGrid.RowTemplate.Height + 4; });
            ViewLayout.CentreOnFirstShow(this);
            targetBox.SelectedIndexChanged += delegate { if (!filling && host != null) host.TargetIndex = targetBox.SelectedIndex; };

            searchBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                Guard(delegate { Find(); });
            };
            // a value typed into the grid is written before the kind or side changes under it (the boxes take the
            // focus first, by mouse or by tab)
            kindBox.Enter += delegate { Guard(delegate { EndEdits(); }); };
            sideBox.Enter += delegate { Guard(delegate { EndEdits(); }); };
            kindBox.SelectedIndexChanged += delegate { if (!filling) Guard(delegate { EndEdits(); code = 0; FillSides(); Find(); }); };
            sideBox.SelectedIndexChanged += delegate { if (!filling) Guard(delegate { EndEdits(); statGrid.Invalidate(); ShowLabel(); }); };
            statGrid.CurrentCellChanged += delegate { if (!filling && detail.Visible && DetailGrade() != detailGrade) Guard(delegate { FillDetail(); }); };
        }

        public override void Initialize()
        {
            base.Initialize();
            Guard(delegate { FillSides(); Find(); });
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
            g.ShowCellToolTips = true;
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

        void EndEdits() { statGrid.EndEdit(); lineGrid.EndEdit(); }

        protected override void OnEnter(EventArgs e)
        {
            TablesView.ItemsInUse = true;
            // an item may have been added or renamed in the Items view meanwhile
            itemNames.Clear();
            items = null;
            itemList.Invalidate();
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
            long keep = code;
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
            itemNames.Clear();
            if (h == null) { code = 0; hits.Clear(); return; }
            h.Changed += OnHostChanged;
            h.Flushing += EndEdits;
            h.BusyChanged += OnHostBusy;
            h.Said += OnHostSaid;
            h.TargetChanged += OnHostTarget;
            OnHostTarget();
            OutputWrap.Set(outputBox, h.OutputText);
            OnHostBusy(h.Busy);
            Reread(keep);
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
                Logger.Warning("MS2 Item options: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        ItemOptionEditor Editor()
        {
            EditorSession s = host.SharedSession();
            if (editor == null || editor.Mod != s.Mod || editor.Data != s.Data)
            {
                editor = new ItemOptionEditor(s.Data, s.Mod);
                editor.Others = delegate { return s.OtherMods(); };
                itemNames.Clear();
                if (editor.Skipped.Count > 0) host.SayOutside("not read (a parse error): " + string.Join(", ", editor.Skipped.ToArray()));
            }
            return editor;
        }

        void OnHostSaid(string text) { OutputWrap.Set(outputBox, text); }

        void OnHostBusy(bool on) { ViewLayout.Lock(this, lockable, on, outputBox); }

        // the mod was replaced or edited in another view: read the tables again and show the same code
        void OnHostChanged()
        {
            statGrid.CancelEdit();
            long keep = code;
            editor = null;
            items = null;
            itemNames.Clear();
            Reread(keep);
        }

        void Reread(long keep)
        {
            try
            {
                FillSides();
                FillHits(keep);
                ShowCode(keep);
            }
            catch (ModKitException ex) { ShowCode(0); OutputWrap.Set(outputBox, ex.Message); }
            catch (System.IO.IOException ex) { ShowCode(0); OutputWrap.Set(outputBox, ex.Message); }
        }

        OptionKind Kind { get { return kindBox.SelectedIndex == 2 ? OptionKind.Random : kindBox.SelectedIndex == 1 ? OptionKind.Static : OptionKind.Constant; } }

        OptionSide Side
        {
            get
            {
                List<OptionSide> l = Editor().Sides(Kind);
                int i = sideBox.SelectedIndex;
                return i >= 0 && i < l.Count ? l[i] : null;
            }
        }

        void FillSides()
        {
            string was = sideBox.SelectedItem as string;
            filling = true;
            try
            {
                sideBox.Items.Clear();
                foreach (OptionSide s in Editor().Sides(Kind)) sideBox.Items.Add(s.Title);
                int i = was == null ? -1 : sideBox.Items.IndexOf(was);
                // the server's rows first: they are what the game applies
                if (i < 0) i = Math.Max(0, sideBox.Items.IndexOf("server"));
                if (sideBox.Items.Count > 0) sideBox.SelectedIndex = i;
            }
            finally { filling = false; }
        }

        // ---- items: names ----

        string ItemName(long id)
        {
            string n;
            if (itemNames.TryGetValue(id, out n)) return n;
            try
            {
                EditorSession s = host.SharedSession();
                if (items == null || items.Mod != s.Mod || items.Data != s.Data) items = s.Items();
                n = items.Open(id).Name ?? "";
            }
            catch (ModKitException) { n = "(no such item)"; }
            catch (System.IO.IOException) { n = ""; }
            itemNames[id] = n;
            return n;
        }

        // ---- screen 1: the codes ----

        void Find()
        {
            EndEdits();
            FillHits(code);
            if (hits.Count > 0 && !Shown(code)) { SelectRow(0); ShowCode(hits[0].Code); }
            else if (hits.Count == 0) ShowCode(0);
        }

        bool Shown(long c)
        {
            foreach (OptionCodeInfo i in hits) if (i.Code == c) return true;
            return false;
        }

        void FillHits(long keep)
        {
            ItemOptionEditor ed = Editor();
            string text = searchBox.Text.Trim();
            List<OptionCodeInfo> all = ed.Codes(Kind);
            hits.Clear();
            firstItem = 0;
            long id;
            var named = new List<long>();
            if (text.Length > 0 && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id))
            {
                try { named = ed.CodesOf(id, Kind); firstItem = id; }
                catch (ModKitException) { }
            }
            foreach (long c in named)
                foreach (OptionCodeInfo i in all) if (i.Code == c) { hits.Add(i); break; }
            int head = hits.Count;
            foreach (OptionCodeInfo i in all)
                if (!named.Contains(i.Code) && (text.Length == 0 || i.Code.ToString(CultureInfo.InvariantCulture).Contains(text))) hits.Add(i);
            // the mod's codes first after the item's, then by code
            List<OptionCodeInfo> tail = hits.GetRange(head, hits.Count - head);
            tail.Sort(delegate(OptionCodeInfo a, OptionCodeInfo b)
            {
                bool ma = a.Added || a.Changed, mb = b.Added || b.Changed;
                if (ma != mb) return ma ? -1 : 1;
                return a.Code.CompareTo(b.Code);
            });
            hits.RemoveRange(head, hits.Count - head);
            hits.AddRange(tail);
            filling = true;
            try
            {
                codeGrid.RowCount = 0;
                codeGrid.RowCount = hits.Count;
                int at = -1;
                for (int i = 0; i < hits.Count; i++) if (hits[i].Code == keep) at = i;
                SelectRow(at);
            }
            finally { filling = false; }
            int changed = 0;
            foreach (OptionCodeInfo i in all) if (i.Added || i.Changed) changed++;
            hitsLabel.Text = hits.Count + " code(s), " + all.Count + " in the table, " + changed + " changed by the mod"
                + (firstItem != 0 ? "; item " + firstItem + " names " + named.Count : "");
        }

        void SelectRow(int i)
        {
            bool was = filling;
            filling = true;
            try
            {
                codeGrid.ClearSelection();
                if (i >= 0 && i < codeGrid.RowCount)
                {
                    codeGrid.Rows[i].Selected = true;
                    codeGrid.CurrentCell = codeGrid.Rows[i].Cells[0];
                }
            }
            finally { filling = was; }
        }

        void OpenSelected()
        {
            if (codeGrid.SelectedRows.Count == 0) return;
            int i = codeGrid.SelectedRows[0].Index;
            if (i < 0 || i >= hits.Count || hits[i].Code == code) return;
            // the code is read before the edit is written: writing it fills the list again, in another order
            long c = hits[i].Code;
            EndEdits();
            ShowCode(c);
            for (int j = 0; j < hits.Count; j++) if (hits[j].Code == c) { SelectRow(j); break; }
        }

        void OnCodeValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= hits.Count) return;
            OptionCodeInfo i = hits[e.RowIndex];
            switch (e.ColumnIndex)
            {
                case 0: e.Value = i.Code.ToString(CultureInfo.InvariantCulture) + (i.Added ? "  (new)" : i.Changed ? "  *" : ""); break;
                case 1: e.Value = i.Items.Count.ToString(CultureInfo.InvariantCulture); break;
                case 2: e.Value = i.GradeText; break;
                case 3: e.Value = string.Join(", ", i.Sides.ToArray()); break;
                case 4: e.Value = string.Join(", ", Short(i.Files).ToArray()); break;
            }
        }

        static List<string> Short(List<string> files)
        {
            var l = new List<string>();
            foreach (string f in files)
            {
                string s = f.Replace("itemoptionconstant_", "").Replace("itemoptionstatic_", "static_").Replace("itemoptionrandom_", "random_");
                if (s.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
                l.Add(s);
            }
            return l;
        }

        void OnCodeFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= hits.Count) return;
            OptionCodeInfo i = hits[e.RowIndex];
            if (e.ColumnIndex == 0 && (i.Added || i.Changed)) e.CellStyle.Font = bold.Of(codeGrid.Font);
            if (e.ColumnIndex == 1 && i.Items.Count == 0) e.CellStyle.ForeColor = SystemColors.GrayText;
        }

        // ---- screen 2: the grade grid ----

        void ShowCode(long c)
        {
            statGrid.CancelEdit();
            if (c != code)
            {
                extraStats.Clear();
                // the parsed files of the code left are read again when needed (the 32-bit host)
                if (editor != null) editor.DropDrafts();
            }
            code = c;
            stats.Clear();
            grades.Clear();
            codeItems.Clear();
            itemList.Items.Clear();
            statGrid.RowCount = 0;
            statGrid.ColumnCount = 0;
            if (c == 0)
            {
                codeLabel.Text = "no option code chosen";
                itemsLabel.Text = "";
                FillDetail();
                return;
            }
            ItemOptionEditor ed = Editor();
            stats.AddRange(ed.Stats(Kind, c));
            foreach (string s in extraStats) if (!stats.Contains(s)) stats.Add(s);
            grades.AddRange(ed.GradesOf(Kind, c));
            statGrid.Columns.Add(Col("stat", 170, false));
            foreach (int g in grades) statGrid.Columns.Add(Col(g + " " + GradeName(g), 90, false));
            statGrid.RowCount = stats.Count + 1;
            codeItems.AddRange(ed.ItemsOf(Kind, c));
            // names for the first ones only: a shared code (1-6) is named by thousands of items
            itemList.BeginUpdate();
            for (int i = 0; i < codeItems.Count; i++)
                itemList.Items.Add(codeItems[i].ToString(CultureInfo.InvariantCulture) + (i < NamedItems ? "  " + ItemName(codeItems[i]) : ""));
            itemList.EndUpdate();
            for (int i = 0; i < codeItems.Count; i++) if (codeItems[i] == firstItem) itemList.SetSelected(i, true);
            itemsLabel.Text = codeItems.Count == 0 ? " no item names this code" : " " + codeItems.Count + " item(s) name this code (choose some for Fork; none chosen = all)";
            ShowLabel();
            FillDetail();
        }

        static string GradeName(int g)
        {
            switch (g)
            {
                case 1: return "normal";
                case 2: return "rare";
                case 3: return "exceptional";
                case 4: return "epic";
                case 5: return "legendary";
                case 6: return "ascendant";
                default: return "?";
            }
        }

        void ShowLabel()
        {
            if (code == 0 || editor == null) return;
            OptionSide s = Side;
            int differ = 0;
            foreach (string st in stats) foreach (int g in grades) if (editor.Differs(Kind, code, g, st)) differ++;
            codeLabel.Text = "Option " + code + " (" + ItemOptionRules.KindName(Kind) + ")"
                + (s == null ? "" : !editor.Has(s, code) ? ", the " + s.Title + " has no row of it" : ", " + s.Title)
                + (differ == 0 ? "" : ", " + differ + " cell(s) differ between the sides");
        }

        string StatAt(int row) { return row >= 0 && row < stats.Count ? stats[row] : null; }

        int GradeAt(int column) { return column >= 1 && column <= grades.Count ? grades[column - 1] : 0; }

        OptionCell CellAt(int row, int column)
        {
            string st = StatAt(row);
            int g = GradeAt(column);
            OptionSide s = Side;
            if (st == null || g == 0 || s == null || editor == null || code == 0) return null;
            return editor.Cell(s, code, g, st);
        }

        void OnStatValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (editor == null || code == 0) return;
            if (e.RowIndex == stats.Count) { e.Value = e.ColumnIndex == 0 ? AddStatRow : ""; return; }
            string st = StatAt(e.RowIndex);
            if (st == null) return;
            if (e.ColumnIndex == 0)
            {
                bool differ = false;
                foreach (int g in grades) if (editor.Differs(Kind, code, g, st)) differ = true;
                e.Value = (differ ? "≠ " : "   ") + st;
                return;
            }
            OptionCell c = CellAt(e.RowIndex, e.ColumnIndex);
            if (c != null) e.Value = c.Value;
        }

        void OnStatFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (editor == null || code == 0) return;
            if (e.RowIndex == stats.Count) { e.CellStyle.ForeColor = SystemColors.GrayText; return; }
            string st = StatAt(e.RowIndex);
            if (st == null) return;
            if (e.ColumnIndex == 0)
            {
                e.CellStyle.BackColor = SystemColors.Control;
                return;
            }
            OptionCell c = CellAt(e.RowIndex, e.ColumnIndex);
            if (c == null) return;
            if (c.ReadOnly != null) { e.CellStyle.ForeColor = SystemColors.GrayText; e.CellStyle.BackColor = SystemColors.Control; }
            else if (editor.Differs(Kind, code, GradeAt(e.ColumnIndex), st)) e.CellStyle.BackColor = DiffBack;
            if (c.Edited) e.CellStyle.Font = bold.Of(statGrid.Font);
            // a kms rank serving more grades than this one: the other grades show the same rank in grey
            if (Side != null && Side.Kms && c.Grades.Count > 0 && c.Grades[0] != GradeAt(e.ColumnIndex)) e.CellStyle.ForeColor = SystemColors.GrayText;
        }

        void OnStatToolTip(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
        {
            if (editor == null || code == 0 || e.RowIndex < 0) return;
            if (e.RowIndex == stats.Count)
            {
                e.ToolTipText = "Type a stat name the side's files use (flat names: mar_value_base, pen_rate_base; the kms client takes the _value_base ones), then give it values";
                return;
            }
            string st = StatAt(e.RowIndex);
            int g = GradeAt(e.ColumnIndex);
            if (st == null) return;
            if (g == 0) { e.ToolTipText = st + (st == ItemOptionRules.Pick ? ": how many of the listed lines the server picks, \"min,max\"" : ""); return; }
            var sb = new StringBuilder();
            sb.Append(st).Append(", grade ").Append(g).Append(" (").Append(GradeName(g)).Append(')');
            foreach (OptionSide s in editor.Sides(Kind))
            {
                OptionCell c = editor.Cell(s, code, g, st);
                sb.Append("\n").Append(s.Title).Append(": ").Append(c.Value.Length == 0 ? "none" : c.Value);
                if (c.Edited) sb.Append(" (shipped ").Append(c.Shipped.Length == 0 ? "none" : c.Shipped).Append(')');
                if (c.Source.Length > 0) sb.Append(", ").Append(c.Source);
                if (c.ReadOnly != null) sb.Append("; ").Append(c.ReadOnly);
            }
            if (editor.Differs(Kind, code, g, st)) sb.Append("\nThe sides differ: an edit is written on the chosen side only.");
            e.ToolTipText = sb.ToString();
        }

        void OnStatBeginEdit(object sender, DataGridViewCellCancelEventArgs e)
        {
            if (editor == null || code == 0) { e.Cancel = true; return; }
            if (e.RowIndex == stats.Count) { if (e.ColumnIndex != 0) e.Cancel = true; return; }
            if (e.ColumnIndex == 0) { e.Cancel = true; return; }
            OptionCell c = CellAt(e.RowIndex, e.ColumnIndex);
            if (c == null || c.ReadOnly != null)
            {
                e.Cancel = true;
                if (c != null) host.SayOutside(c.ReadOnly);
            }
        }

        void OnStatValuePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            string v = ((e.Value as string) ?? "").Trim();
            int row = e.RowIndex, column = e.ColumnIndex;
            Guard(delegate
            {
                ItemOptionEditor ed = Editor();
                if (row == stats.Count)
                {
                    if (column != 0 || v.Length == 0 || v == AddStatRow) return;
                    OptionSide side = Side;
                    if (side == null) return;
                    if (!ed.StatNames(side).Contains(v)) throw new ModKitException("no shipped " + ItemOptionRules.KindName(Kind) + " file of the " + side.Title + " has the stat " + v);
                    if (stats.Contains(v)) return;
                    extraStats.Add(v);
                    ShowCode(code);
                    host.SayOutside("stat " + v + " added to the grid; give it values in the grades (an empty cell writes nothing)");
                    return;
                }
                string st = StatAt(row);
                int g = GradeAt(column);
                OptionSide s = Side;
                if (st == null || g == 0 || s == null) return;
                OptionCell c = ed.Cell(s, code, g, st);
                if (ItemOptionRules.Normal(v) == ItemOptionRules.Normal(c.Value)) return;
                List<string> said = ed.Set(s, code, g, st, v);
                host.EditedOutside();
                long keep = code;
                FillHits(keep);
                ShowCode(keep);
                host.SayOutside(string.Join("\r\n", said.ToArray()));
            });
        }

        // ---- screen 3: the random detail ----

        int DetailGrade()
        {
            int g = statGrid.CurrentCell == null ? 0 : GradeAt(statGrid.CurrentCell.ColumnIndex);
            return g != 0 ? g : grades.Count > 0 ? grades[grades.Count - 1] : 0;
        }

        // the detail takes its share of the code's side, as the window is sized
        void SizeDetail(Control right)
        {
            if (detail.Visible) detail.Height = Math.Max(150, (int)(right.Height * 0.42));
        }

        void FillDetail()
        {
            bool was = filling;
            filling = true;
            try
            {
                lineGrid.CancelEdit();
                lineGrid.Rows.Clear();
                bool on = code != 0 && editor != null && Kind == OptionKind.Random && editor.KmsRandom;
                detail.Visible = detailSplit.Visible = on;
                if (!on) return;
                KmsRandomRow row = editor.KmsRandomOf(code);
                OptionSide flat = null;
                foreach (OptionSide s in editor.Sides(Kind)) if (!s.Kms && (flat == null || s.Title == "server")) flat = s;
                if (flat != null && !editor.Has(flat, code)) flat = null;
                var shown = new List<int>(grades);
                if (shown.Count == 0) shown.AddRange(new[] { 1, 2, 3, 4 });
                var sb = new StringBuilder(" Lines an item gets: ");
                if (flat == null) sb.Append("the server has no row of this code");
                else
                {
                    sb.Append(flat.Title).Append(" (optionNumPick min,max)");
                    foreach (int g in shown)
                    {
                        string v = editor.Cell(flat, code, g, ItemOptionRules.Pick).Value;
                        sb.Append("  ").Append(GradeName(g)).Append(' ').Append(v.Length == 0 ? "none" : v);
                    }
                }
                sb.Append("\r\n ");
                if (row == null) sb.Append("The kms client's table has no row of this code.");
                else
                {
                    sb.Append("kms client (pickCount").Append(row.Attr("isRarePickOne") == "1" ? ", isRarePickOne" : "").Append(')');
                    foreach (int g in shown) sb.Append("  ").Append(GradeName(g)).Append(' ').Append(row.PickCount(g));
                    sb.Append(row.Edited ? "   (row edited by the mod)" : "").Append("   The server ignores the weights.");
                }
                detailLabel.Text = sb.ToString();
                foreach (KeyValuePair<string, TextBox> b in attrBoxes)
                {
                    b.Value.Text = row == null ? "" : row.Attr(b.Key) ?? "";
                    b.Value.Enabled = row != null;
                }
                detailGrade = DetailGrade();
                lineGrid.Columns[4].HeaderText = "server min,max" + (detailGrade == 0 ? "" : " (" + detailGrade + " " + GradeName(detailGrade) + ")");
                if (row == null) return;
                foreach (KmsRandomLine l in row.Lines)
                {
                    string id = l.StatId ?? "";
                    lineGrid.Rows.Add(l.Name, id, l.Weight ?? "", l.HasRange ? l.RangeText : id.Length == 0 ? "" : "no variation row", ServerRange(flat, l.Name));
                }
            }
            finally { filling = was; }
        }

        // the server's min,max of a kms line's stat at the detail's grade ("value 3,5", "rate 0.01,0.02")
        string ServerRange(OptionSide flat, string name)
        {
            if (flat == null || detailGrade == 0) return "";
            var parts = new List<string>();
            foreach (string kind in new[] { "value", "rate" })
            {
                string st = name + "_" + kind + "_base";
                if (!stats.Contains(st)) continue;
                string v = editor.Cell(flat, code, detailGrade, st).Value;
                if (v.Length > 0) parts.Add(kind + " " + v);
            }
            return parts.Count == 0 ? "not in the server's row" : string.Join("; ", parts.ToArray());
        }

        // after an edit of the kms row: the list, the grid and the detail again
        void AfterKms(string said)
        {
            host.EditedOutside();
            long keep = code;
            FillHits(keep);
            ShowCode(keep);
            host.SayOutside(said);
        }

        void SetAttr(string name, TextBox box)
        {
            if (code == 0 || !detail.Visible) return;
            ItemOptionEditor ed = Editor();
            KmsRandomRow row = ed.KmsRandomOf(code);
            if (row == null) return;
            string was = row.Attr(name) ?? "", v = box.Text.Trim();
            if (v == was) return;
            try { AfterKms(ed.SetKmsRandom(code, name, v)); }
            catch (ModKitException) { box.Text = was; throw; }
        }

        // the write is done here; the grids are filled again after the grid has left the cell
        void OnLineEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (filling || e.RowIndex < 0 || (e.ColumnIndex != 1 && e.ColumnIndex != 2)) return;
            int r = e.RowIndex, c = e.ColumnIndex;
            string v = (Convert.ToString(lineGrid.Rows[r].Cells[c].Value, CultureInfo.InvariantCulture) ?? "").Trim();
            string said = null;
            bool changed = false;
            Guard(delegate
            {
                ItemOptionEditor ed = Editor();
                KmsRandomRow row = ed.KmsRandomOf(code);
                if (row == null || r >= row.Lines.Count) return;
                string old = (c == 1 ? row.Lines[r].StatId : row.Lines[r].Weight) ?? "";
                if (v == old) return;
                changed = true;
                said = ed.SetKmsRandomLine(code, r, c == 1 ? "statID" : "weight", v);
                host.EditedOutside();
            });
            if (!changed) return;
            BeginInvoke((MethodInvoker)delegate
            {
                // a refused value: the line as it is comes back, the message stays
                if (said == null) { Guard(delegate { FillDetail(); }); return; }
                Guard(delegate { long keep = code; FillHits(keep); ShowCode(keep); host.SayOutside(said); });
            });
        }

        void AddLine()
        {
            if (code == 0) throw new ModKitException("choose a code first");
            EndEdits();
            AfterKms(Editor().AddKmsRandomLine(code, addName.Text.Trim(), addId.Text.Trim(), addWeight.Text.Trim()));
            addName.Text = addId.Text = addWeight.Text = "";
        }

        void RemoveLine()
        {
            if (code == 0) throw new ModKitException("choose a code first");
            EndEdits();
            if (lineGrid.CurrentCell == null) throw new ModKitException("choose the line to remove first");
            AfterKms(Editor().RemoveKmsRandomLine(code, lineGrid.CurrentCell.RowIndex));
        }

        // ---- revert, fork ----

        void RevertCode()
        {
            if (code == 0) throw new ModKitException("choose a code first");
            EndEdits();
            long c = code;
            int n = Editor().Revert(Kind, c);
            host.EditedOutside();
            FillHits(c);
            ShowCode(Shown(c) ? c : 0);
            host.SayOutside(n == 0 ? "the mod has no edit of option " + c : "option " + c + ": " + n + " edit(s) of the mod taken back");
        }

        void Fork()
        {
            if (code == 0) throw new ModKitException("choose the code to copy first");
            EndEdits();
            ItemOptionEditor ed = Editor();
            long to;
            string text = newCodeBox.Text.Trim();
            if (text.Length > 0)
            {
                if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out to)) throw new ModKitException("\"" + text + "\" is not a code");
            }
            else
            {
                List<long> free = ed.NextCodes(1);
                if (free.Count == 0) throw new ModKitException("no free code is left in " + ItemOptionRules.From + "-" + ItemOptionRules.To);
                to = free[0];
            }
            string bad = ed.CheckNewCode(to);
            if (bad != null) throw new ModKitException(bad);
            var chosen = new List<long>();
            foreach (int i in itemList.SelectedIndices) if (i >= 0 && i < codeItems.Count) chosen.Add(codeItems[i]);
            if (chosen.Count == 0) chosen.AddRange(codeItems);
            if (chosen.Count > 0)
            {
                var ids = new List<string>();
                for (int i = 0; i < chosen.Count && i < 8; i++) ids.Add(chosen[i].ToString(CultureInfo.InvariantCulture));
                string question = "Copy option " + code + " (" + ItemOptionRules.KindName(Kind) + ") to the new code " + to
                    + " and point the items at it?\n\nThis changes " + chosen.Count + " item(s): " + string.Join(", ", ids.ToArray()) + (chosen.Count > 8 ? ", ..." : "")
                    + "\n\nThe other items naming option " + code + " keep it.";
                if (MessageBox.Show(this, question, "MS2 Item options", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            }
            long from = code;
            int n = ed.Fork(Kind, from, to, chosen);
            host.EditedOutside();
            newCodeBox.Text = "";
            searchBox.Text = "";
            itemNames.Clear();
            FillHits(to);
            ShowCode(to);
            host.SayOutside("option " + from + " copied to " + to + " on every side that has it; " + n + " item(s) point at " + to + " now. Save mod, then Plan checks it per target.");
        }
    }
}
