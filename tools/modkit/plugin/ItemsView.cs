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

namespace MS2.Tables
{
    public class ItemsView : AbstractView
    {
        public const string ViewId = "MS2.Tables.ItemsView";
        const int MaxHits = 500;

        sealed class GridRow
        {
            internal ItemSection Section;
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
        readonly ListBox hitList = new ListBox { Dock = DockStyle.Left, Width = 280, IntegralHeight = false };
        readonly Label itemLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 8, 0) };
        readonly TextBox cloneIdBox = new TextBox { Width = 80 };
        readonly TextBox optionBox = new TextBox { Width = 80 };
        readonly CheckBox mirrorBox = new CheckBox { Text = "Client and server together", AutoSize = true, Checked = true, Margin = new Padding(8, 6, 3, 0) };
        readonly TextBox nameBox = new TextBox { Width = 260 };
        readonly TextBox tooltipBox = new TextBox { Width = 340, Height = 44, Multiline = true, ScrollBars = ScrollBars.Vertical };
        readonly TextBox guideBox = new TextBox { Width = 240, Height = 44, Multiline = true, ScrollBars = ScrollBars.Vertical };
        readonly DataGridView grid = new DataGridView { Dock = DockStyle.Fill };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Bottom, Height = 110 };
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
            tips.SetToolTip(targetBox, "Where Apply writes the client's tables (the same choice as in the MS2 Tables view): patched copies of the archives in the workspace folder, or plain files under Data/ of the game folder. The server's archives always go to the workspace.");
            applyBar.Controls.Add(targetBox);
            applyBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change; writes nothing", delegate { return host.DoPlan(); }));
            applyBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change", delegate { return host.DoApply(); }));
            applyBar.Controls.Add(Job("Revert", "Removes what Apply wrote", delegate { return host.DoRevert(); }));

            FlowLayoutPanel itemBar = Bar();
            itemBar.Controls.Add(itemLabel);
            itemBar.Controls.Add(Btn("Clone to new id", "Adds a new item that starts as a copy of this one, under the id in the box (empty = the next free id of the item's block of 100000)", delegate { Clone(); }));
            itemBar.Controls.Add(cloneIdBox);
            itemBar.Controls.Add(Btn("Remove clone", "Takes the chosen new item out of the mod again", delegate { RemoveClone(); }));
            itemBar.Controls.Add(Btn("Own stat row", "Stat rows are shared between items. This copies the item's stat row to the code in the box and points the item at the copy, so a stat change touches this item only", delegate { OwnOption(); }));
            itemBar.Controls.Add(optionBox);
            itemBar.Controls.Add(mirrorBox);
            tips.SetToolTip(mirrorBox, "A field changed in the client's item row is changed in the server's item file too (and the other way round) when both held the same value");
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
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            grid.BackgroundColor = SystemColors.AppWorkspace;
            grid.DefaultCellStyle.BackColor = SystemColors.Window;
            grid.DefaultCellStyle.ForeColor = SystemColors.WindowText;
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "field", ReadOnly = true, Width = 330, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "value", Width = 260, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "shipped value", ReadOnly = true, Width = 260, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.CellValueNeeded += OnCellValueNeeded;
            grid.CellValuePushed += OnCellValuePushed;
            grid.CellFormatting += OnCellFormatting;
            grid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e)
            {
                GridRow r = RowAt(e.RowIndex);
                if (r == null || r.Field == null) e.Cancel = true;
            };

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(grid);
            right.Controls.Add(descriptionBar);
            right.Controls.Add(textBar);
            right.Controls.Add(itemBar);

            Controls.Add(right);
            Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
            Controls.Add(hitList);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(outputBox);
            Controls.Add(applyBar);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, applyBar, hitList, right });
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
                    IViewService views = Platform.GetService<IViewService>();
                    h = views == null ? null : views.CreateOrFindView(TablesView.ViewId) as TablesView;
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
                mirrorBox.Enabled = editor.HasServer;
            }
            return editor;
        }

        void OnHostSaid(string text) { outputBox.Text = text; }

        void OnHostBusy(bool on)
        {
            foreach (Control c in lockable) c.Enabled = !on;
            UseWaitCursor = on;
        }

        void OnHostFlushing() { grid.EndEdit(); }

        // the mod or the whole session was replaced or edited in the Tables view: read the item again
        void OnHostChanged()
        {
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
            List<ItemHit> hits = Editor().Find(searchBox.Text, MaxHits);
            filling = true;
            try
            {
                hitList.BeginUpdate();
                hitList.Items.Clear();
                foreach (ItemHit h in hits)
                {
                    hitList.Items.Add(h);
                    if (h.Id == keep) hitList.SelectedIndex = hitList.Items.Count - 1;
                }
                hitList.EndUpdate();
            }
            finally { filling = false; }
            hitsLabel.Text = hits.Count + (hits.Count >= MaxHits ? "+" : "") + " item(s)";
        }

        ItemHit SelectedHit() { return hitList.SelectedItem as ItemHit; }

        void OpenSelected()
        {
            ItemHit h = SelectedHit();
            if (h == null) return;
            grid.EndEdit();
            ShowItem(Editor().Open(h.Id));
            var sb = new System.Text.StringBuilder();
            sb.Append("item ").Append(draft.Id).Append(draft.IsNew ? " (new in mod " + editor.Mod.Id + ")" : "").Append(": ");
            foreach (ItemPart p in draft.Parts)
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
                nameBox.Text = tooltipBox.Text = guideBox.Text = cloneIdBox.Text = optionBox.Text = "";
                return;
            }
            foreach (ItemSection s in d.Sections)
            {
                rows.Add(new GridRow { Section = s });
                foreach (string f in s.Fields) rows.Add(new GridRow { Section = s, Field = f });
            }
            grid.RowCount = rows.Count;
            itemLabel.Text = "Item " + d.Id.ToString(CultureInfo.InvariantCulture) + (d.IsNew ? " (new)" : "");
            nameBox.Text = d.Name ?? "";
            tooltipBox.Text = Lines(d.Tooltip);
            guideBox.Text = Lines(d.Guide);
            cloneIdBox.Text = "";
            List<string> codes = d.OptionCodes();
            optionBox.Text = "";
            tips.SetToolTip(optionBox, codes.Count == 0 ? "This item names no stat row" : "The code of the new stat row, a number no row of " + ItemEditor.OptionTable + " has yet. The item's stat row now: " + string.Join(", ", codes.ToArray()));
        }

        static string Lines(string text) { return (text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"); }

        static string Unlines(string text) { return text.Replace("\r\n", "\n"); }

        ItemDraft Draft()
        {
            if (draft == null) throw new ModKitException("choose an item in the list first");
            return draft;
        }

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
                ItemSection also = draft.Set(r.Section, r.Field, value, mirrorBox.Checked && mirrorBox.Enabled);
                Edited();
                host.SayOutside(r.Section.Title + "  " + r.Field + " = " + value + (also != null ? "\nalso set in: " + also.Title : "")
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
                e.CellStyle.Font = new Font(grid.Font, FontStyle.Bold);
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
                + (ed.HasServer ? "" : "\nno server data folder is set: the server's item file was not copied, and a server will not know the item"));
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
            host.SayOutside("item " + d.Id + " taken out of the mod");
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
