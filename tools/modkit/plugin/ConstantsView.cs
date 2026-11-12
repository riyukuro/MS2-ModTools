// The "MS2 Constants" view: the game constants (MS2.ModKit.Constants) as a mod edit.
//
// Four copies of the table: table/Server/constants.xml of the server's Server.m2d (the one the server's own tables
// carry), and table/constants.xml of the server's Xml.m2d, the kms client and the global client. A row is a key with
// a value (and a locale or feature it is for). The list shows each constant with its value on every copy with the
// mod (an edited constant in blue, one whose copies differ in orange; the shipped values in the tooltip). Set writes
// the value to every copy that has the constant, or to one copy; Take puts the shipped values back. Plan, Apply and
// Save mod are the MS2 Tables view's.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Emergent.Toolbench;
using Emergent.Toolbench.UI.Workbench;
using MS2.ModKit;

namespace MS2.Tables
{
    public class ConstantsView : AbstractView
    {
        public const string ViewId = "MS2.Tables.ConstantsView";

        TablesView host;
        Constants cs;              // for the session's data and mod
        bool read;                 // cs has read every copy (on the worker thread)
        bool filling;

        readonly TextBox filterBox = new TextBox { Width = 160 };
        readonly CheckBox editedBox = new CheckBox { Text = "edited only", AutoSize = true, Margin = new Padding(8, 6, 0, 0) };
        readonly CheckBox differBox = new CheckBox { Text = "copies differ only", AutoSize = true, Margin = new Padding(8, 6, 0, 0) };
        readonly Label sidesLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListView rowList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        readonly Label keyLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 0, 0), Text = "(pick a constant)" };
        readonly ComboBox sideBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        readonly TextBox valueBox = new TextBox { Width = 160 };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 110 };
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();
        readonly Timer filterTimer = new Timer { Interval = 300 };
        ConstId? picked;

        const string EveryCopy = "(every copy)";

        static string Short(ConstSide s)
        {
            if (s == ConstSide.ServerTable) return "server table";
            if (s == ConstSide.ServerXml) return "server xml";
            if (s == ConstSide.KmsXml) return "kms client";
            return "global client";
        }

        public ConstantsView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            sideBox.Items.Add(EveryCopy);
            foreach (ConstSide s in ConstSide.All) sideBox.Items.Add(Short(s));
            sideBox.SelectedIndex = 0;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(Lbl("Key"));
            top.Controls.Add(filterBox);
            tips.SetToolTip(filterBox, "Shows the constants whose key has this text (any case)");
            top.Controls.Add(editedBox);
            tips.SetToolTip(editedBox, "Shows only the constants the mod changes");
            top.Controls.Add(differBox);
            tips.SetToolTip(differBox, "Shows only the constants whose copies have different values with the mod (some ship different)");
            top.Controls.Add(Btn("Read", "Reads the four copies of the constants from the data folders again", delegate { Reload(); }));
            top.Controls.Add(sidesLabel);

            FlowLayoutPanel editBar = Bar();
            editBar.Controls.Add(keyLabel);
            editBar.Controls.Add(sideBox);
            tips.SetToolTip(sideBox, "Every copy that has the constant (the client shows and the server uses the same value), or one copy");
            editBar.Controls.Add(valueBox);
            tips.SetToolTip(valueBox, "The value, of the shipped value's kind (a whole number, a number, true or false); the shipped value takes the line out of the mod. Enter sets it");
            editBar.Controls.Add(Btn("Set", "Sets the value of the constant on the chosen copies", delegate { SetValue(); }));
            editBar.Controls.Add(Btn("Take", "Puts the shipped values of the constant back on every copy", delegate { TakeOne(); }));
            editBar.Controls.Add(Btn("Take all", "Puts the shipped values of every constant back", delegate { TakeAll(); }));

            FlowLayoutPanel modBar = Bar();
            modBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change, with the checks of the constants; writes nothing", delegate { return host.DoPlan(); }));
            modBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change (the MS2 Tables view's Apply)", delegate { return host.DoApply(); }));
            modBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            rowList.ShowItemToolTips = true;
            rowList.Columns.Add("key", 220, HorizontalAlignment.Left);
            rowList.Columns.Add("locale", 50, HorizontalAlignment.Left);
            rowList.Columns.Add("feature", 90, HorizontalAlignment.Left);
            rowList.Columns.Add("kind", 45, HorizontalAlignment.Left);
            foreach (ConstSide s in ConstSide.All) rowList.Columns.Add(Short(s), 95, HorizontalAlignment.Right);
            rowList.Columns.Add("in mod", 55, HorizontalAlignment.Left);

            Controls.Add(rowList);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(outputBox);
            Controls.Add(modBar);
            Controls.Add(editBar);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, editBar, modBar, rowList });
            ViewLayout.KeepLogLines(outputBox, this);
            ViewLayout.CentreOnFirstShow(this);

            filterBox.TextChanged += delegate { filterTimer.Stop(); filterTimer.Start(); };
            filterTimer.Tick += delegate { filterTimer.Stop(); if (read) ShowRows(); };
            editedBox.CheckedChanged += delegate { if (read) ShowRows(); };
            differBox.CheckedChanged += delegate { if (read) ShowRows(); };
            sideBox.SelectedIndexChanged += delegate { if (!filling) ShowValue(); };
            rowList.SelectedIndexChanged += delegate
            {
                if (filling || rowList.SelectedItems.Count == 0) return;
                picked = (ConstId)rowList.SelectedItems[0].Tag;
                ShowValue();
            };
            valueBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                Guard(delegate { SetValue(); });
            };
            VisibleChanged += delegate { if (Visible && !read && TablesView.Open != null) Guard(delegate { ShowTable(); }); };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { Hook(null); filterTimer.Dispose(); }
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
                Logger.Warning("MS2 Constants: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        void OnHostSaid(string text) { OutputWrap.Set(outputBox, text); }

        void OnHostBusy(bool on) { ViewLayout.Lock(this, lockable, on, outputBox); }

        /// <summary>Another session or mod (or an edit of another view): show the constants again.</summary>
        void OnHostChanged()
        {
            EditorSession s = host.SharedSession();
            if (cs != null && (cs.Data != s.Data || cs.Mod != s.Mod)) { cs = null; read = false; }
            if (Visible) { if (read) ShowRows(); else if (!host.Busy) ShowTable(); }
        }

        // ---- the constants ----

        void Reload()
        {
            cs = null;
            read = false;
            ShowTable();
        }

        /// <summary>Reads the copies on the worker thread (once per session and mod), then shows them.</summary>
        void ShowTable()
        {
            EditorSession s = host.SharedSession();
            if (s.Mod == null) { sidesLabel.Text = "no mod is being edited"; return; }
            if (cs == null || cs.Data != s.Data || cs.Mod != s.Mod) { cs = new Constants(s.Data, s.Mod); read = false; }
            if (read) { ShowRows(); return; }
            Constants reading = cs;
            host.RunOutside("constants", delegate { reading.Ids(); }, delegate
            {
                if (reading != cs) return;
                read = true;
                ShowRows();
            });
        }

        void ShowRows()
        {
            if (cs == null || !read) return;
            List<ConstSide> sides = cs.Sides();
            string filter = filterBox.Text.Trim();
            int shown = 0, differ = 0;
            filling = true;
            try
            {
                rowList.BeginUpdate();
                rowList.Items.Clear();
                foreach (ConstId id in cs.Ids())
                {
                    bool edited = cs.IsEdited(id), differs = cs.Differs(id);
                    if (differs) differ++;
                    if (filter.Length > 0 && id.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (editedBox.Checked && !edited) continue;
                    if (differBox.Checked && !differs) continue;
                    var it = new ListViewItem(id.Key) { Tag = id };
                    it.SubItems.Add(id.Locale);
                    it.SubItems.Add(id.Feature);
                    it.SubItems.Add(Constants.KindOf(cs.GetShipped(id)));
                    var tip = new List<string>();
                    foreach (ConstSide s in ConstSide.All)
                    {
                        string now = cs.Get(s, id), was = cs.GetShipped(s, id);
                        it.SubItems.Add(now ?? "");
                        if (was == null) tip.Add(Short(s) + ": no row");
                        else tip.Add(Short(s) + ": " + was + " shipped" + (now != was ? ", " + now + " with the mod" : ""));
                    }
                    it.SubItems.Add(edited ? "yes" : "");
                    if (differs) it.ForeColor = Color.DarkOrange;
                    else if (edited) it.ForeColor = Color.MediumBlue;
                    if (cs.ShippedDiffers(id)) tip.Add("the copies ship different values");
                    it.ToolTipText = string.Join("\n", tip.ToArray());
                    rowList.Items.Add(it);
                    if (picked != null && picked.Value.Equals(id)) it.Selected = true;
                    shown++;
                }
            }
            finally { rowList.EndUpdate(); filling = false; }
            var names = new List<string>();
            foreach (ConstSide s in sides) names.Add(Short(s));
            sidesLabel.Text = (sides.Count == 0 ? "no data folder has the constants" : string.Join(", ", names.ToArray())) + "; " + cs.Ids().Count + " constants, " + cs.EditedCount() + " edited, "
                + differ + " differ between the copies" + (shown < cs.Ids().Count ? "; " + shown + " shown" : "");
            ShowValue();
        }

        ConstSide Side() { return sideBox.SelectedIndex <= 0 ? null : ConstSide.All[sideBox.SelectedIndex - 1]; }

        void ShowValue()
        {
            if (cs == null || !read || picked == null) return;
            ConstId id = picked.Value;
            keyLabel.Text = id.ToString();
            ConstSide side = Side();
            string v = side == null ? cs.Get(id) : cs.Get(side, id);
            filling = true;
            try { valueBox.Text = v ?? ""; }
            finally { filling = false; }
        }

        ConstId Picked()
        {
            if (host.SharedSession().Mod == null) throw new ModKitException("no mod is being edited");
            if (cs == null || !read) throw new ModKitException("the constants are being read: try again");
            if (picked == null) throw new ModKitException("pick a constant in the list");
            return picked.Value;
        }

        void SetValue()
        {
            ConstId id = Picked();
            ConstSide side = Side();
            int n = cs.Set(id, valueBox.Text, side);
            host.EditedOutside();
            ShowRows();
            host.SayOutside(id + " = " + (side == null ? cs.Get(id) : cs.Get(side, id)) + (n == 0 ? ": as it was" : " on " + n + " cop" + (n == 1 ? "y" : "ies"))
                + (cs.IsEdited(id) ? "" : " (as shipped: no line in the mod)") + (cs.Differs(id) && !cs.ShippedDiffers(id) ? "; the copies now differ" : ""));
        }

        void TakeOne()
        {
            ConstId id = Picked();
            bool had = cs.Take(id);
            host.EditedOutside();
            ShowRows();
            host.SayOutside(id + ": " + (had ? "the shipped values again" : "the mod does not change it"));
        }

        void TakeAll()
        {
            if (host.SharedSession().Mod == null) throw new ModKitException("no mod is being edited");
            if (cs == null || !read) throw new ModKitException("the constants are being read: try again");
            int n = cs.TakeAll();
            host.EditedOutside();
            ShowRows();
            host.SayOutside("constants: the shipped values again (" + n + " taken out of the mod)");
        }
    }
}
