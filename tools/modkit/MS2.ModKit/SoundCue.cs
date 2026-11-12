// A new music cue: a mod adds a cue to the event project (MS2Sound.fev) that plays its own sound.
//
//   <cue id="300" name="My_Theme" bank="MS2Sound_BGM4.fsb" like="15" intro="files/sound/cue/300_intro.mp2" loop="files/sound/cue/300_loop.mp2" />
//
// The sound goes into the bank as new samples after the shipped ones (SoundReplace.Write), so no shipped index moves;
// the cue's segments in the project play them (MusicGraph.AddCue): the intro once (it may be left out), then the
// loop, linked to itself. The theme, segment and link settings are copied from the cue like names. A map plays the cue
// when its fielddata row's bgm id is the cue's id (a <map> line or a row edit). The client finds a cue by its id, so
// the id is the mod's choice; the plan stops on an id or a name the project has, and two mods on one id conflict.
//
// What the files get: the bank its new samples (the hash stays, so the project still takes it), and the project the
// new cue; both are written from the shipped files, as a replace is, and Revert puts the shipped ones back.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>A cue the mod adds to the event project.</summary>
    public sealed class CueOp
    {
        /// <summary>The one rule for cue names, in the view, the editor and the plan: two names are one when they differ only in letter case.</summary>
        public static readonly StringComparer NameRule = StringComparer.OrdinalIgnoreCase;
        public static bool SameName(string a, string b) { return NameRule.Equals(a ?? "", b ?? ""); }

        public int Id;
        public string Name;
        /// <summary>The bank the new samples go into (MS2Sound_BGM4.fsb).</summary>
        public string Bank;
        /// <summary>The cue whose theme, segment and link settings the new cue copies.</summary>
        public int Like;
        /// <summary>The MPEG files under the mod folder: Intro plays once (null: none), then Loop again and again.</summary>
        public string Intro, Loop;
        public int Line;
    }

    public sealed partial class Mod
    {
        /// <summary>New music cues (SoundCue.cs).</summary>
        public readonly List<CueOp> Cues = new List<CueOp>();

        public CueOp CueOf(int id)
        {
            foreach (CueOp c in Cues) if (c.Id == id) return c;
            return null;
        }

        /// <summary>The file under the mod that the editors put a new cue's sound in.</summary>
        public static string CueFile(int id, string part)
        {
            return FilesFolder + "/sound/cue/" + id.ToString(CultureInfo.InvariantCulture) + "_" + part + ".mp2";
        }

        /// <summary>The editors' new cue: the cue line, with the MPEG files kept in Files (saved with the mod). The
        /// files are read first, so a file that is no MPEG audio the client plays changes nothing. intro may be null.</summary>
        public CueOp SetCue(int id, string name, string bank, int like, byte[] intro, byte[] loop, string introLabel, string loopLabel)
        {
            if (id <= 0) throw new ModKitException("cue id " + id + ": a new cue needs an id above 0");
            if (string.IsNullOrEmpty(name) || name.IndexOf('\0') >= 0 || name.Trim() != name) throw new ModKitException("a new cue needs a name (without spaces at its ends)");
            if (!SoundReplace.IsBankName(bank)) throw new ModKitException("\"" + bank + "\" is no bank file name");
            if (loop == null) throw new ModKitException("a new cue needs a sound to loop");
            foreach (CueOp c in Cues) if (c.Id != id && CueOp.SameName(c.Name, name)) throw new ModKitException("the mod has a new cue named " + name + " already (cue " + c.Id + ")");
            Check(intro, introLabel);
            Check(loop, loopLabel);
            CueOp op = CueOf(id);
            if (op == null) { op = new CueOp { Id = id }; Cues.Add(op); }
            else
            {
                if (op.Intro != null) Files.Remove(op.Intro);
                if (op.Loop != null) Files.Remove(op.Loop);
            }
            op.Name = name; op.Bank = bank; op.Like = like;
            op.Intro = intro == null ? null : CueFile(id, "intro");
            op.Loop = CueFile(id, "loop");
            if (intro != null) Files[op.Intro] = intro;
            Files[op.Loop] = loop;
            return op;
        }

        static void Check(byte[] mpeg, string label)
        {
            if (mpeg == null) return;
            MpegAudio a = MpegAudio.Parse(mpeg, label);
            string no = a.NotForTheClient();
            if (no != null) throw new ModKitException(label + ": " + no);
            if (SoundReplace.RateIndex(a.Rate) < 0) throw new ModKitException(label + " is " + a.Rate + " Hz, a rate a bank cannot hold (use 44100 or 48000 Hz)");
        }

        /// <summary>Takes a new cue out. False when there was none.</summary>
        public bool TakeCue(int id)
        {
            CueOp op = CueOf(id);
            if (op == null) return false;
            Cues.Remove(op);
            if (op.Intro != null) Files.Remove(op.Intro);
            if (op.Loop != null) Files.Remove(op.Loop);
            return true;
        }

        sealed partial class Reader
        {
            internal CueOp ReadCue(XmlElem e)
            {
                Only(e, "id", "name", "bank", "like", "intro", "loop");
                var c = new CueOp { Name = Need(e, "name"), Bank = Need(e, "bank"), Loop = Need(e, "loop").Replace('\\', '/'), Line = e.Line };
                string intro = e.Attr("intro");
                if (intro != null)
                {
                    if (intro.Length == 0) throw Bad(e, "intro=\"\": leave the attribute out for a cue without an intro");
                    c.Intro = intro.Replace('\\', '/');
                }
                long id = Num(e, "id"), like = Num(e, "like");
                if (id == 0 || id > int.MaxValue) throw Bad(e, "id=\"" + e.Attr("id") + "\": a cue id is 1 to " + int.MaxValue);
                if (like > int.MaxValue) throw Bad(e, "like=\"" + e.Attr("like") + "\" is too large");
                c.Id = (int)id; c.Like = (int)like;
                if (c.Name.Trim() != c.Name) throw Bad(e, "name=\"" + c.Name + "\" has spaces at its ends");
                if (!SoundReplace.IsBankName(c.Bank)) throw Bad(e, "bank=\"" + c.Bank + "\" is the file name of a bank in the Sound folder (MS2Sound_BGM4.fsb)");
                if (e.Children.Count > 0) throw Bad(e, "<cue> has no child elements");
                return c;
            }
        }

        void WriteCues(StringBuilder sb)
        {
            foreach (CueOp c in Cues)
            {
                sb.Append("  <cue"); A(sb, "id", c.Id.ToString(CultureInfo.InvariantCulture)); A(sb, "name", c.Name); A(sb, "bank", c.Bank);
                A(sb, "like", c.Like.ToString(CultureInfo.InvariantCulture));
                if (c.Intro != null) A(sb, "intro", c.Intro);
                A(sb, "loop", c.Loop);
                sb.Append(" />\n");
            }
        }
    }

    /// <summary>A sample a plan adds to a bank.</summary>
    public sealed class PlanNewSample
    {
        public int Index;
        public string Name, ModId, File;
        public MpegAudio Audio;
        public bool Loop;
    }

    /// <summary>A cue a plan adds to the project.</summary>
    public sealed class PlanCue
    {
        public int Id, Like;
        public string Name, ModId;
        /// <summary>The bank's name in the project (MS2Sound_BGM4) and the samples' indexes there (Intro -1: none).</summary>
        public string Bank;
        public int Intro, Loop;
        public float IntroSeconds, LoopSeconds;
    }

    public static partial class SoundReplace
    {
        /// <summary>Writes the plan's file from src: a bank with its replaced and new samples, or the project with its
        /// new cues. Returns the SHA-1 of what was written.</summary>
        public static string WriteFile(PlanBank pb, string src, string dst, Action<ProgressInfo> progress)
        {
            if (!pb.Project) return Write(src, dst, Replaces(pb), Adds(pb), progress);
            ProgressUtil.Report(progress, "project", pb.Bank, 1, 1);
            byte[] b = Project(File.ReadAllBytes(src), pb, Path.GetFileName(src));
            File.WriteAllBytes(dst, b);
            return Sha1File(dst);
        }

        /// <summary>The project with the plan's cues added.</summary>
        public static byte[] Project(byte[] shipped, PlanBank pb, string label)
        {
            MusicGraph g = MusicGraph.Read(shipped, label);
            foreach (PlanCue c in pb.Cues) Add(g, c);
            return g.Bytes();
        }

        internal static void Add(MusicGraph g, PlanCue c)
        {
            MusicSample intro = c.Intro < 0 ? null : new MusicSample { Bank = c.Bank, Index = c.Intro };
            g.AddCue(c.Id, c.Name, intro, new MusicSample { Bank = c.Bank, Index = c.Loop }, c.IntroSeconds, c.LoopSeconds, c.Like);
        }

        public static List<NewSample> Adds(PlanBank b)
        {
            var l = new List<NewSample>();
            foreach (PlanNewSample s in b.Adds) l.Add(new NewSample { Name = s.Name, Audio = s.Audio, Loop = s.Loop });
            return l;
        }
    }

    public static partial class Planner
    {
        /// <summary>
        /// The cue lines of the mods: the id and the name must be free in the project, the cue like must play
        /// something, the bank must be one of the project's, the files MPEG audio the bank can hold. The new samples
        /// get the indexes after the bank's last. Two mods on one id (or one name): the one of the higher priority
        /// adds its cue. Each cue is added to the shipped project here already, so what would fail at Apply fails here.
        /// </summary>
        static void DoCues(Run run, IList<Mod> mods)
        {
            Plan plan = run.Plan;
            bool any = false;
            foreach (Mod m in mods) if (m.Cues.Count > 0) any = true;
            if (!any) return;
            string client = run.Data.RootFolder(LooseTarget.Root);
            string folder = SoundData.Find(client);
            if (folder == null)
            {
                foreach (Mod m in mods) if (m.Cues.Count > 0) plan.Msg(plan.Errors, m, null, null, "the mod adds music cues, but the client data has no Sound folder with " + SoundData.ProjectFile);
                return;
            }
            string rel = RelSound(client, folder);
            string fev = SoundData.ProjectFile, src;
            MusicGraph g;
            SoundProject sp;
            try
            {
                src = SoundReplace.ShippedPath(folder, fev);
                byte[] b = File.ReadAllBytes(src);
                g = MusicGraph.Read(b, fev);
                sp = SoundProject.Read(b, fev);
            }
            catch (Exception x) when (x is ModKitException || x is IOException || x is UnauthorizedAccessException)
            {
                foreach (Mod m in mods) if (m.Cues.Count > 0) plan.Msg(plan.Errors, m, null, null, "the mod adds music cues, but " + fev + " cannot be read: " + x.Message);
                return;
            }
            var pp = new PlanBank { Bank = fev, Folder = folder, RelFolder = rel, Source = src, Project = true, SourceSha1 = SoundReplace.Sha1File(src), Count = g.Cues.Count };
            var banks = new Dictionary<string, SoundBank>(StringComparer.OrdinalIgnoreCase);
            var bad = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // the shipped cues (the plan's graph gets the new ones as they are checked)
            var shippedCues = new Dictionary<int, string>(g.Cues);
            int shippedMax = g.MaxCue;
            var ids = new Dictionary<int, Mod>();
            var names = new Dictionary<string, Mod>(CueOp.NameRule);
            // from the highest priority down: the first mod to take an id or a name keeps it
            for (int i = mods.Count - 1; i >= 0; i--)
            {
                Mod m = mods[i];
                foreach (CueOp op in m.Cues)
                {
                    string where = "cue " + op.Id.ToString(CultureInfo.InvariantCulture) + " " + op.Name + " (" + ModLine.Of(op.Line) + ")";
                    string shipped;
                    if (shippedCues.TryGetValue(op.Id, out shipped)) { plan.Msg(plan.Errors, m, null, null, where + ": the project has a cue " + op.Id + " (" + shipped + "); a new cue needs an id of its own (above " + shippedMax + " is free)"); continue; }
                    string sameName = null;
                    foreach (string n in shippedCues.Values) if (CueOp.SameName(n, op.Name)) { sameName = n; break; }
                    if (sameName != null) { plan.Msg(plan.Errors, m, null, null, where + ": the project has a cue named " + sameName + " (names that differ only in letter case are one)"); continue; }
                    Mod w;
                    if (ids.TryGetValue(op.Id, out w))
                    {
                        if (w == m) plan.Msg(plan.Errors, m, null, null, where + ": the mod adds cue " + op.Id + " twice");
                        else plan.Conflict(m, null, null, where + ": mods " + w.Id + " and " + m.Id + " both add a cue " + op.Id, w, m);
                        continue;
                    }
                    if (names.TryGetValue(op.Name, out w))
                    {
                        if (w == m) plan.Msg(plan.Errors, m, null, null, where + ": the mod adds two cues named " + op.Name);
                        else plan.Conflict(m, null, null, where + ": mods " + w.Id + " and " + m.Id + " both add a cue named " + op.Name, w, m);
                        continue;
                    }
                    List<MusicSegment> model = shippedCues.ContainsKey(op.Like) ? g.Play(op.Like) : new List<MusicSegment>();
                    if (model.Count == 0) { plan.Msg(plan.Errors, m, null, null, where + ": like=\"" + op.Like + "\": " + (shippedCues.ContainsKey(op.Like) ? "cue " + op.Like + " plays nothing to copy the settings from" : "the project has no cue " + op.Like)); continue; }
                    SoundBank b = BankOf(folder, op.Bank, banks, bad);
                    if (b == null) { plan.Msg(plan.Errors, m, null, null, where + ": " + bad[op.Bank]); continue; }
                    string shortName = Path.GetFileNameWithoutExtension(op.Bank);
                    if (!sp.HasHash(b.Hash)) { plan.Msg(plan.Errors, m, null, null, where + ": " + fev + " does not hold the hash of " + op.Bank + " (it is not a bank of this project)"); continue; }
                    MpegAudio intro = null, loop;
                    try
                    {
                        if (op.Intro != null) intro = Audio(m, op.Intro);
                        loop = Audio(m, op.Loop);
                    }
                    catch (ModKitException x) { plan.Msg(plan.Errors, m, null, null, where + ": " + x.Message); continue; }
                    catch (Exception x) when (x is IOException || x is UnauthorizedAccessException) { plan.Msg(plan.Errors, m, null, null, where + ": " + x.Message); continue; }

                    PlanBank pb = null;
                    foreach (PlanBank x in plan.Banks) if (!x.Project && string.Equals(x.Bank, b.Name, StringComparison.OrdinalIgnoreCase)) pb = x;
                    bool made = pb == null;
                    if (made) pb = new PlanBank { Bank = b.Name, Folder = folder, RelFolder = rel, Source = b.Path, Hash = b.Hash, Count = b.Samples.Count };
                    var taken = new HashSet<string>(StringComparer.Ordinal);
                    foreach (SoundSample x in b.Samples) taken.Add(x.Name);
                    foreach (PlanNewSample x in pb.Adds) taken.Add(x.Name);
                    string introName = op.Name + "_Intro", loopName = op.Name + "_Loop";
                    if ((intro != null && taken.Contains(introName)) || taken.Contains(loopName))
                    {
                        plan.Msg(plan.Errors, m, null, null, where + ": " + op.Bank + " has a sample named " + (intro != null && taken.Contains(introName) ? introName : loopName) + " already");
                        continue;
                    }
                    int next = pb.Count + pb.Adds.Count;
                    var c = new PlanCue
                    {
                        Id = op.Id, Name = op.Name, ModId = m.Id, Like = op.Like, Bank = shortName,
                        Intro = intro == null ? -1 : next, Loop = intro == null ? next : next + 1,
                        IntroSeconds = intro == null ? 0 : (float)intro.Seconds, LoopSeconds = (float)loop.Seconds,
                    };
                    // what would fail at Apply fails here: the cue is added to the plan's copy of the project
                    try { SoundReplace.Add(g, c); }
                    catch (ModKitException x) { plan.Msg(plan.Errors, m, null, null, where + ": " + x.Message); continue; }
                    ids[op.Id] = m;
                    names[op.Name] = m;
                    if (made) plan.Banks.Add(pb);
                    // the loop chunks as the model's samples have them (the shipped loops of a bank have one)
                    if (intro != null)
                        pb.Adds.Add(new PlanNewSample { Index = c.Intro, Name = introName, ModId = m.Id, File = op.Intro, Audio = intro, Loop = model.Count > 1 && Loops(model[0], folder, banks, bad, false) });
                    pb.Adds.Add(new PlanNewSample { Index = c.Loop, Name = loopName, ModId = m.Id, File = op.Loop, Audio = loop, Loop = Loops(model[model.Count - 1], folder, banks, bad, true) });
                    pp.Cues.Add(c);
                    if (intro != null)
                        plan.Changes.Add(new PlanChange { Kind = ChangeKind.AddSample, ModId = m.Id, Mod = m, Archive = pb.Ref, Entry = "sample " + c.Intro + " " + introName, NewValue = op.Intro + ", " + intro.Describe() });
                    plan.Changes.Add(new PlanChange { Kind = ChangeKind.AddSample, ModId = m.Id, Mod = m, Archive = pb.Ref, Entry = "sample " + c.Loop + " " + loopName, NewValue = op.Loop + ", " + loop.Describe() });
                    plan.Changes.Add(new PlanChange { Kind = ChangeKind.AddCue, ModId = m.Id, Mod = m, Archive = pp.Ref, Entry = "cue " + op.Id + " " + op.Name,
                        NewValue = (intro == null ? "" : shortName + " #" + c.Intro + " once, then ") + shortName + " #" + c.Loop + " looped; settings of cue " + op.Like + " " + shippedCues[op.Like] });
                    plan.Msg(plan.Notes, m, pp.Ref, "cue " + op.Id + " " + op.Name, "a map plays the new cue when its fielddata bgm id is " + op.Id + " (a <map> line, or a row edit of table/fielddata.xml)");
                }
            }
            if (pp.Cues.Count > 0) plan.Banks.Add(pp);
            plan.Banks.Sort((x, y) => x.Project != y.Project ? (x.Project ? 1 : -1) : string.Compare(x.Bank, y.Bank, StringComparison.OrdinalIgnoreCase));
        }

        // does the segment's first sample have a loop chunk? dflt when its bank cannot be read
        static bool Loops(MusicSegment s, string folder, Dictionary<string, SoundBank> banks, Dictionary<string, string> bad, bool dflt)
        {
            if (s.Samples.Count == 0) return dflt;
            SoundBank b = BankOf(folder, s.Samples[0].Bank + ".fsb", banks, bad);
            if (b == null || s.Samples[0].Index < 0 || s.Samples[0].Index >= b.Samples.Count) return dflt;
            return b.Samples[s.Samples[0].Index].Loops;
        }

        static MpegAudio Audio(Mod m, string file)
        {
            MpegAudio a = MpegAudio.Parse(SampleBytes(m, new SampleOp { File = file }), file);
            string no = a.NotForTheClient();
            if (no != null) throw new ModKitException(file + ": " + no);
            if (SoundReplace.RateIndex(a.Rate) < 0) throw new ModKitException(file + " is " + a.Rate + " Hz, a rate a bank cannot hold (use 44100 or 48000 Hz)");
            return a;
        }

        // the Sound folder's path under the client's data root ("Sound")
        static string RelSound(string client, string folder)
        {
            string rel = "Sound";
            if (client != null)
            {
                string root = Path.GetFullPath(client).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string f = Path.GetFullPath(folder);
                if (f.StartsWith(root, StringComparison.OrdinalIgnoreCase)) rel = f.Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/');
            }
            return rel;
        }
    }
}
