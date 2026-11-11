// Reader for .m2d/.m2h archives (MS2F, NS2F, OS2F, PS2F).
// Layout ported from Maple2.File.IO (GPL-3.0-or-later: M2dReader, PackStreamVer1-3, PackFileHeaderVer1-3).
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MS2.ModKit.M2d
{
    /// <summary>One record of the file table plus its line of the name list.</summary>
    public sealed class M2dEntry
    {
        public int Index;               // 1-based, contiguous
        public string Hash;             // optional middle field of the name list line; null if absent
        public string Name;             // '/'-separated
        public uint Flag;
        public ulong Offset;
        public uint EncodedSize;
        public ulong CompressedSize;
        public ulong Size;
        // fields the game leaves unused; kept so an untouched record is written back as it was
        internal byte[] PackingDef = new byte[4];   // MS2F
        internal int Reserved0, Reserved1;          // MS2F: both; OS2F/PS2F: Reserved0

        internal M2dEntry Clone() { var e = (M2dEntry)MemberwiseClone(); e.PackingDef = (byte[])PackingDef.Clone(); return e; }
    }

    /// <summary>The fixed part of a .m2h after the version tag.</summary>
    internal sealed class M2hHeader
    {
        internal PackVersion Version;
        internal uint Reserved;
        internal ulong FileCount, HeaderSize, CompressedHeaderSize, EncodedHeaderSize, DataSize, CompressedDataSize, EncodedDataSize;

        internal static M2hHeader Read(BinaryReader r, string path)
        {
            var h = new M2hHeader();
            uint tag = r.ReadUInt32();
            if (tag < (uint)PackVersion.MS2F || tag > (uint)PackVersion.PS2F)
                throw new ModKitException(path + ": not a .m2h header (tag 0x" + tag.ToString("X8") + ")");
            h.Version = (PackVersion)tag;
            switch (h.Version)
            {
                case PackVersion.MS2F:
                    h.Reserved = r.ReadUInt32();
                    h.CompressedDataSize = r.ReadUInt64(); h.EncodedDataSize = r.ReadUInt64(); h.HeaderSize = r.ReadUInt64();
                    h.CompressedHeaderSize = r.ReadUInt64(); h.EncodedHeaderSize = r.ReadUInt64();
                    h.FileCount = r.ReadUInt64(); h.DataSize = r.ReadUInt64();
                    break;
                case PackVersion.NS2F:
                    h.FileCount = r.ReadUInt32();
                    h.CompressedDataSize = r.ReadUInt64(); h.EncodedDataSize = r.ReadUInt64(); h.HeaderSize = r.ReadUInt64();
                    h.CompressedHeaderSize = r.ReadUInt64(); h.EncodedHeaderSize = r.ReadUInt64(); h.DataSize = r.ReadUInt64();
                    break;
                default:
                    h.FileCount = r.ReadUInt32(); h.Reserved = r.ReadUInt32();
                    h.CompressedDataSize = r.ReadUInt64(); h.EncodedDataSize = r.ReadUInt64();
                    h.CompressedHeaderSize = r.ReadUInt64(); h.EncodedHeaderSize = r.ReadUInt64();
                    h.DataSize = r.ReadUInt64(); h.HeaderSize = r.ReadUInt64();
                    break;
            }
            return h;
        }

        // The field order is the reader's. (Upstream's own Encode writes the NS2F/OS2F/PS2F count as u64, which its
        // reader cannot take back; tools/m2d/Repack.cs corrects that for NS2F the same way.)
        internal void Write(BinaryWriter w)
        {
            w.Write((uint)Version);
            switch (Version)
            {
                case PackVersion.MS2F:
                    w.Write(Reserved);
                    w.Write(CompressedDataSize); w.Write(EncodedDataSize); w.Write(HeaderSize);
                    w.Write(CompressedHeaderSize); w.Write(EncodedHeaderSize);
                    w.Write(FileCount); w.Write(DataSize);
                    break;
                case PackVersion.NS2F:
                    w.Write((uint)FileCount);
                    w.Write(CompressedDataSize); w.Write(EncodedDataSize); w.Write(HeaderSize);
                    w.Write(CompressedHeaderSize); w.Write(EncodedHeaderSize); w.Write(DataSize);
                    break;
                default:
                    w.Write((uint)FileCount); w.Write(Reserved);
                    w.Write(CompressedDataSize); w.Write(EncodedDataSize);
                    w.Write(CompressedHeaderSize); w.Write(EncodedHeaderSize);
                    w.Write(DataSize); w.Write(HeaderSize);
                    break;
            }
        }
    }

    internal static class M2dTable
    {
        internal static void ReadRecord(PackVersion v, BinaryReader r, M2dEntry e)
        {
            switch (v)
            {
                case PackVersion.MS2F:
                    e.PackingDef = r.ReadBytes(4);
                    e.Index = r.ReadInt32(); e.Flag = r.ReadUInt32(); e.Reserved0 = r.ReadInt32();
                    e.Offset = r.ReadUInt64(); e.EncodedSize = r.ReadUInt32(); e.Reserved1 = r.ReadInt32();
                    e.CompressedSize = r.ReadUInt64(); e.Size = r.ReadUInt64();
                    break;
                case PackVersion.NS2F:
                    e.Flag = r.ReadUInt32(); e.Index = r.ReadInt32(); e.EncodedSize = r.ReadUInt32();
                    e.CompressedSize = r.ReadUInt64(); e.Size = r.ReadUInt64(); e.Offset = r.ReadUInt64();
                    break;
                default:
                    e.Flag = r.ReadUInt32(); e.Index = r.ReadInt32(); e.EncodedSize = r.ReadUInt32(); e.Reserved0 = r.ReadInt32();
                    e.CompressedSize = r.ReadUInt64(); e.Size = r.ReadUInt64(); e.Offset = r.ReadUInt64();
                    break;
            }
        }

        internal static void WriteRecord(PackVersion v, BinaryWriter w, M2dEntry e)
        {
            switch (v)
            {
                case PackVersion.MS2F:
                    w.Write(e.PackingDef);
                    w.Write(e.Index); w.Write(e.Flag); w.Write(e.Reserved0);
                    w.Write(e.Offset); w.Write(e.EncodedSize); w.Write(e.Reserved1);
                    w.Write(e.CompressedSize); w.Write(e.Size);
                    break;
                case PackVersion.NS2F:
                    w.Write(e.Flag); w.Write(e.Index); w.Write(e.EncodedSize);
                    w.Write(e.CompressedSize); w.Write(e.Size); w.Write(e.Offset);
                    break;
                default:
                    w.Write(e.Flag); w.Write(e.Index); w.Write(e.EncodedSize); w.Write(e.Reserved0);
                    w.Write(e.CompressedSize); w.Write(e.Size); w.Write(e.Offset);
                    break;
            }
        }

        internal static int RecordSize(PackVersion v)
        {
            return v == PackVersion.MS2F ? 48 : v == PackVersion.NS2F ? 36 : 40;
        }
    }

    /// <summary>An open archive. Entries are read from the .m2d by seek; the file is never loaded whole.</summary>
    public sealed class M2dArchive : IDisposable
    {
        readonly FileStream data;
        readonly object ioLock = new object();
        readonly List<M2dEntry> entries;
        readonly Dictionary<string, M2dEntry> byName = new Dictionary<string, M2dEntry>(StringComparer.Ordinal);
        readonly Dictionary<string, M2dEntry> byNameNoCase = new Dictionary<string, M2dEntry>(StringComparer.OrdinalIgnoreCase);

        public string Path { get; private set; }
        public string HeaderPath { get; private set; }
        public PackVersion Version { get; private set; }
        internal uint HeaderReserved;

        /// <summary>All entries in index order.</summary>
        public IList<M2dEntry> Entries { get { return entries.AsReadOnly(); } }
        public int Count { get { return entries.Count; } }

        public static string HeaderPathOf(string m2dPath) { return System.IO.Path.ChangeExtension(m2dPath, ".m2h"); }

        public static M2dArchive Open(string m2dPath) { return new M2dArchive(m2dPath); }

        M2dArchive(string m2dPath)
        {
            Path = m2dPath;
            HeaderPath = HeaderPathOf(m2dPath);
            if (!File.Exists(m2dPath)) throw new ModKitException("archive not found: " + m2dPath);
            if (!File.Exists(HeaderPath)) throw new ModKitException("archive header not found: " + HeaderPath);

            M2hHeader h;
            byte[] names, table;
            try
            {
                byte[] hb;
                using (var hf = new FileStream(HeaderPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    hb = ReadExact(hf, hf.Length);
                using (SHA1 sha = SHA1.Create()) headerHash = sha.ComputeHash(hb);
                using (var fs = new MemoryStream(hb, false))
                using (var r = new BinaryReader(fs))
                {
                    h = M2hHeader.Read(r, HeaderPath);
                    names = h.EncodedHeaderSize == 0 ? new byte[0]
                        : M2dCodec.Decode(h.Version, M2dFlag.AesZlib, ReadExact(fs, (long)h.EncodedHeaderSize), h.CompressedHeaderSize, h.HeaderSize);
                    table = h.EncodedDataSize == 0 ? new byte[0]
                        : M2dCodec.Decode(h.Version, M2dFlag.AesZlib, ReadExact(fs, (long)h.EncodedDataSize), h.CompressedDataSize, h.DataSize);
                }
            }
            catch (EndOfStreamException e) { throw new ModKitException(HeaderPath + ": truncated header", e); }
            catch (InvalidDataException e) { throw new ModKitException(HeaderPath + ": damaged header (" + e.Message + ")", e); }

            Version = h.Version;
            HeaderReserved = h.Reserved;
            entries = ParseNames(Encoding.UTF8.GetString(names));
            entries.Sort(delegate (M2dEntry a, M2dEntry b) { return a.Index.CompareTo(b.Index); });
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Index != i + 1)
                    throw new ModKitException(HeaderPath + ": name list indexes are not 1.." + entries.Count + " (found " + entries[i].Index + " at place " + (i + 1) + ")");
            if ((ulong)entries.Count != h.FileCount)
                throw new ModKitException(HeaderPath + ": header says " + h.FileCount + " entries, the name list has " + entries.Count);
            if (table.Length != entries.Count * M2dTable.RecordSize(Version))
                throw new ModKitException(HeaderPath + ": file table is " + table.Length + " bytes, expected " + entries.Count * M2dTable.RecordSize(Version));

            var seen = new bool[entries.Count];
            var rec = new M2dEntry();
            using (var r = new BinaryReader(new MemoryStream(table)))
                for (int i = 0; i < entries.Count; i++)
                {
                    M2dTable.ReadRecord(Version, r, rec);
                    if (rec.Index < 1 || rec.Index > entries.Count || seen[rec.Index - 1])
                        throw new ModKitException(HeaderPath + ": file table record " + i + " has a bad or repeated index " + rec.Index);
                    seen[rec.Index - 1] = true;
                    M2dEntry e = entries[rec.Index - 1];
                    e.Flag = rec.Flag; e.Offset = rec.Offset; e.EncodedSize = rec.EncodedSize;
                    e.CompressedSize = rec.CompressedSize; e.Size = rec.Size;
                    e.PackingDef = rec.PackingDef; e.Reserved0 = rec.Reserved0; e.Reserved1 = rec.Reserved1;
                    rec.PackingDef = new byte[4];
                }
            foreach (M2dEntry e in entries)
            {
                if (!byName.ContainsKey(e.Name)) byName[e.Name] = e;
                if (!byNameNoCase.ContainsKey(e.Name)) byNameNoCase[e.Name] = e;
            }
            data = new FileStream(m2dPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            dataLength = data.Length;
#if NET35
            dataWritten = File.GetLastWriteTimeUtc(m2dPath);
#else
            dataWritten = File.GetLastWriteTimeUtc(data.SafeFileHandle);
#endif
        }

        readonly byte[] headerHash;
        readonly long dataLength;
        readonly DateTime dataWritten;

        /// <summary>
        /// What the archive was at open: the hash of the .m2h bytes read and the size and write time of the .m2d handle
        /// held. A key of what was read (a file swapped after the open does not change it).
        /// </summary>
        public string OpenStamp
        {
            get
            {
                var sb = new StringBuilder();
                foreach (byte b in headerHash) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return sb.Append(':').Append(dataLength.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(':').Append(dataWritten.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToString();
            }
        }

        // "index,name" or "index,hash,name" per line; lines with another comma count are skipped, as upstream does
        static List<M2dEntry> ParseNames(string text)
        {
            var list = new List<M2dEntry>();
            foreach (string line in text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] f = line.Split(',');
                if (f.Length != 2 && f.Length != 3) continue;
                int index;
                if (!int.TryParse(f[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out index))
                    throw new ModKitException("name list: bad index in line \"" + line + "\"");
                list.Add(new M2dEntry { Index = index, Hash = f.Length == 3 ? f[1] : null, Name = f[f.Length - 1] });
            }
            return list;
        }

        internal static byte[] ReadExact(Stream s, long count)
        {
            if (count < 0 || count > int.MaxValue) throw new ModKitException("block of " + count + " bytes is too large");
            var buf = new byte[count];
            int got = 0;
            while (got < buf.Length)
            {
                int n = s.Read(buf, got, buf.Length - got);
                if (n <= 0) throw new EndOfStreamException();
                got += n;
            }
            return buf;
        }

        /// <summary>The entry of that name, or null. Exact match first, then without case.</summary>
        public M2dEntry Find(string name)
        {
            if (name == null) return null;
            name = name.Replace('\\', '/');
            M2dEntry e;
            if (byName.TryGetValue(name, out e)) return e;
            return byNameNoCase.TryGetValue(name, out e) ? e : null;
        }

        /// <summary>The stored (encoded) bytes of an entry.</summary>
        public byte[] ReadRaw(M2dEntry e)
        {
            lock (ioLock)
            {
                try
                {
                    data.Seek((long)e.Offset, SeekOrigin.Begin);
                    return ReadExact(data, e.EncodedSize);
                }
                catch (EndOfStreamException x) { throw new ModKitException(Path + ": entry " + e.Name + " runs past the end of the file", x); }
            }
        }

        /// <summary>The plain bytes of an entry. An entry of size 0 gives an empty array.</summary>
        public byte[] Read(M2dEntry e)
        {
            if (e.EncodedSize == 0 || e.Size == 0) return new byte[0];
            try { return M2dCodec.Decode(Version, e.Flag, ReadRaw(e), e.CompressedSize, e.Size); }
            catch (InvalidDataException x) { throw new ModKitException(Path + ": entry " + e.Name + " does not inflate (" + x.Message + ")", x); }
        }

        public byte[] Read(string name)
        {
            M2dEntry e = Find(name);
            if (e == null) throw new ModKitException(Path + ": no entry " + name);
            return Read(e);
        }

        public void Dispose() { if (data != null) data.Close(); }
    }
}
