// The files a map editor project needs beside its models: the model files (.nif) the maps' entities name by asset
// id, and the texture files those model files name. They are read from the game's Resource folder, where a path
// /A/B/rest is either a plain file or the entry "rest" of the archive A/B.m2d.
using System;
using System.Collections.Generic;
using System.IO;
using MS2.ModKit.M2d;

namespace MS2.ModKit.MapEdit
{
    /// <summary>Reads files of the game's Resource folder by their asset path. Only reads.</summary>
    public sealed class WbAssetSource : IDisposable
    {
        readonly string dir;
        readonly CasePaths cp = new CasePaths();
        readonly Dictionary<string, M2dArchive> open = new Dictionary<string, M2dArchive>(StringComparer.Ordinal);

        public WbAssetSource(string resourceDir)
        {
            if (!Directory.Exists(resourceDir)) throw new ModKitException("no Resource folder at " + resourceDir);
            dir = Path.GetFullPath(resourceDir);
        }

        M2dArchive Archive(string path)
        {
            M2dArchive a;
            if (open.TryGetValue(path, out a)) return a;
            a = File.Exists(path) && File.Exists(M2dArchive.HeaderPathOf(path)) ? M2dArchive.Open(path) : null;
            open[path] = a;
            return a;
        }

        /// <summary>The bytes of /A/B/rest, or null when neither a file nor an archive entry has it.</summary>
        public byte[] Read(string assetPath)
        {
            string[] parts = assetPath.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            foreach (string p in parts) if (p == "." || p == "..") return null;
            string plain = cp.Resolve(dir, string.Join("/", parts), false);
            if (File.Exists(plain)) return File.ReadAllBytes(plain);
            for (int k = parts.Length - 1; k >= 1; k--)
            {
                string arch = cp.Resolve(dir, string.Join("/", parts, 0, k) + ".m2d", false);
                M2dArchive a = Archive(arch);
                if (a == null) continue;
                M2dEntry e = a.Find(string.Join("/", parts, k, parts.Length - k));
                if (e != null) return a.Read(e);
            }
            return null;
        }

        public void Dispose()
        {
            foreach (M2dArchive a in open.Values) if (a != null) a.Dispose();
            open.Clear();
        }
    }

    public sealed class WbAssetCopy
    {
        public int Models, Textures;
        public long Bytes;
        /// <summary>Lines for the list of missing things.</summary>
        public readonly List<string> Missing = new List<string>();
    }

    public static class WbAssets
    {
        static readonly string[] ImageEnds = { ".dds", ".tga", ".png", ".bmp" };

        static bool Printable(byte b) { return b >= 0x20 && b <= 0x7e; }

        static bool EndsAt(byte[] b, int end, string ext)
        {
            if (end < ext.Length) return false;
            for (int i = 0; i < ext.Length; i++)
            {
                int c = b[end - ext.Length + i];
                if (c >= 'A' && c <= 'Z') c += 32;
                if (c != ext[i]) return false;
            }
            return true;
        }

        /// <summary>The texture file names a model file holds, without folders, in order of first use. A name is a
        /// run of printable bytes that ends in an image file ending; the 4-byte length the file stores before each
        /// of its strings says where the name starts.</summary>
        public static List<string> TextureNames(byte[] nif)
        {
            var res = new List<string>();
            var seen = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (int end = 5; end <= nif.Length; end++)
            {
                bool hit = false;
                foreach (string x in ImageEnds) if (EndsAt(nif, end, x)) { hit = true; break; }
                if (!hit) continue;
                int start = -1, run = end;
                while (run > 0 && end - run < 260 && Printable(nif[run - 1])) run--;
                for (int p = end - 5; p >= run && p >= 4; p--)
                    if (BitConverter.ToUInt32(nif, p - 4) == (uint)(end - p)) { start = p; break; }
                if (start < 0) start = run;
                if (end - start < 5) continue;
                var sb = new System.Text.StringBuilder();
                for (int i = start; i < end; i++) sb.Append((char)nif[i]);
                string name = sb.ToString().Replace('\\', '/');
                name = name.Substring(name.LastIndexOf('/') + 1);
                if (name.Length < 5) continue;
                string key = name.ToLowerInvariant();
                if (!seen.ContainsKey(key)) { seen[key] = true; res.Add(name); }
            }
            return res;
        }

        static bool Write(string root, string assetPath, byte[] data, WbAssetCopy r)
        {
            string full = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string p = Path.GetFullPath(Path.Combine(root, assetPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            if (!p.StartsWith(full, StringComparison.Ordinal)) throw new ModKitException("asset path outside the project: " + assetPath);
            if (File.Exists(p)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllBytes(p, data);
            r.Bytes += data.Length;
            return true;
        }

        /// <summary>Copies the files of the asset ids into root at their asset paths, and for every model file among
        /// them the texture files it names. A texture name is looked up in allPaths by file name; every file of that
        /// name is copied. paths: asset id to path (null for an id the asset list does not have).</summary>
        public static WbAssetCopy Copy(string root, WbAssetSource src, IList<KeyValuePair<string, string>> paths, IEnumerable<string> allPaths)
        {
            var r = new WbAssetCopy();
            var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string p in allPaths)
            {
                string n = p.Substring(p.LastIndexOf('/') + 1).ToLowerInvariant();
                List<string> l;
                if (!byName.TryGetValue(n, out l)) byName[n] = l = new List<string>();
                if (!l.Contains(p)) l.Add(p);
            }
            var done = new Dictionary<string, bool>(StringComparer.Ordinal);
            var noName = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> a in paths)
            {
                if (a.Value == null) { r.Missing.Add("asset " + a.Key + ": not in the asset list"); continue; }
                if (done.ContainsKey(a.Value)) continue;
                done[a.Value] = true;
                byte[] data = src.Read(a.Value);
                if (data == null) { r.Missing.Add("asset " + a.Key + ": " + a.Value + " is in no archive"); continue; }
                bool nif = a.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase);
                if (Write(root, a.Value, data, r) && nif) r.Models++;
                if (!nif) continue;
                foreach (string t in TextureNames(data))
                {
                    List<string> cands;
                    if (!byName.TryGetValue(t.ToLowerInvariant(), out cands))
                    {
                        if (!noName.ContainsKey(t.ToLowerInvariant())) { noName[t.ToLowerInvariant()] = true; r.Missing.Add("texture " + t + " (named by " + a.Value + "): not in the asset list"); }
                        continue;
                    }
                    foreach (string c in cands)
                    {
                        if (done.ContainsKey(c)) continue;
                        done[c] = true;
                        byte[] td = src.Read(c);
                        if (td == null) { r.Missing.Add("texture " + c + " (named by " + a.Value + "): in no archive"); continue; }
                        if (Write(root, c, td, r)) r.Textures++;
                    }
                }
            }
            return r;
        }
    }
}
