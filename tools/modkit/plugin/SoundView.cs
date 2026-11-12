// The "MS2 Sound" view, read only: the music cues of the client's event project (Sound/MS2Sound.fev) with the maps
// that play each one (the bgm rows of the kms client's table/fielddata.xml and of the server's and the global
// client's map/<id>.xml, as the mod being edited leaves them), and the sound banks of the Sound folder (MS2Sound_*.fsb)
// with their samples (name, length, channels, rate, bytes, loop). No playback and no edits: see MS2.ModKit/Sound.cs.
//
// The Sound folder is MS2_SOUND_DATA when set, else Sound (or Data/Sound) in the kms client data folder of the
// MS2 Tables view's settings. The maps come from the MS2 Tables view's session and mod.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Emergent.Toolbench;
using Emergent.Toolbench.UI.Workbench;
using MS2.ModKit;

namespace MS2.Tables
{
    public class SoundView : AbstractView
    {
        public const string ViewId = "MS2.Tables.SoundView";
        static readonly string[] CueHeads = { "map", "name", "plays", "param", "value", "side" };
        static readonly string[] SampleHeads = { "#", "sample", "seconds", "ch", "rate", "bytes", "loop" };

        TablesView host;
        string folder;                  // the Sound folder, or null
        SoundProject project;           // null without the folder or when it does not read
        string projectProblem;
        List<CueUse> uses;              // every side's bgm rows
        Dictionary<long, string> mapNames;
        object usesOf;                  // the session and mod the uses were read for
        readonly Dictionary<string, SoundBank> banks = new Dictionary<string, SoundBank>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> bankProblems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // the list: cue ids (Music) or bank files (Banks)
        readonly List<object> hits = new List<object>();
        readonly List<CueUse> shownUses = new List<CueUse>();
        SoundBank shownBank;
        bool filling;

        readonly ComboBox kindBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        readonly TextBox searchBox = new TextBox { Width = 180 };
        readonly Label hitsLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly Label folderLabel = new Label { AutoSize = true, Margin = new Padding(24, 8, 8, 0) };
        readonly ListBox hitList = new ListBox { Dock = DockStyle.Left, Width = 300, IntegralHeight = false, HorizontalScrollbar = true };
        readonly Label shownLabel = new Label { Dock = DockStyle.Top, Height = 20, Padding = new Padding(4, 3, 0, 0), Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
        readonly DataGridView grid = new DataGridView { Dock = DockStyle.Fill };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 90 };
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();

        public SoundView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(2) };
            kindBox.Items.Add("Music cues");
            kindBox.Items.Add("Banks");
            kindBox.SelectedIndex = 0;
            tips.SetToolTip(kindBox, "Music cues: the cues of MS2Sound.fev with the maps that play them. Banks: the MS2Sound_*.fsb files with their samples");
            top.Controls.Add(kindBox);
            top.Controls.Add(Lbl("find"));
            top.Controls.Add(searchBox);
            tips.SetToolTip(searchBox, "Music: a part of the cue id or name, or a map id (the cues the map plays). Banks: a part of the bank or of a sample name. Empty lists all");
            top.Controls.Add(Btn("Find", "Lists what holds the text", delegate { Find(); }));
            top.Controls.Add(Btn("Reload", "Reads the Sound folder and the maps again", delegate { Reload(); Find(); }));
            top.Controls.Add(hitsLabel);
            top.Controls.Add(folderLabel);

            grid.VirtualMode = true;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToOrderColumns = false;
            grid.RowHeadersVisible = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            grid.BackgroundColor = SystemColors.Window;
            grid.DefaultCellStyle.BackColor = SystemColors.Window;
            grid.DefaultCellStyle.ForeColor = SystemColors.WindowText;
            grid.CellValueNeeded += OnCellNeeded;
            grid.CellToolTipTextNeeded += OnTipNeeded;

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(grid);
            right.Controls.Add(shownLabel);

            Controls.Add(right);
            Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
            Controls.Add(hitList);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(outputBox);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, hitList, right });
            ViewLayout.KeepLogLines(outputBox, this);
            ViewLayout.CentreOnFirstShow(this);

            // a cue's find text means nothing in the banks and back
            kindBox.SelectedIndexChanged += delegate { if (!filling) { searchBox.Text = ""; Guard(delegate { Find(); }); } };
            searchBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                Guard(delegate { Find(); });
            };
            hitList.SelectedIndexChanged += delegate { if (!filling) Guard(delegate { OpenSelected(); }); };
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
                tips.Dispose();
                Font f = outputBox.Font;
                outputBox.Font = null;
                f.Dispose();
            }
            base.Dispose(disposing);
        }

        bool Music { get { return kindBox.SelectedIndex != 1; } }

        static Label Lbl(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(6, 8, 0, 0) }; }

        Button Btn(string text, string tip, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, UseVisualStyleBackColor = true, ForeColor = SystemColors.ControlText };
            b.Click += delegate(object sender, EventArgs e) { Guard(delegate { click(sender, e); }); };
            tips.SetToolTip(b, tip);
            return b;
        }

        // ---- the MS2 Tables view: session, mod, guard ----

        void Hook(TablesView h)
        {
            if (h == host) return;
            if (host != null)
            {
                host.Changed -= OnHostChanged;
                host.BusyChanged -= OnHostBusy;
            }
            host = h;
            usesOf = null;
            if (h == null) return;
            h.Changed += OnHostChanged;
            h.BusyChanged += OnHostBusy;
            OnHostBusy(h.Busy);
        }

        bool Guard(MethodInvoker body)
        {
            try
            {
                TablesView h = TablesView.Open;
                if (h == null || h.IsDisposed) h = TablesView.OpenBehind();
                if (h == null) { OutputWrap.Set(outputBox, "the MS2 Tables view could not be opened; this view reads the data and the mod of that view"); return false; }
                Hook(h);
            }
            catch (Exception ex)
            {
                OutputWrap.Set(outputBox, "the MS2 Tables view could not be opened: " + ex.Message);
                Logger.Warning("MS2 Sound: " + ex.Message);
                return false;
            }
            if (host.Busy) { OutputWrap.Set(outputBox, "wait for the running job of the MS2 Tables view"); return false; }
            return host.RunGuarded(body);
        }

        void OnHostBusy(bool on)
        {
            ViewLayout.Lock(this, lockable, on, outputBox);
            // opened while the MS2 Tables view was still loading: fill the list when it is done
            if (!on && hits.Count == 0 && IsHandleCreated && !IsDisposed)
                BeginInvoke((MethodInvoker)delegate { if (hits.Count == 0 && host != null && !host.Busy) Guard(delegate { Find(); }); });
        }

        // the mod was replaced or edited in another view: the maps' rows may differ
        void OnHostChanged()
        {
            usesOf = null;
            try { Fill(Selected()); }
            catch (ModKitException ex) { OutputWrap.Set(outputBox, ex.Message); }
            catch (IOException ex) { OutputWrap.Set(outputBox, ex.Message); }
        }

        // ---- reading ----

        void Reload()
        {
            folder = null;
            project = null;
            projectProblem = null;
            banks.Clear();
            bankProblems.Clear();
            usesOf = null;
        }

        void ReadFolder()
        {
            if (folder != null || projectProblem != null) return;
            string found = SoundData.Find(TablesSettings.Load().ClientData);
            if (found == null)
            {
                projectProblem = "no Sound folder: set " + SoundData.Override + ", or put Sound (with " + SoundData.ProjectFile + ") in the kms client data folder";
                folderLabel.Text = "no Sound folder";
                return;
            }
            folder = found;
            folderLabel.Text = "Sound: " + found;
            try { project = SoundProject.Read(new SoundData(found).ProjectPath); }
            catch (ModKitException ex) { projectProblem = ex.Message; }
            catch (IOException ex) { projectProblem = SoundData.ProjectFile + ": " + ex.Message; }
            catch (UnauthorizedAccessException ex) { projectProblem = SoundData.ProjectFile + ": " + ex.Message; }
        }

        void ReadUses()
        {
            EditorSession s = host.SharedSession();
            object key = new object[] { s.Data, s.Mod };
            if (usesOf != null && ((object[])usesOf)[0] == s.Data && ((object[])usesOf)[1] == s.Mod && uses != null) return;
            uses = SoundData.MapUses(s.Data, s.Mod);
            TableIdList maps = TableIdList.Maps(s.Data, s.Mod);
            mapNames = new Dictionary<long, string>(maps.Names());
            foreach (KeyValuePair<long, string> kv in maps.AddedNames()) mapNames[kv.Key] = kv.Value;
            usesOf = key;
        }

        SoundBank Bank(string file)
        {
            SoundBank b;
            if (banks.TryGetValue(file, out b)) return b;
            if (bankProblems.ContainsKey(file)) return null;
            try { b = SoundBank.Read(Path.Combine(folder, file)); banks[file] = b; return b; }
            catch (ModKitException ex) { bankProblems[file] = ex.Message; }
            catch (IOException ex) { bankProblems[file] = file + ": " + ex.Message; }
            catch (UnauthorizedAccessException ex) { bankProblems[file] = file + ": " + ex.Message; }
            return null;
        }

        // ---- the list ----

        object Selected() { int i = hitList.SelectedIndex; return i >= 0 && i < hits.Count ? hits[i] : null; }

        void Find()
        {
            Fill(Selected());
            if (hitList.Items.Count > 0 && hitList.SelectedIndex < 0) hitList.SelectedIndex = 0;
            else if (hitList.Items.Count == 0) Show(null);
        }

        void Fill(object keep)
        {
            ReadFolder();
            var said = new List<string>();
            if (projectProblem != null) said.Add(projectProblem);
            hits.Clear();
            var labels = new List<string>();
            string q = searchBox.Text.Trim();
            if (Music)
            {
                ReadUses();
                var count = new Dictionary<int, HashSet<long>>();
                foreach (CueUse u in uses)
                {
                    HashSet<long> m;
                    if (!count.TryGetValue(u.Cue, out m)) count[u.Cue] = m = new HashSet<long>();
                    m.Add(u.Map);
                }
                var ids = new SortedDictionary<int, string>();
                if (project != null) foreach (MusicCue c in project.Cues) ids[c.Id] = c.Name;
                int missing = 0;
                foreach (int id in count.Keys) if (!ids.ContainsKey(id)) { ids[id] = null; if (project != null) missing++; }
                long mapQ;
                bool byMap = long.TryParse(q, NumberStyles.None, CultureInfo.InvariantCulture, out mapQ);
                foreach (KeyValuePair<int, string> kv in ids)
                {
                    bool hit = q.Length == 0
                        || kv.Key.ToString(CultureInfo.InvariantCulture).Contains(q)
                        || (kv.Value != null && kv.Value.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!hit && byMap) foreach (CueUse u in uses) if (u.Cue == kv.Key && u.Map == mapQ) { hit = true; break; }
                    if (!hit) continue;
                    HashSet<long> m;
                    int n = count.TryGetValue(kv.Key, out m) ? m.Count : 0;
                    hits.Add(kv.Key);
                    labels.Add(kv.Key + "  " + (kv.Value ?? (project != null ? "(not in the project)" : "")) + (n > 0 ? "  · " + n + " map" + (n == 1 ? "" : "s") : ""));
                }
                if (missing > 0) said.Add(missing + " cue id(s) the maps play are not in " + SoundData.ProjectFile + ": the client plays nothing there");
                hitsLabel.Text = hits.Count + " cue(s)" + (project != null ? ", " + project.Cues.Count + " in the project" : "") + ", " + uses.Count + " map row(s)";
            }
            else
            {
                List<string> files = folder == null ? new List<string>() : new SoundData(folder).Banks();
                foreach (string f in files)
                {
                    SoundBank b = Bank(f);
                    bool hit = q.Length == 0 || f.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!hit && b != null) foreach (SoundSample x in b.Samples) if (x.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
                    if (!hit) continue;
                    hits.Add(f);
                    labels.Add(SoundData.ShortName(f) + (b != null ? "  · " + b.Samples.Count + " sample" + (b.Samples.Count == 1 ? "" : "s") + ", " + Mb(b.FileSize) : "  (does not read)"));
                    if (b != null && project != null && !project.HasHash(b.Hash)) said.Add(f + ": its hash " + b.HashText + " is not in " + SoundData.ProjectFile + ": the client does not load it");
                }
                foreach (KeyValuePair<string, string> kv in bankProblems) said.Add(kv.Value);
                hitsLabel.Text = hits.Count + " bank(s) of " + files.Count;
            }
            filling = true;
            try
            {
                hitList.BeginUpdate();
                hitList.Items.Clear();
                foreach (string l in labels) hitList.Items.Add(l);
                int at = keep == null ? -1 : hits.IndexOf(keep);
                if (at >= 0) hitList.SelectedIndex = at;
                hitList.EndUpdate();
            }
            finally { filling = false; }
            OutputWrap.Set(outputBox, said.Count == 0 ? "read only: no playback and no edits; what a mod would need is in the README (Sound and music)" : string.Join("\r\n", said.ToArray()));
            Show(Selected());
        }

        static string Mb(long bytes) { return (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB"; }

        void OpenSelected() { Show(Selected()); }

        void Show(object what)
        {
            shownUses.Clear();
            shownBank = null;
            grid.RowCount = 0;
            grid.Columns.Clear();
            if (what == null) { shownLabel.Text = ""; return; }
            if (what is int)
            {
                int cue = (int)what;
                MusicCue c = project != null ? project.Cue(cue) : null;
                foreach (CueUse u in uses) if (u.Cue == cue) shownUses.Add(u);
                shownUses.Sort(delegate(CueUse a, CueUse b)
                {
                    int r = a.Map.CompareTo(b.Map);
                    if (r == 0) r = a.Boss.CompareTo(b.Boss);
                    return r != 0 ? r : string.CompareOrdinal(a.Side, b.Side);
                });
                shownLabel.Text = "cue " + cue + (c != null ? " " + c.Name : "") + ": " + shownUses.Count + " bgm row(s)";
                int[] w = { 70, 180, 60, 50, 50, 110 };
                for (int i = 0; i < CueHeads.Length; i++) grid.Columns.Add(Col(CueHeads[i], w[i]));
                grid.RowCount = shownUses.Count;
            }
            else
            {
                string f = (string)what;
                shownBank = Bank(f);
                if (shownBank == null) { shownLabel.Text = f + ": " + (bankProblems.ContainsKey(f) ? bankProblems[f] : "does not read"); return; }
                shownLabel.Text = f + ": " + shownBank.Samples.Count + " sample(s), " + SoundBank.Codec(shownBank.Mode) + ", " + Mb(shownBank.FileSize);
                int[] w = { 40, 220, 60, 30, 55, 80, 40 };
                for (int i = 0; i < SampleHeads.Length; i++) grid.Columns.Add(Col(SampleHeads[i], w[i]));
                grid.RowCount = shownBank.Samples.Count;
            }
        }

        static DataGridViewColumn Col(string header, int width)
        {
            return ViewLayout.FitColumn(new DataGridViewTextBoxColumn { HeaderText = header, ReadOnly = true, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable });
        }

        void OnCellNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (shownBank != null)
            {
                if (e.RowIndex >= shownBank.Samples.Count) return;
                SoundSample x = shownBank.Samples[e.RowIndex];
                switch (e.ColumnIndex)
                {
                    case 0: e.Value = x.Index; break;
                    case 1: e.Value = x.Name; break;
                    case 2: e.Value = x.Seconds.ToString("0.00", CultureInfo.InvariantCulture); break;
                    case 3: e.Value = x.Channels; break;
                    case 4: e.Value = x.Rate; break;
                    case 5: e.Value = x.Size.ToString("N0", CultureInfo.InvariantCulture); break;
                    case 6: e.Value = x.Loops ? "yes" : ""; break;
                }
                return;
            }
            if (e.RowIndex >= shownUses.Count) return;
            CueUse u = shownUses[e.RowIndex];
            string name;
            switch (e.ColumnIndex)
            {
                case 0: e.Value = u.Map; break;
                case 1: e.Value = mapNames != null && mapNames.TryGetValue(u.Map, out name) ? name : ""; break;
                case 2: e.Value = u.Boss ? "boss" : "normal"; break;
                case 3: e.Value = u.Param; break;
                case 4: e.Value = u.Value; break;
                case 5: e.Value = u.Side; break;
            }
        }

        void OnTipNeeded(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
        {
            if (e.RowIndex >= 0 || e.ColumnIndex < 0) return;
            if (shownBank != null)
                switch (e.ColumnIndex)
                {
                    case 2: e.ToolTipText = "frames / rate"; break;
                    case 5: e.ToolTipText = "the sample's bytes in the bank (" + SoundBank.Codec(shownBank.Mode) + ")"; break;
                    case 6: e.ToolTipText = "the sample has loop points"; break;
                }
            else
                switch (e.ColumnIndex)
                {
                    case 2: e.ToolTipText = "normal: the bgm id; boss: the boss attribute, played in a boss fight"; break;
                    case 3: e.ToolTipText = "the event parameter set with the cue (param, or bossParam)"; break;
                    case 4: e.ToolTipText = "the parameter's value (value, or bossValue)"; break;
                    case 5: e.ToolTipText = "the archive the row is in: the kms client's fielddata.xml, the server's or the global client's map file"; break;
                }
        }
    }
}
