// The "MS2 Attendance" view: the server's login calendars (the AttendGift events of table/Server/NA/GameEvent.xml in
// the server's Server.m2d, MS2.ModKit.AttendGifts) as a mod edit.
//
// The list on the left shows each event with the mod (an edited event in blue, a new one in green, one that has
// ended in grey). On the right: the event's dates, its name and mail texts, the play time a day asks for (Set event
// writes them), and its days, one item each (the first day attended gives the first item); a day is added, set,
// removed or moved. New adds an empty event under an id no event uses, Copy adds a copy of the chosen one, Take
// puts the shipped event back (a new one: takes it out). Plan, Apply and Save mod are the MS2 Tables view's.
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
    public class AttendanceView : AbstractView
    {
        public const string ViewId = "MS2.Tables.AttendanceView";

        TablesView host;
        AttendGifts ag;            // for the session's data and mod
        bool read;                 // ag has read the table (on the worker thread)
        bool filling;
        int shown = -1;            // the id the right side shows

        readonly Label sumLabel = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 0) };
        readonly ListView eventList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, ShowItemToolTips = true };
        readonly ListView dayList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        readonly Label eventLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 0, 0), Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
        readonly TextBox startBox = new TextBox { Width = 120 };
        readonly TextBox endBox = new TextBox { Width = 120 };
        readonly TextBox nameBox = new TextBox { Width = 220 };
        readonly TextBox titleBox = new TextBox { Width = 220 };
        readonly TextBox textBox = new TextBox { Width = 320 };
        readonly TextBox urlBox = new TextBox { Width = 220 };
        readonly TextBox playBox = new TextBox { Width = 60 };
        readonly Label infoLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 0, 0) };
        readonly TextBox itemBox = new TextBox { Width = 90 };
        readonly TextBox countBox = new TextBox { Width = 50, Text = "1" };
        readonly TextBox gradeBox = new TextBox { Width = 40, Text = "1" };
        readonly TextBox newIdBox = new TextBox { Width = 90 };
        readonly TextBox newStartBox = new TextBox { Width = 120 };
        readonly TextBox newEndBox = new TextBox { Width = 120 };
        readonly TextBox outputBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Bottom, Height = 110 };
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> lockable = new List<Control>();

        public AttendanceView()
        {
            outputBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            OutputWrap.Attach(outputBox);
            outputBox.BackColor = SystemColors.Window;
            outputBox.ForeColor = SystemColors.WindowText;
            DateTime today = DateTime.UtcNow.Date;
            newStartBox.Text = AttendGifts.FormatTime(today);
            newEndBox.Text = AttendGifts.FormatTime(today.AddDays(30).AddMinutes(-1));

            FlowLayoutPanel top = Bar();
            top.Controls.Add(Btn("Read", "Reads the server's event table again", delegate { Reload(); }));
            top.Controls.Add(sumLabel);

            FlowLayoutPanel newBar = Bar();
            newBar.Controls.Add(Lbl("New id"));
            newBar.Controls.Add(newIdBox);
            tips.SetToolTip(newIdBox, "An event id no row of the file uses (the ids are one space with every event type); empty: the next free one");
            newBar.Controls.Add(Lbl("from"));
            newBar.Controls.Add(newStartBox);
            newBar.Controls.Add(Lbl("to"));
            newBar.Controls.Add(newEndBox);
            tips.SetToolTip(newStartBox, "yyyy-MM-dd-HH-mm, as the server reads it");
            tips.SetToolTip(newEndBox, "yyyy-MM-dd-HH-mm; the server leaves out an event whose end has passed");
            newBar.Controls.Add(Btn("New", "Adds an event with no days, empty texts and no play time asked", delegate { NewEvent(false); }));
            newBar.Controls.Add(Btn("Copy", "Adds a copy of the chosen event (its days, texts and play time, with the mod) under the new id and dates", delegate { NewEvent(true); }));
            newBar.Controls.Add(Btn("Take event", "Puts the chosen shipped event back as shipped; a new event is taken out of the mod", delegate { TakeEvent(); }));
            newBar.Controls.Add(Btn("Take all", "Puts every event back as shipped and takes the new ones out", delegate { TakeAll(); }));

            FlowLayoutPanel modBar = Bar();
            modBar.Controls.Add(Job("Plan", "Shows what the enabled mods and the pending edits would change, with the checks of the attendance events; writes nothing", delegate { return host.DoPlan(); }));
            modBar.Controls.Add(Job("Apply", "Saves the mod being edited and writes what the enabled mods change (the MS2 Tables view's Apply)", delegate { return host.DoApply(); }));
            modBar.Controls.Add(Btn("Save mod", "Saves the mod being edited (the same Save as in the MS2 Tables view)", delegate { host.SaveFromOutside(); }));

            eventList.Columns.Add("id", 70, HorizontalAlignment.Right);
            eventList.Columns.Add("name", 150);
            eventList.Columns.Add("start", 110);
            eventList.Columns.Add("end", 110);
            eventList.Columns.Add("days", 45, HorizontalAlignment.Right);
            eventList.Columns.Add("play s", 55, HorizontalAlignment.Right);
            eventList.Columns.Add("in mod", 55);

            dayList.Columns.Add("day", 45, HorizontalAlignment.Right);
            dayList.Columns.Add("item", 100, HorizontalAlignment.Right);
            dayList.Columns.Add("count", 60, HorizontalAlignment.Right);
            dayList.Columns.Add("grade", 50, HorizontalAlignment.Right);

            // the right side: the event's fields over its days
            var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(2) };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fields.Controls.Add(eventLabel, 0, 0);
            fields.SetColumnSpan(eventLabel, 2);
            var dates = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            dates.Controls.Add(startBox);
            dates.Controls.Add(Lbl("to"));
            dates.Controls.Add(endBox);
            Row(fields, 1, "Dates", dates, "yyyy-MM-dd-HH-mm, as the server reads them; the server leaves out an event whose end has passed (every shipped event has ended)");
            Row(fields, 2, "Name", nameBox, "The event's tab name the client shows (value2's string tabButton, which the server reads first, else name: setting it writes tabButton, and name too where the row has it)");
            Row(fields, 3, "Mail title", titleBox, "The title of the mail that brings a day's item");
            Row(fields, 4, "Mail text", textBox, "The text of the mail that brings a day's item");
            Row(fields, 5, "Event page", urlBox, "The link of the event's page (empty: none)");
            Row(fields, 6, "Play seconds", playBox, "The play time in a day before the day's item can be had, in seconds (0 or none: given at login, once a day)");
            fields.Controls.Add(infoLabel, 1, 7);
            var setBar = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0) };
            setBar.Controls.Add(Btn("Set event", "Writes the dates, the texts and the play time into the mod (the shipped values take their lines out)", delegate { SetEvent(); }));
            fields.Controls.Add(setBar, 1, 8);

            FlowLayoutPanel dayBar = Bar();
            dayBar.Dock = DockStyle.Bottom;
            dayBar.Controls.Add(Lbl("Item"));
            dayBar.Controls.Add(itemBox);
            tips.SetToolTip(itemBox, "The item id the day gives (an item the server has)");
            dayBar.Controls.Add(Lbl("count"));
            dayBar.Controls.Add(countBox);
            dayBar.Controls.Add(Lbl("grade"));
            dayBar.Controls.Add(gradeBox);
            tips.SetToolTip(gradeBox, "The item's grade (rarity): 1 normal .. 6");
            dayBar.Controls.Add(Btn("Add day", "Adds a day after the chosen one (or at the end)", delegate { EditDays('a'); }));
            dayBar.Controls.Add(Btn("Set day", "Sets the chosen day's item, count and grade", delegate { EditDays('s'); }));
            dayBar.Controls.Add(Btn("Remove day", "Removes the chosen day; the days after it move up one", delegate { EditDays('r'); }));
            dayBar.Controls.Add(Btn("Up", "Moves the chosen day one earlier", delegate { EditDays('u'); }));
            dayBar.Controls.Add(Btn("Down", "Moves the chosen day one later", delegate { EditDays('d'); }));

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(dayList);
            right.Controls.Add(dayBar);
            right.Controls.Add(fields);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
            split.Panel1.Controls.Add(eventList);
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
                split.SplitterDistance = Math.Max(100, split.Width * 45 / 100);
            };

            eventList.SelectedIndexChanged += delegate
            {
                if (filling || eventList.SelectedItems.Count == 0) return;
                shown = (int)eventList.SelectedItems[0].Tag;
                ShowEvent();
            };
            dayList.SelectedIndexChanged += delegate
            {
                if (filling || dayList.SelectedItems.Count == 0) return;
                ListViewItem it = dayList.SelectedItems[0];
                itemBox.Text = it.SubItems[1].Text;
                countBox.Text = it.SubItems[2].Text;
                gradeBox.Text = it.SubItems[3].Text;
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

        void Row(TableLayoutPanel t, int row, string label, Control c, string tip)
        {
            t.Controls.Add(Lbl(label), 0, row);
            t.Controls.Add(c, 1, row);
            tips.SetToolTip(c, tip);
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
                Logger.Warning("MS2 Attendance: " + ex.Message);
                return false;
            }
            if (host.Busy) { host.SayOutside("wait for the running job"); return false; }
            return host.RunGuarded(body);
        }

        void OnHostSaid(string text) { OutputWrap.Set(outputBox, text); }

        void OnHostBusy(bool on) { ViewLayout.Lock(this, lockable, on, outputBox); }

        /// <summary>Another session or mod (or an edit of another view): show the events again.</summary>
        void OnHostChanged()
        {
            EditorSession s = host.SharedSession();
            if (ag != null && (ag.Data != s.Data || ag.Mod != s.Mod)) { ag = null; read = false; }
            if (Visible) { if (read) ShowEvents(); else if (!host.Busy) ShowTable(); }
        }

        // ---- the events ----

        void Reload()
        {
            ag = null;
            read = false;
            ShowTable();
        }

        /// <summary>Reads the server's event table on the worker thread (once per session and mod), then shows it.</summary>
        void ShowTable()
        {
            EditorSession s = host.SharedSession();
            if (s.Mod == null) { sumLabel.Text = "no mod is being edited"; return; }
            if (ag == null || ag.Data != s.Data || ag.Mod != s.Mod) { ag = new AttendGifts(s.Data, s.Mod); read = false; }
            if (read) { ShowEvents(); return; }
            AttendGifts reading = ag;
            host.RunOutside("event table", delegate { reading.Events(); }, delegate
            {
                if (reading != ag) return;
                read = true;
                ShowEvents();
            });
        }

        static bool Ended(string end)
        {
            DateTime? t = AttendGifts.ParseTime(end);
            return t == null || t.Value < DateTime.UtcNow;
        }

        void ShowEvents()
        {
            if (ag == null || !read) return;
            if (!ag.Has) { sumLabel.Text = "the server data has no " + AttendGifts.Entry + " in Server.m2d"; eventList.Items.Clear(); return; }
            List<AttendEvent> evs = ag.Events();
            int live = 0, added = 0;
            filling = true;
            try
            {
                eventList.BeginUpdate();
                eventList.Items.Clear();
                foreach (AttendEvent ev in evs)
                {
                    List<AttendDay> days = ag.Days(ev);
                    string end = ag.End(ev), play = ag.PlaySeconds(ev);
                    var it = new ListViewItem(ev.Id.ToString(CultureInfo.InvariantCulture)) { Tag = ev.Id };
                    it.SubItems.Add(ag.Text(ev, "name"));
                    it.SubItems.Add(ag.Start(ev));
                    it.SubItems.Add(end);
                    it.SubItems.Add(days == null ? "?" : days.Count.ToString(CultureInfo.InvariantCulture));
                    it.SubItems.Add(play ?? (ag.HasPay(ev) ? "paid" : "-"));
                    it.SubItems.Add(ev.IsAdded ? "new" : ag.IsEdited(ev) ? "yes" : "");
                    var tip = new List<string>();
                    bool ended = Ended(end);
                    if (ended) tip.Add("ended: the server leaves it out");
                    else live++;
                    if (ev.IsAdded) added++;
                    if (ev.Rows > 1) tip.Add(ev.Rows + " rows of the id in the file: edit them by hand");
                    if (play == null) tip.Add("no play time (Config): given at login");
                    string req = ag.Requirement(ev);
                    if (req.Length > 0) tip.Add("requires " + req);
                    if (ev.IsAdded) it.ForeColor = Color.DarkGreen;
                    else if (ag.IsEdited(ev)) it.ForeColor = Color.MediumBlue;
                    else if (ended) it.ForeColor = SystemColors.GrayText;
                    it.ToolTipText = string.Join("\n", tip.ToArray());
                    eventList.Items.Add(it);
                    if (ev.Id == shown && !it.Selected) it.Selected = true;
                }
            }
            finally { eventList.EndUpdate(); filling = false; }
            sumLabel.Text = AttendGifts.Entry + ": " + evs.Count + " attendance events, " + live + " running or to come, " + ag.EditedCount() + " in the mod (" + added + " new)";
            if (shown < 0 && evs.Count > 0) shown = evs[evs.Count - 1].Id;
            ShowEvent();
        }

        AttendEvent Shown()
        {
            if (ag == null || !read) throw new ModKitException("the event table is being read: try again");
            if (host.SharedSession().Mod == null) throw new ModKitException("no mod is being edited");
            AttendEvent ev = shown < 0 ? null : ag.Find(shown);
            if (ev == null) throw new ModKitException("choose an event in the list");
            return ev;
        }

        void ShowEvent()
        {
            AttendEvent ev = ag == null || !read || shown < 0 ? null : ag.Find(shown);
            filling = true;
            try
            {
                dayList.BeginUpdate();
                int keep = dayList.SelectedIndices.Count > 0 ? dayList.SelectedIndices[0] : -1;
                dayList.Items.Clear();
                if (ev == null)
                {
                    eventLabel.Text = "no event chosen";
                    foreach (TextBox b in new[] { startBox, endBox, nameBox, titleBox, textBox, urlBox, playBox }) b.Text = "";
                    infoLabel.Text = "";
                    return;
                }
                eventLabel.Text = "Event " + ev.Id + (ev.IsAdded ? " (new)" : ag.IsEdited(ev) ? " (in the mod)" : "");
                startBox.Text = ag.Start(ev);
                endBox.Text = ag.End(ev);
                nameBox.Text = ag.Text(ev, "name");
                titleBox.Text = ag.Text(ev, "mailTitle");
                textBox.Text = ag.Text(ev, "mailContents");
                urlBox.Text = ag.Text(ev, "detailUrl");
                playBox.Text = ag.PlaySeconds(ev) ?? "";
                var info = new List<string>();
                if (Ended(ag.End(ev))) info.Add("ended: the server leaves it out");
                if (ag.Text(ev, "name").Length == 0) info.Add("no tab name (set one)");
                if (ag.PlaySeconds(ev) == null) info.Add("no play time: the day's item is given at login");
                if (ag.HasPay(ev)) info.Add("with paid catch-up of missed days");
                string req = ag.Requirement(ev);
                if (req.Length > 0) info.Add("requires " + req);
                infoLabel.Text = string.Join("; ", info.ToArray());
                List<AttendDay> days = ag.Days(ev);
                if (days == null) { infoLabel.Text += (infoLabel.Text.Length > 0 ? "; " : "") + "value1 is not XML the server reads: edit it by hand"; return; }
                for (int i = 0; i < days.Count; i++)
                {
                    var it = new ListViewItem((i + 1).ToString(CultureInfo.InvariantCulture));
                    it.SubItems.Add(days[i].ItemId);
                    it.SubItems.Add(days[i].Count);
                    it.SubItems.Add(days[i].Grade);
                    if (AttendGifts.DayProblem(days[i]) != null) it.ForeColor = Color.DarkOrange;
                    dayList.Items.Add(it);
                }
                if (keep >= 0 && keep < dayList.Items.Count) dayList.Items[keep].Selected = true;
            }
            finally { dayList.EndUpdate(); filling = false; }
        }

        void Edited(string said)
        {
            host.EditedOutside();
            ShowEvents();
            host.SayOutside(said);
        }

        void SetEvent()
        {
            AttendEvent ev = Shown();
            var changed = new List<string>();
            if (ag.SetDates(ev, startBox.Text, endBox.Text)) changed.Add("dates");
            ev = ag.Find(ev.Id);
            string[] values = { nameBox.Text, titleBox.Text, textBox.Text, urlBox.Text };
            for (int i = 0; i < AttendGifts.TextFields.Length; i++)
                if (ag.SetText(ev, AttendGifts.TextFields[i], values[i])) changed.Add(AttendGifts.TextFields[i]);
            if (playBox.Text.Trim().Length > 0 || ag.PlaySeconds(ev) != null)
                if (ag.SetPlaySeconds(ev, playBox.Text)) changed.Add("play time");
            Edited("event " + ev.Id + ": " + (changed.Count == 0 ? "as it was" : string.Join(", ", changed.ToArray()) + " set") + (ag.IsEdited(ev) ? "" : " (as shipped: no line in the mod)"));
        }

        void EditDays(char what)
        {
            AttendEvent ev = Shown();
            List<AttendDay> days = ag.Days(ev);
            if (days == null) throw new ModKitException("event " + ev.Id + " value1 is not XML the server reads: edit it by hand");
            int at = dayList.SelectedIndices.Count > 0 ? dayList.SelectedIndices[0] : -1;
            var day = new AttendDay(itemBox.Text.Trim(), countBox.Text.Trim(), gradeBox.Text.Trim());
            int sel = at;
            switch (what)
            {
                case 'a':
                    sel = at < 0 ? days.Count : at + 1;
                    days.Insert(sel, day);
                    break;
                case 's':
                    if (at < 0) throw new ModKitException("choose a day");
                    days[at] = day;
                    break;
                case 'r':
                    if (at < 0) throw new ModKitException("choose a day");
                    days.RemoveAt(at);
                    sel = Math.Min(at, days.Count - 1);
                    break;
                case 'u':
                case 'd':
                    if (at < 0) throw new ModKitException("choose a day");
                    int to = what == 'u' ? at - 1 : at + 1;
                    if (to < 0 || to >= days.Count) return;
                    AttendDay t = days[at]; days[at] = days[to]; days[to] = t;
                    sel = to;
                    break;
            }
            bool changed = ag.SetDays(ev, days);
            host.EditedOutside();
            ShowEvents();
            if (sel >= 0 && sel < dayList.Items.Count) { dayList.SelectedIndices.Clear(); dayList.Items[sel].Selected = true; dayList.EnsureVisible(sel); }
            host.SayOutside("event " + ev.Id + ": " + days.Count + " days" + (changed ? "" : " (as it was)") + (ag.IsEdited(ag.Find(ev.Id)) ? "" : " (as shipped: no line in the mod)"));
        }

        int NewId()
        {
            string t = newIdBox.Text.Trim();
            int id;
            if (t.Length == 0)
            {
                id = 1;
                foreach (AttendEvent ev in ag.Events()) if (ev.Id >= id) id = ev.Id + 1;
                while (ag.IdUsed(id)) id++;
                return id;
            }
            if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out id)) throw new ModKitException("the new id \"" + t + "\" is not a whole number");
            return id;
        }

        void NewEvent(bool copy)
        {
            if (ag == null || !read) throw new ModKitException("the event table is being read: try again");
            if (host.SharedSession().Mod == null) throw new ModKitException("no mod is being edited");
            int from = copy ? Shown().Id : 0;
            int id = NewId();
            AttendEvent ev = copy ? ag.Copy(from, id, newStartBox.Text, newEndBox.Text) : ag.New(id, newStartBox.Text, newEndBox.Text);
            shown = ev.Id;
            newIdBox.Text = "";
            Edited("event " + ev.Id + " added" + (copy ? " as a copy of " + from : "") + ", " + ag.Start(ev) + " to " + ag.End(ev) + (copy ? "" : ": give it a name, the mail texts and its days"));
        }

        void TakeEvent()
        {
            AttendEvent ev = Shown();
            bool had = ag.Take(ev);
            if (ev.IsAdded) shown = -1;
            Edited("event " + ev.Id + ": " + (!had ? "the mod does not change it" : ev.IsAdded ? "taken out of the mod" : "as shipped again"));
        }

        void TakeAll()
        {
            if (ag == null || !read) throw new ModKitException("the event table is being read: try again");
            int n = ag.TakeAll();
            if (shown >= 0 && ag.Find(shown) == null) shown = -1;
            Edited("every event as shipped again (" + n + " taken out of the mod)");
        }
    }
}
