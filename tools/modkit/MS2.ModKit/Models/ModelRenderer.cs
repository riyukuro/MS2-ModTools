// A software renderer for the still model preview (tools/modkit/design.md): one lit, textured picture of a
// Model as 0xAARRGGBB pixels on a clear background. It needs no graphics device, so it runs the same in the
// net35 plugin and in the tests.
//
// The camera looks at the middle of the model's bounds from the front, a little to the side and from above
// (the game's models stand on z and face -y), and the picture is scaled so the whole model fills the frame.
// Triangles are filled against a depth buffer at twice the size and averaged down. Opaque and alpha-tested
// meshes come first; blended meshes follow, far to near.
using System;
using System.Collections.Generic;

namespace MS2.ModKit.Models
{
    /// <summary>Gives the decoded texture for a file name a model names, or null when there is none.</summary>
    public delegate Texture TextureLookup(string name);

    public sealed class RenderOptions
    {
        /// <summary>The turn of the camera around the model's upright axis and its rise, in degrees.</summary>
        public float Yaw = 28, Pitch = 14;
        /// <summary>The part of the frame left free around the model, 0 to 0.5.</summary>
        public float Margin = 0.06f;
        /// <summary>The colour of a mesh without a texture that names no colour, 0xRRGGBB.</summary>
        public int Grey = 0xB4B4B4;
    }

    public static class ModelRenderer
    {
        const int Ss = 2;

        sealed class Mips
        {
            public Texture[] Level;
        }

        sealed class Tri
        {
            public ModelPart Part;
            public int A, B, C;
            public float Depth;
        }

        sealed class Frame
        {
            public int W, H;
            public int[] Colour;
            public float[] Z;       // 1 / depth; larger is nearer
        }

        /// <summary>Draws the model. The result has width * height pixels, top row first; a model without triangles gives a clear picture.</summary>
        public static int[] Render(Model model, int width, int height, TextureLookup textures, RenderOptions options)
        {
            if (width <= 0 || height <= 0) return new int[0];
            if (options == null) options = new RenderOptions();
            var f = new Frame { W = width * Ss, H = height * Ss };
            f.Colour = new int[f.W * f.H];
            f.Z = new float[f.W * f.H];
            if (model != null && !model.Empty) Draw(model, f, textures, options);
            return Down(f, width, height);
        }

        public static int[] Render(Model model, int width, int height, TextureLookup textures) { return Render(model, width, height, textures, null); }

        static void Draw(Model model, Frame f, TextureLookup textures, RenderOptions o)
        {
            double cx = (model.Min[0] + model.Max[0]) / 2.0, cy = (model.Min[1] + model.Max[1]) / 2.0, cz = (model.Min[2] + model.Max[2]) / 2.0;
            double dx = model.Max[0] - model.Min[0], dy = model.Max[1] - model.Min[1], dz = model.Max[2] - model.Min[2];
            double radius = Math.Sqrt(dx * dx + dy * dy + dz * dz) / 2;
            if (!(radius > 1e-9) || double.IsInfinity(radius)) return;
            double yaw = o.Yaw * Math.PI / 180, pitch = o.Pitch * Math.PI / 180;
            // the unit vector from the model to the camera, then the camera's right and up
            double ex = Math.Sin(yaw) * Math.Cos(pitch), ey = -Math.Cos(yaw) * Math.Cos(pitch), ez = Math.Sin(pitch);
            double rx = -ey, ry = ex, rz = 0;                   // (0,0,1) x e, pointing to the camera's right
            double rl = Math.Sqrt(rx * rx + ry * ry);
            if (rl < 1e-9) { rx = 1; ry = 0; rl = 1; }
            rx /= rl; ry /= rl;
            double ux = ey * rz - ez * ry, uy = ez * rx - ex * rz, uz = ex * ry - ey * rx;   // e x r
            double dist = radius * 4;
            // the light: from the camera, up and to the left
            double lx = ex + 0.55 * ux - 0.45 * rx, ly = ey + 0.55 * uy - 0.45 * ry, lz = ez + 0.55 * uz - 0.45 * rz;
            double ll = Math.Sqrt(lx * lx + ly * ly + lz * lz);
            lx /= ll; ly /= ll; lz /= ll;

            // project every vertex: x and y on the view plane, q = 1 / depth, and the light on its normal
            var proj = new Dictionary<ModelPart, float[]>();
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (ModelPart p in model.Parts)
            {
                int n = p.VertexCount;
                var v = new float[n * 4];
                for (int i = 0; i < n; i++)
                {
                    double x = p.Positions[i * 3] - cx, y = p.Positions[i * 3 + 1] - cy, z = p.Positions[i * 3 + 2] - cz;
                    double depth = dist - (x * ex + y * ey + z * ez);
                    if (!(depth > dist * 0.05)) depth = dist * 0.05;
                    double sx = (x * rx + y * ry + z * rz) / depth, sy = (x * ux + y * uy + z * uz) / depth;
                    v[i * 4] = (float)sx; v[i * 4 + 1] = (float)sy; v[i * 4 + 2] = (float)(1 / depth);
                    if (p.Normals == null) v[i * 4 + 3] = 2;
                    else
                    {
                        // both sides are lit: a normal that points away from the camera is turned round
                        double nx = p.Normals[i * 3], ny = p.Normals[i * 3 + 1], nz = p.Normals[i * 3 + 2];
                        double l = nx * lx + ny * ly + nz * lz;
                        v[i * 4 + 3] = (float)(nx * ex + ny * ey + nz * ez < 0 ? -l : l);
                    }
                    if (sx < minX) minX = sx; if (sx > maxX) maxX = sx;
                    if (sy < minY) minY = sy; if (sy > maxY) maxY = sy;
                }
                proj[p] = v;
            }
            double margin = Math.Min(0.45, Math.Max(0, o.Margin));
            double spanX = Math.Max(maxX - minX, 1e-12), spanY = Math.Max(maxY - minY, 1e-12);
            double scale = Math.Min(f.W * (1 - 2 * margin) / spanX, f.H * (1 - 2 * margin) / spanY);
            double offX = f.W / 2.0 - (minX + maxX) / 2 * scale, offY = f.H / 2.0 + (minY + maxY) / 2 * scale;
            foreach (float[] v in proj.Values)
                for (int i = 0; i < v.Length; i += 4)
                {
                    v[i] = (float)(offX + v[i] * scale);
                    v[i + 1] = (float)(offY - v[i + 1] * scale);
                }

            var mips = new Dictionary<string, Mips>(StringComparer.OrdinalIgnoreCase);
            var blended = new List<Tri>();
            foreach (ModelPart p in model.Parts)
            {
                Mips tex = Lookup(p, textures, mips);
                bool blend = p.Blend && (p.Alpha < 0.999f || (tex != null && tex.Level[0].HasAlpha));
                float[] v = proj[p];
                for (int t = 0; t + 2 < p.Triangles.Length; t += 3)
                {
                    int a = p.Triangles[t], b = p.Triangles[t + 1], c = p.Triangles[t + 2];
                    if (blend) blended.Add(new Tri { Part = p, A = a, B = b, C = c, Depth = v[a * 4 + 2] + v[b * 4 + 2] + v[c * 4 + 2] });
                    else Fill(f, p, v, a, b, c, tex, false, o, lx, ly, lz);
                }
            }
            blended.Sort(delegate (Tri x, Tri y) { return x.Depth.CompareTo(y.Depth); });      // small 1/depth: far first
            foreach (Tri t in blended)
                Fill(f, t.Part, proj[t.Part], t.A, t.B, t.C, Lookup(t.Part, textures, mips), true, o, lx, ly, lz);
        }

        static Mips Lookup(ModelPart p, TextureLookup textures, Dictionary<string, Mips> cache)
        {
            if (p.Texture == null || p.Uvs == null || textures == null) return null;
            Mips m;
            if (cache.TryGetValue(p.Texture, out m)) return m;
            Texture t = null;
            try { t = textures(p.Texture); }
            catch (ModKitException) { }
            if (t != null && (t.Width <= 0 || t.Height <= 0 || t.Argb.Length < t.Width * t.Height)) t = null;
            if (t != null)
            {
                var levels = new List<Texture> { t };
                while (levels.Count < 12 && (t.Width > 1 || t.Height > 1))
                {
                    t = Half(t);
                    levels.Add(t);
                }
                m = new Mips { Level = levels.ToArray() };
            }
            cache[p.Texture] = m;
            return m;
        }

        internal static Texture Half(Texture s)
        {
            int w = Math.Max(1, s.Width / 2), h = Math.Max(1, s.Height / 2);
            var d = new Texture(w, h) { HasAlpha = s.HasAlpha };
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Min(s.Width - 1, x * 2), x1 = Math.Min(s.Width - 1, x * 2 + 1);
                    int y0 = Math.Min(s.Height - 1, y * 2), y1 = Math.Min(s.Height - 1, y * 2 + 1);
                    int p0 = s.Argb[y0 * s.Width + x0], p1 = s.Argb[y0 * s.Width + x1], p2 = s.Argb[y1 * s.Width + x0], p3 = s.Argb[y1 * s.Width + x1];
                    int a = ((p0 >> 24 & 255) + (p1 >> 24 & 255) + (p2 >> 24 & 255) + (p3 >> 24 & 255) + 2) >> 2;
                    int r = ((p0 >> 16 & 255) + (p1 >> 16 & 255) + (p2 >> 16 & 255) + (p3 >> 16 & 255) + 2) >> 2;
                    int g = ((p0 >> 8 & 255) + (p1 >> 8 & 255) + (p2 >> 8 & 255) + (p3 >> 8 & 255) + 2) >> 2;
                    int b = ((p0 & 255) + (p1 & 255) + (p2 & 255) + (p3 & 255) + 2) >> 2;
                    d.Argb[y * w + x] = a << 24 | r << 16 | g << 8 | b;
                }
            return d;
        }

        static void Fill(Frame f, ModelPart p, float[] v, int ia, int ib, int ic, Mips tex, bool blend, RenderOptions o, double lx, double ly, double lz)
        {
            float ax = v[ia * 4], ay = v[ia * 4 + 1], bx = v[ib * 4], by = v[ib * 4 + 1], cx = v[ic * 4], cy = v[ic * 4 + 1];
            float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (area == 0 || float.IsNaN(area) || float.IsInfinity(area)) return;
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx))));
            int x1 = Math.Min(f.W - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy))));
            int y1 = Math.Min(f.H - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
            if (x0 > x1 || y0 > y1) return;
            float qa = v[ia * 4 + 2], qb = v[ib * 4 + 2], qc = v[ic * 4 + 2];
            float la = v[ia * 4 + 3], lb = v[ib * 4 + 3], lc = v[ic * 4 + 3];
            if (la > 1.5f)
            {
                // no normals: light the face by its own plane
                float[] P = p.Positions;
                double e1x = P[ib * 3] - P[ia * 3], e1y = P[ib * 3 + 1] - P[ia * 3 + 1], e1z = P[ib * 3 + 2] - P[ia * 3 + 2];
                double e2x = P[ic * 3] - P[ia * 3], e2y = P[ic * 3 + 1] - P[ia * 3 + 1], e2z = P[ic * 3 + 2] - P[ia * 3 + 2];
                double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
                double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                la = lb = lc = nl > 0 ? (float)Math.Abs((nx * lx + ny * ly + nz * lz) / nl) : 0;
            }
            float ua = 0, va = 0, ub = 0, vb = 0, uc = 0, vc = 0;
            Texture level = null;
            if (tex != null)
            {
                ua = p.Uvs[ia * 2]; va = p.Uvs[ia * 2 + 1]; ub = p.Uvs[ib * 2]; vb = p.Uvs[ib * 2 + 1]; uc = p.Uvs[ic * 2]; vc = p.Uvs[ic * 2 + 1];
                Texture top = tex.Level[0];
                double texels = Math.Abs((ub - ua) * (vc - va) - (vb - va) * (uc - ua)) * top.Width * top.Height;
                double ratio = texels / Math.Abs(area);
                int l = ratio > 1 ? (int)(Math.Log(ratio) / Math.Log(4) + 0.3) : 0;
                level = tex.Level[Math.Max(0, Math.Min(tex.Level.Length - 1, l))];
            }
            int baseR = o.Grey >> 16 & 255, baseG = o.Grey >> 8 & 255, baseB = o.Grey & 255;
            if (tex == null && p.Texture == null && (p.R != 1 || p.G != 1 || p.B != 1))
            {
                baseR = Clamp((int)(p.R * 255)); baseG = Clamp((int)(p.G * 255)); baseB = Clamp((int)(p.B * 255));
            }
            float partAlpha = float.IsNaN(p.Alpha) ? 1 : Math.Max(0, Math.Min(1, p.Alpha));
            float inv = 1 / area;
            for (int y = y0; y <= y1; y++)
            {
                float py = y + 0.5f;
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f;
                    float w0 = ((bx - px) * (cy - py) - (by - py) * (cx - px)) * inv;
                    float w1 = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) * inv;
                    float w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    float q = w0 * qa + w1 * qb + w2 * qc;
                    int at = y * f.W + x;
                    if (q <= f.Z[at]) continue;
                    // perspective-correct weights
                    float k0 = w0 * qa / q, k1 = w1 * qb / q, k2 = 1 - k0 - k1;
                    int r = baseR, g = baseG, b = baseB, a = 255;
                    if (level != null)
                    {
                        int c = Sample(level, k0 * ua + k1 * ub + k2 * uc, k0 * va + k1 * vb + k2 * vc);
                        a = c >> 24 & 255; r = c >> 16 & 255; g = c >> 8 & 255; b = c & 255;
                    }
                    if (p.AlphaTest && a < p.AlphaThreshold) continue;
                    if (!blend) a = 255;
                    else
                    {
                        a = (int)(a * partAlpha);
                        if (a <= 0) continue;
                    }
                    float light = k0 * la + k1 * lb + k2 * lc;
                    float shade = 0.6f + 0.5f * (light > 0 ? light : 0) + 0.12f * (light < 0 ? -light : 0);
                    r = Clamp((int)(r * shade)); g = Clamp((int)(g * shade)); b = Clamp((int)(b * shade));
                    if (!blend)
                    {
                        f.Z[at] = q;
                        f.Colour[at] = 255 << 24 | r << 16 | g << 8 | b;
                    }
                    else
                    {
                        int d = f.Colour[at];
                        int da = d >> 24 & 255;
                        int keep = da * (255 - a) / 255;
                        int oa = a + keep;
                        if (oa <= 0) continue;
                        int or = (r * a + (d >> 16 & 255) * keep) / oa, og = (g * a + (d >> 8 & 255) * keep) / oa, ob = (b * a + (d & 255) * keep) / oa;
                        f.Colour[at] = oa << 24 | or << 16 | og << 8 | ob;
                    }
                }
            }
        }

        static int Clamp(int v) { return v < 0 ? 0 : v > 255 ? 255 : v; }

        static int Sample(Texture t, float u, float v)
        {
            if (float.IsNaN(u) || float.IsInfinity(u) || float.IsNaN(v) || float.IsInfinity(v)) return t.Argb[0];
            double fx = (u - Math.Floor(u)) * t.Width - 0.5, fy = (v - Math.Floor(v)) * t.Height - 0.5;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            int tx = (int)((fx - x0) * 256), ty = (int)((fy - y0) * 256);
            int x1 = x0 + 1;
            int y1 = y0 + 1;
            x0 = Wrap(x0, t.Width); x1 = Wrap(x1, t.Width); y0 = Wrap(y0, t.Height); y1 = Wrap(y1, t.Height);
            int p00 = t.Argb[y0 * t.Width + x0], p10 = t.Argb[y0 * t.Width + x1], p01 = t.Argb[y1 * t.Width + x0], p11 = t.Argb[y1 * t.Width + x1];
            int r = 0;
            for (int s = 24; s >= 0; s -= 8)
            {
                int top = (p00 >> s & 255) * (256 - tx) + (p10 >> s & 255) * tx;
                int bot = (p01 >> s & 255) * (256 - tx) + (p11 >> s & 255) * tx;
                r |= ((top * (256 - ty) + bot * ty) >> 16) << s;
            }
            return r;
        }

        static int Wrap(int v, int n)
        {
            v %= n;
            return v < 0 ? v + n : v;
        }

        static int[] Down(Frame f, int width, int height)
        {
            var r = new int[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int a = 0, cr = 0, cg = 0, cb = 0;
                    for (int sy = 0; sy < Ss; sy++)
                        for (int sx = 0; sx < Ss; sx++)
                        {
                            int c = f.Colour[(y * Ss + sy) * f.W + x * Ss + sx];
                            int ca = c >> 24 & 255;
                            a += ca; cr += (c >> 16 & 255) * ca; cg += (c >> 8 & 255) * ca; cb += (c & 255) * ca;
                        }
                    if (a == 0) continue;
                    r[y * width + x] = a / (Ss * Ss) << 24 | cr / a << 16 | cg / a << 8 | cb / a;
                }
            return r;
        }
    }
}
