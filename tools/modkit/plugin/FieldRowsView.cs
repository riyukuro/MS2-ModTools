// The "MS2 Field rows" view: opens the maps our client has but cannot load (MS2.ModKit.FieldRows). The client loads
// a map only when its table/fielddata.xml row has an environment with an xblock name; Scan lists the maps whose row
// is empty (Fill) or missing (Add) while the source client has the map's own file map/<8-digit id>.xml, and builds
// the compact row from it. Maps it cannot fill are listed too, unticked: several environments for our locale, no
// xblock file of ours, no xblock name, a file that does not parse.
//
// A row starts ticked when the server's pack has the map and the map is not behind the Develop feature (the
// client's dev and test maps). The feature is not written into the row: a map behind a feature our client has off
// opens anyway; the columns say which feature and whether it is on.
//
// "Put into mod" writes the ticked rows into the mod being edited as row ops on table/fielddata.xml (sync="false");
// "Take out" removes them. Plan, Apply and Save mod are the MS2 Tables view's, as for the other entity views.
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
    public class FieldRowsView : AbstractView
    {
        public const string ViewId = "MS2.Tables.FieldRowsView";

        TablesView host;
        FieldRows scan;
        EditorSession scanSession;
        Mod scanMod;
        bool filling;
        // ticked rows by map id: a rescan makes new FieldRow objects for the same maps
        readonly HashSet<int> chosen = new HashSet<int>();

        readonly TextBox targetBox = new TextBox { Width = 130, Text = "client:Xml.m2d" };
        readonly TextBox sourceBox = new TextBox { Width = 150, Text = "gms-client:Xml.m2d" };
        readonly TextBox serverBox = new TextBox { Width = 130, Text = "server:Xml.m2d" };
        readonly TextBox localeBox = new TextBox { Width = 36, Text = "KR" };
        readonly Label totalLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListView rowList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false, CheckBoxes = true };
        readonly TextBox findBox = new TextBox { Width = 160 };
        readonly CheckBox fillableOnlyBox = new CheckBox { Text = "fillable only", AutoSize = true, Margin = new Padding(8, 6, 3, 0) };
        readonly Label rowsLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 8, 0) };
        readonly TextBox previewBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 130 };
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();

        static readonly string[] Columns = { "id", "kind", "xblock", "feature", "on", "level", "on server", "attrs", "in mod", "row" };
        static readonly int[] Widths = { 80, 70, 160, 170, 50, 50, 70, 50, 60, 200 };

        public FieldRowsView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;
            previewBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            previewBox.BackColor = SystemColors.Window;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(Lbl("Ours"));
            top.Controls.Add(targetBox);
            tips.SetToolTip(targetBox, "The archive whose table/fielddata.xml is filled (client:Xml.m2d, the kms client); its xblock files are looked for in Resource/Exported.m2d next to it");
            top.Controls.Add(Lbl("from"));
            top.Controls.Add(sourceBox);
            tips.SetToolTip(sourceBox, "The archive with the maps' own files map/<8-digit id>.xml (gms-client:Xml.m2d, the global client)");
            top.Controls.Add(Lbl("server"));
            top.Controls.Add(serverBox);
            tips.SetToolTip(serverBox, "The server's pack: a map it has no map file for is not ticked (leave empty, or a data set without it, to skip the check)");
            top.Controls.Add(Lbl("locale"));
            top.Controls.Add(localeBox);
            tips.SetToolTip(localeBox, "Our locale: the source environments for another locale are skipped, and our feature tables are read for it");
            top.Controls.Add(Btn("Scan", "Lists the maps our table has no loadable row for while the source has the map's file (writes nothing)", delegate { Scan(); }));
            top.Controls.Add(totalLabel);

            FlowLayoutPanel modBar = Bar();
            modBar.Controls.Add(Btn("Put into mod", "Writes the ticked rows into the mod being edited: a row op on table/fielddata.xml per map (sync=\"false\"); a row the mod has already is not written again", delegate { Put(); }));
            modBar.Controls.Add(Btn("Take out", "Takes the rows of the scan out of the mod being edited", delegate { Take(); }));
            modBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change; writes nothing", delegate { return host.DoPlan(); }));
            modBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change (the MS2 Tables view's Apply)", delegate { return host.DoApply(); }));
            modBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            for (int i = 0; i < Columns.Length; i++)
                rowList.Columns.Add(Columns[i], Widths[i], i == 0 || (i >= 4 && i <= 8) ? HorizontalAlignment.Right : HorizontalAlignment.Left);
            rowList.ShowItemToolTips = true;
            tips.SetToolTip(rowList, "Fill: our row is empty; Add: we have no row; FillBeside: our row has an environment without xblock name, and the client's pick of the two is not checked (not ticked by default). The other kinds cannot be filled. Choose a row to see what Put writes");

            FlowLayoutPanel rowBar = Bar();
            rowBar.Controls.Add(Lbl("Find"));
            rowBar.Controls.Add(findBox);
            tips.SetToolTip(findBox, "Shows the rows whose id, xblock or feature has this text");
            rowBar.Controls.Add(fillableOnlyBox);
            tips.SetToolTip(fillableOnlyBox, "Shows only the rows Put can write (Fill, Add, FillBeside)");
            rowBar.Controls.Add(Btn("Tick shown", "Ticks the Fill, Add and FillBeside rows shown", delegate { TickShown(true); }));
            rowBar.Controls.Add(Btn("Untick shown", "Unticks the rows shown", delegate { TickShown(false); }));
            rowBar.Controls.Add(Btn("Default ticks", "Ticks the rows the scan ticks: the server has the map and it is not a Develop map", delegate { DefaultTicks(); }));
            rowBar.Controls.Add(rowsLabel);

            var previewPanel = new Panel { Dock = DockStyle.Bottom, Height = 200 };
            previewPanel.Controls.Add(previewBox);
            tips.SetToolTip(previewBox, "What Put writes for the chosen row");

            var listPanel = new Panel { Dock = DockStyle.Fill };
            listPanel.Controls.Add(rowList);
            listPanel.Controls.Add(rowBar);

            var body = new Panel { Dock = DockStyle.Fill };
            body.Controls.Add(listPanel);
            body.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            body.Controls.Add(previewPanel);

            Controls.Add(body);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(outputBox);
            Controls.Add(modBar);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, modBar, body });
            ViewLayout.KeepLogLines(outputBox, this);
            ViewLayout.KeepHeightShare(previewPanel, body, 0.35, 80);
            ViewLayout.CentreOnFirstShow(this);

            rowList.ItemChecked += delegate(object sender, ItemCheckedEventArgs e) { if (!filling) TickRow(e.Item); };
            rowList.SelectedIndexChanged += delegate { if (!filling) ShowPreview(); };
            fillableOnlyBox.CheckedChanged += delegate { if (!filling) ShowRows(); };
            findBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                ShowRows();
            };
            Resize += delegate { FitColumns(); };
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
                Logger.Warning("MS2 Field rows: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        void OnHostSaid(string text) { OutputWrap.Set(outputBox, text); }

        void OnHostBusy(bool on) { ViewLayout.Lock(this, lockable, on, outputBox); }

        /// <summary>Another session or mod: the scan was of the old one.</summary>
        void OnHostChanged()
        {
            EditorSession s = host.SharedSession();
            if (scan != null && (s != scanSession || s.Mod != scanMod)) Clear("the session or the mod changed: scan again");
            else if (scan != null) ShowRows();
        }

        void Clear(string why)
        {
            scan = null;
            chosen.Clear();
            filling = true;
            try { rowList.Items.Clear(); previewBox.Text = ""; }
            finally { filling = false; }
            totalLabel.Text = why;
            rowsLabel.Text = "";
        }

        // ---- scan ----

        void Scan()
        {
            EditorSession s = host.SharedSession();
            string target = targetBox.Text.Trim(), source = sourceBox.Text.Trim(), server = serverBox.Text.Trim(), locale = localeBox.Text.Trim();
            if (target.Length == 0 || source.Length == 0 || locale.Length == 0) throw new ModKitException("give our archive, the source archive and the locale");
            FieldRows found = null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            host.RunOutside("field row scan", delegate { found = FieldRows.Scan(s.Data, target, source, server.Length == 0 ? null : server, locale); }, delegate
            {
                bool again = scan != null && s == scanSession && s.Mod == scanMod;
                scan = found;
                scanSession = s;
                scanMod = s.Mod;
                // a rescan of the same session keeps the ticks of the maps it lists again; a first scan takes the defaults
                var keep = new HashSet<int>(chosen);
                chosen.Clear();
                foreach (FieldRow r in scan.Rows)
                    if (r.Ticked && (again ? keep.Contains(r.Id) : r.Default)) chosen.Add(r.Id);
                ShowRows();
                host.SayOutside(scan.Summary("\r\n") + " (" + (watch.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s)");
            });
        }

        static string N(int n) { return n.ToString("N0", CultureInfo.InvariantCulture); }

        // ---- the rows ----

        static string YesNo(bool? b) { return b == null ? "" : b.Value ? "yes" : "no"; }

        void ShowRows()
        {
            int keepId = rowList.SelectedItems.Count > 0 ? ((FieldRow)rowList.SelectedItems[0].Tag).Id : -1;
            string find = findBox.Text.Trim();
            Mod mod = host == null ? null : host.SharedSession().Mod;
            int total = 0;
            filling = true;
            try
            {
                rowList.BeginUpdate();
                rowList.Items.Clear();
                if (scan != null)
                {
                    foreach (FieldRow r in scan.Rows)
                    {
                        if (fillableOnlyBox.Checked && !r.Ticked) continue;
                        string id = r.Id.ToString(CultureInfo.InvariantCulture);
                        if (find.Length > 0 && !Has(id, find) && !Has(r.XBlock, find) && !Has(r.Feature, find)) continue;
                        total++;
                        var it = new ListViewItem(id) { Tag = r, Checked = r.Ticked && chosen.Contains(r.Id) };
                        it.SubItems.Add(r.Kind.ToString());
                        it.SubItems.Add(r.XBlock ?? "");
                        it.SubItems.Add(r.Feature ?? "");
                        it.SubItems.Add(r.Feature == null || r.Feature.Length == 0 ? "" : YesNo(r.FeatureOn));
                        it.SubItems.Add(r.Feature == null || r.Feature.Length == 0 || r.FeatureOn == null ? "" : r.FeatureLevel.ToString(CultureInfo.InvariantCulture));
                        it.SubItems.Add(YesNo(r.OnServer));
                        it.SubItems.Add(r.Xml == null ? "" : r.Attrs.ToString(CultureInfo.InvariantCulture));
                        it.SubItems.Add(mod != null && r.Xml != null && scan.InMod(mod, new[] { r }) > 0 ? "yes" : "");
                        it.SubItems.Add(r.Fills ? r.At : r.Kind == FieldRowKind.Add ? (r.After != null ? "after " + r.After : "before " + r.Before) : "");
                        if (!r.Ticked) it.ForeColor = r.Kind == FieldRowKind.Error ? Color.Firebrick : SystemColors.GrayText;
                        else if (r.FeatureOn == false) it.ForeColor = Color.DarkOrange;
                        it.ToolTipText = Tip(r);
                        rowList.Items.Add(it);
                        if (r.Id == keepId) it.Selected = true;
                    }
                }
            }
            finally { rowList.EndUpdate(); filling = false; }
            FitColumns();
            rowsLabel.Text = scan == null ? "" : N(total) + " rows shown";
            ShowTotal();
            ShowPreview();
        }

        static string Tip(FieldRow r)
        {
            switch (r.Kind)
            {
                case FieldRowKind.Fill: return "our row is empty: the environment goes into it" + FeatureTip(r);
                case FieldRowKind.FillBeside: return "our row has an environment already, none with an xblock name (one behind a feature, say): the environment goes in beside it. "
                    + "Which of the two the client takes is not checked, so the map may stay closed; not ticked by default" + FeatureTip(r);
                case FieldRowKind.Add: return "we have no row: a new row goes " + (r.After != null ? "after " + r.After : "before " + r.Before) + FeatureTip(r);
                case FieldRowKind.Several: return "the source has " + r.Environments + " environments for our locale: not filled";
                case FieldRowKind.NoMap: return "our client has no xblock file " + r.XBlock + ": not filled";
                case FieldRowKind.NoXBlock: return "the source environment has no xblock name: not filled";
                default: return "the source file cannot be read: " + r.Error;
            }
        }

        static string FeatureTip(FieldRow r)
        {
            if (r.Feature == null || r.Feature.Length == 0) return "";
            if (r.FeatureOn == false) return "; the source puts it behind feature " + r.Feature + ", which our client has off (level " + r.FeatureLevel + "): the row is written without it, so the map opens anyway";
            return "; the source puts it behind feature " + r.Feature + " (not written)";
        }

        static bool Has(string s, string find) { return s != null && s.IndexOf(find, StringComparison.OrdinalIgnoreCase) >= 0; }

        void ShowPreview()
        {
            if (rowList.SelectedItems.Count == 0) { previewBox.Text = ""; return; }
            var r = (FieldRow)rowList.SelectedItems[0].Tag;
            string head = r.File + ": " + Tip(r);
            previewBox.Text = r.Xml == null ? head : head + "\r\n\r\n" + r.Xml.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        void ShowTotal()
        {
            if (scan == null) { totalLabel.Text = ""; return; }
            int n = 0;
            foreach (FieldRow r in scan.Rows) if (r.Ticked && chosen.Contains(r.Id)) n++;
            totalLabel.Text = "ticked: " + N(n) + " maps";
        }

        void TickRow(ListViewItem it)
        {
            var r = (FieldRow)it.Tag;
            if (!r.Ticked)
            {
                // a row that cannot be filled stays unticked
                filling = true;
                try { it.Checked = false; } finally { filling = false; }
                return;
            }
            if (it.Checked) chosen.Add(r.Id); else chosen.Remove(r.Id);
            ShowTotal();
        }

        void TickShown(bool on)
        {
            if (scan == null) return;
            filling = true;
            try
            {
                foreach (ListViewItem it in rowList.Items)
                {
                    var r = (FieldRow)it.Tag;
                    if (!r.Ticked) continue;
                    it.Checked = on;
                    if (on) chosen.Add(r.Id); else chosen.Remove(r.Id);
                }
            }
            finally { filling = false; }
            ShowTotal();
        }

        void DefaultTicks()
        {
            if (scan == null) return;
            chosen.Clear();
            foreach (FieldRow r in scan.Rows) if (r.Default) chosen.Add(r.Id);
            ShowRows();
        }

        // ---- the mod ----

        List<FieldRow> Ticked()
        {
            return scan.Rows.FindAll(delegate(FieldRow r) { return r.Ticked && chosen.Contains(r.Id); });
        }

        void NeedScan()
        {
            EditorSession s = host.SharedSession();
            if (scan == null) throw new ModKitException("scan first");
            if (s != scanSession || s.Mod != scanMod) { Clear("the session or the mod changed: scan again"); throw new ModKitException("the session or the mod changed since the scan: scan again"); }
            if (s.Mod == null) throw new ModKitException("no mod is being edited");
        }

        void Put()
        {
            NeedScan();
            Mod mod = host.SharedSession().Mod;
            List<FieldRow> rows = Ticked();
            int had = scan.InMod(mod, rows);
            int n = scan.Put(mod, rows);
            host.EditedOutside();
            ShowRows();
            host.SayOutside("put " + N(n) + " field rows into mod " + mod.Id + " (" + N(rows.Count) + " ticked, " + N(had) + " in the mod already); Plan shows them, Save mod keeps them");
        }

        void Take()
        {
            NeedScan();
            Mod mod = host.SharedSession().Mod;
            int n = scan.Take(mod, scan.Rows);
            host.EditedOutside();
            ShowRows();
            host.SayOutside("took " + N(n) + " field rows out of mod " + mod.Id);
        }

        // ---- layout ----

        void FitColumns()
        {
            if (rowList.Columns.Count < Columns.Length) return;
            int used = 0;
            for (int i = 0; i < Columns.Length - 1; i++) used += rowList.Columns[i].Width;
            int w = rowList.ClientSize.Width - used - 4;
            rowList.Columns[Columns.Length - 1].Width = w < 200 ? 200 : w;
        }
    }
}
