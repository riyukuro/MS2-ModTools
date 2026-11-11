// The "MS2 Text import" view: fills our string rows that are still Korean from another client's string files
// (MS2.ModKit.TextImport). Scan compares our archive and language (client:Xml.m2d, kr) with the source's
// (gms-client:Xml.m2d, en) file by file; the list of files says, per file, how many attributes would be filled and
// why the others are not (the same text, no source row, a placeholder, other {n} slots, ...).
//
// The rows of the chosen file are listed below with a tick each: a fill (our text is Korean, the source's is not)
// starts ticked, a plainly different row (both texts are not Korean and share few words) starts unticked; such a row
// is only taken when ticked by hand. An English row is never changed by a fill. The tick of a file ticks or
// unticks all its fills.
//
// "Put into mod" writes the ticked rows into the mod being edited as attribute sets (sync="false"); "Take out"
// removes the sets that still have the source text. Plan, Apply and Save mod are the MS2 Tables view's, as for the
// other entity views; the session, the worker thread and the output stay in that view.
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
    public class TextImportView : AbstractView
    {
        public const string ViewId = "MS2.Tables.TextImportView";
        const int MaxRows = 3000;

        TablesView host;
        TextImport scan;
        EditorSession scanSession;
        bool filling;
        // the rows that are ticked (a fill starts ticked, a plain row unticked)
        readonly HashSet<TextRow> chosen = new HashSet<TextRow>();
        readonly Dictionary<string, List<TextRow>> rowsOf = new Dictionary<string, List<TextRow>>(StringComparer.OrdinalIgnoreCase);

        readonly TextBox targetBox = new TextBox { Width = 130, Text = "client:Xml.m2d" };
        readonly TextBox targetLangBox = new TextBox { Width = 36, Text = "kr" };
        readonly TextBox sourceBox = new TextBox { Width = 150, Text = "gms-client:Xml.m2d" };
        readonly TextBox sourceLangBox = new TextBox { Width = 36, Text = "en" };
        readonly Label totalLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListView fileList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, CheckBoxes = true };
        readonly ListView rowList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false, CheckBoxes = true };
        readonly TextBox findBox = new TextBox { Width = 160 };
        readonly CheckBox plainOnlyBox = new CheckBox { Text = "plainly different only", AutoSize = true, Margin = new Padding(8, 6, 3, 0) };
        readonly Label rowsLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 8, 0) };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 130 };
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();

        static readonly string[] FileColumns = { "file", "source files", "keys", "in source", "fill", "plain", "near", "same", "korean", "placeholder", "form", "missing", "ambiguous", "kept", "in mod" };
        static readonly string[] FileTips =
        {
            "our file (string/<language>/<file>)",
            "the source's files for it: the same name, or the files of its family",
            "keys (ids) of ours",
            "of them, keys the source has",
            "attributes to fill: ours is Korean, the source's is not",
            "plainly different: both are not Korean and share few words (taken only when ticked)",
            "both are not Korean and close: left as ours has it",
            "the same text",
            "the source text is Korean too",
            "the source text is a placeholder name (A_B_C): not taken",
            "the source text has other {n} slots than ours: not taken",
            "the source row has no text for the attribute",
            "the source has more rows for the key that disagree",
            "the mod sets the attribute to another text already: left as the mod has it",
            "the fills the mod has already, as Put wrote them",
        };

        public TextImportView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(Lbl("Ours"));
            top.Controls.Add(targetBox);
            tips.SetToolTip(targetBox, "The archive whose string files are filled (client:Xml.m2d, the kms client)");
            top.Controls.Add(targetLangBox);
            tips.SetToolTip(targetLangBox, "Our language folder: string/<language>/");
            top.Controls.Add(Lbl("from"));
            top.Controls.Add(sourceBox);
            tips.SetToolTip(sourceBox, "The archive the texts come from (gms-client:Xml.m2d, the global client)");
            top.Controls.Add(sourceLangBox);
            tips.SetToolTip(sourceLangBox, "The source's language folder: string/<language>/");
            top.Controls.Add(Btn("Scan", "Compares our string files with the source's, file by file (writes nothing)", delegate { Scan(); }));
            top.Controls.Add(totalLabel);

            FlowLayoutPanel modBar = Bar();
            modBar.Controls.Add(Btn("Put into mod", "Writes the ticked rows of every file into the mod being edited: an attribute set per row (sync=\"false\"); an attribute the mod sets already is left as it is", delegate { Put(); }));
            modBar.Controls.Add(Btn("Take out", "Takes the rows of the scan out of the mod being edited: the attribute sets that still have the source text", delegate { Take(); }));
            modBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change; writes nothing", delegate { return host.DoPlan(); }));
            modBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change (the MS2 Tables view's Apply)", delegate { return host.DoApply(); }));
            modBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            // at 1280 wide all the columns fit: the counts 58, the three longer headers as wide as their text
            for (int i = 0; i < FileColumns.Length; i++)
                fileList.Columns.Add(FileColumns[i], i == 0 ? 210 : i == 1 ? 160 : FileColumns[i].Length > 8 ? 78 : 58, i < 2 ? HorizontalAlignment.Left : HorizontalAlignment.Right);
            fileList.ShowItemToolTips = true;
            tips.SetToolTip(fileList, "The tick of a file ticks or unticks all its fills; choose a file to see its rows below");

            rowList.Columns.Add("kind", 60);
            rowList.Columns.Add("id", 90);
            rowList.Columns.Add("attribute", 90);
            rowList.Columns.Add("ours", 330);
            rowList.Columns.Add("source", 330);
            rowList.Columns.Add("row", 200);

            FlowLayoutPanel rowBar = Bar();
            rowBar.Controls.Add(Lbl("Find"));
            rowBar.Controls.Add(findBox);
            tips.SetToolTip(findBox, "Shows the rows whose id or texts have this text");
            rowBar.Controls.Add(plainOnlyBox);
            tips.SetToolTip(plainOnlyBox, "Shows only the plainly different rows of the chosen file");
            rowBar.Controls.Add(Btn("Tick shown", "Ticks the rows shown", delegate { TickShown(true); }));
            rowBar.Controls.Add(Btn("Untick shown", "Unticks the rows shown", delegate { TickShown(false); }));
            rowBar.Controls.Add(rowsLabel);

            var rowPanel = new Panel { Dock = DockStyle.Bottom, Height = 260 };
            rowPanel.Controls.Add(rowList);
            rowPanel.Controls.Add(rowBar);

            var body = new Panel { Dock = DockStyle.Fill };
            body.Controls.Add(fileList);
            body.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            body.Controls.Add(rowPanel);

            Controls.Add(body);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(outputBox);
            Controls.Add(modBar);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, modBar, body });
            ViewLayout.KeepLogLines(outputBox, this);
            ViewLayout.KeepHeightShare(rowPanel, body, 0.55, 120);
            ViewLayout.CentreOnFirstShow(this);

            fileList.SelectedIndexChanged += delegate { if (!filling) ShowRows(); };
            fileList.ItemChecked += delegate(object sender, ItemCheckedEventArgs e) { if (!filling) TickFile(e.Item); };
            rowList.ItemChecked += delegate(object sender, ItemCheckedEventArgs e) { if (!filling) TickRow(e.Item); };
            plainOnlyBox.CheckedChanged += delegate { if (!filling) ShowRows(); };
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
                if (h == null || h.IsDisposed)
                {
                    h = TablesView.OpenBehind();
                }
                if (h == null) { OutputWrap.Set(outputBox, "the MS2 Tables view could not be opened; this view works on the data of that view"); return false; }
                Hook(h);
            }
            catch (Exception ex)
            {
                OutputWrap.Set(outputBox, "the MS2 Tables view could not be opened: " + ex.Message);
                Logger.Warning("MS2 Text import: " + ex.Message);
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
            else if (scan != null) FillFiles();
        }

        Mod scanMod;

        void Clear(string why)
        {
            scan = null;
            chosen.Clear();
            rowsOf.Clear();
            filling = true;
            try { fileList.Items.Clear(); rowList.Items.Clear(); }
            finally { filling = false; }
            totalLabel.Text = why;
            rowsLabel.Text = "";
        }

        // ---- scan ----

        void Scan()
        {
            EditorSession s = host.SharedSession();
            string target = targetBox.Text.Trim(), tl = targetLangBox.Text.Trim(), source = sourceBox.Text.Trim(), sl = sourceLangBox.Text.Trim();
            if (target.Length == 0 || tl.Length == 0 || source.Length == 0 || sl.Length == 0) throw new ModKitException("give both archives and both languages");
            TextImport found = null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            host.RunOutside("text scan", delegate { found = TextImport.Scan(s.Data, s.Mod, target, tl, source, sl); }, delegate
            {
                scan = found;
                scanSession = s;
                scanMod = s.Mod;
                chosen.Clear();
                rowsOf.Clear();
                foreach (TextRow r in scan.Rows)
                {
                    List<TextRow> list;
                    if (!rowsOf.TryGetValue(r.File, out list)) rowsOf[r.File] = list = new List<TextRow>();
                    list.Add(r);
                    if (r.Kind == TextKind.Fill) chosen.Add(r);
                }
                FillFiles();
                if (fileList.Items.Count > 0) fileList.Items[0].Selected = true;
                host.SayOutside(Summary() + " (" + (watch.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s)");
            });
        }

        static string N(int n) { return n.ToString("N0", CultureInfo.InvariantCulture); }

        string Summary()
        {
            int fill = 0, plain = 0, errors = 0;
            foreach (TextRow r in scan.Rows) { if (r.Kind == TextKind.Fill) fill++; else plain++; }
            foreach (TextFileStat f in scan.Files) if (f.Error != null) errors++;
            string text = "text scan of " + scan.Target + " string/" + scan.TargetLang + " from " + scan.Source + " string/" + scan.SourceLang + ": "
                + scan.Files.Count + " files, " + N(fill) + " attributes to fill, " + N(plain) + " plainly different (taken only when ticked)";
            if (errors > 0)
            {
                text += "; " + errors + " files could not be read:";
                foreach (TextFileStat f in scan.Files) if (f.Error != null) text += "\r\n  " + f.File + ": " + f.Error;
            }
            return text;
        }

        void FillFiles()
        {
            string keep = fileList.SelectedItems.Count > 0 ? (string)fileList.SelectedItems[0].Tag : null;
            Mod mod = host.SharedSession().Mod;
            filling = true;
            try
            {
                fileList.BeginUpdate();
                fileList.Items.Clear();
                foreach (TextFileStat f in scan.Files)
                {
                    List<TextRow> rows;
                    rowsOf.TryGetValue(f.File, out rows);
                    int inMod = rows == null || mod == null ? 0 : scan.InMod(mod, rows.FindAll(delegate(TextRow r) { return r.Kind == TextKind.Fill; }));
                    var it = new ListViewItem(f.File) { Tag = f.File };
                    it.SubItems.Add(f.Error != null ? "cannot read: " + f.Error : string.Join(", ", f.SourceFiles.ToArray()));
                    foreach (int v in new[] { f.Keys, f.Has, f.Fill, f.Plain, f.Near, f.Same, f.Korean, f.Placeholder, f.Form, f.Missing, f.Ambiguous, f.Kept, inMod })
                        it.SubItems.Add(N(v));
                    it.Checked = f.Fill > 0 && AllFillsChosen(rows);
                    if (f.Error != null) it.ForeColor = Color.Firebrick;
                    else if (f.Fill == 0 && f.Plain == 0) it.ForeColor = SystemColors.GrayText;
                    it.ToolTipText = f.SourceFiles.Count == 0 ? "the source has no file for it" : "from " + string.Join(", ", f.SourceFiles.ToArray());
                    fileList.Items.Add(it);
                    if (f.File == keep) it.Selected = true;
                }
            }
            finally { fileList.EndUpdate(); filling = false; }
            FitColumns();
            ShowTotal();
        }

        bool AllFillsChosen(List<TextRow> rows)
        {
            if (rows == null) return false;
            foreach (TextRow r in rows) if (r.Kind == TextKind.Fill && !chosen.Contains(r)) return false;
            return true;
        }

        void ShowTotal()
        {
            if (scan == null) { totalLabel.Text = ""; return; }
            int fill = 0, plain = 0;
            foreach (TextRow r in chosen) { if (r.Kind == TextKind.Fill) fill++; else plain++; }
            totalLabel.Text = "ticked: " + N(fill) + " fills, " + N(plain) + " plainly different";
        }

        // ---- the rows of the chosen file ----

        void ShowRows()
        {
            List<TextRow> rows = null;
            if (scan != null && fileList.SelectedItems.Count > 0) rowsOf.TryGetValue((string)fileList.SelectedItems[0].Tag, out rows);
            string find = findBox.Text.Trim();
            int total = 0, shown = 0;
            filling = true;
            try
            {
                rowList.BeginUpdate();
                rowList.Items.Clear();
                if (rows != null)
                {
                    // the plainly different rows first: they are the ones to choose
                    foreach (TextKind kind in new[] { TextKind.Plain, TextKind.Fill })
                    {
                        if (kind == TextKind.Fill && plainOnlyBox.Checked) continue;
                        foreach (TextRow r in rows)
                        {
                            if (r.Kind != kind) continue;
                            if (find.Length > 0 && !Has(r.Id, find) && !Has(r.Ours, find) && !Has(r.Source, find)) continue;
                            total++;
                            if (shown >= MaxRows) continue;
                            var it = new ListViewItem(r.Kind == TextKind.Fill ? "fill" : "plain") { Tag = r, Checked = chosen.Contains(r) };
                            it.SubItems.Add(r.Id);
                            it.SubItems.Add(r.Attr);
                            it.SubItems.Add(r.Ours);
                            it.SubItems.Add(r.Source);
                            it.SubItems.Add(r.At);
                            if (r.Kind == TextKind.Plain) it.ForeColor = Color.DarkOrange;
                            rowList.Items.Add(it);
                            shown++;
                        }
                    }
                }
            }
            finally { rowList.EndUpdate(); filling = false; }
            FitColumns();
            rowsLabel.Text = rows == null ? "" : shown < total ? N(shown) + " of " + N(total) + " rows shown (narrow them with Find)" : N(total) + " rows";
        }

        static bool Has(string s, string find) { return s != null && s.IndexOf(find, StringComparison.OrdinalIgnoreCase) >= 0; }

        void TickRow(ListViewItem it)
        {
            var r = (TextRow)it.Tag;
            if (it.Checked) chosen.Add(r); else chosen.Remove(r);
            SyncFileTick(r.File);
            ShowTotal();
        }

        void SyncFileTick(string file)
        {
            List<TextRow> rows;
            rowsOf.TryGetValue(file, out rows);
            filling = true;
            try
            {
                foreach (ListViewItem f in fileList.Items)
                    if ((string)f.Tag == file) { bool on = AllFillsChosen(rows) && rows.Exists(delegate(TextRow r) { return r.Kind == TextKind.Fill; }); if (f.Checked != on) f.Checked = on; }
            }
            finally { filling = false; }
        }

        void TickFile(ListViewItem f)
        {
            List<TextRow> rows;
            if (!rowsOf.TryGetValue((string)f.Tag, out rows) || !rows.Exists(delegate(TextRow r) { return r.Kind == TextKind.Fill; })) { filling = true; try { f.Checked = false; } finally { filling = false; } return; }
            foreach (TextRow r in rows)
                if (r.Kind == TextKind.Fill) { if (f.Checked) chosen.Add(r); else chosen.Remove(r); }
            if (fileList.SelectedItems.Count > 0 && fileList.SelectedItems[0] == f) ShowRows();
            ShowTotal();
        }

        void TickShown(bool on)
        {
            if (scan == null) return;
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            filling = true;
            try
            {
                foreach (ListViewItem it in rowList.Items)
                {
                    var r = (TextRow)it.Tag;
                    it.Checked = on;
                    if (on) chosen.Add(r); else chosen.Remove(r);
                    files.Add(r.File);
                }
            }
            finally { filling = false; }
            foreach (string f in files) SyncFileTick(f);
            ShowTotal();
        }

        // ---- the mod ----

        List<TextRow> Ticked()
        {
            var list = new List<TextRow>();
            foreach (TextRow r in scan.Rows) if (chosen.Contains(r)) list.Add(r);
            return list;
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
            List<TextRow> rows = Ticked();
            int had = scan.InMod(mod, rows);
            int n = scan.Put(mod, rows);
            host.EditedOutside();
            FillFiles();
            host.SayOutside("put " + N(n) + " attributes into mod " + mod.Id + " (" + N(rows.Count) + " ticked, " + N(had) + " in the mod already"
                + (rows.Count - had - n > 0 ? ", " + N(rows.Count - had - n) + " set to another text by the mod" : "") + "); Plan shows them, Save mod keeps them");
        }

        void Take()
        {
            NeedScan();
            Mod mod = host.SharedSession().Mod;
            int n = scan.Take(mod, scan.Rows);
            host.EditedOutside();
            FillFiles();
            host.SayOutside("took " + N(n) + " attributes out of mod " + mod.Id + " (the sets that still had the source text)");
        }

        // ---- layout ----

        void FitColumns()
        {
            if (rowList.Columns.Count < 6) return;
            int w = rowList.ClientSize.Width - 60 - 90 - 90 - 200 - 4;
            if (w < 300) w = 300;
            rowList.Columns[3].Width = w / 2;
            rowList.Columns[4].Width = w - w / 2;
        }
    }
}
