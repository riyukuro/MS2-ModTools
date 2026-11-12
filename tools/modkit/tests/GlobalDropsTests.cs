using System;
using System.Collections.Generic;
using System.IO;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    /// <summary>A data set with the two global drop tables on the server and a few items. Every id is made up.</summary>
    public sealed class FakeDrops : IDisposable
    {
        public readonly string Dir;
        public readonly DataSet Data;
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        public const string Boxes = Head + "<ms2>\n"
            + "\t<dropBox dropBoxID=\"9300001\" comment=\"meadow\">\n"
            + "\t\t<v dropGroupIDs=\"9400001\" minLevel=\"1\" maxLevel=\"20\" dropCount=\"0,1\" dropCountProbability=\"9000,1000\" isOwnerDrop=\"1\" />\n"
            + "\t\t<v dropGroupIDs=\"9400002\" minLevel=\"21\" dropCount=\"0,1,2\" dropCountProbability=\"5000,4000,1000\" />\n"
            + "\t</dropBox>\n"
            + "\t<dropBox dropBoxID=\"9300002\" comment=\"cave\">\n"
            + "\t\t<v dropGroupIDs=\"9400002\" minLevel=\"1\" dropCount=\"1\" dropCountProbability=\"10000\" />\n"
            + "\t</dropBox>\n</ms2>\n";
        public const string Sets = Head + "<ms2>\n"
            + "\t<dropBox dropGroupID=\"9400001\" comment=\"herbs\">\n"
            + "\t\t<v itemID=\"50100001\" minLevel=\"1\" minCount=\"1\" maxCount=\"3\" />\n"
            + "\t\t<v itemID=\"50100002\" minLevel=\"1\" grade=\"3\" uiItemRank=\"3\" />\n"
            + "\t</dropBox>\n"
            + "\t<dropBox dropGroupID=\"9400002\" comment=\"ores\">\n"
            + "\t\t<v itemID=\"50100003\" minLevel=\"1\" weight=\"30\" />\n"
            + "\t\t<v itemID=\"50100001\" minLevel=\"1\" weight=\"70\" />\n"
            + "\t</dropBox>\n</ms2>\n";

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        public FakeDrops()
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-drops-" + Guid.NewGuid().ToString("N"));
            string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
            foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);
            M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E("itemdata/item_0.xml", Head + "<ms2>\n\t<item id=\"50100001\" />\n\t<item id=\"50100002\" />\n\t<item id=\"50100003\" />\n</ms2>\n"),
            });
            foreach (string d in new[] { g, s })
            {
                var l = new List<KeyValuePair<string, byte[]>>();
                foreach (long i in new long[] { 50100001, 50100002, 50100003 }) l.Add(E("item/5/01/" + i + ".xml", Head + "<ms2>\n\t<environment />\n</ms2>\n"));
                M2dWriter.Create(Path.Combine(d, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, l);
            }
            M2dWriter.Create(Path.Combine(s, "Server.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E(GlobalDropRules.BoxEntry, Boxes), E(GlobalDropRules.SetEntry, Sets),
            });
            Data = new DataSet(k, g, s);
        }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class GlobalDropsTests
    {
        static Mod Empty() { return FakeRooms.ModOf("", "drops.test"); }

        static string Body(Mod m)
        {
            string x = m.ToXml();
            int a = x.IndexOf('>', x.IndexOf("<mod", StringComparison.Ordinal)) + 1, b = x.LastIndexOf("</mod>", StringComparison.Ordinal);
            return x.Substring(a, b - a);
        }

        static Mod Reload(Mod m) { return FakeRooms.ModOf(Body(m), "drops.test"); }
        static Plan PlanOf(FakeDrops f, Mod m) { return Planner.Build(f.Data, new[] { Reload(m) }, new PlanOptions()); }

        static List<string> Lines(IEnumerable<PlanMessage> msgs)
        {
            var l = new List<string>();
            foreach (PlanMessage m in msgs) l.Add(m.ToString());
            return l;
        }

        [Test]
        public void BoxesAndGroupsAreListedWithTheirLines()
        {
            using (var f = new FakeDrops())
            {
                var ed = new GlobalDropEditor(f.Data, Empty());
                Assert.That(ed.Boxes().Count, Is.EqualTo(2));
                Assert.That(ed.Groups().Count, Is.EqualTo(2));
                DropHolder box = ed.Box(9300001);
                Assert.That(ed.Get(box, GlobalDropRules.Comment), Is.EqualTo("meadow"));
                List<DropLine> links = ed.Lines(box);
                Assert.That(links.Count, Is.EqualTo(2));
                Assert.That(ed.GroupsOf(links[1]), Is.EqualTo(new long[] { 9400002 }));
                Assert.That(ed.Get(links[1], GlobalDropRules.Chance), Is.EqualTo("5000,4000,1000"));
                Assert.That(ed.BoxesUsing(9400002).Count, Is.EqualTo(2));
                Assert.That(ed.BoxesUsing(9400001).Count, Is.EqualTo(1));
                Assert.That(ed.FindGroups("50100003").Count, Is.EqualTo(1));
                Assert.That(ed.FindBoxes("50100002").Count, Is.EqualTo(1));       // through its group
                Assert.That(ed.FindBoxes("CAVE").Count, Is.EqualTo(1));
                Assert.That(ed.Check(box), Is.Empty);
                Assert.That(ed.Check(ed.Group(9400002)), Is.Empty);
                Assert.That(ed.IsChanged(box), Is.False);
            }
        }

        [Test]
        public void AnItemAddedToAShippedGroupSurvivesAReload()
        {
            using (var f = new FakeDrops())
            {
                Mod mod = Empty();
                var ed = new GlobalDropEditor(f.Data, mod);
                DropHolder g = ed.Group(9400001);
                DropLine item = ed.AddItem(g, 50100003);
                ed.Set(item, GlobalDropRules.MinCount, "2");
                ed.Set(item, GlobalDropRules.MaxCount, "4");
                ed.Set(item, GlobalDropRules.Grade, "4");
                DropLine first = ed.Lines(g)[0];
                ed.Set(first, GlobalDropRules.MaxCount, "5");
                ed.Remove(ed.Lines(g)[1]);
                Assert.That(Body(mod), Does.Contain("in=\"dropBox[dropGroupID=9400001]\""));

                var again = new GlobalDropEditor(f.Data, Reload(mod));
                DropHolder g2 = again.Group(9400001);
                Assert.That(again.IsChanged(g2), Is.True);
                Assert.That(again.IsChanged(again.Group(9400002)), Is.False);
                List<DropLine> lines = again.Lines(g2);
                Assert.That(lines.Count, Is.EqualTo(3));
                Assert.That(again.Get(lines[0], GlobalDropRules.MaxCount), Is.EqualTo("5"));
                Assert.That(again.IsEdited(lines[0], GlobalDropRules.MaxCount), Is.True);
                Assert.That(lines[1].IsDeleted, Is.True);
                Assert.That(lines[2].IsAdded, Is.True);
                Assert.That(again.Get(lines[2], GlobalDropRules.Item), Is.EqualTo("50100003"));
                Assert.That(again.Get(lines[2], GlobalDropRules.Grade), Is.EqualTo("4"));
                again.Restore(lines[1]);
                again.Revert(lines[0]);
                again.Remove(lines[2]);
                Assert.That(again.IsChanged(g2), Is.False);

                Plan p = PlanOf(f, mod);
                Assert.That(Lines(p.Errors), Is.Empty, Fake.Dump(p));
                Assert.That(Lines(p.Warnings), Is.Empty, Fake.Dump(p));
            }
        }

        [Test]
        public void ANewGroupAndBoxKeepTheirLinesInOneAdd()
        {
            using (var f = new FakeDrops())
            {
                Mod mod = Empty();
                var ed = new GlobalDropEditor(f.Data, mod);
                Assert.That(ed.FreeId(true), Is.EqualTo(9400003));
                DropHolder g = ed.AddGroup(9400010, "gems");
                Assert.That(ed.Check(g), Has.Exactly(1).Contains("has no item"));
                DropLine a = ed.AddItem(g, 50100002), b = ed.AddItem(g, 50100003);
                ed.Set(a, GlobalDropRules.Weight, "10");
                ed.Set(b, GlobalDropRules.Weight, "90");
                ed.Remove(ed.Clone(b));
                Assert.That(ed.Check(g), Has.Exactly(1).Contains("no box has a line"));
                DropHolder box = ed.Clone(ed.Box(9300002), 9300010, "cave copy");
                Assert.That(ed.Lines(box).Count, Is.EqualTo(1));
                DropLine link = ed.AddLink(box, 9400010);
                Assert.That(ed.Check(link), Is.Empty);
                Assert.That(ed.Check(g), Is.Empty);
                ed.SetComment(g, "gems & more");
                Assert.Throws<ModKitException>(() => ed.AddBox(9300001, ""));
                Assert.Throws<ModKitException>(() => ed.AddItem(box, 50100001));

                var again = new GlobalDropEditor(f.Data, Reload(mod));
                DropHolder g2 = again.Group(9400010), b2 = again.Box(9300010);
                Assert.That(g2.IsAdded && b2.IsAdded, Is.True);
                Assert.That(again.Get(g2, GlobalDropRules.Comment), Is.EqualTo("gems & more"));
                Assert.That(again.Lines(g2).Count, Is.EqualTo(2));
                Assert.That(again.Get(again.Lines(g2)[1], GlobalDropRules.Weight), Is.EqualTo("90"));
                Assert.That(again.Lines(b2).Count, Is.EqualTo(2));
                Assert.That(again.GroupsOf(again.Lines(b2)[1]), Is.EqualTo(new long[] { 9400010 }));
                Assert.That(again.BoxesUsing(9400010).Count, Is.EqualTo(1));

                Plan p = PlanOf(f, mod);
                Assert.That(Lines(p.Errors), Is.Empty, Fake.Dump(p));
                Assert.That(Lines(p.Warnings), Is.Empty, Fake.Dump(p));

                again.Remove(g2);
                Assert.That(again.Group(9400010), Is.Null);
                Assert.That(again.Check(again.Lines(b2)[1]), Has.Exactly(1).Contains("no item group 9400010"));
            }
        }

        [Test]
        public void RemovingAShippedBoxDropsItsLinesAndCanBeTakenBack()
        {
            using (var f = new FakeDrops())
            {
                Mod mod = Empty();
                var ed = new GlobalDropEditor(f.Data, mod);
                DropHolder box = ed.Box(9300001);
                ed.AddLink(box, 9400002);
                ed.Set(ed.Lines(box)[0], GlobalDropRules.MaxLevel, "30");
                ed.Remove(box);
                Assert.That(box.IsDeleted, Is.True);
                Assert.That(ed.Box(9300001), Is.Null);
                Assert.That(Body(mod), Does.Not.Contain("maxLevel"));
                Assert.That(ed.BoxesUsing(9400001), Is.Empty);
                ed.Restore(box);
                Assert.That(ed.Lines(box).Count, Is.EqualTo(2));
                Assert.That(ed.IsChanged(box), Is.False);
            }
        }

        [Test]
        public void LinesAreCheckedByThemselves()
        {
            Func<string, string, string, string, Func<string, string>> link = (g, lv, c, p) => n =>
                n == GlobalDropRules.Link ? g : n == GlobalDropRules.MinLevel ? lv : n == GlobalDropRules.MaxLevel ? "10" : n == GlobalDropRules.Count ? c : n == GlobalDropRules.Chance ? p : null;
            Assert.That(GlobalDropRules.CheckLink(link("9400001", "1", "0,1", "9000,1000")), Is.Empty);
            Assert.That(GlobalDropRules.CheckLink(link("9400001", "11", "0,1", "9000")), Has.Count.EqualTo(2));
            Assert.That(GlobalDropRules.CheckLink(link("x", "1", "0", "10000")), Has.Count.EqualTo(2));
            Assert.That(GlobalDropRules.CheckLink(link("9400001", "1", "1,a", "0,0")), Has.Exactly(1).Contains("not a list of whole numbers"));

            Func<string, string, string, string, Func<string, string>> item = (id, lo, hi, grade) => n =>
                n == GlobalDropRules.Item ? id : n == GlobalDropRules.MinCount ? lo : n == GlobalDropRules.MaxCount ? hi : n == GlobalDropRules.Grade ? grade : null;
            Assert.That(GlobalDropRules.CheckItem(item("50100001", "1", "3", "4")), Is.Empty);
            Assert.That(GlobalDropRules.CheckItem(item("50100001", null, null, null)), Is.Empty);
            Assert.That(GlobalDropRules.CheckItem(item("50100001", "4", "3", "7")), Has.Count.EqualTo(2));
            Assert.That(GlobalDropRules.CheckItem(item("0", "0", "3", "-1")), Has.Count.EqualTo(3));
        }

        [Test]
        public void PlanChecksTheLinesTheModsAddOrChange()
        {
            using (var f = new FakeDrops())
            {
                Mod mod = FakeRooms.ModOf(
                    "<edit archive=\"server:Server.m2d\" entry=\"table/Server/globalDropItemSet_Final.xml\">\n" +
                    "  <add in=\"dropBox[dropGroupID=9400001]\"><xml><v itemID=\"50199999\" minLevel=\"1\" minCount=\"5\" maxCount=\"2\" grade=\"9\" /></xml></add>\n" +
                    "  <add><xml><dropBox dropGroupID=\"9400002\" comment=\"twice\">\n\t<v itemID=\"50100001\" weight=\"0\" />\n</dropBox></xml></add>\n" +
                    "  <add><xml><dropBox dropGroupID=\"9400020\" comment=\"loose\">\n\t<v itemID=\"50100001\" />\n</dropBox></xml></add>\n" +
                    "</edit>\n" +
                    "<edit archive=\"server:Server.m2d\" entry=\"table/Server/globalDropItemBox_Final.xml\">\n" +
                    "  <add in=\"dropBox[dropBoxID=9300002]\"><xml><v dropGroupIDs=\"9400099\" minLevel=\"1\" dropCount=\"0,1\" dropCountProbability=\"10000\" /></xml></add>\n" +
                    "  <add><xml><dropBox dropBoxID=\"9300020\" comment=\"empty\" /></xml></add>\n" +
                    "</edit>", "drops.test");
                Plan p = Planner.Build(f.Data, new[] { mod }, new PlanOptions());
                List<string> e = Lines(p.Errors), w = Lines(p.Warnings);
                Assert.That(e, Has.Exactly(1).Contains("item group 9400001, item 50199999: the server has no item 50199999"), Fake.Dump(p));
                Assert.That(e, Has.Exactly(1).Contains("item group 9400002: the table ends up with 2 with this id"), Fake.Dump(p));
                Assert.That(e, Has.Exactly(1).Contains("global drop box 9300002, line 2 (group 9400099)").And.Contains("no item group 9400099"), Fake.Dump(p));
                Assert.That(e.Count, Is.EqualTo(3), Fake.Dump(p));
                Assert.That(w, Has.Exactly(1).Contains("minCount 5 is above maxCount 2"), Fake.Dump(p));
                Assert.That(w, Has.Exactly(1).Contains("grade 9 is not a rarity"), Fake.Dump(p));
                Assert.That(w, Has.Exactly(1).Contains("item group 9400002: every weight of the group is 0"), Fake.Dump(p));
                Assert.That(w, Has.Exactly(1).Contains("item group 9400020: no global drop box has a line for the group"), Fake.Dump(p));
                Assert.That(w, Has.Exactly(1).Contains("dropCount has 2 numbers and dropCountProbability has 1"), Fake.Dump(p));
                Assert.That(w, Has.Exactly(1).Contains("global drop box 9300020: the box has no line"), Fake.Dump(p));
                Assert.That(w.Count, Is.EqualTo(6), Fake.Dump(p));
            }
        }

        [Test]
        public void PlanAcceptsALineChangedToAnotherShippedItem()
        {
            using (var f = new FakeDrops())
            {
                Mod mod = FakeRooms.ModOf(
                    "<edit archive=\"server:Server.m2d\" entry=\"table/Server/globalDropItemSet_Final.xml\">\n" +
                    "  <change at=\"dropBox[dropGroupID=9400002]/v[itemID=50100003]\"><set name=\"itemID\" value=\"50100002\" /></change>\n" +
                    "</edit>", "drops.test");
                Plan p = Planner.Build(f.Data, new[] { mod }, new PlanOptions());
                Assert.That(Lines(p.Errors), Is.Empty, Fake.Dump(p));
                Assert.That(Lines(p.Warnings), Is.Empty, Fake.Dump(p));
            }
        }
    }
}
