// NPC talk scripts (NpcTalk.cs) on made-up data: a kms client with one combined file, a global client and a
// server with a file per NPC. Every id here is made up.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    public sealed class FakeTalks : IDisposable
    {
        public const string Combined = "script/npc/npcscript_final.xml";
        public readonly string Dir;
        public readonly DataSet Data;
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        static string Steps(string ind, long npc, bool extra)
        {
            string i2 = ind + "\t", i3 = i2 + "\t";
            return ind + "<select id=\"0\">\n" + i2 + "<content text=\"$script:" + npc + "001$\" />\n" + ind + "</select>\n" +
                ind + "<script randomPick=\"1\" id=\"10\">\n" + i2 + "<content text=\"$script:" + npc + "002$\">\n" +
                i3 + "<distractor text=\"$script:" + npc + "003$\" goto=\"11\" />\n" + i2 + "</content>\n" + ind + "</script>\n" +
                ind + "<script id=\"11\">\n" + i2 + "<content text=\"$script:" + npc + "004$\" />\n" + i2 + "<content text=\"$script:" + npc + "005$\" />\n" + ind + "</script>\n" +
                (extra ? ind + "<monologue id=\"30\">\n" + i2 + "<content text=\"$script:" + npc + "006$\" />\n" + ind + "</monologue>\n" : "");
        }

        // the kms file: LF line ends, tabs, all NPCs in one file
        public static readonly string Kms = Head + "<ms2>\n" +
            "\t<npc id=\"90000001\">\n" + Steps("\t\t", 90000001, true) + "\t</npc>\n" +
            "\t<npc id=\"90000002\">\n" + Steps("\t\t", 90000002, false) + "\t</npc>\n" +
            "\t<npc id=\"90000003\">\n" + Steps("\t\t", 90000003, false) + "\t</npc>\n" +
            "</ms2>\n";

        // a file of one NPC on the global client and the server (CRLF there); 90000001 has no monologue here
        public static string File(long npc) { return Head + "<ms2>\n" + Steps("\t", npc, false) + "</ms2>\n"; }

        public FakeTalks()
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-talks-" + Guid.NewGuid().ToString("N"));
            string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
            foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);
            M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                new KeyValuePair<string, byte[]>(Combined, Encoding.UTF8.GetBytes(Kms)),
            });
            foreach (string d in new[] { g, s })
                M2dWriter.Create(Path.Combine(d, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                {
                    new KeyValuePair<string, byte[]>("script/npc/90000001.xml", Fake.Crlf(File(90000001))),
                    new KeyValuePair<string, byte[]>("script/npc/90000002.xml", Fake.Crlf(File(90000002))),
                });
            Data = new DataSet(k, g, s);
        }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class NpcTalkTests
    {
        const string K = "client:Xml.m2d", G = "gms-client:Xml.m2d", S = "server:Xml.m2d";
        const string F1 = "script/npc/90000001.xml";

        static Mod NewMod() { return Mod.Parse("<mod id=\"talk.test\" name=\"t\" version=\"1\">\n</mod>", "test mod"); }

        static Plan PlanOf(FakeTalks f, Mod m) { return Planner.Build(f.Data, new[] { m }, new PlanOptions()); }

        static string Lf(string s) { return s == null ? null : s.Replace("\r\n", "\n"); }

        [Test]
        public void Open_ReadsTheCombinedSideAndTheFileSides()
        {
            using (var f = new FakeTalks())
            {
                var ed = new TalkEditor(f.Data, NewMod());
                NpcTalk t = ed.Open(90000001);
                Assert.That(t.Sides.Count, Is.EqualTo(3));
                TalkSide k = t.Side(K), g = t.Side(G);
                Assert.That(k.Combined, Is.True);
                Assert.That(k.Entry, Is.EqualTo(FakeTalks.Combined));
                Assert.That(k.Root.Name, Is.EqualTo("npc"));
                Assert.That(g.Combined, Is.False);
                Assert.That(g.Entry, Is.EqualTo(F1));
                Assert.That(k.Steps.Count, Is.EqualTo(4));
                Assert.That(g.Steps.Count, Is.EqualTo(3));
                Assert.That(t.Keys, Is.EqualTo(new[] { "select 0", "script 10", "script 11", "monologue 30" }));
                Assert.That(k.Shipped && !k.InMod, Is.True);

                NpcTalk only = ed.Open(90000003);
                Assert.That(only.Sides.Count, Is.EqualTo(1));
                Assert.That(ed.Open(90000009).Sides, Is.Empty);
                Assert.That(ed.NpcIds(), Is.EqualTo(new long[] { 90000001, 90000002, 90000003 }));
            }
        }

        [Test]
        public void AnAttributeEdit_IsRowsOnTheCombinedFileAndWholeFilesElsewhere_ByteExact()
        {
            using (var f = new FakeTalks())
            {
                Mod m = NewMod();
                var ed = new TalkEditor(f.Data, m);
                NpcTalk t = ed.Open(90000001);
                var at = new TalkPath("script 11").Then("content", 1);
                var missed = new List<string>();
                t = ed.SetAttr(t, at, "text", "$script:90000001099$", missed);
                Assert.That(missed, Is.Empty);
                foreach (TalkSide s in t.Sides) Assert.That(s.InMod, Is.True, s.Archive);

                // kms: a delete of the NPC and an add of the new <npc> after it
                EditOp kms = null;
                foreach (ModOp o in m.Ops) if (o is EditOp) kms = (EditOp)o;
                Assert.That(kms.Archive, Is.EqualTo(K));
                Assert.That(kms.Entry, Is.EqualTo(FakeTalks.Combined));
                Assert.That(kms.Rows.Count, Is.EqualTo(2));
                Assert.That(kms.Rows[0].Kind, Is.EqualTo(RowOpKind.Delete));
                Assert.That(kms.Rows[0].At, Is.EqualTo("npc[id=90000001]"));
                Assert.That(kms.Rows[1].After, Is.EqualTo("npc[id=90000001]"));
                Assert.That(kms.Rows[0].Sync || kms.Rows[1].Sync, Is.False);

                // read back from a saved and loaded mod (the rows only; the file bytes stay in Mod.Files)
                Mod again = Mod.Parse(m.ToXml(), "round trip");
                foreach (KeyValuePair<string, byte[]> kv in m.Files) again.Files[kv.Key] = kv.Value;
                NpcTalk back = new TalkEditor(f.Data, again).Open(90000001);
                Assert.That(back.Side(K).Text, Is.EqualTo(t.Side(K).Text));

                Plan p = PlanOf(f, again);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string want = FakeTalks.Kms.Replace("$script:90000001005$", "$script:90000001099$");
                Assert.That(Fake.Text(p, K, FakeTalks.Combined), Is.EqualTo(want), "the rest of the combined file stays byte-equal");
                Assert.That(Lf(Fake.Text(p, G, F1)), Is.EqualTo(FakeTalks.File(90000001).Replace("$script:90000001005$", "$script:90000001099$")));
                Assert.That(Fake.Text(p, S, F1), Does.Contain("\r\n"), "the file keeps its line ends");

                // set back: the talk leaves the mod
                t = ed.SetAttr(t, at, "text", "$script:90000001005$", missed);
                Assert.That(m.IsEmpty, Is.True);
                Assert.That(m.Files, Is.Empty);
            }
        }

        [Test]
        public void TheLastNpcOfTheCombinedFile_StaysInPlace()
        {
            using (var f = new FakeTalks())
            {
                Mod m = NewMod();
                var ed = new TalkEditor(f.Data, m);
                var missed = new List<string>();
                ed.SetAttr(ed.Open(90000003), new TalkPath("select 0").Then("content", 0), "voiceID", "x/1", missed);
                Assert.That(missed, Is.Empty);
                Plan p = PlanOf(f, Mod.Parse(m.ToXml(), "round trip"));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Fake.Text(p, K, FakeTalks.Combined), Is.EqualTo(FakeTalks.Kms.Replace("$script:90000003001$\"", "$script:90000003001$\" voiceID=\"x/1\"")));
                Assert.That(p.Find(G) == null || Fake.Text(p, G, "script/npc/90000003.xml") == null, Is.True, "no file side has this NPC");
            }
        }

        [Test]
        public void InsertRemoveAndAddStep_OnEverySideThatHasThePlace()
        {
            using (var f = new FakeTalks())
            {
                Mod m = NewMod();
                var ed = new TalkEditor(f.Data, m);
                NpcTalk t = ed.Open(90000001);
                var missed = new List<string>();

                // a second choice after the first, with the file's indent
                var content = new TalkPath("script 10").Then("content", 0);
                t = ed.Insert(t, content, new KeyValuePair<string, int>("distractor", 0), "<distractor text=\"$script:90000001010$\" goto=\"30\" />", missed);
                Assert.That(Lf(t.Side(G).Text), Does.Contain("goto=\"11\" />\n\t\t\t<distractor text=\"$script:90000001010$\" goto=\"30\" />\n\t\t</content>"));
                Assert.That(t.Side(K).Text, Does.Contain("goto=\"11\" />\n\t\t\t\t<distractor text=\"$script:90000001010$\" goto=\"30\" />\n\t\t\t</content>"));

                // the monologue is on the kms client only: the other sides are named
                t = ed.SetAttr(t, new TalkPath("monologue 30").Then("content", 0), "text", "$script:90000001011$", missed);
                Assert.That(missed, Is.EqualTo(new[] { G, S }));

                // a new step goes after the last step of its kind, on every side
                missed.Clear();
                t = ed.AddStep(t, "script", "12", missed);
                Assert.That(missed, Is.Empty);
                foreach (TalkSide s in t.Sides)
                {
                    Assert.That(s.Steps[3].Key, Is.EqualTo("script 12"), s.Archive);
                    Assert.That(Lf(s.Text), Does.Contain("</script>\n" + (s.Combined ? "\t\t" : "\t") + "<script id=\"12\">\n"), s.Archive);
                }
                Assert.Throws<ModKitException>(() => ed.AddStep(t, "script", "12", null), "a step key twice");
                Assert.Throws<ModKitException>(() => ed.AddStep(t, "content", "13", null), "not a step kind");
                Assert.Throws<ModKitException>(() => ed.Insert(t, null, null, "<a/><b/>", null), "two elements");

                // removing the new step and the new choice gives the shipped text back (but the monologue edit)
                t = ed.Remove(t, new TalkPath("script 12"), missed);
                t = ed.Remove(t, content.Then("distractor", 1), missed);
                Assert.That(Lf(t.Side(G).Text), Is.EqualTo(FakeTalks.File(90000001)));
                Assert.That(t.Side(G).InMod, Is.False);
                Assert.That(t.Side(K).InMod, Is.True);

                Plan p = PlanOf(f, Mod.Parse(m.ToXml(), "round trip"));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Fake.Text(p, K, FakeTalks.Combined), Is.EqualTo(FakeTalks.Kms.Replace("$script:90000001006$", "$script:90000001011$")));

                ed.Revert(90000001);
                Assert.That(m.IsEmpty, Is.True);
            }
        }

        [Test]
        public void TalkPathOf_FindsTheSamePlace()
        {
            using (var f = new FakeTalks())
            {
                NpcTalk t = new TalkEditor(f.Data, NewMod()).Open(90000001);
                TalkSide k = t.Side(K);
                var d = k.Step("script 10").Elem.Children[0].Children[0];
                TalkPath p = TalkPath.Of(k, d);
                Assert.That(p.ToString(), Is.EqualTo("script 10 / content 1 / distractor 1"));
                Assert.That(p.Resolve(t.Side(S)).Attr("goto"), Is.EqualTo("11"));
                Assert.That(TalkPath.Of(k, k.Root), Is.Null);
            }
        }
    }
}
