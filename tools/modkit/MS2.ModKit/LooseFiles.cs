// Loose files: the second place an apply can write to. The client reads a plain file under its Data folder
// before it looks into the archive of the same name, so a merged table can be given to the game as
//   <game folder>/Data/Xml/table/pet.xml          for the entry table/pet.xml of client:Xml.m2d
//   <game folder>/Data/Resource/Exported/x.y      for the entry x.y of client:Resource/Exported.m2d
// and the archives stay as they are. A loose file replaces the archive's entry whole.
//
//   <game folder>/modkit-loose.xml                the record: every file and folder this tool wrote there
//
// The record is what keeps the game folder safe:
//   - a file is "ours" only when the record names it and its content is still what was written;
//   - any other file at a path the plan wants to write is a conflict, and with a conflict nothing is written;
//   - a revert deletes the files that are ours, the folders the tool made (when empty), and nothing else.
// Only the "client" root can be served this way, and only entries that are put: an entry cannot be deleted
// from an archive by a loose file.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public sealed class LooseFile
    {
        public string Archive, Entry;
        /// <summary>Path under the game folder, with '/'.</summary>
        public string Path;
        /// <summary>SHA-1 of the content that was written, lower-case hex.</summary>
        public string Sha1;
    }

    public sealed class LooseState
    {
        public string Applied = "";
        public readonly List<WorkspaceMod> Mods = new List<WorkspaceMod>();
        public readonly List<LooseFile> Files = new List<LooseFile>();
        /// <summary>Folders the tool made, as paths under the game folder with '/'.</summary>
        public readonly List<string> Dirs = new List<string>();

        public LooseFile Find(string path)
        {
            foreach (LooseFile f in Files) if (string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }
    }

    public sealed class LooseTarget
    {
        public const string StateFile = "modkit-loose.xml";
        public const string Root = "client";

        /// <summary>The game folder: the one that holds Data.</summary>
        public readonly string GameDir;

        public LooseTarget(string gameDir)
        {
            if (string.IsNullOrEmpty(gameDir)) throw new ModKitException("no game folder given");
            GameDir = System.IO.Path.GetFullPath(gameDir);
            if (!Directory.Exists(System.IO.Path.Combine(GameDir, "Data")))
                throw new ModKitException(GameDir + " is not a game folder: it has no Data folder");
        }

        /// <summary>True for an archive that loose files can stand in for.</summary>
        public static bool Serves(string archive) { return DataSet.RootOf(DataSet.NormalizeRef(archive)) == Root; }

        /// <summary>"client:Xml.m2d", "table/pet.xml" -> "Data/Xml/table/pet.xml".</summary>
        public static string RelPathOf(string archive, string entry)
        {
            archive = DataSet.NormalizeRef(archive);
            if (!Serves(archive)) throw new ModKitException("archive " + archive + ": only archives of the client can be given as loose files");
            string a = DataSet.PathOf(archive);
            if (a.EndsWith(".m2d", StringComparison.OrdinalIgnoreCase)) a = a.Substring(0, a.Length - 4);
            string e = (entry ?? "").Replace('\\', '/').TrimStart('/');
            if (e.Length == 0 || e.EndsWith("/", StringComparison.Ordinal)) throw new ModKitException("entry \"" + entry + "\" of " + archive + " has no file name");
            foreach (string part in e.Split('/'))
                if (part.Length == 0 || part == "." || part == ".." || part.IndexOf(':') >= 0)
                    throw new ModKitException("entry \"" + entry + "\" of " + archive + " cannot be written as a file");
            return "Data/" + a + "/" + e;
        }

        public string FullPath(string relPath)
        {
            string p = GameDir;
            foreach (string part in relPath.Split('/')) p = System.IO.Path.Combine(p, part);
            return p;
        }

        // ---- the record ----

        public LooseState ReadState()
        {
            string f = System.IO.Path.Combine(GameDir, StateFile);
            if (!File.Exists(f)) return null;
            XmlDoc doc = XmlDoc.Parse(File.ReadAllBytes(f), f);
            if (doc.Root == null || doc.Root.Name != "modkit-loose") throw new ModKitException(f + ": not a loose-file record");
            var s = new LooseState();
            s.Applied = doc.Root.Attr("applied") ?? "";
            foreach (XmlElem e in doc.Root.Children)
            {
                if (e.Name == "mod") s.Mods.Add(new WorkspaceMod { Id = e.Attr("id"), Path = e.Attr("path") ?? "", Version = e.Attr("version") ?? "" });
                else if (e.Name == "file") s.Files.Add(new LooseFile { Archive = e.Attr("archive"), Entry = e.Attr("entry"), Path = e.Attr("path"), Sha1 = e.Attr("sha1") ?? "" });
                else if (e.Name == "dir") s.Dirs.Add(e.Attr("path"));
            }
            return s;
        }

        void WriteState(LooseState s)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<modkit-loose version=\"1\" applied=\"").Append(XmlDoc.EscapeAttr(s.Applied, '"')).Append("\">\n");
            foreach (WorkspaceMod m in s.Mods)
                sb.Append("  <mod id=\"").Append(XmlDoc.EscapeAttr(m.Id, '"')).Append("\" version=\"").Append(XmlDoc.EscapeAttr(m.Version, '"'))
                  .Append("\" path=\"").Append(XmlDoc.EscapeAttr(m.Path, '"')).Append("\"/>\n");
            foreach (string d in s.Dirs)
                sb.Append("  <dir path=\"").Append(XmlDoc.EscapeAttr(d, '"')).Append("\"/>\n");
            foreach (LooseFile f in s.Files)
                sb.Append("  <file path=\"").Append(XmlDoc.EscapeAttr(f.Path, '"')).Append("\" sha1=\"").Append(f.Sha1)
                  .Append("\" archive=\"").Append(XmlDoc.EscapeAttr(f.Archive, '"')).Append("\" entry=\"").Append(XmlDoc.EscapeAttr(f.Entry, '"')).Append("\"/>\n");
            sb.Append("</modkit-loose>\n");
            string p = System.IO.Path.Combine(GameDir, StateFile);
            File.WriteAllBytes(p + ".tmp", new UTF8Encoding(false).GetBytes(sb.ToString()));
            if (File.Exists(p)) File.Delete(p);
            File.Move(p + ".tmp", p);
        }

        public static string Sha1Of(byte[] bytes)
        {
            using (SHA1 sha = SHA1.Create())
            {
                var sb = new StringBuilder(40);
                foreach (byte b in sha.ComputeHash(bytes)) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        // a file is ours when the record names it and it still holds what was written
        bool Ours(LooseState st, string relPath)
        {
            LooseFile f = st == null ? null : st.Find(relPath);
            if (f == null) return false;
            string full = FullPath(relPath);
            return File.Exists(full) && Sha1Of(File.ReadAllBytes(full)) == f.Sha1;
        }

        // ---- plan ----

        /// <summary>
        /// Adds to a plan what stands in the way of writing its client part as loose files: an error for every
        /// entry that would have to be deleted, a conflict for every file that is there and is not ours.
        /// Returns the number of messages added.
        /// </summary>
        public int Check(Plan plan)
        {
            LooseState st = ReadState();
            int n = 0;
            foreach (PlanArchive pa in plan.Archives)
            {
                if (!Serves(pa.Archive)) continue;
                foreach (string d in pa.Delete)
                {
                    plan.Msg(plan.Errors, null, pa.Archive, d, "a loose file cannot delete an entry of an archive; this change needs the patched-archive target");
                    n++;
                }
                foreach (KeyValuePair<string, byte[]> p in pa.Put)
                {
                    string rel = RelPathOf(pa.Archive, p.Key), full = FullPath(rel);
                    if (Directory.Exists(full))
                    {
                        plan.Msg(plan.Errors, null, pa.Archive, p.Key, "a folder is in the place of the file " + full);
                        n++;
                    }
                    else if (File.Exists(full) && !Ours(st, rel))
                    {
                        bool listed = st != null && st.Find(rel) != null;
                        plan.Msg(plan.Conflicts, null, pa.Archive, p.Key, listed
                            ? "the file " + full + " was changed after this tool wrote it; it is not overwritten (move it away or delete it, then apply again)"
                            : "the file " + full + " exists and was not written by this tool; it is not overwritten (move it away or delete it, then apply again)");
                        n++;
                    }
                }
            }
            return n;
        }

        // ---- apply ----

        /// <summary>
        /// Plans the mods and writes the client part of the plan as loose files. Nothing is written when the
        /// plan has errors or conflicts; a file that is not ours is never overwritten, also not with AllowConflicts.
        /// </summary>
        public ApplyResult Apply(DataSet data, IList<Mod> mods, ApplyOptions options)
        {
            if (options == null) options = new ApplyOptions();
            Plan plan = Planner.Build(data, mods, new PlanOptions { Schemas = options.Schemas, Progress = options.Progress });
            return Apply(mods, plan, options);
        }

        /// <summary>The same with a plan that was built already (Check is run on it here).</summary>
        public ApplyResult Apply(IList<Mod> mods, Plan plan, ApplyOptions options)
        {
            if (options == null) options = new ApplyOptions();
            var res = new ApplyResult { Plan = plan };
            int before = plan.Conflicts.Count;
            Check(plan);
            int foreign = plan.Conflicts.Count - before;
            if (plan.HasErrors) { res.Reason = plan.Errors.Count + " error(s)"; return res; }
            if (foreign > 0) { res.Reason = foreign + " file(s) in the game folder that this tool did not write"; return res; }
            if (plan.HasConflicts && !options.AllowConflicts) { res.Reason = plan.Conflicts.Count + " conflict(s)"; return res; }

            LooseState old = ReadState();
            var state = new LooseState { Applied = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) };
            foreach (Mod m in mods) state.Mods.Add(new WorkspaceMod { Id = m.Id, Version = m.Version, Path = m.Dir ?? "" });
            var todo = new List<KeyValuePair<LooseFile, byte[]>>();
            foreach (PlanArchive pa in plan.Archives)
            {
                if (!Serves(pa.Archive)) continue;
                foreach (KeyValuePair<string, byte[]> p in pa.Put)
                {
                    var f = new LooseFile { Archive = pa.Archive, Entry = p.Key, Path = RelPathOf(pa.Archive, p.Key), Sha1 = Sha1Of(p.Value) };
                    // a later put of the same entry replaces the earlier one
                    todo.RemoveAll(delegate(KeyValuePair<LooseFile, byte[]> t) { return string.Equals(t.Key.Path, f.Path, StringComparison.OrdinalIgnoreCase); });
                    todo.Add(new KeyValuePair<LooseFile, byte[]>(f, p.Value));
                }
            }

            // files of the earlier apply that this one does not write any more
            var stale = new List<LooseFile>();
            if (old != null)
            {
                foreach (LooseFile f in old.Files)
                    if (todo.Find(delegate(KeyValuePair<LooseFile, byte[]> t) { return string.Equals(t.Key.Path, f.Path, StringComparison.OrdinalIgnoreCase); }).Key == null)
                        stale.Add(f);
                state.Dirs.AddRange(old.Dirs);
            }

            // the record goes first and names everything that is or will be there, so a write that is cut short
            // still leaves a record a revert can work from
            foreach (KeyValuePair<LooseFile, byte[]> t in todo) state.Files.Add(t.Key);
            foreach (KeyValuePair<LooseFile, byte[]> t in todo) NoteNewDirs(t.Key.Path, state.Dirs);
            var first = new LooseState { Applied = state.Applied };
            first.Mods.AddRange(state.Mods); first.Dirs.AddRange(state.Dirs); first.Files.AddRange(state.Files); first.Files.AddRange(stale);
            WriteState(first);

            int n = 0;
            foreach (KeyValuePair<LooseFile, byte[]> t in todo)
            {
                ProgressUtil.Report(options.Progress, "write", t.Key.Path, ++n, todo.Count);
                string full = FullPath(t.Key.Path);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
                File.WriteAllBytes(full + ".modkit-tmp", t.Value);
                if (File.Exists(full)) File.Delete(full);
                File.Move(full + ".modkit-tmp", full);
                if (Sha1Of(File.ReadAllBytes(full)) != t.Key.Sha1) throw new ModKitException("read-back of " + full + ": the file does not hold the planned bytes");
                res.Written.Add(full);
            }
            foreach (LooseFile f in stale) RemoveFile(old, f, res);
            RemoveEmptyDirs(state.Dirs, res);
            if (state.Files.Count == 0 && state.Dirs.Count == 0) File.Delete(System.IO.Path.Combine(GameDir, StateFile));
            else WriteState(state);
            res.Applied = true;
            return res;
        }

        // folders on the way to a file that are not there yet (they will be made, and are ours)
        void NoteNewDirs(string relPath, List<string> dirs)
        {
            string[] parts = relPath.Split('/');
            string rel = "";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                rel = rel.Length == 0 ? parts[i] : rel + "/" + parts[i];
                if (Directory.Exists(FullPath(rel))) continue;
                if (dirs.Find(delegate(string d) { return string.Equals(d, rel, StringComparison.OrdinalIgnoreCase); }) == null) dirs.Add(rel);
            }
        }

        void RemoveFile(LooseState st, LooseFile f, ApplyResult res)
        {
            string full = FullPath(f.Path);
            if (!File.Exists(full)) return;
            if (Ours(st, f.Path)) { File.Delete(full); res.Removed.Add(full); }
            else res.Left.Add(full);
        }

        // removes the folders the tool made that are empty now, deepest first; the others leave the list
        void RemoveEmptyDirs(List<string> dirs, ApplyResult res)
        {
            dirs.Sort(delegate(string a, string b) { return b.Length != a.Length ? b.Length.CompareTo(a.Length) : string.CompareOrdinal(a, b); });
            for (int i = 0; i < dirs.Count; )
            {
                string full = FullPath(dirs[i]);
                if (!Directory.Exists(full)) { dirs.RemoveAt(i); continue; }
                if (Directory.GetFileSystemEntries(full).Length == 0) { Directory.Delete(full); res.Removed.Add(full); dirs.RemoveAt(i); continue; }
                i++;
            }
        }

        // ---- revert ----

        /// <summary>
        /// Deletes every file of the record that still holds what was written, the folders the tool made that are
        /// empty then, and the record. A file that was changed since is left where it is and named in Left.
        /// </summary>
        public ApplyResult Revert()
        {
            LooseState st = ReadState();
            if (st == null) throw new ModKitException("no loose files of this tool in " + GameDir + " (no " + StateFile + ")");
            var res = new ApplyResult { Applied = true };
            foreach (LooseFile f in st.Files) RemoveFile(st, f, res);
            RemoveEmptyDirs(st.Dirs, res);
            File.Delete(System.IO.Path.Combine(GameDir, StateFile));
            return res;
        }
    }
}
