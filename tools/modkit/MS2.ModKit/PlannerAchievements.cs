// The plan's checks of achievements (achieve/<id>.xml of each target's Xml.m2d, see Achievements.cs): every file the
// mods add or change is read as it ends up in each target and checked with AchieveRules.CheckFile. A problem the
// shipped file has already is not this plan's doing and is not said. On top of the editor's checks the plan knows the
// final data: a reward item that is no item of the target, a reward title that table/titletag.xml does not have, and
// an achievement the server has and a client does not (never shown) or a client has and the server does not (never
// granted). The server's problems block Apply (its data ingest fails); the clients' are warnings.
using System;
using System.Collections.Generic;
using System.Globalization;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        static HashSet<string> TitleIds(Run run, Profile p)
        {
            XmlDoc d = FinalDoc(run, p.ArchiveIn("x:Xml.m2d"), AchieveRules.TitleEntry);
            if (d == null || d.Root == null) return null;
            var set = new HashSet<string>();
            foreach (XmlElem e in d.Root.Children) if (e.Name == "key") set.Add((e.Attr("id") ?? "").Trim());
            return set;
        }

        static List<string> AchieveProblems(Run run, Profile p, XmlDoc doc, long id, Dictionary<Profile, object[]> refs, out List<string> errors)
        {
            errors = new List<string>();
            var warnings = new List<string>();
            bool server = p == Profile.Server;
            object[] r;
            if (!refs.TryGetValue(p, out r))
            {
                XmlDoc cat = FinalDoc(run, p.ArchiveIn("x:Xml.m2d"), AchieveRules.CategoryEntry);
                refs[p] = r = new object[] { AchieveRules.CategoryTags(cat), null, null, false };
            }
            AchieveRules.CheckFile(doc, id, server, (HashSet<string>)r[0], errors, warnings);
            XmlElem top = null;
            if (doc != null && doc.Root != null) foreach (XmlElem e in doc.Root.Children) if (e.Name == AchieveRules.Element) { top = e; break; }
            if (top == null) return warnings;
            foreach (XmlElem g in AchieveRules.Grades(top))
            {
                XmlElem rw = AchieveRules.Child(g, "reward");
                if (rw == null) continue;
                string type = (rw.Attr("type") ?? "").Trim(), code = AchieveRules.Neutral("reward@code", rw.Attr("code"));
                long n;
                if (code.Length == 0 || !long.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out n)) continue;
                string at = "grade " + (g.Attr("value") ?? "?");
                if (string.Equals(type, "item", StringComparison.OrdinalIgnoreCase))
                {
                    if (r[1] == null) r[1] = Items(run, p) ?? (object)new HashSet<long>();
                    var items = (HashSet<long>)r[1];
                    if (items.Count > 0 && !items.Contains(n)) (server ? errors : warnings).Add(at + ": the reward item " + n + " is not an item of the " + p.Title);
                }
                else if (string.Equals(type, "title", StringComparison.OrdinalIgnoreCase))
                {
                    if (!(bool)r[3]) { r[2] = TitleIds(run, p); r[3] = true; }
                    var titles = (HashSet<string>)r[2];
                    if (titles != null && !titles.Contains(code)) warnings.Add(at + ": the reward title " + code + " is not in " + AchieveRules.TitleEntry + " of the " + p.Title);
                }
            }
            return warnings;
        }

        static void AchievementChecks(Run run)
        {
            Plan plan = run.Plan;
            // the achieve/ entries the plan touches, per target
            var touched = new List<EntryState>();
            foreach (EntryState st in run.Order)
            {
                if (!AchieveRules.IsEntry(st.Name)) continue;
                Profile p = Profile.OfArchive(st.Archive);
                if (p == null || !string.Equals(st.Archive, p.ArchiveIn("x:Xml.m2d"), StringComparison.OrdinalIgnoreCase)) continue;
                byte[] fin = st.DeletedBy != null ? null : st.Final ?? st.Base;
                if (fin == null && st.Original == null) continue;
                if (fin != null && st.Original != null && M2dWriter.BytesEqual(fin, st.Original)) continue;
                touched.Add(st);
            }
            if (touched.Count == 0) return;
            // the server before the global client: a file mirrored to the latter says a thing once
            touched.Sort((x, y) => Rank(x) - Rank(y));
            var said = new HashSet<string>();
            var refs = new Dictionary<Profile, object[]>();
            bool hasServer = run.Data.Has(Profile.Server);
            var ids = new List<long>();
            var serverBytes = new Dictionary<long, byte[]>();
            foreach (EntryState st in touched)
            {
                Profile p = Profile.OfArchive(st.Archive);
                long id = AchieveRules.IdOf(st.Name);
                if (!ids.Contains(id)) ids.Add(id);
                Mod mod = st.DeletedBy ?? st.BaseMod;
                byte[] fin = st.DeletedBy != null ? null : st.Final ?? st.Base;
                if (fin == null) continue;
                if (p == Profile.Server) serverBytes[id] = fin;
                // the server's file mirrored to the global client: the server's messages say it
                byte[] sb;
                if (hasServer && p == Profile.GmsClient && serverBytes.TryGetValue(id, out sb) && M2dWriter.BytesEqual(sb, fin)) continue;
                XmlDoc doc, orig = null;
                try { doc = XmlDoc.Parse(fin, st.Archive + " " + st.Name); }
                catch (ModKitException x) { plan.Msg(plan.Errors, mod, st.Archive, st.Name, "achievement " + id + ": " + x.Message); continue; }
                if (st.Original != null)
                    try { orig = XmlDoc.Parse(st.Original, st.Archive + " " + st.Name); } catch (ModKitException) { orig = null; }
                List<string> errors, oldErrors = new List<string>();
                List<string> warnings = AchieveProblems(run, p, doc, id, refs, out errors);
                List<string> oldWarnings = orig == null ? new List<string>() : AchieveProblems(run, p, orig, id, refs, out oldErrors);
                bool mirror = hasServer && p == Profile.GmsClient;
                foreach (bool isError in new[] { true, false })
                    foreach (string t in isError ? errors : warnings)
                    {
                        if ((isError ? oldErrors : oldWarnings).Contains(t)) continue;
                        string key = id + "\n" + t.Replace(p.Title, "");
                        if (mirror && said.Contains(key)) continue;
                        if (p == Profile.Server) said.Add(key);
                        plan.Msg(isError ? plan.Errors : plan.Warnings, mod, st.Archive, st.Name, "achievement " + id + ": " + t);
                    }
            }
            if (!hasServer) return;
            // client and server read achievements from their own files
            foreach (long id in ids)
            {
                string entry = AchieveRules.EntryOf(id);
                bool onServer = Present(run, Profile.Server, entry, false), wasServer = Present(run, Profile.Server, entry, true);
                foreach (Profile c in new[] { Profile.KmsClient, Profile.GmsClient })
                {
                    if (!run.Data.Has(c)) continue;
                    bool onClient = Present(run, c, entry, false), wasClient = Present(run, c, entry, true);
                    if (onServer == onClient || (onServer == wasServer && onClient == wasClient)) continue;
                    EntryState st = null;
                    foreach (EntryState t in touched) if (string.Equals(t.Name, entry, StringComparison.OrdinalIgnoreCase) && (st == null || Profile.OfArchive(t.Archive) == Profile.Server)) st = t;
                    Mod mod = st == null ? null : st.DeletedBy ?? st.BaseMod;
                    string archive = (onServer ? Profile.Server : c).ArchiveIn("x:Xml.m2d");
                    plan.Msg(plan.Warnings, mod, archive, entry, onServer
                        ? "achievement " + id + " is on the server but not in the " + c.Title + ": the server grants it, the client never shows it"
                        : "achievement " + id + " is in the " + c.Title + " but not on the server: the client shows it, the server never grants it");
                }
            }
        }

        static int Rank(EntryState st)
        {
            Profile p = Profile.OfArchive(st.Archive);
            return p == Profile.Server ? 0 : p == Profile.GmsClient ? 2 : 1;
        }

        // whether the target has the entry as the plan leaves it (or as shipped)
        static bool Present(Run run, Profile p, string entry, bool shipped)
        {
            string archive = p.ArchiveIn("x:Xml.m2d");
            if (!run.Data.Exists(archive)) return false;
            M2dArchive arch = run.Data.Open(archive);
            M2dEntry e = arch.Find(entry);
            if (shipped) return e != null;
            EntryState st = null;
            foreach (EntryState t in run.Order)
                if (string.Equals(t.Archive, archive, StringComparison.OrdinalIgnoreCase) && string.Equals(t.Name, e != null ? e.Name : entry, StringComparison.OrdinalIgnoreCase)) { st = t; break; }
            if (st == null) return e != null;
            return st.DeletedBy == null && (st.Final ?? st.Base) != null;
        }
    }
}
