// Local-only check on a real client (not part of the default test run; skipped when the variable is not set):
//   MS2_CLIENT_DATA      the client's Data folder; its Resource/Exported.m2d is read, never written
//   MS2_MAP_PYTHON_DIR   optional: a folder <map>/<map>.block and <map>/out/<map>.xblock made by the scripts under
//                        tools/mapedit; its maps are the sample then, and the files must be the same bytes
// Without the second variable the sample is 20 maps spread evenly over the archive.
using System;
using System.Collections.Generic;
using System.IO;
using MS2.ModKit.M2d;
using MS2.ModKit.MapEdit;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class RealArchiveMapsTests
    {
        // the on-demand listing of the planner finds a model only in the files of its name
        [Test]
        public void EveryModelFileIsNamedAfterItsModel()
        {
            string data = RealData.Client;
            if (string.IsNullOrEmpty(data)) Assert.Ignore("MS2_CLIENT_DATA is not set");
            string path = Path.Combine(Path.Combine(data, "Resource"), "Exported.m2d");
            if (!File.Exists(path)) Assert.Ignore("no " + path);
            using (M2dArchive arch = M2dArchive.Open(path))
            {
                FlatIndex full = FlatIndex.FromArchive(arch), lazy = FlatIndex.FromArchiveOnDemand(arch);
                Assert.That(full.Count, Is.GreaterThan(1000));
                var wrong = new List<string>();
                foreach (string n in full.Names)
                {
                    FlatModel m = full.Get(n), l = lazy.Get(n);
                    if (l == null || l.File != m.File || l.Props.Count != m.Props.Count || l.Mixins.Count != m.Mixins.Count) wrong.Add(m.File);
                }
                Assert.That(wrong, Is.Empty);
            }
        }

        [Test]
        public void TwentyMapsRoundTrip()
        {
            string data = RealData.Client;
            if (string.IsNullOrEmpty(data)) Assert.Ignore("MS2_CLIENT_DATA is not set");
            string path = Path.Combine(Path.Combine(data, "Resource"), "Exported.m2d");
            if (!File.Exists(path)) Assert.Ignore("no " + path);
            string python = Environment.GetEnvironmentVariable("MS2_MAP_PYTHON_DIR");
            string tmp = RealData.TempDir("maps");
            try
            {
                using (M2dArchive arch = M2dArchive.Open(path))
                {
                    FlatIndex ix = FlatIndex.FromArchive(arch);
                    Assert.That(ix.Count, Is.GreaterThan(1000));
                    var names = new List<string>();
                    if (!string.IsNullOrEmpty(python))
                    {
                        foreach (string d in Directory.GetDirectories(python)) names.Add(Path.GetFileName(d));
                        names.Sort(StringComparer.Ordinal);
                    }
                    else
                    {
                        var all = new List<string>();
                        foreach (M2dEntry e in arch.Entries)
                            if (e.Name.StartsWith("xblock/", StringComparison.OrdinalIgnoreCase) && e.Name.EndsWith(".xblock", StringComparison.OrdinalIgnoreCase))
                                all.Add(e.Name.Substring(7, e.Name.Length - 14));
                        all.Sort(StringComparer.Ordinal);
                        for (int i = 0; i < 20 && all.Count > 0; i++) names.Add(all[(int)((long)i * all.Count / 20)]);
                    }
                    Assert.That(names.Count, Is.EqualTo(20));
                    int same = 0;
                    foreach (string name in names)
                    {
                        byte[] shipped = arch.Read("xblock/" + name + ".xblock");
                        XDialect dialect = XBlock.DetectDialect(shipped);
                        BlockDoc blk = MapConvert.ToBlock(XBlock.Read(shipped, name), name, ix, new ToBlockOptions(), new ConvertReport());
                        string file = Path.Combine(tmp, name + ".block");
                        blk.Write(file);
                        List<KeyValuePair<string, List<XEntity>>> back = MapConvert.ToXBlocks(BlockDoc.Read(file), ix, PropOrder.Block);
                        Assert.That(back.Count, Is.EqualTo(1), name);
                        byte[] again = XBlock.Bytes(back[0].Value, dialect);
                        Assert.That(again, Is.EqualTo(shipped), name + ": game map -> editor map -> game map");
                        if (!string.IsNullOrEmpty(python))
                        {
                            string dir = Path.Combine(python, name);
                            Assert.That(File.ReadAllBytes(file), Is.EqualTo(File.ReadAllBytes(Path.Combine(dir, name + ".block"))), name + ": editor map as the script writes it");
                            Assert.That(again, Is.EqualTo(File.ReadAllBytes(Path.Combine(Path.Combine(dir, "out"), name + ".xblock"))), name + ": game map as the script writes it");
                        }
                        same++;
                    }
                    Assert.That(same, Is.EqualTo(20));
                    TestContext.Progress.WriteLine("maps round trip: " + same + "/20 the same bytes" + (string.IsNullOrEmpty(python) ? "" : ", and the same as the scripts' files"));
                }
            }
            finally { Directory.Delete(tmp, true); }
        }
    }
}
