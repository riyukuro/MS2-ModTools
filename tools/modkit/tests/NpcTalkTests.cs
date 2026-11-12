// NPC talk scripts (NpcTalk.cs) on made-up data: a kms client with one combined file, a global client and a
// server with a file per NPC. Every id here is made up.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MS2.ModKit;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;
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

        // talk text files: kms has ko (both NPCs) and en (only 90000002); the global client and the server have en in
        // two files and a quest talk file that takes 90000001007, so a new text of 90000001 is 90000001008
        static string Keys(params string[] rows)
        {
            var b = new StringBuilder(Head + "<ms2>\n");
            foreach (string r in rows) b.Append("\t" + r + "\n");
            return b.Append("</ms2>\n").ToString();
        }

        static string[] KeysOf(long npc, int count, string prefix)
        {
            var l = new List<string>();
            for (int i = 1; i <= count; i++) l.Add("<key id=\"" + npc + i.ToString("000") + "\" name=\"" + prefix + npc % 10 + "-" + i + "\" />");
            return l.ToArray();
        }

        static string[] Join(params string[][] parts)
        {
            var l = new List<string>();
            foreach (string[] p in parts) l.AddRange(p);
            return l.ToArray();
        }

        public static readonly string KoKeys = Keys(Join(KeysOf(90000001, 6, "ko"), new[] { "<key id=\"90000001005\" feature=\"F1\" name=\"ko1-5f\" />" }, KeysOf(90000002, 5, "ko")));
        public static readonly string KmsEnKeys = Keys(KeysOf(90000002, 5, "en"));
        public static readonly string FunctionKeys = Keys(KeysOf(90000001, 5, "en"));
        public static readonly string NamKeys = Keys(KeysOf(90000002, 5, "en"));
        public static readonly string QuestKeys = Keys("<key id=\"90000001007\" name=\"quest\" />");

        public FakeTalks()
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-talks-" + Guid.NewGuid().ToString("N"));
            string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
            foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);
            M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                new KeyValuePair<string, byte[]>(Combined, Encoding.UTF8.GetBytes(Kms)),
                new KeyValuePair<string, byte[]>("string/ko/scriptnpc.xml", Encoding.UTF8.GetBytes(KoKeys)),
                new KeyValuePair<string, byte[]>("string/en/scriptnpc.xml", Encoding.UTF8.GetBytes(KmsEnKeys)),
            });
            foreach (string d in new[] { g, s })
                M2dWriter.Create(Path.Combine(d, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                {
                    new KeyValuePair<string, byte[]>("script/npc/90000001.xml", Fake.Crlf(File(90000001))),
                    new KeyValuePair<string, byte[]>("script/npc/90000002.xml", Fake.Crlf(File(90000002))),
                    new KeyValuePair<string, byte[]>("string/en/scriptnpcfunction.xml", Fake.Crlf(FunctionKeys)),
                    new KeyValuePair<string, byte[]>("string/en/scriptnpcnam.xml", Fake.Crlf(NamKeys)),
                    new KeyValuePair<string, byte[]>("string/en/scriptquest.xml", Fake.Crlf(QuestKeys)),
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
        public void APageMovesUpOrDown_TheOtherRowsStayByteEqual()
        {
            using (var f = new FakeTalks())
            {
                Mod m = NewMod();
                var ed = new TalkEditor(f.Data, m);
                var missed = new List<string>();
                NpcTalk t = ed.Move(ed.Open(90000001), new TalkPath("script 11").Then("content", 1), -1, missed);
                Assert.That(missed, Is.Empty);
                Assert.That(t.Side(K).Step("script 11").Elem.Children[0].Attr("text"), Is.EqualTo("$script:90000001005$"));

                Mod again = Mod.Parse(m.ToXml(), "round trip");
                foreach (KeyValuePair<string, byte[]> kv in m.Files) again.Files[kv.Key] = kv.Value;
                Plan p = PlanOf(f, again);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string a = "<content text=\"$script:90000001004$\" />", b = "<content text=\"$script:90000001005$\" />";
                string kms = FakeTalks.Kms, swapped = kms.Replace("\t\t\t" + a + "\n\t\t\t" + b, "\t\t\t" + b + "\n\t\t\t" + a);
                Assert.That(swapped, Is.Not.EqualTo(kms));
                Assert.That(Fake.Text(p, K, FakeTalks.Combined), Is.EqualTo(swapped), "only the pages of 90000001 change places");
                Assert.That(Lf(Fake.Text(p, G, F1)), Is.EqualTo(FakeTalks.File(90000001).Replace("\t\t" + a + "\n\t\t" + b, "\t\t" + b + "\n\t\t" + a)));

                // the first page cannot move up; moving it back down takes the talk out of the mod
                Assert.Throws<ModKitException>(() => ed.Move(t, new TalkPath("script 11").Then("content", 0), -1, missed));
                Assert.Throws<ModKitException>(() => ed.Move(t, new TalkPath("script 11"), 1, missed), "a step does not move");
                ed.Move(t, new TalkPath("script 11").Then("content", 0), 1, missed);
                Assert.That(m.IsEmpty, Is.True);
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

        static readonly TalkPath Line5 = new TalkPath("script 11").Then("content", 1);

        [Test]
        public void TheFirstTextEdit_CopiesTheRowsUnderAnOwnIdOnEverySide()
        {
            using (var f = new FakeTalks())
            {
                var m = NewMod();
                var ed = new TalkEditor(f.Data, m);
                Assert.That(ed.TextLanguages(), Is.EqualTo(new[] { "en", "ko" }));
                NpcTalk t = ed.Open(90000001);
                Assert.That(ed.Text(90000001, 90000001005, "ko").Rows, Is.EqualTo(2));
                Assert.That(ed.Text(90000001, 90000001005, "ko").Differs, Is.True);
                Assert.That(ed.Text(90000001, 90000001005, "en").Text, Is.EqualTo("en1-5"));
                Assert.That(ed.Text(90000001, 90000001005, "en").Own, Is.False);

                t = ed.SetText(t, Line5, "en", "Hello & bye", null);
                const long N = 90000001008;
                foreach (TalkSide s in t.Sides) Assert.That(Line5.Resolve(s).Attr("text"), Is.EqualTo("$script:90000001008$"), s.Archive);
                TalkText en = ed.Text(90000001, N, "en"), ko = ed.Text(90000001, N, "ko");
                Assert.That(en.Text, Is.EqualTo("Hello & bye"));
                Assert.That(en.Own, Is.True);
                Assert.That(en.Rows, Is.EqualTo(3), "kms gets a new en row, the others copy theirs");
                Assert.That(ko.Rows, Is.EqualTo(2));
                Assert.That(ko.Text, Is.EqualTo("ko1-5"), "other languages keep their text");
                Assert.That(ed.Text(90000001, 90000001005, "en").Text, Is.EqualTo("en1-5"), "the shipped rows stay");
                Assert.That(m.Ids.Count, Is.EqualTo(1));
                Assert.That(m.Ids[0].Space, Is.EqualTo(TalkEditor.TextIdSpace));
                Assert.That(m.Ids[0].From, Is.EqualTo(N));

                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string kmsKo = Fake.Text(p, K, "string/ko/scriptnpc.xml"), kmsEn = Fake.Text(p, K, "string/en/scriptnpc.xml");
                Assert.That(kmsKo, Does.Contain("<key id=\"90000001008\" name=\"ko1-5\" />"));
                Assert.That(kmsKo, Does.Contain("<key id=\"90000001008\" feature=\"F1\" name=\"ko1-5f\" />"));
                Assert.That(kmsEn, Does.Contain("<key id=\"90000001008\" name=\"Hello &amp; bye\" />"));
                foreach (string a in new[] { G, S })
                {
                    Assert.That(Fake.Text(p, a, "string/en/scriptnpcfunction.xml"), Does.Contain("<key id=\"90000001008\" name=\"Hello &amp; bye\" />"), a);
                    Assert.That(Fake.Text(p, a, "string/en/scriptnpcfunction.xml"), Does.Contain("<key id=\"90000001005\" name=\"en1-5\" />"), a);
                    Assert.That(Fake.Text(p, a, F1), Does.Contain("$script:90000001008$"), a);
                }
                Assert.That(Fake.Text(p, K, FakeTalks.Combined), Is.EqualTo(FakeTalks.Kms.Replace("$script:90000001005$", "$script:90000001008$")));
            }
        }

        [Test]
        public void ALaterTextEdit_ChangesTheOwnRows()
        {
            using (var f = new FakeTalks())
            {
                var m = NewMod();
                var ed = new TalkEditor(f.Data, m);
                NpcTalk t = ed.SetText(ed.Open(90000001), Line5, "en", "one", null);
                int ops = m.Ops.Count;
                t = ed.SetText(t, Line5, "ko", "two", null);
                t = ed.SetText(t, Line5, "en", "three", null);
                Assert.That(m.Ops.Count, Is.EqualTo(ops));
                Assert.That(m.Ids.Count, Is.EqualTo(1));
                Assert.That(ed.Text(90000001, 90000001008, "en").Text, Is.EqualTo("three"));
                Assert.That(ed.Text(90000001, 90000001008, "en").Differs, Is.False);
                Assert.That(ed.Text(90000001, 90000001008, "ko").Text, Is.EqualTo("two"));
                Assert.That(ed.Text(90000001, 90000001008, "ko").Rows, Is.EqualTo(2));
                Assert.That(ed.Say(90000001, "$script:90000001008$", "ko"), Is.EqualTo("two"));
                // a language no target has a talk text file of: refused, not "text set" with nothing changed
                var e = Assert.Throws<ModKitException>(() => ed.SetText(t, Line5, "fr", "quatre", null));
                Assert.That(e.Message, Is.EqualTo("no target has a fr talk text file to write the line in"));
                Assert.That(m.Ops.Count, Is.EqualTo(ops));

                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Fake.Text(p, K, "string/ko/scriptnpc.xml"), Does.Contain("<key id=\"90000001008\" feature=\"F1\" name=\"two\" />"));
                Assert.That(Fake.Text(p, G, "string/en/scriptnpcfunction.xml"), Does.Contain("<key id=\"90000001008\" name=\"three\" />"));
            }
        }

        [Test]
        public void ANewTextId_SkipsTheIdsOfOtherMods_AndASecondLineGetsTheNext()
        {
            using (var f = new FakeTalks())
            {
                var other = NewMod();
                new TalkEditor(f.Data, other).SetText(new TalkEditor(f.Data, other).Open(90000001), Line5, "en", "x", null);
                var m = NewMod();
                var ed = new TalkEditor(f.Data, m) { Others = () => new[] { other } };
                NpcTalk t = ed.SetText(ed.Open(90000001), Line5, null, "mine", null);
                Assert.That(Line5.Resolve(t.Side(K)).Attr("text"), Is.EqualTo("$script:90000001009$"));
                Assert.That(ed.Text(90000001, 90000001009, "ko").Text, Is.EqualTo("mine"), "no language: every language");
                t = ed.SetText(t, new TalkPath("select 0").Then("content", 0), "en", "first", null);
                Assert.That(new TalkPath("select 0").Then("content", 0).Resolve(t.Side(G)).Attr("text"), Is.EqualTo("$script:90000001010$"));
                Assert.That(m.Ids.Count, Is.EqualTo(1), "the ranges join");
                Assert.That(m.Ids[0].To, Is.EqualTo(90000001010));
                Assert.Throws<ModKitException>(() => ed.SetText(t, new TalkPath("script 10"), "en", "no", null));
            }
        }

        static readonly TalkPath Choice = new TalkPath("script 10").Then("content", 0).Then("distractor", 0);

        static List<string> Texts(List<PlanMessage> l, string archive)
        {
            var r = new List<string>();
            foreach (PlanMessage m in l) if (archive == null || m.Archive == archive) r.Add(m.Text);
            return r;
        }

        [Test]
        public void TheShippedTalk_HasOnlyShippedProblems_AndAPlainPlanHasNone()
        {
            using (var f = new FakeTalks())
            {
                var ed = new TalkEditor(f.Data, NewMod());
                List<TalkProblem> l = ed.Problems(ed.Open(90000001));
                Assert.That(l.TrueForAll(p => p.Shipped), Is.True, string.Join("\n", l.ConvertAll(p => p.ToString()).ToArray()));
                Assert.That(l.Exists(p => p.Archive == K && p.Text == "select 0 / content 1: talk text 90000001001 has no en row"), Is.True);

                var m = NewMod();
                ed = new TalkEditor(f.Data, m);
                ed.SetAttr(ed.Open(90000001), Choice, "voiceID", "x/1", null);
                Plan p0 = PlanOf(f, m);
                Assert.That(p0.Errors, Is.Empty, Fake.Dump(p0));
                Assert.That(p0.Warnings, Is.Empty, Fake.Dump(p0));
            }
        }

        [Test]
        public void AJumpToNoStep_IsAnError_OnEverySide()
        {
            using (var f = new FakeTalks())
            {
                var m = NewMod();
                var ed = new TalkEditor(f.Data, m);
                NpcTalk t = ed.SetAttr(ed.Open(90000001), Choice, "goto", "11,99", null);
                const string want = "script 10 / content 1 / distractor 1: goto names step 99, which the NPC has not";
                List<TalkProblem> l = ed.Problems(t).FindAll(x => !x.Shipped);
                Assert.That(l.ConvertAll(x => x.Archive + " " + x), Is.EquivalentTo(new[] { K + " error: " + want, G + " error: " + want, S + " error: " + want }));
                Plan p = PlanOf(f, m);
                foreach (string a in new[] { K, G, S })
                    Assert.That(Texts(p.Errors, a), Is.EqualTo(new[] { "NPC 90000001 talk, " + want }), a);
                Assert.That(p.Errors[0].ModId, Is.EqualTo("talk.test"));
            }
        }

        [Test]
        public void ANewUnreachableStep_AnUnknownButtonSetAndATextWithNoRow_AreWarnings()
        {
            using (var f = new FakeTalks())
            {
                var m = NewMod();
                var ed = new TalkEditor(f.Data, m);
                NpcTalk t = ed.AddStep(ed.Open(90000002), "script", "12", null);
                t = ed.SetAttr(t, new TalkPath("select 0").Then("content", 0), "buttonSet", "2", null);
                t = ed.SetAttr(t, new TalkPath("script 11").Then("content", 0), "text", "$script:90000002999$", null);
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Texts(p.Warnings, G), Is.EquivalentTo(new[]
                {
                    "NPC 90000002 talk, select 0 / content 1: buttonSet 2 is none the client shows (known: 1, 3, 4, 5, 6, 7, 8, 9, 16, 17, 19)",
                    "NPC 90000002 talk, script 11 / content 1: talk text 90000002999 has no en row",
                    "NPC 90000002 talk, script 12 is reached from no step: no jump names it and it is no random pick",
                }));
                Assert.That(Texts(p.Warnings, K), Has.Member("NPC 90000002 talk, script 11 / content 1: talk text 90000002999 has no ko row"));
                Assert.That(Texts(p.Warnings, K), Has.Member("NPC 90000002 talk, script 11 / content 1: talk text 90000002999 has no en row"));
            }
        }

        [Test]
        public void ATalkChangedOnOneTargetOnly_IsAWarning()
        {
            using (var f = new FakeTalks())
            {
                var m = NewMod();
                var ed = new TalkEditor(f.Data, m);
                NpcTalk t = ed.SetAttr(ed.Open(90000002), Choice, "voiceID", "x/2", null);
                m.Ops.RemoveAll(o => o.Archive != K);
                t = ed.Open(90000002);
                Assert.That(ed.Problems(t).Exists(x => !x.Shipped && x.Text.StartsWith("script 10 differs from the ", StringComparison.Ordinal)), Is.True);
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(p.Warnings.Count, Is.EqualTo(2), Fake.Dump(p));
                foreach (PlanMessage w in p.Warnings)
                    Assert.That(w.Text, Does.StartWith("NPC 90000002 talk is changed for the ").And.Contains(" has it as shipped"));
            }
        }

        [Test]
        public void AnEditInsideAStepThatDiffers_GoesOnlyToTheSidesLikeTheShownOne()
        {
            using (var f = new FakeTalks())
            {
                var m = NewMod();
                var ed = new TalkEditor(f.Data, m);
                // script 11 has pages 004, 005 everywhere; take page 004 out on the kms client only
                ed.Remove(ed.Open(90000001), new TalkPath("script 11").Then("content", 0), null);
                m.Ops.RemoveAll(o => o.Archive != K);
                NpcTalk t = ed.Open(90000001);
                Assert.That(t.Sides[0].Archive, Is.EqualTo(K));
                Assert.That(TalkEditor.SameOnEverySide(t, "script 11"), Is.False);

                // page 1 is 005 on the kms client (shown) and 004 elsewhere: the edit goes to kms only, the rest are named
                var missed = new List<string>();
                t = ed.SetAttr(t, new TalkPath("script 11").Then("content", 0), "voiceID", "x/1", missed);
                Assert.That(missed.Count, Is.EqualTo(2), string.Join("\n", missed.ToArray()));
                Assert.That(missed[0], Does.StartWith(G + " (its script 11 differs from the " + K + "'s"));
                Assert.That(missed[1], Does.StartWith(S + " (its script 11 differs from the " + K + "'s"));
                foreach (TalkSide s in t.Sides)
                {
                    XmlElem page = new TalkPath("script 11").Then("content", 0).Resolve(s);
                    Assert.That(page.Attr("voiceID"), Is.EqualTo(s.Archive == K ? "x/1" : null), s.Archive);
                }

                // a text edit as well: the text rows are copied for the kms client only
                missed.Clear();
                t = ed.SetText(t, new TalkPath("script 11").Then("content", 0), "en", "x", missed);
                Assert.That(missed.Count, Is.EqualTo(2));
                Assert.That(m.Ops.Exists(o => o.Archive != K), Is.False);
                Assert.That(new TalkPath("script 11").Then("content", 0).Resolve(t.Sides[0]).Attr("text"), Is.EqualTo("$script:90000001008$"));

                // a remove too: page 1 of kms goes, the global client and the server keep both pages
                missed.Clear();
                t = ed.Remove(t, new TalkPath("script 11").Then("content", 0), missed);
                Assert.That(missed.Count, Is.EqualTo(2));
                foreach (TalkSide s in t.Sides)
                    Assert.That(s.Step("script 11").Elem.Children.Count, Is.EqualTo(s.Archive == K ? 0 : 2), s.Archive);

            }
        }

        [Test]
        public void ACellEdit_LandsOnItsOwnNpc_WhenAnotherIsOpenByThen()
        {
            using (var f = new FakeTalks())
            {
                var m = NewMod();
                var ed = new TalkEditor(f.Data, m);
                var q = new TalkEditQueue();
                var e = new TalkCellEdit { NpcId = 90000001, Path = Choice, Language = "en" };
                e.Attrs.Add(new KeyValuePair<string, string>("voiceID", "x/1"));
                q.Post(e);
                // the view opens 90000002, committing the edit typed for 90000001 on the way
                NpcTalk open = ed.Open(90000002);
                string said;
                NpcTalk t = ed.Commit(open, q.Take(), null, out said);
                Assert.That(t.NpcId, Is.EqualTo(90000001));
                Assert.That(said, Is.EqualTo("NPC 90000001, script 10 / content 1 / distractor 1: voiceID = x/1"));
                Assert.That(TalkEditor.Shown(ed.Open(90000001), Choice).Attr("voiceID"), Is.EqualTo("x/1"));
                Assert.That(TalkEditor.Shown(ed.Open(90000002), Choice).Attr("voiceID"), Is.Null);
                Assert.That(new List<long>(ed.ModNpcIds()), Is.EqualTo(new[] { 90000001L }));
                // an <npc id> row of another table is no talk
                var other = new EditOp { Archive = K, Entry = "table/npcdata.xml" };
                other.Rows.Add(new RowOp { Kind = RowOpKind.Add, Xml = "<npc id=\"90000005\" />" });
                other.Rows.Add(new RowOp { Kind = RowOpKind.Delete, At = "npc[id=90000006]" });
                m.Ops.Add(other);
                Assert.That(new List<long>(ed.ModNpcIds()), Is.EqualTo(new[] { 90000001L }));
                Assert.That(ed.NpcIds(), Has.No.Member(90000005L));
                m.Ops.Remove(other);
                // the commit posted when the edit was typed comes after: it finds nothing
                Assert.That(q.Take(), Is.Null);
                Assert.That(ed.Commit(ed.Open(90000002), null, null, out said), Is.Null);
            }
        }

        [Test]
        public void ACellEdit_CommittedBeforeThePlan_IsInThePlan_AndTheSameValueIsNoEdit()
        {
            using (var f = new FakeTalks())
            {
                var m = NewMod();
                var ed = new TalkEditor(f.Data, m);
                var q = new TalkEditQueue();
                var e = new TalkCellEdit { NpcId = 90000001, Path = Choice, Language = "en" };
                e.Attrs.Add(new KeyValuePair<string, string>("goto", "11"));
                q.Post(e);
                string said;
                // the value the place has: nothing goes into the mod
                Assert.That(ed.Commit(ed.Open(90000001), q.Take(), null, out said), Is.Null);
                Assert.That(m.IsEmpty, Is.True);

                e = new TalkCellEdit { NpcId = 90000001, Path = Choice, Language = "en" };
                e.Attrs.Add(new KeyValuePair<string, string>("goto", "11,10"));
                q.Post(e);
                Assert.That(q.Pending, Is.True);
                // Plan and Apply commit the held edit before the mod is read
                ed.Commit(ed.Open(90000001), q.Take(), null, out said);
                Assert.That(q.Pending, Is.False);
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Fake.Dump(p), Does.Contain("npc[id=90000001]"));
                foreach (TalkSide s in ed.Open(90000001).Sides) Assert.That(Choice.Resolve(s).Attr("goto"), Is.EqualTo("11,10"), s.Archive);
            }
        }
    }
}
