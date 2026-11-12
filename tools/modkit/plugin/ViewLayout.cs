// Layout helpers shared by the views: open in Toolbench's centre (document) area, grid columns that share the full
// width, and panes that keep their share of the height when the window is resized.
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Emergent.Toolbench;
using WeifenLuo.WinFormsUI.Docking;

namespace MS2.Tables
{
    internal static class ViewLayout
    {
        /// <summary>
        /// Moves a view into the document (centre) area of the dock panel. Toolbench keeps the document area for views
        /// bound to a document: for any other view it takes Document out of the allowed dock areas when the view is
        /// made, so default-dock="Document" in plugin.xml ends up as a pane at the right edge, a fifth of the window
        /// wide, beside an empty centre. The view's dock window is a DockContent; this allows Document again and shows
        /// it there, as a tab of the document pane. A floating view is left where the user put it.
        /// </summary>
        public static bool ToCentre(Control view) { return ToCentre(view, true); }

        /// <summary>
        /// As above; with front false the document that was in front stays there (the view becomes a tab behind it),
        /// unless the view's window is the active one already (Toolbench showed it for the user).
        /// </summary>
        public static bool ToCentre(Control view, bool front)
        {
            try
            {
                for (Control c = view.Parent; c != null; c = c.Parent)
                {
                    var dc = c as DockContent;
                    if (dc == null) continue;
                    DockPanel p = dc.DockPanel;
                    if (p == null) return false;
                    NoTabDrag(dc);
                    if (dc.DockState == DockState.Document || dc.DockState == DockState.Float) { WholeWindow.Collapse(p); TabLabels.Fit(dc); return true; }
                    dc.DockAreas |= DockAreas.Document;
                    // a view made for another view's job (MS2 Tables under MS2 Triggers) is not the one the user is in
                    IDockContent before = p.ActiveDocument;
                    if (!front && (dc.IsActivated || p.ActiveContent == dc)) front = true;
                    // Show(panel, Document) keeps the pane the window already has (the right one), so the window
                    // joins the document pane that is there as a tab, or makes the document pane when there is none.
                    DockPane doc = p.ActiveDocumentPane;
                    if (doc == null || doc.DockState != DockState.Document)
                    {
                        doc = null;
                        foreach (DockPane pane in p.Panes)
                            if (pane.DockState == DockState.Document && !pane.IsHidden) { doc = pane; break; }
                    }
                    if (doc != null) dc.Show(doc, null);
                    else dc.DockTo(p, DockStyle.Fill);
                    if (front || before == null || before == dc || before.DockHandler.Form.IsDisposed) dc.Activate();
                    else before.DockHandler.Activate();
                    WholeWindow.Collapse(p);
                    TabLabels.Fit(dc);
                    return true;
                }
            }
            catch (Exception ex) { Logger.Warning("MS2 Mods: could not move the view to the centre area: " + ex.Message); return true; }
            return false;
        }

        /// <summary>
        /// Under Wine a plain click on a view's tab can start a drag of the view (the dock library asks DragDetect,
        /// which there takes the first mouse-move message as a move away), and the view then lands where the mouse is:
        /// docked at the top edge when the tab is under the panel's top drop mark in the middle of the tab row, or
        /// floating. There the MS2 views are not draggable by their tab or caption; on Windows they stay draggable.
        /// </summary>
        static void NoTabDrag(DockContent dc)
        {
            if (UnderWine && dc.DockHandler.AllowEndUserDocking) dc.DockHandler.AllowEndUserDocking = false;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        static extern IntPtr GetModuleHandleA(string name);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        static int wine = -1;

        static bool UnderWine
        {
            get
            {
                if (wine < 0)
                {
                    try
                    {
                        IntPtr h = GetModuleHandleA("ntdll.dll");
                        wine = h != IntPtr.Zero && GetProcAddress(h, "wine_get_version") != IntPtr.Zero ? 1 : 0;
                    }
                    catch (Exception) { wine = 0; }
                }
                return wine == 1;
            }
        }

        /// <summary>
        /// The first time the view is shown in a dock window (Toolbench shows the views it had open at start, before or
        /// after their window handle exists), it moves to the centre area.
        /// </summary>
        public static void CentreOnFirstShow(Control view) { CentreOnFirstShow(view, null); }

        /// <summary>As above; when behind says so at that time, the view becomes a tab behind the one in front.</summary>
        public static void CentreOnFirstShow(Control view, Func<bool> behind)
        {
            bool done = false, queued = false;
            EventHandler attempt = null;
            attempt = delegate
            {
                if (done || queued || !view.IsHandleCreated) return;
                queued = true;
                view.BeginInvoke((MethodInvoker)delegate
                {
                    queued = false;
                    if (done || view.IsDisposed) return;
                    done = ToCentre(view, behind == null || !behind());
                });
            };
            view.HandleCreated += attempt;
            view.VisibleChanged += attempt;
            view.ParentChanged += attempt;
        }

        /// <summary>
        /// Locks or unlocks a view's controls for a job of the Tables view. A disabled control that has the focus hands
        /// it to the next control of the window, which can be in the Tables view: the dock panel then brings that tab
        /// to the front. The focus goes to keep (the view's output box, never locked) before the controls are disabled.
        /// </summary>
        public static void Lock(Control view, IEnumerable<Control> lockable, bool on, Control keep)
        {
            if (on && view.ContainsFocus)
                foreach (Control c in lockable)
                    if (c.ContainsFocus) { keep.Focus(); break; }
            foreach (Control c in lockable) c.Enabled = !on;
            view.UseWaitCursor = on;
        }

        /// <summary>
        /// Columns share the grid's full width (fill mode, weighted by the width each column was made with) and never
        /// get narrower than that width: when they do not fit, the grid scrolls sideways instead.
        /// </summary>
        public static void FillColumns(DataGridView g)
        {
            foreach (DataGridViewColumn c in g.Columns) FitColumn(c);
            g.ColumnAdded += delegate(object sender, DataGridViewColumnEventArgs e) { FitColumn(e.Column); };
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        }

        /// <summary>Gives a column its minimum width and fill weight from its width (call before it is added).</summary>
        public static DataGridViewColumn FitColumn(DataGridViewColumn c)
        {
            if (c.MinimumWidth <= 5)   // not set yet: the width the column was made with is its minimum
            {
                int w = Math.Max(20, c.Width);
                c.MinimumWidth = w;
                c.FillWeight = Math.Max(1f, w / 10f);   // the weights of all columns must stay under 65535
            }
            return c;
        }

        /// <summary>
        /// The output box at the bottom of a view keeps two lines of its font and its sideways scroll bar (it scrolls)
        /// until its splitter is dragged.
        /// </summary>
        public static void KeepLogLines(TextBox box, Control container)
        {
            int h = 2 * box.Font.Height + SystemInformation.HorizontalScrollBarHeight + 8;
            box.Height = h;
            KeepHeightShare(box, container, 0, h);
        }

        /// <summary>
        /// A pane docked at the top or bottom of its container keeps a share of the container's height (its first share
        /// is the one given; dragging its splitter sets a new one), never under min pixels and never so high that less
        /// than min pixels are left for the rest.
        /// </summary>
        public static void KeepHeightShare(Control pane, Control container, double share, int min)
        {
            KeepHeightShare(pane, container, share, min, null);
        }

        /// <summary>
        /// As above, and the pane also leaves the rest the pixels keep() gives (the other panes and bars of the container
        /// with the least the pane beside it needs), so in a low window the pane gives way first.
        /// </summary>
        public static void KeepHeightShare(Control pane, Control container, double share, int min, Func<int> keep)
        {
            bool adjusting = false;
            EventHandler fit = delegate
            {
                int h = container.ClientSize.Height;
                if (h <= 0) return;
                int want = (int)Math.Round(h * share);
                int rest = keep != null ? Math.Max(min, keep()) : min;
                want = Math.Max(Math.Min(want, h - rest), Math.Min(min, h / 2));
                if (want == pane.Height) return;
                adjusting = true;
                try { pane.Height = want; }
                finally { adjusting = false; }
            };
            container.Resize += fit;
            pane.SizeChanged += delegate
            {
                int h = container.ClientSize.Height;
                // only a splitter drag (the mouse button is down) sets a new share: the view's own scaling when it is
                // made changes the height too, and must not
                if (!adjusting && h > 2 * min && pane.Visible && (Control.MouseButtons & MouseButtons.Left) != 0) share = Math.Max(0.05, Math.Min(0.95, pane.Height / (double)h));
            };
            fit(container, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The bold variant of a grid's font, made once and kept for as long as the grid keeps that font. A view holds
    /// one of these and disposes it with itself, so painting a heading line does not make a Font per cell.
    /// </summary>
    internal sealed class BoldFont : IDisposable
    {
        System.Drawing.Font from, bold;

        public System.Drawing.Font Of(System.Drawing.Font font)
        {
            if (bold == null || !ReferenceEquals(from, font))
            {
                if (bold != null) bold.Dispose();
                from = font;
                bold = new System.Drawing.Font(font, System.Drawing.FontStyle.Bold);
            }
            return bold;
        }

        public void Dispose()
        {
            if (bold != null) bold.Dispose();
            bold = null; from = null;
        }
    }
}
