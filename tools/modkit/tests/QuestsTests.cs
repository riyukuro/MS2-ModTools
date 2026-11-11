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
            "\t\t<acceptReward>\n" +
            "\t\t\t<v code=\"9100\" rank=\"1\" count=\"1\" />\n" +
            "\t\t</acceptReward>\n" +
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
                (id == 701 ? "\t\t\t<require level=\"5\" />\n\t\t\t<start npc=\"31\" />\n\t\t\t<complete npc=\"32\" />\n\t\t\t<acceptReward>\n\t\t\t\t<essentialItem code=\"9100\" rank=\"1\" count=\"1\" />\n\t\t\t</acceptReward>\n\t\t\t<completeReward exp=\"100\" money=\"20\">\n\t\t\t\t<essentialItem code=\"9001\" rank=\"1\" count=\"1\" />\n\t\t\t</completeReward>\n" +
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
                (id == 701 ? "\t\t\t<acceptReward>\n" + Item("essentialItem", 9100, 1, 1) + "\t\t\t</acceptReward>\n" : "\t\t\t<acceptReward />\n") +
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

        // the files that list quests by id: the talk script (one file in the kms-client, group files elsewhere),
        // the quest's fields and its special type name
        static string Script(long id, string ask)
        {
            return "\t<quest id=\"" + id + "\">\n" +
                "\t\t<script id=\"100\">\n\t\t\t<content text=\"$script:" + id + "100$\">\n\t\t\t\t<distractor text=\"$script:" + id + "101$\" goto=\"200\" />\n\t\t\t</content>\n\t\t</script>\n" +
                "\t\t<script id=\"200\" feature=\"F1\">\n\t\t\t<content text=\"$script:" + id + "200$\" voiceID=\"" + ask + "\" />\n\t\t</script>\n" +
                "\t</quest>\n";
        }

        static void Listed(List<KeyValuePair<string, byte[]>> l, bool oneFile)
        {
            l.Add(E("script/quest/questscript_" + (oneFile ? "final" : "a") + ".xml", Head + "<ms2>\n" + Script(701, "v1") + (oneFile ? Script(702, "v2") : "") + "</ms2>\n"));
            if (!oneFile) l.Add(E("script/quest/questscript_b.xml", Head + "<ms2>\n" + Script(702, "v2") + "</ms2>\n"));
            l.Add(E("table/questtofieldtable.xml", Head + "<ms2>\n\t<quest id=\"701\" fields=\"61,62\" />\n\t<quest id=\"702\" fields=\"63\" />\n</ms2>\n"));
            l.Add(E("string/en/specialquesttype.xml", Head + "<ms2>\n\t<key id=\"701\" name=\"Fake Type\" />\n</ms2>\n"));
        }

        // the server-only archive: several rows per quest, two of them with the same quest and script
        const string ScriptCondition = Head + "<ms2>\n" +
            "\t<condition questID=\"701\" scriptID=\"100\" item=\"9001\" />\n" +
            "\t<condition questID=\"701\" scriptID=\"100\" feature=\"F1\" item=\"9002\" />\n" +
            "\t<condition questID=\"702\" scriptID=\"100\" item=\"9003\" />\n" +
            "\t<condition questID=\"701\" scriptID=\"200\" item=\"9004\" />\n" +
            "</ms2>\n";
        // with twins the server ships this row twice
        const string Twin = "\t<condition questID=\"701\" scriptID=\"200\" item=\"9004\" />\n";
        const string ScriptFunction = Head + "<ms2>\n" +
            "\t<function questID=\"701\" scriptID=\"100\" functionID=\"1\" presentItemID=\"9001\" />\n" +
            "\t<function questID=\"701\" scriptID=\"100\" functionID=\"2\" emoticon=\"wave\" />\n" +
            "</ms2>\n";

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
        public FakeQuests(bool kms, bool gms, bool server, string dir = null, bool twins = false)
        {
            ownDir = dir == null;
            Dir = dir ?? Path.Combine(Path.GetTempPath(), "modkit-quests-" + Guid.NewGuid().ToString("N"));
            string k = null, g = null, s = null;
            if (kms)
            {
                k = Path.Combine(Dir, "kms"); Directory.CreateDirectory(k);
                var l = new List<KeyValuePair<string, byte[]>> { E("questdata/questdata.xml", Table), E("quest/00000701.xml", Sparse(701)), E("quest/00000702.xml", Sparse(702)) };
                Strings(l, "en", true); Strings(l, "zz", false);
                Listed(l, true);
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
                Listed(l, false);
                if (server2)
                    M2dWriter.Create(Path.Combine(d, "Server.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>> {
                        E("table/Server/QuestScriptCondition_final.xml", twins ? ScriptCondition.Replace(Twin, Twin + Twin) : ScriptCondition), E("table/Server/QuestScriptFunction_final.xml", ScriptFunction) });
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
                Assert.That(d.SetStartNpc(0), Is.EqualTo(2), "only the server's files hold the 0");
                Assert.That(d.Get(d.PlacesOf(QuestPlace.Table)[0], "start@npc"), Is.Null);
                Assert.That(f.Mod.ToXml(), Does.Not.Contain("start npc").And.Contain("completeReward exp=\"40\""), "the other added elements stay");
                p = f.Plan();
                Assert.That(FakeQuests.Out(p, K, T), Does.Not.Contain("<start npc=\"0\"").And.Not.Contain("<start npc=\"62\""));
            }
        }

        [Test]
        public void SetField_TakenBack_LeavesNoLine()
        {
            using (var f = new FakeQuests(true, false, true))
            {
                QuestDraft d = f.Editor().Open(702);
                Assert.That(d.SetStartNpc(61), Is.EqualTo(5));
                Assert.That(f.Mod.Ops.Count, Is.GreaterThan(0));
                d.SetStartNpc(0);
                Assert.That(f.Mod.Ops, Is.Empty, f.Mod.ToXml());
                // the same after a save and load
                d.SetStartNpc(61);
                f.Reload();
                f.Editor().Open(702).SetStartNpc(0);
                Assert.That(f.Mod.Ops, Is.Empty, f.Mod.ToXml());
            }
        }

        [Test]
        public void ModLines_CountsWhatACloneWouldNotCopy()
        {
            using (var f = new FakeQuests(true, true, true))
            {
                QuestEditor ed = f.Editor();
                Assert.That(ed.ModLines(701), Is.EqualTo(0));
                ed.Open(701).SetText("name", "Other Errand");
                int text = ed.ModLines(701);
                Assert.That(text, Is.GreaterThan(0));
                ed.Open(701).SetStartNpc(61);
                Assert.That(ed.ModLines(701), Is.GreaterThan(text));
                Assert.That(ed.ModLines(702), Is.EqualTo(0));
                // a clone is no line on its source, and its own rows are not lines on a shipped quest
                long id = ed.NextFreeId(703);
                QuestDraft made = ed.Clone(702, id);
                Assert.That(ed.ModLines(702), Is.EqualTo(0));
                made.SetStartNpc(61);
                Assert.That(ed.ModLines(id), Is.EqualTo(0));
                Assert.That(ed.ModLines(702), Is.EqualTo(0));
                // the same after a save and load
                f.Reload();
                Assert.That(f.Editor().ModLines(701), Is.GreaterThan(text));
                Assert.That(f.Editor().ModLines(702), Is.EqualTo(0));
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

                // remove: an added row leaves the mod, a shipped one is deleted, the code 0 row of an empty list comes back as it shipped
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
                Assert.That(FakeQuests.Out(p, K, T), Does.Not.Contain("<v code=\"90"));
                s = FakeQuests.Out(p, S, F1);
                Assert.That(s, Does.Contain("<completeReward exp=\"100\" money=\"20\">\n\t\t\t\t<globalEssentialItem code=\"0\" rank=\"0\" count=\"0\" />"));
                Assert.That(s, Does.Contain("</acceptReward>\n\t\t\t<completeReward exp=\"100\" money=\"20\">\n\t\t\t\t<globalEssentialItem"), "a removed row is gone, not a code 0 row");
                Assert.That(FakeQuests.Out(p, K, F1), Does.Contain("<completeReward exp=\"100\" money=\"20\">\n\t\t\t</completeReward>"));
                // and a row can be added again after that
                d.AddReward("essentialItem", new QuestItem(9010, 1, 1));
                d.AddReward("v", new QuestItem(9010, 1, 1));
                p = f.Plan();
                Assert.That(FakeQuests.Out(p, S, F1), Does.Contain("<essentialItem code=\"9010\" rank=\"1\" count=\"1\" />"));
                Assert.That(FakeQuests.Out(p, K, T), Does.Contain("<v code=\"9010\" rank=\"1\" count=\"1\" />"));
            }
        }

        [Test]
        public void RemoveReward_OfAnAddedRow_LeavesNoLine()
        {
            using (var f = new FakeQuests(true, true, true))
            {
                QuestDraft d = f.Editor().Open(701);
                d.AddReward("essentialItem", new QuestItem(9005, 2, 1));
                d.AddReward("globalEssentialItem", new QuestItem(9006, 1, 3));
                f.Reload();
                d = f.Editor().Open(701);
                List<QuestReward> l = d.Rewards("essentialItem");
                Assert.That(l.ConvertAll(x => x.Code), Is.EqualTo(new[] { "9001", "9002", "9005" }), "in the order of the longer list");
                Assert.That(l[1].Differs, Is.True);
                Assert.That(l[2].Differs, Is.False);
                d.RemoveReward("essentialItem", 2);
                d.RemoveReward("globalEssentialItem", 0);
                Assert.That(f.Mod.Ops, Is.Empty, f.Mod.ToXml());
            }
        }

        [Test]
        public void Rewards_ArePairedByItem_WhereThePlacesHoldDifferentLists()
        {
            string xml;
            using (var k = new FakeQuests(true, false, false))
            {
                // the kms-client copy alone: its one row becomes the item the other two have second
                k.Editor().Open(701).SetReward("essentialItem", 0, new QuestItem(9002, 1, 5));
                xml = k.Mod.ToXml();
            }
            using (var f = new FakeQuests(true, true, true))
            {
                f.Mod = Mod.Parse(xml, "made on one target");
                QuestDraft d = f.Editor().Open(701);
                List<QuestReward> l = d.Rewards("essentialItem");
                Assert.That(l.Count, Is.EqualTo(2));
                Assert.That(l[0].Code, Is.EqualTo("9001"));
                Assert.That(l[0].Places, Is.EqualTo(1));
                Assert.That(l[0].Differs, Is.True);
                Assert.That(l[0].In.Count, Is.EqualTo(2), "the kms-client file and the one the gms-client and the server share");
                Assert.That(l[0].Values, Is.EqualTo(new[] { null, "9001 x1 (rank 1)" }));
                Assert.That(l[1].Code, Is.EqualTo("9002"));
                Assert.That(l[1].Places, Is.EqualTo(2));
                Assert.That(l[1].Differs, Is.False);
                // a row is one item everywhere: the count goes to the 9002 rows, the 9001 row stays
                Assert.That(d.SetReward("essentialItem", 1, new QuestItem(9002, 1, 8)), Is.EqualTo(2));
                Assert.Throws<ModKitException>(delegate { d.SetReward("essentialItem", 0, new QuestItem(9002, 1, 8)); }, "a place would hold the item twice");
                Assert.Throws<ModKitException>(delegate { d.RemoveReward("essentialItem", 2); });
                Plan p = f.Plan();
                Assert.That(FakeQuests.Out(p, K, F1), Does.Contain("<essentialItem code=\"9002\" rank=\"1\" count=\"8\" />").And.Not.Contain("code=\"9001\""));
                Assert.That(FakeQuests.Out(p, S, F1), Does.Contain("<essentialItem code=\"9001\" rank=\"1\" count=\"1\" />\n\t\t\t\t<essentialItem code=\"9002\" rank=\"1\" count=\"8\" />"));
                Assert.That(d.RemoveReward("essentialItem", 1), Is.EqualTo(2));
                l = d.Rewards("essentialItem");
                Assert.That(l.Count, Is.EqualTo(1));
                Assert.That(l[0].Code, Is.EqualTo("9001"));
                p = f.Plan();
                Assert.That(FakeQuests.Out(p, K, F1), Does.Contain("<completeReward exp=\"100\" money=\"20\">\n\t\t\t</completeReward>"));
                Assert.That(FakeQuests.Out(p, S, F1), Does.Contain("<essentialItem code=\"9001\" rank=\"1\" count=\"1\" />").And.Not.Contain("code=\"9002\""));
                Assert.That(FakeQuests.Out(p, G, F1), Is.EqualTo(FakeQuests.Out(p, S, F1)));
            }
        }

        [Test]
        public void NewIds_SkipWhatOtherEnabledModsReserve()
        {
            using (var f = new FakeQuests(true, true, true))
            {
                QuestEditor e = f.Editor();
                Mod other = Mod.Parse("<mod id=\"other.mod\" name=\"o\" version=\"1\"><ids space=\"quest\" from=\"703\" to=\"710\"/><ids space=\"item\" from=\"711\" to=\"720\"/></mod>", "other");
                e.Others = delegate { return new[] { other, f.Mod }; };
                Assert.That(e.NextFreeId(701), Is.EqualTo(711), "the item range is another id space");
                ModKitException x = Assert.Throws<ModKitException>(delegate { e.Clone(701, 705); });
                Assert.That(x.Message, Is.EqualTo("the quest id 705 is reserved by the enabled mod other.mod"));
                Assert.That(f.Mod.Ops, Is.Empty);
                e.Clone(701, 711);
                Assert.That(e.NextFreeId(701), Is.EqualTo(712), "the mod's own range does not hide its free ids");
                f.Plan();
            }
        }

        [Test]
        public void Session_AppliesAndRevertsAQuestPlan()
        {
            using (var f = new FakeQuests(true, true, true))
            using (var s = new EditorSession(Path.Combine(f.Dir, "kms"), Path.Combine(f.Dir, "gms"), Path.Combine(f.Dir, "server"), Path.Combine(f.Dir, "mods"), Path.Combine(f.Dir, "ws")))
            {
                s.Adopt(f.Mod);
                QuestEditor e = s.Quests();
                QuestDraft d = e.Open(701);
                d.SetLevel(9); d.SetText("name", "Renamed Errand");
                d.AddReward("essentialItem", new QuestItem(9005, 2, 1));
                e.Clone(701, 750).SetExp(77);
                ApplyResult r = s.Apply();
                Assert.That(r.Applied, Is.True, s.Describe(r));
                Assert.That(r.Outputs.Count, Is.EqualTo(4), s.Describe(r));
                string k = Applied(r, K, T), sv = Applied(r, S, F1);
                Assert.That(k, Does.Contain("<quest id=\"701\">\n\t\t<basic chapter=\"1\" />\n\t\t<require level=\"9\" />").And.Contain("<quest id=\"750\">"));
                Assert.That(sv, Does.Contain("<require level=\"9\" />").And.Contain("<essentialItem code=\"9005\" rank=\"2\" count=\"1\" />"));
                Assert.That(Applied(r, G, F1), Is.EqualTo(sv));
                foreach (string a in new[] { K, G, S })
                {
                    Assert.That(Applied(r, a, "quest/00000750.xml"), Does.Contain("questID=\"00000750\"").And.Contain("exp=\"77\""), a);
                    Assert.That(Applied(r, a, "string/en/questdescription_a.xml"), Does.Contain("questID=\"00000701\" name=\"Renamed Errand\""), a);
                    Assert.That(Applied(r, a, QF), Does.Contain("<quest id=\"750\" fields=\"61,62\" />"), a);
                    Assert.That(Applied(r, a, "quest/00000702.xml"), Is.Not.Null, "an entry the mod leaves alone is kept: " + a);
                }
                Assert.That(Count(Applied(r, M, MC), "questID=\"750\""), Is.EqualTo(3));
                // the applied files read back as a data set show the edits as shipped values
                using (var again = new DataSet(Path.Combine(f.Dir, "ws", "client"), Path.Combine(f.Dir, "ws", "gms-client"), Path.Combine(f.Dir, "ws", "server")))
                {
                    var e2 = new QuestEditor(again, Mod.Parse("<mod id=\"second\" name=\"t\" version=\"1\"/>", "second"));
                    QuestDraft d2 = e2.Open(750);
                    Assert.That(d2.Exp, Is.EqualTo("77"));
                    Assert.That(d2.Differs("completeReward@exp"), Is.False);
                    Assert.That(e2.Open(701).Name, Is.EqualTo("Renamed Errand"));
                    Assert.That(e2.Open(701).Rewards("essentialItem").Count, Is.EqualTo(3));
                }
                var paths = new List<string>();
                foreach (ApplyOutput o in r.Outputs) paths.Add(o.Path);
                r = s.Revert();
                Assert.That(r.Applied, Is.True, s.Describe(r));
                foreach (string path in paths) Assert.That(File.Exists(path), Is.False, path);
                // and the shipped archives were never written
                using (var shipped = new FakeQuests(true, true, true))
                    foreach (string sub in new[] { "kms/Xml.m2d", "gms/Xml.m2d", "server/Xml.m2d", "server/Server.m2d" })
                        Assert.That(File.ReadAllBytes(Path.Combine(f.Dir, sub)), Is.EqualTo(File.ReadAllBytes(Path.Combine(shipped.Dir, sub))), sub);
            }
        }

        [Test]
        public void Session_NewIds_SkipTheOtherEnabledMods()
        {
            using (var f = new FakeQuests(true, true, true))
            using (var s = new EditorSession(Path.Combine(f.Dir, "kms"), Path.Combine(f.Dir, "gms"), Path.Combine(f.Dir, "server"), Path.Combine(f.Dir, "mods"), Path.Combine(f.Dir, "ws")))
            {
                s.Adopt(f.Mod);
                Assert.That(s.Quests().NextFreeId(701), Is.EqualTo(703));
                string other = Path.Combine(Path.Combine(f.Dir, "mods"), "other.mod");
                Directory.CreateDirectory(other);
                File.WriteAllText(Path.Combine(other, "mod.xml"), "<mod id=\"other.mod\" name=\"o\" version=\"1\">\n  <ids space=\"quest\" from=\"703\" to=\"704\"/>\n</mod>\n", new UTF8Encoding(false));
                QuestEditor e = s.Quests();
                Assert.That(e.NextFreeId(701), Is.EqualTo(705));
                Assert.Throws<ModKitException>(delegate { e.Clone(701, 703); });
                // a disabled mod reserves nothing
                ModList l = s.ModList();
                l.FindId("other.mod").Enabled = false;
                l.Save();
                Assert.That(e.NextFreeId(701), Is.EqualTo(703));
            }
        }

        [Test]
        public void Session_NewIds_AnUnreadableModHidesOnlyItself()
        {
            using (var f = new FakeQuests(true, true, true))
            using (var s = new EditorSession(Path.Combine(f.Dir, "kms"), Path.Combine(f.Dir, "gms"), Path.Combine(f.Dir, "server"), Path.Combine(f.Dir, "mods"), Path.Combine(f.Dir, "ws")))
            {
                s.Adopt(f.Mod);
                string mods = Path.Combine(f.Dir, "mods");
                foreach (string name in new[] { "a.bad", "other.mod", "z.bad" }) Directory.CreateDirectory(Path.Combine(mods, name));
                File.WriteAllText(Path.Combine(Path.Combine(mods, "other.mod"), "mod.xml"), "<mod id=\"other.mod\" name=\"o\" version=\"1\">\n  <ids space=\"quest\" from=\"703\" to=\"704\"/>\n</mod>\n", new UTF8Encoding(false));
                QuestEditor e = s.Quests();
                Assert.That(e.NextFreeId(701), Is.EqualTo(705));
                Assert.That(s.ModWarnings, Is.Empty);
                // a mod.xml that is cut off, before and behind the good mod in the list
                File.WriteAllText(Path.Combine(Path.Combine(mods, "a.bad"), "mod.xml"), "<mod id=\"a.bad\" name=\"b\" version=\"1\">\n  <ids space=\"quest\" fr", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(Path.Combine(mods, "z.bad"), "mod.xml"), "<mod id=\"z.bad\"", new UTF8Encoding(false));
                Assert.That(e.NextFreeId(701), Is.EqualTo(705), "the readable mod still reserves its ids");
                Assert.That(s.ModWarnings.Count, Is.EqualTo(2), string.Join("\n", s.ModWarnings.ToArray()));
                Assert.That(s.ModWarnings[0], Does.StartWith("mod folder a.bad cannot be read, so the ids it reserves are not known: "));
                Assert.That(s.ModWarnings[1], Does.StartWith("mod folder z.bad cannot be read"));
                Assert.Throws<ModKitException>(delegate { e.Clone(701, 703); });
                e.Clone(701, 705);
                Assert.That(s.ModWarnings.Count, Is.EqualTo(2), "said again with the clone");
            }
        }

        [Test]
        public void ShippedRows_ArePairedByItemRankAndCount()
        {
            Func<string, string, string, QuestReward> row = (c, r, n) => new QuestReward { Code = c, Rank = r, Count = n };
            var shipped = new List<QuestReward> { row("9001", "1", "1"), row("9001", "4", "1"), row("9002", "1", "5") };
            // nothing changed: each row has its own shipped row, also the second of the same item
            QuestReward[] was = QuestDraft.PairShipped(shipped, new List<QuestReward> { row("9001", "1", "1"), row("9001", "4", "1"), row("9002", "1", "5") });
            Assert.That(was, Is.EqualTo(new[] { shipped[0], shipped[1], shipped[2] }));
            // the rank 4 row first, the count of the other changed: still the exact one, then the one of the item that is left
            was = QuestDraft.PairShipped(shipped, new List<QuestReward> { row("9001", "4", "1"), row("9001", "1", "3"), row("9002", "1", "5") });
            Assert.That(was, Is.EqualTo(new[] { shipped[1], shipped[0], shipped[2] }));
            // a third row of the item is new; an item that replaced one takes the row at its index
            was = QuestDraft.PairShipped(shipped, new List<QuestReward> { row("9001", "1", "1"), row("9001", "4", "1"), row("9007", "1", "5"), row("9001", "2", "1") });
            Assert.That(was, Is.EqualTo(new[] { shipped[0], shipped[1], shipped[2], null }));
            Assert.That(QuestDraft.PairShipped(null, new List<QuestReward> { row("9001", "1", "1") }), Is.EqualTo(new QuestReward[] { null }));

            // on a draft: one edit in a list with two rows of one item marks that row only
            using (var f = new FakeQuests(true, true, true))
            {
                QuestDraft d = f.Editor().Open(701);
                d.AddReward("essentialItem", new QuestItem(9001, 4, 1));
                List<QuestReward> ship = new QuestEditor(f.Data, new Mod { Id = "shipped", Name = "shipped" }).Open(701).Rewards("essentialItem"), now = d.Rewards("essentialItem");
                QuestReward[] w = QuestDraft.PairShipped(ship, now);
                for (int i = 0; i < now.Count; i++)
                    Assert.That(w[i] == null, Is.EqualTo(now[i].Rank == "4"), "row " + i + ": only the added row is new");
            }
        }

        /// <summary>The text of an entry in the archive an apply wrote (LF line ends), or null when it is not there.</summary>
        static string Applied(ApplyResult r, string archive, string entry)
        {
            foreach (ApplyOutput o in r.Outputs)
            {
                if (o.Archive != archive) continue;
                using (M2dArchive a = M2dArchive.Open(o.Path))
                {
                    M2dEntry e = a.Find(entry);
                    return e == null ? null : new UTF8Encoding(false).GetString(a.Read(e)).Replace("\r\n", "\n");
                }
            }
            return null;
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
                Assert.That(e.Registration(750).Count, Is.EqualTo(14), "6 and the 8 files that list the quest");

                e.RemoveClone(750);
                Assert.That(f.Mod.Ops, Is.Empty);
                Assert.That(f.Mod.Ids, Is.Empty);
                Assert.Throws<ModKitException>(delegate { e.RemoveClone(750); });
            }
        }

        const string QS = "script/quest/questscript_a.xml", QF = "table/questtofieldtable.xml", QT = "string/en/specialquesttype.xml";
        const string M = QuestEditor.ServerM2d, MC = "table/Server/QuestScriptCondition_final.xml", MF = "table/Server/QuestScriptFunction_final.xml";

        static int Count(string text, string part) { return text.Split(new[] { part }, StringSplitOptions.None).Length - 1; }

        [Test]
        public void Clone_CopiesTheTalkScriptAndTheRowsThatListTheQuest()
        {
            using (var f = new FakeQuests(true, true, true))
            {
                QuestEditor e = f.Editor();
                e.Clone(701, 750);
                List<string> listed = e.ListedRows(750);
                Assert.That(listed.Count, Is.EqualTo(8), string.Join("\n", listed.ToArray()));
                Assert.That(listed, Has.Some.Contains(M + "  " + MC + "  <condition questID=\"750\"> 3 rows (script condition)"));
                Assert.That(listed, Has.Some.Contains(K + "  script/quest/questscript_final.xml  <quest id=\"750\"> row (talk script)"));
                f.Reload();
                e = f.Editor();
                Assert.That(e.ListedRows(750).Count, Is.EqualTo(8), "read from the saved mod");
                Plan p = f.Plan();
                string script = "<quest id=\"750\">\n\t\t<script id=\"100\">\n\t\t\t<content text=\"$script:701100$\">\n\t\t\t\t<distractor text=\"$script:701101$\" goto=\"200\" />";
                string k = FakeQuests.Out(p, K, "script/quest/questscript_final.xml");
                Assert.That(k, Does.Contain(script).And.Contain("<quest id=\"701\">").And.Contain("<quest id=\"702\">"));
                Assert.That(Count(k, "<script id=\"200\" feature=\"F1\">"), Is.EqualTo(3), "the whole row with its children");
                foreach (string a in new[] { G, S })
                {
                    Assert.That(FakeQuests.Out(p, a, QS), Does.Contain(script).And.Contain("<quest id=\"701\">"), a);
                    Assert.That(FakeQuests.Out(p, a, "script/quest/questscript_b.xml"), Is.Null, "the other group file is not written: " + a);
                }
                foreach (string a in new[] { K, G, S })
                {
                    Assert.That(FakeQuests.Out(p, a, QF), Does.Contain("<quest id=\"701\" fields=\"61,62\" />").And.Contain("<quest id=\"750\" fields=\"61,62\" />"), a);
                    Assert.That(FakeQuests.Out(p, a, QT), Does.Contain("<key id=\"750\" name=\"Fake Type\" />").And.Contain("<key id=\"701\" name=\"Fake Type\" />"), a);
                }
                Assert.That(FakeQuests.Out(p, G, QS), Is.EqualTo(FakeQuests.Out(p, S, QS)), "the gms-client gets the server's lines");
                string c = FakeQuests.Out(p, M, MC);
                Assert.That(c, Does.Contain("<condition questID=\"750\" scriptID=\"100\" item=\"9001\" />").And.Contain("<condition questID=\"750\" scriptID=\"100\" feature=\"F1\" item=\"9002\" />").And.Contain("<condition questID=\"750\" scriptID=\"200\" item=\"9004\" />"));
                Assert.That(Count(c, "questID=\"701\""), Is.EqualTo(3), "the source rows stay");
                Assert.That(Count(c, "questID=\"750\""), Is.EqualTo(3));
                Assert.That(Count(c, "questID=\"702\""), Is.EqualTo(1));
                string fn = FakeQuests.Out(p, M, MF);
                Assert.That(fn, Does.Contain("<function questID=\"750\" scriptID=\"100\" functionID=\"1\" presentItemID=\"9001\" />").And.Contain("<function questID=\"750\" scriptID=\"100\" functionID=\"2\" emoticon=\"wave\" />"));

                // remove: no row is left behind, in the mod and in the plan
                e.RemoveClone(750);
                Assert.That(f.Mod.Ops, Is.Empty);
                Assert.That(f.Mod.Ids, Is.Empty);
                Assert.That(e.ListedRows(750), Is.Empty);
                p = f.Plan();
                foreach (string a in new[] { K, G, S, M }) Assert.That(p.Find(a) == null || p.Find(a).Put.Count == 0, Is.True, a);
            }
        }

        [Test]
        public void Clone_OfTwinListRows_MakesTwoRows()
        {
            using (var f = new FakeQuests(true, true, true, null, true))
            {
                QuestEditor e = f.Editor();
                e.Clone(701, 750);
                Assert.That(e.ListedRows(750), Has.Some.Contains(M + "  " + MC + "  <condition questID=\"750\"> 4 rows (script condition)"));
                Plan p = f.Plan();     // no conflict: the two copies are two rows, as in the source
                string c = FakeQuests.Out(p, M, MC);
                Assert.That(Count(c, "<condition questID=\"750\" scriptID=\"200\" item=\"9004\" />"), Is.EqualTo(2), c);
                Assert.That(Count(c, "<condition questID=\"701\" scriptID=\"200\" item=\"9004\" />"), Is.EqualTo(2), c);
                Assert.That(Count(c, "questID=\"750\""), Is.EqualTo(4));
                e.RemoveClone(750);
                Assert.That(f.Mod.Ops, Is.Empty);
            }
            // the same row copied twice from one source row is still one line twice: a conflict
            using (var f = new FakeQuests(true, true, true))
            {
                f.Mod = Mod.Parse("<mod id=\"quests.test\" name=\"t\" version=\"1\"><edit archive=\"" + M + "\" entry=\"" + MC + "\">" +
                    "<add copy=\"condition[item=9004]\"><set name=\"questID\" value=\"750\"/></add><add copy=\"condition[item=9004]\"><set name=\"questID\" value=\"750\"/></add></edit></mod>", "twice");
                Plan p = Planner.Build(f.Data, new[] { f.Mod }, new PlanOptions());
                Assert.That(p.Conflicts.Count, Is.EqualTo(1), Fake.Dump(p));
            }
        }

        // ---- reward rows the mod adds to a quest without the list ----

        [Test]
        public void AddThenRemoveReward_OnAQuestWithoutTheList_WritesNothing()
        {
            using (var f = new FakeQuests(true, true, true))
            {
                QuestDraft d = f.Editor().Open(702);
                d.AddReward("essentialItem", new QuestItem(9005, 1, 1));
                d.AddReward("v", new QuestItem(9005, 1, 1));
                d.AddReward(QuestDraft.AcceptReward, "v", new QuestItem(9006, 1, 1));
                f.Reload();
                d = f.Editor().Open(702);
                d.RemoveReward("essentialItem", 0);
                d.RemoveReward("v", 0);
                d.RemoveReward(QuestDraft.AcceptReward, "v", 0);
                Assert.That(f.Mod.Ops, Is.Empty, f.Mod.ToXml());

                // an element that was added for a field keeps the field when its last row goes
                d.SetExp(40);
                d.AddReward("v", new QuestItem(9005, 1, 1));
                d.RemoveReward("v", 0);
                Assert.That(d.Exp, Is.EqualTo("40"));
                Assert.That(d.Rewards("v"), Is.Empty);
                d.SetExp(0);
                Assert.That(f.Mod.Ops, Is.Empty, f.Mod.ToXml());

                // and in a new quest: the clone's own row and files stay, without an empty element
                QuestEditor e = f.Editor();
                QuestDraft c = e.Clone(702, 750, false);
                string before = f.Mod.ToXml();
                c.AddReward("v", new QuestItem(9005, 1, 1));
                c.RemoveReward("v", 0);
                Assert.That(f.Mod.ToXml(), Is.EqualTo(before));
                f.Plan();
            }
        }

        [Test]
        public void Clone_TwoQuests_RemoveTakesOnlyItsOwnRows()
        {
            using (var f = new FakeQuests(true, true, true))
            {
                QuestEditor e = f.Editor();
                e.Clone(701, 750);
                e.Clone(702, 751);
                Assert.That(e.ListedRows(751).Count, Is.EqualTo(5), "702 has a script, a fields row and one condition; no type name, no function");
                f.Reload();
                e = f.Editor();
                e.RemoveClone(750);
                Assert.That(e.ListedRows(750), Is.Empty);
                Assert.That(e.ListedRows(751).Count, Is.EqualTo(5));
                Plan p = f.Plan();
                string c = FakeQuests.Out(p, M, MC);
                Assert.That(Count(c, "questID=\"750\""), Is.EqualTo(0));
                Assert.That(c, Does.Contain("<condition questID=\"751\" scriptID=\"100\" item=\"9003\" />"));
                Assert.That(FakeQuests.Out(p, M, MF), Is.Null);
                Assert.That(FakeQuests.Out(p, K, "script/quest/questscript_final.xml"), Does.Contain("<quest id=\"751\">").And.Not.Contain("<quest id=\"750\">"));
                Assert.That(FakeQuests.Out(p, S, QF), Does.Contain("<quest id=\"751\" fields=\"63\" />").And.Not.Contain("id=\"750\""));
                e.RemoveClone(751);
                Assert.That(f.Mod.Ops, Is.Empty);
            }
        }

        [Test]
        public void Clone_WithoutTheListedRows()
        {
            using (var f = new FakeQuests(true, true, true))
            {
                QuestEditor e = f.Editor();
                e.Clone(701, 750, false);
                Assert.That(e.ListedRows(750), Is.Empty);
                Assert.That(e.Registration(750).Count, Is.EqualTo(6));
                Plan p = f.Plan();
                Assert.That(FakeQuests.Out(p, K, "script/quest/questscript_final.xml"), Is.Null);
                Assert.That(FakeQuests.Out(p, S, QF), Is.Null);
                Assert.That(p.Find(M) == null || p.Find(M).Put.Count == 0, Is.True);
                e.RemoveClone(750);
                Assert.That(f.Mod.Ops, Is.Empty);
            }
            // a data set without the server: the clients' rows only
            using (var f = new FakeQuests(true, true, false))
            {
                QuestEditor e = f.Editor();
                e.Clone(701, 750);
                Assert.That(e.ListedRows(750).Count, Is.EqualTo(6));
                f.Plan();
            }
        }

        [Test]
        public void Find_ShowsTheModsNameOfARenamedQuest()
        {
            using (var f = new FakeQuests(true, true, true))
            {
                QuestEditor e = f.Editor();
                e.Open(702).SetText("en", "name", "Mud Hunt");
                e.Open(701).SetText("zz", "desc", "only the zz text");
                f.Reload();
                e = f.Editor();
                List<QuestHit> hits = e.Find("", 10);
                Assert.That(hits[0].Name, Is.EqualTo("Fake Errand"));
                Assert.That(hits[1].Name, Is.EqualTo("Mud Hunt"));
                Assert.That(hits[1].Added, Is.False);
                Assert.That(e.Find("mud", 10).Count, Is.EqualTo(1), "found by the new name");
                Assert.That(e.Find("clay", 10), Is.Empty, "and no longer by the shipped one");
                e.Language = "zz";
                Assert.That(e.Find("702", 10)[0].Name, Is.EqualTo("Clay Hunt (zz)"), "the zz name is not changed");
            }
        }

        [Test]
        public void AcceptReward_ReadSetAddRemove()
        {
            using (var f = new FakeQuests(true, true, true))
            {
                QuestDraft d = f.Editor().Open(701);
                const string A = QuestDraft.AcceptReward;
                Assert.That(d.Rewards(A, "v").Count, Is.EqualTo(1));
                Assert.That(d.Rewards(A, "v")[0].Code, Is.EqualTo("9100"));
                Assert.That(d.Rewards(A, "essentialItem").Count, Is.EqualTo(1));
                Assert.That(d.Rewards(A, "essentialItem")[0].Places, Is.EqualTo(2));
                Assert.That(d.Rewards("v")[0].Code, Is.EqualTo("9001"), "the complete reward is its own list");
                Assert.Throws<ModKitException>(delegate { d.Rewards("reward", "v"); });
                d.SetReward(A, "v", 0, new QuestItem(9100, 1, 3));
                d.SetReward(A, "essentialItem", 0, new QuestItem(9100, 1, 3));
                d.AddReward(A, "v", new QuestItem(9101, 2, 1));
                d.AddReward(A, "essentialItem", new QuestItem(9101, 2, 1));
                Assert.Throws<ModKitException>(delegate { d.AddReward(A, "v", new QuestItem(9101, 2, 1)); });
                f.Reload();
                d = f.Editor().Open(701);
                Assert.That(d.Rewards(A, "v").Count, Is.EqualTo(2));
                Assert.That(d.Rewards(A, "essentialItem")[1].Differs, Is.False);
                Plan p = f.Plan();
                Assert.That(FakeQuests.Out(p, K, T), Does.Contain("<acceptReward>\n\t\t\t<v code=\"9100\" rank=\"1\" count=\"3\" />\n\t\t\t<v code=\"9101\" rank=\"2\" count=\"1\" />\n\t\t</acceptReward>"));
                string s = FakeQuests.Out(p, S, F1);
                Assert.That(s, Does.Contain("<acceptReward>\n\t\t\t\t<essentialItem code=\"9100\" rank=\"1\" count=\"3\" />\n\t\t\t\t<essentialItem code=\"9101\" rank=\"2\" count=\"1\" />\n\t\t\t</acceptReward>"));
                Assert.That(s, Does.Contain("<essentialItem code=\"9001\" rank=\"1\" count=\"1\" />"), "the complete reward is untouched");
                Assert.That(FakeQuests.Out(p, G, F1), Is.EqualTo(s));

                // removing every row leaves the element empty: no code 0 row here
                d.RemoveReward(A, "v", 1); d.RemoveReward(A, "v", 0);
                d.RemoveReward(A, "essentialItem", 1); d.RemoveReward(A, "essentialItem", 0);
                p = f.Plan();
                Assert.That(FakeQuests.Out(p, K, T), Does.Not.Contain("9100"));
                Assert.That(FakeQuests.Out(p, S, F1), Does.Not.Contain("9100").And.Not.Contain("code=\"0\" rank=\"0\" count=\"0\" />\n\t\t\t</acceptReward>"));
            }
            // a quest whose element is empty or left out gets the rows
            using (var f = new FakeQuests(true, true, true))
            {
                QuestDraft d = f.Editor().Open(702);
                const string A = QuestDraft.AcceptReward;
                Assert.That(d.Rewards(A, "essentialItem"), Is.Empty);
                Assert.That(d.AddReward(A, "v", new QuestItem(9102, 1, 1)), Is.EqualTo(2));
                Assert.That(d.AddReward(A, "essentialItem", new QuestItem(9102, 1, 1)), Is.EqualTo(3));
                f.Reload();
                Assert.That(f.Editor().Open(702).Rewards(A, "essentialItem")[0].Places, Is.EqualTo(3));
                Plan p = f.Plan();
                Assert.That(Count(FakeQuests.Out(p, K, T), "<acceptReward>\n\t\t\t<v code=\"9102\" rank=\"1\" count=\"1\" />\n\t\t</acceptReward>"), Is.EqualTo(2));
                Assert.That(Count(FakeQuests.Out(p, S, F2), "<essentialItem code=\"9102\" rank=\"1\" count=\"1\" />"), Is.EqualTo(2));
                Assert.That(FakeQuests.Out(p, S, F2), Does.Not.Contain("<acceptReward />"));
            }
        }

        [Test]
        public void ConditionTypeAndCode()
        {
            Assert.That(QuestDraft.CodeTable("npc"), Is.EqualTo("npc"));
            Assert.That(QuestDraft.CodeTable("item_exist"), Is.EqualTo("item"));
            Assert.That(QuestDraft.CodeTable("interact_object_rep"), Is.EqualTo("interact object"));
            Assert.That(QuestDraft.CodeTable("dungeon_rank_clear"), Is.EqualTo("dungeon"));
            Assert.That(QuestDraft.CodeTable("trigger"), Is.Null);
            Assert.That(QuestDraft.CodeTable("talk"), Is.Null);
            // the type itself decides, not how it starts: these take amounts
            Assert.That(QuestDraft.CodeTable("item_gear_score"), Is.Null);
            Assert.That(QuestDraft.CodeTable("item_break"), Is.Null);
            Assert.That(QuestDraft.CodeTable("dungeon_rank_x"), Is.Null);
            Assert.That(QuestDraft.CodeTable("interact_object_count"), Is.Null);
            Assert.That(QuestDraft.CodeTable("meso"), Is.Null);
            using (var f = new FakeQuests(true, true, true))
            {
                QuestEditor e = f.Editor();
                Assert.That(e.ConditionTypes(), Is.EqualTo(new[] { "npc", "talk" }));
                QuestDraft d = e.Open(701);
                // condition 1 is npc 41: its code is an npc id
                Assert.Throws<ModKitException>(delegate { d.SetConditionCode(0, "43"); });
                Assert.Throws<ModKitException>(delegate { d.SetConditionType(0, "talk"); }, "an npc id is not free text");
                Assert.Throws<ModKitException>(delegate { d.SetConditionType(0, "walk"); }, "not a shipped type");
                Assert.Throws<ModKitException>(delegate { d.SetConditionType(0, ""); });
                Assert.Throws<ModKitException>(delegate { d.SetConditionType(5, "npc"); });
                Assert.That(d.SetConditionType(0, "npc"), Is.EqualTo(3));
                Assert.That(f.Mod.Ops, Is.Empty, "the same type writes nothing");
                // condition 2 is talk 32: free
                Assert.That(d.SetConditionCode(1, "hello"), Is.EqualTo(3));
                // a long number is a code like any other: the length says nothing
                Assert.That(d.SetConditionCode(1, "1000000"), Is.EqualTo(3));
                Assert.That(d.SetConditionCode(1, "hello"), Is.EqualTo(3));
                Assert.Throws<ModKitException>(delegate { d.SetConditionType(1, "npc"); }, "free text is not an npc id");
                f.Reload();
                d = f.Editor().Open(701);
                Assert.That(d.Conditions()[1].Code, Is.EqualTo("hello"));
                Assert.That(d.Conditions()[1].Differs, Is.False);
                Plan p = f.Plan();
                Assert.That(FakeQuests.Out(p, K, T), Does.Contain("<condition type=\"talk\" code=\"hello\" />"));
                foreach (string a in new[] { K, G, S }) Assert.That(FakeQuests.Out(p, a, F1), Does.Contain("<condition type=\"talk\" code=\"hello\""), a);
                d.SetConditionCode(1, "32");
                Assert.That(f.Mod.Ops, Is.Empty);
            }
            // without the table any type word is taken, within the same code table
            using (var f = new FakeQuests(false, true, true))
            {
                QuestDraft d = f.Editor().Open(701);
                Assert.That(d.SetConditionType(0, "talk_in"), Is.EqualTo(1));
                // an amount under a type that starts like an item type, and a long one, can be typed
                Assert.That(d.SetConditionType(1, "item_gear_score"), Is.EqualTo(1));
                Assert.That(d.SetConditionCode(1, "3000"), Is.EqualTo(1));
                Assert.That(d.SetConditionCode(1, "1000000"), Is.EqualTo(1));
                Assert.Throws<ModKitException>(delegate { d.SetConditionType(1, "item_exist"); }, "an amount is not an item id");
                Plan p = f.Plan();
                Assert.That(FakeQuests.Out(p, S, F1), Does.Contain("<condition type=\"talk_in\" code=\"41\" value=\"3\" />").And.Contain("<condition type=\"item_gear_score\" code=\"1000000\""));
                Assert.That(FakeQuests.Out(p, G, F1), Is.EqualTo(FakeQuests.Out(p, S, F1)));
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
