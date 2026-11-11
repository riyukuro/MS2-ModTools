// The "MS2 Tables" view: one XML entry of a data archive as a grid (a row per child element, a column per
// attribute), the pending edits as the mod.xml they will become, and Plan / Apply / Revert.
//
// The view holds no data logic. MS2.ModKit.EditorSession owns the folders and the mod being edited, and
// MS2.ModKit.TableDraft turns cell edits into mod.xml operations; this file only shows them. The grid is in virtual
// mode (it asks the draft for each cell), so a table of some ten thousand rows opens at once.
//
// Plan, Apply and Revert run on a worker thread (RunJob): the controls are disabled meanwhile, the progress is shown
// beside the buttons, and the result comes back to the window's thread before anything of the view is touched.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
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
        readonly ComboBox targetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        readonly Dictionary<string, TextBox> featureBoxes = new Dictionary<string, TextBox>();   // data root -> its features field
        readonly CheckBox allowBox = new CheckBox { Text = "Higher mod wins a conflict", Height = 20 };
        readonly CheckedListBox modsList = new CheckedListBox { Dock = DockStyle.Fill, IntegralHeight = false, CheckOnClick = true };
        readonly Label busyLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly List<Control> lockable = new List<Control>();   // what is disabled while a job runs
        ModList modList;
        Thread worker;
        string busy;                                              // what the worker is doing; null = idle
        int lastProgress;
        readonly TextBox pendingBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Fill };

        public TablesView()
        {
            Open = this;
            Font mono = new Font(FontFamily.GenericMonospace, 8.25f);
            pendingBox.Font = mono;
            outputBox.Font = mono;
            OutputWrap.Attach(outputBox);
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

            // under it: locale and server type per target, for the features that are on
            FlowLayoutPanel featBar = Bar(DockStyle.Top);
            featBar.Controls.Add(Lbl("Features (LOCALE/TYPE)"));
            foreach (Profile p in Profile.All)
            {
                Profile profile = p;
                var box = new TextBox { Width = 80 };
                featureBoxes[p.Root] = box;
                featBar.Controls.Add(Lbl(p.Title));
                featBar.Controls.Add(box);
                box.Leave += delegate { FeaturesEdited(profile); };
                box.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; FeaturesEdited(profile); } };
            }

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
            modBar.Controls.Add(Lbl("Apply to"));
            targetBox.Items.Add("workspace (patched archives)");
            targetBox.Items.Add("game folder (loose files)");
            targetBox.SelectedIndex = 0;
            tips.SetToolTip(targetBox, "Where Apply writes the client's tables: patched copies of the archives in the workspace folder, or plain files under Data/ of the game folder, which the game reads before its archives. The server's archives always go to the workspace.");
            modBar.Controls.Add(targetBox);
            modBar.Controls.Add(Btn("Plan", "Shows what the checked mods and the pending edits would change, with conflicts and warnings; writes nothing", delegate { DoPlan(); }));
            modBar.Controls.Add(Btn("Apply", "Saves the mod being edited and writes what the checked mods change to the place chosen under \"Apply to\"", delegate { DoApply(); }));
            modBar.Controls.Add(Btn("Revert", "Removes what Apply wrote: the loose files in the game folder and the patched copies in the workspace folder", delegate { DoRevert(); }));
            modBar.Controls.Add(busyLabel);

            // the mods of the mods folder: a check per mod; the order is the priority, the top one wins a conflict
            FlowLayoutPanel modsBar = Bar(DockStyle.Bottom);
            modsBar.Controls.Add(Btn("Up", "Gives the selected mod a higher priority (one place up). Where two mods set the same thing, the higher one wins. A mod can also be dragged to its place", delegate { MoveMod(-1); }));
            modsBar.Controls.Add(Btn("Down", "Gives the selected mod a lower priority (one place down)", delegate { MoveMod(1); }));
            modsBar.Controls.Add(Btn("Load mod", "Loads the selected mod into the grid, to edit it", delegate { LoadMod(); }));
            var modsPanel = new Panel { Dock = DockStyle.Left, Width = 250 };
            modsPanel.Controls.Add(modsList);
            modsPanel.Controls.Add(modsBar);
            allowBox.Dock = DockStyle.Bottom;
            tips.SetToolTip(allowBox, "Off: Apply writes nothing while two mods conflict. On: Apply goes on, and where two mods set the same thing the one higher in this list wins; Plan and Apply name the winner of every conflict");
            modsPanel.Controls.Add(allowBox);
            modsPanel.Controls.Add(Caption("Mods (checked = applied; 1 = highest priority)"));

            var texts = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
            texts.Panel1.Controls.Add(pendingBox);
            texts.Panel1.Controls.Add(Caption("Pending changes (mod.xml)"));
            texts.Panel2.Controls.Add(outputBox);
            texts.Panel2.Controls.Add(Caption("Output"));
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 260 };
            bottom.Controls.Add(texts);
            bottom.Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
            bottom.Controls.Add(modsPanel);
            bottom.Controls.Add(modBar);

            grid.VirtualMode = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToOrderColumns = false;
            grid.RowHeadersVisible = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            grid.BackgroundColor = SystemColors.Window;      // the room under the last row reads as the table, not as an empty block
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
            Controls.Add(featBar);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, featBar, rowBar, grid, entryList, modBar, modsPanel });
            ViewLayout.KeepHeightShare(bottom, this, 0.32, 120);
            behind = makingBehind;
            ViewLayout.CentreOnFirstShow(this, delegate { return behind; });

            targetBox.SelectedIndexChanged += delegate
            {
                if (!filling && session != null) session.Target = TargetChosen();
                if (TargetChanged != null) TargetChanged();
            };
            modsList.ItemCheck += OnModCheck;
            modsList.MouseDown += OnModMouseDown;
            modsList.MouseMove += OnModMouseMove;
            modsList.MouseUp += delegate { dragFrom = -1; pressOnName = false; modsList.Cursor = Cursors.Default; };
            allowBox.CheckedChanged += delegate
            {
                if (filling) return;
                if (session != null) session.AllowConflicts = allowBox.Checked;
                Guard(delegate { TablesSettings.SaveAllowConflicts(allowBox.Checked); });
            };
            modsList.DoubleClick += delegate { Guard(delegate { LoadMod(); }); };
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
            if (disposing)
            {
                // a running apply finishes first: the session's archives are open under it
                Thread w = worker;
                if (w != null && w.IsAlive) w.Join();
                if (session != null) { session.Dispose(); session = null; }
                if (Open == this) Open = null;
            }
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
            OutputWrap.Set(outputBox, text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            if (Said != null) Said(OutputText);
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
            catch (InvalidDataException ex)
            {
                // a damaged archive or a file that is not what its name says (System.IO, not an IOException)
                Say(ex.Message);
                Logger.Warning("MS2 Tables: " + ex.Message);
            }
            catch (OutOfMemoryException)
            {
                // the 32-bit host: a very large mod or table; the step is dropped, the view and the mod stay usable
                GC.Collect();
                Say("out of memory: the step was not done (the host is 32-bit; a smaller mod or fewer rows at a time may fit)");
                Logger.Warning("MS2 Tables: out of memory");
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
            if (IsBusy()) return;
            Guard(delegate
            {
                Mod keep = session != null ? session.Mod : null;
                bool hadSession = session != null;
                ApplyTarget target = TargetChosen();
                if (session != null) { session.Dispose(); session = null; }
                ShowTable(null);
                archiveBox.Items.Clear();
                entryList.Items.Clear();
                TablesSettings s = TablesSettings.Load();
                session = new EditorSession(s.ClientData, s.GmsData, s.ServerData, s.ModsDir, s.Workspace);
                ShowFeatures(s);
                session.GameDir = string.IsNullOrEmpty(s.GameDir) ? null : s.GameDir;
                session.Server.TargetDir = string.IsNullOrEmpty(s.ServerTarget) ? null : s.ServerTarget;
                session.Server.Command = string.IsNullOrEmpty(s.AfterApply) ? null : s.AfterApply;
                session.SchemaDir = string.IsNullOrEmpty(s.SchemaDir) ? null : s.SchemaDir;
                session.SchemaNotes = s.SchemaNotes;
                filling = true;
                allowBox.Checked = TablesSettings.LoadAllowConflicts();
                filling = false;
                session.AllowConflicts = allowBox.Checked;
                if (keep != null && !keep.IsEmpty) session.Adopt(keep);
                else MarkSaved();
                filling = true;
                foreach (string a in session.Archives()) archiveBox.Items.Add(a);
                // loose files are the default once a game folder is set; a choice made in the box is kept
                if (session.GameDir == null) target = ApplyTarget.Workspace;
                else if (!hadSession) target = ApplyTarget.LooseFiles;
                targetBox.SelectedIndex = target == ApplyTarget.LooseFiles ? 1 : 0;
                session.Target = target;
                filling = false;
                FillMods();
                // a mod saved under the id being edited is the one being edited: without this an Apply right after
                // a start would leave that saved mod out, because the (empty) mod in memory stands for it
                string resumed = "";
                ModListItem saved = session.Mod.IsEmpty && modList != null ? modList.FindId(session.Mod.Id) : null;
                if (saved != null)
                {
                    session.LoadMod(saved.Folder);
                    MarkSaved();
                    FillMods();
                    resumed = "\nediting:     mod " + session.Mod.Id + " as saved in the mods folder";
                }
                ShowMod();
                Say(Targets.Line(session.Data) + "\nkms-client data: " + Shown(s.ClientData) + "\ngms-client data: " + Shown(s.GmsData) + "\nserver data: " + Shown(s.ServerData) + "\nmods folder: " + Shown(s.ModsDir)
                    + "\nworkspace:   " + Shown(s.Workspace) + "\ngame folder: " + Shown(s.GameDir)
                    + "\nserver target folder: " + (session.Server.HasTarget ? session.Server.TargetDir : "(not set: the server archive stays in the workspace)")
                    + "\ncommand after apply:  " + (session.Server.HasCommand ? session.Server.Command : "(not set: nothing is run)")
                    + "\nfeatures:    " + FeaturesLine(s)
                    + "\napply to:    " + targetBox.Text + "\n" + archiveBox.Items.Count + " archive(s), " + modsList.Items.Count + " mod(s)" + resumed);
                if (archiveBox.Items.Count > 0) archiveBox.SelectedIndex = PreferredArchive();
            });
            NotifyChanged();
        }

        // ---- the features fields ----

        const string FeaturesTip = "Locale and server type the game of this target runs with, as LOCALE/TYPE: NA/Dev, KR, /Dev. They decide which features are on, and so which of several map files of a map is loaded. Empty = ";

        // fills the three fields from the settings and hands the values to the data set
        void ShowFeatures(TablesSettings s)
        {
            foreach (Profile p in Profile.All)
            {
                TextBox box = featureBoxes[p.Root];
                string from = s.FeaturesFrom[p.Root], value = s.Features[p.Root];
                string own = p.Locale + "/" + FeatureSet.DefaultServerType;
                box.Text = value;
                box.Tag = value;        // what is saved, to tell an edit from a visit
                box.ReadOnly = from != null;
                tips.SetToolTip(box, from != null
                    ? "Set by " + from + ", which goes before this field. Remove it there to edit the value here."
                    : FeaturesTip + own + ". Saved with the MS2 Mods settings; " + p.FeaturesVar + " in the environment or in modtools.toml goes before it.");
                try { session.Data.SetFeatures(p, value); }
                catch (ModKitException ex)
                {
                    session.Data.SetFeatures(p, null);
                    Logger.Warning("MS2 Tables: " + ex.Message + " (" + p.Title + "); " + own + " is used");
                }
            }
        }

        string FeaturesLine(TablesSettings s)
        {
            var parts = new List<string>();
            foreach (Profile p in Profile.All)
            {
                string locale, type;
                session.Data.FeatureOption(p, out locale, out type);
                parts.Add(p.Name + " " + locale + "/" + type + (s.FeaturesFrom[p.Root] != null ? " (" + s.FeaturesFrom[p.Root] + ")" : ""));
            }
            return string.Join(", ", parts.ToArray());
        }

        // a field was left or Enter was pressed in it: check, save, use
        void FeaturesEdited(Profile p)
        {
            TextBox box = featureBoxes[p.Root];
            if (box.ReadOnly || session == null || IsBusy()) return;
            string value = box.Text.Trim(), locale, type;
            Guard(delegate
            {
                session.Data.SetFeatures(p, value);     // refuses a text that is not LOCALE/TYPE
                if (value == (box.Tag as string ?? "")) return;
                box.Tag = value;
                TablesSettings.SaveFeatures(p, value);
                session.Data.FeatureOption(p, out locale, out type);
                Say("features of the " + p.Title + ": " + locale + "/" + type + (value.Length == 0 ? " (its own)" : "") + ". Saved; Plan uses it from now on.");
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

        ApplyTarget TargetChosen() { return targetBox.SelectedIndex == 1 ? ApplyTarget.LooseFiles : ApplyTarget.Workspace; }

        // ---- the mod list ----

        /// <summary>Reads the mods folder again and shows its list; the selection stays on the same folder.</summary>
        void FillMods()
        {
            string selected = modList != null && modsList.SelectedIndex >= 0 && modsList.SelectedIndex < modList.Items.Count
                ? modList.Items[modsList.SelectedIndex].Folder : null;
            bool was = filling;
            filling = true;
            try
            {
                modsList.Items.Clear();
                modList = null;
                if (session == null) return;
                modList = session.ModList();
                foreach (ModListItem it in modList.Items)
                {
                    bool editing = it.Error == null && it.Id == session.Mod.Id;
                    modsList.Items.Add((modsList.Items.Count + 1) + ". " + it + (editing ? "  [editing, always applied]" : ""), it.Enabled);
                    if (it.Folder == selected) modsList.SelectedIndex = modsList.Items.Count - 1;
                }
            }
            finally { filling = was; }
        }

        void OnModCheck(object sender, ItemCheckEventArgs e)
        {
            if (filling || modList == null || e.Index < 0 || e.Index >= modList.Items.Count) return;
            // the box takes one click (CheckOnClick); a press on the name selects the mod or starts a drag and leaves the check
            if (pressOnName) { e.NewValue = e.CurrentValue; return; }
            ModListItem it = modList.Items[e.Index];
            bool on = e.NewValue == CheckState.Checked;
            if (!Guard(delegate { it.Enabled = on; modList.Save(); })) { it.Enabled = !on; e.NewValue = e.CurrentValue; }
        }

        ModListItem SelectedMod()
        {
            if (modList == null || modsList.SelectedIndex < 0 || modsList.SelectedIndex >= modList.Items.Count)
                throw new ModKitException("select a mod in the list first");
            return modList.Items[modsList.SelectedIndex];
        }

        void MoveMod(int delta)
        {
            SelectedMod();
            MoveModTo(modsList.SelectedIndex, modsList.SelectedIndex + delta);
        }

        void MoveModTo(int from, int to)
        {
            to = modList.MoveTo(from, to);
            modList.Save();
            FillMods();
            modsList.SelectedIndex = to;
        }

        // dragging a mod: it moves as soon as the pointer is over another row, and the order is saved at once
        int dragFrom = -1;
        bool pressOnName;

        void OnModMouseDown(object sender, MouseEventArgs e)
        {
            // not on the check box at the left, a click there only checks (the box is as wide as a row is high,
            // at any display scale)
            bool name = e.Button == MouseButtons.Left && e.X > modsList.ItemHeight + 1;
            pressOnName = name;
            dragFrom = name && modList != null ? modsList.IndexFromPoint(e.Location) : -1;
        }

        void OnModMouseMove(object sender, MouseEventArgs e)
        {
            if (dragFrom < 0 || e.Button != MouseButtons.Left || modList == null || dragFrom >= modList.Items.Count) return;
            int over = modsList.IndexFromPoint(e.Location);
            if (over < 0 || over == dragFrom || over >= modList.Items.Count) return;
            modsList.Cursor = Cursors.SizeNS;
            int from = dragFrom, to = over;
            if (Guard(delegate { MoveModTo(from, to); })) dragFrom = to; else dragFrom = -1;
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
            // the columns are added with fixed widths, then share the full width (ViewLayout.FillColumns); the frozen
            // row column keeps its width (a grid in fill mode refuses a frozen column that fills)
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            grid.Columns.Add(ViewLayout.FitColumn(new DataGridViewTextBoxColumn { HeaderText = "row", ReadOnly = true, Width = 190, Frozen = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, SortMode = DataGridViewColumnSortMode.NotSortable }));
            foreach (string c in d.Columns) AddGridColumn(c);
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.RowCount = d.Rows.Count;
            tableLabel.Text = d.Archive + "  " + d.Entry + (d.ParentSelector.Length > 0 ? "  " + d.ParentSelector : "");
        }

        void AddGridColumn(string name)
        {
            columns.Add(name);
            grid.Columns.Add(ViewLayout.FitColumn(new DataGridViewTextBoxColumn { HeaderText = name, Width = 90, SortMode = DataGridViewColumnSortMode.NotSortable }));
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
            NotifyChanged();
        }

        // the pending pane shows this much of mod.xml (the 32-bit host runs out of memory on a whole text import)
        const int PendingChars = 200000;

        void ShowMod()
        {
            if (session == null) { pendingBox.Text = ""; return; }
            modIdBox.Text = session.Mod.Id;
            pendingBox.Text = session.Mod.IsEmpty
                ? "(mod " + session.Mod.Id + " holds no change yet)"
                : session.Mod.PendingText(PendingChars);
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
            NotifyChanged();
        }

        // ---- the mod ----

        void NewMod()
        {
            EditorSession s = Session();
            grid.CancelEdit();
            if (!s.Mod.IsEmpty && MessageBox.Show(this, "Drop the pending changes of mod " + s.Mod.Id + "?", "MS2 Tables",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            s.NewMod(modIdBox.Text.Trim());
            MarkSaved();
            ReopenTable();
            ShowMod();
            FillMods();
            Say("new mod " + s.Mod.Id);
            NotifyChanged();
        }

        void SaveMod()
        {
            FlushEdits();
            string dir = Session().SaveMod();
            MarkSaved();
            FillMods();
            List<string> gone = Session().Mod.Removed;
            Say("saved " + Path.Combine(dir, "mod.xml") + (gone.Count == 0 ? "" : "; took out " + gone.Count + " file(s) no entry names any more: " + string.Join(", ", gone.ToArray())));
        }

        void LoadMod()
        {
            EditorSession s = Session();
            ModListItem it = SelectedMod();
            if (it.Error != null) throw new ModKitException(it.Error);
            grid.CancelEdit();
            if (!s.Mod.IsEmpty && MessageBox.Show(this, (s.Mod.Id == it.Id ? "Load mod " + it.Id + " again as it was saved?" : "Load mod " + it.Id + "?") + " Changes of mod " + s.Mod.Id + " that are not saved are dropped.",
                    "MS2 Tables", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            s.LoadMod(it.Folder);
            MarkSaved();
            ReopenTable();
            ShowMod();
            FillMods();
            Say("loaded mod " + s.Mod.Id + " from " + s.Mod.Dir + ": " + (s.Mod.Ops.Count + s.Mod.Groups.Count) + " operation block(s)");
            NotifyChanged();
        }

        // ---- unsaved changes ----

        // a print of the mod's text (length and hash, Mod.XmlPrint) when it was last saved, loaded or started; the mod
        // has unsaved changes when the print differs or it holds files not written yet (Mod.Files: trigger scripts).
        // A print, not the text: a text import's mod.xml is tens of MB, too much to keep twice in the 32-bit host
        string savedPrint;
        bool closeAsked;

        void MarkSaved() { savedPrint = session != null ? session.Mod.XmlPrint() : null; }

        bool Unsaved()
        {
            if (session == null) return false;
            try { FlushEdits(); } catch (Exception) { }
            return session.Mod.XmlPrint() != savedPrint || session.Mod.Files.Count > 0;
        }

        /// <summary>
        /// Asks what to do with unsaved changes before the view or Toolbench closes: Yes saves the mod, No drops
        /// the changes, Cancel keeps the window open. False means "do not close".
        /// </summary>
        bool ConfirmClose()
        {
            if (!Unsaved()) return true;
            DialogResult r = MessageBox.Show(this, "Mod " + session.Mod.Id + " has changes that are not saved. Save them before closing?",
                "MS2 Tables", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.No) return true;
            try { SaveMod(); return true; }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The mod was not saved: " + ex.Message, "MS2 Tables", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        /// <summary>Toolbench calls this when the view's pane is closed.</summary>
        public override bool OnClose(bool closeContent, bool checkDirty)
        {
            if (!closeAsked && !ConfirmClose()) return false;
            return base.OnClose(closeContent, checkDirty);
        }

        // Toolbench does not ask its views when the main window closes (and its window raises no FormClosing);
        // it runs the handlers of its ClosingHandler extension point, and ours (ClosingHandler) calls this.
        internal static TablesView Open;

        // made by another view for its job (Find, Scan, a lint): a tab behind that view, not brought to the front
        bool behind;
        static bool makingBehind;

        /// <summary>
        /// The open MS2 Tables view, or a new one made as a tab behind the view in front (another view works on its
        /// data and jobs); null when it cannot be made.
        /// </summary>
        internal static TablesView OpenBehind()
        {
            if (Open != null && !Open.IsDisposed) return Open;
            IViewService views = Platform.GetService<IViewService>();
            if (views == null) return null;
            makingBehind = true;
            try { return views.CreateOrFindView(ViewId) as TablesView; }
            finally { makingBehind = false; }
        }

        /// <summary>False keeps Toolbench open.</summary>
        internal bool ConfirmShutdown()
        {
            if (IsDisposed) return true;
            if (IsBusy()) { Say("wait for the running job before closing"); return false; }
            if (!ConfirmClose()) return false;
            closeAsked = true;      // the views are closed next; do not ask a second time
            // if something else keeps the window open after all, ask again the next time
            try { BeginInvoke((MethodInvoker)delegate { closeAsked = false; }); } catch (Exception) { }
            return true;
        }

        // ---- for the other views of the plugin (MS2 Items, MS2 NPCs) ----
        //
        // The session, the mod being edited, the worker thread and the close question are this view's. Another view
        // edits the same mod through the session and tells this view; it does not keep any of them itself.

        /// <summary>The session or its mod was replaced or edited here: what another view shows of the mod is stale.</summary>
        internal event MethodInvoker Changed;
        /// <summary>The mod is about to be read (save, plan, apply, close question): commit edits still in a cell.</summary>
        internal event MethodInvoker Flushing;
        /// <summary>A job started (true) or ended (false) on the worker thread.</summary>
        internal event Action<bool> BusyChanged;
        /// <summary>The text of the output pane changed.</summary>
        internal event Action<string> Said;

        void NotifyChanged() { if (Changed != null) Changed(); }

        void FlushEdits()
        {
            grid.EndEdit();
            if (Flushing != null) Flushing();
        }

        internal EditorSession SharedSession() { return Session(); }

        internal bool Busy { get { return busy != null; } }

        /// <summary>Another view changed the mod: the table on screen and the pending pane follow.</summary>
        internal void EditedOutside()
        {
            ReopenTable();
            ShowMod();
        }

        /// <summary>Save mod, for another view.</summary>
        internal void SaveFromOutside() { SaveMod(); }

        /// <summary>Runs a command of another view the way this view runs its own: errors go to the output pane.</summary>
        internal bool RunGuarded(MethodInvoker body) { return Guard(body); }

        internal void SayOutside(string text) { Say(text); }

        /// <summary>
        /// Runs work of another view as a job (on the worker thread, this view locked), then done on the window's
        /// thread; errors go to the output pane. False when a job is running already.
        /// </summary>
        internal bool RunOutside(string what, MethodInvoker work, MethodInvoker done)
        {
            NeedHandle();
            return RunJob(what, delegate(Action<ProgressInfo> progress) { work(); }, done);
        }

        internal string OutputText { get { return OutputWrap.Raw(outputBox); } }

        /// <summary>The "Apply to" choice (0 = workspace, 1 = game folder), for another view that shows it too.</summary>
        internal int TargetIndex
        {
            get { return targetBox.SelectedIndex; }
            set { if (value >= 0 && value < targetBox.Items.Count) targetBox.SelectedIndex = value; }
        }

        /// <summary>The "Apply to" choice changed, here or by the session being loaded.</summary>
        internal event MethodInvoker TargetChanged;

        /// <summary>
        /// The view whose buttons were used last: true = MS2 Items or MS2 NPCs. The MS2 Mods menu commands leave the Tables
        /// view where it is when one of those is the one in use.
        /// </summary>
        internal static bool ItemsInUse;

        protected override void OnEnter(EventArgs e)
        {
            ItemsInUse = false;
            base.OnEnter(e);
        }

        /// <summary>The worker thread reports back through this view's window handle; a view never shown has none yet.</summary>
        // A handle made here is for another view's job (the view was restored at start-up but never shown): the view
        // becomes a tab behind the one in front when it first goes to the centre area.
        internal void NeedHandle()
        {
            if (IsHandleCreated) return;
            behind = true;
            CreateControl();
            if (!IsHandleCreated) { IntPtr h = Handle; }
        }

        // ---- jobs on the worker thread ----

        delegate void Job(Action<ProgressInfo> progress);

        bool IsBusy()
        {
            if (busy == null) return false;
            Say(busy + " is still running");
            return true;
        }

        void SetBusy(string what)
        {
            busy = what;
            foreach (Control c in lockable) c.Enabled = what == null;
            busyLabel.Text = what == null ? "" : what + "...";
            UseWaitCursor = what != null;
            if (BusyChanged != null) BusyChanged(what != null);
        }

        /// <summary>
        /// Runs work on a worker thread while the view is locked, then done on the window's thread. The work must
        /// not touch a control; what it throws is shown in the output pane. Returns false when a job is running.
        /// </summary>
        bool RunJob(string what, Job work, MethodInvoker done)
        {
            if (IsBusy()) return false;
            SetBusy(what);
            Say(what + "...");
            var t = new Thread(delegate()
            {
                Exception error = null;
                try { work(delegate(ProgressInfo p) { OnProgress(what, p); }); }
                catch (Exception ex) { error = ex; }
                Post(delegate
                {
                    worker = null;
                    SetBusy(null);
                    if (error == null) { Guard(done); return; }
                    bool known = error is ModKitException || error is IOException || error is UnauthorizedAccessException;
                    Say(known ? error.Message : what + " failed: " + error);
                    Logger.Warning("MS2 Tables: " + what + ": " + error.Message);
                });
            });
            t.Name = "MS2 Tables " + what;
            t.IsBackground = false;     // an apply that is writing is not cut off when the window closes
            worker = t;
            t.Start();
            return true;
        }

        void Post(MethodInvoker m)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(m); }
            catch (InvalidOperationException) { }     // the view went away meanwhile
        }

        // on the worker thread: at most ten updates a second reach the window
        void OnProgress(string what, ProgressInfo p)
        {
            int now = Environment.TickCount;
            if (unchecked(now - lastProgress) < 100) return;
            lastProgress = now;
            string text = what + ": " + p.Stage + (string.IsNullOrEmpty(p.Item) ? "" : " " + p.Item) + (p.Total > 0 ? " " + p.Done + "/" + p.Total : "");
            Post(delegate { if (busy != null) busyLabel.Text = text; });
        }

        // ---- plan, apply, revert (also behind the MS2 Mods menu) ----

        /// <summary>Starts the plan; true when it was started. The result goes to the output pane when it is done.</summary>
        public bool DoPlan()
        {
            Plan p = null;
            return Guard(delegate { FlushEdits(); Session(); })
                && RunJob("Plan", delegate(Action<ProgressInfo> progress) { p = session.Plan(progress); }, delegate
                {
                    Say(session.Describe(p));
                    Logger.Info("MS2 Tables: plan: " + p.Changes.Count + " change(s), " + p.Errors.Count + " error(s), " + p.Conflicts.Count + " conflict(s)");
                });
        }

        public bool DoApply()
        {
            ApplyResult r = null;
            return Guard(delegate { FlushEdits(); Session(); })
                && RunJob("Apply", delegate(Action<ProgressInfo> progress) { r = session.Apply(progress); }, delegate
                {
                    FillMods();
                    ShowMod();
                    Say(session.Describe(r));
                    if (r.Applied) Logger.Info("MS2 Tables: applied " + r.Plan.Mods.Count + " mod(s) to " + Where());
                    else Logger.Warning("MS2 Tables: nothing applied: " + r.Reason);
                });
        }

        /// <summary>The sync report of one item over the enabled mods and the pending edits; into the output pane.</summary>
        public bool DoSync(long id, bool all) { return DoSync("item", id, all); }

        /// <summary>The sync report of one id of an entity ("item", "npc"); into the output pane.</summary>
        public bool DoSync(string entity, long id, bool all)
        {
            SyncReport r = null;
            return Guard(delegate { FlushEdits(); Session(); if (id <= 0) throw new ModKitException("choose an " + entity + " in the list first"); })
                && RunJob("Sync report", delegate(Action<ProgressInfo> progress) { r = session.Sync(entity, id, session.Plan(progress)); }, delegate
                {
                    Say(r.ToText(all));
                });
        }

        /// <summary>The rows of server-only tables that belong to one item, as the mods leave them; into the output pane.</summary>
        public bool DoRefs(long id) { return DoRefs("item", id); }

        /// <summary>The rows of server-only tables that belong to one id of an entity, as the enabled mods and the mod being edited leave them; into the output pane.</summary>
        public bool DoRefs(string entity, long id)
        {
            RefList r = null;
            bool after = false;
            return Guard(delegate { Session(); if (id <= 0) throw new ModKitException("choose an " + entity + " in the list first"); })
                && RunJob("Server rows", delegate(Action<ProgressInfo> progress)
                {
                    Plan p = session.Plan(progress);
                    after = !p.HasErrors && p.Archives.Count > 0;
                    r = session.Refs(entity, id, 20, after ? p : null);
                }, delegate
                {
                    Say(entity + " " + id + ", rows of server tables that belong to it (" + (after ? "as the mods leave them" : "as shipped") + "). To change one, open its archive and entry in the MS2 Tables view; the last column is the row.\r\n" + r.ToText());
                });
        }

        public bool DoRevert()
        {
            ApplyResult r = null;
            return Guard(delegate { Session(); })
                && RunJob("Revert", delegate(Action<ProgressInfo> progress) { r = session.Revert(); }, delegate
                {
                    Say(session.Describe(r));
                    Logger.Info("MS2 Tables: revert removed " + r.Removed.Count + " file(s)");
                });
        }

        string Where()
        {
            return session.Target == ApplyTarget.LooseFiles ? session.GameDir + " (loose files)" : session.WorkspaceDir;
        }
    }
}
