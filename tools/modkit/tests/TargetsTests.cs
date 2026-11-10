// The entity editors on every mix of targets, on made-up data: a kms-client with item bundles, a gms-client and a
// server with one file per item, each with string files in its own languages.
using System;
using System.Collections.Generic;
using System.IO;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    public sealed class FakeSides : IDisposable
    {
        public readonly string Dir;
        public readonly DataSet Data;
        public readonly Mod Mod;
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        static string Names(string suffix)
        {
            return Head + "<ms2>\n\t<key id=\"1200001\" class=\"a\" name=\"Tin Whistle" + suffix + "\" />\n\t<key id=\"1200002\" class=\"a\" name=\"Clay Drum" + suffix + "\" />\n</ms2>\n";
        }

        static string File(int price) { return Head + "<ms2>\n\t<environment>\n\t\t<property slotMax=\"1\" sellPrice=\"" + price + "\" />\n\t</environment>\n</ms2>\n"; }

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        /// <summary>Languages: kms-client en and zz, gms-client en, server en and es (null: no such target).</summary>
        public FakeSides(bool kms, bool gms, bool server)
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-targets-" + Guid.NewGuid().ToString("N"));
            string k = null, g = null, s = null;
            if (kms)
            {
                k = Path.Combine(Dir, "kms"); Directory.CreateDirectory(k);
                M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                {
                    E("itemdata/012.xml", Head + "<ms2>\n\t<item id=\"1200001\">\n\t\t<environment>\n\t\t\t<property slotMax=\"1\" sellPrice=\"40\" />\n\t\t</environment>\n\t</item>\n" +
                        "\t<item id=\"1200002\">\n\t\t<environment>\n\t\t\t<property slotMax=\"1\" sellPrice=\"9\" />\n\t\t</environment>\n\t</item>\n</ms2>\n"),
                    E("string/en/itemname.xml", Names("")),
                    E("string/zz/itemname.xml", Names(" (zz)")),
                });
            }
            if (gms)
            {
                g = Path.Combine(Dir, "gms"); Directory.CreateDirectory(g);
                M2dWriter.Create(Path.Combine(g, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                {
                    E("item/0/12/01200001.xml", File(40)), E("item/0/12/01200002.xml", File(9)),
                    E("string/en/itemname.xml", Names("")),
                });
            }
            if (server)
            {
                s = Path.Combine(Dir, "server"); Directory.CreateDirectory(s);
                M2dWriter.Create(Path.Combine(s, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                {
                    E("item/0/12/01200001.xml", File(40)), E("item/0/12/01200002.xml", File(9)),
                    E("string/en/itemname.xml", Names("")),
                    E("string/es/itemname.xml", Names(" (es)")),
                });
            }
            Data = new DataSet(k, g, s);
            Mod = Mod.Parse("<mod id=\"targets.test\" name=\"t\" version=\"1\"/>", "test mod");
        }

        public ItemEditor Editor() { return new ItemEditor(Data, Mod) { Maps = FakeItems.Maps }; }

        public Plan Plan()
        {
            Mod again = Mod.Parse(Mod.ToXml(), "round trip");
            return Planner.Build(Data, new[] { again }, new PlanOptions { Maps = FakeItems.Maps });
        }

        public static List<string> Kinds(EntityDraft d)
        {
            var l = new List<string>();
            foreach (EntityPart p in d.Parts) l.Add(p.Kind + (p.Language != null ? ":" + p.Language : "") + "@" + p.Target);
            return l;
        }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class TargetsTests
    {
        const string K = EntityEditor.ClientXml, G = EntityEditor.GmsXml, S = EntityEditor.ServerXml;

        static EntitySection Prop(EntityDraft d, string kind)
        {
            foreach (EntitySection s in d.Sections) if (s.Part.Kind == kind && s.Elem.Name == "property") return s;
            throw new AssertionException("no property of " + kind);
        }

        [Test]
        public void GmsAndServerWithoutTheKoreanClient()
        {
            using (var f = new FakeSides(false, true, true))
            {
                ItemEditor ed = f.Editor();
                Assert.That(ed.BundleXml, Is.Null);
                Assert.That(ed.FileXml, Is.EqualTo(S), "the server is edited, the gms-client gets the mirror");
                Assert.That(ed.TwoSided, Is.False);
                Assert.That(ed.Languages, Is.EqualTo(new[] { "en", "es" }), "the languages are the server's own");
                Assert.That(ed.Find("drum", 9)[0].Id, Is.EqualTo(1200002));

                ItemDraft d = ed.Open(1200001);
                Assert.That(FakeSides.Kinds(d), Is.EqualTo(new[] { "name:en@server", "name:es@server", "file@server" }));
                Assert.That(d.Set(Prop(d, "file"), "sellPrice", "55", true), Is.Null, "no other side to sync");
                Assert.That(d.SetName("Brass Whistle"), Is.EqualTo(2));

                ItemDraft n = ed.Clone(1200001, 1200050, "Glass Whistle", null);
                Assert.That(n.IsNew, Is.True);
                Assert.That(FakeSides.Kinds(n), Is.EqualTo(new[] { "name:en@server", "name:es@server", "file@server" }));

                Plan p = f.Plan();
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(p.Warnings.Count, Is.EqualTo(1), dump);
                Assert.That(p.Warnings[0].Archive + " " + p.Warnings[0].Text, Does.Contain("client:Xml.m2d is not in the data set"), "the one warning: the kms-client is not written");
                foreach (string a in new[] { S, G })
                {
                    Assert.That(Fake.Text(p, a, "item/0/12/01200001.xml"), Does.Contain("sellPrice=\"55\""), a);
                    Assert.That(Fake.Text(p, a, "item/0/12/01200050.xml"), Does.Contain("sellPrice=\"40\""), a);
                    Assert.That(Fake.Text(p, a, "string/en/itemname.xml"), Does.Contain("name=\"Brass Whistle\"").And.Contain("id=\"1200050\" class=\"a\" name=\"Glass Whistle\""), a);
                }
                Assert.That(Fake.Text(p, S, "string/es/itemname.xml"), Does.Contain("name=\"Glass Whistle\""));
                Assert.That(dump, Does.Contain("gms-client ships no string/es/itemname.xml"), "a language the gms-client lacks is a note");

                ed.RemoveClone(1200050);
                Assert.That(f.Mod.ToXml(), Does.Not.Contain("1200050"));
            }
        }

        [Test]
        public void SyncReportAndRefsWithoutTheKoreanClient()
        {
            using (var f = new FakeSides(false, true, true))
            {
                EntityMap em = FakeItems.Maps.Entity("item");
                SyncReport r = SyncReport.ForId(f.Data, null, em, 1200001);
                Assert.That(r.ServerShipped, Is.True);
                Assert.That(r.ClientShipped, Is.False);
                Assert.That(string.Join("|", r.Notes.ToArray()), Does.Contain("is not in the data set"));
                RefList refs = ServerRefs.Find(f.Data, em, 1200001, 5);
                Assert.That(refs, Is.Not.Null);
            }
        }

        [Test]
        public void ServerOnly()
        {
            using (var f = new FakeSides(false, false, true))
            {
                ItemEditor ed = f.Editor();
                Assert.That(ed.HasServer, Is.True);
                ItemDraft d = ed.Open(1200002);
                d.Set(Prop(d, "file"), "slotMax", "7");
                ItemDraft n = ed.Clone(1200002, ed.NextFreeId(1200002, 0), null, null);
                Assert.That(n.Id, Is.EqualTo(1200003));
                Plan p = f.Plan();
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Fake.Text(p, S, "item/0/12/01200002.xml"), Does.Contain("slotMax=\"7\""));
                Assert.That(Fake.Text(p, S, "item/0/12/01200003.xml"), Does.Contain("slotMax=\"1\""));
                Assert.That(f.Mod.ToXml(), Does.Contain("item/*/*/{id}.xml").And.Not.Contain("itemdata"), "the reservation names the server place only");
            }
        }

        [Test]
        public void GmsClientOnly()
        {
            using (var f = new FakeSides(false, true, false))
            {
                ItemEditor ed = f.Editor();
                Assert.That(ed.FileXml, Is.EqualTo(G));
                Assert.That(ed.HasServer, Is.False);
                Assert.That(ed.Languages, Is.EqualTo(new[] { "en" }));
                ed.Open(1200001).SetName("Brass Whistle");
                Plan p = f.Plan();
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Fake.Text(p, G, "string/en/itemname.xml"), Does.Contain("Brass Whistle"));
            }
        }

        [Test]
        public void AllThreeOneWritePathAndLanguagesPerTarget()
        {
            using (var f = new FakeSides(true, true, true))
            {
                ItemEditor ed = f.Editor();
                Assert.That(ed.Languages, Is.EqualTo(new[] { "en", "zz" }), "names are searched in the kms-client's languages");
                Assert.That(ed.LanguagesIn(S), Is.EqualTo(new[] { "en", "es" }));
                Assert.That(ed.LanguagesIn(G), Is.EqualTo(new[] { "en" }));
                ItemDraft d = ed.Open(1200001);
                Assert.That(FakeSides.Kinds(d), Is.EqualTo(new[] { "item@client", "name:en@client", "name:zz@client", "name:en@server", "name:es@server", "file@server" }));

                // the edit is written on the kms-client row only; the plan carries it to the server and the gms-client
                EntitySection other = d.Set(Prop(d, "item"), "sellPrice", "55", true);
                Assert.That(other.Part.Kind, Is.EqualTo("file"));
                Assert.That(f.Mod.ToXml(), Does.Not.Contain("server:Xml.m2d"), f.Mod.ToXml());
                Assert.That(d.SetName("Brass Whistle"), Is.EqualTo(4));
                ed.Clone(1200002, 1200060, "Bone Drum", null);

                Plan p = f.Plan();
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(p.Warnings, Is.Empty, dump);
                Assert.That(Fake.Text(p, K, "itemdata/012.xml"), Does.Contain("sellPrice=\"55\"").And.Contain("<item id=\"1200060\">"));
                foreach (string a in new[] { S, G })
                {
                    Assert.That(Fake.Text(p, a, "item/0/12/01200001.xml"), Does.Contain("sellPrice=\"55\""), a);
                    Assert.That(Fake.Text(p, a, "item/0/12/01200060.xml"), Does.Contain("sellPrice=\"9\""), a);
                }
                foreach (string a in new[] { K, S, G })
                    Assert.That(Fake.Text(p, a, "string/en/itemname.xml"), Does.Contain("name=\"Brass Whistle\"").And.Contain("name=\"Bone Drum\""), a);
                Assert.That(Fake.Text(p, K, "string/zz/itemname.xml"), Does.Contain("Bone Drum"));
                Assert.That(Fake.Text(p, S, "string/es/itemname.xml"), Does.Contain("Bone Drum"));
            }
        }

        [Test]
        public void LanguagesFallBackToTheProfile()
        {
            using (var f = new FakeSides(true, false, false))
            {
                ItemEditor ed = f.Editor();
                Assert.That(ed.LanguagesIn(S), Is.EqualTo(new List<string>(Profile.Server.Languages)), "no server data: the profile's list");
            }
        }
    }
}
