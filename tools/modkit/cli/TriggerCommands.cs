// modkit trigger-lint: the lint of the trigger scripts (MS2.ModKit TriggerLint, the port of tools/trigger/lint.py).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MS2.ModKit.Cli
{
    static partial class Program
    {
        /// <summary>A folder holding trigger/ (data null) or an Xml archive of the data set.</summary>
        static int TriggerLintCmd(Args a, DataSet data, string archive)
        {
            TriggerPacks packs;
            if (data == null)
            {
                string root = Path.GetFullPath(a.Pos[0]);
                if (!Directory.Exists(Path.Combine(root, "trigger")) && string.Equals(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)), "trigger", StringComparison.OrdinalIgnoreCase))
                    root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
                packs = TriggerPacks.OfFolder(root);
            }
            else packs = TriggerPacks.OfArchive(data, archive);
            var names = new List<string>();
            foreach (string n in packs.Names) if (n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) names.Add(n);
            names.Sort(StringComparer.Ordinal);
            TriggerLint lint = TriggerLint.Shipped;
            string xspec = a.Get("--xblock");
            TriggerXBlocks xblocks = null;
            TriggerObjects loose = null;
            if (xspec != null)
            {
                if (xspec != "auto" && File.Exists(xspec))
                {
                    // a loose .xblock: its models come from the Exported archive (or extract) all the same
                    xblocks = OpenExported(a, data, archive);
                    string nm = Path.GetFileNameWithoutExtension(xspec).ToLowerInvariant();
                    loose = xblocks.Read(File.ReadAllBytes(xspec), nm);
                }
                else xblocks = OpenExported(a, data, archive);
            }
            var opt = new TriggerLintOptions { Strict = a.Has("--strict") };
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var files = new Dictionary<string, int>(StringComparer.Ordinal);
            var o = new StringBuilder();
            foreach (string n in names)
            {
                byte[] b = packs.Read(n);
                if (b == null) continue;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                if (xspec != null)
                {
                    // auto: the folder of the script (trigger/<xblock>/<name>.xml), as lint.py takes it
                    string nn = n.Replace('\\', '/');
                    int sl = nn.LastIndexOf('/');
                    string dir = sl < 0 ? "" : nn.Substring(0, sl);
                    opt.XBlock = xspec == "auto" ? dir.Substring(dir.LastIndexOf('/') + 1) : xspec;
                    opt.Objects = loose ?? xblocks.Get(opt.XBlock);
                }
                string format;
                foreach (TriggerFinding f in lint.Lint(n, b, packs, opt, out format))
                {
                    int c;
                    counts.TryGetValue(f.Level, out c);
                    counts[f.Level] = c + 1;
                    if (seen.Add(f.Level)) { files.TryGetValue(f.Level, out c); files[f.Level] = c + 1; }
                    if (a.Has("--json"))
                        o.Append("{\"path\": ").Append(Js(f.Entry)).Append(", \"line\": ").Append(f.Line.ToString(CultureInfo.InvariantCulture))
                         .Append(", \"level\": ").Append(Js(f.Level)).Append(", \"code\": ").Append(Js(f.Code)).Append(", \"msg\": ").Append(Js(f.Message)).Append("}\n");
                    else if (!a.Has("-q") || f.IsError) o.Append(f).Append('\n');
                }
            }
            Console.Write(o);
            Func<string, int> get = delegate(string k) { int v; return counts.TryGetValue(k, out v) ? v : 0; };
            Func<string, int> getf = delegate(string k) { int v; return files.TryGetValue(k, out v) ? v : 0; };
            Console.Error.WriteLine("files: " + names.Count.ToString(CultureInfo.InvariantCulture));
            foreach (string lv in new[] { "error", "warning", "allowed" })
                Console.Error.WriteLine(lv + ": " + get(lv).ToString(CultureInfo.InvariantCulture) + " in " + getf(lv).ToString(CultureInfo.InvariantCulture) + " files");
            return get("error") > 0 ? 1 : 0;
        }

        /// <summary>
        /// The map files for --xblock: --exported (an archive ref of the data, an .m2d or an extract folder holding
        /// flat/ and xblock/); without it the Resource/Exported.m2d beside the Xml archive.
        /// </summary>
        static TriggerXBlocks OpenExported(Args a, DataSet data, string archive)
        {
            string ex = a.Get("--exported");
            string cache = XBlockCacheDir(a);
            if (ex == null)
            {
                if (data == null) throw new UsageException("--xblock on a folder needs --exported ARCHIVE|DIR (the map files: flat/ and xblock/)");
                string norm = DataSet.NormalizeRef(archive);
                int colon = norm.IndexOf(':');
                ex = (colon > 0 ? norm.Substring(0, colon) : norm) + ":" + Planner.MapArchivePath;
                if (!data.Exists(ex)) throw new ModKitException("--xblock: no " + ex + "; give --exported ARCHIVE|DIR");
                return Said(TriggerXBlocks.Cached(data.Open(ex), cache));
            }
            if (Directory.Exists(ex) || File.Exists(ex)) return Said(TriggerXBlocks.Cached(ex, cache));
            if (data != null && data.Exists(ex)) return Said(TriggerXBlocks.Cached(data.Open(ex), cache));
            throw new ModKitException("--exported: no archive or folder " + ex);
        }

        /// <summary>The index cache folder: --cache, else the cache/ folder of the workspace (-w), else none.</summary>
        static string XBlockCacheDir(Args a)
        {
            string c = a.Get("--cache");
            if (c != null) return c;
            string w = a.Get("-w");
            return w == null ? null : Path.Combine(w, "cache");
        }

        static TriggerXBlocks Said(TriggerXBlocks x)
        {
            if (x.CacheFile != null) Console.Error.WriteLine("xblock index: " + (x.FromCache ? "read from " : "built into ") + x.CacheFile);
            return x;
        }

        static string Js(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
