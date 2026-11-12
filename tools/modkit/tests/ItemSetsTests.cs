// Item sets (ItemSets.cs, PlannerItemSets.cs) on made-up tables: a kms client, a global client and a server.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    public sealed class FakeSets : IDisposable
    {
        public readonly string Dir;
        public readonly DataSet Data;
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        public const string KmsInfo = Head + "<ms2>\n" +
            "\t<set id=\"8100001\" itemIDs=\"5001,5002,5003\" optionID=\"8100001\">\n" +
            "\t\t<part count=\"2\" str=\"9\" />\n" +
            "\t\t<part count=\"3\" hp=\"100\" pen=\"20\" sgi_boss=\"30\" additionalEffectID=\"70000001\" additionalEffectLevel=\"2\" />\n" +
            "\t</set>\n" +
            "\t<set id=\"8100002\" itemIDs=\"5001,5002\">\n" +
            "\t\t<part count=\"2\" dex=\"4\" car=\"5\" />\n" +
            "\t</set>\n" +
            "\t<set id=\"8100004\" itemIDs=\"5003\">\n" +
            "\t\t<part count=\"1\" luk=\"1\" />\n" +
            "\t</set>\n" +
            "</ms2>\n";

        public const string SrvInfo = Head + "<ms2>\n" +
            "\t<set id=\"8100001\" itemIDs=\"5001,5002,5003\" optionID=\"8100001\" />\n" +
            "\t<set id=\"8100002\" itemIDs=\"5001,5002\" optionID=\"8100009\" feature=\"F\" />\n" +
            "\t<set id=\"8100003\" itemIDs=\"5004\" optionID=\"8100009\" />\n" +
            "</ms2>\n";

        public const string SrvOpt = Head + "<ms2>\n" +
            "\t<option id=\"8100001\">\n" +
            "\t\t<part count=\"2\" additionalEffectID=\"0\" additionalEffectLevel=\"1\" str_value_base=\"9\" />\n" +
            "\t\t<part count=\"3\" additionalEffectID=\"70000001\" additionalEffectLevel=\"2\" hp_value_base=\"100\" pen_rate_base=\"0.02\" sgi_rate_base=\"0.03\" sgi_target=\"4\" />\n" +
            "\t</option>\n" +
            "\t<option id=\"8100009\">\n" +
            "\t\t<part count=\"2\" additionalEffectID=\"0\" additionalEffectLevel=\"1\" dex_value_base=\"4\" car_value_base=\"5\" />\n" +
            "\t</option>\n" +
            "</ms2>\n";

        static string Keys(params string[] kv)
        {
            var sb = new StringBuilder(Head + "<ms2>\n");
            for (int i = 0; i < kv.Length; i += 2) sb.Append("\t<key id=\"" + kv[i] + "\" name=\"" + kv[i + 1] + "\" />\n");
            return sb.Append("</ms2>\n").ToString();
        }

        static string ItemFile(long id) { return Head + "<ms2>\n\t<environment>\n\t\t<basic itemID=\"" + id + "\" />\n\t</environment>\n</ms2>\n"; }

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        public FakeSets() : this(null) { }

        /// <summary>kmsInfo: another shipped set table of the kms client.</summary>
        public FakeSets(string kmsInfo)
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-sets-" + Guid.NewGuid().ToString("N"));
            string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
            foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);
            var rows = new StringBuilder(Head + "<ms2>\n");
            foreach (long i in new long[] { 5001, 5002, 5003 }) rows.Append("\t<item id=\"" + i + "\" />\n");
            M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>>
            {
                E(ItemSetRules.InfoEntry, kmsInfo ?? KmsInfo), E("itemdata/005.xml", rows.Append("</ms2>\n").ToString()),
                E("additional/700.xml", Head + "<ms2>\n\t<additional id=\"70000001\" />\n</ms2>\n"),
                E("string/kr/setitemname.xml", Keys("8100001", "Tin Set")),
            });
            foreach (string d in new[] { g, s })
            {
                var l = new List<KeyValuePair<string, byte[]>>
                {
                    E(ItemSetRules.InfoEntry, SrvInfo), E(ItemSetRules.OptionEntry, SrvOpt),
                    E("additionaleffect/70000001.xml", Head + "<ms2>\n\t<level />\n</ms2>\n"),
                    E("string/en/setitemname.xml", Keys("8100001", "Tin Set", "8100002", "Clay Set")),
                };
                foreach (long i in new long[] { 5001, 5002, 5003, 5004 }) l.Add(E("item/00/00/" + i.ToString("00000000") + ".xml", ItemFile(i)));
                M2dWriter.Create(Path.Combine(d, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, l);
            }
            Data = new DataSet(k, g, s);
        }

        public static Mod ModOf(string body, string id = "sets.test")
        {
            Mod m = Mod.Parse("<mod id=\"" + id + "\" name=\"t\" version=\"1\">\n" + body + "\n</mod>", "test mod");
            return Mod.Parse(m.ToXml(), "round trip");
        }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class ItemSetsTests
    {
        const string K = "client:Xml.m2d", G = "gms-client:Xml.m2d", S = "server:Xml.m2d";

        static Mod Empty() { return FakeSets.ModOf(""); }

        static ItemSet Find(ItemSetEditor ed, string id)
        {
            foreach (ItemSet s in ed.Sets) if (ed.Get(s, "id") == id) return s;
            return null;
        }

        static SetPart Part(ItemSetEditor ed, ItemSet s, string count)
        {
            foreach (SetPart p in ed.Parts(s)) if (p.Count == count) return p;
            return null;
        }

        // the mod's text between <mod ...> and </mod>, read again as a fresh mod (what a save and load gives)
        static string Body(Mod m)
        {
            string x = m.ToXml();
            int a = x.IndexOf('>', x.IndexOf("<mod", StringComparison.Ordinal)) + 1, b = x.LastIndexOf("</mod>", StringComparison.Ordinal);
            return x.Substring(a, b - a);
        }

        static Mod Reload(Mod m) { return FakeSets.ModOf(Body(m)); }

        static Plan PlanOf(FakeSets f, Mod m) { return Planner.Build(f.Data, new[] { Reload(m) }, new PlanOptions()); }

        static string Dump(Plan p)
        {
            var sb = new StringBuilder();
            foreach (PlanMessage m in p.Warnings) sb.Append("W ").Append(m).Append('\n');
            foreach (PlanMessage m in p.Errors) sb.Append("E ").Append(m).Append('\n');
            return sb.ToString();
        }

        static string Text(Plan p, string archive, string entry)
        {
            return p.Find(archive) == null ? null : Fake.Text(p, archive, entry);
        }

        [Test]
        public void RulesReadAndWriteBothNotations()
        {
            var row = new Dictionary<string, string>();
            Func<string, string> get = a => { string v; return row.TryGetValue(a, out v) ? v : null; };
            Action<string, string> set = (a, v) => { if (v == null) row.Remove(a); else row[a] = v; };

            ItemSetRules.Write(get, set, false, "str", "9");
            ItemSetRules.Write(get, set, false, "pen", "55");
            ItemSetRules.Write(get, set, false, "sgi_boss", "30");
            ItemSetRules.Write(get, set, false, ItemSetRules.Effects, "");
            Assert.That(row["str_value_base"], Is.EqualTo("9"));
            Assert.That(row["pen_rate_base"], Is.EqualTo("0.055"));
            Assert.That(row["sgi_boss_rate_base"], Is.EqualTo("0.03"));
            Assert.That(row["sgi_boss_target"], Is.EqualTo("4"));
            Assert.That(row["additionalEffectID"], Is.EqualTo("0"));
            Assert.That(row["additionalEffectLevel"], Is.EqualTo("1"));
            Assert.That(ItemSetRules.Read(get, false, "pen"), Is.EqualTo("55"));
            Assert.That(ItemSetRules.Read(get, false, "sgi_boss"), Is.EqualTo("30"));
            Assert.That(ItemSetRules.Read(get, false, ItemSetRules.Effects), Is.EqualTo(""));

            // a boss bonus in the shared slot moves when elites need the slot
            row.Clear();
            row["sgi_rate_base"] = "0.03";
            row["sgi_target"] = "4";
            Assert.That(ItemSetRules.Read(get, false, "sgi_boss"), Is.EqualTo("30"));
            Assert.That(ItemSetRules.Read(get, false, "sgi_elite"), Is.EqualTo(""));
            ItemSetRules.Write(get, set, false, "sgi_boss", "40");
            Assert.That(row["sgi_rate_base"], Is.EqualTo("0.04"));
            ItemSetRules.Write(get, set, false, "sgi_elite", "15");
            Assert.That(row["sgi_rate_base"], Is.EqualTo("0.015"));
            Assert.That(row["sgi_target"], Is.EqualTo("3"));
            Assert.That(row["sgi_boss_rate_base"], Is.EqualTo("0.04"));
            Assert.That(row["sgi_boss_target"], Is.EqualTo("4"));
            Assert.That(ItemSetRules.Read(get, false, "sgi_boss"), Is.EqualTo("40"));
            ItemSetRules.Write(get, set, false, "sgi_elite", "");
            ItemSetRules.Write(get, set, false, "sgi_boss", "");
            Assert.That(row, Is.Empty);

            // the kms notation: plain attributes, no effect attributes for none, no car
            ItemSetRules.Write(get, set, true, "pen", "55");
            ItemSetRules.Write(get, set, true, "car", "5");
            ItemSetRules.Write(get, set, true, ItemSetRules.Effects, "70000001:2, 70000002");
            Assert.That(row["pen"], Is.EqualTo("55"));
            Assert.That(row.ContainsKey("car"), Is.False);
            Assert.That(row["additionalEffectID"], Is.EqualTo("70000001,70000002"));
            Assert.That(row["additionalEffectLevel"], Is.EqualTo("2,1"));
            Assert.That(ItemSetRules.Read(get, true, ItemSetRules.Effects), Is.EqualTo("70000001:2,70000002:1"));
            ItemSetRules.Write(get, set, true, ItemSetRules.Effects, "");
            Assert.That(row.ContainsKey("additionalEffectID"), Is.False);
            Assert.Throws<ModKitException>(() => ItemSetRules.Write(get, set, true, ItemSetRules.Effects, "x:1"));
        }

        [Test]
        public void RulesCheckParts()
        {
            var row = new Dictionary<string, string> { { "count", "0" } };
            Func<string, string> get = a => { string v; return row.TryGetValue(a, out v) ? v : null; };
            Assert.That(string.Join("\n", ItemSetRules.CheckPart(get, true, 3).ToArray()), Does.Contain("not a number above 0").And.Contain("fails to load the whole set table"));
            row["count"] = "4";
            Assert.That(string.Join("\n", ItemSetRules.CheckPart(get, true, 3).ToArray()), Does.Contain("the set has 3 items, the bonus can never be active"));
            row["count"] = "2";
            row["additionalEffectID"] = "70000001,70000002";
            row["additionalEffectLevel"] = "1";
            row["car"] = "5";
            string t = string.Join("\n", ItemSetRules.CheckPart(get, true, 3).ToArray());
            Assert.That(t, Does.Contain("2 effect ids and 1 effect level"));
            Assert.That(t, Does.Contain("car=\"5\" is not a bonus the kms client reads"));
            row.Clear();
            row["count"] = "2";
            row["sgi_boss_target"] = "3";
            Assert.That(string.Join("\n", ItemSetRules.CheckPart(get, false, 3).ToArray()), Does.Contain("sgi_boss_target \"3\" is not 0 or 4"));

            var set = new Dictionary<string, string> { { "id", "1" }, { "itemIDs", "5001,5001" } };
            var parts = new List<Func<string, string>> { get, get };
            row.Remove("sgi_boss_target");
            string c = string.Join("\n", ItemSetRules.CheckSet(a => { string v; return set.TryGetValue(a, out v) ? v : null; }, parts, true).ToArray());
            Assert.That(c, Does.Contain("item 5001 is in the set twice"));
            Assert.That(c, Does.Contain("two bonuses for 2 items: the kms client fails"));
        }

        [Test]
        public void ReadsTheSidesAsOneSet()
        {
            using (var f = new FakeSets())
            {
                var ed = new ItemSetEditor(f.Data, Empty());
                Assert.That(ed.Sides.Count, Is.EqualTo(2));
                Assert.That(ed.Sets.Count, Is.EqualTo(4));
                ItemSet a = Find(ed, "8100001");
                Assert.That(ed.Name(a), Is.EqualTo("Tin Set"));
                Assert.That(ed.Members(a), Is.EqualTo(new long[] { 5001, 5002, 5003 }));
                List<SetPart> parts = ed.Parts(a);
                Assert.That(parts.Count, Is.EqualTo(2));
                Assert.That(ed.Get(parts[0], "str"), Is.EqualTo("9"));
                Assert.That(ed.Get(parts[1], "pen"), Is.EqualTo("20"));
                Assert.That(ed.Get(parts[1], "sgi_boss"), Is.EqualTo("30"));
                Assert.That(ed.Get(parts[1], ItemSetRules.Effects), Is.EqualTo("70000001:2"));
                Assert.That(ed.Summary(parts[1]), Is.EqualTo("hp 100, pen 20, sgi_boss 30, effect 70000001:2"));
                Assert.That(ed.Check(a), Is.Empty);
                Assert.That(ed.IsChanged(a), Is.False);

                // car on a kms part; an option two sets share
                string b = string.Join("\n", ed.Check(Find(ed, "8100002")).ToArray());
                Assert.That(b, Does.Contain(K + ": bonus for 2 items: car=\"5\" is not a bonus the kms client reads"));
                Assert.That(b, Does.Contain("(option 8100009) are also those of set 8100003"));
                Assert.That(b, Does.Not.Contain("differ"));
                Assert.That(string.Join("\n", ed.Check(Find(ed, "8100004")).ToArray()), Does.Contain("only on " + K + "; not on " + S));
                Assert.That(ed.Find("Tin").Count, Is.EqualTo(1));
                Assert.That(ed.Find("5004").Count, Is.EqualTo(1));
            }
        }

        [Test]
        public void ChangesABonusOnBothSides()
        {
            using (var f = new FakeSets())
            {
                Mod mod = Empty();
                var ed = new ItemSetEditor(f.Data, mod);
                ItemSet a = Find(ed, "8100001");
                ed.Set(a, Part(ed, a, "2"), "str", "12");
                ed.Set(a, Part(ed, a, "3"), "pen", "");
                ed.Set(a, Part(ed, a, "3"), "sgi_elite", "15");
                ed.Set(a, Part(ed, a, "3"), ItemSetRules.Effects, "");
                Assert.That(ed.IsChanged(a), Is.True);
                Assert.That(ed.Get(Part(ed, a, "2"), "str"), Is.EqualTo("12"));
                Assert.That(ed.Check(a), Is.Empty);
                Assert.Throws<ModKitException>(() => ed.Set(a, Part(ed, a, "2"), "str", "x"));
                Assert.Throws<ModKitException>(() => ed.Set(a, Part(ed, a, "2"), ItemSetRules.Count, "3"));

                Plan p = PlanOf(f, mod);
                Assert.That(Dump(p), Is.Empty);
                string k = Text(p, K, ItemSetRules.InfoEntry);

                // plan after plan: the id sets a plan caches replace those of the plan before
                var cache = (Dictionary<string, object>)typeof(DataSet).GetField("Cache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).GetValue(f.Data);
                int keys = cache.Count;
                bool itemSets = false;
                foreach (string x in cache.Keys) if (x.StartsWith("groupcheck-items\n", StringComparison.Ordinal)) itemSets = true;
                Assert.That(itemSets, Is.True);
                for (int i = 0; i < 4; i++) PlanOf(f, mod);
                Assert.That(cache.Count, Is.EqualTo(keys));
                Assert.That(k, Does.Contain("<part count=\"2\" str=\"12\" />"));
                Assert.That(k, Does.Contain("<part count=\"3\" hp=\"100\" sgi_boss=\"30\" sgi_elite=\"15\" />"));
                foreach (string arch in new[] { S, G })
                {
                    string o = Text(p, arch, ItemSetRules.OptionEntry);
                    Assert.That(o, Does.Contain("<part count=\"2\" additionalEffectID=\"0\" additionalEffectLevel=\"1\" str_value_base=\"12\" />"), arch);
                    Assert.That(o, Does.Contain("<part count=\"3\" additionalEffectID=\"0\" additionalEffectLevel=\"1\" hp_value_base=\"100\" sgi_rate_base=\"0.015\" sgi_target=\"3\" sgi_boss_rate_base=\"0.03\" sgi_boss_target=\"4\" />"), arch);
                }
                Assert.That(Text(p, S, ItemSetRules.InfoEntry), Is.Null.Or.EqualTo(FakeSets.SrvInfo));

                // the saved mod shows the same
                var again = new ItemSetEditor(f.Data, Reload(mod));
                ItemSet a2 = Find(again, "8100001");
                Assert.That(again.IsChanged(a2), Is.True);
                Assert.That(again.Get(Part(again, a2, "2"), "str"), Is.EqualTo("12"));
                Assert.That(again.Get(Part(again, a2, "3"), "sgi_elite"), Is.EqualTo("15"));
                Assert.That(again.Differs(Part(again, a2, "3"), "sgi_boss"), Is.False);
                again.Revert(a2);
                Assert.That(again.IsChanged(a2), Is.False);
                Assert.That(Body(again.Mod).Trim(), Is.Empty);
            }
        }

        [Test]
        public void AddsAndRemovesABonusOfAShippedSet()
        {
            using (var f = new FakeSets())
            {
                Mod mod = Empty();
                var ed = new ItemSetEditor(f.Data, mod);
                ItemSet a = Find(ed, "8100001");
                ed.AddPart(a, 1);
                ed.Set(a, Part(ed, a, "1"), "hp", "50");
                ed.Set(a, Part(ed, a, "1"), "lddincrease", "5");
                ed.RemovePart(a, Part(ed, a, "3"));
                Assert.Throws<ModKitException>(() => ed.AddPart(a, 2));
                Assert.That(ed.Parts(a).Count, Is.EqualTo(2));
                Assert.That(ed.Parts(a)[0].Count, Is.EqualTo("1"));
                Assert.That(Body(mod), Does.Contain("in=\"set[id=8100001]\""));

                Plan p = PlanOf(f, mod);
                Assert.That(Dump(p), Is.Empty);
                string k = Text(p, K, ItemSetRules.InfoEntry);
                Assert.That(k, Does.Contain("<part count=\"1\" hp=\"50\" lddincrease=\"5\" />"));
                Assert.That(k, Does.Not.Contain("count=\"3\""));
                Assert.That(k, Does.Contain("<part count=\"1\" luk=\"1\" />"));
                string o = Text(p, S, ItemSetRules.OptionEntry);
                Assert.That(o, Does.Contain("<part count=\"1\" additionalEffectID=\"0\" additionalEffectLevel=\"1\" hp_value_base=\"50\" lddincrease_rate_base=\"0.005\" />"));
                Assert.That(o, Does.Not.Contain("count=\"3\""));

                var again = new ItemSetEditor(f.Data, Reload(mod));
                ItemSet a2 = Find(again, "8100001");
                Assert.That(again.Parts(a2).Count, Is.EqualTo(2));
                Assert.That(again.Get(Part(again, a2, "1"), "lddincrease"), Is.EqualTo("5"));
                Assert.That(again.IsChanged(a2), Is.True);
                again.RemovePart(a2, Part(again, a2, "1"));
                again.Revert(a2);
                Assert.That(Body(again.Mod).Trim(), Is.Empty);
            }
        }

        [Test]
        public void AddsANewSet()
        {
            using (var f = new FakeSets())
            {
                Mod mod = Empty();
                var ed = new ItemSetEditor(f.Data, mod);
                Assert.That(ed.NextIds(null, 2, null), Is.EqualTo(new long[] { 99000000, 99000001 }));
                Assert.Throws<ModKitException>(() => ed.Add(8100005));
                ItemSet n = ed.Add(99000000);
                Assert.Throws<ModKitException>(() => ed.Add(99000000));
                Assert.That(string.Join("\n", ed.Check(n).ToArray()), Does.Contain("the set has no items").And.Contain("the set has no name"));
                ed.SetMembers(n, new long[] { 5001, 5003 });
                ed.SetName(n, "Reed & Pipe Set");
                ed.AddPart(n, 2);
                ed.Set(n, Part(ed, n, "2"), "str", "7");
                ed.Set(n, Part(ed, n, "2"), "sgi_boss", "25");
                ed.Set(n, Part(ed, n, "2"), ItemSetRules.Effects, "70000001:3");
                ed.AddPart(n, 1);
                ed.Set(n, Part(ed, n, "1"), "hp", "10");
                ed.Set(n, "isDisableTooltip", "1");
                Assert.That(ed.Check(n), Is.Empty);
                Assert.That(ed.Name(n), Is.EqualTo("Reed & Pipe Set"));
                Assert.That(ed.NextIds(null, 1, null), Is.EqualTo(new long[] { 99000001 }));
                Assert.That(mod.Ids.Count, Is.EqualTo(1));

                Plan p = PlanOf(f, mod);
                Assert.That(Dump(p), Is.Empty);
                string k = Text(p, K, ItemSetRules.InfoEntry);
                Assert.That(k, Does.Contain("<set id=\"99000000\" itemIDs=\"5001,5003\" isDisableTooltip=\"1\">"));
                Assert.That(k, Does.Contain("<part count=\"2\" str=\"7\" sgi_boss=\"25\" additionalEffectID=\"70000001\" additionalEffectLevel=\"3\" />"));
                Assert.That(k, Does.Contain("<part count=\"1\" hp=\"10\" />"));
                Assert.That(Text(p, K, "string/kr/setitemname.xml"), Does.Contain("<key id=\"99000000\" name=\"Reed &amp; Pipe Set\" />"));
                foreach (string arch in new[] { S, G })
                {
                    Assert.That(Text(p, arch, ItemSetRules.InfoEntry), Does.Contain("<set id=\"99000000\" itemIDs=\"5001,5003\" optionID=\"99000000\" isDisableTooltip=\"1\" />"), arch);
                    string o = Text(p, arch, ItemSetRules.OptionEntry);
                    Assert.That(o, Does.Contain("<option id=\"99000000\">"), arch);
                    Assert.That(o, Does.Contain("<part count=\"2\" additionalEffectID=\"70000001\" additionalEffectLevel=\"3\" str_value_base=\"7\" sgi_boss_rate_base=\"0.025\" sgi_boss_target=\"4\" />"), arch);
                    Assert.That(Text(p, arch, "string/en/setitemname.xml"), Does.Contain("<key id=\"99000000\" name=\"Reed &amp; Pipe Set\" />"), arch);
                }

                var again = new ItemSetEditor(f.Data, Reload(mod));
                ItemSet n2 = Find(again, "99000000");
                Assert.That(n2.IsAdded, Is.True);
                Assert.That(again.Parts(n2).Count, Is.EqualTo(2));
                Assert.That(again.Get(Part(again, n2, "2"), "sgi_boss"), Is.EqualTo("25"));
                Assert.That(again.Check(n2), Is.Empty);
                again.RemovePart(n2, Part(again, n2, "1"));
                Assert.That(again.Parts(n2).Count, Is.EqualTo(1));
                again.Remove(n2);
                Assert.That(Find(again, "99000000"), Is.Null);
                Assert.That(Body(again.Mod).Trim(), Is.Empty);
            }
        }

        [Test]
        public void ClonesASetWithBonusesOfItsOwn()
        {
            using (var f = new FakeSets())
            {
                Mod mod = Empty();
                var ed = new ItemSetEditor(f.Data, mod);
                ItemSet c = ed.Clone(Find(ed, "8100002"), 99000005);
                Assert.That(ed.Name(c), Is.EqualTo("Clay Set"));
                Assert.That(ed.SharesOption(c, 1), Is.Empty);
                Assert.That(ed.Get(Part(ed, c, "2"), "dex"), Is.EqualTo("4"));
                ed.Set(c, Part(ed, c, "2"), "dex", "6");

                Plan p = PlanOf(f, mod);
                Assert.That(Dump(p), Is.Empty);
                string k = Text(p, K, ItemSetRules.InfoEntry);
                Assert.That(k, Does.Contain("<set id=\"99000005\" itemIDs=\"5001,5002\">"));
                Assert.That(k, Does.Contain("<part count=\"2\" dex=\"6\" />"));
                Assert.That(k, Does.Contain("<part count=\"2\" dex=\"4\" car=\"5\" />"));
                Assert.That(Text(p, S, ItemSetRules.InfoEntry), Does.Contain("<set id=\"99000005\" itemIDs=\"5001,5002\" feature=\"F\" optionID=\"99000005\" />"));
                string o = Text(p, S, ItemSetRules.OptionEntry);
                Assert.That(o, Does.Contain("<part count=\"2\" additionalEffectID=\"0\" additionalEffectLevel=\"1\" dex_value_base=\"6\" car_value_base=\"5\" />"));
                Assert.That(o, Does.Contain("dex_value_base=\"4\""));
            }
        }

        [Test]
        public void CarIsReadWhereItIsRead()
        {
            using (var f = new FakeSets())
            {
                // the kms part has no car; the server's value is the one shown, copied and kept
                Mod mod = Empty();
                var ed = new ItemSetEditor(f.Data, mod);
                ItemSet a = Find(ed, "8100001");
                ed.Set(a, Part(ed, a, "2"), "car", "7");
                Assert.That(ed.Get(Part(ed, a, "2"), "car"), Is.EqualTo("7"));
                Assert.That(ed.Summary(Part(ed, a, "2")), Does.Contain("car 7"));
                ed.Clone(a, 99000006);

                var again = new ItemSetEditor(f.Data, Reload(mod));
                Assert.That(again.Get(Part(again, Find(again, "8100001"), "2"), "car"), Is.EqualTo("7"));
                Assert.That(again.Get(Part(again, Find(again, "99000006"), "2"), "car"), Is.EqualTo("7"));

                Plan p = PlanOf(f, mod);
                Assert.That(p.Errors, Is.Empty);
                string o = Text(p, S, ItemSetRules.OptionEntry);
                int at = o.IndexOf("<option id=\"99000006\">", StringComparison.Ordinal);
                Assert.That(at, Is.GreaterThan(0));
                Assert.That(o.Substring(at), Does.Contain("<part count=\"2\" additionalEffectID=\"0\" additionalEffectLevel=\"1\" str_value_base=\"9\" car_value_base=\"7\" />"));
                string k = Text(p, K, ItemSetRules.InfoEntry);
                Assert.That(k.Substring(k.IndexOf("<set id=\"99000006\"", StringComparison.Ordinal)), Does.Not.Contain("car="));
            }
        }

        [Test]
        public void AddedBonusOfASideKeepsCarAndUnknownAttributes()
        {
            using (var f = new FakeSets())
            {
                // the gms client alone has a bonus for 1 item, with car and an attribute the editor has no field for
                Mod mod = FakeSets.ModOf(
                    "<edit archive=\"" + G + "\" entry=\"" + ItemSetRules.OptionEntry + "\">\n" +
                    "<add in=\"option[id=8100001]\"><xml><part count=\"1\" additionalEffectID=\"0\" additionalEffectLevel=\"1\" car_value_base=\"3\" zzz_rate_base=\"0.5\" /></xml></add>\n" +
                    "</edit>");
                var ed = new ItemSetEditor(f.Data, mod);
                ItemSet a = Find(ed, "8100001");
                Assert.That(ed.Get(Part(ed, a, "1"), "car"), Is.EqualTo("3"));
                ed.AddPart(a, 1);
                Plan p = PlanOf(f, mod);
                Assert.That(p.Errors, Is.Empty);
                Assert.That(Text(p, S, ItemSetRules.OptionEntry), Does.Contain("<part count=\"1\" additionalEffectID=\"0\" additionalEffectLevel=\"1\" car_value_base=\"3\" zzz_rate_base=\"0.5\" />"));
                Assert.That(Text(p, K, ItemSetRules.InfoEntry), Does.Contain("<part count=\"1\" />"));

                // a clone keeps it on the sides that have it
                ItemSet c = ed.Clone(a, 99000007);
                Assert.That(ed.Get(Part(ed, c, "1"), "car"), Is.EqualTo("3"));
                string o = Text(PlanOf(f, mod), S, ItemSetRules.OptionEntry);
                Assert.That(o.Substring(o.IndexOf("<option id=\"99000007\">", StringComparison.Ordinal)), Does.Contain("zzz_rate_base=\"0.5\""));
            }
        }

        [Test]
        public void EliteValueKeepsABossValueOfItsOwn()
        {
            var row = new Dictionary<string, string> { { "sgi_target", "4" }, { "sgi_rate_base", "0.01" }, { "sgi_boss_rate_base", "0.05" }, { "sgi_boss_target", "4" } };
            Func<string, string> get = k => { string v; return row.TryGetValue(k, out v) ? v : null; };
            Action<string, string> set = (k, v) => { if (v == null) row.Remove(k); else row[k] = v; };
            Assert.That(ItemSetRules.Read(get, false, "sgi_boss"), Is.EqualTo("50"));
            ItemSetRules.Write(get, set, false, "sgi_elite", "20");
            Assert.That(ItemSetRules.Read(get, false, "sgi_boss"), Is.EqualTo("50"));
            Assert.That(ItemSetRules.Read(get, false, "sgi_elite"), Is.EqualTo("20"));
        }

        [Test]
        public void RemovesAndRestoresAShippedSet()
        {
            using (var f = new FakeSets())
            {
                Mod mod = Empty();
                var ed = new ItemSetEditor(f.Data, mod);
                ItemSet a = Find(ed, "8100001");
                ed.Set(a, Part(ed, a, "2"), "str", "12");
                ed.Remove(a);
                Assert.That(a.IsDeleted, Is.True);
                Assert.Throws<ModKitException>(() => ed.SetMembers(a, new long[] { 5001 }));
                Plan p = PlanOf(f, mod);
                Assert.That(Dump(p), Is.Empty);
                Assert.That(Text(p, K, ItemSetRules.InfoEntry), Does.Not.Contain("8100001"));
                Assert.That(Text(p, S, ItemSetRules.InfoEntry), Does.Not.Contain("id=\"8100001\""));
                ed.Restore(a);
                ed.Revert(a);
                Assert.That(Body(mod).Trim(), Is.Empty);
            }
        }

        [Test]
        public void AShippedDoubleIdDoesNotBlockApply()
        {
            // the shipped kms table has an id twice: a mod that adds another set gets a warning, not an error
            string twice = FakeSets.KmsInfo.Replace("</ms2>", "\t<set id=\"8100077\" itemIDs=\"5001\" />\n\t<set id=\"8100077\" itemIDs=\"5002\" />\n</ms2>");
            using (var f = new FakeSets(twice))
            {
                Mod mod = FakeSets.ModOf(
                    "<edit archive=\"" + K + "\" entry=\"" + ItemSetRules.InfoEntry + "\">\n" +
                    "<add><xml><set id=\"99000001\" itemIDs=\"5001\"><part count=\"2\" str=\"1\" /></set></xml></add>\n</edit>\n");
                Plan p = PlanOf(f, mod);
                var errors = new List<string>();
                foreach (PlanMessage m in p.Errors) errors.Add(m.ToString());
                Assert.That(errors, Is.Empty);
                var w = new List<string>();
                foreach (PlanMessage m in p.Warnings) w.Add(m.ToString());
                Assert.That(w, Has.Some.Contains("set 8100077 is in the table 2 times"));
            }
        }

        [Test]
        public void PlanChecksTheFinalTables()
        {
            using (var f = new FakeSets())
            {
                // a set for the kms client alone, a second row of a shipped id, an item and an effect that are not there
                Mod mod = FakeSets.ModOf(
                    "<edit archive=\"" + K + "\" entry=\"" + ItemSetRules.InfoEntry + "\">\n" +
                    "<add><xml><set id=\"99000001\" itemIDs=\"5001,5009\"><part count=\"2\" str=\"1\" additionalEffectID=\"70000009\" additionalEffectLevel=\"1\" /><part count=\"2\" hp=\"1\" /></set></xml></add>\n" +
                    "<add><xml><set id=\"8100004\" itemIDs=\"5001\" /></xml></add>\n" +
                    "</edit>\n" +
                    "<edit archive=\"" + S + "\" entry=\"" + ItemSetRules.InfoEntry + "\">\n" +
                    "<add><xml><set id=\"99000002\" itemIDs=\"5004\" optionID=\"99000002\" /></xml></add>\n" +
                    "</edit>");
                Plan p = Planner.Build(f.Data, new[] { mod }, new PlanOptions());
                // what stops a side from loading its table is an error (Apply is refused), the rest a warning
                var errors = new List<string>();
                foreach (PlanMessage m in p.Errors) errors.Add(m.ToString());
                Assert.That(errors.Count, Is.EqualTo(3), string.Join("\n", errors.ToArray()));
                Assert.That(errors[0], Does.Contain("set 99000001: two bonuses for 2 items: the kms client fails to load the whole set table"));
                Assert.That(errors[1], Does.Contain("set 8100004 is in the table 2 times: the kms client fails to load the whole set table"));
                Assert.That(errors[2], Does.Contain("set 99000002: optionID \"99000002\" has no <option> row in " + ItemSetRules.OptionEntry + ": the server's data ingest fails"));
                Assert.That(errors[2], Does.Contain(S));
                int twice = 0;
                foreach (PlanMessage m in p.Warnings)
                {
                    string t = m.ToString();
                    Assert.That(t, Does.Not.Contain("fails"));
                    if (t.Contains("set 99000002")) { twice++; Assert.That(t, Does.Not.Contain(G)); }
                }
                Assert.That(twice, Is.EqualTo(1));     // "added here but not in": once, for the server, not for its mirror
                string w = Dump(p);
                Assert.That(w, Does.Contain("set 99000001: item 5009 is not an item of the "));
                Assert.That(w, Does.Contain("set 99000001: bonus for 2 items: effect 70000009 is not an additional effect of the "));
                Assert.That(w, Does.Contain("set 99000001: two bonuses for 2 items: the kms client fails to load the whole set table"));
                Assert.That(w, Does.Contain("set 99000001 is added here but not in " + S));
                Assert.That(w, Does.Contain("set 8100004 is in the table 2 times: the kms client fails to load the whole set table"));
                Assert.That(w, Does.Contain("set 99000002: optionID \"99000002\" has no <option> row in " + ItemSetRules.OptionEntry + ": the server's data ingest fails"));
                Assert.That(w, Does.Contain("set 99000002 is added here but not in " + K));
            }
        }

        [Test]
        public void PlanComparesANewSetBetweenTheSides()
        {
            using (var f = new FakeSets())
            {
                Mod mod = Empty();
                var ed = new ItemSetEditor(f.Data, mod);
                ItemSet n = ed.Add(99000000);
                ed.SetMembers(n, new long[] { 5001, 5003 });
                ed.SetName(n, "Reed Set");
                ed.AddPart(n, 2);
                ed.Set(n, Part(ed, n, "2"), "str", "7");
                string body = Body(mod).Replace("str_value_base=\"7\"", "str_value_base=\"8\"");
                Assert.That(body, Does.Contain("str_value_base=\"8\""));
                Plan p = Planner.Build(f.Data, new[] { FakeSets.ModOf(body) }, new PlanOptions());
                Assert.That(Dump(p), Does.Contain("set 99000000: the bonus for 2 items differs in str between " + K + " and " + S));

                var again = new ItemSetEditor(f.Data, FakeSets.ModOf(body));
                Assert.That(string.Join("\n", again.Check(Find(again, "99000000")).ToArray()), Does.Contain("bonus for 2 items: str differs between the sides"));
            }
        }
    }
}
