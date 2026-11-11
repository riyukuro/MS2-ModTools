// The map editor project pieces on made-up models: the model, preset, project and solution files must be the bytes
// the Python scripts (tools/mapedit/flat2model.py, tools/toolbench/mkgsl.py) make from the same input.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.MapEdit;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class WbProjectTests
    {
        // made-up input files: path below the game folder, text
        static readonly string[,] Input =
        {
            { "Exported/flat/library/tool/base.flat",
              "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<model name=\"Base\" id=\"2838662793\">\n\t<trait value=\"Abstract\"/>\n\t<trait value=\"Active\"/>\n\t<property type=\"Point3\" name=\"Position\" id=\"4254452306\" source=\"x\">\n\t\t<set value=\"1, 2, 3\"/>\n\t\t<trait value=\"Persisted\"/>\n\t</property>\n\t<property type=\"Point3\" name=\"Rotation\" id=\"1757921949\" source=\"x\">\n\t\t<set value=\"0, 0, 90\"/>\n\t</property>\n\t<property type=\"Color\" name=\"Tint\" id=\"3894513984\" source=\"x\">\n\t\t<set value=\"1, 0.5, 0.2509804\"/>\n\t\t<trait value=\"Persisted\"/>\n\t\t<trait value=\"Shown\"/>\n\t</property>\n\t<property type=\"AssocString\" name=\"Names\" id=\"3163806014\" source=\"x\">\n\t\t<set index=\"0\" value=\"ann\"/>\n\t\t<set index=\"1\" value=\"b&amp;b\"/>\n\t</property>\n\t<property type=\"AssocEntityRef\" name=\"Links\" id=\"3526708863\" source=\"x\">\n\t\t<set index=\"0\" value=\"\"/>\n\t\t<set index=\"1\" value=\"11111111-2222-3333-4444-555555555555\"/>\n\t</property>\n\t<behavior name=\"OnTick\" id=\"961293468\" type=\"Native\" source=\"Base\">\n\t\t<trait value=\"Init\"/>\n\t</behavior>\n\t<behavior name=\"OnHit\" id=\"3279879925\" type=\"Virtual\" source=\"Base\">\n\t</behavior>\n</model>\n" },
            { "Exported/flat/library/tool/mover.flat",
              "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<model name=\"Mover\" id=\"1245817287\">\n\t<mixin name=\"Base\" id=\"2838662793\" type=\"x\"/>\n\t<property type=\"Point3\" name=\"Position\" id=\"4254452306\" source=\"x\">\n\t\t<set value=\"1, 2, 3\"/>\n\t\t<trait value=\"Persisted\"/>\n\t</property>\n\t<property type=\"Point3\" name=\"Rotation\" id=\"1757921949\" source=\"x\">\n\t\t<set value=\"0, 0, 90\"/>\n\t</property>\n\t<property type=\"ColorA\" name=\"Tint\" id=\"3894513984\" source=\"x\">\n\t\t<set value=\"0, 1, 0.3, 0.123\"/>\n\t\t<trait value=\"Shown\"/>\n\t\t<trait value=\"Extra\"/>\n\t</property>\n\t<property type=\"AssocString\" name=\"Names\" id=\"3163806014\" source=\"x\">\n\t\t<set index=\"0\" value=\"ann\"/>\n\t\t<set index=\"1\" value=\"b&amp;b\"/>\n\t</property>\n\t<property type=\"AssocEntityRef\" name=\"Links\" id=\"3526708863\" source=\"x\">\n\t\t<set index=\"0\" value=\"\"/>\n\t\t<set index=\"1\" value=\"11111111-2222-3333-4444-555555555555\"/>\n\t</property>\n\t<property type=\"Float32\" name=\"Speed\" id=\"3556802456\" source=\"x\">\n\t\t<set value=\"2.5\"/>\n\t</property>\n\t<property type=\"Float32\" name=\"FOV\" id=\"2293586166\" source=\"x\">\n\t\t<set value=\"60\"/>\n\t</property>\n\t<property type=\"AssocUInt32\" name=\"Categories\" id=\"2597942902\" source=\"x\">\n\t\t<set index=\"a\" value=\"1\"/>\n\t\t<set value=\"2\"/>\n\t</property>\n\t<behavior name=\"OnTick\" id=\"961293468\" type=\"Native\" source=\"Mover\">\n\t\t<trait value=\"Init\"/>\n\t\t<trait value=\"Late\"/>\n\t</behavior>\n\t<behavior name=\"OnMove\" id=\"2899002874\" type=\"Script\" source=\"Mover\">\n\t</behavior>\n\t<behavior name=\"OnHit\" id=\"3279879925\" type=\"Virtual\" source=\"Mover\">\n\t</behavior>\n\t<extradata name=\"Notes\" type=\"Table\">\n\t\t<entry type=\"String\" key=\"zed\" value=\"1\"/>\n\t\t<entry type=\"String\" key=\"alpha\" value=\"2\"/>\n\t</extradata>\n</model>\n" },
            { "Exported/flat/library/tool/extra.flat",
              "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<model name=\"Extra\" id=\"1617477560\">\n\t<mixin name=\"Mover\" id=\"1245817287\" type=\"x\"/>\n\t<mixin name=\"Base\" id=\"2838662793\" type=\"x\"/>\n\t<mixin name=\"Ghost\" id=\"3492135306\" type=\"x\"/>\n\t<mixin name=\"Base\" id=\"2838662793\" type=\"x\"/>\n\t<property type=\"Point3\" name=\"Position\" id=\"4254452306\" source=\"x\">\n\t\t<set value=\"1, 2, 3\"/>\n\t\t<trait value=\"Persisted\"/>\n\t</property>\n\t<property type=\"Point3\" name=\"Rotation\" id=\"1757921949\" source=\"x\">\n\t\t<set value=\"0, 0, 90\"/>\n\t</property>\n\t<property type=\"ColorA\" name=\"Tint\" id=\"3894513984\" source=\"x\">\n\t\t<set value=\"0, 1, 0.3, 0.123\"/>\n\t\t<trait value=\"Shown\"/>\n\t\t<trait value=\"Extra\"/>\n\t</property>\n\t<property type=\"AssocString\" name=\"Names\" id=\"3163806014\" source=\"x\">\n\t\t<set index=\"0\" value=\"ann\"/>\n\t\t<set index=\"1\" value=\"b&amp;b\"/>\n\t</property>\n\t<property type=\"AssocEntityRef\" name=\"Links\" id=\"3526708863\" source=\"x\">\n\t\t<set index=\"0\" value=\"\"/>\n\t\t<set index=\"1\" value=\"11111111-2222-3333-4444-555555555555\"/>\n\t</property>\n\t<property type=\"Float32\" name=\"Speed\" id=\"3556802456\" source=\"x\">\n\t\t<set value=\"2.5\"/>\n\t</property>\n\t<property type=\"Float32\" name=\"FOV\" id=\"2293586166\" source=\"x\">\n\t\t<set value=\"60\"/>\n\t</property>\n\t<property type=\"AssocUInt32\" name=\"Categories\" id=\"2597942902\" source=\"x\">\n\t\t<set index=\"a\" value=\"1\"/>\n\t\t<set value=\"2\"/>\n\t</property>\n\t<property type=\"AssocAttachedNifAsset\" name=\"AttachedObjects\" id=\"4052223838\" source=\"x\">\n\t\t<set index=\"hand\" value=\"Bip;urn:llid:0000aaaa-0000-0000-0000-000000000000;1, 2, 3;0, 0, 45;2\"/>\n\t\t<set index=\"foot\" value=\"Toe;urn:llid:0000bbbb-0000-0000-0000-000000000000\"/>\n\t</property>\n\t<property type=\"AssetID\" name=\"Shape\" id=\"3080957229\" source=\"x\">\n\t\t<set value=\"urn:llid:0000cccc-0000-0000-0000-000000000000\"/>\n\t</property>\n\t<property type=\"AssocPoint2\" name=\"Spots\" id=\"1170785281\" source=\"x\">\n\t\t<set index=\"0\" value=\"1, 2\"/>\n\t\t<set index=\"2\" value=\"3, 4\"/>\n\t</property>\n\t<property type=\"EntityRef\" name=\"Owner\" id=\"1619348985\" source=\"x\">\n\t\t<set value=\"\"/>\n\t</property>\n\t<property type=\"Boolean\" name=\"Solid\" id=\"2733270565\" source=\"x\">\n\t\t<set value=\"True\"/>\n\t</property>\n</model>\n" },
            { "Exported/flat/library/tool/kept.flat",
              "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<model name=\"Kept\" id=\"49098943\">\n\t<mixin name=\"Base\" id=\"2838662793\" type=\"x\"/>\n\t<property type=\"Point3\" name=\"Position\" id=\"4254452306\" source=\"x\">\n\t\t<set value=\"1, 2, 3\"/>\n\t\t<trait value=\"Persisted\"/>\n\t</property>\n\t<property type=\"Point3\" name=\"Rotation\" id=\"1757921949\" source=\"x\">\n\t\t<set value=\"0, 0, 90\"/>\n\t</property>\n\t<property type=\"Color\" name=\"Tint\" id=\"3894513984\" source=\"x\">\n\t\t<set value=\"1, 0.5, 0.2509804\"/>\n\t\t<trait value=\"Persisted\"/>\n\t\t<trait value=\"Shown\"/>\n\t</property>\n\t<property type=\"AssocString\" name=\"Names\" id=\"3163806014\" source=\"x\">\n\t\t<set index=\"0\" value=\"ann\"/>\n\t\t<set index=\"1\" value=\"b&amp;b\"/>\n\t</property>\n\t<property type=\"AssocEntityRef\" name=\"Links\" id=\"3526708863\" source=\"x\">\n\t\t<set index=\"0\" value=\"\"/>\n\t\t<set index=\"1\" value=\"11111111-2222-3333-4444-555555555555\"/>\n\t</property>\n</model>\n" },
            { "Exported/flat/library/gimodellibrary/lamp.flat",
              "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<model name=\"Lamp\" id=\"1929780381\">\n\t<property type=\"Float32\" name=\"OuterSpotAngle\" id=\"3014569209\" source=\"x\">\n\t\t<set value=\"30\"/>\n\t</property>\n\t<property type=\"AssocSInt32\" name=\"Steps\" id=\"1120239523\" source=\"x\">\n\t\t<set index=\"0\" value=\"5\"/>\n\t\t<set index=\"1\" value=\"6\"/>\n\t</property>\n</model>\n" },
            { "Exported/flat/library/gimodellibrary/glow.flat",
              "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<model name=\"Glow\" id=\"3902211528\">\n\t<mixin name=\"Lamp\" id=\"1929780381\" type=\"x\"/>\n\t<property type=\"Float32\" name=\"OuterSpotAngle\" id=\"3014569209\" source=\"x\">\n\t\t<set value=\"45\"/>\n\t</property>\n\t<property type=\"AssocSInt32\" name=\"Steps\" id=\"1120239523\" source=\"x\">\n\t\t<set index=\"0\" value=\"5\"/>\n\t\t<set index=\"1\" value=\"6\"/>\n\t</property>\n</model>\n" },
            { "Exported/flat/presets/presets cube/box_a.flat",
              "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<model name=\"box_a\" id=\"3429319733\">\n\t<mixin name=\"Mover\" id=\"1245817287\" type=\"x\"/>\n\t<mixin name=\"Base\" id=\"2838662793\" type=\"x\"/>\n\t<property type=\"Point3\" name=\"Position\" id=\"4254452306\" source=\"x\">\n\t\t<set value=\"1, 2, 3\"/>\n\t\t<trait value=\"Persisted\"/>\n\t</property>\n\t<property type=\"Point3\" name=\"Rotation\" id=\"1757921949\" source=\"x\">\n\t\t<set value=\"0, 0, 90\"/>\n\t</property>\n\t<property type=\"ColorA\" name=\"Tint\" id=\"3894513984\" source=\"x\">\n\t\t<set value=\"0, 1, 0.3, 0.123\"/>\n\t\t<trait value=\"Shown\"/>\n\t\t<trait value=\"Extra\"/>\n\t</property>\n\t<property type=\"AssocString\" name=\"Names\" id=\"3163806014\" source=\"x\">\n\t\t<set index=\"0\" value=\"ann\"/>\n\t\t<set index=\"1\" value=\"b&amp;b\"/>\n\t</property>\n\t<property type=\"AssocEntityRef\" name=\"Links\" id=\"3526708863\" source=\"x\">\n\t\t<set index=\"0\" value=\"\"/>\n\t\t<set index=\"1\" value=\"11111111-2222-3333-4444-555555555555\"/>\n\t</property>\n\t<property type=\"Float32\" name=\"Speed\" id=\"3556802456\" source=\"x\">\n\t\t<set value=\"9\"/>\n\t</property>\n\t<property type=\"Float32\" name=\"FOV\" id=\"2293586166\" source=\"x\">\n\t\t<set value=\"60\"/>\n\t\t<trait value=\"Shown\"/>\n\t</property>\n\t<property type=\"AssocUInt32\" name=\"Categories\" id=\"2597942902\" source=\"x\">\n\t\t<set index=\"a\" value=\"1\"/>\n\t\t<set value=\"2\"/>\n\t</property>\n</model>\n" },
            { "Exported/flat/presets/presets cube/box_b.flat",
              "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<model name=\"Box_B\" id=\"3429320756\">\n\t<mixin name=\"box_a\" id=\"3429319733\" type=\"x\"/>\n\t<mixin name=\"Mover\" id=\"1245817287\" type=\"x\"/>\n\t<mixin name=\"Base\" id=\"2838662793\" type=\"x\"/>\n\t<property type=\"Point3\" name=\"Position\" id=\"4254452306\" source=\"x\">\n\t\t<set value=\"1, 2, 3\"/>\n\t\t<trait value=\"Persisted\"/>\n\t</property>\n\t<property type=\"Point3\" name=\"Rotation\" id=\"1757921949\" source=\"x\">\n\t\t<set value=\"0, 0, 90\"/>\n\t</property>\n\t<property type=\"ColorA\" name=\"Tint\" id=\"3894513984\" source=\"x\">\n\t\t<set value=\"0, 1, 0.3, 0.123\"/>\n\t\t<trait value=\"Shown\"/>\n\t\t<trait value=\"Extra\"/>\n\t</property>\n\t<property type=\"AssocString\" name=\"Names\" id=\"3163806014\" source=\"x\">\n\t\t<set index=\"0\" value=\"ann\"/>\n\t\t<set index=\"1\" value=\"b&amp;b\"/>\n\t</property>\n\t<property type=\"AssocEntityRef\" name=\"Links\" id=\"3526708863\" source=\"x\">\n\t\t<set index=\"0\" value=\"\"/>\n\t\t<set index=\"1\" value=\"11111111-2222-3333-4444-555555555555\"/>\n\t</property>\n\t<property type=\"Float32\" name=\"Speed\" id=\"3556802456\" source=\"x\">\n\t\t<set value=\"11\"/>\n\t</property>\n\t<property type=\"Float32\" name=\"FOV\" id=\"2293586166\" source=\"x\">\n\t\t<set value=\"60\"/>\n\t\t<trait value=\"Shown\"/>\n\t</property>\n\t<property type=\"AssocUInt32\" name=\"Categories\" id=\"2597942902\" source=\"x\">\n\t\t<set index=\"a\" value=\"1\"/>\n\t\t<set value=\"2\"/>\n\t</property>\n</model>\n" },
            { "Exported/flat/presets/presets cube/box_c.flat",
              "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<model name=\"box_c\" id=\"3429319731\">\n\t<mixin name=\"Mover\" id=\"1245817287\" type=\"x\"/>\n\t<mixin name=\"Base\" id=\"2838662793\" type=\"x\"/>\n\t<property type=\"Point3\" name=\"Position\" id=\"4254452306\" source=\"x\">\n\t\t<set value=\"1, 2, 3\"/>\n\t\t<trait value=\"Persisted\"/>\n\t</property>\n\t<property type=\"Float32\" name=\"Weight\" id=\"452724692\" source=\"x\">\n\t\t<set value=\"4\"/>\n\t</property>\n</model>\n" },
            { "Exported/flat/presets/presets cube/box_d.flat",
              "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<model name=\"box_d\" id=\"3429319738\">\n\t<mixin name=\"Nowhere\" id=\"3718053609\" type=\"x\"/>\n\t<property type=\"Point3\" name=\"Position\" id=\"4254452306\" source=\"x\">\n\t\t<set value=\"1, 2, 3\"/>\n\t\t<trait value=\"Persisted\"/>\n\t</property>\n</model>\n" },
            { "Exported/flat/presets/presets wide thing/w_1.flat",
              "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<model name=\"w_1\" id=\"1514916862\">\n\t<mixin name=\"box_a\" id=\"3429319733\" type=\"x\"/>\n\t<mixin name=\"Mover\" id=\"1245817287\" type=\"x\"/>\n\t<mixin name=\"Base\" id=\"2838662793\" type=\"x\"/>\n\t<property type=\"Point3\" name=\"Position\" id=\"4254452306\" source=\"x\">\n\t\t<set value=\"7, 8, 9\"/>\n\t\t<trait value=\"Persisted\"/>\n\t</property>\n\t<property type=\"Point3\" name=\"Rotation\" id=\"1757921949\" source=\"x\">\n\t\t<set value=\"0, 0, 90\"/>\n\t</property>\n\t<property type=\"ColorA\" name=\"Tint\" id=\"3894513984\" source=\"x\">\n\t\t<set value=\"0, 1, 0.3, 0.123\"/>\n\t\t<trait value=\"Shown\"/>\n\t\t<trait value=\"Extra\"/>\n\t</property>\n\t<property type=\"AssocString\" name=\"Names\" id=\"3163806014\" source=\"x\">\n\t\t<set index=\"0\" value=\"ann\"/>\n\t\t<set index=\"1\" value=\"b&amp;b\"/>\n\t</property>\n\t<property type=\"AssocEntityRef\" name=\"Links\" id=\"3526708863\" source=\"x\">\n\t\t<set index=\"0\" value=\"\"/>\n\t\t<set index=\"1\" value=\"11111111-2222-3333-4444-555555555555\"/>\n\t</property>\n\t<property type=\"Float32\" name=\"Speed\" id=\"3556802456\" source=\"x\">\n\t\t<set value=\"9\"/>\n\t</property>\n\t<property type=\"Float32\" name=\"FOV\" id=\"2293586166\" source=\"x\">\n\t\t<set value=\"60\"/>\n\t\t<trait value=\"Shown\"/>\n\t</property>\n\t<property type=\"AssocUInt32\" name=\"Categories\" id=\"2597942902\" source=\"x\">\n\t\t<set index=\"a\" value=\"1\"/>\n\t\t<set value=\"2\"/>\n\t</property>\n</model>\n" },
            { "Exported/flat/presets/presets wide thing/w_2.flat",
              "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<model name=\"w_2\" id=\"4243800217\">\n\t<mixin name=\"Lamp\" id=\"1929780381\" type=\"x\"/>\n\t<property type=\"Float32\" name=\"OuterSpotAngle\" id=\"3014569209\" source=\"x\">\n\t\t<set value=\"30\"/>\n\t</property>\n\t<property type=\"AssocSInt32\" name=\"Steps\" id=\"1120239523\" source=\"x\">\n\t\t<set index=\"0\" value=\"5\"/>\n\t\t<set index=\"1\" value=\"7\"/>\n\t\t<set index=\"2\" value=\"8\"/>\n\t</property>\n</model>\n" },
            { "Library/tool/tool.emtproj",
              "\uFEFF<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Project version=\"5\">\r\n\t<Files>\r\n\t\t<File path=\"BASE.model\" />\r\n\t\t<File path=\"sub\\Mover.model\" />\r\n\t\t<File path=\"Kept.preset\" />\r\n\t\t<File path=\"Gone.model\" />\r\n\t\t<File path=\"notes.lua\" />\r\n\t</Files>\r\n</Project>" },
            { "Library/tool/kept.preset",
              "shipped preset, kept as it is\n" },
            { "Library/tool/notes.lua",
              "-- made up\n" },
            { "Library/gimodellibrary/gimodellibrary.emtproj",
              "\uFEFF<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Project\r\n  version=\"5\">\r\n  <Files>\r\n    <File\r\n      path=\"Lamp.model\" />\r\n  </Files>\r\n</Project>" },
        };

        // every file the Python scripts make from that input: path, sha256
        static readonly string[,] Expected =
        {
            { "Library/Presets/Cube/Box_B.preset", "84b99dab7a4d0669dd64323edfc9276293710ac27aba410a1f30f6d61ee40376" },
            { "Library/Presets/Cube/box_a.preset", "4572ea3949a6a85462d8585c34665cf5aa5753e3933a4cdaac066b4ce6e550da" },
            { "Library/Presets/Cube/box_c.preset", "edf7cc0fbb9c9ea9a4aa4c6f1de294d7385bb253245572caab4ff93ff6565ece" },
            { "Library/Presets/Cube/box_d.preset", "dfbfc3e1388c64c4ff2cb6ba353fb793a422fe0974f2bcfa3b35fa0403cbecc3" },
            { "Library/Presets/Presets_Cube.emtproj", "44864eade5b7f7764fbc43970d6e5f974daabb09793a71e46348eea68e382e44" },
            { "Library/Presets/Presets_Wide_Thing.emtproj", "0de680d9fb2a5dc15fea95dd101d90f38634cab81239a9780c539cee4bbfc096" },
            { "Library/Presets/Wide_Thing/w_1.preset", "94b4be6494a44bed59a09bdfc58bc8b6deccb4e8323d79ffbd22184162531f84" },
            { "Library/Presets/Wide_Thing/w_2.preset", "3be77c099fcef19088d7da5e057e0b2f2728eea75c4a576404808b01a10f3c7c" },
            { "Library/gimodellibrary/Glow.model", "b8eb345ddabb26440dfe049526c2f1e7124e95b2dee426283b8ae91c913a3b32" },
            { "Library/gimodellibrary/Lamp.model", "58789cdf4dca1b330bca3ccf70e1a52df4cc5520bf998a723b7b0af05897aa3f" },
            { "Library/gimodellibrary/gimodellibrary.emtproj", "d1d9c147ea9f60fb5fe08b98ce06335fdd63ba6f5e33fd081ae358ba55741623" },
            { "Library/tool/BASE.model", "1c9f75361d5dcc53593f401faf059549751e732705e8d113764987ebc904be1e" },
            { "Library/tool/Extra.model", "d0e50f0900639ce5aeb78c9df92935bee801ce247c1933e7e2c2c1e0cc79488e" },
            { "Library/tool/kept.preset", "b3774d8db4eacf8ef9dec40c4f78290b68f5b069c53e2e819e94594644587703" },
            { "Library/tool/notes.lua", "b40d8572a2a8796cdde1e2579aa9f660102401a575bf2eaa06da3ad4df97e293" },
            { "Library/tool/sub/Mover.model", "8bbf9dece790cdc35a3b283eacd2ce95bbb53dd6def7f2404db33988e13b840c" },
            { "Library/tool/tool.emtproj", "251949a187d355b2a060a9aaabb719e2a366de39c5878b5a6c9b4a80b5236ce2" },
            { "fake.gsl", "55974fc48b532e26642e10549471d744b339f9ff1a18abb80425fc74e63f8cd7" },
            { "logs/flat2model-warnings.txt", "b4acc987412e280764fca13fd63ebafd0206e7324ee5485a5414727aceafa4a1" },
        };

        static readonly string[,] Hashes =
        {
            { "", "757602046" },
            { "a", "3452614591" },
            { "ab", "3454580671" },
            { "abc", "536991770" },
            { "abcd", "1594742810" },
            { "Position", "4254452306" },
            { "made up name", "961693258" },
            { "héllo wörld 12345", "636466515" },
        };

        string dir;

        [SetUp]
        public void Make()
        {
            dir = Path.Combine(Path.GetTempPath(), "modkit-wb-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }

        [TearDown]
        public void Clean() { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

        // the library goes to <where>/Library, the flats to <game>/Exported/flat
        void WriteInput(string game, string libraryParent)
        {
            for (int i = 0; i < Input.GetLength(0); i++)
            {
                string rel = Input[i, 0];
                string p = Path.Combine(rel.StartsWith("Library/") ? libraryParent : game, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllBytes(p, new UTF8Encoding(false).GetBytes(Input[i, 1]));
            }
        }

        static string Sha(string path)
        {
            using (SHA256 s = SHA256.Create()) return BitConverter.ToString(s.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        }

        static void Files(string root, string dir, List<string> res)
        {
            foreach (string f in Directory.GetFiles(dir)) res.Add(f.Substring(root.Length + 1).Replace(Path.DirectorySeparatorChar, '/'));
            foreach (string d in Directory.GetDirectories(dir)) if (Path.GetFileName(d) != "Exported") Files(root, d, res);
        }

        [Test]
        public void NameHashIsTheOldRuntimes()
        {
            for (int i = 0; i < Hashes.GetLength(0); i++)
                Assert.AreEqual(Hashes[i, 1], NetHash.Of(Hashes[i, 0]).ToString(), "hash of '" + Hashes[i, 0] + "'");
        }

        [Test]
        public void ModelsProjectsAndSolutionMatchThePythonScripts()
        {
            WriteInput(dir, dir);
            WbFlats flats = WbFlats.FromFiles(WbProject.ReadFlatFiles(Path.Combine(dir, "Exported")));
            Assert.AreEqual(12, flats.Count);
            WbBuildResult r = WbProject.BuildModels(dir, flats, null, null);
            File.WriteAllBytes(Path.Combine(dir, "fake.gsl"), new UTF8Encoding(false).GetBytes(WbProject.SolutionText(dir, "fake.gsl")));
            var got = new List<string>();
            Files(dir, dir, got);
            Assert.AreEqual(Expected.GetLength(0), got.Count, string.Join(" ", got.ToArray()));
            for (int i = 0; i < Expected.GetLength(0); i++)
                Assert.AreEqual(Expected[i, 1], Sha(Path.Combine(dir, Expected[i, 0])), Expected[i, 0]);
            Assert.AreEqual(3, r.Stat("library-model-generated"));
            Assert.AreEqual(2, r.Stat("library-model-generated-and-added-to-project"));
            Assert.AreEqual(1, r.Stat("listed-without-flat"));
            Assert.AreEqual(1, r.Stat("library-preset-kept(shipped)"));
            Assert.AreEqual(6, r.Stat("preset-generated"));
            Assert.AreEqual(4, r.Warnings.Count);
        }

        [Test]
        public void KnownIdIsUsedForTheModelAndForItsUsers()
        {
            WriteInput(dir, dir);
            WbFlats flats = WbFlats.FromFiles(WbProject.ReadFlatFiles(Path.Combine(dir, "Exported")));
            var known = new Dictionary<string, string> { { "Base", "01234567-89ab-cdef-0123-456789abcdef" } };
            WbProject.BuildModels(dir, flats, known, null);
            StringAssert.Contains("ID=\"01234567-89ab-cdef-0123-456789abcdef\"", File.ReadAllText(Path.Combine(dir, "Library/tool/BASE.model")));
            StringAssert.Contains("SourceID=\"01234567-89ab-cdef-0123-456789abcdef\"", File.ReadAllText(Path.Combine(dir, "Library/tool/sub/Mover.model")));
        }

        [Test]
        public void ProjectForOneMapHoldsOnlyWhatTheMapUses()
        {
            string game = Path.Combine(dir, "game"), root = Path.Combine(dir, "proj");
            WriteInput(game, game);
            var a = new XEntity { Id = "000000000000000000000000000000a1", Model = "w_1", Name = "one" };
            a.Props.Add(new XProp("Position", "4, 5, 6"));
            var b = new XEntity { Id = "000000000000000000000000000000a2", Model = "Extra", Name = "two" };
            b.Props.Add(new XProp("Shape", "urn:llid:0000DDDD-0000-0000-0000-000000000000"));
            var o = new WbProjectOptions { Root = root, LibrarySource = Path.Combine(game, "Library") };
            o.Maps.Add(new KeyValuePair<string, byte[]>("90000001_made_up", XBlock.Bytes(new[] { a, b }, XDialect.Tab)));
            o.AssetPaths = new Dictionary<string, string> { { "0000aaaa-0000-0000-0000-000000000000", "/Model/made/up.nif" } };
            List<string> log = WbProject.Build(o, WbProject.ReadFlatFiles(Path.Combine(game, "Exported")));

            Assert.IsTrue(File.Exists(Path.Combine(root, "Maps/90000001_made_up.block")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "Library/Presets/Wide_Thing/w_1.preset")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "Library/Presets/Cube/box_a.preset")), "the preset w_1 is made from");
            Assert.IsFalse(File.Exists(Path.Combine(root, "Library/Presets/Wide_Thing/w_2.preset")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "Library/Presets/Cube/Box_B.preset")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "Library/tool/Extra.model")));
            Assert.IsTrue(Directory.Exists(Path.Combine(root, "Exported/xblock")));
            // the files of the presets that were written are the same as in the whole build
            for (int i = 0; i < Expected.GetLength(0); i++)
                if (Expected[i, 0].EndsWith(".preset") || Expected[i, 0].EndsWith(".model"))
                {
                    string p = Path.Combine(root, Expected[i, 0]);
                    if (File.Exists(p)) Assert.AreEqual(Expected[i, 1], Sha(p), Expected[i, 0]);
                }
            string proj = File.ReadAllText(Path.Combine(root, "Library/Presets/Presets_Cube.emtproj"));
            StringAssert.Contains("Cube\\box_a.preset", proj);
            StringAssert.DoesNotContain("box_c", proj);
            Assert.AreEqual("Library/tool/tool.emtproj: Gone.model\n", File.ReadAllText(Path.Combine(root, "MISSING.txt")));
            Assert.AreEqual("0000dddd-0000-0000-0000-000000000000\t\n0000aaaa-0000-0000-0000-000000000000\t/Model/made/up.nif\n" +
                "0000bbbb-0000-0000-0000-000000000000\t\n0000cccc-0000-0000-0000-000000000000\t\n", File.ReadAllText(Path.Combine(root, "ASSETS.txt")));
            string sol = File.ReadAllText(Path.Combine(root, "proj.gsl"));
            StringAssert.Contains("filepath=\"Maps\\90000001_made_up.block\"", sol);
            StringAssert.Contains("filepath=\"Library\\Presets\\Presets_Wide_Thing.emtproj\"", sol);
            StringAssert.Contains("do not put it in a repository", log[log.Count - 1]);
            // the game's own folder was only read
            Assert.IsFalse(File.Exists(Path.Combine(game, "Library/tool/Extra.model")));
        }

        static byte[] Str(string s)
        {
            var b = new List<byte>(BitConverter.GetBytes((uint)s.Length));
            b.AddRange(Encoding.ASCII.GetBytes(s));
            return b.ToArray();
        }

        // a made-up model file: some bytes, then strings stored as a 4-byte length and the text
        static byte[] FakeModelFile(params string[] strings)
        {
            var b = new List<byte>(Encoding.ASCII.GetBytes("Made-up model file\n"));
            b.AddRange(new byte[] { 1, 0, 0, 0, 0xff, 0x41, 0x42 });
            foreach (string s in strings) b.AddRange(Str(s));
            b.AddRange(new byte[] { 0, 0, 0, 0 });
            return b.ToArray();
        }

        const string LongTexture = "a_texture_name_of_forty_characters_xx.dds";   // its length byte is a printable character

        [Test]
        public void TextureNamesOfAModelFile()
        {
            Assert.AreEqual(41, LongTexture.Length);
            List<string> t = WbAssets.TextureNames(FakeModelFile("NodeName", "Tex_A.dds", LongTexture, "some\\folder\\In_Folder.DDS", "tex_a.dds", "not a texture"));
            Assert.AreEqual(new[] { "Tex_A.dds", LongTexture, "In_Folder.DDS" }, t.ToArray());
        }

        [Test]
        public void ModelAndTextureFilesAreCopiedFromTheResourceFolder()
        {
            string game = Path.Combine(dir, "game"), root = Path.Combine(dir, "proj"), res = Path.Combine(game, "Resource");
            WriteInput(game, game);
            byte[] model = FakeModelFile("Tex_A.dds", LongTexture, "gone.dds", "lost.dds");
            byte[] texA = { 1, 2, 3 }, texLong = { 4, 5, 6, 7 }, texTwin = { 8 };
            Directory.CreateDirectory(Path.Combine(res, "Model"));
            // one archive Model/Made.m2d (entry names in lower case, as the game stores them) and one plain file
            M2dWriter.Create(Path.Combine(res, "Model/Made.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                new KeyValuePair<string, byte[]>("up.nif", model),
                new KeyValuePair<string, byte[]>("textures/tex_a.dds", texA),
                new KeyValuePair<string, byte[]>("textures/" + LongTexture, texLong),
            });
            Directory.CreateDirectory(Path.Combine(res, "Model/Other"));
            File.WriteAllBytes(Path.Combine(res, "Model/Other/tex_a.dds"), texTwin);

            var a = new XEntity { Id = "000000000000000000000000000000a1", Model = "Extra", Name = "one" };
            var o = new WbProjectOptions { Root = root, LibrarySource = Path.Combine(game, "Library"), ResourceDir = res };
            o.Maps.Add(new KeyValuePair<string, byte[]>("90000001_made_up", XBlock.Bytes(new[] { a }, XDialect.Tab)));
            o.AssetPaths = new Dictionary<string, string> { { "0000aaaa-0000-0000-0000-000000000000", "/Model/Made/Up.nif" } };
            o.AssetFiles = new List<string> { "/Model/Made/Up.nif", "/Model/Made/Textures/Tex_A.dds", "/Model/Made/Textures/" + LongTexture,
                "/Model/Other/Tex_A.dds", "/Model/Made/Textures/lost.dds" };
            List<string> log = WbProject.Build(o, WbProject.ReadFlatFiles(Path.Combine(game, "Exported")));

            Assert.AreEqual(model, File.ReadAllBytes(Path.Combine(root, "Model/Made/Up.nif")));
            Assert.AreEqual(texA, File.ReadAllBytes(Path.Combine(root, "Model/Made/Textures/Tex_A.dds")));
            Assert.AreEqual(texLong, File.ReadAllBytes(Path.Combine(root, "Model/Made/Textures/" + LongTexture)));
            Assert.AreEqual(texTwin, File.ReadAllBytes(Path.Combine(root, "Model/Other/Tex_A.dds")), "every file of the name is copied");
            string missing = File.ReadAllText(Path.Combine(root, "MISSING.txt"));
            StringAssert.Contains("texture gone.dds (named by /Model/Made/Up.nif): not in the asset list\n", missing);
            StringAssert.Contains("texture /Model/Made/Textures/lost.dds (named by /Model/Made/Up.nif): in no archive\n", missing);
            StringAssert.Contains("asset 0000bbbb-0000-0000-0000-000000000000: not in the asset list\n", missing);
            StringAssert.Contains("copied 1 model files, 3 texture files and 0 other files (", string.Join("\n", log.ToArray()));

            // the switch off: no ResourceDir, nothing is copied
            string root2 = Path.Combine(dir, "proj2");
            o.Root = root2; o.ResourceDir = null;
            WbProject.Build(o, WbProject.ReadFlatFiles(Path.Combine(game, "Exported")));
            Assert.IsFalse(Directory.Exists(Path.Combine(root2, "Model")));
        }

        // ---- names that could leave the output folder ----

        static readonly string ToolProjectHead = "\uFEFF<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Project version=\"5\">\r\n\t<Files>\r\n\t\t<File path=\"BASE.model\" />\r\n";
        const string ToolProjectTail = "\t</Files>\r\n</Project>";

        void Put(string rel, string text)
        {
            string p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllBytes(p, new UTF8Encoding(false).GetBytes(text));
        }

        // everything below the test folder except the project folder and the game folder: must stay as it was
        List<string> Outside(params string[] own)
        {
            var res = new List<string>();
            foreach (string e in Directory.GetFileSystemEntries(dir))
                if (Array.IndexOf(own, Path.GetFileName(e)) < 0) res.Add(Path.GetFileName(e));
            return res;
        }

        [Test]
        public void OutPathRefusesNamesThatLeaveTheFolder()
        {
            string[] bad = { "..", "../x", "a/../../x", "a\\..\\x", "..\\x", "/x", "\\x", "\\\\host\\share\\x", "C:\\x", "C:x", "c:/x", "a/b:c", "", ".", "./", ".. /x", "a/.../x", "a\nb", null };
            foreach (string b in bad)
            {
                string full, why;
                Assert.IsFalse(OutPath.TryUnder(dir, dir, b, out full, out why), b);
                Assert.IsNotNull(why, b);
                Assert.IsNull(full, b);
                Assert.Throws<ModKitException>(delegate { OutPath.Under(dir, b); }, b);
            }
            string[] good = { "x", "a/b.c", "a\\b.c", "./a/x", "a//x", "..x", "x..", "a.b/..c" };
            foreach (string g in good)
            {
                string p = OutPath.Under(dir, g);
                StringAssert.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, p, g);
            }
            // a base folder that is not inside the root
            string f, w;
            Assert.IsFalse(OutPath.TryUnder(Path.Combine(dir, "in"), dir, "x", out f, out w));
            Assert.IsFalse(OutPath.TryUnder(Path.Combine(dir, "in"), Path.Combine(dir, "inside"), "x", out f, out w), "a folder whose name only starts like the root");
        }

        [Test]
        public void ProjectFilePathWithDotDotIsRefused()
        {
            string root = Path.Combine(dir, "proj");
            WriteInput(dir, root);
            Put("proj/Library/tool/tool.emtproj", ToolProjectHead + "\t\t<File path=\"..\\..\\..\\out\\Mover.model\" />\r\n\t\t<File path=\"sub/../../../out2/Kept.model\" />\r\n" + ToolProjectTail);
            WbFlats flats = WbFlats.FromFiles(WbProject.ReadFlatFiles(Path.Combine(dir, "Exported")));
            WbBuildResult r = WbProject.BuildModels(root, flats, null, null);
            Assert.AreEqual(2, r.Refused.Count, string.Join("\n", r.Refused.ToArray()));
            StringAssert.Contains("Mover.model", r.Refused[1]);
            StringAssert.Contains("'..' part", r.Refused[1]);
            Assert.AreEqual(new string[0], Outside("proj", "Exported").ToArray(), "nothing beside the project folder");
            Assert.IsTrue(File.Exists(Path.Combine(root, "Library/tool/BASE.model")), "the safe files are still written");
        }

        [Test]
        public void ModelNameWithDotDotIsRefused()
        {
            string root = Path.Combine(dir, "proj");
            WriteInput(dir, root);
            const string body = "\">\n\t<property type=\"Float32\" name=\"Weight\" id=\"452724692\" source=\"x\">\n\t\t<set value=\"4\"/>\n\t</property>\n</model>\n";
            Put("Exported/flat/library/tool/esc.flat", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<model name=\"../../../esc\" id=\"1" + body);
            Put("Exported/flat/library/tool/esc2.flat", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<model name=\"..\\..\\..\\esc2\" id=\"2" + body);
            Put("Exported/flat/presets/presets cube/esc3.flat", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<model name=\"../../../../esc3\" id=\"3\">\n\t<mixin name=\"Mover\" id=\"1245817287\" type=\"x\"/>\n</model>\n");
            WbFlats flats = WbFlats.FromFiles(WbProject.ReadFlatFiles(Path.Combine(dir, "Exported")));
            WbBuildResult r = WbProject.BuildModels(root, flats, null, null);
            string all = string.Join("\n", r.Refused.ToArray());
            Assert.AreEqual(3, r.Refused.Count, all);
            StringAssert.Contains("esc.flat", all);
            StringAssert.Contains("esc2.flat", all);
            StringAssert.Contains("esc3.flat", all);
            Assert.AreEqual(new string[0], Outside("proj", "Exported").ToArray(), "nothing beside the project folder");
            var got = new List<string>();
            Files(root, root, got);
            foreach (string g in got) StringAssert.DoesNotContain("esc", g);
            StringAssert.DoesNotContain("esc", File.ReadAllText(Path.Combine(root, "Library/tool/tool.emtproj")));
            StringAssert.DoesNotContain("esc", File.ReadAllText(Path.Combine(root, "Library/Presets/Presets_Cube.emtproj")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "Library/Presets/Cube/box_a.preset")), "the safe files are still written");
        }

        [Test]
        public void DriveLetterNameIsRefused()
        {
            string root = Path.Combine(dir, "proj");
            WriteInput(dir, root);
            Put("proj/Library/tool/tool.emtproj", ToolProjectHead + "\t\t<File path=\"C:\\made\\Mover.model\" />\r\n\t\t<File path=\"c:/x/Kept.model\" />\r\n" + ToolProjectTail);
            WbFlats flats = WbFlats.FromFiles(WbProject.ReadFlatFiles(Path.Combine(dir, "Exported")));
            WbBuildResult r = WbProject.BuildModels(root, flats, null, null);
            Assert.AreEqual(2, r.Refused.Count, string.Join("\n", r.Refused.ToArray()));
            StringAssert.Contains("drive letter", r.Refused[0]);
            var got = new List<string>();
            Files(root, root, got);
            foreach (string g in got) { StringAssert.DoesNotContain(":", g); StringAssert.DoesNotContain("Mover.model", g); StringAssert.DoesNotContain("Kept.model", g); }
        }

        [Test]
        public void RootedNamesAreRefused()
        {
            string game = Path.Combine(dir, "game"), root = Path.Combine(dir, "proj"), away = Path.Combine(dir, "away");
            WriteInput(game, game);
            Directory.CreateDirectory(away);
            // a rooted File path in a project file
            Put("game/Library/tool/tool.emtproj", ToolProjectHead + "\t\t<File path=\"" + away.Replace('\\', '/') + "/Mover.model\" />\r\n" + ToolProjectTail);
            // a rooted map name, a map name with "..", and a good one
            var e = new XEntity { Id = "000000000000000000000000000000a1", Model = "Extra", Name = "one" };
            byte[] map = XBlock.Bytes(new[] { e }, XDialect.Tab);
            var o = new WbProjectOptions { Root = root, LibrarySource = Path.Combine(game, "Library") };
            o.Maps.Add(new KeyValuePair<string, byte[]>(Path.Combine(away, "rooted_map"), map));
            o.Maps.Add(new KeyValuePair<string, byte[]>("../../away/up_map", map));
            o.Maps.Add(new KeyValuePair<string, byte[]>("90000001_made_up", map));
            WbProject.Build(o, WbProject.ReadFlatFiles(Path.Combine(game, "Exported")));
            string all = string.Join("\n", o.Refused.ToArray());
            Assert.AreEqual(3, o.Refused.Count, all);
            StringAssert.Contains("Mover.model", o.Refused[0]);
            StringAssert.Contains("rooted_map", all);
            StringAssert.Contains("up_map", all);
            Assert.AreEqual(0, Directory.GetFileSystemEntries(away).Length, "nothing was written outside the project");
            Assert.IsTrue(File.Exists(Path.Combine(root, "Maps/90000001_made_up.block")), "the safe map is still written");
            StringAssert.DoesNotContain("rooted_map", File.ReadAllText(Path.Combine(root, "proj.gsl")));

            // the solution file is a file name, not a path
            Assert.Throws<ModKitException>(delegate { WbProject.SolutionPath(root, "../x.gsl"); });
            Assert.Throws<ModKitException>(delegate { WbProject.SolutionPath(root, "sub/x.gsl"); });
            Assert.Throws<ModKitException>(delegate { WbProject.SolutionPath(root, Path.Combine(away, "x.gsl")); });
            Assert.Throws<ModKitException>(delegate { WbProject.SolutionPath(root, "C:x.gsl"); });
            o.Root = Path.Combine(dir, "proj3"); o.SolutionName = "..\\x.gsl";
            Assert.Throws<ModKitException>(delegate { WbProject.Build(o, WbProject.ReadFlatFiles(Path.Combine(game, "Exported"))); });
            Assert.IsFalse(File.Exists(Path.Combine(dir, "x.gsl")));
        }

        [Test]
        public void LayerFileNameFromABlockFileIsRefused()
        {
            var doc = new BlockDoc("made_up");
            doc.Layers.Add(new BLayer { Name = "l", Path = "../made_up.l.layer" });
            string dst = Path.Combine(Path.Combine(dir, "maps"), "made_up.block");
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            Assert.Throws<ModKitException>(delegate { doc.Write(dst); });
            Assert.IsFalse(File.Exists(dst), "nothing is written, not even the block");
            Assert.IsFalse(File.Exists(Path.Combine(dir, "made_up.l.layer")));
            Assert.AreEqual(1, doc.UnsafeNames().Count);
        }

        [Test]
        public void UnsafeAssetPathIsRefusedAndTheRestIsCopied()
        {
            string res = Path.Combine(dir, "Resource"), root = Path.Combine(dir, "proj");
            Directory.CreateDirectory(Path.Combine(res, "Model/Made"));
            File.WriteAllBytes(Path.Combine(res, "Model/Made/a.nif"), FakeModelFile("none"));
            File.WriteAllBytes(Path.Combine(res, "Model/Made/b.nif"), FakeModelFile("none"));
            Directory.CreateDirectory(root);
            var paths = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("0000aaaa-0000-0000-0000-000000000000", "//Model/Made/a.nif"),
                new KeyValuePair<string, string>("0000bbbb-0000-0000-0000-000000000000", "/Model/Made/b.nif"),
                new KeyValuePair<string, string>("0000cccc-0000-0000-0000-000000000000", "/Model/../../Made/a.nif"),
            };
            using (var src = new WbAssetSource(res))
            {
                WbAssetCopy c = WbAssets.Copy(root, src, paths, new List<string>());
                Assert.AreEqual(2, c.Refused.Count, string.Join("\n", c.Refused.ToArray()));
                StringAssert.Contains("rooted name", c.Refused[0]);
                StringAssert.Contains("'..' part", c.Refused[1]);
                Assert.AreEqual(1, c.Models);
                Assert.AreEqual(0, c.Missing.Count, "a refused path is not read");
            }
            Assert.IsTrue(File.Exists(Path.Combine(root, "Model/Made/b.nif")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "Model/Made/a.nif")));
        }

        [Test]
        public void AssetPathWithAColonIsNotRead()
        {
            string res = Path.Combine(dir, "Resource"), root = Path.Combine(dir, "proj");
            Directory.CreateDirectory(Path.Combine(res, "Model"));
            Directory.CreateDirectory(root);
            // where such names can be files, they are there and still not read
            if (Path.DirectorySeparatorChar == '/')
            {
                File.WriteAllText(Path.Combine(res, "Model/C:made.nif"), "made up");
                File.WriteAllText(Path.Combine(res, "Model/a\u0001.nif"), "made up");
            }
            using (var src = new WbAssetSource(res))
            {
                Assert.IsNull(src.Read("/Model/a\u0001.nif"));
                Assert.IsNull(src.Read("/Model/C:made.nif"));
                Assert.IsNull(src.Read("/C:/made.nif"));
                Assert.IsNull(src.Read("/Model/./made.nif"));
                Assert.IsNull(src.Read("/Model/../made.nif"));
                WbAssetCopy c = WbAssets.Copy(root, src, new List<KeyValuePair<string, string>> {
                    new KeyValuePair<string, string>("0000aaaa-0000-0000-0000-000000000000", "/Model/C:made.nif") }, new List<string>());
                Assert.AreEqual(1, c.Refused.Count);
                Assert.AreEqual(0, c.Missing.Count);
            }
            Assert.AreEqual(0, Directory.GetFileSystemEntries(root).Length);
        }

        [Test]
        public void AssetFoldersThatDifferInLetterCaseBecomeOne()
        {
            string res = Path.Combine(dir, "Resource"), root = Path.Combine(dir, "proj");
            Directory.CreateDirectory(Path.Combine(res, "Model/Made"));
            byte[] model = FakeModelFile("Tex_A.dds");
            File.WriteAllBytes(Path.Combine(res, "Model/Made/a.nif"), model);
            File.WriteAllBytes(Path.Combine(res, "Model/Made/b.kfm"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(res, "Model/Made/tex_a.dds"), new byte[] { 2 });
            Directory.CreateDirectory(Path.Combine(root, "model"));
            var paths = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("0000aaaa-0000-0000-0000-000000000000", "/Model/Made/a.nif"),
                new KeyValuePair<string, string>("0000bbbb-0000-0000-0000-000000000000", "/model/made/b.kfm"),
            };
            using (var src = new WbAssetSource(res))
            {
                WbAssetCopy c = WbAssets.Copy(root, src, paths, new List<string> { "/MODEL/MADE/Tex_A.dds" });
                Assert.AreEqual(0, c.Refused.Count + c.Missing.Count, string.Join("\n", c.Missing.ToArray()));
                Assert.AreEqual(1, c.Models);
                Assert.AreEqual(1, c.Others, "a file that is not a model file is counted too");
                Assert.AreEqual(1, c.Textures);
            }
            var got = new List<string>();
            Files(root, root, got);
            got.Sort(StringComparer.Ordinal);
            // the folder that was there gives its letter case; a new folder takes the first name written
            Assert.AreEqual(new[] { "model/Made/Tex_A.dds", "model/Made/a.nif", "model/Made/b.kfm" }, got.ToArray());
        }

        [Test]
        public void TextureNameThatGoesOnIsNotATextureName()
        {
            var b = new List<byte>(new byte[] { 0, 0, 0, 0 });
            b.AddRange(Encoding.ASCII.GetBytes("plain_a.dds.bak"));
            b.Add(0);
            b.AddRange(Encoding.ASCII.GetBytes("plain_b.dds"));
            b.Add(0);
            Assert.AreEqual(new[] { "plain_b.dds" }, WbAssets.TextureNames(b.ToArray()).ToArray());
        }

        static void MakeArchive(string path, params string[] names)
        {
            var e = new List<KeyValuePair<string, byte[]>>();
            foreach (string n in names) e.Add(new KeyValuePair<string, byte[]>(n, Encoding.ASCII.GetBytes("made up " + n)));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            M2dWriter.Create(path, PackVersion.NS2F, M2dFlag.AesZlib, e);
        }

        [Test]
        public void LibraryArchiveWithAFolderEntryCopiesNothing()
        {
            string game = Path.Combine(dir, "game"), root = Path.Combine(dir, "proj");
            WriteInput(game, game);
            string arch = Path.Combine(dir, "Library.m2d");
            MakeArchive(arch, "tool/a.model", "tool/", "Tool/b.model");
            var e = new XEntity { Id = "000000000000000000000000000000a1", Model = "Extra", Name = "one" };
            var o = new WbProjectOptions { Root = root, LibrarySource = arch };
            o.Maps.Add(new KeyValuePair<string, byte[]>("90000001_made_up", XBlock.Bytes(new[] { e }, XDialect.Tab)));
            var ex = Assert.Throws<ModKitException>(delegate { WbProject.Build(o, WbProject.ReadFlatFiles(Path.Combine(game, "Exported"))); });
            StringAssert.Contains("names a folder", ex.Message);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "Library")), "no half copied library that a second run would take for a whole one");
            Assert.AreEqual(0, Directory.GetFileSystemEntries(root).Length);

            // a copy that stopped half way is thrown away, and folders that differ in letter case become one
            Directory.CreateDirectory(Path.Combine(root, "Library.copying/old"));
            File.WriteAllText(Path.Combine(root, "Library.copying/old/left.model"), "made up");
            MakeArchive(arch, "tool/a.model", "Tool/b.model");
            WbProject.Build(o, WbProject.ReadFlatFiles(Path.Combine(game, "Exported")));
            var got = new List<string>();
            Files(Path.Combine(root, "Library"), Path.Combine(root, "Library"), got);
            got.Sort(StringComparer.Ordinal);
            Assert.AreEqual(new[] { "tool/a.model", "tool/b.model" }, got.ToArray());
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "Library.copying")));
        }

        [Test]
        public void FlatNameIsEscapedInTheProjectFile()
        {
            string root = Path.Combine(dir, "proj");
            WriteInput(dir, root);
            Put("Exported/flat/library/tool/odd.flat", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<model name=\"Odd&amp;&lt;&quot;Name\" id=\"1\">\n\t<property type=\"Float32\" name=\"Weight\" id=\"452724692\" source=\"x\">\n\t\t<set value=\"4\"/>\n\t</property>\n</model>\n");
            WbFlats flats = WbFlats.FromFiles(WbProject.ReadFlatFiles(Path.Combine(dir, "Exported")));
            WbBuildResult r = WbProject.BuildModels(root, flats, null, null);
            string proj = File.ReadAllText(Path.Combine(root, "Library/tool/tool.emtproj"));
            Assert.AreEqual(0, r.Refused.Count);
            StringAssert.Contains("Odd&amp;&lt;&quot;Name.model\"", proj);
            // the file is found again under its unescaped name: only the made-up input's own missing file is listed
            Assert.AreEqual("Library/tool/tool.emtproj: Gone.model", string.Join("|", WbProject.Missing(root, new List<string>()).ToArray()));
            var x = new System.Xml.XmlDocument();
            x.LoadXml(proj.TrimStart('\uFEFF'));
        }

        [Test]
        public void FlatNameWithEscapesIsListedOnce()
        {
            string root = Path.Combine(dir, "proj");
            WriteInput(dir, root);
            Put("Exported/flat/library/tool/odd.flat", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<model name=\"Odd&amp;Name\" id=\"1\">\n</model>\n");
            WbFlats flats = WbFlats.FromFiles(WbProject.ReadFlatFiles(Path.Combine(dir, "Exported")));
            WbProject.BuildModels(root, flats, null, null);
            string first = File.ReadAllText(Path.Combine(root, "Library/tool/tool.emtproj"));
            WbBuildResult r = WbProject.BuildModels(root, flats, null, null);
            Assert.AreEqual(first, File.ReadAllText(Path.Combine(root, "Library/tool/tool.emtproj")), "a second run adds no entry");
            Assert.AreEqual(0, r.Refused.Count);
            Assert.IsTrue(File.Exists(Path.Combine(root, "Library/tool/Odd&Name.model")));
        }

        [Test]
        public void UnescapeKnowsTheNamedAndTheNumericReferences()
        {
            Assert.AreEqual("a&b<c>d\"e'f", WbProject.Unescape("a&amp;b&lt;c&gt;d&quot;e&apos;f"));
            Assert.AreEqual("A&B €", WbProject.Unescape("&#65;&#x26;&#x42; &#x20AC;"));
            Assert.AreEqual("&amp;", WbProject.Unescape("&amp;amp;"), "one pass");
            Assert.AreEqual("&made; &#xZZ; &#0; x&y", WbProject.Unescape("&made; &#xZZ; &#0; x&y"), "what is no reference stays");
        }

        [Test]
        public void FileExtensionsMatchInAnyLetterCase()
        {
            string root = Path.Combine(dir, "proj");
            Put("Exported/flat/library/tool/loud.FLAT", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<model name=\"Loud\" id=\"1\">\n</model>\n");
            Put("proj/Library/tool/TOOL.EMTPROJ", ToolProjectHead + ToolProjectTail);
            List<KeyValuePair<string, byte[]>> files = WbProject.ReadFlatFiles(Path.Combine(dir, "Exported"));
            Assert.AreEqual(1, files.Count);
            Assert.AreEqual("flat/library/tool/loud.FLAT", files[0].Key);
            Assert.IsNotNull(WbFlats.FromFiles(files).Get("Loud"));
            Assert.AreEqual("Library/tool/TOOL.EMTPROJ: BASE.model", string.Join("|", WbProject.Missing(root, new List<string>()).ToArray()));
        }

        [Test]
        public void ListedFileOutsideItsFolderIsNotLookedFor()
        {
            string root = Path.Combine(dir, "proj");
            Put("proj/Library/outside.model", "made up");
            Put("proj/Library/tool/BASE.model", "made up");
            Put("proj/Library/tool/tool.emtproj", ToolProjectHead + "\t\t<File path=\"..\\outside.model\" />\r\n" + ToolProjectTail);
            Assert.AreEqual("Library/tool/tool.emtproj: ..\\outside.model", string.Join("|", WbProject.Missing(root, new List<string>()).ToArray()));
        }

        [Test]
        public void ListedPresetOutsideItsFolderIsRefusedNotKept()
        {
            string root = Path.Combine(dir, "proj");
            WriteInput(dir, root);
            Put("proj/Library/Kept.preset", "made up");
            Put("proj/Library/tool/tool.emtproj", ToolProjectHead + "\t\t<File path=\"..\\Kept.preset\" />\r\n" + ToolProjectTail);
            WbFlats flats = WbFlats.FromFiles(WbProject.ReadFlatFiles(Path.Combine(dir, "Exported")));
            WbBuildResult r = WbProject.BuildModels(root, flats, null, null);
            Assert.AreEqual(1, r.Refused.Count, string.Join("\n", r.Refused.ToArray()));
            StringAssert.Contains("Kept.preset", r.Refused[0]);
            foreach (KeyValuePair<string, int> st in r.Stats) Assert.AreNotEqual("library-preset-kept(shipped)", st.Key);
            Assert.AreEqual("made up", File.ReadAllText(Path.Combine(root, "Library/Kept.preset")));
        }

        [Test]
        public void MissingIsListedInOrdinalOrderOfTheName()
        {
            string root = Path.Combine(dir, "proj");
            foreach (string n in new[] { "delta", "Zed", "beta", "Core", "alpha", "Mid", "gamma", "Top" })
                Put("proj/Library/" + n + "/" + n + ".emtproj", ToolProjectHead + ToolProjectTail);
            var want = new List<string>();
            foreach (string n in new[] { "Core", "Mid", "Top", "Zed", "alpha", "beta", "delta", "gamma" }) want.Add("Library/" + n + "/" + n + ".emtproj: BASE.model");
            Assert.AreEqual(want.ToArray(), WbProject.Missing(root, new List<string>()).ToArray());
        }

        [Test]
        public void OfTwoNamesThatDifferInCaseTheFirstInOrdinalOrderIsTaken()
        {
            string d = Path.Combine(dir, "case");
            Directory.CreateDirectory(Path.Combine(d, "probe"));
            if (Directory.Exists(Path.Combine(d, "PROBE"))) Assert.Ignore("this file system does not keep two names that differ only in letter case");
            string[] upper = { "Ab", "Cd", "Ef", "Gh", "Ij", "Kl", "Mn", "Op", "Qr", "St" };
            foreach (string n in upper)
            {
                // made first and last in turn: a file system may list in the order of making, or against it
                bool upperFirst = Array.IndexOf(upper, n) % 2 == 0;
                Directory.CreateDirectory(Path.Combine(d, upperFirst ? n.ToUpperInvariant() : n.ToLowerInvariant()));
                Directory.CreateDirectory(Path.Combine(d, n));
                Directory.CreateDirectory(Path.Combine(d, upperFirst ? n.ToLowerInvariant() : n.ToUpperInvariant()));
            }
            var cp = new CasePaths();
            foreach (string n in upper)
            {
                string mixed = n.Substring(0, 1).ToLowerInvariant() + n.Substring(1).ToUpperInvariant();     // "aB": none of the three
                Assert.AreEqual(Path.Combine(d, n.ToUpperInvariant()), cp.Resolve(d, mixed, false));
                Assert.AreEqual(Path.Combine(d, n), cp.Resolve(d, n, false), "the exact name wins");
            }
        }

        [Test]
        public void AssetSettingsFolderIsCopied()
        {
            string game = Path.Combine(dir, "game"), res = Path.Combine(game, "Resource");
            WriteInput(game, game);
            var e = new XEntity { Id = "000000000000000000000000000000a1", Model = "Extra", Name = "one" };
            var o = new WbProjectOptions { Root = Path.Combine(dir, "proj"), LibrarySource = Path.Combine(game, "Library"), ResourceDir = res };
            o.Maps.Add(new KeyValuePair<string, byte[]>("90000001_made_up", XBlock.Bytes(new[] { e }, XDialect.Tab)));
            Directory.CreateDirectory(res);
            // not there: a line in MISSING.txt
            WbProject.Build(o, WbProject.ReadFlatFiles(Path.Combine(game, "Exported")));
            StringAssert.Contains("asset-web-config: not in the Resource folder", File.ReadAllText(Path.Combine(o.Root, "MISSING.txt")));
            // as an archive
            MakeArchive(Path.Combine(res, "asset-web-config.m2d"), "made.ini", "other.ini");
            o.Root = Path.Combine(dir, "proj2");
            List<string> log = WbProject.Build(o, WbProject.ReadFlatFiles(Path.Combine(game, "Exported")));
            Assert.AreEqual("made up made.ini", File.ReadAllText(Path.Combine(o.Root, "asset-web-config/made.ini")));
            Assert.IsTrue(File.Exists(Path.Combine(o.Root, "asset-web-config/other.ini")));
            StringAssert.Contains("asset settings in asset-web-config", string.Join("\n", log.ToArray()));
            // as a folder
            Directory.CreateDirectory(Path.Combine(res, "Asset-Web-Config"));
            File.WriteAllText(Path.Combine(res, "Asset-Web-Config/made.ini"), "from a folder");
            o.Root = Path.Combine(dir, "proj3");
            WbProject.Build(o, WbProject.ReadFlatFiles(Path.Combine(game, "Exported")));
            Assert.AreEqual("from a folder", File.ReadAllText(Path.Combine(o.Root, "asset-web-config/made.ini")));
        }

        [Test]
        public void AssetListIsReadFromTheTwoTripleFiles()
        {
            Dictionary<string, string> p = WbProject.AssetPaths(
                "<urn:uuid:aa> <http://x/llid> \"0000AAAA-0000-0000-0000-000000000000\".\n<urn:uuid:bb> <http://x/llid> \"0000bbbb-0000-0000-0000-000000000000\".\n",
                "<urn:uuid:aa> <http://x/relpath> \"/Model/made/up.nif\".\n");
            Assert.AreEqual(1, p.Count);
            Assert.AreEqual("/Model/made/up.nif", p["0000aaaa-0000-0000-0000-000000000000"]);
        }
    }
}
