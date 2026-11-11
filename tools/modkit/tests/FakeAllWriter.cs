using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    /// <summary>
    /// Writes the made-up quests, npcs, shops, drop boxes, items, skills and effects of the test fixtures as one data
    /// set to the folder in MS2_FAKE_ALL_OUT (kms, gms, server; the first fixture's copy of an entry wins), for trying
    /// the views and their id pickers by hand. Runs only when asked for by name.
    /// </summary>
    [TestFixture, Explicit]
    public class FakeAllWriter
    {
        [Test]
        public void WriteFakeAll()
        {
            string to = Environment.GetEnvironmentVariable("MS2_FAKE_ALL_OUT");
            if (string.IsNullOrEmpty(to)) Assert.Ignore("MS2_FAKE_ALL_OUT is not set");
            using (var q = new FakeQuests(true, true, true))
            using (var n = new FakeNpcs(true, true, true))
            using (var g = new FakeGroups())
            using (var s = new FakeSkills())
            using (var i = new FakeItems(true))
            {
                foreach (string root in new[] { "kms", "gms", "server" })
                    foreach (string archive in new[] { "Xml.m2d", "Server.m2d", Path.Combine("Resource", "Image.m2d") })
                    {
                        // the item fixture has one client folder: it goes with the kms-client
                        string items = Path.Combine(i.Dir, root == "kms" ? "client" : root);
                        Merge(new[] { Path.Combine(q.Dir, root), Path.Combine(n.Dir, root), Path.Combine(g.Dir, root), Path.Combine(s.Dir, root), items }, archive, Path.Combine(to, root));
                    }
            }
        }

        // npc 2900002 gets a shop and a drop box of the group fixture, so the npc view has fields to pick them in
        static byte[] Patch(string name, byte[] data)
        {
            if (!string.Equals(name, "npc/02/90/02900002.xml", StringComparison.OrdinalIgnoreCase)) return data;
            string t = Encoding.UTF8.GetString(data);
            t = t.Replace("<basic ", "<basic shopId=\"9001\" ").Replace("</environment>", "\t<dropiteminfo individualDropBoxId=\"7001\" />\r\n\t</environment>");
            return Encoding.UTF8.GetBytes(t);
        }

        static void Merge(string[] dirs, string archive, string to)
        {
            var l = new List<KeyValuePair<string, byte[]>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            PackVersion version = PackVersion.MS2F;
            bool any = false;
            foreach (string dir in dirs)
            {
                string path = Path.Combine(dir, archive);
                if (!File.Exists(path)) continue;
                using (M2dArchive a = M2dArchive.Open(path))
                {
                    if (!any) version = a.Version;
                    any = true;
                    foreach (M2dEntry e in a.Entries)
                        if (seen.Add(e.Name)) l.Add(new KeyValuePair<string, byte[]>(e.Name, Patch(e.Name, a.Read(e))));
                }
            }
            if (!any) return;
            string outPath = Path.Combine(to, archive);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            M2dWriter.Create(outPath, version, M2dFlag.AesZlib, l);
        }
    }
}
