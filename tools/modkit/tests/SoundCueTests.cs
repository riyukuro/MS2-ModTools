using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MS2.ModKit;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    public class SoundCueTests
    {
        // ---- the bank writer: new samples ----

        static FakeSound.S[] Three()
        {
            return new[]
            {
                new FakeSound.S { Name = "Tune_A", Frames = 44100 * 2, Stereo = true, Loop = true, Bytes = 1000 },
                new FakeSound.S { Name = "Tune_B", Frames = 48000, RateIndex = 9, Stereo = true, Loop = true, Bytes = 700 },
                new FakeSound.S { Name = "Click", Frames = 2205, RateIndex = 5, Rate = 22050, Bytes = 40 },
            };
        }

        // a bank whose data are not zeros, so a moved blob shows
        static byte[] Bank(params FakeSound.S[] samples)
        {
            byte[] b = FakeSound.Bank(samples);
            int shs = BitConverter.ToInt32(b, 12), nts = BitConverter.ToInt32(b, 16);
            int at = SoundBank.HeaderSize + shs + nts;
            for (int i = at; i < b.Length; i++) b[i] = (byte)((i - at) % 251 + 1);
            return b;
        }

        static byte[] Write(byte[] bank, Dictionary<int, MpegAudio> rep, params NewSample[] add)
        {
            var o = new MemoryStream();
            SoundReplace.Write(new MemoryStream(bank), o, rep ?? new Dictionary<int, MpegAudio>(), add, "fake.fsb", null);
            return o.ToArray();
        }

        static byte[] DataOf(byte[] bank, SoundSample s)
        {
            int shs = BitConverter.ToInt32(bank, 12), nts = BitConverter.ToInt32(bank, 16);
            return bank.Skip(SoundBank.HeaderSize + shs + nts + (int)s.Offset).Take((int)s.Size).ToArray();
        }

        static MpegAudio Mp2(int frames, bool mono = false, byte fill = 0x55) { return MpegAudio.Parse(FakeMpeg.Frames(frames, mono, fill, layer: 2), "x.mp2"); }

        [Test]
        public void Write_AddsSamplesAfterTheShippedOnes()
        {
            byte[] b = Bank(Three());
            SoundBank was = SoundBank.Read(new MemoryStream(b), "fake.fsb");
            MpegAudio intro = Mp2(3, mono: true, fill: 0x21), loop = Mp2(5, fill: 0x42), rep = Mp2(2, fill: 0x63);
            byte[] o = Write(b, new Dictionary<int, MpegAudio> { { 1, rep } }, new NewSample { Name = "New_Intro", Audio = intro }, new NewSample { Name = "New_Loop", Audio = loop, Loop = true });
            SoundBank now = SoundBank.Read(new MemoryStream(o), "fake.fsb");

            Assert.That(now.Samples.Select(s => s.Name), Is.EqualTo(new[] { "Tune_A", "Tune_B", "Click", "New_Intro", "New_Loop" }));
            Assert.That(now.HashText, Is.EqualTo(was.HashText), "the hash stays, so the project still takes the bank");
            int shs = BitConverter.ToInt32(o, 12), nts = BitConverter.ToInt32(o, 16);
            Assert.That((SoundBank.HeaderSize + shs + nts) % 32, Is.EqualTo(0), "the data start on a 32-byte boundary, as in the shipped banks");
            Assert.That(BitConverter.ToInt32(o, 20), Is.EqualTo(o.Length - SoundBank.HeaderSize - shs - nts), "the data size");
            Assert.That(DataOf(o, now.Samples[0]), Is.EqualTo(DataOf(b, was.Samples[0])));
            Assert.That(DataOf(o, now.Samples[2]), Is.EqualTo(DataOf(b, was.Samples[2])));
            Assert.That(DataOf(o, now.Samples[1]).Take(rep.Data.Length), Is.EqualTo(rep.Data), "a replace and an add in one write");
            foreach (int k in new[] { 3, 4 }) Assert.That(now.Samples[k].Offset % 32, Is.EqualTo(0));

            SoundSample i = now.Samples[3], l = now.Samples[4];
            Assert.That(DataOf(o, i).Take(intro.Data.Length), Is.EqualTo(intro.Data));
            Assert.That(DataOf(o, l).Take(loop.Data.Length), Is.EqualTo(loop.Data));
            Assert.That(DataOf(o, l).Skip(loop.Data.Length), Is.All.EqualTo((byte)0), "zeros up to the 32-byte boundary");
            Assert.That(new object[] { i.Channels, i.Rate, i.Frames, i.Loops }, Is.EqualTo(new object[] { 1, 44100, 3L * 1152, false }));
            Assert.That(new object[] { l.Channels, l.Rate, l.Frames, l.Loops }, Is.EqualTo(new object[] { 2, 44100, 5L * 1152, true }));
            Assert.That(l.Chunks, Is.EqualTo(new[] { 3 }), "the loop chunk only, as the shipped loops");
            int lh = SoundBank.HeaderSize + shs - 16;
            Assert.That(BitConverter.ToUInt32(o, lh + 8), Is.EqualTo(0u), "the loop starts at 0");
            Assert.That(BitConverter.ToUInt32(o, lh + 12), Is.EqualTo((uint)(5 * 1152 - 1)), "and ends at the last frame");

            // the shipped names and offsets did not move in the name table: read back as above; once more on top
            byte[] again = Write(o, null, new NewSample { Name = "More", Audio = loop });
            Assert.That(SoundBank.Read(new MemoryStream(again), "x").Samples.Select(s => s.Name).Last(), Is.EqualTo("More"));
            Assert.That(Write(b, null), Is.EqualTo(b), "no add, no replace: the same bytes");
        }

        [Test]
        public void Write_RefusesANameTheBankHasOrNoName()
        {
            byte[] b = Bank(Three());
            MpegAudio a = Mp2(2);
            Assert.That(() => Write(b, null, new NewSample { Name = "Click", Audio = a }), Throws.TypeOf<ModKitException>().With.Message.Contains("has a sample Click already"));
            Assert.That(() => Write(b, null, new NewSample { Name = "X", Audio = a }, new NewSample { Name = "X", Audio = a }), Throws.TypeOf<ModKitException>().With.Message.Contains("has a sample X already"));
            Assert.That(() => Write(b, null, new NewSample { Name = "", Audio = a }), Throws.TypeOf<ModKitException>().With.Message.Contains("needs a name"));
            Assert.That(() => Write(b, null, new NewSample { Name = "A\0B", Audio = a }), Throws.TypeOf<ModKitException>().With.Message.Contains("needs a name"));

            // a bank without a name table
            byte[] nn = (byte[])b.Clone();
            int shs = BitConverter.ToInt32(b, 12), nts = BitConverter.ToInt32(b, 16);
            nn = nn.Take(SoundBank.HeaderSize + shs).Concat(b.Skip(SoundBank.HeaderSize + shs + nts)).ToArray();
            Array.Copy(BitConverter.GetBytes(0), 0, nn, 16, 4);
            Assert.That(SoundBank.Read(new MemoryStream(nn), "x").Samples.Count, Is.EqualTo(3));
            Assert.That(() => Write(nn, null, new NewSample { Name = "X", Audio = a }), Throws.TypeOf<ModKitException>().With.Message.Contains("no name table"));
            Assert.That(Write(nn, null), Is.EqualTo(nn), "without an add it still writes");

            // a name offset outside the table
            byte[] bad = (byte[])b.Clone();
            Array.Copy(BitConverter.GetBytes(nts + 5), 0, bad, SoundBank.HeaderSize + shs + 4, 4);
            Assert.That(() => Write(bad, null, new NewSample { Name = "X", Audio = a }), Throws.TypeOf<ModKitException>().With.Message.Contains("outside the name table"));
        }

        // ---- the mod line ----

        static string Cue(int id, string name, string intro = "files/i.mp2", string loop = "files/l.mp2", int like = 7, string bank = "MS2Sound_Fake.fsb")
        {
            return "<cue id=\"" + id + "\" name=\"" + name + "\" bank=\"" + bank + "\" like=\"" + like + "\"" + (intro == null ? "" : " intro=\"" + intro + "\"") + " loop=\"" + loop + "\" />";
        }

        [Test]
        public void Manifest_ReadsAndWritesTheCueLine()
        {
            using (var f = new Fake())
            {
                Mod m = f.Mod("a", Cue(20, "New_Tune") + Cue(21, "Plain", intro: null, like: 8));
                Assert.That(m.Cues.Select(c => c.Id + " " + c.Name + " " + c.Bank + " " + c.Like + " " + (c.Intro ?? "-") + " " + c.Loop),
                    Is.EqualTo(new[] { "20 New_Tune MS2Sound_Fake.fsb 7 files/i.mp2 files/l.mp2", "21 Plain MS2Sound_Fake.fsb 8 - files/l.mp2" }));
                Assert.That(m.IsEmpty, Is.False);
                string d = Path.Combine(f.Dir, "saved");
                m.Save(d);
                Mod r = Mod.Load(d);
                Assert.That(r.Cues.Select(c => c.Id + " " + c.Name + " " + c.Like + " " + (c.Intro ?? "-")), Is.EqualTo(new[] { "20 New_Tune 7 files/i.mp2", "21 Plain 8 -" }));
                Assert.That(File.ReadAllText(Path.Combine(d, "mod.xml")), Does.Contain("<cue id=\"21\" name=\"Plain\" bank=\"MS2Sound_Fake.fsb\" like=\"8\" loop=\"files/l.mp2\" />"));

                foreach (var bad in new[]
                {
                    new[] { Cue(0, "X"), "a cue id is 1 to" },
                    new[] { Cue(5, " X"), "spaces at its ends" },
                    new[] { Cue(5, "X", bank: "a/b.fsb"), "bank=" },
                    new[] { Cue(5, "X", intro: ""), "leave the attribute out" },
                    new[] { "<cue id=\"5\" name=\"X\" bank=\"MS2Sound_Fake.fsb\" like=\"7\" />", "loop" },
                    new[] { "<cue id=\"5\" name=\"X\" bank=\"MS2Sound_Fake.fsb\" like=\"7\" loop=\"l\" volume=\"2\" />", "volume" },
                })
                    Assert.That(() => f.Mod("b" + bad[1].Length, bad[0]), Throws.TypeOf<ModKitException>().With.Message.Contains(bad[1]), bad[0]);
            }
        }

        [Test]
        public void Editor_SetAndTakeACue_KeepsTheFiles()
        {
            using (var f = new Fake())
            {
                Mod m = f.Mod("a", "");
                byte[] i = FakeMpeg.Frames(3, layer: 2), l = FakeMpeg.Frames(4, layer: 2);
                CueOp c = m.SetCue(30, "Mine", "MS2Sound_Fake.fsb", 7, i, l, "i.mp2", "l.mp2");
                Assert.That(new[] { c.Intro, c.Loop }, Is.EqualTo(new[] { "files/sound/cue/30_intro.mp2", "files/sound/cue/30_loop.mp2" }));
                Assert.That(m.Files[c.Loop], Is.EqualTo(l));
                string d = Path.Combine(f.Dir, "saved");
                m.Save(d);
                Assert.That(File.ReadAllBytes(Path.Combine(d, "files", "sound", "cue", "30_intro.mp2")), Is.EqualTo(i));

                // again without an intro: the intro file goes
                m.SetCue(30, "Mine", "MS2Sound_Fake.fsb", 7, null, l, null, "l.mp2");
                Assert.That(m.Cues.Single().Intro, Is.Null);
                Assert.That(m.Files.Keys, Is.EqualTo(new[] { "files/sound/cue/30_loop.mp2" }));
                m.Save(d);
                Assert.That(File.Exists(Path.Combine(d, "files", "sound", "cue", "30_intro.mp2")), Is.False, "the orphan goes at save");

                Assert.That(() => m.SetCue(31, "Mine", "MS2Sound_Fake.fsb", 7, null, l, null, "l.mp2"), Throws.TypeOf<ModKitException>().With.Message.Contains("named Mine"));
                Assert.That(() => m.SetCue(31, "Two", "MS2Sound_Fake.fsb", 7, null, Encoding.ASCII.GetBytes("no mpeg at all"), null, "l.txt"), Throws.TypeOf<ModKitException>());
                Assert.That(() => m.SetCue(31, "Two", "MS2Sound_Fake.fsb", 7, null, FakeMpeg.Frames(4, layer: 3), null, "l.mp3"), Throws.TypeOf<ModKitException>().With.Message.Contains("l.mp3"), "layer III is refused as for a replace");
                Assert.That(m.Cues.Count, Is.EqualTo(1), "a refused cue changes nothing");
                Assert.That(m.TakeCue(30), Is.True);
                Assert.That(m.TakeCue(30), Is.False);
                Assert.That(m.Files, Is.Empty);
                Assert.That(m.IsEmpty, Is.True);
            }
        }

        // ---- the plan ----

        sealed class World : IDisposable
        {
            public readonly Fake F = new Fake();
            public readonly byte[] BankBytes = Bank(Three()), Other = Bank(new FakeSound.S { Name = "O", Frames = 1152, Bytes = 64 }), ProjectBytes;
            public string Sound { get { return Path.Combine(F.ClientDir, "Sound"); } }

            public World()
            {
                Assume.That(Environment.GetEnvironmentVariable(SoundData.Override), Is.Null.Or.Empty, SoundData.Override + " points the plan at another Sound folder");
                Directory.CreateDirectory(Sound);
                // the music project with the banks' hash in it (both fake banks have the one hash)
                ProjectBytes = FakeMusic.Project();
                int at = IndexOf(ProjectBytes, Encoding.ASCII.GetBytes("OBCT")) + 8;
                Array.Copy(FakeSound.Hash, 0, ProjectBytes, at, FakeSound.Hash.Length);
                File.WriteAllBytes(Path.Combine(Sound, SoundData.ProjectFile), ProjectBytes);
                File.WriteAllBytes(Path.Combine(Sound, "MS2Sound_Fake.fsb"), BankBytes);
                File.WriteAllBytes(Path.Combine(Sound, "MS2Sound_Other.fsb"), Other);
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

            public Mod Plain(string id, string lines) { return Mod(id, lines, Mp("files/i.mp2", 3), Mp("files/l.mp2", 5)); }

            public void Dispose() { F.Dispose(); }
        }

        static int IndexOf(byte[] d, byte[] n)
        {
            for (int i = 0; i + n.Length <= d.Length; i++) if (d.Skip(i).Take(n.Length).SequenceEqual(n)) return i;
            return -1;
        }

        static KeyValuePair<string, byte[]> Mp(string file, int frames, int layer = 2) { return new KeyValuePair<string, byte[]>(file, FakeMpeg.Frames(frames, layer: layer)); }

        [Test]
        public void Plan_AddsTheSamplesAndTheCue()
        {
            using (var w = new World())
            {
                Mod m = w.Plain("a", Cue(20, "New_Tune") + Cue(21, "Plain", intro: null, like: 8));
                Plan p = w.F.Plan(m);
                Assert.That(p.Errors, Is.Empty, EditorSession.Report(p));
                Assert.That(p.Banks.Select(b => b.Ref), Is.EqualTo(new[] { "client:Sound/MS2Sound_Fake.fsb", "client:Sound/MS2Sound.fev" }), "the banks first, then the project");
                PlanBank bank = p.Banks[0], proj = p.Banks[1];
                Assert.That(bank.Adds.Select(s => s.Index + " " + s.Name + " " + s.Loop), Is.EqualTo(new[] { "3 New_Tune_Intro True", "4 New_Tune_Loop True", "5 Plain_Loop True" }),
                    "after the bank's last; the loop chunks as the model segments' samples have them (Tune_B, Tune_A, the other bank's O for cue 8 cannot be read past its 1 sample: the default)");
                Assert.That(proj.Project, Is.True);
                Assert.That(proj.SourceSha1, Is.EqualTo(LooseTarget.Sha1Of(w.ProjectBytes)));
                Assert.That(proj.Cues.Select(c => c.Id + " " + c.Bank + " " + c.Intro + " " + c.Loop), Is.EqualTo(new[] { "20 MS2Sound_Fake 3 4", "21 MS2Sound_Fake -1 5" }));
                Assert.That(proj.Cues[0].LoopSeconds, Is.EqualTo(5 * 1152 / 44100f).Within(1e-4));
                Assert.That(p.Changes.Select(c => c.Kind + " " + c.Entry), Is.EqualTo(new[]
                {
                    "AddSample sample 3 New_Tune_Intro", "AddSample sample 4 New_Tune_Loop", "AddCue cue 20 New_Tune",
                    "AddSample sample 5 Plain_Loop", "AddCue cue 21 Plain",
                }));
                Assert.That(p.Changes[2].ToString(), Does.Contain("MS2Sound_Fake #3 once, then MS2Sound_Fake #4 looped; settings of cue 7 Title"));
                Assert.That(p.Notes.Select(n => n.Text), Has.Some.Contains("bgm id is 20"));
                Assert.That(string.Join("\n", Targets.PlanLines(p, w.F.Data)), Does.Contain("kms-client: 5 change(s) in 3 sample(s) of 1 bank(s) and 2 new cue(s)"));
                Assert.That(Targets.Where(0, 0, 3, 1, 2), Is.EqualTo("3 sample(s) of 1 bank(s) and 2 new cue(s)"));
            }
        }

        [Test]
        public void Plan_StopsAtACueItCannotAdd()
        {
            using (var w = new World())
            {
                var cases = new[]
                {
                    new[] { Cue(7, "X"), "the project has a cue 7 (Title)" },
                    new[] { Cue(20, "Field"), "the project has a cue named Field" },
                    new[] { Cue(20, "X", like: 99), "the project has no cue 99" },
                    new[] { Cue(20, "X", bank: "MS2Sound_None.fsb"), "no bank MS2Sound_None.fsb" },
                    new[] { Cue(20, "X") + Cue(20, "Y"), "adds cue 20 twice" },
                    new[] { Cue(20, "X") + Cue(21, "X"), "two cues named X" },
                    new[] { Cue(20, "Tune", intro: null), null },
                    new[] { Cue(20, "Click", intro: "files/i.mp2", loop: "files/l.mp2").Replace("name=\"Click\"", "name=\"Tune_A\""), null },
                    new[] { Cue(20, "X", loop: "files/none.mp2"), "is not there" },
                    new[] { Cue(20, "X", loop: "files/l3.mp2"), "layer III" },
                };
                int k = 0;
                foreach (string[] c in cases)
                {
                    if (c[1] == null) continue;
                    Mod m = w.Mod("m" + k++, c[0], Mp("files/i.mp2", 3), Mp("files/l.mp2", 5), Mp("files/l3.mp2", 5, layer: 3));
                    Plan p = w.F.Plan(m);
                    Assert.That(p.Errors.Select(e => e.Text), Has.Some.Contains(c[1]), c[0] + "\n" + EditorSession.Report(p));
                    Assert.That(p.Banks.Where(b => b.Project).SelectMany(b => b.Cues).Count(), Is.LessThan(2), c[0]);
                }

                // a sample name the bank has: Tune_A for cue "Tune" + "_A" cannot be made, so a bank with a sample
                // named like the new one is made up here
                byte[] nb = Bank(new FakeSound.S { Name = "Tune_A", Frames = 44100 * 2, Stereo = true, Loop = true, Bytes = 1000 },
                    new FakeSound.S { Name = "Tune_B", Frames = 48000, RateIndex = 9, Stereo = true, Loop = true, Bytes = 700 },
                    new FakeSound.S { Name = "Mine_Loop", Frames = 2205, Bytes = 40 });
                File.WriteAllBytes(Path.Combine(w.Sound, "MS2Sound_Fake.fsb"), nb);
                Plan q = w.F.Plan(w.Plain("n", Cue(20, "Mine")));
                Assert.That(q.Errors.Select(e => e.Text), Has.Some.Contains("has a sample named Mine_Loop already"), EditorSession.Report(q));
            }
        }

        [Test]
        public void Plan_TwoModsOnOneCue_TheLaterOneAddsIt()
        {
            using (var w = new World())
            {
                Mod a = w.Plain("a", Cue(20, "From_A")), b = w.Plain("b", Cue(20, "From_B")), c = w.Plain("c", Cue(22, "From_B"));
                Plan p = w.F.Plan(a, b);
                Assert.That(p.Conflicts.Select(x => x.Text), Has.Some.Contains("both add a cue 20"), EditorSession.Report(p));
                Assert.That(p.Banks.Single(x => x.Project).Cues.Single().Name, Is.EqualTo("From_B"));
                Assert.That(p.Banks.Single(x => !x.Project).Adds.Select(x => x.ModId).Distinct(), Is.EqualTo(new[] { "b" }), "the loser's samples are not added");
                p = w.F.Plan(b, c);
                Assert.That(p.Conflicts.Select(x => x.Text), Has.Some.Contains("both add a cue named From_B"), EditorSession.Report(p));
                Assert.That(p.Banks.Single(x => x.Project).Cues.Single().Id, Is.EqualTo(22));
            }
        }

        // ---- apply ----

        static void PlaysTheNewCue(string fev, string fsb, int cue, int frames)
        {
            MusicGraph g = MusicGraph.Read(File.ReadAllBytes(fev), "fev");
            SoundBank b = SoundBank.Read(fsb);
            List<MusicSegment> play = g.Play(cue);
            Assert.That(play.Select(s => s.Samples.Single().Bank), Is.All.EqualTo("MS2Sound_Fake"));
            SoundSample loop = b.Samples[play.Last().Samples.Single().Index];
            Assert.That(loop.Frames, Is.EqualTo(frames * 1152L), "the cue's loop is the mod's sound");
            Assert.That(play.Last().Next, Is.EqualTo(new[] { play.Last().Id }));
            Assert.That(SoundProject.Read(File.ReadAllBytes(fev), "fev").HasHash(b.Hash), Is.True);
        }

        [Test]
        public void Workspace_WritesTheBankAndTheProject()
        {
            using (var w = new World())
            {
                Mod m = w.Plain("a", Cue(20, "New_Tune"));
                var ws = new Workspace(Path.Combine(w.F.Dir, "ws"));
                ApplyResult r = ws.Apply(w.F.Data, new[] { m }, null);
                Assert.That(r.Applied, Is.True, EditorSession.Report(r));
                string fev = Path.Combine(ws.Dir, "client", "Sound", SoundData.ProjectFile), fsb = Path.Combine(ws.Dir, "client", "Sound", "MS2Sound_Fake.fsb");
                Assert.That(r.Outputs.Select(o => o.Path + " " + o.Added), Is.EqualTo(new[] { fsb + " 2", fev + " 1" }));
                PlaysTheNewCue(fev, fsb, 20, 5);
                Assert.That(File.ReadAllBytes(Path.Combine(w.Sound, SoundData.ProjectFile)), Is.EqualTo(w.ProjectBytes), "the shipped files are only read");
                Assert.That(File.ReadAllBytes(Path.Combine(w.Sound, "MS2Sound_Fake.fsb")), Is.EqualTo(w.BankBytes));
                ws.Revert(w.F.Data, null, null);
                Assert.That(File.Exists(fev) || File.Exists(fsb), Is.False);
            }
        }

        static string Game(World w)
        {
            string g = Path.Combine(w.F.Dir, "game"), s = Path.Combine(g, "Data", "Sound");
            Directory.CreateDirectory(s);
            File.WriteAllBytes(Path.Combine(s, "MS2Sound_Fake.fsb"), w.BankBytes);
            File.WriteAllBytes(Path.Combine(s, SoundData.ProjectFile), w.ProjectBytes);
            return g;
        }

        [Test]
        public void Loose_AddsTheCue_AppliesAgain_AndPutsTheShippedFilesBack()
        {
            using (var w = new World())
            {
                string g = Game(w), s = Path.Combine(g, "Data", "Sound"), fev = Path.Combine(s, SoundData.ProjectFile), fsb = Path.Combine(s, "MS2Sound_Fake.fsb");
                var lt = new LooseTarget(g);
                Mod a = w.Plain("a", Cue(20, "New_Tune"));
                ApplyResult r = lt.Apply(w.F.Data, new[] { a }, null);
                Assert.That(r.Applied, Is.True, EditorSession.Report(r));
                Assert.That(r.Written, Is.EquivalentTo(new[] { fev, fsb }));
                PlaysTheNewCue(fev, fsb, 20, 5);
                Assert.That(File.ReadAllBytes(fev + SoundReplace.OrigSuffix), Is.EqualTo(w.ProjectBytes));
                Assert.That(File.ReadAllBytes(fsb + SoundReplace.OrigSuffix), Is.EqualTo(w.BankBytes));
                Assert.That(lt.ReadState().Banks.Select(x => x.Path), Is.EquivalentTo(new[] { "Data/Sound/MS2Sound.fev", "Data/Sound/MS2Sound_Fake.fsb" }));

                // a plan of this game folder takes the files put aside for the shipped ones, though the bank in place
                // has more samples and the project another cue
                Assert.That(SoundReplace.ShippedPath(s, "MS2Sound_Fake.fsb"), Is.EqualTo(fsb + SoundReplace.OrigSuffix));
                Assert.That(SoundReplace.ShippedPath(s, SoundData.ProjectFile), Is.EqualTo(fev + SoundReplace.OrigSuffix));

                // a second apply builds from the shipped files again
                r = lt.Apply(w.F.Data, new[] { w.Plain("b2", Cue(21, "Other_Tune", intro: null)) }, null);
                Assert.That(r.Applied, Is.True, EditorSession.Report(r));
                PlaysTheNewCue(fev, fsb, 21, 5);
                Assert.That(MusicGraph.Read(File.ReadAllBytes(fev), "x").Cues.ContainsKey(20), Is.False, "a's cue is gone with a");

                // a game patch put a new project or bank in place: not taken
                byte[] mine = File.ReadAllBytes(fev), mineBank = File.ReadAllBytes(fsb);
                File.WriteAllBytes(fev, FakeMusic.Project(odd: true));
                Assert.That(() => SoundReplace.ShippedPath(s, SoundData.ProjectFile), Throws.TypeOf<ModKitException>().With.Message.Contains("is not the file"));
                File.WriteAllBytes(fev, mine);
                File.WriteAllBytes(fsb, Bank(Three().Concat(new[] { new FakeSound.S { Name = "Patch", Frames = 1152, Bytes = 64 } }).ToArray()));
                Assert.That(() => SoundReplace.ShippedPath(s, "MS2Sound_Fake.fsb"), Throws.TypeOf<ModKitException>().With.Message.Contains("not the bank"), "more samples, but not the bank this tool wrote");
                File.WriteAllBytes(fsb, mineBank);

                ApplyResult rv = lt.Revert();
                Assert.That(File.ReadAllBytes(fsb), Is.EqualTo(w.BankBytes));
                Assert.That(File.ReadAllBytes(fev), Is.EqualTo(w.ProjectBytes));
                Assert.That(File.Exists(fsb + SoundReplace.OrigSuffix) || File.Exists(fev + SoundReplace.OrigSuffix), Is.False);
            }
        }
    }
}
