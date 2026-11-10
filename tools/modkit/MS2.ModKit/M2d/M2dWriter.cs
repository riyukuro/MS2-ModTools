// Writer for .m2d/.m2h archives. The rules are those of tools/m2d/Repack.cs, so both give the same bytes:
// entries in index order, an untouched entry is copied in its stored form with its table record (offset patched),
// a changed entry is encoded again with its own flag, added entries get the next indexes and no hash.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MS2.ModKit.M2d
{
    /// <summary>What to change while an archive is written again. Names use '/'.</summary>
    public sealed class M2dChanges
    {
        /// <summary>New plain bytes for existing entries, or new entries (appended in the order given here).</summary>
        public readonly List<KeyValuePair<string, byte[]>> Put = new List<KeyValuePair<string, byte[]>>();
        /// <summary>Entries to drop. Later entries get lower indexes.</summary>
        public readonly List<string> Delete = new List<string>();
        /// <summary>Encode again even if the new bytes equal the stored ones.</summary>
        public bool ForceEncode;

        public void Set(string name, byte[] bytes) { Put.Add(new KeyValuePair<string, byte[]>(name.Replace('\\', '/'), bytes)); }
    }

    public sealed class M2dWriteResult
    {
        public int Kept, Replaced, Added, Deleted;
        public long DataBytes;
    }

    public static class M2dWriter
    {
        /// <summary>
        /// Writes a copy of <paramref name="src"/> with the changes to <paramref name="outM2d"/> (+ .m2h).
        /// The files are written as *.tmp and renamed at the end. Writing over the source is refused.
        /// </summary>
        public static M2dWriteResult Rewrite(M2dArchive src, string outM2d, M2dChanges changes, Action<ProgressInfo> progress)
        {
            if (changes == null) changes = new M2dChanges();
            if (SamePath(outM2d, src.Path) || SamePath(M2dArchive.HeaderPathOf(outM2d), src.HeaderPath))
                throw new ModKitException("refusing to write over the source archive " + src.Path);

            var put = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var added = new List<KeyValuePair<string, byte[]>>();
            var addedAt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, byte[]> p in changes.Put)
            {
                if (p.Value == null) throw new ModKitException("no bytes given for entry " + p.Key);
                M2dEntry e = src.Find(p.Key);
                if (e != null) { put[e.Name] = p.Value; continue; }
                CheckName(p.Key);
                int at;
                if (addedAt.TryGetValue(p.Key, out at)) added[at] = p;          // the last one given wins
                else { addedAt[p.Key] = added.Count; added.Add(p); }
            }
            var del = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (string name in changes.Delete)
            {
                M2dEntry e = src.Find(name);
                if (e == null) throw new ModKitException(src.Path + ": cannot delete " + name + ", no such entry");
                if (put.ContainsKey(e.Name)) throw new ModKitException("entry " + e.Name + " is both changed and deleted");
                del[e.Name] = true;
            }

            var res = new M2dWriteResult();
            var w = new Session(src.Version, src.HeaderReserved, outM2d);
            try
            {
                uint defaultFlag = src.Count > 0 ? src.Entries[0].Flag : M2dFlag.AesZlib;
                long total = src.Count + added.Count, done = 0;
                foreach (M2dEntry e in src.Entries)
                {
                    if ((++done & 0x3FF) == 0) ProgressUtil.Report(progress, "write", e.Name, done, total);
                    if (del.ContainsKey(e.Name)) { res.Deleted++; continue; }
                    byte[] bytes;
                    if (put.TryGetValue(e.Name, out bytes) && (changes.ForceEncode || !BytesEqual(bytes, src.Read(e))))
                    {
                        w.AddPlain(e.Hash, e.Name, e.Flag, bytes);
                        res.Replaced++;
                    }
                    else
                    {
                        w.AddRaw(e, src.ReadRaw(e));
                        res.Kept++;
                    }
                }
                foreach (KeyValuePair<string, byte[]> p in added)
                {
                    w.AddPlain(null, p.Key, defaultFlag, p.Value);
                    res.Added++;
                }
                res.DataBytes = w.Finish();
                ProgressUtil.Report(progress, "write", "", total, total);
            }
            catch { w.Abort(); throw; }
            return res;
        }

        /// <summary>Writes a new archive from plain entries (index order = list order), all with one flag.</summary>
        public static void Create(string outM2d, PackVersion version, uint flag, IList<KeyValuePair<string, byte[]>> entries)
        {
            var w = new Session(version, 0, outM2d);
            try
            {
                foreach (KeyValuePair<string, byte[]> p in entries) { CheckName(p.Key); w.AddPlain(null, p.Key, flag, p.Value); }
                w.Finish();
            }
            catch { w.Abort(); throw; }
        }

        static void CheckName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf(',') >= 0 || name.IndexOf('\r') >= 0 || name.IndexOf('\n') >= 0 || name.IndexOf('\\') >= 0)
                throw new ModKitException("bad entry name \"" + name + "\" (empty, or holds a comma, backslash or line break)");
        }

        internal static bool SamePath(string a, string b)
        {
            return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        internal static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        sealed class Session
        {
            readonly PackVersion version;
            readonly uint reserved;
            readonly string outM2d, outM2h, tmpM2d, tmpM2h;
            readonly FileStream fs;
            readonly List<M2dEntry> records = new List<M2dEntry>();
            readonly StringBuilder names = new StringBuilder();
            ulong offset;

            internal Session(PackVersion v, uint headerReserved, string m2d)
            {
                version = v; reserved = headerReserved;
                outM2d = m2d; outM2h = M2dArchive.HeaderPathOf(m2d);
                if (SamePath(outM2d, outM2h)) throw new ModKitException("output name must not end in .m2h: " + m2d);
                tmpM2d = outM2d + ".tmp"; tmpM2h = outM2h + ".tmp";
                string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(m2d));
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                fs = new FileStream(tmpM2d, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            }

            void Emit(M2dEntry rec, string hash, string name, byte[] stored)
            {
                rec.Index = records.Count + 1;
                rec.Offset = offset;
                fs.Write(stored, 0, stored.Length);
                offset += (ulong)stored.Length;
                records.Add(rec);
                names.Append(rec.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
                if (!string.IsNullOrEmpty(hash)) names.Append(hash).Append(',');
                names.Append(name).Append("\r\n");
            }

            internal void AddRaw(M2dEntry from, byte[] stored) { Emit(from.Clone(), from.Hash, from.Name, stored); }

            internal void AddPlain(string hash, string name, uint flag, byte[] plain)
            {
                uint comp;
                byte[] stored = M2dCodec.Encode(version, flag, plain, out comp);
                Emit(new M2dEntry { Flag = flag, EncodedSize = (uint)stored.Length, CompressedSize = comp, Size = (ulong)plain.Length }, hash, name, stored);
            }

            internal long Finish()
            {
                fs.Close();
                var h = new M2hHeader { Version = version, Reserved = reserved, FileCount = (ulong)records.Count };
                byte[] nameBytes = Encoding.UTF8.GetBytes(names.ToString());
                uint comp;
                byte[] encNames = M2dCodec.Encode(version, M2dFlag.AesZlib, nameBytes, out comp);
                h.HeaderSize = (ulong)nameBytes.Length; h.CompressedHeaderSize = comp; h.EncodedHeaderSize = (ulong)encNames.Length;
                byte[] table;
                using (var ms = new MemoryStream())
                {
                    var bw = new BinaryWriter(ms);
                    foreach (M2dEntry r in records) M2dTable.WriteRecord(version, bw, r);
                    bw.Flush();
                    table = ms.ToArray();
                }
                byte[] encTable = M2dCodec.Encode(version, M2dFlag.AesZlib, table, out comp);
                h.DataSize = (ulong)table.Length; h.CompressedDataSize = comp; h.EncodedDataSize = (ulong)encTable.Length;
                using (var hf = new FileStream(tmpM2h, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var bw = new BinaryWriter(hf);
                    h.Write(bw);
                    bw.Write(encNames);
                    bw.Write(encTable);
                    bw.Flush();
                }
                Replace(tmpM2d, outM2d);
                Replace(tmpM2h, outM2h);
                return (long)offset;
            }

            static void Replace(string tmp, string dst)
            {
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(tmp, dst);
            }

            internal void Abort()
            {
                try { fs.Close(); } catch (IOException) { }
                try { if (File.Exists(tmpM2d)) File.Delete(tmpM2d); if (File.Exists(tmpM2h)) File.Delete(tmpM2h); } catch (IOException) { }
            }
        }
    }
}
