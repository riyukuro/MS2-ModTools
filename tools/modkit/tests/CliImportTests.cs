// modkit field-rows and text-import as the command line runs them, on the made-up data of FieldRowsTests and
// TextImportTests.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class CliImportTests
    {
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

        static string[] Roots(string dir, params string[] rest)
        {
            return new[] { "--client", Path.Combine(dir, "kms"), "--gms", Path.Combine(dir, "gms"), "--server", Path.Combine(dir, "server"), "-q" }.Concat(rest).ToArray();
        }

        [Test]
        public void FieldRows_ListsPutsAndTakesOut()
        {
            using (var d = new FieldRowsTests.Data())
            {
                string so, se, mod = Path.Combine(d.Dir, "mods", "t.fields");
                int rc = Run(out so, out se, Roots(d.Dir, "field-rows", "gms-client:Xml.m2d"));
                Assert.That(rc, Is.EqualTo(0), so + se);
                StringAssert.Contains("Fill 1, Add 4, FillBeside 1, Several 1, NoMap 1, NoXBlock 1, Error 1; 3 ticked by default", so);
                StringAssert.Contains("Fill\t19\t00000019_BF\tFakeOff\tno\t99\tyes\t16\tyes\n", so);
                StringAssert.Contains("FillBeside\t30\tThirty\tFakeOn\tyes\t1\tyes\t2\tno\n", so);
                StringAssert.Contains("Add\t41\tFortyOne\t-\t-\t-\tno\t1\tno\n", so);
                Assert.That(File.Exists(Path.Combine(mod, "mod.xml")), Is.False, "a scan writes nothing");

                rc = Run(out so, out se, Roots(d.Dir, "field-rows", "gms-client:Xml.m2d", "--put", mod));
                Assert.That(rc, Is.EqualTo(2), "no mod there and no --id");
                StringAssert.Contains("give --id", se);

                rc = Run(out so, out se, Roots(d.Dir, "field-rows", "gms-client:Xml.m2d", "--put", mod, "--id", "t.fields"));
                Assert.That(rc, Is.EqualTo(0), so + se);
                StringAssert.Contains("put 3 field rows into mod t.fields (3 chosen, 0 in the mod already)", so);
                Mod m = Mod.Load(mod);
                Assert.That(m.Id, Is.EqualTo("t.fields"));
                Assert.That(m.Ops.OfType<EditOp>().Single().Rows.Count, Is.EqualTo(3));

                rc = Run(out so, out se, Roots(d.Dir, "field-rows", "gms-client:Xml.m2d", "--put", mod, "--rows", "41,42,60"));
                Assert.That(rc, Is.EqualTo(2));
                StringAssert.Contains("60 (Several)", se);
                rc = Run(out so, out se, Roots(d.Dir, "field-rows", "gms-client:Xml.m2d", "--put", mod, "--rows", "ticked"));
                Assert.That(rc, Is.EqualTo(0), so + se);
                StringAssert.Contains("put 3 field rows into mod t.fields (6 chosen, 3 in the mod already)", so);
                StringAssert.Contains("\tin mod\n", so);

                rc = Run(out so, out se, Roots(d.Dir, "validate", mod));
                Assert.That(rc, Is.EqualTo(0), so + se);
                StringAssert.Contains("6 change(s)", so);

                rc = Run(out so, out se, Roots(d.Dir, "field-rows", "gms-client:Xml.m2d", "--take", mod, "--rows", "19"));
                Assert.That(rc, Is.EqualTo(0), so + se);
                StringAssert.Contains("took 1 field rows out of mod t.fields", so);
                rc = Run(out so, out se, Roots(d.Dir, "field-rows", "gms-client:Xml.m2d", "--take", mod));
                Assert.That(rc, Is.EqualTo(0), so + se);
                StringAssert.Contains("took 5 field rows", so);
                Assert.That(Mod.Load(mod).IsEmpty, Is.True);

                rc = Run(out so, out se, Roots(d.Dir, "field-rows", "gms-client:Xml.m2d", "--pack", "none", "--json"));
                Assert.That(rc, Is.EqualTo(0), so + se);
                StringAssert.Contains("\"onServer\": null", so);
                StringAssert.Contains("\"kind\": \"Error\"", so);
            }
        }

        [Test]
        public void TextImport_ListsPutsAndTakesOut()
        {
            using (var d = new TextImportTests.Data())
            {
                string so, se, mod = Path.Combine(d.Dir, "mods", "t.text");
                int rc = Run(out so, out se, Roots(d.Dir, "text-import", "gms-client:Xml.m2d", "--list"));
                Assert.That(rc, Is.EqualTo(0), so + se);
                StringAssert.Contains("text scan of client:Xml.m2d string/kr from gms-client:Xml.m2d string/en:", so);
                StringAssert.Contains("fill\tstring/kr/itemname.xml\tkey[id=1001]\t@name\t가짜 검:s\t-> Fake Sword:s\n", so);
                Assert.That(so, Does.Not.Contain("\nplain\t"), "fills only without --plain");

                rc = Run(out so, out se, Roots(d.Dir, "text-import", "gms-client:Xml.m2d", "--files", "nosuch.xml"));
                Assert.That(rc, Is.EqualTo(2));
                StringAssert.Contains("nosuch.xml is not one of our files", se);

                rc = Run(out so, out se, Roots(d.Dir, "text-import", "gms-client:Xml.m2d", "--files", "itemname.xml", "--put", mod, "--id", "t.text"));
                Assert.That(rc, Is.EqualTo(0), so + se);
                StringAssert.Contains("into mod t.text", so);
                Mod m = Mod.Load(mod);
                Assert.That(m.Ops.OfType<EditOp>().Select(e => e.Entry), Is.EqualTo(new[] { "string/kr/itemname.xml" }));

                rc = Run(out so, out se, Roots(d.Dir, "validate", mod));
                Assert.That(rc, Is.EqualTo(0), so + se);

                rc = Run(out so, out se, Roots(d.Dir, "text-import", "gms-client:Xml.m2d", "--take", mod));
                Assert.That(rc, Is.EqualTo(0), so + se);
                StringAssert.Contains("took ", so);
                Assert.That(Mod.Load(mod).IsEmpty, Is.True);
            }
        }
    }
}
