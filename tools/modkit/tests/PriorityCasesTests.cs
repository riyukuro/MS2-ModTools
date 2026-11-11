// Tests of the priority of mods where three mods meet in one place, where a whole entry meets an edit, where one
// of the two lines is a mirrored one, and of the conflicts no priority decides. Made-up data only.
using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class PriorityCasesTests
    {
        const string C = "client:Xml.m2d", S = "server:Xml.m2d", G = "gms-client:Xml.m2d", Pet = "table/pet.xml";
        const string Five = "item/0/12/01200005.xml";

        static string Set(string archive, string entry, string at, string name, string value)
        {
            return "<edit archive=\"" + archive + "\" entry=\"" + entry + "\"><change at=\"" + at + "\"><set name=\"" + name + "\" value=\"" + value + "\"/></change></edit>";
        }

        static string Line(string text, string start)
        {
            return text.Replace("\r", "").Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(start, StringComparison.Ordinal));
        }

        [Test]
        public void ADeleteBetweenTwoChangesOfOneRow()
        {
            using (var f = new Fake())
            {
                // a < b < c: a and c set different fields of a row, b deletes the row
                Mod a = f.Mod("a", Set(C, Pet, "pet[id=101]", "hp", "5"));
                Mod b = f.Mod("b", "<edit archive=\"" + C + "\" entry=\"" + Pet + "\"><delete at=\"pet[id=101]\"/></edit>");
                Mod c = f.Mod("c", Set(C, Pet, "pet[id=101]", "speed", "9"));
                Plan p = f.Plan(a, b, c);
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                // the row stays for c, so b's delete is in no part of the plan and a's change is written
                Assert.That(Line(Fake.Text(p, C, Pet), "<pet id=\"101\""), Does.Contain("hp=\"5\"").And.Contain("speed=\"9\""), dump);
                Assert.That(p.Conflicts, Is.Not.Empty, dump);
                Assert.That(p.Conflicts.All(x => x.WinnerId == "c"), dump);
                Assert.That(p.Changes.Select(x => x.ModId + " " + x.Kind + " " + x.Name), Is.EquivalentTo(new[] { "a SetAttr hp", "c SetAttr speed" }), dump);
                // b is the one mod that has a part left out
                Assert.That(p.LeftOut(), Is.EqualTo(new[] { "c wins 1 part of b" }), dump);
                Assert.That(p.Conflicts.All(x => x.LoserId == "b"), dump);
                // the same result as without b
                Assert.That(Fake.Text(p, C, Pet), Is.EqualTo(Fake.Text(f.Plan(a, c), C, Pet)));

                // two mods: the delete of the higher one wins, and the lower one's change is in no line of the diff
                p = f.Plan(a, b);
                dump = Fake.Dump(p);
                Assert.That(Fake.Text(p, C, Pet), Does.Not.Contain("id=\"101\""), dump);
                Assert.That(p.Conflicts.Count, Is.EqualTo(1), dump);
                Assert.That(p.Conflicts[0].WinnerId, Is.EqualTo("b"), dump);
                Assert.That(p.Changes.Select(x => x.ModId + " " + x.Kind), Is.EqualTo(new[] { "b DeleteRow" }), dump);
                // and the change of the higher one keeps the row
                p = f.Plan(b, a);
                dump = Fake.Dump(p);
                Assert.That(Line(Fake.Text(p, C, Pet), "<pet id=\"101\""), Does.Contain("hp=\"5\""), dump);
                Assert.That(p.Conflicts.All(x => x.WinnerId == "a"), dump);
                Assert.That(p.Changes.Select(x => x.ModId + " " + x.Kind), Is.EqualTo(new[] { "a SetAttr" }), dump);
            }
        }

        [Test]
        public void ADeleteBetweenTwoSkillLines()
        {
            using (var f = new FakeSkills())
            {
                // a < b < c: a and c set different fields of level 1 of a skill, b deletes that level
                const string K = "client:Xml.m2d", File = "skilldata/700.xml";
                Mod a = FakeSkills.ModOf("<skill id=\"70000001\"><level value=\"1\" cooldown=\"3\"/></skill>", "a");
                Mod b = FakeSkills.ModOf("<edit archive=\"" + K + "\" entry=\"" + File + "\" sync=\"false\"><delete at=\"skill[id=70000001]/level[value=1]\"/></edit>", "b");
                Mod c = FakeSkills.ModOf("<skill id=\"70000001\"><attack level=\"1\" motion=\"1\" index=\"1\" distance=\"900\"/></skill>", "c");
                Plan p = Planner.Build(f.Data, new[] { a, b, c }, null);
                string dump = Fake.Dump(p), text = Fake.Text(p, K, File);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(text, Does.Contain("<level value=\"1\" cooldown=\"3000\""), dump);
                Assert.That(text, Does.Contain("<range distance=\"900\""), dump);
                var here = p.Conflicts.Where(x => x.Archive == K).ToList();
                Assert.That(here, Is.Not.Empty, dump);
                Assert.That(here.All(x => x.WinnerId == "c" && x.LoserId == "b"), dump);
                Assert.That(p.Changes.Any(x => x.Archive == K && x.Kind == ChangeKind.DeleteRow), Is.False, dump);
                // the same file as without b
                Assert.That(text, Is.EqualTo(Fake.Text(Planner.Build(f.Data, new[] { a, c }, null), K, File)));
            }
        }

        [Test]
        public void NewRowsOfTwoModsAreWrittenLowestModFirst()
        {
            using (var f = new Fake())
            {
                Func<string, string, string> add = (arch, id) => "<edit archive=\"" + arch + "\" entry=\"" + Pet + "\"><add><xml><pet id=\"" + id + "\" name=\"N" + id + "\"/></xml></add></edit>";
                string where = "<where archive=\"" + C + "\" entry=\"" + Pet + "\" rows=\"pet\"/></ids>";
                Mod lo = f.Mod("lo", "<ids space=\"pet\" from=\"900\" to=\"909\">" + where + add(C, "901") + add(C, "902"));
                Mod hi = f.Mod("hi", "<ids space=\"pet\" from=\"910\" to=\"919\">" + where + add(C, "911"));
                Plan p = f.Plan(lo, hi);
                string dump = Fake.Dump(p), text = Fake.Text(p, C, Pet);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(p.Conflicts, Is.Empty, dump);
                int a = text.IndexOf("id=\"901\""), b = text.IndexOf("id=\"902\""), c = text.IndexOf("id=\"911\"");
                Assert.That(a > 0 && a < b && b < c, "the higher mod's row comes last\n" + text);
                // and the other way round
                text = Fake.Text(f.Plan(hi, lo), C, Pet);
                a = text.IndexOf("id=\"901\""); b = text.IndexOf("id=\"902\""); c = text.IndexOf("id=\"911\"");
                Assert.That(c > 0 && c < a && a < b, text);
            }
        }

        [Test]
        public void ADeleteBetweenTwoEditsOfOneEntry()
        {
            using (var f = new Fake())
            {
                Mod lo = f.Mod("lo", Set(S, "pet/100.xml", "environment/basic", "kind", "4"));
                Mod mid = f.Mod("mid", "<entry archive=\"" + S + "\" name=\"pet/100.xml\" delete=\"true\"/>");
                Mod hi = f.Mod("hi", Set(S, "pet/100.xml", "environment/basic", "name", "Hi"));
                Plan p = f.Plan(lo, mid, hi);
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(Fake.Text(p, S, "pet/100.xml"), Does.Contain("name=\"Hi\" kind=\"4\""), dump);
                Assert.That(p.Find(S).Delete, Is.Empty, dump);
                Assert.That(p.Conflicts, Is.Not.Empty, dump);
                Assert.That(p.Conflicts.All(x => x.WinnerId == "hi"), dump);
                Assert.That(p.Changes.Any(x => x.Kind == ChangeKind.DeleteEntry), Is.False, dump);

                // the delete is the highest: the entry goes, both edits are left out
                p = f.Plan(lo, hi, mid);
                dump = Fake.Dump(p);
                Assert.That(p.Find(S).Delete, Is.EqualTo(new[] { "pet/100.xml" }), dump);
                Assert.That(p.Conflicts.Count(x => x.WinnerId == "mid"), Is.EqualTo(2), dump);
                Assert.That(p.LeftOut(), Is.EqualTo(new[] { "mid wins 1 part of hi", "mid wins 1 part of lo" }), dump);
                Assert.That(p.Changes.Count(x => x.Archive == S), Is.EqualTo(1), dump);
            }
        }

        static string Edit(string archive, string entry, string rows)
        {
            return "<edit archive=\"" + archive + "\" entry=\"" + entry + "\">" + rows + "</edit>";
        }

        [Test]
        public void ADeleteOfARowThatAHigherModAddsARowIn()
        {
            using (var f = new Fake())
            {
                Mod del = f.Mod("del", Edit(S, "pet/100.xml", "<delete at=\"environment\"/>"));
                Mod add = f.Mod("add", Edit(S, "pet/100.xml", "<add in=\"environment\"><xml><extra id=\"1\"/></xml></add>"));

                // the add is the higher one: the row stays and has the new row
                Plan p = f.Plan(del, add);
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(Fake.Text(p, S, "pet/100.xml"), Does.Contain("<basic ").And.Contain("<extra id=\"1\"/>"), dump);
                Assert.That(p.Conflicts.Select(x => x.ModId + " " + x.WinnerId + " " + x.LoserId), Is.EqualTo(new[] { "del add del" }), dump);
                Assert.That(p.Changes.Select(x => x.ModId + " " + x.Kind), Is.EqualTo(new[] { "add AddRow" }), dump);
                Assert.That(p.LeftOut(), Is.EqualTo(new[] { "add wins 1 part of del" }), dump);

                // the delete is the higher one: the row goes, the new row is in no line of the diff
                p = f.Plan(add, del);
                dump = Fake.Dump(p);
                Assert.That(Fake.Text(p, S, "pet/100.xml"), Does.Not.Contain("<basic ").And.Not.Contain("<extra"), dump);
                Assert.That(p.Conflicts.Select(x => x.ModId + " " + x.WinnerId + " " + x.LoserId), Is.EqualTo(new[] { "add del add" }), dump);
                Assert.That(p.Changes.Select(x => x.ModId + " " + x.Kind), Is.EqualTo(new[] { "del DeleteRow" }), dump);
            }
        }

        [Test]
        public void AModThatContradictsItselfInOneRow()
        {
            using (var f = new Fake())
            {
                string hp5 = "<change at=\"pet[id=101]\"><set name=\"hp\" value=\"5\"/></change>", hp6 = hp5.Replace("\"5\"", "\"6\"");
                string del = "<delete at=\"pet[id=101]\"/>", row = "<add in=\"pet[id=101]\"><xml><extra id=\"1\"/></xml></add>";
                foreach (string rows in new[] { hp5 + hp6, del + hp5, del + row, row + del, hp5 + del })
                {
                    Mod m = f.Mod("m", Edit(C, Pet, rows));
                    Plan p = f.Plan(m);
                    string dump = rows + "\n" + Fake.Dump(p);
                    Assert.That(p.Errors, Is.Empty, dump);
                    Assert.That(p.Conflicts.Count, Is.EqualTo(1), dump);
                    Assert.That(p.Conflicts[0].WinnerId, Is.Null, dump);
                    Assert.That(p.Conflicts[0].ConflictLine(), Does.StartWith("conflict, no winner: "), dump);
                    Assert.That(p.Undecided, Is.EqualTo(1), dump);

                    // and nothing is written, allowed or not
                    var ws = new Workspace(Path.Combine(f.Dir, "ws"));
                    ApplyResult r = ws.Apply(f.Data, new[] { m }, new ApplyOptions { AllowConflicts = true });
                    Assert.That(r.Applied, Is.False, dump);
                    Assert.That(r.Reason, Is.EqualTo("1" + Plan.UndecidedReason), dump);
                }

                // a higher mod that keeps the row does not hide what the mod does to itself
                Mod b = f.Mod("b", Edit(C, Pet, hp5 + del));
                Mod c = f.Mod("c", Set(C, Pet, "pet[id=101]", "speed", "9"));
                Plan q = f.Plan(b, c);
                string d2 = Fake.Dump(q);
                Assert.That(q.Conflicts.Count(x => x.WinnerId == null && x.ModId == "b"), Is.EqualTo(1), d2);
                Assert.That(q.Conflicts.Count(x => x.WinnerId == "c"), Is.EqualTo(1), d2);
                Assert.That(q.Undecided, Is.EqualTo(1), d2);
            }
        }

        [Test]
        public void AnEditOfAModsOwnFileThatIsLeftOut()
        {
            using (var f = new Fake())
            {
                // lo puts its own file in place of an entry and edits a row that only its file has; hi edits the entry
                string filed = Fake.PetFile(100, "Filed").Replace("<basic ", "<own id=\"1\" v=\"1\"/><basic ");
                string d = Path.Combine(f.ModDir("lo", "<entry archive=\"" + S + "\" name=\"pet/100.xml\" file=\"data/p.xml\"/>" + Set(S, "pet/100.xml", "environment/own[id=1]", "v", "2")), "data");
                Directory.CreateDirectory(d);
                File.WriteAllBytes(Path.Combine(d, "p.xml"), Fake.Crlf(filed));
                Mod lo = Mod.Load(Path.GetDirectoryName(d));
                Mod hi = f.Mod("hi", Set(S, "pet/100.xml", "environment/basic", "kind", "4"));

                Plan p = f.Plan(lo, hi);
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(Fake.Text(p, S, "pet/100.xml"), Does.Contain("name=\"Mossy\" kind=\"4\"").And.Not.Contain("<own"), dump);
                Assert.That(p.Conflicts.All(x => x.WinnerId == "hi"), dump);
                Assert.That(p.LeftOut(), Is.EqualTo(new[] { "hi wins 2 parts of lo" }), dump);
                Assert.That(p.Changes.Any(x => x.ModId == "lo"), Is.False, dump);

                // the other way round lo's file is written, with lo's edit
                p = f.Plan(hi, lo);
                dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(Fake.Text(p, S, "pet/100.xml"), Does.Contain("<own id=\"1\" v=\"2\"/>").And.Contain("name=\"Filed\""), dump);
            }
        }

        [Test]
        public void AnEditOfAnAddedEntryThatAThirdModDeletes()
        {
            using (var f = new Fake())
            {
                Mod lo = FileMod(f, "lo", "pet/777.xml", Fake.PetFile(777, "New"));
                Mod mid = f.Mod("mid", "<entry archive=\"" + S + "\" name=\"pet/777.xml\" delete=\"true\"/>");
                Mod hi = f.Mod("hi", Set(S, "pet/777.xml", "environment/basic", "kind", "4"));
                Plan p = f.Plan(lo, mid, hi);
                string dump = Fake.Dump(p);
                PlanMessage c = p.Conflicts.Single(x => x.ModId == "hi");
                // mid has the entry gone, and the line does not say that mid is the higher one
                Assert.That(c.WinnerId, Is.EqualTo("mid"), dump);
                Assert.That(c.Text, Does.EndWith("; mod mid wins (the data ships no such entry)").And.Not.Contain("higher priority"), dump);
                Assert.That(c.ConflictLine(), Does.StartWith("conflict, mid wins: "), dump);
            }
        }

        static Mod FileMod(Fake f, string id, string entry, string content)
        {
            string d = Path.Combine(f.ModDir(id, "<entry archive=\"" + S + "\" name=\"" + entry + "\" file=\"data/p.xml\"/>"), "data");
            Directory.CreateDirectory(d);
            File.WriteAllBytes(Path.Combine(d, "p.xml"), Fake.Crlf(content));
            return Mod.Load(Path.GetDirectoryName(d));
        }

        [Test]
        public void AnEditAgainstAWholeEntry()
        {
            using (var f = new Fake())
            {
                string filed = Fake.PetFile(100, "Filed");
                Mod edit = f.Mod("edit", Set(S, "pet/100.xml", "environment/basic", "kind", "4"));
                Mod file = FileMod(f, "file", "pet/100.xml", filed);

                // the whole entry is the higher one: it is written as the mod has it
                Plan p = f.Plan(edit, file);
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(p.Conflicts.Count(x => x.Archive == S), Is.EqualTo(1), dump);
                PlanMessage c = p.Conflicts.First(x => x.Archive == S);
                Assert.That(c.ModId + " " + c.WinnerId, Is.EqualTo("edit file"), dump);
                Assert.That(Fake.Text(p, S, "pet/100.xml"), Is.EqualTo(Encoding.UTF8.GetString(Fake.Crlf(filed))), dump);
                Assert.That(p.Changes.Any(x => x.ModId == "edit" && x.Archive == S), Is.False, dump);

                // the edit is the higher one: the shipped entry is back and gets the edit
                p = f.Plan(file, edit);
                dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                c = p.Conflicts.First(x => x.Archive == S);
                Assert.That(c.ModId + " " + c.WinnerId, Is.EqualTo("edit edit"), dump);
                // the line is the edit's, the part left out is the other mod's
                Assert.That(p.LeftOut(), Is.EqualTo(new[] { "edit wins 1 part of file" }), dump);
                Assert.That(Fake.Text(p, S, "pet/100.xml"), Does.Contain("name=\"Mossy\" kind=\"4\""), dump);
                Assert.That(p.Changes.Any(x => x.ModId == "file" && x.Archive == S), Is.False, dump);

                // an entry that the lower mod adds: the higher mod's edit is made in it, and the higher mod is named
                Mod add = FileMod(f, "add", "pet/777.xml", Fake.PetFile(777, "New"));
                Mod on = f.Mod("on", Set(S, "pet/777.xml", "environment/basic", "kind", "4"));
                p = f.Plan(add, on);
                dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                c = p.Conflicts.First(x => x.Entry == "pet/777.xml");
                Assert.That(c.ModId + " " + c.WinnerId, Is.EqualTo("on on"), dump);
                Assert.That(Fake.Text(p, S, "pet/777.xml"), Does.Contain("name=\"New\" kind=\"4\""), dump);
                // both mods have all they ask for: no part of either is left out
                Assert.That(c.LoserId, Is.Null, dump);
                Assert.That(p.LeftOut(), Is.Empty, dump);
                // the other way round the added entry stays as it is
                p = f.Plan(on, add);
                dump = Fake.Dump(p);
                c = p.Conflicts.First(x => x.Entry == "pet/777.xml");
                Assert.That(c.ModId + " " + c.WinnerId, Is.EqualTo("on add"), dump);
                Assert.That(Fake.Text(p, S, "pet/777.xml"), Does.Contain("name=\"New\" kind=\"3\""), dump);
                // every conflict here has a winner
                Assert.That(p.Conflicts.All(x => x.WinnerId != null), dump);
            }
        }

        [Test]
        public void AMirroredLineAgainstADirectLine()
        {
            using (var f = new FakeTargets(kms: false))
            {
                // one mod sets the field on the global client, the other on the server; the server line is
                // mirrored to the global client
                Mod direct = f.Mod("direct", Set(G, Five, "environment/property", "slotMax", "5"));
                Mod other = f.Mod("other", Set(S, Five, "environment/property", "slotMax", "8"));
                Func<Plan, string, string> value = (pl, ar) => pl.Changes.Where(x => x.Archive == ar && x.Entry == Five && x.Name == "slotMax").Select(x => x.ModId + "=" + x.NewValue).SingleOrDefault();

                // each line is mirrored to the other side, where it meets the other mod's own line: the higher mod
                // has its value on both sides, and each side has a conflict (not a note) that names it
                Plan p = f.Plan(direct, other);
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(value(p, S), Is.EqualTo("other=8"), dump);
                Assert.That(value(p, G), Is.EqualTo("other=8"), dump);
                Assert.That(p.Conflicts.Select(x => x.Archive + " " + x.WinnerId), Is.EquivalentTo(new[] { G + " other", S + " other" }), dump);
                Assert.That(p.Conflicts.All(x => x.Text.StartsWith("mirrored from ", StringComparison.Ordinal)), dump);

                p = f.Plan(other, direct);
                dump = Fake.Dump(p);
                Assert.That(value(p, S), Is.EqualTo("direct=5"), dump);
                Assert.That(value(p, G), Is.EqualTo("direct=5"), dump);
                Assert.That(p.Conflicts.Select(x => x.Archive + " " + x.WinnerId), Is.EquivalentTo(new[] { G + " direct", S + " direct" }), dump);

                // nothing is written while that conflict is not allowed
                var ws = new Workspace(Path.Combine(f.Dir, "ws"));
                ApplyResult r = ws.Apply(f.Data, new[] { direct, other }, new ApplyOptions { Maps = FakeItems.Maps });
                Assert.That(r.Applied, Is.False);
                Assert.That(r.Reason, Is.EqualTo("2 conflict(s)"));

                // two mods on the server side: one conflict there, and its mirror is no second one
                Mod second = f.Mod("second", Set(S, Five, "environment/property", "slotMax", "9"));
                p = f.Plan(other, second);
                dump = Fake.Dump(p);
                Assert.That(p.Conflicts.Count, Is.EqualTo(1), dump);
                Assert.That(p.Conflicts[0].Archive + " " + p.Conflicts[0].WinnerId, Is.EqualTo(S + " second"), dump);
                Assert.That(value(p, G), Is.EqualTo("second=9"), dump);
            }
        }

        [Test]
        public void AConflictWithoutAWinnerAlwaysStopsTheApply()
        {
            using (var f = new Fake())
            {
                string where = "<where archive=\"" + C + "\" entry=\"table/pet.xml\" rows=\"pet\"/></ids>";
                Func<string, int, int, int, Mod> mod = (id, from, to, pet) => f.Mod(id, "<ids space=\"pet\" from=\"" + from + "\" to=\"" + to + "\">" + where
                    + "<edit archive=\"" + C + "\" entry=\"" + Pet + "\"><add><xml><pet id=\"" + pet + "\" name=\"N\"/></xml></add></edit>");
                Mod a = mod("a", 900, 909, 901), b = mod("b", 905, 915, 906);
                var ws = new Workspace(Path.Combine(f.Dir, "ws"));
                ApplyResult r = ws.Apply(f.Data, new[] { a, b }, new ApplyOptions { AllowConflicts = true });
                string dump = Fake.Dump(r.Plan);
                Assert.That(r.Plan.Errors, Is.Empty, dump);
                Assert.That(r.Plan.Conflicts.Count(x => x.WinnerId == null), Is.EqualTo(1), dump);
                Assert.That(r.Applied, Is.False, dump);
                Assert.That(r.Reason, Is.EqualTo("1 conflict(s) that no priority decides"));
                Assert.That(r.Plan.LeftOut(), Is.Empty);
                Assert.That(Directory.Exists(Path.Combine(f.Dir, "ws")), Is.False, "nothing written");

                // a mod that contradicts itself has no winner either (it does not win against itself)
                Mod self = f.Mod("self", "<entry archive=\"" + S + "\" name=\"pet/100.xml\" copy=\"pet/101.xml\"/><entry archive=\"" + S + "\" name=\"pet/100.xml\" delete=\"true\"/>");
                Plan p = f.Plan(self);
                Assert.That(p.Conflicts.Count, Is.EqualTo(1), Fake.Dump(p));
                Assert.That(p.Conflicts[0].WinnerId, Is.Null);
                Assert.That(p.Conflicts[0].ConflictLine(), Does.StartWith("conflict, no winner: [self] " + S + " pet/100.xml: deletes the entry that the same mod replaces"));
            }
        }
    }
}
