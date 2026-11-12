// The "MS2 Sound" view: the music cues of the client's event project (Sound/MS2Sound.fev) with the maps that play
// each one (the bgm rows of the kms client's table/fielddata.xml and of the server's and the global client's
// map/<id>.xml, as the mod being edited leaves them), the maps with the cue each side plays there (normal and boss,
// set by the mod being edited: MS2.ModKit/MapMusic.cs), and the sound banks of the Sound folder (MS2Sound_*.fsb) with
// their samples (name, length, channels, rate, bytes, loop). No playback; the banks and the event project are read only.
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
        static readonly string[] CueHeads = { "map", "name", "plays", "param", "value", "side", "environment" };
        static readonly string[] SampleHeads = { "#", "sample", "seconds", "ch", "rate", "bytes", "loop", "the mod's sound" };
        static readonly string[] PlaceHeads = { "side", "environment", "normal", "normal cue", "boss", "boss cue" };
        const int NormalColumn = 2, BossColumn = 4;
        const string CueKind = "cue";

        TablesView host;
        string folder;                  // the Sound folder, or null
        SoundProject project;           // null without the folder or when it does not read
        string projectProblem;
        List<CueUse> uses;              // every side's bgm rows, null until read (a job of the MS2 Tables view)
        Dictionary<long, string> mapNames;
        DataSet usesData;               // the session data and mod the uses were read for
        Mod usesMod;
        int usesAge;                    // counts the mod's changes: a scan started before one is thrown away
        bool reading;                   // the scan job runs
        string readProblem;             // the last scan failed (the MS2 Tables view said why); Reload tries again
        bool stale;                     // the mod changed: the maps are read again on the next Find, list pick or show
        readonly Dictionary<string, SoundBank> banks = new Dictionary<string, SoundBank>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> bankProblems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // the list: cue ids (Music) or bank files (Banks)
        readonly List<object> hits = new List<object>();
        readonly List<CueUse> shownUses = new List<CueUse>();
        SoundBank shownBank;
        long shownMap = -1;             // the map whose places the grid shows, or -1
        readonly List<MusicPlace> shownPlaces = new List<MusicPlace>();
        MapMusic tunes;                 // the mod's map music, for the session's data and mod
        bool filling;

        readonly ComboBox kindBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        readonly TextBox searchBox = new TextBox { Width = 180 };
        readonly Label hitsLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly Label folderLabel = new Label { AutoSize = true, Margin = new Padding(24, 8, 8, 0) };
        readonly ListBox hitList = new ListBox { Dock = DockStyle.Left, Width = 300, IntegralHeight = false, HorizontalScrollbar = true };
        readonly Label shownLabel = new Label { Dock = DockStyle.Top, Height = 20, Padding = new Padding(4, 3, 0, 0), Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
        readonly DataGridView grid = new DataGridView { Dock = DockStyle.Fill };
        readonly FlowLayoutPanel mapBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(2), Visible = false };
        readonly FlowLayoutPanel cueBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(2), Visible = false };
        readonly FlowLayoutPanel bankBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(2), Visible = false };
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
            kindBox.Items.Add("Maps");
            kindBox.SelectedIndex = 0;
            tips.SetToolTip(kindBox, "Music cues: the cues of MS2Sound.fev with the maps that play them. Banks: the MS2Sound_*.fsb files with their samples. Maps: the cue each map plays, which the mod being edited can change");
            top.Controls.Add(kindBox);
            top.Controls.Add(Lbl("find"));
            top.Controls.Add(searchBox);
            tips.SetToolTip(searchBox, "Music: a part of the cue id or name, or a map id (the cues the map plays). Banks: a part of the bank or of a sample name. Maps: a part of the map id or name, or a cue id (the maps that play it). Empty lists all");
            top.Controls.Add(Btn("Find", "Lists what holds the text", delegate { Find(); }));
            top.Controls.Add(Btn("Reload", "Reads the Sound folder and the maps again", delegate { Reload(); Find(); }));
            top.Controls.Add(hitsLabel);
            top.Controls.Add(folderLabel);

            mapBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change; writes nothing", delegate { return host.DoPlan(); }));
            mapBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change, to every target at once", delegate { return host.DoApply(); }));
            mapBar.Controls.Add(Job("Revert", "Removes what Apply wrote, on every target", delegate { return host.DoRevert(); }));
            mapBar.Controls.Add(Btn("Undo map", "Takes the mod's music lines of the map shown out: every side plays the shipped cues again", delegate { UndoMap(); }));
            mapBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same mod and Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            bankBar.Controls.Add(Btn("Replace sample...", "Gives the selected sample new sound from an .mp2 file (MPEG layer II, as the shipped samples) (kept in the mod); the client finds it by its index, so its name and place stay", delegate { ReplaceSample(); }));
            bankBar.Controls.Add(Btn("Take out", "Takes the mod's new sound of the selected sample out: the shipped sound plays again", delegate { TakeSample(); }));
            bankBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change; writes nothing", delegate { return host.DoPlan(); }));
            bankBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change, to every target at once", delegate { return host.DoApply(); }));
            bankBar.Controls.Add(Job("Revert", "Removes what Apply wrote, on every target", delegate { return host.DoRevert(); }));
            bankBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same mod and Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            cueBar.Controls.Add(Btn("New cue...", "Adds a new music cue (kept in the mod): its sound from .mp2 files goes into a bank as new samples, and it plays as the selected cue does (its theme, fades and links); a map plays it when its bgm id is the new cue's id", delegate { NewCue(); }));
            cueBar.Controls.Add(Btn("Take out cue", "Takes the selected new cue out of the mod", delegate { TakeCue(); }));
            cueBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change; writes nothing", delegate { return host.DoPlan(); }));
            cueBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change, to every target at once", delegate { return host.DoApply(); }));
            cueBar.Controls.Add(Job("Revert", "Removes what Apply wrote, on every target", delegate { return host.DoRevert(); }));
            cueBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same mod and Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            grid.VirtualMode = true;
            grid.ReadOnly = false;
            grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
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
            grid.CellValuePushed += OnCellPushed;
            grid.CellFormatting += OnCellFormatting;
            grid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e) { if (!Editable(e.RowIndex, e.ColumnIndex)) e.Cancel = true; };
            IdPick.Attach(grid, delegate(int row, int column) { return Editable(row, column) ? CueKind : null; }, delegate { return host.SharedSession(); }, null, FindCues);

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(grid);
            right.Controls.Add(mapBar);
            right.Controls.Add(bankBar);
            right.Controls.Add(cueBar);
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
            VisibleChanged += delegate { if (Visible && stale && host != null && !host.Busy) { stale = false; Guard(delegate { Fill(Selected()); }); } };
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

        bool Music { get { return kindBox.SelectedIndex == 0; } }
        bool Maps { get { return kindBox.SelectedIndex == 2; } }

        static Label Lbl(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(6, 8, 0, 0) }; }

        Button Btn(string text, string tip, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, UseVisualStyleBackColor = true, ForeColor = SystemColors.ControlText };
            b.Click += delegate(object sender, EventArgs e) { Guard(delegate { click(sender, e); }); };
            tips.SetToolTip(b, tip);
            return b;
        }

        // a job of the MS2 Tables view (Plan, Apply, Revert): the cell being typed in goes in first
        Button Job(string text, string tip, Func<bool> start)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, UseVisualStyleBackColor = true, ForeColor = SystemColors.ControlText };
            b.Click += delegate { if (Guard(delegate { grid.EndEdit(); host.NeedHandle(); })) start(); };
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
            uses = null;
            tunes = null;
            // a scan of the old host never ends here (its done or busy end may not come): the age drops its result
            reading = false;
            usesAge++;
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
            if (on || !IsHandleCreated || IsDisposed) return;
            // after the job's own end (the scan's done runs first): a scan that failed, a list still empty because the
            // MS2 Tables view was loading when this view opened, or a change while that view was busy
            BeginInvoke((MethodInvoker)delegate
            {
                if (host == null || host.Busy || IsDisposed) return;
                if (reading)
                {
                    reading = false;
                    readProblem = "the maps could not be read (see the MS2 Tables view's output); Reload tries again";
                    Fill(Selected());
                }
                else if (hits.Count == 0) Guard(delegate { Find(); });
            });
        }

        // the mod was replaced or edited in another view: the maps' rows may differ. They are read again on the next
        // Find, Reload or pick in the list, or when the view is shown again; never at each edit, so a chain of cell
        // edits in another view does not wait for the map scan.
        void OnHostChanged()
        {
            uses = null;
            tunes = null;
            usesAge++;
            stale = true;
            try
            {
                if (Visible && !IsDisposed) OutputWrap.Set(outputBox, "the mod changed: Find reads the maps again (the list shows them as they were)");
            }
            catch (Exception ex)
            {
                // the change is another view's: what fails here must not reach it
                Logger.Warning("MS2 Sound: " + ex);
            }
        }

        // ---- reading ----

        void Reload()
        {
            folder = null;
            project = null;
            projectProblem = null;
            banks.Clear();
            bankProblems.Clear();
            uses = null;
            tunes = null;
            usesAge++;
            readProblem = null;
            reading = false;
        }

        // the mod's map music for the session's data and mod (one reading of fielddata.xml while they stay)
        MapMusic Tunes()
        {
            EditorSession s = host.SharedSession();
            if (tunes == null || tunes.Data != s.Data || tunes.Mod != s.Mod) tunes = new MapMusic(s.Data, s.Mod);
            return tunes;
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

        // true when the uses are read for the session's data and mod; else starts the scan (every map file of the
        // server and the global client) as a job of the MS2 Tables view, which fills the list again when it is done
        bool ReadUses()
        {
            EditorSession s = host.SharedSession();
            if (uses != null && usesData == s.Data && usesMod == s.Mod) return true;
            if (reading || readProblem != null) return false;
            DataSet data = s.Data;
            Mod mod = s.Mod;
            int age = usesAge;
            List<CueUse> found = null;
            Dictionary<long, string> names = null;
            reading = true;
            bool started = host.RunOutside("map music scan", delegate
            {
                found = SoundData.MapUses(data, mod);
                TableIdList maps = TableIdList.Maps(data, mod);
                names = new Dictionary<long, string>(maps.Names());
                foreach (KeyValuePair<long, string> kv in maps.AddedNames()) names[kv.Key] = kv.Value;
            }, delegate
            {
                reading = false;
                if (IsDisposed) return;
                // the mod changed while it ran: read again
                if (age == usesAge) { uses = found; mapNames = names; usesData = data; usesMod = mod; }
                Fill(Selected());
            });
            if (!started) reading = false;
            return false;
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
            stale = false;
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
                bool ready = ReadUses();
                if (!ready) said.Add(readProblem ?? "reading the maps' bgm rows (a job of the MS2 Tables view)...");
                List<CueUse> uses = ready ? this.uses : new List<CueUse>();
                var count = new Dictionary<int, HashSet<long>>();
                foreach (CueUse u in uses)
                {
                    HashSet<long> m;
                    if (!count.TryGetValue(u.Cue, out m)) count[u.Cue] = m = new HashSet<long>();
                    m.Add(u.Map);
                }
                var ids = new SortedDictionary<int, string>();
                if (project != null) foreach (MusicCue c in project.Cues) ids[c.Id] = c.Name;
                var added = new HashSet<int>();
                Mod mod = host == null ? null : host.SharedSession().Mod;
                if (mod != null) foreach (CueOp c in mod.Cues) if (!ids.ContainsKey(c.Id)) { ids[c.Id] = c.Name; added.Add(c.Id); }
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
                    labels.Add(kv.Key + "  " + (kv.Value ?? (project != null ? "(not in the project)" : "")) + (added.Contains(kv.Key) ? "  (new, mod)" : "") + (n > 0 ? "  · " + n + " map" + (n == 1 ? "" : "s") : ""));
                }
                if (missing > 0) said.Add(missing + " cue id(s) the maps play are not in " + SoundData.ProjectFile + ": the client plays nothing there");
                hitsLabel.Text = hits.Count + " cue(s)" + (project != null ? ", " + project.Cues.Count + " in the project" : "") + ", " + uses.Count + " map row(s)";
            }
            else if (Maps)
            {
                bool ready = ReadUses();
                if (!ready) said.Add(readProblem ?? "reading the maps (a job of the MS2 Tables view)...");
                var edited = new HashSet<long>();
                if (ready) { MapMusic t = Tunes(); foreach (long m in t.EditedMaps()) if (t.IsEdited(m)) edited.Add(m); }
                var ids = new SortedDictionary<long, string>();
                if (ready)
                {
                    foreach (KeyValuePair<long, string> kv in mapNames) ids[kv.Key] = kv.Value;
                    foreach (CueUse u in uses) if (!ids.ContainsKey(u.Map)) ids[u.Map] = "";
                }
                int cueQ;
                bool byCue = int.TryParse(q, NumberStyles.None, CultureInfo.InvariantCulture, out cueQ);
                var playing = new HashSet<long>();
                if (byCue && ready) foreach (CueUse u in uses) if (u.Cue == cueQ) playing.Add(u.Map);
                foreach (KeyValuePair<long, string> kv in ids)
                {
                    bool hit = q.Length == 0 || kv.Key.ToString(CultureInfo.InvariantCulture).Contains(q)
                        || (kv.Value ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || playing.Contains(kv.Key);
                    if (!hit) continue;
                    hits.Add(kv.Key);
                    labels.Add(kv.Key + "  " + kv.Value + (edited.Contains(kv.Key) ? "  · music edited" : ""));
                }
                hitsLabel.Text = hits.Count + " map(s)" + (ready ? " of " + ids.Count + ", " + edited.Count + " with the mod's music" : "");
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
            string idle = Maps
                ? "a map's normal and boss cells set the cue it plays (F4: pick one by id or name; empty: no music); the kms client's row and the server's map file are set, the mirror writes the global client's"
                : Music ? "no playback; New cue... adds a cue with its own sound (.mp2 files, kept in the mod) that plays as the selected cue does; the maps' music is set in Maps"
                : "no playback; Replace sample... gives the selected sample new sound from an .mp2 file (MPEG layer II, as the shipped samples) (the client finds a sample by its index); the event project's segment length stays";
            OutputWrap.Set(outputBox, said.Count == 0 ? idle : string.Join("\r\n", said.ToArray()));
            Show(Selected());
        }

        static string Mb(long bytes) { return (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB"; }

        // a pick in the list after the mod changed reads the maps again first
        void OpenSelected()
        {
            if (stale && (Music || Maps)) { stale = false; Fill(Selected()); return; }
            Show(Selected());
        }

        void Show(object what)
        {
            shownUses.Clear();
            shownBank = null;
            shownMap = -1;
            shownPlaces.Clear();
            grid.CancelEdit();
            grid.RowCount = 0;
            grid.Columns.Clear();
            mapBar.Visible = Maps;
            bankBar.Visible = !Maps && !Music;
            cueBar.Visible = Music;
            if (what == null) { shownLabel.Text = ""; return; }
            if (what is long)
            {
                shownMap = (long)what;
                shownPlaces.AddRange(Tunes().Places(shownMap));
                string name;
                shownLabel.Text = "map " + shownMap + (mapNames != null && mapNames.TryGetValue(shownMap, out name) && name.Length > 0 ? " " + name : "")
                    + ": " + shownPlaces.Count + " environment(s)" + (shownPlaces.Count == 0 ? " (no fielddata row and no map file)" : "");
                int[] w = { 130, 150, 60, 170, 60, 170 };
                for (int i = 0; i < PlaceHeads.Length; i++)
                {
                    DataGridViewColumn c = Col(PlaceHeads[i], w[i]);
                    c.ReadOnly = i != NormalColumn && i != BossColumn;
                    grid.Columns.Add(c);
                }
                grid.RowCount = shownPlaces.Count;
                return;
            }
            if (what is int)
            {
                int cue = (int)what;
                MusicCue c = project != null ? project.Cue(cue) : null;
                if (uses != null) foreach (CueUse u in uses) if (u.Cue == cue) shownUses.Add(u);
                shownUses.Sort(delegate(CueUse a, CueUse b)
                {
                    int r = a.Map.CompareTo(b.Map);
                    if (r == 0) r = a.Boss.CompareTo(b.Boss);
                    if (r == 0) r = string.CompareOrdinal(a.Side, b.Side);
                    return r != 0 ? r : string.CompareOrdinal(a.Env, b.Env);
                });
                CueOp nc = c == null && host != null && host.SharedSession().Mod != null ? host.SharedSession().Mod.CueOf(cue) : null;
                shownLabel.Text = "cue " + cue + (c != null ? " " + c.Name : nc != null ? " " + nc.Name + " (new, mod: plays as cue " + nc.Like + ", sound in " + nc.Bank + ")" : "") + ": " + shownUses.Count + " bgm row(s)";
                int[] w = { 70, 180, 60, 50, 50, 110, 120 };
                for (int i = 0; i < CueHeads.Length; i++) grid.Columns.Add(Col(CueHeads[i], w[i]));
                grid.RowCount = shownUses.Count;
            }
            else
            {
                string f = (string)what;
                shownBank = Bank(f);
                if (shownBank == null) { shownLabel.Text = f + ": " + (bankProblems.ContainsKey(f) ? bankProblems[f] : "does not read"); return; }
                shownLabel.Text = f + ": " + shownBank.Samples.Count + " sample(s), " + SoundBank.Codec(shownBank.Mode) + ", " + Mb(shownBank.FileSize);
                int[] w = { 40, 220, 60, 30, 55, 80, 40, 160 };
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
                    case 7: SampleOp op = ModSample(x); e.Value = op == null ? "" : op.File; break;
                }
                return;
            }
            if (shownMap >= 0)
            {
                if (e.RowIndex >= shownPlaces.Count) return;
                MusicPlace p = shownPlaces[e.RowIndex];
                switch (e.ColumnIndex)
                {
                    case 0: e.Value = p.Archive; break;
                    case 1: e.Value = SoundData.EnvLabel(p.Feature, p.Locale); break;
                    case NormalColumn: e.Value = Cue(p, false); break;
                    case 3: e.Value = CueName(Cue(p, false)); break;
                    case BossColumn: e.Value = Cue(p, true); break;
                    case 5: e.Value = CueName(Cue(p, true)); break;
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
                case 6: e.Value = u.Env; break;
            }
        }

        void OnTipNeeded(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
        {
            if (shownMap >= 0 && e.RowIndex >= 0 && e.RowIndex < shownPlaces.Count && e.ColumnIndex >= 0)
            {
                MusicPlace p = shownPlaces[e.RowIndex];
                if (!p.Written) e.ToolTipText = p.Archive == EntityEditor.GmsXml && Tunes().PerIdSide == EntityEditor.ServerXml
                    ? "the global client's map file: the mirror writes the server's cues here (shown)" : "not written by this view";
                else if (e.ColumnIndex == NormalColumn || e.ColumnIndex == BossColumn)
                    e.ToolTipText = "shipped: " + Shipped(p, e.ColumnIndex == BossColumn) + "; a cue id or name, empty for no music; it is set on every written row of the map";
                return;
            }
            if (e.RowIndex >= 0 || e.ColumnIndex < 0) return;
            if (shownMap >= 0)
            {
                switch (e.ColumnIndex)
                {
                    case 0: e.ToolTipText = "the archive: the kms client's fielddata.xml row, the server's or the global client's map file"; break;
                    case 1: e.ToolTipText = "the environment the element is in: its feature and locale (empty: the plain one)"; break;
                    case NormalColumn: e.ToolTipText = "the cue the map plays (the bgm element's id)"; break;
                    case BossColumn: e.ToolTipText = "the cue of a boss fight on the map (the bgm element's boss)"; break;
                }
                return;
            }
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
                    case 6: e.ToolTipText = "the environment the element is in: its feature and locale (empty: the plain one). The client plays the one whose feature is on and whose locale is its own"; break;
                }
        }

        // ---- a map's music ----

        bool Editable(int row, int column)
        {
            return shownMap >= 0 && (column == NormalColumn || column == BossColumn) && row >= 0 && row < shownPlaces.Count && shownPlaces[row].Written;
        }

        // the cue a place plays with the mod (the global client's file: the server's, which the mirror carries there)
        string Cue(MusicPlace p, bool boss)
        {
            MapMusic t = Tunes();
            return t.Get(MapMusic.Shown(p, shownPlaces, t.PerIdSide), boss);
        }

        static string Shipped(MusicPlace p, bool boss)
        {
            string v = boss ? p.Boss : p.Id;
            return v.Length == 0 ? "none" : v;
        }

        string CueName(string id)
        {
            int n;
            if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n == 0) return id.Length == 0 ? "" : "(no music)";
            if (project == null) return "";
            MusicCue c = project.Cue(n);
            return c != null ? c.Name : "(not in the project: no music)";
        }

        void OnCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (shownBank != null)
            {
                if (e.RowIndex >= 0 && e.RowIndex < shownBank.Samples.Count && ModSample(shownBank.Samples[e.RowIndex]) != null) e.CellStyle.BackColor = Color.FromArgb(214, 240, 214);
                return;
            }
            if (shownMap < 0 || e.RowIndex < 0 || e.RowIndex >= shownPlaces.Count) return;
            MusicPlace p = shownPlaces[e.RowIndex];
            if (!p.Written) { e.CellStyle.ForeColor = SystemColors.GrayText; return; }
            bool boss = e.ColumnIndex >= BossColumn;
            if (e.ColumnIndex >= NormalColumn && Cue(p, boss) != (boss ? p.Boss : p.Id)) e.CellStyle.BackColor = Color.FromArgb(214, 240, 214);
        }

        // the id picker's search: the event project's cues by id or name
        List<EntityHit> FindCues(string kind, string text, int max)
        {
            if (project == null) throw new ModKitException(projectProblem ?? "the event project is not read");
            string q = (text ?? "").Trim();
            var l = new List<EntityHit>();
            foreach (MusicCue c in project.Cues)
            {
                if (l.Count >= max) break;
                if (q.Length == 0 || c.Id.ToString(CultureInfo.InvariantCulture).StartsWith(q, StringComparison.Ordinal)
                    || (c.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    l.Add(new EntityHit { Id = c.Id, Name = c.Name ?? "" });
            }
            return l;
        }

        // a typed cell: a cue id, or a cue name of the project (the whole name, else the one cue whose name holds it)
        string CueOf(string text)
        {
            string t = (text ?? "").Trim();
            int n;
            if (t.Length == 0 || int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out n)) return t;
            if (project == null) throw new ModKitException("\"" + t + "\" is not a cue id, and the event project is not read to find it by name");
            MusicCue whole = project.Cues.Find(delegate(MusicCue c) { return string.Equals(c.Name, t, StringComparison.OrdinalIgnoreCase); });
            if (whole != null) return whole.Id.ToString(CultureInfo.InvariantCulture);
            List<MusicCue> part = project.Cues.FindAll(delegate(MusicCue c) { return (c.Name ?? "").IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0; });
            if (part.Count == 1) return part[0].Id.ToString(CultureInfo.InvariantCulture);
            throw new ModKitException(part.Count == 0 ? "no cue of the project is named \"" + t + "\"" : part.Count + " cues have \"" + t + "\" in their name: type more of it, or pick one with F4");
        }

        void OnCellPushed(object sender, DataGridViewCellValueEventArgs e)
        {
            if (!Editable(e.RowIndex, e.ColumnIndex)) return;
            long map = shownMap;
            bool boss = e.ColumnIndex == BossColumn;
            string typed = e.Value == null ? "" : e.Value.ToString();
            // the grid pushes while it ends the edit: the change and the refill come after
            BeginInvoke((MethodInvoker)delegate { Guard(delegate { SetCue(map, boss, typed); }); });
        }

        void SetCue(long map, bool boss, string typed)
        {
            if (map != shownMap) return;
            string cue = CueOf(typed);
            int n = Tunes().Set(map, boss, cue);
            string warn = "";
            int id;
            if (cue.Length > 0 && project != null && int.TryParse(cue, NumberStyles.None, CultureInfo.InvariantCulture, out id) && project.Cue(id) == null)
                warn = "; cue " + cue + " is not in " + SoundData.ProjectFile + ": the client plays nothing there";
            MapEdited(map, "map " + map + (boss ? " boss" : "") + ": " + (cue.Length == 0 ? "no music" : "cue " + cue + " " + CueName(cue)) + " on " + n + " row(s)" + warn);
        }

        void UndoMap()
        {
            if (shownMap < 0) { OutputWrap.Set(outputBox, "pick a map in the list first"); return; }
            int n = Tunes().Take(shownMap);
            MapEdited(shownMap, "map " + shownMap + ": " + (n == 0 ? "the mod sets no music there" : "the mod's music lines taken out of " + n + " row(s); the shipped cues play"));
        }

        // ---- sample replace (Banks) ----

        string ShownBankFile() { return shownBank == null ? null : Path.GetFileName((string)Selected()); }

        SampleOp ModSample(SoundSample x)
        {
            Mod m = host == null ? null : host.SharedSession().Mod;
            string f = ShownBankFile();
            return m == null || f == null ? null : m.SampleOf(f, x.Index);
        }

        SoundSample SelectedSample()
        {
            if (shownBank == null) throw new ModKitException("pick a bank in the list first");
            int r = grid.CurrentCell != null ? grid.CurrentCell.RowIndex : -1;
            if (r < 0 || r >= shownBank.Samples.Count) throw new ModKitException("pick a sample in the grid first");
            return shownBank.Samples[r];
        }

        void ReplaceSample()
        {
            if (host.Busy) { OutputWrap.Set(outputBox, "wait for the running job"); return; }
            SoundSample x = SelectedSample();
            Mod m = host.SharedSession().Mod;
            if (m == null) throw new ModKitException("open or start a mod in the MS2 Tables view first");
            string path;
            using (var d = new OpenFileDialog { Title = "New sound for sample " + x.Index + " " + x.Name, Filter = "MPEG layer II audio (*.mp2)|*.mp2|MPEG audio (*.mp2;*.mp3)|*.mp2;*.mp3|All files|*.*", CheckFileExists = true })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                path = d.FileName;
            }
            byte[] b = File.ReadAllBytes(path);
            string bank = ShownBankFile();
            SampleOp op = m.SetSample(bank, x, b, Path.GetFileName(path));
            MpegAudio a = MpegAudio.Parse(b, Path.GetFileName(path));
            string said = bank + " sample " + x.Index + " " + x.Name + ": new sound " + op.File + " (" + a.Describe() + ")";
            if (Math.Abs(a.Seconds - x.Seconds) > 0.1)
                said += "; the shipped sample is " + x.Seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s, and the event project keeps its music segment's length, so the new sound is cut or ends in silence";
            SampleEdited(said);
        }

        void TakeSample()
        {
            SoundSample x = SelectedSample();
            Mod m = host.SharedSession().Mod;
            string bank = ShownBankFile();
            bool had = m != null && m.TakeSample(bank, x.Index);
            SampleEdited(bank + " sample " + x.Index + " " + x.Name + ": " + (had ? "the mod's sound taken out; the shipped sound plays" : "the mod gives this sample no new sound"));
        }

        void SampleEdited(string said)
        {
            host.EditedOutside();
            host.SayOutside(said);
            grid.Invalidate();
            OutputWrap.Set(outputBox, said);
        }

        // ---- new cue (Music) ----

        const string DefaultCueBank = "MS2Sound_BGM4.fsb";

        void NewCue()
        {
            if (host.Busy) { OutputWrap.Set(outputBox, "wait for the running job"); return; }
            Mod m = host.SharedSession().Mod;
            if (m == null) throw new ModKitException("open or start a mod in the MS2 Tables view first");
            if (project == null) throw new ModKitException(projectProblem ?? "the event project does not read");
            object sel = Selected();
            if (!(sel is int) || project.Cue((int)sel) == null) throw new ModKitException("pick a shipped cue in the list first: the new cue plays as it does");
            int like = (int)sel;
            int next = 0;
            foreach (MusicCue c in project.Cues) next = Math.Max(next, c.Id);
            foreach (CueOp c in m.Cues) next = Math.Max(next, c.Id);
            var mpeg = new List<string>();
            foreach (string f in new SoundData(folder).Banks()) { SoundBank b = Bank(f); if (b != null && b.Mode == 11) mpeg.Add(f); }   // 11: MPEG
            if (mpeg.Count == 0) throw new ModKitException("no MPEG bank in the Sound folder to add the samples to");
            using (var d = new NewCueDialog(project.Cue(like).ToString(), next + 1, mpeg, mpeg.Contains(DefaultCueBank) ? DefaultCueBank : mpeg[0]))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (project.Cue(d.Id) != null) throw new ModKitException("cue " + d.Id + " is in the project: a new cue needs an id it does not have");
                foreach (MusicCue c in project.Cues)
                    if (string.Equals(c.Name, d.CueName, StringComparison.OrdinalIgnoreCase)) throw new ModKitException("the project has a cue named " + c.Name + " (cue " + c.Id + ")");
                byte[] intro = d.IntroPath.Length == 0 ? null : File.ReadAllBytes(d.IntroPath);
                byte[] loop = File.ReadAllBytes(d.LoopPath);
                CueOp op = m.SetCue(d.Id, d.CueName, d.BankFile, like, intro, loop,
                    intro == null ? null : Path.GetFileName(d.IntroPath), Path.GetFileName(d.LoopPath));
                CueEdited(op.Id, "new cue " + op.Id + " " + op.Name + ": plays as cue " + like + ", its sound added to " + op.Bank
                    + (intro != null ? " (intro " + op.Intro + ", loop " + op.Loop + ")" : " (loop " + op.Loop + ")")
                    + "; set a map's bgm id to " + op.Id + " in Maps to hear it");
            }
        }

        void TakeCue()
        {
            Mod m = host.SharedSession().Mod;
            object sel = Selected();
            if (!(sel is int)) throw new ModKitException("pick a cue in the list first");
            int id = (int)sel;
            bool had = m != null && m.TakeCue(id);
            CueEdited(had ? -1 : id, "cue " + id + ": " + (had ? "the mod's new cue taken out" : "is no new cue of the mod"));
        }

        void CueEdited(int keep, string said)
        {
            host.EditedOutside();
            host.SayOutside(said);
            Fill(keep < 0 ? null : (object)keep);
            OutputWrap.Set(outputBox, said);
        }

        /// <summary>Asks for a new cue's id, name, bank and sound files.</summary>
        sealed class NewCueDialog : Form
        {
            readonly NumericUpDown idBox = new NumericUpDown { Minimum = 1, Maximum = int.MaxValue, Width = 100 };
            readonly TextBox nameBox = new TextBox { Width = 260 };
            readonly ComboBox bankBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
            readonly TextBox introBox = new TextBox { Width = 260 };
            readonly TextBox loopBox = new TextBox { Width = 260 };

            internal int Id { get { return (int)idBox.Value; } }
            internal string CueName { get { return nameBox.Text.Trim(); } }
            internal string BankFile { get { return (string)bankBox.SelectedItem; } }
            internal string IntroPath { get { return introBox.Text.Trim(); } }
            internal string LoopPath { get { return loopBox.Text.Trim(); } }

            internal NewCueDialog(string like, int id, List<string> banks, string bank)
            {
                Text = "New cue, plays as " + like;
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MinimizeBox = MaximizeBox = false;
                ShowInTaskbar = false;
                Font = SystemFonts.MessageBoxFont;
                AutoSize = true;
                AutoSizeMode = AutoSizeMode.GrowAndShrink;
                idBox.Value = id;
                foreach (string b in banks) bankBox.Items.Add(b);
                bankBox.SelectedItem = bank;

                var t = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8), Dock = DockStyle.Fill };
                Row(t, "id", idBox, null);
                Row(t, "name", nameBox, null);
                Row(t, "bank", bankBox, null);
                Row(t, "intro (may stay empty)", introBox, Browse(introBox, "The sound played once first"));
                Row(t, "loop", loopBox, Browse(loopBox, "The sound played over and over"));
                var ok = new Button { Text = "OK", AutoSize = true };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
                ok.Click += delegate
                {
                    string no = CueName.Length == 0 ? "give the cue a name"
                        : LoopPath.Length == 0 || !File.Exists(LoopPath) ? "pick the loop's .mp2 file"
                        : IntroPath.Length > 0 && !File.Exists(IntroPath) ? "the intro file is not there"
                        : null;
                    if (no != null) { MessageBox.Show(this, no, Text); return; }
                    DialogResult = DialogResult.OK;
                };
                var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(ok);
                t.Controls.Add(buttons, 0, t.RowCount);
                t.SetColumnSpan(buttons, 3);
                Controls.Add(t);
                AcceptButton = ok;
                CancelButton = cancel;
            }

            static void Row(TableLayoutPanel t, string label, Control c, Control extra)
            {
                int r = t.RowCount++;
                t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 6, 3, 0) }, 0, r);
                t.Controls.Add(c, 1, r);
                if (extra != null) t.Controls.Add(extra, 2, r);
            }

            Button Browse(TextBox into, string title)
            {
                var b = new Button { Text = "...", AutoSize = true };
                b.Click += delegate
                {
                    using (var d = new OpenFileDialog { Title = title, Filter = "MPEG layer II audio (*.mp2)|*.mp2|MPEG audio (*.mp2;*.mp3)|*.mp2;*.mp3|All files|*.*", CheckFileExists = true })
                        if (d.ShowDialog(this) == DialogResult.OK) into.Text = d.FileName;
                };
                return b;
            }
        }

        // after this view's own edit: only that map's rows of the lists are read again, not every map file
        void MapEdited(long map, string said)
        {
            if (uses != null) Tunes().Overlay(uses, map);
            host.EditedOutside();
            host.SayOutside(said);
            Fill(Selected());
            OutputWrap.Set(outputBox, said);
        }
    }
}
