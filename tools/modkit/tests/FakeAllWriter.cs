using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    /// <summary>
    /// Writes the made-up quests, npcs, shops, drop boxes, items, skills, effects, maps, interact objects and dungeons of the test fixtures as one data
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
            using (var t = new FakeIdTables())
            {
                foreach (string root in new[] { "kms", "gms", "server" })
                    foreach (string archive in new[] { "Xml.m2d", "Server.m2d", Path.Combine("Resource", "Image.m2d") })
                    {
                        // the item fixture has one client folder: it goes with the kms-client
                        string items = Path.Combine(i.Dir, root == "kms" ? "client" : root);
                        Merge(new[] { Path.Combine(q.Dir, root), Path.Combine(n.Dir, root), Path.Combine(g.Dir, root), Path.Combine(s.Dir, root), items, Path.Combine(t.Dir, root) }, archive, Path.Combine(to, root));
                    }
            }
        }

        // npc 2900002 gets a shop and a drop box of the group fixture and a skill of the skill fixture, and item
        // 1200001 a skill, an additional effect and a repacking scroll, and quest 701 a map condition, so the views have fields to pick them in
        static byte[] Patch(string name, byte[] data)
        {
            if (string.Equals(name, "npc/02/90/02900002.xml", StringComparison.OrdinalIgnoreCase))
            {
                string t = Encoding.UTF8.GetString(data);
                t = t.Replace("<basic ", "<basic shopId=\"9001\" ").Replace("</environment>",
                    "\t<dropiteminfo individualDropBoxId=\"7001\" />\r\n\t\t<skill ids=\"70000001\" levels=\"1\" probs=\"100\" />\r\n\t</environment>");
                return Encoding.UTF8.GetBytes(t);
            }
            string x = Encoding.UTF8.GetString(data);
            // quest 701's talk condition becomes a map one, for the map picker
            const string talk = "<condition type=\"talk\" code=\"32\" />";
            if (x.Contains(talk)) return Encoding.UTF8.GetBytes(x.Replace(talk, "<condition type=\"map\" code=\"9100001\" />"));
            const string item = "<property slotMax=\"1\" sellPrice=\"40\" />";
            if (x.Contains("<item id=\"1200001\">") && x.Contains(item))
                return Encoding.UTF8.GetBytes(x.Replace(item, "<property slotMax=\"1\" sellPrice=\"40\" rePackingScrollID=\"1200002\" />\n\t\t\t<skill skillID=\"70000001\" skillLevel=\"1\" />\n\t\t\t<AdditionalEffect id=\"71000001\" level=\"1\" />"));
            return data;
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
