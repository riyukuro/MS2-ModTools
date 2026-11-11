// "Where used": the places of the data and the mod that name an id, through the fields IdLookup knows:
//   quests     start/complete npc, the condition codes (QuestCodeKind: npc, item, quest, skill, buff = effect, ...),
//              the item codes of the reward lists (completeReward and acceptReward)
//   shops      the item of each stock row (shop_game.xml)
//   drop boxes the items of each row (the nested drop tables and the flat splits; itemID and item are one place,
//              itemID2 and item2 another)
//   npcs       shopId, the *DropBoxId lists, skill@ids, additionalEffect@codes (NpcFieldKind)
//   items      skill@skillID, AdditionalEffect@id, the repacking scrolls (ItemFieldKind)
// Skills name no id through a field IdLookup knows, so a skill is never the place that names something.
//
// The shipped places are read once per data set (each file parsed and dropped again, no document kept; a file that
// cannot be read is skipped and listed in Stats) and the index is kept in the data set's cache, as the plan's inputs
// are. Only the places of the kinds a question can be about (Kinds) are kept. The mod is laid over it: each quest,
// npc, item, shop or drop box the mod adds or edits is read as the mod leaves it (the editors' own drafts, a quest
// place by place; for the shops and drop boxes one plan of the mod's group ops and its raw row edits of the group
// tables, so a cell edited in the Tables view counts as the mod's too) and its places replace the shipped ones, so an
// edit that adds a name shows (as "new") and one that removes it hides it. That mod side is kept too and read again
// only when the mod changes (its manifest text, or a file it adds).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>One place that names an id.</summary>
    public sealed class UseHit
    {
        /// <summary>What names it: IdLookup.Quest, Shop, DropBox, Npc or Item.</summary>
        public string Kind;
        public long Id;
        public string Name = "";
        /// <summary>The place in it, for people ("condition 2 (item_exist)", "skill@ids", "sn 3 id").</summary>
        public string Where = "";
        /// <summary>
        /// The place for a jump: an npc or item field "element@attr"; for a quest "start@npc", "complete@npc",
        /// "condition" (Index = the condition) or "completeReward/v" (reward element/list, Index = the row in that
        /// place); for a shop or drop box the row field ("id", "itemID", ...), Index = the row.
        /// </summary>
        public string Field = "";
        public int Index = -1;
        /// <summary>For a quest: the place (QuestPlace.Key) a reward row's Index counts in; "" otherwise.</summary>
        public string Place = "";
        /// <summary>The mod names it here and the shipped data does not (an edit, or a place the mod adds).</summary>
        public bool New;
        /// <summary>The place is one the mod adds (a clone or a new id).</summary>
        public bool SourceNew;

        internal string Key { get { return Kind + "\n" + Id.ToString(CultureInfo.InvariantCulture) + "\n" + Where; } }

        internal UseHit Copy()
        {
            return new UseHit { Kind = Kind, Id = Id, Name = Name, Where = Where, Field = Field, Index = Index, Place = Place, New = New, SourceNew = SourceNew };
        }

        public override string ToString()
        {
            return Kind + " " + Id.ToString(CultureInfo.InvariantCulture) + (Name.Length > 0 ? " " + Name : "") + ": " + Where + (New ? " (new)" : "");
        }
    }

    /// <summary>What the kept indexes of a data set hold and how often they were built (WhereUsed.Stats).</summary>
    public sealed class WhereUsedStats
    {
        /// <summary>How often the shipped places and the mod side were read.</summary>
        public int ShippedScans, ModScans;
        /// <summary>The shipped places kept, and the ids they name.</summary>
        public int ShippedPlaces, Targets;
        /// <summary>The mod side's places kept (the last read).</summary>
        public int ModPlaces;
        /// <summary>The time of the last shipped read, in milliseconds.</summary>
        public long ShippedMs;
        /// <summary>The shipped files that could not be read ("archive entry: error"), skipped.</summary>
        public readonly List<string> Skipped = new List<string>();
    }

    public sealed class WhereUsed
    {
        readonly EditorSession session;
        ItemEditor items;
        NpcEditor npcs;
        QuestEditor quests;
        GroupEditor groups;
        bool itemsTried, npcsTried, questsTried;

        /// <summary>Over the data and the mod of a session; ask for a new one after the mod is replaced.</summary>
        public WhereUsed(EditorSession session)
        {
            if (session == null) throw new ArgumentNullException("session");
            this.session = session;
        }

        public DataSet Data { get { return session.Data; } }
        public Mod Mod { get { return session.Mod; } }

        /// <summary>The kinds of id a question can be about.</summary>
        public static readonly string[] Kinds = { IdLookup.Item, IdLookup.Npc, IdLookup.Skill, IdLookup.Effect, IdLookup.Quest, IdLookup.Shop, IdLookup.DropBox };

        ItemEditor Items() { if (!itemsTried) { itemsTried = true; try { items = session.Items(); } catch (ModKitException) { } } return items; }
        NpcEditor Npcs() { if (!npcsTried) { npcsTried = true; try { npcs = session.Npcs(); } catch (ModKitException) { } } return npcs; }
        QuestEditor Quests()
        {
            if (!questsTried) { questsTried = true; try { QuestEditor q = session.Quests(); if (q.HasData) quests = q; } catch (ModKitException) { } }
            return quests;
        }
        GroupEditor Groups() { return groups ?? (groups = session.Groups()); }

        static string Inv(long v) { return v.ToString(CultureInfo.InvariantCulture); }
        static string GroupSpace(GroupKind k) { return k == GroupKind.Shop ? IdLookup.Shop : IdLookup.DropBox; }
        static string TargetKey(string kind, long id) { return kind + "\n" + Inv(id); }

        /// <summary>The ids of a value: one id, or a comma list.</summary>
        internal static IEnumerable<long> Ids(string value)
        {
            if (string.IsNullOrEmpty(value)) yield break;
            foreach (string p in value.Split(','))
            {
                long id;
                if (long.TryParse(p.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0) yield return id;
            }
        }

        // ---- the places of one source, as (target kind, target id, hit) ----

        sealed class Ref { internal string Kind; internal long Id; internal UseHit Hit; }

        sealed class Refs
        {
            internal readonly List<Ref> List = new List<Ref>();
            readonly HashSet<string> seen = new HashSet<string>();
            internal void Add(string kind, long id, UseHit h)
            {
                if (kind == null || Array.IndexOf(Kinds, kind) < 0) return;     // a map or a dungeon: nobody can ask
                if (seen.Add(kind + "\n" + Inv(id) + "\n" + h.Key)) List.Add(new Ref { Kind = kind, Id = id, Hit = h });
            }
        }

        static UseHit Hit(string kind, long id, string where, string field, int index)
        {
            return new UseHit { Kind = kind, Id = id, Where = where, Field = field, Index = index };
        }

        /// <summary>An entity field ("element@attr") with its value.</summary>
        static void EntityField(Refs into, string sourceKind, long id, string field, string value)
        {
            string kind = sourceKind == IdLookup.Item ? IdLookup.ItemFieldKind(field) : IdLookup.NpcFieldKind(field);
            if (kind == null) return;
            foreach (long t in Ids(value)) into.Add(kind, t, Hit(sourceKind, id, field, field, -1));
        }

        static void EntityElem(Refs into, string sourceKind, long id, XmlElem e)
        {
            foreach (XmlAttr a in e.Attrs) EntityField(into, sourceKind, id, e.Name + "@" + a.Name, a.Value);
            foreach (XmlElem c in e.Children) EntityElem(into, sourceKind, id, c);
        }

        static void QuestCondition(Refs into, long id, int index, string type, string code)
        {
            string kind = IdLookup.QuestCodeKind(type);
            if (kind == null) return;
            foreach (long t in Ids(code)) into.Add(kind, t, Hit(IdLookup.Quest, id, "condition " + (index + 1) + " (" + type + ")", "condition", index));
        }

        static void QuestReward(Refs into, long id, string reward, string list, int index, string code, string place)
        {
            foreach (long t in Ids(code))
            {
                UseHit h = Hit(IdLookup.Quest, id, reward + " " + list, reward + "/" + list, index);
                h.Place = place;
                into.Add(IdLookup.Item, t, h);
            }
        }

        // one place of a quest, shipped (an element) or as the mod leaves it (the editor's node): the same reading
        delegate List<T> KidsOf<T>(T e);
        delegate string AttrOf<T>(T e, string name);
        delegate string NameOfNode<T>(T e);

        static void QuestPlaceRefs<T>(Refs into, long id, T q, string place, KidsOf<T> kids, AttrOf<T> attr, NameOfNode<T> name)
        {
            int cond = 0;
            foreach (T c in kids(q))
            {
                string cn = name(c);
                if (cn == "condition") { QuestCondition(into, id, cond, attr(c, "type") ?? "", attr(c, "code")); cond++; }
                else if (cn == "start" || cn == "complete")
                    foreach (long t in Ids(attr(c, "npc"))) into.Add(IdLookup.Npc, t, Hit(IdLookup.Quest, id, cn + " npc", cn + "@npc", -1));
                else if (cn == QuestDraft.CompleteReward || cn == QuestDraft.AcceptReward)
                {
                    var n = new Dictionary<string, int>();
                    foreach (T r in kids(c))
                    {
                        string rn = name(r);
                        if (Array.IndexOf(QuestDraft.RewardLists, rn) < 0) continue;
                        string code = QuestDraft.Norm(attr(r, "code"));
                        if (code.Length == 0) continue;
                        int i;
                        n.TryGetValue(rn, out i);
                        n[rn] = i + 1;
                        QuestReward(into, id, cn, rn, i, code, place);
                    }
                }
            }
        }

        static void QuestElem(Refs into, long id, XmlElem q, string place)
        {
            QuestPlaceRefs(into, id, q, place, delegate(XmlElem e) { return e.Children; }, delegate(XmlElem e, string a) { return e.Attr(a); }, delegate(XmlElem e) { return e.Name; });
        }

        static void GroupRow(Refs into, GroupKind kind, long id, int index, string describe, IEnumerable<KeyValuePair<string, string>> fields)
        {
            string sk = kind == GroupKind.Shop ? IdLookup.Shop : IdLookup.DropBox;
            foreach (KeyValuePair<string, string> f in fields)
            {
                string k = IdLookup.GroupRowFieldKind(kind, f.Key);
                if (k == null) continue;
                // a drop box's item is "itemID" in the nested tables and "item" in the flat ones, its second item
                // "itemID2" and "item2": one place by name each
                string label = kind == GroupKind.DropBox && f.Key == "itemID" ? "item" : kind == GroupKind.DropBox && f.Key == "itemID2" ? "item2" : f.Key;
                foreach (long t in Ids(f.Value)) into.Add(k, t, Hit(sk, id, describe + " " + label, f.Key, index));
            }
        }

        static IEnumerable<KeyValuePair<string, string>> Attrs(XmlElem e)
        {
            foreach (XmlAttr a in e.Attrs) yield return new KeyValuePair<string, string>(a.Name, a.Value);
        }

        // ---- the shipped index ----

        sealed class Index
        {
            /// <summary>target kind + id -> the shipped places that name it.</summary>
            internal readonly Dictionary<string, List<UseHit>> By = new Dictionary<string, List<UseHit>>();
            /// <summary>source kind + id -> the keys (UseHit.Key + target) of its shipped places.</summary>
            internal readonly Dictionary<string, HashSet<string>> Of = new Dictionary<string, HashSet<string>>();

            internal void Add(Refs r)
            {
                foreach (Ref x in r.List)
                {
                    string tk = TargetKey(x.Kind, x.Id);
                    List<UseHit> l;
                    if (!By.TryGetValue(tk, out l)) By[tk] = l = new List<UseHit>();
                    l.Add(x.Hit);
                    string sk = TargetKey(x.Hit.Kind, x.Hit.Id);
                    HashSet<string> s;
                    if (!Of.TryGetValue(sk, out s)) Of[sk] = s = new HashSet<string>();
                    s.Add(tk + "\n" + x.Hit.Key);
                }
            }
        }

        const string IndexKey = "whereused\nshipped";

        /// <summary>Whether the shipped places of a data set are read already (the first question reads them).</summary>
        public static bool IsIndexed(DataSet data) { return data != null && data.Cache.ContainsKey(IndexKey); }

        const string StatsKey = "whereused\nstats", ModKey = "whereused\nmod";

        /// <summary>What the kept indexes of a data set hold (null: no question was asked yet).</summary>
        public static WhereUsedStats Stats(DataSet data)
        {
            object o;
            return data != null && data.Cache.TryGetValue(StatsKey, out o) ? (WhereUsedStats)o : null;
        }

        WhereUsedStats MyStats()
        {
            WhereUsedStats st = Stats(Data);
            if (st == null) Data.Cache[StatsKey] = st = new WhereUsedStats();
            return st;
        }

        Index Shipped()
        {
            const string key = IndexKey;
            object o;
            if (Data.Cache.TryGetValue(key, out o)) return (Index)o;
            WhereUsedStats st = MyStats();
            Stopwatch clock = Stopwatch.StartNew();
            st.Skipped.Clear();
            Action<string, Exception> skip = delegate(string file, Exception ex) { st.Skipped.Add(file + ": " + ex.Message); };
            var ix = new Index();
            var seen = new Refs();      // one list over all sources: a place two targets both have counts once
            ItemEditor it = Items();
            if (it != null) it.ScanShipped(delegate(long id, XmlElem e) { EntityElem(seen, IdLookup.Item, id, e); }, skip);
            NpcEditor np = Npcs();
            if (np != null) np.ScanShipped(delegate(long id, XmlElem e) { EntityElem(seen, IdLookup.Npc, id, e); }, skip);
            QuestEditor q = Quests();
            if (q != null) q.ScanShipped(delegate(long id, XmlElem e, string place) { QuestElem(seen, id, e, place); }, skip);
            Groups().ScanShipped(delegate(GroupKind kind, long id, int index, string describe, XmlElem row) { GroupRow(seen, kind, id, index, describe, Attrs(row)); }, skip);
            ix.Add(seen);
            Data.Cache[key] = ix;
            st.ShippedScans++;
            st.ShippedPlaces = seen.List.Count;
            st.Targets = ix.By.Count;
            st.ShippedMs = clock.ElapsedMilliseconds;
            return ix;
        }

        // ---- the mod ----

        sealed class Source { internal string Kind; internal long Id; internal bool New; }

        List<Source> Touched()
        {
            var l = new List<Source>();
            var seen = new HashSet<string>();
            Action<string, long, bool> add = delegate(string kind, long id, bool isNew) { if (seen.Add(TargetKey(kind, id))) l.Add(new Source { Kind = kind, Id = id, New = isNew }); };
            ItemEditor it = Items();
            if (it != null) { List<long> a = it.AddedIds(); foreach (long id in a) add(IdLookup.Item, id, true); foreach (long id in it.TouchedIds()) add(IdLookup.Item, id, false); }
            NpcEditor np = Npcs();
            if (np != null) { List<long> a = np.AddedIds(); foreach (long id in a) add(IdLookup.Npc, id, true); foreach (long id in np.TouchedIds()) add(IdLookup.Npc, id, false); }
            QuestEditor q = Quests();
            if (q != null) { List<long> a = q.AddedIds(); foreach (long id in a) add(IdLookup.Quest, id, true); foreach (long id in q.TouchedIds()) add(IdLookup.Quest, id, false); }
            foreach (GroupOp g in Mod.Groups)
                add(GroupSpace(g.Kind), g.Id, g.New || g.IsClone);
            foreach (KeyValuePair<GroupKind, long> kv in Groups().RawTouched()) add(GroupSpace(kv.Key), kv.Value, false);
            return l;
        }

        Dictionary<string, Refs> modGroups;

        /// <summary>
        /// The places of the shops and drop boxes as the mod leaves them (its group ops and its raw row edits of the
        /// group tables, planned once per question), by source.
        /// </summary>
        Dictionary<string, Refs> ModGroups()
        {
            if (modGroups != null) return modGroups;
            var by = new Dictionary<string, Refs>();
            try
            {
                Groups().ScanMod(delegate(GroupKind kind, long id, int index, string describe, XmlElem row)
                {
                    string k = TargetKey(GroupSpace(kind), id);
                    Refs r;
                    if (!by.TryGetValue(k, out r)) by[k] = r = new Refs();
                    GroupRow(r, kind, id, index, describe, Attrs(row));
                });
            }
            catch (ModKitException) { }
            return modGroups = by;
        }

        /// <summary>The places of one source as the mod leaves it.</summary>
        Refs ModRefs(Source s)
        {
            var r = new Refs();
            try
            {
                if (s.Kind == IdLookup.Item || s.Kind == IdLookup.Npc)
                {
                    EntityEditor ed = s.Kind == IdLookup.Item ? (EntityEditor)Items() : Npcs();
                    EntityDraft d = ed.OpenAny(s.Id);
                    foreach (EntitySection sec in d.Sections)
                    {
                        if (sec.Elem == null) continue;
                        foreach (string f in sec.Fields) EntityField(r, s.Kind, s.Id, sec.Elem.Name + "@" + f, d.Get(sec, f));
                    }
                }
                else if (s.Kind == IdLookup.Quest)
                {
                    // place by place, as the shipped scan reads them: a value only a later place has is not lost
                    QuestEditor qe = Quests();
                    QuestDraft d = qe.Open(s.Id);
                    foreach (QuestPlace p in d.Places)
                        if (p.Kind != QuestPlace.Text)
                            QuestPlaceRefs(r, s.Id, p.Root, p.Key, delegate(QuestNode n) { return qe.Kids(n, null); }, delegate(QuestNode n, string a) { return qe.Attr(n, a); }, qe.NodeName);
                }
                else
                {
                    Refs g;
                    if (ModGroups().TryGetValue(TargetKey(s.Kind, s.Id), out g)) return g;
                }
            }
            catch (ModKitException) { }     // a source the mod leaves unreadable names nothing
            return r;
        }

        // ---- names ----

        string NameOf(string kind, long id, bool touched)
        {
            try
            {
                if (kind == IdLookup.Item && Items() != null) return Items().NameOf(id, touched) ?? "";
                if (kind == IdLookup.Npc && Npcs() != null) return Npcs().NameOf(id, touched) ?? "";
                if (kind == IdLookup.Quest && Quests() != null) return Quests().NameOf(id, touched) ?? "";
                if (kind == IdLookup.Shop || kind == IdLookup.DropBox)
                {
                    GroupOp g = Mod.FindGroup(kind == IdLookup.Shop ? GroupKind.Shop : GroupKind.DropBox, id);
                    if (g == null) return touched ? "changed" : "";
                    return g.IsClone ? "copy of " + Inv(g.Clone) : g.New ? "" : "changed";
                }
            }
            catch (ModKitException) { }
            return "";
        }

        // ---- the question ----

        /// <summary>
        /// The places that name an id of a kind (WhereUsed.Kinds), shipped and as the mod leaves them: the mod's own
        /// sources first, then by kind and id. At most max (0: all).
        /// </summary>
        public List<UseHit> Of(string kind, long id, int max)
        {
            if (Array.IndexOf(Kinds, kind) < 0) throw new ModKitException("no where-used list for \"" + kind + "\" (" + string.Join(", ", Kinds) + ")");
            Index ix = Shipped();
            ModIndex mi = ModSide(ix);
            string tk = TargetKey(kind, id);
            var hits = new List<UseHit>();
            List<UseHit> mine;
            if (mi.By.TryGetValue(tk, out mine)) foreach (UseHit h in mine) hits.Add(h.Copy());
            var rest = new List<UseHit>();
            List<UseHit> shipped;
            if (ix.By.TryGetValue(tk, out shipped))
                foreach (UseHit h in shipped)
                    if (!mi.Touched.Contains(TargetKey(h.Kind, h.Id))) rest.Add(h.Copy());
            rest.Sort(delegate(UseHit a, UseHit b)
            {
                int c = Array.IndexOf(SourceOrder, a.Kind).CompareTo(Array.IndexOf(SourceOrder, b.Kind));
                return c != 0 ? c : a.Id != b.Id ? a.Id.CompareTo(b.Id) : string.CompareOrdinal(a.Where, b.Where);
            });
            hits.AddRange(rest);
            if (max > 0 && hits.Count > max) hits.RemoveRange(max, hits.Count - max);
            foreach (UseHit h in hits) h.Name = NameOf(h.Kind, h.Id, mi.Touched.Contains(TargetKey(h.Kind, h.Id)));
            return hits;
        }

        /// <summary>The mod side: the places of each source the mod adds or edits, by the id they name.</summary>
        sealed class ModIndex
        {
            internal string Print;
            internal readonly Dictionary<string, List<UseHit>> By = new Dictionary<string, List<UseHit>>();
            internal readonly HashSet<string> Touched = new HashSet<string>();
        }

        /// <summary>The mod as one string: its manifest and the size and time of each file it adds (null: cannot say).</summary>
        string Print()
        {
            try
            {
                var sb = new StringBuilder(Mod.ToXml());
                sb.Append('\n').Append(Mod.Dir);
                foreach (ModOp op in Mod.Ops)
                {
                    var en = op as EntryOp;
                    if (en == null || en.File == null || Mod.Dir == null) continue;
                    var fi = new FileInfo(Path.Combine(Mod.Dir, en.File));
                    sb.Append('\n').Append(en.File);
                    if (fi.Exists) sb.Append(' ').Append(fi.Length).Append(' ').Append(fi.LastWriteTimeUtc.Ticks);
                }
                return sb.ToString();
            }
            catch (ModKitException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
        }

        ModIndex ModSide(Index ix)
        {
            string print = Print();
            object o;
            if (print != null && Data.Cache.TryGetValue(ModKey, out o) && ((ModIndex)o).Print == print) return (ModIndex)o;
            var mi = new ModIndex { Print = print };
            modGroups = null;
            int places = 0;
            foreach (Source s in Touched())
            {
                string sk = TargetKey(s.Kind, s.Id);
                if (!mi.Touched.Add(sk)) continue;
                HashSet<string> had;
                ix.Of.TryGetValue(sk, out had);
                foreach (Ref x in ModRefs(s).List)
                {
                    string tk = TargetKey(x.Kind, x.Id);
                    UseHit h = x.Hit.Copy();
                    h.SourceNew = s.New;
                    h.New = had == null || !had.Contains(tk + "\n" + h.Key);
                    List<UseHit> l;
                    if (!mi.By.TryGetValue(tk, out l)) mi.By[tk] = l = new List<UseHit>();
                    l.Add(h);
                    places++;
                }
            }
            modGroups = null;
            WhereUsedStats st = MyStats();
            st.ModScans++;
            st.ModPlaces = places;
            if (print != null) Data.Cache[ModKey] = mi; else Data.Cache.Remove(ModKey);
            return mi;
        }

        public List<UseHit> Of(string kind, long id) { return Of(kind, id, 0); }

        /// <summary>A shipped file read for the index; one that cannot be read is skipped (null) and told to skip.</summary>
        internal static XmlElem Parse(M2dArchive a, string archive, M2dEntry e, Action<string, Exception> skip)
        {
            try { return XmlDoc.Parse(a.Read(e), archive + " " + e.Name).Root; }
            catch (ModKitException ex) { skip(archive + " " + e.Name, ex); }
            catch (IOException ex) { skip(archive + " " + e.Name, ex); }
            catch (InvalidDataException ex) { skip(archive + " " + e.Name, ex); }
            return null;
        }

        static readonly string[] SourceOrder = { IdLookup.Quest, IdLookup.Shop, IdLookup.DropBox, IdLookup.Npc, IdLookup.Item };

        /// <summary>How many places a Remove clone warning lists before "and N more".</summary>
        public const int LeftBehindShown = 10;

        /// <summary>
        /// The end of a Remove clone: a warning that lists the places that still name the removed id, one per line
        /// ("from id, field, name"), the first show of them and then "and N more"; null when none does.
        /// </summary>
        public string LeftBehind(string kind, long id, int show = LeftBehindShown)
        {
            List<UseHit> l = Of(kind, id);
            if (l.Count == 0) return null;
            var lines = new List<string> { "warning: " + l.Count + " place(s) still name " + kind + " " + Inv(id) + ":" };
            for (int i = 0; i < l.Count && i < show; i++)
            {
                UseHit h = l[i];
                lines.Add("  " + h.Kind + " " + Inv(h.Id) + ", " + h.Where + (h.Name.Length > 0 ? ", " + h.Name : ""));
            }
            if (l.Count > show) lines.Add("  and " + (l.Count - show) + " more");
            return string.Join("\n", lines.ToArray());
        }
    }

    // ---- the editors' side: reading every shipped source once, and which ids the mod edits ----

    public abstract partial class EntityEditor
    {
        /// <summary>
        /// Every shipped row of the bundle side and every shipped per-id file, with its id. A file is parsed and
        /// dropped again (a document the editor already holds is used as it is).
        /// </summary>
        internal void ScanShipped(Action<long, XmlElem> visit, Action<string, Exception> skip)
        {
            if (BundleXml != null)
            {
                M2dArchive a = Data.Open(BundleXml);
                foreach (M2dEntry e in a.Entries)
                {
                    long block;
                    if (!bundle.Match(e.Name.Replace('\\', '/'), out block)) continue;
                    XmlElem root = ScanRoot(a, BundleXml, e, skip);
                    if (root == null) continue;
                    foreach (XmlElem row in root.Children)
                    {
                        long id;
                        if (row.Name == bundle.Row && TryId(row.Attr(bundle.Key), out id)) visit(id, row);
                    }
                }
            }
            if (FileXml != null)
            {
                M2dArchive a = Data.Open(FileXml);
                foreach (KeyValuePair<long, string> kv in FileIndex())
                {
                    M2dEntry e = a.Find(kv.Value);
                    XmlElem root = e == null ? null : ScanRoot(a, FileXml, e, skip);
                    if (root != null) visit(kv.Key, root);
                }
            }
        }

        XmlElem ScanRoot(M2dArchive a, string archive, M2dEntry e, Action<string, Exception> skip)
        {
            object o;
            if (Data.Cache.TryGetValue("entitydoc\n" + archive + "\n" + e.Name.ToLowerInvariant(), out o))
                return o == null ? null : ((DocInfo)o).Doc.Root;
            return WhereUsed.Parse(a, archive, e, skip);
        }

        /// <summary>The shipped ids the mod edits: a change of a bundle row (or below it), or an edit of a per-id file.</summary>
        internal List<long> TouchedIds()
        {
            var ids = new List<long>();
            foreach (ModOp op in Mod.Ops.ToArray())
            {
                long id;
                var ed = op as EditOp;
                if (ed != null && BundleXml != null && ed.Archive == BundleXml && bundle.Match(ed.Entry.Replace('\\', '/'), out id))
                {
                    DocInfo doc = Doc(BundleXml, ed.Entry.Replace('\\', '/'));
                    if (doc == null) continue;
                    foreach (RowOp r in ed.Rows)
                    {
                        string at = r.At ?? r.In ?? r.After ?? r.Before;
                        if (string.IsNullOrEmpty(at)) continue;
                        XmlElem e;
                        string err;
                        try { e = RowPath.Parse(at).TryResolve(doc.Doc.Root, out err); } catch (ModKitException) { continue; }
                        while (e != null && e.Parent != null && e.Parent.Parent != null) e = e.Parent;     // up to the row under the root
                        if (e != null && TryId(e.Attr(bundle.Key), out id) && !ids.Contains(id)) ids.Add(id);
                    }
                }
                if (ed != null && FileXml != null && ed.Archive == FileXml && files.Match(ed.Entry.Replace('\\', '/'), out id) && !ids.Contains(id)) ids.Add(id);
                var en = op as EntryOp;
                if (en != null && FileXml != null && en.Archive == FileXml && !en.Delete && en.File != null && files.Match(en.Name.Replace('\\', '/'), out id) && !ids.Contains(id)) ids.Add(id);
            }
            return ids;
        }

        /// <summary>The name of an id: as the mod leaves it when asked (mod), else the shipped name.</summary>
        public string NameOf(long id, bool mod)
        {
            if (mod) { try { return OpenAny(id).Name; } catch (ModKitException) { } }
            return OriginalName(id);
        }
    }

    public sealed partial class QuestEditor
    {
        /// <summary>
        /// Every shipped quest element with its place (QuestPlace.Key): the table's rows, then the quests of each quest
        /// file.
        /// </summary>
        internal void ScanShipped(Action<long, XmlElem, string> visit, Action<string, Exception> skip)
        {
            if (TableXml != null)
            {
                Dictionary<long, List<XmlElem>> rows = null;
                string name = null;
                try { rows = TableRows(); name = Doc(TableXml, TableEntry).Name; }
                catch (ModKitException ex) { skip(TableXml + " " + TableEntry, ex); }
                if (rows != null)
                    foreach (KeyValuePair<long, List<XmlElem>> kv in rows)
                        foreach (XmlElem row in kv.Value) visit(kv.Key, row, QuestPlace.KeyOf(TableXml, name, row.Attr("feature") ?? ""));
            }
            foreach (string archive in FileArchives())
            {
                M2dArchive a = Data.Open(archive);
                foreach (KeyValuePair<long, string> kv in FileIndex(archive))
                {
                    M2dEntry e = a.Find(kv.Value);
                    if (e == null) continue;
                    object o;
                    XmlElem root = Data.Cache.TryGetValue("questdoc\n" + archive + "\n" + kv.Value.ToLowerInvariant(), out o)
                        ? (o == null ? null : ((DocInfo)o).Doc.Root) : WhereUsed.Parse(a, archive, e, skip);
                    if (root == null) continue;
                    var qs = new List<KeyValuePair<XmlElem, string>>();
                    QuestElems(root, qs);
                    foreach (KeyValuePair<XmlElem, string> q in qs) visit(kv.Key, q.Key, QuestPlace.KeyOf(archive, kv.Value, q.Value));
                }
            }
        }

        /// <summary>The shipped quest ids the mod edits: a change in the quest table or in a quest file.</summary>
        internal List<long> TouchedIds()
        {
            var ids = new List<long>();
            foreach (ModOp op in Mod.Ops.ToArray())
            {
                var ed = op as EditOp;
                if (ed == null) continue;
                long id;
                Match m = FileRx.Match(ed.Entry.Replace('\\', '/'));
                if (m.Success && FileArchives().Contains(ed.Archive))
                {
                    if (TryId(m.Groups[1].Value, out id) && !ids.Contains(id)) ids.Add(id);
                    continue;
                }
                if (ed.Archive != TableXml || !SameEntry(ed.Entry, TableEntry)) continue;
                DocInfo doc = Doc(TableXml, TableEntry);
                if (doc == null) continue;
                foreach (RowOp r in ed.Rows)
                {
                    string at = r.At ?? r.In ?? r.After ?? r.Before;
                    if (string.IsNullOrEmpty(at)) continue;
                    XmlElem e;
                    string err;
                    try { e = RowPath.Parse(at).TryResolve(doc.Doc.Root, out err); } catch (ModKitException) { continue; }
                    while (e != null && e.Parent != null && e.Name != "quest") e = e.Parent;
                    if (e != null && e.Name == "quest" && TryId(e.Attr("id"), out id) && !ids.Contains(id)) ids.Add(id);
                }
            }
            return ids;
        }

        /// <summary>The element name of a node (shipped, or one the mod adds).</summary>
        internal string NodeName(QuestNode n) { return n.Literal ? Frag(n).Name : n.Orig.Name; }

        /// <summary>The name of a quest: as the mod leaves it when asked (mod), else the shipped name.</summary>
        public string NameOf(long id, bool mod)
        {
            if (mod) { try { return Open(id).Name; } catch (ModKitException) { } }
            string n;
            return Names().TryGetValue(id, out n) ? n : null;
        }
    }

    public sealed partial class GroupEditor
    {
        internal delegate void RowVisit(GroupKind kind, long id, int index, string describe, XmlElem row);

        /// <summary>Every shipped stock row of the shops and every shipped row of the drop boxes.</summary>
        internal void ScanShipped(RowVisit visit, Action<string, Exception> skip) { Scan(null, visit, skip); }

        /// <summary>The entries that hold group rows: (archive, entry, row element under the root, its id attribute, kind).</summary>
        sealed class RowEntry { internal string Archive, Entry, Row, Key; internal GroupKind Kind; internal bool Flat; }

        List<RowEntry> RowEntries()
        {
            var l = new List<RowEntry>();
            l.Add(new RowEntry { Archive = Planner.ServerTables, Entry = Planner.ShopStockEntry, Row = "shop", Key = "shopID", Kind = GroupKind.Shop });
            l.Add(new RowEntry { Archive = Planner.ServerTables, Entry = Planner.ServerDropEntry, Row = DropMap.BoxNested, Key = "dropBoxID", Kind = GroupKind.DropBox });
            l.Add(new RowEntry { Archive = Profile.KmsClient.ArchiveIn("x:Xml.m2d"), Entry = Planner.KmsDropEntry, Row = DropMap.BoxNested, Key = "dropBoxID", Kind = GroupKind.DropBox });
            foreach (Profile p in new[] { Profile.Server, Profile.GmsClient })
            {
                string a = p.ArchiveIn("x:Xml.m2d");
                if (!Data.Has(p) || !Data.Exists(a)) continue;
                foreach (string entry in Planner.FlatSplits(Data.Open(a)))
                    l.Add(new RowEntry { Archive = a, Entry = entry, Row = DropMap.RowFlat, Key = "individualDropBoxID", Kind = GroupKind.DropBox, Flat = true });
            }
            return l;
        }

        static string Nz(string v) { return string.IsNullOrEmpty(v) ? "?" : v; }

        static long RowId(XmlElem e, RowEntry re)
        {
            long id;
            return e != null && e.Name == re.Row && long.TryParse(e.Attr(re.Key) ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out id) ? id : 0;
        }

        /// <summary>The stock rows and drop rows of every group, as the plan writes them (null: as shipped).</summary>
        void Scan(Plan plan, RowVisit visit, Action<string, Exception> skip = null)
        {
            var n = new Dictionary<string, int>();
            foreach (RowEntry re in RowEntries())
            {
                XmlDoc d;
                try { d = Read(plan, re.Archive, re.Entry); }
                catch (ModKitException ex) { if (skip == null) throw; skip(re.Archive + " " + re.Entry, ex); continue; }
                catch (IOException ex) { if (skip == null) throw; skip(re.Archive + " " + re.Entry, ex); continue; }
                if (d == null || d.Root == null) continue;
                // the flat splits of one archive count their rows together; the nested tables count per table
                string scope = re.Flat ? re.Archive + "\n" : re.Archive + "\n" + re.Entry + "\n";
                foreach (XmlElem top in d.Root.Children)
                {
                    long id = RowId(top, re);
                    if (id == 0) continue;
                    string k = scope + Inv(id);
                    int i;
                    n.TryGetValue(k, out i);
                    if (re.Kind == GroupKind.Shop)
                    {
                        foreach (XmlElem it in top.Children)
                            if (it.Name == "item") visit(GroupKind.Shop, id, i++, "sn " + (it.Attr("sn") ?? "?"), it);
                    }
                    else if (re.Flat) visit(GroupKind.DropBox, id, i++, "group " + Nz(top.Attr("dropGroup")), top);
                    else
                        foreach (XmlElem g in top.Children)
                        {
                            if (g.Name != DropMap.GroupNested) continue;
                            string label = "group " + Nz(g.Attr("dropGroupID") ?? g.Attr("id"));
                            foreach (XmlElem r in g.Children) if (r.Name == DropMap.RowNested) visit(GroupKind.DropBox, id, i++, label, r);
                        }
                    n[k] = i;
                }
            }
        }

        /// <summary>The mod's raw row edits (the Tables view's) of the entries that hold group rows.</summary>
        List<EditOp> RawEdits(List<RowEntry> entries)
        {
            var l = new List<EditOp>();
            foreach (ModOp op in Mod.Ops)
            {
                var ed = op as EditOp;
                if (ed == null || ed.Rows.Count == 0) continue;
                foreach (RowEntry re in entries)
                    if (re.Archive == ed.Archive && string.Equals(re.Entry, ed.Entry.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) { l.Add(ed); break; }
            }
            return l;
        }

        /// <summary>
        /// The shops and drop boxes the mod's raw row edits reach: each row op resolved on the shipped entry and taken
        /// up to the shop, box or flat row under the root (a row added under the root by its own id).
        /// </summary>
        internal List<KeyValuePair<GroupKind, long>> RawTouched()
        {
            var l = new List<KeyValuePair<GroupKind, long>>();
            List<RowEntry> entries = RowEntries();
            foreach (EditOp ed in RawEdits(entries))
            {
                RowEntry re = null;
                foreach (RowEntry x in entries)
                    if (x.Archive == ed.Archive && string.Equals(x.Entry, ed.Entry.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) { re = x; break; }
                XmlDoc d = Read(null, re.Archive, re.Entry);
                if (d == null || d.Root == null) continue;
                foreach (RowOp r in ed.Rows)
                {
                    long id = 0;
                    string at = r.At ?? r.In ?? r.After ?? r.Before;
                    XmlElem e = null;
                    if (!string.IsNullOrEmpty(at))
                    {
                        string err;
                        try { e = RowPath.Parse(at).TryResolve(d.Root, out err); } catch (ModKitException) { e = null; }
                        while (e != null && e.Parent != null && e.Parent != d.Root) e = e.Parent;
                        if (e == d.Root) e = null;     // added under the root: the new row says its id
                        id = RowId(e, re);
                    }
                    if (id == 0 && r.Kind == RowOpKind.Add && r.Xml != null)
                    {
                        try { id = RowId(XmlDoc.ParseFragment(r.Xml, "the added row").Root, re); } catch (ModKitException) { }
                    }
                    var kv = new KeyValuePair<GroupKind, long>(re.Kind, id);
                    if (id != 0 && !l.Contains(kv)) l.Add(kv);
                }
            }
            return l;
        }

        /// <summary>
        /// The rows of the groups as the mod leaves them: its group ops and its raw row edits of the group entries
        /// planned together, each entry read back from the plan.
        /// </summary>
        internal void ScanMod(RowVisit visit)
        {
            List<EditOp> raw = RawEdits(RowEntries());
            if (Mod.Groups.Count == 0 && raw.Count == 0) { Scan(null, visit); return; }
            var m = new Mod { Id = Mod.Id, Name = Mod.Name, Version = Mod.Version, Dir = Mod.Dir };
            foreach (GroupOp g in Mod.Groups) m.Groups.Add(g);
            foreach (EditOp ed in raw) m.Ops.Add(ed);
            foreach (IdRange r in Mod.Ids) m.Ids.Add(r);
            Scan(Planner.Build(Data, new List<Mod> { m }, new PlanOptions { Maps = Maps, NoSync = true }), visit);
        }
    }
}
