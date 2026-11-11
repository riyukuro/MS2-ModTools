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
            Assert.That(IdLookup.QuestCodeKind("map"), Is.EqualTo(IdLookup.Map));
            Assert.That(IdLookup.QuestCodeKind("stay_map"), Is.EqualTo(IdLookup.Map));
            Assert.That(IdLookup.QuestCodeKind("interact_object_rep"), Is.EqualTo(IdLookup.InteractObject));
            Assert.That(IdLookup.QuestCodeKind("dungeon_rank_clear"), Is.EqualTo(IdLookup.Dungeon));
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
                Assert.Throws<ModKitException>(delegate { l.Find("portal", "", 10); });
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

        static Mod ModOf(string body)
        {
            Mod m = Mod.Parse("<mod id=\"idlists.test\" name=\"t\" version=\"1\">\n" + body + "\n</mod>", "test mod");
            return Mod.Parse(m.ToXml(), "round trip");
        }

        static List<long> Ids(List<EntityHit> hits)
        {
            var l = new List<long>();
            foreach (EntityHit h in hits) l.Add(h.Id);
            return l;
        }

        [Test]
        public void FindsMaps_FromTheTableAndTheMapFiles_TheModsNewMapFirst()
        {
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                var l = new IdLookup(s);
                Assert.That(Ids(l.Find(IdLookup.Map, "", 10)), Is.EqualTo(new long[] { 9100001, 9100002, 9100003 }), "the kms table and the global map files");
                List<EntityHit> h = l.Find(IdLookup.Map, "harbor", 10);
                Assert.That(h.Count, Is.EqualTo(1));
                Assert.That(h[0].Id, Is.EqualTo(9100002));
                Assert.That(h[0].Name, Is.EqualTo("Fake Harbor"));
                Assert.That(l.Find(IdLookup.Map, "9100003", 10)[0].Name, Is.EqualTo("Fake Far Shore"));
                Assert.That(l.Find(IdLookup.Map, "", 2).Count, Is.EqualTo(2), "at most max");
                s.Adopt(ModOf(
                    "<edit archive=\"client:Xml.m2d\" entry=\"table/fielddata.xml\"><add copy=\"fieldData[id=9100001]\"><set name=\"id\" value=\"9100050\"/></add></edit>" +
                    "<edit archive=\"client:Xml.m2d\" entry=\"string/en/mapname.xml\"><add><xml><key id=\"9100050\" name=\"Fake New Meadow\" /></xml></add></edit>" +
                    "<entry archive=\"server:Xml.m2d\" name=\"map/09100060.xml\" copy=\"map/09100001.xml\"/>"));
                h = new IdLookup(s).Find(IdLookup.Map, "", 10);
                Assert.That(Ids(h), Is.EqualTo(new long[] { 9100050, 9100060, 9100001, 9100002, 9100003 }), "the mod's new rows and files first");
                Assert.That(h[0].Added && h[1].Added && !h[2].Added, Is.True);
                Assert.That(h[0].Name, Is.EqualTo("Fake New Meadow"), "the name the mod gives");
                Assert.That(Ids(new IdLookup(s).Find(IdLookup.Map, "meadow", 10)), Is.EqualTo(new long[] { 9100050, 9100001 }));
            }
        }

        [Test]
        public void FindsInteractObjects_FromBothTables()
        {
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                var l = new IdLookup(s);
                Assert.That(Ids(l.Find(IdLookup.InteractObject, "", 10)), Is.EqualTo(new long[] { 9200001, 9200002, 9200003 }));
                Assert.That(l.Find(IdLookup.InteractObject, "9200002", 10)[0].Name, Is.EqualTo(""), "an object without a name row");
                Assert.That(Ids(l.Find(IdLookup.InteractObject, "herb", 10)), Is.EqualTo(new long[] { 9200003 }), "the mastery table");
                s.Adopt(ModOf("<edit archive=\"client:Xml.m2d\" entry=\"table/interactobject.xml\"><add><xml><interact id=\"9200077\" type=\"1\" /></xml></add></edit>"));
                List<EntityHit> hits = new IdLookup(s).Find(IdLookup.InteractObject, "92000", 10);
                Assert.That(Ids(hits), Is.EqualTo(new long[] { 9200077, 9200001, 9200002, 9200003 }), "the mod's literal row first");
                Assert.That(hits[0].Added, Is.True);
            }
        }

        [Test]
        public void FindsDungeons_InTheRegionFolderToo()
        {
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                var l = new IdLookup(s);
                Assert.That(Ids(l.Find(IdLookup.Dungeon, "", 10)), Is.EqualTo(new long[] { 9300001, 9300002, 9300003 }), "table/ and table/na/");
                Assert.That(Ids(l.Find(IdLookup.Dungeon, "clock", 10)), Is.EqualTo(new long[] { 9300003 }));
                Assert.That(l.Find(IdLookup.Dungeon, "9300001", 10)[0].Name, Is.EqualTo("Fake Cellar"));
                Assert.That(l.Find(IdLookup.Dungeon, "9399999", 10).Count, Is.EqualTo(0), "no such dungeon");
                s.Adopt(ModOf("<edit archive=\"server:Xml.m2d\" entry=\"table/na/dungeonroom.xml\"><add copy=\"dungeonRoom[dungeonRoomID=9300003]\"><set name=\"dungeonRoomID\" value=\"9300010\"/></add></edit>"));
                List<EntityHit> hits = new IdLookup(s).Find(IdLookup.Dungeon, "93000", 10);
                Assert.That(Ids(hits), Is.EqualTo(new long[] { 9300010, 9300001, 9300002, 9300003 }));
                Assert.That(hits[0].Added, Is.True);
            }
        }
    }
}
