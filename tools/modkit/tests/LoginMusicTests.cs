// LoginMusic on a made-up table/loginbgbgm.xml (fake backgrounds, fake cue ids) in a made-up kms client.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    sealed class FakeLogin : IDisposable
    {
        public readonly string Dir, Client;
        public readonly DataSet Data;

        // BgA: months 1..3, cue 9; BgB: month 2 at priority 1, two cues and one param; BgC: months 4, 5
        public const string Table = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ms2>\n" +
            "\t<login clientPropertyBgDay=\"BgA\" month=\"1,2,3\" bgmID=\"9\" bgmParam=\"5\" priority=\"0\" />\n" +
            "\t<login clientPropertyBgDay=\"BgB\" month=\"2\" bgmID=\"30,31\" bgmParam=\"1\" priority=\"1\" />\n" +
            "\t<login clientPropertyBgDay=\"BgC\" month=\"4,5\" bgmID=\"9\" bgmParam=\"5\" />\n" +
            "</ms2>\n";

        public FakeLogin(bool table = true, bool project = true)
        {
            Assume.That(Environment.GetEnvironmentVariable(SoundData.Override), Is.Null.Or.Empty, SoundData.Override + " points the plan at another Sound folder");
            Dir = Path.Combine(Path.GetTempPath(), "modkit-login-" + Guid.NewGuid().ToString("N"));
            Client = Path.Combine(Dir, "kms");
            Directory.CreateDirectory(Client);
            var entries = new List<KeyValuePair<string, byte[]>> { new KeyValuePair<string, byte[]>("table/other.xml", Encoding.UTF8.GetBytes("<ms2 />\n")) };
            if (table) entries.Add(new KeyValuePair<string, byte[]>(LoginMusic.Entry, Fake.Crlf(Table)));
            M2dWriter.Create(Path.Combine(Client, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, entries);
            if (project)
            {
                string s = Path.Combine(Client, "Sound");
                Directory.CreateDirectory(s);
                File.WriteAllBytes(Path.Combine(s, SoundData.ProjectFile), FakeSound.Project(FakeSound.C(9, "Nine_Fake"), FakeSound.C(30, "Thirty_Fake"), FakeSound.C(31, "ThirtyOne_Fake")));
            }
            Data = new DataSet(Client, null, null);
        }

        public static Mod NewMod() { return Mod.Parse("<mod id=\"login.test\" name=\"t\" version=\"1\" />", "test mod"); }

        public static Mod ModOf(string lines)
        {
            return Mod.Parse("<mod id=\"login.hand\" name=\"t\" version=\"1\">\n<edit archive=\"client:Xml.m2d\" entry=\"table/loginbgbgm.xml\">\n" + lines + "</edit>\n</mod>\n", "hand-written mod");
        }

        public Plan Plan(Mod m) { return Planner.Build(Data, new[] { Mod.Parse(m.ToXml(), "round trip") }, new PlanOptions()); }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class LoginMusicTests
    {
        static List<string> Texts(List<PlanMessage> l) { return l.Select(m => m.Text).ToList(); }

        [Test]
        public void Rows_AreTheShippedOnes()
        {
            using (var f = new FakeLogin())
            {
                var lm = new LoginMusic(f.Data, FakeLogin.NewMod());
                Assert.That(lm.Exists, Is.True);
                Assert.That(lm.Rows().Select(r => r.At), Is.EqualTo(new[] { "login[clientPropertyBgDay=BgA]", "login[clientPropertyBgDay=BgB]", "login[clientPropertyBgDay=BgC]" }));
                LoginRow c = lm.Find("BgC");
                Assert.That(lm.Get(c, LoginMusic.Priority), Is.EqualTo(""), "no priority attribute");
                Assert.That(lm.Get(lm.Find("BgB"), LoginMusic.Bgm), Is.EqualTo("30,31"));
                Assert.That(lm.Find("bga"), Is.Null, "backgrounds are matched as the client reads them");
                Assert.That(lm.AnyEdited(), Is.False);
            }
            using (var f = new FakeLogin(table: false))
                Assert.That(new LoginMusic(f.Data, FakeLogin.NewMod()).Exists, Is.False, "a client without the table (as the global one)");
        }

        [Test]
        public void Set_WritesOneChangeLine_TheShippedValueTakesItOut()
        {
            using (var f = new FakeLogin())
            {
                Mod m = FakeLogin.NewMod();
                var lm = new LoginMusic(f.Data, m);
                LoginRow a = lm.Find("BgA");
                Assert.That(lm.Set(a, LoginMusic.Bgm, " 30, 9 "), Is.True);
                Assert.That(lm.Get(a, LoginMusic.Bgm), Is.EqualTo("30,9"), "spaces out");
                Assert.That(lm.Set(a, LoginMusic.Bgm, "30,9"), Is.False, "no change");
                Assert.That(lm.Set(a, LoginMusic.Priority, ""), Is.True);
                Assert.That(lm.IsEdited(a), Is.True);
                Assert.That(lm.IsEdited(lm.Find("BgB")), Is.False);
                string xml = m.ToXml();
                Assert.That(xml, Does.Contain("<edit archive=\"client:Xml.m2d\" entry=\"table/loginbgbgm.xml\">"));
                Assert.That(xml, Does.Contain("<change at=\"login[clientPropertyBgDay=BgA]\">"));
                Assert.That(xml, Does.Contain("<set name=\"bgmID\" value=\"30,9\" />"));
                Assert.That(xml, Does.Contain("<unset name=\"priority\" />"));

                // read back from the saved text
                var back = new LoginMusic(f.Data, Mod.Parse(xml, "again"));
                Assert.That(back.Get(back.Find("BgA"), LoginMusic.Bgm), Is.EqualTo("30,9"));
                Assert.That(back.Get(back.Find("BgA"), LoginMusic.Priority), Is.EqualTo(""));

                // refused values change nothing
                foreach (string[] c in new[]
                {
                    new[] { LoginMusic.Month, "13", "not 1..12" }, new[] { LoginMusic.Month, "", "the client skips the row" },
                    new[] { LoginMusic.Month, "1;2", "not a list of month numbers" }, new[] { LoginMusic.Bgm, "", "the client skips the row" },
                    new[] { LoginMusic.Bgm, "x", "not a list of cue ids" }, new[] { LoginMusic.Param, "-1", "not a list of numbers" },
                    new[] { LoginMusic.Priority, "1,2", "not a number" },
                })
                    Assert.That(() => lm.Set(a, c[0], c[1]), Throws.TypeOf<ModKitException>().With.Message.Contains(c[2]), c[0] + "=" + c[1]);
                Assert.That(() => lm.Set(a, "bogus", "1"), Throws.TypeOf<ModKitException>());
                Assert.That(m.ToXml(), Is.EqualTo(xml));

                // the shipped values take the lines out, and the edit with the last one
                Assert.That(lm.Set(a, LoginMusic.Bgm, "9"), Is.True);
                Assert.That(lm.Set(a, LoginMusic.Priority, "0"), Is.True);
                Assert.That(lm.AnyEdited(), Is.False);
                Assert.That(m.Ops, Is.Empty);
            }
        }

        [Test]
        public void TakeAndUseMapMusic()
        {
            using (var f = new FakeLogin())
            {
                Mod m = FakeLogin.NewMod();
                var lm = new LoginMusic(f.Data, m);
                Assert.That(lm.UseMapMusic(), Is.EqualTo(3));
                Assert.That(lm.UseMapMusic(), Is.EqualTo(0));
                for (int mo = 1; mo <= 5; mo++) Assert.That(lm.CuesIn(mo), Is.EqualTo(new uint[] { 0 }), "month " + mo + ": the map's own music");
                Assert.That(lm.Take(lm.Find("BgB")), Is.True);
                Assert.That(lm.Take(lm.Find("BgB")), Is.False);
                Assert.That(lm.CuesIn(2), Is.EqualTo(new uint[] { 30, 31 }));
                Assert.That(lm.TakeAll(), Is.EqualTo(2));
                Assert.That(m.Ops, Is.Empty);
            }
        }

        [Test]
        public void PickedIn_IsTheClientsPick()
        {
            using (var f = new FakeLogin())
            {
                var lm = new LoginMusic(f.Data, FakeLogin.NewMod());
                Assert.That(lm.PickedIn(1).Select(r => r.Background), Is.EqualTo(new[] { "BgA" }));
                Assert.That(lm.PickedIn(2).Select(r => r.Background), Is.EqualTo(new[] { "BgB" }), "priority 1 over 0");
                Assert.That(lm.CuesIn(2), Is.EqualTo(new uint[] { 30, 31 }));
                Assert.That(lm.PickedIn(6), Is.Empty);
                Assert.That(lm.CuesIn(6), Is.Empty);
                lm.Set(lm.Find("BgB"), LoginMusic.Priority, "");
                Assert.That(lm.PickedIn(2).Select(r => r.Background), Is.EqualTo(new[] { "BgA", "BgB" }), "no priority is 0");
                Assert.That(lm.CuesIn(2), Is.EqualTo(new uint[] { 9, 30, 31 }));
                lm.Set(lm.Find("BgC"), LoginMusic.Month, "4,6");
                Assert.That(lm.PickedIn(5), Is.Empty);
                Assert.That(lm.PickedIn(6).Select(r => r.Background), Is.EqualTo(new[] { "BgC" }));
            }
        }

        [Test]
        public void Pairs_Problem_Note()
        {
            Assert.That(LoginMusic.Pairs("30,31", "1").Select(p => p.Cue + "/" + p.Param), Is.EqualTo(new[] { "30/1", "31/5" }), "a missing param is 5");
            Assert.That(LoginMusic.Pairs("9", "").Select(p => p.Cue + "/" + p.Param), Is.EqualTo(new[] { "9/5" }));
            Assert.That(LoginMusic.Pairs("x", "1"), Is.Empty);
            Assert.That(LoginMusic.List(""), Is.Empty);
            Assert.That(LoginMusic.List("1, 2"), Is.EqualTo(new uint[] { 1, 2 }));
            Assert.That(LoginMusic.List("1,,2"), Is.Null);
            Assert.That(LoginMusic.List("+1"), Is.Null);
            Assert.That(LoginMusic.Problem(LoginMusic.Month, "12"), Is.Null);
            Assert.That(LoginMusic.Problem(LoginMusic.Month, "0"), Does.Contain("not 1..12"));
            Assert.That(LoginMusic.Problem(LoginMusic.Bgm, "0"), Is.Null);
            Assert.That(LoginMusic.Problem(LoginMusic.Param, ""), Is.Null);
            Assert.That(LoginMusic.Problem(LoginMusic.Priority, ""), Is.Null);
            Assert.That(LoginMusic.Note("30,31", "1"), Does.Contain("the client gives the others 5"));
            Assert.That(LoginMusic.Note("30", "0,0"), Does.Contain("ignores the extra ones"), "as the shipped 213214 row with 0,0");
            Assert.That(LoginMusic.Note("30,31", "1,1"), Is.Null);
        }

        [Test]
        public void HandWrittenSelector_IsTheRow()
        {
            using (var f = new FakeLogin())
            {
                Mod m = FakeLogin.ModOf("<change at=\"login[ clientPropertyBgDay ='BgA']\"><set name=\"bgmID\" value=\"31\" /></change>\n");
                var lm = new LoginMusic(f.Data, m);
                LoginRow a = lm.Find("BgA");
                Assert.That(lm.Get(a, LoginMusic.Bgm), Is.EqualTo("31"));
                Assert.That(lm.IsEdited(a), Is.True);
                Assert.That(lm.Set(a, LoginMusic.Bgm, "30"), Is.True);
                Assert.That(m.ToXml().Split(new[] { "<change " }, StringSplitOptions.None).Length - 1, Is.EqualTo(1), "the hand-written line is edited, no second one");
                Plan p = f.Plan(m);
                Assert.That(p.Errors, Is.Empty, EditorSession.Report(p));
                Assert.That(Encoding.UTF8.GetString(p.Archives.Single().Put.Single(kv => kv.Key == LoginMusic.Entry).Value), Does.Contain("clientPropertyBgDay=\"BgA\" month=\"1,2,3\" bgmID=\"30\""), "the planner takes the line for the row too");
                Assert.That(lm.Take(a), Is.True);
                Assert.That(m.Ops, Is.Empty);
            }
        }

        [Test]
        public void Plan_WritesTheTable_AndChecksIt()
        {
            using (var f = new FakeLogin())
            {
                Mod m = FakeLogin.NewMod();
                var lm = new LoginMusic(f.Data, m);
                lm.Set(lm.Find("BgA"), LoginMusic.Bgm, "31,9");
                lm.Set(lm.Find("BgA"), LoginMusic.Param, "1,1");
                Plan p = f.Plan(m);
                Assert.That(p.Errors, Is.Empty, EditorSession.Report(p));
                Assert.That(p.Warnings, Is.Empty, EditorSession.Report(p));
                PlanArchive pa = p.Archives.Single(a => a.Archive == "client:Xml.m2d");
                string text = Encoding.UTF8.GetString(pa.Put.Single(kv => kv.Key == LoginMusic.Entry).Value);
                Assert.That(text, Does.Contain("clientPropertyBgDay=\"BgA\" month=\"1,2,3\" bgmID=\"31,9\" bgmParam=\"1,1\""));
                Assert.That(text, Does.Contain("clientPropertyBgDay=\"BgB\" month=\"2\" bgmID=\"30,31\""), "the other rows as shipped");

                // the cues of the project, the count of params, a month left without a row
                lm.Set(lm.Find("BgA"), LoginMusic.Bgm, "77,0");
                lm.Set(lm.Find("BgC"), LoginMusic.Month, "6");
                lm.Set(lm.Find("BgC"), LoginMusic.Param, "");
                p = f.Plan(m);
                Assert.That(p.Errors, Is.Empty, EditorSession.Report(p));
                Assert.That(Texts(p.Warnings), Has.Some.Contains("login row BgA: cue 77 is not in MS2Sound.fev"));
                Assert.That(Texts(p.Warnings), Has.None.Contains("cue 0 "), "0 is the map's own music");
                Assert.That(Texts(p.Warnings), Has.Some.Contains("month 4 has no login row"));
                Assert.That(Texts(p.Warnings), Has.Some.Contains("month 5 has no login row"));
                Assert.That(Texts(p.Warnings).Count(t => t.Contains("bgmParam")), Is.EqualTo(1), "BgC: 1 cue, 0 params is the client's default, a note");
            }
        }

        [Test]
        public void Plan_HandWrittenLines_AreReadAsTheClientReadsThem()
        {
            using (var f = new FakeLogin())
            {
                var cases = new[]
                {
                    new[] { "<change at=\"login[clientPropertyBgDay=BgA]\"><set name=\"month\" value=\"1;2\" /></change>", "E", "not a list of month numbers" },
                    new[] { "<change at=\"login[clientPropertyBgDay=BgA]\"><set name=\"month\" value=\"13\" /></change>", "E", "month 13 is not 1..12" },
                    new[] { "<change at=\"login[clientPropertyBgDay=BgA]\"><set name=\"priority\" value=\"high\" /></change>", "E", "priority \"high\" is not a number" },
                    new[] { "<change at=\"login[clientPropertyBgDay=BgA]\"><unset name=\"bgmID\" /></change>", "W", "bgmID is empty: the client skips the row" },
                    new[] { "<change at=\"login[clientPropertyBgDay=BgA]\"><unset name=\"month\" /></change>", "W", "month 1 has no login row" },
                    new[] { "<change at=\"login[clientPropertyBgDay=BgA]\"><unset name=\"clientPropertyBgDay\" /></change>", "W", "the client skips a row with no clientPropertyBgDay" },
                };
                foreach (string[] c in cases)
                {
                    Plan p = f.Plan(FakeLogin.ModOf(c[0] + "\n"));
                    Assert.That(Texts(c[1] == "E" ? p.Errors : p.Warnings), Has.Some.Contains(c[2]), c[0] + "\n" + EditorSession.Report(p));
                    if (c[1] == "W") Assert.That(p.Errors, Is.Empty, c[0] + "\n" + EditorSession.Report(p));
                }
            }
        }

        [Test]
        public void Plan_NoProject_NoCueCheck()
        {
            using (var f = new FakeLogin(project: false))
            {
                Plan p = f.Plan(FakeLogin.ModOf("<change at=\"login[clientPropertyBgDay=BgA]\"><set name=\"bgmID\" value=\"77\" /></change>\n"));
                Assert.That(p.Errors, Is.Empty, EditorSession.Report(p));
                Assert.That(Texts(p.Warnings), Has.None.Contains("cue 77"));
            }
        }
    }
}
