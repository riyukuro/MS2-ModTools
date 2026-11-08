using System.IO.Compression;
using System.Text;
using Maple2.File.IO;
using Maple2.File.IO.Crypto;
using Maple2.File.IO.Crypto.Common;
using Maple2.File.IO.Crypto.Keys;
using Maple2.File.IO.Crypto.Stream;

// Repacker for MS2 .m2d/.m2h archives (all four pack versions).
//
//   m2d repack <orig.m2d> <srcdir> <out.m2d> [--add] [--force-all]
//
// Every entry of <orig> is carried over.  If <srcdir>/<entry name> exists and its bytes differ from the
// archived bytes, the entry is re-encoded (zlib + AES-CTR(base64) with the entry's original flags);
// untouched entries are copied raw (no re-compression).  --add also adds files under srcdir that are not
// in the archive.  The writer lives here because Maple2.File.IO only ships the reader half
// (M2dReader + CryptoManager.Encrypt/Encode primitives).
//
// Note: CryptoManager.Encrypt in the NuGet package AES-transforms only `uncompressedSize` bytes of the
// compressed buffer, which corrupts entries whose zlib stream is longer than the input (tiny files).
// We do our own Encode() below with the full compressed length.
static class Repack {
    public static int Run(string[] a) {
        if (a.Length < 4) { Console.Error.WriteLine("usage: m2d repack <orig.m2d> <srcdir> <out.m2d> [--add] [--force-all]"); return 2; }
        string orig = a[1], srcdir = a[2], outm2d = a[3];
        bool add = a.Contains("--add"), forceAll = a.Contains("--force-all");
        string outm2h = Path.ChangeExtension(outm2d, ".m2h");
        string origm2h = Path.ChangeExtension(orig, ".m2h");

        using var reader = new M2dReader(orig);
        // stream header (version, flags) from the original .m2h
        IPackStream stream;
        using (var br = new BinaryReader(File.OpenRead(origm2h))) stream = IPackStream.CreateStream(br);
        var version = stream.Version;

        var entries = reader.Files.OrderBy(e => e.Index).ToList();
        var known = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        var newFiles = new List<string>();
        if (add && Directory.Exists(srcdir))
            foreach (var f in Directory.EnumerateFiles(srcdir, "*", SearchOption.AllDirectories)) {
                var rel = Path.GetRelativePath(srcdir, f).Replace('\\', '/');
                if (!known.Contains(rel)) newFiles.Add(rel);
            }

        var defaultFlag = entries.Count > 0 ? entries[0].FileHeader.BufferFlag : (Encryption.Aes | Encryption.Zlib);
        int replaced = 0, kept = 0, added = 0;
        using var origFs = new FileStream(orig, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var outFs = new FileStream(outm2d, FileMode.Create, FileAccess.Write);
        var headers = new List<IPackFileHeader>();
        var nameLines = new StringBuilder();
        ulong offset = 0;
        int nextIndex = entries.Count == 0 ? 1 : entries.Max(e => e.Index) + 1;

        void Emit(int index, string hash, string name, Encryption flag, byte[] encoded, uint size, uint sizeCompressed, IPackFileHeader keepFrom) {
            IPackFileHeader h = keepFrom ?? NewHeader(version, index, flag);
            h.FileIndex = index; h.BufferFlag = flag; h.Offset = offset;
            if (keepFrom == null) { h.EncodedFileSize = (uint)encoded.Length; h.CompressedFileSize = sizeCompressed; h.FileSize = size; }
            outFs.Write(encoded, 0, encoded.Length);
            offset += (ulong)encoded.Length;
            headers.Add(h);
            nameLines.Append(string.IsNullOrEmpty(hash) ? $"{index},{name}\r\n" : $"{index},{hash},{name}\r\n");
        }

        foreach (var e in entries) {
            var h = e.FileHeader;
            string path = Path.Combine(srcdir, e.Name.Replace('/', Path.DirectorySeparatorChar));
            bool replace = false; byte[] data = null;
            if (File.Exists(path)) {
                data = File.ReadAllBytes(path);
                replace = forceAll || !data.AsSpan().SequenceEqual(reader.GetBytes(e));
            }
            if (replace) {
                var enc = EncodeEntry(version, data, h.BufferFlag, out uint sz, out uint szc);
                Emit(e.Index, e.Hash, e.Name, h.BufferFlag, enc, sz, szc, null);
                replaced++;
            } else {
                var raw = new byte[h.EncodedFileSize];
                origFs.Seek((long)h.Offset, SeekOrigin.Begin);
                origFs.ReadExactly(raw, 0, raw.Length);
                Emit(e.Index, e.Hash, e.Name, h.BufferFlag, raw, 0, 0, h);   // header object reused, offset patched
                kept++;
            }
        }
        foreach (var rel in newFiles) {
            var data = File.ReadAllBytes(Path.Combine(srcdir, rel.Replace('/', Path.DirectorySeparatorChar)));
            var enc = EncodeEntry(version, data, defaultFlag, out uint sz, out uint szc);
            Emit(nextIndex, null, rel, defaultFlag, enc, sz, szc, null);
            nextIndex++; added++;
        }
        outFs.Flush();

        // file name string + file table, both zlib+AES
        const Encryption hdrFlag = Encryption.Aes | Encryption.Zlib;
        var nameBytes = Encoding.UTF8.GetBytes(nameLines.ToString());
        var encNames = EncodeEntry(version, nameBytes, hdrFlag, out uint nameSize, out uint nameComp);
        byte[] table;
        using (var ms = new MemoryStream()) {
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, true)) foreach (var h in headers) h.Encode(bw);
            table = ms.ToArray();
        }
        var encTable = EncodeEntry(version, table, hdrFlag, out uint tabSize, out uint tabComp);

        stream.FileListCount = (ulong)headers.Count;
        stream.HeaderSize = nameSize; stream.CompressedHeaderSize = nameComp; stream.EncodedHeaderSize = (ulong)encNames.Length;
        stream.DataSize = tabSize; stream.CompressedDataSize = tabComp; stream.EncodedDataSize = (ulong)encTable.Length;
        using (var hf = new FileStream(outm2h, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(hf)) {
            bw.Write((uint)version);
            if (version == PackVersion.NS2F) {
                // NS2F: u32 file count, then six u64 sizes in the order PackStreamVer2's reader takes them
                // (its Encode writes a u64 count, which shifts every field and makes the archive unreadable)
                bw.Write((uint)stream.FileListCount);
                bw.Write(stream.CompressedDataSize); bw.Write(stream.EncodedDataSize); bw.Write(stream.HeaderSize);
                bw.Write(stream.CompressedHeaderSize); bw.Write(stream.EncodedHeaderSize); bw.Write(stream.DataSize);
            } else stream.Encode(bw);
            bw.Write(encNames);
            bw.Write(encTable);
        }
        Console.WriteLine($"wrote {outm2d} ({offset} bytes data) + .m2h: {kept} kept, {replaced} replaced, {added} added");
        return 0;
    }

    static IPackFileHeader NewHeader(PackVersion v, int index, Encryption flag) => v switch {
        PackVersion.MS2F => PackFileHeaderVer1.CreateHeader(index, flag, 0, Array.Empty<byte>()),
        PackVersion.NS2F => PackFileHeaderVer2.CreateHeader(index, flag, 0, Array.Empty<byte>()),
        _ => PackFileHeaderVer3.CreateHeader(v, index, flag, 0, Array.Empty<byte>()),
    };

    // == CryptoManager.Encrypt with the AES length bug fixed (transform the whole compressed buffer).
    public static byte[] EncodeEntry(PackVersion version, byte[] src, Encryption flag, out uint size, out uint sizeCompressed) {
        byte[] buf = flag.HasFlag(Encryption.Zlib) ? Zlib(src) : (byte[])src.Clone();
        size = (uint)src.Length;
        sizeCompressed = (uint)buf.Length;
        if (flag.HasFlag(Encryption.Aes)) {
            CipherKeys.GetKeyAndIV(version, sizeCompressed, out byte[] key, out byte[] iv);
            new AESCipher(key, iv).TransformBlock(buf, 0, (uint)buf.Length, buf, 0);
            buf = Encoding.UTF8.GetBytes(Convert.ToBase64String(buf));
        } else if (flag.HasFlag(Encryption.Xor)) {
            throw new NotSupportedException("Xor-flag entries are not re-encodable");
        }
        return buf;
    }

    static byte[] Zlib(byte[] src) {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78); ms.WriteByte(0x9C);
        using (var ds = new DeflateStream(ms, CompressionMode.Compress, true)) ds.Write(src, 0, src.Length);
        uint a = 1, b = 0;
        foreach (var x in src) { a = (a + x) % 65521; b = (b + a) % 65521; }
        uint ad = (b << 16) | a;
        ms.WriteByte((byte)(ad >> 24)); ms.WriteByte((byte)(ad >> 16)); ms.WriteByte((byte)(ad >> 8)); ms.WriteByte((byte)ad);
        return ms.ToArray();
    }
}
