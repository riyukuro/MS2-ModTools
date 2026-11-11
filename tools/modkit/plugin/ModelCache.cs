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
    /// <summary>What a model box asks for: the model of an item as a gender wears it, or the .kfm file of an npc.</summary>
    internal sealed class ModelRequest
    {
        public readonly ItemQuery Query;
        public readonly int Gender;
        public readonly string Kfm;
        public readonly string Key;

        ModelRequest(ItemQuery item, int gender, string kfm)
        {
            Query = item;
            Gender = gender;
            Kfm = kfm;
            if (kfm != null) { Key = "kfm\n" + kfm; return; }
            var k = new System.Text.StringBuilder("item\n").Append(gender);
            // the id picks the model only when the item's own file names none (the client's list is asked then)
            if (item.Own.Count == 0) k.Append('\n').Append(item.Id);
            foreach (ItemAsset a in item.Own)
                k.Append('\n').Append(a.Slot).Append('|').Append(a.Gender).Append('|').Append(a.TargetNode).Append('|').Append(a.Path);
            if (item.Fallback != null) k.Append("\nas\n").Append(item.Fallback.Path);
            Key = k.ToString();
        }

        public static ModelRequest Item(ItemQuery q, int gender) { return new ModelRequest(q ?? new ItemQuery(), gender == 1 ? 1 : 0, null); }

        public static ModelRequest Npc(string kfmFile) { return new ModelRequest(null, 0, kfmFile ?? ""); }
    }

    /// <summary>A drawn request: the picture, or the line shown in its place.</summary>
    internal sealed class ModelShot
    {
        public Bitmap Picture;
        public string Message;
        public string Notes;
        public int Triangles;
        /// <summary>True for an item whose two genders have different models.</summary>
        public bool Genders;
        internal string RequestKey;
        internal bool Dropped;      // left the cache while a box still showed it: disposed when the box lets go
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
            public readonly List<ModelBox> Boxes = new List<ModelBox>();    // repainted when the picture is there
        }

        readonly DataSet data;
        readonly object gate = new object();
        readonly Dictionary<string, ModelShot> shots = new Dictionary<string, ModelShot>(StringComparer.Ordinal);
        readonly List<string> order = new List<string>();
        readonly List<Job> pending = new List<Job>();       // at most one per box; boxes that ask for the same share one
        readonly Dictionary<ModelBox, ModelShot> held = new Dictionary<ModelBox, ModelShot>();   // what each box shows
        ModelSource source;
        Job running;        // the job the worker is drawing
        bool working, disposed;

        ModelCache(DataSet data) { this.data = data; }

        /// <summary>
        /// The picture of the request at this size, or null while it is drawn; the box is repainted when it is
        /// there. The box holds the picture it was given until it is given another or lets go (Forget).
        /// </summary>
        public ModelShot Get(ModelRequest request, int width, int height, ModelBox box)
        {
            if (request == null || box == null || width < 8 || height < 8) return null;
            string key = width + "x" + height + "\n" + request.Key;
            lock (gate)
            {
                if (disposed) return null;
                ModelShot shot;
                if (shots.TryGetValue(key, out shot)) { Unqueue(box, null); Hold(box, shot); return shot; }
                Job job = running != null && running.Key == key ? running : null;
                if (job == null) foreach (Job j in pending) if (j.Key == key) job = j;
                if (job == null)
                {
                    job = new Job { Key = key, Request = request, Width = width, Height = height };
                    pending.Add(job);
                }
                Unqueue(box, job);
                if (!job.Boxes.Contains(box)) job.Boxes.Add(box);
                if (working) return null;
                working = true;
            }
            ThreadPool.QueueUserWorkItem(delegate { Work(); });
            return null;
        }

        /// <summary>The box shows nothing of this cache any more.</summary>
        public void Forget(ModelBox box)
        {
            if (box == null) return;
            lock (gate)
            {
                Unqueue(box, null);
                Hold(box, null);
            }
        }

        // Takes the box out of the waiting jobs other than keep; a waiting job no box wants is dropped. Under the gate.
        void Unqueue(ModelBox box, Job keep)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                Job j = pending[i];
                if (j == keep || !j.Boxes.Remove(box)) continue;
                if (j.Boxes.Count == 0) pending.RemoveAt(i);
            }
            if (running != null && running != keep) running.Boxes.Remove(box);
        }

        // Notes what the box shows now; a picture that left the cache is disposed once no box shows it. Under the gate.
        void Hold(ModelBox box, ModelShot shot)
        {
            ModelShot old;
            held.TryGetValue(box, out old);
            if (shot == null) held.Remove(box); else held[box] = shot;
            if (old != null && old != shot && old.Dropped && !held.ContainsValue(old)) Free(old);
        }

        static void Free(ModelShot shot)
        {
            Bitmap b = shot.Picture;
            shot.Picture = null;
            if (b != null) b.Dispose();
        }

        // Takes a picture out of the cache. Under the gate.
        void Drop(string key)
        {
            ModelShot shot;
            if (!shots.TryGetValue(key, out shot)) return;
            shots.Remove(key);
            order.Remove(key);
            if (held.ContainsValue(shot)) shot.Dropped = true; else Free(shot);
        }

        void Work()
        {
            for (;;)
            {
                Job job;
                lock (gate)
                {
                    running = null;
                    if (pending.Count == 0 || disposed) { working = false; return; }
                    job = pending[0];
                    pending.RemoveAt(0);
                    running = job;
                }
                ModelShot shot = Draw(job);
                ModelBox[] boxes;
                lock (gate)
                {
                    running = null;
                    if (disposed)
                    {
                        Free(shot);
                        working = false;
                        return;
                    }
                    // one size of a request is kept: a box dragged to new sizes leaves no picture per size behind
                    foreach (string k in new List<string>(order))
                        if (shots[k].RequestKey == job.Request.Key) Drop(k);
                    shots[job.Key] = shot;
                    order.Add(job.Key);
                    while (order.Count > Keep) Drop(order[0]);
                    boxes = job.Boxes.ToArray();
                }
                foreach (ModelBox box in boxes)
                {
                    ModelBox c = box;
                    try
                    {
                        if (!c.IsDisposed && c.IsHandleCreated) c.BeginInvoke((MethodInvoker)delegate { if (!c.IsDisposed) c.Invalidate(); });
                    }
                    catch (InvalidOperationException) { }   // the box's window went away meanwhile
                }
            }
        }

        ModelShot Draw(Job job)
        {
            var shot = new ModelShot { RequestKey = job.Request.Key };
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
                    : src.ItemPicture(job.Request.Query, job.Request.Gender, job.Width, job.Height, null);
                shot.Message = p.Message;
                shot.Genders = p.Genders;
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
                foreach (string k in new List<string>(order)) Drop(k);   // a picture a box still shows goes when the box lets go
                shots.Clear();
                order.Clear();
                pending.Clear();
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
            if (cache != null && (cache != c || r == null)) { cache.Forget(this); last = null; lastKey = null; }
            cache = c;
            request = r;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && cache != null) { cache.Forget(this); cache = null; last = null; }
            base.Dispose(disposing);
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
