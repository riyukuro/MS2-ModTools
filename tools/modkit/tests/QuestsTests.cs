// The quest editor on made-up data: a kms-client with the sparse quest table and sparse per-id files, a
// gms-client and a server with the full per-id files, and quest text files in two layouts (one file per group,
// one file for all).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    public sealed class FakeQuests : IDisposable
    {
        public readonly string Dir;
        public readonly DataSet Data;
        public Mod Mod;
        readonly bool ownDir;
        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

        public const string Table = Head + "<ms2>\n" +
            "\t<quest id=\"701\">\n" +
            "\t\t<basic chapter=\"1\" />\n" +
            "\t\t<require level=\"5\" />\n" +
            "\t\t<start npc=\"31\" />\n" +
            "\t\t<complete npc=\"32\" />\n" +
            "\t\t<completeReward exp=\"100\" money=\"20\">\n" +
            "\t\t\t<v code=\"9001\" rank=\"1\" count=\"2\" />\n" +
            "\t\t</completeReward>\n" +
            "\t\t<condition type=\"npc\" code=\"41\" value=\"3\" />\n" +
            "\t\t<condition type=\"talk\" code=\"32\" />\n" +
            "\t</quest>\n" +
            // the sparse format: no level, no npcs, no reward
            "\t<quest id=\"702\">\n" +
            "\t\t<basic chapter=\"2\" />\n" +
            "\t\t<condition type=\"npc\" code=\"42\" value=\"10\" />\n" +
            "\t</quest>\n" +
            "\t<quest id=\"702\" feature=\"F1\">\n" +
            "\t\t<basic chapter=\"2\" />\n" +
            "\t\t<require level=\"9\" />\n" +
            "\t\t<condition type=\"npc\" code=\"42\" value=\"10\" />\n" +
            "\t</quest>\n" +
            "</ms2>\n";

        // the sparse copy the kms-client ships beside the table
        static string Sparse(long id)
        {
            return Head + "<ms2>\n\t<environment feature=\"\">\n\t\t<quest>\n\t\t\t<basic chapter=\"1\" questID=\"" + QuestEditor.Padded(id) + "\" />\n" +
                (id == 701 ? "\t\t\t<require level=\"5\" />\n\t\t\t<start npc=\"31\" />\n\t\t\t<complete npc=\"32\" />\n\t\t\t<completeReward exp=\"100\" money=\"20\">\n\t\t\t\t<essentialItem code=\"9001\" rank=\"1\" count=\"1\" />\n\t\t\t</completeReward>\n" +
                             "\t\t\t<condition type=\"npc\" code=\"41\" value=\"3\" />\n\t\t\t<condition type=\"talk\" code=\"32\" />\n"
                           : "\t\t\t<condition type=\"npc\" code=\"42\" value=\"10\" />\n") +
                "\t\t</quest>\n\t</environment>\n</ms2>\n";
        }

        static string Env(long id, string feature, int level, int start, int complete, int exp, int money, string items, string conds)
        {
            return "\t<environment feature=\"" + feature + "\" locale=\"\">\n\t\t<quest>\n" +
                "\t\t\t<basic chapter=\"1\" questID=\"" + QuestEditor.Padded(id) + "\" />\n" +
                "\t\t\t<require level=\"" + level + "\" />\n" +
                "\t\t\t<start npc=\"" + start + "\" />\n" +
                "\t\t\t<complete npc=\"" + complete + "\" map=\"0\" />\n" +
                "\t\t\t<completeReward exp=\"" + exp + "\" money=\"" + money + "\">\n" + items +
                "\t\t\t\t<globalEssentialItem code=\"0\" rank=\"0\" count=\"0\" />\n" +
                "\t\t\t\t<essentialJobItem code=\"0\" rank=\"0\" count=\"0\" />\n" +
                "\t\t\t\t<globalEssentialJobItem code=\"0\" rank=\"0\" count=\"0\" />\n" +
                "\t\t\t</completeReward>\n" + conds +
                "\t\t</quest>\n\t</environment>\n";
        }

        static string Item(string list, int code, int rank, int count) { return "\t\t\t\t<" + list + " code=\"" + code + "\" rank=\"" + rank + "\" count=\"" + count + "\" />\n"; }
        static string Cond(string type, int code, int value) { return "\t\t\t<condition type=\"" + type + "\" code=\"" + code + "\" value=\"" + value + "\" />\n"; }

        // the full format of the gms-client and the server
        static string Full(long id)
        {
            if (id == 701)
                return Head + "<ms2>\n" + Env(id, "", 5, 31, 32, 100, 20, Item("essentialItem", 9001, 1, 1) + Item("essentialItem", 9002, 1, 5), Cond("npc", 41, 3) + Cond("talk", 32, 0)) + "</ms2>\n";
            return Head + "<ms2>\n" + Env(id, "", 0, 0, 0, 0, 0, Item("essentialItem", 0, 0, 0), Cond("npc", 42, 10)) +
                Env(id, "F1", 9, 0, 0, 0, 0, Item("essentialItem", 0, 0, 0), Cond("npc", 42, 10)) + "</ms2>\n";
        }

        static string Row(long id, string name, string suffix)
        {
            return "\t<quest questID=\"" + QuestEditor.Padded(id) + "\" name=\"" + name + suffix + "\" desc=\"About " + name + suffix + "\" manual=\"Go|Talk\" complete=\"Done" + suffix + "\" count=\"2\" />\n";
        }

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        // a language with one file per quest group, or with one file for all
        static void Strings(List<KeyValuePair<string, byte[]>> l, string lang, bool groups)
        {
            string sfx = lang == "en" ? "" : " (" + lang + ")";
            if (groups)
            {
                l.Add(E("string/" + lang + "/questdescription_a.xml", Head + "<ms2>\n" + Row(701, "Fake Errand", sfx) + "</ms2>\n"));
                l.Add(E("string/" + lang + "/questdescription_b.xml", Head + "<ms2>\n" + Row(702, "Clay Hunt", sfx) + "</ms2>\n"));
            }
            else l.Add(E("string/" + lang + "/questdescription_final.xml", Head + "<ms2>\n" + Row(701, "Fake Errand", sfx) + Row(702, "Clay Hunt", sfx) + "</ms2>\n"));
        }

        /// <summary>Languages: kms-client en (group files) and zz (one file), gms-client en, server en and zz.</summary>
        public FakeQuests(bool kms, bool gms, bool server, string dir = null)
        {
            ownDir = dir == null;
            Dir = dir ?? Path.Combine(Path.GetTempPath(), "modkit-quests-" + Guid.NewGuid().ToString("N"));
            string k = null, g = null, s = null;
            if (kms)
            {
                k = Path.Combine(Dir, "kms"); Directory.CreateDirectory(k);
                var l = new List<KeyValuePair<string, byte[]>> { E("questdata/questdata.xml", Table), E("quest/00000701.xml", Sparse(701)), E("quest/00000702.xml", Sparse(702)) };
                Strings(l, "en", true); Strings(l, "zz", false);
                M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, l);
            }
            foreach (bool server2 in new[] { false, true })
            {
                if (server2 ? !server : !gms) continue;
                string d = Path.Combine(Dir, server2 ? "server" : "gms"); Directory.CreateDirectory(d);
                if (server2) s = d; else g = d;
                var l = new List<KeyValuePair<string, byte[]>> { E("quest/00000701.xml", Full(701)), E("quest/00000702.xml", Full(702)) };
                Strings(l, "en", true);
                if (server2) Strings(l, "zz", false);
                M2dWriter.Create(Path.Combine(d, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, l);
            }
            Data = new DataSet(k, g, s);
            Mod = Mod.Parse("<mod id=\"quests.test\" name=\"t\" version=\"1\"/>", "test mod");
        }

        public QuestEditor Editor() { return new QuestEditor(Data, Mod); }

        /// <summary>The mod written out and read again, as the view does on save and load.</summary>
        public void Reload() { Mod = Mod.Parse(Mod.ToXml(), "round trip"); }

        public Plan Plan()
        {
            Plan p = Planner.Build(Data, new[] { Mod.Parse(Mod.ToXml(), "round trip") }, new PlanOptions());
            string dump = Fake.Dump(p) + Mod.ToXml();
            Assert.That(p.Errors, Is.Empty, dump);
            Assert.That(p.Conflicts, Is.Empty, dump);
            Assert.That(p.Warnings, Is.Empty, dump);
            return p;
        }

        /// <summary>The planned text of an entry (LF line ends), or null when the plan does not write it.</summary>
        public static string Out(Plan p, string archive, string entry)
        {
            PlanArchive a = p.Find(archive);
            if (a == null) return null;
            foreach (KeyValuePair<string, byte[]> kv in a.Put)
                if (string.Equals(kv.Key.Replace('\\', '/'), entry, StringComparison.OrdinalIgnoreCase)) return new UTF8Encoding(false).GetString(kv.Value).Replace("\r\n", "\n");
            return null;
        }

        public void Dispose()
        {
            Data.Dispose();
            if (ownDir && Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class QuestsTests
    {
        const string K = QuestEditor.ClientXml, G = QuestEditor.GmsXml, S = QuestEditor.ServerXml;
        const string T = QuestEditor.TableEntry, F1 = "quest/00000701.xml", F2 = "quest/00000702.xml";

        [Test]
        public void Open_ListsEveryPlace_AndReadsTheFields()
        {
            using (var f = new FakeQuests(true, true, true))
            {
                QuestEditor e = f.Editor();
                Assert.That(e.TableXml, Is.EqualTo(K));
                Assert.That(e.FileXml, Is.EqualTo(S));
                QuestDraft d = e.Open(701);
                Assert.That(d.PlacesOf(QuestPlace.Table).Count, Is.EqualTo(1));
                Assert.That(d.PlacesOf(QuestPlace.File).Count, Is.EqualTo(2), "the kms-client copy and the server file");
                Assert.That(d.PlacesOf(QuestPlace.Text).Count, Is.EqualTo(4), "kms en + zz, server en + zz");
                Assert.That(d.Level, Is.EqualTo("5"));
                Assert.That(d.StartNpc, Is.EqualTo("31"));
                Assert.That(d.CompleteNpc, Is.EqualTo("32"));
                Assert.That(d.Exp, Is.EqualTo("100"));
                Assert.That(d.Meso, Is.EqualTo("20"));
                Assert.That(d.Differs("require@level"), Is.False);
                Assert.That(d.Name, Is.EqualTo("Fake Errand"));
                Assert.That(d.Languages(), Is.EqualTo(new[] { "en", "zz" }));
                Assert.That(d.Rewards("v").Count, Is.EqualTo(1));
                Assert.That(d.Rewards("v")[0].Count, Is.EqualTo("2"));
                List<QuestReward> ess = d.Rewards("essentialItem");
                Assert.That(ess.Count, Is.EqualTo(2));
                Assert.That(ess[0].Differs, Is.False);
                Assert.That(ess[1].Differs, Is.True, "the kms-client copy has one row only");
                Assert.That(d.Rewards("globalEssentialItem"), Is.Empty, "the code 0 row is an empty list");
                List<QuestCondition> c = d.Conditions();
                Assert.That(c.Count, Is.EqualTo(2));
                Assert.That(c[0].Value, Is.EqualTo("3"));
                Assert.That(c[1].Differs, Is.False, "no value and value 0 are the same");
                Assert.That(f.Mod.Ops, Is.Empty, "reading writes nothing");

                QuestDraft d2 = e.Open(702);
                Assert.That(d2.PlacesOf(QuestPlace.Table).Count, Is.EqualTo(2));
                Assert.That(d2.PlacesOf(QuestPlace.File).Count, Is.EqualTo(3));
                Assert.That(d2.Differs("require@level"), Is.True, "the F1 rows have level 9");
                Assert.Throws<ModKitException>(delegate { e.Open(799); });
            }
        }

        [Test]
        public void Find_ByIdAndName()
        {
            using (var f = new FakeQuests(true, false, true))
            {
                QuestEditor e = f.Editor();
                Assert.That(e.Find("", 10).Count, Is.EqualTo(2));
                Assert.That(e.Find("clay", 10)[0].Id, Is.EqualTo(702));
                Assert.That(e.Find("701", 10)[0].Name, Is.EqualTo("Fake Errand"));
                e.Language = "zz";
                Assert.That(e.Find("701", 10)[0].Name, Is.EqualTo("Fake Errand (zz)"));
            }
        }

        [Test]
        public void SetFields_WritesEveryTarget()
        {
            using (var f = new FakeQuests(true, true, true))
            {
                QuestDraft d = f.Editor().Open(701);
                Assert.That(d.SetLevel(12), Is.EqualTo(3));
                d.SetStartNpc(51); d.SetCompleteNpc(52); d.SetExp(900); d.SetMeso(77);
                f.Reload();
                d = f.Editor().Open(701);
                Assert.That(d.Level, Is.EqualTo("12"));
                Assert.That(d.Differs("start@npc"), Is.False);
                Plan p = f.Plan();
                string t = FakeQuests.Out(p, K, T);
                Assert.That(t, Does.Contain("<require level=\"12\" />").And.Contain("<start npc=\"51\" />").And.Contain("<complete npc=\"52\" />").And.Contain("<completeReward exp=\"900\" money=\"77\">"));
                Assert.That(t, Does.Contain("<quest id=\"702\" feature=\"F1\">\n\t\t<basic chapter=\"2\" />\n\t\t<require level=\"9\" />"), "the other quest is untouched");
                foreach (string a in new[] { K, G, S })
                {
                    string x = FakeQuests.Out(p, a, F1);
                    Assert.That(x, Is.Not.Null, a);
                    Assert.That(x, Does.Contain("<require level=\"12\" />").And.Contain("<start npc=\"51\" />").And.Contain("exp=\"900\" money=\"77\""), a);
                }
                Assert.That(FakeQuests.Out(p, G, F1), Is.EqualTo(FakeQuests.Out(p, S, F1)), "the gms-client gets the server's lines");

                // back to the shipped values: no line is left
                d.SetLevel(5); d.SetStartNpc(31); d.SetCompleteNpc(32); d.SetExp(100); d.SetMeso(20);
                Assert.That(f.Mod.Ops, Is.Empty);
            }
        }

        [Test]
        public void SetField_AddsTheElementTheSparseFormatLeavesOut()
        {
            using (var f = new FakeQuests(true, false, true))
            {
                QuestDraft d = f.Editor().Open(702);
                Assert.That(d.Get("start@npc"), Is.EqualTo("0"), "the table has none; the server file writes 0");
                Assert.That(d.SetStartNpc(0), Is.EqualTo(2), "a default adds nothing to the sparse places");
                Assert.That(f.Mod.Ops, Is.Empty);
                Assert.That(d.SetStartNpc(61), Is.EqualTo(5));
                d.SetExp(40);
                d.SetLevel(3);
                f.Reload();
                d = f.Editor().Open(702);
                Assert.That(d.Differs("start@npc"), Is.False);
                Assert.That(d.Differs("require@level"), Is.False);
                foreach (QuestPlace pl in d.PlacesOf(QuestPlace.Table)) Assert.That(d.Get(pl, "completeReward@exp"), Is.EqualTo("40"));
                // a second edit of an element the mod added rewrites that line
                d.SetStartNpc(62);
                Plan p = f.Plan();
                string t = FakeQuests.Out(p, K, T);
                Assert.That(t, Does.Contain("<quest id=\"702\">\n\t\t<basic chapter=\"2\" />\n\t\t<condition type=\"npc\" code=\"42\" value=\"10\" />\n\t\t<start npc=\"62\" />\n\t\t<completeReward exp=\"40\" />\n\t\t<require level=\"3\" />\n\t</quest>"));
                Assert.That(t, Does.Contain("<require level=\"3\" />\n\t\t<condition type=\"npc\" code=\"42\" value=\"10\" />\n\t\t<start npc=\"62\" />"), "the F1 row had a level");
                Assert.That(FakeQuests.Out(p, K, F2), Does.Contain("<start npc=\"62\" />").And.Contain("<completeReward exp=\"40\" />"));
                Assert.That(FakeQuests.Out(p, S, F2), Does.Not.Contain("<start npc=\"0\""));
                // taking the value back removes the added element
                d = f.Editor().Open(702);
                d.SetStartNpc(0);
                Assert.That(d.Get(d.PlacesOf(QuestPlace.Table)[0], "start@npc"), Is.EqualTo("0"));
            }
        }

        [Test]
        public void Rewards_SetAddRemove_PerList()
        {
            using (var f = new FakeQuests(true, true, true))
            {
                QuestDraft d = f.Editor().Open(701);
                d.SetReward("v", 0, new QuestItem(9001, 1, 7));
                Assert.That(d.AddReward("v", new QuestItem(9005, 2, 1)), Is.EqualTo(1));
                Assert.That(d.AddReward("essentialItem", new QuestItem(9005, 2, 1)), Is.EqualTo(2));
                // the empty list's code 0 row is taken, no second row
                Assert.That(d.AddReward("globalEssentialItem", new QuestItem(9006, 1, 3)), Is.EqualTo(2), "the kms-client copy gets the list");
                Assert.Throws<ModKitException>(delegate { d.AddReward("v", new QuestItem(9005, 2, 1)); });
                Assert.Throws<ModKitException>(delegate { d.AddReward("v", new QuestItem(0, 1, 1)); });
                Assert.Throws<ModKitException>(delegate { d.AddReward("nope", new QuestItem(1, 1, 1)); });
                Assert.Throws<ModKitException>(delegate { d.SetReward("v", 5, new QuestItem(1, 1, 1)); });
                f.Reload();
                d = f.Editor().Open(701);
                Assert.That(d.Rewards("v").Count, Is.EqualTo(2));
                Assert.That(d.Rewards("v")[1].Code, Is.EqualTo("9005"));
                Assert.That(d.Rewards("globalEssentialItem").Count, Is.EqualTo(1));
                Plan p = f.Plan();
                Assert.That(FakeQuests.Out(p, K, T), Does.Contain("<v code=\"9001\" rank=\"1\" count=\"7\" />\n\t\t\t<v code=\"9005\" rank=\"2\" count=\"1\" />\n\t\t</completeReward>"));
                string s = FakeQuests.Out(p, S, F1);
                Assert.That(s, Does.Contain("<essentialItem code=\"9002\" rank=\"1\" count=\"5\" />\n\t\t\t\t<essentialItem code=\"9005\" rank=\"2\" count=\"1\" />"));
                Assert.That(s, Does.Contain("<globalEssentialItem code=\"9006\" rank=\"1\" count=\"3\" />").And.Not.Contain("<globalEssentialItem code=\"0\""));
                Assert.That(FakeQuests.Out(p, G, F1), Is.EqualTo(s));
                Assert.That(FakeQuests.Out(p, K, F1), Does.Contain("<essentialItem code=\"9005\" rank=\"2\" count=\"1\" />"));

                // remove: an added row leaves the mod, a shipped one is deleted, the last one of a file becomes the code 0 row
                d.RemoveReward("v", 1);
                d.RemoveReward("v", 0);
                d.RemoveReward("globalEssentialItem", 0);
                d.RemoveReward("essentialItem", 2);
                d.RemoveReward("essentialItem", 1);
                d.RemoveReward("essentialItem", 0);
                f.Reload();
                d = f.Editor().Open(701);
                foreach (string list in QuestDraft.RewardLists) Assert.That(d.Rewards(list), Is.Empty, list);
                p = f.Plan();
                Assert.That(FakeQuests.Out(p, K, T), Does.Not.Contain("<v "));
                s = FakeQuests.Out(p, S, F1);
                Assert.That(s, Does.Contain("<completeReward exp=\"100\" money=\"20\">\n\t\t\t\t<essentialItem code=\"0\" rank=\"0\" count=\"0\" />\n\t\t\t\t<globalEssentialItem code=\"0\" rank=\"0\" count=\"0\" />"));
                // and a row can be added again after that
                d.AddReward("essentialItem", new QuestItem(9010, 1, 1));
                d.AddReward("v", new QuestItem(9010, 1, 1));
                p = f.Plan();
                Assert.That(FakeQuests.Out(p, S, F1), Does.Contain("<essentialItem code=\"9010\" rank=\"1\" count=\"1\" />"));
                Assert.That(FakeQuests.Out(p, K, T), Does.Contain("<v code=\"9010\" rank=\"1\" count=\"1\" />"));
            }
        }

        [Test]
        public void SetRewardItems_MakesTheListsEqualOnEveryTarget()
        {
            using (var f = new FakeQuests(true, true, true))
            {
                QuestDraft d = f.Editor().Open(701);
                var items = new List<QuestItem> { new QuestItem(9020, 1, 4), new QuestItem(9021, 3, 1), new QuestItem(9022, 1, 9) };
                Assert.That(d.SetRewardItems(items), Is.EqualTo(5), "v; essentialItem and globalEssentialItem in two files");
                Assert.Throws<ModKitException>(delegate { d.SetRewardItems(new List<QuestItem> { new QuestItem(1, 1, 1), new QuestItem(1, 1, 1) }); });
                f.Reload();
                d = f.Editor().Open(701);
                foreach (string list in new[] { "v", "essentialItem", "globalEssentialItem" })
                {
                    List<QuestReward> r = d.Rewards(list);
                    Assert.That(r.Count, Is.EqualTo(3), list);
                    for (int i = 0; i < 3; i++)
                    {
                        Assert.That(r[i].Code, Is.EqualTo(items[i].Code), list);
                        Assert.That(r[i].Count, Is.EqualTo(items[i].Count), list);
                        Assert.That(r[i].Differs, Is.False, list);
                    }
                }
                Assert.That(d.Rewards("essentialJobItem"), Is.Empty);
                Plan p = f.Plan();
                string want = " code=\"9020\" rank=\"1\" count=\"4\" />";
                Assert.That(FakeQuests.Out(p, K, T), Does.Contain("<v" + want));
                Assert.That(FakeQuests.Out(p, S, F1), Does.Contain("<essentialItem" + want).And.Contain("<globalEssentialItem" + want).And.Contain("<essentialJobItem code=\"0\""));
                Assert.That(FakeQuests.Out(p, G, F1), Is.EqualTo(FakeQuests.Out(p, S, F1)));
                // a shorter list
                d.SetRewardItems(new List<QuestItem> { new QuestItem(9020, 1, 4) });
                d.SetRewardItems(new List<QuestItem>());
                foreach (string list in QuestDraft.RewardLists) Assert.That(d.Rewards(list), Is.Empty, list);
                f.Plan();
            }
        }

        [Test]
        public void ConditionCount_And_Texts()
        {
            using (var f = new FakeQuests(true, true, true))
            {
                QuestDraft d = f.Editor().Open(701);
                Assert.That(d.SetConditionCount(0, 8), Is.EqualTo(3));
                Assert.That(d.SetConditionCount(1, 2), Is.EqualTo(3));
                Assert.Throws<ModKitException>(delegate { d.SetConditionCount(2, 2); });
                Assert.Throws<ModKitException>(delegate { d.SetConditionCount(0, 0); });
                Assert.That(d.SetText("en", "name", "Fake Errand 2"), Is.EqualTo(2));
                Assert.That(d.SetText("desc", "New <desc> & more"), Is.EqualTo(4));
                Assert.Throws<ModKitException>(delegate { d.SetText("count", "1"); });
                f.Reload();
                d = f.Editor().Open(701);
                Assert.That(d.Conditions()[0].Value, Is.EqualTo("8"));
                Assert.That(d.Conditions()[1].Differs, Is.False);
                Assert.That(d.Name, Is.EqualTo("Fake Errand 2"));
                Assert.That(d.Text("desc"), Is.EqualTo("New <desc> & more"));
                Plan p = f.Plan();
                Assert.That(FakeQuests.Out(p, K, T), Does.Contain("<condition type=\"npc\" code=\"41\" value=\"8\" />\n\t\t<condition type=\"talk\" code=\"32\" value=\"2\" />"));
                Assert.That(FakeQuests.Out(p, S, F1), Does.Contain("code=\"41\" value=\"8\"").And.Contain("code=\"32\" value=\"2\""));
                foreach (string a in new[] { K, G, S })
                    Assert.That(FakeQuests.Out(p, a, "string/en/questdescription_a.xml"), Does.Contain("name=\"Fake Errand 2\" desc=\"New &lt;desc> &amp; more\""), a);
                Assert.That(FakeQuests.Out(p, K, "string/zz/questdescription_final.xml"), Does.Contain("name=\"Fake Errand (zz)\" desc=\"New &lt;desc> &amp; more\"").And.Contain("name=\"Clay Hunt (zz)\" desc=\"About Clay Hunt (zz)\""));
                Assert.That(FakeQuests.Out(p, K, "string/en/questdescription_b.xml"), Is.Null);
            }
        }

        [Test]
        public void Clone_WritesEveryTarget_AndStaysEditable()
        {
            using (var f = new FakeQuests(true, true, true))
            {
                QuestEditor e = f.Editor();
                Assert.That(e.NextFreeId(701), Is.EqualTo(703));
                Assert.Throws<ModKitException>(delegate { e.Clone(701, 702); });
                Assert.Throws<ModKitException>(delegate { e.Clone(799, 750); });
                Assert.Throws<ModKitException>(delegate { e.Clone(701, 100000000); });
                Assert.That(f.Mod.Ops, Is.Empty);
                QuestDraft d = e.Clone(701, 750);
                Assert.That(d.IsNew, Is.True);
                Assert.Throws<ModKitException>(delegate { e.Clone(701, 750); });
                Assert.Throws<ModKitException>(delegate { e.Clone(750, 751); });
                Assert.That(e.NextFreeId(750), Is.EqualTo(751));
                Assert.That(f.Mod.Ids.Count, Is.EqualTo(1));
                Assert.That(f.Mod.Ids[0].Space, Is.EqualTo("quest"));
                Assert.That(f.Mod.Ids[0].Places.Count, Is.EqualTo(3));
                // edits of the new quest
                d.SetLevel(30); d.SetExp(5000); d.SetText("name", "Second Errand");
                d.SetConditionCount(0, 6);
                d.SetRewardItems(new List<QuestItem> { new QuestItem(9030, 1, 2), new QuestItem(9031, 1, 1) });
                f.Reload();
                e = f.Editor();
                Assert.That(e.AddedIds(), Is.EqualTo(new[] { 750L }));
                List<QuestHit> hits = e.Find("", 10);
                Assert.That(hits[0].Id, Is.EqualTo(750));
                Assert.That(hits[0].Added, Is.True);
                Assert.That(hits[0].Name, Is.EqualTo("Second Errand"));
                d = e.Open(750);
                Assert.That(d.PlacesOf(QuestPlace.Table).Count, Is.EqualTo(1));
                Assert.That(d.PlacesOf(QuestPlace.File).Count, Is.EqualTo(2));
                Assert.That(d.PlacesOf(QuestPlace.Text).Count, Is.EqualTo(4));
                Assert.That(d.Level, Is.EqualTo("30"));
                Assert.That(d.Differs("require@level"), Is.False);
                Assert.That(d.StartNpc, Is.EqualTo("31"));
                Assert.That(d.Conditions()[0].Value, Is.EqualTo("6"));
                Assert.That(d.Rewards("v").Count, Is.EqualTo(2));
                Assert.That(d.Rewards("essentialItem")[1].Code, Is.EqualTo("9031"));
                Assert.That(d.Rewards("essentialItem")[1].Differs, Is.False);
                // the source is as it was
                Assert.That(e.Open(701).Level, Is.EqualTo("5"));
                Assert.That(e.Open(701).Name, Is.EqualTo("Fake Errand"));

                Plan p = f.Plan();
                string t = FakeQuests.Out(p, K, T);
                Assert.That(t, Does.Contain("<quest id=\"750\">\n\t\t<basic chapter=\"1\" />\n\t\t<require level=\"30\" />"));
                Assert.That(t, Does.Contain("<quest id=\"701\">\n\t\t<basic chapter=\"1\" />\n\t\t<require level=\"5\" />"));
                Assert.That(t.IndexOf("id=\"750\"", StringComparison.Ordinal), Is.LessThan(t.IndexOf("id=\"702\"", StringComparison.Ordinal)), "the new row follows its source");
                foreach (string a in new[] { K, G, S })
                {
                    string x = FakeQuests.Out(p, a, "quest/00000750.xml");
                    Assert.That(x, Is.Not.Null, a);
                    Assert.That(x, Does.Contain("questID=\"00000750\"").And.Contain("<require level=\"30\" />").And.Contain("exp=\"5000\"").And.Contain("<essentialItem code=\"9031\" rank=\"1\" count=\"1\" />"), a);
                    Assert.That(FakeQuests.Out(p, a, F1), Is.Null, "the source file is not written: " + a);
                    string st = FakeQuests.Out(p, a, "string/en/questdescription_a.xml");
                    Assert.That(st, Does.Contain("questID=\"00000750\" name=\"Second Errand\" desc=\"About Fake Errand\"").And.Contain("questID=\"00000701\" name=\"Fake Errand\""), a);
                }
                Assert.That(FakeQuests.Out(p, G, "quest/00000750.xml"), Is.EqualTo(FakeQuests.Out(p, S, "quest/00000750.xml")));
                Assert.That(FakeQuests.Out(p, S, "string/zz/questdescription_final.xml"), Does.Contain("questID=\"00000750\" name=\"Second Errand\""));
                Assert.That(e.Registration(750).Count, Is.EqualTo(6));

                e.RemoveClone(750);
                Assert.That(f.Mod.Ops, Is.Empty);
                Assert.That(f.Mod.Ids, Is.Empty);
                Assert.Throws<ModKitException>(delegate { e.RemoveClone(750); });
            }
        }

        [Test]
        public void Clone_OfAQuestWithFeatureRows()
        {
            using (var f = new FakeQuests(true, false, true))
            {
                QuestEditor e = f.Editor();
                QuestDraft d = e.Clone(702, 760);
                Assert.That(d.PlacesOf(QuestPlace.Table).Count, Is.EqualTo(2));
                Assert.That(d.PlacesOf(QuestPlace.File).Count, Is.EqualTo(3));
                d.SetStartNpc(71);
                d.AddReward("v", new QuestItem(9040, 1, 1));
                f.Reload();
                d = f.Editor().Open(760);
                Assert.That(d.Differs("start@npc"), Is.False);
                Assert.That(d.Rewards("v")[0].Places, Is.EqualTo(2));
                Plan p = f.Plan();
                string t = FakeQuests.Out(p, K, T);
                Assert.That(t, Does.Contain("<quest id=\"760\">").And.Contain("<quest id=\"760\" feature=\"F1\">"));
                Assert.That(t, Does.Contain("<start npc=\"71\" />\n\t\t<completeReward>\n\t\t\t<v code=\"9040\" rank=\"1\" count=\"1\" />\n\t\t</completeReward>\n\t</quest>"));
                string s = FakeQuests.Out(p, S, "quest/00000760.xml");
                Assert.That(s.Split(new[] { "questID=\"00000760\"" }, StringSplitOptions.None).Length, Is.EqualTo(3), "both environments");
                Assert.That(s.Split(new[] { "<start npc=\"71\" />" }, StringSplitOptions.None).Length, Is.EqualTo(3));
            }
        }

        [Test]
        public void OneSide_Only()
        {
            using (var f = new FakeQuests(true, false, false))
            {
                QuestEditor e = f.Editor();
                Assert.That(e.FileXml, Is.Null);
                QuestDraft d = e.Open(701);
                Assert.That(d.SetLevel(8), Is.EqualTo(2));
                Assert.That(d.HasList("essentialItem"), Is.True);
                e.Clone(701, 770);
                Plan p = f.Plan();
                Assert.That(FakeQuests.Out(p, K, "quest/00000770.xml"), Does.Contain("questID=\"00000770\""));
            }
            using (var f = new FakeQuests(false, true, false))
            {
                QuestEditor e = f.Editor();
                Assert.That(e.TableXml, Is.Null);
                Assert.That(e.FileXml, Is.EqualTo(G));
                QuestDraft d = e.Open(701);
                Assert.That(d.HasList("v"), Is.False);
                Assert.Throws<ModKitException>(delegate { d.AddReward("v", new QuestItem(1, 1, 1)); });
                d.SetLevel(8);
                e.Clone(701, 770).SetText("name", "Third");
                Plan p = f.Plan();
                Assert.That(FakeQuests.Out(p, G, F1), Does.Contain("<require level=\"8\" />"));
                Assert.That(FakeQuests.Out(p, G, "string/en/questdescription_a.xml"), Does.Contain("questID=\"00000770\" name=\"Third\""));
            }
            using (var f = new FakeQuests(false, false, false))
                Assert.That(f.Editor().HasData, Is.False);
        }
    }
}
