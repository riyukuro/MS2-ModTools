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
    /// <summary>A kms client (short notation, LF), a global client and a server (full notation, CRLF) with made-up achievements.</summary>
    public sealed class FakeAchieves : IDisposable
    {
        public readonly string Dir;
        public readonly DataSet Data;
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";
        public const string Both = "achieve/21800001.xml", SrvOnly = "achieve/23800003.xml";

        // 21800001: combat, two grades, a title reward on the second
        public const string KmsBoth = Head + "<ms2>\n" +
            "\t<achieves id=\"21800001\" categoryTag=\"combat\" icon=\"./data/resource/image/fake.png\" account=\"1\">\n" +
            "\t\t<grade value=\"1\">\n\t\t\t<condition type=\"killcount\" value=\"10\" />\n\t\t</grade>\n" +
            "\t\t<grade value=\"2\">\n\t\t\t<condition type=\"killcount\" value=\"50\" />\n\t\t\t<reward type=\"title\" code=\"90001\" />\n\t\t</grade>\n" +
            "\t</achieves>\n</ms2>";

        public const string SrvBoth = Head + "<ms2>\n" +
            "\t<achieves id=\"21800001\" account=\"1\" icon=\"./data/resource/image/fake.png\" noticePercent=\"1\" locking=\"0\" categoryTag=\"combat\" feature=\"\">\n" +
            "\t\t<grade value=\"1\">\n\t\t\t<condition type=\"killcount\" code=\"\" value=\"10\" target=\"\" />\n\t\t\t<reward type=\"\" code=\"0\" value=\"0\" rank=\"1\" />\n\t\t</grade>\n" +
            "\t\t<grade value=\"2\">\n\t\t\t<condition type=\"killcount\" code=\"\" value=\"50\" target=\"\" />\n\t\t\t<reward type=\"title\" code=\"90001\" value=\"0\" rank=\"1\" />\n\t\t</grade>\n" +
            "\t</achieves>\n</ms2>";

        // 23800003: living, only on the server (and the global client)
        public const string SrvLiving = Head + "<ms2>\n" +
            "\t<achieves id=\"23800003\" account=\"0\" icon=\"\" noticePercent=\"1\" locking=\"0\" categoryTag=\"living\" feature=\"\">\n" +
            "\t\t<grade value=\"1\">\n\t\t\t<condition type=\"fish\" code=\"\" value=\"5\" target=\"\" />\n\t\t\t<reward type=\"item\" code=\"39000001\" value=\"1\" rank=\"1\" />\n\t\t</grade>\n" +
            "\t</achieves>\n</ms2>";

        const string Category = Head + "<ms2>\n\t<category categoryTag=\"combat\">\n\t\t<child categoryTag=\"combat,boss\" />\n\t</category>\n" +
            "\t<category categoryTag=\"adventure\" />\n\t<category categoryTag=\"living\" />\n</ms2>\n";
        const string Titles = Head + "<ms2>\n\t<key id=\"90001\" />\n</ms2>\n";

        static KeyValuePair<string, byte[]> Lf(string name, string text) { return new KeyValuePair<string, byte[]>(name, new UTF8Encoding(false).GetBytes(text)); }
        static KeyValuePair<string, byte[]> Cr(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        public FakeAchieves()
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-ach-" + Guid.NewGuid().ToString("N"));
            string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
            foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);
            var kl = new List<KeyValuePair<string, byte[]>>
            {
                Lf(Both, KmsBoth), Lf(AchieveRules.CategoryEntry, Category), Lf(AchieveRules.TitleEntry, Titles),
                Lf("itemdata/fake.xml", Head + "<ms2>\n\t<item id=\"39000001\" />\n</ms2>\n"),
                Lf("string/en/" + AchieveRules.NameFile, Head + "<ms2>\n\t<key id=\"21800001\" name=\"Pebble Breaker\" />\n</ms2>\n"),
                Lf("string/en/" + AchieveRules.DescFile, Head + "<ms2>\n\t<achieve id=\"21800001\" desc=\"Break pebbles.\" complete=\"Broken.\" />\n</ms2>\n"),
            };
            M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, kl);
            foreach (string d in new[] { g, s })
            {
                var l = new List<KeyValuePair<string, byte[]>>
                {
                    Cr(Both, SrvBoth), Cr(SrvOnly, SrvLiving), Cr(AchieveRules.CategoryEntry, Category), Cr(AchieveRules.TitleEntry, Titles),
                    Cr("item/39/39000001.xml", Head + "<ms2>\n\t<item />\n</ms2>\n"),
                    Cr("string/en/" + AchieveRules.NameFile, Head + "<ms2>\n\t<key id=\"21800001\" name=\"Pebble Breaker\" />\n\t<key id=\"23800003\" name=\"Minnow Friend\" />\n</ms2>\n"),
                };
                M2dWriter.Create(Path.Combine(d, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, l);
            }
            Data = new DataSet(k, g, s);
        }

        public static Mod NewMod(string id = "ach.test") { return Mod.Parse("<mod id=\"" + id + "\" name=\"t\" version=\"1\" />", "test mod"); }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class AchievementsTests
    {
        const string K = "client:Xml.m2d", G = "gms-client:Xml.m2d", S = "server:Xml.m2d";

        static int Side(AchievementEditor ed, string archive) { return ed.Sides.FindIndex(x => x.Archive == archive); }

        static string Planned(Plan p, string archive, string entry) { return Fake.Text(p, archive, entry); }

        static string Dump(Plan p) { return Fake.Dump(p); }

        [Test]
        public void ReadsBothNotationsAlike()
        {
            using (var f = new FakeAchieves())
            {
                var ed = new AchievementEditor(f.Data, FakeAchieves.NewMod());
                Assert.That(ed.Sides.Select(x => x.Archive), Is.EqualTo(new[] { K, S }), "the server's files are planned for the global client too");
                Assert.That(ed.All.Select(x => x.Id), Is.EqualTo(new[] { 21800001L, 23800003L }));
                Achievement a = ed.Find(21800001);
                Assert.That(ed.Get(a, Side(ed, K), "account"), Is.EqualTo("1"));
                Assert.That(ed.Get(a, Side(ed, S), "noticePercent"), Is.EqualTo(""), "the default reads as none");
                Assert.That(ed.Grades(a, Side(ed, K)), Is.EqualTo(ed.Grades(a, Side(ed, S))));
                Assert.That(ed.Grades(a)[0][5], Is.EqualTo(""), "an empty reward is no reward");
                Assert.That(ed.Grades(a)[1].Skip(5), Is.EqualTo(new[] { "title", "90001", "", "" }));
                Assert.That(ed.Differs(a), Is.Empty);
                Assert.That(ed.Text(a, "name"), Is.EqualTo("Pebble Breaker"));
                Assert.That(ed.Text(a, "desc"), Is.EqualTo("Break pebbles."));
                Assert.That(ed.Find("minnow").Select(x => x.Id), Is.EqualTo(new[] { 23800003L }));
                Assert.That(ed.Check(a), Is.Empty);
                Assert.That(ed.Check(ed.Find(23800003)), Has.Some.Contains("not in client:Xml.m2d"));
                Assert.That(ed.CategoryTags(), Is.EqualTo(new[] { "adventure", "combat", "combat,boss", "living" }));
            }
        }

        [Test]
        public void EditsFieldsAndGrades_InEachSidesNotation()
        {
            using (var f = new FakeAchieves())
            {
                Mod m = FakeAchieves.NewMod();
                var ed = new AchievementEditor(f.Data, m);
                Achievement a = ed.Find(21800001);
                ed.Set(a, "noticePercent", "50");
                ed.Set(a, "account", "");
                ed.SetGrade(a, 1, "condition@value", "60");
                ed.SetGrade(a, 0, "reward@type", "item");
                ed.SetGrade(a, 0, "reward@code", "39000001");
                ed.SetGrade(a, 1, "reward@type", "");
                Assert.Throws<ModKitException>(() => ed.Set(a, "id", "21800002"));
                Assert.Throws<ModKitException>(() => ed.SetGrade(a, 0, "value", ""));

                Plan p = Planner.Build(f.Data, new[] { m }, new PlanOptions());
                Assert.That(p.HasErrors, Is.False, Dump(p));
                string kms = Planned(p, K, FakeAchieves.Both), srv = Planned(p, S, FakeAchieves.Both), gms = Planned(p, G, FakeAchieves.Both);
                Assert.That(kms, Is.EqualTo(FakeAchieves.KmsBoth
                    .Replace(" account=\"1\">", " noticePercent=\"50\">")
                    .Replace("value=\"50\" />", "value=\"60\" />")
                    .Replace("value=\"10\" />\n", "value=\"10\" />\n\t\t\t<reward type=\"item\" code=\"39000001\" />\n")
                    .Replace("\n\t\t\t<reward type=\"title\" code=\"90001\" />", "")));
                Assert.That(srv, Is.EqualTo(Encoding.UTF8.GetString(Fake.Crlf(FakeAchieves.SrvBoth
                    .Replace("account=\"1\"", "account=\"0\"").Replace("noticePercent=\"1\"", "noticePercent=\"50\"")
                    .Replace("value=\"50\" target", "value=\"60\" target")
                    .Replace("<reward type=\"\" code=\"0\" value=\"0\" rank=\"1\" />", "<reward type=\"item\" code=\"39000001\" value=\"0\" rank=\"1\" />")
                    .Replace("<reward type=\"title\" code=\"90001\" value=\"0\" rank=\"1\" />", "<reward type=\"\" code=\"0\" value=\"0\" rank=\"1\" />")))));
                Assert.That(gms, Is.EqualTo(srv), "the global client gets the server's file");
                Assert.That(ed.Differs(a), Is.Empty);
                Assert.That(ed.Grades(a)[0].Skip(5), Is.EqualTo(new[] { "item", "39000001", "", "" }));
            }
        }

        [Test]
        public void AddsAndRemovesGrades()
        {
            using (var f = new FakeAchieves())
            {
                Mod m = FakeAchieves.NewMod();
                var ed = new AchievementEditor(f.Data, m);
                Achievement a = ed.Find(21800001);
                ed.AddGrade(a);
                foreach (int s in new[] { 0, 1 })
                {
                    List<string[]> g = ed.Grades(a, s);
                    Assert.That(g.Count, Is.EqualTo(3));
                    Assert.That(g[2], Is.EqualTo(new[] { "3", "killcount", "", "50", "", "", "", "", "" }));
                }
                Plan p = Planner.Build(f.Data, new[] { m }, new PlanOptions());
                Assert.That(p.HasErrors, Is.False, Dump(p));
                Assert.That(Planned(p, K, FakeAchieves.Both), Does.Contain("\t\t<grade value=\"3\">\n\t\t\t<condition type=\"killcount\" value=\"50\" />\n\t\t</grade>\n\t</achieves>"));
                Assert.That(Planned(p, S, FakeAchieves.Both), Does.Contain("\t\t<grade value=\"3\">\r\n\t\t\t<condition type=\"killcount\" code=\"\" value=\"50\" target=\"\" />\r\n\t\t\t<reward type=\"\" code=\"0\" value=\"0\" rank=\"1\" />\r\n\t\t</grade>\r\n\t</achieves>"));
                ed.RemoveGrade(a, 2);
                ed.RemoveGrade(a, 0);
                Assert.That(ed.Grades(a).Select(x => x[0]), Is.EqualTo(new[] { "2" }));
                Assert.Throws<ModKitException>(() => ed.RemoveGrade(a, 0));
            }
        }

        [Test]
        public void ClonesIntoTheNewRange_WithTexts_AndSavesAndReloads()
        {
            using (var f = new FakeAchieves())
            {
                Mod m = FakeAchieves.NewMod();
                var ed = new AchievementEditor(f.Data, m);
                Achievement src = ed.Find(23800003);
                Assert.Throws<ModKitException>(() => ed.Clone(src, 23800004), "outside the new range");
                long id = ed.NextId(3);
                Assert.That(id, Is.EqualTo(23900000L));
                Achievement c = ed.Clone(src, id);
                Assert.That(c.IsAdded, Is.True);
                Assert.That(ed.NextId(3), Is.EqualTo(23900001L));
                Assert.That(m.Ids.Any(r => r.Space == AchieveRules.Space && r.From <= id && r.To >= id), Is.True);
                Assert.That(ed.Text(c, "name"), Is.EqualTo("Minnow Friend"));
                Assert.That(ed.Check(c), Is.Empty, string.Join("\n", ed.Check(c)));

                Plan p = Planner.Build(f.Data, new[] { m }, new PlanOptions());
                Assert.That(p.HasErrors, Is.False, Dump(p));
                string entry = AchieveRules.EntryOf(id);
                Assert.That(Planned(p, S, entry), Is.EqualTo(Encoding.UTF8.GetString(Fake.Crlf(FakeAchieves.SrvLiving.Replace("23800003", "23900000")))));
                Assert.That(Planned(p, G, entry), Is.EqualTo(Planned(p, S, entry)));
                Assert.That(Planned(p, K, entry), Is.EqualTo(
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ms2>\n\t<achieves id=\"23900000\" categoryTag=\"living\">\n" +
                    "\t\t<grade value=\"1\">\n\t\t\t<condition type=\"fish\" value=\"5\" />\n\t\t\t<reward type=\"item\" code=\"39000001\" value=\"1\" />\n\t\t</grade>\n" +
                    "\t</achieves>\n</ms2>"), "the kms client gets the file in its own notation");
                Assert.That(Planned(p, K, "string/en/" + AchieveRules.NameFile), Does.Contain("<key id=\"23900000\" name=\"Minnow Friend\" />"));
                Assert.That(Planned(p, S, "string/en/" + AchieveRules.NameFile), Does.Contain("<key id=\"23900000\" name=\"Minnow Friend\" />"));

                // saved and loaded again, the mod reads the same
                string dir = Path.Combine(f.Dir, "mods", "ach.test");
                m.Save(dir);
                Mod back = Mod.Load(dir);
                var ed2 = new AchievementEditor(f.Data, back);
                Achievement c2 = ed2.Find(id);
                Assert.That(c2, Is.Not.Null);
                Assert.That(c2.IsAdded, Is.True);
                Assert.That(ed2.Grades(c2), Is.EqualTo(ed.Grades(c)));
                Assert.That(ed2.Text(c2, "name"), Is.EqualTo("Minnow Friend"));
                Plan p2 = Planner.Build(f.Data, new[] { back }, new PlanOptions());
                Assert.That(Planned(p2, K, entry), Is.EqualTo(Planned(p, K, entry)));
                Assert.That(Planned(p2, S, entry), Is.EqualTo(Planned(p, S, entry)));

                // another enabled mod with the id: the next id skips it, a clone into it is refused
                Mod owner = CloneOwner(f, id + 1);
                ed2.Others = () => new[] { owner };
                Assert.That(ed2.NextId(3), Is.EqualTo(id + 2));
                Assert.Throws<ModKitException>(() => ed2.Clone(src, id + 1));

                // removing the new one takes it and its texts out
                ed2.Remove(c2);
                Assert.That(ed2.Find(id), Is.Null);
                Assert.That(back.IsEmpty, Is.True, back.ToXml());
            }
        }

        static Mod CloneOwner(FakeAchieves f, long id)
        {
            Mod o = FakeAchieves.NewMod("ach.other");
            var ed = new AchievementEditor(f.Data, o);
            ed.Clone(ed.Find(21800001), 21900000);
            IdSpaces.Reserve(o, AchieveRules.Space, id);
            return o;
        }

        [Test]
        public void RemovesRestoresAndReverts_AShippedOne()
        {
            using (var f = new FakeAchieves())
            {
                Mod m = FakeAchieves.NewMod();
                var ed = new AchievementEditor(f.Data, m);
                Achievement a = ed.Find(21800001);
                ed.Set(a, "icon", "./data/resource/image/other.png");
                ed.SetText(a, "name", "Rock Breaker");
                ed.Remove(a);
                Assert.That(ed.IsDeleted(a), Is.True);
                Assert.Throws<ModKitException>(() => ed.Set(a, "icon", ""));
                Plan p = Planner.Build(f.Data, new[] { m }, new PlanOptions());
                Assert.That(p.HasErrors, Is.False, Dump(p));
                Assert.That(p.Changes.Count(x => x.Kind == ChangeKind.DeleteEntry && x.Entry == FakeAchieves.Both), Is.EqualTo(3), Dump(p));

                ed.Restore(a);
                Assert.That(ed.IsDeleted(a), Is.False);
                Assert.That(ed.Get(a, "icon"), Is.EqualTo("./data/resource/image/fake.png"), "restored as shipped");
                Assert.That(ed.Text(a, "name"), Is.EqualTo("Rock Breaker"));
                ed.Set(a, "locking", "1");
                ed.Revert(a);
                Assert.That(ed.IsChanged(a), Is.False);
                Assert.That(ed.Text(a, "name"), Is.EqualTo("Pebble Breaker"));
                Assert.That(Planner.Build(f.Data, new[] { m }, new PlanOptions()).Changes, Is.Empty);
            }
        }

        [Test]
        public void Revert_AfterAReload_TakesEverySidesTexts()
        {
            using (var f = new FakeAchieves())
            {
                Mod m = FakeAchieves.NewMod();
                var ed = new AchievementEditor(f.Data, m);
                Achievement a = ed.Find(21800001);
                ed.SetText(a, "name", "Rock Breaker");
                string dir = Path.Combine(f.Dir, "mods", "ach.test");
                m.Save(dir);
                Mod back = Mod.Load(dir);
                var ed2 = new AchievementEditor(f.Data, back);
                Achievement a2 = ed2.Find(21800001);
                Assert.That(ed2.Text(a2, "name"), Is.EqualTo("Rock Breaker"), "reads the first side only");
                ed2.Revert(a2);
                Plan p = Planner.Build(f.Data, new[] { back }, new PlanOptions());
                Assert.That(p.Changes, Is.Empty, Dump(p));
            }
        }

        [Test]
        public void AnEditTheSidesCannotAllTake_LeavesEverySideAsItWas()
        {
            using (var f = new FakeAchieves())
            {
                Mod m = FakeAchieves.NewMod();
                // the mod's server file has one grade, the clients two
                string one = FakeAchieves.SrvBoth.Substring(0, FakeAchieves.SrvBoth.IndexOf("\t\t<grade value=\"2\">", StringComparison.Ordinal)) + "\t</achieves>\n</ms2>";
                m.Files["srv-one.xml"] = Fake.Crlf(one);
                m.Ops.Add(new EntryOp { Archive = S, Name = FakeAchieves.Both, File = "srv-one.xml" });
                var ed = new AchievementEditor(f.Data, m);
                Achievement a = ed.Find(21800001);
                Assert.That(ed.Grades(a, Side(ed, S)).Count, Is.EqualTo(1));
                Assert.That(ed.Grades(a, Side(ed, K)).Count, Is.EqualTo(2));
                string before = m.ToXml();
                Assert.Throws<ModKitException>(() => ed.SetGrade(a, 1, "condition@value", "70"));
                Assert.Throws<ModKitException>(() => ed.RemoveGrade(a, 1));
                Assert.That(m.ToXml(), Is.EqualTo(before), "no side is written");
                Assert.That(ed.Grades(a, Side(ed, K))[1][3], Is.EqualTo("50"));
                ed.SetGrade(a, 0, "condition@value", "20");
                Assert.That(ed.Grades(a, Side(ed, K))[0][3], Is.EqualTo("20"));
                Assert.That(ed.Grades(a, Side(ed, S))[0][3], Is.EqualTo("20"));
            }
        }

        [Test]
        public void RulesFollowTheClientsLoad()
        {
            Func<string, bool, List<string>[]> check = (grades, server) =>
            {
                var e = new List<string>();
                var w = new List<string>();
                var doc = MS2.ModKit.Xml.XmlDoc.Parse(Encoding.UTF8.GetBytes("<ms2><achieves id=\"21800001\" categoryTag=\"combat\">" + grades + "</achieves></ms2>"), "t");
                AchieveRules.CheckFile(doc, 21800001, server, new HashSet<string> { "combat" }, e, w);
                return new[] { e, w };
            };
            // the lowest grade counts, not the first in the file
            List<string>[] r = check("<grade value=\"2\"><condition type=\"killcount\" value=\"1\" /></grade><grade value=\"1\"><condition type=\"killcount\" value=\"1\" /></grade>", false);
            Assert.That(r[1], Has.None.Contains("lowest grade"));
            r = check("<grade value=\"1\"><condition type=\"killcount\" value=\"1\" /></grade><grade value=\"0\"><condition type=\"killcount\" value=\"1\" /></grade>", false);
            Assert.That(r[1], Has.Some.EqualTo("the lowest grade is 0, not 1: the client logs it"));
            // the client knows openItemBox and openStoryBook (any case), the server does not
            r = check("<grade value=\"1\"><condition type=\"openitembox\" value=\"1\" /></grade>", false);
            Assert.That(r[0], Is.Empty, string.Join("\n", r[0]));
            Assert.That(AchieveRules.KnownCondition("openStoryBook", false), Is.True);
            Assert.That(AchieveRules.KnownCondition("openItemBox", true), Is.False);
            // a user_find target must be a dictionary of the checker's keys
            Assert.That(AchieveRules.ValidateCondition("user_find", "dict:{jobId:10,Level:20}"), Is.Null);
            Assert.That(AchieveRules.ValidateCondition("killcount", ""), Is.Null);
            Assert.That(AchieveRules.ValidateCondition("user_find", ""), Does.Contain("not a dictionary"));
            Assert.That(AchieveRules.ValidateCondition("user_find", "10,20"), Does.Contain("not a dictionary"));
            Assert.That(AchieveRules.ValidateCondition("user_find", "dict:{jobId:10,hat:1}"), Does.Contain("\"hat\""));
            r = check("<grade value=\"1\"><condition type=\"user_find\" value=\"1\" target=\"dict:{hat:1}\" /></grade>", false);
            Assert.That(r[0], Has.Some.Contains("the client drops the achievement"));
            r = check("<grade value=\"1\"><condition type=\"user_find\" value=\"1\" target=\"dict:{hat:1}\" /></grade>", true);
            Assert.That(r[0], Is.Empty, "the server does not run the client's check");
        }

        static List<string> Lines(IEnumerable<PlanMessage> msgs) { return msgs.Select(x => x.ToString()).ToList(); }

        [Test]
        public void ThePlanChecks_Conditions_Rewards_Grades_AndSides()
        {
            using (var f = new FakeAchieves())
            {
                Mod m = FakeAchieves.NewMod();
                var ed = new AchievementEditor(f.Data, m);
                Achievement a = ed.Find(21800001);
                ed.SetGrade(a, 0, "condition@type", "pebble_count");
                ed.SetGrade(a, 0, "reward@type", "item");
                ed.SetGrade(a, 0, "reward@code", "39000002");
                ed.SetGrade(a, 1, "reward@code", "90002");
                ed.SetGrade(a, 1, "value", "1");
                Plan p = Planner.Build(f.Data, new[] { m }, new PlanOptions());
                List<string> errors = Lines(p.Errors), warnings = Lines(p.Warnings);
                Assert.That(errors, Has.Some.Contains("server:Xml.m2d").And.Contains("achievement 21800001: grade 1: the condition type \"pebble_count\" is not one the server reads"), Dump(p));
                Assert.That(errors, Has.Some.Contains("client:Xml.m2d").And.Contains("\"pebble_count\" is not one the client reads"), Dump(p));
                Assert.That(errors, Has.Some.Contains("the reward item 39000002 is not an item of the server"), Dump(p));
                Assert.That(warnings, Has.Some.Contains("the reward item 39000002 is not an item of the"), Dump(p));
                Assert.That(warnings, Has.Some.Contains("the reward title 90002 is not in table/titletag.xml"), Dump(p));
                Assert.That(errors, Has.Some.Contains("grade 1 is there twice: the server's data ingest fails"), Dump(p));
                Assert.That(warnings, Has.Some.Contains("grade 1 is there twice: the client keeps the later one"), Dump(p));
                Assert.That(Lines(p.Errors.Concat(p.Warnings)).Count(x => x.Contains("gms-client")), Is.EqualTo(0), "the mirrored file says it once: " + Dump(p));

                ed.Revert(a);
                ed.SetGrade(a, 1, "value", "0");
                p = Planner.Build(f.Data, new[] { m }, new PlanOptions());
                Assert.That(Lines(p.Warnings), Has.Some.Contains("the grade values do not rise"), Dump(p));
                Assert.That(p.HasErrors, Is.False, Dump(p));

                // only on one side: a kms-only clone and a server-only delete
                ed.Revert(a);
                Mod one = FakeAchieves.NewMod("ach.one");
                one.Ops.Add(new EntryOp { Archive = S, Name = FakeAchieves.Both, Delete = true });
                one.Ops.Add(new EntryOp { Archive = G, Name = FakeAchieves.Both, Delete = true });
                p = Planner.Build(f.Data, new[] { one }, new PlanOptions());
                Assert.That(Lines(p.Warnings), Has.Some.Contains("achievement 21800001 is in the").And.Contains("but not on the server: the client shows it, the server never grants it"), Dump(p));
                one.Ops.Clear();
                one.Ops.Add(new EntryOp { Archive = K, Name = FakeAchieves.Both, Delete = true });
                p = Planner.Build(f.Data, new[] { one }, new PlanOptions());
                Assert.That(Lines(p.Warnings), Has.Some.Contains("achievement 21800001 is on the server but not in the").And.Contains("the client never shows it"), Dump(p));

                // a problem the shipped file has already is not said
                Assert.That(Lines(Planner.Build(f.Data, new[] { FakeAchieves.NewMod("ach.none") }, new PlanOptions()).Warnings), Has.None.Contains("achievement"));
            }
        }

        [Test]
        public void RulesCheckAFile()
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var doc = MS2.ModKit.Xml.XmlDoc.Parse(Encoding.UTF8.GetBytes("<ms2><achieves id=\"21800009\" categoryTag=\"living\"><grade value=\"2\"><condition type=\"killcount\" value=\"x\" /><reward type=\"item\" /></grade></achieves></ms2>"), "t");
            AchieveRules.CheckFile(doc, 21800001, true, new HashSet<string> { "combat", "living" }, errors, warnings);
            Assert.That(errors, Is.EqualTo(new[] { "grade 2: the condition value \"x\" is not a number" }));
            Assert.That(warnings, Is.EqualTo(new[]
            {
                "id 21800009 is not the file's id 21800001: the client reads the attribute, the server the file name",
                "categoryTag \"living\" is not of the category combat that the id's second digit 1 gives (the client counts it there)",
                "grade 2: the item reward has no code",
                "the lowest grade is 2, not 1: the client logs it",
            }));
            Assert.That(AchieveRules.KnownCondition("item_collect_script_function", false), Is.True);
            Assert.That(AchieveRules.KnownCondition("item_collect_script_function", true), Is.False);
            Assert.That(AchieveRules.InNewRange(22900000), Is.True);
            Assert.That(AchieveRules.InNewRange(22800000), Is.False);
        }
    }
}
