// The targets of a data set as text, for the command line and the editor views: which profiles it has, and what
// a plan or an apply does on each of them. A target is a profile the data set has a folder for (see Profile.cs);
// a root that is no profile is listed under its own name.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MS2.ModKit
{
    public static class Targets
    {
        /// <summary>The roots of the data set in the profiles' order (kms-client, gms-client, server), then any other root.</summary>
        public static List<string> Roots(DataSet data)
        {
            var list = new List<string>();
            foreach (Profile p in data.Profiles) list.Add(p.Root);
            var rest = new List<string>();
            foreach (string r in data.Roots) if (!list.Contains(r)) rest.Add(r);
            rest.Sort(StringComparer.Ordinal);
            list.AddRange(rest);
            return list;
        }

        /// <summary>The profile name of a root ("client" -> "kms-client"); other roots by their own name.</summary>
        public static string NameOf(string root)
        {
            Profile p = Profile.Find(root);
            return p != null && p.Root == Profile.CanonicalRoot(root) ? p.Name : root;
        }

        static string RootOfRef(string archive)
        {
            if (string.IsNullOrEmpty(archive) || archive.IndexOf(':') <= 0) return null;
            return DataSet.RootOf(DataSet.NormalizeRef(archive));
        }

        /// <summary>"targets: kms-client (Korean client), server; no gms-client (global client)".</summary>
        public static string Line(DataSet data)
        {
            var have = new List<string>();
            foreach (string r in Roots(data))
            {
                Profile p = Profile.Find(r);
                have.Add(p != null && p.Root == r && p.Title != p.Name ? p.Name + " (" + p.Title + ")" : NameOf(r));
            }
            var missing = new List<string>();
            foreach (Profile p in Profile.All) if (!data.Has(p)) missing.Add(p.Name + " (" + p.Title + ")");
            string s = "targets: " + (have.Count == 0 ? "none" : string.Join(", ", have.ToArray()));
            if (missing.Count > 0) s += "; not in this data set: " + string.Join(", ", missing.ToArray());
            return s;
        }

        /// <summary>"3 entries of 1 archive(s)", "2 sample(s) of 1 bank(s)", or both joined with "and".</summary>
        public static string Where(int entries, int archives, int samples, int banks) { return Where(entries, archives, samples, banks, 0); }

        /// <summary>As above, with " and 1 new cue(s)" for the cues added to the project (samples and banks: the
        /// replaced and new samples and the banks they are in, the project not counted).</summary>
        public static string Where(int entries, int archives, int samples, int banks, int cues)
        {
            string a = entries + (entries == 1 ? " entry" : " entries") + " of " + archives + " archive(s)";
            string b = samples + " sample(s) of " + banks + " bank(s)";
            string s = banks == 0 ? a : archives == 0 ? b : a + " and " + b;
            return cues == 0 ? s : s + " and " + cues + " new cue(s)";
        }

        /// <summary>The replaced and new samples, the banks and the new cues of a plan.</summary>
        public static void Sounds(IEnumerable<PlanBank> banks, out int samples, out int bankCount, out int cues)
        {
            samples = 0; bankCount = 0; cues = 0;
            foreach (PlanBank b in banks)
            {
                if (b.Project) { cues += b.Cues.Count; continue; }
                bankCount++;
                samples += b.Samples.Count + b.Adds.Count;
            }
        }

        static int Count(List<PlanMessage> list, string root)
        {
            int n = 0;
            foreach (PlanMessage m in list) if (RootOfRef(m.Archive) == root) n++;
            return n;
        }

        /// <summary>One line per target: what the plan changes there, and its errors, conflicts and warnings there.</summary>
        public static List<string> PlanLines(Plan p, DataSet data)
        {
            var lines = new List<string>();
            List<string> roots = Roots(data);
            foreach (PlanArchive a in p.Archives) { string r = RootOfRef(a.Archive); if (r != null && !roots.Contains(r)) roots.Add(r); }
            foreach (string root in roots)
            {
                int changes = 0, synced = 0, mirrored = 0, entries = 0, archives = 0;
                foreach (PlanChange c in p.Changes)
                {
                    if (RootOfRef(c.Archive) != root) continue;
                    changes++;
                    if (c.Synced) synced++;
                    if (c.Mirrored) mirrored++;
                }
                foreach (PlanArchive a in p.Archives)
                {
                    if (RootOfRef(a.Archive) != root) continue;
                    archives++;
                    entries += a.Put.Count + a.Delete.Count;
                }
                int banks, samples, cues;
                Sounds(p.Banks.FindAll(b => RootOfRef(b.Ref) == root), out samples, out banks, out cues);
                int e = Count(p.Errors, root), k = Count(p.Conflicts, root), w = Count(p.Warnings, root);
                var sb = new StringBuilder(NameOf(root)).Append(": ");
                if (entries == 0 && changes == 0 && samples == 0 && cues == 0) sb.Append("nothing to change");
                else
                {
                    sb.Append(changes).Append(" change(s)");
                    if (synced > 0 || mirrored > 0)
                    {
                        var how = new List<string>();
                        if (synced > 0) how.Add(synced + " synced by the field maps");
                        if (mirrored > 0) how.Add(mirrored + " mirrored");
                        sb.Append(" (").Append(string.Join(", ", how.ToArray())).Append(')');
                    }
                    sb.Append(" in ").Append(Where(entries, archives, samples, banks, cues));
                }
                if (e + k + w > 0) sb.Append("; ").Append(e).Append(" error(s), ").Append(k).Append(" conflict(s), ").Append(w).Append(" warning(s)");
                lines.Add(sb.ToString());
            }
            return lines;
        }

        /// <summary>One line per target and file: what an apply or a revert wrote or removed there.</summary>
        /// <param name="workspaceDir">the workspace the result is for; null when there is none</param>
        public static List<string> ResultLines(ApplyResult r, DataSet data, string workspaceDir)
        {
            var lines = new List<string>();
            List<string> roots = Roots(data);
            var byRoot = new Dictionary<string, List<string>>();
            foreach (string root in roots) byRoot[root] = new List<string>();
            Action<string, string> add = delegate(string root, string text)
            {
                if (root == null) root = "";
                List<string> l;
                if (!byRoot.TryGetValue(root, out l)) { byRoot[root] = l = new List<string>(); roots.Add(root); }
                l.Add(text);
            };
            foreach (ApplyOutput o in r.Outputs)
                add(RootOfRef(o.Archive), o.Copied ? "wrote " + o.Path + " (copied as it is)"
                    : "wrote " + o.Path + " (" + o.Replaced + " replaced, " + o.Added + " added, " + o.Deleted + " deleted, " + o.Kept + " kept)");
            foreach (string f in r.Written) add(LooseTarget.Root, "wrote " + f);
            foreach (string f in r.Removed) add(RootUnder(f, workspaceDir), "removed " + f);
            foreach (string f in r.Left) add(LooseTarget.Root, "left alone, changed since it was written: " + f);
            foreach (string root in roots)
            {
                string name = root.Length == 0 ? "other" : NameOf(root);
                if (byRoot[root].Count == 0) lines.Add(name + ": nothing written or removed");
                else foreach (string t in byRoot[root]) lines.Add(name + ": " + t);
            }
            return lines;
        }

        // the root of a file the workspace holds (<workspace>/<root>/...); a file elsewhere is a loose file of the client
        static string RootUnder(string file, string workspaceDir)
        {
            if (workspaceDir == null) return LooseTarget.Root;
            string ws = Path.GetFullPath(workspaceDir).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
            string f = Path.GetFullPath(file);
            if (!f.StartsWith(ws, StringComparison.OrdinalIgnoreCase)) return LooseTarget.Root;
            string rest = f.Substring(ws.Length);
            int s = rest.IndexOfAny(new[] { '/', '\\' });
            return s < 0 ? rest : rest.Substring(0, s);
        }
    }
}
