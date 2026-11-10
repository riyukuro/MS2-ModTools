// Checks on the real archives. Skipped unless MS2_CLIENT_DATA / MS2_SERVER_DATA are set.
// Nothing read here is written anywhere but a temp folder that is removed at the end.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Maple2.File.IO;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    public static class RealData
    {
        public static string Client => Environment.GetEnvironmentVariable("MS2_CLIENT_DATA");
        public static string Server => Environment.GetEnvironmentVariable("MS2_SERVER_DATA");

        public static string Archive(string root, string name)
        {
            string dir = root == "client" ? Client : Server;
            if (string.IsNullOrEmpty(dir)) Assert.Ignore("MS2_" + root.ToUpperInvariant() + "_DATA is not set");
            string p = Path.Combine(dir, name);
            if (!File.Exists(p)) Assert.Ignore("no " + p);
            return Local(root, p);
        }

        // The checks read every entry several times in no particular order. A copy under the temp directory
        // (kept between runs, replaced when the source changes) keeps that off a busy disk and proves that
        // nothing here needs write access to the originals.
        static string Local(string root, string src)
        {
            string baseDir = Environment.GetEnvironmentVariable("MS2_MODKIT_TMP") ?? Path.GetTempPath();
            string dir = Path.Combine(baseDir, "modkit-test-src", root);
            Directory.CreateDirectory(dir);
            string dst = Path.Combine(dir, Path.GetFileName(src));
            foreach (string ext in new[] { ".m2h", ".m2d" })
            {
                string a = Path.ChangeExtension(src, ext), b = Path.ChangeExtension(dst, ext);
                var fa = new FileInfo(a); var fb = new FileInfo(b);
                if (fb.Exists && fb.Length == fa.Length && fb.LastWriteTimeUtc == fa.LastWriteTimeUtc) continue;
                File.Copy(a, b, true);
                File.SetLastWriteTimeUtc(b, fa.LastWriteTimeUtc);
            }
            return dst;
        }

        public static string TempDir(string name)
        {
            string baseDir = Environment.GetEnvironmentVariable("MS2_MODKIT_TMP") ?? Path.GetTempPath();
            string d = Path.Combine(baseDir, "modkit-test-" + name + "-" + Process.GetCurrentProcess().Id);
            if (Directory.Exists(d)) Directory.Delete(d, true);
            Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>tools/m2d/bin/m2d.dll, searched upward from the test binary.</summary>
        public static string M2dTool()
        {
            for (string d = AppContext.BaseDirectory; d != null; d = Path.GetDirectoryName(d))
            {
                string p = Path.Combine(d, "m2d", "bin", "m2d.dll");
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }

    [TestFixture]
    public class RealArchiveTests
    {
        static readonly object[] Archives = { new[] { "client", "Xml.m2d" }, new[] { "server", "Xml.m2d" }, new[] { "server", "Server.m2d" } };

        [TestCaseSource(nameof(Archives))]
        public void ReadEqualsMaple2FileIO(string root, string name)
        {
            string path = RealData.Archive(root, name);
            var sw = Stopwatch.StartNew();
            using var mine = M2dArchive.Open(path);
            long openMs = sw.ElapsedMilliseconds;
            using var theirs = new M2dReader(path);
            var refEntries = theirs.Files.OrderBy(e => e.Index).ToList();
            Assert.That(mine.Count, Is.EqualTo(refEntries.Count));
            long bytes = 0; int empty = 0;
            for (int i = 0; i < refEntries.Count; i++)
            {
                var r = refEntries[i]; var m = mine.Entries[i];
                Assert.That(m.Index, Is.EqualTo(r.Index));
                Assert.That(m.Name, Is.EqualTo(r.Name));
                Assert.That(m.Hash ?? "", Is.EqualTo(r.Hash ?? ""));
                Assert.That(m.Flag, Is.EqualTo((uint)r.FileHeader.BufferFlag));
                Assert.That(m.Offset, Is.EqualTo(r.FileHeader.Offset));
                Assert.That(m.Size, Is.EqualTo(r.FileHeader.FileSize));
                byte[] a = mine.Read(m);
                if (r.FileHeader.FileSize == 0 || r.FileHeader.EncodedFileSize == 0) { empty++; Assert.That(a.Length, Is.EqualTo(0)); continue; }   // upstream throws on these
                byte[] b = theirs.GetBytes(r);
                if (!a.AsSpan().SequenceEqual(b)) Assert.Fail("entry " + m.Name + " differs");
                Assert.That((ulong)a.Length, Is.EqualTo(m.Size), m.Name);
                bytes += a.Length;
            }
            TestContext.Progress.WriteLine($"{root}:{name} {mine.Version}: {mine.Count} entries identical ({bytes} bytes, {empty} empty), open {openMs} ms, all {sw.ElapsedMilliseconds} ms");
        }

        // one replaced entry + one added entry, written by ModKit and by `m2d repack`: the same .m2d and .m2h bytes
        [TestCaseSource(nameof(Archives))]
        public void RewriteEqualsM2dRepack(string root, string name)
        {
            string path = RealData.Archive(root, name);
            string tool = RealData.M2dTool();
            if (tool == null) Assert.Ignore("tools/m2d is not built");
            string tmp = RealData.TempDir("repack-" + root + "-" + Path.GetFileNameWithoutExtension(name));
            try
            {
                using var src = M2dArchive.Open(path);
                M2dEntry victim = src.Entries[src.Count / 2];
                byte[] changed = src.Read(victim).Concat(Encoding.ASCII.GetBytes("<!-- modkit test -->\r\n")).ToArray();
                byte[] added = Encoding.UTF8.GetBytes("<ms2>\r\n\t<row id=\"1\" name=\"made up\" />\r\n</ms2>\r\n");
                const string addedName = "modkit_test/added.xml";

                var ch = new M2dChanges();
                ch.Set(victim.Name, changed);
                ch.Set(addedName, added);
                string mineOut = Path.Combine(tmp, "mine", name);
                var sw = Stopwatch.StartNew();
                M2dWriteResult res = M2dWriter.Rewrite(src, mineOut, ch, null);
                long mineMs = sw.ElapsedMilliseconds;
                Assert.That(res.Replaced, Is.EqualTo(1));
                Assert.That(res.Added, Is.EqualTo(1));
                Assert.That(res.Kept, Is.EqualTo(src.Count - 1));

                string srcdir = Path.Combine(tmp, "src");
                foreach (var (n, b) in new[] { (victim.Name, changed), (addedName, added) })
                {
                    string f = Path.Combine(srcdir, n.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(f));
                    File.WriteAllBytes(f, b);
                }
                string refOut = Path.Combine(tmp, "ref", name);
                Directory.CreateDirectory(Path.GetDirectoryName(refOut));
                sw.Restart();
                var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (string a in new[] { tool, "repack", path, srcdir, refOut, "--add" }) psi.ArgumentList.Add(a);
                psi.Environment["DOTNET_ROLL_FORWARD"] = "Major";
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    Assert.That(p.ExitCode, Is.EqualTo(0), o);
                }
                long refMs = sw.ElapsedMilliseconds;

                FileAssert.AreEqual(refOut, mineOut);
                FileAssert.AreEqual(Path.ChangeExtension(refOut, ".m2h"), Path.ChangeExtension(mineOut, ".m2h"));

                // and the result reads back through the reference reader
                using var back = new M2dReader(mineOut);
                Assert.That(back.GetBytes(back.Files.First(e => e.Name == victim.Name)), Is.EqualTo(changed));
                Assert.That(back.GetBytes(back.Files.First(e => e.Name == addedName)), Is.EqualTo(added));
                TestContext.Progress.WriteLine($"{root}:{name}: ModKit rewrite {mineMs} ms, m2d repack {refMs} ms, {new FileInfo(mineOut).Length} bytes, identical .m2d and .m2h");
            }
            finally { Directory.Delete(tmp, true); }
        }
    }
}
