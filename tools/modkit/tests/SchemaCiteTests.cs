// Tests of "modkit schema cite": where a source tree reads an attribute, and the note line drafted from it.
// The source tree and every name in it are made up.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class SchemaCiteTests
    {
        const string C = "client:Xml.m2d", T = "table/pet.xml";

        static string Tree(Fake f)
        {
            string src = Path.Combine(f.Dir, "src");
            Directory.CreateDirectory(Path.Combine(src, "game", "pets"));
            Directory.CreateDirectory(Path.Combine(src, "other"));
            File.WriteAllText(Path.Combine(src, "game", "pets", "petloader.cpp"),
                "// reads table/pet.xml\nbool LoadPets(Node* n)\n{\n    pet.hp = n->Int(\"hp\");\n    pet.glow = n->Int(\"glow\");\n    return true;\n}\n");
            File.WriteAllText(Path.Combine(src, "other", "aaa.cpp"), "void Lamp(Node* n) { lamp.glow = n->Int(\"glow\"); }\n");
            File.WriteAllText(Path.Combine(src, "other", "notes.txt"), "pet.xml \"glow\"\n");
            File.WriteAllText(Path.Combine(src, "game", "npcs.h"), "// npc/ files\ninline int Shade(Node* n) { return n->Int(\"shade\"); }\n");
            return src;
        }

        [Test]
        public void TheLinesThatReadAnAttribute()
        {
            using (var f = new Fake())
            {
                string src = Tree(f);
                List<CiteHit> hits = SchemaCite.Find(src, T, "glow", 40);
                // the file that names the table comes first, although its path sorts last; no .txt file
                Assert.That(hits.Select(h => h.Where + " " + h.NamesTable), Is.EqualTo(new[] { "game/pets/petloader.cpp:5 True", "other/aaa.cpp:1 False" }));
                Assert.That(hits[0].Text, Is.EqualTo("pet.glow = n->Int(\"glow\");"));
                Assert.That(SchemaCite.Find(src, T, "glow", 1).Count, Is.EqualTo(1));
                // a name inside another word is no hit
                Assert.That(SchemaCite.Find(src, T, "glo", 40), Is.Empty);
                Assert.That(SchemaCite.Find(src, T, "nothing", 40), Is.Empty);

                // a table of numbered files is named by its folder
                Assert.That(SchemaCite.TableWord("npc/#.xml"), Is.EqualTo("npc/"));
                Assert.That(SchemaCite.TableWord("item/#/#/#.xml"), Is.EqualTo("item/"));
                Assert.That(SchemaCite.TableWord(T), Is.EqualTo("pet.xml"));
                Assert.That(SchemaCite.TableWord("#.xml"), Is.EqualTo(""));
                hits = SchemaCite.Find(src, "npc/#.xml", "shade", 40);
                Assert.That(hits.Select(h => h.Where + " " + h.NamesTable), Is.EqualTo(new[] { "game/npcs.h:2 True" }));

                Assert.That(() => SchemaCite.Find(Path.Combine(f.Dir, "none"), T, "glow", 40), Throws.TypeOf<ModKitException>());
            }
        }

        [Test]
        public void TheDraftedNoteTurnsTheWarningIntoANote()
        {
            using (var f = new Fake())
            {
                CiteHit hit = SchemaCite.Find(Tree(f), T, "glow", 40)[0];
                string line = SchemaCite.NoteLine(C, T, "ms2/pet", "glow", hit);
                Assert.That(line, Is.EqualTo("<attr archive=\"client:Xml.m2d\" table=\"table/pet.xml\" element=\"ms2/pet\" name=\"glow\" source=\"client\" ref=\"game/pets/petloader.cpp:5\"/>"));

                var set = new SchemaSet();
                set.Build(f.Data, C, null);
                Mod m = f.Mod("s", "<edit archive=\"" + C + "\" entry=\"" + T + "\"><change at=\"pet[id=100]\"><set name=\"glow\" value=\"1\"/></change></edit>");
                Plan p = Planner.Build(f.Data, new[] { m }, new PlanOptions { Schemas = set });
                Assert.That(Fake.Dump(p), Does.Contain("attribute glow is never set on ms2/pet in the shipped table/pet.xml"));

                File.WriteAllText(Path.Combine(f.Dir, "notes.xml"), "<notes>" + line + "</notes>");
                set.LoadNotes(Path.Combine(f.Dir, "notes.xml"));
                p = Planner.Build(f.Data, new[] { m }, new PlanOptions { Schemas = set });
                string dump = Fake.Dump(p);
                Assert.That(dump, Does.Not.Contain("attribute glow is never set"), dump);
                Assert.That(p.Notes.Any(n => n.Text.Contains("the client loader reads it (game/pets/petloader.cpp:5)")), dump);
            }
        }

        [Test]
        public void ANoteSaysWhatTheClientDoesWithTheValue()
        {
            using (var f = new Fake())
            {
                var set = new SchemaSet();
                set.Build(f.Data, C, null);
                File.WriteAllText(Path.Combine(f.Dir, "notes.xml"), "<notes><attr archive=\"" + C + "\" table=\"" + T + "\" element=\"ms2/pet\" name=\"glow\" source=\"client\" ref=\"game/pets/petloader.cpp:5\" use=\"int, how bright the pet is\"/></notes>");
                set.LoadNotes(Path.Combine(f.Dir, "notes.xml"));
                Mod m = f.Mod("s", "<edit archive=\"" + C + "\" entry=\"" + T + "\"><change at=\"pet[id=100]\"><set name=\"glow\" value=\"1\"/></change></edit>");
                Plan p = Planner.Build(f.Data, new[] { m }, new PlanOptions { Schemas = set });
                Assert.That(p.Notes.Any(n => n.Text.EndsWith("the client loader reads it (game/pets/petloader.cpp:5): int, how bright the pet is")), Fake.Dump(p));
            }
        }

        // The notes file of the repo: every line loads, cites a file and a line, says what the value is used for, and is there once.
        [Test]
        public void TheShippedNotesAreWellFormed()
        {
            string dir = TestContext.CurrentContext.TestDirectory, path = null;
            for (int i = 0; i < 8 && dir != null && path == null; i++, dir = Path.GetDirectoryName(dir))
                if (File.Exists(Path.Combine(dir, "schema-notes.xml"))) path = Path.Combine(dir, "schema-notes.xml");
            Assert.That(path, Is.Not.Null, "schema-notes.xml not found above the test folder");
            var set = new SchemaSet();
            set.LoadNotes(path);
            var seen = new HashSet<string>();
            foreach (SchemaNote n in set.Notes)
            {
                string id = n.Archive + " " + n.Table + " " + n.Element + " @" + n.Attr;
                Assert.That(seen.Add(id + " " + n.Source), id + " is there twice");
                Assert.That(n.Element, Does.StartWith("ms2/"), id);
                if (n.Source != "client") continue;
                Assert.That(n.Archive, Is.Not.EqualTo("*"), id);
                Assert.That(n.Ref, Does.Match("^[a-z0-9_/]+\\.(cpp|h|inl):[0-9]+$"), id);
                Assert.That(n.Use, Is.Not.Empty, id);
                Assert.That(n.Use.Any(char.IsDigit), Is.False, id + ": a use text holds no number, so no game value");
            }
        }
    }
}
