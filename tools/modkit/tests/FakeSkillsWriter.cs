using System;
using System.IO;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    /// <summary>
    /// Writes the made-up skill data (FakeSkills) to the folder in MS2_FAKE_SKILLS_OUT (kms, gms, server), for trying
    /// the Skills view by hand; MS2_FAKE_SKILLS_ICON may name a png used as the icon. Runs only when asked for by name.
    /// </summary>
    [TestFixture, Explicit]
    public class FakeSkillsWriter
    {
        [Test]
        public void WriteFakeSkills()
        {
            string to = Environment.GetEnvironmentVariable("MS2_FAKE_SKILLS_OUT");
            if (string.IsNullOrEmpty(to)) Assert.Ignore("MS2_FAKE_SKILLS_OUT is not set");
            string icon = Environment.GetEnvironmentVariable("MS2_FAKE_SKILLS_ICON");
            byte[] bytes = string.IsNullOrEmpty(icon) ? new byte[] { 1, 2, 3 } : File.ReadAllBytes(icon);
            using (var f = new FakeSkills(bytes))
                foreach (string side in new[] { "kms", "gms", "server" })
                {
                    string src = Path.Combine(f.Dir, side), dst = Path.Combine(to, side);
                    foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                    {
                        string rel = file.Substring(src.Length + 1);
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dst, rel)));
                        File.Copy(file, Path.Combine(dst, rel), true);
                    }
                }
        }
    }
}
