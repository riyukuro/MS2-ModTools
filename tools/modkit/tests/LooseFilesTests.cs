// Tests of the loose-file target (LooseTarget), the mod list (ModList) and their use by EditorSession.
// Made-up data only.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class LooseFilesTests
    {
        const string C = "client:Xml.m2d", S = "server:Xml.m2d", Pet = "table/pet.xml";

        static string Hp(string archive, string id, string hp)
        {
            return "<edit archive=\"" + archive + "\" entry=\"table/pet.xml\"><change at=\"pet[id=" + id + "]\"><set name=\"hp\" value=\"" + hp + "\"/></change></edit>";
        }

        // a made-up game folder: Data with one file of the game's own in it
        static string Game(Fake f)
        {
            string g = Path.Combine(f.Dir, "game");
            Directory.CreateDirectory(Path.Combine(Path.Combine(g, "Data"), "Resource"));
            File.WriteAllText(Path.Combine(Path.Combine(g, "Data"), "keep.txt"), "the game's own file");
            return g;
        }

        static string P(string root, params string[] parts)
        {
            foreach (string p in parts) root = Path.Combine(root, p);
            return root;
        }

        static List<string> Files(string dir)
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(p => p.Substring(dir.Length + 1).Replace('\\', '/')).OrderBy(p => p, System.StringComparer.Ordinal).ToList();
        }

        [Test]
        public void PathsUnderTheGameFolder()
        {
            Assert.That(LooseTarget.RelPathOf("client:Xml.m2d", "table/pet.xml"), Is.EqualTo("Data/Xml/table/pet.xml"));
            Assert.That(LooseTarget.RelPathOf("Client:Resource\\Exported.m2d", "xblock\\a.xblock"), Is.EqualTo("Data/Resource/Exported/xblock/a.xblock"));
            Assert.Throws<ModKitException>(() => LooseTarget.RelPathOf("server:Xml.m2d", "table/pet.xml"));
            Assert.Throws<ModKitException>(() => LooseTarget.RelPathOf(C, "../outside.xml"));
            Assert.Throws<ModKitException>(() => LooseTarget.RelPathOf(C, "table/"));
            Assert.Throws<ModKitException>(() => LooseTarget.RelPathOf(C, "c:/x.xml"));
            using (var f = new Fake())
                Assert.Throws<ModKitException>(() => new LooseTarget(f.Dir), "no Data folder");
        }

        [Test]
        public void ApplyWritesPlainFilesAndRevertRemovesExactlyThose()
        {
            using (var f = new Fake())
            {
                string g = Game(f);
                var before = Files(g);
                var lt = new LooseTarget(g);
                Mod a = f.Mod("a", Hp(C, "100", "65") + Hp(S, "100", "65"));
                Mod b = f.Mod("b", "<entry archive=\"" + C + "\" name=\"pet/900.xml\" copy=\"pet/100.xml\"/>");

                ApplyResult r = lt.Apply(f.Data, new[] { a, b }, null);
                Assert.That(r.Applied, Is.True, EditorSession.Report(r));
                string pet = P(g, "Data", "Xml", "table", "pet.xml"), p900 = P(g, "Data", "Xml", "pet", "900.xml");
                Assert.That(r.Written, Is.EquivalentTo(new[] { pet, p900 }), "the server's entry is not a loose file");
                // the file is the plain text of the merged entry, byte for byte what the plan holds
                Assert.That(File.ReadAllBytes(pet), Is.EqualTo(r.Plan.Find(C).Put.Single(kv => kv.Key == Pet).Value));
                Assert.That(File.ReadAllText(pet), Does.Contain("<pet id=\"100\" name=\"Mossy\" hp=\"65\" speed=\"1.5\" />"));
                Assert.That(File.ReadAllBytes(p900), Is.EqualTo(f.Data.Open(C).Read("pet/100.xml")));
                Assert.That(Encoding.UTF8.GetString(f.Data.Open(C).Read(Pet)), Does.Contain("hp=\"50\""), "the archive is not written");

                LooseState st = lt.ReadState();
                Assert.That(st.Files.Select(x => x.Path), Is.EquivalentTo(new[] { "Data/Xml/table/pet.xml", "Data/Xml/pet/900.xml" }));
                Assert.That(st.Dirs, Is.EquivalentTo(new[] { "Data/Xml", "Data/Xml/table", "Data/Xml/pet" }));
                Assert.That(st.Mods.Select(m => m.Id), Is.EqualTo(new[] { "a", "b" }));

                // a second apply without b: its file goes, a's is rewritten, nothing is a conflict
                r = lt.Apply(f.Data, new[] { f.Mod("a", Hp(C, "100", "66")) }, null);
                Assert.That(r.Applied, Is.True, EditorSession.Report(r));
                Assert.That(File.ReadAllText(pet), Does.Contain("hp=\"66\""));
                Assert.That(File.Exists(p900), Is.False);
                Assert.That(r.Removed, Does.Contain(p900));
                Assert.That(Directory.Exists(P(g, "Data", "Xml", "pet")), Is.False, "the emptied folder the tool made is gone");

                // a file of someone else next to ours survives the revert, and so does its folder
                string other = P(g, "Data", "Xml", "table", "other.xml");
                File.WriteAllText(other, "<ms2/>");
                r = lt.Revert();
                Assert.That(r.Removed, Is.EqualTo(new[] { pet }));
                Assert.That(File.Exists(other));
                Assert.That(File.Exists(Path.Combine(g, LooseTarget.StateFile)), Is.False);
                File.Delete(other);
                Directory.Delete(P(g, "Data", "Xml", "table"));
                Directory.Delete(P(g, "Data", "Xml"));
                Assert.That(Files(g), Is.EqualTo(before));
                Assert.That(Directory.Exists(P(g, "Data", "Resource")), "a folder the game had stays");
                Assert.Throws<ModKitException>(() => lt.Revert(), "no record any more");
            }
        }

        [Test]
        public void RevertLeavesTheGameFolderAsItWas()
        {
            using (var f = new Fake())
            {
                string g = Game(f);
                var before = Files(g);
                var dirsBefore = Directory.GetDirectories(g, "*", SearchOption.AllDirectories).OrderBy(d => d).ToList();
                var lt = new LooseTarget(g);
                Assert.That(lt.Apply(f.Data, new[] { f.Mod("a", Hp(C, "100", "65")) }, null).Applied);
                Assert.That(Files(g).Count, Is.EqualTo(before.Count + 2), "one table and the record");
                lt.Revert();
                Assert.That(Files(g), Is.EqualTo(before));
                Assert.That(Directory.GetDirectories(g, "*", SearchOption.AllDirectories).OrderBy(d => d).ToList(), Is.EqualTo(dirsBefore));
            }
        }

        [Test]
        public void AFileTheToolDidNotWriteIsAConflictAndIsNotOverwritten()
        {
            using (var f = new Fake())
            {
                string g = Game(f);
                var lt = new LooseTarget(g);
                string pet = P(g, "Data", "Xml", "table", "pet.xml");
                Directory.CreateDirectory(Path.GetDirectoryName(pet));
                File.WriteAllText(pet, "someone else's table");
                Mod a = f.Mod("a", Hp(C, "100", "65"));

                Plan p = f.Plan(a);
                Assert.That(lt.Check(p), Is.EqualTo(1));
                Assert.That(p.Conflicts.Single().ToString(), Does.Contain("was not written by this tool"));
                foreach (bool allow in new[] { false, true })
                {
                    ApplyResult r = lt.Apply(f.Data, new[] { a }, new ApplyOptions { AllowConflicts = allow });
                    Assert.That(r.Applied, Is.False);
                    Assert.That(r.Reason, Does.Contain("did not write"));
                    Assert.That(EditorSession.Report(r), Does.Contain("conflict: "));
                }
                Assert.That(File.ReadAllText(pet), Is.EqualTo("someone else's table"));
                Assert.That(lt.ReadState(), Is.Null, "a refused apply leaves no record");

                // with the file out of the way the apply goes through; a hand edit of the written file then
                // makes it someone else's again: not overwritten by an apply, not deleted by a revert
                File.Delete(pet);
                Assert.That(lt.Apply(f.Data, new[] { a }, null).Applied);
                File.AppendAllText(pet, "<!-- edited by hand -->");
                ApplyResult r2 = lt.Apply(f.Data, new[] { f.Mod("a", Hp(C, "100", "66")) }, null);
                Assert.That(r2.Applied, Is.False);
                Assert.That(r2.Plan.Conflicts.Single().ToString(), Does.Contain("was changed after this tool wrote it"));
                ApplyResult rv = lt.Revert();
                Assert.That(rv.Removed, Is.Empty);
                Assert.That(rv.Left, Is.EqualTo(new[] { pet }));
                Assert.That(File.ReadAllText(pet), Does.EndWith("<!-- edited by hand -->"));
                Assert.That(EditorSession.Report(rv), Does.Contain("left alone"));
            }
        }

        [Test]
        public void DeletingAnEntryIsAnErrorForLooseFiles()
        {
            using (var f = new Fake())
            {
                string g = Game(f);
                var lt = new LooseTarget(g);
                ApplyResult r = lt.Apply(f.Data, new[] { f.Mod("a", "<entry archive=\"" + C + "\" name=\"table/readme.txt\" delete=\"true\"/>") }, null);
                Assert.That(r.Applied, Is.False);
                Assert.That(r.Plan.Errors.Single().ToString(), Does.Contain("cannot delete an entry"));
                Assert.That(Directory.Exists(P(g, "Data", "Xml")), Is.False);
                // deleting on the server side is not the loose target's business
                r = lt.Apply(f.Data, new[] { f.Mod("b", "<entry archive=\"" + S + "\" name=\"pet/555.xml\" delete=\"true\"/>") }, null);
                Assert.That(r.Applied, Is.True);
                Assert.That(r.Written, Is.Empty);
                Assert.That(lt.ReadState(), Is.Null, "nothing written, no record");
            }
        }

        [Test]
        public void ModListKeepsOrderAndEnabledStateInTheModsFolder()
        {
            using (var f = new Fake())
            {
                string mods = Path.Combine(f.Dir, "mods");
                Assert.That(ModList.Load(mods).Items, Is.Empty, "no folder yet");
                Assert.That(ModList.Load(null).Items, Is.Empty);
                f.ModDir("b.second", Hp(C, "100", "1"));
                f.ModDir("a.first", Hp(C, "100", "2"));
                f.ModDir("c.third", Hp(C, "101", "3"));
                f.ModDir("_parked", Hp(C, "100", "4"));
                Directory.CreateDirectory(Path.Combine(mods, "notamod"));

                ModList l = ModList.Load(mods);
                Assert.That(l.Items.Select(i => i.Id), Is.EqualTo(new[] { "a.first", "b.second", "c.third" }));
                Assert.That(l.Items.All(i => i.Enabled), "a mod the file does not name is enabled");
                Assert.That(File.Exists(Path.Combine(mods, ModList.FileName)), Is.False, "reading writes nothing");

                Assert.That(l.Move(0, +1), Is.EqualTo(1));
                Assert.That(l.Move(0, -1), Is.EqualTo(0), "the first cannot go up");
                Assert.That(l.Move(2, +1), Is.EqualTo(2), "the last cannot go down");
                l.FindId("c.third").Enabled = false;
                l.Save();
                Assert.That(File.Exists(Path.Combine(Path.Combine(mods, "c.third"), ModList.FileName)), Is.False, "the state is not inside a mod");

                l = ModList.Load(mods);
                Assert.That(l.Items.Select(i => i.Id + ":" + i.Enabled), Is.EqualTo(new[] { "b.second:True", "a.first:True", "c.third:False" }));
                Assert.That(File.ReadAllText(Path.Combine(mods, ModList.FileName)), Does.Contain("<mods version=\"2\">"));
                // a plan takes the mods in rising priority: the first of the list comes last
                Assert.That(l.LoadEnabled(null).Select(m => m.Id), Is.EqualTo(new[] { "a.first", "b.second" }));
                // the order decides who wins: b.second is the top one now
                Plan p = f.Plan(l.LoadEnabled(null).ToArray());
                Assert.That(Fake.Text(p, C, Pet), Does.Contain("<pet id=\"100\" name=\"Mossy\" hp=\"1\""));
                Assert.That(p.Conflicts.Single().WinnerId, Is.EqualTo("b.second"));
                Assert.That(EditorSession.Report(p), Does.Contain("priority, the highest first: b.second > a.first").And.Contain("; mod b.second wins (it has the higher priority)"));
                // a drag: the third to the top, and back
                Assert.That(l.MoveTo(2, 0), Is.EqualTo(0));
                Assert.That(l.Items.Select(i => i.Id), Is.EqualTo(new[] { "c.third", "b.second", "a.first" }));
                Assert.That(l.MoveTo(0, 2), Is.EqualTo(2));
                Assert.That(l.MoveTo(0, 3), Is.EqualTo(0), "no such place");
                Assert.That(l.Items.Select(i => i.Id), Is.EqualTo(new[] { "b.second", "a.first", "c.third" }));

                // the mod being edited takes part although it is switched off, at its place, from memory
                Mod open = Mod.Load(Path.Combine(mods, "c.third"));
                List<Mod> set = l.LoadEnabled(open);
                Assert.That(set.Select(m => m.Id), Is.EqualTo(new[] { "c.third", "a.first", "b.second" }));
                Assert.That(set[0], Is.SameAs(open));
                // one that is not in the folder yet has the highest priority; an empty one is left out
                Mod fresh = new Mod { Id = "t.new", Name = "", Version = "1" };
                Assert.That(l.LoadEnabled(fresh).Count, Is.EqualTo(2));
                var d = new TableDraft(f.Data, fresh, C, Pet, null);
                d.Set(d.Rows[0], "speed", "9");
                Assert.That(l.LoadEnabled(fresh).Select(m => m.Id), Is.EqualTo(new[] { "a.first", "b.second", "t.new" }));
                // a list written before the priority model had the winner last: it is read the other way round
                string listFile = Path.Combine(mods, ModList.FileName);
                File.WriteAllText(listFile, "<mods version=\"1\"><mod folder=\"c.third\" enabled=\"false\"/><mod folder=\"a.first\"/><mod folder=\"b.second\"/></mods>");
                Assert.That(ModList.Load(mods).Items.Select(i => i.Id + ":" + i.Enabled), Is.EqualTo(new[] { "b.second:True", "a.first:True", "c.third:False" }));
                l.Save();

                // a new folder goes to the end, a folder that is gone leaves the list, a broken mod is shown and refused when enabled
                f.ModDir("0.new", Hp(C, "102", "5"));
                Directory.Delete(Path.Combine(mods, "a.first"), true);
                File.WriteAllText(Path.Combine(Path.Combine(mods, "b.second"), "mod.xml"), "<mod");
                l = ModList.Load(mods);
                Assert.That(l.Items.Select(i => i.Folder), Is.EqualTo(new[] { "b.second", "c.third", "0.new" }));
                Assert.That(l.Items[0].Error, Is.Not.Null);
                Assert.Throws<ModKitException>(() => l.LoadEnabled(null));
                l.Items[0].Enabled = false;
                Assert.That(l.LoadEnabled(null).Select(m => m.Id), Is.EqualTo(new[] { "0.new" }));
            }
        }

        [Test]
        public void SessionAppliesEnabledModsAsLooseFilesAndTheServerIntoTheWorkspace()
        {
            using (var f = new Fake())
            {
                string g = Game(f), mods = Path.Combine(f.Dir, "mods"), ws = Path.Combine(f.Dir, "ws");
                string pet = P(g, "Data", "Xml", "table", "pet.xml");
                f.ModDir("m.off", Hp(C, "101", "1"));
                f.ModDir("m.on", Hp(C, "101", "3").Replace("\"hp\"", "\"speed\""));
                using (var s = new EditorSession(f.ClientDir, f.ServerDir, mods, ws))
                {
                    ModList l = s.ModList();
                    l.FindId("m.off").Enabled = false;
                    l.Save();

                    s.Target = ApplyTarget.LooseFiles;
                    Assert.Throws<ModKitException>(() => s.Plan(), "no game folder set");
                    s.GameDir = g;
                    s.NewMod("t.edit");
                    TableDraft d = s.OpenTable(C, Pet, null);
                    d.Set(d.Rows[0], "hp", "65");
                    d.CopyEditsTo(S);
                    Plan p = s.Plan();
                    Assert.That(p.Mods.Select(m => m.Id), Is.EqualTo(new[] { "m.on", "t.edit" }));
                    Assert.That(p.HasConflicts, Is.False);

                    ApplyResult r = s.Apply();
                    Assert.That(r.Applied, Is.True, EditorSession.Report(r));
                    Assert.That(r.Written, Is.EqualTo(new[] { pet }));
                    string text = File.ReadAllText(pet);
                    Assert.That(text, Does.Contain("hp=\"65\""));
                    Assert.That(text, Does.Contain("<pet id=\"101\" name=\"Pebble\" hp=\"70\" speed=\"3\""), "the enabled mod is applied, the switched-off one is not");
                    Assert.That(File.Exists(P(ws, "server", "Xml.m2d")), "the server's archive goes to the workspace");
                    Assert.That(File.Exists(P(ws, "client", "Xml.m2d")), Is.False, "the client's does not");
                    Assert.That(EditorSession.Report(r), Does.Contain("wrote " + pet));

                    // the plan names a file that is in the way
                    string other = P(g, "Data", "Xml", "pet", "100.xml");
                    Directory.CreateDirectory(Path.GetDirectoryName(other));
                    File.WriteAllText(other, "not ours");
                    f.ModDir("m.zz", "<entry archive=\"" + C + "\" name=\"pet/100.xml\" copy=\"pet/101.xml\"/>");
                    Assert.That(EditorSession.Report(s.Plan()), Does.Contain("conflict: client:Xml.m2d pet/100.xml: the file " + other));
                    Assert.That(s.Apply().Applied, Is.False);
                    Assert.That(File.ReadAllText(other), Is.EqualTo("not ours"));
                    Directory.Delete(Path.Combine(mods, "m.zz"), true);

                    // switching the target moves the client's part: copies in the workspace, loose files gone
                    s.Target = ApplyTarget.Workspace;
                    r = s.Apply();
                    Assert.That(r.Applied, Is.True, EditorSession.Report(r));
                    Assert.That(File.Exists(pet), Is.False);
                    Assert.That(r.Removed, Does.Contain(pet));
                    Assert.That(File.Exists(P(ws, "client", "Xml.m2d")));
                    // and back
                    s.Target = ApplyTarget.LooseFiles;
                    r = s.Apply();
                    Assert.That(File.Exists(pet));
                    Assert.That(File.Exists(P(ws, "client", "Xml.m2d")), Is.False);
                    Assert.That(File.Exists(P(ws, "server", "Xml.m2d")));

                    ApplyResult rv = s.Revert();
                    Assert.That(rv.Removed, Does.Contain(pet));
                    Assert.That(File.Exists(P(ws, "server", "Xml.m2d")), Is.False);
                    Assert.That(File.ReadAllText(other), Is.EqualTo("not ours"));
                    Assert.That(File.Exists(P(g, "Data", "keep.txt")));
                    Assert.Throws<ModKitException>(() => s.Revert(), "nothing applied any more");
                }
            }
        }
    }
}
