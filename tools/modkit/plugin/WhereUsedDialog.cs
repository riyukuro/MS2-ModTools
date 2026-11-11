// "Where used": one shared dialog that lists the places naming an item, npc, skill, effect or quest id (quest
// conditions and rewards, shop stock, drop box rows, npc fields, item and skill fields; WhereUsed of the mod kit
// finds them, the mod's places first), and jumps to the view and row of the chosen place. The question runs as a job
// of the Tables view (the first one reads every shipped source), so the window stays live meanwhile.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using MS2.ModKit;
using Emergent.Toolbench;
using Emergent.Toolbench.UI.Workbench;

namespace MS2.Tables
{
    internal static class WhereUsedJump
    {
        const int Max = 2000;

        /// <summary>Asks for an id of a kind as a job, then shows the dialog; Go to opens the chosen place in its view.</summary>
        internal static void Show(Control owner, TablesView host, string kind, long id)
        {
            EditorSession session = host.SharedSession();
            List<UseHit> hits = null;
            List<string> skipped = null;
            string modError = null;
            host.RunOutside("Where used " + kind + " " + id.ToString(CultureInfo.InvariantCulture), delegate
            {
                bool first = !WhereUsed.IsIndexed(session.Data);
                hits = new WhereUsed(session).Of(kind, id, Max);
                skipped = Skipped(session, first);
                modError = ModError(session);
            }, delegate
            {
                // one text: a second SayOutside would replace the first
                var said = new List<string>();
                if (skipped != null) said.Add(SkippedText(skipped));
                if (modError != null) said.Add("Where used: " + modError);
                if (said.Count > 0) host.SayOutside(string.Join("\r\n", said.ToArray()));
                if (owner.IsDisposed) return;
                UseHit go;
                using (var d = new WhereUsedDialog(kind, id, hits, Max, WhereUsedSkipped(session)))
                {
                    if (d.ShowDialog(owner.FindForm()) != DialogResult.OK || d.Chosen == null) return;
                    go = d.Chosen;
                }
                Jump(go, id);
            });
        }

        /// <summary>
        /// The end of a Remove clone or a Discard: says what was done, then, as a job, lists the places that still
        /// name the id.
        /// </summary>
        internal static void SayLeftBehind(TablesView host, string said, string kind, long id)
        {
            // the job says "Checking ..." when it starts: the done block says it all again in one text
            host.SayOutside(said);
            EditorSession session = host.SharedSession();
            string w = null;
            List<string> skipped = null;
            string modError = null;
            host.RunOutside("Checking what still names " + kind + " " + id.ToString(CultureInfo.InvariantCulture), delegate
            {
                bool first = !WhereUsed.IsIndexed(session.Data);
                w = new WhereUsed(session).LeftBehind(kind, id);
                skipped = Skipped(session, first);
                modError = ModError(session);
            }, delegate
            {
                host.SayOutside(LeftBehindText(said, skipped, modError, w, kind, id));
            });
        }

        /// <summary>
        /// The whole text of a Remove clone or Discard: what was done, the skipped files, why the mod's groups were
        /// read as shipped, what still names the id.
        /// </summary>
        internal static string LeftBehindText(string said, List<string> skipped, string modError, string w, string kind, long id)
        {
            var l = new List<string> { said };
            if (skipped != null) l.Add(SkippedText(skipped));
            if (modError != null) l.Add("Where used: " + modError);
            l.Add(w == null ? "no place still names " + kind + " " + id.ToString(CultureInfo.InvariantCulture) : w.Replace("\n", "\r\n"));
            return string.Join("\r\n", l.ToArray());
        }

        static int WhereUsedSkipped(EditorSession session)
        {
            WhereUsedStats st = WhereUsed.Stats(session.Data);
            return st == null ? 0 : st.Skipped.Count;
        }

        // why the mod's shops and drop boxes were read as shipped at the last question; null when they were planned
        static string ModError(EditorSession session)
        {
            WhereUsedStats st = WhereUsed.Stats(session.Data);
            return st == null ? null : st.ModError;
        }

        // the files the shipped read skipped, once: right after the read that skipped them
        static List<string> Skipped(EditorSession session, bool first)
        {
            WhereUsedStats st = WhereUsed.Stats(session.Data);
            return first && st != null && st.Skipped.Count > 0 ? new List<string>(st.Skipped) : null;
        }

        static string SkippedText(List<string> skipped)
        {
            var l = new List<string> { "Where used: " + skipped.Count + " shipped file(s) could not be read and are left out:" };
            for (int i = 0; i < skipped.Count && i < 10; i++) l.Add("  " + skipped[i]);
            if (skipped.Count > 10) l.Add("  and " + (skipped.Count - 10) + " more");
            return string.Join("\r\n", l.ToArray());
        }

        /// <summary>
        /// Selects a cell of a grid and scrolls it into view; a grid with no room for rows yet (a pane just made or
        /// collapsed) refuses, and the jump then only opens the place.
        /// </summary>
        internal static void SelectCell(DataGridView grid, int column, int row, int above = 2)
        {
            if (row < 0 || row >= grid.RowCount || column < 0 || column >= grid.ColumnCount) return;
            try { grid.CurrentCell = grid[column, row]; }
            catch (InvalidOperationException) { }
            try { grid.FirstDisplayedScrollingRowIndex = Math.Max(0, row - above); }
            catch (InvalidOperationException) { }
            catch (ArgumentOutOfRangeException) { }
        }

        static T Shown<T>(string viewId, string what) where T : AbstractView
        {
            IViewService views = Platform.GetService<IViewService>();
            T v = views == null ? null : views.CreateOrFindView(viewId) as T;
            if (v == null) throw new ModKitException("the " + what + " view could not be opened");
            views.ShowView(v);
            return v;
        }

        static void Jump(UseHit h, long named)
        {
            switch (h.Kind)
            {
                case IdLookup.Item: Shown<ItemsView>(ItemsView.ViewId, "MS2 Items").OpenUse(h.Id, h.Field); break;
                case IdLookup.Npc: Shown<NpcsView>(NpcsView.ViewId, "MS2 NPCs").OpenUse(h.Id, h.Field); break;
                case IdLookup.Quest: Shown<QuestsView>(QuestsView.ViewId, "MS2 Quests").OpenUse(h.Id, h.Field, h.Index, h.Place); break;
                case IdLookup.Shop:
                case IdLookup.DropBox: Shown<GroupsView>(GroupsView.ViewId, "MS2 Shops and Drops").OpenUse(h.Kind == IdLookup.Shop ? GroupKind.Shop : GroupKind.DropBox, h.Id, h.Field, named); break;
                default: throw new ModKitException("no view shows " + h.Kind + " " + h.Id);
            }
        }

        /// <summary>The list of places: from, id, name, where, new; Go to / Close.</summary>
        sealed class WhereUsedDialog : Form
        {
            static readonly Color NewRow = Color.FromArgb(214, 240, 214);
            readonly ListView list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
            readonly Label status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            readonly Button go = new Button { Text = "Go to", DialogResult = DialogResult.OK, AutoSize = true };
            readonly List<UseHit> hits;

            internal UseHit Chosen { get; private set; }

            internal WhereUsedDialog(string kind, long id, List<UseHit> hits, int max, int skipped)
            {
                this.hits = hits;
                Text = "Where used: " + kind + " " + id.ToString(CultureInfo.InvariantCulture);
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.Sizable;
                MinimizeBox = MaximizeBox = false;
                ShowInTaskbar = false;
                Font = SystemFonts.MessageBoxFont;
                ClientSize = new Size(720, 440);
                MinimumSize = new Size(420, 260);

                list.Columns.Add("from", 70);
                list.Columns.Add("id", 90);
                list.Columns.Add("name", 220);
                list.Columns.Add("where", 260);
                list.Columns.Add("", 70);
                list.Resize += delegate { Fit(); };
                list.BeginUpdate();
                int fresh = 0;
                foreach (UseHit h in hits)
                {
                    var it = new ListViewItem(new[] { h.Kind, h.Id.ToString(CultureInfo.InvariantCulture), h.Name ?? "", h.Where, h.SourceNew ? "added" : h.New ? "new" : "" }) { Tag = h };
                    if (h.New) { it.BackColor = NewRow; fresh++; }
                    list.Items.Add(it);
                }
                list.EndUpdate();
                if (list.Items.Count > 0) list.Items[0].Selected = true;
                status.Text = hits.Count == 0 ? "nothing names " + kind + " " + id + " (in the fields the id pickers know)"
                    : hits.Count + (hits.Count >= max ? "+" : "") + " place(s), " + fresh + " of the mod (green; \"added\": in a row or entry the mod adds)";
                if (skipped > 0) status.Text += "; " + skipped + " shipped file(s) unreadable, left out (see the output)";

                var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, ColumnCount = 3, RowCount = 1, AutoSize = true, Padding = new Padding(6, 2, 6, 6) };
                bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true };
                bottom.Controls.Add(status, 0, 0);
                bottom.Controls.Add(go, 1, 0);
                bottom.Controls.Add(close, 2, 0);
                go.Enabled = hits.Count > 0;
                go.Click += delegate { Chosen = Selected(); };
                list.DoubleClick += delegate
                {
                    Chosen = Selected();
                    if (Chosen == null) return;
                    DialogResult = DialogResult.OK;
                };

                var middle = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 6, 6, 2) };
                middle.Controls.Add(list);
                Controls.Add(middle);
                Controls.Add(bottom);
                AcceptButton = go;
                CancelButton = close;
                Fit();
            }

            UseHit Selected() { return list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as UseHit : null; }

            void Fit()
            {
                int w = list.ClientSize.Width - list.Columns[0].Width - list.Columns[1].Width - list.Columns[4].Width - 4;
                if (w < 160) return;
                list.Columns[2].Width = w * 4 / 9;
                list.Columns[3].Width = w - w * 4 / 9;
            }
        }
    }
}
