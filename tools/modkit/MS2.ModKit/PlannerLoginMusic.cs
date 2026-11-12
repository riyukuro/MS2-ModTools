// The plan's checks of the login music (see LoginMusic.cs): every <login> row the mods change or add is read as the
// kms client ends up with it, as the client's LoginBgBgmTable::Load reads it. Errors: a month, bgmID or bgmParam that
// is not a comma list of numbers, a month outside 1..12, a priority that is not a number. Warnings: a row the client
// skips (no month, no bgmID), a cue count that differs from the param count, a cue the event project does not have
// (the mods' new cues count), and a month the shipped table gives music that the mods leave without a row.
using System;
using System.Collections.Generic;
using System.IO;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        static Mod LoginMusicMod(Run run)
        {
            Mod own = null;
            foreach (Mod m in run.Plan.Mods)
                foreach (ModOp op in m.Ops)
                {
                    var ed = op as EditOp;
                    if (ed != null && string.Equals(DataSet.NormalizeRef(ed.Archive), DataSet.NormalizeRef(LoginMusic.Archive), StringComparison.OrdinalIgnoreCase)
                        && string.Equals((ed.Entry ?? "").Replace('\\', '/'), LoginMusic.Entry, StringComparison.OrdinalIgnoreCase)) own = m;
                }
            return own;
        }

        static void LoginMusicChecks(Run run)
        {
            Mod mod = LoginMusicMod(run);
            if (mod == null || !run.Data.Exists(LoginMusic.Archive)) return;
            Plan plan = run.Plan;
            XmlDoc fin = FinalDoc(run, LoginMusic.Archive, LoginMusic.Entry), was = OrigDoc(run, LoginMusic.Archive, LoginMusic.Entry);
            if (fin == null || fin.Root == null) return;
            var shipped = new Dictionary<string, string>(StringComparer.Ordinal);
            if (was != null && was.Root != null)
                foreach (XmlElem e in was.Root.Children) if (e.Name == LoginMusic.Row) shipped[e.Attr(LoginMusic.Background) ?? ""] = RowText(e);
            HashSet<int> cues = null;
            bool cuesRead = false;
            foreach (XmlElem e in fin.Root.Children)
            {
                if (e.Name != LoginMusic.Row) continue;
                string bg = e.Attr(LoginMusic.Background) ?? "", text;
                if (shipped.TryGetValue(bg, out text) && text == RowText(e)) continue;
                string who = "login row " + (bg.Length == 0 ? "(no " + LoginMusic.Background + ")" : bg);
                if (bg.Length == 0) { plan.Msg(plan.Warnings, mod, LoginMusic.Archive, LoginMusic.Entry, who + ": the client skips a row with no " + LoginMusic.Background); continue; }
                bool skipped = false;
                foreach (string f in LoginMusic.Fields)
                {
                    string v = e.Attr(f) ?? "";
                    string bad = LoginMusic.Problem(f, v);
                    if (bad == null) continue;
                    // an empty month or bgmID is read: the client skips the row
                    bool skip = v.Trim().Length == 0 && (f == LoginMusic.Month || f == LoginMusic.Bgm);
                    plan.Msg(skip ? plan.Warnings : plan.Errors, mod, LoginMusic.Archive, LoginMusic.Entry, who + ": " + bad);
                    if (skip) skipped = true;
                }
                if (skipped) continue;
                string note = LoginMusic.Note(e.Attr(LoginMusic.Bgm), e.Attr(LoginMusic.Param));
                if (note != null) plan.Msg(plan.Warnings, mod, LoginMusic.Archive, LoginMusic.Entry, who + ": " + note);
                List<uint> ids = LoginMusic.List(e.Attr(LoginMusic.Bgm));
                if (ids == null) continue;
                if (!cuesRead) { cues = ProjectCues(run); cuesRead = true; }
                if (cues == null) continue;
                foreach (uint c in ids)
                    if (c != 0 && (c > int.MaxValue || !cues.Contains((int)c)))
                        plan.Msg(plan.Warnings, mod, LoginMusic.Archive, LoginMusic.Entry, who + ": cue " + c + " is not in " + SoundData.ProjectFile + " (nor a cue the mods add): the login map plays nothing then");
            }
            // the months the shipped table gives music: one left with no row plays the map's own music
            if (was == null || was.Root == null) return;
            for (int m = 1; m <= 12; m++)
                if (HasMonth(was.Root, m) && !HasMonth(fin.Root, m))
                    plan.Msg(plan.Warnings, mod, LoginMusic.Archive, LoginMusic.Entry, "month " + m + " has no login row the client keeps after the mods: the login map plays its own fielddata bgm id then");
        }

        // a row of the month the client keeps (a background, a month list and a bgmID)
        static bool HasMonth(XmlElem root, int month)
        {
            foreach (XmlElem e in root.Children)
            {
                if (e.Name != LoginMusic.Row || (e.Attr(LoginMusic.Background) ?? "").Length == 0 || (e.Attr(LoginMusic.Bgm) ?? "").Trim().Length == 0) continue;
                List<uint> ms = LoginMusic.List(e.Attr(LoginMusic.Month));
                if (ms != null && ms.Contains((uint)month)) return true;
            }
            return false;
        }

        // the cue ids of the client's event project and the mods' new cues (null: no project to check against)
        static HashSet<int> ProjectCues(Run run)
        {
            string folder = SoundData.Find(run.Data.RootFolder(LooseTarget.Root));
            if (folder == null) return null;
            SoundProject sp;
            try { sp = SoundProject.Read(File.ReadAllBytes(SoundReplace.ShippedPath(folder, SoundData.ProjectFile)), SoundData.ProjectFile); }
            catch (Exception x) when (x is ModKitException || x is IOException || x is UnauthorizedAccessException) { return null; }
            var l = new HashSet<int>();
            foreach (MusicCue c in sp.Cues) l.Add(c.Id);
            foreach (Mod m in run.Plan.Mods) foreach (CueOp c in m.Cues) l.Add(c.Id);
            return l;
        }
    }
}
