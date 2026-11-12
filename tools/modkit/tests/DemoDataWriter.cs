using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    /// <summary>
    /// Writes the data set the screenshots in docs/shots are taken with to the folder in MS2_DEMO_OUT (kms, gms,
    /// server): the made-up set of FakeAllWriter, plus drawn icons and portraits, string rows for the text import
    /// and map files for the field row scan. Every name, id and picture in it is made up. Runs only when asked for
    /// by name.
    /// </summary>
    [TestFixture, Explicit]
    public class DemoDataWriter
    {
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        [Test]
        public void WriteDemoData()
        {
            string to = Environment.GetEnvironmentVariable("MS2_DEMO_OUT");
            if (string.IsNullOrEmpty(to)) Assert.Ignore("MS2_DEMO_OUT is not set");
            Write(to);
        }

        /// <summary>Writes the demo data set to a folder (kms, gms, server).</summary>
        internal static void Write(string to)
        {
            FakeAllWriter.Write(to);
            string k = Path.Combine(to, "kms"), g = Path.Combine(to, "gms"), s = Path.Combine(to, "server");

            // our client: working names in string/kr that the source has otherwise, an empty field row, portraits
            Overlay(Path.Combine(k, "Xml.m2d"), PatchNpcs, new[]
            {
                E("string/kr/itemname.xml", Keys("1200001", "Tin Whistle", "1200002", "Clay Drum", "1200003", "Reed Flute", "3300009", "Paper Kite", "5001", "Test Item A", "5002", "Test Item B")),
                E("string/kr/npcname.xml", Keys("2900001", "Fake Mole", "2900002", "Gate Keeper Draft")),
                E("string/kr/mapname.xml", Keys("9100001", "Fake Meadow", "9100002", "Dock Placeholder", "9100003", "Fake Far Shore")),
                E("string/en/mapname.xml", MapNames), E("trigger/fake_meadow/gate.xml", Gate),
                E(RecipeRules.Entry, DemoRecipes(false)), E(RecipeRules.MasteryEntry, DemoMastery),
                E("table/fielddata.xml", Head + "<ms2>\n" +
                    Field("9100001", "fake_meadow") + Field("9100002", "fake_harbor") +
                    "\t<fieldData id=\"9100004\" />\n</ms2>\n"),
            });

            // the source client: the names to import, and the map files of the maps our client cannot open
            Overlay(Path.Combine(g, "Xml.m2d"), null, new[]
            {
                E("string/en/itemname.xml", Keys("1200001", "Tin Whistle", "1200002", "Thunder Drum", "1200003", "Marsh Pipe", "3300009", "Sky Ribbon", "5001", "Copper Bolt", "5002", "Glass Marble")),
                E("string/en/npcname.xml", Keys("2900001", "Fake Mole", "2900002", "Clay Guard")),
                E("string/en/mapname.xml", MapNames), E("trigger/fake_meadow/gate.xml", Gate),
                E("map/09100004.xml", Map("fake_cove", "\t\t<property continentCode=\"1\" mapType=\"1\" revivalreturnid=\"09100001\" />\n\t\t<bgm id=\"3\" />\n")),
                E("map/09100005.xml", Map("fake_ridge", "\t\t<property continentCode=\"1\" mapType=\"2\" />\n\t\t<indoor type=\"1\" />\n")),
                E("map/09100006.xml", Map("fake_attic", "")),
                E(RecipeRules.Entry, DemoRecipes(true)), E(RecipeRules.MasteryEntry, DemoMastery),
            });

            // the server has two of the three new maps: the third is listed unticked
            Overlay(Path.Combine(s, "Xml.m2d"), null, new[]
            {
                E("map/09100004.xml", Map("fake_cove", "")), E("map/09100005.xml", Map("fake_ridge", "")),
                E("string/en/mapname.xml", MapNames), E("trigger/fake_meadow/gate.xml", Gate),
                E(RecipeRules.Entry, DemoRecipes(true)), E(RecipeRules.MasteryEntry, DemoMastery),
            });

            // the map files of our client: the objects the trigger lint checks the ids of the scripts against (the
            // gate script names box 102, which the map does not have), a few props for the Maps view, and no file for
            // fake_attic
            var exported = new List<KeyValuePair<string, byte[]>>
            {
                E("flat/demo/triggerbox_demo.flat", With(Flat("TriggerBox_Demo", "TriggerObjectID", "MS2TriggerObject", "MS2TriggerBox"), "ShapeDimensions", "Point3", "150, 150, 150")),
                E("flat/demo/triggermesh_demo.flat", Flat("TriggerMesh_Demo", "TriggerObjectID", "MS2TriggerObject", "MS2TriggerMesh")),
                E("flat/demo/triggermodel_demo.flat", Flat("TriggerModel_Demo", "TriggerModelID", "MS2TriggerModel")),
                E("flat/demo/crate_demo.flat", Flat("Crate_Demo", null)),
                E("flat/demo/lantern_demo.flat", Flat("Lantern_Demo", null)),
                // the kinds the Maps view adds: a spawn point, a portal, an NPC spawn point, an interact object
                E("flat/demo/spawnpoint_demo.flat", Flat("SpawnPoint_Demo", "SpawnPointID", "SpawnPoint")),
                E("flat/demo/portal_demo.flat", With(With(Flat("Portal_Demo", "PortalID", "Portal"), "TargetFieldSN", "SInt32", "0"), "TargetPortalID", "SInt32", "0")),
                E("flat/demo/spawnpointnpc_demo.flat", With(With(Flat("SpawnPointNPC_Demo", "SpawnPointID", "SpawnPoint", "SpawnPointNPC"), "NpcList", "AssocUInt32", null), "NpcCount", "UInt32", "1")),
                E("flat/demo/interact_demo.flat", Flat("Interact_Demo", "interactID", "MS2InteractObject")),
                E("xblock/fake_meadow.xblock", XBlock(
                    Ent("TriggerBox_Demo", "GateBox", "Position", "0, 450, 0", "TriggerObjectID", "101") +
                    Ent("TriggerMesh_Demo", "GateLeft", "Position", "-150, 600, 0", "TriggerObjectID", "3001") +
                    Ent("TriggerModel_Demo", "GateScript", "Position", "0, 0, 0", "TriggerModelID", "9001", "XmlFilePath", @".\Data\Xml\Trigger\fake_meadow\gate.xml") +
                    Ent("Crate_Demo", "Crate_01", "Position", "300, 150, 0") +
                    Ent("Crate_Demo", "Crate_02", "Position", "450, 150, 0", "Rotation", "0, 0, 90") +
                    Ent("Crate_Demo", "Crate_03", "Position", "450, 150, 150", "Scale", "0.5") +
                    Ent("Lantern_Demo", "Lantern_01", "Position", "-300, 300, 150", "IsVisible", "False") +
                    Ent("Lantern_Demo", "Lantern_02", "Position", "300, 300, 150") +
                    Ent("Portal_Demo", "Portal_ToHarbor", "Position", "-450, 0, 0", "PortalID", "1", "TargetFieldSN", "9100002", "TargetPortalID", "1"))),
            };
            foreach (string block in new[] { "fake_harbor", "fake_far_shore", "fake_cove", "fake_ridge" })
                exported.Add(E("xblock/" + block + ".xblock", XBlock("")));
            M2dWriter.Create(Path.Combine(k, "Resource", "Exported.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, exported);

            var images = new List<KeyValuePair<string, byte[]>>();
            foreach (string name in new[]
            {
                "skill/icon/fake.png", "Skill/icon/fakebleed.png",
                "item/icon/1200001.png", "item/icon/1200002.png", "item/icon/1200003.png", "item/icon/1200005.png", "item/icon/3300009.png",
                "item/icon/5001.png", "item/icon/5002.png", "item/icon/5003.png", "item/icon/9002.png", "item/icon/9100.png",
                "Portrait/Npc/fake_mole.png", "Portrait/Npc/fake_guard.png",
                "MasteryReceipe/10000101.png", "MasteryReceipe/10000102.png", "MasteryReceipe/10000103.png", "MasteryReceipe/30000101.png",
            })
                images.Add(new KeyValuePair<string, byte[]>(name, Icon(name)));
            Directory.CreateDirectory(Path.Combine(k, "Resource"));
            M2dWriter.Create(Path.Combine(k, "Resource", "Image.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, images);
            WriteModels(k);
        }

        // made-up recipes of the Recipes view: crafts and gathering over the demo items; the server asks more mesos
        // for the drum and has one gathering recipe the client lacks
        static string DemoRecipes(bool server)
        {
            return Head + "<ms2>\n" +
                Recipe("10000101", 10, "0", "", new[] { "5001,1,2", "5002,1,1" }, new[] { "1200001,1,1" }, "") +
                Recipe("10000102", 7, "100", server ? "500" : "300", new[] { "5003,1,3", "5001,1,1" }, new[] { "1200002,1,1" }, "") +
                Recipe("10000103", 9, "0", "", new[] { "5002,1,2" }, new[] { "3300009,1,1", "5003,1,1" }, "") +
                Recipe("10000104", 8, "200", "1000", new[] { "1200001,1,1", "5003,1,2" }, new[] { "1200003,1,1" }, "") +
                Recipe("30000101", 3, "0", "", new string[0], new[] { "5003,1,2" }, "9100001") +
                (server ? Recipe("30000102", 4, "0", "", new string[0], new[] { "5002,1,1" }, "9100002") : "") +
                Recipe("40000101", 11, "0", "", new[] { "9002,1,1" }, new[] { "9100,1,1" }, "") +
                "</ms2>\n";
        }

        static string Recipe(string id, int type, string mastery, string meso, string[] materials, string[] results, string habitat)
        {
            var sb = new StringBuilder("\t<receipe id=\"" + id + "\" masteryType=\"" + type + "\" exceptRewardExp=\"0\" requireMastery=\"" + mastery +
                "\" requireMeso=\"" + meso + "\" requireQuest=\"\" rewardExp=\"\" rewardMastery=\"" + (type == 11 ? "0" : "5") + "\" gatheringTime=\"" + (RecipeRules.IsGathering(type) ? "3000" : "") +
                "\" highPropLimitCount=\"\" normalPropLimitCount=\"\"");
            for (int i = 0; i < RecipeRules.Slots; i++) sb.Append(" requireItem" + (i + 1) + "=\"" + (i < materials.Length ? materials[i] : "") + "\"");
            sb.Append(" habitatMapId=\"" + habitat + "\"");
            for (int i = 0; i < RecipeRules.Slots; i++) sb.Append(" rewardItem" + (i + 1) + "=\"" + (i < results.Length ? results[i] : "") + "\"");
            return sb.Append(" feature=\"\" />\n").ToString();
        }

        static string MasteryType(int type, params int[] values)
        {
            var sb = new StringBuilder("\t<mastery type=\"" + type + "\">\n");
            for (int i = 0; i < values.Length; i++) sb.Append("\t\t<v grade=\"" + (i + 1) + "\" value=\"" + values[i] + "\" />\n");
            return sb.Append("\t</mastery>\n").ToString();
        }

        static readonly string DemoMastery = Head + "<ms2>\n" + MasteryType(3, 0, 100, 400) + MasteryType(4, 0, 100, 400) + MasteryType(7, 0, 150, 600) +
            MasteryType(8, 0, 150, 600) + MasteryType(9, 0, 150, 600) + MasteryType(10, 0, 150, 600) + MasteryType(11, 0, 50) + "</ms2>\n";

        static readonly string MapNames = Keys("9100001", "Fake Meadow", "9100002", "Fake Harbor", "9100003", "Fake Far Shore", "9100004", "Fake Cove", "9100005", "Fake Ridge", "9100006", "Fake Attic");

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        static string Keys(params string[] idName)
        {
            string s = Head + "<ms2>\n";
            for (int i = 0; i < idName.Length; i += 2) s += "  <key id=\"" + idName[i] + "\" name=\"" + idName[i + 1] + "\" />\n";
            return s + "</ms2>\n";
        }

        static string Field(string id, string block)
        {
            return "\t<fieldData id=\"" + id + "\">\n\t\t<environment>\n\t\t\t<xblock name=\"" + block + "\" />\n\t\t</environment>\n\t</fieldData>\n";
        }

        static string Map(string block, string body)
        {
            return Head + "<ms2>\n\t<environment feature=\"\" locale=\"\">\n\t\t<xblock name=\"" + block + "\" />\n" + body + "\t</environment>\n</ms2>\n";
        }

        // a script in the named-argument form: a gate of two meshes that opens when a player steps into a box
        const string Gate = Head + "<ms2>\n" +
            "    <state name=\"Closed\">\n" +
            "        <onEnter>\n" +
            "            <action name=\"set_mesh\" trigger_ids=\"3001,3002\" visible=\"1\" />\n" +
            "        </onEnter>\n" +
            "        <condition name=\"user_detected\" box_ids=\"101,102\">\n" +
            "            <transition state=\"Open\" />\n" +
            "        </condition>\n" +
            "    </state>\n" +
            "    <state name=\"Open\">\n" +
            "        <onEnter>\n" +
            "            <action name=\"set_mesh\" trigger_ids=\"3001,3002\" visible=\"0\" />\n" +
            "            <action name=\"set_timer\" timer_id=\"1\" seconds=\"5\" />\n" +
            "        </onEnter>\n" +
            "        <condition name=\"time_expired\" timer_id=\"1\">\n" +
            "            <transition state=\"Closed\" />\n" +
            "        </condition>\n" +
            "    </state>\n" +
            "</ms2>\n";

        static string Flat(string name, string idProp, params string[] mixins)
        {
            var sb = new StringBuilder(Head + "<model name=\"" + name + "\" id=\"1\">\n");
            int id = 2;
            foreach (string m in mixins) sb.Append("\t<mixin name=\"" + m + "\" id=\"" + (id++) + "\" />\n");
            sb.Append("\t<property name=\"Position\" type=\"Point3\" id=\"" + (id++) + "\"><set value=\"0, 0, 0\" /></property>\n");
            sb.Append("\t<property name=\"Rotation\" type=\"Point3\" id=\"" + (id++) + "\"><set value=\"0, 0, 0\" /></property>\n");
            sb.Append("\t<property name=\"Scale\" type=\"Float32\" id=\"" + (id++) + "\"><set value=\"1\" /></property>\n");
            sb.Append("\t<property name=\"IsVisible\" type=\"Boolean\" id=\"" + (id++) + "\"><set value=\"True\" /></property>\n");
            if (idProp != null) sb.Append("\t<property name=\"" + idProp + "\" type=\"SInt32\" id=\"" + id + "\"><set value=\"0\" /></property>\n");
            return sb.Append("</model>\n").ToString();
        }

        /// <summary>The flat with one more property (a null value: no set, as an empty list).</summary>
        static string With(string flat, string name, string type, string value)
        {
            int id = 1;
            while (flat.Contains("id=\"" + id + "\"")) id++;
            string p = "\t<property name=\"" + name + "\" type=\"" + type + "\" id=\"" + id + "\">" + (value == null ? "" : "<set value=\"" + value + "\" />") + "</property>\n";
            return flat.Replace("</model>\n", p + "</model>\n");
        }

        static string Ent(string model, string name, params string[] props)
        {
            var sb = new StringBuilder("    <entity id=\"" + Guid.NewGuid().ToString("N").ToUpperInvariant() + "\" modelName=\"" + model + "\" name=\"" + name + "\" iterations=\"1\">\n");
            for (int i = 0; i < props.Length; i += 2)
                sb.Append("      <property name=\"" + props[i] + "\">\n        <set value=\"" + props[i + 1] + "\" />\n      </property>\n");
            return sb.Append("    </entity>\n").ToString();
        }

        static string XBlock(string entities) { return Head + "<game>\n  <entitySet>\n" + entities + "  </entitySet>\n</game>\n"; }

        // the two npcs of the kms-client bundle get a portrait
        static byte[] PatchNpcs(string name, byte[] data)
        {
            if (!string.Equals(name, "npcdata/029.xml", StringComparison.OrdinalIgnoreCase)) return data;
            string t = Encoding.UTF8.GetString(data);
            t = t.Replace("<environment level=\"5\">", "<environment level=\"5\" portrait=\"./Data/Resource/Image/Portrait/Npc/fake_mole.png\">")
                 .Replace("<environment hpBar=\"1\">", "<environment hpBar=\"1\" portrait=\"./Data/Resource/Image/Portrait/Npc/fake_guard.png\">");
            return Encoding.UTF8.GetBytes(t);
        }

        // rewrites an archive with its entries patched and the given ones put in (in place of an entry of the same name)
        static void Overlay(string path, Func<string, byte[], byte[]> patch, KeyValuePair<string, byte[]>[] put)
        {
            var l = new List<KeyValuePair<string, byte[]>>();
            var mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, byte[]> e in put) mine.Add(e.Key);
            PackVersion version;
            using (M2dArchive a = M2dArchive.Open(path))
            {
                version = a.Version;
                foreach (M2dEntry e in a.Entries)
                {
                    if (mine.Contains(e.Name)) continue;
                    byte[] d = a.Read(e);
                    l.Add(new KeyValuePair<string, byte[]>(e.Name, patch == null ? d : patch(e.Name, d)));
                }
            }
            l.AddRange(put);
            M2dWriter.Create(path, version, M2dFlag.AesZlib, l);
        }

        // ---- a made-up model: a figure of boxes with a striped texture, for the model box of the NPC view ----

        static void WriteModels(string k)
        {
            string dir = Path.Combine(Path.Combine(k, "Resource"), "Model");
            Directory.CreateDirectory(dir);
            var tex = new uint[16 * 16];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                    tex[y * 16 + x] = y < 8 ? (x / 4 % 2 == 0 ? 0xFF4C8FD0u : 0xFFE8E2C8u) : (x / 2 + y / 2) % 2 == 0 ? 0xFF8A5A34u : 0xFFB07840u;
            M2dWriter.Create(Path.Combine(dir, "Npc.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                new KeyValuePair<string, byte[]>("02/90/02900001/fake_kfm_2900001.kfm", Kfm(".\\Fake_Kfm_2900001.nif")),
                new KeyValuePair<string, byte[]>("02/90/02900001/fake_kfm_2900001.nif", Figure("Fake_Figure_D.dds")),
                new KeyValuePair<string, byte[]>("02/90/02900001/fake_figure_d.dds", FakeDds.Bgra(16, 16, tex)),
            });
        }

        static byte[] Kfm(string nif)
        {
            return FakeNif.Make(w =>
            {
                w.Write(Encoding.ASCII.GetBytes(";Gamebryo KFM File Version 30.2.0.3b\n"));
                w.Write((byte)1);
                byte[] s = Encoding.ASCII.GetBytes(nif);
                w.Write(s.Length); w.Write(s);
                w.Write(5); w.Write(Encoding.ASCII.GetBytes("Bip01"));
            });
        }

        // boxes (z up): two legs, a body, two arms, a head and a hat brim; the body and limbs take the lower half of
        // the texture, the head and hat the upper half
        static byte[] Figure(string texture)
        {
            var v = new List<float>();
            var ix = new List<ushort>();
            Box(v, ix, -0.45f, -0.3f, 0f, -0.05f, 0.3f, 0.9f, 0.5f);
            Box(v, ix, 0.05f, -0.3f, 0f, 0.45f, 0.3f, 0.9f, 0.5f);
            Box(v, ix, -0.6f, -0.35f, 0.9f, 0.6f, 0.35f, 2.1f, 0.5f);
            Box(v, ix, -0.95f, -0.2f, 1.1f, -0.6f, 0.2f, 2.05f, 0.5f);
            Box(v, ix, 0.6f, -0.2f, 1.1f, 0.95f, 0.2f, 2.05f, 0.5f);
            Box(v, ix, -0.5f, -0.45f, 2.1f, 0.5f, 0.45f, 3.0f, 0f);
            Box(v, ix, -0.75f, -0.7f, 3.0f, 0.75f, 0.7f, 3.15f, 0f);
            Box(v, ix, -0.35f, -0.3f, 3.15f, 0.35f, 0.3f, 3.6f, 0f);
            var n = new FakeNif();
            int idx = n.Stream("0", new[] { FakeNif.UShort1 }, ix.Count, FakeNif.UShorts(ix.ToArray()));
            int vtx = n.Stream("1", new[] { FakeNif.Float3, FakeNif.Float3, FakeNif.Float2 }, v.Count / 8, FakeNif.Floats(v.ToArray()));
            int[] props = { n.Texturing(n.SourceTexture(texture)) };
            int mesh = n.Mesh("figure", new[] { idx, vtx }, new[] { new[] { "INDEX" }, new[] { "POSITION", "NORMAL", "TEXCOORD" } }, props);
            n.Node("root", null, 1, new[] { mesh });
            return n.ToBytes();
        }

        static void Box(List<float> v, List<ushort> ix, float x0, float y0, float z0, float x1, float y1, float z1, float vTop)
        {
            float[] lo = { x0, y0, z0 }, hi = { x1, y1, z1 };
            for (int axis = 0; axis < 3; axis++)
                for (int side = 0; side < 2; side++)
                {
                    int a = (axis + 1) % 3, b = (axis + 2) % 3;
                    int at = v.Count / 8;
                    for (int c = 0; c < 4; c++)
                    {
                        bool ua = c == 1 || c == 2, ub = c >= 2;
                        var p = new float[3];
                        p[axis] = side == 0 ? lo[axis] : hi[axis];
                        p[a] = ua ? hi[a] : lo[a];
                        p[b] = ub ? hi[b] : lo[b];
                        var nrm = new float[3];
                        nrm[axis] = side == 0 ? -1 : 1;
                        v.AddRange(p); v.AddRange(nrm);
                        v.Add(ua ? 1f : 0f); v.Add(vTop + (ub ? 0f : 0.5f));
                    }
                    // a x b points along +axis: that winding for the far side, the other for the near side
                    int[] order = side == 1 ? new[] { 0, 1, 2, 0, 2, 3 } : new[] { 0, 2, 1, 0, 3, 2 };
                    foreach (int o in order) ix.Add((ushort)(at + o));
                }
        }

        // ---- drawn pictures: a plain shape in a colour picked from the entry name ----

        /// <summary>A 64x64 PNG: a disc, a diamond or a rounded square on a clear ground, coloured from the name.</summary>
        internal static byte[] Icon(string name)
        {
            const int n = 64;
            uint h = 2166136261;
            foreach (char c in name.ToLowerInvariant()) h = (h ^ c) * 16777619;
            int r = 70 + (int)(h & 0x7f), g = 70 + (int)((h >> 8) & 0x7f), b = 70 + (int)((h >> 16) & 0x7f);
            int shape = (int)((h >> 24) % 3);
            var raw = new byte[n * (1 + n * 4)];
            for (int y = 0; y < n; y++)
            {
                int o = y * (1 + n * 4) + 1;
                for (int x = 0; x < n; x++, o += 4)
                {
                    double dx = x - 31.5, dy = y - 31.5;
                    double d = shape == 0 ? Math.Sqrt(dx * dx + dy * dy) : shape == 1 ? (Math.Abs(dx) + Math.Abs(dy)) * 0.75 : Math.Max(Math.Abs(dx), Math.Abs(dy)) * 1.1;
                    if (d > 28) continue;
                    bool edge = d > 24;
                    double light = edge ? 0.55 : 1.0 - (dx + dy) / 160.0;
                    raw[o] = Clamp(r * light); raw[o + 1] = Clamp(g * light); raw[o + 2] = Clamp(b * light); raw[o + 3] = 255;
                }
            }
            var ms = new MemoryStream();
            ms.Write(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }, 0, 8);
            Chunk(ms, "IHDR", new byte[] { 0, 0, 0, n, 0, 0, 0, n, 8, 6, 0, 0, 0 });
            var z = new MemoryStream();
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true)) zs.Write(raw, 0, raw.Length);
            Chunk(ms, "IDAT", z.ToArray());
            Chunk(ms, "IEND", new byte[0]);
            return ms.ToArray();
        }

        static byte Clamp(double v) { return (byte)(v < 0 ? 0 : v > 255 ? 255 : v); }

        static void Chunk(Stream to, string type, byte[] data)
        {
            byte[] t = Encoding.ASCII.GetBytes(type);
            to.Write(Be((uint)data.Length), 0, 4);
            to.Write(t, 0, 4);
            to.Write(data, 0, data.Length);
            uint c = 0xffffffff;
            foreach (byte[] part in new[] { t, data })
                foreach (byte v in part)
                {
                    c ^= v;
                    for (int i = 0; i < 8; i++) c = (c & 1) != 0 ? (c >> 1) ^ 0xedb88320 : c >> 1;
                }
            to.Write(Be(~c), 0, 4);
        }

        static byte[] Be(uint v) { return new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }; }
    }
}
