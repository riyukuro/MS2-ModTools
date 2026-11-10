// Data profiles on made-up data: a Korean client (bundles, table/kr), a global client (one file per item,
// table/na) and a server (the global layout plus a server-only attribute and table), in one data set.
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
    public sealed class FakeTargets : IDisposable
    {
        public readonly string Dir, KmsDir, GmsDir, ServerDir;
        public DataSet Data;

        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        public static string Item(int price, bool server)
        {
            return Head + "<ms2>\n\t<environment>\n\t\t<property slotMax=\"3\" sellPrice=\"" + price + "\" />\n" +
                (server ? "\t\t<drop rate=\"4\" />\n" : "") + "\t</environment>\n</ms2>\n";
        }

        static string Region(string v) { return Head + "<ms2>\n\t<region code=\"1\" value=\"" + v + "\" />\n</ms2>\n"; }
        static string Names(string n) { return Head + "<ms2>\n\t<key id=\"1200005\" name=\"" + n + "\" />\n</ms2>\n"; }

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        public FakeTargets(bool kms = true, bool gms = true, bool server = true)
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-targets-" + Guid.NewGuid().ToString("N"));
            KmsDir = Path.Combine(Dir, "orig", "kms");
            GmsDir = Path.Combine(Dir, "orig", "gms");
            ServerDir = Path.Combine(Dir, "orig", "server");
            Directory.CreateDirectory(KmsDir); Directory.CreateDirectory(GmsDir); Directory.CreateDirectory(ServerDir);
            M2dWriter.Create(Path.Combine(KmsDir, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E("itemdata/012.xml", FakeItems.Bucket),
                E("string/kr/itemname.xml", Names("Whistle (kr)")),
                E("table/kr/region.xml", Region("kr")),
            });
            M2dWriter.Create(Path.Combine(GmsDir, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E("item/0/12/01200001.xml", Item(40, false)),
                E("item/0/12/01200005.xml", Item(7, false)),
                E("string/en/itemname.xml", Names("Whistle")),
                E("table/na/region.xml", Region("na")),
            });
            M2dWriter.Create(Path.Combine(ServerDir, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E("item/0/12/01200001.xml", Item(40, true)),
                E("item/0/12/01200005.xml", Item(7, true)),
                E("string/en/itemname.xml", Names("Whistle")),
                E("table/na/region.xml", Region("na")),
                E("table/bags.xml", Head + "<ms2>\n\t<bag id=\"1\" item=\"1200005\" />\n</ms2>\n"),
            });
            Data = new DataSet(kms ? KmsDir : null, gms ? GmsDir : null, server ? ServerDir : null);
        }

        public Mod Mod(string id, string body)
        {
            string d = Path.Combine(Dir, "mods", id);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "mod.xml"), "<mod id=\"" + id + "\" name=\"test\" version=\"1\">\n" + body + "\n</mod>\n", new UTF8Encoding(false));
            return MS2.ModKit.Mod.Load(d);
        }

        public Plan Plan(params Mod[] mods) { return Planner.Build(Data, mods, new PlanOptions { Maps = FakeItems.Maps }); }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class ProfileTests
    {
        const string K = "client:Xml.m2d", G = "gms-client:Xml.m2d", S = "server:Xml.m2d";
        const string Bucket = "itemdata/012.xml", Five = "item/0/12/01200005.xml";

        static string Edit(string archive, string entry, string at, string name, string value)
        {
            return "<edit archive=\"" + archive + "\" entry=\"" + entry + "\"><change at=\"" + at + "\"><set name=\"" + name + "\" value=\"" + value + "\"/></change></edit>";
        }

        static PlanChange Change(Plan p, string archive, string entry, string name)
        {
            return p.Changes.FirstOrDefault(c => c.Archive == archive && c.Entry == entry && c.Name == name);
        }

        [Test]
        public void ProfilesAndTheirRoots()
        {
            Assert.That(Profile.Find("kms-client"), Is.SameAs(Profile.KmsClient));
            Assert.That(Profile.Find("client"), Is.SameAs(Profile.KmsClient));
            Assert.That(Profile.Find("gms"), Is.SameAs(Profile.GmsClient));
            Assert.That(Profile.Find("server"), Is.SameAs(Profile.Server));
            Assert.That(Profile.Find("other"), Is.Null);
            Assert.That(DataSet.NormalizeRef("kms-client:Xml.m2d"), Is.EqualTo(K));
            Assert.That(DataSet.NormalizeRef("gms:Xml.m2d"), Is.EqualTo(G));
            Assert.That(Profile.OfArchive(G), Is.SameAs(Profile.GmsClient));

            // the region folder and the languages come from the profile
            Assert.That(Profile.KmsClient.EntryIn("table/kr/region.xml", Profile.GmsClient), Is.EqualTo("table/na/region.xml"));
            Assert.That(Profile.GmsClient.EntryIn("table/na/region.xml", Profile.KmsClient), Is.EqualTo("table/kr/region.xml"));
            Assert.That(Profile.GmsClient.EntryIn("table/na/region.xml", Profile.Server), Is.EqualTo("table/na/region.xml"));
            Assert.That(Profile.KmsClient.EntryIn(Bucket, Profile.Server), Is.EqualTo(Bucket));
            Assert.That(Profile.LanguageOf("string/kr/itemname.xml"), Is.EqualTo("kr"));
            Assert.That(Profile.KmsClient.ReadsLanguage("kr"), Is.True);
            Assert.That(Profile.GmsClient.ReadsLanguage("kr"), Is.False);
            Assert.That(Profile.Server.ReadsLanguage("en"), Is.True);
            Assert.That(Profile.GmsClient.Mirrors(Profile.Server), Is.True);
            Assert.That(Profile.KmsClient.Mirrors(Profile.Server), Is.False);

            using (var f = new FakeTargets())
                Assert.That(f.Data.Profiles, Is.EqualTo(new[] { Profile.KmsClient, Profile.GmsClient, Profile.Server }));
            using (var f = new FakeTargets(kms: false))
                Assert.That(f.Data.Profiles, Is.EqualTo(new[] { Profile.GmsClient, Profile.Server }));
        }

        [Test]
        public void EachEntityHasALayoutPerProfile()
        {
            EntityLayout kn = Profile.KmsClient.Layout("npc"), sn = Profile.Server.Layout("npc"), gn = Profile.GmsClient.Layout("npc");
            Assert.That(kn.Bundle, Is.True);
            Assert.That(kn.Row, Is.EqualTo("npc"));
            Assert.That(sn.Bundle, Is.False);
            Assert.That(sn.SameShape(gn), Is.True);
            Assert.That(sn.SameShape(kn), Is.False);
            Assert.That(Profile.KmsClient.Layout("item").SameShape(kn), Is.False, "another entity");
            Assert.That(Profile.Server.Layout("skill"), Is.Null);

            // where a new id goes
            Assert.That(kn.EntryOf(1234567), Is.EqualTo("npcdata/012.xml"));
            Assert.That(sn.EntryOf(12345678), Is.EqualTo("npc/12/34/12345678.xml"));
            Assert.That(sn.EntryOf(345), Is.EqualTo("npc/00/00/00000345.xml"));
            Assert.That(Profile.Server.Layout("item").EntryOf(12345678), Is.EqualTo("item/1/23/12345678.xml"));
            long id;
            Assert.That(sn.Match("npc/12/34/12345678.xml", out id) && id == 12345678, Is.True);
            Assert.That(kn.Match("npcdata/012.xml", out id) && id == 12, Is.True);

            Assert.That(Profile.KmsClient.LayoutText(), Is.EqualTo("item, npc bundles"));
            Assert.That(Profile.Server.LayoutText(), Is.EqualTo("item, npc one file per id"));
            Assert.That(Profile.GmsClient.SameLayout(Profile.Server), Is.True);
            Assert.That(Profile.KmsClient.SameLayout(Profile.GmsClient), Is.False);

            // a field map side stands for every profile with that layout
            EntityMap npc = FieldMaps.Default.Entity("npc");
            Assert.That(Profile.GmsClient.Holds(npc, npc.Server), Is.True);
            Assert.That(Profile.KmsClient.Holds(npc, npc.Client), Is.True);
            Assert.That(Profile.KmsClient.Holds(npc, npc.Server), Is.False);
            bool client;
            Assert.That(FieldMaps.Default.Find("gms-client:Xml.m2d", "npc/12/34/12345678.xml", out client), Is.SameAs(npc));
            Assert.That(client, Is.False);
            Assert.That(FieldMaps.Default.Find("gms-client:Xml.m2d", "npcdata/012.xml", out client), Is.Null, "the gms-client has no bundle");
            Assert.That(FieldMaps.Default.Find("server:Server.m2d", "npc/12/34/12345678.xml", out client), Is.Null, "another archive");
        }

        [Test]
        public void AnOldPairReadsAsKmsClientAndServer()
        {
            // an old data set (client + server) and an old mod naming "client:" and "server:"
            using (var f = new Fake())
            {
                Assert.That(f.Data.Profiles, Is.EqualTo(new[] { Profile.KmsClient, Profile.Server }));
                Assert.That(f.Data.Has(Profile.GmsClient), Is.False);
                var ws = new Workspace(Path.Combine(f.Dir, "ws"));
                Mod a = f.Mod("a", "<edit archive=\"kms-client:Xml.m2d\" entry=\"table/pet.xml\"><change at=\"pet[id=100]\"><set name=\"hp\" value=\"65\"/></change></edit>");
                ApplyResult r = ws.Apply(f.Data, new[] { a }, new ApplyOptions { VerifyAll = true });
                Assert.That(r.Applied, Is.True, Fake.Dump(r.Plan));
                Assert.That(r.Outputs[0].Path, Is.EqualTo(Path.Combine(f.Dir, "ws", "client", "Xml.m2d")));
                Assert.That(ws.ReadState().Archives.Select(x => x.File).ToArray(), Does.Contain("client/Xml.m2d"));
                ws.Revert(f.Data, null, null);
                Assert.That(ws.ReadState(), Is.Null);
            }
        }

        [Test]
        public void AServerLineIsMirroredToTheGlobalClientAndSyncedToTheKoreanOne()
        {
            using (var f = new FakeTargets())
            {
                Plan p = f.Plan(f.Mod("m", Edit(S, Five, "environment/property", "sellPrice", "99")));
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(p.Warnings, Is.Empty, dump);
                Assert.That(Change(p, S, Five, "sellPrice").NewValue, Is.EqualTo("99"), dump);
                PlanChange g = Change(p, G, Five, "sellPrice");
                Assert.That(g, Is.Not.Null, dump);
                Assert.That(g.Mirrored, Is.True);
                Assert.That(g.MirroredFrom, Is.EqualTo(S));
                PlanChange k = Change(p, K, Bucket, "sellPrice");
                Assert.That(k, Is.Not.Null, dump);
                Assert.That(k.Synced, Is.True);
                Assert.That(k.NewValue, Is.EqualTo("99"));
                Assert.That(FakeTargets.Item(99, false), Is.EqualTo(Fake.Text(p, G, Five).Replace("\r\n", "\n")));
            }
        }

        [Test]
        public void AServerOnlyAttributeHasNoPlaceInTheGlobalClient()
        {
            using (var f = new FakeTargets())
            {
                Plan p = f.Plan(f.Mod("m", Edit(S, Five, "environment/drop", "rate", "5") +
                    "<entry archive=\"" + S + "\" name=\"item/0/12/01200009.xml\" copy=\"" + Five + "\"/>" +
                    Edit(S, "table/bags.xml", "bag[id=1]", "item", "1200001")));
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(Change(p, S, Five, "rate"), Is.Not.Null, dump);
                Assert.That(Change(p, G, Five, "rate"), Is.Null, dump);
                Assert.That(p.Warnings.Any(w => w.Archive == G && w.Text.Contains("environment/drop @rate has no place in gms-client")), Is.True, dump);
                // the copied entry is written to the global client without the server-only attribute
                string copy = Fake.Text(p, G, "item/0/12/01200009.xml");
                Assert.That(copy, Is.Not.Null, dump);
                Assert.That(copy, Does.Not.Contain("drop"));
                Assert.That(Fake.Text(p, S, "item/0/12/01200009.xml"), Does.Contain("rate=\"4\""));
                // the server-only table is not in the global client: a warning, nothing written there
                Assert.That(p.Warnings.Any(w => w.Archive == G && w.Entry == "table/bags.xml" && w.Text.Contains("gms-client has no such entry")), Is.True, dump);
                Assert.That(Fake.Text(p, G, "table/bags.xml"), Is.Null);
            }
        }

        [Test]
        public void AKoreanClientChangeIsSyncedToBothPerIdTargets()
        {
            using (var f = new FakeTargets())
            {
                Plan p = f.Plan(f.Mod("m", Edit(K, Bucket, "item[id=1200005]/environment/property", "sellPrice", "55")));
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(p.Warnings, Is.Empty, dump);
                foreach (string a in new[] { S, G })
                {
                    PlanChange c = Change(p, a, Five, "sellPrice");
                    Assert.That(c, Is.Not.Null, a + "\n" + dump);
                    Assert.That(c.Synced, Is.True);
                    Assert.That(c.NewValue, Is.EqualTo("55"));
                }
                Assert.That(p.Changes.Count(c => c.Name == "sellPrice"), Is.EqualTo(3), dump);
            }
        }

        [Test]
        public void AGlobalClientAndServerWorkspace()
        {
            using (var f = new FakeTargets(kms: false))
            {
                Plan p = f.Plan(f.Mod("m", Edit("gms:Xml.m2d", Five, "environment/property", "slotMax", "8")));
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(Change(p, G, Five, "slotMax").Mirrored, Is.False, dump);
                Assert.That(Change(p, S, Five, "slotMax").Mirrored, Is.True, dump);
                Assert.That(p.Changes.Count, Is.EqualTo(2), dump);

                // a line for the Korean client in a data set without it: planned for nothing, a note
                p = f.Plan(f.Mod("k", Edit(K, Bucket, "item[id=1200005]/environment/property", "slotMax", "8")));
                dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(p.Changes, Is.Empty, dump);
                Assert.That(p.Notes.Any(n => n.Text.Contains("kms-client is not a target of this data set")), Is.True, dump);
            }
        }

        [Test]
        public void AnExplicitLineForTheOtherSideWins()
        {
            using (var f = new FakeTargets(kms: false))
            {
                Plan p = f.Plan(f.Mod("m", Edit(S, Five, "environment/property", "sellPrice", "99") + Edit(G, Five, "environment/property", "sellPrice", "50")));
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(Change(p, S, Five, "sellPrice").NewValue, Is.EqualTo("99"), dump);
                Assert.That(Change(p, G, Five, "sellPrice").NewValue, Is.EqualTo("50"), dump);
                Assert.That(p.Changes.Any(c => c.Mirrored), Is.False, dump);
            }
        }

        [Test]
        public void AFieldSetOnServerAndGlobalClientIsSyncedOnce()
        {
            using (var f = new FakeTargets())
            {
                // the same value on both per-id targets: one synced change on the Korean client, no warning
                Plan p = f.Plan(f.Mod("m", Edit(G, Five, "environment/property", "sellPrice", "61") + Edit(S, Five, "environment/property", "sellPrice", "61")));
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(p.Warnings, Is.Empty, dump);
                Assert.That(p.Changes.Count(c => c.Archive == K && c.Name == "sellPrice"), Is.EqualTo(1), dump);
                Assert.That(Change(p, K, Bucket, "sellPrice").NewValue, Is.EqualTo("61"), dump);
                Assert.That(p.Changes.Count(c => c.Name == "sellPrice"), Is.EqualTo(3), dump);

                // two values: the Korean client gets the server's, whatever the order of the lines, and a warning says so
                foreach (bool serverFirst in new[] { true, false })
                {
                    string gl = Edit(G, Five, "environment/property", "sellPrice", "62"), sl = Edit(S, Five, "environment/property", "sellPrice", "63");
                    p = f.Plan(f.Mod("n" + serverFirst, serverFirst ? sl + gl : gl + sl));
                    dump = Fake.Dump(p);
                    Assert.That(p.Errors, Is.Empty, dump);
                    Assert.That(p.Changes.Count(c => c.Archive == K && c.Name == "sellPrice"), Is.EqualTo(1), dump);
                    Assert.That(Change(p, K, Bucket, "sellPrice").NewValue, Is.EqualTo("63"), dump);
                    Assert.That(p.Warnings.Count(w => w.Text.Contains("synced from the server")), Is.EqualTo(1), dump);
                }
            }
        }

        static Dictionary<string, byte[]> Files(string dir)
        {
            var d = new Dictionary<string, byte[]>();
            foreach (string p in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) d[p] = File.ReadAllBytes(p);
            return d;
        }

        [Test]
        public void ApplyAndRevertOnThreeTargets()
        {
            using (var f = new FakeTargets())
            {
                Dictionary<string, byte[]> before = Files(Path.Combine(f.Dir, "orig"));
                var ws = new Workspace(Path.Combine(f.Dir, "ws"));
                Mod m = f.Mod("m", Edit(S, Five, "environment/property", "sellPrice", "99"));
                ApplyResult r = ws.Apply(f.Data, new[] { m }, new ApplyOptions { VerifyAll = true, Maps = FakeItems.Maps });
                Assert.That(r.Applied, Is.True, Fake.Dump(r.Plan));
                Assert.That(r.Outputs.Select(o => o.Archive).ToArray(), Is.EquivalentTo(new[] { K, G, S }));
                foreach (ApplyOutput o in r.Outputs) Assert.That(o.Replaced, Is.EqualTo(1), o.Archive);
                Assert.That(ws.ReadState().Archives.Select(x => x.File).ToArray(), Is.EquivalentTo(new[] { "client/Xml.m2d", "gms-client/Xml.m2d", "server/Xml.m2d" }));
                using (M2dArchive orig = M2dArchive.Open(Path.Combine(f.GmsDir, "Xml.m2d")))
                using (M2dArchive made = M2dArchive.Open(ws.PathOf(G)))
                {
                    Assert.That(made.Count, Is.EqualTo(orig.Count));
                    foreach (M2dEntry e in orig.Entries)
                        if (e.Name != Five) Assert.That(made.ReadRaw(made.Find(e.Name)), Is.EqualTo(orig.ReadRaw(e)), e.Name);
                    Assert.That(Encoding.UTF8.GetString(made.Read(Five)).Replace("\r\n", "\n"), Is.EqualTo(FakeTargets.Item(99, false)));
                }
                ws.Revert(f.Data, "m", new ApplyOptions { Maps = FakeItems.Maps });
                Assert.That(Directory.GetFiles(ws.Dir, "*", SearchOption.AllDirectories), Is.Empty);
                Dictionary<string, byte[]> after = Files(Path.Combine(f.Dir, "orig"));
                foreach (string k in before.Keys) Assert.That(after[k], Is.EqualTo(before[k]), k);
            }
        }

        [Test]
        public void LinesPerTarget()
        {
            using (var f = new FakeTargets(gms: true))
            using (var s = new EditorSession(f.KmsDir, f.GmsDir, f.ServerDir, Path.Combine(f.Dir, "mods"), Path.Combine(f.Dir, "ws")))
            {
                Assert.That(Targets.Line(f.Data), Is.EqualTo("targets: kms-client (Korean client), gms-client (global client), server"));
                s.Maps = FakeItems.Maps;
                s.Adopt(Mod.Parse("<mod id=\"m\" name=\"t\" version=\"1\">" + Edit(S, Five, "environment/drop", "rate", "5") + Edit(S, Five, "environment/property", "sellPrice", "99") + "</mod>", "test"));
                Plan p = s.Plan();
                List<string> lines = Targets.PlanLines(p, s.Data);
                Assert.That(lines, Is.EqualTo(new[]
                {
                    "kms-client: 1 change(s) (1 synced by the field maps) in 1 entry of 1 archive(s)",
                    "gms-client: 1 change(s) (1 mirrored) in 1 entry of 1 archive(s); 0 error(s), 0 conflict(s), 1 warning(s)",
                    "server: 2 change(s) in 1 entry of 1 archive(s)",
                }), Fake.Dump(p));
                ApplyResult r = s.Apply();
                Assert.That(r.Applied, Is.True, s.Describe(r));
                string text = s.Describe(r);
                Assert.That(text, Does.Contain("  gms-client: wrote " + Path.Combine(f.Dir, "ws", "gms-client", "Xml.m2d") + " (1 replaced"));
                Assert.That(text, Does.Contain("  kms-client: wrote " + Path.Combine(f.Dir, "ws", "client", "Xml.m2d")));
                r = s.Revert();
                text = s.Describe(r);
                Assert.That(text, Does.Contain("  server: removed " + Path.Combine(f.Dir, "ws", "server", "Xml.m2d")), text);
                Assert.That(text, Does.Contain("  gms-client: removed " + Path.Combine(f.Dir, "ws", "gms-client", "Xml.m2d")), text);
            }
            using (var f = new FakeTargets(gms: false))
                Assert.That(Targets.Line(f.Data), Is.EqualTo("targets: kms-client (Korean client), server; not in this data set: gms-client (global client)"));
        }
    }
}
