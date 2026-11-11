// The output box of a view: the report is kept as it was said, and shown with the priority line broken to the
// width of the box (ModOrder.Wrap), again after each resize.
using System;
using System.Drawing;
using System.Windows.Forms;

namespace MS2.Tables
{
    static class OutputWrap
    {
        internal static void Attach(TextBox box)
        {
            box.Resize += delegate { if (box.Tag is string) Show(box); };
        }

        internal static void Set(TextBox box, string raw)
        {
            box.Tag = raw ?? "";
            Show(box);
        }

        internal static string Raw(TextBox box) { return box.Tag as string ?? box.Text; }

        static void Show(TextBox box)
        {
            string text = ModKit.ModOrder.Wrap((string)box.Tag, Columns(box));
            if (box.Text != text) box.Text = text;
        }

        static int Columns(TextBox box)
        {
            const string probe = "MMMMMMMMMMMMMMMMMMMM";
            int w = TextRenderer.MeasureText(probe, box.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
            if (w <= 0 || box.ClientSize.Width <= 0) return 0;
            return Math.Max(0, (box.ClientSize.Width - 6) * probe.Length / w - 1);
        }
    }
}
