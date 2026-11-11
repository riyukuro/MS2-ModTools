// A workspace is the folder the patched COPIES of the archives are written to:
//   <workspace>/<root>/<path of the archive under its data root>     for example  ws/client/Xml.m2d + Xml.m2h
//   <workspace>/modkit-state.xml                                      which mods the copies were built from
// Every apply rebuilds the copies from the original archives, so the result never depends on what was applied
// before, and a revert is an apply without the mod. The originals are only ever read.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public sealed class ApplyOptions
    {
        /// <summary>Field maps for the two-sided sync; null = the maps inside the library.</summary>
        public FieldMaps Maps;
        /// <summary>true: the plan writes nothing to the other side of a mapped field.</summary>
        public bool NoSync;

        public PlanOptions PlanOptions() { return new PlanOptions { Schemas = Schemas, Progress = Progress, Maps = Maps, NoSync = NoSync }; }

        /// <summary>Write even when two mods touch the same row or attribute (the later mod wins).</summary>
        public bool AllowConflicts;
        /// <summary>Archives to copy into the workspace although no mod touches them.</summary>
        public readonly List<string> AlsoCopy = new List<string>();
        /// <summary>After writing, compare the stored bytes of every untouched entry with the original (slower).</summary>
        public bool VerifyAll;
        public SchemaSet Schemas;
        public Action<ProgressInfo> Progress;
        /// <summary>When set, only archives of these data roots ("client", "server") are written; the copies of
        /// the other roots are removed from the workspace. Null writes every root.</summary>
        public List<string> Roots;
    }

    public sealed class ApplyOutput
    {
        public string Archive, Path, HeaderPath;
        public int Kept, Replaced, Added, Deleted;
        public bool Copied;             // an untouched archive, copied as it is
        public long Milliseconds;
        public int Verified;            // entries read back and compared
    }

    public sealed class ApplyResult
    {
        public Plan Plan;
        public bool Applied;
        public string Reason = "";      // why nothing was written
        public readonly List<ApplyOutput> Outputs = new List<ApplyOutput>();
        public readonly List<string> Removed = new List<string>();     // copies of an earlier apply that are no longer needed
        public readonly List<string> Written = new List<string>();     // loose files written into a game folder (LooseTarget)
        public readonly List<string> Left = new List<string>();        // loose files of the record that were changed since and are left alone
        public ServerStepResult Server;                                 // what followed on the server's side (ServerStep); null when nothing is set
    }

    public sealed class WorkspaceMod { public string Id, Path, Version; }
    public sealed class WorkspaceArchive { public string Archive, File; public bool Copied; }

    public sealed class WorkspaceState
    {
        public string Applied = "";
        public readonly List<WorkspaceMod> Mods = new List<WorkspaceMod>();
        public readonly List<WorkspaceArchive> Archives = new List<WorkspaceArchive>();
    }

    public sealed class Workspace
    {
        public const string StateFile = "modkit-state.xml";

        public readonly string Dir;

        public Workspace(string dir)
        {
            if (string.IsNullOrEmpty(dir)) throw new ModKitException("no workspace folder given");
            Dir = System.IO.Path.GetFullPath(dir);
        }

        static string Slash(string p) { return p.TrimEnd('/', '\\') + System.IO.Path.DirectorySeparatorChar; }

        static bool Inside(string path, string folder)
        {
            StringComparison cmp = System.IO.Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return Slash(path).StartsWith(Slash(folder), cmp);
        }

        /// <summary>Where the copy of an archive goes.</summary>
        public string PathOf(string archive)
        {
            archive = DataSet.NormalizeRef(archive);
            string p = System.IO.Path.Combine(Dir, DataSet.RootOf(archive));
            foreach (string part in DataSet.PathOf(archive).Split('/')) p = System.IO.Path.Combine(p, part);
            return p;
        }

        void Guard(DataSet data)
        {
            foreach (string root in data.Roots)
            {
                string folder = data.RootFolder(root);
                if (Inside(Dir, folder) || Inside(folder, Dir))
                    throw new ModKitException("the workspace " + Dir + " and the data root \"" + root + "\" (" + folder + ") are inside one another; the patched copies must not land in the original data");
            }
        }

        // ---- state ----

        public WorkspaceState ReadState()
        {
            string f = System.IO.Path.Combine(Dir, StateFile);
            if (!File.Exists(f)) return null;
            XmlDoc doc = XmlDoc.Parse(File.ReadAllBytes(f), f);
            var s = new WorkspaceState();
            if (doc.Root == null || doc.Root.Name != "modkit-state") throw new ModKitException(f + ": not a workspace state file");
            s.Applied = doc.Root.Attr("applied") ?? "";
            foreach (XmlElem e in doc.Root.Children)
            {
                if (e.Name == "mod") s.Mods.Add(new WorkspaceMod { Id = e.Attr("id"), Path = e.Attr("path") ?? "", Version = e.Attr("version") ?? "" });
                else if (e.Name == "archive") s.Archives.Add(new WorkspaceArchive { Archive = e.Attr("name"), File = e.Attr("file"), Copied = e.Attr("copied") == "true" });
            }
            return s;
        }

        void WriteState(WorkspaceState s)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<modkit-state version=\"1\" applied=\"").Append(XmlDoc.EscapeAttr(s.Applied, '"')).Append("\">\n");
            foreach (WorkspaceMod m in s.Mods)
                sb.Append("  <mod id=\"").Append(XmlDoc.EscapeAttr(m.Id, '"')).Append("\" version=\"").Append(XmlDoc.EscapeAttr(m.Version, '"'))
                  .Append("\" path=\"").Append(XmlDoc.EscapeAttr(m.Path, '"')).Append("\"/>\n");
            foreach (WorkspaceArchive a in s.Archives)
                sb.Append("  <archive name=\"").Append(XmlDoc.EscapeAttr(a.Archive, '"')).Append("\" file=\"").Append(XmlDoc.EscapeAttr(a.File, '"'))
                  .Append("\" copied=\"").Append(a.Copied ? "true" : "false").Append("\"/>\n");
            sb.Append("</modkit-state>\n");
            Directory.CreateDirectory(Dir);
            string f = System.IO.Path.Combine(Dir, StateFile);
            File.WriteAllBytes(f + ".tmp", new UTF8Encoding(false).GetBytes(sb.ToString()));
            if (File.Exists(f)) File.Delete(f);
            File.Move(f + ".tmp", f);
        }

        // ---- apply ----

        /// <summary>Plans the mods against the original data and writes the patched copies. Nothing is written
        /// when the plan has errors, or conflicts that are not allowed.</summary>
        public ApplyResult Apply(DataSet data, IList<Mod> mods, ApplyOptions options)
        {
            if (options == null) options = new ApplyOptions();
            Guard(data);
            return Apply(data, mods, Planner.Build(data, mods, options.PlanOptions()), options);
        }

        /// <summary>The same with a plan that was built already from these mods.</summary>
        public ApplyResult Apply(DataSet data, IList<Mod> mods, Plan plan, ApplyOptions options)
        {
            if (options == null) options = new ApplyOptions();
            Guard(data);
            var res = new ApplyResult { Plan = plan };
            if (res.Plan.HasErrors) { res.Reason = res.Plan.Errors.Count + " error(s)"; return res; }
            if (res.Plan.Undecided > 0) { res.Reason = res.Plan.Undecided + Plan.UndecidedReason; return res; }
            if (res.Plan.HasConflicts && !options.AllowConflicts) { res.Reason = res.Plan.Conflicts.Count + " conflict(s)"; return res; }

            var targets = new List<PlanArchive>(res.Plan.Archives);
            if (options.Roots != null)
                targets.RemoveAll(delegate(PlanArchive t) { return !options.Roots.Contains(DataSet.RootOf(t.Archive)); });
            foreach (string extra in options.AlsoCopy)
            {
                string name = DataSet.NormalizeRef(extra);
                if (options.Roots != null && !options.Roots.Contains(DataSet.RootOf(name))) continue;
                if (res.Plan.Find(name) == null && targets.Find(delegate(PlanArchive t) { return t.Archive == name; }) == null)
                    targets.Add(new PlanArchive { Archive = name });
            }

            WorkspaceState old = ReadState();
            string tmpRoot = System.IO.Path.Combine(Dir, ".tmp");
            var state = new WorkspaceState { Applied = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) };
            foreach (Mod m in mods) state.Mods.Add(new WorkspaceMod { Id = m.Id, Version = m.Version, Path = m.Dir ?? "" });
            try
            {
                foreach (PlanArchive pa in targets)
                {
                    var sw = Stopwatch.StartNew();
                    M2dArchive src = data.Open(pa.Archive);
                    string dst = PathOf(pa.Archive);
                    if (M2dWriter.SamePath(dst, src.Path) || M2dWriter.SamePath(M2dArchive.HeaderPathOf(dst), src.HeaderPath))
                        throw new ModKitException("refusing to write over the original archive " + src.Path);
                    string rel = DataSet.RootOf(pa.Archive) + "/" + DataSet.PathOf(pa.Archive);
                    string tmp = System.IO.Path.Combine(tmpRoot, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(tmp));
                    var o = new ApplyOutput { Archive = pa.Archive, Path = dst, HeaderPath = M2dArchive.HeaderPathOf(dst) };
                    if (pa.Put.Count == 0 && pa.Delete.Count == 0)
                    {
                        File.Copy(src.Path, tmp, true);
                        File.Copy(src.HeaderPath, M2dArchive.HeaderPathOf(tmp), true);
                        o.Copied = true; o.Kept = src.Count;
                    }
                    else
                    {
                        M2dWriteResult w = M2dWriter.Rewrite(src, tmp, pa.ToChanges(), options.Progress);
                        o.Kept = w.Kept; o.Replaced = w.Replaced; o.Added = w.Added; o.Deleted = w.Deleted;
                        o.Verified = Verify(src, tmp, pa, options);
                    }
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst));
                    Replace(tmp, dst);
                    Replace(M2dArchive.HeaderPathOf(tmp), o.HeaderPath);
                    o.Milliseconds = sw.ElapsedMilliseconds;
                    res.Outputs.Add(o);
                    state.Archives.Add(new WorkspaceArchive { Archive = pa.Archive, File = rel, Copied = o.Copied });
                }
            }
            finally
            {
                if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true);
            }

            if (old != null)
                foreach (WorkspaceArchive a in old.Archives)
                {
                    if (state.Archives.Find(delegate(WorkspaceArchive n) { return n.Archive == a.Archive; }) != null) continue;
                    RemoveCopy(a.Archive, res.Removed);
                }
            if (state.Archives.Count == 0 && options.Roots != null)
            {
                // a root filter left nothing for the workspace: no state, as after a revert
                string sf = System.IO.Path.Combine(Dir, StateFile);
                if (File.Exists(sf)) File.Delete(sf);
            }
            else WriteState(state);
            res.Applied = true;
            return res;
        }

        static void Replace(string from, string to)
        {
            if (File.Exists(to)) File.Delete(to);
            File.Move(from, to);
        }

        void RemoveCopy(string archive, List<string> removed)
        {
            string p = PathOf(archive);
            foreach (string f in new[] { p, M2dArchive.HeaderPathOf(p) })
                if (File.Exists(f)) { File.Delete(f); removed.Add(f); }
        }

        // reads the written archive back: every put entry holds the planned bytes, every deleted one is gone,
        // the count is right; with VerifyAll every other entry holds the original's stored bytes
        static int Verify(M2dArchive src, string written, PlanArchive pa, ApplyOptions options)
        {
            int checkedCount = 0;
            using (M2dArchive a = M2dArchive.Open(written))
            {
                var touched = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                int added = 0, deleted = 0;
                foreach (string d in pa.Delete)
                {
                    touched[d] = true;
                    if (src.Find(d) != null) deleted++;
                    if (a.Find(d) != null) throw new ModKitException("read-back of " + written + ": the deleted entry " + d + " is still there");
                }
                foreach (KeyValuePair<string, byte[]> p in pa.Put)
                {
                    if (!touched.ContainsKey(p.Key) && src.Find(p.Key) == null) added++;
                    touched[p.Key] = true;
                    M2dEntry e = a.Find(p.Key);
                    if (e == null) throw new ModKitException("read-back of " + written + ": the entry " + p.Key + " is missing");
                    if (!M2dWriter.BytesEqual(a.Read(e), p.Value)) throw new ModKitException("read-back of " + written + ": the entry " + p.Key + " does not hold the planned bytes");
                    checkedCount++;
                }
                if (a.Count != src.Count + added - deleted)
                    throw new ModKitException("read-back of " + written + ": " + a.Count + " entries, expected " + (src.Count + added - deleted));
                if (options.VerifyAll)
                {
                    int n = 0;
                    foreach (M2dEntry e in src.Entries)
                    {
                        ProgressUtil.Report(options.Progress, "verify", e.Name, ++n, src.Count);
                        if (touched.ContainsKey(e.Name)) continue;
                        M2dEntry w = a.Find(e.Name);
                        if (w == null) throw new ModKitException("read-back of " + written + ": the untouched entry " + e.Name + " is missing");
                        if (w.Flag != e.Flag || w.Size != e.Size || !M2dWriter.BytesEqual(a.ReadRaw(w), src.ReadRaw(e)))
                            throw new ModKitException("read-back of " + written + ": the untouched entry " + e.Name + " differs from the original");
                        checkedCount++;
                    }
                }
            }
            return checkedCount;
        }

        // ---- revert ----

        /// <summary>Rebuilds the copies from the originals without one mod; with modId null (or when it was the
        /// only mod) removes the copies. Returns null for "everything removed".</summary>
        public ApplyResult Revert(DataSet data, string modId, ApplyOptions options)
        {
            WorkspaceState st = ReadState();
            if (st == null) throw new ModKitException("nothing is applied in " + Dir + " (no " + StateFile + ")");
            var keep = new List<WorkspaceMod>();
            bool found = modId == null;
            foreach (WorkspaceMod m in st.Mods)
            {
                if (modId != null && m.Id == modId) { found = true; continue; }
                if (modId != null) keep.Add(m);
            }
            if (!found) throw new ModKitException("mod " + modId + " is not applied in " + Dir);
            if (keep.Count == 0)
            {
                var res = new ApplyResult { Applied = true };
                foreach (WorkspaceArchive a in st.Archives) RemoveCopy(a.Archive, res.Removed);
                File.Delete(System.IO.Path.Combine(Dir, StateFile));
                return res;
            }
            var mods = new List<Mod>();
            foreach (WorkspaceMod m in keep)
            {
                if (m.Path.Length == 0) throw new ModKitException("mod " + m.Id + " was applied from memory, it cannot be loaded again; apply the remaining mods yourself");
                Mod mod = Mod.Load(m.Path);
                if (mod.Id != m.Id) throw new ModKitException(m.Path + " now holds the mod " + mod.Id + ", not " + m.Id);
                mods.Add(mod);
            }
            if (options == null) options = new ApplyOptions();
            foreach (WorkspaceArchive a in st.Archives)
                if (a.Copied && !options.AlsoCopy.Contains(a.Archive)) options.AlsoCopy.Add(a.Archive);
            return Apply(data, mods, options);
        }
    }
}
