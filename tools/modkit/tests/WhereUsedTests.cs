using System.Collections.Generic;
using System.IO;
using MS2.ModKit;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class WhereUsedTests
    {
        static EditorSession Session(string dir)
        {
            return new EditorSession(Path.Combine(dir, "kms"), Path.Combine(dir, "gms"), Path.Combine(dir, "server"), Path.Combine(dir, "mods"), Path.Combine(dir, "ws"));
        }

        static List<string> Lines(List<UseHit> hits)
        {
            var l = new List<string>();
            foreach (UseHit h in hits) l.Add(h.Kind + " " + h.Id + ": " + h.Where + (h.New ? " (new)" : "") + (h.SourceNew ? " [added]" : ""));
            return l;
        }

        static KeyValuePair<string, string> A(string n, string v) { return new KeyValuePair<string, string>(n, v); }

        [Test]
        public void ShopsAndDropBoxesNameItems_NpcsNameDropBoxes()
        {
            using (var f = new FakeGroups())
            using (EditorSession s = Session(f.Dir))
            {
                List<UseHit> h = new WhereUsed(s).Of(IdLookup.Item, 5001);
                Assert.That(Lines(h), Is.EqualTo(new[] { "shop 9001: sn 1 id", "shop 9002: sn 1 id", "drop box 7001: group 1 item", "drop box 7100: group 1 item" }),
                    "a row named twice in one group, and in the nested and the flat tables, is one place");
                Assert.That(h[0].Field, Is.EqualTo("id"));
                Assert.That(h[0].Index, Is.EqualTo(0));
                Assert.That(new WhereUsed(s).Of(IdLookup.Item, 5003)[0].Index, Is.EqualTo(3), "the fourth stock row of shop 9001");
                Assert.That(Lines(new WhereUsed(s).Of(IdLookup.DropBox, 7001)), Is.EqualTo(new[] { "npc 2900001: dropiteminfo@individualDropBoxId" }));
                Assert.That(new WhereUsed(s).Of(IdLookup.Item, 5999).Count, Is.EqualTo(0));
                Assert.Throws<ModKitException>(delegate { new WhereUsed(s).Of(IdLookup.Map, 1); });
            }
        }

        [Test]
        public void TheModsRowsAndClonesShow_AndTheIndexIsKeptWithTheData()
        {
            using (var f = new FakeGroups())
            using (EditorSession s = Session(f.Dir))
            {
                Assert.That(WhereUsed.IsIndexed(s.Data), Is.False);
                new WhereUsed(s).Of(IdLookup.Item, 5001);
                Assert.That(WhereUsed.IsIndexed(s.Data), Is.True, "the shipped index is kept with the data set");
                GroupEditor g = s.Groups();
                g.AddRow(GroupKind.DropBox, 7003, "1", new[] { A("itemID", "5001"), A("weight", "5") });
                long shop = g.Clone(GroupKind.Shop, 9002, 0);
                List<UseHit> h = new WhereUsed(s).Of(IdLookup.Item, 5001);
                Assert.That(Lines(h), Is.EqualTo(new[] {
                    "drop box 7003: group 1 item (new)",
                    "shop " + shop + ": sn 1 id (new) [added]",
                    "shop 9001: sn 1 id", "shop 9002: sn 1 id", "drop box 7001: group 1 item", "drop box 7100: group 1 item" }), "the mod's places first");
                Assert.That(h[1].Name, Is.EqualTo("copy of 9002"));
                Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Item, 5002)), Does.Contain("drop box 7003: group 1 item"), "an edited box keeps its shipped rows, not new");
            }
        }

        [Test]
        public void RawRowEditsOfTheGroupTablesCountAsTheMods()
        {
            using (var f = new FakeGroups())
            using (EditorSession s = Session(f.Dir))
            {
                // the Tables view's edits: a stock cell of shop 9002 and a flat row of box 7100
                TableDraft shop = s.OpenTable(Planner.ServerTables, Planner.ShopStockEntry, "shop[shopID=9002]");
                shop.Set(shop.Rows[0], "id", "5005");
                TableDraft flat = s.OpenTable(Profile.Server.ArchiveIn("x:Xml.m2d"), Planner.DefaultDropSplit, null);
                foreach (TableRow r in flat.Rows)
                    if (flat.Get(r, "individualDropBoxID") == "7100") flat.Set(r, "item", "5006");
                Assert.That(s.Mod.Groups.Count, Is.EqualTo(0), "raw rows only, no group op");

                List<UseHit> h = new WhereUsed(s).Of(IdLookup.Item, 5005);
                Assert.That(Lines(h), Is.EqualTo(new[] { "shop 9002: sn 1 id (new)" }), "the mod's value shows, marked");
                Assert.That(h[0].Name, Is.EqualTo("changed"));
                Assert.That(h[0].Index, Is.EqualTo(0));
                Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Item, 5001)), Is.EqualTo(new[] { "shop 9001: sn 1 id", "drop box 7001: group 1 item" }),
                    "the edited shop and box no longer name 5001 (the plan mirrors the flat edit to the other side)");
                Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Item, 5006)), Is.EqualTo(new[] { "drop box 7100: group 1 item (new)" }));
                Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Item, 5003)), Does.Contain("shop 9002: sn 2 id"), "an unedited row of the edited shop stays, not new");
                Assert.That(new WhereUsed(s).LeftBehind(IdLookup.Item, 5005), Does.Contain("shop 9002"));
            }
        }

        [Test]
        public void QuestsNameNpcsAndItems_AsTheModLeavesThem()
        {
            using (var f = new FakeQuests(true, true, true))
            using (EditorSession s = Session(f.Dir))
            {
                s.Adopt(f.Mod);
                Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Npc, 31)), Is.EqualTo(new[] { "quest 701: start npc" }));
                Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Npc, 41)), Is.EqualTo(new[] { "quest 701: condition 1 (npc)" }));
                List<UseHit> c = new WhereUsed(s).Of(IdLookup.Npc, 41);
                Assert.That(c[0].Field, Is.EqualTo("condition"));
                Assert.That(c[0].Index, Is.EqualTo(0));
                Assert.That(c[0].Name, Is.EqualTo("Fake Errand"));
                Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Item, 9001)), Is.EqualTo(new[] { "quest 701: completeReward essentialItem", "quest 701: completeReward v" }), "the quest file's list and the table's");
                Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Item, 9100)), Is.EqualTo(new[] { "quest 701: acceptReward essentialItem", "quest 701: acceptReward v" }));
                Assert.That(new WhereUsed(s).Of(IdLookup.Npc, 32).Count, Is.EqualTo(1), "a talk code is free text: only the complete npc");

                s.Quests().Open(701).SetStartNpc(51);
                Assert.That(new WhereUsed(s).Of(IdLookup.Npc, 31).Count, Is.EqualTo(0), "the mod's start npc replaces the shipped one");
                Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Npc, 51)), Is.EqualTo(new[] { "quest 701: start npc (new)" }));
                s.Quests().Clone(701, 750);
                Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Npc, 41)), Is.EqualTo(new[] { "quest 750: condition 1 (npc) (new) [added]", "quest 701: condition 1 (npc)" }));
                Assert.That(new WhereUsed(s).LeftBehind(IdLookup.Npc, 41), Is.EqualTo(
                    "warning: 2 place(s) still name npc 41:\n" +
                    "  quest 750, condition 1 (npc), Fake Errand\n" +
                    "  quest 701, condition 1 (npc), Fake Errand"), "from, field, name: the places themselves");
                Assert.That(new WhereUsed(s).LeftBehind(IdLookup.Npc, 99), Is.Null);

                for (long q = 751; q <= 761; q++) s.Quests().Clone(701, q);
                string[] w = new WhereUsed(s).LeftBehind(IdLookup.Npc, 41).Split('\n');
                Assert.That(w[0], Is.EqualTo("warning: 13 place(s) still name npc 41:"));
                Assert.That(w.Length, Is.EqualTo(12), "the first 10, then the rest counted");
                Assert.That(w[11], Is.EqualTo("  and 3 more"));
                Assert.That(string.Join("\n", w), Does.Not.Contain("Where used"), "nothing points at a dialog");
            }
        }
        [Test]
        public void ItemsNameTheirRepackingScrolls_WhenTheModSaysSo()
        {
            using (var f = new FakeItems(true))
            using (var s = new EditorSession(Path.Combine(f.Dir, "client"), Path.Combine(f.Dir, "server"), Path.Combine(f.Dir, "mods"), Path.Combine(f.Dir, "ws")) { Maps = FakeItems.Maps })
            {
                Assert.That(new WhereUsed(s).Of(IdLookup.Item, 1200002).Count, Is.EqualTo(0), "no shipped item names it");
                ItemDraft d = s.Items().Open(1200001);
                d.Set(FakeItems.Section(d, "item", "environment/property"), "rePackingScrollID", "1200002,1200003");
                List<UseHit> h = new WhereUsed(s).Of(IdLookup.Item, 1200002);
                Assert.That(Lines(h), Is.EqualTo(new[] { "item 1200001: property@rePackingScrollID (new)" }));
                Assert.That(h[0].Field, Is.EqualTo("property@rePackingScrollID"), "the field of the item view's grid");
                Assert.That(h[0].Name, Is.Not.Empty);
                Assert.That(new WhereUsed(s).Of(IdLookup.Item, 1200003).Count, Is.EqualTo(1), "an id list names each of its ids");
                EntityDraft c = s.Items().CloneAny(1200001, 1200050);
                foreach (EntitySection sec in c.Sections)
                    if (sec.Elem != null && sec.Elem.Name == "property" && sec.Part.Kind == c.Editor.Entity) c.Set(sec, "rePackingScrollID", "1200002");
                Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Item, 1200002)), Is.EqualTo(new[] { "item 1200050: property@rePackingScrollID (new) [added]", "item 1200001: property@rePackingScrollID (new)" }));
                s.Items().RemoveClone(1200050);
                Assert.That(new WhereUsed(s).Of(IdLookup.Item, 1200050).Count, Is.EqualTo(0));
            }
        }
    }
}
