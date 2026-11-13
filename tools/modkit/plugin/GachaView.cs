// The "MS2 Gacha" view: the gacha capsules (table/gacha_info.xml, MS2.ModKit.GachaEditor) as a mod edit. A capsule is
// one row on every side that ships the table (the Korean client and the server); the server decides what a capsule
// gives: its drop box and the coin a won item dismantles into.
//
// The list on the left shows the capsules with the mod (an edited one in blue, a new one in green, a removed one in
// grey), filtered by the find box (an id, drop box, coin item or shop). On the right: the capsule's fields (field, value
// with the mod, the shipped value, what it means or what is wrong with it); choosing a field puts its value in the box,
// Set writes it. Under them the capsule items that open it (server item files with OpenGachaBox), the odds of its drop
// box per group, and its checks. Open drop box shows the box in the MS2 Shops and Drops view, which edits it.
// New adds a capsule with a drop box and coin, Copy a copy of the chosen one, Remove / restore deletes a shipped
// capsule or takes a new one out. Plan, Apply, Revert and Save mod are the MS2 Tables view's.
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
    public class GachaView : AbstractView
    {
        public const string ViewId = "MS2.Tables.GachaView";

        TablesView host;
        GachaEditor ed;             // for the session's data and mod
        List<GachaCapsule> capsules = new List<GachaCapsule>();
        Dictionary<long, List<GachaCapsule>> byGacha = new Dictionary<long, List<GachaCapsule>>();
        bool read;                  // ed and the capsule items are read (on the worker thread)
        bool filling;
        Gacha shown;
        string wantKey;             // the key of the capsule to show again once the table is read again
        ItemEditor items;
        readonly Dictionary<long, string> names = new Dictionary<long, string>();

        readonly TextBox findBox = new TextBox { Width = 110 };
        readonly Label sumLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListView rowList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, ShowItemToolTips = true };
        readonly ListView fieldList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, ShowItemToolTips = true };
        readonly ListView capsuleList = new ListView { Dock = DockStyle.Bottom, Height = 80, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        readonly ListView oddsList = new ListView { Dock = DockStyle.Bottom, Height = 150, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        readonly ListBox checkList = new ListBox { Dock = DockStyle.Bottom, Height = 80, HorizontalScrollbar = true, IntegralHeight = false };
        readonly Label rowLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 0, 0), Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
        readonly Label infoLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 0, 0) };
        readonly Label fieldLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 0, 0), Text = "(choose a field)" };
        readonly TextBox valueBox = new TextBox { Width = 260 };
        readonly TextBox newIdBox = new TextBox { Width = 80 };
        readonly TextBox boxBox = new TextBox { Width = 70 };
        readonly TextBox coinBox = new TextBox { Width = 70 };
        readonly TextBox amountBox = new TextBox { Width = 40, Text = "1" };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 110 };
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();

        public GachaView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(Btn("Read", "Reads the capsule table and the capsule items again", delegate { Reload(); }));
            top.Controls.Add(Lbl("Find"));
            top.Controls.Add(findBox);
            tips.SetToolTip(findBox, "A capsule id, drop box, coin item or shop, or part of one");
            top.Controls.Add(sumLabel);

            FlowLayoutPanel rowBar = Bar();
            rowBar.Controls.Add(Lbl("New id"));
            rowBar.Controls.Add(newIdBox);
            tips.SetToolTip(newIdBox, "The new capsule's gacha id (" + GachaRules.From + "-" + GachaRules.To + "); empty: the next free one");
            rowBar.Controls.Add(Lbl("Drop box"));
            rowBar.Controls.Add(boxBox);
            tips.SetToolTip(boxBox, "New: the drop box (individualItemDrop) the capsule rolls");
            rowBar.Controls.Add(Lbl("Coin"));
            rowBar.Controls.Add(coinBox);
            tips.SetToolTip(coinBox, "New: the item a won item dismantles into (empty or 0: none)");
            rowBar.Controls.Add(Lbl("x"));
            rowBar.Controls.Add(amountBox);
            tips.SetToolTip(amountBox, "New: how many coins");
            rowBar.Controls.Add(Btn("New", "Adds a capsule with the drop box and coin; a capsule item must open it (OpenGachaBox)", delegate { NewRow(false); }));
            rowBar.Controls.Add(Btn("Copy", "Adds a copy of the chosen capsule, as the mod leaves it, under the new id", delegate { NewRow(true); }));
            rowBar.Controls.Add(Btn("Remove / restore", "Removes the chosen capsule (a shipped one: a delete line in the mod), or puts a removed shipped one back", delegate { RemoveOrRestore(); }));
            rowBar.Controls.Add(Btn("Open drop box", "Shows the capsule's drop box in the MS2 Shops and Drops view, which edits its items and weights", delegate { OpenBox(); }));

            FlowLayoutPanel modBar = Bar();
            modBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change, with the checks of the capsules; writes nothing", delegate { return host.DoPlan(); }));
            modBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change (the MS2 Tables view's Apply)", delegate { return host.DoApply(); }));
            modBar.Controls.Add(Job("Revert", "Removes what Apply wrote, on every target", delegate { return host.DoRevert(); }));
            modBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            rowList.Columns.Add("capsule", 90, HorizontalAlignment.Right);
            rowList.Columns.Add("about", 280);
            rowList.Columns.Add("in mod", 60);

            fieldList.Columns.Add("field", 150);
            fieldList.Columns.Add("value", 130);
            fieldList.Columns.Add("shipped", 110);
            fieldList.Columns.Add("means", 300);

            capsuleList.Columns.Add("capsule item", 90, HorizontalAlignment.Right);
            capsuleList.Columns.Add("name", 200);
            capsuleList.Columns.Add("parameter", 140);
            capsuleList.Columns.Add("entry", 200);

            oddsList.Columns.Add("group", 50, HorizontalAlignment.Right);
            oddsList.Columns.Add("item", 80, HorizontalAlignment.Right);
            oddsList.Columns.Add("name", 200);
            oddsList.Columns.Add("count", 60, HorizontalAlignment.Right);
            oddsList.Columns.Add("weight", 60, HorizontalAlignment.Right);
            oddsList.Columns.Add("chance", 70, HorizontalAlignment.Right);

            var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(2) };
            head.Controls.Add(rowLabel);
            head.Controls.Add(infoLabel);

            FlowLayoutPanel setBar = Bar();
            setBar.Dock = DockStyle.Bottom;
            setBar.Controls.Add(fieldLabel);
            setBar.Controls.Add(valueBox);
            tips.SetToolTip(valueBox, "The chosen field's value (empty: the attribute is taken out)");
            setBar.Controls.Add(Btn("Set", "Writes the value of the chosen field into the mod (a client-only field on the clients only)", delegate { SetField(); }));

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(fieldList);
            right.Controls.Add(setBar);
            right.Controls.Add(capsuleList);
            right.Controls.Add(oddsList);
            right.Controls.Add(checkList);
            right.Controls.Add(head);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
            split.Panel1.Controls.Add(rowList);
            split.Panel2.Controls.Add(right);

            Controls.Add(split);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(outputBox);
            Controls.Add(modBar);
            Controls.Add(rowBar);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, rowBar, modBar, split });
            ViewLayout.KeepLogLines(outputBox, this);
            ViewLayout.CentreOnFirstShow(this);
            bool sized = false;
            split.SizeChanged += delegate
            {
                if (sized || split.Width < 200) return;
                sized = true;
                split.SplitterDistance = Math.Max(100, split.Width * 38 / 100);
            };

            findBox.TextChanged += delegate { if (!filling && read) Guard(delegate { ShowRows(); }); };
            rowList.SelectedIndexChanged += delegate
            {
                if (filling || rowList.SelectedItems.Count == 0) return;
                shown = (Gacha)rowList.SelectedItems[0].Tag;
                Guard(delegate { ShowRow(); });
            };
            fieldList.SelectedIndexChanged += delegate
            {
                if (filling || fieldList.SelectedItems.Count == 0) return;
                ListViewItem it = fieldList.SelectedItems[0];
                fieldLabel.Text = it.Text;
                valueBox.Text = it.SubItems[1].Text;
            };
            VisibleChanged += delegate { if (Visible && !read && TablesView.Open != null) Guard(delegate { ReadTable(); }); };
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
                if (h == null || h.IsDisposed) h = TablesView.OpenBehind();
                if (h == null) { OutputWrap.Set(outputBox, "the MS2 Tables view could not be opened; this view works on the data of that view"); return false; }
                Hook(h);
            }
            catch (Exception ex)
            {
                OutputWrap.Set(outputBox, "the MS2 Tables view could not be opened: " + ex.Message);
                Logger.Warning("MS2 Gacha: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        void OnHostSaid(string text) { OutputWrap.Set(outputBox, text); }

        void OnHostBusy(bool on) { ViewLayout.Lock(this, lockable, on, outputBox); }

        /// <summary>Another session or mod (or an edit of another view): read the table again, as the mod now has it.</summary>
        void OnHostChanged()
        {
            if (shown != null && ed != null) wantKey = ed.Key(shown);
            ed = null;
            read = false;
            shown = null;
            items = null;
            names.Clear();
            if (Visible && !host.Busy) ReadTable();
        }

        // ---- the capsules ----

        void Reload()
        {
            if (shown != null && ed != null) wantKey = ed.Key(shown);
            ed = null;
            read = false;
            shown = null;
            ReadTable();
        }

        /// <summary>Reads the capsule table and the server's capsule items on the worker thread, then shows them.</summary>
        void ReadTable()
        {
            EditorSession s = host.SharedSession();
            if (s.Mod == null) { sumLabel.Text = "no mod is being edited"; return; }
            if (ed != null && ed.Data == s.Data && ed.Mod == s.Mod && read) { ShowRows(); return; }
            GachaEditor made = null;
            List<GachaCapsule> caps = null;
            string failed = null;
            host.RunOutside("capsule table and capsule items", delegate
            {
                try
                {
                    made = new GachaEditor(s.Data, s.Mod);
                    caps = made.Capsules();
                    // the drop tables, read once for the odds
                    string why;
                    foreach (Gacha g in made.Gachas) if (!g.IsDeleted) { made.Odds(g, out why); break; }
                }
                catch (ModKitException ex) { failed = ex.Message; }
            }, delegate
            {
                if (failed != null) { sumLabel.Text = failed; rowList.Items.Clear(); return; }
                ed = made;
                ed.Others = delegate { return s.OtherMods(); };
                capsules = caps;
                byGacha = GachaCapsules.ByGacha(caps);
                read = true;
                if (wantKey != null)
                    foreach (Gacha g in ed.Gachas) if (ed.Key(g) == wantKey) { shown = g; break; }
                wantKey = null;
                ShowRows();
            });
        }

        GachaEditor Editor()
        {
            if (ed == null || !read) throw new ModKitException("the capsule table is being read: try again");
            if (host.SharedSession().Mod == null) throw new ModKitException("no mod is being edited");
            return ed;
        }

        string ItemName(long id)
        {
            string n;
            if (id <= 0) return "";
            if (names.TryGetValue(id, out n)) return n;
            n = "";
            if (host == null || host.Busy) return n;
            try
            {
                EditorSession s = host.SharedSession();
                if (items == null || items.Mod != s.Mod || items.Data != s.Data) items = s.Items();
                items.Look(id, delegate(EntityDraft e) { n = ((ItemDraft)e).Name ?? ""; });
            }
            catch (OutOfMemoryException) { return ""; }
            catch (ModKitException) { n = "(no such item)"; }
            catch (System.IO.IOException) { return ""; }
            names[id] = n;
            return n;
        }

        static long Long(string v)
        {
            long n;
            return long.TryParse((v ?? "").Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        List<GachaCapsule> Opening(Gacha g)
        {
            List<GachaCapsule> l;
            return byGacha.TryGetValue(ed.Id(g), out l) ? l : new List<GachaCapsule>();
        }

        string About(Gacha g)
        {
            if (g.IsDeleted) return "removed by the mod";
            string coin = ed.ServerGet(g, GachaRules.Coin) ?? "";
            string amount = GachaRules.Effective(GachaRules.Amount, ed.ServerGet(g, GachaRules.Amount), false);
            int n = Opening(g).Count;
            return "drop box " + (ed.ServerGet(g, GachaRules.DropBox) ?? "")
                + (Long(coin) > 0 ? ", coin " + coin + " x" + amount : ", no coin")
                + ", " + (n == 0 ? "no capsule item" : n == 1 ? "1 capsule item" : n + " capsule items");
        }

        string KeyText(Gacha g)
        {
            string k = (ed.Get(g, GachaRules.Key) ?? "").Trim();
            string f = ed.Get(g, "feature") ?? "", l = ed.Get(g, "locale") ?? "";
            if (f.Length > 0 || l.Length > 0) k += " (" + (f.Length == 0 ? l : l.Length == 0 ? f : f + ", " + l) + ")";
            return k;
        }

        void ShowRows()
        {
            if (ed == null || !read) return;
            filling = true;
            try
            {
                rowList.BeginUpdate();
                rowList.Items.Clear();
                List<Gacha> hits = ed.Find(findBox.Text);
                var order = new List<Gacha>();
                foreach (Gacha g in hits) if (g.IsChanged || g.IsAdded) order.Add(g);
                int changed = order.Count;
                foreach (Gacha g in hits) if (!(g.IsChanged || g.IsAdded)) order.Add(g);
                if (shown != null && !ed.Gachas.Contains(shown)) shown = null;
                int inMod = 0;
                foreach (Gacha g in ed.Gachas) if (g.IsChanged || g.IsAdded) inMod++;
                foreach (Gacha g in order)
                {
                    var it = new ListViewItem(KeyText(g)) { Tag = g };
                    it.SubItems.Add(About(g));
                    it.SubItems.Add(g.IsAdded ? "new" : g.IsDeleted ? "removed" : g.IsChanged ? "yes" : "");
                    if (g.IsAdded) it.ForeColor = Color.DarkGreen;
                    else if (g.IsDeleted) it.ForeColor = SystemColors.GrayText;
                    else if (g.IsChanged) it.ForeColor = Color.MediumBlue;
                    rowList.Items.Add(it);
                    if (g == shown) it.Selected = true;
                }
                sumLabel.Text = GachaRules.Entry + ": " + ed.Gachas.Count + " capsules on " + string.Join(", ", ed.Sides.ConvertAll(t => t.Archive).ToArray())
                    + ", " + inMod + " in the mod; " + capsules.Count + " capsule items"
                    + (findBox.Text.Trim().Length > 0 ? "; " + hits.Count + " found (" + changed + " in the mod)" : "");
                if (shown == null && order.Count > 0) { shown = order[0]; rowList.Items[0].Selected = true; }
            }
            finally { rowList.EndUpdate(); filling = false; }
            ShowRow();
        }

        // what a value means: the field's meaning, a coin's name, or what is wrong
        string Means(Gacha g, string field, string v)
        {
            long n;
            if (!GachaRules.IsClientOnly(field) && field != "feature" && field != "locale" && !GachaRules.ServerValue(field, v, out n))
                return "the server cannot read \"" + v + "\"";
            if (ed.Differs(g, field)) return "the sides differ: " + GachaRules.Means(field);
            if (field == GachaRules.Coin || field == "paymentIconTag")
            {
                long id = Long(v);
                if (id > 0) { string name = ItemName(id); if (name.Length > 0) return name; }
            }
            if (field == GachaRules.Amount && (v ?? "").Trim().Length == 0) return "empty: the client shows 1, the server gives 0 coins";
            return GachaRules.Means(field);
        }

        void ShowRow()
        {
            Gacha g = ed == null || !read ? null : shown;
            filling = true;
            try
            {
                fieldList.BeginUpdate();
                oddsList.BeginUpdate();
                capsuleList.BeginUpdate();
                int keep = fieldList.SelectedIndices.Count > 0 ? fieldList.SelectedIndices[0] : -1;
                fieldList.Items.Clear();
                oddsList.Items.Clear();
                capsuleList.Items.Clear();
                checkList.Items.Clear();
                if (g == null) { rowLabel.Text = "no capsule chosen"; infoLabel.Text = ""; return; }
                rowLabel.Text = "capsule " + KeyText(g) + (g.IsAdded ? " (new)" : g.IsDeleted ? " (removed by the mod)" : g.IsChanged ? " (in the mod)" : "");
                foreach (string f in GachaRules.Fields)
                {
                    string v = ed.Get(g, f) ?? "";
                    string was = g.IsAdded ? "" : ed.Shipped(g, f) ?? "";
                    var it = new ListViewItem(f);
                    it.SubItems.Add(v);
                    it.SubItems.Add(was);
                    it.SubItems.Add(Means(g, f, v));
                    it.ToolTipText = GachaRules.Means(f);
                    long n;
                    if (ed.Differs(g, f) || (!GachaRules.IsClientOnly(f) && f != "feature" && f != "locale" && !GachaRules.ServerValue(f, v, out n))) it.ForeColor = Color.DarkOrange;
                    else if (!g.IsAdded && v != was) it.ForeColor = Color.MediumBlue;
                    fieldList.Items.Add(it);
                }
                if (keep >= 0 && keep < fieldList.Items.Count) fieldList.Items[keep].Selected = true;

                List<GachaCapsule> open = Opening(g);
                foreach (GachaCapsule c in open)
                {
                    var it = new ListViewItem(c.Item.ToString(CultureInfo.InvariantCulture));
                    it.SubItems.Add(ItemName(c.Item));
                    it.SubItems.Add(c.Parameter ?? "");
                    it.SubItems.Add(c.Entry ?? "");
                    capsuleList.Items.Add(it);
                }
                infoLabel.Text = g.IsDeleted ? "" : open.Count == 0 ? "no capsule item opens it: players cannot get it" : "opened by " + open.Count + " capsule item(s)";

                if (!g.IsDeleted)
                {
                    string why;
                    List<GachaOddsGroup> odds = ed.Odds(g, out why);
                    if (odds == null) oddsList.Items.Add(new ListViewItem(new[] { "", "", why ?? "", "", "", "" }));
                    else
                        foreach (GachaOddsGroup grp in odds)
                        {
                            string note = "dropCount " + (grp.DropCount ?? "") + ", weight " + grp.TotalWeight
                                + (grp.JobWeights ? "; job weights apply: the chances shift per job" : "") + (grp.SmartGender ? "; by gender" : "");
                            var gi = new ListViewItem(new[] { grp.Group ?? "", "", note, "", "", "" }) { Font = new Font(oddsList.Font, FontStyle.Bold) };
                            oddsList.Items.Add(gi);
                            foreach (GachaOddsRow r in grp.Rows)
                            {
                                var it = new ListViewItem("");
                                it.SubItems.Add(r.Item ?? "");
                                it.SubItems.Add(ItemName(Long(r.Item)));
                                it.SubItems.Add(r.Min == r.Max ? r.Min.ToString(CultureInfo.InvariantCulture) : r.Min + "-" + r.Max);
                                it.SubItems.Add(r.Weight.ToString(CultureInfo.InvariantCulture));
                                it.SubItems.Add(r.Percent.ToString("0.###", CultureInfo.InvariantCulture) + " %");
                                if (r.Weight <= 0) it.ForeColor = SystemColors.GrayText;
                                oddsList.Items.Add(it);
                            }
                        }
                }

                foreach (string t in ed.Check(g)) checkList.Items.Add(t);
                if (!g.IsDeleted && open.Count == 0) checkList.Items.Add("no capsule item opens it (an item with OpenGachaBox \"" + ed.Id(g) + ",...\"): add one in the MS2 Items view or as an entry");
                foreach (GachaCapsule c in open) if (c.Problem != null) checkList.Items.Add("item " + c.Item + ": " + c.Problem);
                if (checkList.Items.Count == 0) checkList.Items.Add(g.IsDeleted ? "removed by the mod" : "no problem found in the capsule");
            }
            finally { fieldList.EndUpdate(); oddsList.EndUpdate(); capsuleList.EndUpdate(); filling = false; }
        }

        void Edited(string said)
        {
            host.EditedOutside();
            ShowRows();
            if (said != null) host.SayOutside(said);
        }

        Gacha Shown()
        {
            Editor();
            if (shown == null) throw new ModKitException("choose a capsule in the list");
            return shown;
        }

        static long Number(string text, string what)
        {
            long v;
            string t = text.Trim();
            if (!long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out v)) throw new ModKitException(t.Length == 0 ? "give " + what + " first" : "\"" + t + "\" is not " + what);
            return v;
        }

        void SetField()
        {
            Gacha g = Shown();
            if (fieldList.SelectedItems.Count == 0) throw new ModKitException("choose a field in the list");
            string f = fieldList.SelectedItems[0].Text, v = valueBox.Text.Trim();
            if (v == (ed.Get(g, f) ?? "") && !ed.Differs(g, f)) { host.SayOutside("capsule " + KeyText(g) + ": " + f + " as it was"); return; }
            ed.Set(g, f, v.Length == 0 ? null : v);
            Edited("capsule " + KeyText(g) + ": " + f + (v.Length == 0 ? " taken out" : " = " + v));
        }

        long NewId()
        {
            if (newIdBox.Text.Trim().Length > 0) return Number(newIdBox.Text, "an id");   // Add and Clone refuse a taken one
            List<long> free = Editor().NextIds(host.SharedSession().OtherMods(), 1, null);
            if (free.Count == 0) throw new ModKitException("no free gacha id is left in " + GachaRules.From + "-" + GachaRules.To);
            return free[0];
        }

        void NewRow(bool copy)
        {
            GachaEditor e = Editor();
            Gacha from = copy ? Shown() : null;
            long box = 0, coin = 0, amount = 0;
            if (!copy)
            {
                box = Number(boxBox.Text, "a drop box id");
                coin = coinBox.Text.Trim().Length == 0 ? 0 : Number(coinBox.Text, "a coin item id");
                amount = coin == 0 ? 0 : Number(amountBox.Text, "a coin amount");
            }
            long id = NewId();
            Gacha made = copy ? e.Clone(from, id) : e.Add(id, box, coin, amount);
            newIdBox.Text = "";
            findBox.Text = "";
            shown = made;
            Edited("capsule " + id + " added" + (copy ? " as a copy of " + KeyText(from) : "")
                + ". A capsule item opens it: an item with the function OpenGachaBox, parameter \"" + id + ",<its own id>\". Save mod, then Plan checks it.");
        }

        void RemoveOrRestore()
        {
            Gacha g = Shown();
            string what = "capsule " + KeyText(g);
            if (g.IsDeleted) { ed.Restore(g); Edited(what + " is back"); return; }
            bool added = g.IsAdded;
            ed.Remove(g);
            if (added) shown = null;
            Edited(added ? what + " was new in the mod and is gone again" : what + " is removed (Remove / restore brings it back); Plan names the capsule items that still open it");
        }

        void OpenBox()
        {
            Gacha g = Shown();
            long box;
            if (!GachaRules.ServerValue(GachaRules.DropBox, ed.ServerGet(g, GachaRules.DropBox), out box) || box <= 0)
                throw new ModKitException("capsule " + KeyText(g) + " names no drop box");
            IViewService views = Platform.GetService<IViewService>();
            GroupsView v = views == null ? null : views.CreateOrFindView(GroupsView.ViewId) as GroupsView;
            if (v == null) throw new ModKitException("the MS2 Shops and Drops view could not be opened");
            views.ShowView(v);
            v.OpenGroup(GroupKind.DropBox, box);
        }
    }
}
