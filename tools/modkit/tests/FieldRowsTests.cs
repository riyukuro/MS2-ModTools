// Field rows (FieldRows.cs) on a made-up field table and made-up map files of a source client.
// Every id, name and value in here is made up.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class FieldRowsTests
    {
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";
        const string Kms = "client:Xml.m2d", Gms = "gms-client:Xml.m2d";

        // ours: 10 and 50 open, 19 empty (self-closing), 30 with an environment that has no xblock name, 77 empty with
        // no source file
        const string Table = Head + "<ms2>\n" +
            "\t<fieldData id=\"10\">\n\t\t<environment>\n\t\t\t<xblock name=\"Ten_A\" />\n\t\t</environment>\n\t</fieldData>\n" +
            "\t<fieldData id=\"19\" />\n" +
            "\t<fieldData id=\"30\">\n\t\t<environment feature=\"FakeOn\">\n\t\t\t<xblock name=\"\" />\n\t\t</environment>\n\t</fieldData>\n" +
            "\t<fieldData id=\"50\">\n\t\t<environment>\n\t\t\t<xblock name=\"Fifty\" />\n\t\t</environment>\n\t</fieldData>\n" +
            "\t<fieldData id=\"77\" />\n" +
            "</ms2>\n";

        const string Setting = Head + "<ms2>\n\t<setting type=\"Dev\" KR=\"99\" />\n\t<setting type=\"Live\" KR=\"78\" />\n</ms2>\n";
        const string Features = Head + "<ms2>\n\t<feature name=\"FakeOn\" KR=\"1\" />\n\t<feature name=\"FakeOff\" KR=\"99\" />\n\t<feature name=\"Develop\" KR=\"99\" />\n</ms2>\n";

        static string Map(string envs) { return Head + "<!-- made up -->\n<ms2>\n" + envs + "</ms2>\n"; }

        static string Env(string attrs, string xblock, string body = "")
        {
            return "\t<environment " + attrs + ">\n\t\t<xblock name=\"" + xblock + "\" />\n" + body + "\t</environment>\n";
        }

        // the source's full form: every attribute written, most of them the default
        const string Full =
            "\t\t<property continentCode=\"102\" regionCode=\"\" mapType=\"5\" bigCity=\"0\" revivalreturnid=\"00000019\" enterreturnid=\"0\" useTimeEvent=\"0\" homeReturnable=\"0\" popupMenu=\"0\" checkClimb=\"1\" limitContent=\"128\" />\n" +
            "\t\t<worldmap x=\"1\" />\n" +
            "\t\t<drop maplevel=\"10\" dropRank=\"\" globalDropBoxID=\"4001\" />\n" +
            "\t\t<indoor type=\"1\" />\n" +
            "\t\t<optimize fastglow=\"1\" fastshimmer=\"1\" />\n" +
            "\t\t<bgm id=\"47\" param=\"5\" value=\"0\" />\n" +
            "\t\t<hdr threshold=\"0.27\" offset=\"30\" middlegray=\"0.51\" bloomscale=\"2\" glowscale=\"0.1\" />\n" +
            "\t\t<camera distance=\"900\" hangle=\"45\" vangle=\"30\" useFileSetting=\"1\" />\n" +
            "\t\t<ui bgMask=\"m&amp;n\" vscroll=\"1\" usePet=\"0\" isNoSkipFrame=\"0\" showHitEffect=\"1\" />\n" +
            "\t\t<survival a=\"1\" />\n";

        sealed class Data : IDisposable
        {
            public readonly string Dir;
            public Data()
            {
                Dir = Path.Combine(Path.GetTempPath(), "modkit-field-" + Guid.NewGuid().ToString("N"));
                string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
                foreach (string d in new[] { k, g, s, Path.Combine(k, "Resource") }) Directory.CreateDirectory(d);
                M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                {
                    E("table/fielddata.xml", Table), E("table/feature_setting.xml", Setting), E("table/feature.xml", Features),
                });
                M2dWriter.Create(Path.Combine(k, "Resource", "Exported.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                {
                    X("Ten_A"), X("Fifty"), X("00000019_BF"), X("Thirty"), X("Forty"), X("FortyOne"), X("FortyTwo"), X("Five"), X("Sixty"), X("NoName"),
                });
                M2dWriter.Create(Path.Combine(g, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                {
                    E("map/00000010.xml", Map(Env("feature=\"\" locale=\"\"", "Ten_A"))),
                    E("map/00000019.xml", Map(Env("feature=\"FakeOff\" locale=\"\"", "00000019_BF", Full))),
                    E("map/00000030.xml", Map(Env("feature=\"FakeOn\" locale=\"\"", "Thirty", "\t\t<fov value=\"50\" />\n"))),
                    E("map/00000040.xml", Map(Env("feature=\"\" locale=\"NA\"", "Other") + Env("feature=\"Unknown\" locale=\"KR\"", "Forty"))),
                    E("map/00000041.xml", Map(Env("feature=\"\" locale=\"\"", "FortyOne"))),
                    E("map/00000042.xml", Map(Env("feature=\"Develop\" locale=\"\"", "FortyTwo"))),
                    E("map/00000005.xml", Map(Env("feature=\"\" locale=\"\"", "Five"))),
                    E("map/00000060.xml", Map(Env("feature=\"\" locale=\"\"", "Sixty") + Env("feature=\"FakeOn\" locale=\"\"", "Sixty"))),
                    E("map/00000061.xml", Map(Env("feature=\"\" locale=\"\"", "NotShipped"))),
                    E("map/00000062.xml", Map(Env("feature=\"\" locale=\"\"", ""))),
                    E("map/00000063.xml", Head + "<ms2>\n\t<environment>\n</ms2>\n"),
                    E("map/sub/00000064.xml", Map(Env("", "Ten_A"))),
                    E("map/readme.xml", Map("")),
                });
                M2dWriter.Create(Path.Combine(s, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                {
                    E("table/none.xml", Head + "<ms2 />\n"), E("map/00000005.xml", Map("")), E("map/00000019.xml", Map("")),
                    E("map/00000030.xml", Map("")), E("map/00000040.xml", Map("")),
                    E("map/00000042.xml", Map("")),
                });
            }
            public void Dispose() { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); }
        }

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }
        static KeyValuePair<string, byte[]> X(string name) { return new KeyValuePair<string, byte[]>("xblock/" + name + ".xblock", new byte[] { 1, 2, 3 }); }

        static EditorSession Session(string dir)
        {
            return new EditorSession(Path.Combine(dir, "kms"), Path.Combine(dir, "gms"), Path.Combine(dir, "server"), Path.Combine(dir, "mods"), Path.Combine(dir, "ws"));
        }

        [Test]
        public void Compact_DropsDefaultsEmptyAndUnread()
        {
            XmlDoc d = XmlDoc.Parse(Encoding.UTF8.GetBytes(Map(Env("feature=\"FakeOff\" locale=\"\"", "00000019_BF", Full))), "t");
            int attrs;
            string c = FieldRows.Compact(d.Root.Children[0], out attrs);
            Assert.That(c, Is.EqualTo("<environment>\n" +
                "\t<xblock name=\"00000019_BF\" />\n" +
                "\t<property continentCode=\"102\" mapType=\"5\" revivalreturnid=\"19\" homeReturnable=\"0\" popupMenu=\"false\" limitContent=\"128\" />\n" +
                "\t<drop maplevel=\"10\" />\n" +
                "\t<indoor type=\"1\" />\n" +
                "\t<bgm id=\"47\" param=\"5\" />\n" +
                "\t<camera distance=\"900\" useFileSetting=\"true\" />\n" +
                "\t<ui bgMask=\"m&amp;n\" usePet=\"0\" isNoSkipFrame=\"false\" />\n" +
                "</environment>"));
            Assert.That(attrs, Is.EqualTo(16));
            Assert.That(FieldRows.CompactValue("property", "enterreturnid", "0002"), Is.EqualTo("2"));
            Assert.That(FieldRows.CompactValue("property", "enterreturnid", "000"), Is.Null, "0 is the default");
            Assert.That(FieldRows.CompactValue("xblock", "name", "0002"), Is.EqualTo("0002"));
            Assert.That(FieldRows.CompactValue("worldmap", "x", "1"), Is.Null);
            Assert.That(FieldRows.FileOf(2000385), Is.EqualTo("map/02000385.xml"));
        }

        [Test]
        public void Scan_Classes()
        {
            using (var d = new Data())
            using (EditorSession s = Session(d.Dir))
            {
                FieldRows fr = FieldRows.Scan(s.Data, Kms, Gms);
                Assert.That(fr.Open, Is.EqualTo(1), "10; 50 has no source file");
                Assert.That(fr.NoSource, Is.EqualTo(new[] { 77 }));
                Assert.That(fr.Rows.Select(r => r.Id + " " + r.Kind), Is.EqualTo(new[]
                {
                    "5 Add", "19 Fill", "30 Fill", "40 Add", "41 Add", "42 Add", "60 Several", "61 NoMap", "62 NoXBlock", "63 Error",
                }));
                FieldRow r19 = fr.Rows.Single(r => r.Id == 19);
                Assert.That(r19.At, Is.EqualTo("fieldData[id=19]"));
                Assert.That(r19.Feature + " " + r19.FeatureOn + " " + r19.FeatureLevel, Is.EqualTo("FakeOff False 99"));
                Assert.That(r19.File, Is.EqualTo("map/00000019.xml"));
                Assert.That(r19.XBlock, Is.EqualTo("00000019_BF"));
                Assert.That(r19.Xml, Does.StartWith("<environment>\n\t<xblock name=\"00000019_BF\" />"));
                FieldRow r40 = fr.Rows.Single(r => r.Id == 40);
                Assert.That(r40.Environments, Is.EqualTo(1), "the NA environment is not ours");
                Assert.That(r40.Feature + " " + r40.FeatureOn, Is.EqualTo("Unknown False"));
                Assert.That(r40.After, Is.EqualTo("fieldData[id=30]"));
                Assert.That(r40.Xml, Is.EqualTo("<fieldData id=\"40\">\n\t<environment>\n\t\t<xblock name=\"Forty\" />\n\t</environment>\n</fieldData>"));
                FieldRow r5 = fr.Rows.Single(r => r.Id == 5);
                Assert.That(r5.After, Is.Null);
                Assert.That(r5.Before, Is.EqualTo("fieldData[id=10]"));
                Assert.That(fr.Rows.Single(r => r.Id == 30).FeatureOn, Is.True);
                Assert.That(fr.Rows.Single(r => r.Id == 60).Environments, Is.EqualTo(2));
                Assert.That(fr.Rows.Single(r => r.Id == 63).Error, Is.Not.Null);
                Assert.That(fr.Rows.Where(r => r.Ticked).Select(r => r.Id), Is.EqualTo(new[] { 5, 19, 30, 40, 41, 42 }));
                Assert.That(fr.Rows.Where(r => r.Default).Select(r => r.Id), Is.EqualTo(new[] { 5, 19, 30, 40 }), "the server has no map 41; 42 is a Develop map");
                Assert.That(fr.Rows.Single(r => r.Id == 41).OnServer, Is.False);
            }
        }

        [Test]
        public void Put_PlanApply_ThenTakeOut()
        {
            using (var d = new Data())
            using (EditorSession s = Session(d.Dir))
            {
                Mod m = s.NewMod("t.fields");
                FieldRows fr = FieldRows.Scan(s.Data, Kms, Gms);
                var fills = fr.Rows.Where(r => r.Ticked).ToList();
                Assert.That(fr.Put(m, fr.Rows), Is.EqualTo(6), "only Fill and Add rows");
                Assert.That(fr.Put(m, fills), Is.EqualTo(0), "already in");
                Assert.That(fr.InMod(m, fr.Rows), Is.EqualTo(6));
                EditOp ed = m.Ops.OfType<EditOp>().Single();
                Assert.That(ed.Archive + " " + ed.Entry, Is.EqualTo(Kms + " table/fielddata.xml"));
                Assert.That(ed.Rows.All(r => r.Kind == RowOpKind.Add && r.Sync == false));

                Plan p = s.Plan();
                Assert.That(p.HasErrors, Is.False, EditorSession.Report(p));
                Assert.That(p.Warnings, Is.Empty, EditorSession.Report(p));
                ApplyResult res = s.Apply();
                Assert.That(res.Applied, Is.True, EditorSession.Report(res));
                string text;
                using (M2dArchive a = M2dArchive.Open(Path.Combine(d.Dir, "ws", "client", "Xml.m2d")))
                    text = Encoding.UTF8.GetString(a.Read(a.Find("table/fielddata.xml")));
                string want = Head.Replace("\n", "\r\n") + "<ms2>\r\n" +
                    "\t<fieldData id=\"5\">\r\n\t\t<environment>\r\n\t\t\t<xblock name=\"Five\" />\r\n\t\t</environment>\r\n\t</fieldData>\r\n" +
                    "\t<fieldData id=\"10\">\r\n\t\t<environment>\r\n\t\t\t<xblock name=\"Ten_A\" />\r\n\t\t</environment>\r\n\t</fieldData>\r\n" +
                    "\t<fieldData id=\"19\">\r\n\t\t<environment>\r\n\t\t\t<xblock name=\"00000019_BF\" />\r\n";
                Assert.That(text, Does.StartWith(want));
                Assert.That(text, Does.Contain("\t\t\t<ui bgMask=\"m&amp;n\" usePet=\"0\" isNoSkipFrame=\"false\" />\r\n\t\t</environment>\r\n\t</fieldData>\r\n\t<fieldData id=\"30\">"));
                Assert.That(text, Does.Contain(
                    "\t<fieldData id=\"30\">\r\n\t\t<environment feature=\"FakeOn\">\r\n\t\t\t<xblock name=\"\" />\r\n\t\t</environment>\r\n" +
                    "\t\t<environment>\r\n\t\t\t<xblock name=\"Thirty\" />\r\n\t\t\t<fov value=\"50\" />\r\n\t\t</environment>\r\n\t</fieldData>\r\n" +
                    "\t<fieldData id=\"40\">\r\n\t\t<environment>\r\n\t\t\t<xblock name=\"Forty\" />\r\n\t\t</environment>\r\n\t</fieldData>\r\n" +
                    "\t<fieldData id=\"41\">\r\n\t\t<environment>\r\n\t\t\t<xblock name=\"FortyOne\" />\r\n\t\t</environment>\r\n\t</fieldData>\r\n" +
                    "\t<fieldData id=\"42\">\r\n\t\t<environment>\r\n\t\t\t<xblock name=\"FortyTwo\" />\r\n\t\t</environment>\r\n\t</fieldData>\r\n" +
                    "\t<fieldData id=\"50\">"));
                Assert.That(File.Exists(Path.Combine(d.Dir, "ws", "gms-client", "Xml.m2d")), Is.False, "sync=false: nothing for the gms side");

                // the filled table scans open; the saved and loaded mod still holds the rows of the first scan
                Mod back = Mod.Load(Path.Combine(d.Dir, "mods", "t.fields"));
                Assert.That(fr.InMod(back, fills), Is.EqualTo(6));
                Assert.That(fr.Take(back, fills.Where(r => r.Id == 19)), Is.EqualTo(1));
                Assert.That(fr.InMod(back, fills), Is.EqualTo(5));
                Assert.That(fr.Take(back, fr.Rows), Is.EqualTo(5));
                Assert.That(back.IsEmpty, Is.True);
            }
        }

        [Test]
        public void Scan_AfterApply_RowsAreOpen()
        {
            using (var d = new Data())
            {
                byte[] table;
                using (EditorSession s = Session(d.Dir))
                {
                    Mod m = s.NewMod("t.fields");
                    FieldRows fr = FieldRows.Scan(s.Data, Kms, Gms);
                    fr.Put(m, fr.Rows);
                    Assert.That(s.Apply().Applied, Is.True);
                    using (M2dArchive a = M2dArchive.Open(Path.Combine(d.Dir, "ws", "client", "Xml.m2d")))
                        table = a.Read(a.Find("table/fielddata.xml"));
                }
                var maps = new Dictionary<int, Func<byte[]>>();
                foreach (int id in new[] { 5, 19, 30, 40, 41 }) { string t = Map(Env("", "X")); maps[id] = () => Encoding.UTF8.GetBytes(t); }
                FieldRows again = FieldRows.Scan(table, maps, null, null, Kms, Gms, "KR");
                Assert.That(again.Open, Is.EqualTo(5));
                Assert.That(again.Rows, Is.Empty);
            }
        }

        /// <summary>Real data, local only: MS2_FIELDROWS_DATA names a folder with kms/ and gms/ (client Data folders).</summary>
        [Test, Explicit]
        public void RealData()
        {
            string dir = Environment.GetEnvironmentVariable("MS2_FIELDROWS_DATA");
            if (string.IsNullOrEmpty(dir)) Assert.Ignore("MS2_FIELDROWS_DATA is not set");
            string ws = Path.Combine(Path.GetTempPath(), "modkit-fieldreal-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var s = new EditorSession(Path.Combine(dir, "kms"), Path.Combine(dir, "gms"), Path.Combine(dir, "server"), Path.Combine(ws, "mods"), Path.Combine(ws, "ws")))
                {
                    var w = System.Diagnostics.Stopwatch.StartNew();
                    Mod m = s.NewMod("real.fields");
                    FieldRows fr = FieldRows.Scan(s.Data, Kms, Gms);
                    long scan = w.ElapsedMilliseconds;
                    TestContext.Out.WriteLine("scan " + scan + " ms; open " + fr.Open + "; ours closed without source: " + string.Join(" ", fr.NoSource));
                    foreach (var g in fr.Rows.GroupBy(r => r.Kind)) TestContext.Out.WriteLine(g.Key + " " + g.Count());
                    TestContext.Out.WriteLine("ticked by default (the server has the map, not Develop) " + fr.Rows.Count(r => r.Default));
                    foreach (FieldRow r in fr.Rows.Where(r => r.Default || !r.Ticked))
                        TestContext.Out.WriteLine(r.Kind + " " + r.Id + " " + r.XBlock + " attrs " + r.Attrs + " feature " + r.Feature + " on " + r.FeatureOn + " level " + r.FeatureLevel + " " + (r.After ?? r.At ?? r.Before) + " envs " + r.Environments + " " + r.Error);
                    fr.Put(m, fr.Rows.Where(r => r.Default));
                    Plan p = s.Plan();
                    TestContext.Out.WriteLine("plan " + (w.ElapsedMilliseconds - scan) + " ms, " + p.Changes.Count + " changes, errors " + p.HasErrors);
                    TestContext.Out.WriteLine(EditorSession.Report(p));
                }
            }
            finally { if (Directory.Exists(ws)) Directory.Delete(ws, true); }
        }
    }
}
