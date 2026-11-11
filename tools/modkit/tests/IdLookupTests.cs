using System.Collections.Generic;
using System.IO;
using MS2.ModKit;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class IdLookupTests
    {
        static EditorSession Session(string dir)
        {
            return new EditorSession(Path.Combine(dir, "kms"), Path.Combine(dir, "gms"), Path.Combine(dir, "server"), Path.Combine(dir, "mods"), Path.Combine(dir, "ws"));
        }

        [Test]
        public void KindsOfFieldsAndConditionCodes()
        {
            Assert.That(IdLookup.QuestCodeKind("npc"), Is.EqualTo(IdLookup.Npc));
            Assert.That(IdLookup.QuestCodeKind("item_exist"), Is.EqualTo(IdLookup.Item));
            Assert.That(IdLookup.QuestCodeKind("quest_clear"), Is.EqualTo(IdLookup.Quest));
            Assert.That(IdLookup.QuestCodeKind("skill"), Is.EqualTo(IdLookup.Skill));
            Assert.That(IdLookup.QuestCodeKind("buff"), Is.EqualTo(IdLookup.Effect), "a buff is an additional effect");
            Assert.That(IdLookup.QuestCodeKind("map"), Is.Null, "no list of maps to pick from");
            Assert.That(IdLookup.QuestCodeKind("talk"), Is.Null);
            Assert.That(IdLookup.NpcFieldKind("@shopId"), Is.EqualTo(IdLookup.Shop));
            Assert.That(IdLookup.NpcFieldKind("dropiteminfo@globalDeadDropBoxId"), Is.EqualTo(IdLookup.DropBox));
            Assert.That(IdLookup.NpcFieldKind("individualDropBoxId"), Is.EqualTo(IdLookup.DropBox));
            Assert.That(IdLookup.NpcFieldKind("@level"), Is.Null);
            Assert.That(IdLookup.GroupRowFieldKind(GroupKind.Shop, "id"), Is.EqualTo(IdLookup.Item));
            Assert.That(IdLookup.GroupRowFieldKind(GroupKind.Shop, "price"), Is.Null);
            Assert.That(IdLookup.GroupRowFieldKind(GroupKind.DropBox, "itemID"), Is.EqualTo(IdLookup.Item));
            Assert.That(IdLookup.GroupRowFieldKind(GroupKind.DropBox, "id"), Is.Null);
        }

        [Test]
        public void FindsQuestsByIdOrName_AndSaysWhatItHasNoListOf()
        {
            using (var f = new FakeQuests(true, true, true))
            using (EditorSession s = Session(f.Dir))
            {
                s.Adopt(f.Mod);
                var l = new IdLookup(s);
                Assert.That(l.Find(IdLookup.Quest, "", 10).Count, Is.EqualTo(2));
                List<EntityHit> h = l.Find(IdLookup.Quest, "clay", 10);
                Assert.That(h.Count, Is.EqualTo(1));
                Assert.That(h[0].Id, Is.EqualTo(702));
                Assert.That(l.Find(IdLookup.Quest, "701", 10)[0].Name, Is.EqualTo("Fake Errand"));
                Assert.That(l.Find(IdLookup.Quest, "", 1).Count, Is.EqualTo(1), "at most max");
                s.Quests().Clone(701, 750);
                l = new IdLookup(s);
                Assert.That(l.Find(IdLookup.Quest, "", 10)[0].Id, Is.EqualTo(750), "the mod's new ids first");
                Assert.That(l.Find(IdLookup.Quest, "", 10)[0].Added, Is.True);
                Assert.Throws<ModKitException>(delegate { l.Find("map", "", 10); });
            }
        }

        [Test]
        public void FindsSkillsAndEffects_TheModsClonesFirst()
        {
            using (var f = new FakeSkills())
            using (EditorSession s = Session(f.Dir))
            {
                var l = new IdLookup(s);
                List<EntityHit> h = l.Find(IdLookup.Skill, "slash", 10);
                Assert.That(h.Count, Is.EqualTo(1));
                Assert.That(h[0].Id, Is.EqualTo(70000001));
                Assert.That(h[0].Name, Is.EqualTo("Fake Slash"));
                Assert.That(l.Find(IdLookup.Effect, "bleed", 10)[0].Id, Is.EqualTo(71000001));
                s.Skills().Clone(SkillKind.Skill, 70000001, 70000099, null, null, null);
                h = new IdLookup(s).Find(IdLookup.Skill, "slash", 10);
                Assert.That(h.Count, Is.EqualTo(2));
                Assert.That(h[0].Id, Is.EqualTo(70000099));
                Assert.That(h[0].Added, Is.True);
                Assert.That(h[0].Name, Is.EqualTo("Fake Slash"), "a clone without a name of its own has its source's");
                Assert.That(new IdLookup(s).Find(IdLookup.Skill, "7000009", 10)[0].Id, Is.EqualTo(70000099));
            }
        }
    }
}
