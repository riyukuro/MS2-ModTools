// The random kind of the item stat options (ItemOptions.cs, PlannerItemOptions.cs) on the made-up tables of FakeOptions.
using System;
using System.Collections.Generic;
using System.Linq;
using MS2.ModKit;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class ItemOptionsRandomTests
    {
        const string K = "client:Xml.m2d", S = "server:Xml.m2d";
        const string RFile = ItemOptionRules.RandomPrefix + "12.xml", KItems = "itemdata/991.xml", SItem = "item/9/91/99100001.xml";
        const OptionKind R = OptionKind.Random;

        static OptionSide Side(ItemOptionEditor ed, bool kms)
        {
            foreach (OptionSide s in ed.Sides(R)) if (s.Kms == kms) return s;
            return null;
        }

        static Plan PlanOf(FakeOptions f, Mod m) { return Planner.Build(f.Data, new[] { Mod.Parse(m.ToXml(), "round trip") }, new PlanOptions()); }

        static string Edit(string archive, string entry, string ops) { return "<edit archive=\"" + archive + "\" entry=\"" + entry + "\">" + ops + "</edit>"; }

        static bool Has(List<PlanMessage> list, string text)
        {
            foreach (PlanMessage msg in list) if (msg.Text.Contains(text)) return true;
            return false;
        }

        static string Row(string text, string code, string grade)
        {
            if (text == null) return null;
            foreach (string l in text.Split('\n'))
                if (l.Contains("code=\"" + code + "\"") && (grade == null || l.Contains("grade=\"" + grade + "\""))) return l.Trim();
            return null;
        }

        [Test]
        public void SidesCodesAndCells()
        {
            using (var f = new FakeOptions())
            {
                var ed = new ItemOptionEditor(f.Data, FakeOptions.ModOf(""));
                Assert.That(ed.KmsConstant && ed.KmsRandom, Is.True);
                Assert.That(ed.Sides(R).Select(x => x.Archive), Is.EqualTo(new[] { K, S }));
                Assert.That(ed.Sides(OptionKind.Static).Select(x => x.Archive), Is.EqualTo(new[] { S }), "kms has no static table");
                List<OptionCodeInfo> codes = ed.Codes(R);
                Assert.That(codes.Select(c => c.Code), Is.EqualTo(new long[] { 800000700, 800000800 }));
                Assert.That(codes[0].Sides, Is.EqualTo(new[] { "kms client", "server" }));
                Assert.That(codes[0].Items, Is.EqualTo(new long[] { 99100001 }));
                Assert.That(codes[0].GradeText, Is.EqualTo("1,2,3"));
                Assert.That(codes[1].Sides, Is.EqualTo(new[] { "kms client" }));
                Assert.That(ed.ItemsOf(R, 800000700), Is.EqualTo(new long[] { 99100001 }));
                Assert.That(ed.CodesOf(99100001, R), Is.EqualTo(new long[] { 800000700 }));
                // the constant and static codes are not random codes
                Assert.That(ed.Codes(OptionKind.Constant).Any(c => c.Code == 800000700), Is.False);

                OptionSide kms = Side(ed, true), srv = Side(ed, false);
                Assert.That(ed.Stats(R, 800000700), Is.EqualTo(new[] { ItemOptionRules.Pick, "pen_rate_base", "str_value_base" }));
                Assert.That(ed.Stats(R, 800000800), Is.EqualTo(new[] { ItemOptionRules.Pick, "dex_value_base" }), "a kms line no flat side has shows as X_value_base");
                Assert.That(ed.Cell(srv, 800000700, 3, ItemOptionRules.Pick).Value, Is.EqualTo("2,2"));
                Assert.That(ed.Cell(srv, 800000700, 2, "pen_rate_base").Value, Is.EqualTo("0.01,0.02"));
                // kms: the lines an item of the grade gets (isRarePickOne), and each line's range; read-only in the grid
                Assert.That(new[] { 1, 2, 3, 4 }.Select(g => ed.Cell(kms, 800000700, g, ItemOptionRules.Pick).Value), Is.EqualTo(new[] { "0", "1", "2", "2" }));
                OptionCell c1 = ed.Cell(kms, 800000700, 3, "str_value_base");
                Assert.That(c1.Value, Is.EqualTo("3..5"));
                Assert.That(c1.ReadOnly, Is.Not.Null);
                Assert.That(c1.Edited, Is.False);
                Assert.That(ed.Cell(kms, 800000700, 3, "pen_rate_base").Value, Is.EqualTo("7"));
                Assert.That(ed.Cell(kms, 800000800, 1, "dex_value_base").Value, Is.EqualTo("statID 8009"));
                Assert.That(ed.Cell(kms, 800000700, 1, "dex_value_base").Value, Is.EqualTo(""));
                Assert.That(ed.Differs(R, 800000700, 3, "str_value_base"), Is.False, "kms random values are not compared");
                Assert.That(Assert.Throws<ModKitException>(() => ed.Set(kms, 800000700, 3, "str_value_base", "4")).Message, Does.Contain("no grades"));
                Assert.That(ed.Mod.Ops, Is.Empty);
            }
        }

        [Test]
        public void VariationRanges()
        {
            using (var f = new FakeOptions())
            {
                var ed = new ItemOptionEditor(f.Data, FakeOptions.ModOf(""));
                string a, b;
                Assert.That(ed.VariationRange("8001", out a, out b), Is.True);
                Assert.That(a + "-" + b, Is.EqualTo("3-5"));
                Assert.That(ed.VariationRange(" 8002 ", out a, out b), Is.True);
                Assert.That(a + "-" + b, Is.EqualTo("7-7"));
                Assert.That(ed.VariationRange("8009", out a, out b), Is.False);
                Assert.That(ed.VariationRange(null, out a, out b), Is.False);
            }
        }

        [Test]
        public void ServerCellsEditAndThePickCountIsChecked()
        {
            using (var f = new FakeOptions())
            {
                Mod m = FakeOptions.ModOf("");
                var ed = new ItemOptionEditor(f.Data, m);
                OptionSide srv = Side(ed, false);
                ed.Set(srv, 800000700, 3, "str_value_base", "4,6");
                Assert.That(Assert.Throws<ModKitException>(() => ed.Set(srv, 800000700, 3, "str_value_base", "6,4")).Message, Does.Contain("min is above max"));
                Assert.That(Assert.Throws<ModKitException>(() => ed.Set(srv, 800000700, 3, ItemOptionRules.Pick, "2")).Message, Does.Contain("min,max"));
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(p.Warnings, Is.Empty, Fake.Dump(p));
                Assert.That(Row(Fake.Text(p, S, RFile), "800000700", "3"), Does.Contain("str_value_base=\"4,6\""));
                Assert.That(p.Find(K), Is.Null, "the kms table has no values to mirror to");
                ed.Set(srv, 800000700, 3, ItemOptionRules.Pick, "1,3");
                p = PlanOf(f, m);
                Assert.That(Has(p.Warnings, "option 800000700 grade 3: optionNumPick picks up to 3 lines but the row lists 2 stats"), Is.True, Fake.Dump(p));
                // a random row that picks nothing is as shipped: no "skips the row" warning
                ed.Set(srv, 800000700, 3, ItemOptionRules.Pick, "0,0");
                p = PlanOf(f, m);
                Assert.That(p.Warnings, Is.Empty, Fake.Dump(p));
                Assert.That(ed.Revert(R, 800000700), Is.GreaterThan(0));
                Assert.That(PlanOf(f, m).Find(S), Is.Null);
            }
        }

        [Test]
        public void KmsRowAttributesAndLines()
        {
            using (var f = new FakeOptions())
            {
                Mod m = FakeOptions.ModOf("");
                var ed = new ItemOptionEditor(f.Data, m);
                KmsRandomRow row = ed.KmsRandomOf(800000700);
                Assert.That(row.Attrs.Select(a => a.Key + "=" + a.Value), Is.EqualTo(new[] { "statGroup=3", "levelGroupID=1", "pickCount=2", "isRarePickOne=1" }));
                Assert.That(row.Lines.Select(l => l.Name + " " + l.StatId + " " + l.Weight + " " + l.RangeText), Is.EqualTo(new[] { "str 8001  3..5", "pen 8002 3 7" }));
                Assert.That(row.Edited, Is.False);
                Assert.That(new[] { 1, 2, 3 }.Select(g => row.PickCount(g)), Is.EqualTo(new[] { 0, 1, 2 }));
                Assert.That(ed.KmsRandomOf(800000800).Lines[0].HasRange, Is.False);
                Assert.That(ed.KmsRandomOf(800000100), Is.Null);

                // what the client cannot read is refused
                Assert.That(Assert.Throws<ModKitException>(() => ed.SetKmsRandomLine(800000700, 0, "weight", "0")).Message, Does.Contain("never picks"));
                Assert.That(Assert.Throws<ModKitException>(() => ed.SetKmsRandom(800000700, "pickCount", "-1")).Message, Does.Contain("whole number"));
                Assert.That(Assert.Throws<ModKitException>(() => ed.SetKmsRandom(800000700, "isRarePickOne", "2")).Message, Does.Contain("0 or 1"));
                Assert.That(Assert.Throws<ModKitException>(() => ed.SetKmsRandom(800000700, "code", "5")).Message, Does.Contain("no attribute"));
                Assert.That(Assert.Throws<ModKitException>(() => ed.SetKmsRandomLine(800000700, 0, "statID", "")).Message, Does.Contain("needs a statID"));
                Assert.That(Assert.Throws<ModKitException>(() => ed.SetKmsRandomLine(800000700, 5, "weight", "2")).Message, Does.Contain("no line 6"));
                Assert.That(Assert.Throws<ModKitException>(() => ed.AddKmsRandomLine(800000700, "nosuchstat", "8001", "")).Message, Does.Contain("no stat name"));
                Assert.That(Assert.Throws<ModKitException>(() => ed.SetKmsRandom(800000999, "pickCount", "1")).Message, Does.Contain("no option 800000999"));
                Assert.That(m.Ops, Is.Empty);

                ed.SetKmsRandom(800000700, "isRarePickOne", "");
                ed.SetKmsRandom(800000700, "pickCount", "3");
                ed.SetKmsRandomLine(800000700, 1, "weight", "");
                ed.SetKmsRandomLine(800000700, 0, "weight", "5");
                ed.AddKmsRandomLine(800000700, "DEX", "8002", "2");
                row = ed.KmsRandomOf(800000700);
                Assert.That(row.Edited, Is.True);
                Assert.That(row.Lines.Select(l => l.Name + " " + l.StatId + " " + l.Weight), Is.EqualTo(new[] { "str 8001 5", "pen 8002 ", "dex 8002 2" }));
                Assert.That(row.PickCount(1), Is.EqualTo(3));
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(p.Warnings, Is.Empty, Fake.Dump(p));
                string text = Fake.Text(p, K, ItemOptionRules.KmsRandomEntry);
                Assert.That(Row(text, "800000700", null), Is.EqualTo("<option code=\"800000700\" statGroup=\"3\" levelGroupID=\"1\" pickCount=\"3\">"));
                Assert.That(text, Does.Contain("<v name=\"dex\" statID=\"8002\" weight=\"2\" />"));
                Assert.That(text.Split(new[] { "code=\"800000700\"" }, StringSplitOptions.None).Length, Is.EqualTo(2), "the shipped row is replaced, not doubled");
                Assert.That(Fake.Text(p, K, ItemOptionRules.KmsEntry), Is.Null);
                Assert.That(Fake.Text(p, S, RFile), Is.Null, "the server reads no kms attribute");

                // a pick count above the lines is the plan's warning
                ed.RemoveKmsRandomLine(800000700, 2);
                ed.RemoveKmsRandomLine(800000700, 1);
                p = PlanOf(f, m);
                Assert.That(Has(p.Warnings, "random option 800000700: pickCount picks 3 lines but the row lists 1"), Is.True, Fake.Dump(p));

                // back to the shipped text: no line of the mod is left
                Assert.That(ed.Revert(R, 800000700), Is.GreaterThan(0));
                Assert.That(m.Ops.OfType<EditOp>().All(e => e.Rows.Count == 0), Is.True, m.ToXml());
                ed.SetKmsRandom(800000700, "pickCount", "1");
                ed.SetKmsRandom(800000700, "pickCount", "2");
                Assert.That(m.Ops.OfType<EditOp>().All(e => e.Rows.Count == 0), Is.True, "typing the shipped value back restores the row: " + m.ToXml());
            }
        }

        [Test]
        public void ForkMovesBothPointersAndRevertTakesItBack()
        {
            using (var f = new FakeOptions())
            {
                Mod m = FakeOptions.ModOf("");
                var ed = new ItemOptionEditor(f.Data, m);
                long code = ed.NextCodes(1)[0];
                Assert.That(ed.Fork(R, 800000700, code, new long[] { 99100001, 99100002 }), Is.EqualTo(1), "99100002 names no random code");
                string c = code.ToString();
                Assert.That(ed.CheckNewCode(code), Is.Not.Null);
                Assert.That(ed.ItemsOf(R, code), Is.EqualTo(new long[] { 99100001 }));
                Assert.That(ed.ItemsOf(R, 800000700), Is.Empty);
                Assert.That(ed.Codes(R).Single(x => x.Code == code).Added, Is.True);
                Plan p = PlanOf(f, m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Row(Fake.Text(p, K, ItemOptionRules.KmsRandomEntry), c, null), Does.Contain("pickCount=\"2\""));
                Assert.That(Row(Fake.Text(p, K, ItemOptionRules.KmsRandomEntry), "800000700", null), Is.Not.Null);
                foreach (string g in new[] { "1", "2", "3" }) Assert.That(Row(Fake.Text(p, S, RFile), c, g), Is.Not.Null, "grade " + g);
                Assert.That(Fake.Text(p, K, KItems), Does.Contain("constantID=\"800000100\" randomID=\"" + c + "\""));
                Assert.That(Fake.Text(p, S, SItem), Does.Contain("constant=\"800000100\" static=\"800000500\" random=\"" + c + "\""));
                // the fork's own kms row is edited as any
                ed.SetKmsRandom(code, "pickCount", "1");
                Assert.That(ed.KmsRandomOf(code).PickCount(3), Is.EqualTo(1));
                Assert.That(ed.KmsRandomOf(800000700).PickCount(3), Is.EqualTo(2));

                Assert.That(ed.Revert(R, code), Is.GreaterThan(0));
                Assert.That(ed.ItemsOf(R, 800000700), Is.EqualTo(new long[] { 99100001 }));
                p = PlanOf(f, m);
                Assert.That(p.Find(K), Is.Null, Fake.Dump(p));
                Assert.That(p.Find(S), Is.Null, Fake.Dump(p));
                Assert.That(ed.CheckNewCode(code), Is.Null);

                // a code only the kms table has: the kms pointer alone moves
                Assert.That(ed.Fork(R, 800000800, code, null), Is.EqualTo(0));
                p = PlanOf(f, m);
                Assert.That(Row(Fake.Text(p, K, ItemOptionRules.KmsRandomEntry), c, null), Is.Not.Null);
                Assert.That(Fake.Text(p, S, RFile), Is.Null);
            }
        }

        [Test]
        public void DropDraftsKeepsBothKmsTables()
        {
            using (var f = new FakeOptions())
            {
                var ed = new ItemOptionEditor(f.Data, FakeOptions.ModOf(""));
                ed.Cell(Side(ed, true), 800000700, 1, ItemOptionRules.Pick);
                ed.Cell(Side(ed, false), 800000700, 1, ItemOptionRules.Pick);
                ed.Cell(ed.Sides(OptionKind.Constant)[0], 800000100, 1, "mar_value_base");
                ed.Cell(ed.Sides(OptionKind.Constant)[1], 800000100, 1, "mar_value_base");
                Assert.That(ed.DraftCount, Is.EqualTo(4));
                ed.DropDrafts();
                Assert.That(ed.DraftCount, Is.EqualTo(2), "the constant and the random table of the kms client stay");
                Assert.That(ed.Cell(Side(ed, false), 800000700, 3, ItemOptionRules.Pick).Value, Is.EqualTo("2,2"));
            }
        }

        [Test]
        public void PlanChecksHandWrittenRandomLines()
        {
            using (var f = new FakeOptions())
            {
                string body = Edit(K, ItemOptionRules.KmsRandomEntry,
                    "<add><xml><option code=\"800000800\" pickCount=\"1\"><v name=\"dex\" statID=\"8001\" /></option></xml></add>" +
                    "<add><xml><option code=\"900100007\" statGroup=\"x\" pickCount=\"4\"><v name=\"str\" statID=\"8001\" weight=\"0\" /><v name=\"pen\" statID=\"8555\" /></option></xml></add>") +
                    Edit(K, KItems, "<change at=\"item[id=99100001]/option\"><set name=\"randomID\" value=\"900100009\" /></change>") +
                    Edit(S, SItem, "<change at=\"environment/option\"><set name=\"random\" value=\"900100009\" /></change>") +
                    Edit(S, RFile, "<add><xml><option code=\"800000700\" grade=\"3\" optionNumPick=\"1,1\" str_value_base=\"1\" /></xml></add>" +
                        "<change at=\"option[code=800000700][grade=2]\"><set name=\"nosuch_value_base\" value=\"1\" /><set name=\"str_value_base\" value=\"x\" /></change>");
                Plan p = Planner.Build(f.Data, new[] { FakeOptions.ModOf(body) }, new PlanOptions());
                string dump = Fake.Dump(p);
                Assert.That(Has(p.Errors, "random option 800000800 is in the table twice"), Is.True, dump);
                Assert.That(Has(p.Errors, "random option 900100007: statGroup: \"x\""), Is.True, dump);
                Assert.That(Has(p.Errors, "random option 900100007: line str: weight: 0 never picks"), Is.True, dump);
                Assert.That(Has(p.Warnings, "random option 900100007: line pen: statID 8555 has no row in table/itemoptionvariation.xml"), Is.True, dump);
                Assert.That(Has(p.Warnings, "random option 900100007: pickCount picks 4 lines but the row lists 2"), Is.True, dump);
                Assert.That(Has(p.Warnings, "line str: statID 8001 has no row"), Is.False, dump);
                Assert.That(Has(p.Errors, "item 99100001 names random option 900100009 (randomID, was 800000700) but the kms table table/itemoptionrandom.xml has no row of it"), Is.True, dump);
                Assert.That(Has(p.Errors, "the item names random option 900100009 but no random option file the server reads has it"), Is.True, dump);
                Assert.That(Has(p.Errors, "option 800000700 grade 3 is in"), Is.True, dump);
                Assert.That(Has(p.Errors, "no shipped random file has the stat nosuch_value_base"), Is.True, dump);
                Assert.That(Has(p.Errors, "str_value_base: \"x\""), Is.True, dump);
                Assert.That(Has(p.Errors, "constant option"), Is.False, dump);
            }
        }
    }
}
