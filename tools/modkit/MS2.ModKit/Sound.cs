// Sound and music, read only: the FMOD banks and the event project in <client>/Data/Sound, and the maps' bgm rows.
//
// How the client loads them (CSoundService, soundservice.cpp): it sets the media path to the Sound folder and the
// language ("Korea", "en", "jp", "cn"), then loads MS2Sound.fev, the FMOD Ex Designer project; the project names
// its banks, MS2Sound_<bank>.fsb, which FMOD opens from the same folder. The voice banks have a language suffix
// (_Kor_def, _en, _cn). Effects and voices are events played by name (CSoundEventMgr); music goes through the
// FMOD music system: a cue is prompted by its id (promptCue) and parameters set by number (parameter 5 is the loop
// switch in the client's callback).
//
// Where the ids come from: a map's <bgm> row (the kms client: table/fielddata.xml, CFieldDataCli; the server and
// the global client: map/<id>.xml) has id (the cue played on the map), param and value (a music parameter set
// with it), boss, bossParam and bossValue (the cue and parameter while a boss fights) and enterSystemSound. A
// trigger's sound object (TriggerObjectSound, kinds BGM/AMB/Effect) plays a cue by the same number.
//
// The files:
//   - .fsb: FSB5 version 1. A 0x3c byte header ("FSB5", version, sample count, sample header size, name table
//     size, data size, codec mode, 8 zero bytes, a 16 byte hash), then one sample header per sample (a u64 of bit
//     fields, then chained extra chunks), the name table (one u32 offset per sample, then NUL-ended names) and the
//     data. Every MS2 bank has codec mode 11 (MPEG). The bank hash is in the .fev too: a rebuilt bank keeps it.
//   - .fev: RIFF "FEV ". The music cues are a "cues" chunk holding an "entl" list: u16 count, u16 names size,
//     count u32 cue ids, then the names, NUL-ended, in the order of the ids. The chunks after the music settings
//     do not all give byte lengths, so the list is found by its tags and checked, not reached by walking.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;

namespace MS2.ModKit
{
    /// <summary>One sample of a bank: its name and what its sample header says.</summary>
    public sealed class SoundSample
    {
        public int Index;
        public string Name;
        public int Rate;            // Hz
        public int Channels;
        public long Frames;         // samples per channel
        public long Offset;         // in the bank's data
        public long Size;           // bytes of the data
        public bool Loops;          // a loop chunk
        public double Seconds { get { return Rate > 0 ? (double)Frames / Rate : 0; } }
    }

    /// <summary>An FSB5 bank, its headers only (the data is not read).</summary>
    public sealed class SoundBank
    {
        public string Path, Name;
        public int Version, Mode;
        public long DataSize, FileSize;
        public byte[] Hash;         // the 8 bytes at 0x24 (the .fev keeps the same bytes for the bank)
        public readonly List<SoundSample> Samples = new List<SoundSample>();

        public const int HeaderSize = 0x3c;

        /// <summary>The codec of a mode number, as FMOD names it.</summary>
        public static string Codec(int mode)
        {
            switch (mode)
            {
                case 1: return "PCM8";
                case 2: return "PCM16";
                case 3: return "PCM24";
                case 4: return "PCM32";
                case 5: return "PCMFloat";
                case 6: return "GCADPCM";
                case 7: return "IMAADPCM";
                case 8: return "VAG";
                case 9: return "HEVAG";
                case 10: return "XMA";
                case 11: return "MPEG";
                case 12: return "CELT";
                case 13: return "AT9";
                case 14: return "XWMA";
                case 15: return "Vorbis";
                default: return "mode " + mode;
            }
        }

        static readonly int[] ChannelCodes = { 1, 2, 6, 8 };
        static readonly int[] Rates = { 4000, 8000, 11000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 96000 };

        public static SoundBank Read(string path)
        {
            using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                SoundBank b = Read(s, System.IO.Path.GetFileName(path));
                b.Path = path;
                return b;
            }
        }

        /// <summary>Reads the headers and the names; throws ModKitException for a file that is not an FSB5 bank.</summary>
        public static SoundBank Read(Stream s, string label)
        {
            byte[] h = Bytes(s, HeaderSize, label, "header");
            if (h[0] != 'F' || h[1] != 'S' || h[2] != 'B' || h[3] != '5') throw new ModKitException(label + ": not an FSB5 bank");
            var b = new SoundBank { Name = label };
            b.Version = (int)U32(h, 4);
            if (b.Version != 1) throw new ModKitException(label + ": FSB5 version " + b.Version + ", not 1");
            long n = U32(h, 8), shs = U32(h, 12), nts = U32(h, 16);
            b.DataSize = U32(h, 20);
            b.Mode = (int)U32(h, 24);
            b.Hash = new byte[8];
            Array.Copy(h, 0x24, b.Hash, 0, 8);
            if (s.CanSeek) b.FileSize = s.Length;
            if (n > 1000000 || shs > 64L << 20 || nts > 64L << 20) throw new ModKitException(label + ": the header sizes are out of range");
            if (s.CanSeek && HeaderSize + shs + nts + b.DataSize > s.Length) throw new ModKitException(label + ": the bank is shorter than its header says");
            byte[] sh = Bytes(s, (int)shs, label, "sample headers");
            int p = 0;
            for (int i = 0; i < n; i++)
            {
                if (p + 8 > sh.Length) throw new ModKitException(label + ": sample " + i + ": the sample headers end early");
                ulong v = BitConverter.ToUInt64(sh, p);
                p += 8;
                int fi = (int)((v >> 1) & 0xf);
                var x = new SoundSample
                {
                    Index = i,
                    Rate = fi < Rates.Length ? Rates[fi] : 0,
                    // bits 5-6: 1, 2, 6 or 8 channels; bits 7-33: the data offset in 32-byte units
                    Channels = ChannelCodes[(int)((v >> 5) & 3)],
                    Offset = (long)((v >> 7) & 0x7ffffff) * 32,
                    Frames = (long)(v >> 34),
                };
                bool more = (v & 1) != 0;
                while (more)
                {
                    if (p + 4 > sh.Length) throw new ModKitException(label + ": sample " + i + ": an extra chunk runs past the sample headers");
                    uint c = U32(sh, p);
                    p += 4;
                    more = (c & 1) != 0;
                    int size = (int)((c >> 1) & 0xffffff), type = (int)(c >> 25);
                    if (p + size > sh.Length) throw new ModKitException(label + ": sample " + i + ": an extra chunk runs past the sample headers");
                    if (type == 1 && size >= 1) x.Channels = sh[p];
                    else if (type == 2 && size >= 4) x.Rate = (int)U32(sh, p);
                    else if (type == 3) x.Loops = true;
                    p += size;
                }
                b.Samples.Add(x);
            }
            for (int i = 0; i < b.Samples.Count; i++)
            {
                long end = i + 1 < b.Samples.Count ? b.Samples[i + 1].Offset : b.DataSize;
                b.Samples[i].Size = Math.Max(0, end - b.Samples[i].Offset);
            }
            if (nts > 0)
            {
                byte[] nt = Bytes(s, (int)nts, label, "name table");
                for (int i = 0; i < b.Samples.Count; i++)
                {
                    if (4 * i + 4 > nt.Length) throw new ModKitException(label + ": the name table ends early");
                    int o = (int)U32(nt, 4 * i);
                    if (o < 0 || o >= nt.Length) throw new ModKitException(label + ": sample " + i + ": its name is outside the name table");
                    int e = Array.IndexOf(nt, (byte)0, o);
                    if (e < 0) e = nt.Length;
                    b.Samples[i].Name = Encoding.UTF8.GetString(nt, o, e - o);
                }
            }
            foreach (SoundSample x in b.Samples) if (x.Name == null) x.Name = "#" + x.Index;
            return b;
        }

        public string HashText { get { return Hex(Hash); } }

        internal static string Hex(byte[] b)
        {
            var sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        static byte[] Bytes(Stream s, int n, string label, string what)
        {
            var b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = s.Read(b, got, n - got);
                if (r <= 0) throw new ModKitException(label + ": the " + what + " end early");
                got += r;
            }
            return b;
        }

        internal static uint U32(byte[] b, int at) { return BitConverter.ToUInt32(b, at); }
    }

    /// <summary>A music cue of the .fev: the number a map's bgm row and a trigger name, and its name.</summary>
    public sealed class MusicCue
    {
        public int Id;
        public string Name;
        public override string ToString() { return Id + " " + Name; }
    }

    /// <summary>The event project, MS2Sound.fev: its music cues and the hashes of the banks it names.</summary>
    public sealed class SoundProject
    {
        public string Path;
        public readonly List<MusicCue> Cues = new List<MusicCue>();
        readonly Dictionary<int, MusicCue> byId = new Dictionary<int, MusicCue>();
        byte[] bytes;

        public MusicCue Cue(int id) { MusicCue c; return byId.TryGetValue(id, out c) ? c : null; }

        public static SoundProject Read(string path)
        {
            SoundProject p = Read(File.ReadAllBytes(path), System.IO.Path.GetFileName(path));
            p.Path = path;
            return p;
        }

        /// <summary>Reads the cue list; throws ModKitException when the file is no FEV project or the list does not check.</summary>
        public static SoundProject Read(byte[] d, string label)
        {
            if (d.Length < 12 || Encoding.ASCII.GetString(d, 0, 4) != "RIFF" || Encoding.ASCII.GetString(d, 8, 4) != "FEV ")
                throw new ModKitException(label + ": not an FMOD event project (RIFF FEV)");
            var p = new SoundProject { bytes = d };
            byte[] tag = Encoding.ASCII.GetBytes("cuesxxxxentl");
            int found = -1;
            for (int i = 12; i + 16 <= d.Length; i++)
            {
                if (d[i] != 'c' || d[i + 1] != 'u' || d[i + 2] != 'e' || d[i + 3] != 's') continue;
                if (d[i + 8] != tag[8] || d[i + 9] != tag[9] || d[i + 10] != tag[10] || d[i + 11] != tag[11]) continue;
                if (found >= 0) throw new ModKitException(label + ": two music cue lists");
                found = i;
            }
            if (found < 0) throw new ModKitException(label + ": no music cue list (cues/entl)");
            long len = SoundBank.U32(d, found + 4);
            int at = found + 12;
            int n = BitConverter.ToUInt16(d, at), names = BitConverter.ToUInt16(d, at + 2);
            at += 4;
            if (len != 4 + 8 + 4L * n + names || at + 4L * n + names > d.Length)
                throw new ModKitException(label + ": the music cue list's sizes do not agree (" + n + " cues, " + names + " bytes of names, chunk " + len + ")");
            var ids = new int[n];
            for (int i = 0; i < n; i++) ids[i] = (int)SoundBank.U32(d, at + 4 * i);
            at += 4 * n;
            int q = at, end = at + names;
            for (int i = 0; i < n; i++)
            {
                int e = Array.IndexOf(d, (byte)0, q, end - q);
                if (e < 0) throw new ModKitException(label + ": the music cue names end early (" + i + " of " + n + ")");
                var c = new MusicCue { Id = ids[i], Name = Encoding.UTF8.GetString(d, q, e - q) };
                if (c.Name.Length == 0) throw new ModKitException(label + ": music cue " + c.Id + " has no name");
                if (p.byId.ContainsKey(c.Id)) throw new ModKitException(label + ": music cue " + c.Id + " is there twice");
                p.byId[c.Id] = c;
                p.Cues.Add(c);
                q = e + 1;
            }
            p.Cues.Sort((a, b) => a.Id.CompareTo(b.Id));
            return p;
        }

        /// <summary>Does the project hold the bank's hash (the 8 bytes at 0x24 of the bank)? A bank whose hash is not
        /// there is not the one the project was built with.</summary>
        public bool HasHash(byte[] hash)
        {
            if (hash == null || hash.Length == 0) return false;
            for (int i = 0; i + hash.Length <= bytes.Length; i++)
            {
                int k = 0;
                while (k < hash.Length && bytes[i + k] == hash[k]) k++;
                if (k == hash.Length) return true;
            }
            return false;
        }
    }

    /// <summary>A map's use of a cue: its bgm row's id (Boss false) or boss (Boss true), on one side.</summary>
    public sealed class CueUse
    {
        public int Cue;
        public long Map;
        public bool Boss;
        public string Side;         // the archive the row is in
        public string Param, Value; // the parameter set with it (bossParam/bossValue for Boss)
        /// <summary>The environment the element is in: "feature=X locale=Y" (the parts it has), "" for a plain one.</summary>
        public string Env = "";
    }

    /// <summary>The Sound folder and what the maps play.</summary>
    public sealed class SoundData
    {
        public const string ProjectFile = "MS2Sound.fev";
        public const string Override = "MS2_SOUND_DATA";

        public readonly string Folder;
        public SoundData(string folder) { Folder = folder; }

        /// <summary>The Sound folder of a client data folder (the one with Xml.m2d): MS2_SOUND_DATA when set, else
        /// Sound beside Xml.m2d, else Data/Sound under it; null when none has the event project.</summary>
        public static string Find(string clientData)
        {
            string o = Environment.GetEnvironmentVariable(Override);
            if (!string.IsNullOrEmpty(o)) return o;
            if (string.IsNullOrEmpty(clientData)) return null;
            foreach (string f in new[] { Path.Combine(clientData, "Sound"), Path.Combine(Path.Combine(clientData, "Data"), "Sound") })
                if (File.Exists(Path.Combine(f, ProjectFile))) return f;
            return null;
        }

        public string ProjectPath { get { return Path.Combine(Folder, ProjectFile); } }

        /// <summary>The bank files of the folder, by name.</summary>
        public List<string> Banks()
        {
            var l = new List<string>();
            if (!Directory.Exists(Folder)) return l;
            foreach (string f in Directory.GetFiles(Folder, "*.fsb")) l.Add(Path.GetFileName(f));
            l.Sort(StringComparer.OrdinalIgnoreCase);
            return l;
        }

        /// <summary>A bank's short name: MS2Sound_BGM.fsb is BGM.</summary>
        public static string ShortName(string file)
        {
            string n = Path.GetFileNameWithoutExtension(file);
            return n.StartsWith("MS2Sound_", StringComparison.OrdinalIgnoreCase) ? n.Substring(9) : n;
        }

        static readonly Regex BgmTag = new Regex("<bgm\\b[^>]*>", RegexOptions.CultureInvariant);
        static readonly Regex AttrRx = new Regex("\\b([A-Za-z]+)\\s*=\\s*\"([^\"]*)\"", RegexOptions.CultureInvariant);
        static readonly Regex RowRx = new Regex("<fieldData\\b[^>]*\\bid\\s*=\\s*\"([0-9]+)\"", RegexOptions.CultureInvariant);
        static readonly Regex EnvTag = new Regex("<environment\\b[^>]*>", RegexOptions.CultureInvariant);
        static readonly Regex MapEntry = new Regex("^map/([0-9]+)\\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The bgm rows of a table/fielddata.xml (the kms client's form): every row's every bgm element.</summary>
        public static List<CueUse> FromFieldData(string text, string side)
        {
            var l = new List<CueUse>();
            MatchCollection rows = RowRx.Matches(text);
            for (int i = 0; i < rows.Count; i++)
            {
                long map = long.Parse(rows[i].Groups[1].Value, CultureInfo.InvariantCulture);
                int from = rows[i].Index, to = i + 1 < rows.Count ? rows[i + 1].Index : text.Length;
                Tags(l, text.Substring(from, to - from), map, side);
            }
            return l;
        }

        // every bgm element with the environment it is in (the last one opened before it)
        static void Tags(List<CueUse> l, string text, long map, string side)
        {
            MatchCollection envs = EnvTag.Matches(text);
            int e = -1;
            foreach (Match b in BgmTag.Matches(text))
            {
                while (e + 1 < envs.Count && envs[e + 1].Index < b.Index) e++;
                Add(l, b.Value, map, side, e < 0 ? "" : EnvLabel(envs[e].Value));
            }
        }

        /// <summary>The label of an environment element: its feature and locale, the ones that are set.</summary>
        public static string EnvLabel(string feature, string locale)
        {
            string f = (feature ?? "").Trim(), lo = (locale ?? "").Trim();
            return (f.Length > 0 ? "feature=" + f : "") + (f.Length > 0 && lo.Length > 0 ? " " : "") + (lo.Length > 0 ? "locale=" + lo : "");
        }

        static string EnvLabel(string tag)
        {
            string f = null, lo = null;
            foreach (Match m in AttrRx.Matches(tag))
            {
                if (m.Groups[1].Value == "feature") f = m.Groups[2].Value;
                else if (m.Groups[1].Value == "locale") lo = m.Groups[2].Value;
            }
            return EnvLabel(f, lo);
        }

        /// <summary>The bgm rows of a map/&lt;id&gt;.xml (the server's and the global client's form).</summary>
        public static List<CueUse> FromMapFile(string text, long map, string side)
        {
            var l = new List<CueUse>();
            Tags(l, text, map, side);
            return l;
        }

        static void Add(List<CueUse> l, string tag, long map, string side, string env)
        {
            var a = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in AttrRx.Matches(tag)) a[m.Groups[1].Value] = m.Groups[2].Value;
            Use(l, a, "id", "param", "value", false, map, side, env);
            Use(l, a, "boss", "bossParam", "bossValue", true, map, side, env);
        }

        static void Use(List<CueUse> l, Dictionary<string, string> a, string id, string param, string value, bool boss, long map, string side, string env)
        {
            string s;
            int cue;
            if (!a.TryGetValue(id, out s) || !int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out cue) || cue == 0) return;
            foreach (CueUse u in l) if (u.Map == map && u.Boss == boss && u.Cue == cue && u.Side == side && u.Env == env) return;
            string pv, vv;
            a.TryGetValue(param, out pv);
            a.TryGetValue(value, out vv);
            l.Add(new CueUse { Cue = cue, Map = map, Boss = boss, Side = side, Param = pv ?? "", Value = vv ?? "", Env = env });
        }

        /// <summary>
        /// The maps' cue uses of every side the data has, as the mod leaves the files: the kms client's fielddata.xml,
        /// the server's and the global client's map/&lt;id&gt;.xml. A file the mod replaces whole is read from the mod;
        /// row edits of fielddata.xml are not applied (the rows as shipped).
        /// </summary>
        public static List<CueUse> MapUses(DataSet data, Mod mod)
        {
            var l = new List<CueUse>();
            var store = new TriggerEditor(data, mod);
            if (data.Exists(EntityEditor.ClientXml))
            {
                M2dArchive a = data.Open(EntityEditor.ClientXml);
                M2dEntry e = a.Find(FieldRows.Entry);
                byte[] b = Current(store, a, EntityEditor.ClientXml, FieldRows.Entry);
                if (b != null || e != null) l.AddRange(FromFieldData(Text(b), EntityEditor.ClientXml));
            }
            foreach (string archive in new[] { EntityEditor.ServerXml, EntityEditor.GmsXml })
            {
                if (!data.Exists(archive)) continue;
                M2dArchive a = data.Open(archive);
                var names = new List<string>();
                foreach (M2dEntry e in a.Entries) names.Add(e.Name);
                // the map files the mod adds (an entry op for a name the archive does not have)
                foreach (ModOp o in mod != null ? mod.Ops : new List<ModOp>())
                {
                    var op = o as EntryOp;
                    if (op != null && !op.Delete && op.Name != null && string.Equals(op.Archive, archive, StringComparison.OrdinalIgnoreCase) && a.Find(op.Name) == null) names.Add(op.Name);
                }
                foreach (string name in names)
                {
                    Match m = MapEntry.Match(name.Replace('\\', '/'));
                    long map;
                    if (!m.Success || !long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out map)) continue;
                    byte[] b = Current(store, a, archive, name);
                    if (b == null) continue;
                    l.AddRange(FromMapFile(Text(b), map, archive));
                }
            }
            return l;
        }

        static byte[] Current(TriggerEditor store, M2dArchive a, string archive, string entry)
        {
            EntryOp op = store.OpOf(archive, entry);
            if (op != null) return store.ModBytes(op);
            M2dEntry e = a.Find(entry);
            return e == null ? null : a.Read(e);
        }

        static string Text(byte[] b) { return b == null ? "" : Encoding.UTF8.GetString(b); }
    }
}
