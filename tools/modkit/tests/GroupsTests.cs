// Shops and drop boxes (group entities) on made-up data. The three sides ship different rows on purpose:
//   shops: server only (table/Server/shop_game.xml, shop_game_info.xml); sn repeats across shops (numbered per shop).
//   box 7001: server nested has two rows of item 5001 in group 1 and a group 2 with weight 0; the Korean client
//             nested has one row of 5001 and no group 2; the flat sides have both 5001 rows and group 2.
//   box 7003 lives in the quest split, box 7004 in a region folder (table/na/).
// Every id, item and name in here is made up.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    public sealed class FakeGroups : IDisposable
    {
        public readonly string Dir;
        public readonly DataSet Data;
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";
        const string Bom = "﻿";

        public const string ShopGame = Bom + Head + "<ms2>\n" +
            "\t<shop shopID=\"9001\">\n" +
            "\t\t<item sn=\"1\" id=\"5001\" grade=\"1\" price=\"100\" category=\"AA\" />\n" +
            "\t\t<item sn=\"2\" id=\"5002\" grade=\"1\" price=\"200\" category=\"AA\" feature=\"F1\" />\n" +
            "\t\t<item sn=\"2\" id=\"5002\" grade=\"1\" price=\"250\" category=\"AA\" feature=\"F2\" />\n" +
            "\t\t<item sn=\"3\" id=\"5003\" grade=\"1\" price=\"300\" paymentType=\"1\" paymentItemID=\"5002:tag\" category=\"BB\" />\n" +
            "\t</shop>\n" +
            "\t<shop shopID=\"9002\">\n" +
            "\t\t<item sn=\"1\" id=\"5001\" grade=\"1\" price=\"10\" category=\"AA\" />\n" +
            "\t\t<item sn=\"2\" id=\"5003\" grade=\"1\" price=\"20\" category=\"AA\" />\n" +
            "\t</shop>\n" +
            "</ms2>\n";

        public const string ShopInfo = Head + "<ms2>\n" +
            "\t<shop shopID=\"9001\" categoryID=\"1\" iconName=\"fake\" />\n" +
            "\t<shop shopID=\"9002\" categoryID=\"1\" iconName=\"fake\" />\n" +
            "</ms2>\n";

        public const string ServerFinal = Bom + Head + "<ms2>\n" +
            "  <dropBox dropBoxID=\"7001\" comment=\"made up\">\n" +
            "    <group dropGroupID=\"1\" smartDropRate=\"50\">\n" +
            "      <v itemID=\"5001\" weight=\"10\" />\n" +
            "      <v itemID=\"5002\" weight=\"10\" minCount=\"2\" />\n" +
            "      <v itemID=\"5001\" weight=\"20\" />\n" +
            "    </group>\n" +
            "    <group dropGroupID=\"2\">\n" +
            "      <v itemID=\"5003\" weight=\"0\" />\n" +
            "    </group>\n" +
            "  </dropBox>\n" +
            "  <dropBox dropBoxID=\"7003\">\n" +
            "    <group dropGroupID=\"1\">\n" +
            "      <v itemID=\"5002\" weight=\"1\" />\n" +
            "    </group>\n" +
            "  </dropBox>\n" +
            "</ms2>\n";

        public const string KmsFinal = Bom + Head + "<ms2>\n" +
            "\t<dropBox dropBoxID=\"7001\">\n" +
            "\t\t<group dropGroupID=\"1\" smartDropRate=\"50\">\n" +
            "\t\t\t<v itemID=\"5001\" minCount=\"1\" />\n" +
            "\t\t\t<v itemID=\"5002\" minCount=\"2\" />\n" +
            "\t\t</group>\n" +
            "\t</dropBox>\n" +
            "</ms2>\n";

        static string Flat(long box, int group, long item, string more)
        {
            return "\t<individualDropBox individualDropBoxID=\"" + box + "\" dropGroup=\"" + group + "\" item=\"" + item + "\" minCount=\"1\" maxCount=\"1\"" + more + " />\n";
        }

        public static readonly string FlatMain = Head + "<ms2>\n" +
            Flat(7001, 1, 5001, " socketDataID=\"\" smartDropRate=\"50\"") +
            Flat(7001, 1, 5002, " socketDataID=\"\" smartDropRate=\"50\"") +
            Flat(7001, 1, 5001, " socketDataID=\"\" smartDropRate=\"50\"") +
            Flat(7001, 2, 5003, " socketDataID=\"\"") +
            Flat(7100, 1, 5001, " socketDataID=\"\" isApplySmartGenderDrop=\"True\"") +
            "</ms2>\n";

        public static readonly string FlatQuest = Head + "<ms2>\n" + Flat(7003, 1, 5002, "") + "</ms2>\n";
        public static readonly string FlatNa = Head + "<ms2>\n" + Flat(7004, 1, 5003, " locale=\"NA\"") + "</ms2>\n";

        public const string ShopCategory = Head + "<ms2>\n\t<category id=\"1\">\n\t\t<tab name=\"1\" priority=\"1\">\n\t\t\t<tab name=\"2\" category=\"AA,BB\" priority=\"1\" />\n\t\t</tab>\n\t</category>\n</ms2>\n";

        static string ItemFile(long id) { return Head + "<ms2>\n\t<environment>\n\t\t<basic itemID=\"" + id + "\" />\n\t</environment>\n</ms2>\n"; }

        static string NpcFile(long id) { return Head + "<ms2>\n\t<environment>\n\t\t<basic level=\"5\" shopId=\"0\" />\n\t\t<dropiteminfo individualDropBoxId=\"7001\" />\n\t</environment>\n</ms2>\n"; }

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        static readonly long[] Items = { 5001, 5002, 5003 };

        public FakeGroups()
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-groups-" + Guid.NewGuid().ToString("N"));
            string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
            foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);

            var rows = new StringBuilder(Head + "<ms2>\n");
            foreach (long i in Items) rows.Append("\t<item id=\"" + i + "\" />\n");
            M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E("table/individualitemdrop_final.xml", KmsFinal), E("table/shopcategory.xml", ShopCategory),
                E("itemdata/005.xml", rows.Append("</ms2>\n").ToString()),
            });
            foreach (string d in new[] { g, s })
            {
                var l = new List<KeyValuePair<string, byte[]>>
                {
                    E("table/individualitemdrop.xml", FlatMain), E("table/individualitemdrop_quest.xml", FlatQuest),
                    E("table/na/individualitemdrop_monster.xml", FlatNa),
                    E("npc/02/90/02900001.xml", NpcFile(2900001)),
                };
                foreach (long i in Items) l.Add(E("item/00/00/" + i.ToString("00000000") + ".xml", ItemFile(i)));
                if (d == g) l.Add(E("table/shopcategory.xml", ShopCategory));
                M2dWriter.Create(Path.Combine(d, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, l);
            }
            M2dWriter.Create(Path.Combine(s, "Server.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E(Planner.ShopStockEntry, ShopGame), E(Planner.ShopInfoEntry, ShopInfo), E(Planner.ServerDropEntry, ServerFinal),
            });
            Data = new DataSet(k, g, s);
        }

        public static Mod ModOf(string body, string id = "groups.test")
        {
            Mod m = Mod.Parse("<mod id=\"" + id + "\" name=\"t\" version=\"1\">\n" + body + "\n</mod>", "test mod");
            return Mod.Parse(m.ToXml(), "round trip");
        }

        public Plan Plan(params string[] bodies)
        {
            var mods = new List<Mod>();
            for (int i = 0; i < bodies.Length; i++) mods.Add(ModOf(bodies[i], "groups.test" + i));
            return Planner.Build(Data, mods, new PlanOptions());
        }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class GroupsTests
    {
        const string K = "client:Xml.m2d", G = "gms-client:Xml.m2d", SX = "server:Xml.m2d", SS = "server:Server.m2d";

        static string Text(Plan p, string archive, string entry)
        {
            string t = Put(p, archive, entry);
            return t == null ? null : t.Replace("\r\n", "\n");
        }

        static string Put(Plan p, string archive, string entry)
        {
            return p.Find(archive) == null ? null : Fake.Text(p, archive, entry);
        }

        static int Count(string text, string part) { return text.Split(new[] { part }, StringSplitOptions.None).Length - 1; }

        static bool Has(List<PlanMessage> list, string part)
        {
            foreach (PlanMessage m in list) if (m.ToString().Contains(part)) return true;
            return false;
        }

        /// <summary>The lines of a and b that differ, as (line of a, line of b) pairs, after lining up equal lines.</summary>
        static List<string> Changed(string a, string b)
        {
            string[] x = a.Split('\n'), y = b.Split('\n');
            var lcs = new int[x.Length + 1, y.Length + 1];
            for (int i = x.Length - 1; i >= 0; i--)
                for (int j = y.Length - 1; j >= 0; j--)
                    lcs[i, j] = x[i] == y[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            var diff = new List<string>();
            int p = 0, q = 0;
            while (p < x.Length || q < y.Length)
            {
                if (p < x.Length && q < y.Length && x[p] == y[q]) { p++; q++; }
                else if (p < x.Length && (q == y.Length || lcs[p + 1, q] >= lcs[p, q + 1])) diff.Add("-" + x[p++]);
                else diff.Add("+" + y[q++]);
            }
            return diff;
        }

        static string Shipped(string s) { return s.Replace("﻿", ""); }

        [Test]
        public void ShopStockEditKeepsOtherRowsAndOrder()
        {
            using (var f = new FakeGroups())
            {
                Plan p = f.Plan("<shop id=\"9001\">" +
                    "<header><set name=\"iconName\" value=\"fake2\"/></header>" +
                    "<row sn=\"1\"><set name=\"price\" value=\"777\"/></row>" +
                    "<add><xml><item id=\"5002\" grade=\"1\" price=\"5\" category=\"AA\" /></xml></add>" +
                    "<remove sn=\"3\"/></shop>");
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(p.Warnings, Is.Empty, dump);
                Assert.That(Has(p.Notes, "numbered per shop"), dump);
                Assert.That(Has(p.Notes, "gets sn 4"), dump);
                string stock = Text(p, SS, Planner.ShopStockEntry);
                List<string> d = Changed(Shipped(FakeGroups.ShopGame), stock.Replace("﻿", ""));
                Assert.That(d, Is.EqualTo(new[]
                {
                    "-\t\t<item sn=\"1\" id=\"5001\" grade=\"1\" price=\"100\" category=\"AA\" />",
                    "+\t\t<item sn=\"1\" id=\"5001\" grade=\"1\" price=\"777\" category=\"AA\" />",
                    "-\t\t<item sn=\"3\" id=\"5003\" grade=\"1\" price=\"300\" paymentType=\"1\" paymentItemID=\"5002:tag\" category=\"BB\" />",
                    "+\t\t<item id=\"5002\" grade=\"1\" price=\"5\" category=\"AA\" sn=\"4\" />",
                }), dump);
                Assert.That(stock.StartsWith("﻿"), "the BOM is kept");
                Assert.That(Text(p, SS, Planner.ShopInfoEntry), Does.Contain("<shop shopID=\"9001\" categoryID=\"1\" iconName=\"fake2\" />"));
                Assert.That(Put(p, K, "table/shopcategory.xml"), Is.Null, "a client is checked, never written");
            }
        }

        [Test]
        public void ShopVariantsOfOneSnNeedFeature()
        {
            using (var f = new FakeGroups())
            {
                Plan p = f.Plan("<shop id=\"9001\"><row sn=\"2\"><set name=\"price\" value=\"1\"/></row></shop>");
                Assert.That(Has(p.Errors, "variants of one sn"), Fake.Dump(p));
                p = f.Plan("<shop id=\"9001\"><row sn=\"2\" feature=\"F2\"><set name=\"price\" value=\"1\"/></row></shop>");
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string stock = Text(p, SS, Planner.ShopStockEntry);
                Assert.That(stock, Does.Contain("price=\"200\" category=\"AA\" feature=\"F1\"").And.Contain("price=\"1\" category=\"AA\" feature=\"F2\""));
            }
        }

        [Test]
        public void MissingRowsAndShopsWarnAndSkip()
        {
            using (var f = new FakeGroups())
            {
                Plan p = f.Plan("<shop id=\"9001\"><row sn=\"9\"><set name=\"price\" value=\"1\"/></row><row sn=\"1\"><set name=\"price\" value=\"2\"/></row></shop>",
                    "<shop id=\"9555\"><row sn=\"1\"><set name=\"price\" value=\"1\"/></row></shop>");
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(Has(p.Warnings, "no stock row sn 9 here; skipped"), dump);
                Assert.That(Has(p.Warnings, "shop 9555: this target has no such shop"), dump);
                Assert.That(Text(p, SS, Planner.ShopStockEntry), Does.Contain("price=\"2\""));
            }
        }

        [Test]
        public void NewAndClonedShopAndChecks()
        {
            using (var f = new FakeGroups())
            {
                Plan p = f.Plan("<shop id=\"900000001\" new=\"true\"><xml><shop categoryID=\"1\" iconName=\"fake\" /></xml>" +
                    "<add><xml><item id=\"5001\" grade=\"1\" price=\"1\" category=\"AA\" /></xml></add>" +
                    "<add><xml><item id=\"5999\" grade=\"1\" price=\"1\" paymentType=\"1\" category=\"ZZ\" /></xml></add>" +
                    "<add><xml><item id=\"5002\" grade=\"1\" price=\"1\" paymentType=\"77\" category=\"AA\" /></xml></add></shop>",
                    "<shop id=\"900000002\" clone=\"9002\"><header><set name=\"categoryID\" value=\"42\"/></header>" +
                    "<row sn=\"2\"><set name=\"price\" value=\"21\"/></row>" +
                    "<add><xml><item id=\"5002\" grade=\"1\" price=\"3\" category=\"AA\" /></xml></add></shop>");
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                string stock = Text(p, SS, Planner.ShopStockEntry), info = Text(p, SS, Planner.ShopInfoEntry);
                Assert.That(stock, Does.Contain("\t<shop shopID=\"900000001\">\n\t\t<item id=\"5001\" grade=\"1\" price=\"1\" category=\"AA\" sn=\"1\" />\n\t\t<item id=\"5999\""), dump);
                Assert.That(stock, Does.Contain("sn=\"3\" />\n\t</shop>"), "a new shop numbers from 1");
                Assert.That(stock, Does.Contain("\t<shop shopID=\"900000002\">\n\t\t<item sn=\"1\" id=\"5001\" grade=\"1\" price=\"10\" category=\"AA\" />\n\t\t<item sn=\"2\" id=\"5003\" grade=\"1\" price=\"21\" category=\"AA\" />\n\t\t<item id=\"5002\" grade=\"1\" price=\"3\" category=\"AA\" sn=\"3\" />\n\t</shop>"), dump);
                Assert.That(Count(stock, "shopID=\"9002\""), Is.EqualTo(1), "the source is unchanged");
                Assert.That(info, Does.Contain("<shop shopID=\"900000001\" categoryID=\"1\" iconName=\"fake\" />"));
                Assert.That(info, Does.Contain("<shop shopID=\"900000002\" categoryID=\"42\" iconName=\"fake\" />"));
                // checks: the item on every target, the payment, the category of each client
                foreach (string side in new[] { "Korean client", "global client", "server" })
                    Assert.That(Has(p.Warnings, "item 5999 is not an item of the"), dump + side);
                Assert.That(Count(dump, "5999 is not an item"), Is.EqualTo(3), dump);
                Assert.That(Has(p.Warnings, "paymentType 1 is paid with an item, and paymentItemID is not set"), dump);
                Assert.That(Has(p.Warnings, "paymentType \"77\" is not a known currency"), dump);
                Assert.That(Has(p.Warnings, "category \"ZZ\" is in no tab of category 1"), dump);
                Assert.That(Has(p.Warnings, "categoryID 42 is not a category"), dump);
                Assert.That(Has(p.Warnings, "outside of the range"), Is.False, dump);
            }
        }

        [Test]
        public void TwoModsMakingOneShopConflict()
        {
            using (var f = new FakeGroups())
            {
                string body = "<shop id=\"900000003\" new=\"true\"><xml><shop categoryID=\"1\" /></xml></shop>";
                Plan p = f.Plan(body, body);
                Assert.That(Has(p.Conflicts, "also makes a shop with this id"), Fake.Dump(p));
                p = f.Plan("<shop id=\"9001\" new=\"true\"><xml><shop categoryID=\"1\" /></xml></shop>");
                Assert.That(Has(p.Errors, "already used"), Fake.Dump(p));
            }
        }

        [Test]
        public void DropRowEditOnEveryTargetThatHasTheRow()
        {
            using (var f = new FakeGroups())
            {
                // the second row of item 5001 in group 1: on the server and the flat sides, not on the Korean client
                Plan p = f.Plan("<dropbox id=\"7001\"><row group=\"1\" item=\"5001\" n=\"2\"><set name=\"minCount\" value=\"3\"/><set name=\"weight\" value=\"40\"/></row></dropbox>");
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                string srv = Text(p, SS, Planner.ServerDropEntry);
                Assert.That(srv, Does.Contain("<v itemID=\"5001\" weight=\"10\" />\n      <v itemID=\"5002\"").And.Contain("<v itemID=\"5001\" weight=\"40\" minCount=\"3\" />"), dump);
                Assert.That(Has(p.Warnings, "no row group 1 item 5001 (2nd row of that item) here"), dump);
                Assert.That(Put(p, K, Planner.KmsDropEntry), Is.Null, "nothing changes on the Korean client");
                foreach (string a in new[] { G, SX })
                {
                    string flat = Text(p, a, "table/individualitemdrop.xml");
                    List<string> d = Changed(FakeGroups.FlatMain, flat);
                    Assert.That(d.Count, Is.EqualTo(2), a + "\n" + string.Join("\n", d.ToArray()));
                    Assert.That(d[1], Does.Contain("dropGroup=\"1\" item=\"5001\" minCount=\"3\" maxCount=\"1\""), a);
                    Assert.That(flat.IndexOf("item=\"5002\""), Is.LessThan(flat.IndexOf("minCount=\"3\"")), "the second row is changed, not the first");
                }
                Assert.That(Has(p.Notes, "have no place in this layout and are not written here: weight"), dump);
            }
        }

        [Test]
        public void DropAddRemoveAndGroups()
        {
            using (var f = new FakeGroups())
            {
                Plan p = f.Plan("<dropbox id=\"7001\">" +
                    "<add group=\"1\"><xml><v itemID=\"5003\" weight=\"5\" /></xml></add>" +
                    "<remove group=\"1\" item=\"5002\"/>" +
                    "<group id=\"1\"><set name=\"isApplySmartGenderDrop\" value=\"1\"/></group>" +
                    "<add><xml><group dropGroupID=\"3\"><v itemID=\"5002\" weight=\"1\" /><v itemID=\"5002\" weight=\"2\" /></group></xml></add>" +
                    "</dropbox>");
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                string srv = Text(p, SS, Planner.ServerDropEntry);
                Assert.That(srv, Does.Contain("<group dropGroupID=\"1\" smartDropRate=\"50\" isApplySmartGenderDrop=\"1\">\n      <v itemID=\"5001\" weight=\"10\" />\n      <v itemID=\"5001\" weight=\"20\" />\n      <v itemID=\"5003\" weight=\"5\" />\n    </group>"), dump);
                Assert.That(Count(srv, "dropGroupID=\"3\""), Is.EqualTo(1), dump);
                Assert.That(Count(srv, "<v itemID=\"5002\" weight="), Is.EqualTo(3), "two rows of one item in the new group are kept apart (and box 7003's)");
                string kms = Text(p, K, Planner.KmsDropEntry);
                Assert.That(kms, Does.Contain("\t\t\t<v itemID=\"5003\" />\n\t\t</group>"), "the Korean client gets the row without weight\n" + kms);
                Assert.That(kms, Does.Not.Contain("isApplySmartGenderDrop"), "the Korean file carries no such field");
                foreach (string a in new[] { G, SX })
                {
                    string flat = Text(p, a, "table/individualitemdrop.xml");
                    Assert.That(Count(flat, "isApplySmartGenderDrop=\"True\""), Is.EqualTo(4), a + "\n" + flat);
                    Assert.That(flat, Does.Contain("dropGroup=\"1\" item=\"5003\" minCount=\"1\" maxCount=\"1\" socketDataID=\"\" smartDropRate=\"50\" isApplySmartGenderDrop=\"True\" />"), a + "\n" + flat);
                    Assert.That(Count(flat, "dropGroup=\"3\" item=\"5002\""), Is.EqualTo(2), a);
                    Assert.That(flat, Does.Not.Contain("item=\"5002\" minCount=\"1\" maxCount=\"1\" socketDataID=\"\" smartDropRate=\"50\""), "the row of 5002 in group 1 is removed");
                    Assert.That(flat.IndexOf("individualDropBoxID=\"7100\""), Is.GreaterThan(flat.LastIndexOf("individualDropBoxID=\"7001\"")), "the new rows stay with the box");
                }
            }
        }

        [Test]
        public void NewBoxGoesToEveryTargetInItsLayout()
        {
            using (var f = new FakeGroups())
            {
                Plan p = f.Plan("<dropbox id=\"900010001\" new=\"true\"><xml><dropBox comment=\"x\">" +
                    "<group dropGroupID=\"1\" isApplySmartGenderDrop=\"1\" dropCount=\"1\"><v itemID=\"5001\" weight=\"1\" /><v itemID=\"5002\" minCount=\"2\" weight=\"3\" /></group>" +
                    "<group dropGroupID=\"2\"><v itemID=\"5003\" weight=\"0\" /></group>" +
                    "</dropBox></xml></dropbox>");
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                string srv = Text(p, SS, Planner.ServerDropEntry);
                Assert.That(srv, Does.Contain("  <dropBox dropBoxID=\"900010001\" comment=\"x\">\n    <group dropGroupID=\"1\" isApplySmartGenderDrop=\"1\" dropCount=\"1\">\n      <v itemID=\"5001\" weight=\"1\" />"), dump);
                string kms = Text(p, K, Planner.KmsDropEntry);
                Assert.That(kms, Does.Contain("\t<dropBox dropBoxID=\"900010001\">\n\t\t<group dropGroupID=\"1\">\n\t\t\t<v itemID=\"5001\" />\n\t\t\t<v itemID=\"5002\" minCount=\"2\" />"), kms + dump);
                foreach (string a in new[] { G, SX })
                {
                    string flat = Text(p, a, Planner.DefaultDropSplit);
                    Assert.That(flat, Does.Contain("<individualDropBox individualDropBoxID=\"900010001\" dropGroup=\"1\" item=\"5001\" minCount=\"1\" maxCount=\"1\" socketDataID=\"\" isApplySmartGenderDrop=\"True\" />"), a + flat);
                    Assert.That(flat, Does.Contain("item=\"5002\" minCount=\"2\" maxCount=\"2\""), a);
                    Assert.That(Put(p, a, "table/individualitemdrop_quest.xml"), Is.Null, a);
                    // round trip: the flat rows read back into the nested model give the same items, groups and counts
                    XmlDoc d = XmlDoc.Parse(Encoding.UTF8.GetBytes(flat), "flat");
                    var got = new List<string>();
                    foreach (XmlElem e in d.Root.Children)
                        if (e.Attr("individualDropBoxID") == "900010001") got.Add(e.Attr("dropGroup") + "/" + e.Attr("item") + "/" + e.Attr("minCount"));
                    Assert.That(got, Is.EqualTo(new[] { "1/5001/1", "1/5002/2", "2/5003/1" }), a);
                }
                Assert.That(Has(p.Notes, "goes into " + Planner.DefaultDropSplit + ": it is the general split"), dump);
                Assert.That(Has(p.Notes, "not written here: comment, dropCount, weight"), dump);
                Assert.That(Has(p.Warnings, "every row of group 2 has weight 0"), dump);
            }
        }

        [Test]
        public void ClonedBoxSplitAndRegionFolders()
        {
            using (var f = new FakeGroups())
            {
                Plan p = f.Plan("<dropbox id=\"900010002\" clone=\"7003\"><row group=\"1\" item=\"5002\"><set name=\"maxCount\" value=\"9\"/></row></dropbox>",
                    "<dropbox id=\"900010003\" clone=\"7004\"></dropbox>");
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                foreach (string a in new[] { G, SX })
                {
                    string q = Text(p, a, "table/individualitemdrop_quest.xml");
                    Assert.That(q, Does.Contain("individualDropBoxID=\"900010002\" dropGroup=\"1\" item=\"5002\" minCount=\"1\" maxCount=\"9\""), a + dump);
                    Assert.That(Text(p, a, Planner.DefaultDropSplit), Does.Contain("individualDropBoxID=\"900010003\" dropGroup=\"1\" item=\"5003\" minCount=\"1\" maxCount=\"1\" locale=\"NA\""), a);
                    Assert.That(Put(p, a, "table/na/individualitemdrop_monster.xml"), Is.Null, "a region folder is never written for a new box");
                }
                Assert.That(Has(p.Notes, "it holds the box 7003 that is cloned"), dump);
                Assert.That(Has(p.Notes, "region folder"), dump);
                Assert.That(Text(p, SS, Planner.ServerDropEntry), Does.Contain("<dropBox dropBoxID=\"900010002\">\n    <group dropGroupID=\"1\">\n      <v itemID=\"5002\" weight=\"1\" maxCount=\"9\" />"), dump);
                Assert.That(Has(p.Warnings, "the box 7003 to clone is not on this target"), "the Korean client has no 7003\n" + dump);
                Assert.That(Has(p.Warnings, "outside of the range"), Is.False, dump);
            }
        }

        [Test]
        public void ExistingRowInRegionFolderIsEditedThere()
        {
            using (var f = new FakeGroups())
            {
                Plan p = f.Plan("<dropbox id=\"7004\"><row group=\"1\" item=\"5003\"><set name=\"maxCount\" value=\"4\"/></row></dropbox>");
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                foreach (string a in new[] { G, SX })
                    Assert.That(Text(p, a, "table/na/individualitemdrop_monster.xml"), Does.Contain("maxCount=\"4\" locale=\"NA\""), a);
            }
        }

        [Test]
        public void BooleanSpellingAndItemsPerTarget()
        {
            using (var f = new FakeGroups())
            {
                Plan p = f.Plan("<dropbox id=\"7001\"><group id=\"1\"><set name=\"isApplySmartGenderDrop\" value=\"yes\"/></group>" +
                    "<add group=\"1\"><xml><v itemID=\"5888\" /></xml></add></dropbox>");
                string dump = Fake.Dump(p);
                Assert.That(Has(p.Warnings, "isApplySmartGenderDrop is \"yes\"; this layout writes 1 or 0"), dump);
                Assert.That(Has(p.Warnings, "isApplySmartGenderDrop is \"yes\"; this layout writes True or False"), dump);
                Assert.That(Count(dump, "5888 is not an item"), Is.GreaterThanOrEqualTo(4), "server nested and flat, Korean, global\n" + dump);
            }
        }

        [Test]
        public void NpcPointsAtShopAndBoxAfterTheMod()
        {
            using (var f = new FakeGroups())
            {
                string npc = "<edit archive=\"server:Xml.m2d\" entry=\"npc/02/90/02900001.xml\" sync=\"false\">" +
                    "<change at=\"environment/basic\"><set name=\"shopId\" value=\"{0}\"/></change>" +
                    "<change at=\"environment/dropiteminfo\"><set name=\"individualDropBoxId\" value=\"{1}\"/></change></edit>";
                Plan p = f.Plan(string.Format(npc, "9444", "7001,7999"));
                string dump = Fake.Dump(p);
                Assert.That(Has(p.Warnings, "shopId 9444 is not a shop on the server after the mods"), dump);
                Assert.That(Has(p.Warnings, "individualDropBoxId 7999 is not a drop box"), dump);
                Assert.That(Has(p.Warnings, "individualDropBoxId 7001"), Is.False, "shipped values are not reported");
                p = f.Plan(string.Format(npc, "900000005", "900010005") +
                    "<shop id=\"900000005\" new=\"true\"><xml><shop categoryID=\"1\" /></xml></shop>" +
                    "<dropbox id=\"900010005\" new=\"true\"><xml><dropBox><group dropGroupID=\"1\"><v itemID=\"5001\" weight=\"1\" /></group></dropBox></xml></dropbox>");
                dump = Fake.Dump(p);
                Assert.That(Has(p.Warnings, "is not a shop"), Is.False, dump);
                Assert.That(Has(p.Warnings, "is not a drop box"), Is.False, dump);
            }
        }

        [Test]
        public void ReservedRangesForNewGroups()
        {
            using (var f = new FakeGroups())
            {
                Plan p = f.Plan("<shop id=\"9100\" new=\"true\"><xml><shop categoryID=\"1\" /></xml></shop>");
                Assert.That(Has(p.Warnings, "outside of the range 900000000-900009999 kept for new shops"), Fake.Dump(p));
                Mod m = FakeGroups.ModOf("<dropbox id=\"900010000\" new=\"true\"><xml><dropBox><group dropGroupID=\"1\"><v itemID=\"5001\" /></group></dropBox></xml></dropbox>");
                IdSpaces.Reserve(m, IdSpaces.DropBoxSpace, 900010000);
                IdSpaces.Reserve(m, IdSpaces.DropBoxSpace, 900010001);
                Assert.That(m.Ids.Count, Is.EqualTo(1));
                Assert.That(m.Ids[0].To, Is.EqualTo(900010001));
                Assert.That(IdSpaces.NextFreeGroupIds(f.Data, new[] { m }, IdSpaces.DropBoxSpace, 2, null), Is.EqualTo(new long[] { 900010002, 900010003 }));
                Assert.That(IdSpaces.NextFreeGroupIds(f.Data, new Mod[0], IdSpaces.ShopSpace, 1, null), Is.EqualTo(new long[] { 900000000 }));
                Mod back = Mod.Parse(m.ToXml(), "again");
                Assert.That(back.Ids[0].Places.Count, Is.EqualTo(4));
                p = Planner.Build(f.Data, new[] { back }, new PlanOptions());
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(Has(p.Warnings, "outside of the range"), Is.False, dump);
                Assert.That(Has(p.Warnings, "of space \"dropbox\" is added in"), Is.False, "the box is added in all four places\n" + dump);
                // a reserved id the shipped data uses is an error
                Mod used = FakeGroups.ModOf("<dropbox id=\"7100\" clone=\"7003\"></dropbox>", "groups.used");
                IdSpaces.Reserve(used, IdSpaces.DropBoxSpace, 7100);
                p = Planner.Build(f.Data, new[] { used }, new PlanOptions());
                Assert.That(Has(p.Errors, "already used"), Fake.Dump(p));
                IdSpaces.Unreserve(m, IdSpaces.DropBoxSpace, 900010000);
                Assert.That(m.Ids[0].From, Is.EqualTo(900010001));
            }
        }

        [Test]
        public void ModXmlRoundTrip()
        {
            const string body = "<shop id=\"900000009\" clone=\"9001\"><header feature=\"F1\"><set name=\"iconName\" value=\"a\"/></header>" +
                "<row sn=\"2\" feature=\"F1\"><set name=\"price\" value=\"1\"/></row><remove sn=\"1\"/>" +
                "<add><xml><item id=\"5001\" price=\"2\" /></xml></add></shop>" +
                "<dropbox id=\"900010009\" new=\"true\" split=\"table/individualitemdrop_quest.xml\"><xml><dropBox><group dropGroupID=\"1\"><v itemID=\"5001\" /></group></dropBox></xml>" +
                "<row group=\"1\" item=\"5001\" n=\"1\"><unset name=\"minCount\"/></row><remove group=\"2\"/></dropbox>";
            Mod a = FakeGroups.ModOf(body), b = Mod.Parse(a.ToXml(), "again");
            Assert.That(b.ToXml(), Is.EqualTo(a.ToXml()));
            Assert.That(a.Groups.Count, Is.EqualTo(2));
            Assert.That(a.Groups[1].Split, Is.EqualTo("table/individualitemdrop_quest.xml"));
            Assert.That(a.Groups[0].Rows.Count, Is.EqualTo(3));
        }
    }
}
