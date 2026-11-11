// "Where used": one shared dialog that lists the places naming an item, npc, skill, effect or quest id (quest
// conditions and rewards, shop stock, drop box rows, npc fields, item and skill fields; WhereUsed of the mod kit
// finds them, the mod's places first), and jumps to the view and row of the chosen place.
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

        /// <summary>Shows the dialog for an id of a kind; Go to opens the chosen place in its view.</summary>
        internal static void Show(Control owner, EditorSession session, string kind, long id)
        {
            List<UseHit> hits = new WhereUsed(session).Of(kind, id, Max);
            UseHit go;
            using (var d = new WhereUsedDialog(kind, id, hits, Max))
            {
                if (d.ShowDialog(owner.FindForm()) != DialogResult.OK || d.Chosen == null) return;
                go = d.Chosen;
            }
            Jump(go, id);
        }

        /// <summary>The Remove clone line: what still names the removed id, or "" when nothing does.</summary>
        internal static string LeftBehind(EditorSession session, string kind, long id)
        {
            string w = new WhereUsed(session).LeftBehind(kind, id, 3);
            return w == null ? "" : "\n" + w;
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
                case IdLookup.Quest: Shown<QuestsView>(QuestsView.ViewId, "MS2 Quests").OpenUse(h.Id, h.Field, h.Index); break;
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

            internal WhereUsedDialog(string kind, long id, List<UseHit> hits, int max)
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
