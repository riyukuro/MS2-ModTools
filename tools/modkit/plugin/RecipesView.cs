// The "MS2 Recipes" view: the mastery recipes (table/masteryreceipe.xml) of the gathering and crafting life skills, in
// the style of the MS2 Shops and Drops view. A mastery type filter and a find box (recipe id or result item id), the
// list of hits with the first result's icon and name, the recipe's fields as a form (with the shipped value and what
// a value means: the type's name, the grade a mastery value reaches), its materials and results as a grid with icons
// and names, the checks of the recipe, Add / Clone / Remove, and the Plan / Apply / Revert buttons and output box of
// the MS2 Tables view.
//
// A recipe is one thing on every side that ships the table (the Korean client and the server, see
// MS2.ModKit.RecipeEditor); every edit is an op of the mod the MS2 Tables view is editing.
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
    public class RecipesView : AbstractView
    {
        public const string ViewId = "MS2.Tables.RecipesView";
        const int IconRow = 36;

        // the form's fields, in the order the window shows them
        static readonly string[] Fields =
        {
            "id", "masteryType", "requireMastery", "requireMeso", "requireQuest", "gatheringTime",
            "rewardMastery", "rewardExp", "exceptRewardExp", "habitatMapId", "highPropLimitCount", "normalPropLimitCount",
            "feature", "locale",
        };

        sealed class Slot
        {
            internal bool Result;
            internal int Index;   // 1 to 5
        }

        sealed class ItemInfo
        {
            internal string Name, Icon;
            internal bool Missing;
        }

        TablesView host;
        RecipeEditor editor;
        ItemEditor items;
        Recipe recipe;
        readonly List<Recipe> hits = new List<Recipe>();
        readonly List<Slot> slots = new List<Slot>();
        readonly Dictionary<long, ItemInfo> itemInfo = new Dictionary<long, ItemInfo>();
        bool filling;

        readonly ComboBox typeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        readonly TextBox searchBox = new TextBox { Width = 160 };
        readonly Label hitsLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListBox hitList = new ListBox { Dock = DockStyle.Left, Width = 280, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = IconRow };
        readonly Label recipeLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 8, 0) };
        readonly TextBox newIdBox = new TextBox { Width = 90 };
        readonly IconBox pictureBox = new IconBox { Dock = DockStyle.Left, Width = 120, Margin = new Padding(0) };
        readonly DataGridView formGrid = new DataGridView { Dock = DockStyle.Fill };
        readonly Panel formPane = new Panel { Dock = DockStyle.Top, Height = 220 };
        readonly DataGridView itemGrid = new DataGridView { Dock = DockStyle.Fill };
        readonly ListBox checkList = new ListBox { Dock = DockStyle.Bottom, Height = 80, IntegralHeight = false, HorizontalScrollbar = true };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 130 };
        readonly ComboBox targetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();
        readonly BoldFont bold = new BoldFont();

        public RecipesView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            typeBox.Items.Add("All types");
            for (int t = RecipeRules.FirstType; t <= RecipeRules.LastType; t++)
                typeBox.Items.Add(t.ToString(CultureInfo.InvariantCulture) + " " + RecipeRules.TypeName(t));
            typeBox.SelectedIndex = 0;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(typeBox);
            tips.SetToolTip(typeBox, "The mastery type of the recipes listed (3 to 6 gather from props on habitat maps, 7 to 11 are crafts)");
            top.Controls.Add(Lbl("find"));
            top.Controls.Add(searchBox);
            tips.SetToolTip(searchBox, "A part of the recipe id or of a result's item id; empty lists every recipe of the type");
            top.Controls.Add(Btn("Find", "Lists the recipes of the type whose id, or a result's item id, holds the text", delegate { Find(); }));
            top.Controls.Add(hitsLabel);

            FlowLayoutPanel applyBar = Bar();
            applyBar.Controls.Add(Lbl("Apply to"));
            targetBox.Items.Add("workspace (patched archives)");
            targetBox.Items.Add("game folder (loose files)");
            tips.SetToolTip(targetBox, "Where Apply writes the client's tables (the same choice as in the MS2 Tables view). The server's archives and the global client's always go to the workspace.");
            applyBar.Controls.Add(targetBox);
            applyBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change, with the recipe checks for each target; writes nothing", delegate { return host.DoPlan(); }));
            applyBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change, to every target at once", delegate { return host.DoApply(); }));
            applyBar.Controls.Add(Job("Revert", "Removes what Apply wrote, on every target", delegate { return host.DoRevert(); }));

            FlowLayoutPanel recipeBar = Bar();
            recipeBar.Controls.Add(recipeLabel);
            recipeBar.Controls.Add(Btn("Add", "Adds an empty recipe of the type chosen above under the id in the box (empty = the next free id of " + RecipeRules.From + "-" + RecipeRules.To + "), on the client and the server", delegate { Add(); }));
            recipeBar.Controls.Add(Btn("Clone", "Adds a copy of this recipe under the id in the box (empty = the next free id of " + RecipeRules.From + "-" + RecipeRules.To + "), on the client and the server", delegate { Clone(); }));
            recipeBar.Controls.Add(Lbl("new id"));
            recipeBar.Controls.Add(newIdBox);
            recipeBar.Controls.Add(Btn("Remove / restore", "Removes the recipe on every side (a new one goes away), or brings a removed shipped recipe back", delegate { RemoveOrRestore(); }));
            recipeBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same mod and Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            SetupGrid(formGrid);
            formGrid.Columns.Add(Col("field", 150, true));
            formGrid.Columns.Add(Col("value", 160, false));
            formGrid.Columns.Add(Col("shipped value", 140, true));
            formGrid.Columns.Add(Col("means", 220, true));
            formGrid.CellValueNeeded += OnFormValueNeeded;
            formGrid.CellValuePushed += OnFormValuePushed;
            formGrid.CellFormatting += OnFormFormatting;
            formGrid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e) { if (recipe == null || recipe.IsDeleted) e.Cancel = true; };
            tips.SetToolTip(pictureBox, "The recipe's picture in the mastery window (MasteryReceipe/{id}.png of Image.m2d)");
            formPane.Controls.Add(formGrid);
            formPane.Controls.Add(pictureBox);

            SetupGrid(itemGrid);
            itemGrid.RowTemplate.Height = IconRow;
            itemGrid.Columns.Add(Col("", IconRow + 4, true));
            itemGrid.Columns.Add(Col("slot", 110, true));
            itemGrid.Columns.Add(Col("item", 100, false));
            itemGrid.Columns.Add(Col("name", 220, true));
            itemGrid.Columns.Add(Col("count", 60, false));
            itemGrid.Columns.Add(Col("rank", 50, false));
            itemGrid.Columns.Add(Col("tag", 70, false));
            itemGrid.Columns[0].MinimumWidth = IconRow + 4;
            itemGrid.CellValueNeeded += OnItemValueNeeded;
            itemGrid.CellValuePushed += OnItemValuePushed;
            itemGrid.CellPainting += OnItemPainting;
            itemGrid.CellFormatting += OnItemFormatting;
            itemGrid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e)
            {
                Slot s = SlotAt(e.RowIndex);
                if (recipe == null || recipe.IsDeleted || s == null || e.ColumnIndex < 2 || e.ColumnIndex == 3) e.Cancel = true;
                else if (e.ColumnIndex == 6 && s.Result) e.Cancel = true;   // a result takes no tag
            };
            IdPick.Attach(itemGrid, delegate(int row, int column)
            {
                return recipe != null && SlotAt(row) != null && column == 2 ? IdLookup.Item : null;
            }, delegate { return host.SharedSession(); });
            for (int i = 1; i <= RecipeRules.Slots; i++) slots.Add(new Slot { Result = true, Index = i });
            for (int i = 1; i <= RecipeRules.Slots; i++) slots.Add(new Slot { Result = false, Index = i });
            tips.SetToolTip(checkList, "The checks of the recipe as shown; Plan checks every recipe the mods add or change, per target, and the items of each target");

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(itemGrid);
            right.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            right.Controls.Add(checkList);
            right.Controls.Add(new Splitter { Dock = DockStyle.Top, Height = 4 });
            right.Controls.Add(formPane);
            right.Controls.Add(recipeBar);

            Controls.Add(right);
            Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
            Controls.Add(hitList);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(outputBox);
            Controls.Add(applyBar);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, applyBar, hitList, right });
            // the output box keeps two lines; the form gets 38 % of the height beside the items (ten slots), the checks
            // 15 %; in a low window the items keep their header and three rows
            ViewLayout.KeepLogLines(outputBox, this);
            ViewLayout.KeepHeightShare(formPane, right, 0.38, formGrid.ColumnHeadersHeight + 5 * formGrid.RowTemplate.Height + 4,
                delegate { return recipeBar.Height + checkList.Height + 8 + itemGrid.ColumnHeadersHeight + 3 * IconRow + 4; });
            ViewLayout.KeepHeightShare(checkList, right, 0.15, 3 * checkList.ItemHeight + 4,
                delegate { return recipeBar.Height + formPane.Height + 8 + itemGrid.ColumnHeadersHeight + 3 * IconRow + 4; });
            ViewLayout.CentreOnFirstShow(this);
            targetBox.SelectedIndexChanged += delegate { if (!filling && host != null) host.TargetIndex = targetBox.SelectedIndex; };

            searchBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                Guard(delegate { Find(); });
            };
            typeBox.SelectedIndexChanged += delegate { if (!filling) Guard(delegate { Find(); }); };
            hitList.SelectedIndexChanged += delegate { if (!filling) Guard(delegate { OpenSelected(); }); };
            hitList.DrawItem += OnDrawHit;
        }

        public override void Initialize()
        {
            base.Initialize();
            Guard(delegate { Find(); });
        }

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

        void EndEdits() { formGrid.EndEdit(); itemGrid.EndEdit(); }

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

        // ---- the MS2 Tables view: session, mod, worker thread, output ----

        void Hook(TablesView h)
        {
            if (h == host) return;
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
            if (h == null) return;
            h.Changed += OnHostChanged;
            h.Flushing += EndEdits;
            h.BusyChanged += OnHostBusy;
            h.Said += OnHostSaid;
            h.TargetChanged += OnHostTarget;
            OnHostTarget();
            OutputWrap.Set(outputBox, h.OutputText);
            OnHostBusy(h.Busy);
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
                Logger.Warning("MS2 Recipes: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        RecipeEditor Editor()
        {
            EditorSession s = host.SharedSession();
            if (editor == null || editor.Mod != s.Mod || editor.Data != s.Data)
            {
                editor = new RecipeEditor(s.Data, s.Mod);
                itemInfo.Clear();
            }
            return editor;
        }

        void OnHostSaid(string text) { OutputWrap.Set(outputBox, text); }

        void OnHostBusy(bool on) { ViewLayout.Lock(this, lockable, on, outputBox); }

        // the mod was replaced or edited in another view: read the table again and show the same recipe
        void OnHostChanged()
        {
            formGrid.CancelEdit();
            itemGrid.CancelEdit();
            string key = recipe != null && editor != null ? editor.Key(recipe) : null;
            editor = null;
            items = null;
            itemInfo.Clear();
            ShowRecipe(null);
            try { FillHits(key); }
            catch (ModKitException) { ShowRecipe(null); }
            catch (System.IO.IOException) { ShowRecipe(null); }
        }

        int TypeChosen() { return typeBox.SelectedIndex <= 0 ? 0 : RecipeRules.FirstType + typeBox.SelectedIndex - 1; }

        // ---- items: name and icon ----

        IconCache Cache()
        {
            if (host == null) return null;
            try { return IconCache.For(host.SharedSession().Data); }
            catch (ModKitException) { return null; }
            catch (System.IO.IOException) { return null; }
        }

        ItemInfo Info(long id)
        {
            ItemInfo info;
            if (itemInfo.TryGetValue(id, out info)) return info;
            info = new ItemInfo();
            try
            {
                EditorSession s = host.SharedSession();
                if (items == null || items.Mod != s.Mod || items.Data != s.Data) items = s.Items();
                ItemDraft d = items.Open(id);
                info.Name = d.Name;
                info.Icon = d.IconEntry();
            }
            catch (ModKitException) { info.Missing = true; info.Name = "(no such item)"; }
            catch (System.IO.IOException) { info.Name = ""; }
            itemInfo[id] = info;
            return info;
        }

        // ---- the list ----

        void Find()
        {
            EndEdits();
            FillHits(recipe != null ? Editor().Key(recipe) : null);
            if (hitList.Items.Count > 0 && hitList.SelectedIndex < 0) hitList.SelectedIndex = 0;
            else if (hitList.Items.Count == 0) ShowRecipe(null);
        }

        void FillHits(string keep)
        {
            RecipeEditor ed = Editor();
            hits.Clear();
            hits.AddRange(ed.Find(TypeChosen(), searchBox.Text));
            // the mod's new recipes first, then by id
            hits.Sort(delegate(Recipe a, Recipe b)
            {
                if (a.IsAdded != b.IsAdded) return a.IsAdded ? -1 : 1;
                int c = ed.Id(a).CompareTo(ed.Id(b));
                return c != 0 ? c : string.CompareOrdinal(ed.Key(a), ed.Key(b));
            });
            filling = true;
            Recipe show = null;
            try
            {
                hitList.BeginUpdate();
                hitList.Items.Clear();
                foreach (Recipe r in hits)
                {
                    hitList.Items.Add(r);
                    if (keep != null && ed.Key(r) == keep) { hitList.SelectedIndex = hitList.Items.Count - 1; show = r; }
                }
                hitList.EndUpdate();
            }
            finally { filling = false; }
            hitsLabel.Text = hits.Count + " recipe(s), " + ed.Recipes.Count + " in all";
            if (show != null) ShowRecipe(show);
        }

        void OpenSelected()
        {
            Recipe r = hitList.SelectedItem as Recipe;
            if (r == null) return;
            EndEdits();
            ShowRecipe(r);
            host.SayOutside(Describe(r));
        }

        RecipeItem FirstResult(Recipe r)
        {
            foreach (RecipeItem it in Editor().Items(r, true)) if (it != null) return it;
            return null;
        }

        void OnDrawHit(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= hitList.Items.Count || editor == null) return;
            var r = hitList.Items[e.Index] as Recipe;
            if (r == null) return;
            int size = e.Bounds.Height - 4;
            var box = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 2, size, size);
            RecipeItem first = FirstResult(r);
            IconCache cache = Cache();
            ItemInfo info = first != null && host != null ? Info(first.Id) : null;
            IconCache.Draw(e.Graphics, box, cache != null && info != null ? cache.Get(info.Icon, hitList) : null);
            int type = editor.Type(r);
            string line1 = editor.Get(r, "id") + "  " + (first == null ? "(no result)" : (info == null || string.IsNullOrEmpty(info.Name) ? "item " + first.Id : info.Name)
                + (first.Count != 1 ? " x" + first.Count : ""));
            string line2 = (RecipeRules.TypeName(type) ?? "type " + type) + ", mastery " + (editor.Get(r, "requireMastery") ?? "0")
                + (string.IsNullOrEmpty(editor.Get(r, "feature")) ? "" : ", " + editor.Get(r, "feature"))
                + (r.IsDeleted ? "  (removed)" : r.IsAdded ? "  (new)" : r.IsChanged ? "  (changed)" : "");
            Color fore = r.IsDeleted && (e.State & DrawItemState.Selected) == 0 ? SystemColors.GrayText : e.ForeColor;
            int half = e.Bounds.Height / 2;
            var t1 = new Rectangle(box.Right + 6, e.Bounds.Y + 1, e.Bounds.Width - box.Width - 10, half);
            var t2 = new Rectangle(t1.X, e.Bounds.Y + half - 1, t1.Width, half);
            TextRenderer.DrawText(e.Graphics, line1, r.IsAdded || r.IsChanged ? bold.Of(e.Font) : e.Font, t1, fore, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, line2, e.Font, t2, fore == e.ForeColor && (e.State & DrawItemState.Selected) == 0 ? SystemColors.GrayText : fore, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        }

        // ---- the recipe ----

        void ShowRecipe(Recipe r)
        {
            formGrid.CancelEdit();
            itemGrid.CancelEdit();
            recipe = r;
            formGrid.RowCount = 0;
            itemGrid.RowCount = 0;
            checkList.Items.Clear();
            if (r == null)
            {
                recipeLabel.Text = "no recipe chosen";
                pictureBox.Show(null, null);
                return;
            }
            RecipeEditor ed = Editor();
            int type = ed.Type(r);
            recipeLabel.Text = "Recipe " + ed.Get(r, "id") + " (" + (RecipeRules.TypeName(type) ?? "type " + type) + ")"
                + (r.IsDeleted ? ", removed by the mod" : r.IsAdded ? ", new in the mod" : r.IsChanged ? ", changed by the mod" : "");
            pictureBox.Show(Cache(), "MasteryReceipe/" + ed.Get(r, "id") + ".png");
            formGrid.RowCount = Fields.Length;
            itemGrid.RowCount = slots.Count;
            foreach (string t in Checks(r)) checkList.Items.Add(t);
            if (checkList.Items.Count == 0) checkList.Items.Add("no problem found");
            formGrid.Invalidate();
            itemGrid.Invalidate();
        }

        /// <summary>The editor's checks and the items no target of the mod has (the plan names the target).</summary>
        List<string> Checks(Recipe r)
        {
            List<string> l = Editor().Check(r);
            foreach (Slot s in slots)
            {
                RecipeItem it = SlotItem(s);
                if (it != null && Info(it.Id).Missing) l.Add(SlotName(s) + ": item " + it.Id + " is not an item");
            }
            return l;
        }

        static string Describe(RecipeEditor ed, Recipe r)
        {
            var sb = new StringBuilder();
            sb.Append("recipe ").Append(ed.Get(r, "id"));
            if (r.IsDeleted) sb.Append(" (removed by the mod)");
            else if (r.IsAdded) sb.Append(" (new in the mod)");
            else if (r.IsChanged) sb.Append(" (changed by the mod)");
            sb.Append(':');
            for (int i = 0; i < ed.Sides.Count; i++)
                sb.Append("\r\n").Append(ed.Sides[i].Archive).Append(": ").Append(r.Rows[i] == null ? "not there" : r.Rows[i].IsDeleted ? "removed" : "has it");
            return sb.ToString();
        }

        string Describe(Recipe r) { return Describe(Editor(), r); }

        void Refresh(string said)
        {
            host.EditedOutside();
            Recipe r = recipe;
            hitList.Invalidate();
            ShowRecipe(r);
            if (said != null) host.SayOutside(said + "\r\n\r\n" + Describe(r));
        }

        // ---- the form ----

        string Means(Recipe r, string field)
        {
            RecipeEditor ed = editor;
            string v = ed.Get(r, field);
            int type = ed.Type(r);
            long n;
            switch (field)
            {
                case "masteryType":
                    return (RecipeRules.TypeName(type) ?? "not a recipe type") + (RecipeRules.TypeName(type) == null ? "" : RecipeRules.IsGathering(type) ? " (gathering)" : " (crafting)");
                case "requireMastery":
                    if (type == RecipeRules.PetTaming) return "pettaming: the level is 50 whatever the row says";
                    if (!long.TryParse(v ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out n) || !ed.Mastery.Has(type)) return "";
                    return "grade " + ed.Mastery.GradeOf(type, n) + " of " + RecipeRules.TypeName(type) + " (top value " + ed.Mastery.MaxValue(type) + ")";
                case "exceptRewardExp": return v == "1" ? "no experience" : "";
                case "habitatMapId":
                case "highPropLimitCount":
                case "normalPropLimitCount":
                    return RecipeRules.IsGathering(type) ? "gathering" : "gathering only";
                case "requireMeso": return long.TryParse(v ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out n) ? n.ToString("N0", CultureInfo.InvariantCulture) + " meso" : "";
                case "feature":
                case "locale": return "part of the key, with id";
                default: return "";
            }
        }

        void OnFormValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (recipe == null || editor == null || e.RowIndex < 0 || e.RowIndex >= Fields.Length) return;
            string f = Fields[e.RowIndex];
            switch (e.ColumnIndex)
            {
                case 0: e.Value = (editor.Differs(recipe, f) ? "≠ " : editor.Shipped(recipe, f) != editor.Get(recipe, f) && !recipe.IsAdded ? "* " : "   ") + f; break;
                case 1: e.Value = editor.Get(recipe, f); break;
                case 2: e.Value = recipe.IsAdded ? "(new)" : editor.Shipped(recipe, f); break;
                case 3: e.Value = editor.Differs(recipe, f) ? "the sides differ; an edit sets every side" : Means(recipe, f); break;
            }
        }

        void OnFormFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (recipe == null || editor == null || e.RowIndex < 0 || e.RowIndex >= Fields.Length) return;
            string f = Fields[e.RowIndex];
            if (recipe.IsDeleted) e.CellStyle.ForeColor = SystemColors.GrayText;
            else if (e.ColumnIndex == 2 || e.ColumnIndex == 3) e.CellStyle.ForeColor = SystemColors.GrayText;
            else if (!recipe.IsAdded && editor.Shipped(recipe, f) != editor.Get(recipe, f)) e.CellStyle.Font = bold.Of(formGrid.Font);
        }

        void OnFormValuePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            if (recipe == null || e.ColumnIndex != 1 || e.RowIndex < 0 || e.RowIndex >= Fields.Length) return;
            string f = Fields[e.RowIndex];
            string v = ((e.Value as string) ?? "").Trim();
            Guard(delegate
            {
                if (v == (Editor().Get(recipe, f) ?? "")) return;
                Editor().Set(recipe, f, v);
                Refresh(null);
            });
        }

        // ---- materials and results ----

        Slot SlotAt(int i) { return i >= 0 && i < slots.Count ? slots[i] : null; }

        static string SlotName(Slot s) { return (s.Result ? RecipeRules.Result(s.Index) : RecipeRules.Material(s.Index)); }

        RecipeItem SlotItem(Slot s)
        {
            RecipeItem it;
            return RecipeItem.TryParse(Editor().Get(recipe, SlotName(s)), out it) ? it : null;
        }

        void OnItemValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            Slot s = SlotAt(e.RowIndex);
            if (s == null || recipe == null || editor == null) return;
            string raw = editor.Get(recipe, SlotName(s)) ?? "";
            RecipeItem it;
            bool ok = RecipeItem.TryParse(raw, out it);
            switch (e.ColumnIndex)
            {
                case 1: e.Value = (s.Result ? "result " : "material ") + s.Index; break;
                case 2: e.Value = ok ? (it != null ? it.Id.ToString(CultureInfo.InvariantCulture) : "") : raw; break;
                case 3: e.Value = !ok ? "(not \"id,rank,count\")" : it != null ? Info(it.Id).Name : ""; break;
                case 4: e.Value = ok && it != null ? it.Count.ToString(CultureInfo.InvariantCulture) : ""; break;
                case 5: e.Value = ok && it != null ? it.Rank.ToString(CultureInfo.InvariantCulture) : ""; break;
                case 6: e.Value = ok && it != null ? it.Tag : ""; break;
            }
        }

        void OnItemFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            Slot s = SlotAt(e.RowIndex);
            if (s == null || recipe == null) return;
            if (e.ColumnIndex == 1) e.CellStyle.Font = bold.Of(itemGrid.Font);
            if (recipe.IsDeleted || e.ColumnIndex == 6 && s.Result) e.CellStyle.ForeColor = SystemColors.GrayText;
            if (!s.Result) e.CellStyle.BackColor = SystemColors.Control;
            if (e.ColumnIndex == 3 && e.Value as string == "(no such item)") e.CellStyle.ForeColor = Color.Firebrick;
        }

        void OnItemPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.ColumnIndex != 0 || e.RowIndex < 0 || recipe == null) return;
            Slot s = SlotAt(e.RowIndex);
            if (s == null) return;
            e.Paint(e.ClipBounds, DataGridViewPaintParts.All);
            RecipeItem it = SlotItem(s);
            if (it != null)
            {
                IconCache cache = Cache();
                int size = e.CellBounds.Height - 4;
                var box = new Rectangle(e.CellBounds.X + 2, e.CellBounds.Y + 2, size, size);
                IconCache.Draw(e.Graphics, box, cache != null ? cache.Get(Info(it.Id).Icon, itemGrid) : null);
            }
            e.Handled = true;
        }

        void OnItemValuePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            Slot s = SlotAt(e.RowIndex);
            if (s == null || recipe == null) return;
            string v = ((e.Value as string) ?? "").Trim();
            int column = e.ColumnIndex;
            Guard(delegate
            {
                RecipeEditor ed = Editor();
                RecipeItem it = SlotItem(s);
                if (column == 2)
                {
                    if (v.Length == 0 || v == "0") { if (it != null) { ed.SetItem(recipe, s.Result, s.Index, null); Refresh(null); } return; }
                    long id;
                    if (!long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out id)) throw new ModKitException("\"" + v + "\" is not an item id");
                    if (it != null && it.Id == id) return;
                    if (it == null) it = new RecipeItem();
                    it.Id = id;
                }
                else
                {
                    if (it == null) throw new ModKitException("give the slot an item first");
                    long n;
                    if (column == 6) { if (v == (it.Tag ?? "")) return; it.Tag = v.Length == 0 ? null : v; }
                    else if (!long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) throw new ModKitException("\"" + v + "\" is not a number");
                    else if (column == 4) { if (n == it.Count) return; it.Count = n; }
                    else { if (n == it.Rank) return; it.Rank = (int)n; }
                }
                ed.SetItem(recipe, s.Result, s.Index, it);
                Refresh(null);
            });
        }

        // ---- add, clone, remove ----

        long NewId()
        {
            string text = newIdBox.Text.Trim();
            long v;
            if (text.Length > 0)
            {
                if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out v)) throw new ModKitException("\"" + text + "\" is not an id");
                return v;
            }
            List<long> free = Editor().NextIds(host.SharedSession().OtherMods(), 1, null);
            if (free.Count == 0) throw new ModKitException("no free id is left in " + RecipeRules.From + "-" + RecipeRules.To);
            return free[0];
        }

        void Add()
        {
            EndEdits();
            int type = TypeChosen();
            if (type == 0) throw new ModKitException("choose the mastery type of the new recipe in the list above first");
            Recipe r = Editor().Add(NewId(), type);
            Opened(r, "recipe " + Editor().Get(r, "id") + " added, empty: give it materials and results below. Save mod, then Plan checks it per target.");
        }

        void Clone()
        {
            if (recipe == null) throw new ModKitException("choose the recipe to copy first");
            EndEdits();
            string source = Editor().Get(recipe, "id");
            Recipe r = Editor().Clone(recipe, NewId());
            Opened(r, "recipe " + Editor().Get(r, "id") + " added as a copy of " + source + ". Save mod, then Plan checks it per target.");
        }

        void Opened(Recipe r, string said)
        {
            host.EditedOutside();
            newIdBox.Text = "";
            searchBox.Text = "";
            FillHits(Editor().Key(r));
            ShowRecipe(r);
            host.SayOutside(said + "\r\n\r\n" + Describe(r));
        }

        void RemoveOrRestore()
        {
            if (recipe == null) throw new ModKitException("choose a recipe first");
            EndEdits();
            RecipeEditor ed = Editor();
            string id = ed.Get(recipe, "id");
            if (recipe.IsDeleted)
            {
                ed.Restore(recipe);
                Refresh("recipe " + id + " is back");
                return;
            }
            bool added = recipe.IsAdded;
            ed.Remove(recipe);
            if (added)
            {
                host.EditedOutside();
                ShowRecipe(null);
                FillHits(null);
                host.SayOutside("recipe " + id + " was new in the mod and is gone again; its id is free");
            }
            else Refresh("recipe " + id + " removed on every side (Remove / restore brings it back)");
        }
    }
}
