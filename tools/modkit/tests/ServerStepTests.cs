// The server's side after an apply: the target folder and the command (ServerStep), on made-up data.
using System;
using System.IO;
using MS2.ModKit;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class ServerStepTests
    {
        const string S = "server:Xml.m2d", Pet = "table/pet.xml";

        static string Hp(string value)
        {
            return "<edit archive=\"" + S + "\" entry=\"" + Pet + "\"><change at=\"pet[id=101]\"><set name=\"hp\" value=\"" + value + "\"/></change></edit>";
        }

        // a command that prints a line and writes a marker file into the folder it runs in
        static string MarkerCommand()
        {
            return Path.DirectorySeparatorChar == '\\'
                ? "cmd /c \"echo taking {target}& echo done> marker.txt& exit 3\""
                : "/bin/sh -c \"echo taking {target}; echo done > marker.txt; exit 3\"";
        }

        [Test]
        public void NothingFollowsUntilItIsSet()
        {
            using (var f = new Fake())
            {
                f.ModDir("m.one", Hp("71"));
                using (var s = new EditorSession(f.ClientDir, f.ServerDir, Path.Combine(f.Dir, "mods"), Path.Combine(f.Dir, "ws")))
                {
                    Assert.That(s.Server.Enabled, Is.False);
                    Assert.That(EditorSession.Report(s.Plan()), Does.Not.Contain("after a successful apply"));
                    ApplyResult r = s.Apply();
                    Assert.That(r.Applied, Is.True, EditorSession.Report(r));
                    Assert.That(r.Server, Is.Null);
                }
            }
        }

        [Test]
        public void TheArchiveIsCopiedToTheTargetAndTheCommandIsRun()
        {
            using (var f = new Fake())
            {
                string target = Path.Combine(f.Dir, "live"), ws = Path.Combine(f.Dir, "ws");
                f.ModDir("m.one", Hp("71"));
                using (var s = new EditorSession(f.ClientDir, f.ServerDir, Path.Combine(f.Dir, "mods"), ws))
                {
                    s.Server.TargetDir = target;
                    s.Server.Command = MarkerCommand();
                    string plan = EditorSession.Report(s.Plan());
                    Assert.That(plan, Does.Contain("note: after a successful apply " + S + " would be copied to " + target));
                    Assert.That(plan, Does.Contain("note: after a successful apply this command would be run in " + target + ": "));
                    Assert.That(plan, Does.Contain("echo taking " + target));
                    Assert.That(Directory.Exists(target), Is.False, "a plan writes nothing");

                    ApplyResult r = s.Apply();
                    string text = EditorSession.Report(r);
                    Assert.That(r.Applied, Is.True, text);
                    Assert.That(r.Server.Copied, Does.Contain(Path.Combine(target, "Xml.m2d")));
                    Assert.That(File.ReadAllBytes(Path.Combine(target, "Xml.m2d")), Is.EqualTo(File.ReadAllBytes(Path.Combine(Path.Combine(ws, "server"), "Xml.m2d"))));
                    Assert.That(File.Exists(Path.Combine(target, "Xml.m2h")));
                    Assert.That(r.Server.Ran, Is.True, text);
                    Assert.That(r.Server.ExitCode, Is.EqualTo(3));
                    Assert.That(r.Server.Ok, Is.False);
                    Assert.That(File.ReadAllText(Path.Combine(target, "marker.txt")).Trim(), Is.EqualTo("done"));
                    Assert.That(text, Does.Contain("copied to the server target: " + Path.Combine(target, "Xml.m2d")));
                    Assert.That(text, Does.Contain("  | taking " + target));
                    Assert.That(text, Does.Contain("exit code 3 (the command reports a failure)"));

                    // a revert puts the original archive back into the target and runs the command again
                    File.Delete(Path.Combine(target, "marker.txt"));
                    r = s.Revert();
                    text = EditorSession.Report(r);
                    Assert.That(r.Server.Restored, Does.Contain(Path.Combine(target, "Xml.m2d")), text);
                    Assert.That(File.ReadAllBytes(Path.Combine(target, "Xml.m2d")), Is.EqualTo(File.ReadAllBytes(Path.Combine(f.ServerDir, "Xml.m2d"))));
                    Assert.That(File.Exists(Path.Combine(target, ServerStep.RecordFile)), Is.False);
                    Assert.That(File.Exists(Path.Combine(target, "marker.txt")), text);
                }
            }
        }

        [Test]
        public void TheTargetMayNotBeTheOriginalDataAndABadCommandIsReported()
        {
            using (var f = new Fake())
            {
                f.ModDir("m.one", Hp("71"));
                byte[] original = File.ReadAllBytes(Path.Combine(f.ServerDir, "Xml.m2d"));
                using (var s = new EditorSession(f.ClientDir, f.ServerDir, Path.Combine(f.Dir, "mods"), Path.Combine(f.Dir, "ws")))
                {
                    s.Server.TargetDir = f.ServerDir;
                    s.Server.Command = MarkerCommand();
                    ApplyResult r = s.Apply();
                    Assert.That(r.Applied, Is.True);
                    Assert.That(r.Server.Problem, Does.Contain("inside one another"));
                    Assert.That(r.Server.Ran, Is.False);
                    Assert.That(File.ReadAllBytes(Path.Combine(f.ServerDir, "Xml.m2d")), Is.EqualTo(original));

                    s.Server.TargetDir = null;
                    s.Server.Command = "\"" + Path.Combine(f.Dir, "no such program") + "\" --flag";
                    r = s.Apply();
                    Assert.That(r.Server.Ran, Is.False);
                    Assert.That(EditorSession.Report(r), Does.Contain("server step: the command could not be started"));
                }
            }
        }

        static int Cli(out string text, params string[] argv)
        {
            System.Reflection.MethodInfo main = System.Reflection.Assembly.Load("modkit").EntryPoint;
            TextWriter o = Console.Out, e = Console.Error;
            var so = new StringWriter();
            Console.SetOut(so); Console.SetError(so);
            try { return (int)main.Invoke(null, new object[] { argv }); }
            finally { Console.SetOut(o); Console.SetError(e); text = so.ToString(); }
        }

        [Test]
        public void TheCommandLineReadsOneFolderAndWritesIntoAnother()
        {
            using (var f = new Fake())
            {
                // the server's data folder is the target; the tool reads the originals from a second copy of it
                string mod = f.ModDir("m.one", Hp("71")), ws = Path.Combine(f.Dir, "ws"), live = Path.Combine(f.Dir, "live"), text;
                Directory.CreateDirectory(live);
                foreach (string n in new[] { "Xml.m2d", "Xml.m2h" }) File.Copy(Path.Combine(f.ServerDir, n), Path.Combine(live, n));
                byte[] original = File.ReadAllBytes(Path.Combine(f.ServerDir, "Xml.m2d"));

                // one folder for both is refused and nothing in it changes
                Assert.That(Cli(out text, "apply", "-w", ws, "--server", live, "--server-target", live, mod), Is.EqualTo(3));
                Assert.That(text, Does.Contain("inside one another").And.Contain("Give the tool a copy"), text);
                Assert.That(File.ReadAllBytes(Path.Combine(live, "Xml.m2d")), Is.EqualTo(original));
                Assert.That(Cli(out text, "revert", "-w", ws, "--server", live, "--server-target", live), Is.EqualTo(3), text);
                Assert.That(text, Does.Contain("inside one another"), text);

                Assert.That(Cli(out text, "apply", "-w", ws, "--server", f.ServerDir, "--server-target", live, mod), Is.EqualTo(0), text);
                Assert.That(text, Does.Contain("copied to the server target: " + Path.Combine(live, "Xml.m2d")), text);
                Assert.That(File.ReadAllBytes(Path.Combine(live, "Xml.m2d")), Is.Not.EqualTo(original));
                Assert.That(File.ReadAllBytes(Path.Combine(f.ServerDir, "Xml.m2d")), Is.EqualTo(original));

                Assert.That(Cli(out text, "revert", "-w", ws, "--server", f.ServerDir, "--server-target", live), Is.EqualTo(0), text);
                Assert.That(text, Does.Contain("put back from the original data: " + Path.Combine(live, "Xml.m2d")), text);
                Assert.That(File.ReadAllBytes(Path.Combine(live, "Xml.m2d")), Is.EqualTo(original));
            }
        }

        [Test]
        public void AClientOnlyApplyDoesNotRunTheCommand()
        {
            using (var f = new Fake())
            {
                f.ModDir("m.one", Hp("71").Replace(S, "client:Xml.m2d"));
                using (var s = new EditorSession(f.ClientDir, f.ServerDir, Path.Combine(f.Dir, "mods"), Path.Combine(f.Dir, "ws")))
                {
                    s.NoSync = true;
                    s.Server.Command = MarkerCommand();
                    Assert.That(EditorSession.Report(s.Plan()), Does.Not.Contain("after a successful apply"));
                    ApplyResult r = s.Apply();
                    Assert.That(r.Applied, Is.True, EditorSession.Report(r));
                    Assert.That(r.Server.Ran, Is.False);
                    Assert.That(r.Server.Problem, Does.Contain("no server archive changed"));
                }
            }
        }

        [Test]
        public void SplitTakesTheProgramAndTheRest()
        {
            string file, args;
            ServerStep.Split("  \"C:\\some dir\\tool.exe\" --in \"a b\" c ", out file, out args);
            Assert.That(file, Is.EqualTo("C:\\some dir\\tool.exe"));
            Assert.That(args, Is.EqualTo("--in \"a b\" c"));
            ServerStep.Split("tool", out file, out args);
            Assert.That(file, Is.EqualTo("tool"));
            Assert.That(args, Is.EqualTo(""));
        }
    }
}
