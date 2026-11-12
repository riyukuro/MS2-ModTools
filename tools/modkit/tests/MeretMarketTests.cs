// MeretMarket on a made-up shop: a table/Server/shop_merat_custom.xml in the server's Server.m2d and a
// table/na/meratmarketcategory.xml in its Xml.m2d (made-up ids, items, prices and texts).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    sealed class FakeShop : IDisposable
    {
        public readonly string Dir;
        public readonly DataSet Data;

        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";
        const string Jobs = "1,10,20,30,40,50,60,70,80,90,100,110";

        static string ItemRow(int id, int tab, int item, string price, string extra, string end = "")
        {
            return "\t<item id=\"" + id + "\" tabID=\"" + tab + "\" banner=\"\" bannerTag=\"0\" itemID=\"" + item + "\" grade=\"1\" quantity=\"1\" bonusQuantity=\"0\" durationDay=\"0\" saleTag=\"0\" paymentType=\"1\" price=\"" + price + "\" salePrice=\"0\" saleStartTime=\"\" saleEndTime=\"" + end + "\" jobRequire=\"" + Jobs + "\" noRestock=\"false\" minLevel=\"0\" maxLevel=\"0\" achieveID=\"0\" achieveGrade=\"0\" pcCafe=\"false\" giftable=\"true\" showSaleTime=\"false\" promoName=\"\" promoSaleStartTime=\"\" promoSaleEndTime=\"\"" + extra;
        }

        public static readonly string Table = Head + "<ms2>\n" +
            ItemRow(100, 21, 39000001, "50", " />\n") +
            ItemRow(101, 22, 39000002, "200", ">\n\t\t<additionalQuantity id=\"102\" quantity=\"10\" bonusQuantity=\"1\" price=\"1800\" salePrice=\"0\" />\n\t\t<additionalQuantity id=\"103\" quantity=\"30\" bonusQuantity=\"5\" price=\"5000\" salePrice=\"4500\" />\n\t</item>\n") +
            ItemRow(104, 21, 39000003, "10", " />\n", "2001-01-01-00-00-00") +
            ItemRow(105, 21, 39000001, "10", " />\n") +
            ItemRow(105, 21, 39000002, "10", " />\n") +
            "</ms2>\n";

        public const string Tabs = Head + "<ms2>\n\t<category id=\"1\" tabType=\"sort\">\n\t\t<tab name=\"All\" id=\"1\" />\n\t\t<tab name=\"Wear\" id=\"20\">\n\t\t\t<tab name=\"Hats\" id=\"21\" />\n\t\t\t<tab name=\"Tops\" id=\"22\" />\n\t\t</tab>\n\t</category>\n</ms2>\n";

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        public FakeShop()
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-shop-" + Guid.NewGuid().ToString("N"));
            string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
            foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);
            string item = Head + "<ms2>\n\t<item />\n</ms2>\n";
            var items = new List<KeyValuePair<string, byte[]>> { E("item/39/39000001.xml", item), E("item/39/39000002.xml", item), E("item/39/39000003.xml", item) };
            M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>> { E("itemdata/39.xml", Head + "<ms2>\n\t<item id=\"39000001\" />\n</ms2>\n") });
            M2dWriter.Create(Path.Combine(g, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, items);
            var srv = new List<KeyValuePair<string, byte[]>>(items) { E(MeretMarket.TabEntry, Tabs) };
            M2dWriter.Create(Path.Combine(s, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, srv);
            M2dWriter.Create(Path.Combine(s, "Server.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>> { E(MeretMarket.Entry, Table) });
            Data = new DataSet(k, g, s);
        }

        public static Mod NewMod() { return Mod.Parse("<mod id=\"shop.test\" name=\"t\" version=\"1\" />", "test mod"); }

        public Plan Plan(Mod m) { return Planner.Build(Data, new[] { Mod.Parse(m.ToXml(), "round trip") }, new PlanOptions()); }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class MeretMarketTests
    {
        static List<string> Texts(List<PlanMessage> l, string part) { return l.Select(m => m.Text).Where(t => t.Contains(part)).ToList(); }

        [Test]
        public void ReadsTheShop()
        {
            using (var f = new FakeShop())
            {
                var s = new MeretMarket(f.Data, FakeShop.NewMod());
                Assert.That(s.Has, Is.True);
                Assert.That(s.Items().Select(i => i.Id), Is.EqualTo(new[] { 100, 101, 104, 105, 105 }));
                MeretItem two = s.Find(101);
                Assert.That(s.Get(two, "price"), Is.EqualTo("200"));
                Assert.That(s.Bundles(two).Select(b => b.Id), Is.EqualTo(new[] { 102, 103 }));
                Assert.That(s.BundleGet(two, s.Bundles(two)[1], "salePrice"), Is.EqualTo("4500"));
                Assert.That(s.Find(105).Rows, Is.EqualTo(2));
                Assert.That(s.IdUsed(103), Is.True);
                Assert.That(s.FreeId(100), Is.EqualTo(106));
                Assert.That(s.TabIds().OrderBy(x => x), Is.EqualTo(new[] { 1, 20, 21, 22 }));
                Assert.That(MeretMarket.JobsText(s.Get(two, "jobRequire")), Is.EqualTo("every job"));
                Assert.That(MeretMarket.JobsText("10,20"), Is.EqualTo("Knight, Berserker"));
                Assert.That(MeretMarket.JobsText(""), Is.EqualTo("nobody"));
            }
        }

        [Test]
        public void EditsAShippedItemAndItsBundles()
        {
            using (var f = new FakeShop())
            {
                Mod m = FakeShop.NewMod();
                var s = new MeretMarket(f.Data, m);
                MeretItem two = s.Find(101);
                Assert.That(s.SetField(two, "salePrice", "150"), Is.True);
                Assert.That(s.SetField(two, "saleTag", "4"), Is.True);
                Assert.That(s.SetBundle(two, 103, "salePrice", "4000"), Is.True);
                s.RemoveBundle(two, 102);
                s.AddBundle(two, 106, "60", "10", "9000", "");
                Assert.That(s.IsEdited(two), Is.True);
                Assert.That(s.IsEdited(s.Find(100)), Is.False);

                var back = new MeretMarket(f.Data, Mod.Parse(m.ToXml(), "round trip"));
                MeretItem b = back.Find(101);
                Assert.That(back.Get(b, "salePrice"), Is.EqualTo("150"));
                Assert.That(back.Bundles(b).Select(x => x.Id), Is.EqualTo(new[] { 103, 106 }));
                Assert.That(back.BundleGet(b, back.Bundles(b)[0], "salePrice"), Is.EqualTo("4000"));
                Assert.That(back.BundleGet(b, back.Bundles(b)[1], "quantity"), Is.EqualTo("60"));
                Assert.That(back.IdUsed(106), Is.True);
                Assert.That(back.SetBundle(b, 106, "price", "8000"), Is.True);
                Assert.That(back.BundleGet(b, back.Bundles(b)[1], "price"), Is.EqualTo("8000"));

                Plan p = f.Plan(m);
                Assert.That(Texts(p.Errors, "Meret Market item 101"), Is.Empty);
                // the game server puts each bundle under its sale, in any order: no warning for a bundle
                Assert.That(Texts(p.Warnings, "Meret Market item 101"), Is.Empty);
                var fin = p.Errors.Concat(p.Warnings).Select(x => x.Text).Where(t => t.Contains("Meret Market") && !t.Contains("item 101")).ToList();
                Assert.That(fin, Is.Empty);
                // the set line under the row reaches the shipped bundle
                s.SetBundle(two, 103, "salePrice", "-5");
                Assert.That(Texts(f.Plan(m).Errors, "Meret Market item 101: bundle 103: the price is below 0"), Has.Count.EqualTo(1));
                s.SetBundle(two, 103, "salePrice", "4000");
                // a sale the game server leaves out takes its bundles with it
                s.SetField(two, "itemID", "39999999");
                Plan gone = f.Plan(m);
                Assert.That(Texts(gone.Errors, "Meret Market item 101: item 39999999 is not in the server's items"), Has.Count.EqualTo(1));
                Assert.That(Texts(gone.Errors, "Meret Market item 101: 2 bundles: the sale is left out, so the game server drops them too"), Has.Count.EqualTo(1));
            }
        }

        [Test]
        public void TheShippedValueTakesTheLineOut()
        {
            using (var f = new FakeShop())
            {
                Mod m = FakeShop.NewMod();
                var s = new MeretMarket(f.Data, m);
                MeretItem two = s.Find(101);
                s.SetField(two, "price", "300");
                s.SetBundle(two, 102, "quantity", "11");
                Assert.That(m.Ops.Count, Is.EqualTo(1));
                Assert.That(s.SetField(two, "price", "300"), Is.False);
                s.SetField(two, "price", "200");
                s.SetBundle(two, 102, "quantity", "10");
                Assert.That(m.Ops, Is.Empty);
                Assert.That(s.IsEdited(two), Is.False);
                s.AddBundle(two, 107, "2", "0", "1", "0");
                s.RemoveBundle(two, 107);
                Assert.That(m.Ops, Is.Empty);
            }
        }

        [Test]
        public void RefusesWhatTheServerDoesNotRead()
        {
            using (var f = new FakeShop())
            {
                Mod m = FakeShop.NewMod();
                var s = new MeretMarket(f.Data, m);
                MeretItem one = s.Find(100);
                Assert.Throws<ModKitException>(() => s.SetField(one, "price", "12.5"));
                Assert.Throws<ModKitException>(() => s.SetField(one, "giftable", "yes"));
                Assert.Throws<ModKitException>(() => s.SetField(one, "saleTag", "300"));
                Assert.Throws<ModKitException>(() => s.SetField(one, "minLevel", "40000"));
                Assert.Throws<ModKitException>(() => s.SetField(one, "jobRequire", "10,15"));
                Assert.Throws<ModKitException>(() => s.SetField(one, "saleEndTime", "2099-01-01"));
                Assert.Throws<ModKitException>(() => s.SetField(one, "id", "200"));
                Assert.Throws<ModKitException>(() => s.SetField(one, "color", "1"));
                Assert.Throws<ModKitException>(() => s.SetField(s.Find(105), "price", "1"));
                Assert.Throws<ModKitException>(() => s.AddBundle(one, 103, "1", "0", "1", "0"));
                Assert.Throws<ModKitException>(() => s.SetBundle(s.Find(101), 102, "tabID", "1"));
                Assert.Throws<ModKitException>(() => s.New(102, "39000001", "21"));
                Assert.That(m.Ops, Is.Empty);
                Assert.That(s.SetField(one, "saleEndTime", "2099-01-01-00-00-00"), Is.True);
                Assert.That(s.SetField(one, "saleEndTime", ""), Is.True);
                Assert.That(s.SetField(one, "giftable", "0"), Is.True);
            }
        }

        [Test]
        public void NewCopiedAndRemovedItems()
        {
            using (var f = new FakeShop())
            {
                Mod m = FakeShop.NewMod();
                var s = new MeretMarket(f.Data, m);
                MeretItem n = s.New(s.FreeId(100), "39000003", "22");
                Assert.That(n.Id, Is.EqualTo(106));
                Assert.That(s.SetField(n, "price", "75"), Is.True);
                s.AddBundle(n, 107, "5", "1", "350", "");
                s.SetBundle(n, 107, "salePrice", "300");
                MeretItem c = s.Copy(101, 108);
                Assert.That(s.Get(c, "price"), Is.EqualTo("200"));
                Assert.That(s.Bundles(c), Is.Empty);
                s.Remove(s.Find(104));
                Assert.That(s.IsRemoved(s.Find(104)), Is.True);
                Assert.Throws<ModKitException>(() => s.SetField(s.Find(104), "price", "1"));

                var back = new MeretMarket(f.Data, Mod.Parse(m.ToXml(), "round trip"));
                MeretItem b = back.Find(106);
                Assert.That(b.IsAdded, Is.True);
                Assert.That(back.Get(b, "itemID"), Is.EqualTo("39000003"));
                Assert.That(back.Get(b, "price"), Is.EqualTo("75"));
                Assert.That(back.Bundles(b).Single().Attr("salePrice"), Is.EqualTo("300"));
                Assert.That(back.EditedCount(), Is.EqualTo(3));

                Plan p = f.Plan(m);
                Assert.That(Texts(p.Errors, "Meret Market"), Is.Empty);
                Assert.That(Texts(p.Warnings, "Meret Market item 106"), Is.Empty);
                Assert.That(Texts(p.Warnings, "Meret Market item 108"), Is.Empty);
                Assert.That(Texts(p.Warnings, "Meret Market item 104"), Is.Empty);

                Assert.That(s.TakeAll(), Is.EqualTo(3));
                Assert.That(m.Ops, Is.Empty);
            }
        }

        [Test]
        public void PlanErrorsOnHandWrittenRows()
        {
            using (var f = new FakeShop())
            {
                Mod m = Mod.Parse("<mod id=\"shop.hand\" name=\"t\" version=\"1\">" +
                    "<edit archive=\"server:Server.m2d\" entry=\"" + MeretMarket.Entry + "\">" +
                    "<change at=\"item[id=100]\"><set name=\"price\" value=\"1.5\" /><set name=\"jobRequire\" value=\"\" /><set name=\"tabID\" value=\"77\" /></change>" +
                    "<change at=\"item[id=104]\"><set name=\"saleStartTime\" value=\"2099-01-01-00-00-00\" /><set name=\"saleEndTime\" value=\"2099-02-01-00-00-00\" /></change>" +
                    "<add><xml><item id=\"110\" tabID=\"21\" itemID=\"39000009\" grade=\"1\" quantity=\"1\" paymentType=\"5\" price=\"-1\" jobRequire=\"10,15\" minLevel=\"50\" maxLevel=\"10\" giftable=\"yes\" saleEndTime=\"2001-01-01-00-00-00\"><additionalQuantity id=\"102\" quantity=\"1\" price=\"1\" /></item></xml></add>" +
                    "</edit></mod>", "hand mod");
                Plan p = f.Plan(m);
                Assert.That(Texts(p.Errors, "Meret Market item 100: price \"1.5\" is not a whole number"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Errors, "Meret Market item 100: jobRequire names no job"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Warnings, "Meret Market item 100: tabID 77 is not a tab"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Errors, "Meret Market item 104"), Is.Empty);
                Assert.That(Texts(p.Warnings, "Meret Market item 104: the sale starts 2099-01-01-00-00-00"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Errors, "Meret Market item 110: item 39000009 is not in the server's items"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Errors, "Meret Market item 110: paymentType 5"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Errors, "Meret Market item 110: the price is below 0"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Errors, "Meret Market item 110: jobRequire 15 is not a job code"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Errors, "Meret Market item 110: minLevel 50 is above maxLevel 10"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Errors, "Meret Market item 110: giftable \"yes\""), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Warnings, "Meret Market item 110: the sale ended 2001-01-01-00-00-00"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Errors, "Meret Market item 110: bundle 102: id 102 is used by 2 rows"), Has.Count.EqualTo(1));
                // rows the mod leaves as shipped are not checked (item 105's two rows, item 104's end before the edit)
                Assert.That(Texts(p.Errors, "Meret Market item 105"), Is.Empty);
                Assert.That(Texts(p.Warnings, "Meret Market item 101"), Is.Empty);
            }
        }

        [Test]
        public void RealServerShop()
        {
            string srv = RealData.Server;
            if (string.IsNullOrEmpty(srv) || !File.Exists(Path.Combine(srv, "Server.m2d"))) Assert.Ignore("MS2_SERVER_DATA is not set");
            using (var data = new DataSet(null, null, srv))
            {
                Mod m = FakeShop.NewMod();
                var s = new MeretMarket(data, m);
                List<MeretItem> items = s.Items();
                Assert.That(items, Is.Not.Empty);
                Assert.That(s.TabIds(), Is.Not.Empty);
                // the first item without bundles whose sale has not ended: a new sale price, copied under a free id
                DateTime now = DateTime.Now;
                MeretItem e = items.First(x => x.Rows == 1 && s.Bundles(x).Count == 0 && (MeretMarket.ParseTime(s.Get(x, "saleEndTime")) ?? DateTime.MaxValue) > now
                    && MeretMarket.JobsText(s.Get(x, "jobRequire")) != "nobody");
                string price = s.Get(e, "price");
                s.SetField(e, "salePrice", "1");
                int free = s.FreeId(e.Id);
                s.Copy(e.Id, free);
                Plan p = Planner.Build(data, new[] { Mod.Parse(m.ToXml(), "round trip") }, new PlanOptions());
                Assert.That(Texts(p.Errors, "Meret Market item " + e.Id + ":"), Is.Empty);
                Assert.That(Texts(p.Errors, "Meret Market item " + free + ":"), Is.Empty);
                TestContext.Progress.WriteLine(items.Count + " Meret Market items; item " + e.Id + " (price " + price + ") edited and copied to " + free);
            }
        }
    }
}
