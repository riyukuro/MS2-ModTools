// The "MS2 Items" view: one item at a time instead of one table at a time. A search box (id or name), the list of
// hits, the fields of the chosen item grouped by the file and element they are in, the name and description texts,
// Clone to new id, and its own Plan / Apply / Revert buttons and output box.
//
// The view holds no data logic and no mod of its own. MS2.ModKit.ItemEditor turns what is done here into operations
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
using MS2.ModKit.Models;

namespace MS2.Tables
{
    public class ItemsView : AbstractView
    {
        public const string ViewId = "MS2.Tables.ItemsView";
        const int MaxHits = 500;

        sealed class GridRow
        {
            internal EntitySection Section;
            internal string Field;      // null: the section's heading row
        }

        TablesView host;
        ItemEditor editor;
        ItemDraft draft;
        readonly List<GridRow> rows = new List<GridRow>();
        bool filling;

        readonly TextBox searchBox = new TextBox { Width = 200 };
        readonly ComboBox languageBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };
        readonly Label hitsLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListBox hitList = new ListBox { Dock = DockStyle.Left, Width = 260, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 36 };
        readonly IconBox iconBox = new IconBox { Dock = DockStyle.Left, Width = 96, Margin = new Padding(0) };
        ModelBox modelBox;     // the item's model beside the form
        // the icon entry of each listed item as the mod has it (null: none); filled when the row is drawn
        readonly Dictionary<long, string> iconOf = new Dictionary<long, string>();
        readonly Label itemLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 8, 0) };
        readonly TextBox cloneIdBox = new TextBox { Width = 80 };
        readonly TextBox optionBox = new TextBox { Width = 80 };
        readonly CheckBox mirrorBox = new CheckBox { Text = "Sync the other side", AutoSize = true, Checked = true, Margin = new Padding(8, 6, 3, 0) };
        readonly TextBox nameBox = new TextBox { Width = 260 };
        readonly TextBox tooltipBox = new TextBox { Width = 240, Height = 44, Multiline = true, ScrollBars = ScrollBars.Vertical };
        readonly TextBox guideBox = new TextBox { Width = 180, Height = 44, Multiline = true, ScrollBars = ScrollBars.Vertical };
        readonly DataGridView grid = new DataGridView { Dock = DockStyle.Fill };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 110 };
        readonly ComboBox targetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();

        public ItemsView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(Lbl("Item id or name"));
            top.Controls.Add(searchBox);
            top.Controls.Add(Btn("Find", "Lists the items whose id starts with the text or whose name contains it; the items the mod adds come first", delegate { Find(); }));
            top.Controls.Add(Lbl("Names in"));
            top.Controls.Add(languageBox);
            tips.SetToolTip(languageBox, "The language folder (string/<language>) whose names are searched and shown. A name or description set here is written to every language.");
            top.Controls.Add(hitsLabel);

            // the same choice and the same three commands as in the MS2 Tables view, run by that view's worker
            // thread; the result shows in this view's output box and the Tables view stays where it is
            FlowLayoutPanel applyBar = Bar();
            applyBar.Controls.Add(Lbl("Apply to"));
            targetBox.Items.Add("workspace (patched archives)");
            targetBox.Items.Add("game folder (loose files)");
            tips.SetToolTip(targetBox, "Where Apply writes the client's tables (the same choice as in the MS2 Tables view): patched copies of the archives in the workspace folder, or plain files under Data/ of the game folder. The archives of the server and of the global client (gms-client) always go to the workspace.");
            applyBar.Controls.Add(targetBox);
            applyBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change; writes nothing", delegate { return host.DoPlan(); }));
            applyBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change", delegate { return host.DoApply(); }));
            applyBar.Controls.Add(Job("Revert", "Removes what Apply wrote", delegate { return host.DoRevert(); }));
            applyBar.Controls.Add(Job("Sync report", "For the chosen item: the fields the client and the server both hold, where the two disagree as shipped and after the mods, and what the mods change", delegate { return host.DoSync(draft != null ? draft.Id : 0, false); }));
            applyBar.Controls.Add(Job("All fields", "The sync report with every mapped field of the chosen item, also those that agree or that one side does not carry", delegate { return host.DoSync(draft != null ? draft.Id : 0, true); }));
            applyBar.Controls.Add(Job("Server rows", "Lists the rows of server-only tables that belong to the chosen item (option, drop and shop rows) with the archive, entry and row to open in the MS2 Tables view", delegate { return host.DoRefs(draft != null ? draft.Id : 0); }));

            FlowLayoutPanel itemBar = Bar();
            itemBar.Controls.Add(itemLabel);
            itemBar.Controls.Add(Btn("Clone to new id", "Adds a new item that starts as a copy of this one, under the id in the box (empty = the next free id of the item's block of 100000)", delegate { Clone(); }));
            itemBar.Controls.Add(cloneIdBox);
            itemBar.Controls.Add(Btn("Where used", "Lists the quests (conditions, rewards), shops, drop boxes and items that name this item, the mod's first, with Go to", delegate { ShowWhereUsed(); }));
            itemBar.Controls.Add(Btn("Remove clone", "Takes the chosen new item out of the mod again", delegate { RemoveClone(); }));
            itemBar.Controls.Add(Btn("Own stat row", "Stat rows are shared between items. This copies the item's stat row to the code in the box and points the item at the copy, so a stat change touches this item only", delegate { OwnOption(); }));
            itemBar.Controls.Add(optionBox);
            itemBar.Controls.Add(mirrorBox);
            tips.SetToolTip(mirrorBox, "A change is written once, where you make it, the same way a mod file has it. On Plan, a field changed in the kms-client's item row reaches the per-id item file (server, gms-client) too, and the other way round, when the two sides corresponded before; the field map says which field corresponds and how the value is written there. A server change reaches the gms-client as well, since its item files are the same as the server's. Unticked, the change stays on one side and Plan says so. Plan lists what each target gets");
            itemBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same mod and the same Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            FlowLayoutPanel textBar = Bar();
            textBar.Controls.Add(Lbl("Name"));
            textBar.Controls.Add(nameBox);
            textBar.Controls.Add(Btn("Set texts", "Writes the name and the two description texts into the mod, in every language folder", delegate { SetTexts(); }));
            FlowLayoutPanel descriptionBar = Bar();
            descriptionBar.Controls.Add(Lbl("Description"));
            descriptionBar.Controls.Add(tooltipBox);
            descriptionBar.Controls.Add(Lbl("Second line"));
            descriptionBar.Controls.Add(guideBox);

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
                if (r == null || r.Field == null) e.Cancel = true;
            };
            // the skill the item casts, its additional effects and its repacking scrolls: ids of the Skills and Items views
            IdPick.Attach(grid, delegate(int row, int column)
            {
                GridRow r = RowAt(row);
                return r != null && r.Field != null && column == 1 && draft != null ? IdLookup.ItemFieldKind(FieldOf(r)) : null;
            }, delegate { return host.SharedSession(); }, delegate(int row, int column)
            {
                GridRow r = RowAt(row);
                return r != null && r.Field != null && IdLookup.IsIdList(FieldOf(r));
            });

            // the item's icon at the left of the three bars above the grid, as high as the bars (at least the icon)
            var bars = new Panel { Dock = DockStyle.Fill };
            bars.Controls.Add(descriptionBar);
            bars.Controls.Add(textBar);
            bars.Controls.Add(itemBar);
            var iconPad = new Panel { Dock = DockStyle.Left, Width = 104, Padding = new Padding(6, 4, 2, 4) };
            iconPad.Controls.Add(iconBox);
            var head = new Panel { Dock = DockStyle.Top, Height = 104 };
            head.Controls.Add(bars);
            head.Controls.Add(iconPad);
            tips.SetToolTip(iconBox, "The item's icon (property@slotIcon), read from Resource/Image.m2d of your client or server data; an empty box: no data folder has that image");

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(grid);
            right.Controls.Add(head);
            // the item's model at the right of the form, over its whole height
            modelBox = ModelBox.AttachRight(right);
            tips.SetToolTip(modelBox, "The item's model (the first asset of each slot), drawn from Resource/Model of your client data");
            modelBox.Shown += delegate(ModelShot shot) { tips.SetToolTip(modelBox, ModelTip(shot)); };
            // in a low window the icon is drawn at its own size (64) instead of 96, and the bars never take more than
            // the height that leaves the grid a header and four rows
            bool fitting = false;
            EventHandler fitHead = delegate
            {
                if (fitting) return;
                fitting = true;
                try
                {
                int icon = right.ClientSize.Height < 480 ? 64 : 96;
                if (iconBox.Width != icon || iconPad.Width != icon + iconPad.Padding.Horizontal) { iconPad.Width = icon + iconPad.Padding.Horizontal; iconBox.Width = icon; }
                int h = itemBar.Height + textBar.Height + descriptionBar.Height;
                int want = Math.Max(icon + iconPad.Padding.Vertical, h);
                int room = right.ClientSize.Height - (grid.ColumnHeadersHeight + 4 * grid.RowTemplate.Height + 4);
                if (room > 0) want = Math.Min(want, Math.Max(icon + iconPad.Padding.Vertical, room));
                if (head.Height != want) head.Height = want;
                }
                finally { fitting = false; }
            };
            right.Resize += fitHead;
            bars.Layout += delegate { fitHead(bars, EventArgs.Empty); };
            // a bar that wraps to one more row after the head was fitted (the model box takes width) asks again
            itemBar.SizeChanged += fitHead;
            textBar.SizeChanged += fitHead;
            descriptionBar.SizeChanged += fitHead;

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
            cloneIdBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                Guard(delegate { Clone(); });
            };
            hitList.DrawItem += OnDrawHit;
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
                // Guard connects the Tables view (and loads the session); the job itself is started outside it
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
            outputBox.Text = h.OutputText;
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
                    h = TablesView.OpenBehind();
                }
                if (h == null) { outputBox.Text = "the MS2 Tables view could not be opened; the item editor works on the mod of that view"; return false; }
                Hook(h);
            }
            catch (Exception ex)
            {
                outputBox.Text = "the MS2 Tables view could not be opened: " + ex.Message;
                Logger.Warning("MS2 Items: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        ItemEditor Editor()
        {
            EditorSession s = host.SharedSession();
            if (editor == null || editor.Mod != s.Mod || editor.Data != s.Data)
            {
                string language = editor != null ? editor.Language : null;
                editor = s.Items();
                if (language != null && editor.Languages.Contains(language)) editor.Language = language;
                filling = true;
                languageBox.Items.Clear();
                foreach (string l in editor.Languages) languageBox.Items.Add(l);
                languageBox.SelectedItem = editor.Language;
                filling = false;
                mirrorBox.Enabled = editor.TwoSided;
            }
            return editor;
        }

        void OnHostSaid(string text) { outputBox.Text = text; }

        void OnHostBusy(bool on) { ViewLayout.Lock(this, lockable, on, outputBox); }

        void OnHostFlushing() { grid.EndEdit(); }

        // the mod or the whole session was replaced or edited in the Tables view: read the item again
        void OnHostChanged()
        {
            iconOf.Clear();
            grid.CancelEdit();
            long id = draft != null ? draft.Id : 0;
            ShowItem(null);
            try
            {
                FillHits(id);
                if (id != 0 && SelectedHit() != null) ShowItem(Editor().Open(id));
            }
            catch (ModKitException) { ShowItem(null); }
            catch (System.IO.IOException) { ShowItem(null); }
        }

        /// <summary>After an edit made here: the Tables view's table and pending pane follow the mod.</summary>
        void Edited()
        {
            grid.Invalidate();
            if (draft != null) { iconOf.Remove(draft.Id); ShowIcon(draft); ShowModel(draft); hitList.Invalidate(); }
            host.EditedOutside();
        }

        // ---- search ----

        void Find()
        {
            grid.EndEdit();
            FillHits(draft != null ? draft.Id : 0);
            if (hitList.Items.Count > 0 && hitList.SelectedIndex < 0) hitList.SelectedIndex = 0;
        }

        /// <summary>Lists the hits of the search text; the item with the id given stays selected when it is among them.</summary>
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
            hitsLabel.Text = hits.Count + (hits.Count >= MaxHits ? "+" : "") + " item(s)";
        }

        EntityHit SelectedHit() { return hitList.SelectedItem as EntityHit; }

        void OpenSelected()
        {
            EntityHit h = SelectedHit();
            if (h == null) return;
            grid.EndEdit();
            ShowItem(Editor().Open(h.Id));
            var sb = new System.Text.StringBuilder();
            sb.Append("item ").Append(draft.Id).Append(draft.IsNew ? " (new in mod " + editor.Mod.Id + ")" : "").Append(": ");
            foreach (EntityPart p in draft.Parts)
                sb.Append("\n  ").Append(p.Label).Append(": ").Append(p.Archive).Append(' ').Append(p.Entry).Append(p.Shared ? "  (shared with other items)" : "").Append(p.IsNew ? "  (new)" : "");
            host.SayOutside(sb.ToString());
        }

        // ---- the item ----

        void ShowItem(ItemDraft d)
        {
            grid.CancelEdit();
            grid.RowCount = 0;
            rows.Clear();
            draft = d;
            if (d == null)
            {
                itemLabel.Text = "(no item)";
                iconBox.Show(null, null);
                modelBox.Show(null, null);
                nameBox.Text = tooltipBox.Text = guideBox.Text = cloneIdBox.Text = optionBox.Text = "";
                return;
            }
            foreach (EntitySection s in d.Sections)
            {
                rows.Add(new GridRow { Section = s });
                foreach (string f in s.Fields) rows.Add(new GridRow { Section = s, Field = f });
            }
            grid.RowCount = rows.Count;
            ShowIcon(d);
            ShowModel(d);
            itemLabel.Text = "Item " + d.Id.ToString(CultureInfo.InvariantCulture) + (d.IsNew ? " (new)" : "");
            nameBox.Text = d.Name ?? "";
            tooltipBox.Text = Lines(d.Tooltip);
            guideBox.Text = Lines(d.Guide);
            cloneIdBox.Text = "";
            List<string> codes = d.OptionCodes();
            optionBox.Text = "";
            tips.SetToolTip(optionBox, codes.Count == 0 ? "This item names no stat row" : "The code of the new stat row, a number no row of " + ItemEditor.OptionTable + " has yet. The item's stat row now: " + string.Join(", ", codes.ToArray()));
        }

        // ---- icons ----

        IconCache Cache()
        {
            if (host == null) return null;
            try { return IconCache.For(host.SharedSession().Data); }
            catch (ModKitException) { return null; }
            catch (System.IO.IOException) { return null; }
        }

        void ShowIcon(ItemDraft d)
        {
            string entry = null;
            try { entry = d.IconEntry(); }
            catch (ModKitException) { }
            iconOf[d.Id] = entry;
            iconBox.Show(Cache(), entry);
            tips.SetToolTip(iconBox, entry == null ? "This item names no icon" : "Icon: " + entry + " in Resource/Image.m2d");
        }

        // ---- model ----

        /// <summary>Shows the model the draft's slot assets name now; the box draws it again only when they changed.</summary>
        void ShowModel(ItemDraft d)
        {
            ModelCache cache = null;
            List<string> assets = null;
            try
            {
                if (host != null) cache = ModelCache.For(host.SharedSession().Data);
                assets = ModelNames.ItemAssets(d);
            }
            catch (ModKitException) { }
            catch (System.IO.IOException) { }
            modelBox.Show(cache, ModelRequest.Item(assets));
        }

        static string ModelTip(ModelShot shot)
        {
            string t = shot.Picture != null ? "The item's model: " + shot.Triangles.ToString(CultureInfo.InvariantCulture) + " triangles, drawn as the file stores it" : (shot.Message ?? "no model");
            return shot.Notes == null ? t : t + "\n" + shot.Notes;
        }

        string IconEntryOf(long id)
        {
            string entry;
            if (iconOf.TryGetValue(id, out entry)) return entry;
            entry = null;
            try { entry = Editor().Open(id).IconEntry(); }
            catch (ModKitException) { }
            catch (System.IO.IOException) { }
            iconOf[id] = entry;
            return entry;
        }

        void OnDrawHit(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= hitList.Items.Count) return;
            var h = hitList.Items[e.Index] as EntityHit;
            if (h == null) return;
            int size = e.Bounds.Height - 4;
            var box = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 2, size, size);
            IconCache cache = Cache();
            string entry = cache != null ? IconEntryOf(h.Id) : null;
            IconCache.Draw(e.Graphics, box, cache != null ? cache.Get(entry, hitList) : null);
            var text = new Rectangle(box.Right + 6, e.Bounds.Y, e.Bounds.Width - box.Width - 10, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, h.ToString(), e.Font, text, e.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        }

        static string Lines(string text) { return (text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"); }

        static string Unlines(string text) { return text.Replace("\r\n", "\n"); }

        ItemDraft Draft()
        {
            if (draft == null) throw new ModKitException("choose an item in the list first");
            return draft;
        }

        /// <summary>The field of a row as "element@attribute".</summary>
        static string FieldOf(GridRow r) { return r.Section.Elem != null ? r.Section.Elem.Name + "@" + r.Field : r.Field; }

        GridRow RowAt(int index) { return index >= 0 && index < rows.Count ? rows[index] : null; }

        void OnCellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            GridRow r = RowAt(e.RowIndex);
            if (r == null || draft == null) return;
            if (r.Field == null) { e.Value = e.ColumnIndex == 0 ? r.Section.Title : ""; return; }
            if (e.ColumnIndex == 0) e.Value = (draft.IsEdited(r.Section, r.Field) ? "* " : "   ") + r.Field;
            else if (e.ColumnIndex == 1) e.Value = draft.Get(r.Section, r.Field);
            else e.Value = draft.GetOriginal(r.Section, r.Field);
        }

        void OnCellValuePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            GridRow r = RowAt(e.RowIndex);
            if (r == null || r.Field == null || e.ColumnIndex != 1 || draft == null) return;
            string value = e.Value == null ? null : e.Value.ToString();
            // an emptied cell is an empty value; a cell of a missing attribute that stays empty stays missing
            if (value == null && draft.Get(r.Section, r.Field) != null) value = "";
            if (value == draft.Get(r.Section, r.Field)) return;
            Guard(delegate
            {
                EntitySection also = draft.Set(r.Section, r.Field, value, mirrorBox.Checked && mirrorBox.Enabled);
                Edited();
                host.SayOutside(r.Section.Title + "  " + r.Field + " = " + value + (also != null ? "\nPlan carries it to: " + also.Title : "")
                    + (r.Section.Part.Shared ? "\nnote: this row is shared; every item that uses it changes" : ""));
            });
        }

        void OnCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            GridRow r = RowAt(e.RowIndex);
            if (r == null || draft == null) return;
            if (r.Field == null)
            {
                e.CellStyle.BackColor = SystemColors.Control;
                e.CellStyle.ForeColor = SystemColors.ControlText;
                e.CellStyle.Font = bold.Of(grid.Font);
            }
            else if (e.ColumnIndex == 2) e.CellStyle.ForeColor = Color.Gray;
            else if (draft.IsEdited(r.Section, r.Field)) e.CellStyle.BackColor = Color.FromArgb(255, 236, 170);
            else if (r.Section.Part.IsNew) e.CellStyle.BackColor = Color.FromArgb(214, 240, 214);
        }

        // ---- texts ----

        void SetTexts()
        {
            ItemDraft d = Draft();
            grid.EndEdit();
            var said = new List<string>();
            if (nameBox.Text != (d.Name ?? ""))
            {
                int n = d.SetName(nameBox.Text);
                said.Add(n == 0 ? "the item has no name row in any language; the name was not set" : "name set in " + n + " language file(s)");
            }
            if (Unlines(tooltipBox.Text) != Unlines(d.Tooltip ?? "")) said.Add("description set in " + d.SetTooltip(Unlines(tooltipBox.Text)) + " language file(s)");
            if (Unlines(guideBox.Text) != Unlines(d.Guide ?? "")) said.Add("second line set in " + d.SetGuide(Unlines(guideBox.Text)) + " language file(s)");
            if (said.Count == 0) { host.SayOutside("the texts are as the mod has them"); return; }
            Edited();
            FillHits(d.Id);
            ShowItem(d);
            host.SayOutside(string.Join("\n", said.ToArray()));
        }

        // ---- clone ----

        void Clone()
        {
            ItemDraft d = Draft();
            grid.EndEdit();
            ItemEditor ed = Editor();
            long id;
            string text = cloneIdBox.Text.Trim();
            if (text.Length == 0) id = ed.NextFreeId(d.Id, 0);
            else if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id)) throw new ModKitException("\"" + text + "\" is not an item id");
            long source = d.Id;
            ItemDraft made = ed.Clone(source, id, null, null);
            Edited();
            FillHits(id);
            ShowItem(made);
            host.SayOutside("item " + id + " added as a copy of item " + source + ". Give it its own name and description, then Save mod."
                + (ed.HasServer ? "" : "\nno server data folder is set: a server will not know the item"));
        }

        void ShowWhereUsed()
        {
            WhereUsedJump.Show(this, host, IdLookup.Item, Draft().Id);
        }

        /// <summary>Where used's jump: the item, its grid row of the field ("element@attribute"; else the first row of that attribute).</summary>
        public void OpenUse(long id, string field)
        {
            Guard(delegate
            {
                grid.EndEdit();
                filling = true;
                searchBox.Text = id.ToString(CultureInfo.InvariantCulture);
                filling = false;
                FillHits(id);
                ShowItem(Editor().Open(id));
                int at = field.IndexOf('@');
                string attr = at >= 0 ? field.Substring(at + 1) : field;
                int hit = -1;
                for (int i = 0; i < rows.Count && hit < 0; i++)
                    if (FieldOf(rows[i]) == field) hit = i;
                for (int i = 0; i < rows.Count && hit < 0; i++)
                    if (FieldOf(rows[i]).EndsWith("@" + attr, StringComparison.Ordinal)) hit = i;
                if (hit < 0) return;
                WhereUsedJump.SelectCell(grid, 1, hit, 3);
            });
        }

        void RemoveClone()
        {
            ItemDraft d = Draft();
            if (!d.IsNew) throw new ModKitException("item " + d.Id + " is a shipped item; only an item the mod adds can be removed");
            grid.CancelEdit();
            Editor().RemoveClone(d.Id);
            ShowItem(null);
            Edited();
            FillHits(0);
            WhereUsedJump.SayLeftBehind(host, "item " + d.Id + " taken out of the mod", IdLookup.Item, d.Id);
        }

        void OwnOption()
        {
            ItemDraft d = Draft();
            grid.EndEdit();
            List<string> codes = d.OptionCodes();
            if (codes.Count == 0) throw new ModKitException("item " + d.Id + " names no stat row (no option constantID)");
            string code = optionBox.Text.Trim();
            if (code.Length == 0) throw new ModKitException("type the code of the new stat row into the box right of Own stat row (the item's row now: " + string.Join(", ", codes.ToArray()) + ")");
            d.GiveOwnOption(codes[0], code);
            Edited();
            ShowItem(d);
            host.SayOutside("stat row " + codes[0] + " copied to " + code + "; item " + d.Id + " uses the copy");
        }
    }
}
