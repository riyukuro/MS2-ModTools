// Tests of the table editor's model (TableDraft, ModFolder). Made-up data only.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class TableDraftTests
    {
        const string Client = "client:Xml.m2d", Server = "server:Xml.m2d", Pet = "table/pet.xml";

        static Mod NewMod() { return new Mod { Id = "t.draft", Name = "draft", Version = "1" }; }

        static TableRow Row(TableDraft d, string label) { return d.Rows.Single(r => r.Label == label); }

        [Test]
        public void RowsAndColumnsOfTheOriginal()
        {
            using (var f = new Fake())
            {
                var d = new TableDraft(f.Data, NewMod(), Client, Pet, null);
                Assert.That(d.Columns, Is.EqualTo(new[] { "id", "name", "hp", "speed", "feature" }));
                Assert.That(d.Rows.Select(r => r.Label), Is.EqualTo(new[] { "pet[id=100]", "pet[id=101]", "pet[id=102][feature=]", "pet[id=102][feature=Alpha]" }));
                Assert.That(d.Get(d.Rows[0], "hp"), Is.EqualTo("50"));
                Assert.That(d.Get(d.Rows[0], "feature"), Is.Null);
                Assert.That(d.HasEdits, Is.False);
                Assert.That(d.Mod.Ops, Is.Empty);
            }
        }

        [Test]
        public void SetBecomesAChangeAndSettingBackDropsIt()
        {
            using (var f = new Fake())
            {
                var d = new TableDraft(f.Data, NewMod(), Client, Pet, null);
                TableRow r = Row(d, "pet[id=100]");
                d.Set(r, "hp", "65");
                d.Set(r, "speed", null);
                d.Set(r, "hp", "66");       // a second edit of one cell is still one <set>
                Assert.That(d.Get(r, "hp"), Is.EqualTo("66"));
                Assert.That(d.Get(r, "speed"), Is.Null);
                Assert.That(d.IsEdited(r, "hp") && d.IsEdited(r, "speed") && !d.IsEdited(r, "name"));
                Assert.That(d.Mod.ToXml(), Does.Contain(
                    "  <edit archive=\"client:Xml.m2d\" entry=\"table/pet.xml\">\n" +
                    "    <change at=\"pet[id=100]\">\n" +
                    "      <unset name=\"speed\" />\n" +
                    "      <set name=\"hp\" value=\"66\" />\n" +
                    "    </change>\n" +
                    "  </edit>\n"));

                Plan p = f.Plan(d.Mod);
                Assert.That(p.HasErrors || p.HasConflicts, Is.False, Fake.Dump(p));
                Assert.That(Fake.Text(p, Client, Pet), Does.Contain("<pet id=\"100\" name=\"Mossy\" hp=\"66\" />"));

                d.Set(r, "hp", "50");
                d.Set(r, "speed", "1.5");
                Assert.That(r.IsChanged, Is.False);
                Assert.That(d.Mod.Ops, Is.Empty, "no empty <edit> is left behind");
            }
        }

        [Test]
        public void CloneAndDelete()
        {
            using (var f = new Fake())
            {
                var d = new TableDraft(f.Data, NewMod(), Client, Pet, null);
                TableRow src = Row(d, "pet[id=101]");
                d.Set(src, "hp", "71");
                TableRow made = d.Clone(src);
                Assert.That(made.IsAdded);
                Assert.That(d.Get(made, "hp"), Is.EqualTo("71"), "the copy starts from the row as shown");
                d.Set(made, "id", "900");
                d.Set(made, "name", "Cinder");
                d.Set(made, "hp", "70");        // back to the value of the copied row: no <set> needed
                d.Delete(Row(d, "pet[id=100]"));
                Assert.That(Row(d, "pet[id=100]").IsDeleted);
                Assert.Throws<ModKitException>(() => d.Set(Row(d, "pet[id=100]"), "hp", "1"));

                Plan p = f.Plan(d.Mod);
                Assert.That(p.HasErrors || p.HasConflicts, Is.False, Fake.Dump(p));
                string text = Fake.Text(p, Client, Pet);
                Assert.That(text, Does.Not.Contain("Mossy"));
                Assert.That(text, Does.Contain("<pet id=\"101\" name=\"Pebble\" hp=\"71\" speed=\"2\">"));
                Assert.That(text, Does.Contain("<pet id=\"900\" name=\"Cinder\" hp=\"70\" speed=\"2\">"));

                // a clone of the clone, then both taken back
                TableRow second = d.Clone(made);
                Assert.That(d.Get(second, "name"), Is.EqualTo("Cinder"));
                d.Delete(second);
                d.Delete(made);
                d.Restore(Row(d, "pet[id=100]"));
                d.Set(src, "hp", "70");
                Assert.That(d.Rows.Count, Is.EqualTo(4));
                Assert.That(d.Mod.Ops, Is.Empty);
            }
        }

        [Test]
        public void SavedModLoadsBackIntoTheSameGrid()
        {
            using (var f = new Fake())
            {
                var d = new TableDraft(f.Data, NewMod(), Client, Pet, null);
                d.Set(Row(d, "pet[id=102][feature=Alpha]"), "hp", "61");
                d.Set(Row(d, "pet[id=100]"), "tag", "new");     // a column the table did not have
                TableRow made = d.Clone(Row(d, "pet[id=101]"));
                d.Set(made, "id", "901");
                d.Delete(Row(d, "pet[id=102][feature=]"));
                Assert.That(d.CopyEditsTo(Server), Is.EqualTo(4));
                string dir = ModFolder.DirOf(Path.Combine(f.Dir, "mods"), d.Mod.Id);
                d.Mod.Save(dir);

                Mod loaded = Mod.Load(dir);
                Assert.That(loaded.ToXml(), Is.EqualTo(d.Mod.ToXml()));
                foreach (string archive in new[] { Client, Server })
                {
                    var e = new TableDraft(f.Data, loaded, archive, Pet, null);
                    Assert.That(e.Problems, Is.Empty);
                    Assert.That(e.OtherOps, Is.EqualTo(0));
                    Assert.That(e.Columns, Does.Contain("tag"));
                    Assert.That(e.Get(Row(e, "pet[id=102][feature=Alpha]"), "hp"), Is.EqualTo("61"));
                    Assert.That(e.Get(Row(e, "pet[id=100]"), "tag"), Is.EqualTo("new"));
                    Assert.That(Row(e, "pet[id=102][feature=]").IsDeleted);
                    TableRow added = e.Rows.Single(r => r.IsAdded);
                    Assert.That(e.Get(added, "id"), Is.EqualTo("901"));
                    Assert.That(e.Get(added, "name"), Is.EqualTo("Pebble"));
                }
                Plan p = f.Plan(loaded);
                Assert.That(p.HasErrors || p.HasConflicts, Is.False, Fake.Dump(p));
                Assert.That(p.Changes.Count, Is.EqualTo(8));
            }
        }

        [Test]
        public void OperationsWithoutACellAreKept()
        {
            using (var f = new Fake())
            {
                Mod m = f.Mod("t.other",
                    "<edit archive=\"client:Xml.m2d\" entry=\"table/pet.xml\">\n" +
                    "  <change at=\"pet[id=101]/skill[id=8]\"><set name=\"level\" value=\"3\"/></change>\n" +
                    "  <change at=\"pet[id=101]\"><set at=\"skill[id=7]\" name=\"level\" value=\"5\"/><set name=\"hp\" value=\"72\"/></change>\n" +
                    "  <change at=\"pet[id=101]\"><set name=\"speed\" value=\"3\"/></change>\n" +
                    "  <add after=\"pet[id=100]\"><xml><pet id=\"905\" name=\"Ash\" hp=\"1\"/></xml></add>\n" +
                    "  <delete at=\"pet[id=777]\"/>\n" +
                    "</edit>");
                var d = new TableDraft(f.Data, m, Client, Pet, null);
                Assert.That(d.OtherOps, Is.EqualTo(1), "the change of a nested row");
                Assert.That(d.Problems.Count, Is.EqualTo(1), "the delete of a row that is not there");
                TableRow r = Row(d, "pet[id=101]");
                Assert.That(d.Get(r, "hp"), Is.EqualTo("72"));
                Assert.That(d.Get(r, "speed"), Is.EqualTo("3"), "two changes of one row are read as one");
                TableRow ash = d.Rows.Single(x => x.IsAdded);
                Assert.That(d.Get(ash, "name"), Is.EqualTo("Ash"));
                d.Set(ash, "hp", "2");
                d.Set(r, "hp", "70");
                string xml = d.Mod.ToXml();
                Assert.That(xml, Does.Contain("<change at=\"pet[id=101]/skill[id=8]\">"));
                Assert.That(xml, Does.Contain("<set at=\"skill[id=7]\" name=\"level\" value=\"5\" />"));
                Assert.That(xml, Does.Contain("<set name=\"hp\" value=\"2\" />"));
                Assert.That(xml, Does.Not.Contain("value=\"72\""));
                Assert.That(xml, Does.Contain("<delete at=\"pet[id=777]\" />"));

                // the nested rows as a table of their own
                var skills = new TableDraft(f.Data, m, Client, Pet, "pet[id=101]");
                Assert.That(skills.Columns, Is.EqualTo(new[] { "id", "level" }));
                Assert.That(skills.Get(skills.Rows[1], "level"), Is.EqualTo("3"));
                skills.Set(skills.Rows[0], "level", "9");
                Assert.That(d.Mod.ToXml(), Does.Contain("<change at=\"pet[id=101]/skill[id=7]\">"));
            }
        }

        [Test]
        public void MissingEntryAndArchive()
        {
            using (var f = new Fake())
            {
                Assert.Throws<ModKitException>(() => new TableDraft(f.Data, NewMod(), Client, "table/nope.xml", null));
                Assert.Throws<ModKitException>(() => new TableDraft(f.Data, NewMod(), "client:Nope.m2d", Pet, null));
                Assert.Throws<ModKitException>(() => new TableDraft(f.Data, NewMod(), Client, Pet, "pet[id=1]"));
            }
        }

        [Test]
        public void ModFolderListsModsAndTakesTheOpenOneFromMemory()
        {
            using (var f = new Fake())
            {
                string mods = Path.Combine(f.Dir, "mods");
                Assert.That(ModFolder.LoadAll(mods), Is.Empty, "no folder yet");
                f.ModDir("b.second", "<edit archive=\"client:Xml.m2d\" entry=\"table/pet.xml\"><change at=\"pet[id=100]\"><set name=\"hp\" value=\"1\"/></change></edit>");
                f.ModDir("a.first", "<edit archive=\"client:Xml.m2d\" entry=\"table/pet.xml\"><change at=\"pet[id=101]\"><set name=\"hp\" value=\"2\"/></change></edit>");
                f.ModDir("_parked", "<edit archive=\"client:Xml.m2d\" entry=\"table/pet.xml\"><delete at=\"pet[id=100]\"/></edit>");
                Directory.CreateDirectory(Path.Combine(mods, "notamod"));
                Assert.That(ModFolder.LoadAll(mods).Select(m => m.Id), Is.EqualTo(new[] { "a.first", "b.second" }));

                Mod open = Mod.Load(Path.Combine(mods, "b.second"));
                var d = new TableDraft(f.Data, open, Client, Pet, null);
                d.Set(Row(d, "pet[id=100]"), "hp", "9");
                List<Mod> set = ModFolder.LoadAllWith(mods, open);
                Assert.That(set.Count, Is.EqualTo(2));
                Assert.That(set[1], Is.SameAs(open), "the unsaved state is what gets planned");

                Mod fresh = NewMod();
                Assert.That(ModFolder.LoadAllWith(mods, fresh).Count, Is.EqualTo(2), "an empty new mod is not part of the set");
                var e = new TableDraft(f.Data, fresh, Client, Pet, null);
                e.Set(Row(e, "pet[id=102][feature=]"), "hp", "3");
                Assert.That(ModFolder.LoadAllWith(mods, fresh).Select(m => m.Id), Is.EqualTo(new[] { "a.first", "b.second", "t.draft" }));
            }
        }

        [Test]
        public void SessionEditPlanApplyRevert()
        {
            using (var f = new Fake())
            {
                string mods = Path.Combine(f.Dir, "mods"), ws = Path.Combine(f.Dir, "ws");
                using (var s = new EditorSession(f.ClientDir, f.ServerDir, mods, ws))
                {
                    Assert.That(s.Archives(), Is.EqualTo(new[] { Client, Server }));
                    Assert.That(s.Entries(Client, "PET.xml", 50), Is.EqualTo(new[] { Pet }));
                    Assert.That(s.Entries(Client, "", 2).Count, Is.EqualTo(2));
                    Assert.That(s.OtherSide(Client, Pet), Is.EqualTo(Server));
                    Assert.That(s.OtherSide(Server, "pet/555.xml"), Is.Null);
                    Assert.Throws<ModKitException>(() => s.NewMod("bad id"));
                    Assert.Throws<ModKitException>(() => s.SaveMod(), "nothing to save yet");
                    Assert.Throws<ModKitException>(() => s.Apply(), "nothing to apply yet");

                    s.NewMod("t.session");
                    TableDraft d = s.OpenTable(Client, Pet, null);
                    d.Set(Row(d, "pet[id=100]"), "hp", "65");
                    Assert.Throws<ModKitException>(() => d.Set(Row(d, "pet[id=100]"), "h p", "1"));
                    Assert.Throws<ModKitException>(() => d.Set(Row(d, "pet[id=100]"), "9hp", "1"));
                    Assert.That(TableDraft.IsAttrName("hp.max-2:x_"), Is.True);
                    using (var s2 = new EditorSession(f.ClientDir, null, mods, ws))
                    {
                        s2.Adopt(s.Mod);
                        Assert.That(s2.Plan().Changes.Count, Is.EqualTo(1), "the adopted mod is planned");
                    }
                    d.CopyEditsTo(Server);
                    Plan p = s.Plan();
                    Assert.That(p.Changes.Count, Is.EqualTo(2));
                    Assert.That(EditorSession.Report(p), Does.StartWith("1 mod(s), 2 change(s), 0 error(s), 0 conflict(s), 0 warning(s)"));
                    Assert.That(EditorSession.Report(p), Does.Contain("client:Xml.m2d table/pet.xml pet[id=100] @hp: \"50\" -> \"65\""));
                    Assert.That(Directory.Exists(mods), Is.False, "a plan writes nothing");

                    ApplyResult r = s.Apply();
                    Assert.That(r.Applied, Is.True, EditorSession.Report(r));
                    Assert.That(File.Exists(Path.Combine(mods, "t.session", "mod.xml")));
                    Assert.That(EditorSession.Report(r), Does.Contain("wrote "));
                    using (var patched = new DataSet(Path.Combine(ws, "client"), Path.Combine(ws, "server")))
                        foreach (string a in new[] { Client, Server })
                            Assert.That(System.Text.Encoding.UTF8.GetString(patched.Open(a).Read(Pet)), Does.Contain("<pet id=\"100\" name=\"Mossy\" hp=\"65\" speed=\"1.5\" />"));
                    using (var orig = new DataSet(f.ClientDir, f.ServerDir))
                        Assert.That(System.Text.Encoding.UTF8.GetString(orig.Open(Client).Read(Pet)), Does.Contain("hp=\"50\""), "the original is not written");

                    // a conflicting second mod in the folder: the plan says so and apply writes nothing
                    f.ModDir("t.zz", "<edit archive=\"client:Xml.m2d\" entry=\"table/pet.xml\"><change at=\"pet[id=100]\"><set name=\"hp\" value=\"1\"/></change></edit>");
                    Assert.That(EditorSession.Report(s.Plan()), Does.Contain("conflict, "));
                    Assert.That(s.Apply().Applied, Is.False);
                    Directory.Delete(Path.Combine(mods, "t.zz"), true);

                    // the saved mod loads back by folder name, and a new mod cannot take its folder
                    s.NewMod("t.session");
                    d = s.OpenTable(Client, Pet, null);
                    d.Set(Row(d, "pet[id=101]"), "hp", "1");
                    Assert.Throws<ModKitException>(() => s.SaveMod());
                    s.LoadMod("t.session");
                    d = s.OpenTable(Client, Pet, null);
                    Assert.That(d.Get(Row(d, "pet[id=100]"), "hp"), Is.EqualTo("65"));

                    ApplyResult rv = s.Revert();
                    Assert.That(rv.Removed.Count, Is.GreaterThan(0));
                    Assert.That(EditorSession.Report(rv), Does.Contain("removed "));
                    Assert.That(File.Exists(Path.Combine(ws, "client", "Xml.m2d")), Is.False);
                    Assert.Throws<ModKitException>(() => s.Revert(), "nothing applied any more");
                }
                Assert.Throws<ModKitException>(() => new EditorSession(null, "", mods, ws));
            }
        }
    }
}
