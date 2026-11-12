// The shop and drop box editor (GroupEditor.cs) on the made-up data of FakeGroups.
using System.Collections.Generic;
using MS2.ModKit;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class GroupEditorTests
    {
        static KeyValuePair<string, string> A(string k, string v) { return new KeyValuePair<string, string>(k, v); }

        static Mod Empty() { return FakeGroups.ModOf("", "groups.edit"); }

        static GroupRowView Row(GroupSideView s, string item, int n)
        {
            int c = 0;
            foreach (GroupRowView r in s.Rows) if ((r.Get("itemID") ?? r.Get("item")) == item && ++c == n) return r;
            return null;
        }

        [Test]
        public void ShippedTablesAreReadOnce_AndStayWithinTheBudget()
        {
            using (var f = new FakeGroups())
            {
                var ed = new GroupEditor(f.Data, Empty());
                GroupView v = ed.Open(GroupKind.Shop, 9001);
                int files = f.Data.DocCount;
                long bytes = f.Data.DocBytes;
                Assert.That(files, Is.GreaterThan(0));
                // the same and another shop, an edit in between: the tables are the ones already held
                for (int i = 0; i < 20; i++) { ed.Open(GroupKind.Shop, 9001); ed.Open(GroupKind.Shop, 9002); }
                ed.SetRowField(v, v.Sides[0], v.Sides[0].Rows[0], "price", "778");
                Assert.That(ed.Open(GroupKind.Shop, 9001).Sides[0].Rows[0].Get("price"), Is.EqualTo("778"));
                Assert.That(new GroupEditor(f.Data, Empty()).Open(GroupKind.Shop, 9001).Sides[0].Rows[0].Get("price"), Is.Not.EqualTo("778"), "the shipped table is not changed");
                Assert.That(f.Data.DocCount, Is.EqualTo(files));
                Assert.That(f.Data.DocBytes, Is.EqualTo(bytes));

                // a small budget: tables are let go and read again, with the same result
                f.Data.DocBudget = 1;
                for (int i = 0; i < 5; i++)
                {
                    Assert.That(ed.Open(GroupKind.Shop, 9002).Sides.Count, Is.EqualTo(1));
                    Assert.That(f.Data.DocCount, Is.LessThanOrEqualTo(2));
                }
            }
        }

        [Test]
        public void ShopViewAfterTheModAndEdits()
        {
            using (var f = new FakeGroups())
            {
                Mod m = Empty();
                var ed = new GroupEditor(f.Data, m);
                Assert.That(ed.ShippedIds(GroupKind.Shop), Is.EqualTo(new long[] { 9001, 9002 }));
                GroupView v = ed.Open(GroupKind.Shop, 9001);
                Assert.That(v.Sides.Count, Is.EqualTo(1), "shop stock is on the server only");
                GroupSideView s = v.Sides[0];
                Assert.That(s.Rows.Count, Is.EqualTo(4));
                Assert.That(s.Rows[1].Feature, Is.EqualTo("F1"), "an sn with variants keys on the feature");
                Assert.That(s.Rows[0].Feature, Is.Null);

                ed.SetRowField(v, s, s.Rows[0], "price", "777");
                ed.SetRowField(v, s, s.Rows[2], "price", "1");
                ed.SetHeader(GroupKind.Shop, 9001, "iconName", "fake2");
                ed.AddRow(GroupKind.Shop, 9001, null, new[] { A("id", "5002"), A("price", "5"), A("category", "AA") });
                ed.RemoveRow(v, s.Rows[3]);
                v = ed.Open(GroupKind.Shop, 9001);
                s = v.Sides[0];
                Assert.That(s.Rows.Count, Is.EqualTo(4));
                Assert.That(s.Rows[0].Get("price"), Is.EqualTo("777"));
                Assert.That(s.Rows[0].IsChanged);
                Assert.That(s.Rows[1].Get("price"), Is.EqualTo("200"), "the other variant of sn 2 stays");
                Assert.That(s.Rows[2].Get("price"), Is.EqualTo("1"));
                Assert.That(s.Rows[3].IsAdded);
                Assert.That(s.Rows[3].Get("sn"), Is.EqualTo("4"), "the planner gives the next free sn");
                Assert.That(s.Removed.Count, Is.EqualTo(1));
                Assert.That(s.Removed[0].Sn, Is.EqualTo("3"));
                Assert.That(s.Headers[0], Has.Member(A("iconName", "fake2")));

                // an edit of the added row goes into its add op; a value set back to the shipped one drops the op
                ed.SetRowField(v, s, s.Rows[3], "price", "6");
                Assert.That(s.Rows[3].AddOp.Xml, Does.Contain("price=\"6\""));
                ed.SetRowField(v, s, s.Rows[0], "price", "100");
                ed.Restore(v, s.Removed[0]);
                v = ed.Open(GroupKind.Shop, 9001);
                Assert.That(v.Sides[0].Rows[0].IsChanged, Is.False);
                Assert.That(v.Sides[0].Removed, Is.Empty);

                // the mod text holds ops only, and reads back the same
                Mod back = Mod.Parse(m.ToXml(), "again");
                Assert.That(back.ToXml(), Is.EqualTo(m.ToXml()));
                Assert.That(m.ToXml(), Does.Not.Contain("price=\"200\""), "no copy of shipped rows");

                // the view's "shipped value" column: an editor with an empty mod shows the group as shipped
                var shipped = new GroupEditor(f.Data, Mod.Parse("<mod id=\"shipped\" name=\"shipped\" version=\"1\"></mod>", "shipped"));
                Assert.That(shipped.Open(GroupKind.Shop, 9001).Sides[0].Rows[0].Get("price"), Is.EqualTo("100"));
            }
        }

        [Test]
        public void DropRowsWithTheSameItemStayApart()
        {
            using (var f = new FakeGroups())
            {
                Mod m = Empty();
                var ed = new GroupEditor(f.Data, m);
                GroupView v = ed.Open(GroupKind.DropBox, 7001);
                // nested: server final and Korean client final; flat: server Xml and global client
                Assert.That(v.Sides.Count, Is.EqualTo(4));
                GroupSideView server = v.Side(Planner.ServerTables), flat = v.Side("gms-client:Xml.m2d");
                Assert.That(server.Rows.Count, Is.EqualTo(4));
                Assert.That(server.Groups.Count, Is.EqualTo(2));
                Assert.That(flat.Flat);
                Assert.That(flat.Rows.Count, Is.EqualTo(4));
                GroupRowView second = Row(server, "5001", 2);
                Assert.That(second.N, Is.EqualTo(2));
                Assert.That(second.Get("weight"), Is.EqualTo("20"));

                // edit the second 5001 row on the server: only that row changes, on every target that has it
                ed.SetRowField(v, server, second, "itemID", "5003");
                v = ed.Open(GroupKind.DropBox, 7001);
                server = v.Side(Planner.ServerTables);
                Assert.That(server.Rows[0].Get("itemID"), Is.EqualTo("5001"));
                Assert.That(server.Rows[2].Get("itemID"), Is.EqualTo("5003"), "the row keeps its place");
                Assert.That(server.Rows[2].Item, Is.EqualTo("5001"), "and its identity");
                Assert.That(server.Rows[2].IsChanged);
                flat = v.Side("gms-client:Xml.m2d");
                Assert.That(flat.Rows[2].Get("item"), Is.EqualTo("5003"));
                Assert.That(flat.Rows[0].Get("item"), Is.EqualTo("5001"));
                GroupSideView kms = v.Side("client:Xml.m2d");
                Assert.That(kms.Rows.Count, Is.EqualTo(2), "the Korean client ships two rows only");
                Assert.That(kms.Rows[0].Get("itemID"), Is.EqualTo("5001"));
                Assert.That(v.Lines.Count, Is.GreaterThan(0), "the Korean client has no second 5001 row: a warning line");

                // an edit from a flat target is mapped to nested names
                ed.SetRowField(v, flat, flat.Rows[0], "minCount", "3");
                GroupOp g = m.FindGroup(GroupKind.DropBox, 7001);
                Assert.That(g.Rows.Count, Is.EqualTo(2));
                Assert.That(g.Rows[1].Item, Is.EqualTo("5001"));
                Assert.That(g.Rows[1].N, Is.EqualTo(1));
                v = ed.Open(GroupKind.DropBox, 7001);
                Assert.That(v.Side(Planner.ServerTables).Rows[0].Get("minCount"), Is.EqualTo("3"));

                // add a row and remove one; the order stays (kept rows, then added rows)
                ed.AddRow(GroupKind.DropBox, 7001, "1", new[] { A("itemID", "5002"), A("weight", "5") });
                ed.RemoveRow(v, v.Side(Planner.ServerTables).Rows[1]);
                v = ed.Open(GroupKind.DropBox, 7001);
                server = v.Side(Planner.ServerTables);
                Assert.That(server.Rows.Count, Is.EqualTo(4));
                Assert.That(server.Rows[1].Item, Is.EqualTo("5001"));
                Assert.That(server.Rows[1].N, Is.EqualTo(2));
                Assert.That(server.Rows[2].IsAdded);
                Assert.That(server.Rows[3].Group, Is.EqualTo("2"));
                Assert.That(server.Removed.Count, Is.EqualTo(1));
                Assert.That(server.Removed[0].Item, Is.EqualTo("5002"));
                flat = v.Side("gms-client:Xml.m2d");
                Assert.That(flat.Rows.Count, Is.EqualTo(4));
                Assert.That(flat.Rows[2].IsAdded);
            }
        }

        [Test]
        public void CloneAndNewTakeReservedIds()
        {
            using (var f = new FakeGroups())
            {
                Mod m = Empty();
                var ed = new GroupEditor(f.Data, m);
                long shop = ed.Clone(GroupKind.Shop, 9002, 0);
                Assert.That(shop, Is.EqualTo(900000000));
                long box = ed.New(GroupKind.DropBox, 0, "<dropBox><group dropGroupID=\"1\"><v itemID=\"5001\" weight=\"1\" /><v itemID=\"5001\" weight=\"2\" /></group></dropBox>");
                Assert.That(box, Is.EqualTo(900010000));
                long box2 = ed.Clone(GroupKind.DropBox, 7003, 0);
                Assert.That(box2, Is.EqualTo(900010001));
                Assert.That(m.Ids.Count, Is.EqualTo(2), "one range per space");

                GroupView v = ed.Open(GroupKind.Shop, shop);
                Assert.That(v.IsNew);
                Assert.That(v.Sides[0].Rows.Count, Is.EqualTo(2));
                GroupSideView s = v.Sides[0];
                ed.SetRowField(v, s, s.Rows[1], "price", "21");
                v = ed.Open(GroupKind.Shop, shop);
                Assert.That(v.Sides[0].Rows[1].Get("price"), Is.EqualTo("21"));
                Assert.That(ed.Open(GroupKind.Shop, 9002).Sides[0].Rows[1].Get("price"), Is.EqualTo("20"), "the source stays");

                v = ed.Open(GroupKind.DropBox, box);
                GroupSideView server = v.Side(Planner.ServerTables);
                Assert.That(server.Rows.Count, Is.EqualTo(2));
                ed.SetRowField(v, server, server.Rows[1], "weight", "7");
                Assert.That(m.FindGroup(GroupKind.DropBox, box).Xml, Does.Contain("weight=\"7\""), "a new box is edited in its own xml");
                v = ed.Open(GroupKind.DropBox, box);
                Assert.That(v.Side(Planner.ServerTables).Rows[1].Get("weight"), Is.EqualTo("7"));
                Assert.That(v.Side("gms-client:Xml.m2d").Rows.Count, Is.EqualTo(2));

                List<EntityHit> hits = ed.Find(GroupKind.DropBox, "9000", 10);
                Assert.That(hits.Count, Is.EqualTo(2));
                Assert.That(hits[0].Added);

                // a group the mod adds can be copied too: the copy has its rows and edits, under its own id
                long box3 = ed.Clone(GroupKind.DropBox, box, 0);
                Assert.That(box3, Is.EqualTo(900010002));
                Assert.That(ed.Open(GroupKind.DropBox, box3).Side(Planner.ServerTables).Rows[1].Get("weight"), Is.EqualTo("7"));
                long shop2 = ed.Clone(GroupKind.Shop, shop, 0);
                Assert.That(ed.Open(GroupKind.Shop, shop2).Sides[0].Rows[1].Get("price"), Is.EqualTo("21"));
                ed.SetRowField(v, server, server.Rows[1], "weight", "8");
                Assert.That(ed.Open(GroupKind.DropBox, box3).Side(Planner.ServerTables).Rows[1].Get("weight"), Is.EqualTo("7"), "the copy is apart from its source");
                Assert.Throws<ModKitException>(delegate { ed.Clone(GroupKind.DropBox, 123456, 0); });
                ed.Discard(GroupKind.DropBox, box3);
                ed.Discard(GroupKind.Shop, shop2);

                ed.Discard(GroupKind.DropBox, box);
                Assert.That(ed.Exists(GroupKind.DropBox, box), Is.False);
                Assert.That(m.Ids.Count, Is.EqualTo(2));
                Plan p = Planner.Build(f.Data, new[] { Mod.Parse(m.ToXml(), "again") }, new PlanOptions());
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
            }
        }
    }
}
