// MapMusic on made-up maps: the kms client's fielddata rows and the server's and global client's map files.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    sealed class FakeMusicMaps : IDisposable
    {
        public readonly string Dir;
        public readonly DataSet Data;
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        // map 10: one environment; 20: two environments (a feature), a boss cue; 30: no bgm element anywhere
        const string FieldData = Head + "<ms2>\n" +
            "\t<fieldData id=\"10\">\n\t\t<environment>\n\t\t\t<xblock name=\"Ten\" />\n\t\t\t<bgm id=\"9\" param=\"5\" />\n\t\t</environment>\n\t</fieldData>\n" +
            "\t<fieldData id=\"20\">\n\t\t<environment>\n\t\t\t<xblock name=\"Twenty\" />\n\t\t\t<bgm id=\"9\" boss=\"23\" bossParam=\"5\" />\n\t\t</environment>\n" +
            "\t\t<environment feature=\"FakeOn\">\n\t\t\t<xblock name=\"TwentyB\" />\n\t\t\t<bgm id=\"9\" boss=\"23\" bossParam=\"5\" />\n\t\t</environment>\n\t</fieldData>\n" +
            "\t<fieldData id=\"30\">\n\t\t<environment>\n\t\t\t<xblock name=\"Thirty\" />\n\t\t</environment>\n\t</fieldData>\n" +
            "</ms2>\n";

        static string MapFile(string bgm) { return Head + "<ms2>\n\t<environment feature=\"\" locale=\"\">\n\t\t<xblock name=\"x\" />\n" + bgm + "\t</environment>\n</ms2>\n"; }

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        public FakeMusicMaps(bool server = true)
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-music-" + Guid.NewGuid().ToString("N"));
            string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
            foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);
            M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                { new KeyValuePair<string, byte[]>(FieldRows.Entry, new UTF8Encoding(false).GetBytes(FieldData)) });
            foreach (string d in server ? new[] { g, s } : new[] { g })
                M2dWriter.Create(Path.Combine(d, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                {
                    E("map/00000010.xml", MapFile("\t\t<bgm id=\"9\" param=\"5\" />\n")),
                    E("map/00000020.xml", MapFile("\t\t<bgm id=\"9\" boss=\"23\" bossParam=\"5\" />\n")),
                    E("map/00000030.xml", MapFile("")),
                });
            Data = server ? new DataSet(k, g, s) : new DataSet(k, g, null);
        }

        public static Mod NewMod() { return Mod.Parse("<mod id=\"music.test\" name=\"t\" version=\"1\" />", "test mod"); }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class MapMusicTests
    {
        const string K = "client:Xml.m2d", G = "gms-client:Xml.m2d", S = "server:Xml.m2d";

        static Plan Build(DataSet d, Mod m) { return Planner.Build(d, new[] { m }, new PlanOptions()); }

        [Test]
        public void ReadsEverySidesEnvironments()
        {
            using (var f = new FakeMusicMaps())
            {
                var mm = new MapMusic(f.Data, FakeMusicMaps.NewMod());
                List<MusicPlace> p = mm.Places(20);
                Assert.That(p.Select(x => x.Archive + " " + x.Env + " " + x.Id + "/" + x.Boss + (x.Written ? " w" : "")), Is.EqualTo(new[]
                {
                    K + " environment[feature=] 9/23 w", K + " environment[feature=FakeOn] 9/23 w",
                    S + " environment 9/23 w", G + " environment 9/23",
                }));
                Assert.That(p[0].Row, Is.EqualTo("fieldData[id=20]"));
                Assert.That(p[2].Row, Is.EqualTo(""), "a map file's root is the row");
                Assert.That(mm.Places(30).All(x => !x.HasBgm), Is.True);
                Assert.That(mm.Places(40), Is.Empty);
                Assert.That(mm.IsEdited(20), Is.False);
            }
        }

        [Test]
        public void SetsTheCueOnEverySide_AndTheMirrorCarriesItToTheGlobalClient()
        {
            using (var f = new FakeMusicMaps())
            {
                Mod m = FakeMusicMaps.NewMod();
                var mm = new MapMusic(f.Data, m);
                Assert.That(mm.Set(20, false, "15"), Is.EqualTo(3), "two kms environments and the server's");
                Assert.That(mm.Set(20, true, "034"), Is.EqualTo(3));
                Assert.That(mm.IsEdited(20), Is.True);
                Assert.That(mm.Places(20).Where(x => x.Written).Select(x => mm.Get(x, false) + "/" + mm.Get(x, true)).Distinct(), Is.EqualTo(new[] { "15/34" }));
                Assert.That(m.Ops.OfType<EditOp>().Select(e => e.Archive + " " + e.Entry), Is.EqualTo(new[] { K + " table/fielddata.xml", S + " map/00000020.xml" }));

                // through the mod file, as saved
                Mod back = Mod.Parse(m.ToXml(), "saved");
                Plan p = Build(f.Data, back);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string kms = Fake.Text(p, K, FieldRows.Entry);
                StringAssert.Contains("<xblock name=\"Twenty\" />\n\t\t\t<bgm id=\"15\" boss=\"34\" bossParam=\"5\" />", kms.Replace("\r", ""));
                StringAssert.Contains("<xblock name=\"TwentyB\" />\n\t\t\t<bgm id=\"15\" boss=\"34\" bossParam=\"5\" />", kms.Replace("\r", ""));
                StringAssert.Contains("<bgm id=\"9\" param=\"5\" />", kms, "map 10 stays");
                foreach (string side in new[] { S, G })
                    StringAssert.Contains("<bgm id=\"15\" boss=\"34\" bossParam=\"5\" />", Fake.Text(p, side, "map/00000020.xml"), side);

                // the shipped value back: no line is left
                mm.Set(20, false, "9");
                mm.Set(20, true, "23");
                Assert.That(m.Ops, Is.Empty);
            }
        }

        [Test]
        public void NoMusicUnsets_AndAMissingElementIsAdded()
        {
            using (var f = new FakeMusicMaps())
            {
                Mod m = FakeMusicMaps.NewMod();
                var mm = new MapMusic(f.Data, m);
                mm.Set(10, false, "");
                mm.Set(30, false, "42");
                mm.Set(30, true, "23");
                Assert.That(mm.Places(30).Where(x => x.Written).Select(x => mm.Get(x, false) + "/" + mm.Get(x, true)), Is.EqualTo(new[] { "42/23", "42/23" }));
                Plan p = Build(f.Data, Mod.Parse(m.ToXml(), "saved"));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string kms = Fake.Text(p, K, FieldRows.Entry).Replace("\r", "");
                StringAssert.Contains("<xblock name=\"Ten\" />\n\t\t\t<bgm param=\"5\" />", kms, "no music: the id goes");
                StringAssert.Contains("<xblock name=\"Thirty\" />\n\t\t\t<bgm id=\"42\" boss=\"23\" />", kms);
                foreach (string side in new[] { S, G })
                    StringAssert.Contains("<bgm id=\"42\" boss=\"23\" />", Fake.Text(p, side, "map/00000030.xml"), side);

                Assert.That(mm.Take(30), Is.EqualTo(2));
                Assert.That(mm.Take(10), Is.EqualTo(2));
                Assert.That(m.Ops, Is.Empty);
                Assert.Throws<ModKitException>(() => mm.Set(10, false, "Henesys"));
                Assert.Throws<ModKitException>(() => mm.Set(40, false, "9"));
            }
        }

        [Test]
        public void MapUses_ShowsTheEditedCue_PerSideAndEnvironment()
        {
            using (var f = new FakeMusicMaps())
            {
                Mod m = FakeMusicMaps.NewMod();
                new MapMusic(f.Data, m).Set(20, false, "15");
                List<CueUse> u = SoundData.MapUses(f.Data, m).Where(x => x.Map == 20).ToList();
                Assert.That(u.Where(x => !x.Boss).Select(x => x.Side + " " + x.Env + " " + x.Cue), Is.EquivalentTo(new[]
                {
                    K + "  15", K + " feature=FakeOn 15", S + "  15", G + "  15",
                }), "the gms map file shows the server's edit, as the mirror writes it");
                Assert.That(u.Where(x => x.Boss).Select(x => x.Cue).Distinct(), Is.EqualTo(new[] { 23 }), "the boss cue stays");
                Assert.That(SoundData.MapUses(f.Data, m).Where(x => x.Map == 10 && x.Cue == 9).Count(), Is.EqualTo(3), "map 10 untouched");
            }
        }

        [Test]
        public void WithoutAServer_TheGlobalClientIsWritten()
        {
            using (var f = new FakeMusicMaps(server: false))
            {
                Mod m = FakeMusicMaps.NewMod();
                var mm = new MapMusic(f.Data, m);
                Assert.That(mm.PerIdSide, Is.EqualTo(G));
                Assert.That(mm.Set(10, false, "15"), Is.EqualTo(2));
                Plan p = Build(f.Data, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                StringAssert.Contains("<bgm id=\"15\" param=\"5\" />", Fake.Text(p, G, "map/00000010.xml"));
            }
        }
    }
}
