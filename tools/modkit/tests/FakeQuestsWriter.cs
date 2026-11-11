using System;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    /// <summary>
    /// Writes the made-up quest data (FakeQuests) to the folder in MS2_FAKE_QUESTS_OUT (kms, gms, server), for trying
    /// the Quests view by hand. Runs only when asked for by name.
    /// </summary>
    [TestFixture, Explicit]
    public class FakeQuestsWriter
    {
        [Test]
        public void WriteFakeQuests()
        {
            string to = Environment.GetEnvironmentVariable("MS2_FAKE_QUESTS_OUT");
            if (string.IsNullOrEmpty(to)) Assert.Ignore("MS2_FAKE_QUESTS_OUT is not set");
            using (new FakeQuests(true, true, true, to, true)) { }
        }
    }
}
