// A small reader of Gamebryo .nif model files, for the still model preview (tools/modkit/design.md).
//
// It reads the file header and only the block types a still picture needs: the scene nodes and their transforms,
// NiMesh with its data streams, the skinning modifier (for the bind pose), the texturing property with its source
// textures, and the material and alpha properties. Every other block is skipped by the size the header gives, so
// an unknown block never stops the read. The layout was worked out from the files themselves and the public
// niftools format description; nothing here comes from the Gamebryo SDK.
//
// Supported: little-endian files of version 20.5.0.0 and later (the NiMesh generation; the MapleStory 2 client
// ships 30.2.0.3). An older or damaged file gives a NifException with a short reason.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MS2.ModKit.Models
{
    /// <summary>A model file this reader cannot read; the message is a short reason for the user.</summary>
    public sealed class NifException : ModKitException
    {
        public NifException(string message) : base(message) { }
    }

    /// <summary>Bounds-checked little-endian reads over a span of a byte array.</summary>
    internal sealed class ByteSpan
    {
        readonly byte[] b;
        readonly int end;
        public int Pos;

        public ByteSpan(byte[] bytes, int start, int length)
        {
            if (start < 0 || length < 0 || start > bytes.Length - length) throw new NifException("the file is cut short");
            b = bytes; Pos = start; end = start + length;
        }

        public int Left { get { return end - Pos; } }

        void Need(int n) { if (n < 0 || n > end - Pos) throw new NifException("the file is cut short"); }

        public byte U8() { Need(1); return b[Pos++]; }
        public ushort U16() { Need(2); ushort v = (ushort)(b[Pos] | b[Pos + 1] << 8); Pos += 2; return v; }
        public int I32() { Need(4); int v = b[Pos] | b[Pos + 1] << 8 | b[Pos + 2] << 16 | b[Pos + 3] << 24; Pos += 4; return v; }
        public uint U32() { return (uint)I32(); }
        public float F32() { Need(4); float v = BitConverter.ToSingle(b, Pos); Pos += 4; return v; }
        public void Skip(int n) { Need(n); Pos += n; }

        /// <summary>A count that must fit what is left when each item takes at least <paramref name="itemBytes"/> bytes.</summary>
        public int Count(int itemBytes)
        {
            uint n = U32();
            if (n > (uint)(Left / Math.Max(1, itemBytes))) throw new NifException("a count runs past the end of its block");
            return (int)n;
        }

        public int[] Refs() { int n = Count(4); var r = new int[n]; for (int i = 0; i < n; i++) r[i] = I32(); return r; }

        public string SizedString()
        {
            int n = Count(1);
            string s = Encoding.UTF8.GetString(b, Pos, n);
            Pos += n;
            return s;
        }

        public byte[] Bytes(int n) { Need(n); var r = new byte[n]; Buffer.BlockCopy(b, Pos, r, 0, n); Pos += n; return r; }
    }

    /// <summary>Rotation, translation and scale: p' = R * (scale * p) + T, R row-major.</summary>
    public struct NifTransform
    {
        public float[] R;      // 9, row-major
        public float Tx, Ty, Tz, Scale;

        public static NifTransform Identity { get { return new NifTransform { R = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, Scale = 1 }; } }

        internal static NifTransform ReadAv(ByteSpan s)   // NiAVObject order: translation, rotation, scale
        {
            var t = new NifTransform();
            t.Tx = s.F32(); t.Ty = s.F32(); t.Tz = s.F32();
            t.R = new float[9];
            for (int i = 0; i < 9; i++) t.R[i] = s.F32();
            t.Scale = s.F32();
            return t;
        }

        internal static NifTransform ReadPlain(ByteSpan s)   // NiTransform order: rotation, translation, scale
        {
            var t = new NifTransform();
            t.R = new float[9];
            for (int i = 0; i < 9; i++) t.R[i] = s.F32();
            t.Tx = s.F32(); t.Ty = s.F32(); t.Tz = s.F32();
            t.Scale = s.F32();
            return t;
        }

        /// <summary>The 3x4 matrix (12 floats, row-major, last column the translation).</summary>
        public float[] Matrix()
        {
            return new[]
            {
                R[0] * Scale, R[1] * Scale, R[2] * Scale, Tx,
                R[3] * Scale, R[4] * Scale, R[5] * Scale, Ty,
                R[6] * Scale, R[7] * Scale, R[8] * Scale, Tz,
            };
        }
    }

    /// <summary>3x4 affine matrices as float[12], row-major.</summary>
    public static class Affine
    {
        public static readonly float[] Identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 };

        public static float[] Mul(float[] a, float[] b)
        {
            var r = new float[12];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 4; j++)
                    r[i * 4 + j] = a[i * 4] * b[j] + a[i * 4 + 1] * b[4 + j] + a[i * 4 + 2] * b[8 + j];
                r[i * 4 + 3] += a[i * 4 + 3];
            }
            return r;
        }

        /// <summary>The inverse, or null when the matrix has no inverse.</summary>
        public static float[] Invert(float[] m)
        {
            double a = m[0], b = m[1], c = m[2], d = m[4], e = m[5], f = m[6], g = m[8], h = m[9], k = m[10];
            double A = e * k - f * h, B = f * g - d * k, C = d * h - e * g;
            double det = a * A + b * B + c * C;
            if (Math.Abs(det) < 1e-20 || double.IsNaN(det)) return null;
            double id = 1.0 / det;
            var r = new float[12];
            r[0] = (float)(A * id); r[1] = (float)((c * h - b * k) * id); r[2] = (float)((b * f - c * e) * id);
            r[4] = (float)(B * id); r[5] = (float)((a * k - c * g) * id); r[6] = (float)((c * d - a * f) * id);
            r[8] = (float)(C * id); r[9] = (float)((b * g - a * h) * id); r[10] = (float)((a * e - b * d) * id);
            r[3] = -(r[0] * m[3] + r[1] * m[7] + r[2] * m[11]);
            r[7] = -(r[4] * m[3] + r[5] * m[7] + r[6] * m[11]);
            r[11] = -(r[8] * m[3] + r[9] * m[7] + r[10] * m[11]);
            return r;
        }

        public static void Point(float[] m, float x, float y, float z, out float ox, out float oy, out float oz)
        {
            ox = m[0] * x + m[1] * y + m[2] * z + m[3];
            oy = m[4] * x + m[5] * y + m[6] * z + m[7];
            oz = m[8] * x + m[9] * y + m[10] * z + m[11];
        }

        public static void Dir(float[] m, float x, float y, float z, out float ox, out float oy, out float oz)
        {
            ox = m[0] * x + m[1] * y + m[2] * z;
            oy = m[4] * x + m[5] * y + m[6] * z;
            oz = m[8] * x + m[9] * y + m[10] * z;
        }
    }

    /// <summary>One block of the file: its type name and where its bytes are.</summary>
    public sealed class NifBlock
    {
        public int Index;
        public string Type;
        public int Offset, Size;
        internal object Parsed;     // NifNode, NifMesh, NifStream, NifSkin, NifTexturing, NifSourceTexture, NifMaterial, NifAlpha or null
        internal bool Tried;
    }

    internal sealed class NifAv
    {
        public string Name;
        public ushort Flags;
        public NifTransform Local;
        public int[] Properties;
        public bool Hidden { get { return (Flags & 1) != 0; } }
    }

    internal sealed class NifNode
    {
        public NifAv Av;
        public int[] Children;
        public int Shown = -1;      // a switch or level-of-detail node draws this one child; -1: all
    }

    internal sealed class NifStreamRef
    {
        public int Stream;
        public ushort[] SubmeshToRegion;
        public string[] Semantic;
        public int[] SemanticIndex;
    }

    internal sealed class NifMesh
    {
        public NifAv Av;
        public int Primitive;       // 0 triangles, 1 triangle strips
        public int Submeshes;
        public NifStreamRef[] Streams;
        public int[] Modifiers;
    }

    internal sealed class NifStream
    {
        public int[] RegionStart, RegionCount;
        public uint[] Formats;
        public int DataOffset, DataSize;    // in the file's bytes
        public int Stride;
    }

    internal sealed class NifSkin
    {
        public ushort Flags;
        public int Root;
        public NifTransform RootParentToSkin;
        public int[] Bones;
        public NifTransform[] SkinToBone;
    }

    internal sealed class NifTexturing { public int BaseSource = -1; }
    internal sealed class NifSourceTexture { public string File; }
    internal sealed class NifMaterial { public float R = 1, G = 1, B = 1, Alpha = 1; }
    internal sealed class NifAlpha { public bool Blend, Test; public int Threshold; }

    /// <summary>A .nif file: header facts and the blocks. <see cref="ModelBuilder"/> turns it into triangles.</summary>
    public sealed class NifFile
    {
        public const string Magic = "Gamebryo File Format, Version ";

        /// <summary>The version line of the file, e.g. "30.2.0.3".</summary>
        public string VersionText { get; private set; }
        public uint Version { get; private set; }
        public uint UserVersion { get; private set; }
        public IList<NifBlock> Blocks { get { return blocks.AsReadOnly(); } }
        /// <summary>Things the reader had to leave out (a block that did not read, an unknown stream format).</summary>
        public readonly List<string> Notes = new List<string>();

        internal readonly byte[] Data;
        readonly List<NifBlock> blocks = new List<NifBlock>();
        string[] strings = new string[0];

        NifFile(byte[] data) { Data = data; }

        /// <summary>The version of a .nif file from its first line ("30.2.0.3"), or null when the bytes are not a Gamebryo file.</summary>
        public static string VersionLine(byte[] bytes)
        {
            if (bytes == null || bytes.Length < Magic.Length + 2) return null;
            for (int i = 0; i < Magic.Length; i++) if (bytes[i] != Magic[i]) return null;
            int e = Magic.Length;
            while (e < bytes.Length && e < Magic.Length + 24 && bytes[e] != '\n') e++;
            if (e >= bytes.Length || bytes[e] != '\n') return null;
            return Encoding.ASCII.GetString(bytes, Magic.Length, e - Magic.Length).Trim();
        }

        static uint PackVersion(string text)
        {
            string[] p = text.Split('.');
            uint v = 0;
            for (int i = 0; i < 4; i++)
            {
                int n = 0;
                if (i < p.Length && !int.TryParse(p[i], NumberStyles.None, CultureInfo.InvariantCulture, out n)) throw new NifException("the version line \"" + text + "\" is not a version");
                v = v << 8 | (uint)(n & 0xff);
            }
            return v;
        }

        /// <summary>Reads the header and the block table. Throws NifException for a file it cannot read.</summary>
        public static NifFile Read(byte[] bytes)
        {
            string line = VersionLine(bytes);
            if (line == null) throw new NifException("not a Gamebryo model file");
            var f = new NifFile(bytes) { VersionText = line };
            uint fromLine = PackVersion(line);
            var s = new ByteSpan(bytes, 0, bytes.Length);
            s.Skip(Magic.Length);
            while (s.U8() != '\n') { }
            f.Version = s.U32();
            if (f.Version != fromLine) throw new NifException("the header does not match the version line " + line);
            if (f.Version < 0x14050000) throw new NifException("model version " + line + " is older than this reader reads (20.5 and later)");
            if (s.U8() != 1) throw new NifException("big-endian model files are not read");
            f.UserVersion = s.U32();
            int count = s.Count(6);
            if (f.Version >= 0x1E000002) s.Skip(s.Count(1));          // embedded meta data
            int types = s.U16();
            var typeNames = new string[types];
            for (int i = 0; i < types; i++) typeNames[i] = s.SizedString();
            var typeOf = new int[count];
            for (int i = 0; i < count; i++) typeOf[i] = s.U16() & 0x7fff;
            var sizes = new uint[count];
            for (int i = 0; i < count; i++) sizes[i] = s.U32();
            int nstr = s.Count(4);
            s.U32();                                                  // the longest string
            f.strings = new string[nstr];
            for (int i = 0; i < nstr; i++) f.strings[i] = s.SizedString();
            s.Skip(checked(4 * s.Count(4)));                          // groups
            long at = s.Pos;
            for (int i = 0; i < count; i++)
            {
                if (typeOf[i] >= types) throw new NifException("block " + i + " names no type");
                if (sizes[i] > (uint)(bytes.Length - at)) throw new NifException("block " + i + " runs past the end of the file");
                f.blocks.Add(new NifBlock { Index = i, Type = typeNames[typeOf[i]], Offset = (int)at, Size = (int)sizes[i] });
                at += sizes[i];
            }
            return f;
        }

        internal string Str(int index) { return index >= 0 && index < strings.Length ? strings[index] : ""; }

        /// <summary>The type name without the usage and access suffix a data stream type carries.</summary>
        internal static string BaseType(string type)
        {
            int i = type.IndexOf('\x01');
            return i < 0 ? type : type.Substring(0, i);
        }

        static readonly Dictionary<string, int> NodeTypes = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "NiNode", 0 }, { "NiBillboardNode", 0 }, { "NiSortAdjustNode", 0 }, { "NiSwitchNode", 1 }, { "NiLODNode", 2 },
        };

        /// <summary>The parsed form of a block, or null for a type that is not read or a block that did not read.</summary>
        internal object Parsed(int index)
        {
            if (index < 0 || index >= blocks.Count) return null;
            NifBlock b = blocks[index];
            if (b.Tried) return b.Parsed;
            b.Tried = true;
            try
            {
                var s = new ByteSpan(Data, b.Offset, b.Size);
                string type = BaseType(b.Type);
                int kind;
                if (NodeTypes.TryGetValue(type, out kind)) b.Parsed = ReadNode(s, kind);
                else if (type == "NiMesh") b.Parsed = ReadMesh(s);
                else if (type == "NiDataStream") b.Parsed = ReadStream(s);
                else if (type == "NiSkinningMeshModifier") b.Parsed = ReadSkin(s);
                else if (type == "NiTexturingProperty") b.Parsed = ReadTexturing(s);
                else if (type == "NiSourceTexture") b.Parsed = ReadSourceTexture(s);
                else if (type == "NiMaterialProperty") b.Parsed = ReadMaterial(s);
                else if (type == "NiAlphaProperty") b.Parsed = ReadAlpha(s);
                else return null;
                // only these are read to their end; the others stop after the fields the preview uses
                if (s.Left != 0 && (type == "NiNode" || type == "NiMesh" || type == "NiDataStream" || type == "NiSkinningMeshModifier"))
                    Notes.Add(type + " block " + index + ": " + s.Left + " bytes left over");
            }
            catch (NifException e)
            {
                b.Parsed = null;
                Notes.Add(BaseType(b.Type) + " block " + index + " not read: " + e.Message);
            }
            return b.Parsed;
        }

        string ObjectNet(ByteSpan s)
        {
            string name = Str(s.I32());
            s.Refs();       // extra data
            s.I32();        // controller
            return name;
        }

        NifAv ReadAv(ByteSpan s)
        {
            var av = new NifAv();
            av.Name = ObjectNet(s);
            av.Flags = s.U16();
            av.Local = NifTransform.ReadAv(s);
            av.Properties = s.Refs();
            s.I32();        // collision object
            return av;
        }

        NifNode ReadNode(ByteSpan s, int kind)
        {
            var n = new NifNode { Av = ReadAv(s) };
            n.Children = s.Refs();
            s.Refs();       // effects
            if (kind == 1)
            {
                s.U16();
                n.Shown = s.I32();
            }
            else if (kind == 2) n.Shown = 0;    // the nearest level of detail
            return n;
        }

        NifMesh ReadMesh(ByteSpan s)
        {
            var m = new NifMesh { Av = ReadAv(s) };
            int materials = s.Count(8);
            s.Skip(materials * 8);          // names and extra data
            s.I32();                        // active material
            s.U8();                         // material needs update
            m.Primitive = s.I32();
            m.Submeshes = s.U16();
            if (s.U8() != 0) throw new NifException("instanced meshes are not read");
            s.Skip(16);                     // bound
            int n = s.Count(11);
            m.Streams = new NifStreamRef[n];
            for (int i = 0; i < n; i++)
            {
                var r = new NifStreamRef();
                r.Stream = s.I32();
                s.U8();                     // per instance
                int maps = s.U16();
                r.SubmeshToRegion = new ushort[maps];
                for (int k = 0; k < maps; k++) r.SubmeshToRegion[k] = s.U16();
                int comps = s.Count(8);
                r.Semantic = new string[comps];
                r.SemanticIndex = new int[comps];
                for (int k = 0; k < comps; k++) { r.Semantic[k] = Str(s.I32()); r.SemanticIndex[k] = s.I32(); }
                m.Streams[i] = r;
            }
            m.Modifiers = s.Refs();
            return m;
        }

        NifStream ReadStream(ByteSpan s)
        {
            var d = new NifStream();
            int size = s.Count(1);
            s.U32();                        // cloning behaviour
            int regions = s.Count(8);
            d.RegionStart = new int[regions];
            d.RegionCount = new int[regions];
            for (int i = 0; i < regions; i++) { d.RegionStart[i] = s.I32(); d.RegionCount[i] = s.I32(); }
            int comps = s.Count(4);
            d.Formats = new uint[comps];
            for (int i = 0; i < comps; i++)
            {
                d.Formats[i] = s.U32();
                d.Stride += StreamFormat.Bytes(d.Formats[i]);
            }
            d.DataOffset = s.Pos;
            d.DataSize = size;
            s.Skip(size);
            s.U8();                         // streamable
            return d;
        }

        NifSkin ReadSkin(ByteSpan s)
        {
            var k = new NifSkin();
            s.Skip(2 * s.Count(2));         // submit points
            s.Skip(2 * s.Count(2));         // complete points
            k.Flags = s.U16();
            k.Root = s.I32();
            k.RootParentToSkin = NifTransform.ReadPlain(s);
            int n = s.Count(4 + 52);
            k.Bones = new int[n];
            for (int i = 0; i < n; i++) k.Bones[i] = s.I32();
            k.SkinToBone = new NifTransform[n];
            for (int i = 0; i < n; i++) k.SkinToBone[i] = NifTransform.ReadPlain(s);
            if ((k.Flags & 2) != 0) s.Skip(16 * n);     // bone bounds
            return k;
        }

        NifTexturing ReadTexturing(ByteSpan s)
        {
            var t = new NifTexturing();
            ObjectNet(s);
            s.U16();                        // flags
            s.U32();                        // texture count
            if (s.U8() != 0) t.BaseSource = s.I32();
            return t;
        }

        NifSourceTexture ReadSourceTexture(ByteSpan s)
        {
            ObjectNet(s);
            s.U8();                         // external or embedded: the name follows either way
            return new NifSourceTexture { File = Str(s.I32()) };
        }

        NifMaterial ReadMaterial(ByteSpan s)
        {
            ObjectNet(s);
            s.Skip(12);                     // ambient
            var m = new NifMaterial { R = s.F32(), G = s.F32(), B = s.F32() };
            s.Skip(12 + 12 + 4);            // specular, emissive, glossiness
            m.Alpha = s.F32();
            return m;
        }

        NifAlpha ReadAlpha(ByteSpan s)
        {
            ObjectNet(s);
            int flags = s.U16();
            return new NifAlpha { Blend = (flags & 1) != 0, Test = (flags & 0x200) != 0, Threshold = s.U8() };
        }
    }

    /// <summary>The element formats of a data stream: (component count &lt;&lt; 16) | (component bytes &lt;&lt; 8) | type code.</summary>
    internal static class StreamFormat
    {
        public static int Components(uint f) { return (int)(f >> 16 & 0xff); }
        public static int ComponentBytes(uint f) { return (int)(f >> 8 & 0xff); }
        public static int Code(uint f) { return (int)(f & 0xff); }
        public static int Bytes(uint f) { return Code(f) >= 0x39 ? ComponentBytes(f) : Components(f) * ComponentBytes(f); }

        // the type family of a code: 0 int8, 1 uint8, 2 norm int8, 3 norm uint8, 4 int16, 5 uint16, 6 norm int16,
        // 7 norm uint16, 8 int32, 9 uint32, 10 norm int32, 11 norm uint32, 12 float16, 13 float32; -1 packed or unknown
        public static int Family(uint f)
        {
            int c = Code(f);
            return c >= 1 && c <= 0x38 ? (c - 1) / 4 : -1;
        }

        /// <summary>The bytes of one component of a family; 0 for an unknown one.</summary>
        public static int Width(int family)
        {
            if (family < 0 || family > 13) return 0;
            return family < 4 ? 1 : family < 8 || family == 12 ? 2 : 4;
        }

        /// <summary>One component as a float (normalized kinds scaled to -1..1 or 0..1).</summary>
        public static float Float(byte[] d, int at, int family)
        {
            switch (family)
            {
                case 0: return (sbyte)d[at];
                case 1: return d[at];
                case 2: return Math.Max(-1f, (sbyte)d[at] / 127f);
                case 3: return d[at] / 255f;
                case 4: return (short)(d[at] | d[at + 1] << 8);
                case 5: return d[at] | d[at + 1] << 8;
                case 6: return Math.Max(-1f, (short)(d[at] | d[at + 1] << 8) / 32767f);
                case 7: return (d[at] | d[at + 1] << 8) / 65535f;
                case 8: return d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24;
                case 9: return (uint)(d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24);
                case 10: return Math.Max(-1f, (d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24) / 2147483647f);
                case 11: return (uint)(d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24) / 4294967295f;
                case 12: return Half(d[at] | d[at + 1] << 8);
                case 13: return BitConverter.ToSingle(d, at);
            }
            return 0;
        }

        /// <summary>One component as an index (integer kinds only).</summary>
        public static int Int(byte[] d, int at, int family)
        {
            switch (family)
            {
                case 0: case 1: case 2: case 3: return d[at];
                case 4: case 5: case 6: case 7: return d[at] | d[at + 1] << 8;
                case 8: case 9: case 10: case 11: return d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24;
            }
            return 0;
        }

        static float Half(int h)
        {
            int sign = h >> 15 & 1, exp = h >> 10 & 0x1f, man = h & 0x3ff;
            float v;
            if (exp == 0) v = man * (1f / 16777216f);                       // 2^-14 * man / 1024
            else if (exp == 31) v = man == 0 ? float.PositiveInfinity : float.NaN;
            else v = (float)Math.Pow(2, exp - 15) * (1 + man / 1024f);
            return sign != 0 ? -v : v;
        }
    }
}
