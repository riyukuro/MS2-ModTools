// Tab labels of the MS2 views in the document tab row. The dock panel's tab row neither wraps nor scrolls: tabs that
// do not fit are only in its drop-down list. So the views share the row: each tab shows the longest form of its name
// with which all of them fit ("MS2 Item options", "Item options", "Options"); the tooltip has the full name.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Emergent.Toolbench;
using WeifenLuo.WinFormsUI.Docking;

namespace MS2.Tables
{
    internal static class TabLabels
    {
        const string Prefix = "MS2 ";
        /// <summary>What the tab row adds to a tab beside its measured text, and what its buttons and margins take
        /// (measured in Toolbench: 13 tabs with 545 px of text take 810 px).</summary>
        const int TabPad = 24, RowPad = 50;

        static readonly Dictionary<DockContent, string> full = new Dictionary<DockContent, string>();
        static readonly List<DockPane> watched = new List<DockPane>();
        static bool fitting;

        /// <summary>The names of a view's tab, longest first: the full name, without "MS2 ", and its short form.</summary>
        public static string[] Forms(string name)
        {
            if (name == null || !name.StartsWith(Prefix, StringComparison.Ordinal)) return new[] { name ?? "" };
            string plain = name.Substring(Prefix.Length), brief = plain;
            switch (plain)
            {
                case "Shops and Drops": brief = "Shops"; break;
                case "Item options": brief = "Options"; break;
                case "Item sets": brief = "Sets"; break;
                case "Text import": brief = "Text"; break;
                case "Field rows": brief = "Fields"; break;
                case "Dungeons": brief = "Dungeon"; break;
            }
            return brief == plain ? new[] { name, plain } : new[] { name, plain, brief };
        }

        /// <summary>
        /// The form level (0 = full) with which tabs of these names fit a row of the given width: the first level whose
        /// widths (measure gives a text's width) with the row's padding fit; the shortest when none does.
        /// </summary>
        public static int Level(IList<string> names, int width, Func<string, int> measure)
        {
            for (int level = 0; level < 3; level++)
            {
                int sum = RowPad;
                foreach (string n in names) sum += measure(At(Forms(n), level)) + TabPad;
                if (sum <= width) return level;
            }
            return 2;
        }

        static string At(string[] forms, int level) { return forms[Math.Min(level, forms.Length - 1)]; }

        /// <summary>Relabels the tabs of the document pane the view's window is in, and keeps them fitted from then on.</summary>
        public static void Fit(DockContent dc)
        {
            if (dc == null || fitting) return;
            try
            {
                string text = dc.TabText ?? "";
                if (!full.ContainsKey(dc))
                {
                    if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return;
                    full[dc] = text;
                    dc.ToolTipText = text;
                    dc.Disposed += delegate { full.Remove(dc); };
                }
                DockPane pane = dc.Pane;
                if (pane == null) return;
                if (!watched.Contains(pane))
                {
                    watched.Add(pane);
                    pane.SizeChanged += delegate { FitPane(pane); };
                    if (pane.DockPanel != null)
                    {
                        pane.DockPanel.SizeChanged += delegate { FitPane(pane); };
                        pane.DockPanel.ActiveDocumentChanged += delegate { FitPane(pane); };
                    }
                    pane.Disposed += delegate { watched.Remove(pane); };
                }
                foreach (DockPane p in new List<DockPane>(watched)) FitPane(p);
            }
            catch (Exception ex) { Logger.Warning("MS2 Mods: could not fit the tab labels: " + ex.Message); }
        }

        static void FitPane(DockPane pane)
        {
            if (fitting || pane.IsDisposed || pane.Width <= 0) return;
            fitting = true;
            try
            {
                var mine = new List<DockContent>();
                var names = new List<string>();
                foreach (IDockContent c in pane.Contents)
                {
                    var d = c as DockContent;
                    if (d == null || d.IsHidden) continue;
                    string n;
                    if (full.TryGetValue(d, out n)) mine.Add(d);
                    else n = d.TabText ?? "";
                    names.Add(n);
                }
                if (mine.Count == 0) return;
                int level;
                // the tab in front is bold: measure all as bold, which is the wider
                using (var bold = new Font(pane.Font, FontStyle.Bold))
                    level = Level(names, pane.Width, delegate(string s) { return TextRenderer.MeasureText(s, bold).Width; });
                foreach (DockContent d in mine)
                {
                    string to = At(Forms(full[d]), level);
                    if (d.TabText != to) d.TabText = to;
                }
            }
            catch (Exception ex) { Logger.Warning("MS2 Mods: could not fit the tab labels: " + ex.Message); }
            finally { fitting = false; }
        }
    }
}
