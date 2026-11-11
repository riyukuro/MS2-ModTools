// Tests that need no game data. Every table, row and name in here is made up.
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
    /// <summary>A made-up client + server data set in a temp folder.</summary>
    public sealed class Fake : IDisposable
    {
        public readonly string Dir, ClientDir, ServerDir;
        public DataSet Data;

        public const string PetXml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ms2>\n" +
            "\t<pet id=\"100\" name=\"Mossy\" hp=\"50\" speed=\"1.5\" />\n" +
            "\t<pet id=\"101\" name=\"Pebble\" hp=\"70\" speed=\"2\">\n" +
            "\t\t<skill id=\"7\" level=\"1\" />\n" +
            "\t\t<skill id=\"8\" level=\"2\" />\n" +
            "\t</pet>\n" +
            "\t<pet id=\"102\" name=\"Fern\" hp=\"55\" speed=\"1\" />\n" +
            "\t<pet id=\"102\" feature=\"Alpha\" name=\"Fern\" hp=\"60\" speed=\"1\" />\n" +
            "</ms2>\n";

        public static byte[] Crlf(string s) { return new UTF8Encoding(false).GetBytes(s.Replace("\n", "\r\n")); }

        public static string PetFile(int id, string name)
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ms2>\n\t<environment>\n\t\t<basic name=\"" + name + "\" kind=\"3\" />\n\t</environment>\n</ms2>\n";
        }

        public Fake()
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-fake-" + Guid.NewGuid().ToString("N"));
            ClientDir = Path.Combine(Dir, "orig", "client");
            ServerDir = Path.Combine(Dir, "orig", "server");
            Directory.CreateDirectory(ClientDir);
            Directory.CreateDirectory(ServerDir);
            var client = new List<KeyValuePair<string, byte[]>>
            {
                new KeyValuePair<string, byte[]>("table/pet.xml", Crlf(PetXml)),
                new KeyValuePair<string, byte[]>("pet/100.xml", Crlf(PetFile(100, "Mossy"))),
                new KeyValuePair<string, byte[]>("pet/101.xml", Crlf(PetFile(101, "Pebble"))),
                new KeyValuePair<string, byte[]>("table/readme.txt", Encoding.ASCII.GetBytes("not xml")),
                new KeyValuePair<string, byte[]>("table/empty.xml", new byte[0]),
            };
            var server = new List<KeyValuePair<string, byte[]>>
            {
                new KeyValuePair<string, byte[]>("table/pet.xml", Crlf(PetXml)),
                new KeyValuePair<string, byte[]>("pet/100.xml", Crlf(PetFile(100, "Mossy"))),
                new KeyValuePair<string, byte[]>("pet/101.xml", Crlf(PetFile(101, "Pebble"))),
                new KeyValuePair<string, byte[]>("pet/555.xml", Crlf(PetFile(555, "ServerOnly"))),
            };
            M2dWriter.Create(Path.Combine(ClientDir, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, client);
            M2dWriter.Create(Path.Combine(ServerDir, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, server);
            Data = new DataSet(ClientDir, ServerDir);
        }

        public string ModDir(string id, string body)
        {
            string d = Path.Combine(Dir, "mods", id);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "mod.xml"), "<mod id=\"" + id + "\" name=\"test\" version=\"1\">\n" + body + "\n</mod>\n", new UTF8Encoding(false));
            return d;
        }

        public Mod Mod(string id, string body) { return MS2.ModKit.Mod.Load(ModDir(id, body)); }

        public Plan Plan(params Mod[] mods) { return Planner.Build(Data, mods, null); }

        public static string Text(Plan p, string archive, string entry)
        {
            foreach (KeyValuePair<string, byte[]> kv in p.Find(archive).Put)
                if (kv.Key == entry) return Encoding.UTF8.GetString(kv.Value);
            return null;
        }

        public static string Dump(Plan p)
        {
            var sb = new StringBuilder();
            foreach (PlanMessage m in p.Errors) sb.Append("E ").Append(m).Append('\n');
            foreach (PlanMessage m in p.Conflicts) sb.Append("C ").Append(m).Append('\n');
            foreach (PlanMessage m in p.Warnings) sb.Append("W ").Append(m).Append('\n');
            foreach (PlanMessage m in p.Notes) sb.Append("N ").Append(m).Append('\n');
            foreach (PlanChange c in p.Changes) sb.Append("  ").Append(c).Append('\n');
            return sb.ToString();
        }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class ArchiveTests
    {
        [TestCase(PackVersion.MS2F)]
        [TestCase(PackVersion.NS2F)]
        [TestCase(PackVersion.OS2F)]
        [TestCase(PackVersion.PS2F)]
        public void CreateReadRewrite(PackVersion v)
        {
            string dir = Path.Combine(Path.GetTempPath(), "modkit-arch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var rnd = new Random(7);
                var big = new byte[300000];
                rnd.NextBytes(big);
                var entries = new List<KeyValuePair<string, byte[]>>
                {
                    new KeyValuePair<string, byte[]>("a/one.xml", Encoding.UTF8.GetBytes("<a x=\"1\"/>")),
                    new KeyValuePair<string, byte[]>("a/empty.xml", new byte[0]),
                    new KeyValuePair<string, byte[]>("b/random.bin", big),
                    new KeyValuePair<string, byte[]>("b/한글 이름.xml", Encoding.UTF8.GetBytes(new string('z', 5000))),
                };
                string p = Path.Combine(dir, "T.m2d");
                M2dWriter.Create(p, v, M2dFlag.AesZlib, entries);
                using (M2dArchive a = M2dArchive.Open(p))
                {
                    Assert.That(a.Version, Is.EqualTo(v));
                    Assert.That(a.Count, Is.EqualTo(4));
                    for (int i = 0; i < 4; i++)
                    {
                        Assert.That(a.Entries[i].Name, Is.EqualTo(entries[i].Key));
                        Assert.That(a.Entries[i].Index, Is.EqualTo(i + 1));
                        Assert.That(a.Read(a.Entries[i]), Is.EqualTo(entries[i].Value));
                    }
                    Assert.That(a.Find("B/RANDOM.BIN"), Is.Not.Null, "names are found without regard to case");

                    var ch = new M2dChanges();
                    ch.Set("a/one.xml", Encoding.UTF8.GetBytes("<a x=\"2\"/>"));
                    ch.Set("c/new.xml", Encoding.UTF8.GetBytes("<new/>"));
                    ch.Delete.Add("a/empty.xml");
                    string q = Path.Combine(dir, "out", "T.m2d");
                    Directory.CreateDirectory(Path.GetDirectoryName(q));
                    M2dWriteResult r = M2dWriter.Rewrite(a, q, ch, null);
                    Assert.That(new[] { r.Kept, r.Replaced, r.Added, r.Deleted }, Is.EqualTo(new[] { 2, 1, 1, 1 }));
                    using (M2dArchive b = M2dArchive.Open(q))
                    {
                        Assert.That(b.Entries.Select(e => e.Name).ToArray(), Is.EqualTo(new[] { "a/one.xml", "b/random.bin", "b/한글 이름.xml", "c/new.xml" }));
                        Assert.That(b.Entries.Select(e => e.Index).ToArray(), Is.EqualTo(new[] { 1, 2, 3, 4 }));
                        Assert.That(Encoding.UTF8.GetString(b.Read("a/one.xml")), Is.EqualTo("<a x=\"2\"/>"));
                        Assert.That(Encoding.UTF8.GetString(b.Read("c/new.xml")), Is.EqualTo("<new/>"));
                        Assert.That(b.ReadRaw(b.Find("b/random.bin")), Is.EqualTo(a.ReadRaw(a.Find("b/random.bin"))), "an untouched entry keeps its stored bytes");
                    }

                    // no change: the same two files, byte for byte
                    string s = Path.Combine(dir, "same", "T.m2d");
                    Directory.CreateDirectory(Path.GetDirectoryName(s));
                    M2dWriter.Rewrite(a, s, new M2dChanges(), null);
                    Assert.That(File.ReadAllBytes(s), Is.EqualTo(File.ReadAllBytes(p)));
                    Assert.That(File.ReadAllBytes(M2dArchive.HeaderPathOf(s)), Is.EqualTo(File.ReadAllBytes(M2dArchive.HeaderPathOf(p))));

                    Assert.Throws<ModKitException>(() => M2dWriter.Rewrite(a, p, ch, null), "writing over the source is refused");
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void CodecFlags()
        {
            var rnd = new Random(3);
            foreach (uint flag in new[] { M2dFlag.AesZlib, M2dFlag.Aes, M2dFlag.Xor, 0u })
                foreach (int n in new[] { 0, 1, 15, 16, 17, 4096, 70000 })
                {
                    var plain = new byte[n];
                    rnd.NextBytes(plain);
                    for (int i = 0; i < n / 2; i++) plain[i] = (byte)(i % 7);     // half of it compresses
                    foreach (PackVersion v in new[] { PackVersion.MS2F, PackVersion.NS2F, PackVersion.OS2F, PackVersion.PS2F })
                    {
                        uint compressedSize;
                        byte[] stored = M2dCodec.Encode(v, flag, plain, out compressedSize);
                        byte[] back = M2dCodec.Decode(v, flag, stored, compressedSize, (ulong)n);
                        Assert.That(back, Is.EqualTo(plain), v + " flag " + flag.ToString("x") + " size " + n);
                    }
                }
        }

        [Test]
        public void Zlib()
        {
            byte[] data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("row row row ", 500)));
            byte[] z = M2dCodec.ZlibCompress(data);
            Assert.That(z[0], Is.EqualTo(0x78));
            Assert.That(M2dCodec.ZlibDecompress(z, data.Length), Is.EqualTo(data));
            using (var ms = new MemoryStream(z))
            using (var zs = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionMode.Decompress))
            using (var o = new MemoryStream())
            {
                zs.CopyTo(o);
                Assert.That(o.ToArray(), Is.EqualTo(data), "the framework's zlib reads our stream (header and adler32 are right)");
            }
        }
    }

    [TestFixture]
    public class XmlTextTests
    {
        static byte[] U8(string s) { return new UTF8Encoding(false).GetBytes(s); }

        [Test]
        public void NoEditIsIdentity()
        {
            Encoding kr = Encoding.GetEncoding(949);
            var cases = new List<byte[]>
            {
                Fake.Crlf(Fake.PetXml),
                U8(Fake.PetXml),
                new byte[] { 0xEF, 0xBB, 0xBF }.Concat(U8("<a>\r\n  <b x='1'/>\r\n</a>")).ToArray(),
                new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("<?xml version=\"1.0\" encoding=\"utf-16\"?><a n=\"가나\"/>")).ToArray(),
                kr.GetBytes("<?xml version=\"1.0\" encoding=\"euc-kr\"?>\r\n<a n=\"가나다\"><!-- 주석 --><b/></a>"),
                kr.GetBytes("<a n=\"선언 없는 cp949\"/>"),
                U8("<a><![CDATA[ <not a tag> ]]><b k = \"v\"\tz='q'></b><?pi x?></a>\n\n"),
            };
            foreach (byte[] c in cases)
            {
                XmlDoc d = XmlDoc.Parse(c, "case");
                Assert.That(new XmlEditor(d).ApplyBytes(), Is.EqualTo(c));
                Assert.That(d.Encode(d.Text), Is.EqualTo(c));
            }
            Assert.That(XmlDoc.Parse(cases[5], "x").EncodingFallback, Is.True, "undeclared cp949 is read through the fallback");
            Assert.That(XmlDoc.Parse(cases[5], "x").Root.Attr("n"), Is.EqualTo("선언 없는 cp949"));
            Assert.That(XmlDoc.Parse(cases[4], "x").Root.Attr("n"), Is.EqualTo("가나다"));
            Assert.That(XmlDoc.Parse(cases[3], "x").Root.Attr("n"), Is.EqualTo("가나"));
        }

        [Test]
        public void AttributeEdits()
        {
            string src = "<t>\r\n\t<r id=\"1\" a='x' b = \"y\"  c=\"3\"/>\r\n\t<r id=\"2\"/>\r\n\t<r id=\"3\">text</r>\r\n</t>";
            XmlDoc d = XmlDoc.Parse(U8(src), "t");
            var ed = new XmlEditor(d);
            XmlElem r1 = d.Root.Children[0], r2 = d.Root.Children[1], r3 = d.Root.Children[2];
            ed.SetAttr(r1, "a", "it's <1> & \"2\"");
            ed.SetAttr(r1, "c", "4");
            ed.RemoveAttr(r1, "b");
            ed.SetAttr(r2, "new", "v");
            ed.SetAttr(r3, "k", "가");
            ed.SetText(r3, "a < b");
            string got = ed.Apply();
            Assert.That(got, Is.EqualTo("<t>\r\n\t<r id=\"1\" a='it&apos;s &lt;1> &amp; \"2\"'  c=\"4\"/>\r\n\t<r id=\"2\" new=\"v\"/>\r\n\t<r id=\"3\" k=\"가\">a &lt; b</r>\r\n</t>"));
            XmlDoc back = XmlDoc.ParseFragment(got, "back");
            Assert.That(back.Root.Children[0].Attr("a"), Is.EqualTo("it's <1> & \"2\""));
            Assert.That(back.Root.Children[2].InnerText, Is.EqualTo("a < b"));
        }

        [Test]
        public void RowEdits()
        {
            string src = "<t>\r\n\t<r id=\"1\"/>\r\n\t<r id=\"2\">\r\n\t\t<s/>\r\n\t</r>\r\n\t<g/>\r\n\t<h></h>\r\n</t>\r\n";
            XmlDoc d = XmlDoc.Parse(U8(src), "t");
            var ed = new XmlEditor(d);
            ed.Delete(d.Root.Children[0]);
            ed.InsertAfter(d.Root.Children[1], "<r id=\"3\">\n  <s n=\"1\"/>\n</r>", "");
            ed.InsertBefore(d.Root.Children[1], "<r id=\"0\"/>", "");
            ed.Append(d.Root.Children[2], "<k/>", "");
            ed.Append(d.Root.Children[3], "<k/>", "");
            ed.Append(d.Root, "<z/>", "");
            Assert.That(ed.Apply(), Is.EqualTo(
                "<t>\r\n\t<r id=\"0\"/>\r\n\t<r id=\"2\">\r\n\t\t<s/>\r\n\t</r>\r\n\t<r id=\"3\">\r\n\t\t<s n=\"1\"/>\r\n\t</r>\r\n" +
                "\t<g>\r\n\t\t<k/>\r\n\t</g>\r\n\t<h>\r\n\t\t<k/>\r\n\t</h>\r\n\t<z/>\r\n</t>\r\n"));
        }

        [Test]
        public void Malformed()
        {
            Assert.Throws<ModKitException>(() => XmlDoc.Parse(U8("<a><b></a>"), "x"));
            Assert.Throws<ModKitException>(() => XmlDoc.Parse(U8("<a x=1/>"), "x"));
            Assert.Throws<ModKitException>(() => XmlDoc.Parse(U8("<a"), "x"));
        }

        // a table long enough that its rows are kept by id, and the lines by position: the same answers as in a short one
        [Test]
        public void Selectors_AndLines_InALongTable()
        {
            var sb = new System.Text.StringBuilder("<?xml version=\"1.0\"?>\n<ms2>\n");
            for (int i = 1; i <= 200; i++) sb.Append("\t<pet id=\"").Append(i).Append("\" hp=\"").Append(i * 2).Append("\" />\n");
            sb.Append("\t<pet id=\"50\" feature=\"Alpha\" hp=\"7\" />\n\t<pet hp=\"8\" />\n\t<dog id=\"60\" hp=\"9\" />\n</ms2>\n");
            XmlElem root = XmlDoc.Parse(U8(sb.ToString()), "pets").Root;
            Assert.That(RowPath.Parse("pet[id=120]").Resolve(root).Attr("hp"), Is.EqualTo("240"));
            Assert.That(RowPath.Parse("pet[id=120]").Resolve(root).Line, Is.EqualTo(122));
            Assert.That(RowPath.Parse("pet[id=1]").Resolve(root).Line, Is.EqualTo(3));
            Assert.That(root.Line, Is.EqualTo(2));
            Assert.That(RowPath.Parse("pet[hp=120][id=60]").Resolve(root).Line, Is.EqualTo(62));
            Assert.That(RowPath.Parse("*[id=60][hp=9]").Resolve(root).Name, Is.EqualTo("dog"));
            Assert.That(RowPath.Parse("pet[id=50][feature=Alpha]").Resolve(root).Attr("hp"), Is.EqualTo("7"));
            Assert.That(RowPath.Parse("pet[id=50][feature=]").Resolve(root).Attr("hp"), Is.EqualTo("100"));
            Assert.That(RowPath.Parse("pet[id=]").Resolve(root).Attr("hp"), Is.EqualTo("8"), "[id=] is the row without the attribute");
            Assert.That(RowPath.Parse("pet[id=50]").ResolveAll(root).ConvertAll(e => e.Line), Is.EqualTo(new[] { 52, 203 }));
            Assert.That(RowPath.Parse("pet[id=50][#2]").Resolve(root).Attr("hp"), Is.EqualTo("7"));
            var x = Assert.Throws<ModKitException>(() => RowPath.Parse("pet[id=50]").Resolve(root));
            Assert.That(x.Message, Does.Contain("2 elements match").And.Contain("feature"));
            x = Assert.Throws<ModKitException>(() => RowPath.Parse("pet[id=999]").Resolve(root));
            Assert.That(x.Message, Does.Contain("no <pet> has id=\"999\""));
        }

        [Test]
        public void Selectors()
        {
            XmlDoc d = XmlDoc.Parse(Fake.Crlf(Fake.PetXml), "pets");
            XmlElem root = d.Root;
            Assert.That(RowPath.Parse("pet[id=100]").Resolve(root).Attr("name"), Is.EqualTo("Mossy"));
            Assert.That(RowPath.Parse("pet[id=101]/skill[id=8]").Resolve(root).Attr("level"), Is.EqualTo("2"));
            Assert.That(RowPath.Parse("pet[id=102][feature=]").Resolve(root).Attr("hp"), Is.EqualTo("55"), "[feature=] is the row without the attribute");
            Assert.That(RowPath.Parse("pet[id=102][feature='Alpha']").Resolve(root).Attr("hp"), Is.EqualTo("60"));
            Assert.That(RowPath.Parse("pet[#2]").Resolve(root).Attr("id"), Is.EqualTo("101"));
            Assert.That(RowPath.Parse("*[name=Pebble]/*[#1]").Resolve(root).Attr("id"), Is.EqualTo("7"));
            Assert.That(RowPath.Parse("").Resolve(root), Is.SameAs(root));
            Assert.That(RowPath.Parse("pet").ResolveAll(root).Count, Is.EqualTo(4));

            var x = Assert.Throws<ModKitException>(() => RowPath.Parse("pet[id=102]").Resolve(root));
            Assert.That(x.Message, Does.Contain("2 elements match").And.Contain("feature"));
            x = Assert.Throws<ModKitException>(() => RowPath.Parse("pet[id=999]").Resolve(root));
            Assert.That(x.Message, Does.Contain("no <pet> has id=\"999\""));
            x = Assert.Throws<ModKitException>(() => RowPath.Parse("dog").Resolve(root));
            Assert.That(x.Message, Does.Contain("has no <dog>"));
            Assert.Throws<ModKitException>(() => RowPath.Parse("pet[id"));
            Assert.Throws<ModKitException>(() => RowPath.Parse("pet/"));

            foreach (XmlElem e in root.Children)
                Assert.That(RowPath.Parse(RowPath.Canonical(e)).Resolve(root), Is.SameAs(e), RowPath.Canonical(e));
            Assert.That(RowPath.Canonical(root.Children[1].Children[1]), Is.EqualTo("pet[id=101]/skill[id=8]"));
            Assert.That(RowPath.Canonical(root.Children[2]), Is.EqualTo("pet[id=102][feature=]"));
        }
    }

    [TestFixture]
    public class ManifestTests
    {
        const string Full = @"<mod id=""demo.pets"" name=""Demo"" version=""0.1"">
  <description>Made-up rows.</description>
  <ids space=""pet"" from=""900"" to=""909"">
    <where archive=""client:Xml.m2d"" entry=""table/pet.xml"" rows=""pet""/>
    <where archive=""server:Xml.m2d"" entry=""pet/{id}.xml""/>
  </ids>
  <entry archive=""server:Xml.m2d"" name=""pet/900.xml"" copy=""pet/100.xml""/>
  <entry archive=""client:Xml.m2d"" name=""table/readme.txt"" delete=""true""/>
  <edit archive=""client:Xml.m2d"" entry=""table/pet.xml"">
    <change at=""pet[id=100]"">
      <set name=""hp"" value=""65""/>
      <unset name=""speed""/>
    </change>
    <add copy=""pet[id=101]"" after=""pet[id=101]"">
      <set name=""id"" value=""900""/>
      <set at=""skill[id=7]"" name=""level"" value=""5""/>
    </add>
    <add in="""">
      <xml><pet id=""901"" name=""A &amp; B""/></xml>
    </add>
    <delete at=""pet[id=102][feature=Alpha]""/>
  </edit>
</mod>";

        [Test]
        public void RoundTrip()
        {
            Mod m = Mod.Parse(Full, "mod.xml");
            Assert.That(m.Id, Is.EqualTo("demo.pets"));
            Assert.That(m.Description, Is.EqualTo("Made-up rows."));
            Assert.That(m.Ids[0].Places.Count, Is.EqualTo(2));
            Assert.That(m.Ops.Count, Is.EqualTo(3));
            var edit = (EditOp)m.Ops[2];
            Assert.That(edit.Rows.Select(r => r.Kind).ToArray(), Is.EqualTo(new[] { RowOpKind.Change, RowOpKind.Add, RowOpKind.Add, RowOpKind.Delete }));
            Assert.That(edit.Rows[2].Xml, Is.EqualTo("<pet id=\"901\" name=\"A &amp; B\"/>"));
            string again = m.ToXml();
            Mod m2 = Mod.Parse(again, "again");
            Assert.That(m2.ToXml(), Is.EqualTo(again));
            Assert.That(((EditOp)m2.Ops[2]).Rows[1].Attrs[1].At, Is.EqualTo("skill[id=7]"));
        }

        [TestCase("<mod id=\"a\"><edit archive=\"client:Xml.m2d\" entry=\"t.xml\"><change at=\"x\"><set nam=\"a\" value=\"1\"/></change></edit></mod>", "no attribute \"nam\"")]
        [TestCase("<mod id=\"a\">\n<frob/></mod>", "line 2: unknown element <frob>")]
        [TestCase("<mod id=\"a b\"/>", "mod id")]
        [TestCase("<mod id=\"a\"><entry archive=\"Xml.m2d\" name=\"x\" delete=\"true\"/></mod>", "<root>:<path>")]
        [TestCase("<mod id=\"a\"><entry archive=\"client:Xml.m2d\" name=\"x\"/></mod>", "exactly one of")]
        [TestCase("<mod id=\"a\"><edit archive=\"client:Xml.m2d\" entry=\"t.xml\"><delete at=\"x[\"/></edit></mod>", "bad selector")]
        [TestCase("<mod id=\"a\" modkit=\"2\"/>", "format 2")]
        public void Errors(string text, string expect)
        {
            var x = Assert.Throws<ModKitException>(() => Mod.Parse(text, "mod.xml"));
            Assert.That(x.Message, Does.Contain(expect));
        }
    }

    [TestFixture]
    public class PlannerTests
    {
        const string C = "client:Xml.m2d", S = "server:Xml.m2d", T = "table/pet.xml";

        static string Edit(string archive, string body) { return "<edit archive=\"" + archive + "\" entry=\"" + T + "\">" + body + "</edit>"; }

        [Test]
        public void EveryOp()
        {
            using (var f = new Fake())
            {
                Mod m = f.Mod("ops", Edit(C,
                    "<change at=\"pet[id=100]\"><set name=\"hp\" value=\"65\"/><unset name=\"speed\"/><set name=\"tag\" value=\"new\"/></change>" +
                    "<change at=\"pet[id=101]\"><set at=\"skill[id=8]\" name=\"level\" value=\"9\"/></change>" +
                    "<add copy=\"pet[id=101]\"><set name=\"id\" value=\"900\"/><set name=\"name\" value=\"Copy\"/><set at=\"skill[id=7]\" name=\"level\" value=\"5\"/></add>" +
                    "<add before=\"pet[id=100]\"><xml><pet id=\"901\" name=\"First\"/></xml></add>" +
                    "<add in=\"pet[id=101]\"><xml><skill id=\"9\" level=\"1\"/></xml></add>" +
                    "<delete at=\"pet[id=102][feature=Alpha]\"/>") +
                    "<entry archive=\"" + S + "\" name=\"pet/900.xml\" copy=\"pet/100.xml\"/>" +
                    "<edit archive=\"" + S + "\" entry=\"pet/900.xml\"><change at=\"environment/basic\"><set name=\"name\" value=\"Copy\"/></change></edit>" +
                    "<entry archive=\"" + S + "\" name=\"pet/555.xml\" delete=\"true\"/>");
                Plan p = f.Plan(m);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(p.Conflicts, Is.Empty, Fake.Dump(p));
                string want = (
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ms2>\n" +
                    "\t<pet id=\"901\" name=\"First\"/>\n" +
                    "\t<pet id=\"100\" name=\"Mossy\" hp=\"65\" tag=\"new\" />\n" +
                    "\t<pet id=\"101\" name=\"Pebble\" hp=\"70\" speed=\"2\">\n" +
                    "\t\t<skill id=\"7\" level=\"1\" />\n" +
                    "\t\t<skill id=\"8\" level=\"9\" />\n" +
                    "\t\t<skill id=\"9\" level=\"1\"/>\n" +
                    "\t</pet>\n" +
                    "\t<pet id=\"102\" name=\"Fern\" hp=\"55\" speed=\"1\" />\n" +
                    "\t<pet id=\"900\" name=\"Copy\" hp=\"70\" speed=\"2\">\n" +
                    "\t\t<skill id=\"7\" level=\"5\" />\n" +
                    "\t\t<skill id=\"8\" level=\"2\" />\n" +
                    "\t</pet>\n" +
                    "</ms2>\n").Replace("\n", "\r\n");
                Assert.That(Fake.Text(p, C, T), Is.EqualTo(want));
                Assert.That(Fake.Text(p, S, "pet/900.xml"), Is.EqualTo(Fake.PetFile(900, "Copy").Replace("\n", "\r\n")));
                Assert.That(p.Find(S).Delete, Is.EqualTo(new[] { "pet/555.xml" }));
                Assert.That(p.Find(C).Put.Count, Is.EqualTo(1), "only the edited entry is put");
                string dump = Fake.Dump(p);
                Assert.That(dump, Does.Contain(C + " " + T + " pet[id=100] @hp: \"50\" -> \"65\""));
                Assert.That(dump, Does.Contain("pet[id=100] @speed: \"1.5\" -> (removed)"));
                Assert.That(dump, Does.Contain("pet[id=100] @tag: (none) -> \"new\""));
                Assert.That(dump, Does.Contain("pet[id=102][feature=Alpha] - <pet id=\"102\" feature=\"Alpha\""));
                Assert.That(dump, Does.Contain(S + " pet/900.xml + entry (copy of pet/100.xml"));
                Assert.That(dump, Does.Contain(S + " pet/555.xml - entry"));
            }
        }

        [Test]
        public void NothingToDoPutsNothing()
        {
            using (var f = new Fake())
            {
                Plan p = f.Plan(f.Mod("same", Edit(C, "<change at=\"pet[id=100]\"><set name=\"hp\" value=\"50\"/><unset name=\"nothere\"/></change>")));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(p.Archives, Is.Empty);
                Assert.That(p.Changes, Is.Empty);
                Assert.That(p.Notes.Count, Is.EqualTo(2), Fake.Dump(p));
            }
        }

        [Test]
        public void Conflicts()
        {
            using (var f = new Fake())
            {
                Mod a = f.Mod("a", Edit(C,
                    "<change at=\"pet[id=100]\"><set name=\"hp\" value=\"65\"/><set name=\"name\" value=\"Same\"/></change>" +
                    "<change at=\"pet[id=101]/skill[id=7]\"><set name=\"level\" value=\"3\"/></change>" +
                    "<add><xml><pet id=\"900\" name=\"A\"/></xml></add>"));
                Mod b = f.Mod("b", Edit(C,
                    "<change at=\"pet[id=100]\"><set name=\"hp\" value=\"80\"/><set name=\"name\" value=\"Same\"/><set name=\"speed\" value=\"3\"/></change>" +
                    "<delete at=\"pet[id=101]\"/>" +
                    "<add><xml><pet id=\"900\" name=\"B\"/></xml></add>"));
                Plan p = f.Plan(a, b);
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                Assert.That(p.Conflicts.Count, Is.EqualTo(3), dump);
                Assert.That(dump, Does.Contain("C [a] " + C + " " + T + ": pet[id=100] @hp: \"65\" here, \"80\" in mod b; mod b wins"));
                Assert.That(dump, Does.Contain("changes the row pet[id=101]/skill[id=7] that mod b deletes; mod b wins (it has the higher priority)"));
                Assert.That(dump, Does.Contain("adds the row pet[id=900] that mod b also adds; mod b wins"));
                Assert.That(p.Conflicts.All(c => c.ModId == "a" && c.WinnerId == "b"), dump);
                Assert.That(dump, Does.Contain("N [a] " + C + " " + T + ": pet[id=100] @name: same value as mod b"));
                // the later mod has the higher priority: its value, its delete and its new row are in the text
                string text = Fake.Text(p, C, T);
                Assert.That(text, Does.Contain("<pet id=\"100\" name=\"Same\" hp=\"80\" speed=\"3\" />"));
                Assert.That(text, Does.Not.Contain("id=\"101\""));
                Assert.That(text, Does.Contain("name=\"B\"").And.Not.Contain("name=\"A\""));
                Assert.That(p.Changes.Count(c => c.Kind == ChangeKind.SetAttr && c.Name == "hp"), Is.EqualTo(1));
                Assert.That(p.Changes.Count(c => c.Kind == ChangeKind.AddRow), Is.EqualTo(1));
                XmlDoc.Parse(Encoding.UTF8.GetBytes(text), "result");

                // the other order: mod a has the higher priority, so its value, its change and its new row stay
                p = f.Plan(b, a);
                dump = Fake.Dump(p);
                Assert.That(p.Conflicts.Count, Is.EqualTo(3), dump);
                Assert.That(dump, Does.Contain("deletes the row pet[id=101] that mod a changes; mod a wins (it has the higher priority)"));
                Assert.That(p.Conflicts.All(c => c.ModId == "b" && c.WinnerId == "a"), dump);
                text = Fake.Text(p, C, T);
                Assert.That(text, Does.Contain("<pet id=\"100\" name=\"Same\" hp=\"65\" speed=\"3\" />"));
                Assert.That(text, Does.Contain("id=\"101\"").And.Contain("level=\"3\""));
                Assert.That(text, Does.Contain("name=\"A\"").And.Not.Contain("name=\"B\""));
                Assert.That(p.Changes.Count(c => c.Kind == ChangeKind.DeleteRow), Is.EqualTo(0));
                XmlDoc.Parse(Encoding.UTF8.GetBytes(text), "result");
            }
        }

        [Test]
        public void EntryConflicts()
        {
            using (var f = new Fake())
            {
                File.WriteAllText(Path.Combine(f.ModDir("a", "<entry archive=\"" + S + "\" name=\"pet/100.xml\" file=\"data/p.xml\"/>"), "p.xml"), "x");
                Directory.CreateDirectory(Path.Combine(f.Dir, "mods", "a", "data"));
                File.WriteAllText(Path.Combine(f.Dir, "mods", "a", "data", "p.xml"), "<ms2><environment><basic name=\"FromFile\"/></environment></ms2>");
                Mod a = Mod.Load(Path.Combine(f.Dir, "mods", "a"));
                Mod b = f.Mod("b", "<entry archive=\"" + S + "\" name=\"pet/100.xml\" copy=\"pet/101.xml\"/>");
                Mod c = f.Mod("c", "<edit archive=\"" + S + "\" entry=\"pet/100.xml\"><change at=\"environment/basic\"><set name=\"kind\" value=\"4\"/></change></edit>");
                Mod d = f.Mod("d", "<entry archive=\"" + S + "\" name=\"pet/100.xml\" delete=\"true\"/>");
                Plan p = f.Plan(a, b);
                Assert.That(Fake.Dump(p), Does.Contain("C [b] " + S + " pet/100.xml: replaces the entry that mod a also replaces; mod b wins (it has the higher priority)"));
                Assert.That(Fake.Text(p, S, "pet/100.xml"), Does.Not.Contain("FromFile"));
                Assert.That(p.Changes.Count(x => x.Entry == "pet/100.xml"), Is.EqualTo(1), string.Join(" | ", p.Changes.Select(x => x.Kind + " " + x.ModId + " " + x.Archive + " " + x.Entry + " row=" + x.Row).ToArray()));
                p = f.Plan(b, a);
                Assert.That(Fake.Dump(p), Does.Contain("C [a] " + S + " pet/100.xml: replaces the entry that mod b also replaces; mod a wins"));
                Assert.That(Fake.Text(p, S, "pet/100.xml"), Does.Contain("FromFile"));
                p = f.Plan(a, c);
                // the edit has the higher priority: it is made in the shipped entry
                Assert.That(Fake.Dump(p), Does.Contain("C [c] " + S + " pet/100.xml: edits an entry that mod a replaces; mod c wins"));
                Assert.That(Fake.Text(p, S, "pet/100.xml"), Does.Contain("kind=\"4\"").And.Not.Contain("FromFile"));
                p = f.Plan(c, a);
                Assert.That(Fake.Dump(p), Does.Contain("C [c] " + S + " pet/100.xml: edits an entry that mod a replaces; mod a wins"));
                Assert.That(Fake.Text(p, S, "pet/100.xml"), Is.EqualTo("<ms2><environment><basic name=\"FromFile\"/></environment></ms2>"));
                p = f.Plan(c, d);
                Assert.That(Fake.Dump(p), Does.Contain("C [c] " + S + " pet/100.xml: edits an entry that mod d deletes"));
                Assert.That(p.Find(S).Delete, Is.EqualTo(new[] { "pet/100.xml" }));
                Assert.That(p.Find(S).Put, Is.Empty);
                Assert.That(p.Conflicts[0].WinnerId, Is.EqualTo("d"));
                // the edit has the higher priority: the entry stays and is edited
                p = f.Plan(d, c);
                Assert.That(Fake.Dump(p), Does.Contain("C [c] " + S + " pet/100.xml: edits an entry that mod d deletes; mod c wins"));
                Assert.That(p.Find(S).Delete, Is.Empty);
                Assert.That(Fake.Text(p, S, "pet/100.xml"), Does.Contain("kind=\"4\""));
                // and a delete with the higher priority takes an entry another mod replaces
                p = f.Plan(a, d);
                Assert.That(Fake.Dump(p), Does.Contain("; mod d wins"));
                Assert.That(p.Find(S).Delete, Is.EqualTo(new[] { "pet/100.xml" }));
                p = f.Plan(d, a);
                Assert.That(Fake.Dump(p), Does.Contain("; mod a wins"));
                Assert.That(Fake.Text(p, S, "pet/100.xml"), Does.Contain("FromFile"));
                Mod outside = f.Mod("o", "<entry archive=\"" + S + "\" name=\"pet/1.xml\" file=\"../a/data/p.xml\"/>");
                Assert.That(Fake.Dump(f.Plan(outside)), Does.Contain("outside of the mod folder"));
            }
        }

        [Test]
        public void Errors()
        {
            using (var f = new Fake())
            {
                Plan p = f.Plan(f.Mod("e",
                    Edit(C, "<change at=\"pet[id=102]\"><set name=\"hp\" value=\"1\"/></change><change at=\"pet[id=100]\"><set name=\"hp\" value=\"2\"/></change><delete at=\"pet[id=77]\"/>") +
                    "<edit archive=\"" + C + "\" entry=\"table/nope.xml\"><delete at=\"x\"/></edit>" +
                    "<edit archive=\"" + C + "\" entry=\"table/readme.txt\"><delete at=\"x\"/></edit>" +
                    "<edit archive=\"" + C + "\" entry=\"Pet.xml\"><delete at=\"x\"/></edit>" +
                    "<edit archive=\"other:Xml.m2d\" entry=\"t.xml\"><delete at=\"x\"/></edit>" +
                    "<entry archive=\"" + C + "\" name=\"a.xml\" copy=\"nope.xml\"/>"));
                string dump = Fake.Dump(p);
                Assert.That(p.Errors.Count, Is.EqualTo(7), dump);
                Assert.That(dump, Does.Contain("2 elements match \"pet[id=102]\"; they differ in hp, feature; add a test or [#n] (mod.xml line"));
                Assert.That(dump, Does.Contain("no <pet> has id=\"77\""));
                Assert.That(dump, Does.Contain("table/nope.xml: the archive has no such entry"));
                Assert.That(dump, Does.Contain("table/readme.txt: cannot be edited as XML"));
                Assert.That(dump, Does.Contain("did you mean table/pet.xml?"));
                Assert.That(dump, Does.Contain("the data root \"other\" is not set"));
                Assert.That(dump, Does.Contain("copy=\"nope.xml\": the archive has no such entry"));
                // the good op of the same edit is still planned, so one run shows every problem
                Assert.That(dump, Does.Contain("pet[id=100] @hp: \"50\" -> \"2\""));
            }
        }

        const string Ids =
            "<ids space=\"pet\" from=\"900\" to=\"909\"{0}>" +
            "<where archive=\"" + C + "\" entry=\"table/pet.xml\" rows=\"pet\"/>" +
            "<where archive=\"" + C + "\" entry=\"pet/{{id}}.xml\"/>" +
            "<where archive=\"" + S + "\" entry=\"pet/{{id}}.xml\"/></ids>";

        static string NewPet(int id)
        {
            return Edit(C, "<add><xml><pet id=\"" + id + "\" name=\"N\"/></xml></add>") +
                "<entry archive=\"" + C + "\" name=\"pet/" + id + ".xml\" copy=\"pet/100.xml\"/>" +
                "<entry archive=\"" + S + "\" name=\"pet/" + id + ".xml\" copy=\"pet/100.xml\"/>";
        }

        [Test]
        public void IdRanges()
        {
            using (var f = new Fake())
            {
                Plan p = f.Plan(f.Mod("ok", string.Format(Ids, "") + NewPet(900)));
                Assert.That(Fake.Dump(p), Does.Not.Contain("E ").And.Not.Contain("W ").And.Not.Contain("C "));

                // 555 is free on the client and used on the server
                p = f.Plan(f.Mod("used", "<ids space=\"pet\" from=\"500\" to=\"600\"><where archive=\"" + C + "\" entry=\"table/pet.xml\" rows=\"pet\"/><where archive=\"" + S + "\" entry=\"pet/{id}.xml\"/></ids>" +
                    Edit(C, "<add><xml><pet id=\"555\" name=\"N\"/></xml></add>")));
                string dump = Fake.Dump(p);
                Assert.That(dump, Does.Contain("E [used] " + C + " " + T + ": new id 555 of space \"pet\" is already used in the original data (" + S + " pet/{id}.xml)"));
                Assert.That(dump, Does.Contain("W [used] id 555 of space \"pet\" is added in 1 of 2 places; nothing is added in " + S + " pet/{id}.xml"));

                p = f.Plan(f.Mod("lax", "<ids space=\"pet\" from=\"100\" to=\"200\" strict=\"false\"><where archive=\"" + C + "\" entry=\"table/pet.xml\" rows=\"pet\"/></ids>" +
                    Edit(C, "<add><xml><pet id=\"102\" feature=\"Beta\" name=\"N\"/></xml></add>")));
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Fake.Dump(p), Does.Contain("W [lax] " + C + " " + T + ": new id 102 of space \"pet\" is already used"));

                p = f.Plan(f.Mod("out", string.Format(Ids, "") + NewPet(950)));
                Assert.That(Fake.Dump(p), Does.Contain("new id 950 is outside of the range 900-909"));

                Mod a = f.Mod("a", string.Format(Ids, "") + NewPet(901)), b = f.Mod("b", string.Format(Ids, "").Replace("900", "905").Replace("909", "915") + NewPet(901));
                dump = Fake.Dump(f.Plan(a, b));
                Assert.That(dump, Does.Contain("C [b] ids space \"pet\": the range 905-915 overlaps 900-909 of mod a"));
                Assert.That(dump, Does.Contain("adds the row pet[id=901] that mod b also adds; mod b wins"));

                Assert.That(IdSpaces.NextFree(f.Data, f.Mod("n", string.Format(Ids, "") + NewPet(900) + NewPet(902).Replace("<add>", "<add in=\"\">")), "pet", 3, null), Is.EqualTo(new long[] { 901, 903, 904 }));
                Assert.That(IdSpaces.UsedIds(f.Data, new IdPlace { Archive = C, Entry = "table/*.xml", Rows = "pet" }, null).OrderBy(x => x).ToArray(), Is.EqualTo(new long[] { 100, 101, 102 }));
                Assert.That(IdSpaces.UsedIds(f.Data, new IdPlace { Archive = S, Entry = "pet/{id}.xml" }, null).OrderBy(x => x).ToArray(), Is.EqualTo(new long[] { 100, 101, 555 }));
            }
        }

        [Test]
        public void Schema()
        {
            using (var f = new Fake())
            {
                Assert.That(SchemaSet.TableOf("Pet/100.xml"), Is.EqualTo("pet/#.xml"));
                Assert.That(SchemaSet.TableOf("skill/10/200_3.xml"), Is.EqualTo("skill/#/#_#.xml"));
                Assert.That(SchemaSet.TableOf("table/map2d.xml"), Is.EqualTo("table/map2d.xml"));
                Assert.That(SchemaSet.Classify("12"), Is.EqualTo(ValueTypes.Int));
                Assert.That(SchemaSet.Classify("-1.5"), Is.EqualTo(ValueTypes.Float));
                Assert.That(SchemaSet.Classify("1,2,3"), Is.EqualTo(ValueTypes.List));
                Assert.That(SchemaSet.Classify("a,b"), Is.EqualTo(ValueTypes.Text));
                Assert.That(SchemaSet.Classify(""), Is.EqualTo(ValueTypes.Empty));

                var set = new SchemaSet();
                ArchiveSchema a = set.Build(f.Data, C, null);
                Assert.That(a.Entries, Is.EqualTo(3));
                Assert.That(a.Skipped, Is.EqualTo(1), "the empty .xml");
                TableSchema t = a.Table("table/pet.xml");
                Assert.That(new[] { t.Root, t.Row, t.Key, t.Variants }, Is.EqualTo(new[] { "ms2", "pet", "id", "feature" }));
                Assert.That(t.Element("ms2/pet").Count, Is.EqualTo(4));
                Assert.That(t.Element("ms2/pet").Attr("speed").TypeText, Is.EqualTo("int, float 1..2"));
                Assert.That(t.Element("ms2/pet/skill").Attr("level").Samples, Is.EqualTo(new[] { "1", "2" }));
                Assert.That(a.Table("pet/#.xml").Files, Is.EqualTo(2));

                string dir = Path.Combine(f.Dir, "schema");
                set.Save(dir);
                SchemaSet loaded = SchemaSet.Load(dir, f.Data);
                Assert.That(SchemaSet.ToXml(loaded.Archive(C)), Is.EqualTo(SchemaSet.ToXml(a)));
                Assert.That(loaded.Archive(C).Stale, Is.False);

                File.WriteAllText(Path.Combine(f.Dir, "notes.xml"),
                    "<notes><attr archive=\"client:Xml.m2d\" table=\"table/pet.xml\" element=\"ms2/pet\" name=\"glow\" source=\"maple2\" ref=\"PetTable\"/>" +
                    "<attr table=\"table/pet.xml\" element=\"ms2/pet\" name=\"tint\" source=\"client\" ref=\"src/ms2/client/somewhere.cpp\"/></notes>");
                loaded.LoadNotes(Path.Combine(f.Dir, "notes.xml"));
                Mod m = f.Mod("s", "<edit archive=\"" + C + "\" entry=\"" + T + "\">" +
                    "<change at=\"pet[id=100]\"><set name=\"hp\" value=\"lots\"/><set name=\"speed\" value=\"3\"/><set name=\"colour\" value=\"red\"/><set name=\"glow\" value=\"1\"/><set name=\"tint\" value=\"1\"/><set name=\"name\" value=\"\"/></change>" +
                    "<add in=\"pet[id=100]\"><xml><toy kind=\"1\"/></xml></add></edit>" +
                    "<edit archive=\"" + S + "\" entry=\"" + T + "\"><change at=\"pet[id=100]\"><set name=\"hp\" value=\"lots\"/></change></edit>");
                Plan p = Planner.Build(f.Data, new[] { m }, new PlanOptions { Schemas = loaded });
                string dump = Fake.Dump(p);
                Assert.That(p.Errors, Is.Empty, dump);
                // 5 from the schema, and 5 for the attributes set in the client's copy of a table the server holds too
                Assert.That(p.Warnings.Count, Is.EqualTo(10), dump);
                Assert.That(dump, Does.Contain("@speed is changed in 1 row(s) (first: pet[id=100]) on this side only; server:Xml.m2d holds the same table and there is no field map for it"));
                Assert.That(dump, Does.Not.Contain("@hp is changed in"), "hp is set on both sides");
                // sync="false" on the set (or its change, or the edit) says one side is meant: no warning
                Mod one = f.Mod("o", "<edit archive=\"" + C + "\" entry=\"" + T + "\"><change at=\"pet[id=100]\"><set name=\"speed\" value=\"3\" sync=\"false\"/></change>" +
                    "<change at=\"pet[id=101]\" sync=\"false\"><set name=\"speed\" value=\"3\"/></change></edit>");
                Assert.That(Fake.Dump(Planner.Build(f.Data, new[] { one }, new PlanOptions())), Does.Not.Contain("on this side only"));
                Assert.That(dump, Does.Contain("ms2/pet @hp=\"lots\" is text; the shipped table/pet.xml only has int 50..70, for example \"50\""));
                Assert.That(dump, Does.Contain("attribute colour is never set on ms2/pet in the shipped table/pet.xml"));
                Assert.That(dump, Does.Contain("attribute glow of ms2/pet is not in the shipped table/pet.xml; known from Maple2 only, unverified (PetTable)"));
                Assert.That(dump, Does.Contain("N [s] " + C + " " + T + ": attribute tint of ms2/pet is not in the shipped table/pet.xml; the client loader reads it"));
                Assert.That(dump, Does.Contain("ms2/pet @name=\"\" is empty"));
                Assert.That(dump, Does.Contain("the shipped table/pet.xml has no element ms2/pet/toy"));
                Assert.That(dump, Does.Contain("N " + S + " no schema is cached for this archive"));
                Assert.That(p.Find(C).Put.Count, Is.EqualTo(1), "warnings do not stop the plan");
            }
        }
    }

    [TestFixture]
    public class WorkspaceTests
    {
        const string C = "client:Xml.m2d", S = "server:Xml.m2d";

        static string Body(string hp)
        {
            return "<edit archive=\"" + C + "\" entry=\"table/pet.xml\"><change at=\"pet[id=100]\"><set name=\"hp\" value=\"" + hp + "\"/></change></edit>";
        }

        static Dictionary<string, byte[]> Hashes(string dir)
        {
            var d = new Dictionary<string, byte[]>();
            foreach (string p in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) d[p] = File.ReadAllBytes(p);
            return d;
        }

        [Test]
        public void ApplyAndRevert()
        {
            using (var f = new Fake())
            {
                Dictionary<string, byte[]> before = Hashes(Path.Combine(f.Dir, "orig"));
                var ws = new Workspace(Path.Combine(f.Dir, "ws"));
                Mod a = f.Mod("a", Body("65"));
                Mod b = f.Mod("b", "<edit archive=\"" + S + "\" entry=\"pet/100.xml\"><change at=\"environment/basic\"><set name=\"kind\" value=\"4\"/></change></edit>" +
                    "<entry archive=\"" + S + "\" name=\"pet/900.xml\" copy=\"pet/100.xml\"/>");
                ApplyResult r = ws.Apply(f.Data, new[] { a, b }, new ApplyOptions { VerifyAll = true });
                Assert.That(r.Applied, Is.True, Fake.Dump(r.Plan));
                Assert.That(r.Outputs.Count, Is.EqualTo(2));
                Assert.That(r.Outputs[0].Path, Is.EqualTo(Path.Combine(f.Dir, "ws", "client", "Xml.m2d")));
                Assert.That(new[] { r.Outputs[0].Kept, r.Outputs[0].Replaced, r.Outputs[0].Verified }, Is.EqualTo(new[] { 4, 1, 5 }));
                Assert.That(new[] { r.Outputs[1].Kept, r.Outputs[1].Replaced, r.Outputs[1].Added }, Is.EqualTo(new[] { 3, 1, 1 }));

                using (M2dArchive orig = M2dArchive.Open(Path.Combine(f.ClientDir, "Xml.m2d")))
                using (M2dArchive made = M2dArchive.Open(ws.PathOf(C)))
                {
                    Assert.That(made.Version, Is.EqualTo(orig.Version));
                    Assert.That(made.Count, Is.EqualTo(orig.Count));
                    foreach (M2dEntry e in orig.Entries)
                    {
                        if (e.Name == "table/pet.xml")
                            Assert.That(Encoding.UTF8.GetString(made.Read(e.Name)), Is.EqualTo(Encoding.UTF8.GetString(orig.Read(e)).Replace("hp=\"50\"", "hp=\"65\"")));
                        else
                            Assert.That(made.ReadRaw(made.Find(e.Name)), Is.EqualTo(orig.ReadRaw(e)), e.Name);
                    }
                }
                WorkspaceState st = ws.ReadState();
                Assert.That(st.Mods.Select(m => m.Id).ToArray(), Is.EqualTo(new[] { "a", "b" }));
                Assert.That(st.Archives.Select(x => x.File).ToArray(), Is.EqualTo(new[] { "client/Xml.m2d", "server/Xml.m2d" }));

                // without mod a: the client copy goes away, the server copy is rebuilt and is the same file
                byte[] serverCopy = File.ReadAllBytes(ws.PathOf(S));
                r = ws.Revert(f.Data, "a", null);
                Assert.That(r.Applied, Is.True);
                Assert.That(File.Exists(ws.PathOf(C)), Is.False);
                Assert.That(r.Removed.Count, Is.EqualTo(2));
                Assert.That(File.ReadAllBytes(ws.PathOf(S)), Is.EqualTo(serverCopy));
                Assert.That(ws.ReadState().Mods.Select(m => m.Id).ToArray(), Is.EqualTo(new[] { "b" }));
                Assert.Throws<ModKitException>(() => ws.Revert(f.Data, "a", null));

                // without anything: only the empty folders are left
                ws.Revert(f.Data, null, null);
                Assert.That(Directory.GetFiles(ws.Dir, "*", SearchOption.AllDirectories), Is.Empty);
                Assert.That(ws.ReadState(), Is.Null);

                Dictionary<string, byte[]> after = Hashes(Path.Combine(f.Dir, "orig"));
                Assert.That(after.Keys, Is.EquivalentTo(before.Keys));
                foreach (string k in before.Keys) Assert.That(after[k], Is.EqualTo(before[k]), "original " + k + " is untouched");
            }
        }

        [Test]
        public void ConflictsAndErrorsWriteNothing()
        {
            using (var f = new Fake())
            {
                var ws = new Workspace(Path.Combine(f.Dir, "ws"));
                Mod a = f.Mod("a", Body("65")), b = f.Mod("b", Body("80"));
                ApplyResult r = ws.Apply(f.Data, new[] { a, b }, null);
                Assert.That(r.Applied, Is.False);
                Assert.That(r.Reason, Is.EqualTo("1 conflict(s)"));
                Assert.That(Directory.Exists(ws.Dir), Is.False);
                r = ws.Apply(f.Data, new[] { a, b }, new ApplyOptions { AllowConflicts = true });
                Assert.That(r.Applied, Is.True);
                using (M2dArchive made = M2dArchive.Open(ws.PathOf(C)))
                    Assert.That(Encoding.UTF8.GetString(made.Read("table/pet.xml")), Does.Contain("hp=\"80\""));
                r = ws.Apply(f.Data, new[] { f.Mod("bad", Body("1").Replace("id=100", "id=1")) }, null);
                Assert.That(r.Applied, Is.False);
                Assert.That(ws.ReadState().Mods.Count, Is.EqualTo(2), "a failed apply leaves the last good state");
            }
        }

        [Test]
        public void AlsoCopy()
        {
            using (var f = new Fake())
            {
                var ws = new Workspace(Path.Combine(f.Dir, "ws"));
                var o = new ApplyOptions();
                o.AlsoCopy.Add(S);
                ApplyResult r = ws.Apply(f.Data, new[] { f.Mod("a", Body("65")) }, o);
                Assert.That(r.Outputs[1].Copied, Is.True);
                Assert.That(File.ReadAllBytes(ws.PathOf(S)), Is.EqualTo(File.ReadAllBytes(Path.Combine(f.ServerDir, "Xml.m2d"))));
            }
        }

        [Test]
        public void OriginalsAreRefused()
        {
            using (var f = new Fake())
            {
                Mod a = f.Mod("a", Body("65"));
                foreach (string bad in new[] { f.ClientDir, Path.Combine(f.ClientDir, "sub"), Path.Combine(f.Dir, "orig"), f.Dir })
                {
                    var x = Assert.Throws<ModKitException>(() => new Workspace(bad).Apply(f.Data, new[] { a }, null), bad);
                    Assert.That(x.Message, Does.Contain("inside one another"));
                }
            }
        }
    }
}
