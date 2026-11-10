// What can follow an apply or a revert on the server's side, both off until they are set:
//  - a target folder: the server's archives, as the workspace holds them now, are copied there (and an archive
//    that the mods no longer touch is put back from the original data);
//  - a command line: run as a child process once the copies are made, for a server that has to take its data in
//    again. Its output and exit code go into the result.
// Nothing here knows any server; the command is the user's.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public sealed class ServerStepResult
    {
        /// <summary>Files written into the target folder from the workspace.</summary>
        public readonly List<string> Copied = new List<string>();
        /// <summary>Files of the target folder put back from the original data.</summary>
        public readonly List<string> Restored = new List<string>();
        /// <summary>Why nothing was done, or what went wrong; null when all went well.</summary>
        public string Problem;
        /// <summary>The command as it was started (placeholders filled in); null when none was run.</summary>
        public string Command;
        public string WorkDir;
        public bool Ran;
        public int ExitCode;
        public string Output = "";
        public bool Ok { get { return Problem == null && (!Ran || ExitCode == 0); } }
    }

    public sealed class ServerStep
    {
        public const string RecordFile = "modkit-target.xml";

        /// <summary>Folder the server's archives are copied to; null or "" = off.</summary>
        public string TargetDir;
        /// <summary>Command line run after the copies; null or "" = off. {target} and {workspace} stand for the two folders.</summary>
        public string Command;
        /// <summary>Folder the command runs in; null = the target folder, else the workspace.</summary>
        public string WorkDir;
        /// <summary>The command is stopped after this many seconds; 0 = no limit.</summary>
        public int TimeoutSeconds;

        public bool HasTarget { get { return !string.IsNullOrEmpty(TargetDir); } }
        public bool HasCommand { get { return !string.IsNullOrEmpty(Command) && Command.Trim().Length > 0; } }
        public bool Enabled { get { return HasTarget || HasCommand; } }

        // an archive of the server profile (or of a root that is no profile and not the client); the global client's is not one
        static bool IsServer(string archive)
        {
            Profile p = Profile.OfArchive(DataSet.NormalizeRef(archive));
            return p == null ? !LooseTarget.Serves(archive) : p.IsServer;
        }

        string Fill(string text, string workspaceDir)
        {
            return text.Replace("{target}", HasTarget ? Path.GetFullPath(TargetDir) : "").Replace("{workspace}", workspaceDir ?? "");
        }

        string RunDir(string workspaceDir)
        {
            if (!string.IsNullOrEmpty(WorkDir)) return Path.GetFullPath(WorkDir);
            return HasTarget ? Path.GetFullPath(TargetDir) : workspaceDir;
        }

        /// <summary>What an apply of this plan would do afterwards, one line each; empty when nothing is set or the plan changes no server archive.</summary>
        public List<string> Describe(Plan plan, string workspaceDir)
        {
            var lines = new List<string>();
            if (!Enabled) return lines;
            var names = new List<string>();
            foreach (PlanArchive a in plan.Archives) if (IsServer(a.Archive)) names.Add(a.Archive);
            if (names.Count == 0 || plan.HasErrors) return lines;
            if (workspaceDir == null) { lines.Add("after an apply nothing would follow on the server's side: the workspace folder is not set"); return lines; }
            if (HasTarget) lines.Add("after a successful apply " + string.Join(", ", names.ToArray()) + " would be copied to " + Path.GetFullPath(TargetDir));
            if (HasCommand) lines.Add("after a successful apply this command would be run in " + RunDir(workspaceDir) + ": " + Fill(Command.Trim(), workspaceDir));
            return lines;
        }

        static string Slash(string p) { return p.TrimEnd('/', '\\') + Path.DirectorySeparatorChar; }

        static bool Inside(string path, string folder)
        {
            StringComparison cmp = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return Slash(path).StartsWith(Slash(folder), cmp);
        }

        static string Under(string dir, string archive)
        {
            string p = dir;
            foreach (string part in DataSet.PathOf(archive).Split('/')) p = Path.Combine(p, part);
            return p;
        }

        static void CopyFile(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            File.Copy(from, to + ".tmp", true);
            if (File.Exists(to)) File.Delete(to);
            File.Move(to + ".tmp", to);
        }

        // an archive is two files: the .m2d and its .m2h header
        static void CopyArchive(string from, string to, List<string> done)
        {
            CopyFile(from, to);
            done.Add(to);
            string h = Path.ChangeExtension(from, ".m2h");
            if (File.Exists(h)) { CopyFile(h, Path.ChangeExtension(to, ".m2h")); done.Add(Path.ChangeExtension(to, ".m2h")); }
        }

        List<string> ReadRecord(string dir)
        {
            var list = new List<string>();
            string f = Path.Combine(dir, RecordFile);
            if (!File.Exists(f)) return list;
            XmlDoc doc = XmlDoc.Parse(File.ReadAllBytes(f), f);
            if (doc.Root == null || doc.Root.Name != "modkit-target") throw new ModKitException(f + ": not a record of this tool");
            foreach (XmlElem e in doc.Root.Children) if (e.Name == "archive" && e.Attr("name") != null) list.Add(e.Attr("name"));
            return list;
        }

        void WriteRecord(string dir, List<string> archives)
        {
            string f = Path.Combine(dir, RecordFile);
            if (archives.Count == 0) { if (File.Exists(f)) File.Delete(f); return; }
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<modkit-target version=\"1\">\n");
            foreach (string a in archives) sb.Append("  <archive name=\"").Append(XmlDoc.EscapeAttr(a, '"')).Append("\"/>\n");
            sb.Append("</modkit-target>\n");
            File.WriteAllBytes(f, new UTF8Encoding(false).GetBytes(sb.ToString()));
        }

        /// <summary>
        /// To be called after an apply or a revert that went through. changed: the apply or revert wrote or removed
        /// a server archive in the workspace. Never throws for what the command does; a failure is in the result.
        /// </summary>
        public ServerStepResult Run(DataSet data, string workspaceDir, bool changed)
        {
            var res = new ServerStepResult();
            if (!Enabled) return res;
            if (workspaceDir == null) { res.Problem = "the workspace folder is not set, so there is no server archive to pass on"; return res; }
            var now = new List<string>();
            WorkspaceState st = new Workspace(workspaceDir).ReadState();
            if (st != null) foreach (WorkspaceArchive a in st.Archives) if (IsServer(a.Archive)) now.Add(DataSet.NormalizeRef(a.Archive));

            if (HasTarget)
            {
                string dir = Path.GetFullPath(TargetDir);
                foreach (string root in data.Roots)
                {
                    string folder = data.RootFolder(root);
                    if (Inside(dir, folder) || Inside(folder, dir))
                    {
                        res.Problem = "the server target folder " + dir + " and the data root \"" + root + "\" (" + folder + ") are inside one another; the original data is only read. Give the tool a copy of the server's data as its server data folder";
                        return res;
                    }
                }
                if (Inside(dir, workspaceDir) || Inside(workspaceDir, dir)) { res.Problem = "the server target folder " + dir + " and the workspace are inside one another"; return res; }
                try
                {
                    List<string> before = Directory.Exists(dir) ? ReadRecord(dir) : new List<string>();
                    var ws = new Workspace(workspaceDir);
                    foreach (string a in now) CopyArchive(ws.PathOf(a), Under(dir, a), res.Copied);
                    foreach (string a in before)
                        if (!now.Contains(a) && data.Exists(a)) { CopyArchive(data.Resolve(a), Under(dir, a), res.Restored); changed = true; }
                    if (Directory.Exists(dir)) WriteRecord(dir, now);
                }
                catch (IOException x) { res.Problem = "copy to the server target folder: " + x.Message; return res; }
                catch (UnauthorizedAccessException x) { res.Problem = "copy to the server target folder: " + x.Message; return res; }
            }
            if (!HasCommand) return res;
            if (!changed) { res.Problem = "no server archive changed; the command was not run"; return res; }

            string line = Fill(Command.Trim(), workspaceDir), file, args;
            Split(line, out file, out args);
            res.Command = line;
            res.WorkDir = RunDir(workspaceDir);
            var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = res.WorkDir };
            psi.EnvironmentVariables["MODKIT_WORKSPACE"] = workspaceDir;
            psi.EnvironmentVariables["MODKIT_SERVER_TARGET"] = HasTarget ? Path.GetFullPath(TargetDir) : "";
            var paths = new List<string>();
            foreach (string a in now) paths.Add(HasTarget ? Under(Path.GetFullPath(TargetDir), a) : new Workspace(workspaceDir).PathOf(a));
            psi.EnvironmentVariables["MODKIT_SERVER_ARCHIVES"] = string.Join(Path.PathSeparator.ToString(), paths.ToArray());
            var output = new StringBuilder();
            try
            {
                using (var p = new Process { StartInfo = psi })
                {
                    DataReceivedEventHandler take = delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (output) output.Append(e.Data).Append("\r\n"); };
                    p.OutputDataReceived += take;
                    p.ErrorDataReceived += take;
                    p.Start();
                    res.Ran = true;
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (TimeoutSeconds > 0 && !p.WaitForExit(TimeoutSeconds * 1000))
                    {
                        try { p.Kill(); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                        res.Problem = "the command was stopped after " + TimeoutSeconds + " s";
                    }
                    p.WaitForExit();    // also waits until both streams are read to their end
                    res.ExitCode = p.ExitCode;
                }
            }
            catch (Win32Exception x) { res.Problem = "the command could not be started: " + x.Message; }
            catch (InvalidOperationException x) { res.Problem = "the command could not be started: " + x.Message; }
            lock (output) res.Output = output.ToString();
            return res;
        }

        /// <summary>The program (first word, or the first quoted part) and the rest of a command line.</summary>
        public static void Split(string line, out string file, out string args)
        {
            line = line.Trim();
            int end;
            if (line.StartsWith("\"", StringComparison.Ordinal))
            {
                end = line.IndexOf('"', 1);
                if (end < 0) end = line.Length;
                file = line.Substring(1, end - 1);
                args = end + 1 < line.Length ? line.Substring(end + 1).Trim() : "";
                return;
            }
            end = 0;
            while (end < line.Length && !char.IsWhiteSpace(line[end])) end++;
            file = line.Substring(0, end);
            args = line.Substring(end).Trim();
        }

        /// <summary>The result as text lines; "" when nothing was set.</summary>
        public static string Report(ServerStepResult r)
        {
            var sb = new StringBuilder();
            if (r == null) return "";
            foreach (string f in r.Copied) sb.Append("copied to the server target: ").Append(f).Append("\r\n");
            foreach (string f in r.Restored) sb.Append("put back from the original data: ").Append(f).Append("\r\n");
            if (r.Command != null)
            {
                sb.Append(r.Ran ? "ran in " : "not run in ").Append(r.WorkDir).Append(": ").Append(r.Command).Append("\r\n");
                if (r.Output.Length > 0)
                    foreach (string l in r.Output.TrimEnd('\r', '\n').Split(new[] { "\r\n" }, StringSplitOptions.None)) sb.Append("  | ").Append(l).Append("\r\n");
                if (r.Ran) sb.Append("exit code ").Append(r.ExitCode).Append(r.ExitCode == 0 ? "" : " (the command reports a failure)").Append("\r\n");
            }
            if (r.Problem != null) sb.Append("server step: ").Append(r.Problem).Append("\r\n");
            return sb.ToString();
        }
    }
}
