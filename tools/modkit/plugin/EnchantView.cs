// The "MS2 Enchant" view: the stat bonus an item gets per enchant level (table/Server/enchantOptionTable.xml,
// MS2.ModKit.EnchantEditor) as a mod edit. The server alone reads the table: a row gives, for items of one slot, rank
// and level band, the rate one enchant level adds to the item's stats; the bonus at +N is the rates of +1 to +N added up.
//
// The list on the left shows the ladders (a slot, rank and level band with its rows of +1 to +15) with the mod (an
// edited one in blue, a new one in green, one with every row removed in grey), filtered by slot, rank and an item
// level. Several can be chosen: Scale, Set and Stats work on all the chosen ladders. On the right: the rows of the
// first chosen ladder per enchant level (rate with the mod, the shipped rate, the bonus added up to that level, the
// stats and the row id), and its checks. Choosing a row puts its rate in the box, Set rate writes it. Add grade adds a
// row for a level the ladder lacks, Copy ladder copies it for another slot, rank or level band with new ids, Remove /
// restore takes a row or the ladder out or back, Revert drops the mod's changes to the ladder. Plan, Apply, Revert and
// Save mod are the MS2 Tables view's.
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
    public class EnchantView : AbstractView
    {
        public const string ViewId = "MS2.Tables.EnchantView";

        TablesView host;
        EnchantEditor ed;           // for the session's data and mod
        bool read;                  // ed is read (on the worker thread)
        bool filling;
        EnchantLadder shown;
        string wantKey;             // the ladder to show again once the table is read again

        readonly ComboBox slotBox = new ComboBox { Width = 170, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly TextBox rankBox = new TextBox { Width = 30 };
        readonly TextBox levelBox = new TextBox { Width = 40 };
        readonly CheckBox modOnly = new CheckBox { Text = "in the mod", AutoSize = true, Margin = new Padding(8, 6, 0, 0) };
        readonly Label sumLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListView ladderList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false, ShowItemToolTips = true };
        readonly ListView gradeList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, ShowItemToolTips = true };
        readonly ListBox checkList = new ListBox { Dock = DockStyle.Bottom, Height = 90, HorizontalScrollbar = true, IntegralHeight = false };
        readonly Label rowLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 0, 0), Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
        readonly Label infoLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 0, 0) };
        readonly Label gradeLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 0, 0), Text = "(choose a level)" };
        readonly TextBox rateBox = new TextBox { Width = 60 };
        readonly TextBox addGradeBox = new TextBox { Width = 30 };
        readonly TextBox addRateBox = new TextBox { Width = 50 };
        readonly TextBox factorBox = new TextBox { Width = 45, Text = "1.5" };
        readonly ComboBox fieldBox = new ComboBox { Width = 70, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly TextBox fieldValueBox = new TextBox { Width = 50 };
        readonly TextBox statsBox = new TextBox { Width = 70 };
        readonly TextBox copySlotBox = new TextBox { Width = 35 };
        readonly TextBox copyRankBox = new TextBox { Width = 25 };
        readonly TextBox copyMinBox = new TextBox { Width = 35 };
        readonly TextBox copyMaxBox = new TextBox { Width = 35 };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 110 };
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();

        public EnchantView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;

            slotBox.Items.Add(new SlotChoice(0));
            foreach (int s in EnchantRules.AllSlots()) slotBox.Items.Add(new SlotChoice(s));
            slotBox.SelectedIndex = 0;
            foreach (string f in new[] { EnchantRules.MinLv, EnchantRules.MaxLv, EnchantRules.Slot, EnchantRules.Rank }) fieldBox.Items.Add(f);
            fieldBox.SelectedIndex = 1;

            FlowLayoutPanel top = Bar();
            top.Controls.Add(Btn("Read", "Reads the enchant table again", delegate { Reload(); }));
            top.Controls.Add(Lbl("Slot"));
            top.Controls.Add(slotBox);
            tips.SetToolTip(slotBox, "The item slot (the item type the server looks the bonus up by)");
            top.Controls.Add(Lbl("Rank"));
            top.Controls.Add(rankBox);
            tips.SetToolTip(rankBox, "The item rank (1 normal to 6); empty: any");
            top.Controls.Add(Lbl("Item level"));
            top.Controls.Add(levelBox);
            tips.SetToolTip(levelBox, "Only the ladders whose level band holds this item level; empty: any");
            top.Controls.Add(modOnly);
            tips.SetToolTip(modOnly, "Only the ladders the mod changes or adds");
            top.Controls.Add(sumLabel);

            FlowLayoutPanel ladderBar = Bar();
            ladderBar.Controls.Add(Lbl("x"));
            ladderBar.Controls.Add(factorBox);
            ladderBar.Controls.Add(Btn("Scale", "Multiplies every rate of the chosen ladders by the factor (to 0.001): 1.5 gives half again as much bonus", delegate { Scale(); }));
            ladderBar.Controls.Add(fieldBox);
            tips.SetToolTip(fieldBox, "The field of the chosen ladders' rows to set: maxLv 80 moves a band up for a raised level cap");
            ladderBar.Controls.Add(fieldValueBox);
            ladderBar.Controls.Add(Btn("Set", "Sets the field on every row of the chosen ladders", delegate { SetLadderField(); }));
            ladderBar.Controls.Add(Lbl("Stats"));
            ladderBar.Controls.Add(statsBox);
            tips.SetToolTip(statsBox, "The stat codes the rate applies to, comma separated (the option field), e.g. 27, 28");
            ladderBar.Controls.Add(Btn("Set stats", "Sets the stats on every row of the chosen ladders", delegate { SetStats(); }));
            ladderBar.Controls.Add(Btn("Remove / restore", "Removes every row of the chosen ladders (shipped rows: delete lines in the mod), or brings the removed rows back", delegate { RemoveOrRestoreLadders(); }));
            ladderBar.Controls.Add(Btn("Revert", "Drops every change of the mod to the chosen ladders and removes their new rows", delegate { RevertLadders(); }));

            FlowLayoutPanel copyBar = Bar();
            copyBar.Controls.Add(Lbl("Copy for slot"));
            copyBar.Controls.Add(copySlotBox);
            copyBar.Controls.Add(Lbl("rank"));
            copyBar.Controls.Add(copyRankBox);
            copyBar.Controls.Add(Lbl("levels"));
            copyBar.Controls.Add(copyMinBox);
            copyBar.Controls.Add(Lbl("-"));
            copyBar.Controls.Add(copyMaxBox);
            tips.SetToolTip(copySlotBox, "The copy's slot; empty: the ladder's own");
            tips.SetToolTip(copyRankBox, "The copy's rank; empty: the ladder's own");
            tips.SetToolTip(copyMinBox, "The copy's lowest item level; empty: the ladder's own");
            tips.SetToolTip(copyMaxBox, "The copy's highest item level; empty: the ladder's own");
            copyBar.Controls.Add(Btn("Copy ladder", "Adds a copy of the shown ladder (its rows as the mod leaves them) for the slot, rank and levels, with new row ids (" + EnchantRules.From + "-" + EnchantRules.To + ")", delegate { CopyLadder(); }));

            FlowLayoutPanel modBar = Bar();
            modBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change, with the checks of the enchant rows; writes nothing", delegate { return host.DoPlan(); }));
            modBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change (the MS2 Tables view's Apply)", delegate { return host.DoApply(); }));
            modBar.Controls.Add(Job("Revert", "Removes what Apply wrote, on every target", delegate { return host.DoRevert(); }));
            modBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            ladderList.Columns.Add("slot", 150);
            ladderList.Columns.Add("rank", 40, HorizontalAlignment.Right);
            ladderList.Columns.Add("levels", 60);
            ladderList.Columns.Add("at +15", 60, HorizontalAlignment.Right);
            ladderList.Columns.Add("stats", 120);
            ladderList.Columns.Add("in mod", 60);

            gradeList.Columns.Add("level", 45, HorizontalAlignment.Right);
            gradeList.Columns.Add("rate", 60, HorizontalAlignment.Right);
            gradeList.Columns.Add("shipped", 60, HorizontalAlignment.Right);
            gradeList.Columns.Add("bonus", 65, HorizontalAlignment.Right);
            gradeList.Columns.Add("stats", 150);
            gradeList.Columns.Add("row id", 70, HorizontalAlignment.Right);
            gradeList.Columns.Add("in mod", 60);

            var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(2) };
            head.Controls.Add(rowLabel);
            head.Controls.Add(infoLabel);

            FlowLayoutPanel setBar = Bar();
            setBar.Dock = DockStyle.Bottom;
            setBar.Controls.Add(gradeLabel);
            setBar.Controls.Add(rateBox);
            tips.SetToolTip(rateBox, "The rate of the chosen level: 0.05 adds 5 % of the item's stats at that level");
            setBar.Controls.Add(Btn("Set rate", "Writes the rate of the chosen level into the mod", delegate { SetRate(); }));
            setBar.Controls.Add(Btn("Remove / restore row", "Removes the chosen level's row (a shipped one: a delete line in the mod), or brings a removed one back", delegate { RemoveOrRestoreRow(); }));
            setBar.Controls.Add(Lbl("Add +"));
            setBar.Controls.Add(addGradeBox);
            tips.SetToolTip(addGradeBox, "A level (1-" + EnchantRules.MaxGrade + ") the ladder has no row for");
            setBar.Controls.Add(Lbl("rate"));
            setBar.Controls.Add(addRateBox);
            setBar.Controls.Add(Btn("Add grade", "Adds a row for the level with the rate and the ladder's stats, under the next free row id", delegate { AddGrade(); }));

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(gradeList);
            right.Controls.Add(setBar);
            right.Controls.Add(checkList);
            right.Controls.Add(head);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
            split.Panel1.Controls.Add(ladderList);
            split.Panel2.Controls.Add(right);

            Controls.Add(split);
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(outputBox);
            Controls.Add(modBar);
            Controls.Add(copyBar);
            Controls.Add(ladderBar);
            Controls.Add(top);
            lockable.AddRange(new Control[] { top, ladderBar, copyBar, modBar, split });
            ViewLayout.KeepLogLines(outputBox, this);
            ViewLayout.CentreOnFirstShow(this);
            bool sized = false;
            split.SizeChanged += delegate
            {
                if (sized || split.Width < 200) return;
                sized = true;
                split.SplitterDistance = Math.Max(100, split.Width * 45 / 100);
            };

            EventHandler refilter = delegate { if (!filling && read) Guard(delegate { ShowLadders(); }); };
            slotBox.SelectedIndexChanged += refilter;
            rankBox.TextChanged += refilter;
            levelBox.TextChanged += refilter;
            modOnly.CheckedChanged += refilter;
            ladderList.SelectedIndexChanged += delegate
            {
                if (filling || ladderList.SelectedItems.Count == 0) return;
                shown = (EnchantLadder)ladderList.SelectedItems[0].Tag;
                Guard(delegate { ShowLadder(); });
            };
            gradeList.SelectedIndexChanged += delegate
            {
                if (filling || gradeList.SelectedItems.Count == 0) return;
                ListViewItem it = gradeList.SelectedItems[0];
                gradeLabel.Text = "+" + it.Text;
                rateBox.Text = it.SubItems[1].Text;
                addGradeBox.Text = it.Tag == null ? it.Text : "";
            };
            VisibleChanged += delegate { if (Visible && !read && TablesView.Open != null) Guard(delegate { ReadTable(); }); };
        }

        sealed class SlotChoice
        {
            public readonly long Slot;
            public SlotChoice(long slot) { Slot = slot; }
            public override string ToString() { return Slot == 0 ? "(any)" : Slot + " " + EnchantRules.SlotName(Slot); }
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
                Logger.Warning("MS2 Enchant: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        void OnHostSaid(string text) { OutputWrap.Set(outputBox, text); }

        void OnHostBusy(bool on) { ViewLayout.Lock(this, lockable, on, outputBox); }

        static string KeyOf(EnchantLadder l) { return l.Slot + "\n" + l.Rank + "\n" + l.MinLv + "\n" + l.MaxLv; }

        /// <summary>Another session or mod (or an edit of another view): read the table again, as the mod now has it.</summary>
        void OnHostChanged()
        {
            if (shown != null) wantKey = KeyOf(shown);
            ed = null;
            read = false;
            shown = null;
            if (Visible && !host.Busy) ReadTable();
        }

        // ---- the ladders ----

        void Reload()
        {
            if (shown != null) wantKey = KeyOf(shown);
            ed = null;
            read = false;
            shown = null;
            ReadTable();
        }

        void ReadTable()
        {
            EditorSession s = host.SharedSession();
            if (s.Mod == null) { sumLabel.Text = "no mod is being edited"; return; }
            if (ed != null && ed.Data == s.Data && ed.Mod == s.Mod && read) { ShowLadders(); return; }
            EnchantEditor made = null;
            string failed = null;
            host.RunOutside("enchant table", delegate
            {
                try { made = new EnchantEditor(s.Data, s.Mod); }
                catch (ModKitException ex) { failed = ex.Message; }
            }, delegate
            {
                if (failed != null) { sumLabel.Text = failed; ladderList.Items.Clear(); return; }
                ed = made;
                ed.Others = delegate { return s.OtherMods(); };
                read = true;
                if (wantKey != null)
                    foreach (EnchantLadder l in ed.Ladders) if (KeyOf(l) == wantKey) { shown = l; break; }
                wantKey = null;
                ShowLadders();
            });
        }

        EnchantEditor Editor()
        {
            if (ed == null || !read) throw new ModKitException("the enchant table is being read: try again");
            if (host.SharedSession().Mod == null) throw new ModKitException("no mod is being edited");
            return ed;
        }

        static long Long(string v)
        {
            long n;
            return long.TryParse((v ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        bool Gone(EnchantLadder l)
        {
            foreach (TableRow r in l.Rows) if (!r.IsDeleted) return false;
            return true;
        }

        string StatsOf(EnchantLadder l)
        {
            foreach (TableRow r in l.Rows) if (!r.IsDeleted) return EnchantRules.StatsText(ed.Get(r, EnchantRules.Stats));
            return "";
        }

        void ShowLadders()
        {
            if (ed == null || !read) return;
            filling = true;
            try
            {
                ladderList.BeginUpdate();
                ladderList.Items.Clear();
                long slot = slotBox.SelectedItem is SlotChoice ? ((SlotChoice)slotBox.SelectedItem).Slot : 0;
                List<EnchantLadder> hits = ed.Find(slot, Long(rankBox.Text), Long(levelBox.Text));
                var order = new List<EnchantLadder>();
                foreach (EnchantLadder l in hits) if (ed.IsChanged(l)) order.Add(l);
                int changed = order.Count;
                if (!modOnly.Checked) foreach (EnchantLadder l in hits) if (!ed.IsChanged(l)) order.Add(l);
                if (shown != null && !ed.Ladders.Contains(shown)) shown = null;
                int inMod = 0;
                foreach (EnchantLadder l in ed.Ladders) if (ed.IsChanged(l)) inMod++;
                foreach (EnchantLadder l in order)
                {
                    var it = new ListViewItem(l.Slot + " " + EnchantRules.SlotName(l.Slot)) { Tag = l };
                    it.SubItems.Add(l.Rank.ToString(CultureInfo.InvariantCulture));
                    it.SubItems.Add(l.MinLv + "-" + l.MaxLv);
                    it.SubItems.Add(Gone(l) ? "" : EnchantRules.Percent(ed.Total(l, EnchantRules.MaxGrade)));
                    it.SubItems.Add(StatsOf(l));
                    bool added = ed.IsAdded(l), gone = Gone(l);
                    it.SubItems.Add(added ? "new" : gone ? "removed" : ed.IsChanged(l) ? "yes" : "");
                    if (added) it.ForeColor = Color.DarkGreen;
                    else if (gone) it.ForeColor = SystemColors.GrayText;
                    else if (ed.IsChanged(l)) it.ForeColor = Color.MediumBlue;
                    it.ToolTipText = l.Label;
                    ladderList.Items.Add(it);
                    if (l == shown) it.Selected = true;
                }
                sumLabel.Text = EnchantRules.Entry + ": " + ed.Ladders.Count + " ladders, " + inMod + " in the mod; " + order.Count + " shown (" + changed + " in the mod)";
                if (shown == null && order.Count > 0) { shown = order[0]; ladderList.Items[0].Selected = true; }
                if (shown != null && !order.Contains(shown)) shown = order.Count > 0 ? order[0] : null;
            }
            finally { ladderList.EndUpdate(); filling = false; }
            ShowLadder();
        }

        void ShowLadder()
        {
            EnchantLadder l = ed == null || !read ? null : shown;
            filling = true;
            try
            {
                gradeList.BeginUpdate();
                int keep = gradeList.SelectedIndices.Count > 0 ? gradeList.SelectedIndices[0] : -1;
                gradeList.Items.Clear();
                checkList.Items.Clear();
                if (l == null) { rowLabel.Text = "no ladder chosen"; infoLabel.Text = ""; return; }
                rowLabel.Text = l.Label + (ed.IsAdded(l) ? " (new)" : Gone(l) ? " (removed by the mod)" : ed.IsChanged(l) ? " (in the mod)" : "");
                infoLabel.Text = "items of slot " + l.Slot + " (" + EnchantRules.SlotName(l.Slot) + "), rank " + l.Rank + ", level " + l.MinLv + " to " + l.MaxLv
                    + (Gone(l) ? "" : ": " + StatsOf(l) + " +" + EnchantRules.Percent(ed.Total(l, EnchantRules.MaxGrade)) + " at +" + EnchantRules.MaxGrade);
                var listed = new HashSet<TableRow>();
                for (long g = 1; g <= EnchantRules.MaxGrade; g++)
                {
                    TableRow r = ed.Row(l, g);
                    if (r == null)
                        foreach (TableRow d in l.Rows) if (d.IsDeleted && ed.Int(d, EnchantRules.Grade) == g) { r = d; break; }
                    var it = new ListViewItem(g.ToString(CultureInfo.InvariantCulture)) { Tag = r };
                    if (r == null)
                    {
                        it.SubItems.AddRange(new[] { "", "", "", "no row: this level adds nothing", "", "" });
                        it.ForeColor = Color.DarkOrange;
                        gradeList.Items.Add(it);
                        continue;
                    }
                    listed.Add(r);
                    AddRowItem(it, l, r, g);
                }
                // rows the server does not use (a second row of a level, a level outside 1-15)
                foreach (TableRow r in l.Rows)
                {
                    if (listed.Contains(r)) continue;
                    var it = new ListViewItem(ed.Int(r, EnchantRules.Grade).ToString(CultureInfo.InvariantCulture)) { Tag = r };
                    AddRowItem(it, l, r, -1);
                    it.ForeColor = Color.DarkOrange;
                }
                if (keep >= 0 && keep < gradeList.Items.Count) gradeList.Items[keep].Selected = true;

                foreach (string t in ed.Check(l)) checkList.Items.Add(t);
                if (checkList.Items.Count == 0) checkList.Items.Add(Gone(l) ? "removed by the mod" : ed.IsChanged(l) ? "no problem found in the ladder" : "as shipped");
            }
            finally { gradeList.EndUpdate(); filling = false; }
        }

        void AddRowItem(ListViewItem it, EnchantLadder l, TableRow r, long g)
        {
            string rate = ed.Get(r, EnchantRules.Rate) ?? "", was = ed.Shipped(r, EnchantRules.Rate) ?? "";
            it.SubItems.Add(rate);
            it.SubItems.Add(r.IsAdded ? "" : was);
            it.SubItems.Add(r.IsDeleted || g < 0 ? "" : EnchantRules.Percent(ed.Total(l, g)));
            it.SubItems.Add(EnchantRules.StatsText(ed.Get(r, EnchantRules.Stats)));
            it.SubItems.Add(ed.Get(r, EnchantRules.Key) ?? "");
            it.SubItems.Add(r.IsAdded ? "new" : r.IsDeleted ? "removed" : r.IsChanged ? "yes" : "");
            TableRow row = r;
            List<string> problems = EnchantRules.Check(f => ed.Get(row, f));
            if (problems.Count > 0 && (r.IsAdded || r.IsChanged)) { it.ForeColor = Color.DarkOrange; it.ToolTipText = string.Join("; ", problems.ToArray()); }
            else if (r.IsAdded) it.ForeColor = Color.DarkGreen;
            else if (r.IsDeleted) it.ForeColor = SystemColors.GrayText;
            else if (r.IsChanged) it.ForeColor = Color.MediumBlue;
            gradeList.Items.Add(it);
        }

        void Edited(string said)
        {
            host.EditedOutside();
            ShowLadders();
            if (said != null) host.SayOutside(said);
        }

        EnchantLadder Shown()
        {
            Editor();
            if (shown == null) throw new ModKitException("choose a ladder in the list");
            return shown;
        }

        List<EnchantLadder> Chosen()
        {
            Editor();
            var l = new List<EnchantLadder>();
            foreach (ListViewItem it in ladderList.SelectedItems) l.Add((EnchantLadder)it.Tag);
            if (l.Count == 0) throw new ModKitException("choose one or more ladders in the list");
            return l;
        }

        static string Many(List<EnchantLadder> l) { return l.Count == 1 ? l[0].Label : l.Count + " ladders"; }

        static long Number(string text, string what)
        {
            long v;
            string t = text.Trim();
            if (!long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out v)) throw new ModKitException(t.Length == 0 ? "give " + what + " first" : "\"" + t + "\" is not " + what);
            return v;
        }

        static double Rate(string text, string what)
        {
            double v;
            string t = text.Trim();
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v) || double.IsNaN(v) || double.IsInfinity(v))
                throw new ModKitException(t.Length == 0 ? "give " + what + " first" : "\"" + t + "\" is not " + what);
            return v;
        }

        TableRow ChosenRow()
        {
            if (gradeList.SelectedItems.Count == 0) throw new ModKitException("choose a level in the list");
            var r = gradeList.SelectedItems[0].Tag as TableRow;
            if (r == null) throw new ModKitException("the ladder has no row for +" + gradeList.SelectedItems[0].Text + ": Add grade adds one");
            return r;
        }

        void SetRate()
        {
            EnchantLadder l = Shown();
            TableRow r = ChosenRow();
            string v = rateBox.Text.Trim();
            if (v == (ed.Get(r, EnchantRules.Rate) ?? "")) { host.SayOutside(l.Label + " +" + ed.Int(r, EnchantRules.Grade) + ": rate as it was"); return; }
            ed.Set(r, EnchantRules.Rate, v.Length == 0 ? null : v);
            Edited(l.Label + " +" + ed.Int(r, EnchantRules.Grade) + ": rate " + (v.Length == 0 ? "taken out" : v));
        }

        void RemoveOrRestoreRow()
        {
            EnchantLadder l = Shown();
            TableRow r = ChosenRow();
            string what = l.Label + " +" + ed.Int(r, EnchantRules.Grade) + " (row " + ed.Get(r, EnchantRules.Key) + ")";
            if (r.IsDeleted) { ed.Restore(r); Edited(what + " is back"); return; }
            bool added = r.IsAdded;
            ed.Remove(r);
            if (!ed.Ladders.Contains(l)) shown = null;
            Edited(added ? what + " was new in the mod and is gone again" : what + " is removed: that level adds nothing to these items");
        }

        void AddGrade()
        {
            EnchantLadder l = Shown();
            long g = Number(addGradeBox.Text, "an enchant level");
            double rate = Rate(addRateBox.Text, "a rate");
            List<long> free = ed.NextIds(host.SharedSession().OtherMods(), 1, null);
            if (free.Count == 0) throw new ModKitException("no free enchant row id is left in " + EnchantRules.From + "-" + EnchantRules.To);
            ed.AddGrade(l, g, rate, free[0]);
            Edited(l.Label + ": row " + free[0] + " added for +" + g + ", rate " + EnchantRules.FormatRate(rate));
        }

        void Scale()
        {
            List<EnchantLadder> l = Chosen();
            double f = Rate(factorBox.Text, "a factor");
            int n = ed.Scale(l, f);
            Edited(Many(l) + ": " + n + " rate(s) multiplied by " + f.ToString(CultureInfo.InvariantCulture));
        }

        void SetLadderField()
        {
            List<EnchantLadder> l = Chosen();
            string f = (string)fieldBox.SelectedItem;
            long v = Number(fieldValueBox.Text, "a number");
            string about = Many(l);
            ed.SetLadders(l, f, v);
            Edited(about + ": " + f + " = " + v + " on every row; Plan names the levels and items left without a bonus");
        }

        void SetStats()
        {
            List<EnchantLadder> l = Chosen();
            string v = statsBox.Text.Trim();
            List<long> codes;
            string bad;
            if (!EnchantRules.ServerStats(v, out codes, out bad)) throw new ModKitException("the server cannot read \"" + bad + "\" in \"" + v + "\"");
            ed.SetStats(l, v);
            Edited(Many(l) + ": stats " + EnchantRules.StatsText(v));
        }

        void RemoveOrRestoreLadders()
        {
            List<EnchantLadder> l = Chosen();
            bool restore = true;
            foreach (EnchantLadder d in l) if (!Gone(d)) restore = false;
            string about = Many(l);
            foreach (EnchantLadder d in l) { if (restore) ed.Restore(d); else ed.Remove(d); }
            if (shown != null && !ed.Ladders.Contains(shown)) shown = null;
            Edited(about + (restore ? " is back" : " removed (new rows gone, shipped rows deleted): those items get no enchant bonus"));
        }

        void RevertLadders()
        {
            List<EnchantLadder> l = Chosen();
            string about = Many(l);
            foreach (EnchantLadder d in l) ed.Revert(d);
            if (shown != null && !ed.Ladders.Contains(shown)) shown = null;
            Edited(about + ": the mod's changes dropped");
        }

        void CopyLadder()
        {
            EnchantLadder l = Shown();
            long slot = copySlotBox.Text.Trim().Length == 0 ? l.Slot : Number(copySlotBox.Text, "a slot");
            long rank = copyRankBox.Text.Trim().Length == 0 ? l.Rank : Number(copyRankBox.Text, "a rank");
            long lo = copyMinBox.Text.Trim().Length == 0 ? l.MinLv : Number(copyMinBox.Text, "a level");
            long hi = copyMaxBox.Text.Trim().Length == 0 ? l.MaxLv : Number(copyMaxBox.Text, "a level");
            if (slot == l.Slot && rank == l.Rank && lo == l.MinLv && hi == l.MaxLv) throw new ModKitException("give another slot, rank or levels for the copy");
            int n = 0;
            foreach (TableRow r in l.Rows) if (!r.IsDeleted) n++;
            List<long> free = ed.NextIds(host.SharedSession().OtherMods(), n, null);
            if (free.Count < n) throw new ModKitException("only " + free.Count + " free enchant row ids are left in " + EnchantRules.From + "-" + EnchantRules.To + "; the copy needs " + n);
            EnchantLadder made = ed.CopyLadder(l, slot, rank, lo, hi, free);
            shown = made;
            modOnly.Checked = false;
            Edited(made.Label + " added as a copy of " + l.Label + " (" + n + " rows, ids " + free[0] + "-" + free[n - 1] + "). Plan checks which rows come first for the same items.");
        }
    }
}
