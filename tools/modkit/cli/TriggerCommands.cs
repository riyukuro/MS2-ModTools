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
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var files = new Dictionary<string, int>(StringComparer.Ordinal);
            var o = new StringBuilder();
            foreach (string n in names)
            {
                byte[] b = packs.Read(n);
                if (b == null) continue;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (TriggerFinding f in lint.Lint(n, b, packs))
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
