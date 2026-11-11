// The "MS2 NPCs" view: one npc at a time, in the style of the MS2 Items view. A search box (id or name), the list of
// hits, the fields the field map pairs between the kms-client and the server as one form (a value set there is
// written to every target that has the field), below it every field of the npc's rows and files, the name and title,
// Clone to new id (with name, level and hp), and its own Plan / Apply / Revert buttons and output box.
//
// The view holds no data logic and no mod of its own. MS2.ModKit.NpcEditor turns what is done here into operations
// of the mod the MS2 Tables view is editing; the session, the worker thread (Plan / Apply / Revert), the output text
// and the close question stay in that view (TablesView, "for the other views of the plugin").
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
    public class NpcsView : AbstractView
    {
        public const string ViewId = "MS2.Tables.NpcsView";
        const int MaxHits = 500;

        sealed class GridRow
        {
            internal string Heading;        // a heading row
            internal string Mapped;         // a field of the form (the map's client side name)
            internal EntitySection Section; // else: a field of one section
            internal string Field;
        }

        TablesView host;
        NpcEditor editor;
        NpcDraft draft;
        readonly List<GridRow> rows = new List<GridRow>();
        bool filling;

        readonly TextBox searchBox = new TextBox { Width = 200 };
        readonly ComboBox languageBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };
        readonly Label hitsLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListBox hitList = new ListBox { Dock = DockStyle.Left, Width = 280, IntegralHeight = false };
        readonly Label npcLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 8, 0) };
        readonly TextBox cloneIdBox = new TextBox { Width = 80 };
        readonly TextBox cloneNameBox = new TextBox { Width = 160 };
        readonly TextBox cloneLevelBox = new TextBox { Width = 40 };
        readonly TextBox cloneHpBox = new TextBox { Width = 90 };
        readonly TextBox nameBox = new TextBox { Width = 220 };
        readonly TextBox titleBox = new TextBox { Width = 200 };
        readonly CheckBox oneLanguageBox = new CheckBox { Text = "only in this language", AutoSize = true, Margin = new Padding(8, 6, 3, 0) };
        readonly DataGridView grid = new DataGridView { Dock = DockStyle.Fill };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 130 };
        readonly ComboBox targetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        readonly ToolTip tips = new ToolTip();
        readonly IconBox portraitBox = new IconBox { Dock = DockStyle.Left, Width = 96, Margin = new Padding(0) };
        readonly List<Control> lockable = new List<Control>();

        public NpcsView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(Lbl("NPC id or name"));
            top.Controls.Add(searchBox);
            top.Controls.Add(Btn("Find", "Lists the npcs whose id starts with the text or whose name contains it; the npcs the mod adds come first", delegate { Find(); }));
            top.Controls.Add(Lbl("Names in"));
            top.Controls.Add(languageBox);
            tips.SetToolTip(languageBox, "The language folder (string/<language>) whose names are searched and shown, and the language \"only in this language\" writes to");
            top.Controls.Add(hitsLabel);

            FlowLayoutPanel applyBar = Bar();
            applyBar.Controls.Add(Lbl("Apply to"));
            targetBox.Items.Add("workspace (patched archives)");
            targetBox.Items.Add("game folder (loose files)");
            tips.SetToolTip(targetBox, "Where Apply writes the client's tables (the same choice as in the MS2 Tables view): patched copies of the archives in the workspace folder, or plain files under Data/ of the game folder. The archives of the server and of the global client (gms-client) always go to the workspace.");
            applyBar.Controls.Add(targetBox);
            applyBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change, entry by entry for each target (kms-client, gms-client, server); writes nothing", delegate { return host.DoPlan(); }));
            applyBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change, to every target at once", delegate { return host.DoApply(); }));
            applyBar.Controls.Add(Job("Revert", "Removes what Apply wrote, on every target", delegate { return host.DoRevert(); }));
            applyBar.Controls.Add(Job("Sync report", "For the chosen npc: the fields the kms-client and the server both hold, where the two disagree as shipped and after the mods, and what the mods change", delegate { return host.DoSync(NpcEditor.IdSpace, draft != null ? draft.Id : 0, false); }));
            applyBar.Controls.Add(Job("All fields", "The sync report with every mapped field of the chosen npc, also those that agree or that one side does not carry", delegate { return host.DoSync(NpcEditor.IdSpace, draft != null ? draft.Id : 0, true); }));

            FlowLayoutPanel npcBar = Bar();
            npcBar.Controls.Add(npcLabel);
            npcBar.Controls.Add(Btn("Clone to new id", "Adds a new npc that starts as a copy of this one, under the id in the box (empty = the next free id of the npc's block of 100000), with the name, level and hp in the boxes beside it (empty = the source's). The copy has the npc row, the per-id file, the name and title rows in every language and the face and talk files of the source, on every target", delegate { Clone(); }));
            npcBar.Controls.Add(cloneIdBox);
            npcBar.Controls.Add(Lbl("name"));
            npcBar.Controls.Add(cloneNameBox);
            npcBar.Controls.Add(Lbl("level"));
            npcBar.Controls.Add(cloneLevelBox);
            npcBar.Controls.Add(Lbl("hp"));
            npcBar.Controls.Add(cloneHpBox);
            npcBar.Controls.Add(Btn("Where used", "Lists the quests (start, complete npc, conditions) that name this npc, the mod's first, with Go to", delegate { ShowWhereUsed(); }));
            npcBar.Controls.Add(Btn("Remove clone", "Takes the chosen new npc out of the mod again, with every row and file the clone added", delegate { RemoveClone(); }));
            npcBar.Controls.Add(Btn("Open shop", "Opens the npc's shop (shopId, as the mod leaves it) in the MS2 Shops and Drops view", delegate { OpenShop(); }));
            npcBar.Controls.Add(Btn("Open drop box", "Opens the npc's first drop box (individualDropBoxId, else the other drop box ids, as the mod leaves them) in the MS2 Shops and Drops view", delegate { OpenBox(); }));
            npcBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same mod and the same Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            FlowLayoutPanel textBar = Bar();
            textBar.Controls.Add(Lbl("Name"));
            textBar.Controls.Add(nameBox);
            textBar.Controls.Add(Lbl("Title"));
            textBar.Controls.Add(titleBox);
            textBar.Controls.Add(oneLanguageBox);
            tips.SetToolTip(oneLanguageBox, "Ticked: the name and title are written to the language chosen under \"Names in\" only, on every target that has it. Unticked: to every language of every target");
            textBar.Controls.Add(Btn("Set texts", "Writes the name and the title into the mod. A language that has a title file but no title row of the npc gets one", delegate { SetTexts(); }));

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
            grid.Columns.Add(ViewLayout.FitColumn(new DataGridViewTextBoxColumn { HeaderText = "field", ReadOnly = true, Width = 330, SortMode = DataGridViewColumnSortMode.NotSortable }));
            grid.Columns.Add(ViewLayout.FitColumn(new DataGridViewTextBoxColumn { HeaderText = "value", Width = 260, SortMode = DataGridViewColumnSortMode.NotSortable }));
            grid.Columns.Add(ViewLayout.FitColumn(new DataGridViewTextBoxColumn { HeaderText = "shipped value", ReadOnly = true, Width = 260, SortMode = DataGridViewColumnSortMode.NotSortable }));
            grid.CellValueNeeded += OnCellValueNeeded;
            grid.CellValuePushed += OnCellValuePushed;
            grid.CellFormatting += OnCellFormatting;
            grid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e)
            {
                GridRow r = RowAt(e.RowIndex);
                if (r == null || r.Heading != null) e.Cancel = true;
            };
            // the shop and the drop boxes are ids of the Shops and drop boxes view (picked by id), the skills and
            // additional effects ids of the Skills view
            IdPick.Attach(grid, delegate(int row, int column)
            {
                GridRow r = RowAt(row);
                return r != null && r.Heading == null && column == 1 && draft != null ? IdLookup.NpcFieldKind(FieldOf(r)) : null;
            }, delegate { return host.SharedSession(); }, delegate(int row, int column)
            {
                GridRow r = RowAt(row);
                return r != null && IdLookup.IsIdList(FieldOf(r));
            });

            // the npc's portrait at the left of the two bars above the grid, as in the item form: 96 pixels, 64 in a
            // low window, and the bars never take more than the height that leaves the grid a header and four rows
            var bars = new Panel { Dock = DockStyle.Fill };
            bars.Controls.Add(textBar);
            bars.Controls.Add(npcBar);
            var portraitPad = new Panel { Dock = DockStyle.Left, Width = 104, Padding = new Padding(6, 4, 2, 4) };
            portraitPad.Controls.Add(portraitBox);
            var head = new Panel { Dock = DockStyle.Top, Height = 104 };
            head.Controls.Add(bars);
            head.Controls.Add(portraitPad);
            tips.SetToolTip(portraitBox, "The npc's portrait (environment@portrait), read from Resource/Image.m2d of your client or server data; an empty box: no data folder has that image");

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(grid);
            right.Controls.Add(head);
            bool fitting = false;
            EventHandler fitHead = delegate
            {
                if (fitting) return;
                fitting = true;
                try
                {
                    int pic = right.ClientSize.Height < 480 ? 64 : 96;
                    if (portraitBox.Width != pic) { portraitBox.Width = pic; portraitPad.Width = pic + portraitPad.Padding.Horizontal; }
                    int want = Math.Max(pic + portraitPad.Padding.Vertical, npcBar.Height + textBar.Height);
                    int room = right.ClientSize.Height - (grid.ColumnHeadersHeight + 4 * grid.RowTemplate.Height + 4);
                    if (room > 0) want = Math.Min(want, Math.Max(pic + portraitPad.Padding.Vertical, room));
                    if (head.Height != want) head.Height = want;
                }
                finally { fitting = false; }
            };
            right.Resize += fitHead;
            bars.Layout += delegate { fitHead(bars, EventArgs.Empty); };

            Controls.Add(right);
            Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
            Controls.Add(hitList);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(outputBox);
            Controls.Add(applyBar);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, applyBar, hitList, right });
            ViewLayout.KeepLogLines(outputBox, this);
            ViewLayout.CentreOnFirstShow(this);
            targetBox.SelectedIndexChanged += delegate { if (!filling && host != null) host.TargetIndex = targetBox.SelectedIndex; };

            searchBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                Guard(delegate { Find(); });
            };
            hitList.SelectedIndexChanged += delegate { if (!filling) Guard(delegate { OpenSelected(); }); };
            languageBox.SelectedIndexChanged += delegate
            {
                if (filling) return;
                Guard(delegate { Editor().Language = (string)languageBox.SelectedItem; Find(); });
            };
        }

        public override void Initialize()
        {
            base.Initialize();
            Guard(delegate { Find(); });
        }

        readonly BoldFont bold = new BoldFont();   // the heading lines of the grids

        protected override void Dispose(bool disposing)
        {
            if (disposing) { Hook(null); bold.Dispose(); }
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

        /// <summary>A button for Plan, Apply or Revert: the command starts a job of the Tables view and returns at once.</summary>
        Button Job(string text, string tip, Func<bool> start)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, UseVisualStyleBackColor = true, ForeColor = SystemColors.ControlText };
            b.Click += delegate
            {
                if (Guard(delegate { grid.EndEdit(); host.NeedHandle(); })) start();
            };
            tips.SetToolTip(b, tip);
            return b;
        }

        protected override void OnEnter(EventArgs e)
        {
            TablesView.ItemsInUse = true;
            base.OnEnter(e);
        }

        void OnHostTarget()
        {
            bool was = filling;
            filling = true;
            targetBox.SelectedIndex = host.TargetIndex;
            filling = was;
        }

        // ---- the MS2 Tables view: session, mod, worker thread, output, close question ----

        void Hook(TablesView h)
        {
            if (h == host) return;
            if (host != null)
            {
                host.Changed -= OnHostChanged;
                host.Flushing -= OnHostFlushing;
                host.BusyChanged -= OnHostBusy;
                host.Said -= OnHostSaid;
                host.TargetChanged -= OnHostTarget;
            }
            host = h;
            editor = null;
            if (h == null) return;
            h.Changed += OnHostChanged;
            h.Flushing += OnHostFlushing;
            h.BusyChanged += OnHostBusy;
            h.Said += OnHostSaid;
            h.TargetChanged += OnHostTarget;
            OnHostTarget();
            OutputWrap.Set(outputBox, h.OutputText);
            OnHostBusy(h.Busy);
        }

        /// <summary>Runs one command of this view; what goes wrong is shown in the output of both views.</summary>
        bool Guard(MethodInvoker body)
        {
            try
            {
                TablesView h = TablesView.Open;
                if (h == null || h.IsDisposed)
                {
                    IViewService views = Platform.GetService<IViewService>();
                    h = views == null ? null : views.CreateOrFindView(TablesView.ViewId) as TablesView;
                }
                if (h == null) { OutputWrap.Set(outputBox, "the MS2 Tables view could not be opened; the npc editor works on the mod of that view"); return false; }
                Hook(h);
            }
            catch (Exception ex)
            {
                OutputWrap.Set(outputBox, "the MS2 Tables view could not be opened: " + ex.Message);
                Logger.Warning("MS2 NPCs: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        NpcEditor Editor()
        {
            EditorSession s = host.SharedSession();
            if (editor == null || editor.Mod != s.Mod || editor.Data != s.Data)
            {
                string language = editor != null ? editor.Language : null;
                editor = s.Npcs();
                if (language != null && editor.Languages.Contains(language)) editor.Language = language;
                filling = true;
                languageBox.Items.Clear();
                foreach (string l in editor.Languages) languageBox.Items.Add(l);
                languageBox.SelectedItem = editor.Language;
                filling = false;
            }
            return editor;
        }

        void OnHostSaid(string text) { OutputWrap.Set(outputBox, text); }

        void OnHostBusy(bool on)
        {
            foreach (Control c in lockable) c.Enabled = !on;
            UseWaitCursor = on;
        }

        void OnHostFlushing() { grid.EndEdit(); }

        void OnHostChanged()
        {
            grid.CancelEdit();
            long id = draft != null ? draft.Id : 0;
            ShowNpc(null);
            try
            {
                FillHits(id);
                if (id != 0 && SelectedHit() != null) ShowNpc(Editor().Open(id));
            }
            catch (ModKitException) { ShowNpc(null); }
            catch (System.IO.IOException) { ShowNpc(null); }
        }

        void Edited()
        {
            grid.Invalidate();
            host.EditedOutside();
        }

        // ---- search ----

        void Find()
        {
            grid.EndEdit();
            FillHits(draft != null ? draft.Id : 0);
            if (hitList.Items.Count > 0 && hitList.SelectedIndex < 0) hitList.SelectedIndex = 0;
        }

        void FillHits(long keep)
        {
            List<EntityHit> hits = Editor().Find(searchBox.Text, MaxHits);
            filling = true;
            try
            {
                hitList.BeginUpdate();
                hitList.Items.Clear();
                foreach (EntityHit h in hits)
                {
                    hitList.Items.Add(h);
                    if (h.Id == keep) hitList.SelectedIndex = hitList.Items.Count - 1;
                }
                hitList.EndUpdate();
            }
            finally { filling = false; }
            hitsLabel.Text = hits.Count + (hits.Count >= MaxHits ? "+" : "") + " npc(s)";
        }

        EntityHit SelectedHit() { return hitList.SelectedItem as EntityHit; }

        void OpenSelected()
        {
            EntityHit h = SelectedHit();
            if (h == null) return;
            grid.EndEdit();
            ShowNpc(Editor().Open(h.Id));
            host.SayOutside(Describe(draft));
        }

        /// <summary>Where the npc is, per target; for a new npc also every place its id is registered.</summary>
        string Describe(NpcDraft d)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("npc ").Append(d.Id).Append(d.IsNew ? " (new in mod " + editor.Mod.Id + ")" : "").Append(": ");
            foreach (string target in editor.Sides())
            {
                sb.Append("\r\n").Append(DataSet.RootOf(target)).Append(':');
                foreach (EntityPart p in d.Parts)
                    if (p.Archive == target) sb.Append("\r\n  ").Append(p.Label).Append(": ").Append(p.Entry).Append(p.IsNew ? "  (new)" : "");
            }
            if (d.IsNew)
            {
                sb.Append("\r\n\r\nA new npc id is written to:");
                foreach (string l in editor.Registration(d.Id)) sb.Append("\r\n  ").Append(l);
                sb.Append("\r\nNo other table lists npcs that exist; place the npc with the server's spawn command or a spawn table.");
            }
            return sb.ToString();
        }

        // ---- the npc ----

        void ShowNpc(NpcDraft d)
        {
            grid.CancelEdit();
            grid.RowCount = 0;
            rows.Clear();
            draft = d;
            if (d == null)
            {
                npcLabel.Text = "(no npc)";
                portraitBox.Show(null, null);
                nameBox.Text = titleBox.Text = cloneIdBox.Text = cloneNameBox.Text = cloneLevelBox.Text = cloneHpBox.Text = "";
                return;
            }
            rows.Add(new GridRow { Heading = "Fields on every target (field map)" });
            foreach (string f in editor.MappedFields())
                if (d.GetField(f) != null || d.GetFieldShipped(f) != null) rows.Add(new GridRow { Mapped = f });
            foreach (EntitySection s in d.Sections)
            {
                if (s.Part.Kind != editor.Entity && s.Part.Kind != EntityEditor.FileKind) continue;
                rows.Add(new GridRow { Heading = DataSet.RootOf(s.Part.Archive) + ": " + s.Title });
                foreach (string f in s.Fields) rows.Add(new GridRow { Section = s, Field = f });
            }
            grid.RowCount = rows.Count;
            npcLabel.Text = "NPC " + d.Id.ToString(CultureInfo.InvariantCulture) + (d.IsNew ? " (new)" : "");
            ShowPortrait(d);
            nameBox.Text = d.Name ?? "";
            titleBox.Text = d.Title ?? "";
            cloneIdBox.Text = cloneNameBox.Text = cloneLevelBox.Text = cloneHpBox.Text = "";
        }

        void ShowPortrait(NpcDraft d)
        {
            string entry = null;
            try { entry = d.PortraitEntry(); }
            catch (ModKitException) { }
            IconCache cache = null;
            if (host != null)
            {
                try { cache = IconCache.For(host.SharedSession().Data); }
                catch (ModKitException) { }
                catch (System.IO.IOException) { }
            }
            portraitBox.Show(cache, entry);
            tips.SetToolTip(portraitBox, entry == null ? "This npc names no portrait" : "Portrait: " + entry + " in Resource/Image.m2d");
        }

        NpcDraft Draft()
        {
            if (draft == null) throw new ModKitException("choose an npc in the list first");
            return draft;
        }

        GridRow RowAt(int index) { return index >= 0 && index < rows.Count ? rows[index] : null; }

        /// <summary>The field of a row as "element@attribute" (a field of the form: its client side name).</summary>
        static string FieldOf(GridRow r)
        {
            if (r.Mapped != null) return r.Mapped;
            return r.Section != null && r.Section.Elem != null ? r.Section.Elem.Name + "@" + r.Field : r.Field;
        }

        string Value(GridRow r) { return r.Mapped != null ? draft.GetField(r.Mapped) : draft.Get(r.Section, r.Field); }
        string Shipped(GridRow r) { return r.Mapped != null ? draft.GetFieldShipped(r.Mapped) : draft.GetOriginal(r.Section, r.Field); }

        void OnCellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            GridRow r = RowAt(e.RowIndex);
            if (r == null || draft == null) return;
            if (r.Heading != null) { e.Value = e.ColumnIndex == 0 ? r.Heading : ""; return; }
            if (e.ColumnIndex == 0) e.Value = (Value(r) != Shipped(r) ? "* " : "   ") + (r.Mapped ?? r.Field);
            else if (e.ColumnIndex == 1) e.Value = Value(r);
            else e.Value = Shipped(r);
        }

        void OnCellValuePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            GridRow r = RowAt(e.RowIndex);
            if (r == null || r.Heading != null || e.ColumnIndex != 1 || draft == null) return;
            string value = e.Value == null ? null : e.Value.ToString();
            if (value == null && Value(r) != null) value = "";
            if (value == Value(r)) return;
            Guard(delegate
            {
                string said;
                if (r.Mapped != null)
                {
                    int n, level;
                    if (r.Mapped == "@level" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out level)) n = draft.SetLevel(level);
                    else n = draft.SetBoth(r.Mapped, value);
                    said = r.Mapped + " = " + value + " set in " + n + " place(s) (the npc row and the per-id files; Plan lists them per target)";
                }
                else
                {
                    // a one-sided edit: Plan carries a mapped field to the other side, the same way as for a mod file
                    EntitySection also = draft.Set(r.Section, r.Field, value, true);
                    said = r.Section.Title + "  " + r.Field + " = " + value + (also != null ? "\r\nPlan carries it to: " + also.Title : "");
                }
                Edited();
                host.SayOutside(said);
            });
        }

        void OnCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            GridRow r = RowAt(e.RowIndex);
            if (r == null || draft == null) return;
            if (r.Heading != null)
            {
                e.CellStyle.BackColor = SystemColors.Control;
                e.CellStyle.ForeColor = SystemColors.ControlText;
                e.CellStyle.Font = bold.Of(grid.Font);
            }
            else if (e.ColumnIndex == 2) e.CellStyle.ForeColor = Color.Gray;
            else if (Value(r) != Shipped(r)) e.CellStyle.BackColor = Color.FromArgb(255, 236, 170);
            else if (draft.IsNew) e.CellStyle.BackColor = Color.FromArgb(214, 240, 214);
        }

        // ---- texts ----

        void SetTexts()
        {
            NpcDraft d = Draft();
            grid.EndEdit();
            string language = oneLanguageBox.Checked ? Editor().Language : null;
            var said = new List<string>();
            if (nameBox.Text != (d.Name ?? ""))
            {
                int n = language != null ? d.SetName(language, nameBox.Text) : d.SetName(nameBox.Text);
                said.Add(n == 0 ? "the npc has no name row" + (language != null ? " in " + language : "") + "; the name was not set" : "name set in " + n + " language file(s)");
            }
            if (titleBox.Text != (d.Title ?? ""))
            {
                int n = 0;
                if (language == null) n = d.SetTitle(titleBox.Text);
                else
                    foreach (EntityPart p in d.StringParts("title"))
                        if (string.Equals(p.Language, language, StringComparison.OrdinalIgnoreCase)) { d.SetText(p, "name", titleBox.Text); n++; }
                said.Add(n == 0 ? "the npc has no title row" + (language != null ? " in " + language + " (untick \"only in this language\" to add one everywhere)" : "") + "; the title was not set" : "title set in " + n + " language file(s)");
            }
            if (said.Count == 0) { host.SayOutside("the texts are as the mod has them"); return; }
            Edited();
            FillHits(d.Id);
            ShowNpc(d);
            host.SayOutside(string.Join("\r\n", said.ToArray()));
        }

        // ---- clone ----

        static long Number(TextBox box, string what)
        {
            string text = box.Text.Trim();
            if (text.Length == 0) return 0;
            long v;
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out v)) throw new ModKitException("\"" + text + "\" is not a" + (what == "id" ? "n " : " ") + what);
            return v;
        }

        void Clone()
        {
            NpcDraft d = Draft();
            grid.EndEdit();
            NpcEditor ed = Editor();
            long id = Number(cloneIdBox, "id");
            if (id == 0) id = ed.NextFreeId(d.Id, 0);
            long level = Number(cloneLevelBox, "level"), hp = Number(cloneHpBox, "hp");
            if (level > int.MaxValue) throw new ModKitException("the level is too large");
            string name = cloneNameBox.Text.Trim();
            long source = d.Id;
            NpcDraft made = ed.Clone(source, id, name.Length == 0 ? null : name, (int)level, hp);
            Edited();
            FillHits(id);
            ShowNpc(made);
            host.SayOutside("npc " + id + " added as a copy of npc " + source + ". Save mod, then Plan shows it per target.\r\n\r\n" + Describe(made)
                + (ed.HasServer ? "" : "\r\nno server data folder is set: a server will not know the npc"));
        }

        // ---- the npc's shop and drop box, in the MS2 Shops and Drops view ----

        static readonly string[] BoxFields = { "dropiteminfo@individualDropBoxId", "dropiteminfo@individualHitDropBoxId", "dropiteminfo@globalDropBoxId", "dropiteminfo@globalDeadDropBoxId", "dropiteminfo@globalHitDropBoxId" };

        static long FirstId(string value)
        {
            if (value == null) return 0;
            foreach (string part in value.Split(','))
            {
                long v;
                if (long.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out v) && v != 0) return v;
            }
            return 0;
        }

        static GroupsView GroupsViewShown()
        {
            IViewService views = Platform.GetService<IViewService>();
            GroupsView v = views == null ? null : views.CreateOrFindView(GroupsView.ViewId) as GroupsView;
            if (v == null) throw new ModKitException("the MS2 Shops and Drops view could not be opened");
            views.ShowView(v);
            return v;
        }

        void OpenShop()
        {
            NpcDraft d = Draft();
            grid.EndEdit();
            long id = FirstId(d.GetField("@shopId"));
            if (id == 0) throw new ModKitException("npc " + d.Id + " has no shop (shopId is empty or 0)");
            GroupsViewShown().OpenGroup(GroupKind.Shop, id);
        }

        void OpenBox()
        {
            NpcDraft d = Draft();
            grid.EndEdit();
            var said = new List<string>();
            long first = 0;
            foreach (string f in BoxFields)
            {
                string v = d.GetField(f);
                if (FirstId(v) == 0) continue;
                said.Add(f + " = " + v);
                if (first == 0) first = FirstId(v);
            }
            if (first == 0) throw new ModKitException("npc " + d.Id + " has no drop box id");
            GroupsViewShown().OpenGroup(GroupKind.DropBox, first);
            if (said.Count > 1) host.SayOutside(outputBox.Text + "\r\n\r\nnpc " + d.Id + " names more than one box:\r\n  " + string.Join("\r\n  ", said.ToArray()));
        }

        void ShowWhereUsed()
        {
            WhereUsedJump.Show(this, host, IdLookup.Npc, Draft().Id);
        }

        /// <summary>Where used's jump: the npc, its grid row of the field ("element@attribute"; else the first row of that attribute).</summary>
        public void OpenUse(long id, string field)
        {
            Guard(delegate
            {
                grid.EndEdit();
                filling = true;
                searchBox.Text = id.ToString(CultureInfo.InvariantCulture);
                filling = false;
                FillHits(id);
                ShowNpc(Editor().Open(id));
                int at = field.IndexOf('@');
                string attr = at >= 0 ? field.Substring(at + 1) : field;
                int hit = -1;
                for (int i = 0; i < rows.Count && hit < 0; i++)
                    if (rows[i].Heading == null && FieldOf(rows[i]) == field) hit = i;
                for (int i = 0; i < rows.Count && hit < 0; i++)
                    if (rows[i].Heading == null && FieldOf(rows[i]).EndsWith("@" + attr, StringComparison.Ordinal)) hit = i;
                if (hit < 0) return;
                WhereUsedJump.SelectCell(grid, 1, hit, 3);
            });
        }

        void RemoveClone()
        {
            NpcDraft d = Draft();
            if (!d.IsNew) throw new ModKitException("npc " + d.Id + " is a shipped npc; only an npc the mod adds can be removed");
            grid.CancelEdit();
            Editor().RemoveClone(d.Id);
            ShowNpc(null);
            Edited();
            FillHits(0);
            WhereUsedJump.SayLeftBehind(host, "npc " + d.Id + " taken out of the mod", IdLookup.Npc, d.Id);
        }
    }
}
