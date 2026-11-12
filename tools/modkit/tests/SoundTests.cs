using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    /// <summary>Made-up FSB5 banks and FEV projects, built byte by byte (no game audio).</summary>
    public static class FakeSound
    {
        public static readonly byte[] Hash = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };

        public sealed class S
        {
            public string Name;
            public int RateIndex = 8, Frames;
            public bool Stereo, Loop;
            public int Channels;    // 6 or 8: the channel code 2 or 3 (else Stereo picks 1 or 0)
            public int Bytes;
            public int? Rate;   // an extra frequency chunk
        }

        public static byte[] Bank(params S[] samples)
        {
            var sh = new MemoryStream();
            long off = 0;
            foreach (S s in samples)
            {
                bool extra = s.Loop || s.Rate != null;
                // FSB5: bit 0 more chunks, 1-4 the rate, 5-6 the channels (1, 2, 6, 8), 7-33 the offset / 32, 34-63 the frames
                ulong code = s.Channels == 8 ? 3UL : s.Channels == 6 ? 2UL : s.Stereo ? 1UL : 0;
                ulong v = (extra ? 1UL : 0) | ((ulong)s.RateIndex << 1) | (code << 5) | ((ulong)(off / 32) << 7) | ((ulong)s.Frames << 34);
                sh.Write(BitConverter.GetBytes(v), 0, 8);
                if (s.Rate != null)
                {
                    uint c = (s.Loop ? 1u : 0) | (4u << 1) | (2u << 25);
                    sh.Write(BitConverter.GetBytes(c), 0, 4);
                    sh.Write(BitConverter.GetBytes(s.Rate.Value), 0, 4);
                }
                if (s.Loop)
                {
                    uint c = (8u << 1) | (3u << 25);
                    sh.Write(BitConverter.GetBytes(c), 0, 4);
                    sh.Write(new byte[8], 0, 8);
                }
                off += (s.Bytes + 31) / 32 * 32;
            }
            var nt = new MemoryStream();
            var names = new MemoryStream();
            int at = 4 * samples.Length;
            foreach (S s in samples)
            {
                nt.Write(BitConverter.GetBytes(at), 0, 4);
                byte[] n = Encoding.UTF8.GetBytes(s.Name + "\0");
                names.Write(n, 0, n.Length);
                at += n.Length;
            }
            names.WriteTo(nt);
            while (nt.Length % 16 != 0) nt.WriteByte(0);
            var o = new MemoryStream();
            o.Write(Encoding.ASCII.GetBytes("FSB5"), 0, 4);
            foreach (long x in new[] { 1L, samples.Length, sh.Length, nt.Length, off, 11L })
                o.Write(BitConverter.GetBytes((uint)x), 0, 4);
            o.Write(new byte[8], 0, 8);
            o.Write(Hash, 0, 8);
            o.Write(new byte[16], 0, 16);
            Assert.That(o.Length, Is.EqualTo(SoundBank.HeaderSize));
            sh.WriteTo(o);
            nt.WriteTo(o);
            o.Write(new byte[off], 0, (int)off);
            return o.ToArray();
        }

        /// <summary>A RIFF FEV with some chunk before the cue list, the bank hash, and the list itself.</summary>
        public static byte[] Project(params KeyValuePair<int, string>[] cues)
        {
            var names = new MemoryStream();
            foreach (var c in cues) { byte[] n = Encoding.UTF8.GetBytes(c.Value + "\0"); names.Write(n, 0, n.Length); }
            names.WriteByte(0);
            var body = new MemoryStream();
            body.Write(Encoding.ASCII.GetBytes("FEV "), 0, 4);
            body.Write(Encoding.ASCII.GetBytes("LIST"), 0, 4);
            body.Write(BitConverter.GetBytes(8), 0, 4);
            body.Write(Hash, 0, 8);
            body.Write(Encoding.ASCII.GetBytes("comp"), 0, 4);
            body.Write(BitConverter.GetBytes(0x10), 0, 4);
            body.Write(Encoding.ASCII.GetBytes("cues"), 0, 4);
            body.Write(BitConverter.GetBytes((uint)(4 + 8 + 4 * cues.Length + names.Length)), 0, 4);
            body.Write(Encoding.ASCII.GetBytes("entl"), 0, 4);
            body.Write(BitConverter.GetBytes((ushort)cues.Length), 0, 2);
            body.Write(BitConverter.GetBytes((ushort)names.Length), 0, 2);
            foreach (var c in cues) body.Write(BitConverter.GetBytes(c.Key), 0, 4);
            names.WriteTo(body);
            body.Write(Encoding.ASCII.GetBytes("prms"), 0, 4);
            body.Write(BitConverter.GetBytes(0x7fff), 0, 4);
            var o = new MemoryStream();
            o.Write(Encoding.ASCII.GetBytes("RIFF"), 0, 4);
            o.Write(BitConverter.GetBytes((uint)body.Length), 0, 4);
            body.WriteTo(o);
            return o.ToArray();
        }

        public static KeyValuePair<int, string> C(int id, string name) { return new KeyValuePair<int, string>(id, name); }
    }

    [TestFixture]
    public class SoundTests
    {
        [Test]
        public void ReadsABanksSamples()
        {
            byte[] b = FakeSound.Bank(
                new FakeSound.S { Name = "Tune_Meadow", Frames = 44100 * 3, Stereo = true, Loop = true, Bytes = 1000 },
                new FakeSound.S { Name = "Click", Frames = 2205, RateIndex = 5, Bytes = 40 },
                new FakeSound.S { Name = "Odd", Frames = 9000, Rate = 9000, Bytes = 20 });
            SoundBank bank = SoundBank.Read(new MemoryStream(b), "MS2Sound_Fake.fsb");
            Assert.That(bank.Mode, Is.EqualTo(11));
            Assert.That(SoundBank.Codec(bank.Mode), Is.EqualTo("MPEG"));
            Assert.That(bank.HashText, Is.EqualTo("1122334455667788"));
            Assert.That(bank.Samples.Select(x => x.Name), Is.EqualTo(new[] { "Tune_Meadow", "Click", "Odd" }));
            SoundSample t = bank.Samples[0];
            Assert.That(t.Rate, Is.EqualTo(44100));
            Assert.That(t.Channels, Is.EqualTo(2));
            Assert.That(t.Loops, Is.True);
            Assert.That(t.Seconds, Is.EqualTo(3.0).Within(1e-9));
            Assert.That(t.Size, Is.EqualTo(1024), "to the next sample's offset");
            Assert.That(bank.Samples[1].Rate, Is.EqualTo(22050));
            Assert.That(bank.Samples[1].Channels, Is.EqualTo(1));
            Assert.That(bank.Samples[1].Offset, Is.EqualTo(1024));
            Assert.That(bank.Samples[2].Rate, Is.EqualTo(9000), "the frequency chunk wins");
            Assert.That(bank.Samples[2].Size, Is.EqualTo(32), "to the end of the data");
        }

        [Test]
        public void ReadsTheTwoChannelBits_AndTheOffsetIn32ByteUnits()
        {
            // a sample at a large offset after a 6- and an 8-channel one, none with a channels chunk
            byte[] b = FakeSound.Bank(
                new FakeSound.S { Name = "Six", Frames = 10, Channels = 6, Bytes = 70000 },
                new FakeSound.S { Name = "Eight", Frames = 10, Channels = 8, Bytes = 40 },
                new FakeSound.S { Name = "Two", Frames = 10, Stereo = true, Bytes = 32 });
            SoundBank bank = SoundBank.Read(new MemoryStream(b), "x");
            Assert.That(bank.Samples.Select(x => x.Channels), Is.EqualTo(new[] { 6, 8, 2 }));
            Assert.That(bank.Samples.Select(x => x.Offset), Is.EqualTo(new[] { 0L, 70016L, 70080L }));
            Assert.That(bank.Samples.Select(x => x.Size), Is.EqualTo(new[] { 70016L, 64L, 32L }));
        }

        [Test]
        public void RefusesWhatIsNoBank()
        {
            byte[] b = FakeSound.Bank(new FakeSound.S { Name = "A", Frames = 10, Bytes = 16 });
            byte[] bad = (byte[])b.Clone();
            bad[3] = (byte)'4';
            Assert.That(() => SoundBank.Read(new MemoryStream(bad), "x"), Throws.TypeOf<ModKitException>().With.Message.Contains("not an FSB5"));
            byte[] cut = b.Take(b.Length - 8).ToArray();
            Assert.That(() => SoundBank.Read(new MemoryStream(cut), "x"), Throws.TypeOf<ModKitException>().With.Message.Contains("shorter"));
        }

        [Test]
        public void ReadsTheProjectsCues()
        {
            byte[] f = FakeSound.Project(FakeSound.C(40, "Boss_Fake"), FakeSound.C(9, "Field_Fake"), FakeSound.C(12, "Town_Fake"));
            SoundProject p = SoundProject.Read(f, "fake.fev");
            Assert.That(p.Cues.Select(c => c.ToString()), Is.EqualTo(new[] { "9 Field_Fake", "12 Town_Fake", "40 Boss_Fake" }));
            Assert.That(p.Cue(12).Name, Is.EqualTo("Town_Fake"));
            Assert.That(p.Cue(13), Is.Null);
            Assert.That(p.HasHash(FakeSound.Hash), Is.True);
            Assert.That(p.HasHash(new byte[] { 1, 2, 3, 4, 5, 6, 7, 9 }), Is.False);

            // sizes that do not agree, a cue twice, no RIFF FEV
            byte[] bad = (byte[])f.Clone();
            int at = Encoding.ASCII.GetString(bad).IndexOf("entl", StringComparison.Ordinal);
            bad[at + 4] = 4;
            Assert.That(() => SoundProject.Read(bad, "x"), Throws.TypeOf<ModKitException>().With.Message.Contains("do not agree"));
            Assert.That(() => SoundProject.Read(FakeSound.Project(FakeSound.C(9, "A"), FakeSound.C(9, "B")), "x"),
                Throws.TypeOf<ModKitException>().With.Message.Contains("twice"));
            Assert.That(() => SoundProject.Read(Encoding.ASCII.GetBytes("RIFF\0\0\0\0WAVE"), "x"),
                Throws.TypeOf<ModKitException>().With.Message.Contains("not an FMOD"));
        }

        [Test]
        public void FindsTheMapsCues()
        {
            string fd = "<ms2>\n<fieldData id=\"9100001\">\n<environment>\n<bgm id=\"12\" param=\"3\" value=\"1\" boss=\"40\" bossParam=\"5\" bossValue=\"0\" />\n</environment>\n" +
                "<environment feature=\"X\"><bgm id=\"12\" /></environment>\n</fieldData>\n" +
                "<fieldData id=\"9100002\"><environment><bgm id=\"0\" boss=\"0\" /></environment></fieldData>\n" +
                "<fieldData id=\"9100003\"><environment><bgm id=\"9\" /></environment></fieldData>\n</ms2>";
            List<CueUse> l = SoundData.FromFieldData(fd, "client:Xml.m2d");
            Assert.That(l.Select(u => u.Map + ":" + u.Cue + (u.Boss ? "b" : "") + ":" + u.Param + "=" + u.Value),
                Is.EqualTo(new[] { "9100001:12:3=1", "9100001:40b:5=0", "9100001:12:=", "9100003:9:=" }), "one use per environment (the feature's is its own); 0 is none");
            List<CueUse> m = SoundData.FromMapFile("<ms2><environment><bgm id=\"9\" param=\"0\" value=\"0\" boss=\"0\" /></environment></ms2>", 9100004, "server:Xml.m2d");
            Assert.That(m.Single().Cue, Is.EqualTo(9));
            Assert.That(m.Single().Side, Is.EqualTo("server:Xml.m2d"));
            Assert.That(SoundData.ShortName("MS2Sound_Voice_Kor_def.fsb"), Is.EqualTo("Voice_Kor_def"));
        }

        [Test]
        public void EachUseSaysItsEnvironment()
        {
            // a global map file with a cue per locale, a feature's environment, and a plain one
            string map = "<ms2><environment feature=\"\" locale=\"NA\"><bgm id=\"47\" /></environment>" +
                "<environment feature=\"\" locale=\"KR\"><xblock name=\"x\" /><bgm id=\"48\" /></environment>" +
                "<environment feature=\"FakeOn\" locale=\"\"><bgm id=\"47\" /></environment>" +
                "<environment><bgm id=\"47\" /></environment></ms2>";
            List<CueUse> m = SoundData.FromMapFile(map, 9100005, "gms-client:Xml.m2d");
            Assert.That(m.Select(u => u.Cue + " " + u.Env), Is.EqualTo(new[] { "47 locale=NA", "48 locale=KR", "47 feature=FakeOn", "47 " }));
            string fd = "<ms2><fieldData id=\"9100006\"><environment><bgm id=\"12\" /></environment><environment feature=\"FakeOn\"><bgm id=\"12\" /></environment></fieldData></ms2>";
            Assert.That(SoundData.FromFieldData(fd, "client:Xml.m2d").Select(u => u.Cue + " " + u.Env), Is.EqualTo(new[] { "12 ", "12 feature=FakeOn" }));
            Assert.That(SoundData.EnvLabel("FakeOn", "NA"), Is.EqualTo("feature=FakeOn locale=NA"));
        }

        [Test]
        public void MapUses_ReadEverySide_AndTheModsWholeFiles()
        {
            string dir = Path.Combine(Path.GetTempPath(), "modkit-snd-" + Guid.NewGuid().ToString("N"));
            try
            {
                string k = Path.Combine(dir, "kms"), s = Path.Combine(dir, "server");
                Directory.CreateDirectory(k);
                Directory.CreateDirectory(s);
                M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                {
                    new KeyValuePair<string, byte[]>(FieldRows.Entry, Encoding.UTF8.GetBytes("<ms2><fieldData id=\"9100001\"><environment><bgm id=\"12\" /></environment></fieldData></ms2>")),
                });
                M2dWriter.Create(Path.Combine(s, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
                {
                    new KeyValuePair<string, byte[]>("map/09100001.xml", Encoding.UTF8.GetBytes("<ms2><environment><bgm id=\"12\" boss=\"40\" /></environment></ms2>")),
                    new KeyValuePair<string, byte[]>("map/readme.txt", Encoding.UTF8.GetBytes("x")),
                });
                using (var data = new DataSet(k, null, s))
                {
                    Mod mod = Mod.Parse("<mod id=\"snd.test\" name=\"t\" version=\"1\" />", "test mod");
                    List<CueUse> l = SoundData.MapUses(data, mod);
                    Assert.That(l.Select(u => u.Side + " " + u.Map + ":" + u.Cue + (u.Boss ? "b" : "")),
                        Is.EqualTo(new[] { "client:Xml.m2d 9100001:12", "server:Xml.m2d 9100001:12", "server:Xml.m2d 9100001:40b" }));
                    mod.Files["fd.xml"] = Encoding.UTF8.GetBytes("<ms2><fieldData id=\"9100001\"><environment><bgm id=\"9\" /></environment></fieldData></ms2>");
                    mod.Ops.Add(new EntryOp { Archive = EntityEditor.ClientXml, Name = FieldRows.Entry, File = "fd.xml" });
                    l = SoundData.MapUses(data, mod);
                    Assert.That(l.First().Cue, Is.EqualTo(9), "the mod's whole file");

                    // a map file the mod adds, and a whole file of an archive the mod spells in another case
                    mod.Files["new.xml"] = Encoding.UTF8.GetBytes("<ms2><environment><bgm id=\"33\" /></environment></ms2>");
                    mod.Files["old.xml"] = Encoding.UTF8.GetBytes("<ms2><environment><bgm id=\"34\" /></environment></ms2>");
                    mod.Ops.Add(new EntryOp { Archive = "server:Xml.m2d", Name = "map/09100002.xml", File = "new.xml" });
                    mod.Ops.Add(new EntryOp { Archive = "SERVER:xml.M2D", Name = "map/09100001.xml", File = "old.xml" });
                    l = SoundData.MapUses(data, mod);
                    Assert.That(l.Where(u => u.Side == EntityEditor.ServerXml).Select(u => u.Map + ":" + u.Cue),
                        Is.EqualTo(new[] { "9100001:34", "9100002:33" }));
                }
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        /// <summary>The local Sound folder (MS2_SOUND_DATA): every bank reads, the cue list checks, and the banks'
        /// hashes are in the project. Skipped without the folder; nothing of it is kept.</summary>
        [Test]
        public void RealSoundFolder()
        {
            string dir = Environment.GetEnvironmentVariable(SoundData.Override);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) Assert.Ignore(SoundData.Override + " is not set");
            var d = new SoundData(dir);
            SoundProject p = SoundProject.Read(d.ProjectPath);
            Assert.That(p.Cues.Count, Is.GreaterThan(0));
            List<string> banks = d.Banks();
            Assert.That(banks, Is.Not.Empty);
            foreach (string f in banks)
            {
                SoundBank b = SoundBank.Read(Path.Combine(dir, f));
                Assert.That(b.Samples.Count, Is.GreaterThan(0), f);
                Assert.That(b.Samples.All(x => x.Rate > 0 && x.Frames > 0 && x.Size > 0 && !x.Name.StartsWith("#")), f);
                Assert.That(p.HasHash(b.Hash), Is.True, f + ": its hash is not in the project");
            }
        }
    }
}
