// The on-demand model listing the planner uses for map lines: only the files of the models asked for are read.
using System;
using System.Collections.Generic;
using System.Text;
using MS2.ModKit.MapEdit;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class FlatIndexOnDemandTests
    {
        static string Flat(string name, int id, string mixin)
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<model name=\"" + name + "\" id=\"" + id + "\">\n"
                + (mixin == null ? "" : "  <mixin name=\"" + mixin + "\" id=\"" + (id + 1) + "\" type=\"\" />\n")
                + "  <property name=\"Size" + id + "\" type=\"Int32\" id=\"" + (id + 2) + "\"><set value=\"" + id + "\" /></property>\n</model>\n";
        }

        static Dictionary<string, byte[]> Files()
        {
            return new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                { "flat/library/made/madebox.flat", Encoding.UTF8.GetBytes(Flat("MadeBox", 7001, "MadeBase")) },
                { "flat/library/made/madebase.flat", Encoding.UTF8.GetBytes(Flat("MadeBase", 7101, null)) },
                { "flat/presets/made/madelamp.flat", Encoding.UTF8.GetBytes(Flat("MadeLamp", 7201, null)) },
                { "flat/presets/made/broken.flat", Encoding.UTF8.GetBytes("<model name=\"Broken\">") },
                { "xblock/99000001.xblock", Encoding.UTF8.GetBytes("made up") },
            };
        }

        [Test]
        public void ReadsOnlyTheFilesOfTheModelsAskedFor()
        {
            Dictionary<string, byte[]> files = Files();
            var reads = new List<string>();
            FlatIndex ix = FlatIndex.OnDemand(files.Keys, delegate (string n) { reads.Add(n); return files[n]; });
            Assert.AreEqual(4, ix.Count);
            Assert.AreEqual(0, reads.Count);
            FlatModel box = ix.Get("MadeBox");
            Assert.IsNotNull(box);
            Assert.AreEqual("MadeBase", box.Mixins[0].Name);
            Assert.IsNotNull(ix.Get("MadeBase"));
            Assert.AreSame(box, ix.Get("MadeBox"));
            Assert.IsNull(ix.Get("NoSuchModel"));
            Assert.IsNull(ix.Get("madebox"), "names are matched as written, as in the full listing");
            Assert.That(reads, Is.EqualTo(new[] { "flat/library/made/madebox.flat", "flat/library/made/madebase.flat" }));
        }

        [Test]
        public void NamesAndErrorsReadTheWholeListingLikeTheFullIndex()
        {
            Dictionary<string, byte[]> files = Files();
            FlatIndex lazy = FlatIndex.OnDemand(files.Keys, delegate (string n) { return files[n]; });
            Assert.IsNotNull(lazy.Get("MadeLamp"));
            var list = new List<KeyValuePair<string, byte[]>>();
            foreach (KeyValuePair<string, byte[]> f in files) if (f.Key.StartsWith("flat/", StringComparison.Ordinal)) list.Add(f);
            FlatIndex full = FlatIndex.FromFlats(list);
            Assert.That(lazy.Names, Is.EqualTo(full.Names));
            Assert.AreEqual(1, lazy.Errors.Count);
            Assert.AreEqual(full.Errors[0].File, lazy.Errors[0].File);
            Assert.AreEqual(full.Count, lazy.Count);
            Assert.AreEqual(7201m, lazy.Get("MadeLamp").Id);
        }
    }
}
