// The object id checks of the lint (lint.py --xblock, --strict) against tools/trigger/lint.py: made-up map files
// (flat/ and xblock/), indexed by xblockindex.py for lint.py and read by TriggerXBlocks for ours, the same findings.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class TriggerXBlockLintTests
    {
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";
        const string Map = "90000001_tq", NoMap = "90000002_none";

        static string Flat(string name, string idProp, string def, params string[] mixins)
        {
            var sb = new StringBuilder(Head + "<model name=\"" + name + "\" id=\"1\">\n");
            int id = 2;
            foreach (string m in mixins) sb.Append("\t<mixin name=\"" + m + "\" id=\"" + (id++) + "\" />\n");
            sb.Append("\t<property name=\"Position\" type=\"Point3\" id=\"" + (id++) + "\"><set value=\"0, 0, 0\" /></property>\n");
            if (idProp != null)
                sb.Append("\t<property name=\"" + idProp + "\" type=\"SInt32\" id=\"" + (id++) + "\"><set value=\"" + def + "\" /></property>\n");
            return sb.Append("</model>\n").ToString();
        }

        static readonly string[,] Flats =
        {
            { "flat/t/triggerbox_a.flat", Flat("TriggerBox_A", "TriggerObjectID", "0", "MS2TriggerObject", "MS2TriggerBox") },
            { "flat/t/mesh_a.flat", Flat("Mesh_A", "TriggerObjectID", "0", "MS2TriggerObject", "MS2TriggerMesh") },
            // the id of an agent that sets none comes from its model
            { "flat/t/agent_a.flat", Flat("Agent_A", "TriggerObjectID", "7001", "MS2TriggerObject", "MS2TriggerAgent") },
            { "flat/s/spawnnpc_a.flat", Flat("SpawnNpc_A", "SpawnPointID", "0", "SpawnPoint", "SpawnPointNPC") },
            { "flat/s/spawnevent_a.flat", Flat("SpawnEvent_A", "SpawnPointID", "0", "SpawnNpc_A", "EventSpawnPointNPC") },
            { "flat/b/breakable_a.flat", Flat("Breakable_A", "TriggerBreakableID", "0", "MS2BreakableActor") },
            { "flat/p/portal_a.flat", Flat("Portal_A", "PortalID", "0", "Portal") },
            { "flat/i/interact_a.flat", Flat("Interact_A", "interactID", "0", "MS2InteractObject") },
            { "flat/m/triggermodel_a.flat", Flat("TriggerModel_A", "TriggerModelID", "0", "MS2TriggerModel") },
            { "flat/x/rock_a.flat", Flat("Rock_A", null, null, "PhysXProp") },   // no kind: not indexed
        };

        static string Ent(string model, string name, params string[] props)
        {
            var sb = new StringBuilder("    <entity id=\"" + Guid.NewGuid().ToString("N").ToUpperInvariant() + "\" modelName=\"" + model + "\" name=\"" + name + "\" iterations=\"1\"");
            if (props.Length == 0) return sb.Append(" />\n").ToString();
            sb.Append(">\n");
            for (int i = 0; i < props.Length; i += 2)
                sb.Append("      <property name=\"" + props[i] + "\">\n        <set value=\"" + props[i + 1] + "\" />\n      </property>\n");
            return sb.Append("    </entity>\n").ToString();
        }

        static readonly string XBlock = Head + "<game>\n  <entitySet>\n"
            + Ent("TriggerBox_A", "Box101", "Position", "1, 2, 3", "TriggerObjectID", "101")
            + Ent("Mesh_A", "Mesh3001", "TriggerObjectID", "3001")
            + Ent("Mesh_A", "Mesh3002", "TriggerObjectID", "3002.0")
            + Ent("Agent_A", "Agent7001")
            + Ent("SpawnNpc_A", "Npc201", "SpawnPointID", "201")
            + Ent("SpawnEvent_A", "Event202", "SpawnPointID", "202")
            + Ent("Breakable_A", "Break401", "TriggerBreakableID", "401")
            + Ent("MS2BreakableNIF", "Nif402", "TriggerBreakableID", "402")
            + Ent("Portal_A", "Portal5", "PortalID", "5")
            + Ent("Interact_A", "Lever10003", "interactID", "10003")
            + Ent("TriggerModel_A", "Model_Main", "TriggerModelID", "9001", "XmlFilePath", @".\Data\Xml\Trigger\90000001_tq\Main.xml")
            + Ent("Rock_A", "Rock1", "TriggerObjectID", "555")
            + "  </entitySet>\n</game>\n";

        const string MainXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<ms2>
  <state name=""Wait"">
    <condition name=""user_detected"" box_ids=""101,999"">
      <transition state=""Act"" />
    </condition>
  </state>
  <state name=""Act"">
    <onEnter>
      <action name=""set_agent"" trigger_ids=""7001"" visible=""1"" />
      <action name=""set_agent"" trigger_ids=""3001"" visible=""1"" />
      <action name=""set_breakable"" trigger_ids=""401,402,3002,0"" enable=""1"" />
      <action name=""spawn_monster"" spawn_ids=""201,202"" auto_target=""1"" />
      <action name=""set_dialogue"" type=""1"" spawn_id=""555"" script=""x"" />
      <action name=""set_dialogue"" type=""2"" spawn_id=""556"" script=""y"" />
      <action name=""move_to_portal"" portal_id=""5"" box_id=""101"" />
      <action name=""move_to_portal"" portal_id=""6"" box_id=""5"" />
      <action name=""set_interact_object"" trigger_ids=""10010-10000"" state=""1"" />
      <action name=""create_item"" spawn_ids=""all"" />
    </onEnter>
  </state>
</ms2>
";

        const string PlainXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<ms2>
  <state name=""Only"">
    <onEnter>
      <action name=""set_agent"" trigger_ids=""7001"" visible=""0"" />
    </onEnter>
  </state>
</ms2>
";

        static string Write(string root, string rel, string text)
        {
            string p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text, new UTF8Encoding(false));
            return p;
        }

        // root/trigger (the scripts), root/ext (the map files), root/cache (xblockindex.py's index)
        static string MakeRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "modkit-trigxb-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            for (int i = 0; i < Flats.GetLength(0); i++) Write(root, "ext/" + Flats[i, 0], Flats[i, 1]);
            Write(root, "ext/xblock/" + Map + ".xblock", XBlock);
            Write(root, "trigger/" + Map + "/main.xml", MainXml);
            Write(root, "trigger/" + Map + "/orphan.xml", PlainXml);       // no TriggerModel names it
            Write(root, "trigger/" + NoMap + "/plain.xml", PlainXml);      // no such xblock
            return root;
        }

        static void Index(string repo, string root)
        {
            var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string x in new[] { "-I", Path.Combine(repo, "tools", "trigger", "xblockindex.py"), "build", "--exported", "unused.m2d",
                "--from-dir", Path.Combine(root, "ext"), "--cache", Path.Combine(root, "cache") })
                psi.ArgumentList.Add(x);
            Process started;
            try { started = Process.Start(psi); }
            catch (System.ComponentModel.Win32Exception) { Assert.Ignore("python3 is not on PATH; the comparison with lint.py is skipped"); return; }
            using (Process p = started)
            {
                p.StandardOutput.ReadToEnd();
                string e = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.That(p.ExitCode, Is.EqualTo(0), e);
            }
        }

        static string Diff(List<string> py, List<string> ours)
        {
            var onlyPy = new List<string>(py);
            foreach (string s in ours) onlyPy.Remove(s);
            var onlyOurs = new List<string>(ours);
            foreach (string s in py) onlyOurs.Remove(s);
            return onlyPy.Count + onlyOurs.Count == 0 ? "" : "only lint.py:\n  " + string.Join("\n  ", onlyPy) + "\nonly ours:\n  " + string.Join("\n  ", onlyOurs);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TheSameObjectFindingsAsLintPy(bool strict)
        {
            string repo = TriggerLintTests.Repo(), root = MakeRoot();
            try
            {
                Index(repo, root);
                TriggerXBlocks xb = TriggerXBlocks.OfFolder(Path.Combine(root, "ext"));
                TriggerLint lint = TriggerLintTests.WithAllow(null);
                Func<string, TriggerLintOptions> opt = delegate(string entry)
                {
                    string x = Triggers.XBlockOf(entry);
                    return new TriggerLintOptions { XBlock = x, Objects = xb.Get(x), Strict = strict };
                };
                List<string> py = strict ? TriggerLintTests.Python(repo, root, false, "--xblock", "auto", "--strict") : TriggerLintTests.Python(repo, root, false, "--xblock", "auto");
                List<TriggerFinding> all;
                List<string> ours = TriggerLintTests.Ours(lint, root, opt, out all);
                Assert.That(Diff(py, ours), Is.Empty);

                string main = "trigger/" + Map + "/main.xml|";
                string[] want =
                {
                    main + "4|warning|ref-missing|condition user_detected box_ids: box id(s) 999 not in xblock " + Map,
                    main + "11|warning|ref-kind|action set_agent trigger_ids: id(s) 3001 are mesh, not agent",
                    main + "12|" + (strict ? "error" : "warning") + "|maple2-gap|Maple2 server (WIP) currently action set_breakable trigger_ids: id(s) 402 are MS2BreakableNIF objects, "
                        + "which the ingest does not export (case commented out in MapEntityMapper), so the server finds nothing",
                    main + "12|warning|ref-kind|action set_breakable trigger_ids: id(s) 3002 are mesh, not breakable",
                    main + "13|warning|ref-kind|action spawn_monster spawn_ids: id(s) 201 are npc_spawn, not event_npc_spawn",
                    main + "14|warning|ref-missing|action set_dialogue spawn_id: npc_spawn/event_npc_spawn/pc_spawn/region_spawn id(s) 555 not in xblock " + Map,
                    main + "17|warning|ref-missing|action move_to_portal portal_id: portal id(s) 6 not in xblock " + Map,
                    main + "17|warning|ref-kind|action move_to_portal box_id: id(s) 5 are portal, not box",
                    main + "18|warning|ref-missing|action set_interact_object trigger_ids: interact id(s) 10000,10001,10002,10004,10005,10006,10007,10008,...(10) not in xblock " + Map,
                    "trigger/" + Map + "/orphan.xml|0|warning|no-trigger-model|no TriggerModel in xblock " + Map + " names orphan.xml; the script is never loaded",
                    "trigger/" + NoMap + "/plain.xml|0|warning|xblock-missing|xblock '" + NoMap + "' is not in the index; object IDs not checked",
                };
                foreach (string w in want) Assert.That(ours, Has.Member(w), string.Join("\n", ours));
                Assert.That(ours.Count(s => s.Contains("556")), Is.EqualTo(0), "set_dialogue type 2: spawn_id is no object id");
                Assert.That(ours.Count(s => s.StartsWith(main + "10|") || s.StartsWith(main + "19|")), Is.EqualTo(0), "the agent of its model's id; 'all'");
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void TheIndexOfTheMapFiles()
        {
            string root = MakeRoot();
            try
            {
                TriggerXBlocks xb = TriggerXBlocks.OfFolder(Path.Combine(root, "ext"));
                Assert.That(xb.Get("xblock/" + Map.ToUpperInvariant() + ".xblock"), Is.SameAs(xb.Get(Map)));
                Assert.That(xb.Get(NoMap), Is.Null);
                TriggerObjects o = xb.Get(Map);
                Assert.That(o.Has("agent", 7001), Is.True);
                Assert.That(o.Has("mesh", 3002), Is.True);
                Assert.That(o.Has("breakable_nif", 402), Is.True);
                Assert.That(o.KindsOf(555), Is.Empty, "a model without a kind is not indexed");
                Assert.That(o.LoadsScript("main"), Is.True);
                Assert.That(o.LoadsScript("orphan"), Is.False);
            }
            finally { Directory.Delete(root, true); }
        }

        static int Cli(out string stdout, out string stderr, params string[] argv)
        {
            MethodInfo main = Assembly.Load("modkit").EntryPoint;
            TextWriter o = Console.Out, e = Console.Error;
            var so = new StringWriter(); var se = new StringWriter();
            so.NewLine = "\n"; se.NewLine = "\n";
            Console.SetOut(so); Console.SetError(se);
            try { return (int)main.Invoke(null, new object[] { argv }); }
            finally { Console.SetOut(o); Console.SetError(e); stdout = so.ToString(); stderr = se.ToString(); }
        }

        [Test]
        public void TheCommandLineChecksTheObjects()
        {
            string root = MakeRoot();
            try
            {
                string so, se;
                int rc = Cli(out so, out se, "trigger-lint", root, "--xblock", "auto");
                Assert.That(rc, Is.EqualTo(2));
                StringAssert.Contains("--exported", se);

                rc = Cli(out so, out se, "trigger-lint", root, "--xblock", "auto", "--exported", Path.Combine(root, "ext"), "--json");
                Assert.That(rc, Is.EqualTo(0), so + se);
                StringAssert.Contains("\"code\": \"ref-kind\"", so);
                StringAssert.Contains("\"code\": \"no-trigger-model\"", so);
                StringAssert.Contains("\"code\": \"xblock-missing\"", so);

                // one named xblock for every script, and --strict
                rc = Cli(out so, out se, "trigger-lint", root, "--xblock", "xblock/" + Map + ".xblock", "--exported", Path.Combine(root, "ext"), "--strict", "-q");
                Assert.That(rc, Is.EqualTo(1), so + se);
                StringAssert.Contains("maple2-gap", so);
                Assert.That(so, Does.Not.Contain("xblock-missing"));

                // a loose .xblock
                string loose = Path.Combine(root, "ext", "xblock", Map + ".xblock");
                rc = Cli(out so, out se, "trigger-lint", root, "--xblock", loose, "--exported", Path.Combine(root, "ext"), "--json");
                Assert.That(rc, Is.EqualTo(0), so + se);
                StringAssert.Contains("not in xblock " + Map, so);
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
