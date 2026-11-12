// The "MS2 Maps" view: the entities of a map's files and their edits as map ops of the mod. A map search (id or name,
// the maps of the Pick dialog), the map files of the chosen map (its row in the map table: one per environment), the
// entities of the chosen file (Resource/Exported.m2d, read by a job of the MS2 Tables view) with a filter, and the
// property items of the chosen entity: as shipped, as the mod has them, and the model's default.
//
// Edits (MS2.ModKit.MapEditor): set or unset one property item, remove an entity, restore it. Each edit is a <change>
// or <remove> op of the mod's <map xblock="..."> with the shipped value as the expected old one, so Plan makes it on
// every target that ships the same entity (client, global client, server) and warns where one does not. Add spawn
// point and Add portal make a <spawn> or <portal> op: the new entity is listed first as "new", its properties, numbers
// and server rows are edited the same way, and Remove entity takes it out of the mod. Add other adds an NPC spawn
// point, an interact object or a trigger box as a plain <add> op, listed and edited the same way. Plan, Apply and Save mod are the
// MS2 Tables view's. Each edited row has a mark in its first column that stays when the row is selected.
//
// The view holds no data logic: MS2.ModKit.MapEditor reads the files and makes the ops. The session, the worker
// thread and the output stay in the MS2 Tables view, as for the other entity views.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Emergent.Toolbench;
using Emergent.Toolbench.UI.Workbench;
using MS2.ModKit;
using MS2.ModKit.MapEdit;

namespace MS2.Tables
{
    public class MapsView : AbstractView
    {
        public const string ViewId = "MS2.Tables.MapsView";
        const int MaxHits = 500, MaxEntities = 5000;

        TablesView host;
        IdLookup lookup;
        EditorSession lookupSession;
        MapEditor editor;
        DataSet editorData;
        long mapId;
        string xblock;
        List<XEntity> entities;
        XEntity entity;
        MapEntityOp newOp;
        bool filling;

        readonly TextBox searchBox = new TextBox { Width = 200 };
        readonly Label hitsLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListBox mapList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        readonly ListBox fileList = new ListBox { Dock = DockStyle.Bottom, Height = 140, IntegralHeight = false };
        readonly Label filesLabel = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = 20, TextAlign = ContentAlignment.BottomLeft };
        readonly TextBox filterBox = new TextBox { Width = 180 };
        readonly CheckBox editedOnlyBox = new CheckBox { Text = "edited only", AutoSize = true, Margin = new Padding(8, 6, 3, 0) };
        readonly Label entitiesLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 8, 0) };
        readonly ListView entityList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        readonly ListView propList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        readonly Label entityLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 8, 0) };
        readonly ComboBox propBox = new ComboBox { Width = 170 };
        readonly TextBox indexBox = new TextBox { Width = 40 };
        readonly TextBox valueBox = new TextBox { Width = 200 };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 130 };
        readonly ToolTip tips = new ToolTip();
        Font struck;
        /// <summary>A data-set change came while a job ran: the chosen map file is read again when it ends.</summary>
        bool reopen;
        readonly List<Control> lockable = new List<Control>();
        readonly List<long> hitIds = new List<long>();
        readonly List<string> fileNames = new List<string>();

        readonly ImageList marks = new ImageList { ImageSize = new Size(12, 12), ColorDepth = ColorDepth.Depth32Bit };
        const int MarkChanged = 0, MarkRemoved = 1, MarkNew = 2;

        static readonly string[] EntityColumns = { "name", "model", "position", "edit", "id" };
        static readonly int[] EntityWidths = { 220, 200, 160, 70, 260 };
        static readonly string[] PropColumns = { "property", "index", "shipped", "in the mod", "model default", "type" };
        static readonly int[] PropWidths = { 190, 50, 170, 170, 130, 90 };

        public MapsView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(Lbl("Map id or name"));
            top.Controls.Add(searchBox);
            top.Controls.Add(Btn("Find", "Lists the maps whose id starts with the text or whose name has it", delegate { Find(); }));
            top.Controls.Add(hitsLabel);

            var left = new Panel { Dock = DockStyle.Left, Width = 300 };
            // docked from the last child to the first: the file list at the bottom, its label above it, the splitter, the maps
            left.Controls.Add(mapList);
            left.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            left.Controls.Add(filesLabel);
            left.Controls.Add(fileList);
            tips.SetToolTip(fileList, "The map files (xblock/<name>.xblock) the map's row names, one per environment, with its feature and locale");

            FlowLayoutPanel entityBar = Bar();
            entityBar.Controls.Add(Lbl("Filter"));
            entityBar.Controls.Add(filterBox);
            tips.SetToolTip(filterBox, "Shows the entities whose name, model or id has this text (Enter)");
            entityBar.Controls.Add(editedOnlyBox);
            tips.SetToolTip(editedOnlyBox, "Shows only the entities the mod changes or removes");
            entityBar.Controls.Add(entitiesLabel);
            entityBar.Controls.Add(Btn("Add spawn point", "Adds a spawn point to the map file in the mod, with the next free SpawnPointID of the mod range; give it server rows as \"" + MapEditor.ServerRow + "\"", delegate { AddNew(false); }));
            entityBar.Controls.Add(Btn("Add portal", "Adds a portal to the map file in the mod, with the next free PortalID of the mod range, that leads to a portal of a map", delegate { AddNew(true); }));
            var otherMenu = new ContextMenuStrip();
            foreach (MapEditor.AddKind kind in MapEditor.AddKinds)
            {
                MapEditor.AddKind k = kind;
                otherMenu.Items.Add("Add " + k.Label).Click += delegate { Guard(delegate { AddOther(k); }); };
            }
            Disposed += delegate { otherMenu.Dispose(); };
            Button other = null;
            other = Btn("Add other...", "Adds an NPC spawn point, an interact object or a trigger box to the map file in the mod (a plain <add>); other kinds are written in mod.xml by hand",
                delegate { otherMenu.Show(other, new Point(0, other.Height)); });
            entityBar.Controls.Add(other);
            MakeMarks();
            entityList.SmallImageList = marks;
            propList.SmallImageList = marks;
            for (int i = 0; i < EntityColumns.Length; i++) entityList.Columns.Add(EntityColumns[i], EntityWidths[i]);
            entityList.ShowItemToolTips = true;
            var entityPanel = new Panel { Dock = DockStyle.Fill };
            entityPanel.Controls.Add(entityList);
            entityPanel.Controls.Add(entityBar);

            FlowLayoutPanel editBar = Bar();
            editBar.Controls.Add(entityLabel);
            editBar.Controls.Add(Lbl("Property"));
            editBar.Controls.Add(propBox);
            tips.SetToolTip(propBox, "A property of the chosen entity (pick one, or type the name of one its model has)");
            editBar.Controls.Add(Lbl("index"));
            editBar.Controls.Add(indexBox);
            tips.SetToolTip(indexBox, "The item of a list property (empty for a plain one)");
            editBar.Controls.Add(valueBox);
            tips.SetToolTip(valueBox, "The value; Set with the shipped value again takes the set out of the mod");
            editBar.Controls.Add(Btn("Set", "Sets the property item of the chosen entity in the mod (the shipped value is kept as the expected old one)", delegate { SetProp(false); }));
            editBar.Controls.Add(Btn("Unset", "The entity stores no value for the item in the mod: it takes its model's default", delegate { SetProp(true); }));
            editBar.Controls.Add(Btn("Remove entity", "Removes the chosen entity from the map file in the mod (its sets are dropped)", delegate { RemoveEntity(); }));
            editBar.Controls.Add(Btn("Restore entity", "Takes every edit of the chosen entity out of the mod", delegate { RestoreEntity(); }));

            for (int i = 0; i < PropColumns.Length; i++) propList.Columns.Add(PropColumns[i], PropWidths[i], i == 1 ? HorizontalAlignment.Right : HorizontalAlignment.Left);
            propList.ShowItemToolTips = true;
            var propPanel = new Panel { Dock = DockStyle.Bottom, Height = 260 };
            propPanel.Controls.Add(propList);
            propPanel.Controls.Add(editBar);

            FlowLayoutPanel modBar = Bar();
            modBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change, with a warning for each target whose map file differs; writes nothing", delegate { return host.DoPlan(); }));
            modBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change (the MS2 Tables view's Apply)", delegate { return host.DoApply(); }));
            modBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(entityPanel);
            right.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            right.Controls.Add(propPanel);
            right.Controls.Add(modBar);

            Controls.Add(right);
            Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
            Controls.Add(left);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(outputBox);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, left, right });
            ViewLayout.KeepLogLines(outputBox, this);
            ViewLayout.KeepHeightShare(fileList, left, 0.3, 4 * fileList.ItemHeight + 4);
            ViewLayout.KeepHeightShare(propPanel, right, 0.45, 140);
            ViewLayout.CentreOnFirstShow(this);

            searchBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                Guard(delegate { Find(); });
            };
            filterBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                Guard(delegate { FillEntities(); });
            };
            editedOnlyBox.CheckedChanged += delegate { if (!filling) Guard(delegate { FillEntities(); }); };
            mapList.SelectedIndexChanged += delegate { if (!filling) Guard(delegate { OpenMap(); }); };
            fileList.SelectedIndexChanged += delegate { if (!filling) Guard(delegate { OpenFile(); }); };
            entityList.SelectedIndexChanged += delegate { if (!filling) Guard(delegate { OpenEntity(); }); };
            propList.SelectedIndexChanged += delegate { if (!filling) PickProp(); };
            propBox.SelectedIndexChanged += delegate { if (!filling) ShowValue(); };
            valueBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                Guard(delegate { SetProp(false); });
            };
            entityList.Resize += delegate { FitColumns(entityList, EntityColumns.Length - 1); };
            propList.Resize += delegate { FitColumns(propList, 2, 3); };
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
                marks.Dispose();
                if (struck != null) struck.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---- small builders ----

        /// <summary>The marks of edited rows: an image keeps its colours on a selected row, a ForeColor does not.</summary>
        void MakeMarks()
        {
            Color[] fill = { Color.RoyalBlue, Color.Gray, Color.SeaGreen };
            for (int i = 0; i < fill.Length; i++)
            {
                var b = new Bitmap(12, 12);
                using (Graphics g = Graphics.FromImage(b))
                using (var br = new SolidBrush(fill[i]))
                using (var pen = new Pen(Color.White, 2))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    g.FillEllipse(br, 1, 1, 10, 10);
                    if (i == MarkRemoved) g.DrawLine(pen, 3, 6, 9, 6);
                    if (i == MarkNew) { g.DrawLine(pen, 3, 6, 9, 6); g.DrawLine(pen, 6, 3, 6, 9); }
                }
                marks.Images.Add(b);
            }
        }

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
            lookup = null;
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
                Logger.Warning("MS2 Maps: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        void OnHostSaid(string text) { OutputWrap.Set(outputBox, text); }

        void OnHostBusy(bool on)
        {
            ViewLayout.Lock(this, lockable, on, outputBox);
            if (on || !reopen || IsDisposed || !IsHandleCreated) return;
            reopen = false;
            // after the job's own end: the file chosen when the data set changed is read from the new one
            BeginInvoke((MethodInvoker)delegate
            {
                if (IsDisposed || host == null || host.Busy || xblock != null || fileList.SelectedIndex < 0) return;
                Guard(delegate { OpenFile(); });
            });
        }

        /// <summary>Another session, mod or edit: the lists again from the same map, file and entity.</summary>
        void OnHostChanged()
        {
            lookup = null;
            try
            {
                FillMaps(mapId);
                if (editorData != Session().Data)
                {
                    // another data set: its map files are read again
                    editor = null;
                    editorData = null;
                    entities = null;
                    entity = null;
                    newOp = null;
                    string keep = xblock;
                    xblock = null;
                    FillEntities();
                    if (mapId != 0)
                    {
                        ShowFiles(mapId, keep);
                        if (fileList.SelectedIndex >= 0)
                        {
                            if (host.Busy) reopen = true;
                            else OpenFile();
                        }
                    }
                }
                else FillEntities();
            }
            catch (ModKitException ex) { host.SayOutside(ex.Message); }
            catch (System.IO.IOException ex) { host.SayOutside(ex.Message); }
            catch (System.IO.InvalidDataException ex) { host.SayOutside(ex.Message); }
            catch (UnauthorizedAccessException ex) { host.SayOutside(ex.Message); }
        }

        EditorSession Session() { return host.SharedSession(); }

        IdLookup Lookup()
        {
            EditorSession s = Session();
            if (lookup == null || lookupSession != s || lookup.Mod != s.Mod) { lookup = new IdLookup(s); lookupSession = s; }
            return lookup;
        }

        MapEditor Editor()
        {
            DataSet d = Session().Data;
            if (editor == null || editorData != d) { editor = new MapEditor(d, null); editorData = d; }
            return editor;
        }

        static string N(int n) { return n.ToString("N0", CultureInfo.InvariantCulture); }

        static bool Has(string s, string find) { return s != null && s.IndexOf(find, StringComparison.OrdinalIgnoreCase) >= 0; }

        // ---- maps and their files ----

        void Find()
        {
            FillMaps(mapId);
            if (mapList.Items.Count > 0 && mapList.SelectedIndex < 0) mapList.SelectedIndex = 0;
        }

        void FillMaps(long keep)
        {
            List<EntityHit> hits = Lookup().Find(IdLookup.Map, searchBox.Text.Trim(), MaxHits);
            filling = true;
            try
            {
                mapList.BeginUpdate();
                mapList.Items.Clear();
                hitIds.Clear();
                int sel = -1;
                foreach (EntityHit h in hits)
                {
                    if (h.Id == keep) sel = hitIds.Count;
                    hitIds.Add(h.Id);
                    mapList.Items.Add(h.ToString());
                }
                mapList.SelectedIndex = sel;
                mapList.EndUpdate();
            }
            finally { filling = false; }
            hitsLabel.Text = hits.Count >= MaxHits ? MaxHits + "+ maps" : hits.Count + " map(s)";
        }

        void OpenMap()
        {
            int i = mapList.SelectedIndex;
            if (i < 0 || i >= hitIds.Count) return;
            ShowFiles(hitIds[i], null);
            if (fileList.Items.Count > 0 && fileList.SelectedIndex < 0) fileList.SelectedIndex = 0;
            else if (fileList.Items.Count == 0) { ShowEntities(null, null); host.SayOutside("map " + hitIds[i] + ": its row names no map file"); }
        }

        void ShowFiles(long id, string keep)
        {
            mapId = id;
            List<TriggerFolder> files = MapEditor.FilesOf(Session().Data, id);
            filling = true;
            int sel = -1;
            try
            {
                fileList.BeginUpdate();
                fileList.Items.Clear();
                fileNames.Clear();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (TriggerFolder f in files)
                {
                    if (f.XBlock == null || !seen.Add(f.XBlock)) continue;
                    if (keep != null && string.Equals(f.XBlock, keep, StringComparison.OrdinalIgnoreCase)) sel = fileNames.Count;
                    fileNames.Add(f.XBlock);
                    string extra = (f.Feature.Length > 0 ? " feature " + f.Feature : "") + (f.Locale.Length > 0 ? " locale " + f.Locale : "");
                    fileList.Items.Add(f.XBlock + extra);
                }
                fileList.SelectedIndex = sel;
            }
            finally { fileList.EndUpdate(); filling = false; }
            filesLabel.Text = fileNames.Count + " map file(s) of map " + id.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The chosen map file: its entities are read by a job (a big file takes a moment).</summary>
        void OpenFile()
        {
            int i = fileList.SelectedIndex;
            if (i < 0 || i >= fileNames.Count) return;
            string name = fileNames[i];
            MapEditor ed = Editor();
            if (!ed.Has(name)) { ShowEntities(null, null); host.SayOutside(ed.Exported + " has no map file xblock/" + MapEditor.Key(name) + ".xblock"); return; }
            // the old file's entities go now: a read that fails must not leave them to be edited under this file's name
            ShowEntities(null, null);
            List<XEntity> got = null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            host.RunOutside("reading map file " + name, delegate { got = ed.Shipped(name); }, delegate
            {
                if (IsDisposed || host == null || ed != editor) return;
                ShowEntities(name, got);
                host.SayOutside(Describe() + " (" + (watch.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s)");
            });
        }

        string Describe()
        {
            if (xblock == null || entities == null) return "";
            Mod mod = Session().Mod;
            int changed = 0, removed = 0;
            foreach (MapOpKind k in MapEditor.Edits(mod, xblock).Values)
            {
                if (k == MapOpKind.Remove) removed++;
                else changed++;
            }
            int added = MapEditor.NewEntities(mod, xblock);
            return "map file " + xblock + " (" + editor.Exported + "): " + N(entities.Count) + " entities; mod " + mod.Id + " changes "
                + N(changed) + ", removes " + N(removed) + ", adds " + N(added);
        }

        // ---- entities ----

        void ShowEntities(string name, List<XEntity> list)
        {
            xblock = name;
            entities = list;
            entity = null;
            newOp = null;
            FillEntities();
        }

        void FillEntities()
        {
            string keepId = entity != null ? entity.Id : null;
            MapEntityOp keepNew = newOp;
            string find = filterBox.Text.Trim();
            Mod mod = Session().Mod;
            int shown = 0, matched = 0, added = 0;
            Dictionary<string, MapOpKind> edits = MapEditor.Edits(mod, xblock);
            if (struck == null) struck = new Font(entityList.Font, FontStyle.Strikeout);
            filling = true;
            try
            {
                entityList.BeginUpdate();
                entityList.Items.Clear();
                if (entities != null)
                {
                    // the mod's new entities first: they are few and always in view
                    foreach (MapEntityOp op in MapEditor.NewOps(mod, xblock))
                    {
                        added++;
                        string kind = op.Kind == MapOpKind.Spawn ? "spawn point " : op.Kind == MapOpKind.Portal ? "portal " : "";
                        string idText = "(new " + Editor().NewLabel(op) + ")";
                        if (find.Length > 0 && !Has(op.Name, find) && !Has(op.Model, find) && !Has(idText, find) && !Has(op.NewId, find)) continue;
                        matched++;
                        shown++;
                        var it = new ListViewItem(op.Name ?? "", MarkNew) { Tag = op };
                        it.SubItems.Add(op.Model ?? "");
                        it.SubItems.Add(MapEditor.Position(op));
                        it.SubItems.Add("new");
                        it.SubItems.Add(idText);
                        it.ForeColor = Color.SeaGreen;
                        it.ToolTipText = "the mod adds this " + (kind.Length > 0 ? kind.Trim() : "entity") + "; Remove entity takes it out";
                        entityList.Items.Add(it);
                        if (op == keepNew) it.Selected = true;
                    }
                    foreach (XEntity e in entities)
                    {
                        if (find.Length > 0 && !Has(e.Name, find) && !Has(e.Model, find) && !Has(e.Id, find)) continue;
                        MapOpKind k = MapOpKind.Change;
                        bool edited = e.Id != null && edits.TryGetValue(e.Id, out k);
                        bool removed = edited && k == MapOpKind.Remove, changed = edited && !removed;
                        if (editedOnlyBox.Checked && !removed && !changed) continue;
                        matched++;
                        if (shown >= MaxEntities) continue;
                        shown++;
                        var it = new ListViewItem(e.Name ?? "", removed ? MarkRemoved : changed ? MarkChanged : -1) { Tag = e };
                        it.SubItems.Add(e.Model ?? "");
                        it.SubItems.Add(MapEditor.Position(e));
                        it.SubItems.Add(removed ? "removed" : changed ? "changed" : "");
                        it.SubItems.Add(e.Id ?? "");
                        if (removed) { it.ForeColor = SystemColors.GrayText; it.Font = struck; }
                        else if (changed) it.ForeColor = Color.RoyalBlue;
                        it.ToolTipText = removed ? "the mod removes this entity" : changed ? "the mod sets properties of this entity" : "as shipped";
                        entityList.Items.Add(it);
                        if (keepId != null && string.Equals(e.Id, keepId, StringComparison.OrdinalIgnoreCase)) it.Selected = true;
                    }
                }
            }
            finally { entityList.EndUpdate(); filling = false; }
            FitColumns(entityList, EntityColumns.Length - 1);
            entitiesLabel.Text = entities == null ? "" : matched > shown ? N(shown) + " of " + N(matched) + " entities shown (filter to see the rest)"
                : N(matched) + " of " + N(entities.Count + added) + " entities";
            if (entityList.SelectedItems.Count > 0) entityList.SelectedItems[0].EnsureVisible();
            else { entity = null; newOp = null; }
            FillProps();
        }

        void OpenEntity()
        {
            object tag = entityList.SelectedItems.Count > 0 ? entityList.SelectedItems[0].Tag : null;
            entity = tag as XEntity;
            newOp = tag as MapEntityOp;
            FillProps();
        }

        // ---- properties ----

        void FillProps()
        {
            string keepName = propBox.Text, keepIndex = indexBox.Text;
            filling = true;
            try
            {
                propList.BeginUpdate();
                propList.Items.Clear();
                propBox.Items.Clear();
                if (entity == null && newOp == null)
                {
                    entityLabel.Text = "no entity chosen";
                    return;
                }
                Mod mod = Session().Mod;
                List<MapPropRow> rows;
                if (newOp != null)
                {
                    entityLabel.Text = newOp.Name + " (new " + Editor().NewLabel(newOp) + ")";
                    rows = Editor().RowsOfNew(newOp);
                    if (newOp.Kind == MapOpKind.Spawn)
                        rows.Add(new MapPropRow { Name = MapEditor.ServerRow, Index = newOp.Rows.Count.ToString(CultureInfo.InvariantCulture), Type = "server" });   // the next row to add
                }
                else
                {
                    bool removed = MapEditor.IsRemoved(mod, xblock, entity.Id);
                    entityLabel.Text = (entity.Name ?? entity.Id) + (removed ? " (removed by the mod)" : "");
                    rows = Editor().Rows(mod, xblock, entity);
                }
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (MapPropRow r in rows)
                {
                    var it = new ListViewItem(r.Name ?? "", r.Edited ? (newOp != null ? MarkNew : MarkChanged) : -1) { Tag = r };
                    it.SubItems.Add(r.Index ?? "");
                    it.SubItems.Add(r.Shipped ?? "");
                    it.SubItems.Add(r.Edited ? (r.Current ?? "(unset)") : "");
                    it.SubItems.Add(r.Default ?? "");
                    it.SubItems.Add(r.Type ?? "");
                    if (r.Edited) it.ForeColor = newOp != null ? Color.SeaGreen : Color.RoyalBlue;
                    else if (r.Shipped == null) it.ForeColor = SystemColors.GrayText;
                    it.ToolTipText = newOp != null ? (r.Edited ? "the new entity has this item" : r.Name == MapEditor.ServerRow ? "Set a server row here to add one (key=value pairs)" : "the model's default holds; Set gives the new entity a value")
                        : r.Edited ? "the mod " + (r.Current == null ? "unsets" : "sets") + " this item; shipped: " + (r.Shipped ?? "none")
                        : r.Shipped == null ? "the map stores no value: the model's default holds" : "as shipped";
                    propList.Items.Add(it);
                    if (r.Name != null && names.Add(r.Name)) propBox.Items.Add(r.Name);
                    if (r.Name == keepName && (r.Index ?? "") == keepIndex) it.Selected = true;
                }
            }
            finally { propList.EndUpdate(); filling = false; }
            FitColumns(propList, 2, 3);
            propBox.Text = keepName;
        }

        void PickProp()
        {
            if (propList.SelectedItems.Count == 0) return;
            var r = (MapPropRow)propList.SelectedItems[0].Tag;
            filling = true;
            try
            {
                propBox.Text = r.Name;
                indexBox.Text = r.Index ?? "";
                valueBox.Text = r.Current ?? r.Default ?? "";
            }
            finally { filling = false; }
        }

        /// <summary>A property picked by name: its first item, the value as the mod has it.</summary>
        void ShowValue()
        {
            foreach (ListViewItem it in propList.Items)
            {
                var r = (MapPropRow)it.Tag;
                if (r.Name != propBox.Text) continue;
                indexBox.Text = r.Index ?? "";
                valueBox.Text = r.Current ?? r.Default ?? "";
                return;
            }
        }

        // ---- edits ----

        Mod NeedEntity()
        {
            if (xblock == null || entities == null) throw new ModKitException("choose a map file first");
            if (entity == null && newOp == null) throw new ModKitException("choose an entity first");
            Mod mod = Session().Mod;
            if (mod == null) throw new ModKitException("no mod is being edited");
            return mod;
        }

        void Edited(string what)
        {
            host.EditedOutside();
            FillEntities();
            host.SayOutside(what + "; Plan shows it on every target, Save mod keeps it");
        }

        void SetProp(bool unset)
        {
            Mod mod = NeedEntity();
            string name = propBox.Text.Trim(), index = indexBox.Text.Trim();
            if (name.Length == 0) throw new ModKitException("give the name of the property");
            string value = unset ? null : valueBox.Text;
            if (newOp != null) Editor().SetNew(mod, xblock, newOp, name, index.Length == 0 ? null : index, value);
            else MapEditor.Set(mod, xblock, entity, name, index.Length == 0 ? null : index, value, Editor().Model(entity.Model));
            Edited((unset ? "unset " : "set ") + name + (index.Length > 0 ? "[" + index + "]" : "") + (unset ? "" : " = " + value)
                + " on " + (newOp != null ? newOp.Name + " (new)" : entity.Name ?? entity.Id) + " in " + xblock);
        }

        void RemoveEntity()
        {
            Mod mod = NeedEntity();
            if (newOp != null)
            {
                string gone = newOp.Name;
                MapEditor.RemoveNew(mod, xblock, newOp);
                newOp = null;
                Edited("took the new entity " + gone + " out of the mod");
                return;
            }
            MapEditor.Remove(mod, xblock, entity);
            Edited("removed " + (entity.Name ?? entity.Id) + " from " + xblock);
        }

        void RestoreEntity()
        {
            Mod mod = NeedEntity();
            if (newOp != null) throw new ModKitException(newOp.Name + " is new: nothing of it ships to restore; Remove entity takes it out of the mod");
            MapEditor.Restore(mod, xblock, entity.Id);
            Edited("restored " + (entity.Name ?? entity.Id) + " in " + xblock + " (no edit of it is left in the mod)");
        }

        /// <summary>Add spawn point / Add portal: the models of the file that fit are read by a job, then the dialog.</summary>
        void AddNew(bool portal)
        {
            if (xblock == null || entities == null) throw new ModKitException("choose a map file first");
            Mod mod = Session().Mod;
            if (mod == null) throw new ModKitException("no mod is being edited");
            MapEditor ed = Editor();
            string file = xblock;
            long map = mapId;
            string near = entity != null ? MapEditor.Position(entity) : newOp != null ? MapEditor.Position(newOp) : "";
            List<string> models = null;
            long number = 0;
            host.RunOutside("reading the models of " + file, delegate
            {
                models = portal ? ed.ModelsWith(file, "PortalID", "portal") : ed.SpawnModels(file);
                number = ed.NextNumber(mod, file, portal);
            }, delegate
            {
                if (IsDisposed || ed != editor || file != xblock) return;
                Guard(delegate { ShowAddDialog(ed, mod, map, file, portal, models, number, near); });
            });
        }

        /// <summary>Add other: an NPC spawn point, interact object or trigger box; its models are read by a job, then the dialog.</summary>
        void AddOther(MapEditor.AddKind kind)
        {
            if (xblock == null || entities == null) throw new ModKitException("choose a map file first");
            Mod mod = Session().Mod;
            if (mod == null) throw new ModKitException("no mod is being edited");
            MapEditor ed = Editor();
            string file = xblock;
            long map = mapId;
            string near = entity != null ? MapEditor.Position(entity) : newOp != null ? MapEditor.Position(newOp) : "";
            List<string> models = null;
            long number = -1;
            host.RunOutside("reading the models of " + file, delegate
            {
                models = ed.ModelsOf(file, kind);
                if (kind.Number != null) number = ed.NextNumber(mod, file, kind.Number);
            }, delegate
            {
                if (IsDisposed || ed != editor || file != xblock) return;
                Guard(delegate { ShowOtherDialog(ed, mod, map, file, kind, models, number, near); });
            });
        }

        void ShowOtherDialog(MapEditor ed, Mod mod, long map, string file, MapEditor.AddKind kind, List<string> models, long number, string near)
        {
            string stem = kind == MapEditor.NpcSpawn ? "npc-" : kind == MapEditor.Interact ? "interact-" : "trigger-";
            int k = 1;
            while (MapEditor.FindNew(mod, file, stem + k) != null) k++;
            using (var dlgTips = new ToolTip())
            using (var dlg = new Form { Text = "Add " + kind.Label + " to " + file, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ShowInTaskbar = false })
            {
                var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8) };
                var nameBox = new TextBox { Width = 260, Text = stem + k };
                var modelBox = new ComboBox { Width = 260 };
                foreach (string m in models) modelBox.Items.Add(m);
                if (models.Count > 0) modelBox.SelectedIndex = 0;
                var posBox = new TextBox { Width = 260, Text = near };
                var aBox = new TextBox { Width = 260 };
                var bBox = new TextBox { Width = 260, Text = "1" };
                Action<string, Control, string> row = delegate (string label, Control c, string tip)
                {
                    grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 8, 0) });
                    grid.Controls.Add(c);
                    dlgTips.SetToolTip(c, tip);
                };
                row("Name", nameBox, "The name of the op in the mod and of the new entity; its id comes from the mod id, the map file and this name");
                row("Model", modelBox, models.Count > 0 ? "The " + kind.Label + " models (mixin " + kind.Mixin + ") the map file's entities use, most used first; when none does, the listing's models named like one (or type another of the model listing)"
                    : "No " + kind.Label + " model found: type one of the model listing (it needs the mixin " + kind.Mixin + ")");
                row("Position", posBox, "x, y, z (the chosen entity's position to start from); empty: the model's default");
                if (kind == MapEditor.NpcSpawn)
                {
                    row("NPC id", aBox, "The id of the NPC that spawns there (a row of the NPC table): NpcList [NPC id]");
                    row("Count", bBox, "How many of the NPC spawn there: the NpcList value; NpcCount follows the sum of the NpcList values");
                }
                else if (kind == MapEditor.Interact)
                    row("interactID", aBox, "The id of the object's row in the interact object table (several entities may share it)");
                else
                {
                    aBox.Text = "";
                    row("Size", aBox, "ShapeDimensions x, y, z of the box; empty: the model's");
                }
                if (kind.Number != null)
                {
                    grid.Controls.Add(new Label { Text = kind.Number + " " + number.ToString(CultureInfo.InvariantCulture) + " (the next free one of the mod range; editable after)",
                        AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
                    grid.SetColumnSpan(grid.Controls[grid.Controls.Count - 1], 2);
                }
                var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, UseVisualStyleBackColor = true, ForeColor = SystemColors.ControlText };
                var ok = new Button { Text = "Add", AutoSize = true, UseVisualStyleBackColor = true, ForeColor = SystemColors.ControlText };
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(ok);
                grid.Controls.Add(buttons);
                grid.SetColumnSpan(buttons, 2);
                dlg.Controls.Add(grid);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                MapEntityOp made = null;
                ok.Click += delegate
                {
                    try
                    {
                        string model = modelBox.Text.Trim();
                        if (kind == MapEditor.NpcSpawn)
                            made = ed.AddNpcSpawn(mod, map, file, nameBox.Text, model, posBox.Text, Whole("NPC id", aBox.Text), Whole("Count", bBox.Text));
                        else if (kind == MapEditor.Interact)
                            made = ed.AddInteract(mod, map, file, nameBox.Text, model, posBox.Text, Whole("interactID", aBox.Text));
                        else
                            made = ed.AddTriggerBox(mod, map, file, nameBox.Text, model, posBox.Text, aBox.Text.Trim());
                        dlg.DialogResult = DialogResult.OK;
                    }
                    catch (ModKitException ex) { MessageBox.Show(dlg, ex.Message, dlg.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                };
                if (dlg.ShowDialog(this) != DialogResult.OK || made == null) return;
                entity = null;
                newOp = made;
                Edited("added " + made.Name + " (" + ed.NewLabel(made) + ") to " + file);
            }
        }

        static long Whole(string what, string s)
        {
            long v;
            if (!long.TryParse((s ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out v)) throw new ModKitException(what + " takes a whole number");
            return v;
        }

        void ShowAddDialog(MapEditor ed, Mod mod, long map, string file, bool portal, List<string> models, long number, string near)
        {
            string what = portal ? "portal" : "spawn point";
            int k = 1;
            while (MapEditor.FindNew(mod, file, (portal ? "portal-" : "spawn-") + k) != null) k++;
            using (var dlgTips = new ToolTip())
            using (var dlg = new Form { Text = "Add " + what + " to " + file, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ShowInTaskbar = false })
            {
                var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8) };
                var nameBox = new TextBox { Width = 260, Text = (portal ? "portal-" : "spawn-") + k };
                var modelBox = new ComboBox { Width = 260 };
                foreach (string m in models) modelBox.Items.Add(m);
                if (models.Count > 0) modelBox.SelectedIndex = 0;
                var posBox = new TextBox { Width = 260, Text = near };
                var toMapBox = new TextBox { Width = 260 };
                var toPortalBox = new TextBox { Width = 260 };
                Action<string, Control, string> row = delegate (string label, Control c, string tip)
                {
                    grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 8, 0) });
                    grid.Controls.Add(c);
                    dlgTips.SetToolTip(c, tip);
                };
                row("Name", nameBox, "The name of the op in the mod and of the new entity; its id comes from the mod id, the map file and this name");
                row("Model", modelBox, models.Count > 0 ? "The " + what + " models the map file's entities use, most used first; when none does, the listing's models named like one (or type another of the model listing)"
                    : "No " + what + " model found: type one of the model listing");
                row("Position", posBox, "x, y, z (the chosen entity's position to start from); empty: the model's default");
                if (portal)
                {
                    row("To map", toMapBox, "The map id the portal leads to (TargetFieldSN)");
                    row("To portal", toPortalBox, "The PortalID of the portal it leads to on that map (TargetPortalID)");
                }
                grid.Controls.Add(new Label { Text = (portal ? "PortalID " : "SpawnPointID ") + number.ToString(CultureInfo.InvariantCulture) + " (the next free one of the mod range; editable after)",
                    AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
                grid.SetColumnSpan(grid.Controls[grid.Controls.Count - 1], 2);
                var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, UseVisualStyleBackColor = true, ForeColor = SystemColors.ControlText };
                var ok = new Button { Text = "Add", AutoSize = true, UseVisualStyleBackColor = true, ForeColor = SystemColors.ControlText };
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(ok);
                grid.Controls.Add(buttons);
                grid.SetColumnSpan(buttons, 2);
                dlg.Controls.Add(grid);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                MapEntityOp made = null;
                ok.Click += delegate
                {
                    try
                    {
                        if (portal)
                        {
                            long tm, tp;
                            if (!long.TryParse(toMapBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out tm)) throw new ModKitException("To map takes a map id");
                            if (!long.TryParse(toPortalBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out tp)) throw new ModKitException("To portal takes a PortalID");
                            made = ed.AddPortal(mod, map, file, nameBox.Text, modelBox.Text.Trim(), posBox.Text, tm, tp);
                        }
                        else made = ed.AddSpawn(mod, map, file, nameBox.Text, modelBox.Text.Trim(), posBox.Text);
                        dlg.DialogResult = DialogResult.OK;
                    }
                    catch (ModKitException ex) { MessageBox.Show(dlg, ex.Message, dlg.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                };
                if (dlg.ShowDialog(this) != DialogResult.OK || made == null) return;
                entity = null;
                newOp = made;
                Edited("added " + what + " " + made.Name + " (" + (portal ? "PortalID " : "SpawnPointID ") + made.Number.ToString(CultureInfo.InvariantCulture) + ") to " + file
                    + (portal ? "" : "; give it a server row (property " + MapEditor.ServerRow + ", e.g. tag=... population=... coolTime=...) so that something spawns"));
            }
        }

        // ---- layout ----

        /// <summary>The columns named (0-based) share the room the others leave.</summary>
        static void FitColumns(ListView list, params int[] fill)
        {
            int used = 0;
            for (int i = 0; i < list.Columns.Count; i++) if (Array.IndexOf(fill, i) < 0) used += list.Columns[i].Width;
            int w = (list.ClientSize.Width - used - 4) / fill.Length;
            foreach (int i in fill) if (i < list.Columns.Count) list.Columns[i].Width = w < 120 ? 120 : w;
        }
    }
}
