// The index cache of TriggerXBlocks (what tools/trigger/xblockindex.py keeps in its cache folder): the model kinds and
// the objects of every xblock of one Exported archive (or extract), in one text file of a cache folder the caller
// names (the workspace's cache/, never the game folder or a data root). The file is keyed by its source: an archive
// by the hash of its .m2h and the size and write time of its .m2d as opened, a folder by the size and write time of
// every flat/**.flat and xblock/*.xblock; another key rebuilds the file. Each writer writes its own temp file and
// swaps it in whole; a file without its end line (cut short) reads as no cache and is rebuilt.
//
//   modkit-xblock-index 2
//   source <full path>
//   key <key>
//   m <model>\t<kind>[\t<prop>=<value>]...      the models that have a kind, with their default ids
//   x <xblock>                                  an xblock; its objects follow
//   o <kind>\t<id or empty>\t<entity>\t<script>
//   b <xblock>\t<why>                            an xblock that does not read
//   end <x lines> <o lines> <b lines>
//
// Fields escape \, tab, CR and LF as \\, \t, \r, \n. Bump Magic when the format or what is indexed changes
// (TriggerSub, IdProps, Classify, Collect in TriggerObjects.cs).
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
        const string Magic = "modkit-xblock-index 2";

        /// <summary>The index came from the cache file (no flat or xblock was read).</summary>
        public bool FromCache { get; private set; }

        /// <summary>The cache file in use, null without a cache (or when it could not be written).</summary>
        public string CacheFile { get; private set; }

        /// <summary>Why the built index was not written to the cache, null when it was (or there is no cache).</summary>
        public string WriteError { get; private set; }

        // a cached index: every xblock is in the cache; there is nothing more to read
        TriggerXBlocks()
        {
            readXBlock = delegate { return null; };
            listXBlocks = null;
        }

        /// <summary>
        /// An Exported archive (.m2d) or a folder holding flat/ and xblock/, through the index cache in cacheDir: read
        /// from it when its key still fits the source, else built from the source (every xblock) and written there.
        /// cacheDir null: no cache (Load). keepOut: folders the cache must not be in (the data roots, the game folder);
        /// the source's own folder, and for an archive the game folder above it, are always kept out.
        /// A cache that cannot be written leaves the built index all the same (WriteError says why).
        /// </summary>
        public static TriggerXBlocks Cached(string source, string cacheDir, IEnumerable<string> keepOut = null)
        {
            if (cacheDir == null) return Load(source);
            string full = Path.GetFullPath(source);
            if (Directory.Exists(full))
            {
                string dir = CacheDirFor(cacheDir, full, true, keepOut);
                return Cached(full, dir, SourceKey(full), delegate { return OfFolder(full); }, delegate { return SourceKey(full); });
            }
            if (!File.Exists(full)) throw new ModKitException("no Exported archive or extract at " + source);
            string d = CacheDirFor(cacheDir, full, false, keepOut);
            M2dArchive m = M2dArchive.Open(full);
            TriggerXBlocks x;
            try { x = Cached(full, d, "m2d:" + m.OpenStamp, delegate { return OfArchive(m); }, null); }
            catch { m.Dispose(); throw; }
            if (x.FromCache) m.Dispose();
            return x;
        }

        /// <summary>An opened Exported archive through the index cache in cacheDir (null: no cache); keepOut as above.</summary>
        public static TriggerXBlocks Cached(M2dArchive exported, string cacheDir, IEnumerable<string> keepOut = null)
        {
            if (cacheDir == null) return OfArchive(exported);
            string full = Path.GetFullPath(exported.Path);
            string dir = CacheDirFor(cacheDir, full, false, keepOut);
            return Cached(full, dir, "m2d:" + exported.OpenStamp, delegate { return OfArchive(exported); }, null);
        }

        // keyAfter: the key again after the build (a folder); another key means the source changed while read, and
        // what was read is not written under either key
        static TriggerXBlocks Cached(string full, string dir, string key, Func<TriggerXBlocks> build, Func<string> keyAfter)
        {
            string file = Path.Combine(dir, "xblock-index-" + Hex(Encoding.UTF8.GetBytes(full.ToLowerInvariant())).Substring(0, 16) + ".txt");
            TriggerXBlocks x = File.Exists(file) ? ReadCache(file, full, key) : null;
            if (x != null) { x.FromCache = true; x.CacheFile = file; return x; }
            x = build();
            if (x.listXBlocks == null) return x;
            TriggerObjects o;
            string why;
            foreach (string n in x.listXBlocks()) x.TryGet(n, out o, out why);
            if (keyAfter != null && keyAfter() != key)
            {
                x.WriteError = "the source changed while it was read; the index is not cached";
                return x;
            }
            string tmp = file + "." + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)
                + "." + Guid.NewGuid().ToString("N") + ".new";
            try
            {
                Directory.CreateDirectory(dir);
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                    {
                        w.NewLine = "\n";
                        x.WriteCache(w, full, key);
                        w.Flush();
#if !NET35
                        fs.Flush(true);
#endif
                    }
                }
                FileSwap.Into(tmp, file);
                x.CacheFile = file;
            }
            catch (Exception e)
            {
                if (!(e is IOException) && !(e is UnauthorizedAccessException)) throw;
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                x.WriteError = "the index cache " + file + " was not written: " + e.Message;
            }
            return x;
        }

        /// <summary>
        /// The full cache folder, refused when it is in a folder the cache must stay out of: the source's folder (for
        /// an archive also the game folder above it: the parent of Resource/ or of Data/), each keepOut folder and the
        /// game folder above it. Links are followed where the framework can (not on net35).
        /// </summary>
        internal static string CacheDirFor(string cacheDir, string full, bool folder, IEnumerable<string> keepOut)
        {
            string dir = Path.GetFullPath(cacheDir);
            var roots = new List<string>();
            string home = folder ? full : Path.GetDirectoryName(full);
            roots.Add(home);
            if (!folder) AddGame(roots, home);
            if (keepOut != null)
                foreach (string k in keepOut)
                    if (!string.IsNullOrEmpty(k)) { string kf = Path.GetFullPath(k); roots.Add(kf); AddGame(roots, kf); }
            var dirs = new List<string> { dir, Planner.RealPath(dir) };
            foreach (string r in roots)
                foreach (string rr in new[] { r, Planner.RealPath(r) })
                    foreach (string dd in dirs)
                        if (Under(dd, rr))
                            throw new ModKitException("the xblock index cache " + dir + " is in " + rr + " (game or data files); give a folder of the workspace");
            return dir;
        }

        // the game folder above a data folder: the parent of the nearest Data/ (or of Resource/) on the way up
        static void AddGame(List<string> roots, string path)
        {
            for (string p = path.TrimEnd(Path.DirectorySeparatorChar); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            {
                string n = Path.GetFileName(p);
                if (string.Equals(n, "Data", StringComparison.OrdinalIgnoreCase) || string.Equals(n, "Resource", StringComparison.OrdinalIgnoreCase))
                {
                    string up = Path.GetDirectoryName(p);
                    if (!string.IsNullOrEmpty(up) && Path.GetPathRoot(up) != up) roots.Add(up);
                    if (string.Equals(n, "Data", StringComparison.OrdinalIgnoreCase)) return;
                }
            }
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
            int xs = 0, os = 0;
            foreach (string n in names)
            {
                TriggerObjects o = cache[n];
                if (o == null) continue;
                w.WriteLine("x " + Esc(n));
                xs++;
                foreach (TriggerObjects.Obj b in o.All)
                {
                    sb.Length = 0;
                    sb.Append("o ").Append(Esc(b.Kind)).Append('\t')
                      .Append(b.Id == null ? "" : b.Id.Value.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(Esc(b.Entity)).Append('\t').Append(Esc(b.Script));
                    w.WriteLine(sb.ToString());
                    os++;
                }
            }
            var bad = Broken;
            foreach (KeyValuePair<string, string> kv in bad) w.WriteLine("b " + Esc(kv.Key) + "\t" + Esc(kv.Value));
            w.WriteLine("end " + xs.ToString(CultureInfo.InvariantCulture) + " " + os.ToString(CultureInfo.InvariantCulture) + " "
                + bad.Count.ToString(CultureInfo.InvariantCulture));
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
                    int xs = 0, os = 0, bs = 0;
                    bool ended = false;
                    while ((line = r.ReadLine()) != null)
                    {
                        if (ended) return null;
                        if (line.StartsWith("end ", StringComparison.Ordinal))
                        {
                            string[] c = line.Substring(4).Split(' ');
                            if (c.Length != 3 || c[0] != xs.ToString(CultureInfo.InvariantCulture) || c[1] != os.ToString(CultureInfo.InvariantCulture)
                                || c[2] != bs.ToString(CultureInfo.InvariantCulture)) return null;
                            ended = true;
                            continue;
                        }
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
                                xs++;
                                break;
                            case 'b':
                                if (f.Length != 2) return null;
                                x.broken[Unesc(f[0])] = Unesc(f[1]);
                                bs++;
                                break;
                            case 'o':
                                if (cur == null || f.Length != 4) return null;
                                long id = 0;
                                if (f[1].Length > 0 && !long.TryParse(f[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out id)) return null;
                                cur.Add(Unesc(f[0]), f[1].Length > 0 ? id : (long?)null, Unesc(f[2]), Unesc(f[3]));
                                os++;
                                break;
                            default:
                                return null;
                        }
                    }
                    return ended ? x : null;
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
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
