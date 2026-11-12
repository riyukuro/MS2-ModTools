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

        [Test]
        public void UnsettingEveryItemOfAListDeletesTheProperty()
        {
            using (var f = new FakeMaps())
            {
                var ed = new MapEditor(f.Data, null);
                const string X = "90000001_AB";
                XEntity crate2 = ed.Find(X, FakeMaps.Crate2);

                Mod all = f.Mod("all", "");
                MapEditor.Set(all, X, crate2, "Tags", "0", null);
                MapEditor.Set(all, X, crate2, "Tags", "1", null);
                Plan p = f.Plan(all);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                foreach (string ar in new[] { K, G, S })
                {
                    string t = Text(p, ar, FakeMaps.EntryA);
                    Assert.That(t, Does.Not.Contain("\"Tags\""), ar + ": no <property> without a <set>");
                    Assert.That(t, Does.Contain("300, 0, 150"), ar);
                }

                Mod one = f.Mod("one", "");
                MapEditor.Set(one, X, crate2, "Tags", "0", null);
                p = f.Plan(one);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string k = Text(p, K, FakeMaps.EntryA);
                Assert.That(k, Does.Contain("\"Tags\""));
                Assert.That(k, Does.Contain("value=\"b\""));
                Assert.That(k, Does.Not.Contain("value=\"a\""));

                // every shipped item unset but a new one set: the property stays with the new item
                Mod swap = f.Mod("swap", "");
                MapEditor.Set(swap, X, crate2, "Tags", "0", null);
                MapEditor.Set(swap, X, crate2, "Tags", "1", null);
                MapEditor.Set(swap, X, crate2, "Tags", "2", "z");
                p = f.Plan(swap);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                k = Text(p, K, FakeMaps.EntryA);
                Assert.That(k, Does.Contain("\"Tags\""));
                Assert.That(k, Does.Contain("value=\"z\""));
                Assert.That(k, Does.Not.Contain("value=\"a\"").And.Not.Contain("value=\"b\""));
            }
        }

        [Test]
        public void SetRefusesWhatTheModelLacksAndIdsAModCannotKeep()
        {
            using (var f = new FakeMaps())
            {
                var ed = new MapEditor(f.Data, null);
                var mod = new Mod { Id = "m" };
                const string X = "90000001_AB";
                XEntity crate2 = ed.Find(X, FakeMaps.Crate2);
                FlatModel model = ed.Model(crate2.Model);
                Assert.That(model, Is.Not.Null);
                Assert.Throws<ModKitException>(() => MapEditor.Set(mod, X, crate2, "Nope", null, "1", model), "the model has no such property");
                Assert.Throws<ModKitException>(() => MapEditor.Set(mod, X, crate2, "Scale", "3", "2", model), "Scale is a plain value");
                Assert.Throws<ModKitException>(() => MapEditor.Set(mod, X, crate2, "Position", "", "1, 1, 1", model), "an empty index");
                Assert.That(mod.Maps, Is.Empty);
                MapEditor.Set(mod, X, crate2, "Scale", null, "2", model);
                MapEditor.Set(mod, X, crate2, "Nope", null, "1");         // without the model: the planner warns, as for a hand-written mod
                Assert.That(mod.Maps[0].Ops[0].Sets.Count, Is.EqualTo(2));
                // a set the mod already has is not checked again: with the model it can still be taken out
                MapEditor.Set(mod, X, crate2, "Nope", null, null, model);
                Assert.That(mod.Maps[0].Ops[0].Sets.Select(s => s.Name).ToArray(), Is.EqualTo(new[] { "Scale" }));

                var bad = new XEntity { Id = "not-an-id", Model = crate2.Model, Name = "x" };
                Assert.Throws<ModKitException>(() => MapEditor.Set(mod, X, bad, "Scale", null, "2"));
                Assert.Throws<ModKitException>(() => MapEditor.Remove(mod, X, bad));
                Assert.Throws<ModKitException>(() => MapEditor.Remove(mod, X, new XEntity { Model = "m" }));
                var upper = new XEntity { Id = FakeMaps.Crate1.ToUpperInvariant(), Model = crate2.Model, Name = "u" };
                MapEditor.Remove(mod, X, upper);
                Assert.That(mod.Maps[0].Ops.Last().Id, Is.EqualTo(FakeMaps.Crate1.ToLowerInvariant()), "a mod keeps ids in lower case");
            }
        }

        [Test]
        public void AnIdInCapitalsInTheFileIsFoundByTheLowerCaseOp()
        {
            using (var f = new FakeMaps())
            {
                var ed = new MapEditor(f.Data, null);
                XEntity crate = ed.Shipped(FakeMaps.MapUpper).Single();
                Assert.That(crate.Id, Is.EqualTo(FakeMaps.Crate2.ToUpperInvariant()));
                Mod mod = f.Mod("up", "");
                MapEditor.Set(mod, FakeMaps.MapUpper, crate, "Position", null, "7, 7, 7");
                Assert.That(mod.Maps[0].Ops[0].Id, Is.EqualTo(FakeMaps.Crate2));
                Plan p = f.Plan(mod);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(Fake.Dump(p), Does.Not.Contain("has no entity"));
                string t = Text(p, K, "xblock/" + FakeMaps.MapUpper + ".xblock");
                Assert.That(t, Does.Contain("7, 7, 7"));
            }
        }

        [Test]
        public void AMapFileWithoutPortalsOffersTheListingsPortalModels()
        {
            using (var f = new FakeMaps())
            {
                var ed = new MapEditor(f.Data, null);
                Assert.That(ed.ModelsWith(FakeMaps.MapA, "PortalID", "portal"), Is.EqualTo(new[] { "Portal_A" }), "the map file's own");
                Assert.That(ed.ModelsWith(FakeMaps.MapUpper, "PortalID"), Is.Empty, "only a crate, no hint");
                Assert.That(ed.ModelsWith(FakeMaps.MapUpper, "PortalID", "portal"), Is.EqualTo(new[] { "Portal_A" }));
                Assert.That(ed.ModelsWith(FakeMaps.MapUpper, "SpawnPointID", "spawnpoint"), Is.EqualTo(new[] { "SpawnPointNPC", "SpawnPointNPC_A", "SpawnPoint_A" }));
                Assert.That(ed.ModelsWith(FakeMaps.MapUpper, "SpawnPointID", "portal"), Is.Empty, "named like one but without the property");
            }
        }

        [Test]
        public void EveryChangeOfAnEntityCounts()
        {
            using (var f = new FakeMaps())
            {
                var ed = new MapEditor(f.Data, null);
                var mod = new Mod { Id = "m" };
                const string X = "90000001_AB";
                XEntity crate2 = ed.Find(X, FakeMaps.Crate2);
                // a hand-written mod: two changes of one entity
                var m = new MapOp { XBlock = MapEditor.Key(X) };
                mod.Maps.Add(m);
                var c1 = new MapEntityOp { Kind = MapOpKind.Change, Id = crate2.Id, Model = crate2.Model, Position = MapEditor.Position(crate2) };
                c1.Sets.Add(new MapSet { Name = "Position", Old = "300, 0, 150", Value = "1, 1, 1" });
                var c2 = new MapEntityOp { Kind = MapOpKind.Change, Id = crate2.Id, Model = crate2.Model, Position = MapEditor.Position(crate2) };
                c2.Sets.Add(new MapSet { Name = "Scale", Value = "3" });
                m.Ops.Add(c1);
                m.Ops.Add(c2);

                List<MapPropRow> rows = ed.Rows(mod, X, crate2);
                Assert.That(rows.Where(r => r.Edited).Select(r => r.Name + "=" + r.Current).ToArray(), Is.EqualTo(new[] { "Position=1, 1, 1", "Scale=3" }));
                MapEditor.Set(mod, X, crate2, "Scale", null, "4");
                Assert.That(c2.Sets.Single().Value, Is.EqualTo("4"), "the set is changed where it is");
                Assert.That(c1.Sets.Count, Is.EqualTo(1));
                Assert.That(MapEditor.Edits(mod, X)[crate2.Id], Is.EqualTo(MapOpKind.Change));

                MapEditor.Remove(mod, X, crate2);
                Assert.That(m.Ops.Single().Kind, Is.EqualTo(MapOpKind.Remove), "both changes give way");
                Assert.That(MapEditor.Edits(mod, X)[crate2.Id.ToUpperInvariant()], Is.EqualTo(MapOpKind.Remove));
            }
        }

        [Test]
        public void OnlyTheLastMapFilesStayParsed()
        {
            using (var f = new FakeMaps())
            {
                var ed = new MapEditor(f.Data, null);
                List<XEntity> a = ed.Shipped("90000001_AB");
                Assert.That(ed.Shipped("90000001_ab"), Is.SameAs(a));
                ed.Shipped("90000005_AB");
                ed.Shipped("90000005_CD");
                List<XEntity> again = ed.Shipped("90000001_AB");
                Assert.That(again, Is.Not.SameAs(a), "read again after two others");
                Assert.That(again.Count, Is.EqualTo(a.Count));
            }
        }

        [Test]
        public void NewSpawnPointsAndPortalsPlanAndComeBack()
        {
            using (var f = new FakeMaps())
            {
                var ed = new MapEditor(f.Data, null);
                Mod mod = f.Mod("added", "");
                const string X = "90000001_AB";
                Assert.That(ed.ModelsWith(X, "SpawnPointID"), Is.EqualTo(new[] { "SpawnPoint_A" }));
                Assert.That(ed.ModelsWith(X, "PortalID"), Is.EqualTo(new[] { "Portal_A" }));
                Assert.That(ed.NextNumber(mod, X, false), Is.EqualTo(IdSpaces.SpawnPointFrom));

                MapEntityOp sp = ed.AddSpawn(mod, 90000001, X, "wolves", "SpawnPoint_A", "10, 20, 30");
                MapEntityOp sp2 = ed.AddSpawn(mod, 90000001, X, "crows", "SpawnPoint_A", null);
                MapEntityOp po = ed.AddPortal(mod, 90000001, X, "east", "Portal_A", "40, 50, 60", 90000002, 1);
                Assert.That(sp.Number, Is.EqualTo(IdSpaces.SpawnPointFrom));
                Assert.That(sp2.Number, Is.EqualTo(IdSpaces.SpawnPointFrom + 1), "the next free number");
                Assert.That(po.Number, Is.EqualTo(IdSpaces.PortalFrom));
                Assert.That(mod.Maps.Single().MapId, Is.EqualTo(-1), "the map id is the file name's: not written");
                Assert.Throws<ModKitException>(() => ed.AddSpawn(mod, 90000001, X, "wolves", "SpawnPoint_A", null), "a name is used once");
                Assert.Throws<ModKitException>(() => ed.AddSpawn(mod, 90000001, X, "x", "Crate_A", null), "the model has no SpawnPointID");
                Assert.Throws<ModKitException>(() => ed.AddPortal(mod, 90000001, X, "y", "No_Such", null, 1, 1), "no such model");
                Assert.Throws<ModKitException>(() => ed.AddPortal(mod, 90000001, X, "z", "Portal_A", null, -1, 1), "a negative map");
                Assert.That(MapEditor.NewOps(mod, X).Select(o => o.Name).ToArray(), Is.EqualTo(new[] { "wolves", "crows", "east" }));

                // properties, numbers and server rows
                ed.SetNew(mod, X, sp, "NpcList", "0", "21000001");
                ed.SetNew(mod, X, sp, MapEditor.ServerRow, "0", "difficulty=1 tag=wolf coolTime=20 population=3");
                ed.SetNew(mod, X, sp, MapEditor.ServerRow, "1", "difficulty=1 tag=bat coolTime=20 population=1");
                ed.SetNew(mod, X, sp, MapEditor.ServerRow, "1", "tag=\"wolf\" note=\"two words\" empty=\"\"");
                Assert.That(sp.Rows[1].Select(kv => kv.Key + "|" + kv.Value).ToArray(), Is.EqualTo(new[] { "tag|wolf", "note|two words", "empty|" }), "the quotes are not part of a value");
                Assert.That(ed.RowsOfNew(sp).Last().Current, Is.EqualTo("tag=wolf note=\"two words\" empty=\"\""), "shown so that it reads back the same");
                {
                    string rx = mod.ToXml();
                    mod.Save(System.IO.Path.Combine(f.Dir, "mods", "added"));
                    Mod rb = Mod.Load(System.IO.Path.Combine(f.Dir, "mods", "added"));
                    Assert.That(rb.ToXml(), Is.EqualTo(rx));
                    Assert.That(MapEditor.FindNew(rb, X, "wolves").Rows[1][1].Value, Is.EqualTo("two words"));
                }
                ed.SetNew(mod, X, sp, MapEditor.ServerRow, "1", null);
                ed.SetNew(mod, X, sp2, "Position", null, "1, 2, 3");
                ed.SetNew(mod, X, sp2, "Position", null, null);
                ed.SetNew(mod, X, po, "TargetPortalID", null, "1");
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, sp, MapEditor.ServerRow, "3", "tag=a"), "rows count up from 0");
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, sp, MapEditor.ServerRow, "1", "mapCode=1"), "the tool writes mapCode");
                foreach (string bad in new[] { "1tag=a", "tag\"=a", "<tag=a", "a/b=1", "=a", "tag", "tag=a tag=b", "tag=\"a", "tag=a\"b", "tag=\"a\"b", "   " })
                    Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, sp, MapEditor.ServerRow, "1", bad), bad);
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, sp, "SpawnPointID", null, null), "a number is not unset");
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, sp, "SpawnPointID", null, "x"), "a number");
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, sp, "NpcList", null, "1"), "a list takes an index");
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, po, MapEditor.ServerRow, "0", "tag=a"), "a portal has no server rows");
                Assert.That(sp.Rows.Count, Is.EqualTo(1));
                Assert.That(sp2.Props, Is.Empty);

                List<MapPropRow> rows = ed.RowsOfNew(sp);
                Assert.That(rows.Select(r => r.Name + (r.Index == null ? "" : "[" + r.Index + "]") + "=" + (r.Current ?? "-") + (r.Edited ? "*" : "")).ToArray(),
                    Is.EqualTo(new[] { "SpawnPointID=140000*", "Position=10, 20, 30*", "NpcList[0]=21000001*", "(server row)[0]=difficulty=1 tag=wolf coolTime=20 population=3*" }));
                Assert.That(rows.All(r => r.Shipped == null), Is.True, "nothing of a new entity ships");
                Assert.That(ed.RowsOfNew(po).Select(r => r.Name).ToArray(), Is.EqualTo(new[] { "PortalID", "TargetFieldSN", "TargetPortalID", "Position" }));

                Plan p = f.Plan(mod);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                string k = Text(p, K, FakeMaps.EntryA);
                Assert.That(k, Does.Contain(MapValues.NewEntityId("added", X, "wolves")));
                Assert.That(k, Does.Contain("value=\"140000\""));
                Assert.That(k, Does.Contain("value=\"50000\""));
                Assert.That(Text(p, FakeMaps.Tags, FakeMaps.TagEntry), Does.Contain("spawnPointID=\"140000\"").And.Contain("tag=\"wolf\""));
                Assert.That(p.Warnings.Select(x => x.ToString()).Count(x => x.Contains("spawn point 140001") && x.Contains("no <row>")), Is.EqualTo(1), Fake.Dump(p));

                string xml = mod.ToXml();
                mod.Save(System.IO.Path.Combine(f.Dir, "mods", "added"));
                Mod back = Mod.Load(System.IO.Path.Combine(f.Dir, "mods", "added"));
                Assert.That(back.ToXml(), Is.EqualTo(xml));
                Assert.That(MapEditor.NewOps(back, X).Count, Is.EqualTo(3));

                MapEditor.RemoveNew(mod, X, sp);
                MapEditor.RemoveNew(mod, X, sp2);
                Assert.That(ed.NextNumber(mod, X, false), Is.EqualTo(IdSpaces.SpawnPointFrom), "a removed one frees its number");
                MapEditor.RemoveNew(mod, X, po);
                Assert.That(mod.Maps, Is.Empty);
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, po, "Position", null, "1, 1, 1"), "it is no longer in the mod");
            }
        }

        [Test]
        public void SetNewChecksNamesModelsAndNumbers()
        {
            using (var f = new FakeMaps())
            {
                var ed = new MapEditor(f.Data, null);
                Mod mod = f.Mod("checks", "");
                const string X = "90000001_AB";
                MapEntityOp sp = ed.AddSpawn(mod, 90000001, X, "wolves", "SpawnPoint_A", null);
                MapEntityOp sp2 = ed.AddSpawn(mod, 90000001, X, "crows", "SpawnPoint_A", null);
                MapEntityOp po = ed.AddPortal(mod, 90000001, X, "east", "Portal_A", null, 90000002, 1);

                // a property the model has not, a list without index, a plain value with one, a comma, an index of spaces
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, sp, "Positon", null, "1, 2, 3"), "misspelt");
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, sp, "NpcList", null, "1"), "a list takes an index");
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, sp, "Position", "0", "1, 2, 3"), "a plain value takes none");
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, sp, "A,B", null, "1"), "a comma breaks order=");
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, sp, "NpcList", "  ", "1"), "an index of spaces");
                Assert.That(sp.Props, Is.Empty);

                // a number a shipped entity or another new one has is refused; the entity's own one is fine
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, sp, "SpawnPointID", null, "7"), "shipped Spawn_1 has 7");
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, sp, "SpawnPointID", null, sp2.Number.ToString()), "crows has it");
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, po, "PortalID", null, "1"), "shipped Portal_1 has 1");
                ed.SetNew(mod, X, sp, "SpawnPointID", null, sp.Number.ToString());
                ed.SetNew(mod, X, sp, "SpawnPointID", null, "8");
                Assert.That(sp.Number, Is.EqualTo(8));
                ed.SetNew(mod, X, po, "PortalID", null, "7");   // the numbers of spawn points and portals are apart
                Assert.That(f.Plan(mod).Errors, Is.Empty);
            }
        }

        [Test]
        public void SetNewOnAHandWrittenAddWithOrder()
        {
            using (var f = new FakeMaps())
            {
                var ed = new MapEditor(f.Data, null);
                const string X = "90000001_AB";
                Mod mod = f.Mod("ord", "<map xblock=\"" + FakeMaps.MapA + "\"><add name=\"s\" model=\"SpawnPoint_A\" entity-name=\"S_1\" order=\"SpawnPointID,Position\">"
                    + "<property name=\"Position\" value=\"1, 2, 3\" /><property name=\"SpawnPointID\" value=\"140005\" /></add></map>");
                MapEntityOp op = MapEditor.FindNew(mod, X, "s");
                Assert.That(op, Is.Not.Null);
                ed.SetNew(mod, X, op, "NpcList", "21000001", "1");
                Assert.That(op.Order, Is.EqualTo(new[] { "SpawnPointID", "Position", "NpcList" }));
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, op, "Npc,List", "1", "1"));
                ed.SetNew(mod, X, op, "Position", null, null);
                Assert.That(op.Order, Is.EqualTo(new[] { "SpawnPointID", "NpcList" }));
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, op, "SpawnPointID", null, "7"), "shipped Spawn_1 has 7");
                Assert.That(ed.NextNumber(mod, X, false), Is.EqualTo(IdSpaces.SpawnPointFrom), "140005 is not the first");

                string xml = mod.ToXml();
                mod.Save(System.IO.Path.Combine(f.Dir, "mods", "ord"));
                Mod back = Mod.Load(System.IO.Path.Combine(f.Dir, "mods", "ord"));
                Assert.That(back.ToXml(), Is.EqualTo(xml));
                Assert.That(MapEditor.FindNew(back, X, "s").Order, Is.EqualTo(new[] { "SpawnPointID", "NpcList" }));
            }
        }

        [Test]
        public void NextNumberCountsEveryWayTheModBringsOne()
        {
            using (var f = new FakeMaps())
            {
                var ed = new MapEditor(f.Data, null);
                const string X = "90000001_AB";
                // a hand-written <add> with SpawnPointID 140000, a <change> giving shipped Spawn_1 140001
                Mod mod = f.Mod("nums", "<map xblock=\"" + FakeMaps.MapA + "\"><add name=\"s\" model=\"SpawnPoint_A\" entity-name=\"S_1\">"
                    + "<property name=\"SpawnPointID\" value=\"140000\" /></add>"
                    + "<change id=\"" + FakeMaps.Spawn1 + "\" model=\"SpawnPoint_A\" position=\"150, 150, 150\"><set name=\"SpawnPointID\" old=\"7\" value=\"140001\" /></change></map>");
                Assert.That(ed.NextNumber(mod, X, false), Is.EqualTo(IdSpaces.SpawnPointFrom + 2));
                MapEntityOp sp = ed.AddSpawn(mod, 90000001, X, "wolves", "SpawnPoint_A", null);
                Assert.That(sp.Number, Is.EqualTo(IdSpaces.SpawnPointFrom + 2));
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, sp, "SpawnPointID", null, "140001"), "the change gives it");
                Assert.That(f.Plan(mod).Errors, Is.Empty, Fake.Dump(f.Plan(mod)));

                // every number of the range used: the throw
                Mod full = f.Mod("full", "");
                var m = new MapOp { XBlock = FakeMaps.MapA };
                full.Maps.Add(m);
                for (long n = IdSpaces.TriggerObjectFrom; n <= IdSpaces.TriggerObjectTo; n++)
                {
                    var op = new MapEntityOp { Kind = MapOpKind.Add, Name = "b" + n, Model = "TriggerBox_A" };
                    op.Props.Add(new XProp("TriggerObjectID", n.ToString()));
                    m.Ops.Add(op);
                }
                Assert.That(Assert.Throws<ModKitException>(() => ed.NextNumber(full, X, "TriggerObjectID")).Message, Does.Contain("every TriggerObjectID"));
                m.Ops.RemoveAt(3);
                Assert.That(ed.NextNumber(full, X, "TriggerObjectID"), Is.EqualTo(IdSpaces.TriggerObjectFrom + 3), "the one free number");
            }
        }

        [Test]
        public void ANewEntityDoesNotMoveTheModsMapToAnotherId()
        {
            using (var f = new FakeMaps())
            {
                var ed = new MapEditor(f.Data, null);
                const string X = "90000001_AB";
                Mod mod = f.Mod("ids", "<map xblock=\"" + FakeMaps.MapA + "\" id=\"90000005\">" + "<add name=\"c\" model=\"Crate_A\" entity-name=\"C_1\" /></map>");
                Assert.That(Assert.Throws<ModKitException>(() => ed.AddSpawn(mod, 90000001, X, "wolves", "SpawnPoint_A", null)).Message, Does.Contain("90000005"));
                Assert.That(MapEditor.NewOps(mod, X).Count, Is.EqualTo(1), "nothing added");
                Assert.That(mod.Maps.Single().MapId, Is.EqualTo(90000005));
                ed.AddSpawn(mod, 90000005, X, "wolves", "SpawnPoint_A", null);
                Assert.That(mod.Maps.Single().MapId, Is.EqualTo(90000005));
            }
        }

        [Test]
        public void NewNpcSpawnsInteractObjectsAndTriggerBoxesPlanAndComeBack()
        {
            using (var f = new FakeMaps())
            {
                var ed = new MapEditor(f.Data, null);
                Mod mod = f.Mod("kinds", "");
                const string X = "90000001_AB";
                // no entity of the map file is of these kinds: the listing's models named like one, by mixin
                Assert.That(ed.ModelsOf(X, MapEditor.NpcSpawn), Is.EqualTo(new[] { "SpawnPointNPC", "SpawnPointNPC_A" }));
                Assert.That(ed.ModelsOf(X, MapEditor.Interact), Is.EqualTo(new[] { "Interact_A", "MS2InteractObject" }));
                Assert.That(ed.ModelsOf(X, MapEditor.TriggerBox), Is.EqualTo(new[] { "TriggerBox_A", "MS2TriggerBox" }));
                Assert.That(ed.HasMixin("TriggerBox_A", "ms2triggerobject"), Is.True, "two mixins deep, any case");
                Assert.That(ed.HasMixin("Crate_A", "MS2TriggerObject"), Is.False);
                Assert.That(ed.NextNumber(mod, X, "TriggerObjectID"), Is.EqualTo(IdSpaces.TriggerObjectFrom));

                MapEntityOp npc = ed.AddNpcSpawn(mod, 90000001, X, "guard", "SpawnPointNPC_A", "10, 20, 30", 21000001, 2);
                MapEntityOp sp = ed.AddSpawn(mod, 90000001, X, "wolves", "SpawnPoint_A", null);
                MapEntityOp io = ed.AddInteract(mod, 90000001, X, "lever", "Interact_A", "40, 50, 60", 10000123);
                MapEntityOp box = ed.AddTriggerBox(mod, 90000001, X, "gate", "TriggerBox_A", "70, 80, 90", "300, 300.5, 200");
                MapEntityOp box2 = ed.AddTriggerBox(mod, 90000001, X, "gate2", "TriggerBox_A", null, null);
                Assert.That(npc.Kind, Is.EqualTo(MapOpKind.Add), "a plain <add>");
                Assert.That(sp.Number, Is.EqualTo(IdSpaces.SpawnPointFrom + 1), "the NPC spawn's SpawnPointID counts for the next spawn point");
                Assert.That(ed.NextNumber(mod, X, "TriggerObjectID"), Is.EqualTo(IdSpaces.TriggerObjectFrom + 2));
                Assert.That(new[] { npc, sp, io, box }.Select(o => ed.KindOf(o) == null ? "-" : ed.KindOf(o).Label).ToArray(),
                    Is.EqualTo(new[] { "NPC spawn point", "-", "interact object", "trigger box" }));
                Assert.Throws<ModKitException>(() => ed.AddInteract(mod, 90000001, X, "x", "Crate_A", null, 1), "not an interact object model");
                Assert.Throws<ModKitException>(() => ed.AddInteract(mod, 90000001, X, "x", "TriggerBox_A", null, 1), "a trigger box is not one either");
                Assert.Throws<ModKitException>(() => ed.AddInteract(mod, 90000001, X, "x", "Interact_A", null, 0), "an interactID is needed");
                Assert.Throws<ModKitException>(() => ed.AddNpcSpawn(mod, 90000001, X, "x", "SpawnPointNPC_A", null, 0, 1), "an NPC id is needed");
                Assert.Throws<ModKitException>(() => ed.AddNpcSpawn(mod, 90000001, X, "x", "SpawnPointNPC_A", null, 1, 0), "one NPC or more");
                Assert.Throws<ModKitException>(() => ed.AddTriggerBox(mod, 90000001, X, "x", "TriggerBox_A", null, "1, 2"), "three sizes");
                Assert.Throws<ModKitException>(() => ed.AddTriggerBox(mod, 90000001, X, "x", "TriggerBox_A", null, "1, 0, 2"), "above 0");
                Assert.Throws<ModKitException>(() => ed.AddTriggerBox(mod, 90000001, X, "gate", "TriggerBox_A", null, null), "a name is used once");
                Assert.That(MapEditor.NewOps(mod, X).Count, Is.EqualTo(5));

                List<MapPropRow> rows = ed.RowsOfNew(npc);
                Assert.That(rows.Take(4).Select(r => r.Name + (r.Index == null ? "" : "[" + r.Index + "]") + "=" + (r.Current ?? "-") + (r.Edited ? "*" : "")).ToArray(),
                    Is.EqualTo(new[] { "SpawnPointID=140000*", "Position=10, 20, 30*", "NpcList[21000001]=2*", "NpcCount=2*" }));
                Assert.That(ed.RowsOfNew(box).Take(3).Select(r => r.Name + "=" + r.Current).ToArray(),
                    Is.EqualTo(new[] { "TriggerObjectID=170000", "Position=70, 80, 90", "ShapeDimensions=300, 300.5, 200" }));
                Assert.That(ed.RowsOfNew(box2).First(r => r.Name == "ShapeDimensions").Default, Is.EqualTo("100, 100, 100"), "the model's size when none is given");

                // the number of an added entity is changed, never unset
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, box, "TriggerObjectID", null, null));
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, box, "TriggerObjectID", null, "x"));
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, npc, "SpawnPointID", "0", "1"));
                ed.SetNew(mod, X, npc, "NpcList", "21000002", "1");
                ed.SetNew(mod, X, io, "interactID", null, "10000124");

                Plan p = f.Plan(mod);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(p.Warnings.Select(x => x.ToString()).Where(x => x.Contains("TriggerObjectID") || x.Contains("guard") || x.Contains("no property")), Is.Empty, Fake.Dump(p));
                foreach (string ar in new[] { K, G, S })
                {
                    string t = Text(p, ar, FakeMaps.EntryA);
                    Assert.That(t, Does.Contain(MapValues.NewEntityId("kinds", X, "gate")), ar);
                    Assert.That(t, Does.Contain("value=\"170000\"").And.Contain("value=\"170001\"").And.Contain("300, 300.5, 200"), ar);
                    Assert.That(t, Does.Contain("index=\"21000001\"").And.Contain("index=\"21000002\"").And.Contain("value=\"10000124\""), ar);
                }

                string xml = mod.ToXml();
                mod.Save(System.IO.Path.Combine(f.Dir, "mods", "kinds"));
                Mod back = Mod.Load(System.IO.Path.Combine(f.Dir, "mods", "kinds"));
                Assert.That(back.ToXml(), Is.EqualTo(xml));
                Assert.That(MapEditor.NewOps(back, X).Count, Is.EqualTo(5));
                Assert.That(ed.NextNumber(back, X, "TriggerObjectID"), Is.EqualTo(IdSpaces.TriggerObjectFrom + 2), "the numbers of loaded <add> ops count");

                // the same TriggerObjectID twice in the map is an error; one outside the mod range a warning
                Assert.Throws<ModKitException>(() => ed.SetNew(mod, X, box2, "TriggerObjectID", null, "170000"), "the view refuses a number in use");
                // written by hand into mod.xml, Plan still catches it
                box2.Props[box2.Props.FindIndex(x => x.Name == "TriggerObjectID")] = new XProp("TriggerObjectID", "170000");
                p = f.Plan(mod);
                Assert.That(p.Errors.Select(x => x.ToString()).Count(x => x.Contains("TriggerObjectID 170000 is already used in this map")), Is.EqualTo(3), Fake.Dump(p));
                ed.SetNew(mod, X, box2, "TriggerObjectID", null, "5");
                p = f.Plan(mod);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(p.Warnings.Select(x => x.ToString()).Count(x => x.Contains("TriggerObjectID 5 is outside the range kept for mods (170000-179999)")), Is.EqualTo(3), Fake.Dump(p));

                MapEditor.RemoveNew(mod, X, box);
                Assert.That(ed.NextNumber(mod, X, "TriggerObjectID"), Is.EqualTo(IdSpaces.TriggerObjectFrom), "a removed one frees its number");
            }
        }

        [Test]
        public void ANewSpawnOnAFileWithoutTheMapIdInItsNameKeepsTheMapId()
        {
            using (var f = new FakeMaps())
            {
                var ed = new MapEditor(f.Data, null);
                var mod = new Mod { Id = "m" };
                ed.AddSpawn(mod, 90000007, "90000001_AB", "w", "SpawnPoint_A", null);
                Assert.That(mod.Maps[0].MapId, Is.EqualTo(90000007));
                Assert.That(mod.Maps[0].EffectiveMapId, Is.EqualTo(90000007));
            }
        }
    }
}
