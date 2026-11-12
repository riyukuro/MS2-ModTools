// Tests of the sample replace (SoundReplace.cs): the bank writer, the MPEG reader, the plan, the workspace and the
// loose target. Made-up banks and made-up MPEG frames only (no game audio).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    /// <summary>Made-up MPEG audio frames: headers that parse, bodies of filler bytes.</summary>
    public static class FakeMpeg
    {
        // MPEG-1 layer III, 128 kbit/s, 44100 Hz: 417 bytes a frame (418 with padding), 1152 samples
        public static byte[] Frame(bool mono = false, bool pad = false, byte fill = 0x55, int layer = 3, int version = 1, int bitIndex = 9, int rateIndex = 0)
        {
            int v = version == 1 ? 3 : version == 2 ? 2 : 0;
            int l = layer == 3 ? 1 : layer == 2 ? 2 : 3;
            byte b1 = (byte)(0xE0 | v << 3 | l << 1 | 1);
            byte b2 = (byte)(bitIndex << 4 | rateIndex << 2 | (pad ? 2 : 0));
            byte b3 = (byte)(mono ? 0xC0 : 0x00);
            int rate = new[] { 44100, 48000, 32000 }[rateIndex] / (version == 1 ? 1 : version == 2 ? 2 : 4);
            int[] kb = version == 1 ? (layer == 2 ? new[] { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 } : new[] { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 })
                : new[] { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };
            int spf = layer == 3 && version != 1 ? 576 : 1152;
            int len = layer == 1 || bitIndex == 0 ? 100 : spf / 8 * kb[bitIndex] * 1000 / rate + (pad ? 1 : 0);
            var f = new byte[len];
            for (int i = 4; i < len; i++) f[i] = fill;
            f[0] = 0xFF; f[1] = b1; f[2] = b2; f[3] = b3;
            return f;
        }

        public static byte[] Xing(bool mono = false)
        {
            byte[] f = Frame(mono, fill: 0);
            Encoding.ASCII.GetBytes("Xing").CopyTo(f, 4 + (mono ? 17 : 32));
            return f;
        }

        public static byte[] Join(params byte[][] parts) { return parts.SelectMany(p => p).ToArray(); }

        public static byte[] Frames(int n, bool mono = false, byte fill = 0x55)
        {
            return Join(Enumerable.Range(0, n).Select(i => Frame(mono, i % 3 == 1, (byte)(fill + i))).ToArray());
        }

        public static byte[] Id3v2(int size)
        {
            var b = new byte[10 + size];
            Encoding.ASCII.GetBytes("ID3").CopyTo(b, 0);
            b[3] = 4;
            b[6] = (byte)(size >> 21 & 0x7f); b[7] = (byte)(size >> 14 & 0x7f); b[8] = (byte)(size >> 7 & 0x7f); b[9] = (byte)(size & 0x7f);
            for (int i = 10; i < b.Length; i++) b[i] = 0xFF;    // sync-like bytes inside the tag are skipped with it
            return b;
        }

        public static byte[] Id3v1()
        {
            var b = new byte[128];
            Encoding.ASCII.GetBytes("TAG").CopyTo(b, 0);
            return b;
        }
    }

    [TestFixture]
    public class SoundReplaceTests
    {
        // a bank whose data are not zeros, so a moved blob shows
        static byte[] Bank(params FakeSound.S[] samples)
        {
            byte[] b = FakeSound.Bank(samples);
            int n = BitConverter.ToInt32(b, 8), shs = BitConverter.ToInt32(b, 12), nts = BitConverter.ToInt32(b, 16);
            int at = SoundBank.HeaderSize + shs + nts;
            for (int i = at; i < b.Length; i++) b[i] = (byte)((i - at) % 251 + 1);
            return b;
        }

        static FakeSound.S[] Three()
        {
            return new[]
            {
                new FakeSound.S { Name = "Tune_A", Frames = 44100 * 2, Stereo = true, Loop = true, Bytes = 1000 },
                new FakeSound.S { Name = "Tune_B", Frames = 48000, RateIndex = 9, Stereo = true, Loop = true, Bytes = 700 },
                new FakeSound.S { Name = "Click", Frames = 2205, RateIndex = 5, Rate = 22050, Bytes = 40 },
            };
        }

        static byte[] Write(byte[] bank, Dictionary<int, MpegAudio> rep)
        {
            var o = new MemoryStream();
            SoundReplace.Write(new MemoryStream(bank), o, rep, "fake.fsb", null);
            return o.ToArray();
        }

        static byte[] DataOf(byte[] bank, SoundSample s)
        {
            int shs = BitConverter.ToInt32(bank, 12), nts = BitConverter.ToInt32(bank, 16);
            return bank.Skip(SoundBank.HeaderSize + shs + nts + (int)s.Offset).Take((int)s.Size).ToArray();
        }

        // ---- the writer ----

        [Test]
        public void Write_WithNothingToReplace_GivesTheSameBytes()
        {
            byte[] b = Bank(Three());
            Assert.That(Write(b, new Dictionary<int, MpegAudio>()), Is.EqualTo(b));
        }

        [Test]
        public void Write_ReplacesOneSample_AndKeepsTheNamesTheHashAndTheOtherData()
        {
            byte[] b = Bank(Three());
            MpegAudio a = MpegAudio.Parse(FakeMpeg.Frames(5, mono: true), "new.mp3");
            byte[] w = Write(b, new Dictionary<int, MpegAudio> { { 1, a } });
            SoundBank old = SoundBank.Read(new MemoryStream(b), "old"), nb = SoundBank.Read(new MemoryStream(w), "new");

            Assert.That(nb.Samples.Select(s => s.Name), Is.EqualTo(old.Samples.Select(s => s.Name)), "every index keeps its name");
            Assert.That(nb.HashText, Is.EqualTo(old.HashText), "the event project checks the hash");
            Assert.That(w.Skip(SoundBank.HeaderSize - 0x18).Take(0x18).ToArray(), Is.EqualTo(b.Skip(SoundBank.HeaderSize - 0x18).Take(0x18).ToArray()));
            SoundSample t = nb.Samples[1];
            Assert.That(t.Frames, Is.EqualTo(5 * 1152));
            Assert.That(t.Rate, Is.EqualTo(44100));
            Assert.That(t.Channels, Is.EqualTo(1));
            Assert.That(t.Loops, Is.True, "the loop chunk stays");
            Assert.That(DataOf(w, t).Take(a.Data.Length).ToArray(), Is.EqualTo(a.Data));
            Assert.That(t.Size % 32, Is.EqualTo(0));
            Assert.That(DataOf(w, nb.Samples[0]), Is.EqualTo(DataOf(b, old.Samples[0])));
            Assert.That(DataOf(w, nb.Samples[2]), Is.EqualTo(DataOf(b, old.Samples[2])), "the data after the new sample move along");
            Assert.That(nb.Samples[2].Offset, Is.EqualTo(old.Samples[2].Offset - old.Samples[1].Size + t.Size));
            Assert.That(nb.DataSize, Is.EqualTo(old.DataSize - old.Samples[1].Size + t.Size));
            Assert.That(w.Length, Is.EqualTo(b.Length - old.Samples[1].Size + t.Size));

            // the loop chunk spans the new sound: 0 to frames - 1
            int shs = BitConverter.ToInt32(w, 12);
            byte[] sh = w.Skip(SoundBank.HeaderSize).Take(shs).ToArray();
            int p = 8 + 4 + 8;                      // sample 0: its header, the loop chunk
            Assert.That(BitConverter.ToUInt32(sh, p + 8 + 4), Is.EqualTo(0u));
            Assert.That(BitConverter.ToUInt32(sh, p + 8 + 8), Is.EqualTo((uint)(5 * 1152 - 1)));
        }

        [Test]
        public void Write_FollowsTheRateChunk()
        {
            byte[] b = Bank(Three());
            MpegAudio a = MpegAudio.Parse(FakeMpeg.Join(FakeMpeg.Frame(rateIndex: 1), FakeMpeg.Frame(rateIndex: 1)), "x.mp3");
            byte[] w = Write(b, new Dictionary<int, MpegAudio> { { 2, a } });
            SoundSample t = SoundBank.Read(new MemoryStream(w), "new").Samples[2];
            Assert.That(t.Rate, Is.EqualTo(48000), "the frequency chunk holds the new rate");
            Assert.That(t.Channels, Is.EqualTo(2));
            Assert.That(t.Frames, Is.EqualTo(2 * 1152));
        }

        [Test]
        public void Write_RefusesWhatItCannotFollow()
        {
            byte[] b = Bank(Three());
            MpegAudio a = MpegAudio.Parse(FakeMpeg.Frames(2), "x.mp3");
            Assert.That(() => Write(b, new Dictionary<int, MpegAudio> { { 3, a } }), Throws.TypeOf<ModKitException>().With.Message.Contains("no sample 3"));
            byte[] pcm = (byte[])b.Clone();
            pcm[24] = 2;
            Assert.That(() => Write(pcm, new Dictionary<int, MpegAudio> { { 0, a } }), Throws.TypeOf<ModKitException>().With.Message.Contains("not MPEG"));
            // a sample with a chunk of another kind (type 6) cannot be replaced; the others can
            byte[] odd = (byte[])b.Clone();
            int at = SoundBank.HeaderSize + 8;      // sample 0's loop chunk
            uint c = BitConverter.ToUInt32(odd, at);
            BitConverter.GetBytes((c & 0x1ffffff) | (6u << 25)).CopyTo(odd, at);
            Assert.That(() => Write(odd, new Dictionary<int, MpegAudio> { { 0, a } }), Throws.TypeOf<ModKitException>().With.Message.Contains("type 6"));
            Assert.That(SoundReplace.CannotReplace(SoundBank.Read(new MemoryStream(odd), "odd").Samples[0]), Does.Contain("type 6"));
            Assert.That(SoundReplace.CannotReplace(SoundBank.Read(new MemoryStream(odd), "odd").Samples[1]), Is.Null);
            Assert.That(() => Write(odd, new Dictionary<int, MpegAudio> { { 1, a } }), Throws.Nothing);
        }

        // ---- the MPEG reader ----

        [Test]
        public void Mpeg_TakesTheFramesOnly()
        {
            byte[] frames = FakeMpeg.Frames(4);
            byte[] file = FakeMpeg.Join(FakeMpeg.Id3v2(300), FakeMpeg.Xing(), frames, FakeMpeg.Frame().Take(100).ToArray(), FakeMpeg.Id3v1());
            MpegAudio a = MpegAudio.Parse(file, "x.mp3");
            Assert.That(a.Data, Is.EqualTo(frames), "no tags, no Xing frame, no cut-off last frame");
            Assert.That(a.MpegFrames, Is.EqualTo(4));
            Assert.That(a.Frames, Is.EqualTo(4 * 1152));
            Assert.That(a.Rate, Is.EqualTo(44100));
            Assert.That(a.Channels, Is.EqualTo(2));
            Assert.That(a.Layer, Is.EqualTo(3));
            Assert.That(a.Seconds, Is.EqualTo(4 * 1152 / 44100.0).Within(1e-9));
            Assert.That(a.Describe(), Is.EqualTo("0.10 s, MPEG-1 layer III, 44100 Hz, stereo"));

            MpegAudio m = MpegAudio.Parse(FakeMpeg.Join(FakeMpeg.Xing(mono: true), FakeMpeg.Frames(2, mono: true)), "m.mp3");
            Assert.That(m.MpegFrames, Is.EqualTo(2), "the Xing frame of a mono file is found at its own place");
            Assert.That(m.Channels, Is.EqualTo(1));

            MpegAudio two = MpegAudio.Parse(FakeMpeg.Join(FakeMpeg.Frame(layer: 2), FakeMpeg.Frame(layer: 2)), "x.mp2");
            Assert.That(two.Layer, Is.EqualTo(2));
            Assert.That(two.Frames, Is.EqualTo(2 * 1152));
            MpegAudio v2 = MpegAudio.Parse(FakeMpeg.Join(FakeMpeg.Frame(version: 2, bitIndex: 8), FakeMpeg.Frame(version: 2, bitIndex: 8)), "x.mp3");
            Assert.That(v2.Rate, Is.EqualTo(22050));
            Assert.That(v2.Frames, Is.EqualTo(2 * 576), "MPEG-2 layer III has 576 samples a frame");
            Assert.That(v2.Data.Length, Is.EqualTo(2 * 208));
        }

        [Test]
        public void Mpeg_RefusesWhatIsNoMpegAudioOrMixed()
        {
            Assert.That(() => MpegAudio.Parse(Encoding.ASCII.GetBytes("RIFF....WAVEfmt "), "x.wav"), Throws.TypeOf<ModKitException>().With.Message.Contains("not an .mp2 or .mp3"));
            Assert.That(() => MpegAudio.Parse(FakeMpeg.Join(FakeMpeg.Frame(layer: 1), FakeMpeg.Frame(layer: 1)), "x.mp1"), Throws.TypeOf<ModKitException>().With.Message.Contains("layer I"));
            Assert.That(() => MpegAudio.Parse(FakeMpeg.Frame(bitIndex: 0), "x.mp3"), Throws.TypeOf<ModKitException>().With.Message.Contains("free-format"));
            Assert.That(() => MpegAudio.Parse(FakeMpeg.Join(FakeMpeg.Frame(), FakeMpeg.Frame(mono: true)), "x.mp3"), Throws.TypeOf<ModKitException>().With.Message.Contains("another kind"));
            Assert.That(() => MpegAudio.Parse(FakeMpeg.Join(FakeMpeg.Frame(), new byte[] { 1, 2, 3, 4, 5 }, FakeMpeg.Frame()), "x.mp3"), Throws.TypeOf<ModKitException>().With.Message.Contains("no MPEG audio frame at byte 417"));
            Assert.That(() => MpegAudio.Parse(FakeMpeg.Xing(), "x.mp3"), Throws.TypeOf<ModKitException>().With.Message.Contains("no MPEG audio frames"));
        }

        // ---- the manifest ----

        const string Line = "<sample bank=\"MS2Sound_Fake.fsb\" index=\"1\" name=\"Tune_B\" file=\"files/sound/Fake/1.mp3\" />";

        [Test]
        public void Manifest_ReadsAndWritesTheSampleLine()
        {
            Mod m = Mod.Parse("<mod id=\"s\" name=\"t\" version=\"1\">" + Line + "</mod>", "t");
            Assert.That(m.IsEmpty, Is.False);
            SampleOp s = m.Samples.Single();
            Assert.That(new object[] { s.Bank, s.Index, s.Name, s.File }, Is.EqualTo(new object[] { "MS2Sound_Fake.fsb", 1, "Tune_B", "files/sound/Fake/1.mp3" }));
            Assert.That(m.SampleOf("ms2sound_fake.FSB", 1), Is.SameAs(s));
            Assert.That(m.ToXml(), Does.Contain("  " + Line + "\n"));
            Assert.That(Mod.Parse(m.ToXml(), "again").Samples.Single().Name, Is.EqualTo("Tune_B"));
            Assert.That(Mod.SampleFile("MS2Sound_Fake.fsb", 1, ".mp3"), Is.EqualTo("files/sound/Fake/1.mp3"));

            Assert.That(() => Mod.Parse("<mod id=\"s\" name=\"t\" version=\"1\"><sample bank=\"Sound/x.fsb\" index=\"1\" name=\"a\" file=\"f\" /></mod>", "t"),
                Throws.TypeOf<ModKitException>().With.Message.Contains("file name of a bank"));
            Assert.That(() => Mod.Parse("<mod id=\"s\" name=\"t\" version=\"1\"><sample bank=\"x.fsb\" index=\"1\" file=\"f\" /></mod>", "t"),
                Throws.TypeOf<ModKitException>().With.Message.Contains("name"));
            Assert.That(() => Mod.Parse("<mod id=\"s\" name=\"t\" version=\"1\"><sample bank=\"x.fsb\" index=\"1\" name=\"a\" file=\"f\" loop=\"1\" /></mod>", "t"),
                Throws.TypeOf<ModKitException>().With.Message.Contains("loop"));
        }

        [Test]
        public void Manifest_SaveKeepsTheSampleFiles()
        {
            using (var f = new Fake())
            {
                string d = f.ModDir("s", Line);
                Directory.CreateDirectory(Path.Combine(d, "files", "sound", "Fake"));
                File.WriteAllBytes(Path.Combine(d, "files", "sound", "Fake", "1.mp3"), new byte[] { 1, 2, 3 });
                File.WriteAllBytes(Path.Combine(d, "files", "orphan.bin"), new byte[] { 9 });
                Mod m = Mod.Load(d);
                m.Save(d);
                Assert.That(File.Exists(Path.Combine(d, "files", "sound", "Fake", "1.mp3")), Is.True, "a file a sample line names is no orphan");
                Assert.That(File.Exists(Path.Combine(d, "files", "orphan.bin")), Is.False);
                string other = Path.Combine(f.Dir, "mods", "s2");
                m.Save(other);
                Assert.That(File.ReadAllBytes(Path.Combine(other, "files", "sound", "Fake", "1.mp3")), Is.EqualTo(new byte[] { 1, 2, 3 }), "saved elsewhere, the file goes along");
            }
        }

        [Test]
        public void Editor_SetAndTakeASample()
        {
            SoundBank b = SoundBank.Read(new MemoryStream(Bank(Three())), "MS2Sound_Fake.fsb");
            Mod m = Mod.Parse("<mod id=\"s\" name=\"t\" version=\"1\"></mod>", "t");
            byte[] mp3 = FakeMpeg.Frames(3);
            Assert.That(() => m.SetSample("MS2Sound_Fake.fsb", b.Samples[1], new byte[] { 1, 2, 3 }, "x.mp3"), Throws.TypeOf<ModKitException>());
            Assert.That(m.Samples, Is.Empty, "a file that does not read changes nothing");
            SampleOp op = m.SetSample("MS2Sound_Fake.fsb", b.Samples[1], mp3, "new.mp3");
            Assert.That(new object[] { op.Index, op.Name, op.File }, Is.EqualTo(new object[] { 1, "Tune_B", "files/sound/Fake/1.mp3" }));
            Assert.That(m.Files[op.File], Is.SameAs(mp3));
            m.SetSample("MS2Sound_Fake.fsb", b.Samples[1], mp3, "new.MP2");
            Assert.That(m.Samples.Single().File, Is.EqualTo("files/sound/Fake/1.mp2"));
            Assert.That(m.Files.Keys, Is.EqualTo(new[] { "files/sound/Fake/1.mp2" }), "the earlier file goes");
            Assert.That(Mod.Parse(m.ToXml(), "again").Samples.Single().Name, Is.EqualTo("Tune_B"));
            Assert.That(m.TakeSample("MS2Sound_Fake.fsb", 1), Is.True);
            Assert.That(m.Samples, Is.Empty);
            Assert.That(m.Files, Is.Empty);
            Assert.That(m.TakeSample("MS2Sound_Fake.fsb", 1), Is.False);
        }

        // ---- the plan ----

        sealed class World : IDisposable
        {
            public readonly Fake F = new Fake();
            public readonly byte[] BankBytes = Bank(Three());
            public string Sound { get { return Path.Combine(F.ClientDir, "Sound"); } }

            public World()
            {
                Assume.That(Environment.GetEnvironmentVariable(SoundData.Override), Is.Null.Or.Empty, SoundData.Override + " points the plan at another Sound folder");
                Directory.CreateDirectory(Sound);
                File.WriteAllBytes(Path.Combine(Sound, SoundData.ProjectFile), FakeSound.Project(FakeSound.C(1, "Tune_A")));
                File.WriteAllBytes(Path.Combine(Sound, "MS2Sound_Fake.fsb"), BankBytes);
            }

            public Mod Mod(string id, string lines, params KeyValuePair<string, byte[]>[] files)
            {
                string d = F.ModDir(id, lines);
                foreach (var kv in files)
                {
                    string p = Path.Combine(d, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                    File.WriteAllBytes(p, kv.Value);
                }
                return MS2.ModKit.Mod.Load(d);
            }

            public void Dispose() { F.Dispose(); }
        }

        static string S(int index, string name, string file) { return "<sample bank=\"MS2Sound_Fake.fsb\" index=\"" + index + "\" name=\"" + name + "\" file=\"" + file + "\" />"; }
        static KeyValuePair<string, byte[]> Mp3(string file, int frames, bool mono = false) { return new KeyValuePair<string, byte[]>(file, FakeMpeg.Frames(frames, mono)); }

        [Test]
        public void Plan_NamesTheSample_AndWarnsAboutTheLength()
        {
            using (var w = new World())
            {
                // 77 frames of 1152 at 44100 Hz: 2.01 s for the 2 s Tune_A; 5 frames for the 1 s Tune_B
                Mod m = w.Mod("a", S(0, "Tune_A", "files/a.mp3") + S(1, "Tune_B", "files/b.mp3"), Mp3("files/a.mp3", 77), Mp3("files/b.mp3", 5, mono: true));
                Plan p = w.F.Plan(m);
                Assert.That(p.Errors, Is.Empty, EditorSession.Report(p));
                PlanBank b = p.Banks.Single();
                Assert.That(b.Ref, Is.EqualTo("client:Sound/MS2Sound_Fake.fsb"));
                Assert.That(b.Count, Is.EqualTo(3));
                Assert.That(b.Samples.Select(s => s.Index + " " + s.Name + " " + s.ModId), Is.EqualTo(new[] { "0 Tune_A a", "1 Tune_B a" }));
                Assert.That(p.Changes.Select(c => c.Archive + " " + c.Entry), Is.EquivalentTo(new[] { "client:Sound/MS2Sound_Fake.fsb sample 0 Tune_A", "client:Sound/MS2Sound_Fake.fsb sample 1 Tune_B" }));
                Assert.That(p.Warnings.Select(x => x.Entry), Is.EqualTo(new[] { "sample 1 Tune_B" }), "0.01 s off is no warning");
                Assert.That(p.Warnings.Single().Text, Does.Contain("keeps the shipped length"));
                Assert.That(p.Notes.Select(x => x.Entry), Does.Contain("sample 1 Tune_B"), "48000 Hz stereo to 44100 Hz mono");
            }
        }

        [Test]
        public void Plan_StopsAtAWrongLine()
        {
            using (var w = new World())
            {
                var mp3 = Mp3("files/a.mp3", 3);
                Func<string, string> err = lines =>
                {
                    Plan p = w.F.Plan(w.Mod("e" + Guid.NewGuid().ToString("N").Substring(0, 6), lines, mp3));
                    Assert.That(p.Errors, Is.Not.Empty);
                    Assert.That(p.Banks.Sum(b => b.Samples.Count), Is.LessThanOrEqualTo(1), "a wrong line is left out");
                    return string.Join(" | ", p.Errors.Select(x => x.Text));
                };
                Assert.That(err(S(1, "Tune_A", "files/a.mp3")), Does.Contain("is Tune_B, not Tune_A"));
                Assert.That(err(S(3, "Click", "files/a.mp3")), Does.Contain("has 3 samples"));
                Assert.That(err(S(0, "Tune_A", "files/none.mp3")), Does.Contain("is not there"));
                Assert.That(err(S(0, "Tune_A", "mod.xml")), Does.Contain("not an .mp2 or .mp3"));
                Assert.That(err("<sample bank=\"MS2Sound_None.fsb\" index=\"0\" name=\"x\" file=\"files/a.mp3\" />"), Does.Contain("no bank MS2Sound_None.fsb"));
                Assert.That(err(S(0, "Tune_A", "files/a.mp3") + S(0, "Tune_A", "files/a.mp3")), Does.Contain("replaces this sample twice"));
            }
        }

        [Test]
        public void Plan_TwoModsOnOneSample_TheLaterOneWins()
        {
            using (var w = new World())
            {
                Mod a = w.Mod("a", S(0, "Tune_A", "files/a.mp3"), Mp3("files/a.mp3", 77));
                Mod b = w.Mod("b", S(0, "Tune_A", "files/b.mp3"), Mp3("files/b.mp3", 76, mono: true));
                Plan p = w.F.Plan(a, b);
                PlanMessage c = p.Conflicts.Single();
                Assert.That(c.WinnerId, Is.EqualTo("b"));
                Assert.That(c.LoserId, Is.EqualTo("a"));
                Assert.That(p.Banks.Single().Samples.Single().ModId, Is.EqualTo("b"));
                Assert.That(p.Banks.Single().Samples.Single().Audio.Channels, Is.EqualTo(1));
            }
        }

        // ---- the workspace ----

        [Test]
        public void Workspace_WritesTheBank_AndTakesItAwayAgain()
        {
            using (var w = new World())
            {
                Mod m = w.Mod("a", S(1, "Tune_B", "files/b.mp3"), Mp3("files/b.mp3", 5));
                var ws = new Workspace(Path.Combine(w.F.Dir, "ws"));
                ApplyResult r = ws.Apply(w.F.Data, new[] { m }, null);
                Assert.That(r.Applied, Is.True, EditorSession.Report(r));
                string bank = Path.Combine(ws.Dir, "client", "Sound", "MS2Sound_Fake.fsb");
                ApplyOutput o = r.Outputs.Single();
                Assert.That(o.Path, Is.EqualTo(bank));
                Assert.That(new[] { o.Replaced, o.Kept }, Is.EqualTo(new[] { 1, 2 }));
                Assert.That(SoundBank.Read(bank).Samples[1].Frames, Is.EqualTo(5 * 1152));
                Assert.That(File.ReadAllBytes(Path.Combine(w.Sound, "MS2Sound_Fake.fsb")), Is.EqualTo(w.BankBytes), "the shipped bank is only read");
                Assert.That(ws.ReadState().Archives.Single(a => a.Bank).File, Is.EqualTo("client/Sound/MS2Sound_Fake.fsb"));

                // the root filter leaves the bank out, as the loose target does for the workspace
                r = ws.Apply(w.F.Data, new[] { m }, new ApplyOptions { Roots = new List<string> { "server" } });
                Assert.That(r.Outputs, Is.Empty);
                Assert.That(File.Exists(bank), Is.False);
                Assert.That(r.Removed, Does.Contain(bank));

                ws.Apply(w.F.Data, new[] { m }, null);
                ApplyResult rv = ws.Revert(w.F.Data, null, null);
                Assert.That(rv.Removed, Does.Contain(bank));
                Assert.That(File.Exists(bank), Is.False);
            }
        }

        // ---- the loose target ----

        static string Game(World w)
        {
            string g = Path.Combine(w.F.Dir, "game");
            Directory.CreateDirectory(Path.Combine(g, "Data", "Sound"));
            File.WriteAllBytes(Path.Combine(g, "Data", "Sound", "MS2Sound_Fake.fsb"), w.BankBytes);
            return g;
        }

        [Test]
        public void Loose_PutsTheShippedBankAside_AndBack()
        {
            using (var w = new World())
            {
                string g = Game(w), bank = Path.Combine(g, "Data", "Sound", "MS2Sound_Fake.fsb"), orig = bank + SoundReplace.OrigSuffix;
                var lt = new LooseTarget(g);
                Mod a = w.Mod("a", S(1, "Tune_B", "files/b.mp3"), Mp3("files/b.mp3", 5));
                ApplyResult r = lt.Apply(w.F.Data, new[] { a }, null);
                Assert.That(r.Applied, Is.True, EditorSession.Report(r));
                Assert.That(r.Written, Is.EqualTo(new[] { bank }));
                Assert.That(File.ReadAllBytes(orig), Is.EqualTo(w.BankBytes));
                Assert.That(SoundBank.Read(bank).Samples[1].Frames, Is.EqualTo(5 * 1152));
                LooseFile rec = lt.ReadState().Banks.Single();
                Assert.That(rec.Path, Is.EqualTo("Data/Sound/MS2Sound_Fake.fsb"));
                Assert.That(rec.Sha1, Is.EqualTo(SoundReplace.Sha1File(bank)));
                Assert.That(new DirectoryInfo(Path.Combine(g, "Data", "Sound")).GetFiles().Select(x => x.Name).OrderBy(x => x),
                    Is.EqualTo(new[] { "MS2Sound_Fake.fsb", "MS2Sound_Fake.fsb.modkit-orig" }), "no temporary file is left");

                // a second apply builds from the shipped bank again, not from the one in place
                Mod b = w.Mod("b", S(0, "Tune_A", "files/a.mp3"), Mp3("files/a.mp3", 77));
                r = lt.Apply(w.F.Data, new[] { b }, null);
                Assert.That(r.Applied, Is.True, EditorSession.Report(r));
                SoundBank now = SoundBank.Read(bank);
                Assert.That(now.Samples[1].Frames, Is.EqualTo(48000), "a's sample is gone with a");
                Assert.That(now.Samples[0].Frames, Is.EqualTo(77 * 1152));
                Assert.That(File.ReadAllBytes(orig), Is.EqualTo(w.BankBytes));

                // without any sample line the shipped bank goes back
                r = lt.Apply(w.F.Data, new[] { w.Mod("c", "") }, null);
                Assert.That(r.Applied, Is.True, EditorSession.Report(r));
                Assert.That(File.ReadAllBytes(bank), Is.EqualTo(w.BankBytes));
                Assert.That(File.Exists(orig), Is.False);

                lt.Apply(w.F.Data, new[] { a }, null);
                ApplyResult rv = lt.Revert();
                Assert.That(rv.Removed, Does.Contain(orig));
                Assert.That(File.ReadAllBytes(bank), Is.EqualTo(w.BankBytes));
                Assert.That(File.Exists(orig), Is.False);
                Assert.That(lt.ReadState(), Is.Null);
            }
        }

        [Test]
        public void Loose_LeavesWhatItDidNotWrite()
        {
            using (var w = new World())
            {
                string g = Game(w), bank = Path.Combine(g, "Data", "Sound", "MS2Sound_Fake.fsb"), orig = bank + SoundReplace.OrigSuffix;
                var lt = new LooseTarget(g);
                Mod a = w.Mod("a", S(1, "Tune_B", "files/b.mp3"), Mp3("files/b.mp3", 5));

                // a backup of someone else's
                File.WriteAllBytes(orig, new byte[] { 1 });
                ApplyResult r = lt.Apply(w.F.Data, new[] { a }, null);
                Assert.That(r.Applied, Is.False);
                Assert.That(r.Plan.Conflicts.Single().Text, Does.Contain("was not written by this tool"));
                Assert.That(File.ReadAllBytes(bank), Is.EqualTo(w.BankBytes));
                File.Delete(orig);

                // a game bank of another version
                byte[] other = (byte[])w.BankBytes.Clone();
                other[0x24] ^= 0xff;
                File.WriteAllBytes(bank, other);
                r = lt.Apply(w.F.Data, new[] { a }, null);
                Assert.That(r.Applied, Is.False);
                Assert.That(r.Plan.Errors.Single().Text, Does.Contain("not the bank the plan was made from"));
                File.WriteAllBytes(bank, w.BankBytes);

                // the bank in place changed after the apply: no new apply, and a revert leaves it with its record
                Assert.That(lt.Apply(w.F.Data, new[] { a }, null).Applied, Is.True);
                File.AppendAllText(bank, "x");
                r = lt.Apply(w.F.Data, new[] { a }, null);
                Assert.That(r.Applied, Is.False);
                Assert.That(r.Plan.Conflicts.Single().Text, Does.Contain("was changed after this tool wrote it"));
                ApplyResult rv = lt.Revert();
                Assert.That(rv.Left, Is.EqualTo(new[] { bank }));
                Assert.That(File.Exists(orig), Is.True);
                Assert.That(lt.ReadState().Banks.Single().Path, Is.EqualTo("Data/Sound/MS2Sound_Fake.fsb"), "the shipped bank beside it is not forgotten");
            }
        }

        [Test]
        public void Loose_RefusesASoundFolderThatIsALink()
        {
            using (var w = new World())
            {
                string g = Path.Combine(w.F.Dir, "game"), shared = Path.Combine(w.F.Dir, "shared");
                Directory.CreateDirectory(shared);
                File.WriteAllBytes(Path.Combine(shared, "MS2Sound_Fake.fsb"), w.BankBytes);
                Directory.CreateDirectory(Path.Combine(g, "Data"));
                try { Directory.CreateSymbolicLink(Path.Combine(g, "Data", "Sound"), shared); }
                catch (Exception x) when (x is IOException || x is UnauthorizedAccessException) { Assert.Ignore("no symbolic links here: " + x.Message); }
                Mod a = w.Mod("a", S(1, "Tune_B", "files/b.mp3"), Mp3("files/b.mp3", 5));
                ApplyResult r = new LooseTarget(g).Apply(w.F.Data, new[] { a }, null);
                Assert.That(r.Applied, Is.False);
                Assert.That(r.Plan.Errors.Single().Text, Does.Contain("is a link"));
                Assert.That(File.ReadAllBytes(Path.Combine(shared, "MS2Sound_Fake.fsb")), Is.EqualTo(w.BankBytes));
                Assert.That(Directory.GetFiles(shared).Length, Is.EqualTo(1));
            }
        }
    }
}
