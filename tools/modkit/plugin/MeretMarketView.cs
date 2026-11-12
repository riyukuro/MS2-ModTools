// The "MS2 Meret Market" view: the server's cash shop (the item rows of table/Server/shop_merat_custom.xml in the
// server's Server.m2d, MS2.ModKit.MeretMarket) as a mod edit.
//
// The list on the left shows each sale with the mod (an edited one in blue, a new one in green, one taken off sale or
// whose sale has ended in grey). On the right: the sale's attributes (field, value with the mod, shipped value, what
// the value means); choosing a field puts its value in the box, Set writes it. Under them the sale's bundles (more of
// the item for another price), added, set or removed. New adds a sale of an item under an id no sale or bundle uses,
// Copy adds a copy of the chosen one, Off sale takes a shipped sale out of the shop (a delete line), Take puts the
// shipped sale back (a new one: takes it out). Plan, Apply and Save mod are the MS2 Tables view's.
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
    public class MeretMarketView : AbstractView
    {
        public const string ViewId = "MS2.Tables.MeretMarketView";

        TablesView host;
        MeretMarket mm;            // for the session's data and mod
        bool read;                 // mm has read the table (on the worker thread)
        bool filling;
        int shown = -1;            // the id the right side shows
        ItemEditor items;
        readonly Dictionary<long, string> itemNames = new Dictionary<long, string>();

        readonly Label sumLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListView saleList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, ShowItemToolTips = true };
        readonly ListView fieldList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, ShowItemToolTips = true };
        readonly ListView bundleList = new ListView { Dock = DockStyle.Bottom, Height = 120, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        readonly Label saleLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 0, 0), Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
        readonly Label infoLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 0, 0) };
        readonly Label fieldLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 0, 0), Text = "(choose a field)" };
        readonly TextBox valueBox = new TextBox { Width = 260 };
        readonly TextBox bIdBox = new TextBox { Width = 70 };
        readonly TextBox bQtyBox = new TextBox { Width = 50, Text = "10" };
        readonly TextBox bBonusBox = new TextBox { Width = 50, Text = "0" };
        readonly TextBox bPriceBox = new TextBox { Width = 70 };
        readonly TextBox bSaleBox = new TextBox { Width = 70, Text = "0" };
        readonly TextBox bDaysBox = new TextBox { Width = 40, Text = "0" };
        readonly TextBox newIdBox = new TextBox { Width = 80 };
        readonly TextBox newItemBox = new TextBox { Width = 90 };
        readonly TextBox newTabBox = new TextBox { Width = 50 };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 110 };
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();

        static readonly Dictionary<string, string> FieldTips = new Dictionary<string, string>
        {
            { "id", "The sale's id (one id space with the bundles: an id used twice stops the server's ingest)" },
            { "tabID", "The shop tab the sale shows in (a tab id of table/na/meratmarketcategory.xml in Xml.m2d)" },
            { "banner", "The banner image name the shop shows on the sale (empty: none)" },
            { "bannerTag", "0 none, 1 pink gift, 2 blue gift" },
            { "itemID", "The item a purchase gives (an item the server has: else the game server leaves the sale out)" },
            { "grade", "The item's grade (rarity): 1 normal .. 6" },
            { "quantity", "How many of the item a purchase gives" },
            { "bonusQuantity", "How many more of the item a purchase gives (shown as a bonus)" },
            { "durationDay", "Above 0: the item expires that many days after the purchase" },
            { "saleTag", "The tag the shop shows: 0 none, 1 new, 2 hot, 3 event, 4 sale, 5 special" },
            { "paymentType", "0 meso, 1 meret, 2 red meret (any other: nobody can pay)" },
            { "price", "The price, in the payment type's money" },
            { "salePrice", "The price the purchase takes when above 0 (the shop shows the price struck out)" },
            { "saleStartTime", MeretMarket.TimeFormat + "; empty: no start. Before it the server refuses the purchase" },
            { "saleEndTime", MeretMarket.TimeFormat + "; empty: no end. After it the server refuses the purchase" },
            { "jobRequire", "The job codes that may buy, a comma list: 1 beginner, 10 knight, 20 berserker, 30 wizard, 40 priest, 50 archer, 60 heavy gunner, 70 thief, 80 assassin, 90 rune blade, 100 striker, 110 soul binder, 999 GM (empty: nobody)" },
            { "noRestock", "true/false (1/0), read by the server's ingest" },
            { "minLevel", "Above 0: the lowest level that may buy" },
            { "maxLevel", "Above 0: the highest level that may buy" },
            { "achieveID", "Above 0: a player who has this trophy may not buy" },
            { "achieveGrade", "The grade of achieveID's trophy" },
            { "pcCafe", "true/false (1/0): a PC cafe sale" },
            { "giftable", "true/false (1/0): the sale can be bought as a gift" },
            { "showSaleTime", "true/false (1/0): the shop shows the sale's end" },
            { "promoName", "The promotion's name the shop shows (empty: none)" },
            { "promoSaleStartTime", MeretMarket.TimeFormat + ": the promotion's start (empty: none)" },
            { "promoSaleEndTime", MeretMarket.TimeFormat + ": the promotion's end (empty: none)" },
        };

        public MeretMarketView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(Btn("Read", "Reads the server's shop table again", delegate { Reload(); }));
            top.Controls.Add(sumLabel);

            FlowLayoutPanel newBar = Bar();
            newBar.Controls.Add(Lbl("New id"));
            newBar.Controls.Add(newIdBox);
            tips.SetToolTip(newIdBox, "An id no sale or bundle uses (they are one id space); empty: the next free one above the chosen sale");
            newBar.Controls.Add(Lbl("item"));
            newBar.Controls.Add(newItemBox);
            tips.SetToolTip(newItemBox, "The item id the new sale gives (an item the server has)");
            newBar.Controls.Add(Lbl("tab"));
            newBar.Controls.Add(newTabBox);
            tips.SetToolTip(newTabBox, "The shop tab of the new sale (a tab id of meratmarketcategory.xml); empty: the chosen sale's tab");
            newBar.Controls.Add(Btn("New", "Adds a sale of one of the item, grade 1, for 0 meret, for every job but GM, with no time limit: set its price after", delegate { NewSale(false); }));
            newBar.Controls.Add(Btn("Copy", "Adds a copy of the chosen sale (its attributes with the mod, without its bundles) under the new id", delegate { NewSale(true); }));
            newBar.Controls.Add(Btn("Off sale", "Takes the chosen shipped sale out of the shop (a delete line in the mod); a new sale is taken out of the mod", delegate { OffSale(); }));
            newBar.Controls.Add(Btn("Take sale", "Puts the chosen shipped sale back as shipped (its edits, bundles and delete line go); a new sale is taken out of the mod", delegate { TakeSale(); }));
            newBar.Controls.Add(Btn("Take all", "Puts every sale back as shipped and takes the new ones out", delegate { TakeAll(); }));

            FlowLayoutPanel modBar = Bar();
            modBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change, with the checks of the Meret Market sales; writes nothing", delegate { return host.DoPlan(); }));
            modBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change (the MS2 Tables view's Apply)", delegate { return host.DoApply(); }));
            modBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            saleList.Columns.Add("id", 70, HorizontalAlignment.Right);
            saleList.Columns.Add("item", 160);
            saleList.Columns.Add("tab", 40, HorizontalAlignment.Right);
            saleList.Columns.Add("price", 60, HorizontalAlignment.Right);
            saleList.Columns.Add("sale", 60, HorizontalAlignment.Right);
            saleList.Columns.Add("pay", 60);
            saleList.Columns.Add("ends", 130);
            saleList.Columns.Add("bundles", 50, HorizontalAlignment.Right);
            saleList.Columns.Add("in mod", 55);

            fieldList.Columns.Add("field", 120);
            fieldList.Columns.Add("value", 160);
            fieldList.Columns.Add("shipped", 120);
            fieldList.Columns.Add("means", 220);

            bundleList.Columns.Add("bundle", 70, HorizontalAlignment.Right);
            bundleList.Columns.Add("quantity", 60, HorizontalAlignment.Right);
            bundleList.Columns.Add("bonus", 50, HorizontalAlignment.Right);
            bundleList.Columns.Add("price", 70, HorizontalAlignment.Right);
            bundleList.Columns.Add("sale", 70, HorizontalAlignment.Right);
            bundleList.Columns.Add("days", 45, HorizontalAlignment.Right);
            bundleList.Columns.Add("in mod", 55);

            var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(2) };
            head.Controls.Add(saleLabel);
            head.Controls.Add(infoLabel);

            FlowLayoutPanel setBar = Bar();
            setBar.Dock = DockStyle.Bottom;
            setBar.Controls.Add(fieldLabel);
            setBar.Controls.Add(valueBox);
            tips.SetToolTip(valueBox, "The chosen field's value (the shipped value takes the line out of the mod)");
            setBar.Controls.Add(Btn("Set", "Writes the value of the chosen field into the mod", delegate { SetField(); }));
            setBar.Controls.Add(Btn("Shipped", "Puts the chosen field back to its shipped value", delegate { SetShipped(); }));

            FlowLayoutPanel bundleBar = Bar();
            bundleBar.Dock = DockStyle.Bottom;
            bundleBar.Controls.Add(Lbl("Bundle"));
            bundleBar.Controls.Add(bIdBox);
            tips.SetToolTip(bIdBox, "The bundle's id (one id space with the sales); empty for Add: the next free one");
            bundleBar.Controls.Add(Lbl("qty"));
            bundleBar.Controls.Add(bQtyBox);
            bundleBar.Controls.Add(Lbl("bonus"));
            bundleBar.Controls.Add(bBonusBox);
            bundleBar.Controls.Add(Lbl("price"));
            bundleBar.Controls.Add(bPriceBox);
            bundleBar.Controls.Add(Lbl("sale"));
            bundleBar.Controls.Add(bSaleBox);
            tips.SetToolTip(bSaleBox, "The price the purchase takes when above 0");
            bundleBar.Controls.Add(Lbl("days"));
            bundleBar.Controls.Add(bDaysBox);
            tips.SetToolTip(bDaysBox, "Above 0: the item expires that many days after the purchase");
            bundleBar.Controls.Add(Btn("Add bundle", "Adds a bundle of the sale's item: the quantity and bonus for the price (it takes the sale's item, tab, payment, jobs and times)", delegate { EditBundle('a'); }));
            bundleBar.Controls.Add(Btn("Set bundle", "Sets the chosen bundle's quantity, bonus, price, sale price and days", delegate { EditBundle('s'); }));
            bundleBar.Controls.Add(Btn("Remove bundle", "Removes the chosen bundle (a shipped one: a delete line in the mod)", delegate { EditBundle('r'); }));

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(fieldList);
            right.Controls.Add(setBar);
            right.Controls.Add(bundleList);
            right.Controls.Add(bundleBar);
            right.Controls.Add(head);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
            split.Panel1.Controls.Add(saleList);
            split.Panel2.Controls.Add(right);

            Controls.Add(split);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(outputBox);
            Controls.Add(modBar);
            Controls.Add(newBar);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, newBar, modBar, split });
            ViewLayout.KeepLogLines(outputBox, this);
            ViewLayout.CentreOnFirstShow(this);
            bool sized = false;
            split.SizeChanged += delegate
            {
                if (sized || split.Width < 200) return;
                sized = true;
                split.SplitterDistance = Math.Max(100, split.Width * 50 / 100);
            };

            saleList.SelectedIndexChanged += delegate
            {
                if (filling || saleList.SelectedItems.Count == 0) return;
                shown = (int)saleList.SelectedItems[0].Tag;
                ShowSale();
            };
            fieldList.SelectedIndexChanged += delegate
            {
                if (filling || fieldList.SelectedItems.Count == 0) return;
                ListViewItem it = fieldList.SelectedItems[0];
                fieldLabel.Text = it.Text;
                valueBox.Text = it.SubItems[1].Text;
            };
            bundleList.SelectedIndexChanged += delegate
            {
                if (filling || bundleList.SelectedItems.Count == 0) return;
                ListViewItem it = bundleList.SelectedItems[0];
                TextBox[] boxes = { bIdBox, bQtyBox, bBonusBox, bPriceBox, bSaleBox, bDaysBox };
                for (int i = 0; i < boxes.Length; i++) boxes[i].Text = it.SubItems[i].Text;
            };
            VisibleChanged += delegate { if (Visible && !read && TablesView.Open != null) Guard(delegate { ShowTable(); }); };
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
                Logger.Warning("MS2 Meret Market: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        void OnHostSaid(string text) { OutputWrap.Set(outputBox, text); }

        void OnHostBusy(bool on) { ViewLayout.Lock(this, lockable, on, outputBox); }

        /// <summary>Another session or mod (or an edit of another view): show the sales again.</summary>
        void OnHostChanged()
        {
            EditorSession s = host.SharedSession();
            if (mm != null && (mm.Data != s.Data || mm.Mod != s.Mod)) { mm = null; read = false; }
            itemNames.Clear();
            if (Visible) { if (read) ShowSales(); else if (!host.Busy) ShowTable(); }
        }

        // ---- the sales ----

        void Reload()
        {
            mm = null;
            read = false;
            itemNames.Clear();
            ShowTable();
        }

        /// <summary>Reads the server's shop table and the names of its items on the worker thread (once per session and mod), then shows it.</summary>
        void ShowTable()
        {
            EditorSession s = host.SharedSession();
            if (s.Mod == null) { sumLabel.Text = "no mod is being edited"; return; }
            if (mm == null || mm.Data != s.Data || mm.Mod != s.Mod) { mm = new MeretMarket(s.Data, s.Mod); read = false; }
            if (read) { ShowSales(); return; }
            MeretMarket reading = mm;
            var names = new Dictionary<long, string>();
            host.RunOutside("shop table", delegate
            {
                foreach (MeretItem it in reading.Items())
                {
                    long id;
                    if (long.TryParse(reading.Get(it, "itemID") ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out id) && !names.ContainsKey(id)) names[id] = ReadName(s, id);
                }
            }, delegate
            {
                if (reading != mm) return;
                read = true;
                foreach (KeyValuePair<long, string> kv in names) itemNames[kv.Key] = kv.Value;
                ShowSales();
            });
        }

        string ReadName(EditorSession s, long id)
        {
            try
            {
                if (items == null || items.Mod != s.Mod || items.Data != s.Data) items = s.Items();
                return items.Open(id).Name ?? "";
            }
            catch (ModKitException) { return "(no such item)"; }
            catch (System.IO.IOException) { return ""; }
        }

        string ItemName(string itemId)
        {
            long id;
            if (!long.TryParse((itemId ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id)) return "";
            string n;
            if (!itemNames.TryGetValue(id, out n)) itemNames[id] = n = ReadName(host.SharedSession(), id);
            return n;
        }

        static bool Ended(string end)
        {
            DateTime? t = MeretMarket.ParseTime(end);
            return t != null && t.Value < DateTime.Now;
        }

        static string Pay(string v)
        {
            int n;
            return int.TryParse((v ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n) && n < MeretMarket.Payments.Length ? MeretMarket.Payments[n] : "?" + v;
        }

        void ShowSales()
        {
            if (mm == null || !read) return;
            if (!mm.Has) { sumLabel.Text = "the server data has no " + MeretMarket.Entry + " in Server.m2d"; saleList.Items.Clear(); return; }
            List<MeretItem> all = mm.Items();
            int live = 0, added = 0;
            filling = true;
            try
            {
                saleList.BeginUpdate();
                saleList.Items.Clear();
                var seen = new HashSet<int>();
                foreach (MeretItem sale in all)
                {
                    if (!seen.Add(sale.Id)) continue;       // a second row of an id: edit by hand (the tip says so)
                    string itemId = mm.Get(sale, "itemID"), end = mm.Get(sale, "saleEndTime");
                    var it = new ListViewItem(sale.Id.ToString(CultureInfo.InvariantCulture)) { Tag = sale.Id };
                    it.SubItems.Add(itemId + " " + ItemName(itemId));
                    it.SubItems.Add(mm.Get(sale, "tabID") ?? "");
                    it.SubItems.Add(mm.Get(sale, "price") ?? "");
                    it.SubItems.Add(mm.Get(sale, "salePrice") ?? "");
                    it.SubItems.Add(Pay(mm.Get(sale, "paymentType")));
                    it.SubItems.Add(end ?? "");
                    bool removed = mm.IsRemoved(sale);
                    it.SubItems.Add(removed ? "" : mm.Bundles(sale).Count.ToString(CultureInfo.InvariantCulture));
                    it.SubItems.Add(sale.IsAdded ? "new" : removed ? "off sale" : mm.IsEdited(sale) ? "yes" : "");
                    var tip = new List<string>();
                    bool ended = Ended(end);
                    if (ended) tip.Add("the sale has ended: the server refuses the purchase");
                    else if (!removed) live++;
                    if (removed) tip.Add("taken off sale by the mod");
                    if (sale.IsAdded) added++;
                    if (sale.Rows > 1) tip.Add("id " + sale.Id + " is used by " + sale.Rows + " rows (sales and bundles): the server's ingest stops; edit them by hand");
                    if (sale.IsAdded) it.ForeColor = Color.DarkGreen;
                    else if (removed) it.ForeColor = SystemColors.GrayText;
                    else if (mm.IsEdited(sale)) it.ForeColor = Color.MediumBlue;
                    else if (ended) it.ForeColor = SystemColors.GrayText;
                    it.ToolTipText = string.Join("\n", tip.ToArray());
                    saleList.Items.Add(it);
                    if (sale.Id == shown && !it.Selected) it.Selected = true;
                }
            }
            finally { saleList.EndUpdate(); filling = false; }
            sumLabel.Text = MeretMarket.Entry + ": " + all.Count + " sales, " + live + " on sale, " + mm.EditedCount() + " in the mod (" + added + " new)";
            if (shown < 0 && all.Count > 0) shown = all[0].Id;
            ShowSale();
        }

        MeretItem Shown()
        {
            if (mm == null || !read) throw new ModKitException("the shop table is being read: try again");
            if (host.SharedSession().Mod == null) throw new ModKitException("no mod is being edited");
            MeretItem sale = shown < 0 ? null : mm.Find(shown);
            if (sale == null) throw new ModKitException("choose a sale in the list");
            return sale;
        }

        // what a field's value means, in words
        string Means(string field, string v)
        {
            int n;
            bool num = int.TryParse((v ?? "").Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n);
            string bad = MeretMarket.FieldProblem(field, v);
            if (bad != null) return bad;
            switch (field)
            {
                case "itemID": return ItemName(v);
                case "paymentType": return Pay(v);
                case "saleTag": return num && n >= 0 && n < MeretMarket.SaleTags.Length ? MeretMarket.SaleTags[n] : "";
                case "bannerTag": return num && n >= 0 && n < MeretMarket.BannerTags.Length ? MeretMarket.BannerTags[n] : "";
                case "jobRequire": return MeretMarket.JobsText(v);
                case "saleStartTime": return (v ?? "").Length == 0 ? "no start" : "";
                case "saleEndTime": return (v ?? "").Length == 0 ? "no end" : Ended(v) ? "ended" : "";
                case "salePrice": return num && n == 0 ? "the price" : "";
                case "durationDay": return num && n > 0 ? "expires " + n + " days after purchase" : "does not expire";
                case "minLevel":
                case "maxLevel":
                case "achieveID": return num && n == 0 ? "none" : "";
            }
            return "";
        }

        void ShowSale()
        {
            MeretItem sale = mm == null || !read || shown < 0 ? null : mm.Find(shown);
            filling = true;
            try
            {
                fieldList.BeginUpdate();
                bundleList.BeginUpdate();
                int keep = fieldList.SelectedIndices.Count > 0 ? fieldList.SelectedIndices[0] : -1;
                fieldList.Items.Clear();
                bundleList.Items.Clear();
                if (sale == null)
                {
                    saleLabel.Text = "no sale chosen";
                    infoLabel.Text = "";
                    return;
                }
                bool removed = mm.IsRemoved(sale);
                saleLabel.Text = "Sale " + sale.Id + " " + ItemName(mm.Get(sale, "itemID")) + (sale.IsAdded ? " (new)" : removed ? " (off sale in the mod)" : mm.IsEdited(sale) ? " (in the mod)" : "");
                var info = new List<string>();
                if (Ended(mm.Get(sale, "saleEndTime"))) info.Add("the sale has ended");
                if (sale.Rows > 1) info.Add("id used by " + sale.Rows + " rows: edit them by hand");
                List<MeretBundle> bundles = removed ? new List<MeretBundle>() : mm.Bundles(sale);
                infoLabel.Text = string.Join("; ", info.ToArray());
                foreach (string f in MeretMarket.Fields)
                {
                    string v = mm.Get(sale, f), was = sale.IsAdded ? null : sale.ShippedAttr(f);
                    var it = new ListViewItem(f);
                    it.SubItems.Add(v ?? "");
                    it.SubItems.Add(sale.IsAdded ? "(new)" : was ?? "(none)");
                    it.SubItems.Add(Means(f, v));
                    string tip;
                    if (FieldTips.TryGetValue(f, out tip)) it.ToolTipText = tip;
                    if (MeretMarket.FieldProblem(f, v) != null) it.ForeColor = Color.DarkOrange;
                    else if (!sale.IsAdded && v != was) it.ForeColor = Color.MediumBlue;
                    fieldList.Items.Add(it);
                }
                if (keep >= 0 && keep < fieldList.Items.Count) fieldList.Items[keep].Selected = true;
                foreach (MeretBundle b in bundles)
                {
                    var it = new ListViewItem(b.Id.ToString(CultureInfo.InvariantCulture));
                    bool edited = false;
                    foreach (string f in new[] { "quantity", "bonusQuantity", "price", "salePrice", "durationDay" })
                    {
                        string v = mm.BundleGet(sale, b, f);
                        if (b.IsShipped && v != b.Attr(f)) edited = true;
                        it.SubItems.Add(v ?? "");
                    }
                    it.SubItems.Add(!b.IsShipped ? "new" : edited ? "yes" : "");
                    if (!b.IsShipped) it.ForeColor = Color.DarkGreen;
                    else if (edited) it.ForeColor = Color.MediumBlue;
                    bundleList.Items.Add(it);
                }
            }
            finally { fieldList.EndUpdate(); bundleList.EndUpdate(); filling = false; }
        }

        void Edited(string said)
        {
            host.EditedOutside();
            ShowSales();
            host.SayOutside(said);
        }

        string ChosenField()
        {
            if (fieldList.SelectedItems.Count == 0) throw new ModKitException("choose a field in the list");
            return fieldList.SelectedItems[0].Text;
        }

        void SetField()
        {
            MeretItem sale = Shown();
            string f = ChosenField();
            bool changed = mm.SetField(sale, f, valueBox.Text);
            if (f == "id") { int id; if (int.TryParse(valueBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id)) shown = id; }
            MeretItem now = mm.Find(shown);
            Edited("sale " + shown + ": " + f + (changed ? " = " + valueBox.Text.Trim() : " as it was") + (now != null && !mm.IsEdited(now) ? " (as shipped: no line in the mod)" : ""));
        }

        void SetShipped()
        {
            MeretItem sale = Shown();
            if (sale.IsAdded) throw new ModKitException("sale " + sale.Id + " is new: it has no shipped value");
            string f = ChosenField();
            bool changed = mm.SetField(sale, f, sale.ShippedAttr(f) ?? "");
            Edited("sale " + sale.Id + ": " + f + (changed ? " as shipped again" : " was as shipped"));
        }

        void EditBundle(char what)
        {
            MeretItem sale = Shown();
            string t = bIdBox.Text.Trim();
            int id = 0;
            if (t.Length > 0 && !int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out id)) throw new ModKitException("the bundle id \"" + t + "\" is not a whole number");
            switch (what)
            {
                case 'a':
                    if (t.Length == 0) id = mm.FreeId(sale.Id);
                    mm.AddBundle(sale, id, bQtyBox.Text, bBonusBox.Text, bPriceBox.Text, bSaleBox.Text);
                    if (bDaysBox.Text.Trim().Length > 0 && bDaysBox.Text.Trim() != "0") mm.SetBundle(mm.Find(sale.Id), id, "durationDay", bDaysBox.Text);
                    Edited("sale " + sale.Id + ": bundle " + id + " added");
                    return;
                case 's':
                    if (t.Length == 0) throw new ModKitException("choose a bundle");
                    var changed = new List<string>();
                    string[] fields = { "quantity", "bonusQuantity", "price", "salePrice", "durationDay" };
                    TextBox[] boxes = { bQtyBox, bBonusBox, bPriceBox, bSaleBox, bDaysBox };
                    for (int i = 0; i < fields.Length; i++)
                    {
                        string v = boxes[i].Text.Trim();
                        if (v.Length == 0) v = "0";
                        if (mm.SetBundle(mm.Find(sale.Id), id, fields[i], v)) changed.Add(fields[i]);
                    }
                    Edited("sale " + sale.Id + " bundle " + id + ": " + (changed.Count == 0 ? "as it was" : string.Join(", ", changed.ToArray()) + " set"));
                    return;
                case 'r':
                    if (t.Length == 0) throw new ModKitException("choose a bundle");
                    mm.RemoveBundle(sale, id);
                    Edited("sale " + sale.Id + ": bundle " + id + " removed");
                    return;
            }
        }

        void NewSale(bool copy)
        {
            if (mm == null || !read) throw new ModKitException("the shop table is being read: try again");
            if (host.SharedSession().Mod == null) throw new ModKitException("no mod is being edited");
            MeretItem from = copy || shown >= 0 ? (copy ? Shown() : mm.Find(shown)) : null;
            string t = newIdBox.Text.Trim();
            int id;
            if (t.Length == 0) id = mm.FreeId(from == null ? 1 : from.Id);
            else if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out id)) throw new ModKitException("the new id \"" + t + "\" is not a whole number");
            MeretItem sale;
            if (copy) sale = mm.Copy(from.Id, id);
            else
            {
                string tab = newTabBox.Text.Trim();
                if (tab.Length == 0) tab = from == null ? "" : mm.Get(from, "tabID") ?? "";
                sale = mm.New(id, newItemBox.Text, tab);
            }
            shown = sale.Id;
            newIdBox.Text = "";
            Edited("sale " + sale.Id + " added" + (copy ? " as a copy of " + from.Id : ": set its price, payment and the rest"));
        }

        void OffSale()
        {
            MeretItem sale = Shown();
            mm.Remove(sale);
            if (sale.IsAdded) shown = -1;
            Edited("sale " + sale.Id + ": " + (sale.IsAdded ? "taken out of the mod" : "off sale (a delete line in the mod)"));
        }

        void TakeSale()
        {
            MeretItem sale = Shown();
            bool had = mm.Take(sale);
            if (sale.IsAdded) shown = -1;
            Edited("sale " + sale.Id + ": " + (!had ? "the mod does not change it" : sale.IsAdded ? "taken out of the mod" : "as shipped again"));
        }

        void TakeAll()
        {
            if (mm == null || !read) throw new ModKitException("the shop table is being read: try again");
            int n = mm.TakeAll();
            if (shown >= 0 && mm.Find(shown) == null) shown = -1;
            Edited("every sale as shipped again (" + n + " taken out of the mod)");
        }
    }
}
