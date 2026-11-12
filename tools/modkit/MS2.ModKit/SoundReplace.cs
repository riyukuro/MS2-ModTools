// Sample replace: a mod gives a sample of a shipped FMOD bank new sound.
//
//   <sample bank="MS2Sound_BGM.fsb" index="12" name="Field_Town_A" file="files/sound/BGM/12.mp2" />
//
// The client finds a sample by its place in the bank, not by its name: the event project (MS2Sound.fev) names the
// bank and the index for each music segment and sound definition. This was checked in game on a private test client
// (a bank whose entries were all moved two places on played the sample two places on, under the old name's slot), so
// a replace keeps every index, every name and the bank's hash (the project checks it), and changes only the data and
// the sample header of the sample it names. index decides; name is checked against the shipped bank, so a mod made
// for another client version stops with an error instead of replacing another sample.
//
// The new sound is an MPEG-1 or 2 layer II file (.mp2), as the MS2 banks hold MPEG (codec mode 11) and every shipped
// sample is layer II. Its frames go into the bank as they are, without the tags and a Xing/Info frame. Layer III
// (.mp3) is read but refused: in game, a self-made layer III sample played only a low hum, with or without the bit
// reservoir, where the same sound as layer II played (private test client, null sink).
//
// The music segments of the event project have their own length: the client goes on to the next segment when the
// segment's time is up, not when the sample ends (seen in game: a 69 s sample in a 5 s segment played 5 s). A new
// sample of another length is cut there, or probably ends in silence; the plan warns about it.
//
// Where the bank goes: the workspace target writes <workspace>/client/<Sound folder>/<bank>; the loose target puts
// the new bank in the game's Data/Sound and keeps the shipped one beside it as <bank>.modkit-orig (LooseFiles.cs).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>A sample of a bank that the mod replaces.</summary>
    public sealed class SampleOp
    {
        /// <summary>The bank's file name in the Sound folder (MS2Sound_BGM.fsb).</summary>
        public string Bank;
        public int Index;
        /// <summary>The name the shipped bank has at Index.</summary>
        public string Name;
        /// <summary>The MPEG file under the mod folder.</summary>
        public string File;
        public int Line;
    }

    /// <summary>A sample the writer adds to a bank.</summary>
    public sealed class NewSample
    {
        public string Name;
        public MpegAudio Audio;
        /// <summary>A loop chunk over the whole sound, as the shipped music samples that loop have.</summary>
        public bool Loop;
    }

    public sealed partial class Mod
    {
        /// <summary>Sample replaces (SoundReplace.cs).</summary>
        public readonly List<SampleOp> Samples = new List<SampleOp>();

        /// <summary>The sample op for a bank and index, or null.</summary>
        public SampleOp SampleOf(string bank, int index)
        {
            foreach (SampleOp s in Samples) if (s.Index == index && string.Equals(s.Bank, bank, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        /// <summary>The folder under the mod that the editors put a sample's file in.</summary>
        public static string SampleFile(string bank, int index, string ext)
        {
            return FilesFolder + "/sound/" + SoundData.ShortName(bank) + "/" + index.ToString(CultureInfo.InvariantCulture) + ext;
        }

        /// <summary>The editors' replace: the sample line for a bank's sample, with the MPEG file kept in Files (saved
        /// with the mod). The file is read first, so a file that is no MPEG audio changes nothing.</summary>
        public SampleOp SetSample(string bank, SoundSample shipped, byte[] mpeg, string label)
        {
            if (!SoundReplace.IsBankName(bank)) throw new ModKitException("\"" + bank + "\" is no bank file name");
            string why = SoundReplace.CannotReplace(shipped);
            if (why != null) throw new ModKitException(bank + " sample " + shipped.Index + " " + shipped.Name + ": " + why);
            string no = MpegAudio.Parse(mpeg, label).NotForTheClient();
            if (no != null) throw new ModKitException(label + ": " + no);
            const string ext = ".mp2";
            SampleOp op = SampleOf(bank, shipped.Index);
            if (op == null) { op = new SampleOp { Bank = bank, Index = shipped.Index }; Samples.Add(op); }
            else if (op.File != null) Files.Remove(op.File);
            op.Name = shipped.Name;
            op.File = SampleFile(bank, shipped.Index, ext);
            Files[op.File] = mpeg;
            return op;
        }

        /// <summary>Takes a sample line out; the shipped sound plays again. False when there was none.</summary>
        public bool TakeSample(string bank, int index)
        {
            SampleOp op = SampleOf(bank, index);
            if (op == null) return false;
            Samples.Remove(op);
            if (op.File != null) Files.Remove(op.File);
            return true;
        }

        sealed partial class Reader
        {
            internal SampleOp ReadSample(XmlElem e)
            {
                Only(e, "bank", "index", "name", "file");
                var s = new SampleOp { Bank = Need(e, "bank"), Name = Need(e, "name"), File = Need(e, "file").Replace('\\', '/'), Line = e.Line };
                long i = Num(e, "index");
                if (i > int.MaxValue) throw Bad(e, "index=\"" + e.Attr("index") + "\" is too large");
                s.Index = (int)i;
                if (!SoundReplace.IsBankName(s.Bank)) throw Bad(e, "bank=\"" + s.Bank + "\" is the file name of a bank in the Sound folder (MS2Sound_BGM.fsb)");
                if (e.Children.Count > 0) throw Bad(e, "<sample> has no child elements");
                return s;
            }
        }

        void WriteSamples(StringBuilder sb)
        {
            foreach (SampleOp s in Samples)
            {
                sb.Append("  <sample"); A(sb, "bank", s.Bank); A(sb, "index", s.Index.ToString(CultureInfo.InvariantCulture)); A(sb, "name", s.Name); A(sb, "file", s.File);
                sb.Append(" />\n");
            }
        }
    }

    /// <summary>The frames of an MPEG audio file and what they hold.</summary>
    public sealed class MpegAudio
    {
        public int Version;         // 1, 2 or 25 (MPEG 2.5)
        public int Layer;           // 2 or 3
        public int Rate;            // Hz
        public int Channels;        // 1 or 2
        public long Frames;         // samples per channel
        public int MpegFrames;
        /// <summary>The encoder's delay and padding (samples per channel) from a LAME tag; in Frames, not in Seconds.</summary>
        public int EncoderGap;
        /// <summary>The frames only: no tags, no Xing/Info frame.</summary>
        public byte[] Data;
        /// <summary>Why the client cannot play these frames from a bank (null: it can). See the file's head.</summary>
        public string NotForTheClient()
        {
            if (Layer == 2) return null;
            return "MPEG layer " + (Layer == 3 ? "III (.mp3)" : Layer.ToString(CultureInfo.InvariantCulture)) + " does not play from a bank in the client (every shipped sample is layer II;"
                + " a layer III sample played only a low hum in game); convert it to layer II, for example: ffmpeg -i in.wav -c:a mp2 -b:a 192k out.mp2";
        }

        public double Seconds { get { return Rate > 0 ? (double)Math.Max(0, Frames - EncoderGap) / Rate : 0; } }

        public string Describe()
        {
            return Seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s, MPEG-" + (Version == 25 ? "2.5" : Version.ToString(CultureInfo.InvariantCulture))
                + " layer " + (Layer == 2 ? "II" : "III") + ", " + Rate.ToString(CultureInfo.InvariantCulture) + " Hz, " + (Channels == 1 ? "mono" : "stereo");
        }

        static readonly int[][] Kbps =
        {
            new[] { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 },     // MPEG-1 layer II
            new[] { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 },      // MPEG-1 layer III
            new[] { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },          // MPEG-2 and 2.5 layers II, III
        };
        static readonly int[] Rates1 = { 44100, 48000, 32000 };

        /// <summary>The frames of an .mp2/.mp3 file; throws ModKitException for anything else (layer I, free format,
        /// frames of mixed kinds, bytes between frames).</summary>
        public static MpegAudio Parse(byte[] b, string label)
        {
            int at = 0, end = b.Length;
            // ID3v2 at the start (a syncsafe size, a footer flag)
            while (end - at >= 10 && b[at] == 'I' && b[at + 1] == 'D' && b[at + 2] == '3')
            {
                int size = (b[at + 6] & 0x7f) << 21 | (b[at + 7] & 0x7f) << 14 | (b[at + 8] & 0x7f) << 7 | (b[at + 9] & 0x7f);
                at += 10 + size + ((b[at + 5] & 0x10) != 0 ? 10 : 0);
            }
            // ID3v1 and APE tags at the end
            if (end - at >= 128 && b[end - 128] == 'T' && b[end - 127] == 'A' && b[end - 126] == 'G') end -= 128;
            // Lyrics3 (before ID3v1): v2 ends in a 6-digit size and "LYRICS200", v1 runs from "LYRICSBEGIN" to "LYRICSEND"
            if (end - at >= 15 && Encoding.ASCII.GetString(b, end - 9, 9) == "LYRICS200")
            {
                int size;
                if (int.TryParse(Encoding.ASCII.GetString(b, end - 15, 6), NumberStyles.None, CultureInfo.InvariantCulture, out size) && size + 15 <= end - at) end -= size + 15;
            }
            else if (end - at >= 20 && Encoding.ASCII.GetString(b, end - 9, 9) == "LYRICSEND")
            {
                for (int i = end - 20; i >= Math.Max(at, end - 5100 - 20); i--)
                    if (b[i] == 'L' && Encoding.ASCII.GetString(b, i, 11) == "LYRICSBEGIN") { end = i; break; }
            }
            if (end - at >= 32 && Encoding.ASCII.GetString(b, end - 32, 8) == "APETAGEX")
            {
                int size = (int)BitConverter.ToUInt32(b, end - 20);
                if (size >= 32 && size <= end - at) end -= size + ((BitConverter.ToUInt32(b, end - 12) & 0x80000000) != 0 ? 32 : 0);
            }
            var a = new MpegAudio();
            var data = new MemoryStream();
            bool first = true;
            while (at < end)
            {
                // after the last frame: a few bytes or zeros that some editors leave there
                if (!first && (end - at < 4 || Zeros(b, at, end))) break;
                if (end - at < 4 || b[at] != 0xFF || (b[at + 1] & 0xE0) != 0xE0)
                    throw new ModKitException(label + ": no MPEG audio frame at byte " + at.ToString(CultureInfo.InvariantCulture) + (first ? " (this is not an .mp2 or .mp3 file)" : ""));
                int v = (b[at + 1] >> 3) & 3, layer = 4 - ((b[at + 1] >> 1) & 3), bi = b[at + 2] >> 4, ri = (b[at + 2] >> 2) & 3, pad = (b[at + 2] >> 1) & 1;
                int mode = b[at + 3] >> 6;
                if (v == 1 || layer == 4 || ri == 3 || bi == 15) throw new ModKitException(label + ": a broken MPEG frame header at byte " + at.ToString(CultureInfo.InvariantCulture));
                int version = v == 3 ? 1 : v == 2 ? 2 : 25;
                if (layer == 1) throw new ModKitException(label + ": MPEG layer I; the banks take layer II or III (.mp2 or .mp3)");
                if (bi == 0) throw new ModKitException(label + ": a free-format MPEG frame (no bit rate); save the file with a fixed or variable bit rate");
                int rate = Rates1[ri] / (version == 1 ? 1 : version == 2 ? 2 : 4);
                int kbps = Kbps[version == 1 ? layer - 2 : 2][bi];
                int spf = layer == 3 && version != 1 ? 576 : 1152;
                int len = spf / 8 * kbps * 1000 / rate + pad;
                int ch = mode == 3 ? 1 : 2;
                if (at + len > end)
                {
                    // a cut-off last frame is left out
                    if (!first) break;
                    throw new ModKitException(label + ": the first MPEG frame runs past the end of the file");
                }
                if (first)
                {
                    a.Version = version; a.Layer = layer; a.Rate = rate; a.Channels = ch;
                    first = false;
                    int gap;
                    if (layer == 3 && IsInfoFrame(b, at, version, ch, len, out gap)) { a.EncoderGap = gap; at += len; continue; }
                }
                else if (version != a.Version || layer != a.Layer || rate != a.Rate || ch != a.Channels)
                    throw new ModKitException(label + ": the MPEG frame at byte " + at.ToString(CultureInfo.InvariantCulture) + " is of another kind than the first one (version, layer, rate or channels)");
                data.Write(b, at, len);
                a.MpegFrames++;
                a.Frames += spf;
                at += len;
            }
            if (a.MpegFrames == 0) throw new ModKitException(label + ": no MPEG audio frames");
            a.Data = data.ToArray();
            return a;
        }

        // the Xing, Info or VBRI frame an encoder puts first (it holds no sound)
        // a Xing/Info or VBRI frame (no sound); gap: the delay and padding of a LAME tag in it, else 0
        static bool IsInfoFrame(byte[] b, int at, int version, int ch, int len, out int gap)
        {
            gap = 0;
            int side = version == 1 ? (ch == 1 ? 17 : 32) : (ch == 1 ? 9 : 17);
            int x = at + 4 + ((b[at + 1] & 1) == 0 ? 2 : 0) + side, stop = at + len;
            if (x + 8 <= stop)
            {
                string t = Encoding.ASCII.GetString(b, x, 4);
                if (t == "Xing" || t == "Info")
                {
                    int flags = b[x + 7], p = x + 8;
                    if ((flags & 1) != 0) p += 4;      // frames
                    if ((flags & 2) != 0) p += 4;      // bytes
                    if ((flags & 4) != 0) p += 100;    // table of contents
                    if ((flags & 8) != 0) p += 4;      // quality
                    // the LAME tag (also written by ffmpeg as "Lavc"/"Lavf"): 12 bits delay, 12 bits padding at +21
                    if (p + 24 <= stop)
                    {
                        string e = Encoding.ASCII.GetString(b, p, 4);
                        if (e == "LAME" || e == "Lavc" || e == "Lavf" || e == "L3.9")
                            gap = (b[p + 21] << 4 | b[p + 22] >> 4) + ((b[p + 22] & 15) << 8 | b[p + 23]);
                    }
                    return true;
                }
            }
            return at + 36 + 4 <= stop && Encoding.ASCII.GetString(b, at + 36, 4) == "VBRI";
        }

        static bool Zeros(byte[] b, int from, int to)
        {
            for (int i = from; i < to; i++) if (b[i] != 0) return false;
            return true;
        }
    }

    /// <summary>What a plan does to one bank.</summary>
    public sealed class PlanBank
    {
        /// <summary>The bank's file name (MS2Sound_BGM.fsb).</summary>
        public string Bank;
        /// <summary>The Sound folder the plan read and its path under the client's data root ("Sound").</summary>
        public string Folder, RelFolder;
        /// <summary>The bank the plan read: the shipped one (its .modkit-orig when a loose apply has put a new one in its place).</summary>
        public string Source;
        public byte[] Hash;
        /// <summary>The samples the bank has.</summary>
        public int Count;
        public readonly List<PlanSample> Samples = new List<PlanSample>();
        /// <summary>The samples the plan adds after the shipped ones (SoundCue.cs).</summary>
        public readonly List<PlanNewSample> Adds = new List<PlanNewSample>();
        /// <summary>True for the event project (MS2Sound.fev): Cues are added to it, SourceSha1 is the shipped file's
        /// SHA-1 and Count its cues.</summary>
        public bool Project;
        public string SourceSha1;
        public readonly List<PlanCue> Cues = new List<PlanCue>();

        /// <summary>"client:Sound/MS2Sound_BGM.fsb", the name the plan's lines use.</summary>
        public string Ref { get { return LooseTarget.Root + ":" + RelFolder + "/" + Bank; } }
    }

    public sealed class PlanSample
    {
        public int Index;
        public string Name, ModId, File;
        public MpegAudio Audio;
        public double OldSeconds;
    }

    public static partial class SoundReplace
    {
        public const string OrigSuffix = ".modkit-orig";

        public static bool IsBankName(string name)
        {
            return !string.IsNullOrEmpty(name) && name.EndsWith(".fsb", StringComparison.OrdinalIgnoreCase) && name.IndexOfAny(new[] { '/', '\\', ':' }) < 0
                && name != ".fsb" && !name.StartsWith(".", StringComparison.Ordinal);
        }

        /// <summary>
        /// The shipped bank of a folder: its .modkit-orig when a loose apply of this tool has put a new bank in its
        /// place. A .modkit-orig that no loose-file record names, or one that is not the bank in place before the
        /// samples (a game patch has put a new bank there since), is not taken: ModKitException.
        /// </summary>
        public static string ShippedPath(string folder, string bank)
        {
            string p = Path.Combine(folder, bank), orig = p + OrigSuffix;
            if (!File.Exists(orig)) return p;
            string full = Path.GetFullPath(p);
            LooseFile rec = null;
            for (string d = Path.GetDirectoryName(full); d != null && rec == null; d = Path.GetDirectoryName(d))
            {
                if (!File.Exists(Path.Combine(d, LooseTarget.StateFile))) continue;
                if (!Directory.Exists(Path.Combine(d, "Data"))) break;
                LooseState st = new LooseTarget(d).ReadState();
                string rel = full.Substring(d.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                if (st != null) rec = st.FindBank(rel);
                break;
            }
            if (rec == null)
                throw new ModKitException(orig + " is there, but no record of this tool's loose files names " + bank + "; it is not taken for the shipped bank (move it away, or put it back in place of " + bank + ")");
            if (File.Exists(p) && !bank.EndsWith(".fsb", StringComparison.OrdinalIgnoreCase))
            {
                // the project: the file in place must be the one the record names
                if (!rec.Holds(Sha1File(p)))
                    throw new ModKitException(p + " is not the file its " + OrigSuffix + " was put aside for (a game patch may have put a new one there); revert the loose files, or move " + orig + " away");
            }
            else if (File.Exists(p))
            {
                SoundBank now = SoundBank.Read(p), was = SoundBank.Read(orig);
                // a bank with new samples has more than the shipped one: the record then tells it is this tool's
                if (SoundBank.Hex(now.Hash) != SoundBank.Hex(was.Hash) || (now.Samples.Count != was.Samples.Count && !rec.Holds(Sha1File(p))))
                    throw new ModKitException(p + " is not the bank its " + OrigSuffix + " was put aside for (a game patch may have put a new bank there); revert the loose files, or move " + orig + " away");
            }
            return orig;
        }

        /// <summary>The bank's file name for an index of the rate table, -1 for a rate the FSB5 header cannot hold.</summary>
        internal static int RateIndex(int rate) { return Array.IndexOf(SoundBank.Rates, rate); }

        // ---- the writer ----

        /// <summary>
        /// Writes a bank like src with the given samples replaced; every other byte of the headers, the names and the
        /// data stays. Returns the SHA-1 of what was written (lower-case hex).
        /// </summary>
        public static string Write(string src, string dst, IDictionary<int, MpegAudio> replace, Action<ProgressInfo> progress)
        {
            return Write(src, dst, replace, null, progress);
        }

        /// <summary>As Write, and with new samples added after the last one (their indexes are the bank's count on).</summary>
        public static string Write(string src, string dst, IDictionary<int, MpegAudio> replace, IList<NewSample> add, Action<ProgressInfo> progress)
        {
            using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var o = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None))
                return Write(i, o, replace, add, Path.GetFileName(src), progress);
        }

        public static string Write(Stream src, Stream dst, IDictionary<int, MpegAudio> replace, string label, Action<ProgressInfo> progress)
        {
            return Write(src, dst, replace, null, label, progress);
        }

        /// <summary>
        /// A new sample goes after the last one: its header (with a loop chunk when Loop is set, as the shipped music
        /// samples that loop have), its name at the end of the name table (every name offset moves by 4 a sample, as
        /// the offsets come first) and its data, from a 32-byte boundary. The count, the sizes and the data size of the
        /// header follow; the hash stays, so the event project still takes the bank. The name table is padded so that
        /// the data starts on 32 bytes, as in every shipped bank.
        /// </summary>
        public static string Write(Stream src, Stream dst, IDictionary<int, MpegAudio> replace, IList<NewSample> add, string label, Action<ProgressInfo> progress)
        {
            if (add == null) add = new NewSample[0];
            byte[] h = Read(src, SoundBank.HeaderSize, label);
            if (h[0] != 'F' || h[1] != 'S' || h[2] != 'B' || h[3] != '5' || BitConverter.ToUInt32(h, 4) != 1) throw new ModKitException(label + ": not an FSB5 version 1 bank");
            int n = (int)BitConverter.ToUInt32(h, 8), shs = (int)BitConverter.ToUInt32(h, 12), nts = (int)BitConverter.ToUInt32(h, 16);
            long ds = BitConverter.ToUInt32(h, 20);
            if (n < 0 || n > 1000000 || shs < 0 || shs > 64 << 20 || nts < 0 || nts > 64 << 20) throw new ModKitException(label + ": the header sizes are out of range");
            if ((replace.Count > 0 || add.Count > 0) && BitConverter.ToUInt32(h, 24) != 11) throw new ModKitException(label + ": the bank's codec is " + SoundBank.Codec((int)BitConverter.ToUInt32(h, 24)) + ", not MPEG");
            byte[] sh = Read(src, shs, label), nt = Read(src, nts, label);
            long dataAt = SoundBank.HeaderSize + (long)shs + nts;

            // the sample headers: where each starts, its data offset
            var heads = new int[n];
            var offs = new long[n + 1];
            int p = 0;
            for (int k = 0; k < n; k++)
            {
                if (p + 8 > sh.Length) throw new ModKitException(label + ": the sample headers end early");
                heads[k] = p;
                ulong v = BitConverter.ToUInt64(sh, p);
                offs[k] = (long)((v >> 7) & 0x7ffffff) * 32;
                if (k > 0 ? offs[k] < offs[k - 1] : offs[k] != 0) throw new ModKitException(label + ": the sample data is not in the order of the samples");
                p += 8;
                bool more = (v & 1) != 0;
                while (more)
                {
                    if (p + 4 > sh.Length) throw new ModKitException(label + ": sample " + k + ": an extra chunk runs past the sample headers");
                    uint c = BitConverter.ToUInt32(sh, p);
                    more = (c & 1) != 0;
                    p += 4 + (int)((c >> 1) & 0xffffff);
                    if (p > sh.Length) throw new ModKitException(label + ": sample " + k + ": an extra chunk runs past the sample headers");
                }
            }
            offs[n] = ds;
            if (n > 0 && offs[n - 1] > ds) throw new ModKitException(label + ": a sample starts past the end of the data");
            foreach (int k in replace.Keys) if (k < 0 || k >= n) throw new ModKitException(label + ": no sample " + k + " (the bank has " + n + ")");

            // the new headers and data offsets
            var nsh = (byte[])sh.Clone();
            long at = 0;
            var lens = new long[n];
            for (int k = 0; k < n; k++)
            {
                MpegAudio m;
                bool rep = replace.TryGetValue(k, out m);
                lens[k] = rep ? Pad32(m.Data.Length) : offs[k + 1] - offs[k];
                if (at / 32 > 0x7ffffff) throw new ModKitException(label + ": the data grows past what the sample headers can address (4 GB)");
                ulong v = BitConverter.ToUInt64(nsh, heads[k]);
                v = (v & ~(0x7ffffffUL << 7)) | ((ulong)(at / 32) << 7);
                if (rep) v = Header(v, m, label, k);
                Array.Copy(BitConverter.GetBytes(v), 0, nsh, heads[k], 8);
                if (rep) Chunks(nsh, heads[k], m, label, k);
                at += lens[k];
            }
            // the new samples: after the old data, from a 32-byte boundary
            long tail = 0;
            if (add.Count > 0)
            {
                tail = Pad32(at) - at;
                at += tail;
                var hs = new MemoryStream();
                hs.Write(nsh, 0, nsh.Length);
                for (int j = 0; j < add.Count; j++)
                {
                    NewSample x = add[j];
                    if (x == null || x.Audio == null || x.Audio.Data == null) throw new ModKitException(label + ": new sample " + (n + j) + " has no sound");
                    if (at / 32 > 0x7ffffff) throw new ModKitException(label + ": the data grows past what the sample headers can address (4 GB)");
                    ulong v = Header((ulong)(at / 32) << 7, x.Audio, label, n + j);
                    if (x.Loop) v |= 1;
                    hs.Write(BitConverter.GetBytes(v), 0, 8);
                    if (x.Loop)
                    {
                        hs.Write(BitConverter.GetBytes((3u << 25) | (8u << 1)), 0, 4);
                        hs.Write(BitConverter.GetBytes(0u), 0, 4);
                        hs.Write(BitConverter.GetBytes((uint)(x.Audio.Frames - 1)), 0, 4);
                    }
                    at += Pad32(x.Audio.Data.Length);
                }
                nsh = hs.ToArray();
                nt = Names(nt, n, add, label, SoundBank.HeaderSize + nsh.Length);
            }
            if (at > uint.MaxValue) throw new ModKitException(label + ": the data grows past 4 GB");
            var nh = (byte[])h.Clone();
            Array.Copy(BitConverter.GetBytes((uint)at), 0, nh, 20, 4);
            if (add.Count > 0)
            {
                Array.Copy(BitConverter.GetBytes((uint)(n + add.Count)), 0, nh, 8, 4);
                Array.Copy(BitConverter.GetBytes((uint)nsh.Length), 0, nh, 12, 4);
                Array.Copy(BitConverter.GetBytes((uint)nt.Length), 0, nh, 16, 4);
            }

            using (SHA1 sha = SHA1.Create())
            {
                Out(dst, sha, nh, nh.Length);
                Out(dst, sha, nsh, nsh.Length);
                Out(dst, sha, nt, nt.Length);
                var buf = new byte[1 << 20];
                for (int k = 0; k < n; k++)
                {
                    ProgressUtil.Report(progress, "bank", label, k + 1, n);
                    MpegAudio m;
                    if (replace.TryGetValue(k, out m))
                    {
                        Out(dst, sha, m.Data, m.Data.Length);
                        int z = (int)(lens[k] - m.Data.Length);
                        if (z > 0) Out(dst, sha, new byte[z], z);
                        continue;
                    }
                    src.Seek(dataAt + offs[k], SeekOrigin.Begin);
                    for (long left = lens[k]; left > 0; )
                    {
                        int r = src.Read(buf, 0, (int)Math.Min(buf.Length, left));
                        if (r <= 0) throw new ModKitException(label + ": the data of sample " + k + " ends early");
                        Out(dst, sha, buf, r);
                        left -= r;
                    }
                }
                if (tail > 0) Out(dst, sha, new byte[tail], (int)tail);
                foreach (NewSample x in add)
                {
                    Out(dst, sha, x.Audio.Data, x.Audio.Data.Length);
                    int z = (int)(Pad32(x.Audio.Data.Length) - x.Audio.Data.Length);
                    if (z > 0) Out(dst, sha, new byte[z], z);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return SoundBank.Hex(sha.Hash);
            }
        }

        static long Pad32(long n) { return (n + 31) / 32 * 32; }

        // the name table with the new names: n + new offsets, the old names as they were, the new names, then zeros
        // up to a 32-byte boundary of the file (before is the header and the sample headers)
        static byte[] Names(byte[] nt, int n, IList<NewSample> add, string label, int before)
        {
            if (nt.Length == 0) throw new ModKitException(label + ": the bank has no name table; a new sample needs a name");
            if (4L * n > nt.Length) throw new ModKitException(label + ": the name table ends early");
            var names = new HashSet<string>(StringComparer.Ordinal);
            int end = 4 * n;
            for (int k = 0; k < n; k++)
            {
                int o = (int)BitConverter.ToUInt32(nt, 4 * k);
                if (o < 4 * n || o >= nt.Length) throw new ModKitException(label + ": sample " + k + ": its name is outside the name table");
                int e = Array.IndexOf(nt, (byte)0, o);
                if (e < 0) throw new ModKitException(label + ": sample " + k + ": its name has no end");
                names.Add(Encoding.UTF8.GetString(nt, o, e - o));
                end = Math.Max(end, e + 1);
            }
            int a = add.Count, shift = 4 * a;
            var o2 = new MemoryStream();
            for (int k = 0; k < n; k++) o2.Write(BitConverter.GetBytes(BitConverter.ToUInt32(nt, 4 * k) + (uint)shift), 0, 4);
            var text = new MemoryStream();
            text.Write(nt, 4 * n, end - 4 * n);
            int at = end + shift;
            foreach (NewSample x in add)
            {
                if (string.IsNullOrEmpty(x.Name) || x.Name.IndexOf('\0') >= 0) throw new ModKitException(label + ": a new sample needs a name");
                if (!names.Add(x.Name)) throw new ModKitException(label + ": the bank has a sample " + x.Name + " already");
                o2.Write(BitConverter.GetBytes((uint)at), 0, 4);
                byte[] b = Encoding.UTF8.GetBytes(x.Name);
                text.Write(b, 0, b.Length);
                text.WriteByte(0);
                at += b.Length + 1;
            }
            text.WriteTo(o2);
            while ((before + o2.Length) % 32 != 0) o2.WriteByte(0);
            return o2.ToArray();
        }

        // the bit fields of a replaced sample: frames, rate, channels
        static ulong Header(ulong v, MpegAudio m, string label, int k)
        {
            int ri = RateIndex(m.Rate), ci = Array.IndexOf(SoundBank.ChannelCodes, m.Channels);
            if (ri < 0) throw new ModKitException(label + ": sample " + k + ": a bank cannot hold the rate " + m.Rate + " Hz");
            if (ci < 0) throw new ModKitException(label + ": sample " + k + ": a bank cannot hold " + m.Channels + " channels");
            if (m.Frames >= 1L << 30) throw new ModKitException(label + ": sample " + k + ": too long for a sample header");
            v &= (1UL << 34) - 1;
            v = (v & ~(0xfUL << 1) & ~(3UL << 5)) | ((ulong)ri << 1) | ((ulong)ci << 5) | ((ulong)m.Frames << 34);
            return v;
        }

        // the extra chunks of a replaced sample: channels, rate and loop follow the new data; any other kind stops
        static void Chunks(byte[] sh, int head, MpegAudio m, string label, int k)
        {
            int p = head + 8;
            bool more = (BitConverter.ToUInt64(sh, head) & 1) != 0;
            while (more)
            {
                uint c = BitConverter.ToUInt32(sh, p);
                more = (c & 1) != 0;
                int size = (int)((c >> 1) & 0xffffff), type = (int)(c >> 25);
                p += 4;
                if (type == 1 && size >= 1) sh[p] = (byte)m.Channels;
                else if (type == 2 && size >= 4) Array.Copy(BitConverter.GetBytes((uint)m.Rate), 0, sh, p, 4);
                else if (type == 3 && size >= 8)
                {
                    Array.Copy(BitConverter.GetBytes(0u), 0, sh, p, 4);
                    Array.Copy(BitConverter.GetBytes((uint)(m.Frames - 1)), 0, sh, p + 4, 4);
                }
                else throw new ModKitException(label + ": sample " + k + " has an extra chunk of type " + type + " that a replace cannot follow");
                p += size;
            }
        }

        /// <summary>Why a sample cannot be replaced (an extra chunk the writer cannot follow), or null.</summary>
        public static string CannotReplace(SoundSample s)
        {
            foreach (int t in s.Chunks) if (t < 1 || t > 3) return "sample " + s.Index + " has an extra chunk of type " + t + " that a replace cannot follow";
            return null;
        }

        static void Out(Stream dst, SHA1 sha, byte[] b, int n)
        {
            dst.Write(b, 0, n);
            sha.TransformBlock(b, 0, n, null, 0);
        }

        static byte[] Read(Stream s, int n, string label)
        {
            var b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = s.Read(b, got, n - got);
                if (r <= 0) throw new ModKitException(label + ": the bank ends early");
                got += r;
            }
            return b;
        }

        /// <summary>SHA-1 of a file, lower-case hex.</summary>
        public static string Sha1File(string path)
        {
            using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA1 sha = SHA1.Create())
                return SoundBank.Hex(sha.ComputeHash(s));
        }

        /// <summary>The replaces of a plan bank by index.</summary>
        public static Dictionary<int, MpegAudio> Replaces(PlanBank b)
        {
            var d = new Dictionary<int, MpegAudio>();
            foreach (PlanSample s in b.Samples) d[s.Index] = s.Audio;
            return d;
        }

        /// <summary>Is the path, or a folder on the way to it below stop, a link (symbolic link or junction)?</summary>
        public static string LinkOnTheWay(string path, string stop)
        {
            string full = Path.GetFullPath(path), top = Path.GetFullPath(stop).TrimEnd(Path.DirectorySeparatorChar);
            for (string d = full; d != null && d.Length > top.Length; d = Path.GetDirectoryName(d))
                if ((Directory.Exists(d) || File.Exists(d)) && (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) return d;
            return null;
        }
    }

    public static partial class Planner
    {
        /// <summary>
        /// The sample lines of the mods: the bank and the sample must be there under the name the line gives, the file
        /// must be MPEG audio the bank can hold. Two mods on one sample: the one of the higher priority is written.
        /// </summary>
        static void DoSamples(Run run, IList<Mod> mods)
        {
            Plan plan = run.Plan;
            bool any = false;
            foreach (Mod m in mods) if (m.Samples.Count > 0) any = true;
            if (!any) return;
            string client = run.Data.RootFolder(LooseTarget.Root);
            string folder = SoundData.Find(client);
            if (folder == null)
            {
                foreach (Mod m in mods) if (m.Samples.Count > 0) plan.Msg(plan.Errors, m, null, null, "the mod replaces samples, but the client data has no Sound folder with " + SoundData.ProjectFile);
                return;
            }
            string rel = "Sound";
            if (client != null)
            {
                string root = Path.GetFullPath(client).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string f = Path.GetFullPath(folder);
                if (f.StartsWith(root, StringComparison.OrdinalIgnoreCase)) rel = f.Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/');
            }
            var banks = new Dictionary<string, SoundBank>(StringComparer.OrdinalIgnoreCase);
            var bad = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var winners = new Dictionary<string, KeyValuePair<Mod, SampleOp>>(StringComparer.OrdinalIgnoreCase);
            // from the highest priority down: the first mod to name a sample keeps it
            for (int i = mods.Count - 1; i >= 0; i--)
            {
                Mod m = mods[i];
                var own = new Dictionary<string, SampleOp>(StringComparer.OrdinalIgnoreCase);
                foreach (SampleOp op in m.Samples)
                {
                    string where = op.Bank + " sample " + op.Index.ToString(CultureInfo.InvariantCulture) + " (" + ModLine.Of(op.Line) + ")";
                    string key = op.Bank + "\n" + op.Index.ToString(CultureInfo.InvariantCulture);
                    SampleOp twice;
                    if (own.TryGetValue(key, out twice)) { plan.Msg(plan.Errors, m, null, null, where + ": the mod replaces this sample twice (also on " + ModLine.Of(twice.Line) + ")"); continue; }
                    own[key] = op;
                    SoundBank b = BankOf(folder, op.Bank, banks, bad);
                    if (b == null) { plan.Msg(plan.Errors, m, null, null, where + ": " + bad[op.Bank]); continue; }
                    if (op.Index >= b.Samples.Count) { plan.Msg(plan.Errors, m, null, null, where + ": the bank has " + b.Samples.Count + " samples (0 to " + (b.Samples.Count - 1) + ")"); continue; }
                    SoundSample old = b.Samples[op.Index];
                    if (old.Name != op.Name)
                    {
                        plan.Msg(plan.Errors, m, null, null, where + ": the shipped sample " + op.Index + " is " + old.Name + ", not " + op.Name + " (the line was made for another bank; the client finds a sample by its index)");
                        continue;
                    }
                    string cannot = SoundReplace.CannotReplace(old);
                    if (cannot != null) { plan.Msg(plan.Errors, m, null, null, where + ": " + cannot); continue; }
                    MpegAudio a;
                    try { a = MpegAudio.Parse(SampleBytes(m, op), op.File); }
                    catch (ModKitException x) { plan.Msg(plan.Errors, m, null, null, where + ": " + x.Message); continue; }
                    catch (Exception x) when (x is IOException || x is UnauthorizedAccessException) { plan.Msg(plan.Errors, m, null, null, where + ": " + op.File + ": " + x.Message); continue; }
                    string no = a.NotForTheClient();
                    if (no != null) { plan.Msg(plan.Errors, m, null, null, where + ": " + op.File + ": " + no); continue; }
                    if (SoundReplace.RateIndex(a.Rate) < 0) { plan.Msg(plan.Errors, m, null, null, where + ": " + op.File + " is " + a.Rate + " Hz, a rate a bank cannot hold (use 44100 or 48000 Hz)"); continue; }
                    KeyValuePair<Mod, SampleOp> w;
                    if (winners.TryGetValue(key, out w))
                    {
                        plan.Conflict(m, null, null, where + ": mods " + w.Key.Id + " and " + m.Id + " both replace " + old.Name, w.Key, m);
                        continue;
                    }
                    winners[key] = new KeyValuePair<Mod, SampleOp>(m, op);
                    PlanBank pb = null;
                    foreach (PlanBank x in plan.Banks) if (string.Equals(x.Bank, b.Name, StringComparison.OrdinalIgnoreCase)) pb = x;
                    if (pb == null) plan.Banks.Add(pb = new PlanBank { Bank = b.Name, Folder = folder, RelFolder = rel, Source = b.Path, Hash = b.Hash, Count = b.Samples.Count });
                    pb.Samples.Add(new PlanSample { Index = op.Index, Name = old.Name, ModId = m.Id, File = op.File, Audio = a, OldSeconds = old.Seconds });
                    plan.Changes.Add(new PlanChange { Kind = ChangeKind.ReplaceSample, ModId = m.Id, Mod = m, Archive = pb.Ref, Entry = "sample " + op.Index + " " + old.Name, NewValue = op.File + ", " + a.Describe() });
                    if (Math.Abs(a.Seconds - old.Seconds) > 0.1)
                        plan.Msg(plan.Warnings, m, pb.Ref, "sample " + op.Index + " " + old.Name,
                            "the new sound is " + a.Seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s, the shipped one " + old.Seconds.ToString("0.00", CultureInfo.InvariantCulture)
                            + " s; the music segment that plays it keeps the shipped length, so a longer sound is cut there and a shorter one probably ends in silence");
                    if (a.Channels != old.Channels || a.Rate != old.Rate)
                        plan.Msg(plan.Notes, m, pb.Ref, "sample " + op.Index + " " + old.Name,
                            "the shipped sound is " + old.Rate + " Hz " + (old.Channels == 1 ? "mono" : old.Channels == 2 ? "stereo" : old.Channels + " channels") + ", the new one " + a.Rate + " Hz " + (a.Channels == 1 ? "mono" : "stereo"));
                }
            }
            foreach (PlanBank pb in plan.Banks) pb.Samples.Sort((x, y) => x.Index.CompareTo(y.Index));
            plan.Banks.Sort((x, y) => string.Compare(x.Bank, y.Bank, StringComparison.OrdinalIgnoreCase));
        }

        static SoundBank BankOf(string folder, string name, Dictionary<string, SoundBank> banks, Dictionary<string, string> bad)
        {
            SoundBank b;
            if (banks.TryGetValue(name, out b)) return b;
            if (bad.ContainsKey(name)) return null;
            string p = Path.Combine(folder, name);
            try
            {
                p = SoundReplace.ShippedPath(folder, name);
                if (!File.Exists(p)) { bad[name] = "no bank " + name + " in " + folder; return null; }
                b = SoundBank.Read(p);
            }
            catch (ModKitException x) { bad[name] = x.Message; return null; }
            catch (Exception x) when (x is IOException || x is UnauthorizedAccessException) { bad[name] = p + ": " + x.Message; return null; }
            // the name of the file the bank stands for, also when it was read from its .modkit-orig
            b.Name = name;
            if (b.Mode != 11) { bad[name] = name + " holds " + SoundBank.Codec(b.Mode) + ", not MPEG; only MPEG banks can take a new sample"; return null; }
            banks[name] = b;
            return b;
        }

        static byte[] SampleBytes(Mod m, SampleOp op)
        {
            byte[] b;
            if (m.Files.TryGetValue(op.File, out b)) return b;
            string p = Mod.PathIn(m.Dir, op.File);
            if (p == null) throw new ModKitException("file=\"" + op.File + "\" is not a path inside the mod folder");
            if (!File.Exists(p)) throw new ModKitException("the file " + p + " is not there");
            return File.ReadAllBytes(p);
        }
    }
}
