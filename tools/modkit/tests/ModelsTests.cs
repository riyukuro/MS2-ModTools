// The model preview on made-up bytes: small .nif files and .dds textures are built here, read, turned into
// triangles and drawn. One [Explicit] test at the end reads the real model archives named by MS2_CLIENT_DATA.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MS2.ModKit;
using MS2.ModKit.M2d;
using MS2.ModKit.Models;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    /// <summary>Builds the bytes of a small .nif file of the NiMesh generation.</summary>
    sealed class FakeNif
    {
        public string Version = "30.2.0.3";
        public byte Endian = 1;
        readonly List<string> types = new List<string>();
        readonly List<byte[]> blocks = new List<byte[]>();
        readonly List<string> strings = new List<string>();

        public const uint Float3 = 0x00030437, Float2 = 0x00020436, UShort1 = 0x00010215, UByte4 = 0x00040108;

        public int Str(string s)
        {
            int i = strings.IndexOf(s);
            if (i < 0) { strings.Add(s); i = strings.Count - 1; }
            return i;
        }

        public int Add(string type, byte[] bytes)
        {
            types.Add(type);
            blocks.Add(bytes);
            return blocks.Count - 1;
        }

        public static byte[] Make(Action<BinaryWriter> write)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            write(w);
            w.Flush();
            return ms.ToArray();
        }

        static void Refs(BinaryWriter w, int[] refs)
        {
            w.Write(refs == null ? 0 : refs.Length);
            if (refs != null) foreach (int r in refs) w.Write(r);
        }

        void Av(BinaryWriter w, string name, ushort flags, float[] translate, float scale, int[] props)
        {
            w.Write(Str(name)); w.Write(0); w.Write(-1);                // name, extra data, controller
            w.Write(flags);
            for (int i = 0; i < 3; i++) w.Write(translate == null ? 0f : translate[i]);
            foreach (float f in new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }) w.Write(f);
            w.Write(scale);
            Refs(w, props);
            w.Write(-1);                                                // collision object
        }

        public int Node(string name, float[] translate, float scale, int[] children, ushort flags = 0, int[] props = null, string type = "NiNode", int shown = -1)
        {
            return Add(type, Make(w =>
            {
                Av(w, name, flags, translate, scale, props);
                Refs(w, children);
                w.Write(0);                                             // effects
                if (type == "NiSwitchNode") { w.Write((ushort)0); w.Write(shown); }
            }));
        }

        /// <summary>A data stream with one region over all of its items.</summary>
        public int Stream(string usage, uint[] formats, int items, byte[] data)
        {
            return Add("NiDataStream\x01" + usage + "\x01" + "24", Make(w =>
            {
                w.Write(data.Length); w.Write(0);
                w.Write(1); w.Write(0); w.Write(items);
                w.Write(formats.Length);
                foreach (uint f in formats) w.Write(f);
                w.Write(data);
                w.Write((byte)1);
            }));
        }

        public int Mesh(string name, int[] streams, string[][] semantics, int[] props = null, int[] modifiers = null, ushort flags = 0, float[] translate = null, int primitive = 0, ushort submeshes = 1, bool map = true)
        {
            return Add("NiMesh", Make(w =>
            {
                Av(w, name, flags, translate, 1, props);
                w.Write(0); w.Write(-1); w.Write((byte)0);              // materials, active, needs update
                w.Write(primitive); w.Write(submeshes); w.Write((byte)0);
                for (int i = 0; i < 4; i++) w.Write(0f);                // bound
                w.Write(streams.Length);
                for (int i = 0; i < streams.Length; i++)
                {
                    w.Write(streams[i]); w.Write((byte)0);
                    if (map) { w.Write((ushort)1); w.Write((ushort)0); } else w.Write((ushort)0);
                    w.Write(semantics[i].Length);
                    foreach (string s in semantics[i]) { w.Write(Str(s)); w.Write(0); }
                }
                Refs(w, modifiers);
            }));
        }

        public int Texturing(int source)
        {
            return Add("NiTexturingProperty", Make(w =>
            {
                w.Write(Str("")); w.Write(0); w.Write(-1);
                w.Write((ushort)0); w.Write(1);
                w.Write((byte)1); w.Write(source);
                w.Write((ushort)0); w.Write((ushort)0); w.Write((byte)0);
                for (int i = 0; i < 6; i++) w.Write((byte)0);
            }));
        }

        public int SourceTexture(string file)
        {
            return Add("NiSourceTexture", Make(w =>
            {
                w.Write(Str("")); w.Write(0); w.Write(-1);
                w.Write((byte)1); w.Write(Str(file)); w.Write(-1);
                w.Write(0); w.Write(0); w.Write(0); w.Write((byte)1); w.Write((byte)0); w.Write((byte)0);
            }));
        }

        public int Alpha(ushort flags, byte threshold)
        {
            return Add("NiAlphaProperty", Make(w => { w.Write(Str("")); w.Write(0); w.Write(-1); w.Write(flags); w.Write(threshold); }));
        }

        public int Skin(int root, int[] bones, float[][] skinToBoneTranslate)
        {
            return Add("NiSkinningMeshModifier", Make(w =>
            {
                w.Write(0); w.Write(0);                                 // submit and complete points
                w.Write((ushort)0);
                w.Write(root);
                foreach (float f in new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1 }) w.Write(f);
                w.Write(bones.Length);
                foreach (int b in bones) w.Write(b);
                for (int i = 0; i < bones.Length; i++)
                {
                    foreach (float f in new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }) w.Write(f);
                    for (int k = 0; k < 3; k++) w.Write(skinToBoneTranslate == null ? 0f : skinToBoneTranslate[i][k]);
                    w.Write(1f);
                }
            }));
        }

        public static byte[] Floats(params float[] v) { return Make(w => { foreach (float f in v) w.Write(f); }); }
        public static byte[] UShorts(params ushort[] v) { return Make(w => { foreach (ushort f in v) w.Write(f); }); }

        public byte[] ToBytes()
        {
            var names = new List<string>();
            foreach (string t in types) if (!names.Contains(t)) names.Add(t);
            uint version = 0;
            foreach (string p in Version.Split('.')) version = version << 8 | uint.Parse(p);
            return Make(w =>
            {
                w.Write(Encoding.ASCII.GetBytes("Gamebryo File Format, Version " + Version + "\n"));
                w.Write(version); w.Write(Endian); w.Write(0);
                w.Write(blocks.Count);
                if (version >= 0x1E000002) w.Write(0);                  // meta data
                w.Write((ushort)names.Count);
                foreach (string n in names) { byte[] b = Encoding.ASCII.GetBytes(n); w.Write(b.Length); w.Write(b); }
                foreach (string t in types) w.Write((ushort)names.IndexOf(t));
                foreach (byte[] b in blocks) w.Write(b.Length);
                w.Write(strings.Count);
                int longest = 0;
                foreach (string s in strings) longest = Math.Max(longest, s.Length);
                w.Write(longest);
                foreach (string s in strings) { byte[] b = Encoding.UTF8.GetBytes(s); w.Write(b.Length); w.Write(b); }
                w.Write(0);                                             // groups
                foreach (byte[] b in blocks) w.Write(b);
            });
        }

        /// <summary>
        /// A square of two triangles in the x-z plane (x and z from -1 to 1, facing -y) under a root node, with
        /// normals, texture coordinates and a base texture, and a block of a made-up type in between.
        /// </summary>
        public static FakeNif Square(string texture, float[] rootTranslate = null, float rootScale = 1, ushort meshFlags = 0)
        {
            var n = new FakeNif();
            int idx = n.Stream("0", new[] { UShort1 }, 6, UShorts(0, 1, 2, 0, 2, 3));
            n.Add("NiMadeUpBlock", new byte[] { 1, 2, 3, 4, 5, 6, 7 });
            int vtx = n.Stream("1", new[] { Float3, Float3, Float2 }, 4, Floats(
                -1, 0, -1, 0, -1, 0, 0, 1,
                1, 0, -1, 0, -1, 0, 1, 1,
                1, 0, 1, 0, -1, 0, 1, 0,
                -1, 0, 1, 0, -1, 0, 0, 0));
            int[] props = null;
            if (texture != null) props = new[] { n.Texturing(n.SourceTexture(texture)) };
            int mesh = n.Mesh("square", new[] { idx, vtx }, new[] { new[] { "INDEX" }, new[] { "POSITION", "NORMAL", "TEXCOORD" } }, props, null, meshFlags);
            n.Node("root", rootTranslate, rootScale, new[] { mesh });
            return n;
        }
    }

    static class FakeDds
    {
        public static byte[] Header(int w, int h, uint pfFlags, string fourCc, int bits, uint r, uint g, uint b, uint a)
        {
            return FakeNif.Make(wr =>
            {
                wr.Write(0x20534444u); wr.Write(124); wr.Write(0x1007); wr.Write(h); wr.Write(w);
                for (int i = 0; i < 14; i++) wr.Write(0);
                wr.Write(32); wr.Write(pfFlags);
                wr.Write(fourCc == null ? 0u : (uint)(fourCc[0] | fourCc[1] << 8 | fourCc[2] << 16 | fourCc[3] << 24));
                wr.Write(bits); wr.Write(r); wr.Write(g); wr.Write(b); wr.Write(a);
                for (int i = 0; i < 5; i++) wr.Write(0);
            });
        }

        public static byte[] Join(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }

        /// <summary>A plain 32-bit texture (blue, green, red, alpha byte order) from 0xAARRGGBB pixels.</summary>
        public static byte[] Bgra(int w, int h, params uint[] argb)
        {
            return Join(Header(w, h, 0x41, null, 32, 0xff0000, 0xff00, 0xff, 0xff000000), FakeNif.Make(wr => { foreach (uint p in argb) wr.Write(p); }));
        }
    }

    [TestFixture]
    public class ModelsTests
    {
        [Test]
        public void VersionLineAndHeader()
        {
            byte[] b = FakeNif.Square("a.dds").ToBytes();
            Assert.AreEqual("30.2.0.3", NifFile.VersionLine(b));
            Assert.IsNull(NifFile.VersionLine(Encoding.ASCII.GetBytes("<?xml version=\"1.0\"?><a/>                         ")));
            Assert.IsNull(NifFile.VersionLine(new byte[0]));
            Assert.IsNull(NifFile.VersionLine(null));
            NifFile f = NifFile.Read(b);
            Assert.AreEqual(0x1E020003u, f.Version);
            Assert.AreEqual(7, f.Blocks.Count);
            Assert.AreEqual("NiMadeUpBlock", f.Blocks[1].Type);
            Assert.AreEqual(7, f.Blocks[1].Size);
        }

        [Test]
        public void FilesTheReaderDoesNotRead()
        {
            Assert.That(Assert.Throws<NifException>(() => NifFile.Read(Encoding.ASCII.GetBytes("not a model file at all, only some text to be long enough"))).Message, Does.Contain("not a Gamebryo model"));
            Assert.That(Assert.Throws<NifException>(() => NifFile.Read(new FakeNif { Version = "20.2.0.7" }.ToBytes())).Message, Does.Contain("older"));
            Assert.That(Assert.Throws<NifException>(() => NifFile.Read(new FakeNif { Endian = 0 }.ToBytes())).Message, Does.Contain("big-endian"));
            // every cut of a good file either reads or says why; it never fails another way
            byte[] good = FakeNif.Square("a.dds").ToBytes();
            for (int n = 0; n < good.Length; n += 3)
            {
                var cut = new byte[n];
                Buffer.BlockCopy(good, 0, cut, 0, n);
                try { ModelBuilder.Build(cut); }
                catch (NifException) { }
            }
        }

        [Test]
        public void SquareBecomesTwoTrianglesInWorldSpace()
        {
            Model m = ModelBuilder.Build(FakeNif.Square("Folder\\Wall_D.dds", new float[] { 10, 0, 0 }, 2).ToBytes());
            CollectionAssert.IsEmpty(m.Notes);
            Assert.AreEqual(1, m.Parts.Count);
            ModelPart p = m.Parts[0];
            Assert.AreEqual("square", p.Name);
            Assert.AreEqual("Folder\\Wall_D.dds", p.Texture);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 0, 2, 3 }, p.Triangles);
            // the root's scale 2 and move by 10 along x
            CollectionAssert.AreEqual(new float[] { 8, 0, -2, 12, 0, -2, 12, 0, 2, 8, 0, 2 }, p.Positions);
            CollectionAssert.AreEqual(new float[] { 0, -1, 0, 0, -1, 0, 0, -1, 0, 0, -1, 0 }, p.Normals);
            CollectionAssert.AreEqual(new float[] { 0, 1, 1, 1, 1, 0, 0, 0 }, p.Uvs);
            CollectionAssert.AreEqual(new float[] { 8, 0, -2 }, m.Min);
            CollectionAssert.AreEqual(new float[] { 12, 0, 2 }, m.Max);
            Assert.IsFalse(p.Blend);
            Assert.IsFalse(p.AlphaTest);
        }

        [Test]
        public void HiddenMeshesAndUnshownSwitchChildrenAreLeftOut()
        {
            Assert.IsTrue(ModelBuilder.Build(FakeNif.Square("a.dds", null, 1, 1).ToBytes()).Empty);

            var n = new FakeNif();
            int idx = n.Stream("0", new[] { FakeNif.UShort1 }, 3, FakeNif.UShorts(0, 1, 2));
            int vtx = n.Stream("1", new[] { FakeNif.Float3 }, 3, FakeNif.Floats(0, 0, 0, 1, 0, 0, 0, 0, 1));
            string[][] sem = { new[] { "INDEX" }, new[] { "POSITION" } };
            int a = n.Mesh("first", new[] { idx, vtx }, sem), b = n.Mesh("second", new[] { idx, vtx }, sem, null, null, 0, new float[] { 0, 0, 7 });
            n.Node("switch", null, 1, new[] { a, b }, 0, null, "NiSwitchNode", 1);
            Model m = ModelBuilder.Build(n.ToBytes());
            Assert.AreEqual(1, m.Parts.Count);
            Assert.AreEqual("second", m.Parts[0].Name);
            Assert.AreEqual(7f, m.Min[2]);
            Assert.IsNull(m.Parts[0].Normals);
            Assert.IsNull(m.Parts[0].Uvs);
            Assert.IsNull(m.Parts[0].Texture);
        }

        [Test]
        public void AlphaPropertyAndInheritedProperties()
        {
            var n = new FakeNif();
            int idx = n.Stream("0", new[] { FakeNif.UShort1 }, 3, FakeNif.UShorts(0, 1, 2));
            int vtx = n.Stream("1", new[] { FakeNif.Float3 }, 3, FakeNif.Floats(0, 0, 0, 1, 0, 0, 0, 0, 1));
            int mesh = n.Mesh("leaf", new[] { idx, vtx }, new[] { new[] { "INDEX" }, new[] { "POSITION" } }, new[] { n.Alpha(0x12ED, 96) });
            int tex = n.Texturing(n.SourceTexture("leaf.dds"));
            n.Node("root", null, 1, new[] { mesh }, 0, new[] { tex });
            ModelPart p = ModelBuilder.Build(n.ToBytes()).Parts[0];
            Assert.IsTrue(p.Blend);
            Assert.IsTrue(p.AlphaTest);
            Assert.AreEqual(96, p.AlphaThreshold);
            Assert.AreEqual("leaf.dds", p.Texture);      // the root's property reaches the mesh under it
        }

        [Test]
        public void SkinnedMeshStandsInItsBindPose()
        {
            // two bones; the bind pose of the mesh is at the origin, bone 1's node stands 5 higher than its
            // skin-to-bone transform undoes, so the vertices of bone 1 rise by 5 and a shared vertex by half
            var n = new FakeNif();
            int idx = n.Stream("0", new[] { FakeNif.UShort1 }, 3, FakeNif.UShorts(0, 1, 2));
            int vtx = n.Stream("1", new[] { FakeNif.Float3, FakeNif.UByte4, FakeNif.Float3 }, 3, FakeNif.Make(w =>
            {
                float[][] pos = { new float[] { 0, 0, 0 }, new float[] { 1, 0, 0 }, new float[] { 0, 0, 1 } };
                byte[][] bi = { new byte[] { 0, 0, 0, 0 }, new byte[] { 1, 0, 0, 0 }, new byte[] { 0, 1, 0, 0 } };
                float[][] bw = { new float[] { 1, 0, 0 }, new float[] { 1, 0, 0 }, new float[] { 0.5f, 0.5f, 0 } };
                for (int i = 0; i < 3; i++)
                {
                    foreach (float f in pos[i]) w.Write(f);
                    w.Write(bi[i]);
                    foreach (float f in bw[i]) w.Write(f);
                }
            }));
            int pal = n.Stream("1", new[] { FakeNif.UShort1 }, 2, FakeNif.UShorts(1, 0));     // blend index 0 is bone 1 of the modifier
            // blocks: 0 index, 1 vertices, 2 palette, 3 skin, 4 mesh, 5 bone a, 6 bone b, 7 root
            int skin = n.Skin(7, new[] { 6, 5 }, new[] { new float[] { 0, 0, -2 }, new float[] { 0, 0, 0 } });
            int mesh = n.Mesh("body", new[] { idx, vtx, pal }, new[] { new[] { "INDEX" }, new[] { "POSITION_BP", "BLENDINDICES", "BLENDWEIGHT" }, new[] { "BONE_PALETTE" } }, null, new[] { skin });
            int boneA = n.Node("bone a", new float[] { 0, 0, 7 }, 1, null);
            int boneB = n.Node("bone b", new float[] { 0, 0, 2 }, 1, null);
            n.Node("root", null, 1, new[] { mesh, boneA, boneB });
            Assert.AreEqual(5, boneA);
            Model m = ModelBuilder.Build(n.ToBytes());
            CollectionAssert.IsEmpty(m.Notes);
            // modifier bone 0 is block 6 (bone b, at 2, bind at 2: no move); bone 1 is block 5 (bone a, at 7, bind at 0: up 7)
            // palette: blend index 0 -> bone 1 (up 7), blend index 1 -> bone 0 (no move)
            CollectionAssert.AreEqual(new float[] { 0, 0, 7, 1, 0, 0, 0, 0, 4.5f }, m.Parts[0].Positions);
        }

        [Test]
        public void MeshThatCannotBeReadIsLeftOutWithANote()
        {
            var n = new FakeNif();
            int idx = n.Stream("0", new[] { FakeNif.UShort1 }, 3, FakeNif.UShorts(0, 1, 2));
            int vtx = n.Stream("1", new[] { FakeNif.Float3 }, 3, FakeNif.Floats(0, 0, 0, 1, 0, 0, 0, 0, 1));
            int good = n.Mesh("good", new[] { idx, vtx }, new[] { new[] { "INDEX" }, new[] { "POSITION" } });
            int noPos = n.Mesh("no positions", new[] { idx }, new[] { new[] { "INDEX" } });
            int lines = n.Mesh("lines", new[] { idx, vtx }, new[] { new[] { "INDEX" }, new[] { "POSITION" } }, null, null, 0, null, 3);
            int broken = n.Add("NiMesh", new byte[] { 1, 2, 3 });
            // a stream whose region is longer than its data
            int shortStream = n.Add("NiDataStream\x01" + "1" + "\x01" + "24", FakeNif.Make(w =>
            {
                w.Write(12); w.Write(0); w.Write(1); w.Write(0); w.Write(50); w.Write(1); w.Write(FakeNif.Float3);
                w.Write(new byte[12]); w.Write((byte)1);
            }));
            int past = n.Mesh("past the end", new[] { idx, shortStream }, new[] { new[] { "INDEX" }, new[] { "POSITION" } });
            n.Node("root", null, 1, new[] { good, noPos, lines, broken, past, 99, -1 });
            Model m = ModelBuilder.Build(n.ToBytes());
            Assert.AreEqual(1, m.Parts.Count);
            Assert.AreEqual("good", m.Parts[0].Name);
            string notes = string.Join("\n", m.Notes.ToArray());
            Assert.That(notes, Does.Contain("mesh no positions left out: it has no positions"));
            Assert.That(notes, Does.Contain("mesh lines left out: it is not made of triangles"));
            Assert.That(notes, Does.Contain("mesh past the end left out: a stream region runs past its data"));
            Assert.That(notes, Does.Contain("NiMesh block " + broken + " not read"));
        }

        static string Notes(Model m) { return string.Join("\n", m.Notes.ToArray()); }

        [Test]
        public void DamagedSubmeshCountsCostNothing()
        {
            var n = new FakeNif();
            int idx = n.Stream("0", new[] { FakeNif.UShort1 }, 3, FakeNif.UShorts(0, 1, 2));
            int vtx = n.Stream("1", new[] { FakeNif.Float3 }, 3, FakeNif.Floats(0, 0, 0, 1, 0, 0, 0, 0, 1));
            string[][] sem = { new[] { "INDEX" }, new[] { "POSITION" } };
            int many = n.Mesh("many", new[] { idx, vtx }, sem, null, null, 0, null, 0, 65535);
            // three submeshes and streams without a submesh map: the one region is drawn once, not once per submesh
            int noMap = n.Mesh("no map", new[] { idx, vtx }, sem, null, null, 0, null, 0, 3, false);
            // more stream elements than a mesh has
            var refs = new int[ModelBuilder.MaxElements + 1];
            var sems = new string[refs.Length][];
            for (int i = 0; i < refs.Length; i++) { refs[i] = vtx; sems[i] = new[] { "POSITION" }; }
            int wide = n.Mesh("wide", refs, sems);
            n.Node("root", null, 1, new[] { many, noMap, wide });
            Model m = ModelBuilder.Build(n.ToBytes());
            Assert.AreEqual(1, m.Parts.Count);
            Assert.AreEqual("no map", m.Parts[0].Name);
            Assert.That(Notes(m), Does.Contain("mesh many left out: it has 65535 submeshes"));
            Assert.That(Notes(m), Does.Contain("mesh wide left out: it has " + refs.Length + " stream elements"));
        }

        [Test]
        public void ModelPastTheVertexLimitStopsWithANote()
        {
            var n = new FakeNif();
            int idx = n.Stream("0", new[] { FakeNif.UShort1 }, 3, FakeNif.UShorts(0, 1, 2));
            int vtx = n.Stream("1", new[] { FakeNif.Float3 }, 3, FakeNif.Floats(0, 0, 0, 1, 0, 0, 0, 0, 1));
            int count = ModelBuilder.MaxVertices;      // with the three of the first mesh: three too many
            int big = n.Stream("1", new[] { FakeNif.Float3 }, count, new byte[count * 12]);
            string[][] sem = { new[] { "INDEX" }, new[] { "POSITION" } };
            int first = n.Mesh("first", new[] { idx, vtx }, sem);
            int second = n.Mesh("second", new[] { idx, big }, sem);
            int third = n.Mesh("third", new[] { idx, vtx }, sem);
            n.Node("root", null, 1, new[] { first, second, third });
            Model m = ModelBuilder.Build(n.ToBytes());
            Assert.AreEqual(1, m.Parts.Count);
            Assert.AreEqual("first", m.Parts[0].Name);
            CollectionAssert.AreEqual(new[] { ModelBuilder.TooLarge }, m.Notes);
            // a file past the size limit is refused before it is read
            Assert.Throws<NifException>(() => ModelBuilder.Build(new byte[ModelBuilder.MaxFileBytes + 1]));
        }

        [Test]
        public void FormatsThatDoNotAddUpAreNotRead()
        {
            const uint NoComponents = 0x00000215;       // a ushort element of no components
            const uint ThinFloat3 = 0x00030137;         // float32 by its type, one byte a component by its size
            var n = new FakeNif();
            int vtx = n.Stream("1", new[] { FakeNif.Float3 }, 3, FakeNif.Floats(0, 0, 0, 1, 0, 0, 0, 0, 1));
            int idx0 = n.Stream("0", new[] { NoComponents, FakeNif.UShort1 }, 3, FakeNif.UShorts(2, 1, 0));
            int thin = n.Stream("1", new[] { ThinFloat3 }, 3, new byte[9]);
            // at the very end of the file: a read by the type's width would leave the array
            int a = n.Mesh("no index components", new[] { idx0, vtx }, new[] { new[] { "INDEX", "OTHER" }, new[] { "POSITION" } });
            int b = n.Mesh("thin", new[] { thin }, new[] { new[] { "POSITION" } });
            n.Node("root", null, 1, new[] { a, b });
            Model m = ModelBuilder.Build(n.ToBytes());
            Assert.AreEqual(1, m.Parts.Count);
            // the index element is not read: the three vertices are one triangle in their own order
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, m.Parts[0].Triangles);
            Assert.That(Notes(m), Does.Contain("mesh thin left out: it has no positions"));

            // the same thin stream as the last bytes of the file
            var z = new FakeNif();
            int root = z.Node("root", null, 1, new[] { 1 });
            z.Mesh("thin", new[] { 2 }, new[] { new[] { "POSITION" } });
            z.Stream("1", new[] { ThinFloat3 }, 3, new byte[9]);
            Assert.AreEqual(0, root);
            Assert.IsTrue(ModelBuilder.Build(z.ToBytes()).Empty);
        }

        [Test]
        public void NegativeIndicesAreNotFollowed()
        {
            const uint Int4 = 0x00040421, Int1 = 0x00010421;     // signed 32 bit
            var n = new FakeNif();
            int idx = n.Stream("0", new[] { Int1 }, 6, FakeNif.Make(w => { foreach (int i in new[] { 0, 1, 2, 0, -1, 2 }) w.Write(i); }));
            int vtx = n.Stream("1", new[] { FakeNif.Float3, Int4, FakeNif.Float3 }, 3, FakeNif.Make(w =>
            {
                float[][] pos = { new float[] { 0, 0, 0 }, new float[] { 1, 0, 0 }, new float[] { 0, 0, 1 } };
                int[][] bi = { new[] { -1, 0, 0, 0 }, new[] { 1, 0, 0, 0 }, new[] { int.MinValue, 0, 0, 0 } };
                for (int i = 0; i < 3; i++)
                {
                    foreach (float f in pos[i]) w.Write(f);
                    foreach (int k in bi[i]) w.Write(k);
                    w.Write(1f); w.Write(0f); w.Write(0f);
                }
            }));
            int pal = n.Stream("1", new[] { Int1 }, 2, FakeNif.Make(w => { w.Write(0); w.Write(-7); }));
            // blocks: 0 index, 1 vertices, 2 palette, 3 skin, 4 mesh, 5 bone, 6 root
            int skin = n.Skin(6, new[] { 5 }, new[] { new float[] { 0, 0, 0 } });
            int mesh = n.Mesh("body", new[] { idx, vtx, pal }, new[] { new[] { "INDEX" }, new[] { "POSITION_BP", "BLENDINDICES", "BLENDWEIGHT" }, new[] { "BONE_PALETTE" } }, null, new[] { skin });
            int bone = n.Node("bone", new float[] { 0, 0, 3 }, 1, null);
            n.Node("root", null, 1, new[] { mesh, bone });
            Assert.AreEqual(5, bone);
            Model m = ModelBuilder.Build(n.ToBytes());
            Assert.AreEqual(1, m.Parts.Count);
            // blend index -1 and the lowest int name no bone, palette entry -7 (blend index 1) none either: the
            // vertices stay where the mesh stands; the triangle with index -1 is dropped
            CollectionAssert.AreEqual(new float[] { 0, 0, 0, 1, 0, 0, 0, 0, 1 }, m.Parts[0].Positions);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, m.Parts[0].Triangles);
        }

        [Test]
        public void SceneThatLoopsIsWalkedOnce()
        {
            var n = new FakeNif();
            int idx = n.Stream("0", new[] { FakeNif.UShort1 }, 3, FakeNif.UShorts(0, 1, 2));
            int vtx = n.Stream("1", new[] { FakeNif.Float3 }, 3, FakeNif.Floats(0, 0, 0, 1, 0, 0, 0, 0, 1));
            int mesh = n.Mesh("m", new[] { idx, vtx }, new[] { new[] { "INDEX" }, new[] { "POSITION" } });
            // blocks 3 and 4 name each other and themselves as children; block 5 is the root over them
            n.Node("a", null, 1, new[] { 4, 3, mesh });
            n.Node("b", null, 1, new[] { 3, 4 });
            n.Node("root", null, 1, new[] { 3, 5 });
            Model m = ModelBuilder.Build(n.ToBytes());
            Assert.LessOrEqual(m.Parts.Count, 1);
        }

        [Test]
        public void PlaceMovesWhatIsNotSkinned()
        {
            Model m = ModelBuilder.Build(FakeNif.Square(null).ToBytes());
            // x' = z, y' = -y + 1, z' = x + 80: the turn of a bone that points up, and where it stands
            m.Place(new float[] { 0, 0, 1, 0, 0, -1, 0, 1, 1, 0, 0, 80 });
            CollectionAssert.AreEqual(new float[] { -1, 1, 79, -1, 1, 81, 1, 1, 81, 1, 1, 79 }, m.Parts[0].Positions);
            CollectionAssert.AreEqual(new float[] { 0, 1, 0 }, new[] { m.Parts[0].Normals[0], m.Parts[0].Normals[1], m.Parts[0].Normals[2] });
            CollectionAssert.AreEqual(new float[] { -1, 1, 79 }, m.Min);
            CollectionAssert.AreEqual(new float[] { 1, 1, 81 }, m.Max);
            Assert.IsFalse(m.Parts[0].Skinned);
            m.Place(null);
            Assert.AreEqual(79, m.Parts[0].Positions[2]);
            m.Parts[0].Skinned = true;
            m.Place(new float[] { 1, 0, 0, 5, 0, 1, 0, 5, 0, 0, 1, 5 });
            Assert.AreEqual(79, m.Parts[0].Positions[2]);
        }

        [Test]
        public void NodesOfAFileByName()
        {
            var n = new FakeNif();
            int head = n.Node("Head", new float[] { 0, 0, 10 }, 1, null);
            int twin = n.Node("head", new float[] { 9, 9, 9 }, 1, null);
            n.Node("root", new float[] { 1, 0, 0 }, 2, new[] { head, twin });
            Dictionary<string, float[]> nodes = ModelBuilder.Nodes(NifFile.Read(n.ToBytes()));
            Assert.AreEqual(2, nodes.Count);
            // under a root moved by 1 and scaled by 2; the first of two names wins, without regard to case
            CollectionAssert.AreEqual(new float[] { 2, 0, 0, 1, 0, 2, 0, 0, 0, 0, 2, 20 }, nodes["HEAD"]);
        }

        [Test]
        public void IndicesOutsideTheVerticesAreDropped()
        {
            var n = new FakeNif();
            int idx = n.Stream("0", new[] { FakeNif.UShort1 }, 6, FakeNif.UShorts(0, 1, 2, 0, 1, 9));
            int vtx = n.Stream("1", new[] { FakeNif.Float3 }, 3, FakeNif.Floats(0, 0, 0, 1, 0, 0, 0, 0, 1));
            n.Mesh("m", new[] { idx, vtx }, new[] { new[] { "INDEX" }, new[] { "POSITION" } });
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, ModelBuilder.Build(n.ToBytes()).Parts[0].Triangles);
        }

        [Test]
        public void DdsPlainPixels()
        {
            string why;
            Texture t = Dds.Decode(FakeDds.Bgra(2, 1, 0xFF102030, 0x80FFFFFF), out why);
            Assert.IsNull(why);
            Assert.AreEqual(2, t.Width);
            Assert.AreEqual(1, t.Height);
            Assert.AreEqual(unchecked((int)0xFF102030), t.Argb[0]);
            Assert.AreEqual(unchecked((int)0x80FFFFFF), t.Argb[1]);
            Assert.IsTrue(t.HasAlpha);
            // red, green, blue byte order without alpha
            byte[] rgb = FakeDds.Join(FakeDds.Header(1, 2, 0x40, null, 24, 0xff, 0xff00, 0xff0000, 0), new byte[] { 0x10, 0x20, 0x30, 0xAA, 0xBB, 0xCC });
            t = Dds.Decode(rgb, out why);
            Assert.AreEqual(unchecked((int)0xFF102030), t.Argb[0]);
            Assert.AreEqual(unchecked((int)0xFFAABBCC), t.Argb[1]);
            Assert.IsFalse(t.HasAlpha);
            // 16 bits: 5-6-5
            t = Dds.Decode(FakeDds.Join(FakeDds.Header(1, 1, 0x40, null, 16, 0xf800, 0x07e0, 0x001f, 0), new byte[] { 0x00, 0xf8 }), out why);
            Assert.AreEqual(unchecked((int)0xFFFF0000), t.Argb[0]);
        }

        [Test]
        public void DdsBlocks()
        {
            string why;
            // DXT1: colour 0 red, colour 1 blue, c0 > c1; pixel 0 uses colour 0, pixel 1 colour 1, pixel 2 the 2/3 mix
            byte[] block = { 0x00, 0xF8, 0x1F, 0x00, 0x24, 0x00, 0x00, 0x00 };
            Texture t = Dds.Decode(FakeDds.Join(FakeDds.Header(4, 4, 4, "DXT1", 0, 0, 0, 0, 0), block), out why);
            Assert.IsNull(why);
            Assert.AreEqual(unchecked((int)0xFFFF0000), t.Argb[0]);
            Assert.AreEqual(unchecked((int)0xFF0000FF), t.Argb[1]);
            Assert.AreEqual(unchecked((int)0xFFAA0055), t.Argb[2]);
            Assert.IsFalse(t.HasAlpha);
            // DXT1 with c0 <= c1: index 3 is clear
            byte[] punch = { 0x1F, 0x00, 0x00, 0xF8, 0x03, 0x00, 0x00, 0x00 };
            t = Dds.Decode(FakeDds.Join(FakeDds.Header(4, 4, 4, "DXT1", 0, 0, 0, 0, 0), punch), out why);
            Assert.AreEqual(0, t.Argb[0]);
            Assert.AreEqual(unchecked((int)0xFF0000FF), t.Argb[1]);
            Assert.IsTrue(t.HasAlpha);
            // DXT3: four bits of alpha a pixel, then the colour block
            byte[] dxt3 = FakeDds.Join(new byte[] { 0xF0, 0x08, 0, 0, 0, 0, 0, 0 }, block);
            t = Dds.Decode(FakeDds.Join(FakeDds.Header(4, 4, 4, "DXT3", 0, 0, 0, 0, 0), dxt3), out why);
            Assert.AreEqual(unchecked((int)0x00FF0000), t.Argb[0]);
            Assert.AreEqual(unchecked((int)0xFF0000FF), t.Argb[1]);
            Assert.AreEqual(unchecked((int)0x88AA0055), t.Argb[2]);
            // DXT5: alpha 0 = 255, alpha 1 = 0, pixel 0 uses alpha 1, pixel 1 alpha 0, pixel 2 the first step between
            byte[] dxt5 = FakeDds.Join(new byte[] { 0xFF, 0x00, 0x81, 0x00, 0, 0, 0, 0 }, block);
            t = Dds.Decode(FakeDds.Join(FakeDds.Header(4, 4, 4, "DXT5", 0, 0, 0, 0, 0), dxt5), out why);
            Assert.AreEqual(unchecked((int)0x00FF0000), t.Argb[0]);
            Assert.AreEqual(unchecked((int)0xFF0000FF), t.Argb[1]);
            Assert.AreEqual(unchecked((int)0xDAAA0055), t.Argb[2]);
            // a 5 x 3 picture takes two blocks and keeps only its own pixels
            t = Dds.Decode(FakeDds.Join(FakeDds.Header(5, 3, 4, "DXT1", 0, 0, 0, 0, 0), FakeDds.Join(block, block)), out why);
            Assert.AreEqual(15, t.Argb.Length);
            Assert.AreEqual(unchecked((int)0xFFFF0000), t.Argb[4]);
        }

        [Test]
        public void DdsThatIsNotShown()
        {
            string why;
            Assert.IsNull(Dds.Decode(null, out why));
            Assert.AreEqual("not a DDS texture", why);
            Assert.IsNull(Dds.Decode(new byte[200], out why));
            Assert.AreEqual("not a DDS texture", why);
            Assert.IsNull(Dds.Decode(FakeDds.Join(FakeDds.Header(4, 4, 4, "ATI2", 0, 0, 0, 0, 0), new byte[16]), out why));
            Assert.AreEqual("texture format ATI2 is not shown", why);
            Assert.IsNull(Dds.Decode(FakeDds.Header(8, 8, 4, "DXT1", 0, 0, 0, 0, 0), out why));
            Assert.AreEqual("the texture is cut short", why);
            Assert.IsNull(Dds.Decode(FakeDds.Header(64, 64, 0x41, null, 32, 0xff0000, 0xff00, 0xff, 0xff000000), out why));
            Assert.AreEqual("the texture is cut short", why);
            // a header alone that states the largest picture: refused by its length, before any picture is made
            Assert.IsNull(Dds.Decode(FakeDds.Header(Dds.MaxSide, Dds.MaxSide, 4, "DXT5", 0, 0, 0, 0, 0), out why));
            Assert.AreEqual("the texture is cut short", why);
            Assert.IsNull(Dds.Decode(FakeDds.Header(Dds.MaxSide, Dds.MaxSide, 0x41, null, 32, 0xff0000, 0xff00, 0xff, 0xff000000), out why));
            Assert.AreEqual("the texture is cut short", why);
            Assert.IsNull(Dds.Decode(FakeDds.Header(Dds.MaxSide + 1, 4, 4, "DXT1", 0, 0, 0, 0, 0), out why));
            Assert.That(why, Does.Contain("is not shown"));
            // cut inside the second header
            Assert.IsNull(Dds.Decode(FakeDds.Join(FakeDds.Header(4, 4, 4, "DX10", 0, 0, 0, 0, 0), new byte[8]), out why));
            Assert.AreEqual("the texture is cut short", why);
            Assert.IsNull(Dds.Decode(FakeDds.Header(100000, 4, 4, "DXT1", 0, 0, 0, 0, 0), out why));
            Assert.That(why, Does.Contain("is not shown"));
        }

        static int Alpha(int c) { return c >> 24 & 255; }

        [Test]
        public void DrawsTheSquareWithItsTexture()
        {
            Model m = ModelBuilder.Build(FakeNif.Square("t.dds").ToBytes());
            string why;
            // left half red, right half blue
            Texture tex = Dds.Decode(FakeDds.Bgra(2, 1, 0xFFFF0000, 0xFF0000FF), out why);
            var asked = new List<string>();
            var o = new RenderOptions { Yaw = 0, Pitch = 0, Margin = 0.1f };
            int[] px = ModelRenderer.Render(m, 40, 40, name => { asked.Add(name); return tex; }, o);
            Assert.AreEqual(1600, px.Length);
            CollectionAssert.AreEqual(new[] { "t.dds" }, asked);
            // seen from the front (-y) the square's x runs to the right: red on the left, blue on the right
            int left = px[20 * 40 + 10], right = px[20 * 40 + 30];
            Assert.AreEqual(255, Alpha(left));
            Assert.AreEqual(255, Alpha(right));
            Assert.Greater(left >> 16 & 255, 150);
            Assert.Less(left & 255, 40);
            Assert.Greater(right & 255, 150);
            Assert.Less(right >> 16 & 255, 40);
            // the margin stays clear and the square fills the rest
            Assert.AreEqual(0, px[0]);
            Assert.AreEqual(0, px[20 * 40 + 1]);
            Assert.AreEqual(255, Alpha(px[6 * 40 + 6]));
            Assert.AreEqual(255, Alpha(px[33 * 40 + 33]));
        }

        [Test]
        public void MissingTextureGivesAPlainGreyModel()
        {
            Model m = ModelBuilder.Build(FakeNif.Square("gone.dds").ToBytes());
            var o = new RenderOptions { Yaw = 0, Pitch = 0 };
            foreach (TextureLookup lookup in new TextureLookup[] { null, name => null, name => throw new ModKitException("no such texture") })
            {
                int c = ModelRenderer.Render(m, 32, 32, lookup, o)[16 * 32 + 16];
                Assert.AreEqual(255, Alpha(c));
                Assert.AreEqual(c >> 16 & 255, c >> 8 & 255);
                Assert.AreEqual(c >> 8 & 255, c & 255);
                Assert.Greater(c & 255, 90);
            }
        }

        [Test]
        public void NearerTrianglesCoverFartherOnesAndBlendedOnesMix()
        {
            // two squares facing -y: an opaque one at y = 0 and one in front of it at y = -1
            Func<bool, int> centre = blend =>
            {
                var n = new FakeNif();
                int idx = n.Stream("0", new[] { FakeNif.UShort1 }, 6, FakeNif.UShorts(0, 1, 2, 0, 2, 3));
                int back = n.Stream("1", new[] { FakeNif.Float3, FakeNif.Float2 }, 4, FakeNif.Floats(-1, 0, -1, 0, 0, 1, 0, -1, 0, 0, 1, 0, 1, 0, 0, -1, 0, 1, 0, 0));
                int front = n.Stream("1", new[] { FakeNif.Float3, FakeNif.Float2 }, 4, FakeNif.Floats(-1, -1, -1, 0, 0, 1, -1, -1, 0, 0, 1, -1, 1, 0, 0, -1, -1, 1, 0, 0));
                string[][] sem = { new[] { "INDEX" }, new[] { "POSITION", "TEXCOORD" } };
                // the front mesh comes first in the file, so only the depth test can put it on top
                int a = n.Mesh("front", new[] { idx, front }, sem, blend ? new[] { n.Texturing(n.SourceTexture("front.dds")), n.Alpha(1, 0) } : new[] { n.Texturing(n.SourceTexture("front.dds")) });
                int b = n.Mesh("back", new[] { idx, back }, sem, new[] { n.Texturing(n.SourceTexture("back.dds")) });
                n.Node("root", null, 1, new[] { a, b });
                string why;
                Texture red = Dds.Decode(FakeDds.Bgra(1, 1, 0x80FF0000), out why), blue = Dds.Decode(FakeDds.Bgra(1, 1, 0xFF0000FF), out why);
                return ModelRenderer.Render(ModelBuilder.Build(n.ToBytes()), 32, 32, name => name == "front.dds" ? red : blue, new RenderOptions { Yaw = 0, Pitch = 0 })[16 * 32 + 16];
            };
            int opaque = centre(false), mixed = centre(true);
            Assert.Greater(opaque >> 16 & 255, 120);
            Assert.AreEqual(0, opaque & 255);
            Assert.AreEqual(255, Alpha(mixed));
            Assert.Greater(mixed >> 16 & 255, 50);
            Assert.Greater(mixed & 255, 50);
        }

        [Test]
        public void EmptyAndOddModelsDrawNothingAndDoNotFail()
        {
            Assert.AreEqual(0, ModelRenderer.Render(null, 0, 10, null).Length);
            foreach (int c in ModelRenderer.Render(null, 8, 8, null)) Assert.AreEqual(0, c);
            foreach (int c in ModelRenderer.Render(new Model(), 8, 8, null)) Assert.AreEqual(0, c);
            // one point repeated: no size
            var n = new FakeNif();
            int idx = n.Stream("0", new[] { FakeNif.UShort1 }, 3, FakeNif.UShorts(0, 1, 2));
            int vtx = n.Stream("1", new[] { FakeNif.Float3 }, 3, FakeNif.Floats(1, 1, 1, 1, 1, 1, 1, 1, 1));
            n.Mesh("m", new[] { idx, vtx }, new[] { new[] { "INDEX" }, new[] { "POSITION" } });
            foreach (int c in ModelRenderer.Render(ModelBuilder.Build(n.ToBytes()), 8, 8, null)) Assert.AreEqual(0, c);
            // not-a-number positions
            n = new FakeNif();
            idx = n.Stream("0", new[] { FakeNif.UShort1 }, 3, FakeNif.UShorts(0, 1, 2));
            vtx = n.Stream("1", new[] { FakeNif.Float3 }, 3, FakeNif.Floats(float.NaN, 0, 0, 1, float.PositiveInfinity, 0, 0, 0, 1));
            n.Mesh("m", new[] { idx, vtx }, new[] { new[] { "INDEX" }, new[] { "POSITION" } });
            Assert.AreEqual(64, ModelRenderer.Render(ModelBuilder.Build(n.ToBytes()), 8, 8, null).Length);
        }

        [Test]
        public void NamesAndPaths()
        {
            string a, e;
            Assert.IsTrue(ModelNames.Split("Data/Resource/Model/Item/1/13/01200001_Hat.nif", out a, out e));
            Assert.AreEqual("Item", a);
            Assert.AreEqual("1/13/01200001_Hat.nif", e);
            Assert.IsTrue(ModelNames.Split(".\\data\\resource\\model\\Npc\\02\\x.nif", out a, out e));
            Assert.AreEqual("Npc", a);
            Assert.AreEqual("02/x.nif", e);
            Assert.IsFalse(ModelNames.Split("Data/Resource/Image/item/icon/1.png", out a, out e));
            Assert.IsFalse(ModelNames.Split("Data/Resource/Model/x.nif", out a, out e));
            Assert.IsFalse(ModelNames.Split("Data/Resource/Model/Item/../../x.nif", out a, out e));
            // the archive name comes from a mod's field: a plain file name only
            Assert.IsFalse(ModelNames.Split("Data/Resource/Model/C:x/y.nif", out a, out e));
            Assert.IsFalse(ModelNames.Split("Data/Resource/Model/a*b/y.nif", out a, out e));
            Assert.IsFalse(ModelNames.Split("Data/Resource/Model/a\"b/y.nif", out a, out e));
            Assert.IsFalse(ModelNames.Split("Data/Resource/Model/../y.nif", out a, out e));
            Assert.IsTrue(ModelNames.PlainName("Item"));
            Assert.IsFalse(ModelNames.PlainName(".."));
            Assert.IsFalse(ModelNames.PlainName(null));
            Assert.IsFalse(ModelNames.Split("", out a, out e));
            Assert.IsFalse(ModelNames.Split(null, out a, out e));
            Assert.AreEqual("itemmodel/12.xml", ModelNames.ItemModelEntry(1200001));
            Assert.AreEqual("02900005_Digger.kfm", ModelNames.NpcKfmFile("02900005_Digger"));
            Assert.AreEqual("x.kfm", ModelNames.NpcKfmFile(" Data/Resource/Model/Npc/x.KFM "));
            Assert.IsNull(ModelNames.NpcKfmFile(""));
            Assert.IsNull(ModelNames.NpcKfmFile((string)null));
        }

        static byte[] KfmBytes(string nif)
        {
            return FakeNif.Make(w =>
            {
                w.Write(Encoding.ASCII.GetBytes(";Gamebryo KFM File Version 30.2.0.3b\n"));
                w.Write((byte)1);
                byte[] s = Encoding.ASCII.GetBytes(nif);
                w.Write(s.Length); w.Write(s);
                w.Write(5); w.Write(Encoding.ASCII.GetBytes("Bip01"));
            });
        }

        [Test]
        public void KfmNamesItsNif()
        {
            Assert.AreEqual(".\\02900005_Digger.nif", Kfm.NifPath(KfmBytes(".\\02900005_Digger.nif")));
            Assert.IsNull(Kfm.NifPath(Encoding.ASCII.GetBytes(";Gamebryo KFM File Version 2.2.0.0\nsome text form of the file")));
            Assert.IsNull(Kfm.NifPath(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 30.2.0.3\n and more bytes")));
            byte[] cut = KfmBytes(".\\a.nif");
            Array.Resize(ref cut, cut.Length - 14);
            Assert.IsNull(Kfm.NifPath(cut));
            Assert.IsNull(Kfm.NifPath(null));
        }

        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        const string MapXml = "<fieldmaps version=\"1\"><entity name=\"item\">" +
            "<client archive=\"client:Xml.m2d\" entry=\"itemdata/{id/100000}.xml\" row=\"item\" key=\"id\" under=\"environment\" />" +
            "<server archive=\"server:Xml.m2d\" entry=\"item/*/*/{id}.xml\" under=\"environment\" />" +
            "</entity><entity name=\"npc\">" +
            "<client archive=\"client:Xml.m2d\" entry=\"npcdata/{id/100000}.xml\" row=\"npc\" key=\"id\" under=\"environment\" />" +
            "<server archive=\"server:Xml.m2d\" entry=\"npc/*/*/{id}.xml\" under=\"environment\" />" +
            "<map client=\"model@kfm\" server=\"model@kfm\" />" +
            "</entity></fieldmaps>";

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }
        static KeyValuePair<string, byte[]> B(string name, byte[] bytes) { return new KeyValuePair<string, byte[]>(name, bytes); }

        static string ItemFile(string slots, string install)
        {
            return Head + "<ms2>\n\t<environment feature=\"\" locale=\"\">\n\t\t<property slotMax=\"1\" />\n" + slots +
                "\t\t<install asset=\"" + install + "\" />\n\t\t<drop dropNif=\"\" />\n\t</environment>\n</ms2>\n";
        }

        static string Slot(string name, params string[] assets)
        {
            var sb = new StringBuilder("\t\t\t<slot name=\"" + name + "\">\n");
            for (int i = 0; i < assets.Length; i++)
                sb.Append("\t\t\t\t<asset name=\"" + assets[i] + "\" gender=\"" + i + "\"" + (name == "CP" ? " selfnode=\"CP\" targetnode=\"Head\"" : "") + " />\n");
            return sb.Append("\t\t\t</slot>\n").ToString();
        }

        /// <summary>Made-up data roots with model archives under the Korean client.</summary>
        sealed class FakeModels : IDisposable
        {
            public readonly string Dir;
            public readonly DataSet Data;

            public FakeModels(bool models = true)
            {
                Dir = Path.Combine(Path.GetTempPath(), "modkit-models-" + Guid.NewGuid().ToString("N"));
                string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
                foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);
                M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                {
                    E("itemdata/012.xml", Head + "<ms2>\n" + Row(1200001) + Row(1200002) + Row(1200003) + Row(1200004) + "</ms2>\n"),
                    E("itemmodel/12.xml", Head + "<ms2>\n\t<ItemModel id=\"1200001\">\n\t\t<slots>\n" + Slot("CP", "Data/Resource/Model/Item/0/12/Hat.nif") + "\t\t</slots>\n\t</ItemModel>\n" +
                        "\t<ItemModel id=\"1200002\">\n\t\t<slots>\n" + Slot("CP", "Data/Resource/Model/Item/0/12/ClientOnly.nif") + "\t\t</slots>\n\t</ItemModel>\n</ms2>\n"),
                    E("npcdata/029.xml", Head + "<ms2>\n\t<npc id=\"2900001\">\n\t\t<environment>\n\t\t\t<model kfm=\"02900001_Digger\" />\n\t\t</environment>\n\t</npc>\n" +
                        "\t<npc id=\"2900002\">\n\t\t<environment>\n\t\t\t<model kfm=\"Gone\" />\n\t\t</environment>\n\t</npc>\n" +
                        "\t<npc id=\"2900003\">\n\t\t<environment>\n\t\t\t<model kfm=\"\" />\n\t\t</environment>\n\t</npc>\n</ms2>\n"),
                });
                foreach (string d in new[] { g, s })
                    M2dWriter.Create(Path.Combine(d, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                    {
                        // two slots; the first has an asset for each gender
                        E("item/0/12/01200002.xml", ItemFile("\t\t<slots>\n" + Slot("CL", "Data/Resource/Model/Item/0/12/Top_M.nif", "Data/Resource/Model/Item/0/12/Top_F.nif") +
                            Slot("PA", "Data/Resource/Model/Item/0/12/Legs.nif") + "\t\t</slots>\n", "")),
                        E("item/0/12/01200003.xml", ItemFile("", "Data/Resource/Model/Map/Cube/Crate.nif")),
                        E("item/0/12/01200004.xml", ItemFile("", "")),
                    });
                if (models)
                {
                    string dir = Path.Combine(Path.Combine(k, "Resource"), "Model");
                    Directory.CreateDirectory(dir);
                    string why;
                    Assert.IsNotNull(Dds.Decode(FakeDds.Bgra(1, 1, 0xFF00C000), out why));
                    M2dWriter.Create(Path.Combine(dir, "Item.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                    {
                        B("0/12/hat.nif", FakeNif.Square("Hat_D.dds").ToBytes()),
                        B("0/12/top_m.nif", FakeNif.Square("Missing_D.dds").ToBytes()),
                        B("0/12/top_f.nif", FakeNif.Square("Hat_D.dds", new float[] { 0, 0, 3 }).ToBytes()),
                        B("0/12/legs.nif", FakeNif.Square("Hat_D.dds", new float[] { 0, 0, -2 }).ToBytes()),
                        B("0/12/junk.nif", Encoding.ASCII.GetBytes("this is not a model file, it is only a line of text")),
                        B("0/12/hidden.nif", FakeNif.Square("Hat_D.dds", null, 1, 1).ToBytes()),
                    });
                    M2dWriter.Create(Path.Combine(dir, "Textures.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                    {
                        B("item_cap/hat_d.dds", FakeDds.Bgra(1, 1, 0xFF00C000)),
                        B("item_cap/bad_d.dds", new byte[] { 1, 2, 3 }),
                        B("item_cap/wide_d.dds", FakeDds.Bgra(ModelSource.TextureSide * 2, 1, new uint[ModelSource.TextureSide * 2])),
                    });
                    // the skeletons: the head node stands at 10 in the male one, at 20 in the female one
                    var male = new FakeNif();
                    male.Node("root", null, 1, new[] { male.Node("Head", new float[] { 0, 0, 10 }, 1, null) });
                    var female = new FakeNif();
                    female.Node("root", null, 1, new[] { female.Node("Head", new float[] { 0, 0, 20 }, 1, null) });
                    M2dWriter.Create(Path.Combine(dir, "Character.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                    {
                        B("male/m_body.nif", male.ToBytes()),
                        B("female/f_body.nif", female.ToBytes()),
                    });
                    M2dWriter.Create(Path.Combine(dir, "Npc.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                    {
                        B("02/90/02900001/02900001_digger.kfm", KfmBytes(".\\02900001_Digger.nif")),
                        B("02/90/02900001/02900001_digger.nif", FakeNif.Square("Digger_D.dds").ToBytes()),
                        B("02/90/02900001/digger_d.dds", FakeDds.Bgra(1, 1, 0xFF0000C0)),
                        B("02/90/02900009/lost.kfm", KfmBytes(".\\Nowhere.nif")),
                        B("02/90/02900009/text.kfm", Encoding.ASCII.GetBytes(";Gamebryo KFM File Version 2.2.0.0\ntext")),
                    });
                }
                Data = new DataSet(k, g, s);
            }

            static string Row(long id) { return "\t<item id=\"" + id + "\">\n\t\t<environment>\n\t\t\t<property slotMax=\"1\" />\n\t\t</environment>\n\t</item>\n"; }

            public ItemEditor Items(Mod m) { return new ItemEditor(Data, m) { Maps = FieldMaps.Parse(MapXml) }; }
            public NpcEditor Npcs(Mod m) { return new NpcEditor(Data, m) { Maps = FieldMaps.Parse(MapXml) }; }

            public void Dispose()
            {
                Data.Dispose();
                if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
            }
        }

        static Mod NewMod() { return Mod.Parse("<mod id=\"models.test\" name=\"t\" version=\"1\"/>", "test mod"); }

        static string[] Paths(ModelSource src, EntityDraft d, int gender)
        {
            var list = new List<string>();
            foreach (ItemAsset a in ModelNames.Pick(src.Assets(ModelNames.Query(d)), gender)) list.Add(a.Path);
            return list.ToArray();
        }

        [Test]
        public void ItemAssetsOfADraft()
        {
            const string I = "Data/Resource/Model/Item/0/12/";
            using (var f = new FakeModels())
            using (var src = new ModelSource(f.Data))
            {
                ItemEditor ed = f.Items(NewMod());
                // only the client knows this item: its model list names the slot asset, and the node it hangs on
                ItemDraft hat = ed.Open(1200001);
                CollectionAssert.AreEqual(new[] { I + "Hat.nif" }, Paths(src, hat, 0));
                CollectionAssert.IsEmpty(ModelNames.Query(hat).Own);
                ItemAsset a = src.Assets(ModelNames.Query(hat))[0];
                Assert.AreEqual("Head", a.TargetNode);
                Assert.AreEqual("CP", a.SelfNode);
                Assert.AreEqual(0, a.Gender);
                // one asset for both genders: the same file either way
                CollectionAssert.AreEqual(new[] { I + "Hat.nif" }, Paths(src, hat, 1));
                Assert.IsFalse(ModelNames.HasGenders(src.Assets(ModelNames.Query(hat))));
                // the item's own file wins over the client's list: one asset of each slot, by gender
                ItemDraft d = ed.Open(1200002);
                CollectionAssert.AreEqual(new[] { I + "Top_M.nif", I + "Legs.nif" }, Paths(src, d, 0));
                CollectionAssert.AreEqual(new[] { I + "Top_F.nif", I + "Legs.nif" }, Paths(src, d, 1));
                Assert.IsTrue(ModelNames.HasGenders(src.Assets(ModelNames.Query(d))));
                Assert.IsNull(ModelNames.Query(d).Own[0].TargetNode);
                // an edit of the slot asset shows at once
                bool edited = false;
                foreach (EntitySection s in d.Sections)
                    if (s.Elem.Name == "asset" && d.Get(s, "name").EndsWith("Top_M.nif", StringComparison.Ordinal))
                    {
                        d.Set(s, "name", I + "Hat.nif");
                        edited = true;
                    }
                Assert.IsTrue(edited);
                CollectionAssert.AreEqual(new[] { I + "Hat.nif", I + "Legs.nif" }, Paths(src, d, 0));
                // no slots: the model the item is placed as; nothing named: an empty list
                CollectionAssert.AreEqual(new[] { "Data/Resource/Model/Map/Cube/Crate.nif" }, Paths(src, ed.Open(1200003), 0));
                CollectionAssert.IsEmpty(Paths(src, ed.Open(1200004), 0));
                CollectionAssert.IsEmpty(Paths(src, null, 0));
                CollectionAssert.IsEmpty(src.Assets(null));
            }
        }

        [Test]
        public void PickTakesOneAssetOfEachSlot()
        {
            var all = new List<ItemAsset>
            {
                new ItemAsset { Path = "a", Slot = 0, Gender = 1 },
                new ItemAsset { Path = "b", Slot = 0, Gender = 0 },
                new ItemAsset { Path = "c", Slot = 1 },
                new ItemAsset { Path = "d", Slot = 1, Gender = 1 },
                new ItemAsset { Path = "e", Slot = 2, Gender = 1 },
                new ItemAsset { Path = "c", Slot = 3 },
            };
            Func<int, string> pick = g => { string t = ""; foreach (ItemAsset a in ModelNames.Pick(all, g)) t += a.Path; return t; };
            // the gender's asset; else the one without a gender; else the slot's first; a file is listed once
            Assert.AreEqual("bce", pick(0));
            Assert.AreEqual("adec", pick(1));
            Assert.IsTrue(ModelNames.HasGenders(all));
            CollectionAssert.IsEmpty(ModelNames.Pick(null, 0));
            Assert.IsFalse(ModelNames.HasGenders(null));
        }

        [Test]
        public void WornItemStandsOnItsCharacterNode()
        {
            const string Hat = "Data/Resource/Model/Item/0/12/Hat.nif";
            using (var f = new FakeModels())
            using (var src = new ModelSource(f.Data))
            {
                string why;
                // the square's z runs from -1 to 1; the head node stands at 10 (male) and 20 (female)
                Model m = src.Load(new[] { new ItemAsset { Path = Hat, TargetNode = "Head" } }, 0, out why);
                Assert.AreEqual(9, m.Min[2]);
                Assert.AreEqual(11, m.Max[2]);
                m = src.Load(new[] { new ItemAsset { Path = Hat, TargetNode = " head " } }, 1, out why);
                Assert.AreEqual(19, m.Min[2]);
                // the asset's own gender picks the skeleton
                m = src.Load(new[] { new ItemAsset { Path = Hat, TargetNode = "Head", Gender = 1 } }, 0, out why);
                Assert.AreEqual(19, m.Min[2]);
                // no node named, or one the skeleton has not: as the file stores it
                m = src.Load(new[] { new ItemAsset { Path = Hat }, new ItemAsset { Path = Hat, TargetNode = "Tail" } }, 0, out why);
                Assert.AreEqual(-1, m.Min[2]);
                Assert.AreEqual(1, m.Max[2]);
                Assert.IsNotNull(src.Skeleton(0));
                Assert.AreEqual(20, src.Skeleton(1)["Head"][11]);

                // through the draft: the client's list names the node; the picture says whether the genders differ
                ItemEditor ed = f.Items(NewMod());
                ModelPicture p = src.ItemPicture(ModelNames.Query(ed.Open(1200001)), 0, 24, 24, null);
                Assert.IsNull(p.Message);
                Assert.IsFalse(p.Genders);
                Assert.AreEqual(2, p.Triangles);
                p = src.ItemPicture(ModelNames.Query(ed.Open(1200002)), 1, 24, 24, null);
                Assert.IsNull(p.Message);
                Assert.IsTrue(p.Genders);
                CollectionAssert.IsEmpty(p.Notes);       // the female top has its texture; the male one has not
                p = src.ItemPicture(ModelNames.Query(ed.Open(1200002)), 0, 24, 24, null);
                CollectionAssert.AreEqual(new[] { "no texture Missing_D.dds" }, p.Notes);
                Assert.AreEqual("no model", src.ItemPicture(ModelNames.Query(ed.Open(1200004)), 0, 24, 24, null).Message);
                Assert.AreEqual("no model", src.ItemPicture(null, 0, 24, 24, null).Message);

                // a texture wider than the kept size is halved until it fits
                Texture t = src.ReadTexture("Wide_D.dds", "Item");
                Assert.AreEqual(ModelSource.TextureSide, t.Width);
                Assert.AreEqual(1, t.Height);
            }
            // data without a skeleton: the item is drawn as stored
            using (var f = new FakeModels())
            {
                File.Delete(Path.Combine(Path.Combine(Path.Combine(Path.Combine(f.Dir, "kms"), "Resource"), "Model"), "Character.m2d"));
                using (var src = new ModelSource(f.Data))
                {
                    string why;
                    Model m = src.Load(new[] { new ItemAsset { Path = Hat, TargetNode = "Head" } }, 0, out why);
                    Assert.AreEqual(-1, m.Min[2]);
                    Assert.IsNull(src.Skeleton(0));
                }
            }
        }

        [Test]
        public void PictureOfAnItemAndWhatShowsWhenItCannot()
        {
            using (var f = new FakeModels())
            using (var src = new ModelSource(f.Data))
            {
                Assert.IsTrue(src.Available);
                var o = new RenderOptions { Yaw = 0, Pitch = 0 };
                // found without regard to case; the texture by its file name in a folder of the texture archive
                ModelPicture p = src.Picture(new[] { "Data/Resource/Model/Item/0/12/Hat.nif" }, 24, 24, o);
                Assert.IsNull(p.Message);
                Assert.AreEqual(2, p.Triangles);
                CollectionAssert.IsEmpty(p.Notes);
                int c = p.Argb[12 * 24 + 12];
                Assert.Greater(c >> 8 & 255, 100);
                Assert.AreEqual(0, c >> 16 & 255);
                // a texture that is not there: a grey model and a note
                p = src.Picture(new[] { "Data/Resource/Model/Item/0/12/Top_M.nif" }, 24, 24, o);
                Assert.IsNull(p.Message);
                c = p.Argb[12 * 24 + 12];
                Assert.AreEqual(c >> 16 & 255, c & 255);
                CollectionAssert.AreEqual(new[] { "no texture Missing_D.dds" }, p.Notes);
                // two files are drawn together; one missing file leaves the other
                p = src.Picture(new[] { "Data/Resource/Model/Item/0/12/Hat.nif", "Data/Resource/Model/Item/0/12/Legs.nif" }, 24, 24, o);
                Assert.AreEqual(4, p.Triangles);
                p = src.Picture(new[] { "Data/Resource/Model/Item/0/12/Nope.nif", "Data/Resource/Model/Item/0/12/Legs.nif" }, 24, 24, o);
                Assert.AreEqual(2, p.Triangles);
                CollectionAssert.Contains(p.Notes, "no file Nope.nif");
                // the lines shown in place of a picture
                Assert.AreEqual("no model", src.Picture(new string[0], 24, 24, o).Message);
                Assert.AreEqual("no model", src.Picture(null, 24, 24, o).Message);
                p = src.Picture(new[] { "Data/Resource/Model/Item/0/12/Nope.nif" }, 24, 24, o);
                Assert.IsNull(p.Argb);
                Assert.AreEqual("cannot show: no file Nope.nif", p.Message);
                Assert.AreEqual("cannot show: junk.nif: not a Gamebryo model file", src.Picture(new[] { "Data/Resource/Model/Item/0/12/junk.nif" }, 24, 24, o).Message);
                Assert.AreEqual("cannot show: the model has no triangles", src.Picture(new[] { "Data/Resource/Model/Item/0/12/hidden.nif" }, 24, 24, o).Message);
                Assert.AreEqual("cannot show: no file Crate.nif", src.Picture(new[] { "Data/Resource/Model/Map/Cube/Crate.nif" }, 24, 24, o).Message);
                Assert.IsNull(src.ReadTexture("bad_d.dds", null));
                Assert.IsNull(src.ReadTexture(null, null));
            }
        }

        [Test]
        public void PictureOfAnNpc()
        {
            using (var f = new FakeModels())
            using (var src = new ModelSource(f.Data))
            {
                NpcEditor ed = f.Npcs(NewMod());
                string kfm = ModelNames.NpcKfmFile(ed.Open(2900001));
                Assert.AreEqual("02900001_Digger.kfm", kfm);
                string why;
                Assert.AreEqual("Data/Resource/Model/Npc/02/90/02900001/02900001_digger.nif", src.NpcNif(kfm, out why));
                // the texture is beside the model in the npc archive
                ModelPicture p = src.NpcPicture(kfm, 24, 24, new RenderOptions { Yaw = 0, Pitch = 0 });
                Assert.IsNull(p.Message);
                CollectionAssert.IsEmpty(p.Notes);
                Assert.Greater(p.Argb[12 * 24 + 12] & 255, 100);
                Assert.AreEqual("cannot show: no file Gone.kfm", src.NpcPicture(ModelNames.NpcKfmFile(ed.Open(2900002)), 24, 24, null).Message);
                Assert.AreEqual("no model", src.NpcPicture(ModelNames.NpcKfmFile(ed.Open(2900003)), 24, 24, null).Message);
                Assert.AreEqual("cannot show: no file Nowhere.nif", src.NpcPicture("lost.kfm", 24, 24, null).Message);
                Assert.AreEqual("cannot show: text.kfm names no model file", src.NpcPicture("text.kfm", 24, 24, null).Message);
            }
        }

        [Test]
        public void DataWithoutModelArchives()
        {
            using (var f = new FakeModels(false))
            {
                var src = new ModelSource(f.Data);
                Assert.IsFalse(src.Available);
                Assert.AreEqual("cannot show: the data has no model archives", src.Picture(new[] { "Data/Resource/Model/Item/0/12/Hat.nif" }, 8, 8, null).Message);
                Assert.AreEqual("cannot show: no file x.kfm", src.NpcPicture("x.kfm", 8, 8, null).Message);
                src.Dispose();
                Assert.IsNull(src.ReadGamePath("Data/Resource/Model/Item/0/12/Hat.nif"));
            }
        }

        // The real client archives: every .nif of Item.m2d and Npc.m2d carries the version line the reader is
        // written for and builds without an error, and the first item file of some size and the first NPC draw.
        [Test, Explicit("reads the real model archives; set MS2_CLIENT_DATA")]
        public void RealModelArchives()
        {
            string root = RealData.Client;
            if (string.IsNullOrEmpty(root)) Assert.Ignore("MS2_CLIENT_DATA is not set");
            string dir = Path.Combine(Path.Combine(root, "Resource"), "Model");
            if (!File.Exists(Path.Combine(dir, "Item.m2d"))) Assert.Ignore("no Resource/Model/Item.m2d under MS2_CLIENT_DATA");
            string firstItem = null, firstKfm = null;
            foreach (string name in new[] { "Item", "Npc" })
            {
                var versions = new SortedDictionary<string, int>();
                var failures = new List<string>();
                int files = 0, drawable = 0, noted = 0;
                using (M2dArchive a = M2dArchive.Open(Path.Combine(dir, name + ".m2d")))
                    foreach (M2dEntry e in a.Entries)
                    {
                        if (name == "Npc" && firstKfm == null && e.Name.EndsWith(".kfm", StringComparison.OrdinalIgnoreCase))
                            firstKfm = e.Name.Substring(e.Name.Replace('\\', '/').LastIndexOf('/') + 1);
                        if (!e.Name.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) continue;
                        files++;
                        byte[] b = a.Read(e);
                        string v = NifFile.VersionLine(b) ?? "(none)";
                        int n;
                        versions.TryGetValue(v, out n);
                        versions[v] = n + 1;
                        try
                        {
                            Model m = ModelBuilder.Build(b);
                            if (!m.Empty) drawable++;
                            if (name == "Item" && firstItem == null && m.TriangleCount > 500) firstItem = e.Name.Replace('\\', '/');
                            if (m.Notes.Count > 0) noted++;
                        }
                        catch (NifException x) { failures.Add(e.Name + ": " + x.Message); }
                    }
                var sb = new StringBuilder();
                foreach (KeyValuePair<string, int> kv in versions) sb.Append(kv.Key + " x " + kv.Value + "  ");
                TestContext.Out.WriteLine(name + ".m2d: " + files + " .nif, versions: " + sb + "; with triangles " + drawable + ", with notes " + noted + ", not read " + failures.Count);
                foreach (string s in failures.GetRange(0, Math.Min(10, failures.Count))) TestContext.Out.WriteLine("  " + s);
                Assert.Greater(files, 1000);
                // nearly all are 30.2.0.3; a few item files are 30.1.0.3, which has the same layout
                foreach (string v in versions.Keys) Assert.That(new[] { "30.1.0.3", "30.2.0.3" }, Does.Contain(v));
                Assert.Greater(versions["30.2.0.3"], files * 9 / 10);
                CollectionAssert.IsEmpty(failures);
            }
            using (var data = new DataSet(root, null, null))
            using (var src = new ModelSource(data))
            {
                // the first item file with a fair number of triangles and the first .kfm, whatever they are
                Assert.IsNotNull(firstItem);
                Assert.IsNotNull(firstKfm);
                ModelPicture item = src.Picture(new[] { "Data/Resource/Model/Item/" + firstItem }, 256, 256, null);
                Assert.IsNull(item.Message);
                Assert.Greater(item.Triangles, 500);
                Assert.IsNotNull(src.Skeleton(0));
                Assert.IsNotNull(src.Skeleton(1));
                ModelPicture npc = src.NpcPicture(firstKfm, 256, 256, null);
                Assert.IsNull(npc.Message);
                Assert.Greater(npc.Triangles, 0);
                int covered = 0;
                foreach (int c in npc.Argb) if ((c >> 24 & 255) == 255) covered++;
                Assert.Greater(covered, 256 * 256 / 50);
            }
        }
    }
}
