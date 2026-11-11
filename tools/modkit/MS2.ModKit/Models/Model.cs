// Turns a read .nif file into plain triangles in world space, ready to draw (tools/modkit/design.md).
//
// The scene is walked from its roots with the local transforms multiplied down. A skinned mesh is put in its bind
// pose: each vertex is moved by its bones' matrices, and the bones stand where the file's nodes stand. Nothing here
// throws for a mesh it cannot use; the mesh is left out and a note says why.
using System;
using System.Collections.Generic;

namespace MS2.ModKit.Models
{
    /// <summary>One mesh of a model: world-space vertices, triangles and what to paint them with.</summary>
    public sealed class ModelPart
    {
        public string Name;
        /// <summary>x, y, z per vertex.</summary>
        public float[] Positions;
        /// <summary>x, y, z per vertex, unit length; null when the mesh has none.</summary>
        public float[] Normals;
        /// <summary>u, v per vertex; null when the mesh has none.</summary>
        public float[] Uvs;
        /// <summary>Three vertex numbers per triangle.</summary>
        public int[] Triangles;
        /// <summary>The base texture's file name as the model names it, or null.</summary>
        public string Texture;
        public float R = 1, G = 1, B = 1, Alpha = 1;
        public bool Blend, AlphaTest;
        public int AlphaThreshold;
        /// <summary>True when the vertices were placed by the mesh's bones (the mesh stands where its skeleton stands).</summary>
        public bool Skinned;

        public int VertexCount { get { return Positions.Length / 3; } }
        public int TriangleCount { get { return Triangles.Length / 3; } }
    }

    /// <summary>The drawable content of a model file.</summary>
    public sealed class Model
    {
        public readonly List<ModelPart> Parts = new List<ModelPart>();
        /// <summary>What was left out and why.</summary>
        public readonly List<string> Notes = new List<string>();
        public float[] Min = { 0, 0, 0 }, Max = { 0, 0, 0 };

        public int TriangleCount { get { int n = 0; foreach (ModelPart p in Parts) n += p.TriangleCount; return n; } }
        public int VertexCount { get { int n = 0; foreach (ModelPart p in Parts) n += p.VertexCount; return n; } }
        public bool Empty { get { return TriangleCount == 0; } }

        /// <summary>Adds the parts of another model (an NPC made of several files).</summary>
        public void Add(Model other)
        {
            Parts.AddRange(other.Parts);
            Notes.AddRange(other.Notes);
            Measure();
        }

        /// <summary>
        /// Moves the parts that are not skinned by a 3x4 matrix (see Affine): an item stored in the space of the bone
        /// it hangs on is put where that bone stands. Skinned parts already stand where their skeleton stands.
        /// </summary>
        public void Place(float[] m)
        {
            if (m == null || m.Length < 12) return;
            foreach (ModelPart p in Parts)
            {
                if (p.Skinned) continue;
                for (int i = 0; i + 2 < p.Positions.Length; i += 3)
                    Affine.Point(m, p.Positions[i], p.Positions[i + 1], p.Positions[i + 2], out p.Positions[i], out p.Positions[i + 1], out p.Positions[i + 2]);
                if (p.Normals == null) continue;
                for (int i = 0; i + 2 < p.Normals.Length; i += 3)
                {
                    float x, y, z;
                    Affine.Dir(m, p.Normals[i], p.Normals[i + 1], p.Normals[i + 2], out x, out y, out z);
                    float len = (float)Math.Sqrt(x * x + y * y + z * z);
                    if (len < 1e-12f || float.IsNaN(len)) continue;
                    p.Normals[i] = x / len; p.Normals[i + 1] = y / len; p.Normals[i + 2] = z / len;
                }
            }
            Measure();
        }

        internal void Measure()
        {
            bool any = false;
            foreach (ModelPart p in Parts)
                for (int i = 0; i + 2 < p.Positions.Length; i += 3)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        float v = p.Positions[i + k];
                        if (!any || v < Min[k]) Min[k] = v;
                        if (!any || v > Max[k]) Max[k] = v;
                    }
                    any = true;
                }
        }
    }

    public static class ModelBuilder
    {
        const int MaxDepth = 256;

        // The most one model may hold. Past any of these the rest of the file is left out with a note, so a damaged
        // or hostile file costs a bounded amount of work and memory (the largest shipped model is far below them).
        public const int MaxVertices = 1000000;
        public const int MaxTriangles = 2000000;
        public const int MaxSubmeshes = 256;        // of one mesh
        public const int MaxElements = 256;         // named stream elements of one mesh
        public const int MaxFileBytes = 64 << 20;   // of one model file
        public const string TooLarge = "the model is too large; the rest is left out";

        sealed class State
        {
            public NifTexturing Tex;
            public NifMaterial Mat;
            public NifAlpha Alpha;
        }

        sealed class Walk
        {
            public NifFile File;
            public Model Model = new Model();
            public Dictionary<int, float[]> World = new Dictionary<int, float[]>();
            public List<KeyValuePair<int, State>> Meshes = new List<KeyValuePair<int, State>>();
            public int Vertices, Triangles;     // taken so far
            public bool Full;                   // a limit was passed: nothing more is added
        }

        /// <summary>Reads a model file's bytes into triangles. Throws NifException only for a file that is not readable at all.</summary>
        public static Model Build(byte[] nif)
        {
            if (nif != null && nif.Length > MaxFileBytes) throw new NifException("the model file is too large");
            return Build(NifFile.Read(nif));
        }

        public static Model Build(NifFile file)
        {
            Walk w = WalkScene(file);
            foreach (KeyValuePair<int, State> m in w.Meshes)
            {
                try { AddMesh(w, m.Key, m.Value); }
                catch (NifException e)
                {
                    if (!w.Full) w.Model.Notes.Add("mesh " + ((NifMesh)file.Parsed(m.Key)).Av.Name + " left out: " + e.Message);
                }
                if (w.Full) { w.Model.Notes.Add(TooLarge); break; }
            }
            w.Model.Notes.AddRange(file.Notes);
            w.Model.Measure();
            return w.Model;
        }

        /// <summary>
        /// The nodes of a model file by name with where they stand in the file's space (a 3x4 matrix, see Affine).
        /// A skeleton file gives its bones this way. The first node of a name wins; names are compared without case.
        /// </summary>
        public static Dictionary<string, float[]> Nodes(NifFile file)
        {
            Walk w = WalkScene(file);
            var nodes = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < file.Blocks.Count; i++)
            {
                var node = file.Parsed(i) as NifNode;
                float[] world;
                if (node == null || string.IsNullOrEmpty(node.Av.Name) || !w.World.TryGetValue(i, out world)) continue;
                if (!nodes.ContainsKey(node.Av.Name)) nodes[node.Av.Name] = world;
            }
            return nodes;
        }

        static Walk WalkScene(NifFile file)
        {
            var w = new Walk { File = file };
            int n = file.Blocks.Count;
            var isChild = new bool[n];
            for (int i = 0; i < n; i++)
            {
                var node = file.Parsed(i) as NifNode;
                if (node == null) continue;
                foreach (int c in node.Children) if (c >= 0 && c < n) isChild[c] = true;
            }
            for (int i = 0; i < n; i++)
                if (!isChild[i] && (file.Parsed(i) is NifNode || file.Parsed(i) is NifMesh))
                    Visit(w, i, Affine.Identity, new State(), true, 0);
            return w;
        }

        static State Apply(NifFile f, State s, NifAv av)
        {
            var r = new State { Tex = s.Tex, Mat = s.Mat, Alpha = s.Alpha };
            foreach (int p in av.Properties)
            {
                object o = f.Parsed(p);
                if (o is NifTexturing) r.Tex = (NifTexturing)o;
                else if (o is NifMaterial) r.Mat = (NifMaterial)o;
                else if (o is NifAlpha) r.Alpha = (NifAlpha)o;
            }
            return r;
        }

        static void Visit(Walk w, int index, float[] parent, State state, bool shown, int depth)
        {
            if (depth > MaxDepth || w.World.ContainsKey(index)) return;     // a loop in a damaged file
            object o = w.File.Parsed(index);
            var node = o as NifNode;
            var mesh = o as NifMesh;
            if (node == null && mesh == null) return;
            NifAv av = node != null ? node.Av : mesh.Av;
            float[] world = Affine.Mul(parent, av.Local.Matrix());
            w.World[index] = world;
            shown = shown && !av.Hidden;
            State s = Apply(w.File, state, av);
            if (mesh != null)
            {
                if (shown) w.Meshes.Add(new KeyValuePair<int, State>(index, s));
                return;
            }
            for (int i = 0; i < node.Children.Length; i++)
                Visit(w, node.Children[i], world, s, shown && (node.Shown < 0 || node.Shown == i), depth + 1);
        }

        /// <summary>One named element of a mesh, resolved to its stream for one submesh.</summary>
        sealed class Element
        {
            public byte[] Data;
            public int At, Stride, Count, Components, ComponentBytes, Family;

            public float F(int item, int component) { return StreamFormat.Float(Data, At + item * Stride + component * ComponentBytes, Family); }
            public int I(int item, int component) { return StreamFormat.Int(Data, At + item * Stride + component * ComponentBytes, Family); }
        }

        static Element Find(Walk w, NifMesh m, int submesh, string semantic, int semanticIndex)
        {
            foreach (NifStreamRef r in m.Streams)
            {
                for (int c = 0; c < r.Semantic.Length; c++)
                {
                    if (r.SemanticIndex[c] != semanticIndex || !string.Equals(r.Semantic[c], semantic, StringComparison.Ordinal)) continue;
                    var st = w.File.Parsed(r.Stream) as NifStream;
                    if (st == null || c >= st.Formats.Length || st.Stride <= 0) return null;
                    // a stream without a submesh map has one region, for the mesh's only submesh
                    int region;
                    if (submesh < r.SubmeshToRegion.Length) region = r.SubmeshToRegion[submesh];
                    else if (submesh == 0 && r.SubmeshToRegion.Length == 0) region = 0;
                    else return null;
                    if (region >= st.RegionStart.Length) return null;
                    uint f = st.Formats[c];
                    var e = new Element
                    {
                        Data = w.File.Data, Stride = st.Stride, Count = st.RegionCount[region],
                        Components = StreamFormat.Components(f), ComponentBytes = StreamFormat.ComponentBytes(f), Family = StreamFormat.Family(f),
                    };
                    // the reads step by the component bytes the format states: they must be the width of its type,
                    // and the element must lie inside one stride, or a read would leave the region
                    if (e.Family < 0 || e.Components <= 0 || e.ComponentBytes != StreamFormat.Width(e.Family)) return null;
                    int offset = 0;
                    for (int k = 0; k < c; k++) offset += StreamFormat.Bytes(st.Formats[k]);
                    if (offset < 0 || (long)offset + (long)e.Components * e.ComponentBytes > st.Stride) return null;
                    long start = (long)st.RegionStart[region] * st.Stride;
                    if (st.RegionStart[region] < 0 || e.Count < 0 || start + (long)e.Count * st.Stride > st.DataSize
                        || st.DataOffset < 0 || (long)st.DataOffset + st.DataSize > e.Data.Length)
                        throw new NifException("a stream region runs past its data");
                    e.At = st.DataOffset + (int)start + offset;
                    return e;
                }
            }
            return null;
        }

        static void AddMesh(Walk w, int index, State state)
        {
            var m = (NifMesh)w.File.Parsed(index);
            if (m.Primitive != 0 && m.Primitive != 1) throw new NifException("it is not made of triangles");
            NifSkin skin = null;
            foreach (int mod in m.Modifiers) if (skin == null) skin = w.File.Parsed(mod) as NifSkin;

            if (m.Submeshes > MaxSubmeshes) throw new NifException("it has " + m.Submeshes + " submeshes");
            int elements = 0;
            foreach (NifStreamRef r in m.Streams) elements += r.Semantic.Length;
            if (elements > MaxElements) throw new NifException("it has " + elements + " stream elements");

            for (int sub = 0; sub < Math.Max(1, m.Submeshes); sub++)
            {
                bool skinned = skin != null && Find(w, m, sub, "BLENDINDICES", 0) != null;
                Element pos = skinned ? Find(w, m, sub, "POSITION_BP", 0) ?? Find(w, m, sub, "POSITION", 0)
                                      : Find(w, m, sub, "POSITION", 0) ?? Find(w, m, sub, "POSITION_BP", 0);
                Element nrm = skinned ? Find(w, m, sub, "NORMAL_BP", 0) ?? Find(w, m, sub, "NORMAL", 0)
                                      : Find(w, m, sub, "NORMAL", 0) ?? Find(w, m, sub, "NORMAL_BP", 0);
                if (pos == null || pos.Components < 3) throw new NifException("it has no positions");
                if (nrm != null && (nrm.Components < 3 || nrm.Count < pos.Count)) nrm = null;
                Element uv = Find(w, m, sub, "TEXCOORD", 0);
                if (uv != null && (uv.Components < 2 || uv.Count < pos.Count)) uv = null;
                Element idx = Find(w, m, sub, "INDEX", 0);
                int nv = pos.Count;
                int ni = idx != null ? idx.Count : nv;
                if (nv > MaxVertices - w.Vertices || (m.Primitive == 1 ? ni : ni / 3) > MaxTriangles - w.Triangles)
                {
                    w.Full = true;
                    throw new NifException(TooLarge);
                }
                w.Vertices += nv;

                var part = new ModelPart { Name = m.Av.Name, Positions = new float[nv * 3] };
                if (nrm != null) part.Normals = new float[nv * 3];
                if (uv != null) part.Uvs = new float[nv * 2];

                float[] world = w.World[index];
                float[][] bone = skinned ? BoneMatrices(w, m, sub, skin, world) : null;
                Element bi = bone != null ? Find(w, m, sub, "BLENDINDICES", 0) : null;
                Element bw = bone != null ? Find(w, m, sub, "BLENDWEIGHT", 0) : null;
                if (bi == null || bw == null || bi.Count < nv || bw.Count < nv || bi.Family > 11) bone = null;
                if (skinned && bone == null) w.Model.Notes.Add("mesh " + m.Av.Name + ": drawn without its skin");

                var wt = new float[4];
                for (int v = 0; v < nv; v++)
                {
                    float x = pos.F(v, 0), y = pos.F(v, 1), z = pos.F(v, 2);
                    float nx = 0, ny = 0, nz = 1;
                    if (nrm != null) { nx = nrm.F(v, 0); ny = nrm.F(v, 1); nz = nrm.F(v, 2); }
                    float ox, oy, oz, onx, ony, onz;
                    bool done = false;
                    ox = oy = oz = onx = ony = onz = 0;
                    if (bone != null)
                    {
                        int nw = Math.Min(4, bi.Components);
                        float sum = 0;
                        for (int k = 0; k < nw; k++)
                        {
                            wt[k] = k < bw.Components ? bw.F(v, k) : (k == bw.Components ? Math.Max(0, 1 - sum) : 0);
                            sum += wt[k];
                        }
                        float used = 0;
                        for (int k = 0; k < nw; k++)
                        {
                            int b = bi.I(v, k);
                            if (wt[k] < 1e-6f || float.IsNaN(wt[k]) || (uint)b >= (uint)bone.Length || bone[b] == null) continue;
                            float tx, ty, tz;
                            Affine.Point(bone[b], x, y, z, out tx, out ty, out tz);
                            ox += tx * wt[k]; oy += ty * wt[k]; oz += tz * wt[k];
                            Affine.Dir(bone[b], nx, ny, nz, out tx, out ty, out tz);
                            onx += tx * wt[k]; ony += ty * wt[k]; onz += tz * wt[k];
                            used += wt[k];
                        }
                        if (used > 1e-6f) { ox /= used; oy /= used; oz /= used; done = true; }
                    }
                    if (done) part.Skinned = true;
                    if (!done)
                    {
                        Affine.Point(world, x, y, z, out ox, out oy, out oz);
                        Affine.Dir(world, nx, ny, nz, out onx, out ony, out onz);
                    }
                    part.Positions[v * 3] = ox; part.Positions[v * 3 + 1] = oy; part.Positions[v * 3 + 2] = oz;
                    if (nrm != null)
                    {
                        float len = (float)Math.Sqrt(onx * onx + ony * ony + onz * onz);
                        if (len < 1e-12f || float.IsNaN(len)) { onx = 0; ony = 0; onz = 1; len = 1; }
                        part.Normals[v * 3] = onx / len; part.Normals[v * 3 + 1] = ony / len; part.Normals[v * 3 + 2] = onz / len;
                    }
                    if (uv != null) { part.Uvs[v * 2] = uv.F(v, 0); part.Uvs[v * 2 + 1] = uv.F(v, 1); }
                }

                part.Triangles = Triangles(idx, nv, m.Primitive == 1);
                w.Triangles += part.TriangleCount;
                if (part.Triangles.Length == 0) continue;
                if (state.Tex != null)
                {
                    var src = w.File.Parsed(state.Tex.BaseSource) as NifSourceTexture;
                    if (src != null && !string.IsNullOrEmpty(src.File)) part.Texture = src.File;
                }
                if (state.Mat != null) { part.R = state.Mat.R; part.G = state.Mat.G; part.B = state.Mat.B; part.Alpha = state.Mat.Alpha; }
                if (state.Alpha != null) { part.Blend = state.Alpha.Blend; part.AlphaTest = state.Alpha.Test; part.AlphaThreshold = state.Alpha.Threshold; }
                w.Model.Parts.Add(part);
            }
        }

        // The matrix of each blend index of a submesh: mesh world * (root parent to skin) * inverse(world of the
        // skeleton root) * world of the bone * (skin to bone). Null when the skin cannot be worked out.
        static float[][] BoneMatrices(Walk w, NifMesh m, int sub, NifSkin skin, float[] meshWorld)
        {
            float[] rootWorld;
            if (!w.World.TryGetValue(skin.Root, out rootWorld)) rootWorld = Affine.Identity;
            float[] inv = Affine.Invert(rootWorld);
            if (inv == null) return null;
            float[] toMesh = Affine.Mul(meshWorld, Affine.Mul(skin.RootParentToSkin.Matrix(), inv));
            var perBone = new float[skin.Bones.Length][];
            for (int i = 0; i < perBone.Length; i++)
            {
                float[] bw;
                if (!w.World.TryGetValue(skin.Bones[i], out bw)) continue;
                perBone[i] = Affine.Mul(toMesh, Affine.Mul(bw, skin.SkinToBone[i].Matrix()));
            }
            Element pal = Find(w, m, sub, "BONE_PALETTE", 0);
            if (pal == null || pal.Family > 11) return perBone;
            var r = new float[pal.Count][];
            for (int i = 0; i < r.Length; i++)
            {
                int b = pal.I(i, 0);
                r[i] = (uint)b < (uint)perBone.Length ? perBone[b] : null;
            }
            return r;
        }

        static int[] Triangles(Element idx, int vertices, bool strip)
        {
            int n = idx != null ? idx.Count : vertices;
            if (idx != null && idx.Family > 11) throw new NifException("its index format is not read");
            var list = new List<int>(strip ? n * 3 : n);
            if (!strip)
            {
                for (int i = 0; i + 2 < n; i += 3)
                    Tri(list, At(idx, i), At(idx, i + 1), At(idx, i + 2), vertices);
            }
            else
            {
                for (int i = 0; i + 2 < n; i++)
                {
                    int a = At(idx, i), b = At(idx, i + 1), c = At(idx, i + 2);
                    if ((i & 1) != 0) Tri(list, b, a, c, vertices); else Tri(list, a, b, c, vertices);
                }
            }
            return list.ToArray();
        }

        static int At(Element idx, int i) { return idx != null ? idx.I(i, 0) : i; }

        static void Tri(List<int> list, int a, int b, int c, int vertices)
        {
            if (a == b || b == c || a == c) return;
            if ((uint)a >= (uint)vertices || (uint)b >= (uint)vertices || (uint)c >= (uint)vertices) return;
            list.Add(a); list.Add(b); list.Add(c);
        }
    }
}
