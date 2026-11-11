using System;
using System.Collections.Generic;
using System.IO;
using MS2.ModKit;
using MS2.ModKit.M2d;
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
                List<UseHit> second = new WhereUsed(s).Of(IdLookup.Item, 5004);
                Assert.That(Lines(second), Is.EqualTo(new[] { "drop box 7003: group 1 item2" }), "a flat row's second item (both flat sides: one place)");
                Assert.That(second[0].Field, Is.EqualTo("item2"));
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
        public void AGroupWithoutAnIdIsOneLabelOnBothSides()
        {
            string final = FakeGroups.ServerFinal.Replace("</ms2>",
                "  <dropBox dropBoxID=\"7200\">\n    <group>\n      <v itemID=\"5001\" weight=\"1\" />\n      <v itemID=\"5003\" weight=\"1\" />\n    </group>\n  </dropBox>\n</ms2>");
            using (var f = new FakeGroups(final))
            using (EditorSession s = Session(f.Dir))
            {
                Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Item, 5001)), Does.Contain("drop box 7200: group ? item"));
                TableDraft t = s.OpenTable(Planner.ServerTables, Planner.ServerDropEntry, "dropBox[dropBoxID=7200]/group");
                foreach (TableRow r in t.Rows) if (t.Get(r, "itemID") == "5003") t.Set(r, "weight", "2");
                Assert.That(s.Mod.Groups.Count, Is.EqualTo(0), "a raw row edit of the box");
                List<UseHit> h = new WhereUsed(s).Of(IdLookup.Item, 5001);
                Assert.That(Lines(h), Does.Contain("drop box 7200: group ? item"), "the box as the mod leaves it: the same label, so not new");
                Assert.That(Lines(h), Does.Not.Contain("drop box 7200: group ? item (new)"));
                Assert.That(h.Find(x => x.Id == 7200).Name, Is.EqualTo("changed"), "read from the mod's side");
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

        /// <summary>
        /// Quest 801 in three places: the kms table (start npc 31; conditions item_exist 5101, skill 5201, buff 5301,
        /// map 9100001, npc 41; complete reward v 9001), the gms file (npc 41; essentialItem 9001) and the server
        /// file (npc 42; essentialItem 9002, 9001), and a server quest file that does not parse.
        /// </summary>
        sealed class FakeUses : IDisposable
        {
            public readonly string Dir = Path.Combine(Path.GetTempPath(), "modkit-uses-" + Guid.NewGuid().ToString("N"));
            const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

            static string Cond(string type, int code) { return "\t\t\t<condition type=\"" + type + "\" code=\"" + code + "\" value=\"1\" />\n"; }
            static string Item(int code) { return "\t\t\t\t<essentialItem code=\"" + code + "\" rank=\"1\" count=\"1\" />\n"; }

            static string File(string items, string conds)
            {
                return Head + "<ms2>\n\t<environment feature=\"\" locale=\"\">\n\t\t<quest>\n" +
                    "\t\t\t<basic chapter=\"1\" questID=\"" + QuestEditor.Padded(801) + "\" />\n" +
                    "\t\t\t<start npc=\"31\" />\n" +
                    "\t\t\t<completeReward exp=\"10\" money=\"0\">\n" + items + "\t\t\t</completeReward>\n" + conds +
                    "\t\t</quest>\n\t</environment>\n</ms2>\n";
            }

            static void Write(string dir, params KeyValuePair<string, byte[]>[] entries)
            {
                Directory.CreateDirectory(dir);
                M2dWriter.Create(Path.Combine(dir, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>(entries));
            }

            static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

            public FakeUses()
            {
                Write(Path.Combine(Dir, "kms"), E("questdata/questdata.xml", Head + "<ms2>\n\t<quest id=\"801\">\n\t\t<basic chapter=\"1\" />\n\t\t<start npc=\"31\" />\n" +
                    "\t\t<completeReward exp=\"10\">\n\t\t\t<v code=\"9001\" rank=\"1\" count=\"1\" />\n\t\t</completeReward>\n" +
                    "\t\t<condition type=\"item_exist\" code=\"5101\" value=\"1\" />\n\t\t<condition type=\"skill\" code=\"5201\" value=\"1\" />\n" +
                    "\t\t<condition type=\"buff\" code=\"5301\" value=\"1\" />\n\t\t<condition type=\"map\" code=\"9100001\" value=\"1\" />\n" +
                    "\t\t<condition type=\"npc\" code=\"41\" value=\"1\" />\n\t</quest>\n</ms2>\n"));
                Write(Path.Combine(Dir, "gms"), E("quest/00000801.xml", File(Item(9001), Cond("npc", 41))));
                Write(Path.Combine(Dir, "server"), E("quest/00000801.xml", File(Item(9002) + Item(9001), Cond("npc", 42))),
                    E("quest/00000802.xml", Head + "<ms2>\n\t<environment feature=\"\">\n\t\t<quest>\n"));
            }

            public void Dispose() { try { Directory.Delete(Dir, true); } catch (IOException) { } }
        }

        [Test]
        public void QuestConditionsNameItemsSkillsAndBuffs_AndMapsAreNotKept()
        {
            using (var f = new FakeUses())
            using (EditorSession s = Session(f.Dir))
            {
                Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Item, 5101)), Is.EqualTo(new[] { "quest 801: condition 1 (item_exist)" }));
                Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Skill, 5201)), Is.EqualTo(new[] { "quest 801: condition 2 (skill)" }));
                Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Effect, 5301)), Is.EqualTo(new[] { "quest 801: condition 3 (buff)" }));
                WhereUsedStats st = WhereUsed.Stats(s.Data);
                // npc 31, 41, 42; item 5101, 9001, 9002; skill 5201; effect 5301: the map condition is not kept
                Assert.That(st.Targets, Is.EqualTo(8));
            }
        }

        [Test]
        public void OneBadShippedFileIsSkippedAndListed_AndTheQuestionIsAskedOnce()
        {
            using (var f = new FakeUses())
            using (EditorSession s = Session(f.Dir))
            {
                Assert.That(WhereUsed.Stats(s.Data), Is.Null);
                Assert.That(new WhereUsed(s).Of(IdLookup.Npc, 42).Count, Is.EqualTo(1), "the other files are read");
                WhereUsedStats st = WhereUsed.Stats(s.Data);
                Assert.That(st.Skipped.Count, Is.EqualTo(1));
                Assert.That(st.Skipped[0], Does.Contain("00000802"));
                Assert.That(st.ShippedScans, Is.EqualTo(1));
                Assert.That(st.ModScans, Is.EqualTo(1));

                new WhereUsed(s).Of(IdLookup.Npc, 41);
                new WhereUsed(s).LeftBehind(IdLookup.Item, 9001);
                Assert.That(st.ShippedScans, Is.EqualTo(1), "a second question reads nothing shipped again");
                Assert.That(st.ModScans, Is.EqualTo(1), "nor the mod, unchanged");

                s.Quests().Open(801).SetStartNpc(51);
                Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Npc, 51)), Is.EqualTo(new[] { "quest 801: start npc (new)" }));
                Assert.That(st.ModScans, Is.EqualTo(2), "a changed mod is read again");
                Assert.That(st.ShippedScans, Is.EqualTo(1));
                new WhereUsed(s).Of(IdLookup.Npc, 31);
                Assert.That(st.ModScans, Is.EqualTo(2));

                s.Quests().Open(801).SetStartNpc(31);
                Assert.That(new WhereUsed(s).Of(IdLookup.Npc, 51).Count, Is.EqualTo(0), "the shipped index holds no mod place");
                Assert.That(st.ShippedScans, Is.EqualTo(1));
            }
        }

        [Test]
        public void EachPlaceOfAQuestCounts_AsTheModLeavesItToo()
        {
            using (var f = new FakeUses())
            using (EditorSession s = Session(f.Dir))
            {
                Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Npc, 42)), Is.EqualTo(new[] { "quest 801: condition 1 (npc)" }), "the server file's condition");
                s.Quests().Open(801).SetStartNpc(51);
                Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Npc, 42)), Is.EqualTo(new[] { "quest 801: condition 1 (npc)" }),
                    "a touched quest keeps the value only its later place has, and it is not new");

                // reward rows: the server's first row is 9002, merged with the gms file's lists by item
                UseHit h = new WhereUsed(s).Of(IdLookup.Item, 9002)[0];
                Assert.That(h.Field, Is.EqualTo("completeReward/essentialItem"));
                Assert.That(h.Index, Is.EqualTo(0));
                Assert.That(h.Place, Is.Not.Empty);
                QuestReward mine = null, other = null;
                foreach (QuestReward w in s.Quests().Open(801).Rewards("essentialItem")) if (w.Code == "9002") mine = w; else if (w.Code == "9001") other = w;
                Assert.That(mine, Is.Not.Null);
                Assert.That(mine.IsRow(h.Place, h.Index), Is.True, "Go to finds the row by its place and its row there");
                Assert.That(other.IsRow(h.Place, h.Index), Is.False);
                Assert.That(mine.IsRow("no such place", 0), Is.False);
            }
        }

        [Test]
        public void SkillsEffectsAndShopsAreNamed_AndAnItemFieldOnTheShippedPath()
        {
            string dir = Path.Combine(Path.GetTempPath(), "modkit-alluses-" + Guid.NewGuid().ToString("N"));
            try
            {
                FakeAllWriter.Write(dir);
                using (EditorSession s = Session(dir))
                {
                    Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Shop, 9001)), Is.EqualTo(new[] { "npc 2900002: basic@shopId" }));
                    Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Skill, 70000001)), Is.EqualTo(new[] { "npc 2900002: skill@ids", "item 1200001: skill@skillID" }));
                    Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Effect, 71000001)), Is.EqualTo(new[] { "item 1200001: AdditionalEffect@id" }));
                    Assert.That(Lines(new WhereUsed(s).Of(IdLookup.Item, 1200002)), Is.EqualTo(new[] { "item 1200001: property@rePackingScrollID" }), "a shipped item field");
                }
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }
    }
}
