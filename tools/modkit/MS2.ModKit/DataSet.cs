// Where the original game data is: named roots ("client", "gms-client", "server"; see Profile) mapped to folders. Archives are named
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

        /// <summary>
        /// The three data profiles at once: the Korean client's data folder (root "client"), the global client's
        /// (root "gms-client") and the server's (root "server"); any of them may be null.
        /// </summary>
        public DataSet(string kmsClientData, string gmsClientData, string serverData)
        {
            if (!string.IsNullOrEmpty(kmsClientData)) AddTarget(Profile.KmsClient, kmsClientData);
            if (!string.IsNullOrEmpty(gmsClientData)) AddTarget(Profile.GmsClient, gmsClientData);
            if (!string.IsNullOrEmpty(serverData)) AddTarget(Profile.Server, serverData);
        }

        public void AddTarget(Profile profile, string folder) { AddRoot(profile.Root, folder); }

        /// <summary>The profiles this data set has, in the fixed order kms-client, gms-client, server.</summary>
        public List<Profile> Profiles
        {
            get
            {
                var list = new List<Profile>();
                foreach (Profile p in Profile.All) if (roots.ContainsKey(p.Root)) list.Add(p);
                return list;
            }
        }

        public bool Has(Profile p) { return p != null && roots.ContainsKey(p.Root); }

        /// <summary>The profile of an archive name, or null for a root that is not a profile.</summary>
        public static Profile ProfileOf(string archiveRef) { return Profile.OfArchive(archiveRef); }

        public void AddRoot(string name, string folder)
        {
            name = Profile.CanonicalRoot(name);
            if (!Directory.Exists(folder)) throw new ModKitException("data root \"" + name + "\": no folder " + folder);
            roots[name] = Path.GetFullPath(folder);
        }

        public ICollection<string> Roots { get { return roots.Keys; } }

        readonly Dictionary<string, string[]> features = new Dictionary<string, string[]>(StringComparer.Ordinal);

        /// <summary>
        /// Overrides the locale and/or the server type a profile's features are worked out for: "LOCALE/TYPE", for
        /// example "NA/Live", "KR" or "/Dev". Null or "" keeps the profile's own (its locale, type "Live").
        /// </summary>
        public void SetFeatures(Profile profile, string localeAndType)
        {
            string locale, type;
            FeatureSet.ParseOption(localeAndType, out locale, out type);
            features[profile.Root] = new[] { locale, type };
        }

        /// <summary>The locale and the server type the features of a profile are worked out for.</summary>
        public void FeatureOption(Profile profile, out string locale, out string serverType)
        {
            string[] o;
            features.TryGetValue(profile.Root, out o);
            locale = o != null && o[0] != null ? o[0] : profile.Locale;
            serverType = o != null && o[1] != null ? o[1] : FeatureSet.DefaultServerType;
        }

        public string RootFolder(string name)
        {
            string f;
            return roots.TryGetValue(Profile.CanonicalRoot(name), out f) ? f : null;
        }

        /// <summary>"Client:Resource\Image.m2d" -> "client:Resource/Image.m2d".</summary>
        public static string NormalizeRef(string archiveRef)
        {
            int c = archiveRef.IndexOf(':');
            if (c <= 0) throw new ModKitException("archive \"" + archiveRef + "\" must be <root>:<path>, for example client:Xml.m2d");
            string path = archiveRef.Substring(c + 1).Replace('\\', '/').TrimStart('/');
            if (path.Length == 0 || path.IndexOf("..", StringComparison.Ordinal) >= 0) throw new ModKitException("bad archive path in \"" + archiveRef + "\"");
            return Profile.CanonicalRoot(archiveRef.Substring(0, c)) + ":" + path;
        }

        public static string RootOf(string archiveRef) { return archiveRef.Substring(0, archiveRef.IndexOf(':')); }
        public static string PathOf(string archiveRef) { return archiveRef.Substring(archiveRef.IndexOf(':') + 1); }

        /// <summary>The file of an archive. Names are matched without regard to case when the exact one is missing.</summary>
        public string Resolve(string archiveRef)
        {
            archiveRef = NormalizeRef(archiveRef);
            string root = RootOf(archiveRef), folder;
            if (!roots.TryGetValue(root, out folder))
                throw new ModKitException("archive " + archiveRef + ": the data root \"" + root + "\" is not set" + (Profile.Find(root) != null ? " (" + Profile.Find(root).EnvVar + ")" : ""));
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
            // what the planner keeps here may hold files open (the image archives of the icon check)
            foreach (object o in Cache.Values)
            {
                IDisposable d = o as IDisposable;
                if (d != null) d.Dispose();
            }
            Cache.Clear();
        }
    }
}
