// The npc editor on made-up data: a kms-client with npc bundles, a gms-client and a server with one file per npc,
// string files with 8-digit ids and the per-npc emotion and script files.
using System;
using System.Collections.Generic;
using System.IO;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    public sealed class FakeNpcs : IDisposable
    {
        public readonly string Dir;
        public readonly DataSet Data;
        public readonly Mod Mod;
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        const string MapXml = "<fieldmaps version=\"1\"><entity name=\"npc\">" +
            "<client archive=\"client:Xml.m2d\" entry=\"npcdata/{id/100000}.xml\" row=\"npc\" key=\"id\" under=\"environment\" />" +
            "<server archive=\"server:Xml.m2d\" entry=\"npc/*/*/{id}.xml\" under=\"environment\" />" +
            "<map client=\"@level\" server=\"basic@level\" />" +
            "<map client=\"stat@hp\" server=\"stat@hp\" />" +
            "<map client=\"stat@pap\" server=\"stat@pap\" />" +
            "<map client=\"model@kfm\" server=\"model@kfm\" />" +
            "<map client=\"@customExp\" server=\"exp@customExp\" default=\"-1\" />" +
            "<serverOnly field=\"basic@difficulty\" />" +
            "</entity></fieldmaps>";

        public static readonly FieldMaps Maps = FieldMaps.Parse(MapXml);

        static string Names(string file, string suffix)
        {
            // the zz title file has no row of 2900001: shipped npcs often have title rows in a few languages only
            if (file == "npctitle.xml" && suffix == " (zz)") return Head + "<ms2>\n\t<key id=\"02900009\" name=\"Other (zz)\" />\n</ms2>\n";
            if (file == "npctitle.xml")
                return Head + "<ms2>\n\t<key id=\"02900001\" name=\"Digger" + suffix + "\" />\n\t<key id=\"02900001\" name=\"Digger" + suffix + "\" feature=\"F1\" />\n</ms2>\n";
            string a = file == "npcnameplural.xml" ? "Fake Moles" : "Fake Mole", b = file == "npcnameplural.xml" ? "Clay Guards" : "Clay Guard";
            return Head + "<ms2>\n\t<key id=\"02900001\" name=\"" + a + suffix + "\" />\n\t<key id=\"02900002\" name=\"" + b + suffix + "\" />\n</ms2>\n";
        }

        // level 0: the row has no level attribute (the client's default), as some shipped rows have it
        static string Row(long id, int level, int hp) { return "\t<npc id=\"" + id + "\">\n\t\t<environment" + (level == 0 ? " hpBar=\"1\"" : " level=\"" + level + "\"") + ">\n\t\t\t<model kfm=\"Fake_Kfm_" + id + "\" />\n\t\t\t<stat hp=\"" + hp + "\" pap=\"3\" />\n\t\t</environment>\n\t</npc>\n"; }

        static string File(long id, int level, int difficulty, int hp)
        {
            return Head + "<ms2>\n\t<environment feature=\"\" locale=\"\">\n\t\t<model kfm=\"Fake_Kfm_" + id + "\" />\n\t\t<basic level=\"" + level + "\" difficulty=\"" + difficulty + "\" />\n" +
                "\t\t<stat hp=\"" + hp + "\" hp_rgp=\"0\" pap=\"3\" />\n\t\t<exp customExp=\"-1\" />\n\t</environment>\n</ms2>\n";
        }

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        static void Strings(List<KeyValuePair<string, byte[]>> l, params string[] langs)
        {
            foreach (string lang in langs)
                foreach (string f in new[] { "npcname.xml", "npcnameplural.xml", "npctitle.xml" })
                    l.Add(E("string/" + lang + "/" + f, Names(f, lang == "en" ? "" : " (" + lang + ")")));
        }

        const string Emotion = Head + "<ms2>\n\t<emotion type=\"1\" name=\"default\" />\n</ms2>\n";
        const string Script = Head + "<ms2>\n\t<talk id=\"1\" />\n</ms2>\n";

        /// <summary>Languages: kms-client en and zz, gms-client en, server en and es.</summary>
        public FakeNpcs(bool kms, bool gms, bool server)
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-npcs-" + Guid.NewGuid().ToString("N"));
            string k = null, g = null, s = null;
            if (kms)
            {
                k = Path.Combine(Dir, "kms"); Directory.CreateDirectory(k);
                var l = new List<KeyValuePair<string, byte[]>> { E("npcdata/029.xml", Head + "<ms2>\n" + Row(2900001, 5, 100) + Row(2900002, 0, 40) + "</ms2>\n"), E("emotion/npc/02900001.xml", Emotion) };
                Strings(l, "en", "zz");
                M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, l);
            }
            foreach (bool server2 in new[] { false, true })
            {
                if (server2 ? !server : !gms) continue;
                string dir = Path.Combine(Dir, server2 ? "server" : "gms"); Directory.CreateDirectory(dir);
                if (server2) s = dir; else g = dir;
                var l = new List<KeyValuePair<string, byte[]>>
                {
                    E("npc/02/90/02900001.xml", File(2900001, 5, 5, 100)), E("npc/02/90/02900002.xml", File(2900002, 7, 0, 40)),
                    E("emotion/npc/02900001.xml", Emotion), E("script/npc/02900001.xml", Script),
                };
                // an emotion file of an id that has no npc, as shipped data has some
                if (!server2) l.Add(E("emotion/npc/02900060.xml", Emotion));
                if (server2) Strings(l, "en", "es"); else Strings(l, "en");
                M2dWriter.Create(Path.Combine(dir, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, l);
            }
            Data = new DataSet(k, g, s);
            Mod = Mod.Parse("<mod id=\"npcs.test\" name=\"t\" version=\"1\"/>", "test mod");
        }

        public NpcEditor Editor() { return new NpcEditor(Data, Mod) { Maps = Maps }; }

        public Plan Plan()
        {
            Mod again = Mod.Parse(Mod.ToXml(), "round trip");
            return Planner.Build(Data, new[] { again }, new PlanOptions { Maps = Maps });
        }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class NpcsTests
    {
        const string K = EntityEditor.ClientXml, G = EntityEditor.GmsXml, S = EntityEditor.ServerXml;

        [Test]
        public void EditAndCloneOnAllThreeTargets()
        {
            using (var f = new FakeNpcs(true, true, true))
            {
                NpcEditor ed = f.Editor();
                Assert.That(ed.Find("mole", 5)[0].Id, Is.EqualTo(2900001));
                Assert.That(ed.Find("2900002", 5)[0].Id, Is.EqualTo(2900002));
                NpcDraft d = ed.Open(2900001);
                Assert.That(d.Name, Is.EqualTo("Fake Mole"));
                Assert.That(d.Title, Is.EqualTo("Digger"));
                Assert.That(d.Level, Is.EqualTo("5"));
                Assert.That(d.Hp, Is.EqualTo("100"));
                Assert.That(d.Model, Is.EqualTo("Fake_Kfm_2900001"));
                d.SetLevel(9);
                d.SetHp(500);
                d.SetName("Fake Mole King");
                Assert.That(d.Level, Is.EqualTo("9"));
                Assert.That(d.GetFieldShipped("@level"), Is.EqualTo("5"), "the shipped value stays readable for the form");

                NpcDraft n = ed.Clone(2900001, 2900050, "Mole Chief", 12, 900);
                Assert.That(n.IsNew, Is.True);
                n.SetModel("Fake_Kfm_Other");
                n.SetName("zz", "Mole Chief (zz)");

                Plan p = f.Plan();
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(p.Warnings, Is.Empty, dump);

                string bundle = Fake.Text(p, K, "npcdata/029.xml");
                Assert.That(bundle, Does.Contain("<npc id=\"2900050\">"));
                Assert.That(bundle, Does.Contain("level=\"9\"").And.Contain("hp=\"500\"").And.Contain("level=\"12\"").And.Contain("hp=\"900\"").And.Contain("Fake_Kfm_Other"));
                foreach (string a in new[] { S, G })
                {
                    string own = Fake.Text(p, a, "npc/02/90/02900001.xml");
                    Assert.That(own, Does.Contain("level=\"9\" difficulty=\"9\"").And.Contain("hp=\"500\""), a);
                    string clone = Fake.Text(p, a, "npc/02/90/02900050.xml");
                    Assert.That(clone, Does.Contain("level=\"12\" difficulty=\"12\"").And.Contain("hp=\"900\"").And.Contain("Fake_Kfm_Other"), a);
                    Assert.That(Fake.Text(p, a, "script/npc/02900050.xml"), Does.Contain("<talk id=\"1\""), a);
                }
                foreach (string a in new[] { K, S, G })
                {
                    Assert.That(Fake.Text(p, a, "emotion/npc/02900050.xml"), Does.Contain("<emotion"), a);
                    Assert.That(Fake.Text(p, a, "string/en/npcname.xml"), Does.Contain("<key id=\"02900050\" name=\"Mole Chief\" />").And.Contain("name=\"Fake Mole King\""), a);
                    Assert.That(Fake.Text(p, a, "string/en/npctitle.xml"), Does.Contain("<key id=\"02900050\" name=\"Digger\" feature=\"F1\" />"), a);
                }
                Assert.That(Fake.Text(p, K, "string/zz/npcname.xml"), Does.Contain("name=\"Mole Chief (zz)\""));
                Assert.That(Fake.Text(p, S, "string/es/npcname.xml"), Does.Contain("<key id=\"02900050\" name=\"Mole Chief\" />"));
                Assert.That(Fake.Text(p, K, "npcdata/029.xml").Split(new[] { "id=\"2900050\"" }, StringSplitOptions.None).Length, Is.EqualTo(2), "one row");

                List<string> reg = ed.Registration(2900050);
                Assert.That(string.Join("\n", reg.ToArray()), Does.Contain("npcdata/029.xml").And.Contain("npc/02/90/02900050.xml").And.Contain("emotion/npc/02900050.xml").And.Contain("02900050"));
            }
        }

        [Test]
        public void DifficultyFollowsOnlyWhereItEqualledTheLevel()
        {
            using (var f = new FakeNpcs(true, true, true))
            {
                f.Editor().Open(2900002).SetLevel(20);
                Plan p = f.Plan();
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Fake.Text(p, S, "npc/02/90/02900002.xml"), Does.Contain("level=\"20\" difficulty=\"0\""));
            }
        }

        [Test]
        public void ALevelTheClientRowLacksIsReadFromTheFileAndWrittenToBoth()
        {
            using (var f = new FakeNpcs(true, true, true))
            {
                NpcDraft d = f.Editor().Open(2900002);
                Assert.That(d.Level, Is.EqualTo("7"), "the server's basic@level");
                Assert.That(d.GetFieldShipped("@level"), Is.EqualTo("7"));
                d.SetLevel(11);
                Plan p = f.Plan();
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Fake.Text(p, K, "npcdata/029.xml"), Does.Contain("<environment hpBar=\"1\" level=\"11\">"));
                foreach (string a in new[] { S, G }) Assert.That(Fake.Text(p, a, "npc/02/90/02900002.xml"), Does.Contain("level=\"11\" difficulty=\"0\""), a);
            }
        }

        [Test]
        public void ATitleIsAddedWhereThereIsNone()
        {
            using (var f = new FakeNpcs(true, false, true))
            {
                NpcDraft d = f.Editor().Open(2900002);
                Assert.That(d.Title, Is.Null);
                Assert.That(d.SetTitle("Gate Keeper"), Is.EqualTo(4), "kms en, zz; server en, es");
                Plan p = f.Plan();
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Fake.Text(p, S, "string/es/npctitle.xml"), Does.Contain("<key id=\"02900002\" name=\"Gate Keeper\" />"));
            }
        }

        [Test]
        public void ATitleIsAddedInTheLanguagesThatLackOne()
        {
            using (var f = new FakeNpcs(true, true, true))
            {
                NpcEditor ed = f.Editor();
                NpcDraft d = ed.Clone(2900001, 2900051, null, 0, 0);
                Assert.That(d.SetTitle("Boss"), Is.EqualTo(7), "kms en 2 rows, zz 1 new; server en 2, es 2");
                Plan p = f.Plan();
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Fake.Text(p, K, "string/zz/npctitle.xml"), Does.Contain("<key id=\"02900051\" name=\"Boss\" />"));
                Assert.That(Fake.Text(p, K, "string/en/npctitle.xml"), Does.Contain("<key id=\"02900051\" name=\"Boss\" feature=\"F1\" />"));
                Assert.That(Fake.Text(p, G, "string/en/npctitle.xml"), Does.Contain("<key id=\"02900051\" name=\"Boss\" />"), "mirrored");
                Assert.That(ed.Open(2900051).SetTitle("Boss"), Is.EqualTo(7), "no second row");
                Assert.That(string.Join("\n", ed.Registration(2900051).ToArray()), Does.Contain("gms-client:Xml.m2d  the server's lines above, mirrored"));
            }
        }

        [Test]
        public void AnIdWithAShippedFileOfNoNpcIsNotFree()
        {
            using (var f = new FakeNpcs(true, true, true))
            {
                NpcEditor ed = f.Editor();
                Assert.That(ed.Exists(2900060), Is.False);
                Assert.That(ed.ShippedCompanion(2900060), Is.EqualTo("gms-client:Xml.m2d emotion/npc/02900060.xml"));
                Assert.That(ed.NextFreeId(2900001, 2900060), Is.EqualTo(2900061));
                var ex = Assert.Throws<ModKitException>(() => ed.Clone(2900001, 2900060, null, 0, 0));
                Assert.That(ex.Message, Does.Contain("ships already"));
            }
        }

        [Test]
        public void ServerAndGmsWithoutTheKoreanClient()
        {
            using (var f = new FakeNpcs(false, true, true))
            {
                NpcEditor ed = f.Editor();
                NpcDraft d = ed.Open(2900001);
                Assert.That(d.Level, Is.EqualTo("5"), "the level from the server's basic@level");
                d.SetLevel(8);
                Assert.That(d.GetField("@level") + "/" + d.GetFieldShipped("@level"), Is.EqualTo("8/5"), "both from the server's file");
                ed.Clone(2900002, 2900003, "Clay Guard Two", 0, 77);
                Plan p = f.Plan();
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(p.Warnings.Count, Is.EqualTo(1), dump);
                foreach (string a in new[] { S, G })
                {
                    Assert.That(Fake.Text(p, a, "npc/02/90/02900001.xml"), Does.Contain("level=\"8\" difficulty=\"8\""), a);
                    Assert.That(Fake.Text(p, a, "npc/02/90/02900003.xml"), Does.Contain("hp=\"77\"").And.Contain("level=\"7\" difficulty=\"0\""), a);
                    Assert.That(Fake.Text(p, a, "string/en/npcname.xml"), Does.Contain("<key id=\"02900003\" name=\"Clay Guard Two\" />"), a);
                }
                Assert.That(dump, Does.Not.Contain("emotion/npc/02900003.xml"), "the source has no emotion file");
            }
        }

        [Test]
        public void RemoveCloneTakesEverythingBack()
        {
            using (var f = new FakeNpcs(true, true, true))
            {
                NpcEditor ed = f.Editor();
                string before = f.Mod.ToXml();
                ed.Clone(2900001, 2900050, "Mole Chief", 12, 900);
                Assert.That(f.Mod.ToXml(), Does.Contain("emotion/npc/02900050.xml").And.Contain("script/npc/02900050.xml"));
                ed.RemoveClone(2900050);
                Assert.That(f.Mod.ToXml(), Is.EqualTo(before));
            }
        }

        [Test]
        public void CloneOutsideTheBlockIsRefused()
        {
            using (var f = new FakeNpcs(true, false, true))
            {
                var ex = Assert.Throws<ModKitException>(() => f.Editor().Clone(2900001, 3000001, null, 0, 0));
                Assert.That(ex.Message, Does.Contain("block"));
            }
        }
    }
}
