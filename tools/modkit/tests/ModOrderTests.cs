// Tests of the priority of mods: ModOrder, the --priority option and the MS2_MOD_PRIORITY setting of the command
// line, and an apply with conflicts allowed. Made-up data only (the Fake of SyntheticTests).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    [NonParallelizable]
    public class ModOrderTests
    {
        const string C = "client:Xml.m2d", Pet = "table/pet.xml";

        static string Hp(string hp)
        {
            return "<edit archive=\"" + C + "\" entry=\"" + Pet + "\"><change at=\"pet[id=100]\"><set name=\"hp\" value=\"" + hp + "\"/></change></edit>";
        }

        static int Run(out string stdout, params string[] argv)
        {
            MethodInfo main = Assembly.Load("modkit").EntryPoint;
            TextWriter o = Console.Out, e = Console.Error;
            var so = new StringWriter(); var se = new StringWriter();
            so.NewLine = "\n"; se.NewLine = "\n";
            Console.SetOut(so); Console.SetError(se);
            try { return (int)main.Invoke(null, new object[] { argv }); }
            finally { Console.SetOut(o); Console.SetError(e); stdout = so.ToString() + se.ToString(); }
        }

        [Test]
        public void ParseAndArrange()
        {
            Assert.That(ModOrder.Parse(null), Is.Empty);
            Assert.That(ModOrder.Parse(" b.two , a.one;;b.two "), Is.EqualTo(new[] { "b.two", "a.one" }));
            var mods = new List<Mod>();
            foreach (string id in new[] { "a.one", "b.two", "c.three", "d.four" }) mods.Add(new Mod { Id = id, Name = "", Version = "1" });
            // no list: the order given, the later one is the higher
            Assert.That(ModOrder.Arrange(mods, null, null), Is.EqualTo(mods));
            Assert.That(ModOrder.Text(mods), Is.EqualTo("d.four > c.three > b.two > a.one"));
            // the named ones on top, the first named the highest; the rest below in the order given
            var unknown = new List<string>();
            List<Mod> r = ModOrder.Arrange(mods, new[] { "b.two", "x.none", "d.four" }, unknown);
            Assert.That(ModOrder.Text(r), Is.EqualTo("b.two > d.four > c.three > a.one"));
            Assert.That(unknown, Is.EqualTo(new[] { "x.none" }));
            Assert.That(mods.Select(m => m.Id), Is.EqualTo(new[] { "a.one", "b.two", "c.three", "d.four" }), "the list given is not changed");
        }

        [Test]
        public void CommandLinePriority()
        {
            using (var f = new Fake())
            {
                string a = f.ModDir("a.one", Hp("11")), b = f.ModDir("b.two", Hp("22")), ws = Path.Combine(f.Dir, "ws");
                string old = Environment.GetEnvironmentVariable(ModOrder.Setting);
                try
                {
                    Environment.SetEnvironmentVariable(ModOrder.Setting, null);
                    string text;
                    // the default: the later mod is the higher one; a conflict stops the apply
                    Assert.That(Run(out text, "validate", "--client", f.ClientDir, "-q", a, b), Is.EqualTo(1), text);
                    Assert.That(text, Does.Contain("conflict, b.two wins: [a.one] " + C + " " + Pet + ": pet[id=100] @hp: \"11\" here, \"22\" in mod b.two\n"));
                    Assert.That(text, Does.Contain("priority, the highest first: b.two > a.one"));
                    Assert.That(Run(out text, "apply", "--client", f.ClientDir, "-q", "-w", ws, a, b), Is.EqualTo(1), text);
                    Assert.That(Directory.Exists(ws), Is.False, "nothing written");

                    // the option turns it round, and with conflicts allowed the higher mod's value is written
                    Assert.That(Run(out text, "validate", "--client", f.ClientDir, "-q", "--priority", "a.one", a, b), Is.EqualTo(1), text);
                    Assert.That(text, Does.Contain("conflict, a.one wins: [b.two] ").And.Contain("priority, the highest first: a.one > b.two"));
                    Assert.That(Run(out text, "apply", "--client", f.ClientDir, "-q", "-w", ws, "--allow-conflicts", "--priority", "a.one,b.two", a, b), Is.EqualTo(0), text);
                    Assert.That(Written(ws), Does.Contain("hp=\"11\""));
                    Assert.That(Run(out text, "apply", "--client", f.ClientDir, "-q", "-w", ws, "--allow-conflicts", a, b), Is.EqualTo(0), text);
                    Assert.That(Written(ws), Does.Contain("hp=\"22\""));

                    // the setting does the same; it may name a mod that is not given, the option may not
                    Environment.SetEnvironmentVariable(ModOrder.Setting, "x.none, a.one");
                    Assert.That(Run(out text, "validate", "--client", f.ClientDir, "-q", "--json", a, b), Is.EqualTo(1), text);
                    Assert.That(text.Replace(" ", "").Replace("\n", ""), Does.Contain("\"priority\":[\"a.one\",\"b.two\"]").And.Contain("\"winner\":\"a.one\""));
                    Assert.That(Run(out text, "validate", "--client", f.ClientDir, "-q", "--priority", "b.two", a, b), Is.EqualTo(1), text);
                    Assert.That(text, Does.Contain("conflict, b.two wins: "), "the option goes before the setting");
                    Assert.That(Run(out text, "validate", "--client", f.ClientDir, "-q", "--priority", "x.none", a, b), Is.EqualTo(2), text);
                    Assert.That(text, Does.Contain("--priority names x.none"));
                }
                finally { Environment.SetEnvironmentVariable(ModOrder.Setting, old); }
            }
        }

        [Test]
        public void SessionAppliesWithTheTopModWinning()
        {
            using (var f = new Fake())
            {
                string mods = Path.Combine(f.Dir, "mods"), ws = Path.Combine(f.Dir, "ws");
                f.ModDir("a.one", Hp("11"));
                f.ModDir("b.two", Hp("22"));
                using (var s = new EditorSession(f.ClientDir, f.ServerDir, mods, ws))
                {
                    // the list as it is found: a.one on top, so it is the highest
                    ApplyResult r = s.Apply(null);
                    Assert.That(r.Applied, Is.False);
                    Assert.That(r.Reason, Is.EqualTo("1 conflict(s)"));
                    Assert.That(r.Plan.Conflicts[0].WinnerId, Is.EqualTo("a.one"));
                    s.AllowConflicts = true;
                    r = s.Apply(null);
                    Assert.That(r.Applied, EditorSession.Report(r));
                    Assert.That(Written(ws), Does.Contain("hp=\"11\""));
                    // a view shows the order and the conflicts first, each conflict with its winner in front
                    Assert.That(s.Describe(r.Plan), Does.StartWith("priority, the highest first: a.one > b.two\r\nconflict, a.one wins: [b.two] "));
                    Assert.That(s.Describe(r), Does.Contain("priority, the highest first: a.one > b.two"));

                    // b.two moved to the top
                    ModList l = s.ModList();
                    l.Move(1, -1);
                    l.Save();
                    r = s.Apply(null);
                    Assert.That(r.Applied, EditorSession.Report(r));
                    Assert.That(Written(ws), Does.Contain("hp=\"22\""));
                    Assert.That(r.Plan.Conflicts[0].ToString(), Does.EndWith("; mod b.two wins (it has the higher priority)"));
                }
            }
        }

        // the pet table as the apply left it in the workspace
        static string Written(string ws)
        {
            using (var data = new DataSet())
            {
                data.AddRoot("client", Path.Combine(ws, "client"));
                M2d.M2dArchive ar = data.Open(C);
                return System.Text.Encoding.UTF8.GetString(ar.Read(ar.Find(Pet)));
            }
        }
    }
}
