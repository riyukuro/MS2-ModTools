// Item stat options (ItemOptions.cs, PlannerItemOptions.cs) on made-up tables: a kms client and a server.
using System;
using System.Collections.Generic;
using System.IO;
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

        // kms: code 100 has a rank for grades 1-2 and one for 3 and up; 200 has one rank without grade; 300 is Lua
        public const string Kms = Head + "<ms2>\n" +
            "\t<option code=\"100\">\n" +
            "\t\t<rank grade=\"1\">\n\t\t\t<v name=\"mar\" value=\"4\" />\n\t\t</rank>\n" +
            "\t\t<rank grade=\"3\">\n\t\t\t<v name=\"mar\" value=\"8\" />\n\t\t\t<v name=\"str\" value=\"2\" />\n\t\t</rank>\n" +
            "\t</option>\n" +
            "\t<option code=\"200\">\n\t\t<rank>\n\t\t\t<v name=\"dex\" value=\"1\" />\n\t\t</rank>\n\t</option>\n" +
            "\t<option code=\"300\">\n\t\t<rank grade=\"1\" createType=\"3\" luaIndex=\"5\" />\n\t</option>\n" +
            "</ms2>\n";

        public const string Constant = Head + "<ms2>\n" +
            "\t<option code=\"100\" grade=\"1\" mar_value_base=\"4\" />\n" +
            "\t<option code=\"100\" grade=\"2\" mar_value_base=\"4\" />\n" +
            "\t<option code=\"100\" grade=\"3\" mar_value_base=\"8\" str_value_base=\"2\" />\n" +
            "\t<option code=\"100\" grade=\"4\" mar_value_base=\"8\" str_value_base=\"2\" pen_rate_base=\"0.01\" />\n" +
            "\t<option code=\"200\" grade=\"1\" dex_value_base=\"1\" />\n" +
            "</ms2>\n";

        public const string Static = Head + "<ms2>\n" +
            "\t<option code=\"500\" grade=\"3\" optionNumPick=\"1,1\" str_value_base=\"3,5\" />\n" +
            "\t<option code=\"500\" grade=\"4\" optionNumPick=\"1,1\" str_value_base=\"4,6\" />\n" +
            "</ms2>\n";

        static string ItemFile(long id, string constant, string stat)
        {
            return Head + "<ms2>\n\t<environment>\n\t\t<basic itemID=\"" + id + "\" />\n\t\t<option constant=\"" + constant + "\" static=\"" + stat + "\" random=\"0\" />\n\t</environment>\n</ms2>\n";
        }

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        public FakeOptions()
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-options-" + Guid.NewGuid().ToString("N"));
            string k = Path.Combine(Dir, "kms"), s = Path.Combine(Dir, "server");
            foreach (string d in new[] { k, s }) Directory.CreateDirectory(d);
            string rows = Head + "<ms2>\n" +
                "\t<item id=\"11200001\">\n\t\t<option constantID=\"100\" optionLevel=\"1\" />\n\t</item>\n" +
                "\t<item id=\"11200002\">\n\t\t<option constantID=\"100\" optionLevel=\"1\" />\n\t</item>\n" +
                "\t<item id=\"11200003\">\n\t\t<option constantID=\"200\" optionLevel=\"1\" />\n\t</item>\n" +
                "</ms2>\n";
            M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E(ItemOptionRules.KmsEntry, Kms), E("itemdata/112.xml", rows),
            });
            M2dWriter.Create(Path.Combine(s, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E(ItemOptionRules.ConstantPrefix + "equip.xml", Constant),
                E(ItemOptionRules.StaticPrefix + "12.xml", Static),
                E("item/1/12/11200001.xml", ItemFile(11200001, "100", "500")),
                E("item/1/12/11200002.xml", ItemFile(11200002, "100", "0")),
                E("item/1/12/11200003.xml", ItemFile(11200003, "200", "0")),
            });
            Data = new DataSet(k, null, s);
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
        const string K = "client:Xml.m2d", S = "server:Xml.m2d";
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
                OptionCell c = ed.Cell(k, 100, 2, "mar_value_base");
                Assert.That(c.Value, Is.EqualTo("4"));
                Assert.That(c.Grades, Is.EqualTo(new[] { 1, 2 }));
                Assert.That(ed.Cell(k, 100, 5, "mar_value_base").Grades, Is.EqualTo(new[] { 3, 4, 5, 6 }));
                Assert.That(ed.Cell(k, 200, 4, "dex_value_base").Value, Is.EqualTo("1"));
                Assert.That(ed.Cell(k, 300, 1, "mar_value_base").ReadOnly, Is.Not.Null);
                Assert.That(ed.Cell(s, 100, 4, "pen_rate_base").Value, Is.EqualTo("0.01"));
                Assert.That(ed.Cell(s, 100, 5, "mar_value_base").Source, Does.Contain("no row"));
                Assert.That(ed.Differs(OptionKind.Constant, 100, 3, "mar_value_base"), Is.False);
                // kms has no rank value at grade 5 for code 200's... both agree; code 100 grade 5: kms 8, server none
                Assert.That(ed.Differs(OptionKind.Constant, 100, 5, "mar_value_base"), Is.True);
                List<OptionCodeInfo> codes = ed.Codes(OptionKind.Constant);
                OptionCodeInfo c100 = codes.Find(i => i.Code == 100);
                Assert.That(c100.Items, Is.EquivalentTo(new long[] { 11200001, 11200002 }));
                Assert.That(c100.Sides, Is.EquivalentTo(new[] { "kms client", "server" }));
                Assert.That(ed.Codes(OptionKind.Static).Find(i => i.Code == 500).Items, Is.EqualTo(new long[] { 11200001 }));
                Assert.That(ed.Stats(OptionKind.Constant, 100), Does.Contain("pen_rate_base").And.Contain("str_value_base"));
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
                List<string> said = ed.Set(s, 100, 1, "mar_value_base", "5");
                Assert.That(said.Count, Is.EqualTo(1), string.Join("\n", said.ToArray()));
                Assert.That(ed.Cell(k, 100, 1, "mar_value_base").Value, Is.EqualTo("4"));
                Assert.That(ed.Differs(OptionKind.Constant, 100, 1, "mar_value_base"), Is.True);
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Line(Fake.Text(p, S, CFile), "100", "1"), Does.Contain("mar_value_base=\"5\""));
                Assert.That(Line(Fake.Text(p, S, CFile), "100", "2"), Does.Contain("mar_value_base=\"4\""));
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
                List<string> said = ed.Set(k, 100, 3, "mar_value_base", "9");
                Assert.That(said.Count, Is.EqualTo(2), string.Join("\n", said.ToArray()));
                Assert.That(ed.Cell(k, 100, 6, "mar_value_base").Value, Is.EqualTo("9"));
                Assert.That(ed.Cell(s, 100, 3, "mar_value_base").Value, Is.EqualTo("9"));
                Assert.That(ed.Cell(s, 100, 4, "mar_value_base").Value, Is.EqualTo("9"));
                Assert.That(ed.Cell(s, 100, 2, "mar_value_base").Value, Is.EqualTo("4"));
                // a new stat on the kms rank of grades 1-2: the server rows are written where they had none either
                ed.Set(k, 100, 1, "str_value_base", "1");
                Assert.That(ed.Cell(s, 100, 2, "str_value_base").Value, Is.EqualTo("1"));
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string kt = Fake.Text(p, K, ItemOptionRules.KmsEntry);
                Assert.That(kt, Does.Contain("<v name=\"mar\" value=\"9\" />"));
                Assert.That(kt, Does.Contain("<v name=\"str\" value=\"1\" />"));
                Assert.That(kt, Does.Contain("<option code=\"200\">"), "the other rows stay");
                Assert.That(Line(Fake.Text(p, S, CFile), "100", "4"), Does.Contain("mar_value_base=\"9\""));
                // back to what ships: the kms row is the shipped one again
                ed.Set(k, 100, 3, "mar_value_base", "8");
                ed.Set(k, 100, 1, "str_value_base", "");
                Assert.That(ed.Cell(s, 100, 3, "mar_value_base").Edited, Is.False);
                Assert.That(ed.Cell(s, 100, 1, "str_value_base").Value, Is.EqualTo(""));
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
                ed.Set(s, 100, 5, "mar_value_base", "12");
                Assert.That(ed.Cell(s, 100, 5, "mar_value_base").Value, Is.EqualTo("12"));
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Line(Fake.Text(p, S, CFile), "100", "5"), Does.Contain("mar_value_base=\"12\""));
                ed.Set(s, 100, 5, "mar_value_base", "");
                Assert.That(ed.Cell(s, 100, 5, "mar_value_base").Source, Does.Contain("no row"));
                Assert.Throws<ModKitException>(() => ed.Set(s, 100, 1, "mar_value_base", "x"));
                Assert.Throws<ModKitException>(() => ed.Set(s, 100, 1, "nosuch_value_base", "1"));
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
                Assert.That(ed.Stats(OptionKind.Static, 500)[0], Is.EqualTo(ItemOptionRules.Pick));
                ed.Set(s, 500, 3, "str_value_base", "4,7");
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Line(Fake.Text(p, S, SFile), "500", "3"), Does.Contain("str_value_base=\"4,7\""));
            }
        }

        [Test]
        public void ForkCopiesRowsAndRepointsItems()
        {
            using (var f = new FakeOptions())
            {
                Mod m = FakeOptions.ModOf("");
                var ed = new ItemOptionEditor(f.Data, m);
                Assert.That(ed.CheckNewCode(100), Is.Not.Null);
                long code = ed.NextCodes(1)[0];
                Assert.That(code, Is.EqualTo(ItemOptionRules.From));
                Assert.That(ed.ItemsOf(OptionKind.Constant, 100), Is.EqualTo(new long[] { 11200001, 11200002 }));
                int n = ed.Fork(OptionKind.Constant, 100, code, new long[] { 11200001 });
                Assert.That(n, Is.EqualTo(1));
                Assert.That(ed.ItemsOf(OptionKind.Constant, 100), Is.EqualTo(new long[] { 11200002 }));
                Assert.That(ed.ItemsOf(OptionKind.Constant, code), Is.EqualTo(new long[] { 11200001 }));
                OptionSide k = Side(ed, OptionKind.Constant, true), s = Side(ed, OptionKind.Constant, false);
                Assert.That(ed.Cell(s, code, 4, "pen_rate_base").Value, Is.EqualTo("0.01"));
                ed.Set(k, code, 3, "mar_value_base", "20");
                Assert.That(ed.Cell(s, code, 4, "mar_value_base").Value, Is.EqualTo("20"));
                Assert.That(ed.Cell(s, 100, 4, "mar_value_base").Value, Is.EqualTo("8"));
                Assert.That(ed.CheckNewCode(code), Is.Not.Null);
                bool reserved = false;
                foreach (IdRange r in m.Ids) if (r.Space == ItemOptionRules.Space && r.From <= code && code <= r.To) reserved = true;
                Assert.That(reserved, Is.True);
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string c = code.ToString();
                Assert.That(Line(Fake.Text(p, S, CFile), c, "4"), Does.Contain("mar_value_base=\"20\""));
                Assert.That(Fake.Text(p, K, ItemOptionRules.KmsEntry), Does.Contain("<option code=\"" + c + "\">"));
                Assert.That(Fake.Text(p, S, "item/1/12/11200001.xml"), Does.Contain("constant=\"" + c + "\""));
                Assert.That(Fake.Text(p, K, "itemdata/112.xml"), Does.Contain("constantID=\"" + c + "\""));
                Assert.That(ed.Revert(OptionKind.Constant, code), Is.GreaterThan(0));
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
                    Edit(S, CFile, "<add><xml><option code=\"200\" grade=\"1\" dex_value_base=\"2\" /></xml></add>" +
                        "<change at=\"option[code=100][grade=3]\"><set name=\"mar_value_base\" value=\"x\" /><set name=\"luck_value_base\" value=\"1\" /></change>") +
                    Edit(S, SFile, "<change at=\"option[code=500][grade=3]\"><set name=\"optionNumPick\" value=\"1,3\" /></change>" +
                        "<change at=\"option[code=500][grade=4]\"><set name=\"optionNumPick\" value=\"0,0\" /></change>");
                Plan p = Planner.Build(f.Data, new[] { FakeOptions.ModOf(body) }, new PlanOptions());
                Assert.That(Has(p.Errors, "option 200 grade 1 is in"), Is.True, Fake.Dump(p));
                Assert.That(Has(p.Errors, "luck_value_base"), Is.True, Fake.Dump(p));
                Assert.That(Has(p.Errors, "option 100 grade 3: "), Is.True, Fake.Dump(p));
                Assert.That(Has(p.Warnings, "picks up to 3 lines but the row lists 1"), Is.True, Fake.Dump(p));
                Assert.That(Has(p.Warnings, "option 500 grade 4: optionNumPick is empty or 0,0"), Is.True, Fake.Dump(p));
            }
        }

        [Test]
        public void ChecksSidesAndPointers()
        {
            using (var f = new FakeOptions())
            {
                // the server's grade 3 row moves away from the kms rank it agreed with; an item names a code nobody has
                string body =
                    Edit(S, CFile, "<change at=\"option[code=100][grade=3]\"><set name=\"mar_value_base\" value=\"9\" /></change>") +
                    Edit(S, "item/1/12/11200002.xml", "<change at=\"environment/option\"><set name=\"static\" value=\"777\" /></change>");
                Plan p = Planner.Build(f.Data, new[] { FakeOptions.ModOf(body) }, new PlanOptions());
                Assert.That(Has(p.Warnings, "option 100: the kms client shows other stats than the server applies: grade 3 mar (kms client 8, server 9)"), Is.True, Fake.Dump(p));
                Assert.That(Has(p.Warnings, "grade 1 mar"), Is.False, "grade 1 still agrees");
                Assert.That(Has(p.Errors, "static option 777"), Is.True, Fake.Dump(p));
                Assert.That(Has(p.Errors, "constant option"), Is.False, Fake.Dump(p));
            }
        }

        [Test]
        public void ChecksKmsRows()
        {
            using (var f = new FakeOptions())
            {
                string body = Edit(K, ItemOptionRules.KmsEntry,
                    "<add><xml><option code=\"200\"><rank><v name=\"dex\" value=\"3\" /></rank></option></xml></add>" +
                    "<change at=\"option[code=100]/rank[grade=1]/v[name=mar]\"><set name=\"value\" value=\"4.5\" /></change>");
                Plan p = Planner.Build(f.Data, new[] { FakeOptions.ModOf(body) }, new PlanOptions());
                Assert.That(Has(p.Errors, "option 200 is in the table twice"), Is.True, Fake.Dump(p));
                Assert.That(Has(p.Errors, "option 100: "), Is.True, Fake.Dump(p));
            }
        }
    }
}
