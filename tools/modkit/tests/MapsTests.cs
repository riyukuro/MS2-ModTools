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
        // MapLoose: a map file no row of the map table names. MapTwo: one of the two map files of map 90000005.
        public const string MapLoose = "90000004_ab", MapTwo = "90000005_ab";
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
            list.Add(E("xblock/" + MapLoose + ".xblock", XBlock.Bytes(B(), XDialect.Tab)));
            list.Add(E("xblock/" + MapTwo + ".xblock", XBlock.Bytes(B(), XDialect.Tab)));
            list.Add(E("xblock/90000005_cd.xblock", XBlock.Bytes(B(), XDialect.Tab)));
            list.Add(E("texture/made_up.bin", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
            M2dWriter.Create(Path.Combine(Path.Combine(dir, "Resource"), "Exported.m2d"), gms ? PackVersion.MS2F : PackVersion.NS2F, M2dFlag.AesZlib, list);
        }

        // a row of the map table: one environment that names the map file (in capitals, as shipped tables do)
        public static string Row(string open, string close, string file)
        {
            return "\t<" + open + ">\n\t\t<environment>\n\t\t\t<xblock name=\"" + file + "\" />\n\t\t</environment>\n\t</" + close + ">\n";
        }

        // a map with one map file per feature
        public static string TwoFiles(string open, string close, string feature = "MadeUp")
        {
            return "\t<" + open + ">\n\t\t<environment>\n\t\t\t<xblock name=\"90000005_AB\" />\n\t\t</environment>\n"
                + "\t\t<environment feature=\"" + feature + "\">\n\t\t\t<xblock name=\"90000005_CD\" />\n\t\t</environment>\n\t</" + close + ">\n";
        }

        public const string TagRow = "<region mapCode=\"90000001\" spawnPointID=\"7\" difficulty=\"1\" tag=\"wolf\" coolTime=\"20\" population=\"3\" />";
        // a row of another map after it: new rows of the first map go between the two
        public const string TagRowOther = "<region mapCode=\"90000003\" spawnPointID=\"1\" difficulty=\"1\" tag=\"crow\" coolTime=\"20\" population=\"2\" />";
        public const string TagTable = Head + "<ms2>\n\t" + TagRow + "\n\t" + TagRowOther + "\n</ms2>\n";

        // made-up feature tables: server type Live is at level 10 for KR and 5 for NA; feature MadeUp switches on at 8 / 7
        public const string FeatureSetting = Head + "<ms2>\n\t<setting type=\"Dev\" KR=\"99\" NA=\"99\" />\n\t<setting type=\"Live\" KR=\"10\" NA=\"5\" />\n</ms2>\n";
        public const string Feature = Head + "<ms2>\n\t<feature name=\"MadeUp\" KR=\"8\" NA=\"7\" />\n\t<feature name=\"Other\" KR=\"8\" NA=\"0\" />\n</ms2>\n";

        public FakeMaps(bool serverLinksToKms = false, bool features = false, string secondFileFeature = "MadeUp")
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
            var kms = new List<KeyValuePair<string, byte[]>>
            {
                E("table/fielddata.xml", Head + "<ms2>\n" + Row("fieldData id=\"90000001\"", "fieldData", "90000001_AB") + Row("fieldData id=\"90000002\"", "fieldData", "90000002_AB") + TwoFiles("fieldData id=\"90000005\"", "fieldData", secondFileFeature) + "</ms2>\n"),
                E("npcdata/990.xml", Head + "<ms2>\n\t<npc id=\"99000001\" />\n</ms2>\n"),
                E(TagEntry, TagTable),      // the Korean client ships the table too; the made-up global client does not
            };
            if (features) { kms.Add(E(FeatureSet.SettingTable, FeatureSetting)); kms.Add(E(FeatureSet.FeatureTable, Feature)); }
            M2dWriter.Create(Path.Combine(KmsDir, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, kms);
            var perId = new List<KeyValuePair<string, byte[]>>
            {
                E("map/90000001.xml", Head + Row("ms2", "ms2", "90000001_AB")),
                E("map/90000002.xml", Head + Row("ms2", "ms2", "90000002_AB")),
                E("map/90000005.xml", Head + TwoFiles("ms2", "ms2", secondFileFeature)),
                E("npc/9/90/99000001.xml", Head + "<ms2 />\n"),
            };
            if (features) { perId.Add(E(FeatureSet.SettingTable, FeatureSetting)); perId.Add(E(FeatureSet.FeatureTable, Feature)); }
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

                // a layer file that lists itself is refused instead of being read without end
                var loop = new BLayer { Name = "loop", Path = "loop.layer" };
                loop.Layers.Add(new BLayer { Name = "loop", Path = "loop.layer" });
                File.WriteAllText(Path.Combine(dir, "loop.layer"), BlockDoc.LayerText(loop));
                BlockDoc withLoop = BlockDoc.Read(file);
                withLoop.Layers.Add(new BLayer { Name = "loop", Path = "loop.layer" });
                File.WriteAllText(file, withLoop.Text());
                Assert.Throws<ModKitException>(() => BlockDoc.Read(file));
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

        // ---- is the changed map file one the game loads? ----

        static string MapOf(string file, string attrs, string body) { return "<map xblock=\"" + file + "\"" + attrs + ">" + body + "</map>"; }

        [Test]
        public void MapFileTheMapTableNamesGivesNoWarning()
        {
            using (var f = new FakeMaps())
            {
                // the table writes the name in capitals, the archive in small letters: the same file
                Plan p = f.Plan(f.Mod("a", Map(AddCrate)));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Lines(p.Warnings).Where(w => w.Contains("map table")), Is.Empty, Fake.Dump(p));
                Assert.That(Lines(p.Notes).Where(n => n.Contains("more than one map file")), Is.Empty, Fake.Dump(p));
            }
        }

        [Test]
        public void MapFileNoMapUsesGivesAWarningAndIsStillChanged()
        {
            using (var f = new FakeMaps())
            {
                // no row has the file, and no row has the map id its name starts with
                Plan p = f.Plan(f.Mod("a", MapOf(FakeMaps.MapLoose, "", AddCrate)));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string[] w = Lines(p.Warnings).Where(x => x.Contains("no map of this target's map table uses this map file")).ToArray();
                Assert.That(w.Length, Is.EqualTo(3), Fake.Dump(p));       // one per target: bundle table and one file per id
                Assert.That(w.All(x => x.Contains("xblock/" + FakeMaps.MapLoose + ".xblock")), Is.True, Fake.Dump(p));
                Assert.That(w.Any(x => x.Contains("the map table names")), Is.False, Fake.Dump(p));
                // a warning, not a refusal
                Assert.That(Encoding.UTF8.GetString(Bytes(p, K, "xblock/" + FakeMaps.MapLoose + ".xblock")), Does.Contain("Crate_A_900"));

                // the mod says which map it means: the warning names the file the table has for that map
                p = f.Plan(f.Mod("b", MapOf(FakeMaps.MapLoose, " id=\"90000001\"", AddCrate)));
                w = Lines(p.Warnings).Where(x => x.Contains("no map of this target's map table uses this map file")).ToArray();
                Assert.That(w.Length, Is.EqualTo(3), Fake.Dump(p));
                Assert.That(w.All(x => x.Contains("for map 90000001 the map table names 90000001_AB (no feature)")), Is.True, Fake.Dump(p));
            }
        }

        [Test]
        public void MapFileWhoseRowTheModBringsGivesNoWarningThere()
        {
            using (var f = new FakeMaps())
            {
                string row = "<edit archive=\"client:Xml.m2d\" entry=\"table/fielddata.xml\"><add><xml><fieldData id=\"90000004\"><environment><xblock name=\"90000004_AB\" /></environment></fieldData></xml></add></edit>";
                Plan p = f.Plan(f.Mod("a", row + MapOf(FakeMaps.MapLoose, "", AddCrate)));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string[] w = Lines(p.Warnings).Where(x => x.Contains("no map of this target's map table uses this map file")).ToArray();
                Assert.That(w.Length, Is.EqualTo(2), Fake.Dump(p));
                Assert.That(w.Count(x => x.Contains(" " + G + " ")) + w.Count(x => x.Contains(" " + S + " ")), Is.EqualTo(2), Fake.Dump(p));
            }
        }

        [Test]
        public void OneOfSeveralMapFilesOfAMapGivesANote()
        {
            using (var f = new FakeMaps())
            {
                Plan p = f.Plan(f.Mod("a", MapOf(FakeMaps.MapTwo, "", AddCrate)));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Lines(p.Warnings).Where(w => w.Contains("map table")), Is.Empty, Fake.Dump(p));
                string[] n = Lines(p.Notes).Where(x => x.Contains("map 90000005 has more than one map file in the map table")).ToArray();
                Assert.That(n.Length, Is.EqualTo(3), Fake.Dump(p));
                Assert.That(n.All(x => x.Contains("90000005_AB (no feature), 90000005_CD (feature MadeUp)")), Is.True, Fake.Dump(p));
            }
        }

        [Test]
        public void WithKnownFeaturesTheFileTheGameLoadsGivesNoMessageAndAnotherOneAWarning()
        {
            using (var f = new FakeMaps(features: true))
            {
                // KR/Live is at level 10, MadeUp is on there: the Korean client loads 90000005_CD. NA/Live is at 5, MadeUp
                // is off: the global client and the server load 90000005_AB, the file the mod changes.
                Plan p = f.Plan(f.Mod("a", MapOf(FakeMaps.MapTwo, "", AddCrate)));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Lines(p.Notes).Where(x => x.Contains("more than one map file")), Is.Empty, Fake.Dump(p));
                string[] w = Lines(p.Warnings).Where(x => x.Contains("map table")).ToArray();
                Assert.That(w.Length, Is.EqualTo(1), Fake.Dump(p));
                Assert.That(w[0], Does.Contain("with this target's features (locale KR, server type Live, level 10) the game loads 90000005_CD for map 90000005, not this map file"));
                Assert.That(w[0], Does.Contain(" " + K + " "));
                // still changed
                Assert.That(Encoding.UTF8.GetString(Bytes(p, K, "xblock/" + FakeMaps.MapTwo + ".xblock")), Does.Contain("Crate_A_900"));

                // the other file: the warning moves to the two targets that load 90000005_AB
                p = f.Plan(f.Mod("b", MapOf("90000005_cd", "", AddCrate)));
                w = Lines(p.Warnings).Where(x => x.Contains("map table")).ToArray();
                Assert.That(w.Length, Is.EqualTo(2), Fake.Dump(p));
                Assert.That(w.Count(x => x.Contains("(locale NA, server type Live, level 5) the game loads 90000005_AB for map 90000005") && x.Contains(" " + G + " ")), Is.EqualTo(1), Fake.Dump(p));
                Assert.That(w.Count(x => x.Contains("(locale NA, server type Live, level 5) the server loads 90000005_AB for map 90000005") && x.Contains(" " + S + " ")), Is.EqualTo(1), Fake.Dump(p));
                Assert.That(w.Any(x => x.Contains(" " + K + " ")), Is.False, Fake.Dump(p));
            }
        }

        [Test]
        public void TheFeaturesSettingOfATargetChangesWhichFileCounts()
        {
            using (var f = new FakeMaps(features: true))
            {
                // the server runs as type Dev (level 99): MadeUp is on, it loads 90000005_CD
                f.Data.SetFeatures(Profile.Server, "/Dev");
                // a locale the tables have no level for: the features of the global client are not known
                f.Data.SetFeatures(Profile.GmsClient, "ZZ");
                Plan p = f.Plan(f.Mod("a", MapOf(FakeMaps.MapTwo, "", AddCrate)));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string[] w = Lines(p.Warnings).Where(x => x.Contains("map table")).ToArray();
                Assert.That(w.Length, Is.EqualTo(2), Fake.Dump(p));
                Assert.That(w.Count(x => x.Contains("(locale NA, server type Dev, level 99) the server loads 90000005_CD") && x.Contains(" " + S + " ")), Is.EqualTo(1), Fake.Dump(p));
                string[] n = Lines(p.Notes).Where(x => x.Contains("map 90000005 has more than one map file in the map table")).ToArray();
                Assert.That(n.Length, Is.EqualTo(1), Fake.Dump(p));
                Assert.That(n[0], Does.Contain(" " + G + " "));
                Assert.That(() => f.Data.SetFeatures(Profile.Server, "NA/Live/x"), Throws.InstanceOf<ModKitException>());
            }
        }

        [Test]
        public void FeatureSetPicksRowsAsTheGameDoes()
        {
            byte[] setting = Encoding.UTF8.GetBytes(FakeMaps.FeatureSetting), feature = Encoding.UTF8.GetBytes(FakeMaps.Feature);
            FeatureSet kr = FeatureSet.Read(setting, feature, "KR", "live");       // the type compares without case
            Assert.That(kr.Level, Is.EqualTo(10));
            Assert.That(kr.IsOn(""), Is.True);
            Assert.That(kr.IsOn("MadeUp"), Is.True);
            Assert.That(kr.IsOn("NotInTheTable"), Is.False);
            FeatureSet na = FeatureSet.Read(setting, feature, "NA", "Live");
            Assert.That(na.IsOn("MadeUp"), Is.False);       // level 7 over 5
            Assert.That(na.IsOn("Other"), Is.False);        // level 0 = not in the table for this locale
            Assert.That(FeatureSet.Read(setting, feature, "NA", "Dev").IsOn("MadeUp"), Is.True);
            // unknown: no table, no row of the type, no level for the locale
            Assert.That(FeatureSet.Read(null, feature, "KR", "Live"), Is.Null);
            Assert.That(FeatureSet.Read(setting, feature, "KR", "Stage"), Is.Null);
            Assert.That(FeatureSet.Read(setting, feature, "ZZ", "Live"), Is.Null);

            // a row of another locale is out; a row of this locale or of none stays
            Assert.That(kr.Allows("", "NA"), Is.False);
            Assert.That(kr.Allows("", "KR"), Is.True);
            Assert.That(kr.Allows("MadeUp", ""), Is.True);
            // the higher feature level wins, from either side
            Assert.That(kr.SecondWins("", "", "MadeUp", ""), Is.True);
            Assert.That(kr.SecondWins("MadeUp", "", "", ""), Is.False);
            // equal levels: the name that sorts later
            Assert.That(kr.SecondWins("MadeUp", "", "Other", ""), Is.True);
            Assert.That(kr.SecondWins("Other", "", "MadeUp", ""), Is.False);
            // the same feature: the row of this locale wins over the row without one; the same twice: the first stays
            Assert.That(kr.SecondWins("MadeUp", "", "MadeUp", "KR"), Is.True);
            Assert.That(kr.SecondWins("MadeUp", "KR", "MadeUp", ""), Is.False);
            Assert.That(kr.SecondWins("MadeUp", "", "MadeUp", ""), Is.False);
        }

        [Test]
        public void TheServerPicksRowsByItsOwnRule()
        {
            byte[] setting = Encoding.UTF8.GetBytes(FakeMaps.FeatureSetting), feature = Encoding.UTF8.GetBytes(FakeMaps.Feature);
            FeatureSet game = FeatureSet.Read(setting, feature, "NA", "Live"), server = FeatureSet.Read(setting, feature, "NA", "Live", true);
            // a feature at level 0: off for the game, on for the server
            Assert.That(game.IsOn("Other"), Is.False);
            Assert.That(server.IsOn("Other"), Is.True);
            Assert.That(server.IsOn("MadeUp"), Is.False);       // level 7 over 5, as for the game
            Assert.That(server.IsOn("NotInTheTable"), Is.False);
            // the server type and the feature name compare with letter case, the row's locale without
            Assert.That(FeatureSet.Read(setting, feature, "NA", "live", true), Is.Null);
            Assert.That(server.IsOn("other"), Is.False);
            Assert.That(server.Allows("", "na"), Is.True);
            Assert.That(game.Allows("", "na"), Is.False);
            // a locale the server does not know; a level that is not written counts as 0
            Assert.That(FeatureSet.Read(setting, feature, "ZZ", "Live", true), Is.Null);
            Assert.That(FeatureSet.Read(setting, feature, "JP", "Live", true).Level, Is.EqualTo(0));

            FeatureSet gameDev = FeatureSet.Read(setting, feature, "KR", "Dev"), serverDev = FeatureSet.Read(setting, feature, "KR", "Dev", true);
            // a row of the locale against a row without one with a feature: the game takes the feature, the server the locale
            Assert.That(gameDev.SecondWins("", "KR", "MadeUp", ""), Is.True);
            Assert.That(serverDev.SecondWins("", "KR", "MadeUp", ""), Is.False);
            Assert.That(serverDev.SecondWins("MadeUp", "", "", "KR"), Is.True);
            // equal levels: the game takes the later name, the server the first row
            Assert.That(gameDev.SecondWins("MadeUp", "", "Other", ""), Is.True);
            Assert.That(serverDev.SecondWins("MadeUp", "", "Other", ""), Is.False);
            // two rows without a feature: the game keeps the first, the server the last
            Assert.That(gameDev.SecondWins("", "", "", ""), Is.False);
            Assert.That(serverDev.SecondWins("", "", "", ""), Is.True);
            // a feature goes before no feature on both
            Assert.That(serverDev.SecondWins("", "", "MadeUp", ""), Is.True);
            Assert.That(serverDev.SecondWins("MadeUp", "", "", ""), Is.False);
        }

        [Test]
        public void AFeatureAtLevelZeroIsOnForTheServerOnly()
        {
            // the second file of map 90000005 hangs on feature Other, which is at level 0 for NA: the global client
            // loads 90000005_AB, the server 90000005_CD, from the same tables with the same locale and server type
            using (var f = new FakeMaps(features: true, secondFileFeature: "Other"))
            {
                Plan p = f.Plan(f.Mod("a", MapOf(FakeMaps.MapTwo, "", AddCrate)));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string[] w = Lines(p.Warnings).Where(x => x.Contains("map table")).ToArray();
                Assert.That(w.Length, Is.EqualTo(2), Fake.Dump(p));
                Assert.That(w.Count(x => x.Contains("the server loads 90000005_CD for map 90000005") && x.Contains(" " + S + " ")), Is.EqualTo(1), Fake.Dump(p));
                Assert.That(w.Count(x => x.Contains("(locale KR, server type Live, level 10) the game loads 90000005_CD") && x.Contains(" " + K + " ")), Is.EqualTo(1), Fake.Dump(p));
            }
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
        public void WholeFileAgainstMapEdit()
        {
            using (var f = new FakeMaps())
            {
                // one mod edits a map, the other brings the whole map file
                M2dArchive arch = f.Data.Open(K);
                string shipped = Encoding.UTF8.GetString(arch.Read(arch.Find(FakeMaps.EntryA)));
                Assert.That(shipped, Does.Contain("0, 0, 90"));
                byte[] whole = Encoding.UTF8.GetBytes(shipped.Replace("0, 0, 90", "0, 0, 45"));
                Func<string, Mod> file = id =>
                {
                    string d = Path.Combine(f.Dir, "mods", id, "data");
                    Directory.CreateDirectory(d);
                    File.WriteAllBytes(Path.Combine(d, "a.xblock"), whole);
                    return f.Mod(id, "<entry archive=\"" + K + "\" name=\"" + FakeMaps.EntryA + "\" file=\"data/a.xblock\"/>");
                };

                // the whole file has the higher priority: it is written as it is, the edit is left out there
                Mod edit = f.Mod("edit", Map(AddCrate)), top = file("whole");
                Plan p = f.Plan(edit, top);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                var c = p.Conflicts.Where(x => x.Archive == K).ToList();
                Assert.That(c.Count, Is.EqualTo(1), Fake.Dump(p));
                Assert.That(c[0].ModId, Is.EqualTo("edit"));
                Assert.That(c[0].WinnerId, Is.EqualTo("whole"));
                Assert.That(c[0].Text, Does.Contain("changes a map that mod whole replaces as a whole file; mod whole wins"));
                Assert.That(c[0].ConflictLine(), Does.StartWith("conflict, whole wins: [edit] " + K + " " + FakeMaps.EntryA + ": changes a map").And.Not.Contain("; mod whole wins"));
                Assert.That(Bytes(p, K, FakeMaps.EntryA), Is.EqualTo(whole));
                // the other targets have no whole file: the edit is made there
                Assert.That(Encoding.UTF8.GetString(Bytes(p, G, FakeMaps.EntryA)), Does.Contain(MapValues.NewEntityId("edit", FakeMaps.MapA, "crate-1")));

                // the edit has the higher priority: the shipped file comes back and gets the edit
                Mod low = file("whole2"), high = f.Mod("edit2", Map(AddCrate));
                p = f.Plan(low, high);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                c = p.Conflicts.Where(x => x.Archive == K).ToList();
                Assert.That(c.Count, Is.EqualTo(1), Fake.Dump(p));
                Assert.That(c[0].ModId, Is.EqualTo("edit2"));
                Assert.That(c[0].WinnerId, Is.EqualTo("edit2"));
                string text = Encoding.UTF8.GetString(Bytes(p, K, FakeMaps.EntryA));
                Assert.That(text, Does.Contain(MapValues.NewEntityId("edit2", FakeMaps.MapA, "crate-1")).And.Contain("0, 0, 90").And.Not.Contain("0, 0, 45"));
            }
        }

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

                // the same entity: the mod given later has the higher priority, it is planned first and its change stays
                string change = "<change id=\"" + FakeMaps.Crate1 + "\" model=\"Crate_A\" position=\"0, 0, 150\"><set name=\"Rotation\" old=\"0, 0, 90\" value=\"0, 0, 180\" /></change>";
                string remove = "<remove id=\"" + FakeMaps.Crate1 + "\" model=\"Crate_A\" position=\"0, 0, 150\" />";
                p = f.Plan(f.Mod("c", Map(change)), f.Mod("d", Map(remove)));
                Assert.That(Lines(p.Conflicts).Count(x => x.Contains("changes entity " + FakeMaps.Crate1 + " that mod d removes; mod d wins")), Is.EqualTo(2), Fake.Dump(p));
                Assert.That(p.Conflicts.All(x => x.ModId == "c" && x.WinnerId == "d"), Fake.Dump(p));
                Assert.That(Encoding.UTF8.GetString(Bytes(p, K, FakeMaps.EntryA)), Does.Not.Contain(FakeMaps.Crate1));
                p = f.Plan(f.Mod("c2", Map(remove)), f.Mod("d2", Map(change)));
                Assert.That(Lines(p.Conflicts).Count(x => x.Contains("removes entity " + FakeMaps.Crate1 + " that mod d2 changes; mod d2 wins")), Is.EqualTo(2), Fake.Dump(p));
                Assert.That(Encoding.UTF8.GetString(Bytes(p, K, FakeMaps.EntryA)), Does.Contain(FakeMaps.Crate1).And.Contain("0, 0, 180"));
                p = f.Plan(f.Mod("c3", Map(change)), f.Mod("d3", Map(change)));
                Assert.That(Lines(p.Conflicts).Count(x => x.Contains("changes entity " + FakeMaps.Crate1 + " that mod d3 changes; mod d3 wins")), Is.EqualTo(2), Fake.Dump(p));

                // the same PortalID, the same SpawnPointID
                p = f.Plan(f.Mod("e", Map(Portal("east", 50001, 90000002, 1))), f.Mod("g", Map(Portal("north", 50001, 90000002, 1))));
                Assert.That(Lines(p.Conflicts).Count(x => x.Contains("PortalID 50001 is also brought by mod g") && x.Contains("; mod g wins")), Is.EqualTo(3), Fake.Dump(p));
                p = f.Plan(f.Mod("h", Map(Spawn("s", 140001, "<row tag=\"wolf\" />"))), f.Mod("i", Map(Spawn("t", 140001, "<row tag=\"bear\" />"))));
                Assert.That(Lines(p.Conflicts).Count(x => x.Contains("SpawnPointID 140001 is also brought by mod i") && x.Contains("; mod i wins")), Is.EqualTo(3), Fake.Dump(p));

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
                // numbers the client reads as 32 bits: above 2^31-1 is refused, not only warned about
                const string big = "2147483648";
                Assert.Throws<ModKitException>(() => f.Mod("n1", Map(Spawn("s", 140000, "<row tag=\"wolf\" />").Replace("point=\"140000\"", "point=\"" + big + "\""))));
                string okPortal = Portal("p", 50000, 90000002, 1);
                Assert.Throws<ModKitException>(() => f.Mod("n2", Map(okPortal.Replace(" id=\"50000\"", " id=\"" + big + "\""))));
                Assert.Throws<ModKitException>(() => f.Mod("n3", Map(okPortal.Replace("to-map=\"90000002\"", "to-map=\"" + big + "\""))));
                Assert.Throws<ModKitException>(() => f.Mod("n4", Map(okPortal.Replace("to-portal=\"1\"", "to-portal=\"" + big + "\""))));
                Assert.Throws<ModKitException>(() => f.Mod("n5", "<map xblock=\"zz\" id=\"" + big + "\">" + AddCrate + "</map>"));
                Assert.That(f.Mod("n6", Map(okPortal.Replace("to-portal=\"1\"", "to-portal=\"2147483647\""))).Maps[0].Ops[0].ToPortal, Is.EqualTo(int.MaxValue));
                // iterations: counts from 1 and fits 32 bits (4294967297 would wrap to 1)
                foreach (string it in new[] { "0", "4294967297", "2147483648", "-1" })
                    Assert.Throws<ModKitException>(() => f.Mod("it" + it.Length, Map(AddCrate.Replace("entity-name=", "iterations=\"" + it + "\" entity-name="))), it);
                Assert.That(f.Mod("it-ok", Map(AddCrate.Replace("entity-name=", "iterations=\"2147483647\" entity-name="))).Maps[0].Ops[0].Iterations, Is.EqualTo(int.MaxValue));

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
                        Assert.That(same, Is.EqualTo(7));       // three models, three maps no mod names, the other file: stored bytes untouched
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

        // ---- shipped map + edited map -> the ops of a mod ----

        const string New1 = "40000000000000000000000000000004", New2 = "50000000000000000000000000000005", New3 = "00000000000000000000000000000009";

        // every kind of edit on map A; the new entities stand at their place in id order, as the files do
        static List<XEntity> Edited(bool gms)
        {
            List<XEntity> l = FakeMaps.A(gms);
            XEntity crate1 = l[0], spawn1 = l[1], portal1 = l[2], crate2 = l[3];
            crate1.Find("Position").Items[0] = new XSet(null, "0, 0, 450");                 // a value changed
            crate1.Props.RemoveAll(x => x.Name == "Rotation");                               // a stored value removed
            crate1.Props.Insert(0, new XProp("IsVisible", "False"));                        // a new property, at its place by name
            crate2.Find("Tags").Items[1] = new XSet("1", "c");                              // a list item changed
            crate2.Find("Tags").Items.Add(new XSet("2", "d"));                              // a list item added
            l.Remove(spawn1);                                                               // an entity removed
            portal1.Find("TargetPortalID").Items[0] = new XSet(null, "4");
            var crate = new XEntity { Id = New1, Model = "Crate_A", Name = "Crate_A_900", Iterations = 2 };
            crate.Props.Add(new XProp("Scale", "2"));                                       // not the name order
            crate.Props.Add(new XProp("Position", "150, 300, 450"));
            crate.Props.Add(new XProp("Tags", new List<XSet> { new XSet("0", "x"), new XSet("1", "y") }));
            var portal = new XEntity { Id = New2, Model = "Portal_A", Name = "Portal_900", Iterations = 1 };
            portal.Props.Add(new XProp("Position", "300, 300, 150"));                       // not the shipped portals' order
            portal.Props.Add(new XProp("TargetPortalID", "1"));
            portal.Props.Add(new XProp("PortalID", "50000"));
            portal.Props.Add(new XProp("TargetFieldSN", "90000002"));
            var spawn = new XEntity { Id = New3, Model = "SpawnPoint_A", Name = "Spawn_900", Iterations = 1 };
            spawn.Props.Add(new XProp("SpawnPointID", "140000"));
            spawn.Props.Add(new XProp("Position", "150, 150, 450"));
            l.Add(crate); l.Add(portal); l.Add(spawn);
            MapConvert.SortById(l);
            return l;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DifferenceOfTwoMapsAsAModGivesTheEditedMapBack(bool gms)
        {
            XDialect d = gms ? XDialect.Spaced : XDialect.Tab;
            byte[] shipped = XBlock.Bytes(FakeMaps.A(gms), d), edited = XBlock.Bytes(Edited(gms), d);
            var problems = new List<string>();
            MapOp ops = MapDiff.Build(FakeMaps.MapA, shipped, edited, problems);
            Assert.That(problems, Is.Empty);
            Assert.That(ops.Ops.Select(o => o.Kind + " " + (o.Id ?? o.NewId)).ToArray(), Is.EqualTo(new[]
            {
                "Change " + FakeMaps.Crate1, "Remove " + FakeMaps.Spawn1, "Change " + FakeMaps.Portal1, "Change " + FakeMaps.Crate2,
                "Spawn " + New3, "Add " + New1, "Portal " + New2,
            }));
            MapEntityOp c1 = ops.Ops[0];
            Assert.That(c1.Position, Is.EqualTo(gms ? "0, 0, 300" : "0, 0, 150"));
            Assert.That(c1.Sets.Select(x => x.Name + "|" + x.Index + "|" + x.Old + "|" + x.Value).ToArray(), Is.EqualTo(new[]
            {
                "Position||" + c1.Position + "|0, 0, 450", "Rotation||0, 0, 90|", "IsVisible|||False",
            }));
            Assert.That(ops.Ops[3].Sets.Select(x => x.Name + "|" + x.Index + "|" + x.Old + "|" + x.Value).ToArray(), Is.EqualTo(new[] { "Tags|1|b|c", "Tags|2||d" }));

            var mod = new Mod { Id = "diffed", Name = "diffed", Version = "1" };
            mod.Maps.Add(ops);
            string text = mod.ToXml();
            Mod loaded = Mod.Parse(text, "generated");
            Assert.That(loaded.ToXml(), Is.EqualTo(text));
            Assert.That(MapDiff.Check(loaded, FakeMaps.MapA, shipped, edited), Is.Null);

            // through the planner on the three targets: the target that ships this file gets the edited file, byte for byte
            using (var f = new FakeMaps())
            {
                Plan p = f.Plan(loaded);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(p.Conflicts, Is.Empty, Fake.Dump(p));
                Assert.That(Bytes(p, gms ? G : K, FakeMaps.EntryA), Is.EqualTo(edited));
                if (!gms) Assert.That(Bytes(p, S, FakeMaps.EntryA), Is.EqualTo(edited));
                // the other target ships the first crate somewhere else: that one change is skipped there and said
                Assert.That(Lines(p.Warnings).Count(w => w.Contains(FakeMaps.Crate1) && w.Contains("is skipped on")), Is.EqualTo(gms ? 2 : 1), Fake.Dump(p));
                Assert.That(Lines(p.Warnings).Count(w => w.Contains("spawn point 140000") && w.Contains("has no <row>")), Is.EqualTo(1), Fake.Dump(p));
            }
        }

        // the map editor counts an entity's iterations up with each edit: a change carries the new number
        [Test]
        public void IterationsOfAnEditedEntityGoIntoTheChange()
        {
            byte[] shipped = XBlock.Bytes(FakeMaps.A(false), XDialect.Tab);
            List<XEntity> l = FakeMaps.A(false);
            l[0].Iterations += 2;                                  // only the number
            l[1].Iterations += 1;                                  // the number and a value
            l[1].Props[0].Items[0] = new XSet(l[1].Props[0].Items[0].Index, l[1].Props[0].Items[0].Value + "1");
            byte[] edited = XBlock.Bytes(l, XDialect.Tab);
            var problems = new List<string>();
            MapOp ops = MapDiff.Build(FakeMaps.MapA, shipped, edited, problems);
            Assert.That(problems, Is.Empty);
            Assert.That(ops.Ops.Select(o => o.Kind + " " + o.SetIterations + " " + o.Sets.Count).ToArray(),
                Is.EqualTo(new[] { "Change " + l[0].Iterations + " 0", "Change " + l[1].Iterations + " 1" }));
            var mod = new Mod { Id = "diffed", Name = "diffed", Version = "1" };
            mod.Maps.Add(ops);
            string text = mod.ToXml();
            Assert.That(text, Does.Contain(" iterations=\"" + l[0].Iterations + "\" />"));
            Mod loaded = Mod.Parse(text, "generated");
            Assert.That(loaded.ToXml(), Is.EqualTo(text));
            Assert.That(MapDiff.Check(loaded, FakeMaps.MapA, shipped, edited), Is.Null);
            Assert.Throws<ModKitException>(() => Mod.Parse(text.Replace(" iterations=\"" + l[0].Iterations + "\" />", " iterations=\"0\" />"), "bad"));
            Assert.Throws<ModKitException>(() => Mod.Parse(text.Replace(" iterations=\"" + l[0].Iterations + "\" />", " />"), "empty"), "a change that changes nothing");

            // a number below 1 cannot be said
            l[0].Iterations = 0;
            MapDiff.Build(FakeMaps.MapA, shipped, XBlock.Bytes(l, XDialect.Tab), problems);
            Assert.That(problems.Count, Is.EqualTo(1));
            Assert.That(problems[0], Does.Contain("iterations=\"0\""));
        }

        [Test]
        public void DifferenceThatAModCannotSayIsReported()
        {
            byte[] shipped = XBlock.Bytes(FakeMaps.A(false), XDialect.Tab);
            var problems = new List<string>();
            Assert.That(MapDiff.Build(FakeMaps.MapA, shipped, shipped, problems), Is.Null);
            Assert.That(problems, Is.Empty);

            // another display name
            List<XEntity> l = FakeMaps.A(false);
            l[0].Name = "Crate_A_renamed";
            Assert.That(MapDiff.Build(FakeMaps.MapA, shipped, XBlock.Bytes(l, XDialect.Tab), problems), Is.Null);
            Assert.That(problems.Count, Is.EqualTo(1));
            Assert.That(problems[0], Does.Contain(FakeMaps.Crate1).And.Contain("a mod cannot say that"));

            // the same values, properties of one entity in another order: no op, and the check says the bytes differ
            l = FakeMaps.A(false);
            l[0].Props.Reverse();
            l[3].Find("Position").Items[0] = new XSet(null, "300, 0, 300");
            byte[] edited = XBlock.Bytes(l, XDialect.Tab);
            problems.Clear();
            MapOp ops = MapDiff.Build(FakeMaps.MapA, shipped, edited, problems);
            Assert.That(problems, Is.Empty);
            Assert.That(ops.Ops.Count, Is.EqualTo(1));
            var mod = new Mod { Id = "diffed", Name = "diffed", Version = "1" };
            mod.Maps.Add(ops);
            Assert.That(MapDiff.Check(mod, FakeMaps.MapA, shipped, edited), Does.Contain("is not the edited map, byte for byte"));

            // the other layout
            Assert.That(MapDiff.Check(mod, FakeMaps.MapA, shipped, XBlock.Bytes(l, XDialect.Spaced)), Does.Contain("is not the edited map, byte for byte"));
        }

        [Test]
        public void EntityIdTwiceInTheShippedMapIsReported()
        {
            List<XEntity> l = FakeMaps.A(false);
            byte[] edited = XBlock.Bytes(l, XDialect.Tab);
            var twin = new XEntity { Id = l[0].Id, Model = l[0].Model, Name = l[0].Name, Iterations = 1 };
            twin.Props.Add(new XProp("Position", "900, 900, 900"));
            l.Insert(1, twin);
            var problems = new List<string>();
            Assert.That(MapDiff.Build(FakeMaps.MapA, XBlock.Bytes(l, XDialect.Tab), edited, problems), Is.Null);
            Assert.That(problems.Count, Is.EqualTo(1));
            Assert.That(problems[0], Does.Contain("the shipped map has the entity id " + l[0].Id + " twice"));
        }

        [Test]
        public void EntityIdAndOrderOfANewEntity()
        {
            const string open = "<add name=\"c\" model=\"Crate_A\" entity-name=\"Crate_A_900\"";
            const string props = "><property name=\"Position\" value=\"150, 300, 450\" /><property name=\"Scale\" value=\"2\" /></add>";
            using (var f = new FakeMaps())
            {
                Plan p = f.Plan(f.Mod("a", Map(open + " entity-id=\"" + New1 + "\" order=\"Scale,Position\"" + props)));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                List<XEntity> got = XBlock.Read(Bytes(p, K, FakeMaps.EntryA), "k");
                Assert.That(got.Select(e => e.Id).ToArray(), Is.EqualTo(new[] { FakeMaps.Crate1, FakeMaps.Spawn1, FakeMaps.Portal1, New1, FakeMaps.Crate2 }));
                Assert.That(got[3].Props.Select(x => x.Name).ToArray(), Is.EqualTo(new[] { "Scale", "Position" }));

                // an id the map has already
                p = f.Plan(f.Mod("b", Map(open + " entity-id=\"" + FakeMaps.Crate2 + "\"" + props)));
                Assert.That(Lines(p.Errors).Count(e => e.Contains("which is already in the map")), Is.EqualTo(3), Fake.Dump(p));

                foreach (string[] bad in new[]
                {
                    new[] { " entity-id=\"12\"", "is not an entity id" },
                    new[] { " order=\"Scale\"", "leaves out Position" },
                    new[] { " order=\"Scale,Position,Rotation\"", "names Rotation" },
                    new[] { " order=\"Scale,Scale,Position\"", "named twice" },
                })
                    Assert.That(Assert.Throws<ModKitException>(() => f.Mod("x" + bad[1].Length, Map(open + bad[0] + props))).Message, Does.Contain(bad[1]), bad[0]);
                Assert.That(Assert.Throws<ModKitException>(() => f.Mod("y", Map(
                    open + " entity-id=\"" + New1 + "\"" + props + open.Replace("\"c\"", "\"d\"") + " entity-id=\"" + New1 + "\"" + props))).Message, Does.Contain("is used twice"));
                // the properties the tool writes are part of the order
                Assert.That(Assert.Throws<ModKitException>(() => f.Mod("z", Map(Spawn("s", 140000, "").Replace(" point=", " order=\"Position\" point=")))).Message, Does.Contain("leaves out SpawnPointID"));
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
