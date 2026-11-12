// Two-sided sync on made-up data: a made-up field map (FakeItems.MapXml) with one line per kind of conversion,
// and mods that change one side, both sides, or one side on purpose.
using System.Collections.Generic;
using MS2.ModKit;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class SyncTests
    {
        const string C = ItemEditor.ClientXml, S = ItemEditor.ServerXml;
        const string Bucket = "itemdata/012.xml", Five = "item/0/12/01200005.xml";

        static string Client(string rows) { return "<edit archive=\"" + C + "\" entry=\"" + Bucket + "\">" + rows + "</edit>"; }
        static string Server(string rows) { return "<edit archive=\"" + S + "\" entry=\"" + Five + "\">" + rows + "</edit>"; }
        static string Set(string at, string name, string value) { return "<change at=\"" + at + "\"><set name=\"" + name + "\" value=\"" + value + "\"/></change>"; }

        static Plan Build(FakeItems f, string ops, bool noSync = false)
        {
            Mod m = Mod.Parse("<mod id=\"sync.test\" name=\"t\" version=\"1\">" + ops + "</mod>", "test mod");
            m = Mod.Parse(m.ToXml(), "round trip");
            return Planner.Build(f.Data, new[] { m }, new PlanOptions { Maps = FakeItems.Maps, NoSync = noSync });
        }

        static int Synced(Plan p) { int n = 0; foreach (PlanChange c in p.Changes) if (c.Synced) n++; return n; }

        [Test]
        public void AClientChangeIsWrittenToTheServerInItsForm()
        {
            using (var f = new FakeItems(true))
            {
                const string env = "item[id=1200005]/environment/";
                Plan p = Build(f, Client(
                    Set(env + "property", "slotMax", "9") + Set(env + "trade", "locked", "false") + Set(env + "shape", "box", "4,5.5,6") +
                    Set(env + "look", "icon", "drum") + Set(env + "worth", "coin", "55")));
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(p.Warnings, Is.Empty, dump);
                Assert.That(Synced(p), Is.EqualTo(5), dump);
                Assert.That(dump, Does.Contain("(synced from the other side)"));
                string srv = Fake.Text(p, S, Five);
                Assert.That(srv, Does.Contain("<property slotMax=\"9\" sellPrice=\"8\" />"));
                Assert.That(srv, Does.Contain("<trade locked=\"0\" />"));
                Assert.That(srv, Does.Contain("<shape box=\"4.000000, 5.500000, 6.000000\" />"));
                Assert.That(srv, Does.Contain("<look iconPath=\"./img/drum.png\" />"));
                Assert.That(srv, Does.Contain("<worth coins=\"10,55,90\" tier=\"2\" />"));
                Assert.That(Fake.Text(p, S, "item/0/12/01200001.xml"), Is.Null, "other items are untouched");
            }
        }

        [Test]
        public void AServerChangeIsWrittenToTheClientInItsForm()
        {
            using (var f = new FakeItems(true))
            {
                Plan p = Build(f, Server(
                    Set("environment/trade", "locked", "0") + Set("environment/shape", "box", "7.000000, 8.250000, 9.000000") +
                    Set("environment/look", "iconPath", "./img/kite.png") + Set("environment/worth", "coins", "10,77,90")));
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(p.Warnings, Is.Empty, dump);
                Assert.That(Synced(p), Is.EqualTo(4), dump);
                string c = Fake.Text(p, C, Bucket);
                Assert.That(c, Does.Contain("<trade locked=\"false\" />"));
                Assert.That(c, Does.Contain("<shape box=\"7,8.25,9\" />"));
                Assert.That(c, Does.Contain("<look icon=\"kite\" />"));
                Assert.That(c, Does.Contain("<worth coin=\"77\" />"));
                Assert.That(c, Does.Contain("<property slotMax=\"1\" sellPrice=\"40\" />"), "the other rows of the file are as shipped");
            }
        }

        [Test]
        public void ARemovedClientAttributeBecomesTheMapsDefault()
        {
            using (var f = new FakeItems(true))
            {
                Plan p = Build(f, Client("<change at=\"item[id=1200005]/environment/trade\"><unset name=\"locked\"/></change>" +
                    "<change at=\"item[id=1200005]/environment/look\"><unset name=\"icon\"/></change>"));
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(Fake.Text(p, S, Five), Does.Contain("<trade locked=\"0\" />"));
                Assert.That(Fake.Text(p, S, Five), Does.Contain("<look iconPath=\"./img/whistle.png\" />"));
                Assert.That(p.Warnings.Count, Is.EqualTo(1), dump);
                Assert.That(dump, Does.Contain("@icon is changed on the client only: the client attribute is removed and the map gives no server value for that"));
            }
        }

        [Test]
        public void BothSidesSetByTheMod()
        {
            using (var f = new FakeItems(true))
            {
                // the two values correspond: nothing to say, nothing synced
                Plan p = Build(f, Client(Set("item[id=1200005]/environment/look", "icon", "drum")) + Server(Set("environment/look", "iconPath", "./img/drum.png")));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(p.Warnings, Is.Empty, Fake.Dump(p));
                Assert.That(Synced(p), Is.EqualTo(0));

                // they do not: both are written as the mod says, with one warning
                p = Build(f, Client(Set("item[id=1200005]/environment/look", "icon", "drum")) + Server(Set("environment/look", "iconPath", "./img/bell.png")));
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(p.Warnings.Count, Is.EqualTo(1), dump);
                Assert.That(dump, Does.Contain("explicit one-sided override: item 1200005 look@icon is set to \"drum\" on the client and look@iconPath to \"./img/bell.png\" on the server"));
                Assert.That(Fake.Text(p, C, Bucket), Does.Contain("<look icon=\"drum\" />"));
                Assert.That(Fake.Text(p, S, Five), Does.Contain("<look iconPath=\"./img/bell.png\" />"));
            }
        }

        [Test]
        public void SyncOffForALineARowAnEditOrTheWholePlan()
        {
            using (var f = new FakeItems(true))
            {
                const string at = "item[id=1200005]/environment/trade";
                string[] ops =
                {
                    Client("<change at=\"" + at + "\"><set name=\"locked\" value=\"false\" sync=\"false\"/></change>"),
                    Client("<change at=\"" + at + "\" sync=\"false\"><set name=\"locked\" value=\"false\"/></change>"),
                    "<edit archive=\"" + C + "\" entry=\"" + Bucket + "\" sync=\"false\">" + Set(at, "locked", "false") + "</edit>",
                    Client(Set(at, "locked", "false")),
                };
                for (int i = 0; i < ops.Length; i++)
                {
                    Plan p = Build(f, ops[i], i == 3);
                    string dump = Fake.Dump(p);
                    Assert.That(p.Errors, Is.Empty, dump);
                    Assert.That(p.Warnings.Count, Is.EqualTo(1), dump);
                    Assert.That(dump, Does.Contain("explicit one-sided override: " + at + " @locked = \"false\" on the client only (sync is off for it); server trade@locked stays \"1\""));
                    Assert.That(p.Find(S) == null || Fake.Text(p, S, Five) == null, Is.True, "case " + i);
                }
            }
        }

        [Test]
        public void SidesThatDisagreeInTheShippedDataAreNotSynced()
        {
            using (var f = new FakeItems(true))
            {
                Plan p = Build(f, Client(Set("item[id=1200005]/environment/property", "sellPrice", "70")));
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(p.Warnings.Count, Is.EqualTo(1), dump);
                Assert.That(dump, Does.Contain("@sellPrice is changed on the client only: the shipped values do not correspond (client \"7\", server property@sellPrice \"8\")"));
                Assert.That(p.Find(S) == null || Fake.Text(p, S, Five) == null, Is.True);
            }
        }

        [Test]
        public void FieldsWithoutAMapLine()
        {
            using (var f = new FakeItems(true))
            {
                const string env = "item[id=1200005]/environment/";
                Plan p = Build(f, Client(Set(env + "odd", "a", "2") + Set(env + "option", "constantID", "78") + Set(env + "option", "optionLevel", "3") +
                    Set(env + "property", "tradable", "0")));
                string dump = Fake.Dump(p);
                // the one error: the kms option table has no row 78 (ItemOptionChecks)
                Assert.That(p.Errors.Count, Is.EqualTo(1), dump);
                Assert.That(dump, Does.Contain("item 1200005 names constant option 78 (constantID, was 77) but the kms table table/itemoptionconstant.xml has no row of it"));
                Assert.That(p.Warnings.Count, Is.EqualTo(2), dump);     // client-only fields are silent
                Assert.That(dump, Does.Contain("odd@a is changed on the client only: the field map of \"item\" has no entry for it"));
                Assert.That(dump, Does.Contain("option@constantID is changed on the client only: the field map of \"item\" has no entry for it (server drop@rate is a candidate that agreed on 6 of 10 compared ids; it is not written)"));
                Assert.That(Synced(p), Is.EqualTo(0));

                // an item with no server file
                p = Build(f, Client(Set("item[id=1200003]/environment/property", "slotMax", "2")));
                Assert.That(p.Warnings.Count, Is.EqualTo(1), Fake.Dump(p));
                Assert.That(Fake.Dump(p), Does.Contain("item 1200003 has no entry in server:Xml.m2d; changed on the client only"));
            }
            // no server archive in the data set: one line for the mod, not one per field
            using (var f = new FakeItems(false))
            {
                Plan p = Build(f, Client(Set("item[id=1200005]/environment/trade", "locked", "false") + Set("item[id=1200005]/environment/look", "icon", "drum")));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Fake.Dump(p), Does.Contain("is not in the data set: the mapped fields this mod changes are written on the client side only"));
            }
        }

        [Test]
        public void AClonedItemIsSyncedToo()
        {
            using (var f = new FakeItems(true))
            {
                ItemDraft n = f.Editor().Clone(1200001, 1250000, "Glass Whistle", "It hums.");
                n.Set(FakeItems.Section(n, "item", "environment/limit"), "levelLimit", "30");       // the client row only
                Plan p = f.Plan();
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(p.Warnings, Is.Empty, dump);
                Assert.That(Fake.Text(p, C, Bucket), Does.Contain("<limit levelLimit=\"30\" />"));
                Assert.That(Fake.Text(p, S, "item/0/12/01250000.xml"), Does.Contain("<limit levelLimit=\"30\" />"));
            }
        }

        [Test]
        public void TheItemDraftMirrorsThroughTheMap()
        {
            using (var f = new FakeItems(true))
            {
                ItemDraft d = f.Editor().Open(1200005);
                EntitySection look = FakeItems.Section(d, "item", "environment/look");
                string field; FieldMapEntry e;
                EntitySection other = d.Mirror(look, "icon", out field, out e);
                Assert.That(other.Part.Kind, Is.EqualTo("file"));
                Assert.That(field, Is.EqualTo("iconPath"));
                Assert.That(e.Convert, Is.EqualTo("affix"));
                Assert.That(d.Set(look, "icon", "drum", true), Is.SameAs(other));
                Assert.That(d.IsEdited(other, "iconPath"), Is.False, "the edit is written once; the plan carries it");

                EntitySection worth = FakeItems.Section(d, "file", "environment/worth");
                EntitySection cw = d.Set(worth, "coins", "10,41,90", true);
                Assert.That(cw.Part.Kind, Is.EqualTo("item"));
                Assert.That(d.IsEdited(cw, "coin"), Is.False);
                Assert.That(d.Mirror(worth, "tier"), Is.Null, "server-only");
                Assert.That(d.Mirror(FakeItems.Section(d, "item", "environment/odd"), "a"), Is.Null, "no map line");
                // the shipped prices differ: nothing to mirror to
                Assert.That(d.Set(FakeItems.Section(d, "item", "environment/property"), "sellPrice", "70", true), Is.Null);

                // not mirrored on purpose: the line is marked, and Plan says so instead of writing the server
                d.Set(FakeItems.Section(d, "item", "environment/trade"), "locked", "false", false);
                Assert.That(f.Mod.ToXml(), Does.Contain("<set name=\"locked\" value=\"false\" sync=\"false\" />"));
                Plan p = f.Plan();
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(Fake.Text(p, S, Five), Does.Contain("<trade locked=\"1\" />"));
                Assert.That(Fake.Text(p, S, Five), Does.Contain("<look iconPath=\"./img/drum.png\" />"));
                Assert.That(Fake.Text(p, C, Bucket), Does.Contain("coin=\"41\""));
                Assert.That(dump, Does.Contain("(sync is off for it)"));
                Assert.That(dump, Does.Contain("the shipped values do not correspond"));
                Assert.That(p.Warnings.Count, Is.EqualTo(2), dump);
            }
        }

        [Test]
        public void TheShippedMapNamesFieldsOnly()
        {
            EntityMap item = FieldMaps.Default.Entity("item");
            Assert.That(item, Is.Not.Null);
            Assert.That(item.Client.Row, Is.EqualTo("item"));
            Assert.That(item.Server.Row, Is.Null);
            Assert.That(item.Entries.Count, Is.GreaterThan(50));
            var seen = new Dictionary<string, bool>();
            foreach (FieldMapEntry e in item.Entries)
            {
                Assert.That(e.Client, Does.Contain("@"));
                Assert.That(e.Server, Does.Contain("@"));
                Assert.That(e.Confidence, Is.Not.Empty, e.Client);
                if (!e.Candidate) { Assert.That(seen.ContainsKey(e.Client), Is.False, e.Client); seen[e.Client] = true; }
            }
            bool client;
            Assert.That(FieldMaps.Default.Find(C, "itemdata/012.xml", out client), Is.SameAs(item));
            Assert.That(client, Is.True);
            Assert.That(FieldMaps.Default.Find(S, "item/1/23/12300000.xml", out client), Is.SameAs(item));
            Assert.That(client, Is.False);
            Assert.That(FieldMaps.Default.Find(C, "table/pet.xml", out client), Is.Null);
            Assert.Throws<ModKitException>(() => FieldMaps.Parse("<fieldmaps><entity name=\"x\"><client archive=\"client:Xml.m2d\" entry=\"a/{nope}.xml\" under=\"e\"/></entity></fieldmaps>"));
        }

        [Test]
        public void TheShippedNpcMapPairsEachFieldOnce()
        {
            EntityMap npc = FieldMaps.Default.Entity("npc");
            Assert.That(npc, Is.Not.Null);
            Assert.That(npc.Client.Row, Is.EqualTo("npc"));
            Assert.That(npc.Server.Row, Is.Null);
            Assert.That(npc.Entries.Count, Is.GreaterThan(100));
            var client = new Dictionary<string, bool>();
            var server = new Dictionary<string, bool>();
            foreach (FieldMapEntry e in npc.Entries)
            {
                if (e.Candidate) continue;
                Assert.That(client.ContainsKey(e.Client), Is.False, e.Client);
                Assert.That(server.ContainsKey(e.Server), Is.False, e.Server);
                client[e.Client] = server[e.Server] = true;
            }
            Assert.That(client.ContainsKey("@level"), Is.True, "an attribute of <environment> itself");
            Assert.That(client.ContainsKey("stat@hp"), Is.True);
            bool c;
            Assert.That(FieldMaps.Default.Find(C, "npcdata/001.xml", out c), Is.SameAs(npc));
            Assert.That(c, Is.True);
            Assert.That(FieldMaps.Default.Find(S, "npc/12/34/12345678.xml", out c), Is.SameAs(npc));
            Assert.That(c, Is.False);
        }
        [Test]
        public void TheRowsOfOtherTablesThatBelongToAnItemAreFound()
        {
            using (var f = new FakeItems(true))
            {
                EntityMap em = FakeItems.Maps.Entity("item");
                RefList five = ServerRefs.Find(f.Data, em, 1200005, 10);
                string text = five.ToText();
                Assert.That(five.Hits.Count, Is.EqualTo(3), text);
                Assert.That(text, Does.Contain("rate rows (code = 4):"));
                Assert.That(text, Does.Contain("server:Xml.m2d rates/a.xml  rate[code=4]"), text);
                Assert.That(text, Does.Contain("code=\"4\" grade=\"1\" bonus=\"3\""));
                Assert.That(text, Does.Contain("bag rows (item = 1200005):"));
                Assert.That(text, Does.Contain("bag[id=2]/v"), text);
                // the selector of a hit opens the row
                RefHit bag = five.Hits[five.Hits.Count - 1];
                M2dArchive arch = f.Data.Open(bag.Archive);
                XmlDoc doc = XmlDoc.Parse(arch.Read(arch.Find(bag.Entry)), "bags");
                Assert.That(RowPath.Parse(bag.Selector).Resolve(doc.Root).Attr("count"), Is.EqualTo("9"));

                RefList capped = ServerRefs.Find(f.Data, em, 1200005, 1);
                Assert.That(capped.Hits.Count, Is.EqualTo(2));
                Assert.That(capped.ToText(), Does.Contain("bag rows: 1 more row(s) are not listed"));
                Assert.That(ServerRefs.Find(f.Data, em, 1200002, 10).ToText(), Does.Not.Contain("bag rows ("));
                string none = ServerRefs.Find(f.Data, em, 1200009, 10).ToText();
                Assert.That(none, Does.Contain("no row of another table refers to it"));
                Assert.That(none, Does.Contain("the server has no entry of this id"));
            }
        }

        [Test]
        public void RefsShowTheRowsAsTheModLeavesThem()
        {
            using (var f = new FakeItems(true))
            {
                EntityMap em = FakeItems.Maps.Entity("item");
                Plan p = Build(f, "<edit archive=\"" + S + "\" entry=\"rates/a.xml\">" + Set("rate[code=4]", "bonus", "8") + "</edit>");
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string after = ServerRefs.Find(f.Data, em, 1200005, 10, p).ToText();
                Assert.That(after, Does.Contain("code=\"4\" grade=\"1\" bonus=\"8\""), after);
                Assert.That(ServerRefs.Find(f.Data, em, 1200005, 10).ToText(), Does.Contain("bonus=\"3\""), "without a plan: as shipped");
            }
        }

        [Test]
        public void TheSyncReportListsWhatDiffersBeforeAndAfter()
        {
            using (var f = new FakeItems(true))
            {
                EntityMap em = FakeItems.Maps.Entity("item");
                SyncReport shipped = SyncReport.ForId(f.Data, null, em, 1200005);
                string text = shipped.ToText(false);
                Assert.That(shipped.ClientEntry, Is.EqualTo(Bucket));
                Assert.That(shipped.ServerEntry, Is.EqualTo(Five));
                Assert.That(shipped.Disagreeing(false), Is.EqualTo(1), text);
                Assert.That(text, Does.Contain("DIFFERS  client property@sellPrice"));
                Assert.That(text, Does.Contain("client \"7\", server \"8\""));
                Assert.That(text, Does.Not.Contain("look@icon"), "fields that agree are not listed");
                Assert.That(shipped.ToText(true), Does.Contain("agrees  client look@icon  server look@iconPath  (affix"));

                const string env = "item[id=1200005]/environment/";
                Plan p = Build(f, Client(Set(env + "property", "sellPrice", "8") + Set(env + "look", "icon", "drum") +
                    "<change at=\"" + env + "trade\" sync=\"false\"><set name=\"locked\" value=\"false\"/></change>"));
                SyncReport after = SyncReport.ForId(f.Data, p, em, 1200005);
                text = after.ToText(false);
                Assert.That(after.Disagreeing(false), Is.EqualTo(1), text);
                Assert.That(after.Disagreeing(true), Is.EqualTo(1), text);
                Assert.That(text, Does.Contain("differed, agrees now"), text);
                Assert.That(text, Does.Contain("changed, agrees  client look@icon"), text);
                Assert.That(text, Does.Contain("after:  client \"drum\", server \"./img/drum.png\""), text);
                Assert.That(text, Does.Contain("DIFFERS  client trade@locked"), text);
                Assert.That(text, Does.Contain("after:  client \"false\", server \"1\"  (differ)"), text);

                // a field that one side does not carry is listed apart and is not counted as a difference
                EntityMap wide = FieldMaps.Parse(FakeItems.MapXml.Replace("<map client=\"property@slotMax\"", "<map client=\"odd@a\" server=\"odd@a\" /><map client=\"property@slotMax\"")).Entity("item");
                SyncReport odd = SyncReport.ForId(f.Data, null, wide, 1200005);
                Assert.That(odd.Disagreeing(true), Is.EqualTo(1), odd.ToText(true));
                Assert.That(odd.Omitted(true), Is.EqualTo(1));
                Assert.That(odd.ToText(false), Does.Not.Contain("odd@a"));
                Assert.That(odd.ToText(false), Does.Contain("1 carried by one side only (listed with all fields)"));
                Assert.That(odd.ToText(true), Does.Contain("server omits it  client odd@a"));

                Assert.Throws<ModKitException>(() => SyncReport.ForId(f.Data, null, em, 1299999));
            }
        }
    }
}
