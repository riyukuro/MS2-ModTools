// Item icons and npc portraits on made-up data: the client's path rule, the entry per profile with a mod's edit,
// and reading the image from the first data root that has Resource/Image.m2d.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class IconsTests
    {
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        [Test]
        public void ClientPathRule()
        {
            Assert.AreEqual("./Data/Resource/Image/item/icon/1200001.png", Icons.ItemIconPath(1200001, null));
            Assert.AreEqual("./Data/Resource/Image/item/icon/1200001.png", Icons.ItemIconPath(1200001, " "));
            Assert.AreEqual("./Data/Resource/Image/item/icon/77000001.png", Icons.ItemIconPath(1200001, "77000001"));
            Assert.AreEqual("./Data/Resource/Image/item/icon/fake_reed.png", Icons.ItemIconPath(1200001, "fake_reed"));
            Assert.AreEqual("./Data/Resource/Image/item/icon/sub/1.png", Icons.ItemIconPath(1200001, "./Data/Resource/Image/item/icon/sub/1.png"));
        }

        [Test]
        public void ImageEntryOfPaths()
        {
            Assert.AreEqual("item/icon/sub/1.png", Icons.ImageEntry("./Data/Resource/Image/item/icon/sub/1.png"));
            Assert.AreEqual("Item/Icon/A.png", Icons.ImageEntry(".\\data\\resource\\image\\Item\\Icon\\A.png"));
            Assert.AreEqual("portrait/npc/x.png", Icons.ImageEntry("Data/Resource/Image/portrait/npc/x.png"));
            Assert.IsNull(Icons.ImageEntry("./Data/Resource/Model/x.nif"));
            Assert.IsNull(Icons.ImageEntry(""));
            Assert.IsNull(Icons.ImageEntry(null));
            Assert.AreEqual("item/icon/5.png", Icons.ItemIconEntry(5, null));
        }

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }
        static KeyValuePair<string, byte[]> B(string name, byte[] bytes) { return new KeyValuePair<string, byte[]>(name, bytes); }

        static string Row(long id, string icon)
        {
            return "\t<item id=\"" + id + "\">\n\t\t<environment>\n\t\t\t<property slotMax=\"1\"" + (icon == null ? "" : " slotIcon=\"" + icon + "\"") + " />\n\t\t</environment>\n\t</item>\n";
        }

        static string File(string icon)
        {
            return Head + "<ms2>\n\t<environment feature=\"\" locale=\"\">\n\t\t<property slotMax=\"1\" slotIcon=\"" + icon + "\" />\n\t</environment>\n</ms2>\n";
        }

        const string MapXml = "<fieldmaps version=\"1\"><entity name=\"item\">" +
            "<client archive=\"client:Xml.m2d\" entry=\"itemdata/{id/100000}.xml\" row=\"item\" key=\"id\" under=\"environment\" />" +
            "<server archive=\"server:Xml.m2d\" entry=\"item/*/*/{id}.xml\" under=\"environment\" />" +
            "<map client=\"property@slotIcon\" server=\"property@slotIcon\" convert=\"affix\" prefix=\"./Data/Resource/Image/item/icon/\" suffix=\".png\" />" +
            "</entity><entity name=\"npc\">" +
            "<client archive=\"client:Xml.m2d\" entry=\"npcdata/{id/100000}.xml\" row=\"npc\" key=\"id\" under=\"environment\" />" +
            "<server archive=\"server:Xml.m2d\" entry=\"npc/*/*/{id}.xml\" under=\"environment\" />" +
            "<map client=\"@portrait\" server=\"basic@portrait\" />" +
            "</entity></fieldmaps>";

        // made-up image bytes: the reader hands them on as they are
        static byte[] Img(string tag) { return Encoding.ASCII.GetBytes("img:" + tag); }

        sealed class FakeIcons : IDisposable
        {
            public readonly string Dir;
            public readonly DataSet Data;

            public FakeIcons(bool kmsImages, bool serverImages)
            {
                Dir = Path.Combine(Path.GetTempPath(), "modkit-icons-" + Guid.NewGuid().ToString("N"));
                string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
                foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);
                M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                {
                    E("itemdata/012.xml", Head + "<ms2>\n" + Row(1200001, null) + Row(1200002, "77000001") + Row(1200003, "./Data/Resource/Image/item/icon/sub/reed.png") + "</ms2>\n"),
                    E("string/en/itemname.xml", Head + "<ms2>\n\t<key id=\"1200001\" name=\"Tin Whistle\" />\n</ms2>\n"),
                    E("npcdata/029.xml", Head + "<ms2>\n\t<npc id=\"2900001\">\n\t\t<environment level=\"3\" portrait=\"./Data/Resource/Image/Portrait/Npc/fake_mole.png\" />\n\t</npc>\n" +
                        "\t<npc id=\"2900002\">\n\t\t<environment level=\"3\" />\n\t</npc>\n</ms2>\n"),
                    E("string/en/npcname.xml", Head + "<ms2>\n\t<key id=\"02900001\" name=\"Fake Mole\" />\n</ms2>\n"),
                });
                foreach (string d in new[] { g, s })
                    M2dWriter.Create(Path.Combine(d, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                    {
                        E("item/0/12/01200002.xml", File(d == s ? "77000009" : "77000001")),
                        E("npc/02/90/02900002.xml", Head + "<ms2>\n\t<environment feature=\"\" locale=\"\">\n\t\t<basic level=\"3\" portrait=\"./Data/Resource/Image/Portrait/Npc/fake_guard.png\" />\n\t</environment>\n</ms2>\n"),
                        E("string/en/itemname.xml", Head + "<ms2>\n\t<key id=\"1200002\" name=\"Clay Drum\" />\n</ms2>\n"),
                    });
                var images = new List<KeyValuePair<string, byte[]>>
                {
                    B("item/icon/1200001.png", Img("k1")), B("item/icon/77000001.png", Img("k77")), B("item/icon/sub/reed.png", Img("reed")),
                    B("portrait/npc/fake_mole.png", Img("mole")),
                };
                Directory.CreateDirectory(Path.Combine(k, "Resource"));
                Directory.CreateDirectory(Path.Combine(s, "Resource"));
                if (kmsImages) M2dWriter.Create(Path.Combine(k, "Resource", "Image.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, images);
                if (serverImages)
                    M2dWriter.Create(Path.Combine(s, "Resource", "Image.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                    {
                        B("item/icon/77000001.png", Img("s77")), B("item/icon/77000009.png", Img("s79")), B("portrait/npc/fake_guard.png", Img("guard")),
                    });
                Data = new DataSet(k, g, s);
            }

            public void Dispose()
            {
                Data.Dispose();
                if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
            }
        }

        static ItemEditor Items(FakeIcons f, Mod m) { return new ItemEditor(f.Data, m) { Maps = FieldMaps.Parse(MapXml) }; }
        static Mod NewMod() { return Mod.Parse("<mod id=\"icons.test\" name=\"t\" version=\"1\"/>", "test mod"); }

        [Test]
        public void ItemEntryPerProfile()
        {
            using (var f = new FakeIcons(true, true))
            {
                ItemEditor ed = Items(f, NewMod());
                Assert.AreEqual("item/icon/1200001.png", ed.Open(1200001).IconEntry());
                Assert.AreEqual("item/icon/77000001.png", ed.Open(1200002).IconEntry());
                Assert.AreEqual("item/icon/sub/reed.png", ed.Open(1200003).IconEntry());
                // the server's file names another icon than the kms-client's row
                Assert.AreEqual("item/icon/77000001.png", ed.Open(1200002).IconEntry(Profile.KmsClient));
                Assert.AreEqual("item/icon/77000009.png", ed.Open(1200002).IconEntry(Profile.Server));
                // a profile the item has no row or file of: the item's field
                Assert.AreEqual("item/icon/1200001.png", ed.Open(1200001).IconEntry(Profile.Server));
            }
        }

        [Test]
        public void ModEditShowsTheNewIcon()
        {
            using (var f = new FakeIcons(true, true))
            {
                Mod m = NewMod();
                ItemEditor ed = Items(f, m);
                ItemDraft d = ed.Open(1200001);
                d.SetBoth(Icons.ItemIconField, "./Data/Resource/Image/item/icon/sub/reed.png");
                Assert.AreEqual("item/icon/sub/reed.png", d.IconEntry());
                // read back from the mod, as a view opens it again
                Mod again = Mod.Parse(m.ToXml(), "round trip");
                Assert.AreEqual("item/icon/sub/reed.png", Items(f, again).Open(1200001).IconEntry());
            }
        }

        [Test]
        public void ReadsFromTheFirstRootThatHasTheImage()
        {
            using (var f = new FakeIcons(true, true))
            using (var kms = new ImageSource(f.Data, null))
            using (var srv = new ImageSource(f.Data, Profile.Server))
            {
                CollectionAssert.AreEqual(new[] { "client", "server" }, kms.Roots);
                CollectionAssert.AreEqual(new[] { "server", "client" }, srv.Roots);
                Assert.AreEqual(Img("k77"), kms.Read("item/icon/77000001.png"));
                Assert.AreEqual(Img("s77"), srv.Read("item/icon/77000001.png"));
                // an entry only the other root has, and the case of the name does not matter
                Assert.AreEqual(Img("s79"), kms.Read("item/icon/77000009.png"));
                Assert.AreEqual(Img("reed"), srv.Read("Item/Icon/Sub/Reed.png"));
                Assert.IsNull(kms.Read("item/icon/404.png"));
                Assert.IsNull(kms.Read(null));
                Assert.IsTrue(kms.Has("item/icon/1200001.png"));
                Assert.IsFalse(kms.Has("item/icon/404.png"));
            }
        }

        [Test]
        public void NoImageArchive()
        {
            using (var f = new FakeIcons(false, false))
            using (var src = new ImageSource(f.Data, Profile.GmsClient))
            {
                Assert.IsFalse(src.Available);
                Assert.IsNull(src.Read("item/icon/1200001.png"));
                // the entry is still known: the view shows the placeholder
                Assert.AreEqual("item/icon/1200001.png", Items(f, NewMod()).Open(1200001).IconEntry());
            }
        }

        [Test]
        public void NpcPortraits()
        {
            using (var f = new FakeIcons(true, true))
            using (var src = new ImageSource(f.Data, null))
            {
                Mod m = NewMod();
                var ed = new NpcEditor(f.Data, m) { Maps = FieldMaps.Parse(MapXml) };
                EntityDraft mole = ed.OpenAny(2900001), guard = ed.OpenAny(2900002);
                Assert.AreEqual("Portrait/Npc/fake_mole.png", mole.PortraitEntry());
                // the kms-client row has none: the server file's basic@portrait
                Assert.AreEqual("Portrait/Npc/fake_guard.png", guard.PortraitEntry());
                Assert.AreEqual(Img("mole"), src.Read(mole.PortraitEntry()));
                Assert.AreEqual(Img("guard"), src.Read(guard.PortraitEntry()));
            }
        }
    }
}
