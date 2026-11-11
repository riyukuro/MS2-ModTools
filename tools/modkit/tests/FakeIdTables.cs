// Maps, interact objects and dungeons on made-up data, for the id lists of IdLookup (IdLists.cs):
//   kms-client  table/fielddata.xml (maps 9100001, 9100002), table/interactobject.xml (objects 9200001, 9200002),
//               table/dungeonroom.xml (dungeons 9300001, 9300002), and their name files in string/en/
//   gms-client and server  one file per map (map/09100001.xml, map/09100003.xml: map 9100003 is global only),
//               table/interactobject_mastery.xml (object 9200003), table/na/dungeonroom.xml (dungeon 9300003)
// Dungeon 9300002 and object 9200002 have no name. Every id and name in here is made up.
using System;
using System.Collections.Generic;
using System.IO;
using MS2.ModKit.M2d;

namespace MS2.ModKit.Tests
{
    public sealed class FakeIdTables : IDisposable
    {
        public readonly string Dir;
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        static string Keys(params string[] idName)
        {
            string s = Head + "<ms2>\n";
            for (int i = 0; i < idName.Length; i += 2) s += "  <key id=\"" + idName[i] + "\" name=\"" + idName[i + 1] + "\" />\n";
            return s + "</ms2>\n";
        }

        static string MapFile(string block)
        {
            return Head + "<ms2>\n\t<environment>\n\t\t<xblock name=\"" + block + "\" />\n\t</environment>\n</ms2>\n";
        }

        public FakeIdTables()
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-idtables-" + Guid.NewGuid().ToString("N"));
            string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
            foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);
            string maps = Keys("9100001", "Fake Meadow", "9100002", "Fake Harbor", "9100003", "Fake Far Shore");
            string objects = Keys("9200001", "Fake Lever", "9200003", "Fake Herb Patch");
            string dungeons = Keys("9300001", "Fake Cellar", "9300003", "Fake Clock Tower");
            M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E("table/fielddata.xml", Head + "<ms2>\n" +
                    "\t<fieldData id=\"9100001\">\n\t\t<environment>\n\t\t\t<xblock name=\"fake_meadow\" />\n\t\t</environment>\n\t</fieldData>\n" +
                    "\t<fieldData id=\"9100002\">\n\t\t<environment>\n\t\t\t<xblock name=\"fake_harbor\" />\n\t\t</environment>\n\t</fieldData>\n" +
                    "</ms2>\n"),
                E("table/interactobject.xml", Head + "<ms2>\n\t<interact id=\"9200001\" type=\"1\" />\n\t<interact id=\"9200002\" type=\"1\" />\n</ms2>\n"),
                E("table/dungeonroom.xml", Head + "<ms2>\n\t<dungeonRoom dungeonRoomID=\"9300001\" rewardCount=\"1\" />\n\t<dungeonRoom dungeonRoomID=\"9300002\" rewardCount=\"1\" />\n</ms2>\n"),
                E("string/en/mapname.xml", maps), E("string/en/interactname.xml", objects), E("string/en/stringfieldenterance.xml", dungeons),
            });
            foreach (string d in new[] { g, s })
                M2dWriter.Create(Path.Combine(d, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                {
                    E("map/09100001.xml", MapFile("fake_meadow")), E("map/09100003.xml", MapFile("fake_far_shore")),
                    E("table/interactobject_mastery.xml", Head + "<ms2>\n\t<interact id=\"9200003\" type=\"2\" />\n</ms2>\n"),
                    E("table/na/dungeonroom.xml", Head + "<ms2>\n\t<dungeonRoom dungeonRoomID=\"9300003\" rewardCount=\"1\" />\n</ms2>\n"),
                    E("string/en/mapname.xml", maps), E("string/en/interactname.xml", objects), E("string/en/stringfieldenterance.xml", dungeons),
                });
        }

        public void Dispose()
        {
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }
}
