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
            // the same rule as for a name that is written: no ':' (a drive letter), no ".." part, no control character
            if (OutPath.Why(string.Join("/", parts)) != null) return null;
            foreach (string p in parts) if (p == ".") return null;
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
        /// <summary>Files written: model files (.nif), the texture files those name, and files of any other kind
        /// that an asset id names.</summary>
        public int Models, Textures, Others;
        public long Bytes;
        /// <summary>Lines for the list of missing things.</summary>
        public readonly List<string> Missing = new List<string>();
        /// <summary>Asset paths that were not written because they could leave the project folder.</summary>
        public readonly List<string> Refused = new List<string>();
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
                if (start < 0)
                {
                    // no length in front: a name that goes on ("a.dds.bak") is not a texture name
                    if (end < nif.Length && (nif[end] == '.' || nif[end] == '_' || char.IsLetterOrDigit((char)nif[end]))) continue;
                    start = run;
                }
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

        /// <summary>The path an asset path is written at, with the letter case of the folders and files that are
        /// already there (so /Model/a and /model/b go into one folder on a system that tells the two apart); null,
        /// and a line in r.Refused, when the name could leave root.</summary>
        static string Dest(CasePaths cp, string root, string assetPath, WbAssetCopy r)
        {
            // an asset path is written "/A/B/file": that one leading '/' is the list's form, anything more is refused
            string rel = assetPath.StartsWith("/", StringComparison.Ordinal) ? assetPath.Substring(1) : assetPath;
            string why = OutPath.Why(rel);
            if (why == null)
            {
                why = OutPath.WhyNotUnder(root, cp.Resolve(root, rel, false));
                if (why == null) return Path.GetFullPath(cp.Resolve(root, rel, true));
            }
            r.Refused.Add(OutPath.Line("asset path", assetPath, why));
            return null;
        }

        static bool Write(string p, byte[] data, WbAssetCopy r)
        {
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
            var cp = new CasePaths();
            root = Path.GetFullPath(root);
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
                // a refused name is not read, and the textures of a refused model file are not copied
                string dest = Dest(cp, root, a.Value, r);
                if (dest == null) continue;
                byte[] data = src.Read(a.Value);
                if (data == null) { r.Missing.Add("asset " + a.Key + ": " + a.Value + " is in no archive"); continue; }
                bool nif = a.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase);
                if (Write(dest, data, r)) { if (nif) r.Models++; else r.Others++; }
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
                        string tdest = Dest(cp, root, c, r);
                        if (tdest == null) continue;
                        byte[] td = src.Read(c);
                        if (td == null) { r.Missing.Add("texture " + c + " (named by " + a.Value + "): in no archive"); continue; }
                        if (Write(tdest, td, r)) r.Textures++;
                    }
                }
            }
            return r;
        }
    }
}
