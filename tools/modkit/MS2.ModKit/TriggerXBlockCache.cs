// The index cache of TriggerXBlocks (what tools/trigger/xblockindex.py keeps in its cache folder): the model kinds and
// the objects of every xblock of one Exported archive (or extract), in one text file of a cache folder the caller
// names (the workspace's cache/, never the game folder). The file is keyed by its source: an archive by the size and
// write time of its .m2d and .m2h, a folder by the size and write time of every flat/**.flat and xblock/*.xblock;
// another key rebuilds the file.
//
//   modkit-xblock-index 1
//   source <full path>
//   key <key>
//   m <model>\t<kind>[\t<prop>=<value>]...      the models that have a kind, with their default ids
//   x <xblock>                                  an xblock; its objects follow
//   o <kind>\t<id or empty>\t<entity>\t<script>
//
// Fields escape \, tab, CR and LF as \\, \t, \r, \n.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MS2.ModKit.M2d;

namespace MS2.ModKit
{
    public sealed partial class TriggerXBlocks
    {
        const string Magic = "modkit-xblock-index 1";

        /// <summary>The index came from the cache file (no flat or xblock was read).</summary>
        public bool FromCache { get; private set; }

        /// <summary>The cache file in use, null without a cache.</summary>
        public string CacheFile { get; private set; }

        // a cached index: every xblock is in the cache; there is nothing more to read
        TriggerXBlocks()
        {
            readXBlock = delegate { return null; };
            listXBlocks = null;
        }

        /// <summary>
        /// An Exported archive (.m2d) or a folder holding flat/ and xblock/, through the index cache in cacheDir: read
        /// from it when its key still fits the source, else built from the source (every xblock) and written there.
        /// cacheDir null: no cache (Load).
        /// </summary>
        public static TriggerXBlocks Cached(string source, string cacheDir)
        {
            if (cacheDir == null) return Load(source);
            string full = Path.GetFullPath(source);
            bool folder = Directory.Exists(full);
            if (!folder && !File.Exists(full)) throw new ModKitException("no Exported archive or extract at " + source);
            return Cached(full, cacheDir, delegate { return folder ? OfFolder(full) : OfArchive(M2dArchive.Open(full)); });
        }

        /// <summary>An opened Exported archive through the index cache in cacheDir (null: no cache).</summary>
        public static TriggerXBlocks Cached(M2dArchive exported, string cacheDir)
        {
            if (cacheDir == null) return OfArchive(exported);
            return Cached(Path.GetFullPath(exported.Path), cacheDir, delegate { return OfArchive(exported); });
        }

        static TriggerXBlocks Cached(string full, string cacheDir, Func<TriggerXBlocks> build)
        {
            string dir = Path.GetFullPath(cacheDir);
            string home = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
            if (Under(dir, home)) throw new ModKitException("the xblock index cache " + dir + " is in the folder of its source " + home + "; give a folder of the workspace");
            string key = SourceKey(full);
            string file = Path.Combine(dir, "xblock-index-" + Hex(Encoding.UTF8.GetBytes(full.ToLowerInvariant())).Substring(0, 16) + ".txt");
            TriggerXBlocks x = File.Exists(file) ? ReadCache(file, full, key) : null;
            if (x != null) { x.FromCache = true; x.CacheFile = file; return x; }
            x = build();
            if (x.listXBlocks == null) return x;
            foreach (string n in x.listXBlocks()) x.Get(n);
            Directory.CreateDirectory(dir);
            string tmp = file + ".new";
            using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            {
                w.NewLine = "\n";
                x.WriteCache(w, full, key);
            }
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
            x.CacheFile = file;
            return x;
        }

        static bool Under(string dir, string home)
        {
            string d = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string h = home.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return d.StartsWith(h, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        /// <summary>The key of a source: what changes when the archive or a file of the folder does.</summary>
        internal static string SourceKey(string full)
        {
            var sb = new StringBuilder();
            if (Directory.Exists(full))
            {
                var files = new List<string>();
                string flat = Path.Combine(full, "flat"), xb = Path.Combine(full, "xblock");
                if (Directory.Exists(flat))
                    foreach (string f in Directory.GetFiles(flat, "*", SearchOption.AllDirectories))
                        if (f.EndsWith(".flat", StringComparison.OrdinalIgnoreCase)) files.Add(f);
                if (Directory.Exists(xb))
                    foreach (string f in Directory.GetFiles(xb))
                        if (f.EndsWith(".xblock", StringComparison.OrdinalIgnoreCase)) files.Add(f);
                files.Sort(StringComparer.Ordinal);
                foreach (string f in files) Stamp(sb, f, f.Substring(full.Length));
                return "dir:" + files.Count.ToString(CultureInfo.InvariantCulture) + ":" + Hex(Encoding.UTF8.GetBytes(sb.ToString()));
            }
            Stamp(sb, full, full);
            string h = M2dArchive.HeaderPathOf(full);
            if (File.Exists(h)) Stamp(sb, h, h);
            return "m2d:" + Hex(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        static void Stamp(StringBuilder sb, string path, string shown)
        {
            var fi = new FileInfo(path);
            sb.Append(shown).Append('\t').Append(fi.Length.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        static string Hex(byte[] data)
        {
            byte[] h;
            using (SHA1 sha = SHA1.Create()) h = sha.ComputeHash(data);
            var sb = new StringBuilder();
            foreach (byte b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        void WriteCache(TextWriter w, string full, string key)
        {
            w.WriteLine(Magic);
            w.WriteLine("source " + Esc(full));
            w.WriteLine("key " + key);
            var models = new List<string>(kinds.Keys);
            models.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            foreach (string m in models)
            {
                Kind k = kinds[m];
                sb.Length = 0;
                sb.Append("m ").Append(Esc(m)).Append('\t').Append(Esc(k.Name));
                var props = new List<string>(k.Defaults.Keys);
                props.Sort(StringComparer.Ordinal);
                foreach (string p in props) sb.Append('\t').Append(Esc(p)).Append('=').Append(Esc(k.Defaults[p]));
                w.WriteLine(sb.ToString());
            }
            var names = new List<string>(cache.Keys);
            names.Sort(StringComparer.Ordinal);
            foreach (string n in names)
            {
                TriggerObjects o = cache[n];
                if (o == null) continue;
                w.WriteLine("x " + Esc(n));
                foreach (TriggerObjects.Obj b in o.All)
                {
                    sb.Length = 0;
                    sb.Append("o ").Append(Esc(b.Kind)).Append('\t')
                      .Append(b.Id == null ? "" : b.Id.Value.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(Esc(b.Entity)).Append('\t').Append(Esc(b.Script));
                    w.WriteLine(sb.ToString());
                }
            }
        }

        /// <summary>The cached index, or null when the file is not for this source and key (or does not read).</summary>
        static TriggerXBlocks ReadCache(string file, string full, string key)
        {
            try
            {
                using (var r = new StreamReader(file, Encoding.UTF8))
                {
                    if (r.ReadLine() != Magic) return null;
                    if (r.ReadLine() != "source " + Esc(full)) return null;
                    if (r.ReadLine() != "key " + key) return null;
                    var x = new TriggerXBlocks();
                    TriggerObjects cur = null;
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        if (line.Length < 2) return null;
                        string[] f = line.Substring(2).Split('\t');
                        switch (line[0])
                        {
                            case 'm':
                                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                                for (int i = 2; i < f.Length; i++)
                                {
                                    int eq = f[i].IndexOf('=');
                                    if (eq < 0) return null;
                                    d[Unesc(f[i].Substring(0, eq))] = Unesc(f[i].Substring(eq + 1));
                                }
                                if (f.Length < 2) return null;
                                x.kinds[Unesc(f[0])] = new Kind { Name = Unesc(f[1]), Defaults = d };
                                break;
                            case 'x':
                                cur = new TriggerObjects(Unesc(f[0]));
                                x.cache[cur.Name] = cur;
                                break;
                            case 'o':
                                if (cur == null || f.Length != 4) return null;
                                long id = 0;
                                if (f[1].Length > 0 && !long.TryParse(f[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out id)) return null;
                                cur.Add(Unesc(f[0]), f[1].Length > 0 ? id : (long?)null, Unesc(f[2]), Unesc(f[3]));
                                break;
                            default:
                                return null;
                        }
                    }
                    return x;
                }
            }
            catch (IOException) { return null; }
        }

        static string Esc(string s)
        {
            if (s == null) return "";
            if (s.IndexOfAny(new[] { '\\', '\t', '\r', '\n' }) < 0) return s;
            return s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        static string Unesc(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 == s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                sb.Append(n == 't' ? '\t' : n == 'r' ? '\r' : n == 'n' ? '\n' : n);
            }
            return sb.ToString();
        }
    }
}
