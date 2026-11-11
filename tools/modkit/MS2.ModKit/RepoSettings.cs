// The settings of a checkout: folders and options a user writes once, for the command line and the Toolbench views.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace MS2.ModKit
{
    /// <summary>
    /// The repo's settings: environment first, then modtools.toml in the repo root (see modtools.example.toml). The repo
    /// root is the first folder at or above one of the start folders that holds modtools.example.toml.
    /// </summary>
    public sealed class RepoSettings
    {
        public string Repo;     // null when the tool does not run from a checkout
        readonly Dictionary<string, string> file = new Dictionary<string, string>();

        public static RepoSettings Load(params string[] startFolders)
        {
            var s = new RepoSettings();
            foreach (string start in startFolders)
            {
                for (string d = start; !string.IsNullOrEmpty(d) && s.Repo == null; d = System.IO.Path.GetDirectoryName(d))
                    if (File.Exists(System.IO.Path.Combine(d, "modtools.example.toml"))) s.Repo = d;
                if (s.Repo != null) break;
            }
            if (s.Repo == null) return s;
            string f = System.IO.Path.Combine(s.Repo, "modtools.toml");
            if (!File.Exists(f)) return s;
            // the file only holds NAME = "value" lines and comments
            var re = new Regex("^\\s*([A-Za-z0-9_]+)\\s*=\\s*(?:\"((?:[^\"\\\\]|\\\\.)*)\"|'([^']*)')\\s*(#.*)?$");
            foreach (string line in File.ReadAllLines(f))
            {
                Match m = re.Match(line);
                if (!m.Success) continue;
                s.file[m.Groups[1].Value] = m.Groups[3].Success ? m.Groups[3].Value : Regex.Replace(m.Groups[2].Value, "\\\\(.)", "$1");
            }
            return s;
        }

        public string Path(string name)
        {
            string v = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(v)) return v;
            if (!file.TryGetValue(name, out v) || v.Length == 0) return null;
            return System.IO.Path.IsPathRooted(v) ? v : System.IO.Path.Combine(Repo, v);
        }

        /// <summary>A setting that is not a path: environment first, then modtools.toml; null when not set.</summary>
        public string Value(string name)
        {
            string v = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(v)) return v;
            return file.TryGetValue(name, out v) && v.Length > 0 ? v : null;
        }

        /// <summary>Where Value(name) comes from: "the environment variable NAME", "modtools.toml", or null when not set.</summary>
        public string SourceOf(string name)
        {
            string v;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))) return "the environment variable " + name;
            return file.TryGetValue(name, out v) && v.Length > 0 ? "modtools.toml" : null;
        }

        public string OutDir { get { return Path("MS2_OUT") ?? System.IO.Path.Combine(Repo ?? Directory.GetCurrentDirectory(), "out"); } }
    }
}
