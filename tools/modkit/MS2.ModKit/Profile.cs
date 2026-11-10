// Data profiles: the three kinds of game data a mod can be applied to.
//
//   kms-client   the Korean client's Data folder. Items, npcs and skills are bundles (itemdata/<id/100000>.xml
//                with <item id> rows); region tables sit in table/kr; strings in cn de en jp kr pt.
//   gms-client   the global client's Data folder. One file per item, npc and skill (item/<a>/<bb>/<id>.xml);
//                region tables in table/na; strings in de en pt.
//   server       the server's data: Xml.m2d in the global client's layout (a superset of it: some attributes,
//                tables and string folders only the server has) and Server.m2d, which no client has.
//
// A data set (DataSet) holds any of the three, each as a data root. The root of a profile is the first word of
// an archive name in a mod: "client:Xml.m2d" (kms-client; the name is kept from the time there was one client),
// "gms-client:Xml.m2d", "server:Xml.m2d". "kms-client:" may be written for "client:", and "gms:" for
// "gms-client:".
//
// What a plan carries between profiles (Planner, PlannerSync, PlannerMirror):
//   kms-client <-> server, gms-client   the field maps (Maps/*.fieldmap.xml): bundle rows against per-id files
//   server     <-> gms-client           the same entry, row and attribute (identity), minus what the target's own
//                                       shipped archive never has (server-only attributes, tables and files)
using System;
using System.Collections.Generic;

namespace MS2.ModKit
{
    public sealed class Profile
    {
        /// <summary>"kms-client", "gms-client", "server".</summary>
        public readonly string Name;
        /// <summary>The data root of the profile: the first word of its archive names ("client", "gms-client", "server").</summary>
        public readonly string Root;
        /// <summary>True for the bundle layout (itemdata/, npcdata/, skilldata/); false for one file per id (item/, npc/, skill/).</summary>
        public readonly bool Bundles;
        public readonly bool IsServer;
        /// <summary>The folder of the region tables under table/: "kr" or "na".</summary>
        public readonly string Region;
        /// <summary>The string folders the game of this profile reads (string/&lt;language&gt;/).</summary>
        public readonly string[] Languages;
        /// <summary>The environment variable and the setting that name the data folder.</summary>
        public readonly string EnvVar;
        public readonly string Title;

        Profile(string name, string root, bool bundles, bool server, string region, string[] languages, string env, string title)
        {
            Name = name; Root = root; Bundles = bundles; IsServer = server; Region = region; Languages = languages; EnvVar = env; Title = title;
        }

        public static readonly Profile KmsClient = new Profile("kms-client", "client", true, false, "kr", new[] { "cn", "de", "en", "jp", "kr", "pt" }, "MS2_CLIENT_DATA", "Korean client");
        public static readonly Profile GmsClient = new Profile("gms-client", "gms-client", false, false, "na", new[] { "de", "en", "pt" }, "MS2_GMS_CLIENT_DATA", "global client");
        public static readonly Profile Server = new Profile("server", "server", false, true, "na", new[] { "de", "en", "pt" }, "MS2_SERVER_DATA", "server");

        /// <summary>The three profiles in their fixed order.</summary>
        public static readonly Profile[] All = { KmsClient, GmsClient, Server };

        /// <summary>The root a name stands for: "kms-client" -> "client", "gms" -> "gms-client"; other names as they are (lower case).</summary>
        public static string CanonicalRoot(string name)
        {
            string n = (name ?? "").Trim().ToLowerInvariant();
            if (n == "kms-client" || n == "kms") return KmsClient.Root;
            if (n == "gms" || n == "gms-client") return GmsClient.Root;
            return n;
        }

        /// <summary>The profile of a profile name, a root name or an alias; null for another name.</summary>
        public static Profile Find(string nameOrRoot)
        {
            string root = CanonicalRoot(nameOrRoot);
            foreach (Profile p in All) if (p.Root == root) return p;
            return null;
        }

        /// <summary>The profile of an archive name ("gms-client:Xml.m2d"); null for a root that is not a profile.</summary>
        public static Profile OfArchive(string archive)
        {
            int c = archive.IndexOf(':');
            return c <= 0 ? null : Find(archive.Substring(0, c));
        }

        /// <summary>The same archive in another profile: ("server:Xml.m2d", gms-client) -> "gms-client:Xml.m2d".</summary>
        public string ArchiveIn(string archive) { return Root + ":" + DataSet.PathOf(archive); }

        /// <summary>
        /// An entry of this profile as the other profile names it: the region folder is swapped
        /// ("table/kr/x.xml" of kms-client is "table/na/x.xml" of gms-client); every other entry keeps its name.
        /// </summary>
        public string EntryIn(string entry, Profile to)
        {
            if (to == null || to.Region == Region) return entry;
            string e = entry.Replace('\\', '/');
            string from = "table/" + Region + "/";
            if (e.StartsWith(from, StringComparison.OrdinalIgnoreCase)) return "table/" + to.Region + "/" + e.Substring(from.Length);
            return entry;
        }

        /// <summary>The language folder of a string entry ("string/en/itemname.xml" -> "en"), or null.</summary>
        public static string LanguageOf(string entry)
        {
            string e = entry.Replace('\\', '/');
            if (!e.StartsWith("string/", StringComparison.OrdinalIgnoreCase)) return null;
            int s = e.IndexOf('/', 7);
            return s < 0 ? null : e.Substring(7, s - 7);
        }

        public bool ReadsLanguage(string lang)
        {
            foreach (string l in Languages) if (string.Equals(l, lang, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>The profiles that share the per-id layout and so mirror each other one to one (gms-client and server).</summary>
        public bool Mirrors(Profile other) { return other != null && other != this && !Bundles && !other.Bundles; }

        public override string ToString() { return Name; }
    }
}
