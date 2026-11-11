// A mod's own contradictions (its conflicts without a winner) and its own errors do not depend on the other mods of
// a plan. This is checked over a seeded set of generated plans: small random mods over the made-up pet tables, with
// add, edit, delete and file lines. The cases the set has found are kept as tests of their own below it.
// Every id and name in here is made up.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class GeneratedPlansTests
    {
        const int Seed = 20, Plans = 600;
        static readonly string[] Archives = { "client:Xml.m2d", "server:Xml.m2d" };
        static readonly string[] Files = { "pet/100.xml", "pet/101.xml" };
        const string Pet = "table/pet.xml";

        static string Pick(Random r, params string[] of) { return of[r.Next(of.Length)]; }

        static string Edit(string ar, string entry, string rows) { return "<edit archive=\"" + ar + "\" entry=\"" + entry + "\">" + rows + "</edit>"; }

        /// <summary>One line of a mod. slot: the number of the mod in its plan; the rows a mod adds have ids of its own.</summary>
        static string Line(Random r, int slot, ref bool file)
        {
            string ar = Pick(r, Archives), row = "pet[id=" + Pick(r, "100", "101") + "]";
            switch (r.Next(9))
            {
                case 0:
                case 1: return Edit(ar, Pet, "<change at=\"" + row + "\"><set name=\"" + Pick(r, "hp", "speed") + "\" value=\"" + Pick(r, "1", "2") + "\"/></change>");
                case 2: return Edit(ar, Pet, "<delete at=\"" + row + "\"/>");
                case 3: return Edit(ar, Pet, "<add><xml><pet id=\"" + (200 + slot * 10 + r.Next(2)) + "\" name=\"" + Pick(r, "Ash", "Birch") + "\"/></xml></add>");
                case 4: return Edit(ar, Pet, "<add in=\"" + row + "\"><xml><extra id=\"" + Pick(r, "1", "2") + "\"/></xml></add>");
                case 5: return "<entry archive=\"" + ar + "\" name=\"" + Pick(r, Pet, Files[0], Files[1]) + "\" delete=\"true\"/>";
                case 6: file = true; return "<entry archive=\"" + ar + "\" name=\"" + Pick(r, Files) + "\" file=\"data/p.xml\"/>";
                case 7: return Edit(ar, Pick(r, Files), "<change at=\"environment/basic\"><set name=\"" + Pick(r, "name", "kind") + "\" value=\"" + Pick(r, "5", "6") + "\"/></change>");
                default: return Edit(ar, Pick(r, Files), r.Next(2) == 0 ? "<delete at=\"environment/basic\"/>" : "<add in=\"environment\"><xml><extra id=\"" + Pick(r, "1", "2") + "\"/></xml></add>");
            }
        }

        static Mod Make(Fake f, Random r, string id, int slot, out string body)
        {
            var sb = new StringBuilder();
            bool file = false;
            for (int n = 1 + r.Next(5); n > 0; n--) sb.Append(Line(r, slot, ref file)).Append('\n');
            body = sb.ToString();
            string d = f.ModDir(id, body);
            if (file)
            {
                Directory.CreateDirectory(Path.Combine(d, "data"));
                File.WriteAllBytes(Path.Combine(d, "data", "p.xml"), Fake.Crlf(Fake.PetFile(1, "Gen" + slot)));
            }
            return Mod.Load(d);
        }

        /// <summary>The conflicts without a winner and the errors of one mod.</summary>
        static List<string> Own(Plan p, string mod)
        {
            var l = new List<string>();
            foreach (PlanMessage m in p.Conflicts) if (m.WinnerId == null && m.ModId == mod) l.Add(m.Archive + " " + m.Entry + ": " + m.Text);
            foreach (PlanMessage m in p.Errors) if (m.ModId == mod) l.Add("error " + m.Archive + " " + m.Entry + ": " + m.Text);
            l.Sort(string.CompareOrdinal);
            return l;
        }

        [Test]
        public void AModsOwnContradictionsAreThoseOfTheModAlone()
        {
            using (var f = new Fake())
            {
                var r = new Random(Seed);
                int withOwn = 0, leftOut = 0, total = 0;
                for (int n = 0; n < Plans; n++)
                {
                    int count = 2 + r.Next(2);
                    var mods = new Mod[count];
                    var alone = new List<string>[count];
                    var text = new StringBuilder("plan " + n + "\n");
                    int sum = 0;
                    for (int k = 0; k < count; k++)
                    {
                        string body;
                        mods[k] = Make(f, r, "g" + n + "x" + k, k, out body);
                        text.Append("[").Append(mods[k].Id).Append("]\n").Append(body);
                        Plan one = f.Plan(mods[k]);
                        alone[k] = Own(one, mods[k].Id);
                        sum += one.Undecided;
                    }
                    Plan p = f.Plan(mods);
                    string dump = text + Fake.Dump(p);
                    for (int k = 0; k < count; k++) Assert.That(Own(p, mods[k].Id), Is.EqualTo(alone[k]), dump + "ALONE\n" + Fake.Dump(f.Plan(mods[k])));
                    Assert.That(p.Undecided, Is.EqualTo(sum), dump);
                    // the same plan again gives the same messages
                    Assert.That(Fake.Dump(f.Plan(mods)), Is.EqualTo(Fake.Dump(p)), dump);
                    total += sum;
                    if (sum > 0) withOwn++;
                    if (p.LeftOut().Count > 0) leftOut++;
                }
                // the set is not an empty one: many plans have a contradiction, and many leave lines of a mod out
                Assert.That(withOwn, Is.GreaterThan(Plans / 10), "plans with a contradiction");
                Assert.That(leftOut, Is.GreaterThan(Plans / 4), "plans where lines are left out");
                TestContext.Out.WriteLine(Plans + " plans, " + withOwn + " with a contradiction (" + total + " in all), " + leftOut + " with lines left out");
            }
        }

        /// <summary>The mod "own" has the same contradictions and errors with the other mod below it and above it.</summary>
        static void Same(Fake f, string name, int expect, string own, string other, bool file)
        {
            Func<string, string, Mod> load = (id, body) =>
            {
                string d = f.ModDir(id, body);
                if (file) { Directory.CreateDirectory(Path.Combine(d, "data")); File.WriteAllBytes(Path.Combine(d, "data", "p.xml"), Fake.Crlf(Fake.PetFile(1, "Gen"))); }
                return Mod.Load(d);
            };
            Mod a = load("own", own), b = load("other", other);
            Plan alone = f.Plan(a);
            Assert.That(Own(alone, "own").Count, Is.EqualTo(expect), name + "\n" + Fake.Dump(alone));
            Assert.That(Own(f.Plan(b), "other"), Is.Empty, name);
            foreach (Mod[] order in new[] { new[] { a, b }, new[] { b, a } })
            {
                Plan p = f.Plan(order);
                string dump = name + ", " + order[0].Id + " first\n" + Fake.Dump(p);
                Assert.That(Own(p, "own"), Is.EqualTo(Own(alone, "own")), dump);
                Assert.That(Own(p, "other"), Is.Empty, dump);
            }
        }

        const string C = "client:Xml.m2d", S = "server:Xml.m2d";
        static string Del(string ar, string e) { return "<entry archive=\"" + ar + "\" name=\"" + e + "\" delete=\"true\"/>"; }
        const string Child = "<add in=\"pet[id=100]\"><xml><extra id=\"2\"/></xml></add>";

        [Test]
        public void AModThatEditsTheEntryItDeletesIsWrongAlsoWhereAnotherModBringsTheEntryBack()
        {
            using (var f = new Fake())
                Same(f, "edit", 1, Del(C, Pet) + Edit(C, Pet, "<delete at=\"pet[id=101]\"/>"),
                    Edit(C, Pet, "<change at=\"pet[id=101]\"><set name=\"speed\" value=\"2\"/></change>"), false);
            // with two row lines that contradict each other: the entry is not edited, so they are not reported
            using (var f = new Fake())
                Same(f, "two rows", 1, Del(C, Pet) + Edit(C, Pet, "<add in=\"pet[id=101]\"><xml><extra id=\"1\"/></xml></add>") + Edit(C, Pet, "<delete at=\"pet[id=101]\"/>"),
                    Edit(C, Pet, "<change at=\"pet[id=101]\"><set name=\"speed\" value=\"2\"/></change>"), false);
            // the other mod brings the entry back with a file
            using (var f = new Fake())
                Same(f, "file", 1, Del(S, Files[1]) + Edit(S, Files[1], "<delete at=\"environment/basic\"/>"),
                    "<entry archive=\"" + S + "\" name=\"" + Files[1] + "\" file=\"data/p.xml\"/>", true);
        }

        [Test]
        public void AModThatDeletesAndReplacesOneEntryWhereAnotherModDeletesItToo()
        {
            string both = Del(S, Files[1]) + "<entry archive=\"" + S + "\" name=\"" + Files[1] + "\" file=\"data/p.xml\"/>";
            using (var f = new Fake()) Same(f, "delete and file", 1, both, Del(S, Files[1]), true);
            using (var f = new Fake())
                Same(f, "delete, file and edit", 2, both + Edit(S, Files[1], "<add in=\"environment\"><xml><extra id=\"1\"/></xml></add>"), Del(S, Files[1]), true);
        }

        [Test]
        public void ARowLineThatLosesToAnotherModStillContradictsTheModsOtherLine()
        {
            using (var f = new Fake())
                Same(f, "add and delete", 1, Edit(S, Pet, Child) + Edit(S, Pet, "<delete at=\"pet[id=100]\"/>"), Edit(S, Pet, Child.Replace("/>", " v=\"9\"/>")), false);
            using (var f = new Fake())
                Same(f, "change, add and delete", 2, Edit(S, Pet, "<change at=\"pet[id=100]\"><set name=\"hp\" value=\"2\"/></change>") + Edit(S, Pet, Child) + Edit(S, Pet, "<delete at=\"pet[id=100]\"/>"),
                    Edit(S, Pet, Child), false);
        }
    }
}
