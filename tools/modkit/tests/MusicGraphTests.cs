using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MS2.ModKit;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    /// <summary>A made-up event project in the layout of the shipped one: RIFF FEV, LIST PROJ, the legacy chunk whose
    /// end is the music (comp), then a chunk after it. Two cues: 7 plays segment 3 once and then loops 4; 8 loops 5.</summary>
    public static class FakeMusic
    {
        static byte[] Cat(params byte[][] p) { var o = new MemoryStream(); foreach (byte[] x in p) o.Write(x, 0, x.Length); return o.ToArray(); }
        static byte[] I(int v) { return BitConverter.GetBytes(v); }
        static byte[] H(int v) { return BitConverter.GetBytes((ushort)v); }
        static byte[] F(float v) { return BitConverter.GetBytes(v); }
        static byte[] A(string s) { return Encoding.ASCII.GetBytes(s); }

        /// <summary>A comp chunk: u32 length with the head, then the tag.</summary>
        public static byte[] Ch(string tag, params byte[][] p) { byte[] b = Cat(p); return Cat(I(b.Length + 8), A(tag), b); }

        static byte[] Riff(string tag, byte[] b) { return Cat(A(tag), I(b.Length), b, (b.Length & 1) == 1 ? new byte[1] : new byte[0]); }

        static byte[] Seg(int id, int theme, float bpm, float sec, string bank, int index)
        {
            byte[] name = A(bank + "\0");
            return Ch("sgmd", I(id), I(theme), I(0), new byte[] { 4, 4 }, F(bpm), F(sec), I(0x55),
                Ch("smps", Ch("smph", new byte[] { 0 }, I(1)), Ch("smp ", I(name.Length), name, I(index))));
        }

        static byte[] Link(int id, int to, byte[] cond) { return Ch("lnk ", Ch("lnkd", I(id), I(to), I(4)), Ch("cond", cond)); }

        static byte[] Theme(int id, int start, int fade)
        {
            return Ch("thm ", Ch("thmd", I(id), new byte[] { 0, 2, 0, 0, 0, 0, 0 }, H(fade), new byte[4], H(1), I(start)), Ch("cond"));
        }

        public static byte[] Comp()
        {
            byte[] names = A("Title\0Field\0");
            return Ch("comp",
                Ch("sett", F(0.5f), F(1f)),
                Ch("cues", Ch("entl", H(2), H(names.Length), I(7), I(8), names)),
                Ch("prms", Ch("prmh", H(1)), Ch("prmd", I(5)), Ch("entl", H(1), H(5), I(5), A("Loop\0"))),
                Ch("lnks", Ch("lnkh", H(3)),
                    Link(10, 4, Ch("cprm", H(0), I(5), I(0), F(10f), H(0))), Link(11, 4, new byte[0]), Link(12, 5, new byte[0]),
                    Ch("lfsh", H(3)), Ch("lfsd", I(3), H(1), I(10)), Ch("lfsd", I(4), H(1), I(11)), Ch("lfsd", I(5), H(1), I(12))),
                Ch("tlns", Ch("tlnh", H(1)), Ch("tlnd", I(2))),
                Ch("sgms", Ch("sgmh", H(3)),
                    Seg(3, 1, 120, 5, "MS2Sound_Fake", 1), Seg(4, 1, 120, 60, "MS2Sound_Fake", 0), Seg(5, 2, 90, 30, "MS2Sound_Other", 2),
                    Ch("smpf", Ch("str ", I(1), I(0), I(6), A("a.wav\0")), Ch("smpm", I(1), I(3), I(0), I(0)))),
                Ch("thms", Ch("thmh", H(2)), Theme(1, 3, 2000), Theme(2, 5, 1000)),
                Ch("scns", Ch("scnh", H(1)), Ch("scnd", I(1), H(2), I(7), I(1), I(8), I(2))));
        }

        public static readonly byte[] After = Encoding.ASCII.GetBytes("after the music");

        /// <summary>The project around a comp chunk; <paramref name="odd"/> gives the legacy chunk an odd length.</summary>
        public static byte[] Project(bool odd = false)
        {
            byte[] lgcy = Cat(I(0x0b1750), new byte[odd ? 3 : 4], Comp());
            byte[] proj = Cat(A("PROJ"), Riff("OBCT", new byte[12]), Riff("PROP", A("MS2Sound\0")), Riff("LGCY", lgcy), Riff("EPRP", After));
            byte[] body = Cat(A("FEV "), Riff("FMT ", I(0x450000)), Riff("LIST", proj));
            return Cat(A("RIFF"), I(body.Length), body);
        }
    }

    [TestFixture]
    public class MusicGraphTests
    {
        [Test]
        public void Read_FollowsCueThemeSegmentAndLink()
        {
            MusicGraph g = MusicGraph.Read(FakeMusic.Project(), "MS2Sound.fev");
            Assert.That(g.Cues[7], Is.EqualTo("Title"));
            List<MusicSegment> p = g.Play(7);
            Assert.That(p.Select(s => s.Id), Is.EqualTo(new[] { 3, 4 }), "the intro, then the loop (once: it repeats)");
            Assert.That(p.Select(s => s.Samples.Single().ToString()), Is.EqualTo(new[] { "MS2Sound_Fake #1", "MS2Sound_Fake #0" }));
            Assert.That(p[1].Next, Is.EqualTo(new[] { 4 }));
            Assert.That(p[0].Seconds, Is.EqualTo(5f));
            Assert.That(g.Play(8).Single().Samples.Single().Bank, Is.EqualTo("MS2Sound_Other"));
            Assert.That(g.Play(99), Is.Empty);
        }

        [Test]
        public void Bytes_WithNothingAdded_AreTheSameBytes()
        {
            foreach (bool odd in new[] { false, true })
            {
                byte[] d = FakeMusic.Project(odd);
                Assert.That(MusicGraph.Read(d, "x").Bytes(), Is.EqualTo(d));
                Assert.That(Walk(d).Last(), Does.StartWith("EPRP="));
            }
        }

        [TestCase(false, null)]
        [TestCase(true, null)]
        [TestCase(false, 9)]
        [TestCase(true, 9)]
        public void AddCue_WritesAProjectThatReadsBackWithTheNewCue(bool odd, int? introIndex)
        {
            byte[] d = FakeMusic.Project(odd);
            MusicGraph g = MusicGraph.Read(d, "x");
            MusicSample intro = introIndex == null ? null : new MusicSample { Bank = "MS2Sound_Fake", Index = introIndex.Value };
            int cue = g.AddCue("My Tune", intro, new MusicSample { Bank = "MS2Sound_Fake", Index = 2 }, 4.5f, 33f, 7);
            Assert.That(cue, Is.EqualTo(9));
            byte[] b = g.Bytes();

            MusicGraph r = MusicGraph.Read(b, "x");
            Assert.That(r.Cues[9], Is.EqualTo("My Tune"));
            Assert.That(r.Cues[7], Is.EqualTo("Title"));
            Assert.That(r.Cues[8], Is.EqualTo("Field"));
            List<MusicSegment> p = r.Play(9);
            Assert.That(p.Select(s => s.Samples.Single().Index), Is.EqualTo(introIndex == null ? new[] { 2 } : new[] { 9, 2 }));
            Assert.That(p.Last().Next, Is.EqualTo(new[] { p.Last().Id }), "the last segment loops");
            Assert.That(p.Last().Seconds, Is.EqualTo(33f));
            Assert.That(p.Last().Bpm, Is.EqualTo(120f), "settings from the model cue's loop");
            Assert.That(p.All(s => s.Id > 5 && s.Theme == 3), Is.True, "new ids, past the highest");
            Assert.That(r.Play(7).Select(s => s.Id), Is.EqualTo(new[] { 3, 4 }), "the other cues play as before");
            Assert.That(r.Play(8).Select(s => s.Id), Is.EqualTo(new[] { 5 }));

            // the existing parser reads the new list too; the RIFF around it agrees, the chunk after it is kept
            SoundProject sp = SoundProject.Read(b, "x");
            Assert.That(sp.Cue(9).Name, Is.EqualTo("My Tune"));
            Assert.That(BitConverter.ToUInt32(b, 4), Is.EqualTo(b.Length - 8));
            Assert.That(Walk(b), Is.EqualTo(new[] { "FMT ", "LIST", "OBCT", "PROP", "LGCY", "EPRP=" + Encoding.ASCII.GetString(FakeMusic.After) }), "every RIFF chunk where it should be, pad bytes too");
            Assert.That(MusicGraph.Read(b, "x").Bytes(), Is.EqualTo(b));
            Assert.That(g.Bytes(), Is.EqualTo(b), "the graph reads itself back after the add");
        }

        [Test]
        public void AddCue_RefusesWhatItCannotWrite()
        {
            MusicGraph g = MusicGraph.Read(FakeMusic.Project(), "x");
            var s = new MusicSample { Bank = "MS2Sound_Fake", Index = 0 };
            Assert.That(() => g.AddCue("Title", null, s, 0, 1, 7), Throws.TypeOf<ModKitException>().With.Message.Contains("named Title already"));
            Assert.That(() => g.AddCue("", null, s, 0, 1, 7), Throws.TypeOf<ModKitException>());
            Assert.That(() => g.AddCue("New", null, null, 0, 1, 7), Throws.TypeOf<ModKitException>());
            Assert.That(() => g.AddCue("New", null, s, 0, 1, 99), Throws.TypeOf<ModKitException>().With.Message.Contains("cue 99 plays nothing"));
        }

        [Test]
        public void Read_RefusesAMusicChunkThatIsNotTheEndOfItsChunk()
        {
            byte[] d = FakeMusic.Project();
            Assert.That(() => MusicGraph.Read(d.Take(d.Length - 1).ToArray(), "x"), Throws.TypeOf<ModKitException>());
            // a comp whose length is short by 4: its chunks no longer fit
            byte[] c = FakeMusic.Comp();
            int at = IndexOf(d, c);
            byte[] e = (byte[])d.Clone();
            Buffer.BlockCopy(BitConverter.GetBytes(c.Length - 4), 0, e, at, 4);
            Assert.That(() => MusicGraph.Read(e, "x"), Throws.TypeOf<ModKitException>());
        }

        /// <summary>The RIFF chunks, PROJ's too, walked with their pad bytes; the last one with its text.</summary>
        static List<string> Walk(byte[] d)
        {
            var l = new List<string>();
            Walk(d, 12, d.Length, l);
            return l;
        }

        static void Walk(byte[] d, int at, int end, List<string> l)
        {
            while (at < end)
            {
                Assert.That(at + 8, Is.LessThanOrEqualTo(end));
                string t = Encoding.ASCII.GetString(d, at, 4);
                int n = BitConverter.ToInt32(d, at + 4);
                Assert.That(at + 8 + n, Is.LessThanOrEqualTo(end), t);
                l.Add(t == "EPRP" ? t + "=" + Encoding.ASCII.GetString(d, at + 8, n) : t);
                if (t == "LIST") Walk(d, at + 12, at + 8 + n, l);
                at += 8 + n + (n & 1);
            }
            Assert.That(at, Is.EqualTo(end));
        }

        static int IndexOf(byte[] d, byte[] n)
        {
            for (int i = 0; i + n.Length <= d.Length; i++) { int k = 0; while (k < n.Length && d[i + k] == n[k]) k++; if (k == n.Length) return i; }
            return -1;
        }

        [Test]
        public void RealProject_ReadsAndWritesTheSameBytes()
        {
            string dir = Environment.GetEnvironmentVariable(SoundData.Override);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) Assert.Ignore(SoundData.Override + " is not set");
            string path = new SoundData(dir).ProjectPath;
            byte[] d = File.ReadAllBytes(path);
            MusicGraph g = MusicGraph.Read(d, path);
            Assert.That(g.Bytes(), Is.EqualTo(d));
            SoundProject sp = SoundProject.Read(d, path);
            Assert.That(g.Cues.Count, Is.EqualTo(sp.Cues.Count));
            int any = g.Cues.Keys.Count(c => g.Play(c).Count > 0);
            Assert.That(any, Is.EqualTo(g.Cues.Count), "every cue plays a segment");
            int cue = g.AddCue("ModKit Test", null, g.Play(g.Cues.Keys.First()).Last().Samples[0], 0, 10, g.Cues.Keys.First());
            Assert.That(SoundProject.Read(g.Bytes(), path).Cue(cue), Is.Not.Null);
        }
    }
}
