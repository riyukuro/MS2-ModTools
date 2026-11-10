// Layout helpers shared by the views: open in Toolbench's centre (document) area, grid columns that share the full
// width, and panes that keep their share of the height when the window is resized.
using System;
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
        public static bool ToCentre(Control view)
        {
            try
            {
                for (Control c = view.Parent; c != null; c = c.Parent)
                {
                    var dc = c as DockContent;
                    if (dc == null) continue;
                    DockPanel p = dc.DockPanel;
                    if (p == null) return false;
                    if (dc.DockState == DockState.Document || dc.DockState == DockState.Float) return true;
                    dc.DockAreas |= DockAreas.Document;
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
                    dc.Activate();
                    return true;
                }
            }
            catch (Exception ex) { Logger.Warning("MS2 Mods: could not move the view to the centre area: " + ex.Message); return true; }
            return false;
        }

        /// <summary>
        /// The first time the view is shown in a dock window (Toolbench shows the views it had open at start, before or
        /// after their window handle exists), it moves to the centre area.
        /// </summary>
        public static void CentreOnFirstShow(Control view)
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
                    done = ToCentre(view);
                });
            };
            view.HandleCreated += attempt;
            view.VisibleChanged += attempt;
            view.ParentChanged += attempt;
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
        /// A pane docked at the top or bottom of its container keeps a share of the container's height (its first share
        /// is the one given; dragging its splitter sets a new one), never under min pixels and never so high that less
        /// than min pixels are left for the rest.
        /// </summary>
        public static void KeepHeightShare(Control pane, Control container, double share, int min)
        {
            bool adjusting = false;
            EventHandler fit = delegate
            {
                int h = container.ClientSize.Height;
                if (h <= 0) return;
                int want = (int)Math.Round(h * share);
                want = Math.Max(Math.Min(want, h - min), Math.Min(min, h / 2));
                if (want == pane.Height) return;
                adjusting = true;
                try { pane.Height = want; }
                finally { adjusting = false; }
            };
            container.Resize += fit;
            pane.SizeChanged += delegate
            {
                int h = container.ClientSize.Height;
                if (!adjusting && h > 2 * min && pane.Visible) share = Math.Max(0.05, Math.Min(0.95, pane.Height / (double)h));
            };
            fit(container, EventArgs.Empty);
        }
    }
}
