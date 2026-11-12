using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    // Edits of trigger scripts (TriggerEditor.cs): whole-file entries of the mod, Plan/Apply, the lint gate.
    [TestFixture]
    public class TriggerEditorTests
    {
        const string S = EntityEditor.ServerXml;
        const string Door = "trigger/fake_meadow/door.xml";

        static EditorSession Session(string dir)
        {
            return new EditorSession(Path.Combine(dir, "kms"), Path.Combine(dir, "gms"), Path.Combine(dir, "server"), Path.Combine(dir, "mods"), Path.Combine(dir, "ws"));
        }

        // the fake scripts have CRLF line ends (FakeIdTables); the edits keep them, the asserts read LF
        static string Text(TriggerScript t) { return Encoding.UTF8.GetString(t.Bytes).Replace("\r\n", "\n"); }

        static string Errors(TriggerScript t)
        {
            return string.Join("\n", TriggerLint.Shipped.Lint(t.Entry, t.Bytes, null).Where(x => x.IsError).Select(x => x.ToString()).ToArray());
        }

        [Test]
        public void AnAttributeChange_IsAWholeFileEntryOfTheMod_ByteExactElsewhere()
        {
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                Mod m = s.NewMod("t.trig");
                var ed = new TriggerEditor(s.Data, m);
                TriggerScript t = ed.Current(S, Door);
                Assert.That(ed.IsModded(S, Door), Is.False);
                TriggerNode cond = t.State("Wait").Children[1];
                t = ed.SetAttr(t, cond, "arg1", "102");
                Assert.That(Text(t), Is.EqualTo(FakeIdTables.Door.Replace("arg1=\"101\"", "arg1=\"102\"")));
                EntryOp op = ed.OpOf(S, Door);
                Assert.That(op, Is.Not.Null);
                Assert.That(op.File, Is.EqualTo("files/server/" + Door));
                Assert.That(m.Files[op.File], Is.EqualTo(t.Bytes));
                Assert.That(m.ToXml(), Does.Contain("<entry archive=\"server:Xml.m2d\" name=\"" + Door + "\" file=\"files/server/" + Door + "\" />"));
                Assert.That(ed.Current(S, Door).Bytes, Is.EqualTo(t.Bytes), "the next edit starts from the mod's text");

                // a new attribute, then its removal
                t = ed.SetAttr(t, t.State("Wait"), "note", "x");
                Assert.That(Text(t), Does.Contain("<state name=\"Wait\" note=\"x\">"));
                t = ed.SetAttr(t, t.State("Wait"), "note", null);
                // edited back to the shipped bytes: the script leaves the mod
                t = ed.SetAttr(t, t.State("Wait").Children[1], "arg1", "101");
                Assert.That(Text(t), Is.EqualTo(FakeIdTables.Door));
                Assert.That(ed.IsModded(S, Door), Is.False);
                Assert.That(m.Files, Is.Empty);
                Assert.That(m.IsEmpty, Is.True);
            }
        }

        [Test]
        public void AddAndRemove_ActionsConditionsStates()
        {
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                var ed = new TriggerEditor(s.Data, s.NewMod("t.trig"));
                TriggerScript t = ed.Current(S, Door);

                // an action at the end of the on enter block, with the file's indent
                t = ed.Add(t, t.State("Wait").Children[0], TriggerKind.Action, "set_timer", null);
                Assert.That(Text(t), Does.Contain("arg2=\"0\" />\n            <action name=\"set_timer\" />\n        </onEnter>"));
                t = ed.SetAttr(t, t.State("Wait").Children[0].Children[1], "arg1", "7");

                // a state after the last one, with an empty on enter
                t = ed.Add(t, t.Root, TriggerKind.State, "Closed", null);
                Assert.That(Text(t), Does.EndWith("    </state>\n    <state name=\"Closed\">\n        <onEnter />\n    </state>\n</ms2>\n"));
                Assert.Throws<ModKitException>(() => ed.Add(t, t.Root, TriggerKind.State, "Closed", null), "two states of one name");

                // a condition with its transition goes before the on exit block
                t = ed.Add(t, t.State("Open"), TriggerKind.Condition, "wait_tick", "Closed");
                Assert.That(Text(t), Does.Contain("        </condition>\n        <condition name=\"wait_tick\">\n            <transition state=\"Closed\" />\n        </condition>\n        <onExit>"));
                Assert.That(t.State("Open").GoesTo(), Is.EqualTo(new[] { "Wait", "Closed" }));
                // an action in the new state's on enter block (a self-closed element opens)
                t = ed.Add(t, t.State("Closed").Children[0], TriggerKind.Action, "set_mesh", null);
                Assert.That(Text(t), Does.Contain("<state name=\"Closed\">\n        <onEnter>\n            <action name=\"set_mesh\" />\n        </onEnter>\n    </state>"));

                // what may go where
                Assert.That(TriggerEditor.CanHold(t.Root, TriggerKind.Action), Is.False);
                Assert.That(TriggerEditor.CanHold(t.State("Open"), TriggerKind.OnExit), Is.False, "Open has one");
                Assert.That(TriggerEditor.CanHold(t.State("Closed"), TriggerKind.OnExit), Is.True);
                Assert.Throws<ModKitException>(() => ed.Add(t, t.Root, TriggerKind.Action, "set_mesh", null));
                Assert.Throws<ModKitException>(() => ed.Add(t, t.State("Open"), TriggerKind.Condition, "", null), "no name");

                // removal takes the element and its line
                TriggerNode exit = t.State("Open").Children[3];
                Assert.That(exit.Kind, Is.EqualTo(TriggerKind.OnExit));
                t = ed.Remove(t, exit);
                Assert.That(Text(t), Does.Not.Contain("onExit"));
                Assert.That(Text(t), Does.Not.Contain("reset_timer"));
                t = ed.Remove(t, t.State("Closed"));
                Assert.That(t.States.Select(x => x.Name), Is.EqualTo(new[] { "Wait", "Open" }));
                Assert.That(Text(t), Does.Contain("    </state>\n</ms2>\n"));

                // a literal element after an anchor
                TriggerNode enter = t.State("Open").Children[0];
                t = ed.Insert(t, enter, enter.Children[0], "<action name=\"set_mesh\" arg1=\"9\" arg2=\"1\" />");
                Assert.That(t.State("Open").Children[0].Children[1].Label, Is.EqualTo("set_mesh arg1=\"9\" arg2=\"1\""));
                Assert.Throws<ModKitException>(() => ed.Insert(t, t.Root, null, "<state name=\"x\">"), "not well-formed");
                Assert.That(Errors(t), Does.Contain("[bad-transition] transition to state 'Closed'"), "the condition still goes to the removed state");
                t = ed.Remove(t, t.State("Open").Children[2]);
                Assert.That(t.State("Open").GoesTo(), Is.EqualTo(new[] { "Wait" }));
                Assert.That(Errors(t), Is.Empty);
            }
        }

        [Test]
        public void ANewScript_AppliesAndSavesBesideTheManifest()
        {
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                Mod m = s.NewMod("t.trig");
                var ed = new TriggerEditor(s.Data, m);
                Assert.Throws<ModKitException>(() => ed.NewScript(S, "fake_meadow", "door", null), "there already");
                Assert.Throws<ModKitException>(() => ed.NewScript(S, "fake_meadow", "a/b", null));
                TriggerScript t = ed.NewScript(S, "Fake_Meadow", "bell", "Ring");
                Assert.That(t.Entry, Is.EqualTo("trigger/fake_meadow/bell.xml"));
                Assert.That(t.States.Select(x => x.Name), Is.EqualTo(new[] { "Ring" }));
                Assert.That(ed.Added(S, "fake_meadow"), Is.EqualTo(new[] { "trigger/fake_meadow/bell.xml" }));
                Assert.That(ed.Added(S, "fake_far_shore"), Is.Empty);
                // a mod written by hand may name the archive in another case: the planner and OpOf ignore it, so does the list
                EntryOp bell = m.Ops.OfType<EntryOp>().Single(x => x.Name == "trigger/fake_meadow/bell.xml");
                bell.Archive = S.ToUpperInvariant();
                Assert.That(ed.Added(S, "fake_meadow"), Is.EqualTo(new[] { "trigger/fake_meadow/bell.xml" }), "archive in another case");
                bell.Archive = S;
                t = ed.Add(t, t.State("Ring").Children[0], TriggerKind.Action, "set_mesh", null);
                t = ed.SetAttr(t, t.State("Ring").Children[0].Children[0], "arg1", "3001");
                Assert.That(Errors(t), Is.Empty);

                // plan and apply from memory (Apply saves the mod: the file goes beside mod.xml)
                Plan p = s.Plan();
                Assert.That(p.HasErrors, Is.False, EditorSession.Report(p));
                Assert.That(p.Changes.Any(c => c.Kind == ChangeKind.AddEntry && c.Entry == "trigger/fake_meadow/bell.xml"));
                ApplyResult r = s.Apply();
                Assert.That(r.Applied, Is.True, EditorSession.Report(r));
                string saved = Path.Combine(f.Dir, "mods", "t.trig", "files", "server", "trigger", "fake_meadow", "bell.xml");
                Assert.That(File.ReadAllBytes(saved), Is.EqualTo(t.Bytes));
                using (M2dArchive a = M2dArchive.Open(Path.Combine(f.Dir, "ws", "server", "Xml.m2d")))
                    Assert.That(a.Read(a.Find("trigger/fake_meadow/bell.xml")), Is.EqualTo(t.Bytes));

                // loaded again, the mod reads the file from its folder, and edits it from there
                Mod back = Mod.Load(Path.Combine(f.Dir, "mods", "t.trig"));
                Assert.That(back.Files, Is.Empty);
                var ed2 = new TriggerEditor(s.Data, back);
                TriggerScript t2 = ed2.Current(S, "trigger/fake_meadow/bell.xml");
                Assert.That(t2.Bytes, Is.EqualTo(t.Bytes));
                t2 = ed2.SetAttr(t2, t2.State("Ring"), "name", "Rang");
                Assert.That(back.Files.Count, Is.EqualTo(1), "the edit is pending until the mod is saved");
                Assert.That(File.ReadAllBytes(saved), Is.EqualTo(t.Bytes));
                // revert of an added script takes it out
                ed2.Revert(S, "trigger/fake_meadow/bell.xml");
                Assert.That(ed2.Current(S, "trigger/fake_meadow/bell.xml"), Is.Null);
                Assert.That(back.IsEmpty, Is.True);
            }
        }

        [Test]
        public void ALintError_BlocksApply_UnlessTheModSaysWarn()
        {
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                Mod m = s.NewMod("t.trig");
                var ed = new TriggerEditor(s.Data, m);
                TriggerScript t = ed.Current(S, Door);
                t = ed.Add(t, t.State("Wait").Children[1], TriggerKind.Transition, "Nowhere", null);
                Assert.That(Errors(t), Is.Not.Empty);

                Plan p = s.Plan();
                Assert.That(p.HasErrors, Is.True);
                Assert.That(EditorSession.Report(p), Does.Contain("trigger lint: line 9: [bad-transition] transition to state 'Nowhere'"));
                ApplyResult r = s.Apply();
                Assert.That(r.Applied, Is.False);
                Assert.That(File.Exists(Path.Combine(f.Dir, "ws", "server", "Xml.m2d")), Is.False);

                m.TriggerLintWarn = true;
                p = s.Plan();
                Assert.That(p.HasErrors, Is.False, EditorSession.Report(p));
                Assert.That(p.Warnings.Count, Is.GreaterThan(0));
                Assert.That(EditorSession.Report(p), Does.Contain("(trigger-lint=\"warn\")"));
                r = s.Apply();
                Assert.That(r.Applied, Is.True, EditorSession.Report(r));

                // the setting is kept in mod.xml
                Mod back = Mod.Load(Path.Combine(f.Dir, "mods", "t.trig"));
                Assert.That(back.TriggerLintWarn, Is.True);
                Assert.That(File.ReadAllText(Path.Combine(f.Dir, "mods", "t.trig", "mod.xml")), Does.StartWith("<mod id=\"t.trig\" name=\"\" version=\"1\" modkit=\"1\" trigger-lint=\"warn\">"));
                Assert.Throws<ModKitException>(() => Mod.Parse("<mod id=\"x\" trigger-lint=\"maybe\" />", "t"));
            }
        }

        static Mod ModFolder(string dir, string id, string body, params string[] files)
        {
            string root = Path.Combine(dir, "mods", id);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "mod.xml"), "<mod id=\"" + id + "\" name=\"\" version=\"1\" modkit=\"1\">" + body + "</mod>");
            for (int i = 0; i + 1 < files.Length; i += 2)
            {
                string path = Path.Combine(root, files[i].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, files[i + 1]);
            }
            return Mod.Load(root);
        }

        [Test]
        public void ARowEditOfAScript_IsLintedOnItsFinalBytes()
        {
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                s.Adopt(ModFolder(f.Dir, "t.rows", "<edit archive=\"" + S + "\" entry=\"" + Door + "\"><change at=\"state[name=Wait]/condition/transition\"><set name=\"state\" value=\"Nowhere\"/></change></edit>"));
                Plan p = s.Plan();
                Assert.That(p.HasErrors, Is.True, EditorSession.Report(p));
                Assert.That(EditorSession.Report(p), Does.Contain("[bad-transition] transition to state 'Nowhere'"));
            }
            // the same edit to a state the script has: clean
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                s.Adopt(ModFolder(f.Dir, "t.rows2", "<edit archive=\"" + S + "\" entry=\"" + Door + "\"><change at=\"state[name=Wait]/condition/transition\"><set name=\"state\" value=\"Wait\"/></change></edit>"));
                Plan p = s.Plan();
                Assert.That(p.HasErrors, Is.False, EditorSession.Report(p));
            }
        }

        const string Lib = "trigger/fake_meadow/lib.xml";
        const string Main = "trigger/fake_meadow/main.xml";
        const string LibText = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ms2>\n    <state name=\"Shared\">\n        <onEnter>\n            <action name=\"SetMesh\" arg1=\"3001\" arg2=\"0\" />\n        </onEnter>\n    </state>\n</ms2>\n";
        const string MainText = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ms2>\n    <import path=\"Data/Xml/Trigger/fake_meadow/lib.xml\" />\n    <state name=\"Start\">\n        <onEnter>\n            <action name=\"SetMesh\" arg1=\"3001\" arg2=\"1\" />\n        </onEnter>\n        <condition name=\"UserDetected\" arg1=\"101\">\n            <transition state=\"Shared\" />\n        </condition>\n    </state>\n</ms2>\n";

        [Test]
        public void AModsOwnLibrary_ResolvesTheImportsOfItsScripts()
        {
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                string entries = "<entry archive=\"" + S + "\" name=\"" + Lib + "\" file=\"files/lib.xml\" />" +
                    "<entry archive=\"" + S + "\" name=\"" + Main + "\" file=\"files/main.xml\" />";
                s.Adopt(ModFolder(f.Dir, "t.lib", entries, "files/lib.xml", LibText, "files/main.xml", MainText));
                Plan p = s.Plan();
                Assert.That(p.HasErrors, Is.False, EditorSession.Report(p));
            }
            // without the library the import does not resolve
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                s.Adopt(ModFolder(f.Dir, "t.nolib", "<entry archive=\"" + S + "\" name=\"" + Main + "\" file=\"files/main.xml\" />", "files/main.xml", MainText));
                Plan p = s.Plan();
                Assert.That(p.HasErrors, Is.True);
                Assert.That(EditorSession.Report(p), Does.Contain("does not resolve"));
            }
        }
    
        [Test]
        public void Save_TakesOutTheFileOfARevertedScript_AndKeepsTheAuthorsFiles()
        {
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                Mod m = s.NewMod("t.orphan");
                var ed = new TriggerEditor(s.Data, m);
                TriggerScript t = ed.Current(S, Door);
                ed.SetAttr(t, t.State("Wait").Children[1], "arg1", "102");
                ed.NewScript(S, "fake_meadow", "bell", "Ring");
                string dir = s.SaveMod();
                string door = Path.Combine(dir, "files", "server", "trigger", "fake_meadow", "door.xml");
                string bell = Path.Combine(dir, "files", "server", "trigger", "fake_meadow", "bell.xml");
                string notes = Path.Combine(dir, "notes.txt");
                File.WriteAllText(notes, "the author's");
                Assert.That(File.Exists(door) && File.Exists(bell), Is.True);
                Assert.That(m.Removed, Is.Empty);

                ed.Revert(S, Door);
                s.SaveMod();
                Assert.That(File.Exists(door), Is.False, "no op names it");
                Assert.That(File.Exists(bell), Is.True);
                Assert.That(File.Exists(notes), Is.True, "outside of files/");
                Assert.That(m.Removed, Is.EqualTo(new[] { "files/server/trigger/fake_meadow/door.xml" }));
                Assert.That(Mod.Load(dir).Ops.Count, Is.EqualTo(1));
            }
        }

        [Test]
        public void ASaveIntoAFolderNewToTheMod_KeepsTheFilesThere()
        {
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                // map-diff -o <folder> and a first Save into <mods>/<id> both save into a folder the mod did not come from
                string dir = Path.Combine(f.Dir, "out");
                string mine = Path.Combine(dir, "files", "x.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(mine));
                File.WriteAllText(mine, "the user's");
                Mod m = s.NewMod("t.new");
                m.Save(dir);
                Assert.That(File.Exists(mine), Is.True);
                Assert.That(m.Removed, Is.Empty);
                var ed = new TriggerEditor(s.Data, m);
                ed.NewScript(S, "fake_meadow", "bell", "Ring");
                m.Save(Path.Combine(f.Dir, "other"));
                Assert.That(File.Exists(mine), Is.True, "the mod moved on; the old folder is not cleaned");
            }
        }

        [Test]
        public void ALinkedFolderUnderFiles_IsNotEntered()
        {
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                string outside = Path.Combine(f.Dir, "outside");
                Directory.CreateDirectory(outside);
                string theirs = Path.Combine(outside, "keep.txt");
                File.WriteAllText(theirs, "outside of the mod");
                Mod m = s.NewMod("t.link");
                var ed = new TriggerEditor(s.Data, m);
                ed.NewScript(S, "fake_meadow", "bell", "Ring");
                string dir = s.SaveMod();
                string link = Path.Combine(dir, "files", "linked");
                try { Directory.CreateSymbolicLink(link, outside); }
                catch (System.Exception ex) { Assert.Ignore("no symbolic link here: " + ex.Message); }
                s.SaveMod();
                Assert.That(File.Exists(theirs), Is.True);
                Assert.That(m.Removed, Is.Empty);
                Assert.That(File.Exists(Path.Combine(dir, "files", "server", "trigger", "fake_meadow", "bell.xml")), Is.True);
            }
        }

        [Test]
        public void SavedUnderANewId_TheModCarriesItsFiles()
        {
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                Mod m = s.NewMod("t.one");
                var ed = new TriggerEditor(s.Data, m);
                TriggerScript t = ed.NewScript(S, "fake_meadow", "bell", "Ring");
                string one = s.SaveMod();
                m.Id = "t.two";
                string two = s.SaveMod();
                Assert.That(two, Is.Not.EqualTo(one));
                string rel = Path.Combine("files", "server", "trigger", "fake_meadow", "bell.xml");
                Assert.That(File.ReadAllBytes(Path.Combine(two, rel)), Is.EqualTo(t.Bytes));
                Assert.That(File.Exists(Path.Combine(one, rel)), Is.True, "the old folder is left as it was");
                Assert.That(new TriggerEditor(s.Data, Mod.Load(two)).Current(S, "trigger/fake_meadow/bell.xml").Bytes, Is.EqualTo(t.Bytes));
            }
        }

        [Test]
        public void APendingFile_BeatsTheSavedOne()
        {
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                Mod m = s.NewMod("t.pending");
                var ed = new TriggerEditor(s.Data, m);
                TriggerScript t = ed.Current(S, Door);
                t = ed.SetAttr(t, t.State("Wait").Children[1], "arg1", "102");
                s.SaveMod();
                t = ed.SetAttr(t, t.State("Wait").Children[1], "arg1", "103");
                Assert.That(m.Files.Count, Is.EqualTo(1));
                Plan p = s.Plan();
                Assert.That(p.HasErrors, Is.False, EditorSession.Report(p));
                ApplyResult r = s.Apply();
                Assert.That(r.Applied, Is.True, EditorSession.Report(r));
                using (M2dArchive a = M2dArchive.Open(Path.Combine(f.Dir, "ws", "server", "Xml.m2d")))
                    Assert.That(Encoding.UTF8.GetString(a.Read(a.Find(Door))), Does.Contain("arg1=\"103\"").And.Not.Contain("arg1=\"102\""));
            }
        }

        [Test]
        public void AFileOutsideOfTheModFolder_IsRefused_AndAnEditMovesItUnderFiles()
        {
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                File.WriteAllText(Path.Combine(f.Dir, "outside.xml"), FakeIdTables.Door);
                Mod m = ModFolder(f.Dir, "t.out", "<entry archive=\"" + S + "\" name=\"" + Door + "\" file=\"../../outside.xml\" />");
                s.Adopt(m);
                var ed = new TriggerEditor(s.Data, m);
                var ex = Assert.Throws<ModKitException>(() => ed.Current(S, Door));
                Assert.That(ex.Message, Does.Contain("outside of the mod folder"));
                Assert.That(EditorSession.Report(s.Plan()), Does.Contain("outside of the mod folder"));

                // an edit stores the bytes under files/ (the op moves there), and Save writes them inside the folder
                ed.Store(S, Door, Encoding.UTF8.GetBytes(FakeIdTables.Door.Replace("arg1=\"101\"", "arg1=\"102\"")));
                Assert.That(ed.OpOf(S, Door).File, Is.EqualTo("files/server/" + Door));
                string dir = s.SaveMod();
                Assert.That(File.Exists(Path.Combine(dir, "files", "server", "trigger", "fake_meadow", "door.xml")), Is.True);
                Assert.That(File.ReadAllText(Path.Combine(f.Dir, "outside.xml")), Is.EqualTo(FakeIdTables.Door));
            }
        }

        // a script as the edits get it: the line ends, a byte order mark and the encoding are the file's own
        static byte[] Script(string nl, string encoding, bool bom, string state)
        {
            string text = "<?xml version=\"1.0\" encoding=\"" + encoding + "\"?>" + nl + "<ms2>" + nl
                + "    <state name=\"" + state + "\">" + nl + "        <onEnter>" + nl
                + "            <action name=\"set_mesh\" arg1=\"1\" />" + nl + "        </onEnter>" + nl
                + "    </state>" + nl + "</ms2>" + nl;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            byte[] body = Encoding.GetEncoding(encoding).GetBytes(text);
            return bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
        }

        static TriggerScript AddTimer(TriggerEditor ed, byte[] bytes, string state)
        {
            TriggerScript t = ed.Store(S, Door, bytes);
            t = ed.Add(t, t.State(state).Children[0], TriggerKind.Action, "set_timer", null);
            return ed.SetAttr(t, t.State(state).Children[0].Children[1], "arg1", state);
        }

        [Test]
        public void AnInsertedElement_TakesTheLineEndsOfTheFile()
        {
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                var ed = new TriggerEditor(s.Data, s.NewMod("t.nl"));
                foreach (string nl in new[] { "\r\n", "\n" })
                {
                    string got = Encoding.UTF8.GetString(AddTimer(ed, Script(nl, "utf-8", false, "Wait"), "Wait").Bytes);
                    Assert.That(got, Is.EqualTo(Encoding.UTF8.GetString(Script(nl, "utf-8", false, "Wait")).Replace(
                        "arg1=\"1\" />" + nl + "        </onEnter>",
                        "arg1=\"1\" />" + nl + "            <action name=\"set_timer\" arg1=\"Wait\" />" + nl + "        </onEnter>")));
                    if (nl == "\r\n") Assert.That(got.Replace("\r\n", ""), Does.Not.Contain("\n"), "no lone LF in a CRLF file");
                }
            }
        }

        [Test]
        public void AByteOrderMark_IsKept()
        {
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                var ed = new TriggerEditor(s.Data, s.NewMod("t.bom"));
                byte[] b = AddTimer(ed, Script("\r\n", "utf-8", true, "Wait"), "Wait").Bytes;
                Assert.That(b.Take(3).ToArray(), Is.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
                Assert.That(b.Skip(3).Take(5).ToArray(), Is.EqualTo(Encoding.ASCII.GetBytes("<?xml")), "one mark, not two");
                Assert.That(ed.Current(S, Door).State("Wait").Children[0].Children.Count, Is.EqualTo(2));
            }
        }

        [Test]
        public void AFileInItsOwnEncoding_StaysInIt()
        {
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                var ed = new TriggerEditor(s.Data, s.NewMod("t.enc"));
                const string state = "\uB300\uAE30";      // two hangul syllables, two bytes each in the Korean code page
                Encoding ks = Encoding.GetEncoding("ks_c_5601-1987");
                byte[] b = AddTimer(ed, Script("\r\n", "ks_c_5601-1987", false, state), state).Bytes;
                string want = ks.GetString(Script("\r\n", "ks_c_5601-1987", false, state)).Replace(
                    "arg1=\"1\" />\r\n        </onEnter>",
                    "arg1=\"1\" />\r\n            <action name=\"set_timer\" arg1=\"" + state + "\" />\r\n        </onEnter>");
                Assert.That(b, Is.EqualTo(ks.GetBytes(want)), "the new value is written in the file's code page");
                Assert.Throws<DecoderFallbackException>(() => new UTF8Encoding(false, true).GetString(b), "not UTF-8");
                Assert.That(ed.Current(S, Door).State(state), Is.Not.Null);
            }
        }
    }
}
