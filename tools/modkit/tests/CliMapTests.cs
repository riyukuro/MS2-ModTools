// The map commands as the command line runs them: what is printed and the exit code. Made-up maps only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MS2.ModKit.MapEdit;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class CliMapTests
    {
        string dir;

        [SetUp]
        public void Make()
        {
            dir = Path.Combine(Path.GetTempPath(), "modkit-cli-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }

        [TearDown]
        public void Clean() { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

        static int Run(out string stdout, out string stderr, params string[] argv)
        {
            MethodInfo main = Assembly.Load("modkit").EntryPoint;
            TextWriter o = Console.Out, e = Console.Error;
            var so = new StringWriter(); var se = new StringWriter();
            so.NewLine = "\n"; se.NewLine = "\n";
            Console.SetOut(so); Console.SetError(se);
            try { return (int)main.Invoke(null, new object[] { argv }); }
            finally { Console.SetOut(o); Console.SetError(e); stdout = so.ToString(); stderr = se.ToString(); }
        }

        static XEntity Ent(string id, string name, int iterations)
        {
            var x = new XEntity { Id = id, Model = "MadeUpModel", Name = name, Iterations = iterations };
            return x;
        }

        string Put(string rel, IList<XEntity> ents, XDialect d)
        {
            string p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllBytes(p, XBlock.Bytes(ents, d));
            return p;
        }

        [Test]
        public void MapDiffSaysWhyWhenOnlyAProblemIsFound()
        {
            var ents = new[] { Ent("000000000000000000000000000000a1", "one", 1), Ent("000000000000000000000000000000a1", "two", 1) };
            string shipped = Put("shipped/90000001_made_up.xblock", ents, XDialect.Tab);
            string edited = Put("edited/90000001_made_up.xblock", ents, XDialect.Tab);
            string so, se;
            int rc = Run(out so, out se, "map-diff", "--id", "made-up-mod", "-o", Path.Combine(dir, "mod"), shipped, edited);
            Assert.AreEqual(1, rc, so + se);
            StringAssert.Contains("nothing to write", so);
            StringAssert.Contains("problem: 90000001_made_up: ", se);
            Assert.IsFalse(Directory.Exists(Path.Combine(dir, "mod")));
        }

        [Test]
        public void MapDiffWritesAnAddedEntityThatHasNoProperty()
        {
            string shipped = Put("shipped/90000001_made_up.xblock", new[] { Ent("000000000000000000000000000000a1", "one", 1) }, XDialect.Tab);
            string edited = Put("edited/90000001_made_up.xblock", new[] { Ent("000000000000000000000000000000a1", "one", 1), Ent("000000000000000000000000000000a4", "four", 1) }, XDialect.Tab);
            string so, se;
            int rc = Run(out so, out se, "map-diff", "--id", "made-up-mod", "-o", Path.Combine(dir, "mod"), shipped, edited);
            Assert.AreEqual(0, rc, so + se);
            StringAssert.Contains("1 added, 0 removed, 0 changed", so);
            StringAssert.Contains("byte for byte", so);
            StringAssert.Contains("order=\"\"", File.ReadAllText(Path.Combine(dir, "mod/mod.xml")));
        }

        [Test]
        public void AFolderGivesItsMapsWhateverTheLetterCaseOfTheExtension()
        {
            var ents = new[] { Ent("000000000000000000000000000000a1", "one", 1) };
            Put("in/90000001_made_up.XBLOCK", ents, XDialect.Tab);
            Put("in/90000002_made_up.xblock", ents, XDialect.Tab);
            File.WriteAllText(Path.Combine(dir, "in/notes.txt"), "made up");
            string flat = Path.Combine(dir, "game/flat");
            Directory.CreateDirectory(flat);
            File.WriteAllText(Path.Combine(flat, "made.flat"), "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<model name=\"MadeUpModel\" id=\"1\">\n</model>\n");
            string so, se, index = Path.Combine(dir, "index.json");
            Assert.AreEqual(0, Run(out so, out se, "map-index", Path.Combine(dir, "game"), index), so + se);
            Assert.AreEqual(0, Run(out so, out se, "map-to-block", Path.Combine(dir, "in"), "-o", Path.Combine(dir, "out"), "--index", index), so + se);
            var made = new List<string>();
            foreach (string f in Directory.GetFiles(Path.Combine(dir, "out"))) made.Add(Path.GetFileName(f));
            made.Sort(StringComparer.Ordinal);
            Assert.AreEqual("90000001_made_up.block|90000002_made_up.block", string.Join("|", made.ToArray()), so + se);
        }

        [Test]
        public void MapNormalizeWritesTheLayoutOfTheOriginal()
        {
            var a = Ent("000000000000000000000000000000a1", "one", 4);
            var b = Ent("000000000000000000000000000000b2", "two", 1);
            string shipped = Put("shipped/90000001_made_up.xblock", new[] { a, b }, XDialect.Spaced);
            // the editor's file: the other layout, the other order, one entity edited
            string edited = Put("edited/90000001_made_up.xblock", new[] { b, Ent(a.Id, "one", 5) }, XDialect.Tab);
            string so, se;
            Assert.AreEqual(0, Run(out so, out se, "map-normalize", edited, "-o", Path.Combine(dir, "with"), "--original", shipped), so + se);
            byte[] with = File.ReadAllBytes(Path.Combine(dir, "with/90000001_made_up.xblock"));
            Assert.AreEqual(XBlock.Bytes(new[] { Ent(a.Id, "one", 5), b }, XDialect.Spaced), with);

            // without the original, and with an --original folder that does not hold the map: the tab layout
            Assert.AreEqual(0, Run(out so, out se, "map-normalize", edited, "-o", Path.Combine(dir, "plain")), so + se);
            Assert.AreEqual(XBlock.Bytes(new[] { Ent(a.Id, "one", 5), b }, XDialect.Tab), File.ReadAllBytes(Path.Combine(dir, "plain/90000001_made_up.xblock")));
            Directory.CreateDirectory(Path.Combine(dir, "empty"));
            Assert.AreEqual(0, Run(out so, out se, "map-normalize", edited, "-o", Path.Combine(dir, "none"), "--original", Path.Combine(dir, "empty")), so + se);
            Assert.AreEqual(XBlock.Bytes(new[] { Ent(a.Id, "one", 5), b }, XDialect.Tab), File.ReadAllBytes(Path.Combine(dir, "none/90000001_made_up.xblock")));

            // and the shipped map against that file is one change that passes the check
            Assert.AreEqual(0, Run(out so, out se, "map-diff", "--id", "made-up-mod", "-o", Path.Combine(dir, "mod"), shipped, Path.Combine(dir, "with/90000001_made_up.xblock")), so + se);
            StringAssert.Contains("0 added, 0 removed, 1 changed", so);
            StringAssert.Contains("byte for byte", so);
        }
    }
}
