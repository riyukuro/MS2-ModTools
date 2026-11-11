// The object id checks of the lint (lint.py --xblock, --strict) against tools/trigger/lint.py: made-up map files
// (flat/ and xblock/), indexed by xblockindex.py for lint.py and read by TriggerXBlocks for ours, the same findings.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using MS2.ModKit.M2d;
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

        static string Dump(TriggerXBlocks xb, string name)
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, List<long>> k in xb.Get(name).Ids())
            {
                sb.Append(k.Key).Append(':');
                foreach (long id in k.Value) sb.Append(' ').Append(id);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        [Test]
        public void TheIndexCacheOfAFolderRebuildsWhenAFileChanges()
        {
            string root = MakeRoot();
            try
            {
                string ext = Path.Combine(root, "ext"), cache = Path.Combine(root, "ws", "cache");
                TriggerXBlocks plain = TriggerXBlocks.OfFolder(ext);
                TriggerXBlocks first = TriggerXBlocks.Cached(ext, cache);
                Assert.That(first.FromCache, Is.False);
                Assert.That(File.Exists(first.CacheFile), Is.True);
                Assert.That(Path.GetDirectoryName(first.CacheFile), Is.EqualTo(Path.GetFullPath(cache)));

                TriggerXBlocks second = TriggerXBlocks.Cached(ext, cache);
                Assert.That(second.FromCache, Is.True);
                Assert.That(Dump(second, Map), Is.EqualTo(Dump(plain, Map)));
                Assert.That(second.Get(NoMap), Is.Null);
                Assert.That(second.Get(Map).LoadsScript("main"), Is.True);
                Assert.That(second.Get(Map).LoadsScript("orphan"), Is.False);
                Assert.That(second.ModelCount, Is.EqualTo(plain.ModelCount));
                // a loose xblock is read with the cached model kinds
                byte[] xbBytes = File.ReadAllBytes(Path.Combine(ext, "xblock", Map + ".xblock"));
                Assert.That(Dump2(second.Read(xbBytes, "loose")), Is.EqualTo(Dump2(plain.Read(xbBytes, "loose"))));

                // the lint gives the same findings either way
                foreach (string script in new[] { "main.xml", "orphan.xml" })
                {
                    string entry = "trigger/" + Map + "/" + script;
                    byte[] b = File.ReadAllBytes(Path.Combine(root, entry.Replace('/', Path.DirectorySeparatorChar)));
                    string f;
                    var a = TriggerLint.Shipped.Lint(entry, b, null, new TriggerLintOptions { ScanImporters = false, XBlock = Map, Objects = plain.Get(Map) }, out f);
                    var c = TriggerLint.Shipped.Lint(entry, b, null, new TriggerLintOptions { ScanImporters = false, XBlock = Map, Objects = second.Get(Map) }, out f);
                    Assert.That(string.Join("\n", c.ConvertAll(x => x.ToString()).ToArray()), Is.EqualTo(string.Join("\n", a.ConvertAll(x => x.ToString()).ToArray())));
                }

                // an xblock changes: one more object (the file's time moves on, as an edit does)
                string xbFile = Path.Combine(ext, "xblock", Map + ".xblock");
                File.WriteAllText(xbFile, XBlock.Replace("  </entitySet>", Ent("Mesh_A", "Mesh3999", "TriggerObjectID", "3999") + "  </entitySet>"), new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(xbFile, DateTime.UtcNow.AddMinutes(1));
                TriggerXBlocks third = TriggerXBlocks.Cached(ext, cache);
                Assert.That(third.FromCache, Is.False, "a changed xblock rebuilds the index");
                Assert.That(third.Get(Map).Has("mesh", 3999), Is.True);
                Assert.That(TriggerXBlocks.Cached(ext, cache).Get(Map).Has("mesh", 3999), Is.True);
                Assert.That(TriggerXBlocks.Cached(ext, cache).FromCache, Is.True);

                // a new xblock file: rebuilt and found
                Write(root, "ext/xblock/" + NoMap + ".xblock", XBlock);
                TriggerXBlocks fourth = TriggerXBlocks.Cached(ext, cache);
                Assert.That(fourth.FromCache, Is.False, "a new xblock rebuilds the index");
                Assert.That(fourth.Get(NoMap), Is.Not.Null);
                Assert.That(Directory.GetFiles(cache).Length, Is.EqualTo(1), "one cache file per source, rewritten");

                // never in the folder of the source
                Assert.Throws<ModKitException>(delegate { TriggerXBlocks.Cached(ext, Path.Combine(ext, "cache")); });
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void TheIndexCacheRebuildsForADeletedOrRenamedXBlockAndAChangedFlat()
        {
            string root = MakeRoot();
            try
            {
                string ext = Path.Combine(root, "ext"), cache = Path.Combine(root, "ws", "cache");
                Write(root, "ext/xblock/" + NoMap + ".xblock", XBlock);
                Assert.That(TriggerXBlocks.Cached(ext, cache).Get(NoMap), Is.Not.Null);
                Assert.That(TriggerXBlocks.Cached(ext, cache).FromCache, Is.True);

                // deleted
                File.Delete(Path.Combine(ext, "xblock", NoMap + ".xblock"));
                TriggerXBlocks a = TriggerXBlocks.Cached(ext, cache);
                Assert.That(a.FromCache, Is.False, "a deleted xblock rebuilds the index");
                Assert.That(a.Get(NoMap), Is.Null);
                Assert.That(TriggerXBlocks.Cached(ext, cache).Get(NoMap), Is.Null);

                // renamed (the time stays)
                const string Renamed = "90000003_moved";
                File.Move(Path.Combine(ext, "xblock", Map + ".xblock"), Path.Combine(ext, "xblock", Renamed + ".xblock"));
                TriggerXBlocks b = TriggerXBlocks.Cached(ext, cache);
                Assert.That(b.FromCache, Is.False, "a renamed xblock rebuilds the index");
                Assert.That(b.Get(Map), Is.Null);
                Assert.That(b.Get(Renamed).Has("mesh", 3001), Is.True);

                // a flat changes: Mesh_A is no trigger mesh now, so its objects are no meshes
                string flat = Path.Combine(ext, "flat", "t", "mesh_a.flat");
                File.WriteAllText(flat, Flat("Mesh_A", "TriggerObjectID", "0", "MS2TriggerObject", "MS2TriggerBox"), new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(flat, DateTime.UtcNow.AddMinutes(1));
                TriggerXBlocks c = TriggerXBlocks.Cached(ext, cache);
                Assert.That(c.FromCache, Is.False, "a changed flat rebuilds the index");
                Assert.That(Dump(c, Renamed), Is.EqualTo(Dump(TriggerXBlocks.OfFolder(ext), Renamed)));
                Assert.That(c.Get(Renamed).Has("mesh", 3001), Is.False);
                Assert.That(Dump(TriggerXBlocks.Cached(ext, cache), Renamed), Is.EqualTo(Dump(c, Renamed)));
                Assert.That(Directory.GetFiles(cache).Length, Is.EqualTo(1), "no temp file is left");
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void ACacheFileCutShortReadsAsNoCache()
        {
            string root = MakeRoot();
            try
            {
                string ext = Path.Combine(root, "ext"), cache = Path.Combine(root, "ws", "cache");
                TriggerXBlocks first = TriggerXBlocks.Cached(ext, cache);
                string file = first.CacheFile;
                string whole = File.ReadAllText(file);
                StringAssert.EndsWith("\n", whole);
                StringAssert.Contains("\nend ", whole);
                // cut at every line end before the last: never served
                string[] lines = whole.TrimEnd('\n').Split('\n');
                for (int n = 3; n < lines.Length; n++)
                {
                    File.WriteAllText(file, string.Join("\n", lines, 0, n) + "\n", new UTF8Encoding(false));
                    TriggerXBlocks x = TriggerXBlocks.Cached(ext, cache);
                    Assert.That(x.FromCache, Is.False, "cut after " + n + " lines");
                    Assert.That(File.ReadAllText(file), Is.EqualTo(whole), "rebuilt whole");
                }
                // a count that does not fit, or a line after the end
                File.WriteAllText(file, whole.Replace("\nend ", "\nend 9"), new UTF8Encoding(false));
                Assert.That(TriggerXBlocks.Cached(ext, cache).FromCache, Is.False);
                File.WriteAllText(file, whole + "x 99999999_more\n", new UTF8Encoding(false));
                Assert.That(TriggerXBlocks.Cached(ext, cache).FromCache, Is.False);
                Assert.That(TriggerXBlocks.Cached(ext, cache).FromCache, Is.True);
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void AnXBlockThatDoesNotReadIsAFindingAndTheRestIsChecked()
        {
            string root = MakeRoot();
            try
            {
                string ext = Path.Combine(root, "ext"), cache = Path.Combine(root, "ws", "cache");
                Write(root, "ext/xblock/" + NoMap + ".xblock", Head + "<game>\n  <entitySet>\n    <entity id=\"1\"");
                foreach (bool cached in new[] { false, true })
                {
                    TriggerXBlocks x = TriggerXBlocks.Cached(ext, cache);
                    Assert.That(x.FromCache, Is.EqualTo(cached));
                    Assert.That(x.Get(Map).Has("mesh", 3001), Is.True, "the other xblocks are indexed");
                    TriggerObjects o;
                    string why;
                    Assert.That(x.TryGet(NoMap, out o, out why), Is.False);
                    StringAssert.Contains(NoMap, why);
                    Assert.That(x.Broken.Count, Is.EqualTo(1));
                    Assert.Throws<ModKitException>(delegate { x.Get(NoMap); });

                    string entry = "trigger/" + NoMap + "/plain.xml", f;
                    byte[] b = File.ReadAllBytes(Path.Combine(root, "trigger", NoMap, "plain.xml"));
                    var found = TriggerLint.Shipped.Lint(entry, b, null, new TriggerLintOptions { ScanImporters = false, XBlock = NoMap, Objects = o, XBlockError = why }, out f);
                    Assert.That(found.Exists(y => y.Code == "xblock-unreadable" && y.Level == "warning"), Is.True,
                        string.Join("\n", found.ConvertAll(y => y.ToString()).ToArray()));
                    Assert.That(found.Exists(y => y.Code == "xblock-missing"), Is.False);
                }
                // the lazy reader (no cache) the same
                TriggerXBlocks lazy = TriggerXBlocks.OfFolder(ext);
                TriggerObjects lo;
                string lw;
                Assert.That(lazy.TryGet(NoMap, out lo, out lw), Is.False);
                Assert.That(lazy.Get(Map), Is.Not.Null);

                // the command line lints every script and names the broken xblock
                string so, se;
                int rc = Cli(out so, out se, "trigger-lint", root, "--xblock", "auto", "--exported", ext, "--cache", cache, "--json");
                Assert.That(rc, Is.EqualTo(0), so + se);
                StringAssert.Contains("xblock-unreadable", so);
                StringAssert.Contains("trigger/" + Map + "/main.xml", so);
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void TheCacheStaysOutOfTheGameAndDataFolders()
        {
            string root = MakeRoot();
            try
            {
                string game = Path.Combine(root, "game"), m2d = Path.Combine(game, "Data", "Resource", "Exported.m2d");
                Directory.CreateDirectory(Path.GetDirectoryName(m2d));
                var entries = new List<KeyValuePair<string, byte[]>>();
                for (int i = 0; i < Flats.GetLength(0); i++) entries.Add(new KeyValuePair<string, byte[]>(Flats[i, 0], Encoding.UTF8.GetBytes(Flats[i, 1])));
                entries.Add(new KeyValuePair<string, byte[]>("xblock/" + Map + ".xblock", Encoding.UTF8.GetBytes(XBlock)));
                M2dWriter.Create(m2d, PackVersion.MS2F, M2dFlag.AesZlib, entries);
                foreach (string bad in new[] { Path.Combine(game, "Data", "Resource", "cache"), Path.Combine(game, "Data", "cache"), Path.Combine(game, "cache"), game })
                {
                    Assert.Throws<ModKitException>(delegate { TriggerXBlocks.Cached(m2d, bad); }, bad);
                    Assert.That(Directory.Exists(Path.Combine(game, "cache")) || Directory.Exists(Path.Combine(game, "Data", "cache")), Is.False);
                }
                // a keep-out folder (a data root, the game folder) and the game folder above a Data root
                string ext = Path.Combine(root, "ext"), other = Path.Combine(root, "data2");
                Assert.Throws<ModKitException>(delegate { TriggerXBlocks.Cached(ext, Path.Combine(other, "cache"), new[] { other }); });
                Assert.Throws<ModKitException>(delegate { TriggerXBlocks.Cached(ext, Path.Combine(game, "cache"), new[] { Path.Combine(game, "Data") }); });
#if !NET35
                // through a link to the game folder
                string link = Path.Combine(root, "ws-link");
                Directory.CreateSymbolicLink(link, game);
                Assert.Throws<ModKitException>(delegate { TriggerXBlocks.Cached(m2d, Path.Combine(link, "cache")); });
#endif
                // the command line: --cache <game>/Data/cache and -w <game>
                string so, se;
                int rc = Cli(out so, out se, "trigger-lint", root, "--xblock", "auto", "--exported", m2d, "--cache", Path.Combine(game, "Data", "cache"));
                Assert.That(rc, Is.Not.EqualTo(0), so + se);
                StringAssert.Contains("index cache", se);
                rc = Cli(out so, out se, "trigger-lint", root, "--xblock", "auto", "--exported", m2d, "-w", game);
                Assert.That(rc, Is.Not.EqualTo(0), so + se);
                StringAssert.Contains("index cache", se);
                Assert.That(Directory.Exists(Path.Combine(game, "cache")), Is.False);
                // a workspace beside the game is fine
                Directory.CreateDirectory(Path.Combine(root, "ws"));
                rc = Cli(out so, out se, "trigger-lint", root, "--xblock", "auto", "--exported", m2d, "-w", Path.Combine(root, "ws"));
                Assert.That(rc, Is.EqualTo(0), so + se);
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void ACacheThatCannotBeWrittenLeavesTheBuiltIndex()
        {
            string root = MakeRoot();
            try
            {
                string ext = Path.Combine(root, "ext"), ws = Path.Combine(root, "ws");
                Directory.CreateDirectory(ws);
                // a file where the cache folder goes
                string cache = Path.Combine(ws, "cache");
                File.WriteAllText(cache, "not a folder");
                TriggerXBlocks x = TriggerXBlocks.Cached(ext, cache);
                Assert.That(x.FromCache, Is.False);
                Assert.That(x.CacheFile, Is.Null);
                Assert.That(x.WriteError, Is.Not.Null);
                Assert.That(x.Get(Map).Has("mesh", 3001), Is.True);

                string so, se;
                int rc = Cli(out so, out se, "trigger-lint", root, "--xblock", "auto", "--exported", ext, "-w", ws, "--json");
                Assert.That(rc, Is.EqualTo(0), so + se);
                StringAssert.Contains("was not written", se);
                Assert.That(Directory.GetFiles(ws).Length, Is.EqualTo(1), "no temp file is left");
            }
            finally { Directory.Delete(root, true); }
        }

        static string Dump2(TriggerObjects o)
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, List<long>> k in o.Ids()) sb.Append(k.Key).Append(':').Append(string.Join(",", k.Value.ConvertAll(x => x.ToString()).ToArray())).Append('\n');
            return sb.ToString();
        }

        [Test]
        public void TheIndexCacheOfAnArchiveRebuildsWhenItChanges()
        {
            string root = MakeRoot();
            try
            {
                string ext = Path.Combine(root, "ext"), m2d = Path.Combine(root, "game", "Exported.m2d"), cache = Path.Combine(root, "ws", "cache");
                Directory.CreateDirectory(Path.GetDirectoryName(m2d));
                var entries = new List<KeyValuePair<string, byte[]>>();
                for (int i = 0; i < Flats.GetLength(0); i++) entries.Add(new KeyValuePair<string, byte[]>(Flats[i, 0], Encoding.UTF8.GetBytes(Flats[i, 1])));
                entries.Add(new KeyValuePair<string, byte[]>("xblock/" + Map + ".xblock", Encoding.UTF8.GetBytes(XBlock)));
                M2dWriter.Create(m2d, PackVersion.MS2F, M2dFlag.AesZlib, entries);
                string want = Dump(TriggerXBlocks.OfFolder(ext), Map);

                TriggerXBlocks a = TriggerXBlocks.Cached(m2d, cache);
                Assert.That(a.FromCache, Is.False);
                Assert.That(Dump(a, Map), Is.EqualTo(want));
                TriggerXBlocks b = TriggerXBlocks.Cached(M2dArchive.Open(m2d), cache);
                Assert.That(b.FromCache, Is.True, "the path and the opened archive share the cache file");
                Assert.That(Dump(b, Map), Is.EqualTo(want));

                entries.Add(new KeyValuePair<string, byte[]>("xblock/" + NoMap + ".xblock", Encoding.UTF8.GetBytes(XBlock)));
                M2dWriter.Create(m2d, PackVersion.MS2F, M2dFlag.AesZlib, entries);
                File.SetLastWriteTimeUtc(m2d, DateTime.UtcNow.AddMinutes(1));
                TriggerXBlocks c = TriggerXBlocks.Cached(m2d, cache);
                Assert.That(c.FromCache, Is.False, "a rewritten archive rebuilds the index");
                Assert.That(c.Get(NoMap), Is.Not.Null);
                Assert.That(TriggerXBlocks.Cached(m2d, cache).FromCache, Is.True);

                // the command line: -w puts it in the workspace's cache/
                string so, se;
                string ws = Path.Combine(root, "ws2");
                // -w names a workspace that is there; it is not made
                int rc = Cli(out so, out se, "trigger-lint", root, "--xblock", "auto", "--exported", m2d, "-w", ws, "--json");
                Assert.That(rc, Is.Not.EqualTo(0), so + se);
                Assert.That(Directory.Exists(ws), Is.False);
                Directory.CreateDirectory(ws);
                rc = Cli(out so, out se, "trigger-lint", root, "--xblock", "auto", "--exported", m2d, "-w", ws, "--json");
                Assert.That(rc, Is.EqualTo(0), so + se);
                StringAssert.Contains("xblock index: built into " + Path.Combine(ws, "cache"), se);
                string so2;
                rc = Cli(out so2, out se, "trigger-lint", root, "--xblock", "auto", "--exported", m2d, "-w", ws, "--json");
                StringAssert.Contains("xblock index: read from ", se);
                Assert.That(so2, Is.EqualTo(so));
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void TheMapFilesComeFromARootThatHasThem()
        {
            string root = MakeRoot();
            try
            {
                string c = Path.Combine(root, "client"), sv = Path.Combine(root, "server");
                Directory.CreateDirectory(Path.Combine(c, "Resource"));
                Directory.CreateDirectory(sv);
                using (var data = new DataSet(c, sv))
                {
                    Assert.That(Triggers.ExportedOf(data, "server:Xml.m2d"), Is.Null, "no root has the map files");
                    File.WriteAllBytes(Path.Combine(c, "Resource", "Exported.m2d"), new byte[0]);
                    Assert.That(Triggers.ExportedOf(data, "server:Xml.m2d"), Is.EqualTo("client:Resource/Exported.m2d"), "the server's scripts take the client's map files");
                    Directory.CreateDirectory(Path.Combine(sv, "Resource"));
                    File.WriteAllBytes(Path.Combine(sv, "Resource", "Exported.m2d"), new byte[0]);
                    Assert.That(Triggers.ExportedOf(data, "server:Xml.m2d"), Is.EqualTo("server:Resource/Exported.m2d"), "the archive's own root first");
                    Assert.That(Triggers.ExportedOf(data, "client:Xml.m2d"), Is.EqualTo("client:Resource/Exported.m2d"));
                }
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
