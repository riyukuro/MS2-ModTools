// Text import (TextImport.cs) on made-up string files: our client's string/kr against the global client's string/en.
// Every id and text in here is made up.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class TextImportTests
    {
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";
        const string Kms = "client:Xml.m2d", Gms = "gms-client:Xml.m2d";

        // ours: two Korean item names (one with a leading-zero key on the source side), one English name, an English
        // name the source has plainly otherwise, a row with feature variants, a key the source lacks
        const string OurItems = Head + "<ms2>\n" +
            "  <key id=\"1001\" class=\"\" name=\"가짜 검:s\" />\n" +
            "  <key id=\"1002\" class=\"\" name=\"가짜 방패:s\" />\n" +
            "  <key id=\"1003\" class=\"\" name=\"Paper Hat:s\" />\n" +
            "  <key id=\"1004\" class=\"\" name=\"Wooden Spoon\" />\n" +
            "  <key id=\"1005\" class=\"\" name=\"가짜 반지\" />\n" +
            "  <key id=\"1005\" feature=\"Alpha\" class=\"\" name=\"가짜 반지 알파\" />\n" +
            "  <key id=\"1006\" class=\"\" name=\"가짜 신발\" />\n" +
            "  <key id=\"1007\" class=\"\" name=\"가짜 망토\" />\n" +
            "  <key id=\"1008\" class=\"\" name=\"Glass Cup\" />\n" +
            "</ms2>\n";

        const string TheirItems = Head + "<ms2>\n" +
            "  <key id=\"1001\" feature=\"\" locale=\"\" name=\"Fake Sword:s\" />\n" +
            "  <key id=\"01002\" feature=\"\" locale=\"\" name=\"Fake Shield:s\" />\n" +
            "  <key id=\"1003\" feature=\"\" locale=\"\" name=\"Paper Cap:s\" />\n" +
            "  <key id=\"1004\" feature=\"\" locale=\"\" name=\"Thunder Drum\" />\n" +
            "  <key id=\"1005\" feature=\"\" locale=\"\" name=\"Fake Ring\" />\n" +
            "  <key id=\"1005\" feature=\"Alpha\" locale=\"\" name=\"Fake Ring Alpha\" />\n" +
            "  <key id=\"1006\" feature=\"Beta\" locale=\"NA\" name=\"Fake Boots\" />\n" +
            "  <key id=\"1006\" feature=\"Gamma\" locale=\"NA\" name=\"Fake Shoes\" />\n" +
            "  <key id=\"1007\" feature=\"\" locale=\"\" name=\"가짜 망토\" />\n" +
            "  <key id=\"1009\" feature=\"\" locale=\"\" name=\"Fake Lamp\" />\n" +
            "</ms2>\n";

        // ours: one quest script file; the source splits it in two, and one of its texts is a placeholder
        const string OurQuests = Head + "<ms2>\n" +
            "  <quest questID=\"2001\" name=\"가짜 퀘스트\" desc=\"가짜 설명\" />\n" +
            "  <quest questID=\"2002\" name=\"가짜 두번째\" />\n" +
            "</ms2>\n";

        const string TheirQuestsA = Head + "<ms2>\n  <quest questID=\"2001\" npc=\"가짜\" name=\"Fake Quest\" desc=\"SCRIPTQUEST_0000000000002001_DESC\" />\n</ms2>\n";
        const string TheirQuestsB = Head + "<ms2>\n  <quest questID=\"2002\" name=\"Second Fake\" />\n</ms2>\n";

        // ours: one row per level, a text with a {0} slot; the source (split files) writes one level's number in
        const string OurAdditional = Head + "<ms2>\n" +
            "  <key id=\"3001\" level=\"1\" name=\"가짜 함성\" tooltipDescription=\"공격력 {0}% 증가\" extra=\"가짜\" />\n" +
            "  <key id=\"3001\" level=\"2\" name=\"가짜 함성\" tooltipDescription=\"공격력 {0}% 증가\" />\n" +
            "</ms2>\n";

        const string TheirAdditional = Head + "<ms2>\n" +
            "  <key id=\"3001\" level=\"1\" name=\"Fake Shout\" tooltipDescription=\"Raises attack by 3%.\" />\n" +
            "  <key id=\"3001\" level=\"2\" name=\"Fake Shout\" tooltipDescription=\"Raises attack by {0}%.\" />\n" +
            "  <key id=\"3001\" level=\"3\" name=\"Fake Shout\" tooltipDescription=\"Raises attack by 5%.\" />\n" +
            "</ms2>\n";

        sealed class Data : IDisposable
        {
            public readonly string Dir;
            public Data()
            {
                Dir = Path.Combine(Path.GetTempPath(), "modkit-text-" + Guid.NewGuid().ToString("N"));
                string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
                foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);
                M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                {
                    E("string/kr/itemname.xml", OurItems), E("string/kr/scriptquest.xml", OurQuests),
                    E("string/kr/sub/itemname.xml", OurItems),
                    E("string/kr/stringadditionaldescription.xml", OurAdditional), E("string/en/itemname.xml", OurItems),
                });
                M2dWriter.Create(Path.Combine(g, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                {
                    E("string/en/itemname.xml", TheirItems), E("string/en/scriptquest_a.xml", TheirQuestsA),
                    E("string/en/scriptquest_b.xml", TheirQuestsB), E("string/en/scriptnpc.xml", Head + "<ms2 />\n"),
                    E("string/en/koradditionaldescription_1.xml", TheirAdditional),
                });
                M2dWriter.Create(Path.Combine(s, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                {
                    E("table/none.xml", Head + "<ms2 />\n"),
                });
            }
            public void Dispose() { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); }
        }

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        static EditorSession Session(string dir)
        {
            return new EditorSession(Path.Combine(dir, "kms"), Path.Combine(dir, "gms"), Path.Combine(dir, "server"), Path.Combine(dir, "mods"), Path.Combine(dir, "ws"));
        }

        static TextImport Scan(EditorSession s, Mod m) { return TextImport.Scan(s.Data, m, Kms, "kr", Gms, "en"); }

        [Test]
        public void Helpers()
        {
            Assert.That(TextImport.Key("02040998"), Is.EqualTo("2040998"));
            Assert.That(TextImport.Key("000"), Is.EqualTo("0"));
            Assert.That(TextImport.IsKorean("Fake 검"), Is.True);
            Assert.That(TextImport.IsKorean("Fake Sword"), Is.False);
            Assert.That(TextImport.IsPlaceholder("SCRIPTQUEST_0831174302000023_NAME"), Is.True);
            Assert.That(TextImport.IsPlaceholder("Fake Quest"), Is.False);
            Assert.That(TextImport.IsPlaceholder("HP"), Is.False);
            Assert.That(TextImport.Overlap("Paper Hat", "Paper Cap"), Is.EqualTo(1.0 / 3).Within(1e-9));
            Assert.That(TextImport.Overlap("Wooden Spoon", "Thunder Drum"), Is.EqualTo(0));
            var files = new[] { "scriptquest_b.xml", "scriptquest_a.xml", "scriptquestx.xml", "itemname.xml" };
            Assert.That(TextImport.SourceFilesOf("scriptquest.xml", files), Is.EqualTo(new[] { "scriptquest_a.xml", "scriptquest_b.xml" }));
            Assert.That(TextImport.SourceFilesOf("itemname.xml", files), Is.EqualTo(new[] { "itemname.xml" }));
            Assert.That(TextImport.SourceFilesOf("npcname.xml", files), Is.Empty);
        }

        [Test]
        public void Scan_FillsKoreanOnly_AndListsPlainlyDifferent()
        {
            using (var d = new Data())
            using (EditorSession s = Session(d.Dir))
            {
                Mod m = s.NewMod("t.text");
                TextImport ti = Scan(s, m);
                // only the top folder of string/kr
                Assert.That(ti.Files.Select(f => f.File), Is.EqualTo(new[] { "itemname.xml", "scriptquest.xml", "stringadditionaldescription.xml" }));

                TextFileStat items = ti.Files[0];
                Assert.That(items.Error, Is.Null);
                Assert.That(items.SourceFiles, Is.EqualTo(new[] { "itemname.xml" }));
                Assert.That(items.Keys, Is.EqualTo(8));
                Assert.That(items.Has, Is.EqualTo(7), "1008 is ours only");
                Assert.That(items.SourceOnly, Is.EqualTo(1), "1009");
                Assert.That(items.Fill, Is.EqualTo(4), "1001, 1002 (leading zero), 1005 and 1005 Alpha");
                Assert.That(items.Ambiguous, Is.EqualTo(1), "1006: two variants, neither ours, different texts");
                Assert.That(items.Same, Is.EqualTo(1), "1007: the source is Korean too, and the same");
                Assert.That(items.Near, Is.EqualTo(1), "1003: Paper Hat / Paper Cap");
                Assert.That(items.Plain, Is.EqualTo(1), "1004");

                var fills = ti.Rows.Where(r => r.Kind == TextKind.Fill && r.File == "itemname.xml").ToList();
                Assert.That(fills.Select(r => r.Id + "=" + r.Source), Is.EqualTo(new[] { "1001=Fake Sword:s", "1002=Fake Shield:s", "1005=Fake Ring", "1005=Fake Ring Alpha" }));
                Assert.That(fills[0].At, Is.EqualTo("key[id=1001]"));
                Assert.That(fills[2].At, Is.EqualTo("key[id=1005][feature=]"));
                Assert.That(fills[3].At, Is.EqualTo("key[id=1005][feature=Alpha]"));
                Assert.That(fills[0].Entry, Is.EqualTo("string/kr/itemname.xml"));
                TextRow plain = ti.Rows.Single(r => r.Kind == TextKind.Plain);
                Assert.That(plain.Id + " " + plain.Ours + " / " + plain.Source, Is.EqualTo("1004 Wooden Spoon / Thunder Drum"));

                // the quest family: the split files, the placeholder not taken, the npc attribute is not text
                TextFileStat quests = ti.Files[1];
                Assert.That(quests.SourceFiles, Is.EqualTo(new[] { "scriptquest_a.xml", "scriptquest_b.xml" }));
                Assert.That(quests.Placeholder, Is.EqualTo(1));
                Assert.That(ti.Rows.Where(r => r.File == "scriptquest.xml").Select(r => r.Id + "@" + r.Attr + "=" + r.Source),
                    Is.EqualTo(new[] { "2001@name=Fake Quest", "2002@name=Second Fake" }));

                // levels: each row of ours takes the source row of its level; a text with the number written in
                // where ours has {0} is not taken; the source lacks "extra"
                TextFileStat add = ti.Files[2];
                Assert.That(add.SourceFiles, Is.EqualTo(new[] { "koradditionaldescription_1.xml" }));
                Assert.That(add.Form, Is.EqualTo(1));
                Assert.That(add.Missing, Is.EqualTo(1));
                Assert.That(ti.Rows.Where(r => r.File == add.File).Select(r => r.At + "@" + r.Attr + "=" + r.Source), Is.EqualTo(new[]
                {
                    "key[id=3001][level=1]@name=Fake Shout", "key[id=3001][level=2]@name=Fake Shout", "key[id=3001][level=2]@tooltipDescription=Raises attack by {0}%.",
                }));
                Assert.That(TextImport.Slots("a {1} b {0} c {1}"), Is.EqualTo("{0}{1}"));
            }
        }

        [Test]
        public void Put_PlanApply_ThenTakeOut()
        {
            using (var d = new Data())
            using (EditorSession s = Session(d.Dir))
            {
                Mod m = s.NewMod("t.text");
                TextImport ti = Scan(s, m);
                var fills = ti.Rows.Where(r => r.Kind == TextKind.Fill).ToList();
                Assert.That(ti.Put(m, fills), Is.EqualTo(9));
                Assert.That(ti.Put(m, fills), Is.EqualTo(0), "already in");
                Assert.That(ti.InMod(m, fills), Is.EqualTo(9));
                EditOp items = m.Ops.OfType<EditOp>().Single(o => o.Entry == "string/kr/itemname.xml");
                Assert.That(items.Archive, Is.EqualTo(Kms));
                Assert.That(items.Rows.All(r => r.Kind == RowOpKind.Change && r.Sync == false));

                Plan p = s.Plan();
                Assert.That(p.HasErrors, Is.False, EditorSession.Report(p));
                ApplyResult r = s.Apply();
                Assert.That(r.Applied, Is.True, EditorSession.Report(r));
                string text;
                using (M2dArchive a = M2dArchive.Open(Path.Combine(d.Dir, "ws", "client", "Xml.m2d")))
                    text = Encoding.UTF8.GetString(a.Read(a.Find("string/kr/itemname.xml")));
                Assert.That(text, Does.Contain("<key id=\"1001\" class=\"\" name=\"Fake Sword:s\" />"));
                Assert.That(text, Does.Contain("<key id=\"1005\" feature=\"Alpha\" class=\"\" name=\"Fake Ring Alpha\" />"));
                Assert.That(text, Does.Contain("name=\"Wooden Spoon\""), "the plain row is not taken");
                Assert.That(text, Does.Contain("name=\"가짜 신발\""), "the ambiguous row is left");
                Assert.That(File.Exists(Path.Combine(d.Dir, "ws", "gms-client", "Xml.m2d")), Is.False, "sync=false: nothing for the gms side");

                // a scan with the mod: the attributes the mod sets are kept, not offered again
                Mod back = Mod.Load(Path.Combine(d.Dir, "mods", "t.text"));
                TextImport again = Scan(s, back);
                Assert.That(again.Files[0].Kept, Is.EqualTo(4));
                Assert.That(again.Rows.Count(x => x.Kind == TextKind.Fill), Is.EqualTo(0));

                // take out the plain row (not in) and two fills
                Assert.That(ti.Take(back, ti.Rows.Where(x => x.Kind == TextKind.Plain)), Is.EqualTo(0));
                Assert.That(ti.Take(back, fills.Where(x => x.Id == "1005")), Is.EqualTo(2));
                Assert.That(ti.InMod(back, fills), Is.EqualTo(7));
                Assert.That(ti.Take(back, fills), Is.EqualTo(7));
                Assert.That(back.IsEmpty, Is.True);
            }
        }

        [Test]
        public void Put_LeavesAValueTheModSetsAlready()
        {
            using (var d = new Data())
            using (EditorSession s = Session(d.Dir))
            {
                Mod m = s.NewMod("t.text");
                var op = new EditOp { Archive = Kms, Entry = "string/kr/itemname.xml" };
                var row = new RowOp { Kind = RowOpKind.Change, At = "key[id=1001]" };
                row.Attrs.Add(new AttrOp { Kind = AttrOpKind.Set, Name = "name", Value = "My Sword" });
                op.Rows.Add(row);
                m.Ops.Add(op);
                TextImport ti = Scan(s, m);
                Assert.That(ti.Files[0].Kept, Is.EqualTo(1));
                Assert.That(ti.Rows.Any(x => x.Id == "1001"), Is.False);
                Assert.That(ti.Put(m, ti.Rows.Where(x => x.Kind == TextKind.Fill)), Is.EqualTo(8));
                Assert.That(row.Attrs.Single().Value, Is.EqualTo("My Sword"));
                Assert.That(m.Ops.OfType<EditOp>().Count(o => o.Entry == "string/kr/itemname.xml"), Is.EqualTo(1), "one edit of the entry");
                Assert.That(op.Rows.Count, Is.EqualTo(4), "1001 (the user's) + 1002 + 1005 + 1005 Alpha");
            }
        }

        // the real data: MS2_TEXTIMPORT_DATA is a folder with kms/ gms/ (and server/) Xml.m2d
        [Test, Explicit("real data: set MS2_TEXTIMPORT_DATA")]
        public void RealData_Counts()
        {
            string dir = Environment.GetEnvironmentVariable("MS2_TEXTIMPORT_DATA");
            if (string.IsNullOrEmpty(dir)) Assert.Ignore("MS2_TEXTIMPORT_DATA is not set");
            string mods = Path.Combine(Path.GetTempPath(), "modkit-text-real-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (EditorSession s = new EditorSession(Path.Combine(dir, "kms"), Path.Combine(dir, "gms"), Path.Combine(dir, "server"), Path.Combine(mods, "mods"), Path.Combine(mods, "ws")))
                {
                    var w = System.Diagnostics.Stopwatch.StartNew();
                    TextImport ti = Scan(s, null);
                    long scan = w.ElapsedMilliseconds;
                    int fill = 0, plain = 0;
                    foreach (TextFileStat f in ti.Files)
                    {
                        fill += f.Fill; plain += f.Plain;
                        TestContext.Out.WriteLine(f.File + ": keys " + f.Keys + " has " + f.Has + " fill " + f.Fill + " plain " + f.Plain + " near " + f.Near +
                                " same " + f.Same + " korean " + f.Korean + " ambiguous " + f.Ambiguous + " placeholder " + f.Placeholder + " form " + f.Form + " missing " + f.Missing + " source-only " + f.SourceOnly +
                                " from " + string.Join(",", f.SourceFiles.ToArray()) + (f.Error != null ? " ERROR " + f.Error : ""));
                    }
                    var keys = new HashSet<string>(ti.Rows.Where(r => r.Kind == TextKind.Fill).Select(r => r.File + "\n" + r.At));
                    TestContext.Out.WriteLine("fill " + fill + " attributes in " + keys.Count + " rows, plain " + plain + ", scan " + scan + " ms");
                    Mod m = s.NewMod("real.text");
                    w.Restart();
                    ti.Put(m, ti.Rows.Where(r => r.Kind == TextKind.Fill));
                    Plan p = s.Plan();
                    TestContext.Out.WriteLine("put+plan " + w.ElapsedMilliseconds + " ms, errors " + p.HasErrors + ", changes " + p.Changes.Count);
                    Assert.That(p.HasErrors, Is.False, EditorSession.Report(p));
                }
            }
            finally { if (Directory.Exists(mods)) Directory.Delete(mods, true); }
        }
    }
}
