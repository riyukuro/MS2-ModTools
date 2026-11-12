// The music of the event project (MS2Sound.fev) as the client plays it, and a writer that adds a cue.
//
// Where it is: the .fev is RIFF "FEV " with a LIST "PROJ"; one of its chunks, "LGCY", holds the FMOD Ex Designer
// project in the older layout, and the music is the last part of it: a "comp" chunk that ends where LGCY ends. The
// chunks inside comp are u32 length (with the 8 byte head), then the tag: the length comes first, unlike RIFF.
//
//   comp
//     sett            two floats
//     cues / entl     the cue list: u16 count, u16 names size, count u32 cue ids, the names NUL-ended
//     prms            the parameters (prmh u16 count, prmd u32 id each, entl of their names)
//     lnks            lnkh u16 count; lnk (lnkd: u32 id, u32 target segment, u32 kind; cond: the condition, empty
//                     or cprm chunks on a parameter); lfsh u16 count; lfsd per segment: u32 segment, u16 n, n u32 links
//     tlns            timelines
//     sgms            sgmh u16 count; sgmd per segment: u32 id, u32 theme, u32, u16 time signature, float bpm, float
//                     seconds, u32, then smps (smph: u8 play mode, u32 count; smp each: u32 name size, the bank name
//                     NUL-ended, u32 sample index); smpf: str (u32 count, count u32 offsets, u32 size, the source
//                     file names) and smpm (u32 count, then u32 segment, u32 sub, u32 name index each)
//     thms            thmh u16 count; thm per theme: thmd (u32 id, 13 bytes of settings, u16 n, n u32 start
//                     segments) and cond
//     scns            scnh u16 count; scnd: u32 scene, u16 n, n pairs of u32 cue and u32 theme
//
// A cue plays its theme's start segment; when a segment ends, the client follows its link (lfsd) to the next one, and
// a loop is a segment linked to itself. The login cue is: cue 15, theme 24, segment 35 (BGM4 sample 50, 5 s), then
// a link to segment 78 (BGM4 sample 49), which links to itself. Theme, segment and link ids are each their own number
// space. Nothing outside comp points into it, so it can grow: LGCY, PROJ and RIFF get the new lengths. (Checked on the
// KR client's project: of every u32 outside comp, only those three lengths name an offset or a size of the chunks after
// comp, EPRP, STRR and LANG, or of the file; OBCT holds 33 pairs of an index and a count.)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MS2.ModKit
{
    /// <summary>One sample a segment plays: the bank (MS2Sound_BGM4) and the sample's index in it.</summary>
    public sealed class MusicSample
    {
        public string Bank;
        public int Index;
        public override string ToString() { return Bank + " #" + Index; }
    }

    /// <summary>A segment of the music: its samples and the segment it goes to when it ends (-1: none).</summary>
    public sealed class MusicSegment
    {
        public int Id, Theme;
        public float Bpm, Seconds;
        public readonly List<MusicSample> Samples = new List<MusicSample>();
        public readonly List<int> Next = new List<int>();
        /// <summary>The kind of each link in Next (the third u32 of lnkd; 4 for every link of the KR client's project).</summary>
        public readonly List<int> NextKind = new List<int>();
        /// <summary>The source file name the project keeps for the segment's first sample (smpf), or null.</summary>
        public string Source;
        public override string ToString() { return "segment " + Id + " (" + string.Join(", ", Samples.Select(s => s.ToString()).ToArray()) + ")"; }
    }

    /// <summary>The music part of MS2Sound.fev: cue, theme, segment and link, read; AddCue writes a new cue.</summary>
    public sealed class MusicGraph
    {
        sealed class Node
        {
            public string Tag;
            public byte[] Head;                 // the bytes before the children (all of them for a leaf)
            public List<Node> Kids;             // null for a leaf
            public int Size() { int n = 8 + Head.Length; if (Kids != null) foreach (Node k in Kids) n += k.Size(); return n; }
            public void Write(Stream o)
            {
                o.Write(BitConverter.GetBytes(Size()), 0, 4);
                o.Write(Encoding.ASCII.GetBytes(Tag), 0, 4);
                o.Write(Head, 0, Head.Length);
                if (Kids != null) foreach (Node k in Kids) k.Write(o);
            }
            public Node Kid(string tag) { return Kids.FirstOrDefault(k => k.Tag == tag); }
            public Node Clone()
            {
                var c = new Node { Tag = Tag, Head = (byte[])Head.Clone() };
                if (Kids != null) c.Kids = Kids.Select(k => k.Clone()).ToList();
                return c;
            }
            public IEnumerable<Node> All(string tag) { return Kids.Where(k => k.Tag == tag); }
        }

        static readonly HashSet<string> Containers = new HashSet<string>
            { "comp", "cues", "prms", "lnks", "lnk ", "cond", "tlns", "sgms", "smps", "smpf", "thms", "thm ", "scns" };
        const int SegmentHead = 26;
        const int MaxDepth = 16;            // the project nests 5 deep; a deeper file is no project (and would overflow the stack)

        byte[] file;
        int compAt, compEnd;
        readonly List<int> lengthsAt = new List<int>();     // the RIFF lengths around comp: RIFF, PROJ, LGCY
        Node comp;
        readonly string label;

        public readonly Dictionary<int, string> Cues = new Dictionary<int, string>();
        public readonly Dictionary<int, int> ThemeOf = new Dictionary<int, int>();          // cue -> theme
        public readonly Dictionary<int, List<int>> StartOf = new Dictionary<int, List<int>>();  // theme -> segments
        public readonly Dictionary<int, MusicSegment> Segments = new Dictionary<int, MusicSegment>();
        readonly Dictionary<int, int> linkTo = new Dictionary<int, int>();
        readonly Dictionary<int, int> linkKind = new Dictionary<int, int>();

        MusicGraph(string label) { this.label = label; }

        ModKitException Bad(string what) { return new ModKitException(label + ": " + what); }

        public static MusicGraph Read(byte[] d, string label)
        {
            var g = new MusicGraph(label) { file = d };
            g.FindComp();
            int end;
            g.comp = g.Parse(g.compAt, g.compEnd, 0, out end);
            if (end != g.compEnd) throw g.Bad("the music chunk's length does not agree");
            g.Load();
            return g;
        }

        // ---- reading

        void FindComp()
        {
            byte[] d = file;
            if (d.Length < 12 || Encoding.ASCII.GetString(d, 0, 4) != "RIFF" || Encoding.ASCII.GetString(d, 8, 4) != "FEV ")
                throw Bad("not an FMOD event project (RIFF FEV)");
            if (BitConverter.ToUInt32(d, 4) != d.Length - 8) throw Bad("the RIFF length is not the file's");
            // the music: "comp", then "sett" (16 bytes) and "cues"; the length is before each tag
            int found = -1;
            for (int i = 4; i + 28 <= d.Length; i++)
            {
                if (d[i] != 'c' || d[i + 1] != 'o' || d[i + 2] != 'm' || d[i + 3] != 'p') continue;
                if (Encoding.ASCII.GetString(d, i + 8, 4) != "sett" || Encoding.ASCII.GetString(d, i + 24, 4) != "cues") continue;
                if (found >= 0) throw Bad("two music chunks");
                found = i - 4;
            }
            if (found < 0) throw Bad("no music chunk (comp/sett/cues)");
            compAt = found;
            long len = BitConverter.ToUInt32(d, found);
            if (len < 8 || found + len > d.Length) throw Bad("the music chunk runs past the end");
            compEnd = (int)(found + len);
            // the RIFF chunks that hold it; comp must be the last thing in the innermost one
            lengthsAt.Add(4);
            int at = 12, end = d.Length, inner = -1;
            while (at + 8 <= end)
            {
                string t = Encoding.ASCII.GetString(d, at, 4);
                long n = BitConverter.ToUInt32(d, at + 4);
                if (at + 8 + n > end) throw Bad("RIFF chunk " + t + " runs past its end");
                if (found >= at + 8 && found < at + 8 + n)
                {
                    lengthsAt.Add(at + 4);
                    if (t == "LIST") { end = (int)(at + 8 + n); at += 12; continue; }
                    inner = (int)(at + 8 + n);
                    if ((n & 1) != 0 && inner + 1 > end) throw Bad("RIFF chunk " + t + " has an odd length but no pad byte");
                    break;
                }
                at += 8 + (int)n + (int)(n & 1);
            }
            if (inner != compEnd) throw Bad("the music chunk is not the end of its RIFF chunk");
        }

        Node Parse(int at, int limit, int depth, out int end)
        {
            byte[] d = file;
            if (depth > MaxDepth) throw Bad("the music chunks nest deeper than " + MaxDepth);
            if (at + 8 > limit) throw Bad("a music chunk is cut short at " + at);
            long n = BitConverter.ToUInt32(d, at);
            string tag = Encoding.ASCII.GetString(d, at + 4, 4);
            if (n < 8 || at + n > limit) throw Bad("music chunk " + tag + " at " + at + " has a length that does not fit");
            end = (int)(at + n);
            var node = new Node { Tag = tag };
            int head = tag == "sgmd" ? SegmentHead : Containers.Contains(tag) ? 0 : (int)n - 8;
            if (head > n - 8) throw Bad("segment chunk at " + at + " is too short");
            node.Head = new byte[head];
            Buffer.BlockCopy(d, at + 8, node.Head, 0, head);
            if (head == n - 8 && tag != "sgmd" && !Containers.Contains(tag)) return node;
            node.Kids = new List<Node>();
            int k = at + 8 + head;
            while (k < end) { int e; node.Kids.Add(Parse(k, end, depth + 1, out e)); k = e; }
            return node;
        }

        Node Need(Node parent, string tag)
        {
            Node k = parent.Kids == null ? null : parent.Kid(tag);
            if (k == null) throw Bad("no " + tag + " chunk in " + parent.Tag);
            return k;
        }

        void Load()
        {
            byte[] e = Need(Need(comp, "cues"), "entl").Head;
            if (e.Length < 4) throw Bad("the music cue list is cut short");
            int n = BitConverter.ToUInt16(e, 0);
            if (e.Length != 4 + 4 * n + BitConverter.ToUInt16(e, 2)) throw Bad("the music cue list's sizes do not agree");
            int q = 4 + 4 * n;
            for (int i = 0; i < n; i++)
            {
                int z = Array.IndexOf(e, (byte)0, q);
                if (z < 0) throw Bad("the music cue names end early");
                Cues[BitConverter.ToInt32(e, 4 + 4 * i)] = Encoding.UTF8.GetString(e, q, z - q);
                q = z + 1;
            }
            foreach (Node s in Need(comp, "scns").All("scnd"))
            {
                if (s.Head.Length < 6) throw Bad("a scene is cut short");
                int c = BitConverter.ToUInt16(s.Head, 4);
                if (s.Head.Length != 6 + 8 * c) throw Bad("a scene's size does not agree");
                for (int i = 0; i < c; i++) ThemeOf[BitConverter.ToInt32(s.Head, 6 + 8 * i)] = BitConverter.ToInt32(s.Head, 10 + 8 * i);
            }
            foreach (Node t in Need(comp, "thms").All("thm "))
            {
                byte[] h = Need(t, "thmd").Head;
                int c = h.Length >= 19 ? BitConverter.ToUInt16(h, 17) : -1;
                if (c < 0 || h.Length != 19 + 4 * c) throw Bad("a theme's size does not agree");
                var l = new List<int>();
                for (int i = 0; i < c; i++) l.Add(BitConverter.ToInt32(h, 19 + 4 * i));
                StartOf[BitConverter.ToInt32(h, 0)] = l;
            }
            foreach (Node s in Need(comp, "sgms").All("sgmd"))
            {
                var m = new MusicSegment
                {
                    Id = BitConverter.ToInt32(s.Head, 0), Theme = BitConverter.ToInt32(s.Head, 4),
                    Bpm = BitConverter.ToSingle(s.Head, 14), Seconds = BitConverter.ToSingle(s.Head, 18),
                };
                foreach (Node p in Need(s, "smps").All("smp "))
                {
                    byte[] h = p.Head;
                    int len = h.Length >= 4 ? BitConverter.ToInt32(h, 0) : -1;
                    if (len < 1 || h.Length != 8 + len || h[3 + len] != 0) throw Bad("a segment sample's size does not agree");
                    m.Samples.Add(new MusicSample { Bank = Encoding.ASCII.GetString(h, 4, len - 1), Index = BitConverter.ToInt32(h, 4 + len) });
                }
                Segments[m.Id] = m;
            }
            Node lk = Need(comp, "lnks");
            foreach (Node l in lk.All("lnk "))
            {
                byte[] h = Need(l, "lnkd").Head;
                if (h.Length < 12) throw Bad("a link is cut short");
                linkTo[BitConverter.ToInt32(h, 0)] = BitConverter.ToInt32(h, 4);
                linkKind[BitConverter.ToInt32(h, 0)] = BitConverter.ToInt32(h, 8);
            }
            foreach (Node f in lk.All("lfsd"))
            {
                if (f.Head.Length < 6) throw Bad("a segment's link list is cut short");
                int c = BitConverter.ToUInt16(f.Head, 4);
                if (f.Head.Length != 6 + 4 * c) throw Bad("a segment's link list's size does not agree");
                MusicSegment m;
                if (!Segments.TryGetValue(BitConverter.ToInt32(f.Head, 0), out m)) throw Bad("links from a segment that is not there");
                for (int i = 0; i < c; i++)
                {
                    int to;
                    if (!linkTo.TryGetValue(BitConverter.ToInt32(f.Head, 6 + 4 * i), out to)) throw Bad("segment " + m.Id + " names a link that is not there");
                    m.Next.Add(to);
                    m.NextKind.Add(linkKind[BitConverter.ToInt32(f.Head, 6 + 4 * i)]);
                }
            }
            Node sf = Need(comp, "sgms").Kid("smpf");
            if (sf != null) LoadSources(sf);
        }

        void LoadSources(Node f)
        {
            byte[] a = Need(f, "str ").Head, m = Need(f, "smpm").Head;
            int n = a.Length >= 4 ? BitConverter.ToInt32(a, 0) : -1;
            if (n < 0 || a.Length < 8 + 4L * n || BitConverter.ToInt32(a, 4 + 4 * n) != a.Length - 8 - 4 * n) throw Bad("the sample file names' size does not agree");
            int names = 8 + 4 * n;
            var text = new string[n];
            for (int i = 0; i < n; i++)
            {
                int o = BitConverter.ToInt32(a, 4 + 4 * i), z = o < 0 || (long)names + o >= a.Length ? -1 : Array.IndexOf(a, (byte)0, names + o);
                if (z < 0) throw Bad("a sample file name runs past the names");
                text[i] = Encoding.UTF8.GetString(a, names + o, z - names - o);
            }
            int rows = m.Length >= 4 ? BitConverter.ToInt32(m, 0) : -1;
            if (rows < 0 || m.Length != 4 + 12L * rows) throw Bad("the sample file map's size does not agree");
            for (int r = 0; r < rows; r++)
            {
                int seg = BitConverter.ToInt32(m, 4 + 12 * r), sub = BitConverter.ToInt32(m, 8 + 12 * r), name = BitConverter.ToInt32(m, 12 + 12 * r);
                if (name < 0 || name >= n) throw Bad("the sample file map names a file that is not there");
                MusicSegment s;
                if (sub == 0 && Segments.TryGetValue(seg, out s)) s.Source = text[name];
            }
        }

        /// <summary>The segments a cue plays, in order, until one repeats (a loop) or one has no link.</summary>
        public List<MusicSegment> Play(int cue)
        {
            var l = new List<MusicSegment>();
            int theme; List<int> start;
            if (!ThemeOf.TryGetValue(cue, out theme) || !StartOf.TryGetValue(theme, out start) || start.Count == 0) return l;
            MusicSegment s;
            var seen = new HashSet<int>();
            int id = start[0];
            while (Segments.TryGetValue(id, out s) && seen.Add(id))
            {
                l.Add(s);
                if (s.Next.Count == 0) break;
                id = s.Next[0];
            }
            return l;
        }

        // ---- writing

        /// <summary>The highest cue id, so a new cue can take the next one.</summary>
        public int MaxCue { get { return Cues.Count == 0 ? 0 : Cues.Keys.Max(); } }

        /// <summary>Adds the cue <paramref name="id"/> (0: one past the highest) that plays <paramref name="intro"/>
        /// once (when not null) and then loops <paramref name="loop"/>; the theme, segment and link settings are copied
        /// from the cue <paramref name="like"/>. <paramref name="introSource"/> and <paramref name="loopSource"/> are the
        /// source file names the project keeps for each segment sample (smpf; the designer's, the client does not open
        /// them). Returns the new cue's id; Bytes() then gives the project. On an error nothing changes.</summary>
        public int AddCue(int id, string name, MusicSample intro, MusicSample loop, float introSeconds, float loopSeconds, int like,
            string introSource = null, string loopSource = null)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf('\0') >= 0) throw new ModKitException("a new cue needs a name");
            if (Cues.Values.Contains(name)) throw new ModKitException("there is a cue named " + name + " already");
            if (loop == null) throw new ModKitException("a new cue needs a sample to loop");
            if (id < 0) throw new ModKitException("cue id " + id + " is below 0");
            if (id > 0 && Cues.ContainsKey(id)) throw new ModKitException("there is a cue " + id + " already (" + Cues[id] + ")");
            List<MusicSegment> model = Play(like);
            if (model.Count == 0) throw new ModKitException("cue " + like + " plays nothing to copy the settings from");
            int cue = id > 0 ? id : MaxCue + 1, theme = StartOf.Keys.Max() + 1, seg = Segments.Keys.Max() + 1;
            int link = linkTo.Count == 0 ? 1 : linkTo.Keys.Max() + 1;
            // the work is done on a copy, so a failure leaves this graph as it was
            Node work = comp.Clone();

            // the cue's name and id
            Node entl = Need(Need(work, "cues"), "entl");
            byte[] e = entl.Head;
            int n = BitConverter.ToUInt16(e, 0), names = BitConverter.ToUInt16(e, 2);
            int nameEnd = 4 + 4 * n;
            for (int i = 0; i < n; i++) nameEnd = Array.IndexOf(e, (byte)0, nameEnd) + 1;
            byte[] nb = Encoding.UTF8.GetBytes(name + "\0");
            if (n + 1 > ushort.MaxValue || names + nb.Length > ushort.MaxValue) throw new ModKitException("the cue list is full");
            var ne = new MemoryStream();
            ne.Write(BitConverter.GetBytes((ushort)(n + 1)), 0, 2);
            ne.Write(BitConverter.GetBytes((ushort)(names + nb.Length)), 0, 2);
            ne.Write(e, 4, 4 * n);
            ne.Write(BitConverter.GetBytes(cue), 0, 4);
            ne.Write(e, 4 + 4 * n, nameEnd - 4 - 4 * n);
            ne.Write(nb, 0, nb.Length);
            ne.Write(e, nameEnd, e.Length - nameEnd);
            entl.Head = ne.ToArray();

            // the scene: cue -> theme
            Node scnd = Need(Need(work, "scns"), "scnd");
            int sc = BitConverter.ToUInt16(scnd.Head, 4);
            if (sc + 1 > ushort.MaxValue) throw new ModKitException("the scene is full");
            scnd.Head = Concat(scnd.Head, BitConverter.GetBytes(cue), BitConverter.GetBytes(theme));
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)(sc + 1)), 0, scnd.Head, 4, 2);

            // the theme, settings from the model's
            Node thms = Need(work, "thms");
            Node mt = thms.All("thm ").First(t => BitConverter.ToInt32(Need(t, "thmd").Head, 0) == ThemeOf[like]);
            byte[] th = new byte[23];
            Buffer.BlockCopy(Need(mt, "thmd").Head, 0, th, 0, 17);
            Buffer.BlockCopy(BitConverter.GetBytes(theme), 0, th, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)1), 0, th, 17, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(seg), 0, th, 19, 4);
            InsertAfterLast(thms, new Node { Tag = "thm ", Head = new byte[0], Kids = new List<Node> { Leaf("thmd", th), new Node { Tag = "cond", Head = new byte[0], Kids = new List<Node>() } } });
            Count(thms, "thmh", 1);

            // the segments and their links: intro -> loop -> loop; the link kind is the model's last link's
            Node sgms = Need(work, "sgms"), lnks = Need(work, "lnks");
            MusicSegment last = model[model.Count - 1];
            Node ms = sgms.All("sgmd").First(s => BitConverter.ToInt32(s.Head, 0) == last.Id);
            int kind = last.NextKind.Count > 0 ? last.NextKind[0] : 4;
            int loopSeg = intro == null ? seg : seg + 1;
            if (intro != null) AddSegment(sgms, lnks, ms, seg, theme, intro, introSeconds, link++, loopSeg, kind, introSource ?? name + "_intro");
            AddSegment(sgms, lnks, ms, loopSeg, theme, loop, loopSeconds, link, loopSeg, kind, loopSource ?? name + "_loop");

            // a fresh read of what was written, so the graph and the bytes agree
            byte[] b = Bytes(work);
            MusicGraph g = Read(b, label);
            List<MusicSegment> got = g.Play(cue);
            if (got.Count != (intro == null ? 1 : 2) || got[got.Count - 1].Next.Count != 1 || got[got.Count - 1].Next[0] != loopSeg)
                throw Bad("the new cue does not read back");
            file = g.file; comp = g.comp; compAt = g.compAt; compEnd = g.compEnd;
            lengthsAt.Clear(); lengthsAt.AddRange(g.lengthsAt);
            Cues.Clear(); foreach (var kv in g.Cues) Cues[kv.Key] = kv.Value;
            ThemeOf.Clear(); foreach (var kv in g.ThemeOf) ThemeOf[kv.Key] = kv.Value;
            StartOf.Clear(); foreach (var kv in g.StartOf) StartOf[kv.Key] = kv.Value;
            Segments.Clear(); foreach (var kv in g.Segments) Segments[kv.Key] = kv.Value;
            linkTo.Clear(); foreach (var kv in g.linkTo) linkTo[kv.Key] = kv.Value;
            linkKind.Clear(); foreach (var kv in g.linkKind) linkKind[kv.Key] = kv.Value;
            return cue;
        }

        void AddSegment(Node sgms, Node lnks, Node model, int id, int theme, MusicSample s, float seconds, int link, int next, int kind, string source)
        {
            byte[] h = (byte[])model.Head.Clone();
            Buffer.BlockCopy(BitConverter.GetBytes(id), 0, h, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(theme), 0, h, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(seconds), 0, h, 18, 4);
            byte[] bank = Encoding.ASCII.GetBytes(s.Bank + "\0");
            byte[] smp = Concat(BitConverter.GetBytes(bank.Length), bank, BitConverter.GetBytes(s.Index));
            byte[] smph = Concat(new byte[] { 0 }, BitConverter.GetBytes(1));
            var smps = new Node { Tag = "smps", Head = new byte[0], Kids = new List<Node> { Leaf("smph", smph), Leaf("smp ", smp) } };
            InsertAfterLast(sgms, new Node { Tag = "sgmd", Head = h, Kids = new List<Node> { smps } });
            Count(sgms, "sgmh", 1);
            AddSource(sgms, id, source);

            byte[] lnkd = Concat(BitConverter.GetBytes(link), BitConverter.GetBytes(next), BitConverter.GetBytes(kind));
            InsertAfterLast(lnks, new Node { Tag = "lnk ", Head = new byte[0], Kids = new List<Node> { Leaf("lnkd", lnkd), new Node { Tag = "cond", Head = new byte[0], Kids = new List<Node>() } } });
            Count(lnks, "lnkh", 1);
            InsertAfterLast(lnks, Leaf("lfsd", Concat(BitConverter.GetBytes(id), BitConverter.GetBytes((ushort)1), BitConverter.GetBytes(link))));
            Count(lnks, "lfsh", 1);
        }

        // the source file name of a new segment's sample: a name in smpf/str and a row (segment, 0, name) in smpf/smpm,
        // as every shipped segment has; a project without smpf gets none
        void AddSource(Node sgms, int seg, string source)
        {
            Node f = sgms.Kid("smpf");
            if (f == null) return;
            Node str = Need(f, "str "), map = Need(f, "smpm");
            byte[] a = str.Head;
            if (a.Length < 8) throw Bad("the sample file names are cut short");
            int n = BitConverter.ToInt32(a, 0);
            if (n < 0 || a.Length < 8 + 4L * n) throw Bad("the sample file names are cut short");
            int size = BitConverter.ToInt32(a, 4 + 4 * n), names = 8 + 4 * n;
            if (size != a.Length - names) throw Bad("the sample file names' size does not agree");
            byte[] nb = Encoding.UTF8.GetBytes(source + "\0");
            str.Head = Concat(BitConverter.GetBytes(n + 1), Slice(a, 4, 4 * n), BitConverter.GetBytes(size), BitConverter.GetBytes(size + nb.Length), Slice(a, names, size), nb);
            byte[] m = map.Head;
            if (m.Length < 4) throw Bad("the sample file map is cut short");
            int rows = BitConverter.ToInt32(m, 0);
            if (rows < 0 || m.Length != 4 + 12L * rows) throw Bad("the sample file map's size does not agree");
            map.Head = Concat(BitConverter.GetBytes(rows + 1), Slice(m, 4, m.Length - 4), BitConverter.GetBytes(seg), BitConverter.GetBytes(0), BitConverter.GetBytes(n));
        }

        static byte[] Slice(byte[] a, int at, int n) { var b = new byte[n]; Buffer.BlockCopy(a, at, b, 0, n); return b; }

        static Node Leaf(string tag, byte[] head) { return new Node { Tag = tag, Head = head }; }

        void InsertAfterLast(Node parent, Node k)
        {
            int i = parent.Kids.FindLastIndex(x => x.Tag == k.Tag);
            if (i < 0) throw Bad("no " + k.Tag + " chunk in " + parent.Tag + " to add after");
            parent.Kids.Insert(i + 1, k);
        }

        void Count(Node parent, string tag, int add)
        {
            Node c = Need(parent, tag);
            if (c.Head.Length != 2) throw Bad("the " + tag + " count does not fit");
            int v = BitConverter.ToUInt16(c.Head, 0) + add;
            if (v > ushort.MaxValue) throw Bad("the " + tag + " count does not fit");
            c.Head = BitConverter.GetBytes((ushort)v);
        }

        static byte[] Concat(params byte[][] parts)
        {
            var o = new MemoryStream();
            foreach (byte[] p in parts) o.Write(p, 0, p.Length);
            return o.ToArray();
        }

        /// <summary>The project with the music as it is now: the same bytes as read when nothing was added.</summary>
        public byte[] Bytes() { return Bytes(comp); }

        byte[] Bytes(Node root)
        {
            var m = new MemoryStream();
            root.Write(m);
            byte[] c = m.ToArray();
            int delta = c.Length - (compEnd - compAt);
            // the innermost RIFF chunk's pad byte follows its length
            int inner = lengthsAt[lengthsAt.Count - 1];
            long oldLen = BitConverter.ToUInt32(file, inner), newLen = oldLen + delta;
            int oldPad = (int)(oldLen & 1), newPad = (int)(newLen & 1);
            var o = new MemoryStream();
            o.Write(file, 0, compAt);
            o.Write(c, 0, c.Length);
            if (newPad == 1) o.WriteByte(0);
            int rest = compEnd + oldPad;
            o.Write(file, rest, file.Length - rest);
            byte[] b = o.ToArray();
            int grow = delta + newPad - oldPad;
            foreach (int at in lengthsAt)
            {
                long v = BitConverter.ToUInt32(file, at) + (at == inner ? delta : grow);
                if (v < 0 || v > uint.MaxValue) throw Bad("the project does not fit");
                Buffer.BlockCopy(BitConverter.GetBytes((uint)v), 0, b, at, 4);
            }
            return b;
        }
    }
}
