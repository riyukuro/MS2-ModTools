// Where the original game data is: named roots ("client", "server") mapped to folders. Archives are named
// "<root>:<path under the root>", for example "client:Xml.m2d" or "client:Resource/Image.m2d".
// The data set only ever reads.
using System;
using System.Collections.Generic;
using System.IO;
using MS2.ModKit.M2d;

namespace MS2.ModKit
{
    public sealed class DataSet : IDisposable
    {
        readonly Dictionary<string, string> roots = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<string, M2dArchive> open = new Dictionary<string, M2dArchive>(StringComparer.Ordinal);

        // results of scans over the original data (used ids of a place), kept as long as the data set
        internal readonly Dictionary<string, object> Cache = new Dictionary<string, object>(StringComparer.Ordinal);

        public DataSet() { }

        /// <summary>Client data folder (the one that holds Xml.m2d) and server data folder; either may be null.</summary>
        public DataSet(string clientData, string serverData)
        {
            if (!string.IsNullOrEmpty(clientData)) AddRoot("client", clientData);
            if (!string.IsNullOrEmpty(serverData)) AddRoot("server", serverData);
        }

        public void AddRoot(string name, string folder)
        {
            name = name.ToLowerInvariant();
            if (!Directory.Exists(folder)) throw new ModKitException("data root \"" + name + "\": no folder " + folder);
            roots[name] = Path.GetFullPath(folder);
        }

        public ICollection<string> Roots { get { return roots.Keys; } }

        public string RootFolder(string name)
        {
            string f;
            return roots.TryGetValue(name.ToLowerInvariant(), out f) ? f : null;
        }

        /// <summary>"Client:Resource\Image.m2d" -> "client:Resource/Image.m2d".</summary>
        public static string NormalizeRef(string archiveRef)
        {
            int c = archiveRef.IndexOf(':');
            if (c <= 0) throw new ModKitException("archive \"" + archiveRef + "\" must be <root>:<path>, for example client:Xml.m2d");
            string path = archiveRef.Substring(c + 1).Replace('\\', '/').TrimStart('/');
            if (path.Length == 0 || path.IndexOf("..", StringComparison.Ordinal) >= 0) throw new ModKitException("bad archive path in \"" + archiveRef + "\"");
            return archiveRef.Substring(0, c).ToLowerInvariant() + ":" + path;
        }

        public static string RootOf(string archiveRef) { return archiveRef.Substring(0, archiveRef.IndexOf(':')); }
        public static string PathOf(string archiveRef) { return archiveRef.Substring(archiveRef.IndexOf(':') + 1); }

        /// <summary>The file of an archive. Names are matched without regard to case when the exact one is missing.</summary>
        public string Resolve(string archiveRef)
        {
            archiveRef = NormalizeRef(archiveRef);
            string root = RootOf(archiveRef), folder;
            if (!roots.TryGetValue(root, out folder))
                throw new ModKitException("archive " + archiveRef + ": the data root \"" + root + "\" is not set" + (root == "client" ? " (MS2_CLIENT_DATA)" : root == "server" ? " (MS2_SERVER_DATA)" : ""));
            string cur = folder;
            foreach (string part in PathOf(archiveRef).Split('/'))
            {
                string next = Path.Combine(cur, part);
                if (!File.Exists(next) && !Directory.Exists(next) && Directory.Exists(cur))
                    foreach (string cand in Directory.GetFileSystemEntries(cur))
                        if (string.Equals(Path.GetFileName(cand), part, StringComparison.OrdinalIgnoreCase)) { next = cand; break; }
                cur = next;
            }
            if (!File.Exists(cur)) throw new ModKitException("archive " + archiveRef + ": no file " + cur);
            return cur;
        }

        public bool Exists(string archiveRef)
        {
            try { Resolve(archiveRef); return true; }
            catch (ModKitException) { return false; }
        }

        /// <summary>The archive, opened once and kept until Dispose.</summary>
        public M2dArchive Open(string archiveRef)
        {
            archiveRef = NormalizeRef(archiveRef);
            M2dArchive a;
            if (open.TryGetValue(archiveRef, out a)) return a;
            a = M2dArchive.Open(Resolve(archiveRef));
            open[archiveRef] = a;
            return a;
        }

        /// <summary>Every .m2d under the roots, as archive names.</summary>
        public List<string> ListArchives()
        {
            var list = new List<string>();
            foreach (KeyValuePair<string, string> r in roots)
                foreach (string f in Directory.GetFiles(r.Value, "*.m2d", SearchOption.AllDirectories))
                    list.Add(r.Key + ":" + f.Substring(r.Value.Length).TrimStart('/', '\\').Replace('\\', '/'));
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        public void Dispose()
        {
            foreach (M2dArchive a in open.Values) a.Dispose();
            open.Clear();
        }
    }
}
