// MS2.Tok: Gamebryo Toolbench / World Builder plugin that builds a PathEngine ground mesh (.tok) for the active block.
// Design: tools/tok/PLUGIN.md §3 (export, then build in external processes). Usage: tools/tok/README.md.
//
//   1. block.Export()      stock XmlBlockExporter -> <export folder>/<block>.xblock
//   2. tokgeom             the geometry extractor (tools/tok/extract) -> <out>.geom + <out>.solids (tools/tok/FORMAT.md)
//   3. tokbuild build      the 32-bit builder (tools/tok/builder) with the user's own ContentProcessing3D.dll -> <out>.tok
//
// This assembly has no PathEngine code and loads no PathEngine file: the DLL path is only passed on to tokbuild.exe.
// Every path is a per-user setting (plugin.xml, "MS2 .tok"), empty by default; while one is empty the plugin does nothing
// but log that it is not configured.
// Targets .NET 3.5 / C# 7.3 (Toolbench is a CLR 2 x86 host).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Emergent.Toolbench;
using Emergent.Toolbench.Actions;
using Emergent.Toolbench.ContentService;
using Emergent.Toolbench.Settings;
using Emergent.Toolbench.WorldBuilder;

namespace MS2.Tok
{
    internal class Plugin : PluginBase
    {
        public override void Start()
        {
            // "Build .tok after export": hook BlockDocument.Exported on every block that opens. (The per-block
            // "Run-time Exporter" setting cannot select a custom exporter in stock 3.2, see PLUGIN.md §2.)
            IContentService cs = Platform.GetService<IContentService>();
            cs.ContentOpened += OnContentOpened;
            cs.ContentClosed += OnContentClosed;
        }

        public override void Stop()
        {
            IContentService cs = Platform.GetService<IContentService>();
            if (cs != null)
            {
                cs.ContentOpened -= OnContentOpened;
                cs.ContentClosed -= OnContentClosed;
            }
        }

        private static void OnContentOpened(object sender, ContentEventArgs e)
        {
            BlockDocument block = sender as BlockDocument;
            if (block == null)
                return;
            block.Exported -= OnBlockExported;
            block.Exported += OnBlockExported;
        }

        private static void OnContentClosed(object sender, ContentEventArgs e)
        {
            BlockDocument block = sender as BlockDocument;
            if (block != null)
                block.Exported -= OnBlockExported;
        }

        private static void OnBlockExported(object sender, BlockExportEventArgs e)
        {
            BlockDocument block = sender as BlockDocument;
            if (block == null || TokPipeline.Running || !TokSettings.Load().BuildAfterExport)
                return;
            if (e.Exporter == null || e.Exporter.FileExtension() != "xblock" || e.ExportPath == null)
                return;
            string xblock = Path.Combine(e.ExportPath.LocalPath, TokPipeline.BlockName(block) + ".xblock");
            if (!File.Exists(xblock))
            {
                Logger.Warning("MS2 .tok: no " + xblock + " after export; .tok not built");
                return;
            }
            TokPipeline.Build(block, xblock);
        }
    }

    // World Builder > "Build PathEngine ground (.tok)": export the active block, then build its .tok.
    public class BuildTokAction : IActionHandler
    {
        public ExecutionResult Execute(ExecutionContext context, ExecutionStyle style)
        {
            BlockDocument block = Platform.GetService<IContentService>().ActiveContent as BlockDocument;
            if (block == null)
            {
                Logger.Warning("MS2 .tok: open a block (World Builder) first");
                return ExecutionResult.Failed;
            }
            return TokPipeline.ExportAndBuild(block) ? ExecutionResult.Success : ExecutionResult.Failed;
        }
    }

    // Headless, through ToolbenchConsole (the same mechanism as the stock ContentExporter runnable):
    //   ToolbenchConsole -run TokBuild -solution <file.gsl> -block <file.block>
    // A block opens only inside a loaded game solution (GameSolutionService.CheckSolutionRequirement).
    public class TokBuildRunnable : IRunnable
    {
        public int Run(Arguments args)
        {
            Log.Echo = true;
            int rc;
            try
            {
                rc = RunCore(args);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "MS2 .tok: headless build failed");
                rc = 1;
            }
            // ToolbenchConsole does not return after a runnable that opened content (closing it hangs, and so does
            // Environment.Exit), so end the process without teardown.
            Console.Out.Flush();
            Console.Error.Flush();
            TerminateProcess(GetCurrentProcess(), (uint)rc);
            return rc;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        private static int RunCore(Arguments args)
        {
            List<string> v;
            IContentService cs = Platform.GetService<IContentService>();
            const string usage = "usage: -run TokBuild -solution <file.gsl> -block <file.block> [-cp3d <dll>] [-tokbuild <exe>]"
                + " [-tokgeom <exe>] [-tokgeomargs <args>] [-resource <dir>] [-project <dir>] [-out <dir>] [-tokbuildargs <args>]"
                + " (the optional ones override the MS2 .tok settings)";
            for (int i = 0; i < TokSettings.ArgNames.GetLength(0); i++)
            {
                if (args.KeyedArguments.TryGetValue(TokSettings.ArgNames[i, 0], out v) && v.Count > 0)
                    TokSettings.Overrides[TokSettings.ArgNames[i, 1]] = v[0];
            }
            if (!args.KeyedArguments.TryGetValue("solution", out v) || v.Count == 0)
            {
                Console.WriteLine(usage);
                return -1;
            }
            string solutionPath = v[0];
            AbstractDocument solution = cs.OpenDocumentContent(new Uri(Path.GetFullPath(solutionPath)), ContentContext.Global,
                ContentInteractivity.OnlyLogs);
            if (solution == null)
            {
                Console.WriteLine("MS2 .tok: cannot open game solution " + v[0]);
                return -1;
            }
            if (!args.KeyedArguments.TryGetValue("block", out v) || v.Count == 0)
            {
                Console.WriteLine(usage);
                return -1;
            }
            // A relative -block is looked up in the current folder, then in the solution's folder.
            string blockPath = Path.GetFullPath(v[0]);
            if (!File.Exists(blockPath) && !Path.IsPathRooted(v[0]))
                blockPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(solutionPath)), v[0]);
            BlockDocument block = cs.OpenDocumentContent(new Uri(blockPath), ContentContext.Global,
                ContentInteractivity.OnlyLogs) as BlockDocument;
            if (block == null)
            {
                Console.WriteLine("MS2 .tok: " + v[0] + " is not a block");
                return -1;
            }
            // No CloseContent: in ToolbenchConsole it does not return (100% CPU after the .tok was written), and Run
            // ends the process right after this anyway.
            return TokPipeline.ExportAndBuild(block) ? 0 : 1;
        }

        public void OnExternalCommand(Arguments args)
        {
        }
    }

    internal class TokSettings
    {
        public string CP3DDll, TokbuildExe, TokgeomExe, TokgeomArgs, ResourceDir, ProjectDir, OutputFolder, TokbuildArgs;
        public bool BuildAfterExport;

        // Headless overrides from the ToolbenchConsole command line (-cp3d, -tokbuild, ...), keyed by setting id.
        public static readonly Dictionary<string, string> Overrides = new Dictionary<string, string>();

        // ToolbenchConsole argument -> setting id.
        public static readonly string[,] ArgNames =
        {
            { "cp3d", "MS2.Tok.CP3DDll" },
            { "tokbuild", "MS2.Tok.TokbuildExe" },
            { "tokgeom", "MS2.Tok.TokgeomExe" },
            { "tokgeomargs", "MS2.Tok.TokgeomArgs" },
            { "resource", "MS2.Tok.ResourceDir" },
            { "project", "MS2.Tok.ProjectDir" },
            { "out", "MS2.Tok.OutputFolder" },
            { "tokbuildargs", "MS2.Tok.TokbuildArgs" },
        };

        public static TokSettings Load()
        {
            ISettingService s = Platform.GetService<ISettingService>();
            if (s == null)
                Log.Debug("MS2 .tok: no ISettingService");
            TokSettings t = new TokSettings();
            t.CP3DDll = Str(s, "MS2.Tok.CP3DDll");
            t.TokbuildExe = Str(s, "MS2.Tok.TokbuildExe");
            t.TokgeomExe = Str(s, "MS2.Tok.TokgeomExe");
            t.TokgeomArgs = Str(s, "MS2.Tok.TokgeomArgs");
            if (t.TokgeomArgs.Length == 0)
                t.TokgeomArgs = "\"{resource}\" \"{xblock}\" \"{out}\" --project \"{project}\"";
            t.ResourceDir = Str(s, "MS2.Tok.ResourceDir");
            t.ProjectDir = Str(s, "MS2.Tok.ProjectDir");
            t.OutputFolder = Str(s, "MS2.Tok.OutputFolder");
            t.TokbuildArgs = Str(s, "MS2.Tok.TokbuildArgs");
            bool b;
            t.BuildAfterExport = s != null && s.GetSetting<bool>("MS2.Tok.BuildAfterExport", out b) && b;
            return t;
        }

        private static string Str(ISettingService s, string id)
        {
            string v;
            if (Overrides.TryGetValue(id, out v))
                return Environment.ExpandEnvironmentVariables(v.Trim());
            if (s == null)
                return "";
            if (!s.GetSetting<string>(id, out v))
            {
                Log.Debug("MS2 .tok: GetSetting(" + id + ") returned false");
                return "";
            }
            if (v == null)
            {
                Log.Debug("MS2 .tok: GetSetting(" + id + ") returned null");
                return "";
            }
            return Environment.ExpandEnvironmentVariables(v.Trim());
        }

        // Empty = not configured: the plugin then builds nothing.
        public string Missing()
        {
            List<string> m = new List<string>();
            if (CP3DDll.Length == 0) m.Add("ContentProcessing3D.dll (bring your own, from your MapleStory2 client)");
            if (TokbuildExe.Length == 0) m.Add("tokbuild.exe");
            if (TokgeomExe.Length == 0) m.Add("extractor (tokgeom)");
            return m.Count == 0 ? null : string.Join(", ", m.ToArray());
        }
    }

    // Logger has no target in ToolbenchConsole, so the headless runnable also echoes to the console.
    internal static class Log
    {
        public static bool Echo;

        // Console only (headless diagnostics).
        public static void Debug(string msg)
        {
            if (Echo)
                Console.WriteLine(msg);
        }

        public static void Info(string msg)
        {
            Logger.Info(msg);
            if (Echo)
                Console.WriteLine(msg);
        }

        public static void Warning(string msg)
        {
            Logger.Warning(msg);
            if (Echo)
                Console.WriteLine(msg);
        }

        public static void Error(string msg)
        {
            Logger.Error(msg);
            if (Echo)
                Console.WriteLine(msg);
        }

        public static void Error(Exception ex, string msg)
        {
            Logger.Error(ex, msg);
            if (Echo)
                Console.WriteLine(msg + ": " + ex);
        }
    }

    internal static class TokPipeline
    {
        public static bool Running;

        public static string BlockName(BlockDocument block)
        {
            return Path.GetFileNameWithoutExtension(block.ResourceName);
        }

        public static bool ExportAndBuild(BlockDocument block)
        {
            TokSettings st = TokSettings.Load();
            string missing = st.Missing();
            if (missing != null)
            {
                Log.Warning("MS2 .tok: not configured, nothing built. Set (Settings > MS2 .tok): " + missing);
                return false;
            }
            DateTime started = DateTime.Now;
            Running = true;   // the export below fires Exported; don't build twice
            List<Uri> files;
            try
            {
                files = block.Export();
            }
            finally
            {
                Running = false;
            }
            string want = BlockName(block) + ".xblock", xblock = null;
            foreach (Uri u in files)
            {
                if (string.Equals(Path.GetFileName(u.LocalPath), want, StringComparison.OrdinalIgnoreCase))
                    xblock = u.LocalPath;
            }
            if (xblock == null && files.Count > 0)
                xblock = files[0].LocalPath;
            if (xblock == null)
            {
                Log.Error("MS2 .tok: the export wrote no .xblock");
                return false;
            }
            // Export() may leave an unchanged .xblock as it was; the time shows which file is used.
            if (File.Exists(xblock))
            {
                DateTime written = File.GetLastWriteTime(xblock);
                string msg = "MS2 .tok: exported " + xblock + " (last written " + written.ToString("yyyy-MM-dd HH:mm:ss") + ")";
                if (written < started.AddSeconds(-2))
                    Log.Warning(msg + ": the export did not rewrite it (block unchanged since the last export?); using it as it is");
                else
                    Log.Info(msg);
            }
            return Build(block, xblock);
        }

        public static bool Build(BlockDocument block, string xblock)
        {
            TokSettings st = TokSettings.Load();
            string missing = st.Missing();
            if (missing != null)
            {
                Log.Warning("MS2 .tok: not configured, nothing built. Set (Settings > MS2 .tok): " + missing);
                return false;
            }
            Running = true;
            try
            {
                string name = BlockName(block);
                string outDir = st.OutputFolder.Length > 0 ? st.OutputFolder : Path.GetDirectoryName(xblock);
                Directory.CreateDirectory(outDir);
                string prefix = Path.Combine(outDir, name);
                string project = st.ProjectDir.Length > 0 ? st.ProjectDir
                    : Path.GetDirectoryName(block.ResourceLocation.LocalPath);
                Dictionary<string, string> vars = new Dictionary<string, string>();
                vars["resource"] = st.ResourceDir;
                vars["xblock"] = xblock;
                vars["out"] = prefix;
                vars["project"] = project;
                vars["block"] = name;

                Log.Info("MS2 .tok: building " + prefix + ".tok from " + xblock);
                if (!Run(st.TokgeomExe, Expand(st.TokgeomArgs, vars), "extractor"))
                    return false;
                if (!File.Exists(prefix + ".geom") || !File.Exists(prefix + ".solids"))
                {
                    Log.Error("MS2 .tok: the extractor wrote no " + prefix + ".geom/.solids");
                    return false;
                }
                string args = "build --dll " + Quote(st.CP3DDll) + " --geom " + Quote(prefix + ".geom") + " --solids "
                    + Quote(prefix + ".solids") + " --out " + Quote(prefix + ".tok");
                if (st.TokbuildArgs.Length > 0)
                    args += " " + Expand(st.TokbuildArgs, vars);
                if (!Run(st.TokbuildExe, args, "tokbuild"))
                    return false;
                Log.Info("MS2 .tok: wrote " + prefix + ".tok");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "MS2 .tok: build failed");
                return false;
            }
            finally
            {
                Running = false;
            }
        }

        // {name} -> value; {{ and }} are literal braces.
        internal static string Expand(string template, Dictionary<string, string> vars)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < template.Length; ++i)
            {
                char c = template[i];
                if ((c == '{' || c == '}') && i + 1 < template.Length && template[i + 1] == c)
                {
                    sb.Append(c);
                    ++i;
                    continue;
                }
                int end = c == '{' ? template.IndexOf('}', i + 1) : -1;
                string value;
                if (end > i && vars.TryGetValue(template.Substring(i + 1, end - i - 1), out value))
                {
                    sb.Append(value);
                    i = end;
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Quote(string s)
        {
            return "\"" + s + "\"";
        }

        private static bool Run(string exe, string args, string what)
        {
            Log.Info("MS2 .tok: " + Quote(exe) + " " + args);
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe));
            List<string> lines = new List<string>();
            using (Process p = new Process())
            {
                p.StartInfo = psi;
                DataReceivedEventHandler collect = delegate(object o, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                        lock (lines) lines.Add(e.Data);
                };
                p.OutputDataReceived += collect;
                p.ErrorDataReceived += collect;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                lock (lines)
                {
                    foreach (string l in lines)
                        Log.Info("  " + l);
                }
                if (p.ExitCode != 0)
                {
                    Log.Error("MS2 .tok: " + what + " failed (exit code " + p.ExitCode + ")");
                    return false;
                }
            }
            return true;
        }
    }
}
