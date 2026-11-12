// Item stat options (ItemOptions.cs, PlannerItemOptions.cs) on made-up tables: a kms client and a server.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    public sealed class FakeOptions : IDisposable
    {
        public readonly string Dir;
        public readonly DataSet Data;
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        // kms: code 800000100 has a rank for grades 1-2 and one for 3 and up; 800000200 has one rank without grade; 800000300 is Lua
        public const string Kms = Head + "<ms2>\n" +
            "\t<option code=\"800000100\">\n" +
            "\t\t<rank grade=\"1\">\n\t\t\t<v name=\"mar\" value=\"4\" />\n\t\t</rank>\n" +
            "\t\t<rank grade=\"3\">\n\t\t\t<v name=\"mar\" value=\"8\" />\n\t\t\t<v name=\"str\" value=\"2\" />\n\t\t</rank>\n" +
            "\t</option>\n" +
            "\t<option code=\"800000200\">\n\t\t<rank>\n\t\t\t<v name=\"dex\" value=\"1\" />\n\t\t</rank>\n\t</option>\n" +
            "\t<option code=\"800000300\">\n\t\t<rank grade=\"1\" createType=\"3\" luaIndex=\"5\" />\n\t</option>\n" +
            "</ms2>\n";

        public const string Constant = Head + "<ms2>\n" +
            "\t<option code=\"800000100\" grade=\"1\" mar_value_base=\"4\" />\n" +
            "\t<option code=\"800000100\" grade=\"2\" mar_value_base=\"4\" />\n" +
            "\t<option code=\"800000100\" grade=\"3\" mar_value_base=\"8\" str_value_base=\"2\" />\n" +
            "\t<option code=\"800000100\" grade=\"4\" mar_value_base=\"8\" str_value_base=\"2\" pen_rate_base=\"0.01\" />\n" +
            "\t<option code=\"800000200\" grade=\"1\" dex_value_base=\"1\" />\n" +
            "</ms2>\n";

        public const string Static = Head + "<ms2>\n" +
            "\t<option code=\"800000500\" grade=\"3\" optionNumPick=\"1,1\" str_value_base=\"3,5\" />\n" +
            "\t<option code=\"800000500\" grade=\"4\" optionNumPick=\"1,1\" str_value_base=\"4,6\" />\n" +
            "</ms2>\n";

        // kms random: 800000700 picks 2 of 2 lines (1 at grade 2, none at grade 1), pen with weight 3; 800000800 is in
        // the kms table only and its statID has no variation row
        public const string KmsRandom = Head + "<ms2>\n" +
            "\t<option code=\"800000700\" statGroup=\"3\" levelGroupID=\"1\" pickCount=\"2\" isRarePickOne=\"1\">\n" +
            "\t\t<v name=\"str\" statID=\"8001\" />\n\t\t<v name=\"pen\" statID=\"8002\" weight=\"3\" />\n\t</option>\n" +
            "\t<option code=\"800000800\" statGroup=\"3\" levelGroupID=\"1\" pickCount=\"1\">\n\t\t<v name=\"dex\" statID=\"8009\" />\n\t</option>\n" +
            "</ms2>\n";

        public const string KmsVariation = Head + "<ms2>\n" +
            "\t<option id=\"8001\" groupType=\"1\" name=\"str\" levelGroupID=\"1\" highStat=\"5\">\n\t\t<v stat=\"3\" />\n\t\t<v stat=\"4\" />\n\t\t<v stat=\"5\" />\n\t</option>\n" +
            "\t<option id=\"8002\" groupType=\"1\" name=\"pen\" levelGroupID=\"1\" highStat=\"7\">\n\t\t<v stat=\"7\" />\n\t</option>\n" +
            "</ms2>\n";

        public const string Random = Head + "<ms2>\n" +
            "\t<option code=\"800000700\" grade=\"1\" optionNumPick=\"0,0\" str_value_base=\"3,5\" pen_rate_base=\"0.01,0.02\" />\n" +
            "\t<option code=\"800000700\" grade=\"2\" optionNumPick=\"1,1\" str_value_base=\"3,5\" pen_rate_base=\"0.01,0.02\" />\n" +
            "\t<option code=\"800000700\" grade=\"3\" optionNumPick=\"2,2\" str_value_base=\"3,5\" pen_rate_base=\"0.01,0.02\" />\n" +
            "</ms2>\n";

        static string ItemFile(long id, string constant, string stat, string random = "0")
        {
            return Head + "<ms2>\n\t<environment>\n\t\t<basic itemID=\"" + id + "\" />\n\t\t<option constant=\"" + constant + "\" static=\"" + stat + "\" random=\"" + random + "\" />\n\t</environment>\n</ms2>\n";
        }

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        // a static file the server does not read (static_55), listed before the read one, with rows of 800000500 too:
        // grade 2, and grade 3, which the read file has as well
        public const string Static55 = Head + "<ms2>\n" +
            "\t<option code=\"800000500\" grade=\"2\" optionNumPick=\"1,1\" str_value_base=\"1,2\" />\n" +
            "\t<option code=\"800000500\" grade=\"3\" optionNumPick=\"1,1\" str_value_base=\"9,9\" />\n" +
            "</ms2>\n";

        public FakeOptions() : this(false) { }

        // the global client's constant file: 800000100 grade 1 has mar 5 there (4 on the server)
        public const string GmsConstant = Head + "<ms2>\n" +
            "\t<option code=\"800000100\" grade=\"1\" mar_value_base=\"5\" />\n" +
            "\t<option code=\"800000100\" grade=\"2\" mar_value_base=\"4\" />\n" +
            "</ms2>\n";

        public FakeOptions(bool twoFiles) : this(twoFiles, false) { }

        /// <summary>With <paramref name="gms"/>, a global client too, with constant and static files of its own.</summary>
        public FakeOptions(bool twoFiles, bool gms)
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-options-" + Guid.NewGuid().ToString("N"));
            string k = Path.Combine(Dir, "kms"), s = Path.Combine(Dir, "server");
            foreach (string d in new[] { k, s }) Directory.CreateDirectory(d);
            string rows = Head + "<ms2>\n" +
                "\t<item id=\"99100001\">\n\t\t<option constantID=\"800000100\" randomID=\"800000700\" optionLevel=\"1\" />\n\t</item>\n" +
                "\t<item id=\"99100002\">\n\t\t<option constantID=\"800000100\" optionLevel=\"1\" />\n\t</item>\n" +
                "\t<item id=\"99100003\">\n\t\t<option constantID=\"800000200\" optionLevel=\"1\" />\n\t</item>\n" +
                // two options, the first naming no code: the pointer check goes option by option
                "\t<item id=\"99100004\">\n\t\t<option constantID=\"0\" optionLevel=\"1\" />\n\t\t<option constantID=\"800000200\" optionLevel=\"2\" />\n\t</item>\n" +
                "</ms2>\n";
            M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E(ItemOptionRules.KmsEntry, Kms), E(ItemOptionRules.KmsRandomEntry, KmsRandom), E(ItemOptionRules.KmsVariationEntry, KmsVariation), E("itemdata/991.xml", rows),
            });
            M2dWriter.Create(Path.Combine(s, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E(ItemOptionRules.ConstantPrefix + "equip.xml", Constant),
                twoFiles ? E(ItemOptionRules.StaticPrefix + "55.xml", Static55) : E("table/none.xml", Head + "<ms2 />\n"),
                E(ItemOptionRules.StaticPrefix + "12.xml", Static),
                E(ItemOptionRules.RandomPrefix + "12.xml", Random),
                E("item/9/91/99100001.xml", ItemFile(99100001, "800000100", "800000500", "800000700")),
                E("item/9/91/99100002.xml", ItemFile(99100002, "800000100", "0")),
                E("item/9/91/99100003.xml", ItemFile(99100003, "800000200", "0")),
            });
            string g = null;
            if (gms)
            {
                g = Path.Combine(Dir, "gms");
                Directory.CreateDirectory(g);
                M2dWriter.Create(Path.Combine(g, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                {
                    E(ItemOptionRules.ConstantPrefix + "equip.xml", GmsConstant),
                    E(ItemOptionRules.StaticPrefix + "12.xml", Static),
                });
            }
            Data = new DataSet(k, g, s);
        }

        public static Mod ModOf(string body, string id = "options.test")
        {
            Mod m = Mod.Parse("<mod id=\"" + id + "\" name=\"t\" version=\"1\">\n" + body + "\n</mod>", "test mod");
            return Mod.Parse(m.ToXml(), "round trip");
        }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class ItemOptionsTests
    {
        const string K = "client:Xml.m2d", S = "server:Xml.m2d", G = "gms-client:Xml.m2d";
        const string CFile = ItemOptionRules.ConstantPrefix + "equip.xml", SFile = ItemOptionRules.StaticPrefix + "12.xml";

        static OptionSide Side(ItemOptionEditor ed, OptionKind kind, bool kms)
        {
            foreach (OptionSide s in ed.Sides(kind)) if (s.Kms == kms) return s;
            return null;
        }

        static string Body(Mod m)
        {
            string x = m.ToXml();
            int a = x.IndexOf('>', x.IndexOf("<mod", StringComparison.Ordinal)) + 1, b = x.LastIndexOf("</mod>", StringComparison.Ordinal);
            return x.Substring(a, b - a);
        }

        static Plan PlanOf(FakeOptions f, Mod m) { return Planner.Build(f.Data, new[] { FakeOptions.ModOf(Body(m)) }, new PlanOptions()); }

        static string Line(string text, string code, string grade)
        {
            if (text == null) return null;
            foreach (string l in text.Split('\n'))
                if (l.Contains("code=\"" + code + "\"") && l.Contains("grade=\"" + grade + "\"")) return l.Trim();
            return null;
        }

        [Test]
        public void Rules()
        {
            Assert.That(ItemOptionRules.CheckValue(OptionKind.Constant, true, "mar_value_base", "12"), Is.Null);
            Assert.That(ItemOptionRules.CheckValue(OptionKind.Constant, true, "mar_value_base", "1.5"), Is.Not.Null);
            Assert.That(ItemOptionRules.CheckValue(OptionKind.Constant, false, "pen_rate_base", "0.05"), Is.Null);
            Assert.That(ItemOptionRules.CheckValue(OptionKind.Constant, false, "mar_value_base", "3,5"), Is.Not.Null);
            Assert.That(ItemOptionRules.CheckValue(OptionKind.Static, false, "str_value_base", "3,5"), Is.Null);
            Assert.That(ItemOptionRules.CheckValue(OptionKind.Static, false, "str_value_base", "5,3"), Is.Not.Null);
            Assert.That(ItemOptionRules.CheckValue(OptionKind.Static, false, ItemOptionRules.Pick, "2,1"), Is.Not.Null);
            Assert.That(ItemOptionRules.CheckValue(OptionKind.Static, false, ItemOptionRules.Pick, "1,2"), Is.Null);
            Assert.That(ItemOptionRules.ServerReads(OptionKind.Static, ItemOptionRules.StaticPrefix + "55.xml"), Is.False);
            Assert.That(ItemOptionRules.ServerReads(OptionKind.Constant, CFile), Is.True);
            Assert.That(ItemOptionRules.KmsName("mar_value_base"), Is.EqualTo("mar"));
            Assert.That(ItemOptionRules.KmsName("pen_rate_base"), Is.Null);
        }

        [Test]
        public void ReadsBothLayouts()
        {
            using (var f = new FakeOptions())
            {
                var ed = new ItemOptionEditor(f.Data, FakeOptions.ModOf(""));
                OptionSide k = Side(ed, OptionKind.Constant, true), s = Side(ed, OptionKind.Constant, false);
                OptionCell c = ed.Cell(k, 800000100, 2, "mar_value_base");
                Assert.That(c.Value, Is.EqualTo("4"));
                Assert.That(c.Grades, Is.EqualTo(new[] { 1, 2 }));
                Assert.That(ed.Cell(k, 800000100, 5, "mar_value_base").Grades, Is.EqualTo(new[] { 3, 4, 5, 6 }));
                Assert.That(ed.Cell(k, 800000200, 4, "dex_value_base").Value, Is.EqualTo("1"));
                Assert.That(ed.Cell(k, 800000300, 1, "mar_value_base").ReadOnly, Is.Not.Null);
                Assert.That(ed.Cell(s, 800000100, 4, "pen_rate_base").Value, Is.EqualTo("0.01"));
                Assert.That(ed.Cell(s, 800000100, 5, "mar_value_base").Source, Does.Contain("no row"));
                Assert.That(ed.Differs(OptionKind.Constant, 800000100, 3, "mar_value_base"), Is.False);
                // code 800000100 at grade 5: kms 8 (the rank of grade 3 serves it), the server has no row
                Assert.That(ed.Differs(OptionKind.Constant, 800000100, 5, "mar_value_base"), Is.True);
                List<OptionCodeInfo> codes = ed.Codes(OptionKind.Constant);
                OptionCodeInfo c100 = codes.Find(i => i.Code == 800000100);
                Assert.That(c100.Items, Is.EquivalentTo(new long[] { 99100001, 99100002 }));
                Assert.That(c100.Sides, Is.EquivalentTo(new[] { "kms client", "server" }));
                Assert.That(ed.Codes(OptionKind.Static).Find(i => i.Code == 800000500).Items, Is.EqualTo(new long[] { 99100001 }));
                Assert.That(ed.Stats(OptionKind.Constant, 800000100), Does.Contain("pen_rate_base").And.Contain("str_value_base"));
            }
        }

        [Test]
        public void FlatEditMirrorsOnlyAnExactRank()
        {
            using (var f = new FakeOptions())
            {
                Mod m = FakeOptions.ModOf("");
                var ed = new ItemOptionEditor(f.Data, m);
                OptionSide k = Side(ed, OptionKind.Constant, true), s = Side(ed, OptionKind.Constant, false);
                // the kms rank of grade 1 serves 1 and 2, the server has both: server only
                List<string> said = ed.Set(s, 800000100, 1, "mar_value_base", "5");
                Assert.That(said.Count, Is.EqualTo(1), string.Join("\n", said.ToArray()));
                Assert.That(ed.Cell(k, 800000100, 1, "mar_value_base").Value, Is.EqualTo("4"));
                Assert.That(ed.Differs(OptionKind.Constant, 800000100, 1, "mar_value_base"), Is.True);
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Line(Fake.Text(p, S, CFile), "800000100", "1"), Does.Contain("mar_value_base=\"5\""));
                Assert.That(Line(Fake.Text(p, S, CFile), "800000100", "2"), Does.Contain("mar_value_base=\"4\""));
                Assert.That(p.Find(K), Is.Null);
            }
        }

        [Test]
        public void KmsEditMirrorsTheGradesItServes()
        {
            using (var f = new FakeOptions())
            {
                Mod m = FakeOptions.ModOf("");
                var ed = new ItemOptionEditor(f.Data, m);
                OptionSide k = Side(ed, OptionKind.Constant, true), s = Side(ed, OptionKind.Constant, false);
                List<string> said = ed.Set(k, 800000100, 3, "mar_value_base", "9");
                Assert.That(said.Count, Is.EqualTo(2), string.Join("\n", said.ToArray()));
                Assert.That(ed.Cell(k, 800000100, 6, "mar_value_base").Value, Is.EqualTo("9"));
                Assert.That(ed.Cell(s, 800000100, 3, "mar_value_base").Value, Is.EqualTo("9"));
                Assert.That(ed.Cell(s, 800000100, 4, "mar_value_base").Value, Is.EqualTo("9"));
                Assert.That(ed.Cell(s, 800000100, 2, "mar_value_base").Value, Is.EqualTo("4"));
                // a new stat on the kms rank of grades 1-2: the server rows are written where they had none either
                ed.Set(k, 800000100, 1, "str_value_base", "1");
                Assert.That(ed.Cell(s, 800000100, 2, "str_value_base").Value, Is.EqualTo("1"));
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string kt = Fake.Text(p, K, ItemOptionRules.KmsEntry);
                Assert.That(kt, Does.Contain("<v name=\"mar\" value=\"9\" />"));
                Assert.That(kt, Does.Contain("<v name=\"str\" value=\"1\" />"));
                Assert.That(kt, Does.Contain("<option code=\"800000200\">"), "the other rows stay");
                Assert.That(Line(Fake.Text(p, S, CFile), "800000100", "4"), Does.Contain("mar_value_base=\"9\""));
                // back to what ships: the kms row is the shipped one again
                ed.Set(k, 800000100, 3, "mar_value_base", "8");
                ed.Set(k, 800000100, 1, "str_value_base", "");
                Assert.That(ed.Cell(s, 800000100, 3, "mar_value_base").Edited, Is.False);
                Assert.That(ed.Cell(s, 800000100, 1, "str_value_base").Value, Is.EqualTo(""));
                Plan q = PlanOf(f, m);
                Assert.That(q.Find(K), Is.Null, Fake.Dump(q));
            }
        }

        [Test]
        public void MissingGradeRowIsAdded()
        {
            using (var f = new FakeOptions())
            {
                Mod m = FakeOptions.ModOf("");
                var ed = new ItemOptionEditor(f.Data, m);
                OptionSide s = Side(ed, OptionKind.Constant, false);
                ed.Set(s, 800000100, 5, "mar_value_base", "12");
                Assert.That(ed.Cell(s, 800000100, 5, "mar_value_base").Value, Is.EqualTo("12"));
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Line(Fake.Text(p, S, CFile), "800000100", "5"), Does.Contain("mar_value_base=\"12\""));
                ed.Set(s, 800000100, 5, "mar_value_base", "");
                Assert.That(ed.Cell(s, 800000100, 5, "mar_value_base").Source, Does.Contain("no row"));
                Assert.Throws<ModKitException>(() => ed.Set(s, 800000100, 1, "mar_value_base", "x"));
                Assert.Throws<ModKitException>(() => ed.Set(s, 800000100, 1, "nosuch_value_base", "1"));
            }
        }

        [Test]
        public void StaticRanges()
        {
            using (var f = new FakeOptions())
            {
                Mod m = FakeOptions.ModOf("");
                var ed = new ItemOptionEditor(f.Data, m);
                Assert.That(ed.Sides(OptionKind.Static).Count, Is.EqualTo(1));
                OptionSide s = ed.Sides(OptionKind.Static)[0];
                Assert.That(ed.Stats(OptionKind.Static, 800000500)[0], Is.EqualTo(ItemOptionRules.Pick));
                ed.Set(s, 800000500, 3, "str_value_base", "4,7");
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Line(Fake.Text(p, S, SFile), "800000500", "3"), Does.Contain("str_value_base=\"4,7\""));
            }
        }

        [Test]
        public void ForkCopiesRowsAndRepointsItems()
        {
            using (var f = new FakeOptions())
            {
                Mod m = FakeOptions.ModOf("");
                var ed = new ItemOptionEditor(f.Data, m);
                Assert.That(ed.CheckNewCode(800000100), Is.Not.Null);
                long code = ed.NextCodes(1)[0];
                Assert.That(code, Is.EqualTo(ItemOptionRules.From));
                Assert.That(ed.ItemsOf(OptionKind.Constant, 800000100), Is.EqualTo(new long[] { 99100001, 99100002 }));
                int n = ed.Fork(OptionKind.Constant, 800000100, code, new long[] { 99100001 });
                Assert.That(n, Is.EqualTo(1));
                Assert.That(ed.ItemsOf(OptionKind.Constant, 800000100), Is.EqualTo(new long[] { 99100002 }));
                Assert.That(ed.ItemsOf(OptionKind.Constant, code), Is.EqualTo(new long[] { 99100001 }));
                OptionCodeInfo was = null, now = null;
                foreach (OptionCodeInfo i in ed.Codes(OptionKind.Constant)) { if (i.Code == 800000100) was = i; if (i.Code == code) now = i; }
                Assert.That(was.Items, Is.EqualTo(new long[] { 99100002 }));
                Assert.That(now.Items, Is.EqualTo(new long[] { 99100001 }));
                Assert.That(now.Added, Is.True);
                Assert.That(now.GradeText, Is.EqualTo(was.GradeText));
                OptionSide k = Side(ed, OptionKind.Constant, true), s = Side(ed, OptionKind.Constant, false);
                Assert.That(ed.Cell(s, code, 4, "pen_rate_base").Value, Is.EqualTo("0.01"));
                ed.Set(k, code, 3, "mar_value_base", "20");
                Assert.That(ed.Cell(s, code, 4, "mar_value_base").Value, Is.EqualTo("20"));
                Assert.That(ed.Cell(s, 800000100, 4, "mar_value_base").Value, Is.EqualTo("8"));
                Assert.That(ed.CheckNewCode(code), Is.Not.Null);
                bool reserved = false;
                foreach (IdRange r in m.Ids) if (r.Space == ItemOptionRules.Space && r.From <= code && code <= r.To) reserved = true;
                Assert.That(reserved, Is.True);
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string c = code.ToString();
                Assert.That(Line(Fake.Text(p, S, CFile), c, "4"), Does.Contain("mar_value_base=\"20\""));
                Assert.That(Fake.Text(p, K, ItemOptionRules.KmsEntry), Does.Contain("<option code=\"" + c + "\">"));
                Assert.That(Fake.Text(p, S, "item/9/91/99100001.xml"), Does.Contain("constant=\"" + c + "\""));
                Assert.That(Fake.Text(p, K, "itemdata/991.xml"), Does.Contain("constantID=\"" + c + "\""));
                Assert.That(ed.Revert(OptionKind.Constant, code), Is.GreaterThan(0));
            }
        }

        static bool Reserved(Mod m, long code)
        {
            foreach (IdRange r in m.Ids) if (r.Space == ItemOptionRules.Space && r.From <= code && code <= r.To) return true;
            return false;
        }

        // the planned text, less the lines that hold one of the given words, is the shipped text byte for byte
        static void SameBut(string planned, string shipped, params string[] skip)
        {
            Func<string, string> less = t =>
            {
                var kept = new List<string>();
                foreach (string l in t.Replace("\r\n", "\n").Split('\n'))
                {
                    bool drop = false;
                    foreach (string w in skip) if (l.Contains(w)) drop = true;
                    if (!drop) kept.Add(l);
                }
                return string.Join("\n", kept.ToArray());
            };
            Assert.That(less(planned), Is.EqualTo(less(shipped)));
        }

        [Test]
        public void ForkFailingMidwayLeavesNothing()
        {
            using (var f = new FakeOptions())
            {
                Mod m = FakeOptions.ModOf("");
                var ed = new ItemOptionEditor(f.Data, m);
                ed.Set(Side(ed, OptionKind.Constant, false), 800000100, 1, "mar_value_base", "6");
                string before = m.ToXml();
                long code = ed.NextCodes(1)[0];
                // 99100009 is no item: the first item is not repointed either
                var ex = Assert.Throws<ModKitException>(() => ed.Fork(OptionKind.Constant, 800000100, code, new long[] { 99100001, 99100009, 99100002 }));
                Assert.That(ex.Message, Does.Contain("99100009").And.Contain("nothing was forked"));
                Assert.That(m.ToXml(), Is.EqualTo(before));
                Assert.That(Reserved(m, code), Is.False);
                Assert.That(ed.ItemsOf(OptionKind.Constant, 800000100), Is.EqualTo(new long[] { 99100001, 99100002 }));
                Assert.That(ed.Codes(OptionKind.Constant).Exists(i => i.Code == code), Is.False);
                Assert.That(ed.CheckNewCode(code), Is.Null);
            }
        }

        [Test]
        public void ForkFailingWhileWritingIsTakenBack()
        {
            using (var f = new FakeOptions())
            {
                Mod m = FakeOptions.ModOf("");
                var ed = new ItemOptionEditor(f.Data, m);
                ed.Set(Side(ed, OptionKind.Constant, false), 800000100, 2, "mar_value_base", "5");
                string before = m.ToXml();
                // the undo puts back the ops, the rows inside them and the ids as they were
                var undo = new ModUndo(m);
                long code = ed.NextCodes(1)[0];
                ed.Fork(OptionKind.Constant, 800000100, code, new long[] { 99100001 });
                Assert.That(m.ToXml(), Is.Not.EqualTo(before));
                undo.Restore();
                Assert.That(m.ToXml(), Is.EqualTo(before));
                var again = new ItemOptionEditor(f.Data, m);
                Assert.That(again.ItemsOf(OptionKind.Constant, 800000100), Is.EqualTo(new long[] { 99100001, 99100002 }));
                Assert.That(again.Cell(Side(again, OptionKind.Constant, false), 800000100, 2, "mar_value_base").Value, Is.EqualTo("5"));
            }
        }

        [Test]
        public void RevertOfAForkedCodeGivesThePointersBack()
        {
            using (var f = new FakeOptions())
            {
                Mod m = FakeOptions.ModOf("");
                var ed = new ItemOptionEditor(f.Data, m);
                string before = m.ToXml();
                long code = ed.NextCodes(1)[0];
                Assert.That(ed.Fork(OptionKind.Constant, 800000100, code, new long[] { 99100001, 99100002 }), Is.EqualTo(2));
                Assert.That(ed.Revert(OptionKind.Constant, code), Is.GreaterThan(0));
                Assert.That(Reserved(m, code), Is.False);
                Assert.That(ed.ItemsOf(OptionKind.Constant, 800000100), Is.EqualTo(new long[] { 99100001, 99100002 }));
                Assert.That(ed.ItemsOf(OptionKind.Constant, code), Is.Empty);
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(p.Find(S), Is.Null, Fake.Dump(p));
                Assert.That(p.Find(K), Is.Null, Fake.Dump(p));
                Assert.That(ed.CheckNewCode(code), Is.Null);
            }
        }

        [Test]
        public void RevertLeavesACloneOfAForkedItemAlone()
        {
            using (var f = new FakeOptions())
            {
                Mod m = FakeOptions.ModOf("");
                var ed = new ItemOptionEditor(f.Data, m);
                long code = ed.NextCodes(1)[0];
                ed.Fork(OptionKind.Constant, 800000100, code, new long[] { 99100001 });
                // a new item made from 99100001 after the fork starts from the shipped item, so it names the old code
                var items = new ItemEditor(f.Data, m);
                items.Clone(99100001, 99100009, null, null);
                ed = new ItemOptionEditor(f.Data, m);
                Assert.That(ed.ItemsOf(OptionKind.Constant, code), Is.EqualTo(new long[] { 99100001 }));
                Assert.That(ed.CodesOf(99100009, OptionKind.Constant), Is.EqualTo(new long[] { 800000100 }));
                ed.Revert(OptionKind.Constant, code);
                Assert.That(ed.ItemsOf(OptionKind.Constant, code), Is.Empty);
                Assert.That(ed.ItemsOf(OptionKind.Constant, 800000100), Is.EqualTo(new long[] { 99100001, 99100002, 99100009 }));
                Assert.That(Reserved(m, code), Is.False);
            }
        }

        // the item files DataSet holds parsed (the cache is internal: read by reflection)
        static int ItemFilesCached(DataSet data)
        {
            var cache = (System.Collections.Generic.Dictionary<string, object>)typeof(DataSet).GetField("Cache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(data);
            return cache.Keys.Count(k => k.StartsWith("entitydoc\n", StringComparison.Ordinal) && k.Contains("\nitem/"));
        }

        static int CacheKeys(DataSet data)
        {
            return ((System.Collections.Generic.Dictionary<string, object>)typeof(DataSet).GetField("Cache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).GetValue(data)).Count;
        }

        static List<string> DocsCached(DataSet data)
        {
            var cache = (System.Collections.Generic.Dictionary<string, object>)typeof(DataSet).GetField("Cache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(data);
            return cache.Keys.Where(k => k.StartsWith("entitydoc\n", StringComparison.Ordinal)).ToList();
        }

        [Test]
        public void ParsedFilesStayWithinTheBudget_HoweverManyIdsAreOpened_AndPlanWorksAfter()
        {
            long[] ids = { 99100001, 99100002, 99100003 };
            using (var f = new FakeOptions())
            {
                // without a bound every file an Open reads stays
                var items = new ItemEditor(f.Data, FakeOptions.ModOf(""));
                foreach (long id in ids) items.Open(id);
                int all = DocsCached(f.Data).Count;
                long allBytes = f.Data.DocBytes;
                Assert.That(all, Is.GreaterThan(6));
                Assert.That(f.Data.DocCount, Is.EqualTo(all));
                Assert.That(allBytes, Is.GreaterThan(600));
            }
            using (var f = new FakeOptions())
            {
                f.Data.DocBudget = 600;
                Mod m = FakeOptions.ModOf("");
                var items = new ItemEditor(f.Data, m);
                var seen = new List<long>();
                for (int round = 0; round < 20; round++)
                    foreach (long id in ids)
                    {
                        if (round % 2 == 0) seen.Add(items.Open(id).Id); else items.Look(id, d => seen.Add(d.Id));
                        // the file used last stays even when it alone is over the budget
                        Assert.That(f.Data.DocBytes <= 600 || f.Data.DocCount == 1, Is.True, "after item " + id + ": " + f.Data.DocBytes + " bytes in " + f.Data.DocCount + " files");
                        Assert.That(DocsCached(f.Data).Count, Is.EqualTo(f.Data.DocCount));
                    }
                Assert.That(seen.Count, Is.EqualTo(60));
                Assert.That(items.Open(99100002).Name, Is.EqualTo(new ItemEditor(f.Data, m).Open(99100002).Name));
                Assert.Throws<ModKitException>(() => items.Look(99199999, d => { }), "no such item");

                // an edit and a plan after all that reading
                var ed = new ItemOptionEditor(f.Data, m);
                ed.Set(Side(ed, OptionKind.Constant, false), 800000100, 1, "mar_value_base", "5");
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Line(Fake.Text(p, S, CFile), "800000100", "1"), Does.Contain("mar_value_base=\"5\""));
                Assert.That(f.Data.DocBytes <= 600 || f.Data.DocCount == 1, Is.True);

                // plan after plan: what a plan caches replaces what the plan before cached
                PlanOf(f, m);
                int keys = CacheKeys(f.Data);
                for (int i = 0; i < 5; i++) Assert.That(PlanOf(f, m).Errors, Is.Empty);
                Assert.That(CacheKeys(f.Data), Is.EqualTo(keys));
            }
        }

        [Test]
        public void ForkAndRevertOpenOneItemAtATimeAndLetGoOfItsFile()
        {
            using (var f = new FakeOptions())
            {
                Mod m = FakeOptions.ModOf("");
                var ed = new ItemOptionEditor(f.Data, m);
                long code = ed.NextCodes(1)[0];
                Assert.That(ItemFilesCached(f.Data), Is.EqualTo(0));
                Assert.That(ed.Fork(OptionKind.Constant, 800000100, code, new long[] { 99100001, 99100002 }), Is.EqualTo(2));
                Assert.That(ItemFilesCached(f.Data), Is.EqualTo(0));
                ed = new ItemOptionEditor(f.Data, m);
                Assert.That(ed.ItemsOf(OptionKind.Constant, code), Is.EqualTo(new long[] { 99100001, 99100002 }));
                Assert.That(ed.Revert(OptionKind.Constant, code), Is.GreaterThan(0));
                Assert.That(ItemFilesCached(f.Data), Is.EqualTo(0));
                ed = new ItemOptionEditor(f.Data, m);
                Assert.That(ed.ItemsOf(OptionKind.Constant, code), Is.Empty);
                // a file read before stays read
                new ItemEditor(f.Data, m).Open(99100001);
                int before = ItemFilesCached(f.Data);
                Assert.That(before, Is.GreaterThan(0));
                ed.Fork(OptionKind.Constant, 800000100, code, new long[] { 99100001 });
                Assert.That(ItemFilesCached(f.Data), Is.EqualTo(before));
            }
        }

        [Test]
        public void DropDraftsKeepsTheKmsTable()
        {
            using (var f = new FakeOptions())
            {
                Mod m = FakeOptions.ModOf("");
                var ed = new ItemOptionEditor(f.Data, m);
                // an edit of the kms table, then a read of the code's flat file: two parsed files
                OptionSide k = Side(ed, OptionKind.Constant, true);
                ed.Set(k, 800000100, 3, "mar_value_base", "9");
                Assert.That(ed.Cell(Side(ed, OptionKind.Constant, false), 800000100, 1, "mar_value_base").Value, Is.EqualTo("4"));
                Assert.That(ed.DraftCount, Is.EqualTo(2));
                // leaving the code drops the flat file only
                ed.DropDrafts();
                Assert.That(ed.DraftCount, Is.EqualTo(1));
                Assert.That(ed.Cell(k, 800000100, 3, "mar_value_base").Value, Is.EqualTo("9"));
                Assert.That(ed.Cell(Side(ed, OptionKind.Constant, false), 800000100, 1, "mar_value_base").Value, Is.EqualTo("4"));
            }
        }

        [Test]
        public void AGlobalClientWithLinesIsASecondFlatSide()
        {
            using (var f = new FakeOptions(false, true))
            {
                // no lines in the global client: the server stands for it
                var ed = new ItemOptionEditor(f.Data, FakeOptions.ModOf(""));
                Assert.That(ed.FlatArchives, Is.EqualTo(new[] { S }));

                Mod m = FakeOptions.ModOf(Edit(G, CFile, "<change at=\"option[code=800000100][grade=2]\"><set name=\"mar_value_base\" value=\"6\" /></change>"));
                ed = new ItemOptionEditor(f.Data, m);
                Assert.That(ed.FlatArchives, Is.EqualTo(new[] { S, G }));
                List<OptionSide> sides = ed.Sides(OptionKind.Constant);
                Assert.That(sides.Select(x => x.Archive), Is.EqualTo(new[] { K, S, G }));
                OptionSide srv = sides[1], gms = sides[2];
                // each side's own values, and the mod's line on the global client
                Assert.That(ed.Cell(srv, 800000100, 1, "mar_value_base").Value, Is.EqualTo("4"));
                Assert.That(ed.Cell(gms, 800000100, 1, "mar_value_base").Value, Is.EqualTo("5"));
                Assert.That(ed.Cell(gms, 800000100, 2, "mar_value_base").Value, Is.EqualTo("6"));
                Assert.That(ed.Differs(OptionKind.Constant, 800000100, 1, "mar_value_base"), Is.True);
                // grades the global client lacks: no row there
                Assert.That(ed.Has(gms, 800000200), Is.False);

                // a hand line on the global client is mirrored to the server, as any line
                Assert.That(Line(Fake.Text(PlanOf(f, m), S, CFile), "800000100", "2"), Does.Contain("mar_value_base=\"6\""));

                // an edit on the global client stays there: the editor writes it into an <edit mirror="false"> of its
                // own; the hand line keeps its edit and is mirrored as before
                ed.Set(gms, 800000100, 1, "mar_value_base", "7");
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Line(Fake.Text(p, G, CFile), "800000100", "1"), Does.Contain("mar_value_base=\"7\""));
                Assert.That(Line(Fake.Text(p, G, CFile), "800000100", "2"), Does.Contain("mar_value_base=\"6\""));
                Assert.That(Line(Fake.Text(p, S, CFile), "800000100", "1"), Does.Contain("mar_value_base=\"4\""), "the editor's line is not mirrored");
                Assert.That(Line(Fake.Text(p, S, CFile), "800000100", "2"), Does.Contain("mar_value_base=\"6\""), "the hand line still is");
                Assert.That(Mod.Parse(m.ToXml(), "mod.xml").Ops.OfType<EditOp>().Select(x => x.Mirror), Is.EqualTo(new[] { true, false }), "mirror=\"false\" loads back");
                // a second edit goes into the same <edit mirror="false">
                ed.Set(gms, 800000100, 3, "mar_value_base", "9");
                Assert.That(m.Ops.OfType<EditOp>().Count(), Is.EqualTo(2));
                ed.Set(gms, 800000100, 3, "mar_value_base", "8");

                // a fork copies the rows on every flat side that has the code
                long code = ed.NextCodes(1)[0];
                ed.Fork(OptionKind.Constant, 800000100, code, null);
                p = PlanOf(f, m);
                Assert.That(Line(Fake.Text(p, G, CFile), CodeText(code), "1"), Does.Contain("mar_value_base=\"7\""));
                Assert.That(Line(Fake.Text(p, S, CFile), CodeText(code), "1"), Does.Contain("mar_value_base=\"4\""));
            }
        }

        [Test]
        public void AnEditOfAHandMirroredRowStaysOnItsSide()
        {
            using (var f = new FakeOptions(false, true))
            {
                string hand = "<change at=\"option[code=800000100][grade=2]\"><set name=\"mar_value_base\" value=\"6\" /><set name=\"str_value_base\" value=\"2\" /></change>";
                Mod m = FakeOptions.ModOf(Edit(G, CFile, hand));
                var ed = new ItemOptionEditor(f.Data, m);
                OptionSide gms = ed.Sides(OptionKind.Constant)[2];

                // the editor changes a cell of the row the hand line names: the hand line keeps its other cell and
                // is mirrored as before; the editor's value is a change of its own in an <edit mirror="false">
                ed.Set(gms, 800000100, 2, "mar_value_base", "7");
                List<EditOp> edits = m.Ops.OfType<EditOp>().ToList();
                Assert.That(edits.Select(x => x.Mirror), Is.EqualTo(new[] { true, false }));
                Assert.That(edits[0].Rows.Single().Attrs.Select(a => a.Name + "=" + a.Value), Is.EqualTo(new[] { "str_value_base=2" }), "one value per cell in the mod");
                Assert.That(edits[1].Rows.Single().Attrs.Select(a => a.Name + "=" + a.Value), Is.EqualTo(new[] { "mar_value_base=7" }));
                Assert.That(ed.Cell(gms, 800000100, 2, "mar_value_base").Value, Is.EqualTo("7"));
                Assert.That(ed.Cell(gms, 800000100, 2, "str_value_base").Value, Is.EqualTo("2"));
                Plan p = PlanOf(f, m);
                // (the fake global client has no str stat; that check is not what this test is about)
                Assert.That(p.Errors.Where(x => !x.Text.Contains("no shipped constant file has the stat")), Is.Empty, Fake.Dump(p));
                Assert.That(p.Conflicts, Is.Empty, Fake.Dump(p));
                Assert.That(Line(Fake.Text(p, G, CFile), "800000100", "2"), Does.Contain("mar_value_base=\"7\"").And.Contain("str_value_base=\"2\""));
                Assert.That(Line(Fake.Text(p, S, CFile), "800000100", "2"), Does.Contain("mar_value_base=\"4\"").And.Contain("str_value_base=\"2\""),
                    "the editor's cell is not mirrored, the hand line's other cell is");

                // it loads back as two changes of the row
                Mod again = Mod.Parse(m.ToXml(), "mod.xml");
                var ed2 = new ItemOptionEditor(f.Data, again);
                OptionSide gms2 = ed2.Sides(OptionKind.Constant)[2];
                Assert.That(ed2.Cell(gms2, 800000100, 2, "mar_value_base").Value, Is.EqualTo("7"));
                ed2.Set(gms2, 800000100, 2, "mar_value_base", "8");
                Assert.That(again.Ops.OfType<EditOp>().Select(x => x.Rows.Count), Is.EqualTo(new[] { 1, 1 }));
                Assert.That(again.ToXml(), Does.Contain("value=\"8\"").And.Not.Contain("value=\"7\""));

                // the shipped value again: the editor's change goes away, the hand line is all there is
                ed.Set(gms, 800000100, 2, "mar_value_base", "4");
                Assert.That(m.Ops.OfType<EditOp>().Select(x => x.Mirror), Is.EqualTo(new[] { true }));

                // Revert code drops the row's changes, the hand line's too
                ed.Set(gms, 800000100, 2, "mar_value_base", "9");
                Assert.That(m.Ops.OfType<EditOp>().Count(x => x.Archive == G), Is.EqualTo(2));
                ed.Revert(OptionKind.Constant, 800000100);
                Assert.That(m.Ops.OfType<EditOp>().Count(), Is.EqualTo(0));
                Assert.That(ed.Cell(gms, 800000100, 2, "mar_value_base").Value, Is.EqualTo("4"));
            }
        }

        static string CodeText(long c) { return c.ToString(System.Globalization.CultureInfo.InvariantCulture); }

        [Test]
        public void RevertOfAReservedCodeWithNothingKeepsTheReservation()
        {
            using (var f = new FakeOptions())
            {
                Mod m = FakeOptions.ModOf("");
                var ed = new ItemOptionEditor(f.Data, m);
                long code = ed.NextCodes(1)[0];
                IdSpaces.Reserve(m, ItemOptionRules.Space, code);
                Assert.That(ed.Revert(OptionKind.Constant, code), Is.EqualTo(0));
                Assert.That(Reserved(m, code), Is.True);
            }
        }

        [Test]
        public void ForkRepointsKmsOnlyWhereKmsHasTheCode()
        {
            using (var f = new FakeOptions())
            {
                Mod m = FakeOptions.ModOf("");
                var ed = new ItemOptionEditor(f.Data, m);
                OptionSide k = Side(ed, OptionKind.Constant, true);
                // the kms table loses 800000200 in this mod: the fork copies the server rows only
                ed.Revert(OptionKind.Constant, 800000200);
                m.Ops.Add(new EditOp { Archive = K, Entry = ItemOptionRules.KmsEntry, Rows = { new RowOp { Kind = RowOpKind.Delete, At = "option[code=800000200]" } } });
                ed = new ItemOptionEditor(f.Data, m);
                Assert.That(ed.Has(Side(ed, OptionKind.Constant, true), 800000200), Is.False);
                long code = ed.NextCodes(1)[0];
                Assert.That(ed.Fork(OptionKind.Constant, 800000200, code, new long[] { 99100003 }), Is.EqualTo(1));
                Plan p = PlanOf(f, m);
                Assert.That(Fake.Text(p, S, "item/9/91/99100003.xml"), Does.Contain("constant=\"" + code + "\""));
                string bucket = Fake.Text(p, K, "itemdata/991.xml");
                Assert.That(bucket == null || !bucket.Contains("constantID=\"" + code + "\""), Is.True, bucket);
            }
        }

        [Test]
        public void NewCodeOfAnotherModIsTaken()
        {
            using (var f = new FakeOptions())
            {
                Mod other = FakeOptions.ModOf(Edit(S, CFile, "<add><xml><option code=\"900100000\" grade=\"1\" mar_value_base=\"1\" /></xml></add>"), "other.mod");
                var ed = new ItemOptionEditor(f.Data, FakeOptions.ModOf(""));
                ed.Others = delegate { return new List<Mod> { other }; };
                Assert.That(ed.CheckNewCode(900100000), Does.Contain("other.mod adds option 900100000"));
                Assert.That(ed.NextCodes(1)[0], Is.EqualTo(900100001));
            }
        }

        [Test]
        public void GradeRowGoesBesideItsCodeInAFileTheServerReads()
        {
            using (var f = new FakeOptions(true))
            {
                Mod m = FakeOptions.ModOf("");
                var ed = new ItemOptionEditor(f.Data, m);
                OptionSide s = ed.Sides(OptionKind.Static)[0];
                Assert.That(ed.Codes(OptionKind.Static).Find(i => i.Code == 800000500).Files.Count, Is.EqualTo(2), "a code in two files");
                ed.Set(s, 800000500, 1, "str_value_base", "1,1");
                ed.Set(s, 800000500, 5, "str_value_base", "5,7");
                // grade 3 is in both files: the cell shows, and the edit lands in, the file the server reads
                Assert.That(ed.Cell(s, 800000500, 3, "str_value_base").Value, Is.EqualTo("3,5"));
                ed.Set(s, 800000500, 3, "str_value_base", "3,6");
                Plan p = PlanOf(f, m);
                string t = Fake.Text(p, S, SFile);
                Assert.That(Fake.Text(p, S, ItemOptionRules.StaticPrefix + "55.xml"), Is.Null, "the unread file stays as shipped");
                var order = new List<string>();
                foreach (string l in t.Replace("\r\n", "\n").Split('\n')) if (l.Contains("code=\"800000500\"")) order.Add(l.Substring(l.IndexOf("grade=", StringComparison.Ordinal), 9));
                Assert.That(order, Is.EqualTo(new[] { "grade=\"1\"", "grade=\"3\"", "grade=\"4\"", "grade=\"5\"" }));
                Assert.That(Line(t, "800000500", "3"), Does.Contain("str_value_base=\"3,6\""));
                SameBut(t, FakeOptions.Static, "code=\"800000500\" grade=\"1\"", "code=\"800000500\" grade=\"5\"", "code=\"800000500\" grade=\"3\"");
                Assert.That(t.Length, Is.GreaterThan(FakeOptions.Static.Length));
            }
        }

        [Test]
        public void FlatEditLeavesTheOtherRowsByteForByte()
        {
            using (var f = new FakeOptions())
            {
                Mod m = FakeOptions.ModOf("");
                var ed = new ItemOptionEditor(f.Data, m);
                ed.Set(Side(ed, OptionKind.Constant, false), 800000100, 2, "mar_value_base", "5");
                Plan p = PlanOf(f, m);
                SameBut(Fake.Text(p, S, CFile), FakeOptions.Constant, "code=\"800000100\" grade=\"2\"");
                Assert.That(Line(Fake.Text(p, S, CFile), "800000100", "2"), Is.EqualTo("<option code=\"800000100\" grade=\"2\" mar_value_base=\"5\" />"));
            }
        }

        [Test]
        public void KmsWriteKeepsANestedHandEdit()
        {
            using (var f = new FakeOptions())
            {
                Mod m = FakeOptions.ModOf(Edit(K, ItemOptionRules.KmsEntry, "<change at=\"option[code=800000100]\"><set at=\"rank[grade=1]/v[name=mar]\" name=\"value\" value=\"6\" /></change>"));
                var ed = new ItemOptionEditor(f.Data, m);
                OptionSide k = Side(ed, OptionKind.Constant, true);
                Assert.That(ed.Cell(k, 800000100, 1, "mar_value_base").Value, Is.EqualTo("6"));
                ed.Set(k, 800000100, 3, "str_value_base", "4");
                Assert.That(ed.Cell(k, 800000100, 1, "mar_value_base").Value, Is.EqualTo("6"), "the hand edit stays");
                Plan p = PlanOf(f, m);
                string kt = Fake.Text(p, K, ItemOptionRules.KmsEntry);
                Assert.That(kt, Does.Contain("<v name=\"mar\" value=\"6\" />"));
                Assert.That(kt, Does.Contain("<v name=\"str\" value=\"4\" />"));
            }
        }

        [Test]
        public void PlanWarnsOfAGradeItemsLose()
        {
            using (var f = new FakeOptions())
            {
                // 99100003 moves from 800000200 (grade 1) to 800000100 (grades 1-4): nothing lost; 99100002 moves
                // from 800000100 to 800000200, which has no rows at grades 2-4
                string body = Edit(S, "item/9/91/99100002.xml", "<change at=\"environment/option\"><set name=\"constant\" value=\"800000200\" /></change>") +
                    Edit(S, "item/9/91/99100003.xml", "<change at=\"environment/option\"><set name=\"constant\" value=\"800000100\" /></change>") +
                    Edit(S, CFile, "<delete at=\"option[code=800000100][grade=4]\" />");
                Plan p = Planner.Build(f.Data, new[] { FakeOptions.ModOf(body) }, new PlanOptions());
                Assert.That(Has(p.Warnings, "the item moves from constant option 800000100 to 800000200, which has no row at grade 2,3"), Is.True, Fake.Dump(p));
                Assert.That(Has(p.Warnings, "moves from constant option 800000200"), Is.False, Fake.Dump(p));
                Assert.That(Has(p.Warnings, "option 800000100 grade 4: the code ships a row at this grade and the mod leaves none"), Is.True, Fake.Dump(p));
            }
        }

        [Test]
        public void PlanChecksTheKmsPointer()
        {
            using (var f = new FakeOptions())
            {
                string body = Edit(K, "itemdata/991.xml", "<change at=\"item[id=99100002]/option\"><set name=\"constantID\" value=\"800000777\" /></change>");
                Plan p = Planner.Build(f.Data, new[] { FakeOptions.ModOf(body) }, new PlanOptions());
                Assert.That(Has(p.Errors, "item 99100002 names constant option 800000777 (constantID, was 800000100) but the kms table"), Is.True, Fake.Dump(p));
                body = Edit(K, "itemdata/991.xml", "<change at=\"item[id=99100002]/option\"><set name=\"constantID\" value=\"800000200\" /></change>");
                p = Planner.Build(f.Data, new[] { FakeOptions.ModOf(body) }, new PlanOptions());
                Assert.That(Has(p.Errors, "constantID"), Is.False, Fake.Dump(p));
                // the second option of 99100004 is compared with its own shipped code, not with the first option's 0
                body = Edit(K, "itemdata/991.xml", "<change at=\"item[id=99100004]/option[constantID=800000200]\"><set name=\"constantID\" value=\"800000777\" /></change>");
                p = Planner.Build(f.Data, new[] { FakeOptions.ModOf(body) }, new PlanOptions());
                Assert.That(Has(p.Errors, "item 99100004 names constant option 800000777 (constantID, was 800000200) but the kms table"), Is.True, Fake.Dump(p));
                // the first option moving off 0 to an unknown code: it named no row before, so no error
                body = Edit(K, "itemdata/991.xml", "<change at=\"item[id=99100004]/option[constantID=0]\"><set name=\"constantID\" value=\"800000777\" /></change>");
                p = Planner.Build(f.Data, new[] { FakeOptions.ModOf(body) }, new PlanOptions());
                Assert.That(Has(p.Errors, "constantID"), Is.False, Fake.Dump(p));
            }
        }

        static string Edit(string archive, string entry, string ops) { return "<edit archive=\"" + archive + "\" entry=\"" + entry + "\">" + ops + "</edit>"; }

        static bool Has(List<PlanMessage> list, string text)
        {
            foreach (PlanMessage msg in list) if (msg.Text.Contains(text)) return true;
            return false;
        }

        [Test]
        public void ChecksFlatRows()
        {
            using (var f = new FakeOptions())
            {
                string body =
                    Edit(S, CFile, "<add><xml><option code=\"800000200\" grade=\"1\" dex_value_base=\"2\" /></xml></add>" +
                        "<change at=\"option[code=800000100][grade=3]\"><set name=\"mar_value_base\" value=\"x\" /><set name=\"luck_value_base\" value=\"1\" /></change>") +
                    Edit(S, SFile, "<change at=\"option[code=800000500][grade=3]\"><set name=\"optionNumPick\" value=\"1,3\" /></change>" +
                        "<change at=\"option[code=800000500][grade=4]\"><set name=\"optionNumPick\" value=\"0,0\" /></change>");
                Plan p = Planner.Build(f.Data, new[] { FakeOptions.ModOf(body) }, new PlanOptions());
                Assert.That(Has(p.Errors, "option 800000200 grade 1 is in"), Is.True, Fake.Dump(p));
                Assert.That(Has(p.Errors, "luck_value_base"), Is.True, Fake.Dump(p));
                Assert.That(Has(p.Errors, "option 800000100 grade 3: "), Is.True, Fake.Dump(p));
                Assert.That(Has(p.Warnings, "picks up to 3 lines but the row lists 1"), Is.True, Fake.Dump(p));
                Assert.That(Has(p.Warnings, "option 800000500 grade 4: optionNumPick is empty or 0,0"), Is.True, Fake.Dump(p));
            }
        }

        [Test]
        public void ChecksSidesAndPointers()
        {
            using (var f = new FakeOptions())
            {
                // the server's grade 3 row moves away from the kms rank it agreed with; an item names a code nobody has
                string body =
                    Edit(S, CFile, "<change at=\"option[code=800000100][grade=3]\"><set name=\"mar_value_base\" value=\"9\" /></change>") +
                    Edit(S, "item/9/91/99100002.xml", "<change at=\"environment/option\"><set name=\"static\" value=\"800000777\" /></change>");
                Plan p = Planner.Build(f.Data, new[] { FakeOptions.ModOf(body) }, new PlanOptions());
                Assert.That(Has(p.Warnings, "option 800000100: the kms client shows other stats than the server applies: grade 3 mar (kms client 8, server 9)"), Is.True, Fake.Dump(p));
                Assert.That(Has(p.Warnings, "grade 1 mar"), Is.False, "grade 1 still agrees");
                Assert.That(Has(p.Errors, "static option 800000777"), Is.True, Fake.Dump(p));
                Assert.That(Has(p.Errors, "constant option"), Is.False, Fake.Dump(p));
            }
        }

        [Test]
        public void EditorEditsPlanWithoutErrors()
        {
            using (var f = new FakeOptions())
            {
                Mod m = FakeOptions.ModOf("");
                var ed = new ItemOptionEditor(f.Data, m);
                OptionSide k = Side(ed, OptionKind.Constant, true), s = Side(ed, OptionKind.Constant, false);
                ed.Set(k, 800000100, 1, "mar_value_base", "5");
                ed.Set(s, 800000100, 4, "mar_value_base", "11");
                long code = ed.NextCodes(1)[0];
                ed.Fork(OptionKind.Constant, 800000100, code, new long[] { 99100001 });
                Plan p = Planner.Build(f.Data, new[] { m }, new PlanOptions());
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Has(p.Warnings, "space \"itemoption\""), Is.False, "a rewritten code is no new id, the option places are alternatives: " + Fake.Dump(p));
                Assert.That(Has(p.Warnings, "new id 800000100 "), Is.False, Fake.Dump(p));
                Assert.That(Has(p.Warnings, "constantID is changed on the client only"), Is.False, "the pointers are set per side: " + Fake.Dump(p));
            }
        }

        [Test]
        public void ChecksKmsRows()
        {
            using (var f = new FakeOptions())
            {
                string body = Edit(K, ItemOptionRules.KmsEntry,
                    "<add><xml><option code=\"800000200\"><rank><v name=\"dex\" value=\"3\" /></rank></option></xml></add>" +
                    "<change at=\"option[code=800000100]/rank[grade=1]/v[name=mar]\"><set name=\"value\" value=\"4.5\" /></change>");
                Plan p = Planner.Build(f.Data, new[] { FakeOptions.ModOf(body) }, new PlanOptions());
                Assert.That(Has(p.Errors, "option 800000200 is in the table twice"), Is.True, Fake.Dump(p));
                Assert.That(Has(p.Errors, "option 800000100: "), Is.True, Fake.Dump(p));
            }
        }
    }
}
