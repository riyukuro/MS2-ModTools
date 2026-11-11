// A mod that contradicts itself in its shop, drop box, skill or job skill lines, under a higher mod that deletes
// the entry or the row (or brings a file for the entry): the contradiction is found as it is for the mod alone.
// Every id and name in here is made up.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class HiddenLinesTests
    {
        static Mod Load(string root, string id, string body, string file)
        {
            string d = Path.Combine(root, "pm-" + id + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(d, "data"));
            File.WriteAllText(Path.Combine(d, "mod.xml"), "<mod id=\"" + id + "\" name=\"t\" version=\"1\">\n" + body + "\n</mod>\n", new UTF8Encoding(false));
            if (file != null) File.WriteAllBytes(Path.Combine(d, "data", "p.xml"), Fake.Crlf(file));
            return Mod.Load(d);
        }

        static string Del(string ar, string e) { return "<entry archive=\"" + ar + "\" name=\"" + e + "\" delete=\"true\"/>"; }
        static string FileOp(string ar, string e) { return "<entry archive=\"" + ar + "\" name=\"" + e + "\" file=\"data/p.xml\"/>"; }
        static string SetRow(string ar, string e, string at, string name, string value) { return "<edit archive=\"" + ar + "\" entry=\"" + e + "\"><change at=\"" + at + "\"><set name=\"" + name + "\" value=\"" + value + "\"/></change></edit>"; }
        static string DelRow(string ar, string e, string at) { return "<edit archive=\"" + ar + "\" entry=\"" + e + "\"><delete at=\"" + at + "\"/></edit>"; }

        static List<string> NoWinner(Plan p)
        {
            var l = new List<string>();
            foreach (PlanMessage m in p.Conflicts) if (m.WinnerId == null && m.ModId == "own") l.Add(m.ToString());
            l.Sort(string.CompareOrdinal);
            return l;
        }

        /// <summary>expect: the number of contradictions of the mod alone (0: this kind of line reports none as a conflict).</summary>
        static void Run(DataSet data, string root, string name, int expect, string own, string[][] highers)
        {
            Mod a = Load(root, "own", own, null);
            Plan alone = Planner.Build(data, new[] { a }, null);
            Assert.That(alone.Undecided, Is.EqualTo(expect), name + "\n" + Fake.Dump(alone));
            foreach (string[] h in highers)
            {
                Mod b = Load(root, "hi", h[1], h.Length > 2 ? h[2] : null);
                Plan p = Planner.Build(data, new[] { a, b }, null);
                Assert.That(NoWinner(p), Is.EqualTo(NoWinner(alone)), name + ", under " + h[0] + "\n" + Fake.Dump(p));
                // the higher mod has no contradiction of its own, in either order of the two
                Assert.That(p.Undecided, Is.EqualTo(expect), name + ", under " + h[0] + "\n" + Fake.Dump(p));
                Plan q = Planner.Build(data, new[] { b, a }, null);
                Assert.That(NoWinner(q), Is.EqualTo(NoWinner(alone)), name + ", over " + h[0] + "\n" + Fake.Dump(q));
            }
        }

        const string SS = "server:Server.m2d", K = "client:Xml.m2d", G = "gms-client:Xml.m2d", S = "server:Xml.m2d";

        [Test]
        public void AShopOrDropBoxContradictionUnderAHigherDeleteOrFile()
        {
            using (var f = new FakeGroups())
            {
                string[][] hi =
                {
                    new[] { "delete stock entry", Del(SS, Planner.ShopStockEntry) },
                    new[] { "file stock entry", FileOp(SS, Planner.ShopStockEntry), FakeGroups.ShopGame },
                    new[] { "delete shop row", DelRow(SS, Planner.ShopStockEntry, "shop[shopID=9001]") },
                    new[] { "delete item row", DelRow(SS, Planner.ShopStockEntry, "shop[shopID=9001]/item[sn=1]") },
                    new[] { "change item row", SetRow(SS, Planner.ShopStockEntry, "shop[shopID=9001]/item[sn=1]", "price", "5") },
                };
                Run(f.Data, f.Dir, "shop: two ops set one value", 1, "<shop id=\"9001\"><row sn=\"1\"><set name=\"price\" value=\"1\"/></row></shop><shop id=\"9001\"><row sn=\"1\"><set name=\"price\" value=\"2\"/></row></shop>", hi);
                Run(f.Data, f.Dir, "shop: one op set one value twice", 1, "<shop id=\"9001\"><row sn=\"1\"><set name=\"price\" value=\"1\"/></row><row sn=\"1\"><set name=\"price\" value=\"2\"/></row></shop>", hi);
                Run(f.Data, f.Dir, "shop: change and remove", 1, "<shop id=\"9001\"><row sn=\"1\"><set name=\"price\" value=\"1\"/></row><remove sn=\"1\"/></shop>", hi);
                Run(f.Data, f.Dir, "shop: remove and change", 1, "<shop id=\"9001\"><remove sn=\"1\"/></shop><shop id=\"9001\"><row sn=\"1\"><set name=\"price\" value=\"1\"/></row></shop>", hi);
                string nw = "<shop id=\"900000003\" new=\"true\"><xml><shop categoryID=\"1\" /></xml></shop>";
                Run(f.Data, f.Dir, "shop: new twice", 1, nw + nw, hi);
                string[][] hd =
                {
                    new[] { "delete drop entry", Del(SS, Planner.ServerDropEntry) },
                    new[] { "file drop entry", FileOp(SS, Planner.ServerDropEntry), FakeGroups.ServerFinal },
                    new[] { "delete box row", DelRow(SS, Planner.ServerDropEntry, "dropBox[dropBoxID=7001]") },
                    new[] { "delete kms drop entry", Del(K, Planner.KmsDropEntry) },
                };
                Run(f.Data, f.Dir, "box: two ops set one value", 4, "<dropbox id=\"7001\"><row group=\"1\" item=\"5002\"><set name=\"minCount\" value=\"3\"/></row></dropbox><dropbox id=\"7001\"><row group=\"1\" item=\"5002\"><set name=\"minCount\" value=\"4\"/></row></dropbox>", hd);
                string nb = "<dropbox id=\"900010002\" clone=\"7003\"></dropbox>";
                Run(f.Data, f.Dir, "box: clone twice", 4, nb + nb, hd);
            }
        }

        [Test]
        public void ASkillOrJobSkillContradictionUnderAHigherDeleteOrFile()
        {
            using (var f = new FakeSkills())
            {
                string[][] hi =
                {
                    new[] { "delete kms bundle", Del(K, "skilldata/700.xml") },
                    new[] { "delete kms row", DelRow(K, "skilldata/700.xml", "skill[id=70000001]") },
                    new[] { "delete gms file", Del(G, "skill/70/70000001.xml") },
                    new[] { "delete server file", Del(S, "skill/70/70000001.xml") },
                    new[] { "file server", FileOp(S, "skill/70/70000001.xml"), FakeSkills.GmsSkill("Attack_A", "8", true, "70000001") },
                };
                Run(f.Data, f.Dir, "skill: two ops set one value", 3, "<skill id=\"70000001\"><level value=\"1\" sp=\"1\"/></skill><skill id=\"70000001\"><level value=\"1\" sp=\"2\"/></skill>", hi);
                string cl = "<skill id=\"78000001\" clone=\"70000001\"/>";
                Run(f.Data, f.Dir, "skill: clone twice", 1, cl + cl, hi);
                string[][] hj =
                {
                    new[] { "delete kms job", Del(K, "table/job.xml") },
                    new[] { "delete server job", Del(S, "table/job.xml") },
                    new[] { "delete kms job row", DelRow(K, "table/job.xml", "job[jobGroupID=90]") },
                    new[] { "delete server job row", DelRow(S, "table/job.xml", "job[code=090]") },
                    new[] { "change kms job row", SetRow(K, "table/job.xml", "job[jobGroupID=90]/skills/v[skillID=70000002]", "maxLevel", "7") },
                };
                Run(f.Data, f.Dir, "jobskill: one skill twice", 3, "<jobskill job=\"90\" skill=\"70000002\" replace=\"70000002\" max-level=\"5\"/><jobskill job=\"90\" skill=\"70000002\" replace=\"70000002\" max-level=\"6\"/>", hj);
                Run(f.Data, f.Dir, "jobskill: replace twice", 0, cl + "<jobskill job=\"90\" skill=\"78000001\" replace=\"70000002\"/><jobskill job=\"90\" skill=\"78000001\" replace=\"70000002\"/>", hj);
            }
        }

        [Test]
        public void MapLinesReportAContradictionAsAnErrorWithOrWithoutAHigherDelete()
        {
            using (var f = new FakeMaps())
            {
                string[][] hi =
                {
                    new[] { "delete server map", Del(FakeMaps.S, FakeMaps.EntryA) },
                    new[] { "delete tag table", Del(FakeMaps.Tags, FakeMaps.TagEntry) },
                    new[] { "delete tag row", DelRow(FakeMaps.Tags, FakeMaps.TagEntry, "region[mapCode=90000001]") },
                };
                string ch = "<change id=\"" + FakeMaps.Crate1 + "\" model=\"Crate_A\" position=\"0, 0, 150\"><set name=\"IsVisible\" value=\"{0}\" /></change>";
                Run(f.Data, f.Dir, "map: two changes of one entity", 0, "<map xblock=\"" + FakeMaps.MapA + "\">" + string.Format(ch, "False") + string.Format(ch, "True") + "</map>", hi);
                Run(f.Data, f.Dir, "map: two map ops", 0, "<map xblock=\"" + FakeMaps.MapA + "\">" + string.Format(ch, "False") + "</map><map xblock=\"" + FakeMaps.MapA + "\">" + string.Format(ch, "True") + "</map>", hi);
                string sp = "<spawn name=\"w{0}\" model=\"SpawnPoint_A\" entity-name=\"Spawn_90{0}\" point=\"14000{0}\"><property name=\"Position\" value=\"150, 300, 450\" /><row difficulty=\"1\" tag=\"wolf\" coolTime=\"2{0}\" population=\"3\" /></spawn>";
                Run(f.Data, f.Dir, "map: one spawn point twice", 0, "<map xblock=\"" + FakeMaps.MapA + "\">" + string.Format(sp, "0") + string.Format(sp, "0").Replace("w0", "w1").Replace("Spawn_900", "Spawn_901") + "</map>", hi);
            }
        }
    }
}
