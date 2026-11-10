// Skills, effects and job skill trees on made-up data. Every id, name, path and number in here is made up.
//   skill 70000001: two levels; level 2 has three variants on the Korean side (none, FakeOn, FakeOff);
//                   level 1 attack 1 hits with effect 71000001.
//   skill 70000002: one level, used as a motion source.
//   job 90: two tier-1 slots (0,2) and (1,2).
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    public sealed class FakeSkills : IDisposable
    {
        public readonly string Dir;
        public readonly DataSet Data;
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";
        public const string Icon = "./data/resource/image/skill/icon/fake.png";

        public const string KmsSkills = Head + "<ms2>\n" +
            "\t<skill id=\"70000001\" comment=\"made up\">\n" +
            "\t\t<basic>\n" +
            "\t\t\t<ui icon=\"" + Icon + "\" />\n" +
            "\t\t\t<stateAttr battle=\"1\" cooldownGroupID=\"70000001\" />\n" +
            "\t\t</basic>\n" +
            "\t\t<level value=\"1\" cooldown=\"8000\" stringParam=\"150,8\" sp=\"4\">\n" +
            "\t\t\t<motion sequenceName=\"Attack_A\" strTagEffects=\"e0=CH/fake/hit.xml\" sequenceSpeed=\"1\">\n" +
            "\t\t\t\t<attack point=\"1\" targetCount=\"3\">\n" +
            "\t\t\t\t\t<range distance=\"600\" height=\"100\" width=\"50\" />\n" +
            "\t\t\t\t\t<damage rate=\"1.5\" hitAdditionalID=\"71000001\" />\n" +
            "\t\t\t\t</attack>\n" +
            "\t\t\t</motion>\n" +
            "\t\t</level>\n" +
            "\t\t<level value=\"2\" cooldown=\"7000\" stringParam=\"160,7\">\n" +
            "\t\t\t<motion sequenceName=\"Attack_A\">\n" +
            "\t\t\t\t<attack point=\"1\" targetCount=\"3\">\n" +
            "\t\t\t\t\t<range distance=\"600\" />\n" +
            "\t\t\t\t\t<damage rate=\"1.6\" />\n" +
            "\t\t\t\t</attack>\n" +
            "\t\t\t</motion>\n" +
            "\t\t</level>\n" +
            "\t\t<level value=\"2\" cooldown=\"6000\" stringParam=\"160,6\" feature=\"FakeOn\">\n" +
            "\t\t\t<motion sequenceName=\"Attack_A\">\n" +
            "\t\t\t\t<attack point=\"1\" targetCount=\"3\">\n" +
            "\t\t\t\t\t<range distance=\"600\" />\n" +
            "\t\t\t\t\t<damage rate=\"1.6\" />\n" +
            "\t\t\t\t</attack>\n" +
            "\t\t\t</motion>\n" +
            "\t\t</level>\n" +
            "\t\t<level value=\"2\" cooldown=\"5000\" stringParam=\"160,5\" feature=\"FakeOff\">\n" +
            "\t\t\t<motion sequenceName=\"Attack_A\" />\n" +
            "\t\t</level>\n" +
            "\t</skill>\n" +
            "\t<skill id=\"70000002\">\n" +
            "\t\t<level value=\"1\" cooldown=\"1000\">\n" +
            "\t\t\t<motion sequenceName=\"Attack_B\">\n" +
            "\t\t\t\t<attack point=\"1\" targetCount=\"1\" />\n" +
            "\t\t\t</motion>\n" +
            "\t\t</level>\n" +
            "\t</skill>\n" +
            "</ms2>\n";

        public const string KmsEffects = Head + "<ms2>\n" +
            "\t<additional id=\"71000001\" mainType=\"1\">\n" +
            "\t\t<level level=\"1\" stringParam=\"20\">\n" +
            "\t\t\t<Basic durationTick=\"20000\" intervalTick=\"1000\" maxBuffCount=\"1\" />\n" +
            "\t\t</level>\n" +
            "\t</additional>\n" +
            "</ms2>\n";

        public static string GmsSkill(string seqA, string cooldown, bool second, string group = null)
        {
            string lv1 = "\t<level value=\"1\">\n" +
                "\t\t<beginCondition cooldownTime=\"" + cooldown + "\" />\n" +
                "\t\t<consume money=\"0\">\n\t\t\t<stat sp=\"4\" />\n\t\t</consume>\n" +
                "\t\t<motion>\n" +
                "\t\t\t<motionProperty sequenceName=\"" + seqA + "\" sequenceSpeed=\"1\" strTagEffects=\"e0=CH/fake/hit.xml\" />\n" +
                "\t\t\t<attack targetCount=\"3\">\n" +
                "\t\t\t\t<rangeProperty distance=\"600\" width=\"50\" height=\"100\" />\n" +
                "\t\t\t\t<damageProperty rate=\"1.5\" hitEffectID=\"71000001\" />\n" +
                "\t\t\t</attack>\n" +
                "\t\t</motion>\n" +
                "\t</level>\n";
            string lv2 = !second ? "" : "\t<level value=\"2\">\n" +
                "\t\t<beginCondition cooldownTime=\"7\" />\n" +
                "\t\t<motion>\n" +
                "\t\t\t<motionProperty sequenceName=\"" + seqA + "\" />\n" +
                "\t\t\t<attack targetCount=\"3\">\n" +
                "\t\t\t\t<rangeProperty distance=\"600\" />\n" +
                "\t\t\t\t<damageProperty rate=\"1.6\" hitEffectID=\"71000001\" />\n" +   // a dropped level pointing at the own effect

                "\t\t\t</attack>\n" +
                "\t\t</motion>\n" +
                "\t</level>\n";
            string state = group == null ? "" : "\t\t<stateAttr battle=\"1\" cooldownGroupID=\"" + group + "\" />\n";
            return Head + "<ms2>\n\t<basic>\n\t\t<ui icon=\"" + Icon + "\" />\n" + state + "\t</basic>\n" + lv1 + lv2 + "</ms2>\n";
        }

        public const string GmsEffect = Head + "<ms2>\n\t<level>\n\t\t<BasicProperty level=\"1\" durationTick=\"20000\" intervalTick=\"1000\" />\n\t</level>\n</ms2>\n";

        public const string KmsJob = Head + "<ms2>\n" +
            "\t<job jobGroupID=\"90\" subJob1=\"900\" subJob2=\"901\">\n" +
            "\t\t<skills>\n" +
            "\t\t\t<v skillID=\"70000001\" jobLevel=\"1\" isLearn=\"1\" uiPosition=\"0,2\" maxLevel=\"10\" requireLevel=\"1\" />\n" +
            "\t\t\t<v skillID=\"70000002\" jobLevel=\"1\" uiPosition=\"1,2\" maxLevel=\"10\" />\n" +
            "\t\t</skills>\n" +
            "\t</job>\n" +
            "</ms2>\n";

        public const string GmsJob = Head + "<ms2>\n" +
            "\t<job code=\"090\">\n" +
            "\t\t<skills>\n" +
            "\t\t\t<skill main=\"70000001\" uiPosition=\"0, 2\" maxLevel=\"10\" />\n" +
            "\t\t\t<skill main=\"70000002\" uiPosition=\"1, 2\" maxLevel=\"10\" />\n" +
            "\t\t</skills>\n" +
            "\t\t<learn level=\"1\">\n\t\t\t<skill id=\"70000001\" />\n\t\t</learn>\n" +
            "\t</job>\n" +
            "</ms2>\n";

        const string Features = Head + "<ms2>\n\t<v name=\"FakeOn\" KR=\"1\" NA=\"1\" />\n\t<v name=\"FakeOff\" KR=\"9\" NA=\"9\" />\n</ms2>\n";
        const string FeatureSetting = Head + "<ms2>\n\t<v type=\"Live\" KR=\"5\" NA=\"5\" />\n</ms2>\n";
        const string Anim = Head + "<ms2>\n\t<kfm name=\"DummyMale\">\n\t\t<seq name=\"Attack_A\" />\n\t\t<seq name=\"Attack_B\" />\n\t</kfm>\n\t<kfm name=\"FakeMonster\">\n\t\t<seq name=\"Bite_A\" />\n\t</kfm>\n</ms2>\n";

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }
        static string Keys(string rows) { return Head + "<ms2>\n" + rows + "</ms2>\n"; }

        public FakeSkills() : this(new byte[] { 1, 2, 3 }) { }

        /// <summary>icon: the bytes of the made-up icon image.</summary>
        public FakeSkills(byte[] icon)
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-skills-" + Guid.NewGuid().ToString("N"));
            string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
            foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(Path.Combine(d, "Resource"));
            M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E("skilldata/700.xml", KmsSkills), E("additional/710.xml", KmsEffects), E("table/job.xml", KmsJob),
                E("table/feature.xml", Features), E("table/feature_setting.xml", FeatureSetting), E("anikeytext.xml", Anim),
                E("effect/ch/fake/hit.xml", "<x />\n"),
                E("string/cn/skillname.xml", Keys("\t<key id=\"70000001\" name=\"Fake Slash cn\" />\n")),   // sorts before kr
                E("string/kr/skillname.xml", Keys("\t<key id=\"70000001\" name=\"Fake Slash\" />\n")),
                E("string/kr/stringskilldescription.xml", Keys("\t<key id=\"70000001\" name=\"hits for #0#%\" />\n")),
                E("string/kr/stringadditionaldescription.xml", Keys("\t<key id=\"71000001\" level=\"1\" name=\"Fake Bleed\" tooltipDescription=\"bleeds\" />\n")),
            });
            M2dWriter.Create(Path.Combine(k, "Resource", "Image.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                new KeyValuePair<string, byte[]>(Icons.ImageEntry(Icon), icon),
            });
            foreach (string d in new[] { g, s })
                M2dWriter.Create(Path.Combine(d, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                {
                    E("skill/70/70000001.xml", GmsSkill("Attack_A", "8", true, "70000001")), E("skill/70/70000002.xml", GmsSkill("Attack_B", "1", false)),
                    E("additionaleffect/71000001.xml", GmsEffect), E("table/job.xml", GmsJob),
                    E("table/feature.xml", Features), E("table/feature_setting.xml", FeatureSetting), E("anikeytext.xml", Anim),
                    E("effect/ch/fake/hit.xml", "<x />\n"),
                    E("string/en/skillname.xml", Keys("\t<key id=\"70000001\" name=\"Fake Slash\" />\n")),
                    E("string/de/korskilldescription.xml", Keys("\t<key id=\"70000001\" level=\"1\" uiDescription=\"trifft 150%\" />\n")),
                    E("string/en/korskilldescription.xml", Keys("\t<key id=\"70000001\" level=\"1\" uiDescription=\"hits for 150%\" />\n\t<key id=\"70000001\" level=\"2\" uiDescription=\"hits for 160%\" />\n")),
                    E("string/en/koradditionaldescription.xml", Keys("\t<key id=\"71000001\" level=\"1\" name=\"Fake Bleed\" tooltipDescription=\"bleeds for 20 s\" />\n")),
                });
            Data = new DataSet(k, g, s);
        }

        public static Mod ModOf(string body, string id = "skills.test")
        {
            Mod m = Mod.Parse("<mod id=\"" + id + "\" name=\"t\" version=\"1\">\n" + body + "\n</mod>", "test mod");
            return Mod.Parse(m.ToXml(), "round trip");
        }

        public Plan Plan(string body) { return Planner.Build(Data, new List<Mod> { ModOf(body) }, new PlanOptions()); }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class SkillsTests
    {
        const string K = "client:Xml.m2d", G = "gms-client:Xml.m2d", S = "server:Xml.m2d";

        static string Text(Plan p, string archive, string entry)
        {
            if (p.Find(archive) == null) return null;
            string t = Fake.Text(p, archive, entry);
            return t == null ? null : t.Replace("\r\n", "\n");
        }

        static bool Has(List<PlanMessage> list, string part)
        {
            foreach (PlanMessage m in list) if (m.ToString().Contains(part)) return true;
            return false;
        }

        static int Lines(string a, string b)
        {
            string[] x = a.Split('\n'), y = b.Split('\n');
            if (x.Length != y.Length) return -1;
            int n = 0;
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) n++;
            return n;
        }

        const string Main = "<skill id=\"70000001\">" +
            "<level value=\"1\" cooldown=\"3\"/>" +
            "<attack level=\"1\" motion=\"1\" index=\"1\" distance=\"900\" damage=\"*2\"/>" +
            "</skill>";

        [Test]
        public void FieldsAreWrittenInEachSidesUnits()
        {
            using (var f = new FakeSkills())
            {
                Plan p = f.Plan(Main);
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                string kms = Text(p, K, "skilldata/700.xml");
                Assert.That(kms, Does.Contain("<level value=\"1\" cooldown=\"3000\" stringParam=\"150,8\" sp=\"4\">"), dump);
                Assert.That(kms, Does.Contain("<range distance=\"900\" height=\"100\" width=\"50\" />"), dump);
                Assert.That(kms, Does.Contain("<damage rate=\"3\" hitAdditionalID=\"71000001\" />"), dump);
                Assert.That(Lines(FakeSkills.KmsSkills, kms), Is.EqualTo(3), "only the three changed lines differ\n" + kms);
                foreach (string a in new[] { G, S })
                {
                    string gms = Text(p, a, "skill/70/70000001.xml");
                    Assert.That(gms, Does.Contain("<beginCondition cooldownTime=\"3\" />"), a + dump);
                    Assert.That(gms, Does.Contain("<rangeProperty distance=\"900\" width=\"50\" height=\"100\" />"), a + dump);
                    Assert.That(gms, Does.Contain("<damageProperty rate=\"3\" hitEffectID=\"71000001\" />"), a + dump);
                    Assert.That(Lines(FakeSkills.GmsSkill("Attack_A", "8", true, "70000001"), gms), Is.EqualTo(3), a + gms);
                }
                Assert.That(Has(p.Warnings, "the tooltip is not"), Is.True, "cooldown and damage are tooltip numbers\n" + dump);
                foreach (PlanChange c in p.Changes) Assert.That(c.Synced, Is.False, c.ToString());
            }
        }

        [Test]
        public void TooltipFieldsSilenceTheTooltipWarning()
        {
            using (var f = new FakeSkills())
            {
                Plan p = f.Plan(Main.Replace("</skill>", "<tooltip level=\"1\" kms-param=\"300,3\" gms-text=\"hits for 300%\"/></skill>"));
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(Has(p.Warnings, "the tooltip is not"), Is.False, dump);
                Assert.That(Text(p, K, "skilldata/700.xml"), Does.Contain("cooldown=\"3000\" stringParam=\"300,3\""), dump);
                foreach (string a in new[] { G, S })
                {
                    string t = Text(p, a, "string/en/korskilldescription.xml");
                    Assert.That(t, Does.Contain("<key id=\"70000001\" level=\"1\" uiDescription=\"hits for 300%\" />"), a + dump);
                    Assert.That(t, Does.Contain("level=\"2\" uiDescription=\"hits for 160%\""), a + dump);
                }
            }
        }

        [Test]
        public void GmsTextGoesToItsOneLanguage()
        {
            using (var f = new FakeSkills())
            {
                Plan p = f.Plan("<skill id=\"70000001\"><tooltip level=\"1\" gms-text=\"hits for 300%\"/></skill>");
                string dump = Fake.Dump(p);
                foreach (string a in new[] { G, S })
                {
                    Assert.That(Text(p, a, "string/en/korskilldescription.xml"), Does.Contain("uiDescription=\"hits for 300%\""), a + dump);
                    Assert.That(Text(p, a, "string/de/korskilldescription.xml"), Is.Null, "en is the default, de stays " + a + dump);
                }
                p = f.Plan("<skill id=\"70000001\"><tooltip level=\"1\" gms-text=\"trifft 300%\" lang=\"de\"/></skill>");
                dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                foreach (string a in new[] { G, S })
                {
                    Assert.That(Text(p, a, "string/de/korskilldescription.xml"), Does.Contain("<key id=\"70000001\" level=\"1\" uiDescription=\"trifft 300%\" />"), a + dump);
                    Assert.That(Text(p, a, "string/en/korskilldescription.xml"), Is.Null, a + dump);
                }
                p = f.Plan("<skill id=\"70000001\"><tooltip level=\"1\" gms-text=\"x\" lang=\"jp\"/></skill>");
                dump = Fake.Dump(p);
                Assert.That(Has(p.Warnings, "has no language \"jp\""), Is.True, dump);
                Assert.That(Text(p, G, "string/en/korskilldescription.xml"), Is.Null, dump);
                Assert.Throws<ModKitException>(() => FakeSkills.ModOf("<skill id=\"70000001\"><tooltip level=\"1\" gms-text=\"x\" lang=\"E N\"/></skill>"));
            }
        }

        [Test]
        public void ASpeedTheBundleLeavesOutIsOneAndIsWrittenWhenSet()
        {
            using (var f = new FakeSkills())
            {
                // kms level 2 has no sequenceSpeed: the client's default 1 applies
                Plan p = f.Plan("<skill id=\"70000001\"><motion level=\"2\" index=\"1\" speed=\"*2\"/></skill>");
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                string kms = Text(p, K, "skilldata/700.xml");
                Assert.That(kms, Does.Contain("<motion sequenceName=\"Attack_A\" sequenceSpeed=\"2\">"), dump + kms);
                Assert.That(kms.Split(new[] { "sequenceSpeed=\"2\"" }, StringSplitOptions.None).Length, Is.EqualTo(2), "only the variant the side reads");
                foreach (string a in new[] { G, S })
                    Assert.That(Text(p, a, "skill/70/70000001.xml"), Does.Contain("<motionProperty sequenceName=\"Attack_A\" sequenceSpeed=\"2\" />"), a + dump);
            }
        }

        [Test]
        public void ACloneKeepsItsSourcesCooldownGroupUnlessTheModSetsIt()
        {
            using (var f = new FakeSkills())
            {
                Plan p = f.Plan("<skill id=\"78000001\" clone=\"70000001\"/>");
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(Has(p.Warnings, "shares cooldown group 70000001 with its source"), Is.True, dump);
                Assert.That(Text(p, K, "skilldata/780.xml"), Does.Contain("cooldownGroupID=\"70000001\""), dump);

                p = f.Plan("<skill id=\"78000001\" clone=\"70000001\" cooldown-group=\"own\"/>");
                dump = Fake.Dump(p);
                Assert.That(Has(p.Warnings, "cooldown group"), Is.False, dump);
                Assert.That(Text(p, K, "skilldata/780.xml"), Does.Contain("<stateAttr battle=\"1\" cooldownGroupID=\"78000001\" />"), dump);
                foreach (string a in new[] { G, S })
                    Assert.That(Text(p, a, "skill/78/78000001.xml"), Does.Contain("<stateAttr battle=\"1\" cooldownGroupID=\"78000001\" />"), a + dump);

                p = f.Plan("<skill id=\"78000001\" clone=\"70000001\" cooldown-group=\"0\"/>");
                Assert.That(Text(p, G, "skill/78/78000001.xml"), Does.Contain("cooldownGroupID=\"0\""), Fake.Dump(p));

                // a source with no group (70000002 has no stateAttr on any side) gives no warning
                p = f.Plan("<skill id=\"78000002\" clone=\"70000002\"/>");
                Assert.That(Has(p.Warnings, "cooldown group"), Is.False, Fake.Dump(p));
                p = f.Plan("<skill id=\"78000002\" clone=\"70000002\" cooldown-group=\"own\"/>");
                Assert.That(Has(p.Warnings, "has no basic/stateAttr"), Is.True, Fake.Dump(p));

                Assert.Throws<ModKitException>(() => FakeSkills.ModOf("<skill id=\"70000001\" cooldown-group=\"own\"><level value=\"1\" sp=\"1\"/></skill>"));
                Assert.Throws<ModKitException>(() => FakeSkills.ModOf("<skill id=\"78000001\" clone=\"70000001\" cooldown-group=\"mine\"/>"));
                Assert.That(FakeSkills.ModOf("<skill id=\"78000001\" clone=\"70000001\" cooldown-group=\"own\"/>").ToXml(), Does.Contain("cooldown-group=\"own\""));
            }
        }

        [Test]
        public void AMissingLevelOrAttackIsAWarningAndSkipped()
        {
            using (var f = new FakeSkills())
            {
                Plan p = f.Plan("<skill id=\"70000002\"><level value=\"1,3\" sp=\"9\"/><attack level=\"1\" motion=\"1\" index=\"2\" distance=\"5\"/></skill>");
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(Has(p.Warnings, "level 3 is not there"), Is.True, dump);
                Assert.That(Has(p.Warnings, "attack 2 is not there"), Is.True, dump);
                Assert.That(Text(p, G, "skill/70/70000002.xml"), Does.Contain("<stat sp=\"9\" />"), dump);
            }
        }

        [Test]
        public void OnlyTheVariantTheSideReadsIsChanged()
        {
            using (var f = new FakeSkills())
            {
                Plan p = f.Plan("<skill id=\"70000001\"><level value=\"2\" cooldown=\"2\"/></skill>");
                string dump = Fake.Dump(p);
                string kms = Text(p, K, "skilldata/700.xml");
                Assert.That(kms, Does.Contain("cooldown=\"7000\" stringParam=\"160,7\">"), dump);
                Assert.That(kms, Does.Contain("cooldown=\"2000\" stringParam=\"160,6\" feature=\"FakeOn\""), dump);
                Assert.That(kms, Does.Contain("cooldown=\"5000\" stringParam=\"160,5\" feature=\"FakeOff\""), dump);
                Assert.That(Has(p.Warnings, "several variants"), Is.True, dump);
            }
        }

        const string CloneOp = "<skill id=\"78000001\" clone=\"70000001\" levels=\"1\" name=\"My Slash\">" +
            "<own effect=\"71000001\" as=\"77100001\"/>" +
            "<level value=\"1\" cooldown=\"2\"/>" +
            "<tooltip level=\"1\" kms-param=\"150,2\" gms-text=\"hits for 150%, 2 s\"/>" +
            "</skill>";

        [Test]
        public void ACloneCopiesOneSideAtATimeAndRepointsItsOwnEffect()
        {
            using (var f = new FakeSkills())
            {
                Plan p = f.Plan(CloneOp);
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                string band = Text(p, K, "skilldata/780.xml");
                Assert.That(band, Does.StartWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ms2>\n\t<skill id=\"78000001\" comment=\"made up\">"), dump + band);
                Assert.That(band, Does.Contain("<level value=\"1\" cooldown=\"2000\" stringParam=\"150,2\" sp=\"4\">"), band);
                Assert.That(band, Does.Contain("hitAdditionalID=\"77100001\""), band);
                Assert.That(band, Does.Not.Contain("value=\"2\""), "level 2 is not kept");
                Assert.That(Text(p, K, "skilldata/700.xml"), Is.Null, "the source file is not changed");
                string eff = Text(p, K, "additional/771.xml");
                Assert.That(eff, Does.Contain("<additional id=\"77100001\" mainType=\"1\">"), dump);
                Assert.That(Text(p, K, "string/kr/skillname.xml"), Does.Contain("<key id=\"78000001\" name=\"My Slash\" />"), dump);
                Assert.That(Text(p, K, "string/kr/stringskilldescription.xml"), Does.Contain("<key id=\"78000001\" name=\"hits for #0#%\" />"), dump);
                Assert.That(Text(p, K, "string/kr/stringadditionaldescription.xml"), Does.Contain("<key id=\"77100001\" level=\"1\" name=\"Fake Bleed\" tooltipDescription=\"bleeds\" />"), dump);
                foreach (string a in new[] { G, S })
                {
                    string gms = Text(p, a, "skill/78/78000001.xml");
                    Assert.That(gms, Does.Contain("<beginCondition cooldownTime=\"2\" />"), a + dump);
                    Assert.That(gms, Does.Contain("hitEffectID=\"77100001\""), a + gms);
                    Assert.That(gms, Does.Not.Contain("value=\"2\""), a);
                    Assert.That(Text(p, a, "additionaleffect/77100001.xml"), Is.EqualTo(FakeSkills.GmsEffect), a);
                    string names = Text(p, a, "string/en/skillname.xml");
                    Assert.That(names, Does.Contain("<key id=\"78000001\" name=\"My Slash\" />"), a + dump);
                    string desc = Text(p, a, "string/en/korskilldescription.xml");
                    Assert.That(desc, Does.Contain("<key id=\"78000001\" level=\"1\" uiDescription=\"hits for 150%, 2 s\" />"), a + dump);
                    Assert.That(desc, Does.Not.Contain("id=\"78000001\" level=\"2\""), a + desc);
                    Assert.That(Text(p, a, "string/en/koradditionaldescription.xml"), Does.Contain("<key id=\"77100001\" level=\"1\" name=\"Fake Bleed\""), a + dump);
                }
                Assert.That(Has(p.Warnings, "the tooltip is not"), Is.False, dump);
                Assert.That(Has(p.Warnings, "refers to"), Is.False, dump);
                Assert.That(Has(p.Warnings, "icon"), Is.False, dump);
            }
        }

        [Test]
        public void ChecksWarnAboutIdsFilesSequencesIconsAndRanges()
        {
            using (var f = new FakeSkills())
            {
                Plan p = f.Plan("<skill id=\"70000099\" clone=\"70000001\" levels=\"1\">" +
                    "<raw side=\"kms-client\" level=\"1\" at=\"motion\"><set name=\"sequenceName\" value=\"Bite_A\"/><set name=\"strTagEffects\" value=\"e0=CH/fake/none.xml\"/></raw>" +
                    "<raw side=\"kms-client\" level=\"1\" at=\"motion/attack/damage\"><set name=\"hitAdditionalID\" value=\"71009999\"/></raw>" +
                    "</skill>");
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(Has(p.Warnings, "belong in 78000000-78099999"), Is.True, dump);
                Assert.That(Has(p.Warnings, "sequence Bite_A is not a player"), Is.True, dump);
                Assert.That(Has(p.Warnings, "effect file CH/fake/none.xml is not in"), Is.True, dump);
                Assert.That(Has(p.Warnings, "refers to effect 71009999"), Is.True, dump);

                p = f.Plan("<skill id=\"70000001\"><raw side=\"kms-client\" level=\"1\" at=\"motion\"><set name=\"sequenceName\" value=\"Attack_B\"/></raw></skill>");
                Assert.That(Has(p.Warnings, "not a player"), Is.False, Fake.Dump(p));

                p = f.Plan("<skill id=\"70000001\" clone=\"70000002\"/>");
                Assert.That(Has(p.Warnings, "ships id 70000001 already"), Is.True, Fake.Dump(p));
            }
        }

        [Test]
        public void AMotionSwapCopiesTheSourceMotion()
        {
            using (var f = new FakeSkills())
            {
                Plan p = f.Plan("<skill id=\"70000001\"><swap level=\"1\" motion=\"1\" from-skill=\"70000002\" from-level=\"1\" from-motion=\"1\"/></skill>");
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                string kms = Text(p, K, "skilldata/700.xml");
                Assert.That(kms, Does.Contain("sp=\"4\">\n\t\t\t<motion sequenceName=\"Attack_B\">\n\t\t\t\t<attack point=\"1\" targetCount=\"1\" />\n\t\t\t</motion>\n\t\t</level>"), dump + kms);
                Assert.That(Text(p, G, "skill/70/70000001.xml"), Does.Contain("<motionProperty sequenceName=\"Attack_B\""), dump);
            }
        }

        [Test]
        public void EffectDurationIsTicks()
        {
            using (var f = new FakeSkills())
            {
                Plan p = f.Plan("<effect id=\"71000001\"><level value=\"1\" duration=\"30\" stacks=\"3\"/><tooltip level=\"1\" kms-param=\"30\" gms-text=\"bleeds for 30 s\"/></effect>");
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(Text(p, K, "additional/710.xml"), Does.Contain("<Basic durationTick=\"30000\" intervalTick=\"1000\" maxBuffCount=\"3\" />"), dump);
                Assert.That(Text(p, K, "additional/710.xml"), Does.Contain("<level level=\"1\" stringParam=\"30\">"), dump);
                Assert.That(Text(p, G, "additionaleffect/71000001.xml"), Does.Contain("durationTick=\"30000\""), dump);
                Assert.That(Has(p.Warnings, "stacks has no"), Is.False, "maxBuffCount is an attribute, absent on gms but its element is there\n" + dump);
                Assert.That(Text(p, G, "string/en/koradditionaldescription.xml"), Does.Contain("tooltipDescription=\"bleeds for 30 s\""), dump);
            }
        }

        [Test]
        public void JobRowsAreReplacedOrAdded()
        {
            using (var f = new FakeSkills())
            {
                Plan p = f.Plan(CloneOp + "<jobskill job=\"90\" skill=\"78000001\" replace=\"70000002\" max-level=\"5\" learn-level=\"7\"/>");
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(Text(p, K, "table/job.xml"), Does.Contain("<v skillID=\"78000001\" jobLevel=\"1\" uiPosition=\"1,2\" maxLevel=\"5\" requireLevel=\"7\" />"), dump);
                Assert.That(Text(p, G, "table/job.xml"), Does.Contain("<skill main=\"78000001\" uiPosition=\"1, 2\" maxLevel=\"5\" />"), dump);
                Assert.That(Has(p.Warnings, "has no field for learn-level"), Is.True, dump);
                Assert.That(Has(p.Warnings, "nor made by this mod"), Is.False, dump);

                p = f.Plan(CloneOp + "<jobskill job=\"90\" skill=\"78000001\" position=\"1,2\" max-level=\"5\"/>");
                dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(Has(p.Warnings, "position 1,2 of tier 1 is taken by skill 70000002"), Is.True, dump);
                Assert.That(Text(p, K, "table/job.xml"), Does.Contain("\t\t\t<v skillID=\"70000002\" jobLevel=\"1\" uiPosition=\"1,2\" maxLevel=\"10\" />\n\t\t\t<v skillID=\"78000001\" jobLevel=\"1\" uiPosition=\"1,2\" maxLevel=\"5\" />\n\t\t</skills>"), dump);
                Assert.That(Text(p, S, "table/job.xml"), Does.Contain("\t\t\t<skill main=\"78000001\" uiPosition=\"1, 2\" maxLevel=\"5\" />\n\t\t</skills>"), dump);
            }
        }

        [Test]
        public void TheManifestRoundTrips()
        {
            string body = CloneOp + Main + "<effect id=\"71000001\"><level value=\"*\" duration=\"*1.5\"/></effect>" +
                "<jobskill job=\"90\" skill=\"78000001\" position=\"3,4\" tier=\"2\" require-skills=\"70000001\" require-levels=\"2\"/>";
            Mod a = FakeSkills.ModOf(body);
            Mod b = Mod.Parse(a.ToXml(), "again");
            Assert.That(b.ToXml(), Is.EqualTo(a.ToXml()));
            Assert.That(a.Skills.Count, Is.EqualTo(3));
            Assert.That(a.JobSkills.Count, Is.EqualTo(1));
            Assert.Throws<ModKitException>(() => FakeSkills.ModOf("<skill id=\"1\"><level value=\"1\" nope=\"3\"/></skill>"));
            Assert.Throws<ModKitException>(() => FakeSkills.ModOf("<jobskill job=\"1\" skill=\"2\"/>"));
        }

        static string Sha(string path)
        {
            using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(File.ReadAllBytes(path)));
        }

        [Test]
        public void ApplyThenRevertLeavesTheArchivesAsShipped()
        {
            using (var f = new FakeSkills())
            {
                var before = new Dictionary<string, string>();
                foreach (string d in new[] { "kms", "gms", "server" }) before[d] = Sha(Path.Combine(f.Dir, d, "Xml.m2d"));
                var ws = new Workspace(Path.Combine(f.Dir, "ws"));
                Mod m = FakeSkills.ModOf(CloneOp + Main + "<jobskill job=\"90\" skill=\"78000001\" replace=\"70000002\"/>");
                ApplyResult r = ws.Apply(f.Data, new[] { m }, new ApplyOptions { VerifyAll = true });
                Assert.That(r.Applied, Is.True, Fake.Dump(r.Plan));
                ws.Revert(f.Data, null, null);
                Assert.That(ws.ReadState(), Is.Null);
                foreach (KeyValuePair<string, string> kv in before) Assert.That(Sha(Path.Combine(f.Dir, kv.Key, "Xml.m2d")), Is.EqualTo(kv.Value), kv.Key);
            }
        }
    }
}
