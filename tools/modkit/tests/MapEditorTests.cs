// The entity edits of the Maps view (MapEditor) on the made-up maps of FakeMaps: they become map ops that plan on
// every target, keep the shipped value as the expected old one and come back the same from mod.xml.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MS2.ModKit.MapEdit;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class MapEditorTests
    {
        const string K = FakeMaps.K, G = FakeMaps.G, S = FakeMaps.S;

        static string Text(Plan p, string archive, string entry)
        {
            PlanArchive pa = p.Find(archive);
            if (pa == null) return null;
            foreach (KeyValuePair<string, byte[]> kv in pa.Put) if (kv.Key == entry) return Encoding.UTF8.GetString(kv.Value);
            return null;
        }

        [Test]
        public void TheMapFilesOfAMapAndTheirEntities()
        {
            using (var f = new FakeMaps())
            {
                var files = MapEditor.FilesOf(f.Data, 90000005);
                Assert.That(files.Select(x => x.XBlock).ToArray(), Is.EqualTo(new[] { "90000005_AB", "90000005_CD" }));
                Assert.That(files[1].Feature, Is.EqualTo("MadeUp"));
                var ed = new MapEditor(f.Data, null);
                Assert.That(ed.Exported, Is.EqualTo(K));
                Assert.That(ed.Has("90000001_AB"), Is.True);
                Assert.That(ed.Has("90000009_AB"), Is.False);
                Assert.That(ed.Shipped("90000009_AB"), Is.Null);
                Assert.That(ed.Shipped("90000001_AB").Count, Is.EqualTo(4));
                Assert.That(new MapEditor(f.Data, G).Find("90000001_AB", FakeMaps.Crate1).Find("Position").Items[0].Value, Is.EqualTo("0, 0, 300"));

                // the rows: the stored items, then the model's other properties with their defaults
                var mod = new Mod { Id = "m" };
                XEntity crate = ed.Find("90000001_AB", FakeMaps.Crate2);
                List<MapPropRow> rows = ed.Rows(mod, "90000001_AB", crate);
                Assert.That(rows.Select(r => r.Name + (r.Index == null ? "" : "[" + r.Index + "]") + "=" + (r.Current ?? "-")).ToArray(),
                    Is.EqualTo(new[] { "Position=300, 0, 150", "Tags[0]=a", "Tags[1]=b", "Rotation=-", "IsVisible=-", "Scale=-" }));
                Assert.That(rows.First(r => r.Name == "Scale").Default, Is.EqualTo("1"));
                Assert.That(rows.First(r => r.Name == "Rotation").Type, Is.EqualTo("Point3"));
            }
        }

        [Test]
        public void EditsBecomeOpsThatPlanOnEveryTarget()
        {
            using (var f = new FakeMaps())
            {
                var ed = new MapEditor(f.Data, null);
                Mod mod = f.Mod("edits", "");
                const string X = "90000001_AB";
                XEntity crate1 = ed.Find(X, FakeMaps.Crate1), crate2 = ed.Find(X, FakeMaps.Crate2), spawn = ed.Find(X, FakeMaps.Spawn1);
                MapEditor.Set(mod, X, crate2, "Position", null, "300, 0, 450");
                MapEditor.Set(mod, X, crate2, "Tags", "1", "c");
                MapEditor.Set(mod, X, crate2, "Scale", null, "2");        // not stored: a new property
                MapEditor.Set(mod, X, crate1, "Rotation", null, null);    // unset
                MapEditor.Remove(mod, X, spawn);
                Assert.Throws<ModKitException>(() => MapEditor.Set(mod, X, spawn, "Position", null, "1, 1, 1"), "a removed entity is restored first");
                Assert.Throws<ModKitException>(() => MapEditor.Set(mod, X, crate2, "Tags", null, "x"), "a list takes an index");
                Assert.Throws<ModKitException>(() => MapEditor.Set(mod, X, crate2, "Position", "0", "x"), "a plain value takes none");

                Assert.That(mod.Maps.Count, Is.EqualTo(1));
                MapOp m = mod.Maps[0];
                Assert.That(m.XBlock, Is.EqualTo("90000001_ab"));
                MapEntityOp ch = m.Ops.First(o => o.Id == FakeMaps.Crate2);
                Assert.That(ch.Kind, Is.EqualTo(MapOpKind.Change));
                Assert.That(ch.Position, Is.EqualTo("300, 0, 150"), "the entity is found where it ships");
                Assert.That(ch.Sets.Select(s => s.Name + "/" + (s.Index ?? "") + "/" + (s.Old ?? "-") + "/" + (s.Value ?? "-")).ToArray(),
                    Is.EqualTo(new[] { "Position//300, 0, 150/300, 0, 450", "Tags/1/b/c", "Scale//-/2" }));
                Assert.That(MapEditor.IsRemoved(mod, X, FakeMaps.Spawn1), Is.True);
                Assert.That(MapEditor.IsChanged(mod, X, FakeMaps.Crate1), Is.True);

                // the rows show the mod's values
                List<MapPropRow> rows = ed.Rows(mod, X, crate2);
                MapPropRow pos = rows.First(r => r.Name == "Position");
                Assert.That(pos.Shipped + " -> " + pos.Current + " " + pos.Edited, Is.EqualTo("300, 0, 150 -> 300, 0, 450 True"));
                Assert.That(rows.First(r => r.Name == "Scale").Edited, Is.True);

                Plan p = f.Plan(mod);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                foreach (string ar in new[] { K, G, S })
                {
                    string t = Text(p, ar, FakeMaps.EntryA);
                    Assert.That(t, Does.Contain("300, 0, 450"), ar);
                    Assert.That(t, Does.Contain("value=\"c\""), ar);
                    Assert.That(t, Does.Not.Contain(FakeMaps.Spawn1), ar);
                }
                // the global client's first crate stands elsewhere: that change is skipped there, with a warning
                string[] w = p.Warnings.Select(x => x.ToString()).ToArray();
                Assert.That(w.Count(x => x.Contains("stands at \"0, 0, 300\" here, the mod expects \"0, 0, 150\"") && x.Contains("gms-client")), Is.EqualTo(1), Fake.Dump(p));
                Assert.That(Text(p, K, FakeMaps.EntryA), Does.Not.Contain("0, 0, 90"));
                Assert.That(Text(p, G, FakeMaps.EntryA), Does.Contain("0, 0, 90"));

                // mod.xml keeps them as they are
                string xml = mod.ToXml();
                mod.Save(System.IO.Path.Combine(f.Dir, "mods", "edits"));
                Mod back = Mod.Load(System.IO.Path.Combine(f.Dir, "mods", "edits"));
                Assert.That(back.ToXml(), Is.EqualTo(xml));
                Assert.That(back.Maps[0].Ops.Count, Is.EqualTo(3));
            }
        }

        [Test]
        public void TheShippedValueAgainOrARestoreDropsTheOps()
        {
            using (var f = new FakeMaps())
            {
                var ed = new MapEditor(f.Data, null);
                var mod = new Mod { Id = "m" };
                const string X = "90000001_AB";
                XEntity crate2 = ed.Find(X, FakeMaps.Crate2);
                MapEditor.Set(mod, X, crate2, "Position", null, "1, 2, 3");
                MapEditor.Set(mod, X, crate2, "Position", null, "4, 5, 6");
                Assert.That(mod.Maps[0].Ops[0].Sets.Count, Is.EqualTo(1));
                Assert.That(mod.Maps[0].Ops[0].Sets[0].Old, Is.EqualTo("300, 0, 150"));
                MapEditor.Set(mod, X, crate2, "Position", null, "300, 0, 150");
                Assert.That(mod.Maps, Is.Empty, "no set left: no change, no map");

                MapEditor.Set(mod, X, crate2, "Tags", "0", "z");
                MapEditor.Remove(mod, X, crate2);
                Assert.That(mod.Maps[0].Ops.Single().Kind, Is.EqualTo(MapOpKind.Remove), "the remove takes the change's place");
                MapEditor.Restore(mod, X, crate2.Id);
                Assert.That(mod.Maps, Is.Empty);

                // an unset of what the map does not store is nothing
                MapEditor.Set(mod, X, crate2, "Rotation", null, null);
                Assert.That(mod.Maps, Is.Empty);
            }
        }
    }
}
