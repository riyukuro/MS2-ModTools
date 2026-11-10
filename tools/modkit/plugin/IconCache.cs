// Item icons and npc portraits for the views: the images are read from Resource/Image.m2d of the user's own data
// folders on a background thread, decoded by WinForms (they are PNG files) and kept for the session. A view asks for
// an image by its entry; until it is loaded (or when no archive has it) the view draws a plain placeholder box, and
// the view is told to repaint once the image is there.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using MS2.ModKit;

namespace MS2.Tables
{
    internal sealed class IconCache : IDisposable
    {
        static IconCache shared;

        /// <summary>The cache of the data set (one for all views; a new data set gets a new cache).</summary>
        public static IconCache For(DataSet data)
        {
            if (data == null) return null;
            if (shared == null || shared.data != data)
            {
                if (shared != null) shared.Dispose();
                shared = new IconCache(data);
            }
            return shared;
        }

        static readonly Image Missing = new Bitmap(1, 1);   // marks an entry no archive has (or that does not decode)

        readonly DataSet data;
        readonly ImageSource source;
        readonly object gate = new object();
        readonly Dictionary<string, Image> images = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, List<Control>> waiting = new Dictionary<string, List<Control>>(StringComparer.OrdinalIgnoreCase);
        bool disposed;

        IconCache(DataSet data)
        {
            this.data = data;
            source = new ImageSource(data, null);
        }

        /// <summary>False when no data folder has an image archive (every icon is the placeholder).</summary>
        public bool Available { get { return source.Available; } }

        /// <summary>
        /// The image of the entry, or null while it loads or when there is none. A null answer for an entry not asked
        /// for before starts the load; the control given is repainted (Invalidate) when the image is there.
        /// </summary>
        public Image Get(string entry, Control repaint)
        {
            if (string.IsNullOrEmpty(entry) || !source.Available) return null;
            lock (gate)
            {
                if (disposed) return null;
                Image img;
                if (images.TryGetValue(entry, out img)) return img == Missing ? null : img;
                List<Control> list;
                if (waiting.TryGetValue(entry, out list))
                {
                    if (repaint != null && !list.Contains(repaint)) list.Add(repaint);
                    return null;
                }
                list = new List<Control>();
                if (repaint != null) list.Add(repaint);
                waiting[entry] = list;
            }
            ThreadPool.QueueUserWorkItem(delegate { Load(entry); });
            return null;
        }

        /// <summary>True when the entry was looked for and no archive has it (or it is not an image).</summary>
        public bool IsMissing(string entry)
        {
            if (string.IsNullOrEmpty(entry) || !source.Available) return true;
            lock (gate)
            {
                Image img;
                return images.TryGetValue(entry, out img) && img == Missing;
            }
        }

        void Load(string entry)
        {
            Image img = Missing;
            try
            {
                byte[] bytes = source.Read(entry);
                if (bytes != null)
                    using (var ms = new MemoryStream(bytes))
                    using (Image decoded = Image.FromStream(ms))
                        img = new Bitmap(decoded);   // a copy that does not need the stream
            }
            catch (Exception) { img = Missing; }
            List<Control> list;
            lock (gate)
            {
                if (disposed) { if (img != Missing) img.Dispose(); return; }
                images[entry] = img;
                if (!waiting.TryGetValue(entry, out list)) list = new List<Control>();
                waiting.Remove(entry);
            }
            foreach (Control c in list)
            {
                try
                {
                    if (!c.IsDisposed && c.IsHandleCreated) c.BeginInvoke((MethodInvoker)delegate { if (!c.IsDisposed) c.Invalidate(); });
                }
                catch (InvalidOperationException) { }   // the control's window went away meanwhile
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                foreach (Image i in images.Values) if (i != Missing) i.Dispose();
                images.Clear();
                waiting.Clear();
            }
            source.Dispose();
            if (shared == this) shared = null;
        }

        /// <summary>
        /// Draws the image scaled into the box (keeping its shape), or the placeholder: a light box with a border, and a
        /// cross in it when the image is known to be missing.
        /// </summary>
        public static void Draw(Graphics g, Rectangle box, Image img, bool missing)
        {
            if (img != null)
            {
                float scale = Math.Min(box.Width / (float)img.Width, box.Height / (float)img.Height);
                int w = Math.Max(1, (int)(img.Width * scale)), h = Math.Max(1, (int)(img.Height * scale));
                InterpolationMode old = g.InterpolationMode;
                g.InterpolationMode = scale >= 1.5f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h));
                g.InterpolationMode = old;
                return;
            }
            using (var fill = new SolidBrush(Color.FromArgb(236, 236, 236))) g.FillRectangle(fill, box);
            using (var pen = new Pen(Color.FromArgb(170, 170, 170)))
            {
                g.DrawRectangle(pen, box.X, box.Y, box.Width - 1, box.Height - 1);
                if (missing)
                {
                    int m = Math.Max(3, box.Width / 4);
                    g.DrawLine(pen, box.X + m, box.Y + m, box.Right - 1 - m, box.Bottom - 1 - m);
                    g.DrawLine(pen, box.Right - 1 - m, box.Y + m, box.X + m, box.Bottom - 1 - m);
                }
            }
        }
    }

    /// <summary>A box that shows one icon of the cache (the item form's larger icon, the npc form's portrait).</summary>
    internal sealed class IconBox : Control
    {
        IconCache cache;
        string entry;

        public IconBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        /// <summary>Shows the entry's image (null: the plain placeholder).</summary>
        public void Show(IconCache c, string e)
        {
            cache = c;
            entry = e;
            Invalidate();
        }

        public string Entry { get { return entry; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            Image img = cache != null ? cache.Get(entry, this) : null;
            bool missing = entry == null || cache == null || cache.IsMissing(entry);
            IconCache.Draw(e.Graphics, new Rectangle(0, 0, Width, Height), img, entry != null && missing);
        }
    }
}
