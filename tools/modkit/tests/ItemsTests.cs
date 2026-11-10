// The item editor's model on made-up data: a client archive with an item block file, name / plural / description
// files in two languages and a stat table, and a server archive with one file per item.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    public sealed class FakeItems : IDisposable
    {
        public readonly string Dir;
        public readonly DataSet Data;
        public readonly Mod Mod;

        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        public const string Bucket =
            Head + "<ms2>\n" +
            "\t<item id=\"1200001\">\n" +
            "\t\t<environment>\n" +
            "\t\t\t<property slotMax=\"1\" sellPrice=\"40\" />\n" +
            "\t\t\t<limit levelLimit=\"10\" />\n" +
            "\t\t\t<option constantID=\"77\" optionLevel=\"2\" />\n" +
            "\t\t</environment>\n" +
            "\t</item>\n" +
            "\t<item id=\"1200002\">\n" +
            "\t\t<environment>\n" +
            "\t\t\t<property slotMax=\"5\" sellPrice=\"9\" />\n" +
            "\t\t\t<option constantID=\"77\" optionLevel=\"1\" />\n" +
            "\t\t</environment>\n" +
            "\t\t<environment feature=\"Beta\">\n" +
            "\t\t\t<property slotMax=\"6\" sellPrice=\"9\" />\n" +
            "\t\t</environment>\n" +
            "\t</item>\n" +
            "\t<item id=\"1200003\">\n" +
            "\t\t<environment>\n" +
            "\t\t\t<property slotMax=\"1\" sellPrice=\"1\" />\n" +
            "\t\t</environment>\n" +
            "\t</item>\n" +
            "\t<item id=\"1200005\">\n" +
            "\t\t<environment>\n" +
            "\t\t\t<property slotMax=\"3\" sellPrice=\"7\" />\n" +
            "\t\t\t<trade locked=\"true\" />\n" +
            "\t\t\t<shape box=\"1,2,3\" />\n" +
            "\t\t\t<look icon=\"whistle\" />\n" +
            "\t\t\t<worth coin=\"40\" />\n" +
            "\t\t\t<odd a=\"1\" />\n" +
            "\t\t\t<option constantID=\"77\" optionLevel=\"1\" />\n" +
            "\t\t</environment>\n" +
            "\t</item>\n" +
            "</ms2>\n";

        static string Names(string suffix)
        {
            return Head + "<ms2>\n" +
                "\t<key id=\"1200001\" class=\"a\" name=\"Tin Whistle" + suffix + "\" />\n" +
                "\t<key id=\"1200002\" class=\"a\" name=\"Clay Drum" + suffix + "\" />\n" +
                "\t<key id=\"1200003\" class=\"a\" name=\"Reed Flute" + suffix + "\" />\n" +
                "\t<key id=\"3300009\" class=\"b\" name=\"Paper Kite" + suffix + "\" />\n" +
                "</ms2>\n";
        }

        static string Descriptions(string suffix)
        {
            return Head + "<ms2>\n" +
                "\t<key id=\"1200001\" tooltipDescription=\"A shrill little whistle." + suffix + "\" guideDescription=\"Blow gently.\" />\n" +
                "</ms2>\n";
        }

        const string Plural = Head + "<ms2>\n\t<key id=\"1200001\" name=\"Tin Whistles\" />\n</ms2>\n";

        const string Options =
            Head + "<ms2>\n" +
            "\t<option code=\"77\">\n" +
            "\t\t<rank grade=\"1\">\n" +
            "\t\t\t<v name=\"str\" value=\"3\" />\n" +
            "\t\t\t<v name=\"hp\" value=\"20\" />\n" +
            "\t\t</rank>\n" +
            "\t</option>\n" +
            "</ms2>\n";

        /// <summary>The server file of item 1200005: every mapped field in the server's own form.</summary>
        public const string ServerFive =
            Head + "<ms2>\n\t<environment>\n" +
            "\t\t<property slotMax=\"3\" sellPrice=\"8\" />\n" +
            "\t\t<trade locked=\"1\" />\n" +
            "\t\t<shape box=\"1.000000, 2.000000, 3.000000\" />\n" +
            "\t\t<look iconPath=\"./img/whistle.png\" />\n" +
            "\t\t<worth coins=\"10,40,90\" tier=\"2\" />\n" +
            "\t\t<drop rate=\"4\" />\n" +
            "\t</environment>\n</ms2>\n";

        /// <summary>A made-up field map for the made-up items: one line per kind of conversion.</summary>
        public const string MapXml =
            "<fieldmaps version=\"1\"><entity name=\"item\" sample=\"0\">" +
            "<client archive=\"client:Xml.m2d\" entry=\"itemdata/{id/100000}.xml\" row=\"item\" key=\"id\" under=\"environment\" />" +
            "<server archive=\"server:Xml.m2d\" entry=\"item/*/*/{id}.xml\" under=\"environment\" />" +
            "<map client=\"property@slotMax\" server=\"property@slotMax\" />" +
            "<map client=\"property@sellPrice\" server=\"property@sellPrice\" />" +
            "<map client=\"limit@levelLimit\" server=\"limit@levelLimit\" />" +
            "<map client=\"trade@locked\" server=\"trade@locked\" convert=\"bool\" default=\"0\" />" +
            "<map client=\"shape@box\" server=\"shape@box\" convert=\"numlist\" />" +
            "<map client=\"look@icon\" server=\"look@iconPath\" convert=\"affix\" prefix=\"./img/\" suffix=\".png\" />" +
            "<map client=\"worth@coin\" server=\"worth@coins\" convert=\"pick\" index=\"worth@tier\" />" +
            "<candidate client=\"option@constantID\" server=\"drop@rate\" compared=\"10\" agree=\"6\" />" +
            "<ref name=\"rate rows\" archive=\"server:Xml.m2d\" entries=\"rates/*.xml\" attribute=\"code\" key=\"drop@rate\" />" +
            "<ref name=\"bag rows\" archive=\"server:Xml.m2d\" entries=\"table/bags.xml\" attribute=\"item\" />" +
            "<clientOnly field=\"property@tradable\" /><clientOnly field=\"option@optionLevel\" />" +
            "<serverOnly field=\"drop@rate\" /><serverOnly field=\"worth@tier\" />" +
            "</entity></fieldmaps>";

        public static readonly FieldMaps Maps = FieldMaps.Parse(MapXml);

        static string ServerItem(int price, int level)
        {
            return Head + "<ms2>\n\t<environment>\n\t\t<property slotMax=\"1\" sellPrice=\"" + price + "\" />\n\t\t<limit levelLimit=\"" + level + "\" />\n\t\t<drop rate=\"4\" />\n\t</environment>\n</ms2>\n";
        }

        public FakeItems(bool server)
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-items-" + Guid.NewGuid().ToString("N"));
            string c = Path.Combine(Dir, "client"), s = Path.Combine(Dir, "server");
            Directory.CreateDirectory(c);
            Directory.CreateDirectory(s);
            var client = new List<KeyValuePair<string, byte[]>>
            {
                new KeyValuePair<string, byte[]>("itemdata/012.xml", Fake.Crlf(Bucket)),
                new KeyValuePair<string, byte[]>("string/en/itemname.xml", Fake.Crlf(Names(""))),
                new KeyValuePair<string, byte[]>("string/zz/itemname.xml", Fake.Crlf(Names(" (zz)"))),
                new KeyValuePair<string, byte[]>("string/en/itemnameplural.xml", Fake.Crlf(Plural)),
                new KeyValuePair<string, byte[]>("string/en/koritemdescription.xml", Fake.Crlf(Descriptions(""))),
                new KeyValuePair<string, byte[]>("string/zz/koritemdescription.xml", Fake.Crlf(Descriptions(" (zz)"))),
                new KeyValuePair<string, byte[]>("table/itemoptionconstant.xml", Fake.Crlf(Options)),
            };
            M2dWriter.Create(Path.Combine(c, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, client);
            if (server)
            {
                var srv = new List<KeyValuePair<string, byte[]>>
                {
                    new KeyValuePair<string, byte[]>("item/0/12/01200001.xml", Fake.Crlf(ServerItem(40, 10))),
                    new KeyValuePair<string, byte[]>("item/0/12/01200002.xml", Fake.Crlf(ServerItem(8, 1))),
                    new KeyValuePair<string, byte[]>("item/0/12/01200005.xml", Fake.Crlf(ServerFive)),
                    new KeyValuePair<string, byte[]>("table/bags.xml", Fake.Crlf(Head + "<ms2>\n\t<bag id=\"1\">\n\t\t<v item=\"1200005\" count=\"2\" />\n\t\t<v item=\"1200001\" count=\"1\" />\n\t</bag>\n" +
                        "\t<bag id=\"2\">\n\t\t<v item=\"1200005\" count=\"9\" />\n\t</bag>\n</ms2>\n")),
                    new KeyValuePair<string, byte[]>("rates/a.xml", Fake.Crlf(Head + "<ms2>\n\t<rate code=\"4\" grade=\"1\" bonus=\"3\" />\n\t<rate code=\"5\" grade=\"1\" bonus=\"1\" />\n</ms2>\n")),
                };
                M2dWriter.Create(Path.Combine(s, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, srv);
            }
            Data = new DataSet(c, server ? s : null);
            Mod = Mod.Parse("<mod id=\"items.test\" name=\"t\" version=\"1\"/>", "test mod");
        }

        public ItemEditor Editor() { return new ItemEditor(Data, Mod) { Maps = Maps }; }

        /// <summary>The mod written out and read back, planned alone.</summary>
        public Plan Plan()
        {
            Mod again = Mod.Parse(Mod.ToXml(), "round trip");
            return Planner.Build(Data, new[] { again }, new PlanOptions { Maps = Maps });
        }

        public static ItemSection Section(ItemDraft d, string kind, string path)
        {
            foreach (ItemSection s in d.Sections) if (s.Part.Kind == kind && s.Path == path) return s;
            throw new AssertionException("no section " + kind + " " + path);
        }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class ItemsTests
    {
        const string C = ItemEditor.ClientXml, S = ItemEditor.ServerXml;

        [Test]
        public void FindByIdAndName()
        {
            using (var f = new FakeItems(true))
            {
                ItemEditor ed = f.Editor();
                Assert.That(ed.Languages, Is.EqualTo(new[] { "en", "zz" }));
                Assert.That(ed.Language, Is.EqualTo("en"));
                Assert.That(ed.HasServer, Is.True);
                List<ItemHit> h = ed.Find("drum", 50);
                Assert.That(h.Count, Is.EqualTo(1));
                Assert.That(h[0].Id, Is.EqualTo(1200002));
                Assert.That(h[0].Name, Is.EqualTo("Clay Drum"));
                Assert.That(ed.Find("12000", 50).Count, Is.EqualTo(3));
                Assert.That(ed.Find("1200003", 50)[0].Name, Is.EqualTo("Reed Flute"));
                Assert.That(ed.Find("", 2).Count, Is.EqualTo(2));
                Assert.That(ed.Find("no such thing", 50), Is.Empty);
                ed.Language = "zz";
                Assert.That(ed.Find("kite", 50)[0].Name, Is.EqualTo("Paper Kite (zz)"));
                Assert.That(BucketOf(1200001), Is.EqualTo("itemdata/012.xml"));
                Assert.That(BucketOf(99), Is.EqualTo("itemdata/000.xml"));
            }
        }

        static string BucketOf(long id) { return ItemEditor.BucketEntry(id); }

        [Test]
        public void OpenShowsEveryFileOfTheItem()
        {
            using (var f = new FakeItems(true))
            {
                ItemDraft d = f.Editor().Open(1200001);
                var kinds = new List<string>();
                foreach (ItemPart p in d.Parts) kinds.Add(p.Kind + (p.Language != null ? ":" + p.Language : ""));
                Assert.That(kinds, Is.EqualTo(new[] { "item", "name:en", "name:zz", "plural:en", "description:en", "description:zz", "option", "server" }));
                Assert.That(d.Name, Is.EqualTo("Tin Whistle"));
                Assert.That(d.Tooltip, Is.EqualTo("A shrill little whistle."));
                Assert.That(d.Guide, Is.EqualTo("Blow gently."));
                Assert.That(d.OptionCodes(), Is.EqualTo(new[] { "77" }));
                Assert.That(d.IsNew, Is.False);
                ItemSection prop = FakeItems.Section(d, "item", "environment/property");
                Assert.That(prop.Fields, Is.EqualTo(new[] { "slotMax", "sellPrice" }));
                Assert.That(d.Get(prop, "sellPrice"), Is.EqualTo("40"));
                ItemSection opt = FakeItems.Section(d, "option", "rank/v[name=hp]");
                Assert.That(opt.Part.Shared, Is.True);
                Assert.That(d.Get(opt, "value"), Is.EqualTo("20"));
                Assert.That(FakeItems.Section(d, "server", "environment/drop").Part.Entry, Is.EqualTo("item/0/12/01200001.xml"));
                Assert.Throws<ModKitException>(() => f.Editor().Open(555));
            }
        }

        [Test]
        public void ChangeFieldsOfAShippedItem()
        {
            using (var f = new FakeItems(true))
            {
                ItemEditor ed = f.Editor();
                ItemDraft d = ed.Open(1200001);
                ItemSection prop = FakeItems.Section(d, "item", "environment/property");
                ItemSection mirrored = d.Set(prop, "sellPrice", "55", true);
                Assert.That(mirrored, Is.Not.Null);
                Assert.That(mirrored.Part.Kind, Is.EqualTo("server"));
                Assert.That(d.IsEdited(prop, "sellPrice"), Is.True);
                Assert.That(d.GetOriginal(prop, "sellPrice"), Is.EqualTo("40"));
                d.Set(FakeItems.Section(d, "option", "rank/v[name=str]"), "value", "9");
                d.Set(FakeItems.Section(d, "server", "environment/drop"), "rate", null);
                Assert.That(d.SetName("Brass Whistle"), Is.EqualTo(2));
                Assert.That(d.SetTooltip("Louder."), Is.EqualTo(2));

                // a fresh draft over the same mod sees the edits
                ItemDraft again = f.Editor().Open(1200001);
                Assert.That(again.Name, Is.EqualTo("Brass Whistle"));
                Assert.That(again.Get(FakeItems.Section(again, "item", "environment/property"), "sellPrice"), Is.EqualTo("55"));
                Assert.That(f.Editor().Find("brass", 9), Is.Empty, "the search reads the original names");

                Plan p = f.Plan();
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string bucket = Fake.Text(p, C, "itemdata/012.xml");
                Assert.That(bucket, Does.Contain("<property slotMax=\"1\" sellPrice=\"55\" />"));
                Assert.That(bucket, Does.Contain("<property slotMax=\"5\" sellPrice=\"9\" />"));
                Assert.That(Fake.Text(p, C, "string/en/itemname.xml"), Does.Contain("id=\"1200001\" class=\"a\" name=\"Brass Whistle\""));
                Assert.That(Fake.Text(p, C, "string/zz/itemname.xml"), Does.Contain("name=\"Brass Whistle\""));
                Assert.That(Fake.Text(p, C, "string/zz/koritemdescription.xml"), Does.Contain("tooltipDescription=\"Louder.\" guideDescription=\"Blow gently.\""));
                Assert.That(Fake.Text(p, C, "table/itemoptionconstant.xml"), Does.Contain("<v name=\"str\" value=\"9\" />"));
                string srv = Fake.Text(p, S, "item/0/12/01200001.xml");
                Assert.That(srv, Does.Contain("sellPrice=\"55\""));
                Assert.That(srv, Does.Contain("<drop />"));
                Assert.That(Fake.Text(p, S, "item/0/12/01200002.xml"), Is.Null);

                // setting everything back leaves an empty mod
                d = f.Editor().Open(1200001);
                d.Set(FakeItems.Section(d, "item", "environment/property"), "sellPrice", "40", true);
                d.Set(FakeItems.Section(d, "option", "rank/v[name=str]"), "value", "3");
                d.Set(FakeItems.Section(d, "server", "environment/drop"), "rate", "4");
                foreach (ItemPart part in d.Parts)
                    if (part.Kind == "name")
                        foreach (ItemSection s in d.Sections)
                            if (s.Part == part) d.Set(s, "name", d.GetOriginal(s, "name"));
                foreach (ItemPart part in d.Parts)
                    if (part.Kind == "description")
                        foreach (ItemSection s in d.Sections)
                            if (s.Part == part) d.Set(s, "tooltipDescription", d.GetOriginal(s, "tooltipDescription"));
                Assert.That(f.Mod.Ops, Is.Empty, f.Mod.ToXml());
            }
        }

        [Test]
        public void MirrorOnlyWhenTheOtherSideHeldTheSameValue()
        {
            using (var f = new FakeItems(true))
            {
                ItemDraft d = f.Editor().Open(1200002);
                // two environments on the client: no telling which one the server's is
                ItemSection prop = FakeItems.Section(d, "item", "environment[feature=]/property");
                Assert.That(d.Set(prop, "slotMax", "7", true), Is.Null);
                Plan p = f.Plan();
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(p.Find(S) == null || Fake.Text(p, S, "item/0/12/01200002.xml") == null, Is.True);
            }
            using (var f = new FakeItems(true))
            {
                ItemDraft d = f.Editor().Open(1200001);
                ItemSection lim = FakeItems.Section(d, "server", "environment/limit");
                ItemSection m = d.Set(lim, "levelLimit", "12", true);
                Assert.That(m, Is.Not.Null);
                Assert.That(m.Part.Kind, Is.EqualTo("item"));
                ItemSection drop = FakeItems.Section(d, "server", "environment/drop");
                Assert.That(d.Set(drop, "rate", "5", true), Is.Null, "the client item has no drop element");
            }
        }

        [Test]
        public void CloneToANewId()
        {
            using (var f = new FakeItems(true))
            {
                ItemEditor ed = f.Editor();
                Assert.That(ed.NextFreeId(1200001, 0), Is.EqualTo(1200004));
                Assert.That(ed.NextFreeId(1200001, 1250000), Is.EqualTo(1250000));
                ItemDraft n = ed.Clone(1200001, 1250000, "Glass Whistle", "It hums.");
                Assert.That(n.IsNew, Is.True);
                Assert.That(n.Name, Is.EqualTo("Glass Whistle"));
                Assert.That(n.Tooltip, Is.EqualTo("It hums."));
                Assert.That(n.Guide, Is.EqualTo("Blow gently."));
                n.Set(FakeItems.Section(n, "item", "environment/limit"), "levelLimit", "30", true);
                n.Set(FakeItems.Section(n, "item", "environment/property"), "tradable", "0");
                Assert.Throws<ModKitException>(() => n.Set(FakeItems.Section(n, "item", ""), "id", "5"));

                List<ItemHit> hits = f.Editor().Find("glass", 9);
                Assert.That(hits.Count, Is.EqualTo(1));
                Assert.That(hits[0].Added, Is.True);
                Assert.That(f.Editor().NextFreeId(1200001, 1250000), Is.EqualTo(1250001));

                Assert.That(f.Mod.Ids.Count, Is.EqualTo(1));
                Assert.That(f.Mod.Ids[0].Space, Is.EqualTo("item"));
                Assert.That(f.Mod.Ids[0].Places.Count, Is.EqualTo(2));

                Plan p = f.Plan();
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(p.Warnings, Is.Empty, Fake.Dump(p));
                string bucket = Fake.Text(p, C, "itemdata/012.xml");
                Assert.That(bucket, Does.Contain("<item id=\"1250000\">"));
                Assert.That(bucket, Does.Contain("<limit levelLimit=\"30\" />"));
                Assert.That(bucket, Does.Contain("<limit levelLimit=\"10\" />"), "the source item is untouched");
                Assert.That(bucket, Does.Contain("sellPrice=\"40\" tradable=\"0\" />"));
                Assert.That(Fake.Text(p, C, "string/en/itemname.xml"), Does.Contain("<key id=\"1250000\" class=\"a\" name=\"Glass Whistle\" />"));
                Assert.That(Fake.Text(p, C, "string/en/itemname.xml"), Does.Contain("name=\"Tin Whistle\""));
                Assert.That(Fake.Text(p, C, "string/zz/itemname.xml"), Does.Contain("id=\"1250000\" class=\"a\" name=\"Glass Whistle\""));
                Assert.That(Fake.Text(p, C, "string/en/itemnameplural.xml"), Does.Contain("<key id=\"1250000\" name=\"Tin Whistles\" />"));
                Assert.That(Fake.Text(p, C, "string/zz/koritemdescription.xml"), Does.Contain("<key id=\"1250000\" tooltipDescription=\"It hums.\" guideDescription=\"Blow gently.\" />"));
                string srv = Fake.Text(p, S, "item/0/12/01250000.xml");
                Assert.That(srv, Does.Contain("<limit levelLimit=\"30\" />"));
                Assert.That(Fake.Text(p, S, "item/0/12/01200001.xml"), Is.Null);

                // a second clone next to it widens the range; removing both empties the mod
                f.Editor().Clone(1200002, 1250001, null, null);
                Assert.That(f.Mod.Ids.Count, Is.EqualTo(1));
                Assert.That(f.Mod.Ids[0].To, Is.EqualTo(1250001));
                Assert.That(f.Editor().Open(1250001).Name, Is.EqualTo("Clay Drum"));
                p = f.Plan();
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Fake.Text(p, C, "itemdata/012.xml"), Does.Contain("<environment feature=\"Beta\">\r\n\t\t\t<property slotMax=\"6\" sellPrice=\"9\" />"));
                f.Editor().RemoveClone(1250001);
                f.Editor().RemoveClone(1250000);
                Assert.That(f.Mod.Ops, Is.Empty);
                Assert.That(f.Mod.Ids, Is.Empty);
            }
        }

        [Test]
        public void CloneRefusesBadIds()
        {
            using (var f = new FakeItems(false))
            {
                ItemEditor ed = f.Editor();
                Assert.That(ed.HasServer, Is.False);
                Assert.That(Assert.Throws<ModKitException>(() => ed.Clone(1200001, 1300000, null, null)).Message, Does.Contain("block"));
                Assert.That(Assert.Throws<ModKitException>(() => ed.Clone(1200001, 1200002, null, null)).Message, Does.Contain("already used"));
                Assert.Throws<ModKitException>(() => ed.Clone(1200999, 1250000, null, null));
                Assert.That(f.Mod.Ops, Is.Empty);
                ed.Clone(1200001, 1250000, null, null);
                Assert.That(Assert.Throws<ModKitException>(() => ed.Clone(1200002, 1250000, null, null)).Message, Does.Contain("already adds"));
                Assert.That(f.Mod.Ids[0].Places.Count, Is.EqualTo(1));
                Plan p = f.Plan();
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(p.Warnings, Is.Empty, Fake.Dump(p));
            }
        }

        [Test]
        public void TooltipForAnItemWithoutADescriptionRow()
        {
            using (var f = new FakeItems(false))
            {
                ItemDraft d = f.Editor().Open(1200003);
                Assert.That(d.Tooltip, Is.Null);
                Assert.That(d.SetTooltip(""), Is.EqualTo(0));
                Assert.That(f.Mod.Ops, Is.Empty);
                Assert.That(d.SetTooltip("Made of <reed> & \"string\"."), Is.EqualTo(2));
                Assert.That(d.Tooltip, Is.EqualTo("Made of <reed> & \"string\"."));
                Plan p = f.Plan();
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Fake.Text(p, C, "string/en/koritemdescription.xml"), Does.Contain("<key id=\"1200003\" tooltipDescription=\"Made of &lt;reed> &amp; &quot;string&quot;.\" guideDescription=\"\" />"));

                // a clone of an item without a description gets its own rows too
                ItemDraft n = f.Editor().Clone(1200003, 1200010, "Bone Flute", "Hollow.");
                Assert.That(n.Tooltip, Is.EqualTo("Hollow."));
                p = f.Plan();
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Fake.Text(p, C, "string/zz/koritemdescription.xml"), Does.Contain("<key id=\"1200010\" tooltipDescription=\"Hollow.\""));
            }
        }

        [Test]
        public void OwnStatRow()
        {
            using (var f = new FakeItems(false))
            {
                ItemDraft d = f.Editor().Open(1200001);
                Assert.Throws<ModKitException>(() => d.GiveOwnOption("77", "77"));
                Assert.Throws<ModKitException>(() => d.GiveOwnOption("5", "900"));
                d.GiveOwnOption("77", "900");
                Assert.That(d.OptionCodes(), Is.EqualTo(new[] { "900" }));
                ItemSection hp = FakeItems.Section(d, "option", "rank/v[name=hp]");
                Assert.That(hp.Part.Shared, Is.False);
                d.Set(hp, "value", "99");
                Plan p = f.Plan();
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string opt = Fake.Text(p, C, "table/itemoptionconstant.xml");
                Assert.That(opt, Does.Contain("<option code=\"900\">"));
                Assert.That(opt, Does.Contain("<v name=\"hp\" value=\"99\" />"));
                Assert.That(opt, Does.Contain("<v name=\"hp\" value=\"20\" />"), "the shared row keeps its value");
                string bucket = Fake.Text(p, C, "itemdata/012.xml");
                Assert.That(bucket, Does.Contain("<option constantID=\"900\" optionLevel=\"2\" />"));
                Assert.That(bucket, Does.Contain("<option constantID=\"77\" optionLevel=\"1\" />"));
            }
        }
    }
}
