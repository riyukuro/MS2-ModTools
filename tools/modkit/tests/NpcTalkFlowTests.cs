// The flow of a talk, the NPCs the mod has a talk for and the problems of many NPCs (NpcTalkFlow.cs), on the
// made-up talks of NpcTalkTests. Every id here is made up.
using System.Collections.Generic;
using MS2.ModKit;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class NpcTalkFlowTests
    {
        const string K = "client:Xml.m2d", G = "gms-client:Xml.m2d";
        static readonly TalkPath Choice = new TalkPath("script 10").Then("content", 0).Then("distractor", 0);

        static Mod NewMod() { return Mod.Parse("<mod id=\"flow.test\" name=\"t\" version=\"1\">\n</mod>", "test mod"); }

        static List<string> Lines(TalkSide s) { return TalkFlow.Of(s.Root).ConvertAll(x => x.ToString()); }

        [Test]
        public void TheFlow_StartsAtTheEntrySteps_AndFollowsTheJumps()
        {
            using (var f = new FakeTalks())
            {
                var ed = new TalkEditor(f.Data, NewMod());
                NpcTalk t = ed.Open(90000001);
                Assert.That(Lines(t.Side(K)), Is.EqualTo(new[]
                {
                    "select 0",
                    "script 10",
                    "  goto at content 1 / distractor 1 -> script 11",
                    "monologue 30",
                }));
            }
        }

        [Test]
        public void ADanglingJump_ALoopAndAnUnreachableStep_AreMarked()
        {
            using (var f = new FakeTalks())
            {
                var ed = new TalkEditor(f.Data, NewMod());
                NpcTalk t = ed.SetAttr(ed.Open(90000002), Choice, "goto", "11,99", null);
                t = ed.SetAttr(t, new TalkPath("script 11").Then("content", 1), "gotoFail", "10", null);
                t = ed.AddStep(t, "script", "12", null);
                List<TalkFlowLine> l = TalkFlow.Of(t.Side(G).Root);
                Assert.That(l.ConvertAll(x => x.ToString()), Is.EqualTo(new[]
                {
                    "select 0",
                    "script 10",
                    "  goto at content 1 / distractor 1 -> script 11",
                    "    gotoFail at content 2 -> script 10 (again)",
                    "  goto at content 1 / distractor 1 -> step 99 (no such step)",
                    "script 12 (reached from no step)",
                }));
                Assert.That(l[4].Dangling && l[4].Key == null && l[4].Target == "99", Is.True);
                Assert.That(l[5].Unreachable && l[5].Key == "script 12", Is.True);
            }
        }

        [Test]
        public void ModNpcIds_AreTheNpcsTheModChangesOnAnyTarget()
        {
            using (var f = new FakeTalks())
            {
                var m = NewMod();
                var ed = new TalkEditor(f.Data, m);
                Assert.That(ed.ModNpcIds(), Is.Empty);
                ed.SetAttr(ed.Open(90000002), Choice, "voiceID", "x/2", null);
                Assert.That(ed.ModNpcIds(), Is.EquivalentTo(new[] { 90000002L }));
                m.Ops.RemoveAll(o => o.Archive != K);
                Assert.That(ed.ModNpcIds(), Is.EquivalentTo(new[] { 90000002L }), "the combined side alone");
            }
        }

        [Test]
        public void ProblemsWithCachedTexts_AreTheProblems_AndTheFirstLineAndSidesAreRead()
        {
            using (var f = new FakeTalks())
            {
                var ed = new TalkEditor(f.Data, NewMod());
                NpcTalk t = ed.SetAttr(ed.Open(90000002), Choice, "goto", "99", null);
                var cache = new Dictionary<string, Dictionary<string, HashSet<long>>>();
                Assert.That(ed.Problems(t, cache).ConvertAll(p => p.Archive + " " + p), Is.EqualTo(ed.Problems(t).ConvertAll(p => p.Archive + " " + p)));
                Assert.That(cache.Count, Is.EqualTo(3));
                Assert.That(ed.FirstLine(90000002, t.Side(G).Step("script 11"), "en"), Is.EqualTo("en2-4"));
                Assert.That(TalkEditor.SameOnEverySide(t, "script 10"), Is.True);
                Assert.That(TalkEditor.SameOnEverySide(t, "script 11"), Is.True);
                Assert.That(ed.FirstLine(90000003, ed.Open(90000003).Side(K).Step("select 0"), "ko"), Is.EqualTo("$script:90000003001$"), "no row: the reference");
            }
        }
    }
}
