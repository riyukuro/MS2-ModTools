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
using MS2.ModKit.Xml;
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
        // The check the plan asks for: one attribute of one row changed in a table that is in the client and in the
        // server Xml.m2d, applied to copies, read back; every other entry and every other byte of the table is as before.
        [Test]
        public void OneAttributeOnBothSides()
        {
            string client = RealData.Archive("client", "Xml.m2d"), server = RealData.Archive("server", "Xml.m2d");
            string tmp = RealData.TempDir("oneattr");
            try
            {
                using var data = new DataSet(Path.GetDirectoryName(client), Path.GetDirectoryName(server));
                string[] refs = { "client:Xml.m2d", "server:Xml.m2d" };
                M2dArchive ca = data.Open(refs[0]), sa = data.Open(refs[1]);

                // the first table of both sides whose first row has a whole-number attribute after its first one
                string entry = null, attr = null; var at = new string[2]; var oldValue = new string[2];
                foreach (M2dEntry e in ca.Entries)
                {
                    if (!e.Name.StartsWith("table/") || !e.Name.EndsWith(".xml") || sa.Find(e.Name) == null) continue;
                    XmlDoc cd, sd;
                    try { cd = XmlDoc.Parse(ca.Read(e), e.Name); sd = XmlDoc.Parse(sa.Read(e.Name), e.Name); }
                    catch (ModKitException) { continue; }
                    if (cd.Root == null || sd.Root == null || cd.Root.Children.Count == 0) continue;
                    XmlElem row = cd.Root.Children[0];
                    string sel = RowPath.Canonical(row);
                    string why;
                    XmlElem srow = RowPath.Parse(sel).TryResolve(sd.Root, out why);
                    if (srow == null) continue;
                    foreach (XmlAttr a in row.Attrs.Skip(1))
                    {
                        long n;
                        if (!long.TryParse(a.Value, out n) || n < 0 || n > 1000000 || srow.Attr(a.Name) == null || sel.Contains("[" + a.Name + "=")) continue;
                        entry = e.Name; attr = a.Name; at[0] = sel; at[1] = sel; oldValue[0] = a.Value; oldValue[1] = srow.Attr(a.Name);
                        break;
                    }
                    if (entry != null) break;
                }
                Assert.That(entry, Is.Not.Null, "no table fits");
                const string newValue = "1234567";

                var sb = new StringBuilder("<mod id=\"test.oneattr\" name=\"one attribute\" version=\"1\">\n");
                for (int i = 0; i < 2; i++)
                    sb.Append("  <edit archive=\"" + refs[i] + "\" entry=\"" + entry + "\">\n    <change at=\"" + XmlDoc.EscapeAttr(at[i], '"') + "\"><set name=\"" + attr + "\" value=\"" + newValue + "\"/></change>\n  </edit>\n");
                sb.Append("</mod>\n");
                Directory.CreateDirectory(Path.Combine(tmp, "mod"));
                File.WriteAllText(Path.Combine(tmp, "mod", "mod.xml"), sb.ToString());
                Mod mod = Mod.Load(Path.Combine(tmp, "mod"));

                var sw = Stopwatch.StartNew();
                Plan plan = Planner.Build(data, new[] { mod }, null);
                long planMs = sw.ElapsedMilliseconds;
                Assert.That(plan.Errors, Is.Empty);
                Assert.That(plan.Changes.Count, Is.EqualTo(2));
                foreach (PlanChange c in plan.Changes) TestContext.Progress.WriteLine("diff: " + c);

                var ws = new Workspace(Path.Combine(tmp, "ws"));
                sw.Restart();
                ApplyResult res = ws.Apply(data, new[] { mod }, new ApplyOptions());
                long applyMs = sw.ElapsedMilliseconds;
                Assert.That(res.Applied, Is.True, res.Reason);
                sw.Restart();
                var ws2 = new Workspace(Path.Combine(tmp, "ws-verify-all"));
                ApplyResult res2 = ws2.Apply(data, new[] { mod }, new ApplyOptions { VerifyAll = true });
                long applyAllMs = sw.ElapsedMilliseconds;
                Assert.That(res2.Applied, Is.True, res2.Reason);

                for (int i = 0; i < 2; i++)
                {
                    M2dArchive orig = data.Open(refs[i]);
                    FileAssert.AreEqual(ws.PathOf(refs[i]), ws2.PathOf(refs[i]));
                    using var made = M2dArchive.Open(ws.PathOf(refs[i]));
                    Assert.That(made.Version, Is.EqualTo(orig.Version));
                    Assert.That(made.Count, Is.EqualTo(orig.Count));
                    int same = 0;
                    for (int k = 0; k < orig.Count; k++)
                    {
                        M2dEntry o = orig.Entries[k], m = made.Entries[k];
                        Assert.That(m.Name, Is.EqualTo(o.Name));
                        Assert.That(m.Index, Is.EqualTo(o.Index));
                        if (string.Equals(o.Name, entry, StringComparison.OrdinalIgnoreCase)) continue;
                        Assert.That(m.Flag, Is.EqualTo(o.Flag), o.Name);
                        Assert.That(m.Size, Is.EqualTo(o.Size), o.Name);
                        if (!made.ReadRaw(m).AsSpan().SequenceEqual(orig.ReadRaw(o))) Assert.Fail(refs[i] + " " + o.Name + ": stored bytes changed");
                        same++;
                    }
                    Assert.That(same, Is.EqualTo(orig.Count - 1));

                    // the table: the same bytes but for the one value, and the reference reader agrees
                    byte[] before = orig.Read(entry), after = made.Read(entry);
                    int pre = 0;
                    while (pre < before.Length && pre < after.Length && before[pre] == after[pre]) pre++;
                    int suf = 0;
                    while (suf < before.Length - pre && suf < after.Length - pre && before[before.Length - 1 - suf] == after[after.Length - 1 - suf]) suf++;
                    Assert.That(before.Length - pre - suf, Is.LessThanOrEqualTo(oldValue[i].Length), "bytes of the old value");
                    Assert.That(after.Length - pre - suf, Is.LessThanOrEqualTo(newValue.Length), "bytes of the new value");
                    Assert.That(after.Length - before.Length, Is.EqualTo(newValue.Length - oldValue[i].Length));
                    XmlDoc d = XmlDoc.Parse(after, entry);
                    Assert.That(RowPath.Parse(at[i]).Resolve(d.Root).Attr(attr), Is.EqualTo(newValue));
                    using (var theirs = new M2dReader(ws.PathOf(refs[i])))
                        Assert.That(theirs.GetBytes(theirs.Files.First(f => f.Name == made.Find(entry).Name)), Is.EqualTo(after));
                    ApplyOutput o1 = res.Outputs[i], o2 = res2.Outputs[i];
                    TestContext.Progress.WriteLine($"{refs[i]} {entry}: @{attr} changed in 1 row; {same} other entries have the same stored bytes; rewrite {o1.Milliseconds} ms (read back {o1.Verified}), with VerifyAll {o2.Milliseconds} ms (read back {o2.Verified})");
                }
                TestContext.Progress.WriteLine($"plan {planMs} ms, apply both archives {applyMs} ms, apply with VerifyAll {applyAllMs} ms");

                // revert: nothing of the mod is left
                ws.Revert(data, null, null);
                Assert.That(File.Exists(ws.PathOf(refs[0])), Is.False);
            }
            finally { Directory.Delete(tmp, true); }
        }
    }
}
