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

            public FakeIcons(bool kmsImages, bool serverImages, bool gmsImages = false)
            {
                Dir = Path.Combine(Path.GetTempPath(), "modkit-icons-" + Guid.NewGuid().ToString("N"));
                string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
                foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);
                M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                {
                    E("itemdata/012.xml", Head + "<ms2>\n" + Row(1200001, null) + Row(1200002, "77000001") + Row(1200003, "./Data/Resource/Image/item/icon/sub/reed.png")
                        + "\t<item id=\"1200004\">\n\t\t<environment>\n\t\t\t<limit level=\"1\" />\n\t\t</environment>\n\t</item>\n" + "</ms2>\n"),
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
                Directory.CreateDirectory(Path.Combine(g, "Resource"));
                if (gmsImages) M2dWriter.Create(Path.Combine(g, "Resource", "Image.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>> { B("item/icon/77000001.png", Img("g77")) });
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
        public void ReadsNothingAfterDispose()
        {
            using (var f = new FakeIcons(true, false))
            {
                var src = new ImageSource(f.Data, null);
                Assert.AreEqual(Img("k1"), src.Read("item/icon/1200001.png"));
                src.Dispose();
                // a background load that ends after the view let go of the source gets nothing (and opens nothing)
                Assert.IsNull(src.Read("item/icon/1200001.png"));
                Assert.IsFalse(src.Has("item/icon/1200001.png"));
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

        // ---- an item's own icon (ItemIcon.cs) ----

        // a made-up PNG: the signature, an IHDR chunk of the size and an empty IEND (the checks read the header only)
        static byte[] Png(int w, int h)
        {
            var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
            foreach (int v in new[] { w, h }) { b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v); }
            b.AddRange(new byte[] { 8, 6, 0, 0, 0, 0, 0, 0, 0 });
            b.AddRange(new byte[] { 0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0, 0, 0, 0 });
            return b.ToArray();
        }

        static Plan PlanOf(FakeIcons f, Mod m) { return Planner.Build(f.Data, new[] { m }, new PlanOptions { Maps = FieldMaps.Parse(MapXml) }); }

        static List<string> Lines(List<PlanMessage> l, string part)
        {
            var r = new List<string>();
            foreach (PlanMessage x in l) if (x.ToString().IndexOf(part, StringComparison.Ordinal) >= 0) r.Add(x.ToString());
            return r;
        }

        static List<EntryOp> ImageOps(Mod m)
        {
            var l = new List<EntryOp>();
            foreach (ModOp o in m.Ops) { var e = o as EntryOp; if (e != null) l.Add(e); }
            return l;
        }

        [Test]
        public void PngHeader()
        {
            string why;
            PngInfo p = PngInfo.Read(Png(60, 60), out why);
            Assert.IsNotNull(p, why);
            Assert.AreEqual("60x60", p.ToString());
            Assert.AreEqual(6, p.ColorType);
            Assert.IsNull(PngInfo.Read(Img("not a png at all, long enough to read"), out why));
            StringAssert.Contains("no PNG signature", why);
            Assert.IsNull(PngInfo.Read(new byte[] { 0x89, 0x50 }, out why));
            StringAssert.Contains("too short", why);
            Assert.IsNull(PngInfo.Read(Png(0, 60), out why));
            Assert.IsNull(PngInfo.Read(null, out why));
        }

        [Test]
        public void OwnIconGoesToEachClientAndBothSides()
        {
            using (var f = new FakeIcons(true, true, true))
            {
                Mod m = NewMod();
                ItemDraft d = Items(f, m).Open(1200002);
                byte[] png = Png(60, 60);
                CollectionAssert.AreEqual(new[] { "client:Resource/Image.m2d", "gms-client:Resource/Image.m2d" }, ItemIcon.Set(d, png));
                List<EntryOp> ops = ImageOps(m);
                Assert.AreEqual(2, ops.Count);
                foreach (EntryOp o in ops)
                {
                    Assert.AreEqual("item/icon/mod/1200002.png", o.Name);
                    Assert.AreEqual(png, m.Files[o.File]);
                }
                Assert.AreEqual("files/gms-client/item/icon/mod/1200002.png", ops[1].File);
                // the kms row and the per-id files name it; the server's file too, though the server draws no icon
                Assert.AreEqual("item/icon/mod/1200002.png", d.IconEntry(Profile.KmsClient));
                Assert.AreEqual("item/icon/mod/1200002.png", d.IconEntry(Profile.GmsClient));
                Assert.AreEqual("item/icon/mod/1200002.png", d.IconEntry(Profile.Server));
                Assert.IsTrue(ItemIcon.HasOwn(d));
                Assert.AreEqual(png, ItemIcon.ModImage(m, "Item/Icon/Mod/1200002.png"));
                Assert.IsNull(ItemIcon.ModImage(m, "item/icon/1200002.png"));
                Plan plan = PlanOf(f, m);
                CollectionAssert.IsEmpty(plan.Errors);
                CollectionAssert.IsEmpty(Lines(plan.Warnings, "image"));
                CollectionAssert.IsEmpty(Lines(plan.Warnings, "slotIcon"));
            }
        }

        [Test]
        public void TakeOutPutsTheShippedIconsBack()
        {
            using (var f = new FakeIcons(true, true))
            {
                Mod m = NewMod();
                ItemDraft d = Items(f, m).Open(1200002);
                ItemIcon.Set(d, Png(60, 60));
                Assert.AreEqual(1, ImageOps(m).Count);   // no gms image archive: the kms client's only
                ItemIcon.Take(d);
                CollectionAssert.IsEmpty(ImageOps(m));
                CollectionAssert.IsEmpty(m.Files);
                Assert.IsFalse(ItemIcon.HasOwn(d));
                // each side its own shipped value again, and no edit is left in the mod
                Assert.AreEqual("item/icon/77000001.png", d.IconEntry(Profile.KmsClient));
                Assert.AreEqual("item/icon/77000009.png", d.IconEntry(Profile.Server));
                foreach (EntitySection s in d.Sections) Assert.IsFalse(d.IsEdited(s, "slotIcon"), s.Part.Archive);
                CollectionAssert.IsEmpty(Items(f, Mod.Parse(m.ToXml(), "round trip")).Open(1200002).Sections.FindAll(s => s.Part.IsNew));
            }
        }

        [Test]
        public void OwnIconRejectsWhatIsNotAPng()
        {
            using (var f = new FakeIcons(true, true))
            {
                Mod m = NewMod();
                ItemDraft d = Items(f, m).Open(1200001);
                var x = Assert.Throws<ModKitException>(delegate { ItemIcon.Set(d, Img("a jpeg, say, or some text file")); });
                StringAssert.Contains("not a PNG image", x.Message);
                CollectionAssert.IsEmpty(m.Ops);
                CollectionAssert.IsEmpty(m.Files);
            }
            using (var f = new FakeIcons(false, true))
            {
                // the server's image archive does not count: no client could show the icon
                Mod m = NewMod();
                var x = Assert.Throws<ModKitException>(delegate { ItemIcon.Set(Items(f, m).Open(1200001), Png(60, 60)); });
                StringAssert.Contains("no client data folder", x.Message);
                CollectionAssert.IsEmpty(m.Ops);
            }
        }

        [Test]
        public void OwnIconLeavesNoImageWhenTheItemHasNoSlotIcon()
        {
            using (var f = new FakeIcons(true, true, true))
            {
                // item 1200004 has no property element: nothing to name the icon, so no image entry stays in the mod
                Mod m = NewMod();
                var x = Assert.Throws<ModKitException>(delegate { ItemIcon.Set(Items(f, m).Open(1200004), Png(60, 60)); });
                StringAssert.Contains("has no property@slotIcon", x.Message);
                CollectionAssert.IsEmpty(ImageOps(m));
                CollectionAssert.IsEmpty(m.Files);
                // a file far bigger than an icon is refused before anything is written
                var big = new byte[ItemIcon.MaxBytes + 1];
                Array.Copy(Png(60, 60), big, Png(60, 60).Length);
                x = Assert.Throws<ModKitException>(delegate { ItemIcon.Set(Items(f, m).Open(1200001), big); });
                StringAssert.Contains("more than", x.Message);
                CollectionAssert.IsEmpty(m.Ops);
            }
        }

        [Test]
        public void ApplyWritesTheImageAndRevertTakesItOut()
        {
            using (var f = new FakeIcons(true, true))
            {
                Mod m = NewMod();
                byte[] png = Png(60, 60);
                ItemIcon.Set(Items(f, m).Open(1200001), png);
                var ws = new Workspace(Path.Combine(f.Dir, "ws"));
                ApplyResult r = ws.Apply(f.Data, new[] { m }, null);
                Assert.IsTrue(r.Applied, EditorSession.Report(r));
                string images = Path.Combine(ws.Dir, "client", "Resource", "Image.m2d");
                Assert.IsTrue(System.IO.File.Exists(images), string.Join("\n", r.Outputs.ConvertAll(o => o.Path).ToArray()));
                using (M2dArchive a = M2dArchive.Open(images))
                {
                    Assert.AreEqual(png, a.Read(a.Find("item/icon/mod/1200001.png")));
                    Assert.AreEqual(Img("k1"), a.Read(a.Find("item/icon/1200001.png")), "the shipped icons stay");
                }
                using (M2dArchive shipped = M2dArchive.Open(Path.Combine(f.Dir, "kms", "Resource", "Image.m2d")))
                    Assert.IsNull(shipped.Find("item/icon/mod/1200001.png"), "the shipped archive is only read");
                ws.Revert(f.Data, null, null);
                Assert.IsFalse(System.IO.File.Exists(images));
            }
        }

        [Test]
        public void SavedModReadsTheIconFromItsFolder()
        {
            using (var f = new FakeIcons(true, true))
            {
                Mod m = NewMod();
                byte[] png = Png(60, 60);
                ItemIcon.Set(Items(f, m).Open(1200001), png);
                string dir = Path.Combine(f.Dir, "mod");
                m.Save(dir);
                CollectionAssert.IsEmpty(m.Files);
                Assert.AreEqual(png, ItemIcon.ModImage(m, "item/icon/mod/1200001.png"));
                Assert.IsTrue(System.IO.File.Exists(Path.Combine(dir, "files", "client", "item", "icon", "mod", "1200001.png")));
                CollectionAssert.IsEmpty(PlanOf(f, m).Errors);
            }
        }

        [Test]
        public void PlanChecksTheImages()
        {
            using (var f = new FakeIcons(true, true))
            {
                Mod m = NewMod();
                ItemEditor ed = Items(f, m);
                var trig = new TriggerEditor(f.Data, m);
                ItemIcon.Set(ed.Open(1200001), Png(32, 32));
                trig.Store("client:Resource/Image.m2d", "item/icon/mod/broken.png", Img("no png"));
                trig.Store("client:Resource/Image.m2d", "item/icon/1200001.png", Png(60, 60));
                ed.Open(1200003).SetBoth(Icons.ItemIconField, "./Data/Resource/Image/item/icon/sub/none.png");
                Plan plan = PlanOf(f, m);
                Assert.AreEqual(1, Lines(plan.Errors, "broken.png: not a PNG image").Count, string.Join("\n", Lines(plan.Errors, "")));
                Assert.AreEqual(1, Lines(plan.Warnings, "1200001.png: an item icon of 32x32").Count, string.Join("\n", Lines(plan.Warnings, "")));
                Assert.AreEqual(1, Lines(plan.Warnings, "item/icon/1200001.png: replaces a shipped image").Count);
                Assert.AreEqual(1, Lines(plan.Warnings, "item 1200003: slotIcon names item/icon/sub/none.png").Count, string.Join("\n", Lines(plan.Warnings, "")));
                // the shipped icons the mod does not touch, and the mod's own new entry, raise nothing
                Assert.AreEqual(0, Lines(plan.Warnings, "item 1200002").Count);
                Assert.AreEqual(0, Lines(plan.Warnings, "mod/1200001.png: replaces").Count);
            }
        }
    }
}
