// Mastery recipes (Recipes.cs, PlannerRecipes.cs) on made-up tables: a kms client, a global client and a server.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    public sealed class FakeRecipes : IDisposable
    {
        public readonly string Dir;
        public readonly DataSet Data;
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        static string Row(string id, int type, string mastery, string materials, string results, string more)
        {
            return "\t<receipe id=\"" + id + "\" masteryType=\"" + type + "\" exceptRewardExp=\"0\" requireMastery=\"" + mastery + "\" requireMeso=\"\" requireQuest=\"\" rewardExp=\"\" rewardMastery=\"1\" gatheringTime=\"\" " +
                "highPropLimitCount=\"\" normalPropLimitCount=\"\" requireItem1=\"" + materials + "\" requireItem2=\"\" requireItem3=\"\" requireItem4=\"\" requireItem5=\"\" habitatMapId=\"\" " +
                "rewardItem1=\"" + results + "\" rewardItem2=\"\" rewardItem3=\"\" rewardItem4=\"\" rewardItem5=\"\"" + more + " />\n";
        }

        public static string Recipes(bool server)
        {
            return Head + "<ms2>\n" +
                Row("10000001", 7, "0", "5001,1,2", "5003,1,1", " feature=\"F\"") +
                Row("10000001", 7, "0", "5001,1,3", "5003,1,1", " feature=\"G\"") +
                Row("30000001", 3, "100", "", "5002,1,1", " feature=\"F\"") +
                Row("", 0, "", "", "", " feature=\"\"") +
                (server ? Row("10000002", 9, "0", "5002:fake,1,1", "5001,1," + (server ? "4" : "1"), " feature=\"F\"") : "") +
                "</ms2>\n";
        }

        public const string Mastery = Head + "<ms2>\n" +
            "\t<mastery type=\"3\">\n\t\t<v grade=\"1\" value=\"0\" />\n\t\t<v grade=\"2\" value=\"100\" />\n\t</mastery>\n" +
            "\t<mastery type=\"7\">\n\t\t<v grade=\"1\" value=\"0\" />\n\t\t<v grade=\"2\" value=\"500\" />\n\t</mastery>\n" +
            "\t<mastery type=\"9\">\n\t\t<v grade=\"1\" value=\"0\" />\n\t</mastery>\n" +
            "</ms2>\n";

        static string ItemFile(long id) { return Head + "<ms2>\n\t<environment>\n\t\t<basic itemID=\"" + id + "\" />\n\t</environment>\n</ms2>\n"; }

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        public FakeRecipes()
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-recipes-" + Guid.NewGuid().ToString("N"));
            string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
            foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);
            var rows = new StringBuilder(Head + "<ms2>\n");
            foreach (long i in new long[] { 5001, 5002, 5003 }) rows.Append("\t<item id=\"" + i + "\" />\n");
            M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E(RecipeRules.Entry, Recipes(false)), E(RecipeRules.MasteryEntry, Mastery), E("itemdata/005.xml", rows.Append("</ms2>\n").ToString()),
            });
            foreach (string d in new[] { g, s })
            {
                var l = new List<KeyValuePair<string, byte[]>> { E(RecipeRules.Entry, Recipes(true)), E(RecipeRules.MasteryEntry, Mastery) };
                foreach (long i in new long[] { 5001, 5002, 5003, 5004 }) l.Add(E("item/00/00/" + i.ToString("00000000") + ".xml", ItemFile(i)));
                M2dWriter.Create(Path.Combine(d, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, l);
            }
            Data = new DataSet(k, g, s);
        }

        public static Mod ModOf(string body, string id = "recipes.test")
        {
            Mod m = Mod.Parse("<mod id=\"" + id + "\" name=\"t\" version=\"1\">\n" + body + "\n</mod>", "test mod");
            return Mod.Parse(m.ToXml(), "round trip");
        }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class RecipesTests
    {
        const string K = "client:Xml.m2d", G = "gms-client:Xml.m2d", S = "server:Xml.m2d";

        static Mod Empty() { return FakeRecipes.ModOf(""); }

        static Recipe Find(RecipeEditor ed, string id, string feature)
        {
            foreach (Recipe r in ed.Recipes) if (ed.Get(r, "id") == id && ed.Get(r, "feature") == feature) return r;
            return null;
        }

        static Plan PlanOf(FakeRecipes f, Mod m) { return Planner.Build(f.Data, new[] { FakeRecipes.ModOf(Body(m)) }, new PlanOptions()); }

        // the mod's text between <mod ...> and </mod>, read again as a fresh mod (what a save and load gives)
        static string Body(Mod m)
        {
            string x = m.ToXml();
            int a = x.IndexOf('>', x.IndexOf("<mod", StringComparison.Ordinal)) + 1, b = x.LastIndexOf("</mod>", StringComparison.Ordinal);
            return x.Substring(a, b - a);
        }

        static List<string> Texts(List<PlanMessage> l) { var t = new List<string>(); foreach (PlanMessage m in l) t.Add(m.ToString()); return t; }

        static string Text(Plan p, string archive)
        {
            return p.Find(archive) == null ? null : Fake.Text(p, archive, RecipeRules.Entry);
        }

        [Test]
        public void ItemText()
        {
            RecipeItem it;
            Assert.That(RecipeItem.TryParse("5001:tag,1,3", out it), Is.True);
            Assert.That(it.Id, Is.EqualTo(5001)); Assert.That(it.Tag, Is.EqualTo("tag")); Assert.That(it.Count, Is.EqualTo(3));
            Assert.That(it.ToString(), Is.EqualTo("5001:tag,1,3"));
            Assert.That(RecipeItem.TryParse("", out it) && it == null);
            Assert.That(RecipeItem.TryParse("0,1,1", out it) && it == null, "id 0 is no item");
            Assert.That(RecipeItem.TryParse("5001,1", out it), Is.False, "the client wants three parts");
            Assert.That(RecipeItem.TryParse("x,1,1", out it), Is.False);
        }

        [Test]
        public void SidesPairByIdFeatureAndLocale()
        {
            using (var f = new FakeRecipes())
            {
                var ed = new RecipeEditor(f.Data, Empty());
                Assert.That(RecipeEditor.SideArchives(f.Data), Is.EqualTo(new[] { K, S }), "the server's lines are planned for the global client");
                Assert.That(ed.Recipes.Count, Is.EqualTo(4), "the row without an id is no recipe");
                Recipe g = Find(ed, "10000001", "G");
                Assert.That(g.Rows[0], Is.Not.Null); Assert.That(g.Rows[1], Is.Not.Null);
                Assert.That(ed.Items(g, false)[0].Count, Is.EqualTo(3));
                Recipe so = Find(ed, "10000002", "F");
                Assert.That(ed.MissingOn(so), Is.EqualTo(new[] { K }));
                Assert.That(ed.Check(so), Has.Some.Contains("not on " + K));
                Assert.That(ed.Find(7, "").Count, Is.EqualTo(2));
                Assert.That(ed.Find(0, "5002").Count, Is.EqualTo(1), "a result id finds its recipe");
                Assert.That(ed.Mastery.MaxValue(7), Is.EqualTo(500));
                Assert.That(ed.Mastery.GradeOf(3, 100), Is.EqualTo(2));
                Assert.That(ed.Check(Find(ed, "30000001", "F")), Is.Empty);
            }
        }

        [Test]
        public void CloneWritesEverySideAndPlansClean()
        {
            using (var f = new FakeRecipes())
            {
                Mod m = Empty();
                var ed = new RecipeEditor(f.Data, m);
                List<long> ids = ed.NextIds(null, 2, null);
                Assert.That(ids, Is.EqualTo(new long[] { RecipeRules.From, RecipeRules.From + 1 }));
                Assert.Throws<ModKitException>(() => ed.Clone(Find(ed, "30000001", "F"), 30000002), "outside the range");
                Recipe c = ed.Clone(Find(ed, "10000001", "G"), ids[0]);
                ed.SetItem(c, true, 2, new RecipeItem { Id = 5002, Count = 2 });
                ed.Set(c, "requireMeso", "1000");
                Assert.That(ed.NextIds(null, 1, null), Is.EqualTo(new long[] { RecipeRules.From + 1 }));
                Assert.Throws<ModKitException>(() => ed.Clone(c, ids[0]), "the id is taken");
                Assert.That(m.Ids.Count, Is.EqualTo(1)); Assert.That(m.Ids[0].Space, Is.EqualTo(RecipeRules.Space));

                Plan p = PlanOf(f, m);
                Assert.That(Texts(p.Errors), Is.Empty);
                Assert.That(Texts(p.Warnings), Is.Empty);
                foreach (string a in new[] { K, G, S })
                {
                    string t = Text(p, a);
                    Assert.That(t, Does.Contain("id=\"" + ids[0] + "\""), a);
                    Assert.That(t, Does.Contain("rewardItem2=\"5002,1,2\""), a);
                    Assert.That(t, Does.Contain("requireItem1=\"5001,1,3\""), a + ": the copy keeps the source's values");
                }

                // a load of the saved mod shows the same recipe
                var again = new RecipeEditor(f.Data, FakeRecipes.ModOf(Body(m)));
                Recipe back = Find(again, ids[0].ToString(), "G");
                Assert.That(back, Is.Not.Null); Assert.That(back.IsAdded); Assert.That(again.MissingOn(back), Is.Empty);
                Assert.That(again.Get(back, "requireMeso"), Is.EqualTo("1000"));

                // removing the new recipe takes it out of both sides and frees its id
                ed.Remove(c);
                Assert.That(m.Ids, Is.Empty);
                Assert.That(m.Ops, Is.Empty);
            }
        }

        [Test]
        public void ChecksOfANewRecipe()
        {
            using (var f = new FakeRecipes())
            {
                Mod m = Empty();
                var ed = new RecipeEditor(f.Data, m);
                Recipe r = ed.Add(RecipeRules.From, 7);
                Assert.That(ed.Check(r), Has.Some.Contains("has no result"));
                Assert.That(ed.Check(r), Has.Some.Contains("MasteryReceipe/" + RecipeRules.From + ".png"));
                ed.SetItem(r, true, 1, new RecipeItem { Id = 5001, Count = 1 });
                ed.SetItem(r, false, 1, new RecipeItem { Id = 5001, Count = 1 });
                ed.SetItem(r, false, 2, new RecipeItem { Id = 5003, Count = 0 });
                ed.SetItem(r, false, 3, new RecipeItem { Id = 5004, Count = 1 });
                ed.Set(r, "requireMastery", "501");
                List<string> live = ed.Check(r);
                Assert.That(live, Has.Some.Contains("requireItem1: item 5001 is a material and a result"));
                Assert.That(live, Has.Some.Contains("requireItem2: count 0 of item 5003 is not above 0"));
                Assert.That(live, Has.Some.Contains("requireMastery 501 is above the top blacksmithing value 500"));
                Assert.Throws<ModKitException>(() => ed.Set(r, "id", "10000001"));
                Assert.Throws<ModKitException>(() => ed.Set(Find(ed, "30000001", "F"), "id", "99000005"), "a shipped recipe keeps its id");

                Plan p = PlanOf(f, m);
                List<string> w = Texts(p.Warnings);
                Assert.That(w, Has.Some.Contains("recipe 99000000: requireItem1: item 5001 is a material and a result"));
                Assert.That(w, Has.Some.Contains("count 0 of item 5003"));
                Assert.That(w, Has.Some.Contains("requireMastery 501 is above"));
                Assert.That(w, Has.Some.Contains("item 5004 is not an item of the Korean client"));
                Assert.That(w, Has.None.Contains("item 5004 is not an item of the server"));
                Assert.That(Texts(p.Errors), Is.Empty);

                // a type the client has no name for
                ed.Set(r, "masteryType", "2");
                Assert.That(Texts(PlanOf(f, m).Warnings), Has.Some.Contains("masteryType \"2\" is not a recipe type"));
            }
        }

        [Test]
        public void EditAndRemoveOfAShippedRecipe()
        {
            using (var f = new FakeRecipes())
            {
                Mod m = Empty();
                var ed = new RecipeEditor(f.Data, m);
                Recipe r = Find(ed, "10000001", "F");
                ed.Set(r, "requireMeso", "50");
                Recipe gone = Find(ed, "30000001", "F");
                ed.Remove(gone);
                Assert.That(gone.IsDeleted);
                Plan p = PlanOf(f, m);
                Assert.That(Texts(p.Errors), Is.Empty);
                Assert.That(Texts(p.Warnings), Is.Empty);
                foreach (string a in new[] { K, G, S })
                {
                    string t = Text(p, a);
                    Assert.That(t, Does.Not.Contain("id=\"30000001\""), a);
                    Assert.That(t, Does.Contain("requireMeso=\"50\""), a);
                }
                ed.Restore(gone);
                ed.Set(r, "requireMeso", "");
                Assert.That(m.Ops, Is.Empty, "values set back drop the edits");
            }
        }

        [Test]
        public void ANewRecipeOnOneSideIsAWarning()
        {
            using (var f = new FakeRecipes())
            {
                string row = "<receipe id=\"99000001\" masteryType=\"10\" requireMastery=\"0\" rewardItem1=\"5002,1,1\" />";
                Plan p = Planner.Build(f.Data, new[] { FakeRecipes.ModOf("<edit archive=\"" + K + "\" entry=\"" + RecipeRules.Entry + "\"><add><xml>" + row + "</xml></add></edit>") }, new PlanOptions());
                Assert.That(Texts(p.Errors), Is.Empty);
                Assert.That(Texts(p.Warnings), Has.Some.Contains("recipe 99000001 is added here but not in " + S));
            }
        }
    }
}
