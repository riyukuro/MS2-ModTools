using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class TriggersTests
    {
        static EditorSession Session(string dir)
        {
            return new EditorSession(Path.Combine(dir, "kms"), Path.Combine(dir, "gms"), Path.Combine(dir, "server"), Path.Combine(dir, "mods"), Path.Combine(dir, "ws"));
        }

        [Test]
        public void ReadsTheScriptsOfAMap_FromTheServerFirst()
        {
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                Assert.That(Triggers.ArchiveOf(s.Data), Is.EqualTo(EntityEditor.ServerXml));
                List<TriggerFolder> l = Triggers.FoldersOf(s.Data, EntityEditor.ServerXml, 9100001);
                Assert.That(l.Count, Is.EqualTo(1));
                Assert.That(l[0].XBlock, Is.EqualTo("fake_meadow"));
                Assert.That(l[0].Scripts, Is.EqualTo(new[] { "trigger/fake_meadow/door.xml", "trigger/fake_meadow/broken.xml" }));
                Assert.That(Triggers.FoldersOf(s.Data, EntityEditor.ServerXml, 9100003)[0].Scripts, Is.EqualTo(new[] { "trigger/fake_far_shore/tide.xml" }));

                // map 9100002 has a row in the kms table only, and no scripts
                l = Triggers.FoldersOf(s.Data, EntityEditor.ServerXml, 9100002);
                Assert.That(l.Count, Is.EqualTo(1));
                Assert.That(l[0].XBlock, Is.EqualTo("fake_harbor"));
                Assert.That(l[0].Scripts, Is.Empty);
                Assert.That(Triggers.FoldersOf(s.Data, EntityEditor.ServerXml, 9199999), Is.Empty, "no such map");

                // the kms table names map 9100001 too
                Assert.That(Triggers.FoldersOf(s.Data, EntityEditor.ClientXml, 9100001)[0].Scripts.Count, Is.EqualTo(2));
                Assert.That(Triggers.FoldersOf(s.Data, EntityEditor.ClientXml, 9100003)[0].Scripts, Is.Empty, "kms has no far shore scripts");
            }
        }

        [Test]
        public void TheTree_StatesConditionsActionsAndWhereTheyGo()
        {
            using (var f = new FakeIdTables())
            using (EditorSession s = Session(f.Dir))
            {
                TriggerScript t = Triggers.Read(s.Data, EntityEditor.ServerXml, "trigger/fake_meadow/door.xml");
                Assert.That(t.ParseError, Is.Null);
                Assert.That(t.XBlock, Is.EqualTo("fake_meadow"));
                Assert.That(t.FileName, Is.EqualTo("door.xml"));
                List<TriggerNode> st = t.States;
                Assert.That(st.Count, Is.EqualTo(2));
                Assert.That(st[0].Label, Is.EqualTo("state Wait"));
                Assert.That(st[0].GoesTo(), Is.EqualTo(new[] { "Open" }));
                Assert.That(st[1].GoesTo(), Is.EqualTo(new[] { "Wait" }));
                TriggerNode enter = st[1].Children[0];
                Assert.That(enter.Kind, Is.EqualTo(TriggerKind.OnEnter));
                Assert.That(enter.Children[1].Label, Is.EqualTo("set_timer arg1=\"1\" arg2=\"5\""));
                TriggerNode cond = st[1].Children[1];
                Assert.That(cond.Kind, Is.EqualTo(TriggerKind.Condition));
                Assert.That(cond.Label, Is.EqualTo("if time_expired arg1=\"1\"  → Wait"));
                Assert.That(cond.Children[0].Label, Is.EqualTo("→ Wait"));
                Assert.That(st[1].Children[2].Kind, Is.EqualTo(TriggerKind.OnExit));
                Assert.That(t.NodeAt(cond.Line), Is.SameAs(cond));
                Assert.That(t.State("Open"), Is.SameAs(st[1]));
            }
        }

        [Test]
        public void ABadFile_GivesAParseErrorAndNoTree()
        {
            TriggerScript t = TriggerScript.Parse(EntityEditor.ServerXml, "trigger/x/y.xml", Encoding.UTF8.GetBytes("<ms2><state name=\"a\"></ms2>"));
            Assert.That(t.ParseError, Is.Not.Null);
            Assert.That(t.Root, Is.Null);
            Assert.That(t.States, Is.Empty);
        }

        [Test]
        public void WhatIsAScript()
        {
            Assert.That(Triggers.IsScript("trigger/a_b/c.xml"), Is.True);
            Assert.That(Triggers.IsScript("trigger/a_b/sub/c.xml"), Is.False);
            Assert.That(Triggers.IsScript("trigger/c.xml"), Is.False);
            Assert.That(Triggers.IsScript("trigger/a_b/.xml"), Is.False);
            Assert.That(Triggers.IsScript("map/c.xml"), Is.False);
            Assert.That(Triggers.XBlockOf("Trigger/A_B/c.xml"), Is.EqualTo("A_B"));
        }
    }
}
