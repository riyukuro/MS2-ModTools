// The NPC talk checks of a plan (NpcTalkCheck.cs): after the merge of all mods, every NPC talk the plan changes (an
// <npc> of a combined file whose text differs from the shipped one, or a talk file of one NPC) is checked against the
// talk texts the target has after the plan, and the problems the shipped talk of the NPC does not have are errors
// and warnings of the mod that changes the file. An NPC changed on one target and left as shipped on another that
// ships its talk is a warning too: the two clients would say different things (or the server would not know a step).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        static readonly Regex NpcTalkCombined = new Regex(@"^script/npc/npcscript[^/]*\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex NpcTalkFile = new Regex(@"^script/npc/(\d+)\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex NpcTextFile = new Regex(@"^string/([^/]+)/scriptnpc[^/]*\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // the npc talk text ids of a target after the plan, by language
        static Dictionary<string, HashSet<long>> NpcTextIds(Run run, string archive)
        {
            var d = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
            M2dArchive arch = run.Data.Open(archive);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (EntryState st in run.Order)
            {
                if (st.Archive != archive) continue;
                Match m = NpcTextFile.Match(st.Name.Replace('\\', '/'));
                if (!m.Success) continue;
                seen.Add(st.Name);
                if (st.DeletedBy != null) continue;
                AddTextIds(d, m.Groups[1].Value, TalkIdsOf(st.Final ?? st.Base ?? st.Original, archive + " " + st.Name, st.FinalDoc));
            }
            foreach (M2dEntry e in arch.Entries)
            {
                Match m = NpcTextFile.Match(e.Name.Replace('\\', '/'));
                if (!m.Success || seen.Contains(e.Name)) continue;
                string key = "npc talk text ids\n" + archive + "\n" + e.Name;
                object o;
                if (!run.Data.Cache.TryGetValue(key, out o)) run.Data.Cache[key] = o = TalkIdsOf(arch.Read(e), archive + " " + e.Name, null) ?? new HashSet<long>();
                AddTextIds(d, m.Groups[1].Value, (HashSet<long>)o);
            }
            return d;
        }

        static void AddTextIds(Dictionary<string, HashSet<long>> d, string lang, HashSet<long> ids)
        {
            HashSet<long> set;
            if (!d.TryGetValue(lang, out set)) d[lang] = set = new HashSet<long>();
            if (ids != null) set.UnionWith(ids);
        }

        // the highest mod with an op on the entry
        static Mod EntryMod(Run run, string archive, string entry)
        {
            for (int i = run.Plan.Mods.Count - 1; i >= 0; i--)
                foreach (ModOp op in run.Plan.Mods[i].Ops)
                {
                    if (op.Archive != archive) continue;
                    var ed = op as EditOp;
                    var en = op as EntryOp;
                    string name = ed != null ? ed.Entry : en != null ? en.Name : null;
                    if (name != null && string.Equals(name.Replace('\\', '/'), entry.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) return run.Plan.Mods[i];
                }
            return null;
        }

        static XmlDoc ParseOrNull(byte[] b, string label)
        {
            if (b == null) return null;
            try { return XmlDoc.Parse(b, label); }
            catch (ModKitException) { return null; }
        }

        // the <npc> rows of a combined file by id (the first of each id)
        static Dictionary<long, XmlElem> NpcRows(XmlDoc d)
        {
            var m = new Dictionary<long, XmlElem>();
            if (d == null || d.Root == null) return m;
            foreach (XmlElem n in d.Root.Children)
            {
                long id;
                if (n.Name == "npc" && QuestEditor.TryId(n.Attr("id") ?? "", out id) && !m.ContainsKey(id)) m[id] = n;
            }
            return m;
        }

        static void NpcTalkCheck(Run run)
        {
            // archive -> npc ids the plan changes there; archive -> npc ids it ships a talk for
            var changed = new Dictionary<string, SortedDictionary<long, bool>>(StringComparer.Ordinal);
            var where = new Dictionary<string, string>(StringComparer.Ordinal);
            var texts = new Dictionary<string, Dictionary<string, HashSet<long>>>(StringComparer.Ordinal);
            foreach (EntryState st in run.Order)
            {
                string name = st.Name.Replace('\\', '/');
                bool combined = NpcTalkCombined.IsMatch(name);
                Match file = combined ? Match.Empty : NpcTalkFile.Match(name);
                if (!combined && !file.Success) continue;
                if (st.DeletedBy != null || st.Final == null || (st.Existed && M2dWriter.BytesEqual(st.Final, st.Original))) continue;
                XmlDoc now = st.FinalDoc ?? ParseOrNull(st.Final, st.Archive + " " + st.Name);
                XmlDoc was = st.Existed ? ParseOrNull(st.Original, st.Archive + " " + st.Name) : null;
                if (now == null || now.Root == null) continue;
                var roots = new List<KeyValuePair<long, KeyValuePair<XmlElem, XmlElem>>>();
                if (combined)
                {
                    Dictionary<long, XmlElem> a = NpcRows(now), b = NpcRows(was);
                    foreach (KeyValuePair<long, XmlElem> kv in a)
                    {
                        XmlElem old;
                        b.TryGetValue(kv.Key, out old);
                        if (old != null && old.OuterText == kv.Value.OuterText) continue;
                        roots.Add(new KeyValuePair<long, KeyValuePair<XmlElem, XmlElem>>(kv.Key, new KeyValuePair<XmlElem, XmlElem>(kv.Value, old)));
                    }
                }
                else
                {
                    long id;
                    if (!QuestEditor.TryId(file.Groups[1].Value, out id)) continue;
                    roots.Add(new KeyValuePair<long, KeyValuePair<XmlElem, XmlElem>>(id, new KeyValuePair<XmlElem, XmlElem>(now.Root, was == null ? null : was.Root)));
                }
                if (roots.Count == 0) continue;
                Dictionary<string, HashSet<long>> have;
                if (!texts.TryGetValue(st.Archive, out have)) texts[st.Archive] = have = NpcTextIds(run, st.Archive);
                Mod mod = EntryMod(run, st.Archive, st.Name);
                SortedDictionary<long, bool> set;
                if (!changed.TryGetValue(st.Archive, out set)) changed[st.Archive] = set = new SortedDictionary<long, bool>();
                foreach (KeyValuePair<long, KeyValuePair<XmlElem, XmlElem>> r in roots)
                {
                    set[r.Key] = true;
                    where[st.Archive + "\n" + r.Key] = st.Name;
                    List<KeyValuePair<bool, string>> shipped = r.Value.Value == null ? new List<KeyValuePair<bool, string>>() : TalkCheck.Of(r.Value.Value, have);
                    foreach (KeyValuePair<bool, string> p in TalkCheck.New(TalkCheck.Of(r.Value.Key, have), shipped))
                        run.Plan.Msg(p.Key ? run.Plan.Errors : run.Plan.Warnings, mod, st.Archive, st.Name, "NPC " + r.Key.ToString(CultureInfo.InvariantCulture) + " talk, " + p.Value);
                }
            }
            if (changed.Count == 0) return;

            // the targets that ship a talk for an NPC another target's talk is changed for, and leave it as shipped
            foreach (Profile p in run.Data.Profiles)
            {
                string archive = p.ArchiveIn("x:Xml.m2d");
                if (!run.Data.Exists(archive)) continue;
                SortedDictionary<long, bool> own;
                changed.TryGetValue(archive, out own);
                HashSet<long> ships = null;
                foreach (KeyValuePair<string, SortedDictionary<long, bool>> c in changed)
                {
                    if (c.Key == archive) continue;
                    foreach (long npc in c.Value.Keys)
                    {
                        if (own != null && own.ContainsKey(npc)) continue;
                        if (ships == null) ships = ShippedNpcTalks(run, archive);
                        if (!ships.Contains(npc)) continue;
                        Profile from = Profile.OfArchive(c.Key);
                        run.Plan.Msg(run.Plan.Warnings, EntryMod(run, c.Key, where[c.Key + "\n" + npc]), c.Key, where[c.Key + "\n" + npc],
                            "NPC " + npc.ToString(CultureInfo.InvariantCulture) + " talk is changed for the " + (from != null ? from.Name : c.Key) + " and the " + p.Name + " has it as shipped");
                    }
                }
            }
        }

        // the npc ids a target ships a talk for, kept with the data set
        static HashSet<long> ShippedNpcTalks(Run run, string archive)
        {
            string key = "npc talks\n" + archive;
            object o;
            if (run.Data.Cache.TryGetValue(key, out o)) return (HashSet<long>)o;
            var set = new HashSet<long>();
            M2dArchive arch = run.Data.Open(archive);
            foreach (M2dEntry e in arch.Entries)
            {
                string n = e.Name.Replace('\\', '/');
                long id;
                Match m = NpcTalkFile.Match(n);
                if (m.Success && QuestEditor.TryId(m.Groups[1].Value, out id)) set.Add(id);
                else if (NpcTalkCombined.IsMatch(n)) set.UnionWith(NpcRows(ParseOrNull(arch.Read(e), archive + " " + e.Name)).Keys);
            }
            run.Data.Cache[key] = set;
            return set;
        }
    }
}
