// The priority cases through the real apply: the mods are applied to a workspace in a temp folder and the written
// archives are read back. Made-up data only.
using System;
using System.IO;
using System.Linq;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.MapEdit;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class ApplyPriorityTests
    {
        static string Written(Workspace ws, string archive, string entry)
        {
            using (M2dArchive made = M2dArchive.Open(ws.PathOf(archive)))
            {
                M2dEntry e = made.Find(entry);
                return e == null ? null : Encoding.UTF8.GetString(made.Read(e));
            }
        }

        static string Line(string text, string start)
        {
            return text.Replace("\r", "").Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(start, StringComparison.Ordinal));
        }

        static string Set(string archive, string entry, string at, string name, string value)
        {
            return "<edit archive=\"" + archive + "\" entry=\"" + entry + "\"><change at=\"" + at + "\"><set name=\"" + name + "\" value=\"" + value + "\"/></change></edit>";
        }

        [Test]
        public void AMapEditOnAMapFileThatALowerModAdds()
        {
            using (var f = new FakeMaps())
            {
                const string K = FakeMaps.K, NewMap = "90000009_ab", NewEntry = "xblock/" + NewMap + ".xblock";
                M2dArchive arch = f.Data.Open(K);
                string shipped = Encoding.UTF8.GetString(arch.Read(arch.Find(FakeMaps.EntryA)));
                byte[] whole = Encoding.UTF8.GetBytes(shipped.Replace("0, 0, 90", "0, 0, 45"));
                string d = Path.Combine(f.Dir, "mods", "added", "data");
                Directory.CreateDirectory(d);
                File.WriteAllBytes(Path.Combine(d, "n.xblock"), whole);
                Mod added = f.Mod("added", "<entry archive=\"" + K + "\" name=\"" + NewEntry + "\" file=\"data/n.xblock\"/>");
                Mod on = f.Mod("on", "<map xblock=\"" + NewMap + "\"><add name=\"crate-1\" model=\"Crate_A\" entity-name=\"Crate_A_900\"><property name=\"Position\" value=\"150, 300, 450\" /></add></map>");

                var ws = new Workspace(Path.Combine(f.Dir, "ws"));
                ApplyResult r = ws.Apply(f.Data, new[] { added, on }, null);
                Assert.That(r.Applied, Is.False, "a conflict stops the apply");
                Assert.That(File.Exists(ws.PathOf(K)), Is.False);

                r = ws.Apply(f.Data, new[] { added, on }, new ApplyOptions { AllowConflicts = true, VerifyAll = true });
                Assert.That(r.Applied, Is.True, Fake.Dump(r.Plan));
                Assert.That(r.Plan.Conflicts.Where(x => x.Archive == K).Select(x => x.WinnerId), Is.EqualTo(new[] { "on" }), Fake.Dump(r.Plan));
                // the written file is the lower mod's file with the higher mod's new entity in it
                string text = Written(ws, K, NewEntry);
                Assert.That(text, Does.Contain(MapValues.NewEntityId("on", NewMap, "crate-1")).And.Contain("0, 0, 45"));

                // the other way round the file is written as the mod brings it
                var ws2 = new Workspace(Path.Combine(f.Dir, "ws2"));
                r = ws2.Apply(f.Data, new[] { on, added }, new ApplyOptions { AllowConflicts = true, VerifyAll = true });
                Assert.That(r.Applied, Is.True, Fake.Dump(r.Plan));
                Assert.That(r.Plan.Conflicts.Where(x => x.Archive == K).Select(x => x.WinnerId), Is.EqualTo(new[] { "added" }), Fake.Dump(r.Plan));
                Assert.That(Encoding.UTF8.GetBytes(Written(ws2, K, NewEntry)), Is.EqualTo(whole));
            }
        }

        [Test]
        public void ADeleteBetweenALowerAndAHigherChangeOfARow()
        {
            using (var f = new Fake())
            {
                const string C = "client:Xml.m2d", Pet = "table/pet.xml";
                Mod lo = f.Mod("lo", Set(C, Pet, "pet[id=101]", "hp", "5"));
                Mod mid = f.Mod("mid", "<edit archive=\"" + C + "\" entry=\"" + Pet + "\"><delete at=\"pet[id=101]\"/></edit>");
                Mod hi = f.Mod("hi", Set(C, Pet, "pet[id=101]", "speed", "9"));

                var ws = new Workspace(Path.Combine(f.Dir, "ws"));
                ApplyResult r = ws.Apply(f.Data, new[] { lo, mid, hi }, null);
                Assert.That(r.Applied, Is.False);
                Assert.That(File.Exists(ws.PathOf(C)), Is.False);

                r = ws.Apply(f.Data, new[] { lo, mid, hi }, new ApplyOptions { AllowConflicts = true, VerifyAll = true });
                Assert.That(r.Applied, Is.True, Fake.Dump(r.Plan));
                Assert.That(r.Plan.Conflicts.All(x => x.WinnerId == "hi" && x.LoserId == "mid"), Fake.Dump(r.Plan));
                // the row is in the written archive with both changes
                Assert.That(Line(Written(ws, C, Pet), "<pet id=\"101\""), Does.Contain("hp=\"5\"").And.Contain("speed=\"9\""));

                // the delete on top: the row is gone from the written archive
                var ws2 = new Workspace(Path.Combine(f.Dir, "ws2"));
                r = ws2.Apply(f.Data, new[] { lo, hi, mid }, new ApplyOptions { AllowConflicts = true, VerifyAll = true });
                Assert.That(r.Applied, Is.True, Fake.Dump(r.Plan));
                Assert.That(r.Plan.Conflicts.All(x => x.WinnerId == "mid"), Fake.Dump(r.Plan));
                Assert.That(Written(ws2, C, Pet), Does.Not.Contain("id=\"101\""));
            }
        }

        [Test]
        public void AMirroredLineAgainstADirectLine()
        {
            using (var f = new FakeTargets(kms: false))
            {
                const string S = "server:Xml.m2d", G = "gms-client:Xml.m2d", Five = "item/0/12/01200005.xml";
                Mod direct = f.Mod("direct", Set(G, Five, "environment/property", "slotMax", "5"));
                Mod other = f.Mod("other", Set(S, Five, "environment/property", "slotMax", "8"));

                var ws = new Workspace(Path.Combine(f.Dir, "ws"));
                ApplyResult r = ws.Apply(f.Data, new[] { direct, other }, new ApplyOptions { Maps = FakeItems.Maps });
                Assert.That(r.Applied, Is.False, "a mirrored conflict stops the apply");
                Assert.That(File.Exists(ws.PathOf(S)) || File.Exists(ws.PathOf(G)), Is.False);

                r = ws.Apply(f.Data, new[] { direct, other }, new ApplyOptions { Maps = FakeItems.Maps, AllowConflicts = true, VerifyAll = true });
                Assert.That(r.Applied, Is.True, Fake.Dump(r.Plan));
                // the higher mod's value is in both written archives
                foreach (string a in new[] { S, G })
                    Assert.That(Written(ws, a, Five), Does.Contain("slotMax=\"8\"").And.Not.Contain("slotMax=\"5\""), a);

                var ws2 = new Workspace(Path.Combine(f.Dir, "ws2"));
                r = ws2.Apply(f.Data, new[] { other, direct }, new ApplyOptions { Maps = FakeItems.Maps, AllowConflicts = true, VerifyAll = true });
                Assert.That(r.Applied, Is.True, Fake.Dump(r.Plan));
                foreach (string a in new[] { S, G })
                    Assert.That(Written(ws2, a, Five), Does.Contain("slotMax=\"5\"").And.Not.Contain("slotMax=\"8\""), a);
            }
        }
    }
}
