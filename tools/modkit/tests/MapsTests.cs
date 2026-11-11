// Map mods on made-up data: three targets (Korean client, global client, server), each with its own
// Resource/Exported.m2d that holds two small maps and three models. Every name, id and number here is made up.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MS2.ModKit;
using MS2.ModKit.M2d;
using MS2.ModKit.MapEdit;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    public sealed class FakeMaps : IDisposable
    {
        public const string Crate1 = "10000000000000000000000000000001", Spawn1 = "20000000000000000000000000000002",
            Portal1 = "30000000000000000000000000000003", Crate2 = "f000000000000000000000000000000f";
        public const string MapA = "90000001_ab", MapB = "90000002_ab";
        public const string K = "client:Resource/Exported.m2d", G = "gms-client:Resource/Exported.m2d", S = "server:Resource/Exported.m2d";
        public const string EntryA = "xblock/" + MapA + ".xblock", EntryB = "xblock/" + MapB + ".xblock";
        public const string Tags = "server:Xml.m2d", TagsK = "client:Xml.m2d", TagEntry = "table/mapspawntag.xml";

        public readonly string Dir, KmsDir, GmsDir, ServerDir;
        public DataSet Data;

        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        static XEntity Ent(string id, string model, string name, params string[] kv)
        {
            var e = new XEntity { Id = id, Model = model, Name = name, Iterations = 1 };
            for (int i = 0; i < kv.Length; i += 2) e.Props.Add(new XProp(kv[i], kv[i + 1]));
            return e;
        }

        /// <summary>Map A as the targets ship it. The global client's first crate stands somewhere else.</summary>
        public static List<XEntity> A(bool gms)
        {
            XEntity crate2 = Ent(Crate2, "Crate_A", "Crate_A_2", "Position", "300, 0, 150");
            crate2.Props.Add(new XProp("Tags", new List<XSet> { new XSet("0", "a"), new XSet("1", "b") }));
            return new List<XEntity>
            {
                Ent(Crate1, "Crate_A", "Crate_A_1", "Position", gms ? "0, 0, 300" : "0, 0, 150", "Rotation", "0, 0, 90"),
                Ent(Spawn1, "SpawnPoint_A", "Spawn_1", "Position", "150, 150, 150", "SpawnPointID", "7"),
                Ent(Portal1, "Portal_A", "Portal_1", "TargetFieldSN", "90000002", "TargetPortalID", "1", "PortalID", "1", "Position", "450, 0, 150"),   // not in name order
                crate2,
            };
        }

        public static List<XEntity> B()
        {
            return new List<XEntity>
            {
                Ent(Crate1, "Crate_A", "Crate_B_1", "Position", "0, 0, 150"),
                Ent(Portal1, "Portal_A", "Portal_B_1", "TargetFieldSN", "90000001", "TargetPortalID", "1", "PortalID", "1", "Position", "450, 0, 150"),
            };
        }

        static string Flat(string name, int id, params string[] props)
        {
            var sb = new StringBuilder(Head + "<model name=\"" + name + "\" id=\"" + id + "\">\n");
            for (int i = 0; i < props.Length; i += 3)
                sb.Append("\t<property name=\"" + props[i] + "\" type=\"" + props[i + 1] + "\" id=\"" + (i + 1) + "\">" + (props[i + 2] == null ? "" : "<set value=\"" + props[i + 2] + "\" />") + "</property>\n");
            return sb.Append("</model>\n").ToString();
        }

        public static readonly KeyValuePair<string, byte[]>[] Flats =
        {
            E("flat/a/crate_a.flat", Flat("Crate_A", 1, "Position", "Point3", "0, 0, 0", "Rotation", "Point3", "0, 0, 0", "Tags", "AssocString", null, "IsVisible", "Boolean", "True", "Scale", "Float32", "1")),
            E("flat/a/spawnpoint_a.flat", Flat("SpawnPoint_A", 2, "Position", "Point3", "0, 0, 0", "SpawnPointID", "SInt32", "0", "NpcList", "AssocUInt32", null)),
            E("flat/a/portal_a.flat", Flat("Portal_A", 3, "Position", "Point3", "0, 0, 0", "PortalID", "SInt32", "0", "TargetFieldSN", "SInt32", "0", "TargetPortalID", "SInt32", "0")),
        };

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }
        static KeyValuePair<string, byte[]> E(string name, byte[] bytes) { return new KeyValuePair<string, byte[]>(name, bytes); }

        static void Exported(string dir, bool gms)
        {
            Directory.CreateDirectory(Path.Combine(dir, "Resource"));
            var list = new List<KeyValuePair<string, byte[]>>(Flats);
            list.Add(E(EntryA, XBlock.Bytes(A(gms), gms ? XDialect.Spaced : XDialect.Tab)));
            list.Add(E(EntryB, XBlock.Bytes(B(), XDialect.Tab)));
            list.Add(E("texture/made_up.bin", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
            M2dWriter.Create(Path.Combine(Path.Combine(dir, "Resource"), "Exported.m2d"), gms ? PackVersion.MS2F : PackVersion.NS2F, M2dFlag.AesZlib, list);
        }

        public const string TagRow = "<region mapCode=\"90000001\" spawnPointID=\"7\" difficulty=\"1\" tag=\"wolf\" coolTime=\"20\" population=\"3\" />";
        // a row of another map after it: new rows of the first map go between the two
        public const string TagRowOther = "<region mapCode=\"90000003\" spawnPointID=\"1\" difficulty=\"1\" tag=\"crow\" coolTime=\"20\" population=\"2\" />";
        public const string TagTable = Head + "<ms2>\n\t" + TagRow + "\n\t" + TagRowOther + "\n</ms2>\n";

        public FakeMaps(bool serverLinksToKms = false)
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-maps-" + Guid.NewGuid().ToString("N"));
            KmsDir = Path.Combine(Dir, "orig", "kms");
            GmsDir = Path.Combine(Dir, "orig", "gms");
            ServerDir = Path.Combine(Dir, "orig", "server");
            Directory.CreateDirectory(KmsDir); Directory.CreateDirectory(GmsDir); Directory.CreateDirectory(ServerDir);
            Exported(KmsDir, false);
            Exported(GmsDir, true);
            if (serverLinksToKms)
            {
                // the link's target lies under a second linked folder, as on a machine whose game folder is itself a link
                Directory.CreateSymbolicLink(Path.Combine(Dir, "alias"), Path.Combine(Dir, "orig"));
                Directory.CreateSymbolicLink(Path.Combine(ServerDir, "Resource"), Path.Combine(Dir, "alias", "kms", "Resource"));
            }
            else Exported(ServerDir, false);
            M2dWriter.Create(Path.Combine(KmsDir, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E("table/fielddata.xml", Head + "<ms2>\n\t<fieldData id=\"90000001\" />\n\t<fieldData id=\"90000002\" />\n</ms2>\n"),
                E("npcdata/990.xml", Head + "<ms2>\n\t<npc id=\"99000001\" />\n</ms2>\n"),
                E(TagEntry, TagTable),      // the Korean client ships the table too; the made-up global client does not
            });
            var perId = new List<KeyValuePair<string, byte[]>>
            {
                E("map/90000001.xml", Head + "<ms2 />\n"),
                E("map/90000002.xml", Head + "<ms2 />\n"),
                E("npc/9/90/99000001.xml", Head + "<ms2 />\n"),
            };
            M2dWriter.Create(Path.Combine(GmsDir, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, perId);
            perId.Add(E(TagEntry, TagTable));
            M2dWriter.Create(Path.Combine(ServerDir, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, perId);
            Data = new DataSet(KmsDir, GmsDir, ServerDir);
        }

        public Mod Mod(string id, string body)
        {
            string d = Path.Combine(Dir, "mods", id);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "mod.xml"), "<mod id=\"" + id + "\" name=\"test\" version=\"1\">\n" + body + "\n</mod>\n", new UTF8Encoding(false));
            return MS2.ModKit.Mod.Load(d);
        }

        public Plan Plan(params Mod[] mods) { return Planner.Build(Data, mods, null); }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class MapsTests
    {
        const string K = FakeMaps.K, G = FakeMaps.G, S = FakeMaps.S;

        static string Map(string body) { return "<map xblock=\"" + FakeMaps.MapA + "\">" + body + "</map>"; }
        static string MapB(string body) { return "<map xblock=\"" + FakeMaps.MapB + "\">" + body + "</map>"; }

        const string AddCrate = "<add name=\"crate-1\" model=\"Crate_A\" entity-name=\"Crate_A_900\"><property name=\"Position\" value=\"150, 300, 450\" /></add>";

        static string Portal(string name, int id, int toMap, int toPortal)
        {
            return "<portal name=\"" + name + "\" model=\"Portal_A\" entity-name=\"Portal_" + name + "\" id=\"" + id + "\" to-map=\"" + toMap + "\" to-portal=\"" + toPortal + "\"><property name=\"Position\" value=\"150, 300, 450\" /></portal>";
        }

        static string Spawn(string name, int point, string rows)
        {
            return "<spawn name=\"" + name + "\" model=\"SpawnPoint_A\" entity-name=\"Spawn_" + name + "\" point=\"" + point + "\"><property name=\"Position\" value=\"150, 300, 450\" />" + rows + "</spawn>";
        }

        static byte[] Bytes(Plan p, string archive, string entry)
        {
            PlanArchive pa = p.Find(archive);
            if (pa == null) return null;
            foreach (KeyValuePair<string, byte[]> kv in pa.Put) if (kv.Key == entry) return kv.Value;
            return null;
        }

        static string[] Lines(IEnumerable<PlanMessage> ms) { return ms.Select(m => m.ToString()).ToArray(); }

        // ---- the two conversions ----

        [TestCase(XDialect.Tab)]
        [TestCase(XDialect.Spaced)]
        public void MapToEditorFileAndBack(XDialect dialect)
        {
            FlatIndex ix = FlatIndex.FromFlats(FakeMaps.Flats);
            Assert.That(ix.Count, Is.EqualTo(3));
            byte[] shipped = XBlock.Bytes(FakeMaps.A(false), dialect);
            Assert.That(XBlock.DetectDialect(shipped), Is.EqualTo(dialect));
            string dir = Path.Combine(Path.GetTempPath(), "modkit-mapconv-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var rep = new ConvertReport();
                BlockDoc blk = MapConvert.ToBlock(XBlock.Read(shipped, "a"), FakeMaps.MapA, ix, new ToBlockOptions(), rep);
                string file = Path.Combine(dir, blk.Name + ".block");
                blk.Write(file);
                List<KeyValuePair<string, List<XEntity>>> back = MapConvert.ToXBlocks(BlockDoc.Read(file), ix, PropOrder.Shipped);
                Assert.That(back.Count, Is.EqualTo(1));
                Assert.That(back[0].Key, Is.EqualTo(FakeMaps.MapA + ".xblock"));
                var ents = new List<XEntity>(back[0].Value);
                MapConvert.SortById(ents);
                // an entity whose shipped order is not the name order gets it back from the shipped file
                var orig = new Dictionary<string, List<string>>();
                foreach (XEntity e in XBlock.Read(shipped, "a")) orig[e.Id] = e.Props.Select(x => x.Name).ToList();
                XBlock.ReorderProps(ents, true, orig);
                Assert.That(XBlock.Bytes(ents, dialect), Is.EqualTo(shipped));

                // the editor file itself survives a read and a write
                string text = File.ReadAllText(file);
                BlockDoc.Read(file).Write(file);
                Assert.That(File.ReadAllText(file), Is.EqualTo(text));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void ReadAndWriteKeepTheBytes()
        {
            foreach (XDialect d in new[] { XDialect.Tab, XDialect.Spaced })
            {
                byte[] shipped = XBlock.Bytes(FakeMaps.A(false), d);
                Assert.That(XBlock.Bytes(XBlock.Read(shipped, "a"), d), Is.EqualTo(shipped));
                Assert.That(shipped.Take(3).ToArray(), Is.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
                Assert.That(Encoding.UTF8.GetString(shipped).Replace("\r\n", "").Contains("\n"), Is.False);
            }
        }

        // ---- ids and ranges ----

        [Test]
        public void NewEntityIdIsStable()
        {
            string id = MapValues.NewEntityId("test-mod", "90000001_ab", "crate-1");
            Assert.That(id, Is.EqualTo("d5cbdf947de5797e0ae026504b9ef971"));
            Assert.That(MapValues.NewEntityId("test-mod", "90000001_AB", "crate-1"), Is.EqualTo(id));
            Assert.That(MapValues.NewEntityId("test-mod2", "90000001_ab", "crate-1"), Is.Not.EqualTo(id));
            Assert.That(MapValues.NewEntityId("test-mod", "90000002_ab", "crate-1"), Is.Not.EqualTo(id));
            Assert.That(MapValues.NewEntityId("test-mod", "90000001_ab", "crate-2"), Is.Not.EqualTo(id));
        }

        [Test]
        public void ReservedRanges()
        {
            Assert.That(new[] { IdSpaces.PortalFrom, IdSpaces.PortalTo }, Is.EqualTo(new long[] { 50000, 59999 }));
            Assert.That(new[] { IdSpaces.SpawnPointFrom, IdSpaces.SpawnPointTo }, Is.EqualTo(new long[] { 140000, 149999 }));
            using (var f = new FakeMaps())
            {
                // inside the ranges: no warning about the number
                Plan p = f.Plan(f.Mod("a", Map(Portal("east", 50000, 90000002, 1) + Spawn("wolves", 140000, "<row tag=\"wolf\" population=\"3\" coolTime=\"20\" />"))));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Lines(p.Warnings).Where(w => w.Contains("outside the range")), Is.Empty, Fake.Dump(p));

                // outside: a warning, and the entity is still added
                p = f.Plan(f.Mod("b", Map(Portal("east", 2, 90000002, 1) + Spawn("wolves", 8, "<row tag=\"wolf\" />"))));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string[] w2 = Lines(p.Warnings).Where(w => w.Contains("outside the range")).ToArray();
                Assert.That(w2.Count(w => w.Contains("PortalID 2 ")), Is.EqualTo(3), Fake.Dump(p));
                Assert.That(w2.Count(w => w.Contains("SpawnPointID 8 ")), Is.EqualTo(3), Fake.Dump(p));
                Assert.That(Encoding.UTF8.GetString(Bytes(p, K, FakeMaps.EntryA)), Does.Contain("Portal_east"));

                // a number the map has already: refused
                p = f.Plan(f.Mod("c", Map(Portal("east", 1, 90000002, 1))));
                Assert.That(Lines(p.Errors).Count(e => e.Contains("PortalID 1 is already used in this map by entity " + FakeMaps.Portal1)), Is.EqualTo(3), Fake.Dump(p));
                Assert.That(Bytes(p, K, FakeMaps.EntryA), Is.Null);
                p = f.Plan(f.Mod("d", Map(Spawn("wolves", 7, ""))));
                Assert.That(Lines(p.Errors).Count(e => e.Contains("SpawnPointID 7 is already used")), Is.EqualTo(3), Fake.Dump(p));
            }
        }

        // ---- each kind of op ----

        const string AllOps = AddCrate +
            "<remove id=\"" + FakeMaps.Crate2 + "\" model=\"Crate_A\" position=\"300, 0, 150\" />" +
            "<change id=\"" + FakeMaps.Crate1 + "\" model=\"Crate_A\" position=\"0, 0, 150\">" +
                "<set name=\"Position\" old=\"0, 0, 150\" value=\"0, 0, 300\" />" +
                "<unset name=\"Rotation\" old=\"0, 0, 90\" />" +
                "<set name=\"IsVisible\" value=\"False\" />" +
                "<set name=\"Tags\" index=\"0\" value=\"x\" />" +
            "</change>" +
            "<spawn name=\"wolves\" model=\"SpawnPoint_A\" entity-name=\"Spawn_900\" point=\"140000\">" +
                "<property name=\"Position\" value=\"150, 300, 450\" />" +
                "<property name=\"NpcList\"><set index=\"99000001\" value=\"2\" /></property>" +
                "<row difficulty=\"1\" tag=\"wolf\" coolTime=\"20\" population=\"3\" />" +
                "<row difficulty=\"2\" tag=\"bear\" coolTime=\"40\" population=\"1\" />" +
            "</spawn>" +
            "<portal name=\"east\" model=\"Portal_A\" entity-name=\"Portal_900\" id=\"50000\" to-map=\"90000002\" to-portal=\"50000\">" +
                "<property name=\"Position\" value=\"150, 300, 450\" />" +
            "</portal>";

        const string WayBack = "<portal name=\"west\" model=\"Portal_A\" entity-name=\"Portal_901\" id=\"50000\" to-map=\"90000001\" to-portal=\"50000\"><property name=\"Position\" value=\"0, 300, 450\" /></portal>";

        // what the ops should leave, made a second way: the entity list changed in memory and written as a whole
        static byte[] Expected(bool gms, string modId)
        {
            List<XEntity> ents = FakeMaps.A(gms);
            ents.RemoveAll(e => e.Id == FakeMaps.Crate2);
            if (!gms)
            {
                XEntity c = ents[0];
                c.Props.Clear();
                c.Props.Add(new XProp("IsVisible", "False"));
                c.Props.Add(new XProp("Position", "0, 0, 300"));
                c.Props.Add(new XProp("Tags", new List<XSet> { new XSet("0", "x") }));
            }
            var crate = new XEntity { Id = MapValues.NewEntityId(modId, FakeMaps.MapA, "crate-1"), Model = "Crate_A", Name = "Crate_A_900", Iterations = 1 };
            crate.Props.Add(new XProp("Position", "150, 300, 450"));
            var spawn = new XEntity { Id = MapValues.NewEntityId(modId, FakeMaps.MapA, "wolves"), Model = "SpawnPoint_A", Name = "Spawn_900", Iterations = 1 };
            // as the shipped spawn point of the file lists them; the list it does not have, where the model declares it
            spawn.Props.Add(new XProp("Position", "150, 300, 450"));
            spawn.Props.Add(new XProp("SpawnPointID", "140000"));
            spawn.Props.Add(new XProp("NpcList", new List<XSet> { new XSet("99000001", "2") }));
            var portal = new XEntity { Id = MapValues.NewEntityId(modId, FakeMaps.MapA, "east"), Model = "Portal_A", Name = "Portal_900", Iterations = 1 };
            // in the order of the file's shipped portal
            portal.Props.Add(new XProp("TargetFieldSN", "90000002"));
            portal.Props.Add(new XProp("TargetPortalID", "50000"));
            portal.Props.Add(new XProp("PortalID", "50000"));
            portal.Props.Add(new XProp("Position", "150, 300, 450"));
            ents.Add(crate); ents.Add(spawn); ents.Add(portal);
            MapConvert.SortById(ents);
            return XBlock.Bytes(ents, gms ? XDialect.Spaced : XDialect.Tab);
        }

        static string[] PropNames(Plan p, string archive, string entry, string id)
        {
            string t = Encoding.UTF8.GetString(Bytes(p, archive, entry));
            int a = t.IndexOf("id=\"" + id + "\"", StringComparison.Ordinal);
            Assert.That(a, Is.GreaterThan(0), id);
            t = t.Substring(a, t.IndexOf("</entity>", a, StringComparison.Ordinal) - a);
            return System.Text.RegularExpressions.Regex.Matches(t, "<property\\s+name=\"([^\"]+)\"").Cast<System.Text.RegularExpressions.Match>().Select(x => x.Groups[1].Value).ToArray();
        }

        [Test]
        public void NewEntitiesListPropertiesLikeShippedOnes()
        {
            using (var f = new FakeMaps())
            {
                Plan p = f.Plan(f.Mod("order",
                    Map("<add name=\"c\" model=\"Crate_A\" entity-name=\"c\"><property name=\"Scale\" value=\"2\" /><property name=\"Tags\"><set index=\"0\" value=\"x\" /></property>" +
                        "<property name=\"IsVisible\" value=\"False\" /><property name=\"Rotation\" value=\"0, 0, 0\" /><property name=\"Position\" value=\"150, 300, 450\" /></add>" +
                        Portal("p", 50000, 90000002, 1)) +
                    MapB(Spawn("s", 140000, "<row tag=\"wolf\" />").Replace("</spawn>", "<property name=\"NpcList\"><set index=\"99000001\" value=\"2\" /></property></spawn>"))));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                foreach (string ar in new[] { K, G, S })
                {
                    // the shipped portal of the file: its order, which is not the name order
                    Assert.That(PropNames(p, ar, FakeMaps.EntryA, MapValues.NewEntityId("order", FakeMaps.MapA, "p")),
                        Is.EqualTo(PropNames(p, ar, FakeMaps.EntryA, FakeMaps.Portal1)), ar);
                    Assert.That(PropNames(p, ar, FakeMaps.EntryA, FakeMaps.Portal1), Is.EqualTo(new[] { "TargetFieldSN", "TargetPortalID", "PortalID", "Position" }));
                    // the first shipped crate gives Position, Rotation; Tags stands after Position in the second one;
                    // IsVisible and Scale are in no shipped crate and follow their neighbours of the model's declaration
                    Assert.That(PropNames(p, ar, FakeMaps.EntryA, MapValues.NewEntityId("order", FakeMaps.MapA, "c")),
                        Is.EqualTo(new[] { "Position", "Tags", "Rotation", "IsVisible", "Scale" }), ar);
                    // the other map has no spawn point: the model's declared order
                    Assert.That(PropNames(p, ar, FakeMaps.EntryB, MapValues.NewEntityId("order", FakeMaps.MapB, "s")),
                        Is.EqualTo(new[] { "Position", "SpawnPointID", "NpcList" }), ar);
                }
            }
        }

        [Test]
        public void EveryOpOnEveryTarget()
        {
            using (var f = new FakeMaps())
            {
                Plan p = f.Plan(f.Mod("ops", Map(AllOps) + MapB(WayBack)));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(p.Conflicts, Is.Empty, Fake.Dump(p));

                // the Korean client and the server: every op fits; the bytes are those of a whole rewrite
                Assert.That(Encoding.UTF8.GetString(Bytes(p, K, FakeMaps.EntryA)), Is.EqualTo(Encoding.UTF8.GetString(Expected(false, "ops"))));
                Assert.That(Bytes(p, S, FakeMaps.EntryA), Is.EqualTo(Expected(false, "ops")));

                // the global client: its first crate stands somewhere else, so the change is skipped there with a
                // warning; the other ops are applied, in that file's own writing style
                Assert.That(Encoding.UTF8.GetString(Bytes(p, G, FakeMaps.EntryA)), Is.EqualTo(Encoding.UTF8.GetString(Expected(true, "ops"))));
                string[] w = Lines(p.Warnings);
                Assert.That(w.Count(x => x.Contains("stands at \"0, 0, 300\" here, the mod expects \"0, 0, 150\"") && x.Contains("skipped on gms-client")), Is.EqualTo(1), Fake.Dump(p));
                Assert.That(w.Length, Is.EqualTo(1), Fake.Dump(p));

                // the way back in the other map
                foreach (string ar in new[] { K, G, S })
                    Assert.That(Encoding.UTF8.GetString(Bytes(p, ar, FakeMaps.EntryB)), Does.Contain("id=\"" + MapValues.NewEntityId("ops", FakeMaps.MapB, "west") + "\""));

                // the rows of the spawn point: after the map's last shipped row and before the next map's row, the map
                // and the point filled in, in the order of the mod; the same on every target that ships the table
                foreach (string ar in new[] { FakeMaps.Tags, FakeMaps.TagsK })
                    Assert.That(Fake.Text(p, ar, FakeMaps.TagEntry), Does.Contain("\t" + FakeMaps.TagRow + "\r\n" +
                        "\t<region mapCode=\"90000001\" spawnPointID=\"140000\" difficulty=\"1\" tag=\"wolf\" coolTime=\"20\" population=\"3\" />\r\n" +
                        "\t<region mapCode=\"90000001\" spawnPointID=\"140000\" difficulty=\"2\" tag=\"bear\" coolTime=\"40\" population=\"1\" />\r\n" +
                        "\t" + FakeMaps.TagRowOther + "\r\n</ms2>"), ar);
                Assert.That(Fake.Text(p, FakeMaps.TagsK, FakeMaps.TagEntry), Is.EqualTo(Fake.Text(p, FakeMaps.Tags, FakeMaps.TagEntry)));
                // a target without the table is left alone, with a note
                Assert.That(p.Find("gms-client:Xml.m2d"), Is.Null);
                Assert.That(Lines(p.Notes).Count(n => n.Contains("gms-client:Xml.m2d") && n.Contains("this target has no such table")), Is.EqualTo(1), Fake.Dump(p));

                // a map without rows: its rows go to the end of the table
                Plan pe = f.Plan(f.Mod("end", MapB(Spawn("s9", 140001, "<row tag=\"wolf\" /><row tag=\"bear\" />"))));
                Assert.That(pe.Errors, Is.Empty, Fake.Dump(pe));
                foreach (string ar in new[] { FakeMaps.Tags, FakeMaps.TagsK })
                    Assert.That(Fake.Text(pe, ar, FakeMaps.TagEntry), Does.Contain("\t" + FakeMaps.TagRowOther + "\r\n" +
                        "\t<region mapCode=\"90000002\" spawnPointID=\"140001\" tag=\"wolf\" />\r\n" +
                        "\t<region mapCode=\"90000002\" spawnPointID=\"140001\" tag=\"bear\" />\r\n</ms2>"), ar);

                // the plan ends its map part with what the server needs
                Assert.That(Lines(p.Notes).Count(n => n.Contains("maps changed: 90000001_ab, 90000002_ab") && n.Contains("ingest again") && n.Contains("navigation meshes")), Is.EqualTo(1), Fake.Dump(p));
            }
        }

        [Test]
        public void UntouchedEntitiesKeepTheirBytes()
        {
            using (var f = new FakeMaps())
            {
                foreach (bool gms in new[] { false, true })
                {
                    string before = Encoding.UTF8.GetString(XBlock.Bytes(FakeMaps.A(gms), gms ? XDialect.Spaced : XDialect.Tab));
                    Plan p = f.Plan(f.Mod("one" + gms, Map(AddCrate)));
                    string after = Encoding.UTF8.GetString(Bytes(p, gms ? G : K, FakeMaps.EntryA));
                    // taking the one new entity out again gives the shipped file
                    string id = MapValues.NewEntityId("one" + gms, FakeMaps.MapA, "crate-1");
                    int a = after.IndexOf("id=\"" + id, StringComparison.Ordinal);
                    Assert.That(a, Is.GreaterThan(0));
                    a = after.LastIndexOf("<entity", a, StringComparison.Ordinal);
                    Assert.That(a, Is.GreaterThan(0));
                    a = after.LastIndexOf("\r\n", a, StringComparison.Ordinal) + 2;
                    int b = after.IndexOf("</entity>\r\n", a, StringComparison.Ordinal) + "</entity>\r\n".Length;
                    Assert.That(after.Remove(a, b - a), Is.EqualTo(before));
                }
            }
        }

        [Test]
        public void ExpectedOldValues()
        {
            using (var f = new FakeMaps())
            {
                // another model, another position, another old value, no such entity: a warning each, nothing written
                Plan p = f.Plan(f.Mod("a", Map(
                    "<remove id=\"" + FakeMaps.Crate2 + "\" model=\"Crate_B\" position=\"300, 0, 150\" />" +
                    "<remove id=\"" + FakeMaps.Spawn1 + "\" model=\"SpawnPoint_A\" position=\"1, 1, 1\" />" +
                    "<change id=\"" + FakeMaps.Portal1 + "\" model=\"Portal_A\" position=\"450, 0, 150\"><set name=\"TargetPortalID\" old=\"4\" value=\"5\" /><set name=\"Position\" old=\"450, 0, 150\" value=\"0, 0, 0\" /></change>" +
                    "<remove id=\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" model=\"Crate_A\" position=\"0, 0, 0\" />")));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string[] w = Lines(p.Warnings);
                Assert.That(w.Count(x => x.Contains("is a \"Crate_A\" here, the mod expects \"Crate_B\"")), Is.EqualTo(3), Fake.Dump(p));
                Assert.That(w.Count(x => x.Contains("stands at \"150, 150, 150\" here, the mod expects \"1, 1, 1\"")), Is.EqualTo(3), Fake.Dump(p));
                Assert.That(w.Count(x => x.Contains("property TargetPortalID is \"1\" here, the mod expects \"4\"")), Is.EqualTo(3), Fake.Dump(p));
                Assert.That(w.Count(x => x.Contains("has no entity aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")), Is.EqualTo(3), Fake.Dump(p));
                foreach (string ar in new[] { K, G, S }) Assert.That(Bytes(p, ar, FakeMaps.EntryA), Is.Null, "a change is made as a whole or not at all");

                // a value the map does not store must be expected as not stored
                p = f.Plan(f.Mod("b", Map("<change id=\"" + FakeMaps.Crate2 + "\" model=\"Crate_A\" position=\"300, 0, 150\"><set name=\"Rotation\" old=\"0, 0, 0\" value=\"0, 0, 90\" /></change>")));
                Assert.That(Lines(p.Warnings).Count(x => x.Contains("property Rotation is not stored here")), Is.EqualTo(3), Fake.Dump(p));
            }
        }

        // ---- two mods ----

        [Test]
        public void TwoModsOnOneMap()
        {
            using (var f = new FakeMaps())
            {
                // different entities: both apply, in the order the mods are given; the result does not depend on it
                Mod a = f.Mod("a", Map(AddCrate + "<remove id=\"" + FakeMaps.Crate2 + "\" model=\"Crate_A\" position=\"300, 0, 150\" />"));
                Mod b = f.Mod("b", Map(AddCrate + "<change id=\"" + FakeMaps.Spawn1 + "\" model=\"SpawnPoint_A\" position=\"150, 150, 150\"><set name=\"Position\" old=\"150, 150, 150\" value=\"150, 150, 300\" /></change>"));
                Plan p = f.Plan(a, b);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(p.Conflicts, Is.Empty, Fake.Dump(p));
                string text = Encoding.UTF8.GetString(Bytes(p, K, FakeMaps.EntryA));
                Assert.That(text, Does.Contain(MapValues.NewEntityId("a", FakeMaps.MapA, "crate-1")));
                Assert.That(text, Does.Contain(MapValues.NewEntityId("b", FakeMaps.MapA, "crate-1")));
                Assert.That(text, Does.Not.Contain(FakeMaps.Crate2));
                Assert.That(text, Does.Contain("150, 150, 300"));
                Assert.That(Bytes(f.Plan(b, a), K, FakeMaps.EntryA), Is.EqualTo(Bytes(p, K, FakeMaps.EntryA)));

                // the same entity
                string change = "<change id=\"" + FakeMaps.Crate1 + "\" model=\"Crate_A\" position=\"0, 0, 150\"><set name=\"Rotation\" old=\"0, 0, 90\" value=\"0, 0, 180\" /></change>";
                string remove = "<remove id=\"" + FakeMaps.Crate1 + "\" model=\"Crate_A\" position=\"0, 0, 150\" />";
                p = f.Plan(f.Mod("c", Map(change)), f.Mod("d", Map(remove)));
                Assert.That(Lines(p.Conflicts).Count(x => x.Contains("removes entity " + FakeMaps.Crate1 + " that mod c changes")), Is.EqualTo(2), Fake.Dump(p));
                p = f.Plan(f.Mod("c2", Map(remove)), f.Mod("d2", Map(change)));
                Assert.That(Lines(p.Conflicts).Count(x => x.Contains("changes entity " + FakeMaps.Crate1 + " that mod c2 removes")), Is.EqualTo(2), Fake.Dump(p));
                p = f.Plan(f.Mod("c3", Map(change)), f.Mod("d3", Map(change)));
                Assert.That(Lines(p.Conflicts).Count(x => x.Contains("changes entity " + FakeMaps.Crate1 + " that mod c3 changes")), Is.EqualTo(2), Fake.Dump(p));

                // the same PortalID, the same SpawnPointID
                p = f.Plan(f.Mod("e", Map(Portal("east", 50001, 90000002, 1))), f.Mod("g", Map(Portal("north", 50001, 90000002, 1))));
                Assert.That(Lines(p.Conflicts).Count(x => x.Contains("PortalID 50001 is also brought by mod e")), Is.EqualTo(3), Fake.Dump(p));
                p = f.Plan(f.Mod("h", Map(Spawn("s", 140001, "<row tag=\"wolf\" />"))), f.Mod("i", Map(Spawn("t", 140001, "<row tag=\"bear\" />"))));
                Assert.That(Lines(p.Conflicts).Count(x => x.Contains("SpawnPointID 140001 is also brought by mod h")), Is.EqualTo(3), Fake.Dump(p));

                // one mod, the same op name in two <map> blocks of one map: the same id (inside one block the mod does not load)
                p = f.Plan(f.Mod("j", Map(AddCrate) + Map(AddCrate)));
                Assert.That(Lines(p.Errors).Count(x => x.Contains("which is already in the map")), Is.EqualTo(3), Fake.Dump(p));
            }
        }

        // ---- checks ----

        [Test]
        public void Checks()
        {
            using (var f = new FakeMaps())
            {
                Plan p = f.Plan(f.Mod("a", Map(
                    "<add name=\"n1\" model=\"Crate_Z\" entity-name=\"x1\"><property name=\"Position\" value=\"0, 0, 0\" /></add>" +
                    "<add name=\"n2\" model=\"Crate_A\" entity-name=\"x2\"><property name=\"Colour\" value=\"1\" /><property name=\"Position\" value=\"0, 0\" /><property name=\"IsVisible\" value=\"yes\" /><property name=\"Scale\" value=\"big\" /><property name=\"Tags\" value=\"plain\" /></add>" +
                    "<spawn name=\"n3\" model=\"SpawnPoint_A\" entity-name=\"x3\" point=\"140002\"><property name=\"NpcList\"><set index=\"99000001\" value=\"1\" /><set index=\"99000099\" value=\"1\" /></property></spawn>" +
                    Portal("n4", 50002, 90000077, 1) + Portal("n5", 50003, 90000002, 9))));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string[] w = Lines(p.Warnings);
                Action<string, int> has = (text, n) => Assert.That(w.Count(x => x.Contains(text)), Is.EqualTo(n), text + "\n" + Fake.Dump(p));
                has("the model \"Crate_Z\" is not in the model listing", 3);
                has("the model \"Crate_A\" has no property Colour", 3);
                has("the value \"0, 0\" of the property Position is not a Point3", 3);
                has("the value \"yes\" of the property IsVisible is not a Boolean", 3);
                has("the value \"big\" of the property Scale is not a Float32", 3);
                has("the property Tags of \"Crate_A\" is a list", 3);
                has("NPC 99000099 of the NPC list is not in the NPC data", 3);
                has("NPC 99000001", 0);
                has("has no <row>", 1);
                has("this target's map table has no map 90000077", 3);
                has("portal 50003 leads to portal 9 of map 90000002, and that map has no such portal", 3);
                has("portal 50003 leads to portal 9 of map 90000002, and no portal of that map leads back", 0);

                // a way back is missing when no portal of the other map leads here
                p = f.Plan(f.Mod("b", MapB("<remove id=\"" + FakeMaps.Portal1 + "\" model=\"Portal_A\" position=\"450, 0, 150\" />") + Map(Portal("n6", 50004, 90000002, 1))));
                w = Lines(p.Warnings);
                Assert.That(w.Count(x => x.Contains("that map has no such portal after this plan")), Is.EqualTo(3), Fake.Dump(p));
                Assert.That(w.Count(x => x.Contains("no portal of that map leads back to map 90000001")), Is.EqualTo(3), Fake.Dump(p));

                // a spawn point the server has a row for already
                p = f.Plan(f.Mod("c", Map("<remove id=\"" + FakeMaps.Spawn1 + "\" model=\"SpawnPoint_A\" position=\"150, 150, 150\" />" + Spawn("again", 7, "<row tag=\"wolf\" />"))));
                Assert.That(Lines(p.Errors).Count(x => x.Contains("this target already has a row for spawn point 7")), Is.EqualTo(2), Fake.Dump(p));

                // a map a target does not have
                p = f.Plan(f.Mod("d", "<map xblock=\"90000009_zz\">" + AddCrate + "</map>"));
                Assert.That(Lines(p.Warnings).Count(x => x.Contains("has no such map")), Is.EqualTo(3), Fake.Dump(p));

                // a spawn place that no target could take gets no rows: no table is touched, and the plan says so
                p = f.Plan(f.Mod("d2", "<map xblock=\"90000009_zz\">" + Spawn("lost", 140000, "<row tag=\"wolf\" />") + "</map>"));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Lines(p.Warnings).Count(x => x.Contains("has no such map")), Is.EqualTo(3), Fake.Dump(p));
                Assert.That(Lines(p.Warnings).Count(x => x.Contains("spawn point 140000 of map 90000009_zz was put into no map file, so its spawn rows are not written either")), Is.EqualTo(1), Fake.Dump(p));
                Assert.That(p.Find(FakeMaps.Tags), Is.Null);
                Assert.That(p.Find(FakeMaps.TagsK), Is.Null);
                // an entity refused for its number leaves no rows either
                p = f.Plan(f.Mod("d3", Map(Spawn("dup", 7, "<row tag=\"wolf\" />"))));
                Assert.That(p.Find(FakeMaps.Tags), Is.Null, Fake.Dump(p));
            }
        }

        [Test]
        public void ManifestRefusals()
        {
            using (var f = new FakeMaps())
            {
                Assert.Throws<ModKitException>(() => f.Mod("a", Map("<remove id=\"123\" model=\"Crate_A\" position=\"0, 0, 0\" />")));
                Assert.Throws<ModKitException>(() => f.Mod("b", Map("<remove id=\"" + FakeMaps.Crate1 + "\" />")));
                Assert.Throws<ModKitException>(() => f.Mod("c", Map(Spawn("s", 140000, "<row mapCode=\"1\" tag=\"wolf\" />"))));
                Assert.Throws<ModKitException>(() => f.Mod("d", Map("<portal name=\"p\" model=\"Portal_A\" entity-name=\"p\" id=\"50000\" to-map=\"90000002\" to-portal=\"1\"><property name=\"PortalID\" value=\"3\" /></portal>")));
                Assert.Throws<ModKitException>(() => f.Mod("e", Map("<add model=\"Crate_A\" entity-name=\"x\" />")));

                // a mod written out again reads back as the same ops
                Mod m = f.Mod("g", Map(AllOps) + MapB(WayBack));
                Mod again = f.Mod("g2", "");
                string xml = m.ToXml();
                Assert.That(xml, Does.Contain("<map xblock=\"" + FakeMaps.MapA + "\""));
                File.WriteAllText(Path.Combine(again.Dir, "mod.xml"), xml.Replace("id=\"g\"", "id=\"g2\""), new UTF8Encoding(false));
                Mod back = MS2.ModKit.Mod.Load(again.Dir);
                Assert.That(back.ToXml(), Is.EqualTo(xml.Replace("id=\"g\"", "id=\"g2\"")));
                Assert.That(back.Maps.Sum(x => x.Ops.Count), Is.EqualTo(6));
            }
        }

        // ---- files ----

        static Dictionary<string, byte[]> Files(string dir)
        {
            var d = new Dictionary<string, byte[]>();
            foreach (string p in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) d[p] = File.ReadAllBytes(p);
            return d;
        }

        [Test]
        public void ApplyAndRevert()
        {
            using (var f = new FakeMaps())
            {
                Dictionary<string, byte[]> before = Files(Path.Combine(f.Dir, "orig"));
                var ws = new Workspace(Path.Combine(f.Dir, "ws"));
                Mod m = f.Mod("ops", Map(AllOps) + MapB(WayBack));
                ApplyResult r = ws.Apply(f.Data, new[] { m }, new ApplyOptions { VerifyAll = true });
                Assert.That(r.Applied, Is.True, Fake.Dump(r.Plan));
                Assert.That(r.Outputs.Select(o => o.Archive).ToArray(), Is.EquivalentTo(new[] { K, G, S, FakeMaps.Tags, FakeMaps.TagsK }));

                foreach (string ar in new[] { K, G, S })
                    using (M2dArchive orig = M2dArchive.Open(f.Data.Resolve(ar)))
                    using (M2dArchive made = M2dArchive.Open(ws.PathOf(ar)))
                    {
                        Assert.That(made.Version, Is.EqualTo(orig.Version));
                        Assert.That(made.Count, Is.EqualTo(orig.Count));
                        int same = 0;
                        foreach (M2dEntry e in orig.Entries)
                        {
                            if (e.Name == FakeMaps.EntryA) Assert.That(made.Read(e.Name), Is.EqualTo(Expected(ar == G, "ops")));
                            else if (e.Name == FakeMaps.EntryB) Assert.That(made.Read(e.Name), Is.Not.EqualTo(orig.Read(e)));
                            else { Assert.That(made.ReadRaw(made.Find(e.Name)), Is.EqualTo(orig.ReadRaw(e)), e.Name); same++; }
                        }
                        Assert.That(same, Is.EqualTo(4));       // three models and the other file: stored bytes untouched
                    }

                // a second apply of the same mod writes the same files
                Dictionary<string, byte[]> first = Files(ws.Dir);
                r = ws.Apply(f.Data, new[] { m }, new ApplyOptions());
                Assert.That(r.Applied, Is.True, Fake.Dump(r.Plan));
                foreach (string ar in new[] { K, G, S, FakeMaps.Tags, FakeMaps.TagsK }) Assert.That(File.ReadAllBytes(ws.PathOf(ar)), Is.EqualTo(first[ws.PathOf(ar)]), ar);

                // revert: the copies go away, the shipped files were never written to
                ws.Revert(f.Data, null, null);
                Assert.That(Directory.GetFiles(ws.Dir, "*", SearchOption.AllDirectories), Is.Empty);
                Dictionary<string, byte[]> after = Files(Path.Combine(f.Dir, "orig"));
                Assert.That(after.Keys, Is.EquivalentTo(before.Keys));
                foreach (string k in before.Keys) Assert.That(after[k], Is.EqualTo(before[k]), k);
            }
        }

        [Test]
        public void ServerFolderThatIsALinkIsWrittenOnce()
        {
            using (var f = new FakeMaps(true))
            {
                Plan p = f.Plan(f.Mod("a", Map(AddCrate)));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Bytes(p, K, FakeMaps.EntryA), Is.Not.Null);
                Assert.That(Bytes(p, G, FakeMaps.EntryA), Is.Not.Null);
                Assert.That(p.Find(S), Is.Null);
                Assert.That(Lines(p.Notes).Count(n => n.Contains("server reads its maps from the same file as kms-client") && n.Contains("written once")), Is.EqualTo(1), Fake.Dump(p));
            }
        }
    }
}
