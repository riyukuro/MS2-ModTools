// The model preview of the Items and NPCs views: a still picture of the model, drawn by the library's own
// renderer (MS2.ModKit.Models) from the model archives of the user's data folders. Pictures are drawn on a
// background thread, one at a time; a newer request replaces one that has not started. Nothing here throws into
// a view: what cannot be drawn shows as one short line in the box.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using MS2.ModKit;
using MS2.ModKit.Models;

namespace MS2.Tables
{
    /// <summary>What a model box asks for: the files of an item, or the .kfm file of an npc.</summary>
    internal sealed class ModelRequest
    {
        public readonly string[] Paths;
        public readonly string Kfm;
        public readonly string Key;

        ModelRequest(string[] paths, string kfm)
        {
            Paths = paths;
            Kfm = kfm;
            Key = kfm != null ? "kfm\n" + kfm : "nif\n" + string.Join("\n", paths ?? new string[0]);
        }

        public static ModelRequest Item(IList<string> paths)
        {
            var a = new string[paths == null ? 0 : paths.Count];
            if (paths != null) paths.CopyTo(a, 0);
            return new ModelRequest(a, null);
        }

        public static ModelRequest Npc(string kfmFile) { return new ModelRequest(null, kfmFile ?? ""); }
    }

    /// <summary>A drawn request: the picture, or the line shown in its place.</summary>
    internal sealed class ModelShot
    {
        public Bitmap Picture;
        public string Message;
        public string Notes;
        public int Triangles;
    }

    internal sealed class ModelCache : IDisposable
    {
        const int Keep = 12;
        static ModelCache shared;

        /// <summary>The cache of the data set (one for all views; a new data set gets a new cache).</summary>
        public static ModelCache For(DataSet data)
        {
            if (data == null) return null;
            if (shared == null || shared.data != data)
            {
                if (shared != null) shared.Dispose();
                shared = new ModelCache(data);
            }
            return shared;
        }

        sealed class Job
        {
            public string Key;
            public ModelRequest Request;
            public int Width, Height;
            public Control Repaint;
        }

        readonly DataSet data;
        readonly object gate = new object();
        readonly Dictionary<string, ModelShot> shots = new Dictionary<string, ModelShot>(StringComparer.Ordinal);
        readonly List<string> order = new List<string>();
        ModelSource source;
        Job next;           // the request waiting for the worker (a newer one replaces it)
        string running;     // the key the worker is drawing
        bool working, disposed;

        ModelCache(DataSet data) { this.data = data; }

        /// <summary>
        /// The picture of the request at this size, or null while it is drawn; the control is repainted when it
        /// is there.
        /// </summary>
        public ModelShot Get(ModelRequest request, int width, int height, Control repaint)
        {
            if (request == null || width < 8 || height < 8) return null;
            string key = width + "x" + height + "\n" + request.Key;
            lock (gate)
            {
                if (disposed) return null;
                ModelShot shot;
                if (shots.TryGetValue(key, out shot)) return shot;
                if (running == key) return null;
                next = new Job { Key = key, Request = request, Width = width, Height = height, Repaint = repaint };
                if (working) return null;
                working = true;
            }
            ThreadPool.QueueUserWorkItem(delegate { Work(); });
            return null;
        }

        void Work()
        {
            for (;;)
            {
                Job job;
                lock (gate)
                {
                    job = next;
                    next = null;
                    if (job == null || disposed) { working = false; running = null; return; }
                    running = job.Key;
                }
                ModelShot shot = Draw(job);
                lock (gate)
                {
                    running = null;
                    if (disposed)
                    {
                        if (shot.Picture != null) shot.Picture.Dispose();
                        working = false;
                        return;
                    }
                    shots[job.Key] = shot;
                    order.Add(job.Key);
                    // old pictures are dropped from the list but not disposed: a box may still be showing one
                    while (order.Count > Keep) { shots.Remove(order[0]); order.RemoveAt(0); }
                }
                Control c = job.Repaint;
                try
                {
                    if (c != null && !c.IsDisposed && c.IsHandleCreated) c.BeginInvoke((MethodInvoker)delegate { if (!c.IsDisposed) c.Invalidate(); });
                }
                catch (InvalidOperationException) { }   // the control's window went away meanwhile
            }
        }

        ModelShot Draw(Job job)
        {
            var shot = new ModelShot();
            try
            {
                ModelSource src;
                lock (gate)
                {
                    if (source == null) source = new ModelSource(data);
                    src = source;
                }
                ModelPicture p = job.Request.Kfm != null
                    ? src.NpcPicture(job.Request.Kfm, job.Width, job.Height, null)
                    : src.Picture(job.Request.Paths, job.Width, job.Height, null);
                shot.Message = p.Message;
                shot.Triangles = p.Triangles;
                if (p.Notes.Count > 0) shot.Notes = string.Join("; ", p.Notes.ToArray());
                if (p.Argb != null && p.Message == null)
                {
                    var bmp = new Bitmap(p.Width, p.Height, PixelFormat.Format32bppArgb);
                    BitmapData bits = bmp.LockBits(new Rectangle(0, 0, p.Width, p.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        for (int y = 0; y < p.Height; y++)
                            Marshal.Copy(p.Argb, y * p.Width, new IntPtr(bits.Scan0.ToInt64() + (long)y * bits.Stride), p.Width);
                    }
                    finally { bmp.UnlockBits(bits); }
                    shot.Picture = bmp;
                }
            }
            catch (Exception x)
            {
                // the library gives its failures as a message; this is for what it did not foresee
                shot.Picture = null;
                shot.Message = "cannot show: " + x.GetType().Name;
            }
            return shot;
        }

        public void Dispose()
        {
            ModelSource src;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                shots.Clear();
                order.Clear();
                next = null;
                src = source;
                source = null;
            }
            if (src != null) src.Dispose();
            if (shared == this) shared = null;
        }
    }

    /// <summary>A box that shows the model picture of one request, drawn at the box's own size.</summary>
    internal sealed class ModelBox : Control
    {
        const int MaxSide = 640;
        ModelCache cache;
        ModelRequest request;
        ModelShot last;         // what was painted last: shown until the next picture is ready
        string lastKey;

        public ModelBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(236, 236, 236);
        }

        /// <summary>Called after a picture was painted for the first time (to set a tooltip).</summary>
        public event Action<ModelShot> Shown;

        /// <summary>Shows the request (null: an empty box).</summary>
        public void Show(ModelCache c, ModelRequest r)
        {
            if (cache == c && request != null && r != null && request.Key == r.Key) return;
            cache = c;
            request = r;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            var box = new Rectangle(0, 0, Width, Height);
            ModelShot shot = null;
            string line = null;
            try
            {
                if (request == null) last = null;
                else if (cache == null) { last = null; line = "no model data"; }
                else
                {
                    // drawn at the box's size (a very large box gets a picture of the largest size, scaled up)
                    float scale = Math.Min(1f, MaxSide / (float)Math.Max(1, Math.Max(Width, Height)));
                    int w = Math.Max(8, (int)(Width * scale)), h = Math.Max(8, (int)(Height * scale));
                    shot = cache.Get(request, w, h, this);
                    if (shot != null)
                    {
                        bool first = shot != last;
                        last = shot;
                        lastKey = request.Key;
                        if (first && Shown != null) Shown(shot);
                    }
                    else if (last != null && lastKey == request.Key) shot = last;   // the same model at the old size
                    else line = "drawing the model...";
                }
                if (shot != null)
                {
                    if (shot.Picture != null) g.DrawImage(shot.Picture, box);
                    else line = shot.Message ?? "no model";
                }
            }
            catch (Exception) { line = "cannot show"; }   // a picture disposed under the box, a failed draw
            if (line != null)
            {
                using (var f = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (var b = new SolidBrush(Color.FromArgb(96, 96, 96)))
                    g.DrawString(line, Font, b, new RectangleF(4, 4, Math.Max(1, Width - 8), Math.Max(1, Height - 8)), f);
            }
            using (var pen = new Pen(Color.FromArgb(200, 200, 200))) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        /// <summary>
        /// Puts a model box at the right side of a view's form panel, over its whole height. The box takes about
        /// a quarter of the panel's width and goes away when the panel is too narrow for both. Call this after the
        /// panel's other controls were added.
        /// </summary>
        public static ModelBox AttachRight(Panel form)
        {
            var box = new ModelBox { Dock = DockStyle.Fill };
            var pad = new Panel { Dock = DockStyle.Right, Width = 260, Padding = new Padding(2, 4, 6, 4) };
            pad.Controls.Add(box);
            form.Controls.Add(pad);
            EventHandler fit = delegate
            {
                int all = form.ClientSize.Width;
                bool show = all >= 560;
                int want = Math.Max(200, Math.Min(460, all * 28 / 100));
                if (pad.Visible != show) pad.Visible = show;
                if (show && pad.Width != want) pad.Width = want;
            };
            form.Resize += fit;
            fit(form, EventArgs.Empty);
            return box;
        }
    }
}
