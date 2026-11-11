using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    // The lint port against tools/trigger/lint.py: the same made-up scripts through both, the same findings.
    [TestFixture]
    public class TriggerLintTests
    {
        // made-up scripts: trigger/<folder>/<file> -> text
        static readonly string[,] Scripts =
        {
            { "trigger/fake_clean/door.xml", @"<?xml version=""1.0"" encoding=""utf-8""?>
<ms2>
  <state name=""Wait"">
    <condition name=""user_detected"" box_ids=""101"">
      <transition state=""Open"" />
    </condition>
  </state>
  <state name=""Open"">
    <onEnter>
      <action name=""set_mesh"" trigger_ids=""3001,3002"" visible=""1"" />
      <action name=""set_timer"" timer_id=""1"" seconds=""5"" />
    </onEnter>
    <condition name=""time_expired"" timer_id=""1"">
      <transition state=""Wait"" />
    </condition>
    <onExit>
      <action name=""set_mesh"" trigger_ids=""3001"" visible=""0"" />
    </onExit>
  </state>
</ms2>" },
            { "trigger/fake_server/bad.xml", @"<ms2>
  <state name=""Start"">
    <onEnter>
      <action name=""no_such_action"" arg1=""1"" />
      <action name=""set_mesh"" visible=""maybe"" />
      <action name=""set_timer"" timer_id=""1"" seconds=""five"" />
      <action name=""announce"" />
      <action name=""set_mesh"" trigger_ids=""1,x,2-3,4-y"" />
      <action name=""set_mesh"" trigger_ids=""abc"" />
      <action name=""move_user_to_pos"" pos=""1,2"" />
      <action name=""add_cinematic_talk"" npc_id=""1"" align=""diagonal"" />
      <action name=""set_skip"" state=""Nowhere"" />
      <action name=""set_skip"" state=""1st place"" />
      <note>ignored</note>
      <transition state=""Gone"" />
    </onEnter>
    <condition name=""no_such_condition"">
      <transition state=""Next"" />
    </condition>
    <condition name=""time_expired"" />
    <condition name=""user_value"" key=""k"" value=""1"" operator=""Greater"">
      <transition state=""Next"" />
    </condition>
    <condition name=""all_of"">
      <transition state=""Next"" />
    </condition>
    <condition name=""any_one"">
      <group>
        <condition name=""wait_tick"" wait_tick=""10"" />
        <condition name=""bogus_inner"" />
        <transition state=""Next"" />
      </group>
      <transition state=""Missing"" />
    </condition>
    <condition name=""wait_tick"" wait_tick=""10"" extraArg=""1"">
      <transition state=""Next"" />
      <odd />
    </condition>
    <stray />
  </state>
  <state name=""Next"">
    <onExit><transition state=""Start"" /></onExit>
  </state>
  <state name=""Next"" />
  <state />
  <state name=""Island"" />
  <ignoredTop />
</ms2>" },
            { "trigger/fake_server/empty.xml", "<ms2></ms2>" },
            { "trigger/fake_server/wrongroot.xml", "<trigger><state name=\"a\" /></trigger>" },
            { "trigger/fake_server/broken.xml", "<ms2><state name=\"a\"></ms2>" },
            // what expat refuses and XmlDoc reads (a bare '&' or a '<' in a value), and the other expat errors
            { "trigger/fake_server/amp.xml", "<ms2>\n  <state name=\"a&b\" />\n</ms2>\n" },
            { "trigger/fake_server/lt.xml", "<ms2>\n  <state name=\"a\">\n    <action name=\"x<y\" />\n  </state>\n</ms2>\n" },
            { "trigger/fake_server/entity.xml", "<ms2>\n  <state name=\"&nbsp;\" />\n</ms2>\n" },
            { "trigger/fake_server/dup.xml", "<ms2>\n  <state name=\"a\" name=\"b\" />\n</ms2>\n" },
            { "trigger/fake_server/open.xml", "<ms2>\n  <state name=\"a\">\n  </state>\n" },
            { "trigger/fake_server/junk.xml", "<ms2 />\n<ms2 />\n" },
            { "trigger/fake_server/comment.xml", "<ms2>\n  <!-- a -- b -->\n</ms2>\n" },
            { "trigger/fake_server/noquote.xml", "<ms2>\n  <state name=a />\n</ms2>\n" },
            { "trigger/fake_server/before.xml", "text\n<ms2 />\n" },
            { "trigger/fake_server/decl.xml", "\n<?xml version=\"1.0\"?>\n<ms2 />\n" },
            // a raw script that imports a file expat does not read
            { "trigger/fake_raw/usesbroken.xml", "<ms2>\n  <import path=\"Data/Xml/Trigger/fake_raw/broken_lib.xml\" />\n  <state name=\"Start\">\n    <onEnter>\n      <action name=\"SetTimer\" arg1=\"1\" arg2=\"5\" />\n    </onEnter>\n  </state>\n</ms2>\n" },
            { "trigger/fake_raw/broken_lib.xml", "<ms2>\n  <state name=\"L\">\n    <onEnter>\n  </state>\n</ms2>\n" },
            { "trigger/fake_server/imports.xml", "<ms2><import path=\"x.xml\" /><state name=\"a\" /></ms2>" },
            // raw (shipped) form: CamelCase names, arg1.., `!Name`, numbered state names, imports, features
            { "trigger/fake_raw/main.xml", @"<ms2>
  <import path=""Data/Xml/Trigger/fake_raw/lib.xml"" />
  <import path=""./nowhere.xml"" />
  <state name=""1st-wait"">
    <onEnter>
      <action name=""SetTimer"" arg1=""1"" arg2=""3"" />
      <action name=""ArcadeBoomBoomOcean"" type=""EndGame"" />
      <action name=""ArcadeBoomBoomOcean"" type=""Bogus"" />
      <action name=""DungeonVariable"" arg1=""1"" />
      <action name=""SetMesh"" arg1=""1"" feature=""SomeFeature"" />
    </onEnter>
    <condition name=""!UserDetected"" arg1=""101"">
      <transition state=""Shared"" />
    </condition>
    <condition name=""TimeExpired"" arg1=""1"">
      <transition state=""2nd step"" />
    </condition>
  </state>
  <state name=""2nd step"">
    <condition name=""WaitTick"" arg1=""100"">
      <transition state=""1st-wait"" />
    </condition>
  </state>
  <state name=""Lone"" feature=""SomeFeature"" />
</ms2>" },
            { "trigger/fake_raw/lib.xml", @"<ms2>
  <state name=""Shared"">
    <onEnter><action name=""SetMesh"" arg1=""1"" arg2=""0"" bogus=""1"" /></onEnter>
    <condition name=""WaitTick"" arg1=""10"">
      <transition state=""Back"" />
    </condition>
  </state>
</ms2>" },
            { "trigger/fake_raw/other.xml", @"<ms2>
  <import path=""lib.xml"" />
  <state name=""Begin"">
    <condition name=""WaitTick"" arg1=""10""><transition state=""Shared"" /></condition>
  </state>
  <state name=""Back"" />
</ms2>" },
        };

        const string AllowJson = @"{""comment"": ""made up"", ""entries"": [
  {""code"": ""unknown-action"", ""path"": ""fake_server/bad.xml"", ""reason"": ""made-up reason""},
  {""code"": ""maple2-gap"", ""path"": ""fake_*/*.xml"", ""match"": ""DungeonVariable"", ""reason"": ""another""}
]}";

        internal static string Repo()
        {
            string dir = TestContext.CurrentContext.TestDirectory;
            for (int i = 0; i < 10 && dir != null; i++, dir = Path.GetDirectoryName(dir))
                if (File.Exists(Path.Combine(dir, "tools", "trigger", "lint.py"))) return dir;
            Assert.Fail("tools/trigger/lint.py not found above the test folder");
            return null;
        }

        static string Folder(out string root)
        {
            root = Path.Combine(Path.GetTempPath(), "modkit-triglint-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            for (int i = 0; i < Scripts.GetLength(0); i++)
            {
                string p = Path.Combine(root, Scripts[i, 0].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, Scripts[i, 1], new UTF8Encoding(false));
            }
            File.WriteAllText(Path.Combine(root, "allow.json"), AllowJson);
            return root;
        }

        internal static string Key(string path, int line, string level, string code, string msg)
        {
            return path + "|" + line + "|" + level + "|" + code + "|" + msg;
        }

        internal static List<string> Python(string repo, string root, bool allow, params string[] extra)
        {
            var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
            psi.ArgumentList.Add("-I");
            psi.ArgumentList.Add(Path.Combine(repo, "tools", "trigger", "lint.py"));
            psi.ArgumentList.Add("--json");
            psi.ArgumentList.Add("--allow");
            psi.ArgumentList.Add(allow ? Path.Combine(root, "allow.json") : "");
            psi.ArgumentList.Add("--cache");
            psi.ArgumentList.Add(Path.Combine(root, "cache"));
            foreach (string x in extra) psi.ArgumentList.Add(x);
            psi.ArgumentList.Add(Path.Combine(root, "trigger"));
            var l = new List<string>();
            Process started;
            // without python3 (a Windows dev box) there is nothing to compare with
            try { started = Process.Start(psi); }
            catch (System.ComponentModel.Win32Exception) { Assert.Ignore("python3 is not on PATH; the comparison with lint.py is skipped"); return null; }
            using (Process p = started)
            {
                string o = p.StandardOutput.ReadToEnd();
                string e = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.That(p.ExitCode, Is.LessThanOrEqualTo(1), e);
                foreach (string line in o.Split('\n'))
                {
                    if (line.Trim().Length == 0) continue;
                    using (JsonDocument d = JsonDocument.Parse(line))
                    {
                        JsonElement r = d.RootElement;
                        string path = Path.GetRelativePath(root, r.GetProperty("path").GetString()).Replace(Path.DirectorySeparatorChar, '/');
                        l.Add(Key(path, r.GetProperty("line").GetInt32(), r.GetProperty("level").GetString(), r.GetProperty("code").GetString(), r.GetProperty("msg").GetString()));
                    }
                }
            }
            l.Sort(StringComparer.Ordinal);
            return l;
        }

        static List<string> Ours(TriggerLint lint, string root, out List<TriggerFinding> all)
        {
            return Ours(lint, root, null, out all);
        }

        /// <summary>The findings of the folder; options (by entry) give the xblock check and strict, null: none.</summary>
        internal static List<string> Ours(TriggerLint lint, string root, Func<string, TriggerLintOptions> options, out List<TriggerFinding> all)
        {
            TriggerPacks packs = TriggerPacks.OfFolder(root);
            var l = new List<string>();
            all = new List<TriggerFinding>();
            foreach (string n in packs.Names)
            {
                string format;
                foreach (TriggerFinding f in options == null ? lint.Lint(n, packs.Read(n), packs) : lint.Lint(n, packs.Read(n), packs, options(n), out format))
                {
                    all.Add(f);
                    l.Add(Key(f.Entry, f.Line, f.Level, f.Code, f.Message));
                }
            }
            l.Sort(StringComparer.Ordinal);
            return l;
        }

        internal static TriggerLint WithAllow(string json)
        {
            return new TriggerLint(TriggerLint.Shipped.Schema, TriggerLint.ParseAllow(json));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TheSameFindingsAsLintPy(bool allow)
        {
            string repo = Repo(), root;
            string dir = Folder(out root);
            try
            {
                List<string> py = Python(repo, root, allow);
                List<TriggerFinding> all;
                List<string> ours = Ours(WithAllow(allow ? AllowJson : null), root, out all);
                Assert.That(py.Count, Is.GreaterThan(30), "lint.py found too little: " + string.Join("\n", py));
                var onlyPy = new List<string>(py);
                foreach (string s in ours) onlyPy.Remove(s);
                var onlyOurs = new List<string>(ours);
                foreach (string s in py) onlyOurs.Remove(s);
                Assert.That(onlyPy.Count + onlyOurs.Count, Is.EqualTo(0),
                    "only lint.py:\n  " + string.Join("\n  ", onlyPy) + "\nonly ours:\n  " + string.Join("\n  ", onlyOurs));
                if (allow) Assert.That(all.Exists(f => f.Level == "allowed" && f.AllowReason == "made-up reason"));
                // expat's words and lines, in the script and in an imported file
                foreach (string m in new[] { "fake_server/amp.xml|2|error|parse|XML parse error: not well-formed (invalid token)",
                    "fake_server/entity.xml|2|error|parse|XML parse error: undefined entity", "fake_server/open.xml|4|error|parse|XML parse error: no element found",
                    "fake_raw/usesbroken.xml|2|error|import|import 'Data/Xml/Trigger/fake_raw/broken_lib.xml': trigger/fake_raw/broken_lib.xml parse error line 4: mismatched tag" })
                    Assert.That(ours, Has.Some.EndsWith(m));
                // every check is exercised
                foreach (string code in new[] { "unknown-action", "unknown-condition", "missing-arg", "bad-transition", "unreachable", "state-dup",
                    "state-noname", "ignored-node", "group-missing", "no-states", "root", "parse", "import", "ingest", "lib-transition", "int-list-dropped",
                    "skip-state-missing", "maple2-gap" }) // import-ignored needs a forced server format; auto mode reads an import as raw
                    Assert.That(all.Exists(f => f.Code == code), code);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void TheShippedAllowlistAndSchemaLoad()
        {
            TriggerLint l = TriggerLint.Shipped;
            Assert.That(l.Allow.Count, Is.GreaterThan(100));
            // a clean server-form script has no findings
            List<TriggerFinding> f = l.Lint(Scripts[0, 0], Encoding.UTF8.GetBytes(Scripts[0, 1]), null);
            Assert.That(f, Is.Empty, string.Join("\n", f));
        }

        [Test]
        public void TheIngestNames()
        {
            Assert.That(TriggerNormalizer.ToSnakeCase("SetNPCEmotionloop"), Is.EqualTo("set_npc_emotion_loop"));
            Assert.That(TriggerNormalizer.FixClassName("1st-wait"), Is.EqualTo("FirstTowait"));
            Assert.That(TriggerNormalizer.FixClassName("None"), Is.EqualTo("StateNone"));
            Assert.That(TriggerNormalizer.FixClassName("10"), Is.EqualTo("State10"));
            Assert.That(TriggerNormalizer.FixClassName("2nd step"), Is.EqualTo("Second_step"));
            Assert.That(TriggerNormalizer.ImportPackPath(".\\Data\\Xml\\Trigger\\a\\b.xml"), Is.EqualTo("trigger/a/b.xml"));
            Assert.That(TriggerNormalizer.ImportPackPath("b.xml"), Is.EqualTo("trigger/b.xml"));
        }
    }
}
