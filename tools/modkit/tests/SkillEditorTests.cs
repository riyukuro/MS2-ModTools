using System.Collections.Generic;
using MS2.ModKit;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class SkillEditorTests
    {
        const string K = "client:Xml.m2d", G = "gms-client:Xml.m2d";

        [Test]
        public void TheIconEntryOfASkill_AndOfACloneItsSource()
        {
            using (var f = new FakeSkills())
            {
                var ed = new SkillEditor(f.Data, FakeSkills.ModOf(""));
                Assert.That(ed.IconEntry(SkillKind.Skill, 70000001), Is.EqualTo(Icons.ImageEntry(FakeSkills.Icon)));
                Assert.That(ed.IconEntry(SkillKind.Skill, 70000999), Is.Null, "no target ships it");
                ed.Clone(SkillKind.Skill, 70000001, 78000001, "1", "My Slash", null);
                Assert.That(ed.IconEntry(SkillKind.Skill, 78000001), Is.EqualTo(Icons.ImageEntry(FakeSkills.Icon)));
            }
        }

        [Test]
        public void TheIconOfAnEffect_ComesFromItsUIPropertyRow()
        {
            using (var f = new FakeSkills())
            {
                var ed = new SkillEditor(f.Data, FakeSkills.ModOf(""));
                Assert.That(ed.Open(SkillKind.Effect, 71000001).Icon, Is.EqualTo(FakeSkills.EffectIcon));
                Assert.That(ed.IconEntry(SkillKind.Effect, 71000001), Is.EqualTo("Skill/icon/fakebleed.png"));
            }
        }

        [Test]
        public void FindAndOpenShowEachSideInNeutralUnits()
        {
            using (var f = new FakeSkills())
            {
                var ed = new SkillEditor(f.Data, FakeSkills.ModOf(""));
                List<KeyValuePair<long, string>> hits = ed.Find(SkillKind.Skill, "slash", 10);
                Assert.That(hits.Count, Is.EqualTo(1));
                Assert.That(hits[0].Key, Is.EqualTo(70000001));
                Assert.That(hits[0].Value, Is.EqualTo("Fake Slash"));
                Assert.That(ed.Find(SkillKind.Skill, "7000000", 10).Count, Is.EqualTo(2));
                Assert.That(ed.Find(SkillKind.Effect, "bleed", 10)[0].Key, Is.EqualTo(71000001));

                SkillInfo s = ed.Open(SkillKind.Skill, 70000001);
                Assert.That(s.Sides.Count, Is.EqualTo(3));
                Assert.That(s.Icon, Is.EqualTo(FakeSkills.Icon));
                SkillSideInfo k = s.Sides.Find(x => x.Archive == K), g = s.Sides.Find(x => x.Archive == G);
                Assert.That(k.Levels.Keys, Is.EquivalentTo(new[] { 1, 2 }));
                Assert.That(k.Variants, Is.EquivalentTo(new[] { 2 }));
                Assert.That(k.Levels[1].Values["cooldown"], Is.EqualTo("8"));
                Assert.That(g.Levels[1].Values["cooldown"], Is.EqualTo("8"));
                Assert.That(k.Levels[2].Values["cooldown"], Is.EqualTo("6"));   // the FakeOn variant the side reads
                Assert.That(k.Levels[1].Children[0].Children[0].Values["distance"], Is.EqualTo("600"));
                Assert.That(g.Levels[1].Children[0].Children[0].Values["damage"], Is.EqualTo("1.5"));
                Assert.That(g.Levels[1].Values["sp"], Is.EqualTo("4"));

                SkillInfo e = ed.Open(SkillKind.Effect, 71000001);
                Assert.That(e.Name, Is.EqualTo("Fake Bleed"));
                Assert.That(e.Sides.Find(x => x.Archive == G).Levels[1].Values["duration"], Is.EqualTo("20"));
                Assert.That(ed.Open(SkillKind.Skill, 79999999), Is.Null);
            }
        }

        [Test]
        public void NamesComeInTheChosenLanguageAndSayWhichOne()
        {
            using (var f = new FakeSkills())
            {
                var ed = new SkillEditor(f.Data, FakeSkills.ModOf(""));
                // the kms-client has cn and kr names: its own language kr wins, not the first one (cn)
                List<KeyValuePair<long, string>> hits = ed.Find(SkillKind.Skill, "slash", 10);
                Assert.That(hits[0].Value, Is.EqualTo("Fake Slash"));
                Assert.That(ed.NamesUsed, Does.Contain("kms-client kr"));
                Assert.That(ed.NamesUsed, Does.Contain("gms-client en"));
                Assert.That(ed.NameLanguages(SkillKind.Skill), Is.SupersetOf(new[] { "cn", "en", "kr" }));
                ed.NameLanguage = "cn";
                hits = ed.Find(SkillKind.Skill, "slash", 10);
                Assert.That(ed.NamesUsed, Does.Contain("kms-client cn"));
                Assert.That(ed.NamesUsed, Does.Contain("gms-client en"), "a target without cn names says it falls back");
                Assert.That(ed.Open(SkillKind.Skill, 70000001).Sides.Find(x => x.Archive == K).Name, Is.EqualTo("Fake Slash cn"));
            }
        }

        [Test]
        public void ASpeedLeftOutIsShownAsTheDefault()
        {
            using (var f = new FakeSkills())
            {
                var ed = new SkillEditor(f.Data, FakeSkills.ModOf(""));
                SkillSideInfo k = ed.Open(SkillKind.Skill, 70000001).Sides.Find(x => x.Archive == K);
                Assert.That(k.Levels[1].Children[0].Values["speed"], Is.EqualTo("1"));
                Assert.That(k.Levels[1].Children[0].Defaulted, Is.Empty);
                Assert.That(k.Levels[2].Children[0].Values["speed"], Is.EqualTo("1"));
                Assert.That(k.Levels[2].Children[0].Defaulted, Does.Contain("speed"));
            }
        }

        [Test]
        public void TooltipLanguageAndCloneCooldownGroupReachTheMod()
        {
            using (var f = new FakeSkills())
            {
                Mod m = FakeSkills.ModOf("");
                var ed = new SkillEditor(f.Data, m);
                ed.SetTooltip(SkillKind.Skill, 70000001, 1, null, "trifft 300%", "de");
                ed.Clone(SkillKind.Skill, 70000001, 78000001, null, "Fake Cut", null, "own");
                Assert.Throws<ModKitException>(() => ed.Clone(SkillKind.Skill, 70000001, 78000002, null, null, null, "mine"));
                Assert.Throws<ModKitException>(() => ed.Clone(SkillKind.Effect, 71000001, 77100002, null, null, null, "own"));
                Assert.Throws<ModKitException>(() => ed.SetTooltip(SkillKind.Skill, 70000001, 1, null, "x", "E N"));
                string xml = m.ToXml();
                Assert.That(xml, Does.Contain("lang=\"de\""));
                Assert.That(xml, Does.Contain("cooldown-group=\"own\""));
            }
        }

        [Test]
        public void SetsCloneAndJobRowsGoToTheModAndPlan()
        {
            using (var f = new FakeSkills())
            {
                Mod m = FakeSkills.ModOf("");
                var ed = new SkillEditor(f.Data, m);
                ed.Set(SkillKind.Skill, 70000001, 1, 0, 0, "cooldown", "3");
                ed.Set(SkillKind.Skill, 70000001, 1, 1, 1, "distance", "900");
                ed.Set(SkillKind.Skill, 70000001, 1, 1, 1, "damage", "*2");
                Assert.That(ed.Get(SkillKind.Skill, 70000001, 1, 1, 1, "damage"), Is.EqualTo("*2"));
                Assert.That(ed.Get(SkillKind.Skill, 70000001, 2, 0, 0, "cooldown"), Is.Null);
                Assert.Throws<ModKitException>(() => ed.Set(SkillKind.Skill, 70000001, 1, 0, 0, "cooldown", "soon"));
                ed.SetTooltip(SkillKind.Skill, 70000001, 1, "300,3", "hits for 300%");
                ed.Clone(SkillKind.Skill, 70000001, 78000001, "1", "Fake Cut", new[] { new KeyValuePair<long, long>(71000001, 77100001) });
                ed.JobRow(90, 78000001, 70000002, null, 1);

                Mod again = Mod.Parse(m.ToXml(), "saved");
                Plan p = Planner.Build(f.Data, new List<Mod> { again }, new PlanOptions());
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(Fake.Text(p, K, "skilldata/700.xml"), Does.Contain("cooldown=\"3000\""), dump);
                Assert.That(Fake.Text(p, G, "skill/70/70000001.xml"), Does.Contain("distance=\"900\""), dump);
                Assert.That(Fake.Text(p, G, "skill/78/78000001.xml"), Is.Not.Null, dump);
                Assert.That(Fake.Text(p, K, "table/job.xml"), Does.Contain("skillID=\"78000001\""), dump);

                // clearing every value drops the op again
                ed.Set(SkillKind.Skill, 70000001, 1, 0, 0, "cooldown", "");
                ed.Set(SkillKind.Skill, 70000001, 1, 1, 1, "distance", "");
                ed.Set(SkillKind.Skill, 70000001, 1, 1, 1, "damage", "");
                ed.SetTooltip(SkillKind.Skill, 70000001, 1, null, null);
                Assert.That(ed.Op(SkillKind.Skill, 70000001, false), Is.Null);
                Assert.That(m.Skills.Count, Is.EqualTo(1));
            }
        }
    }
}
