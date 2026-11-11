// "Where used": the places of the data and the mod that name an id, through the fields IdLookup knows:
//   quests     start/complete npc, the condition codes (QuestCodeKind: npc, item, quest, skill, buff = effect, ...),
//              the item codes of the reward lists (completeReward and acceptReward)
//   shops      the item of each stock row (shop_game.xml)
//   drop boxes the items of each row (the nested drop tables and the flat splits; itemID and item are one place)
//   npcs       shopId, the *DropBoxId lists, skill@ids, additionalEffect@codes (NpcFieldKind)
//   items      skill@skillID, AdditionalEffect@id, the repacking scrolls (ItemFieldKind)
// Skills name no id through a field IdLookup knows, so a skill is never the place that names something.
//
// The shipped places are read once per data set (each file parsed and dropped again, no document kept) and the index
// is kept in the data set's cache, as the plan's inputs are. The mod is laid over it on every question: each quest,
// npc, item, shop or drop box the mod adds or edits is read as the mod leaves it (the editors' own drafts) and its
// places replace the shipped ones, so an edit that adds a name shows (as "new") and one that removes it hides it.
using System;
using System.Collections.Generic;
using System.Globalization;
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
        /// <summary>The mod names it here and the shipped data does not (an edit, or a place the mod adds).</summary>
        public bool New;
        /// <summary>The place is one the mod adds (a clone or a new id).</summary>
        public bool SourceNew;

        internal string Key { get { return Kind + "\n" + Id.ToString(CultureInfo.InvariantCulture) + "\n" + Where; } }

        public override string ToString()
        {
            return Kind + " " + Id.ToString(CultureInfo.InvariantCulture) + (Name.Length > 0 ? " " + Name : "") + ": " + Where + (New ? " (new)" : "");
        }
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
                if (kind == null) return;
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

        static void QuestReward(Refs into, long id, string reward, string list, int index, string code)
        {
            foreach (long t in Ids(code)) into.Add(IdLookup.Item, t, Hit(IdLookup.Quest, id, reward + " " + list, reward + "/" + list, index));
        }

        static void QuestElem(Refs into, long id, XmlElem q)
        {
            int cond = 0;
            foreach (XmlElem c in q.Children)
            {
                if (c.Name == "condition") { QuestCondition(into, id, cond, c.Attr("type") ?? "", c.Attr("code")); cond++; }
                else if (c.Name == "start" || c.Name == "complete")
                    foreach (long t in Ids(c.Attr("npc"))) into.Add(IdLookup.Npc, t, Hit(IdLookup.Quest, id, c.Name + " npc", c.Name + "@npc", -1));
                else if (c.Name == QuestDraft.CompleteReward || c.Name == QuestDraft.AcceptReward)
                {
                    var n = new Dictionary<string, int>();
                    foreach (XmlElem r in c.Children)
                    {
                        if (Array.IndexOf(QuestDraft.RewardLists, r.Name) < 0) continue;
                        string code = QuestDraft.Norm(r.Attr("code"));
                        if (code.Length == 0) continue;
                        int i;
                        n.TryGetValue(r.Name, out i);
                        n[r.Name] = i + 1;
                        QuestReward(into, id, c.Name, r.Name, i, code);
                    }
                }
            }
        }

        static void GroupRow(Refs into, GroupKind kind, long id, int index, string describe, IEnumerable<KeyValuePair<string, string>> fields)
        {
            string sk = kind == GroupKind.Shop ? IdLookup.Shop : IdLookup.DropBox;
            foreach (KeyValuePair<string, string> f in fields)
            {
                string k = IdLookup.GroupRowFieldKind(kind, f.Key);
                if (k == null) continue;
                // a drop box's item is "itemID" in the nested tables and "item" in the flat ones: one place by name
                string label = kind == GroupKind.DropBox && f.Key == "itemID" ? "item" : f.Key;
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

        Index Shipped()
        {
            const string key = IndexKey;
            object o;
            if (Data.Cache.TryGetValue(key, out o)) return (Index)o;
            var ix = new Index();
            var seen = new Refs();      // one list over all sources: a place two targets both have counts once
            ItemEditor it = Items();
            if (it != null) it.ScanShipped(delegate(long id, XmlElem e) { EntityElem(seen, IdLookup.Item, id, e); });
            NpcEditor np = Npcs();
            if (np != null) np.ScanShipped(delegate(long id, XmlElem e) { EntityElem(seen, IdLookup.Npc, id, e); });
            QuestEditor q = Quests();
            if (q != null) q.ScanShipped(delegate(long id, XmlElem e) { QuestElem(seen, id, e); });
            Groups().ScanShipped(delegate(GroupKind kind, long id, int index, string describe, XmlElem row) { GroupRow(seen, kind, id, index, describe, Attrs(row)); });
            ix.Add(seen);
            Data.Cache[key] = ix;
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
                add(g.Kind == GroupKind.Shop ? IdLookup.Shop : IdLookup.DropBox, g.Id, g.New || g.IsClone);
            return l;
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
                    QuestDraft d = Quests().Open(s.Id);
                    foreach (long t in Ids(d.StartNpc)) r.Add(IdLookup.Npc, t, Hit(IdLookup.Quest, s.Id, "start npc", "start@npc", -1));
                    foreach (long t in Ids(d.CompleteNpc)) r.Add(IdLookup.Npc, t, Hit(IdLookup.Quest, s.Id, "complete npc", "complete@npc", -1));
                    foreach (QuestCondition c in d.Conditions()) QuestCondition(r, s.Id, c.Index, c.Type ?? "", c.Code);
                    foreach (string reward in new[] { QuestDraft.CompleteReward, QuestDraft.AcceptReward })
                        foreach (string list in QuestDraft.RewardLists)
                        {
                            if (!d.HasList(list)) continue;
                            foreach (QuestReward w in d.Rewards(reward, list)) QuestReward(r, s.Id, reward, list, w.Index, w.Code);
                        }
                }
                else
                {
                    GroupKind kind = s.Kind == IdLookup.Shop ? GroupKind.Shop : GroupKind.DropBox;
                    GroupView v = Groups().Open(kind, s.Id);
                    foreach (GroupSideView side in v.Sides)
                        for (int i = 0; i < side.Rows.Count; i++)
                            GroupRow(r, kind, s.Id, i, GroupEditor.RowLabel(kind, side.Rows[i]), side.Rows[i].Fields);
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
                    return g == null ? "" : g.IsClone ? "copy of " + Inv(g.Clone) : g.New ? "" : "changed";
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
            string tk = TargetKey(kind, id);
            var touched = new Dictionary<string, Source>();
            var hits = new List<UseHit>();
            foreach (Source s in Touched())
            {
                touched[TargetKey(s.Kind, s.Id)] = s;
                HashSet<string> had;
                ix.Of.TryGetValue(TargetKey(s.Kind, s.Id), out had);
                foreach (Ref x in ModRefs(s).List)
                {
                    if (x.Kind != kind || x.Id != id) continue;
                    UseHit h = x.Hit;
                    h.SourceNew = s.New;
                    h.New = had == null || !had.Contains(tk + "\n" + h.Key);
                    hits.Add(h);
                }
            }
            var rest = new List<UseHit>();
            List<UseHit> shipped;
            if (ix.By.TryGetValue(tk, out shipped))
                foreach (UseHit h in shipped)
                    if (!touched.ContainsKey(TargetKey(h.Kind, h.Id)))
                        rest.Add(new UseHit { Kind = h.Kind, Id = h.Id, Where = h.Where, Field = h.Field, Index = h.Index });
            rest.Sort(delegate(UseHit a, UseHit b)
            {
                int c = Array.IndexOf(SourceOrder, a.Kind).CompareTo(Array.IndexOf(SourceOrder, b.Kind));
                return c != 0 ? c : a.Id != b.Id ? a.Id.CompareTo(b.Id) : string.CompareOrdinal(a.Where, b.Where);
            });
            hits.AddRange(rest);
            if (max > 0 && hits.Count > max) hits.RemoveRange(max, hits.Count - max);
            foreach (UseHit h in hits) h.Name = NameOf(h.Kind, h.Id, touched.ContainsKey(TargetKey(h.Kind, h.Id)));
            return hits;
        }

        public List<UseHit> Of(string kind, long id) { return Of(kind, id, 0); }

        static readonly string[] SourceOrder = { IdLookup.Quest, IdLookup.Shop, IdLookup.DropBox, IdLookup.Npc, IdLookup.Item };

        /// <summary>
        /// A line for the end of a Remove clone: the places that still name the removed id, or null when none does.
        /// </summary>
        public string LeftBehind(string kind, long id, int show)
        {
            List<UseHit> l = Of(kind, id);
            if (l.Count == 0) return null;
            var parts = new List<string>();
            for (int i = 0; i < l.Count && i < show; i++) parts.Add(l[i].ToString());
            return "warning: " + l.Count + " place(s) still name " + kind + " " + Inv(id) + ": " + string.Join("; ", parts.ToArray())
                + (l.Count > show ? "; ..." : "") + " (Where used lists them all)";
        }
    }

    // ---- the editors' side: reading every shipped source once, and which ids the mod edits ----

    public abstract partial class EntityEditor
    {
        /// <summary>
        /// Every shipped row of the bundle side and every shipped per-id file, with its id. A file is parsed and
        /// dropped again (a document the editor already holds is used as it is).
        /// </summary>
        internal void ScanShipped(Action<long, XmlElem> visit)
        {
            if (BundleXml != null)
            {
                M2dArchive a = Data.Open(BundleXml);
                foreach (M2dEntry e in a.Entries)
                {
                    long block;
                    if (!bundle.Match(e.Name.Replace('\\', '/'), out block)) continue;
                    XmlElem root = ScanRoot(a, BundleXml, e);
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
                    XmlElem root = e == null ? null : ScanRoot(a, FileXml, e);
                    if (root != null) visit(kv.Key, root);
                }
            }
        }

        XmlElem ScanRoot(M2dArchive a, string archive, M2dEntry e)
        {
            object o;
            if (Data.Cache.TryGetValue("entitydoc\n" + archive + "\n" + e.Name.ToLowerInvariant(), out o))
                return o == null ? null : ((DocInfo)o).Doc.Root;
            return XmlDoc.Parse(a.Read(e), archive + " " + e.Name).Root;
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
        /// <summary>Every shipped quest element: the table's rows, then the quests of each quest file.</summary>
        internal void ScanShipped(Action<long, XmlElem> visit)
        {
            foreach (KeyValuePair<long, List<XmlElem>> kv in TableRows())
                foreach (XmlElem row in kv.Value) visit(kv.Key, row);
            foreach (string archive in FileArchives())
            {
                M2dArchive a = Data.Open(archive);
                foreach (KeyValuePair<long, string> kv in FileIndex(archive))
                {
                    M2dEntry e = a.Find(kv.Value);
                    if (e == null) continue;
                    object o;
                    XmlElem root = Data.Cache.TryGetValue("questdoc\n" + archive + "\n" + kv.Value.ToLowerInvariant(), out o)
                        ? (o == null ? null : ((DocInfo)o).Doc.Root) : XmlDoc.Parse(a.Read(e), archive + " " + e.Name).Root;
                    if (root == null) continue;
                    var qs = new List<KeyValuePair<XmlElem, string>>();
                    QuestElems(root, qs);
                    foreach (KeyValuePair<XmlElem, string> q in qs) visit(kv.Key, q.Key);
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

        /// <summary>How a row is named in a where-used line: a shop row by its sn, a drop row by its group.</summary>
        internal static string RowLabel(GroupKind kind, GroupRowView r)
        {
            if (kind == GroupKind.Shop) return "sn " + (r.Sn ?? "?");
            return r.Group != null ? "group " + r.Group : "row";
        }

        /// <summary>Every shipped stock row of the shops and every shipped row of the drop boxes.</summary>
        internal void ScanShipped(RowVisit visit)
        {
            XmlDoc stock = Read(null, Planner.ServerTables, Planner.ShopStockEntry);
            if (stock != null && stock.Root != null)
            {
                var n = new Dictionary<long, int>();
                foreach (XmlElem s in stock.Root.Children)
                {
                    long id;
                    if (s.Name != "shop" || !long.TryParse(s.Attr("shopID") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) continue;
                    int i;
                    n.TryGetValue(id, out i);
                    foreach (XmlElem it in s.Children)
                        if (it.Name == "item") visit(GroupKind.Shop, id, i++, "sn " + (it.Attr("sn") ?? "?"), it);
                    n[id] = i;
                }
            }
            foreach (KeyValuePair<string, string> src in new[] {
                new KeyValuePair<string, string>(Planner.ServerTables, Planner.ServerDropEntry),
                new KeyValuePair<string, string>(Profile.KmsClient.ArchiveIn("x:Xml.m2d"), Planner.KmsDropEntry) })
            {
                XmlDoc d = Read(null, src.Key, src.Value);
                if (d == null || d.Root == null) continue;
                foreach (XmlElem box in d.Root.Children)
                {
                    long id;
                    if (box.Name != DropMap.BoxNested || !long.TryParse(box.Attr("dropBoxID") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) continue;
                    int i = 0;
                    foreach (XmlElem g in box.Children)
                    {
                        if (g.Name != DropMap.GroupNested) continue;
                        string label = "group " + (g.Attr("dropGroupID") ?? g.Attr("id") ?? "?");
                        foreach (XmlElem r in g.Children) if (r.Name == DropMap.RowNested) visit(GroupKind.DropBox, id, i++, label, r);
                    }
                }
            }
            foreach (Profile p in new[] { Profile.Server, Profile.GmsClient })
            {
                string a = p.ArchiveIn("x:Xml.m2d");
                if (!Data.Has(p) || !Data.Exists(a)) continue;
                var n = new Dictionary<long, int>();
                foreach (string entry in Planner.FlatSplits(Data.Open(a)))
                {
                    XmlDoc d = Read(null, a, entry);
                    if (d == null || d.Root == null) continue;
                    foreach (XmlElem r in d.Root.Children)
                    {
                        long id;
                        if (r.Name != DropMap.RowFlat || !long.TryParse(r.Attr("individualDropBoxID") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) continue;
                        int i;
                        n.TryGetValue(id, out i);
                        n[id] = i + 1;
                        visit(GroupKind.DropBox, id, i, "group " + (r.Attr("dropGroup") ?? "?"), r);
                    }
                }
            }
        }
    }
}
