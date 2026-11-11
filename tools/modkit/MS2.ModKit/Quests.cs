// The quest editor: one quest on every target at once.
//
// What makes up a quest:
//   client:Xml.m2d      questdata/questdata.xml           <quest id> rows, one file for all quests. This is what the
//   (kms-client)                                          Korean client reads. The format is sparse: an attribute
//                                                         at its default and an empty element are left out.
//   client:Xml.m2d      quest/<8-digit id>.xml            <environment feature>/<quest>: shipped beside the table
//                                                         (the client does not read it); kept in step with it.
//   server:Xml.m2d,     quest/<8-digit id>.xml            the same file format with every element and attribute
//   gms-client:Xml.m2d                                    written; the id is the file name. A mod's server lines
//                                                         are mirrored to the gms-client by the plan.
//   every target        string/<lang>/questdescription*.xml   <quest questID name desc manual complete> rows; a
//                                                         language has one file or one file per quest group.
//
// Fields are "element@attribute" under <quest> ("require@level", "start@npc", "completeReward@exp"). A field is
// set in every place of the quest; a place that lacks the element gets it (the sparse table), unless the value
// is the default (empty or 0).
//
// Reward items are rows under <completeReward>: <v code rank count> in the table, <essentialItem>,
// <globalEssentialItem> and the two job lists in the files. Shipped data does not keep the table's list and the
// files' lists equal, so every list is edited under its own name; SetRewardItems writes one list to all three.
//
// The mod's lines: a change of a shipped element is <change at>, a new element under a shipped one is
// <add in> with literal XML, a removed one is <delete at>. A cloned quest is a literal <quest> row in the table
// (its edits rewrite that text), a copy of the source's file with edits on the new entry, and copies of the
// source's string rows. With "listed" (the default) it also gets copies of the source's rows in the files that
// list quests by id:
//   every target        script/quest/questscript*.xml          <quest id> rows: the talk script of the quest
//   every target        table/questtofieldtable.xml            <quest id fields>
//   every target        string/<lang>/specialquesttype.xml     <key id name>
//   server:Server.m2d   table/Server/QuestScriptCondition*.xml <condition questID ...>, any number per quest
//   server:Server.m2d   table/Server/QuestScriptFunction*.xml  <function questID ...>, any number per quest
// The copied talk script keeps the source's text keys ($script:<n>$): the talk texts are keyed by a text id.
//
// <acceptReward> has item rows only (the same list names as <completeReward>) and no "code 0" row.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public sealed class QuestHit
    {
        public long Id;
        public string Name = "";
        public bool Added;
        public override string ToString() { return Id.ToString(CultureInfo.InvariantCulture) + "  " + Name + (Added ? "  (new)" : ""); }
    }

    /// <summary>One place a quest is written in: a table row, a quest element of a per-id file or a string row.</summary>
    public sealed class QuestPlace
    {
        public const string Table = "table", File = "file", Text = "text";
        public string Archive, Entry, Kind;
        /// <summary>The feature of the row or of its environment ("" for none).</summary>
        public string Feature = "";
        /// <summary>The language folder of a string row.</summary>
        public string Language;
        /// <summary>True for a place the mod adds (a cloned quest).</summary>
        public bool IsNew;
        internal QuestNode Root;

        public string Target { get { Profile p = Profile.OfArchive(Archive); return p != null ? p.Name : DataSet.RootOf(Archive); } }
        public string Label { get { return Target + "  " + Entry + (Feature.Length > 0 ? "  [" + Feature + "]" : ""); } }
        public override string ToString() { return Label; }
    }

    // An element of a quest: a shipped one (Orig; the mod's lines name it by its selector), an element inside a
    // literal row the mod adds (Add with Xml; Path = child indexes from the row's root), or a copied row (Add with
    // Copy; Orig = the row it copies).
    sealed class QuestNode
    {
        internal QuestPlace Place;
        internal XmlElem Orig;
        internal RowOp Add;
        internal int[] Path = new int[0];
        internal bool Literal { get { return Add != null && Add.Xml != null; } }
    }

    public sealed class QuestItem
    {
        public string Code = "", Rank = "1", Count = "1";
        public QuestItem() { }
        public QuestItem(long code, int rank, long count)
        {
            Code = code.ToString(CultureInfo.InvariantCulture); Rank = rank.ToString(CultureInfo.InvariantCulture); Count = count.ToString(CultureInfo.InvariantCulture);
        }
    }

    public sealed class QuestReward
    {
        public string List;
        public int Index;
        public string Code, Rank, Count;
        /// <summary>The number of places that have this row, and whether one of the places of the list lacks it or has other values.</summary>
        public int Places;
        public bool Differs;
        /// <summary>Every place of the list, and what each holds for this row ("code xcount (rank r)"; null: no such row there).</summary>
        public readonly List<QuestPlace> In = new List<QuestPlace>();
        public readonly List<string> Values = new List<string>();
        internal readonly List<QuestNode> Nodes = new List<QuestNode>();
    }

    public sealed class QuestCondition
    {
        public int Index;
        public string Type, Code, Value;
        public int Places;
        public bool Differs;
    }

    public sealed class QuestDraft
    {
        public readonly QuestEditor Editor;
        public readonly long Id;
        public readonly List<QuestPlace> Places = new List<QuestPlace>();

        /// <summary>The reward lists: the table's, then the files' four.</summary>
        public static readonly string[] RewardLists = { "v", "essentialItem", "globalEssentialItem", "essentialJobItem", "globalEssentialJobItem" };
        public static readonly string[] TextFields = { "name", "desc", "manual", "complete" };

        internal QuestDraft(QuestEditor editor, long id) { Editor = editor; Id = id; }

        public bool IsNew { get { foreach (QuestPlace p in Places) if (p.Kind != QuestPlace.Text) return p.IsNew; return false; } }

        List<QuestNode> Roots(string kind)
        {
            var l = new List<QuestNode>();
            foreach (QuestPlace p in Places)
                if (p.Kind != QuestPlace.Text && (kind == null || p.Kind == kind)) l.Add(p.Root);
            return l;
        }

        /// <summary>The places of one kind (QuestPlace.Table, File or Text).</summary>
        public List<QuestPlace> PlacesOf(string kind)
        {
            var l = new List<QuestPlace>();
            foreach (QuestPlace p in Places) if (p.Kind == kind) l.Add(p);
            return l;
        }

        internal static string Norm(string v) { return v == null || v == "0" ? "" : v; }

        static void Split(string field, out string elem, out string attr)
        {
            int at = field == null ? -1 : field.IndexOf('@');
            if (at <= 0 || at == field.Length - 1) throw new ModKitException("a quest field is element@attribute, not \"" + field + "\"");
            elem = field.Substring(0, at); attr = field.Substring(at + 1);
        }

        QuestNode Kid(QuestNode n, string name)
        {
            List<QuestNode> k = Editor.Kids(n, name);
            return k.Count == 0 ? null : k[0];
        }

        /// <summary>The value of a field in one place (null: the place does not write it).</summary>
        public string Get(QuestPlace p, string field)
        {
            string elem, attr; Split(field, out elem, out attr);
            QuestNode k = Kid(p.Root, elem);
            return k == null ? null : Editor.Attr(k, attr);
        }

        /// <summary>The value of a field: the first place that writes it, the table first.</summary>
        public string Get(string field)
        {
            foreach (QuestPlace p in Places)
            {
                if (p.Kind == QuestPlace.Text) continue;
                string v = Get(p, field);
                if (v != null) return v;
            }
            return null;
        }

        /// <summary>True when the places do not agree on a field (a missing value, an empty one and 0 count as equal).</summary>
        public bool Differs(string field)
        {
            string first = null;
            foreach (QuestPlace p in Places)
            {
                if (p.Kind == QuestPlace.Text) continue;
                string v = Norm(Get(p, field));
                if (first == null) first = v; else if (v != first) return true;
            }
            return false;
        }

        /// <summary>
        /// Sets a field in every place. A place without the element gets it, and one without the attribute gets
        /// that, except when the value is the default (empty or 0); an element the mod added for this field alone
        /// leaves the mod again when the value goes back to the default. Returns the number of places that hold
        /// the value.
        /// </summary>
        public int Set(string field, string value)
        {
            if (value == null) throw new ArgumentNullException("value");
            string elem, attr; Split(field, out elem, out attr);
            int n = 0;
            foreach (QuestNode root in Roots(null))
            {
                QuestNode k = Kid(root, elem);
                if (k == null)
                {
                    if (Norm(value).Length == 0) continue;
                    k = Editor.AddKid(root, "<" + elem + " />");
                }
                else if (Norm(value).Length == 0 && Editor.AddedOnlyFor(k, attr))
                {
                    // the element is there only because this field was set before: the value taken back takes it out again
                    Editor.Remove(k);
                    continue;
                }
                string was = Editor.Attr(k, attr);
                if (was == null && Norm(value).Length == 0) continue;
                if (was != value) Editor.SetAttr(k, attr, value);
                n++;
            }
            return n;
        }

        static string N(long v) { return v.ToString(CultureInfo.InvariantCulture); }

        public string Level { get { return Get("require@level"); } }
        public int SetLevel(int level)
        {
            if (level < 0) throw new ModKitException("the level must be 0 or more");
            return Set("require@level", N(level));
        }

        public string StartNpc { get { return Get("start@npc"); } }
        public int SetStartNpc(long npc)
        {
            if (npc < 0) throw new ModKitException("the npc id must be 0 or more");
            return Set("start@npc", N(npc));
        }

        public string CompleteNpc { get { return Get("complete@npc"); } }
        public int SetCompleteNpc(long npc)
        {
            if (npc < 0) throw new ModKitException("the npc id must be 0 or more");
            return Set("complete@npc", N(npc));
        }

        public string Exp { get { return Get("completeReward@exp"); } }
        public int SetExp(long exp)
        {
            if (exp < 0) throw new ModKitException("the exp must be 0 or more");
            return Set("completeReward@exp", N(exp));
        }

        public string Meso { get { return Get("completeReward@money"); } }
        public int SetMeso(long meso)
        {
            if (meso < 0) throw new ModKitException("the meso must be 0 or more");
            return Set("completeReward@money", N(meso));
        }

        // ---- reward items ----

        /// <summary>The reward elements: what the quest gives when it is completed and when it is accepted.</summary>
        public const string CompleteReward = "completeReward", AcceptReward = "acceptReward";

        static void CheckList(string list)
        {
            if (Array.IndexOf(RewardLists, list) < 0) throw new ModKitException("no reward list \"" + list + "\"");
        }

        static void CheckReward(string reward)
        {
            if (reward != CompleteReward && reward != AcceptReward) throw new ModKitException("no reward element \"" + reward + "\" (completeReward, acceptReward)");
        }

        List<QuestNode> ListRoots(string list) { return Roots(list == "v" ? QuestPlace.Table : QuestPlace.File); }

        // every row of a list in one place, with the "code 0" row the full format writes for an empty list
        List<QuestNode> RawRows(QuestNode root, string reward, string list)
        {
            QuestNode cr = Kid(root, reward);
            return cr == null ? new List<QuestNode>() : Editor.Kids(cr, list);
        }

        List<QuestNode> Rows(QuestNode root, string reward, string list)
        {
            List<QuestNode> rows = RawRows(root, reward, list);
            rows.RemoveAll(delegate(QuestNode r) { return Norm(Editor.Attr(r, "code")).Length == 0; });
            return rows;
        }

        /// <summary>True when the data set has a place for the list (the table for "v", a quest file for the others).</summary>
        public bool HasList(string list) { CheckList(list); return ListRoots(list).Count > 0; }

        /// <summary>The rows of one reward list. Row i is read from the first place that has an i-th row.</summary>
        public List<QuestReward> Rewards(string list) { return Rewards(CompleteReward, list); }

        /// <summary>
        /// The rows of one list of a reward element (CompleteReward or AcceptReward). The places are paired by item:
        /// a row of a place joins the row of the same item id (the same rank and count first) that an earlier place
        /// gave, else it is a row of its own behind the place's row before it. So where the places hold different lists, a row never
        /// stands for two different items; In and Values say what each place has.
        /// </summary>
        public List<QuestReward> Rewards(string reward, string list)
        {
            CheckReward(reward); CheckList(list);
            List<QuestNode> roots = ListRoots(list);
            var l = new List<QuestReward>();
            for (int pi = 0; pi < roots.Count; pi++)
            {
                var taken = new List<QuestReward>();
                int at = 0;     // where a row no earlier place has goes: behind the place's row before it
                foreach (QuestNode row in Rows(roots[pi], reward, list))
                {
                    string c = Editor.Attr(row, "code") ?? "", r = Editor.Attr(row, "rank") ?? "", n = Editor.Attr(row, "count") ?? "";
                    QuestReward w = null;
                    for (int pass = 0; pass < 2 && w == null; pass++)
                        foreach (QuestReward x in l)
                            if (x.Code == c && !taken.Contains(x) && (pass == 1 || (Norm(r) == Norm(x.Rank) && Norm(n) == Norm(x.Count)))) { w = x; break; }
                    if (w == null)
                    {
                        w = new QuestReward { List = list, Code = c, Rank = r, Count = n };
                        foreach (QuestNode root in roots) { w.In.Add(root.Place); w.Values.Add(null); w.Nodes.Add(null); }
                        l.Insert(Math.Min(at, l.Count), w);
                    }
                    else if (Norm(r) != Norm(w.Rank) || Norm(n) != Norm(w.Count)) w.Differs = true;
                    taken.Add(w);
                    at = Math.Max(at, l.IndexOf(w) + 1);
                    w.Nodes[pi] = row;
                    w.Values[pi] = c + " x" + n + " (rank " + r + ")";
                    w.Places++;
                }
            }
            for (int i = 0; i < l.Count; i++)
            {
                l[i].Index = i;
                if (l[i].Places < roots.Count) l[i].Differs = true;
            }
            return l;
        }

        /// <summary>
        /// The shipped row each row of a list stands for (null: a new row), paired as the places of a list are:
        /// a shipped row of the same item, rank and count first, then one of the same item, each shipped row once;
        /// a row whose item no shipped row has takes the shipped row at its index when that item is gone.
        /// </summary>
        public static QuestReward[] PairShipped(IList<QuestReward> shipped, IList<QuestReward> now)
        {
            var was = new QuestReward[now.Count];
            if (shipped == null) return was;
            var taken = new bool[shipped.Count];
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < now.Count; i++)
                {
                    if (was[i] != null) continue;
                    for (int j = 0; j < shipped.Count; j++)
                    {
                        if (taken[j] || shipped[j].Code != now[i].Code) continue;
                        if (pass == 0 && (Norm(shipped[j].Rank) != Norm(now[i].Rank) || Norm(shipped[j].Count) != Norm(now[i].Count))) continue;
                        was[i] = shipped[j]; taken[j] = true;
                        break;
                    }
                }
            for (int i = 0; i < now.Count; i++)
            {
                if (was[i] != null || i >= shipped.Count || taken[i]) continue;
                bool kept = false;
                foreach (QuestReward x in now) if (x.Code == shipped[i].Code) kept = true;
                if (!kept) { was[i] = shipped[i]; taken[i] = true; }
            }
            return was;
        }

        QuestReward RewardRow(string reward, string list, int index)
        {
            List<QuestReward> l = Rewards(reward, list);
            if (index < 0 || index >= l.Count) throw new ModKitException("the list " + list + " has no row " + (index + 1));
            return l[index];
        }

        static void CheckItem(QuestItem it)
        {
            long v;
            if (it == null || !QuestEditor.TryId(it.Code, out v) || v <= 0) throw new ModKitException("the item id must be a number above 0");
            if (!QuestEditor.TryId(it.Rank, out v)) throw new ModKitException("the rank must be a number");
            if (!QuestEditor.TryId(it.Count, out v) || v <= 0) throw new ModKitException("the count must be a number above 0");
        }

        void Write(QuestNode row, QuestItem it)
        {
            if (Editor.Attr(row, "code") != it.Code) Editor.SetAttr(row, "code", it.Code);
            if (Editor.Attr(row, "rank") != it.Rank) Editor.SetAttr(row, "rank", it.Rank);
            if (Editor.Attr(row, "count") != it.Count) Editor.SetAttr(row, "count", it.Count);
        }

        /// <summary>Sets row index of a list (as Rewards numbers them) in every place that has that row. Returns the number of places.</summary>
        public int SetReward(string list, int index, QuestItem item) { return SetReward(CompleteReward, list, index, item); }

        public int SetReward(string reward, string list, int index, QuestItem item)
        {
            CheckReward(reward); CheckList(list); CheckItem(item);
            QuestReward w = RewardRow(reward, list, index);
            List<QuestNode> roots = ListRoots(list);
            for (int pi = 0; pi < roots.Count; pi++)
            {
                if (w.Nodes[pi] == null) continue;
                foreach (QuestNode r in Rows(roots[pi], reward, list))
                    if (!SameNode(r, w.Nodes[pi]) && Same(r, item))
                        throw new ModKitException("the list " + list + " already has item " + item.Code + " rank " + item.Rank + " count " + item.Count + " (" + roots[pi].Place.Label + "); change the count of that row");
            }
            int n = 0;
            foreach (QuestNode node in w.Nodes)
                if (node != null) { Write(node, item); n++; }
            return n;
        }

        void AddRow(QuestNode root, string reward, string list, QuestItem it)
        {
            foreach (QuestNode r in RawRows(root, reward, list))
                if (Norm(Editor.Attr(r, "code")).Length == 0) { Write(r, it); return; }
            QuestNode cr = Kid(root, reward) ?? Editor.AddKid(root, "<" + reward + " />");
            Editor.AddKid(cr, "<" + list + " code=\"" + it.Code + "\" rank=\"" + it.Rank + "\" count=\"" + it.Count + "\" />");
        }

        static bool SameNode(QuestNode a, QuestNode b)
        {
            if (a.Orig != b.Orig || a.Add != b.Add || a.Path.Length != b.Path.Length) return false;
            for (int i = 0; i < a.Path.Length; i++) if (a.Path[i] != b.Path[i]) return false;
            return true;
        }

        bool Same(QuestNode r, QuestItem it)
        {
            return Editor.Attr(r, "code") == it.Code && Norm(Editor.Attr(r, "rank")) == Norm(it.Rank) && Norm(Editor.Attr(r, "count")) == Norm(it.Count);
        }

        /// <summary>
        /// Adds a row to a list in every place of that list. A row equal to one that is there is refused (two equal
        /// new rows are one line twice for the plan): raise the count of the row instead.
        /// </summary>
        public int AddReward(string list, QuestItem item) { return AddReward(CompleteReward, list, item); }

        public int AddReward(string reward, string list, QuestItem item)
        {
            CheckReward(reward); CheckList(list); CheckItem(item);
            List<QuestNode> roots = ListRoots(list);
            if (roots.Count == 0) throw new ModKitException("no target of this data set has the list " + list);
            foreach (QuestNode root in roots)
                foreach (QuestNode r in Rows(root, reward, list))
                    if (Same(r, item)) throw new ModKitException("the list " + list + " already has item " + item.Code + " rank " + item.Rank + " count " + item.Count + "; change the count of that row");
            foreach (QuestNode root in roots) AddRow(root, reward, list, item);
            return roots.Count;
        }

        // A removed row goes away: a row the mod added leaves the mod, a shipped one is deleted. Only the "code 0"
        // row the full format ships for an empty list, when an added item took it, is put back as it shipped.
        void RemoveRow(QuestNode row)
        {
            if (row.Orig != null && Norm(row.Orig.Attr("code")).Length == 0)
                foreach (string a in new[] { "code", "rank", "count" })
                {
                    if (Editor.Attr(row, a) != row.Orig.Attr(a)) Editor.SetAttr(row, a, row.Orig.Attr(a));
                }
            else
            {
                Editor.Remove(row);
                // the reward element the mod added for the row alone goes with its last row
                Editor.RemoveIfEmptyAdd(row);
            }
        }

        /// <summary>Removes row index of a list (as Rewards numbers them) in every place that has that row.</summary>
        public int RemoveReward(string list, int index) { return RemoveReward(CompleteReward, list, index); }

        public int RemoveReward(string reward, string list, int index)
        {
            CheckReward(reward); CheckList(list);
            int n = 0;
            foreach (QuestNode node in RewardRow(reward, list, index).Nodes)
                if (node != null) { RemoveRow(node); n++; }
            return n;
        }

        /// <summary>
        /// "Same on every target": makes the table's v list and the files' essentialItem and globalEssentialItem
        /// lists equal to items, in every place. The job lists stay as they are. Returns the number of places.
        /// </summary>
        public int SetRewardItems(IList<QuestItem> items)
        {
            if (items == null) throw new ArgumentNullException("items");
            for (int i = 0; i < items.Count; i++)
            {
                CheckItem(items[i]);
                for (int j = 0; j < i; j++)
                    if (items[j].Code == items[i].Code && items[j].Rank == items[i].Rank && items[j].Count == items[i].Count)
                        throw new ModKitException("rows " + (j + 1) + " and " + (i + 1) + " are the same item, rank and count; make them one row");
            }
            int n = 0;
            foreach (string list in new[] { "v", "essentialItem", "globalEssentialItem" })
                foreach (QuestNode root in ListRoots(list))
                {
                    List<QuestNode> rows = Rows(root, CompleteReward, list);
                    for (int i = rows.Count - 1; i >= items.Count; i--) RemoveRow(rows[i]);
                    for (int i = 0; i < items.Count && i < rows.Count; i++) Write(rows[i], items[i]);
                    for (int i = rows.Count; i < items.Count; i++) AddRow(root, CompleteReward, list, items[i]);
                    n++;
                }
            return n;
        }

        // ---- conditions ----

        /// <summary>The conditions of the quest, in order. Value is the count ("" or 0: once).</summary>
        public List<QuestCondition> Conditions()
        {
            var per = new List<List<QuestNode>>();
            int max = 0;
            foreach (QuestNode root in Roots(null)) { List<QuestNode> r = Editor.Kids(root, "condition"); per.Add(r); max = Math.Max(max, r.Count); }
            var l = new List<QuestCondition>();
            for (int i = 0; i < max; i++)
            {
                QuestCondition c = null;
                foreach (List<QuestNode> rows in per)
                {
                    if (i >= rows.Count) continue;
                    string t = Editor.Attr(rows[i], "type") ?? "", code = Editor.Attr(rows[i], "code") ?? "", v = Editor.Attr(rows[i], "value") ?? "";
                    if (c == null) c = new QuestCondition { Index = i, Type = t, Code = code, Value = v };
                    else if (t != c.Type || code != c.Code || Norm(v) != Norm(c.Value)) c.Differs = true;
                    c.Places++;
                }
                if (c.Places < per.Count) c.Differs = true;
                l.Add(c);
            }
            return l;
        }

        /// <summary>Sets the count (condition@value) of condition index in every place that has it.</summary>
        public int SetConditionCount(int index, long count)
        {
            if (count < 1) throw new ModKitException("the count must be 1 or more");
            int n = 0;
            foreach (QuestNode root in Roots(null))
            {
                List<QuestNode> rows = Editor.Kids(root, "condition");
                if (index < 0 || index >= rows.Count) continue;
                if (Editor.Attr(rows[index], "value") != N(count)) Editor.SetAttr(rows[index], "value", N(count));
                n++;
            }
            if (n == 0) throw new ModKitException("the quest has no condition " + (index + 1));
            return n;
        }

        // The table a condition's code is an id of, by the condition type alone (the full type name; a type that is
        // not listed takes free text: a trigger name, a word, an amount, however long the number is).
        static readonly string[][] CodeTables =
        {
            new[] { "npc", "npc", "talk_in", "dialogue", "skill_damage_npc" },
            new[] { "item", "item_add", "item_exist", "item_inven", "item_move", "item_pickup", "openitembox", "equip_exist", "install_item", "uninstall_item" },
            new[] { "map", "map", "stay_map", "survive_map" },
            new[] { "skill", "skill", "buff" },
            new[] { "quest", "quest", "quest_daily", "quest_clear" },
            new[] { "interact object", "interact_object", "interact_object_rep" },
            new[] { "dungeon", "dungeon_clear", "dungeon_rank", "dungeon_rank_clear" },
        };

        /// <summary>
        /// The table whose id the code of a condition of this type is ("npc", "item", "map", "skill", "quest",
        /// "interact object", "dungeon"), or null when the code is free. Such a code is not edited here: the editor
        /// has no list of that table to pick from.
        /// </summary>
        public static string CodeTable(string type)
        {
            foreach (string[] t in CodeTables)
                for (int i = 1; i < t.Length; i++)
                    if (string.Equals(type, t[i], StringComparison.OrdinalIgnoreCase)) return t[0];
            return null;
        }

        delegate void RowEdit(QuestNode row);

        int EachCondition(int index, RowEdit edit)
        {
            int n = 0;
            foreach (QuestNode root in Roots(null))
            {
                List<QuestNode> rows = Editor.Kids(root, "condition");
                if (index < 0 || index >= rows.Count) continue;
                edit(rows[index]); n++;
            }
            if (n == 0) throw new ModKitException("the quest has no condition " + (index + 1));
            return n;
        }

        QuestCondition Condition(int index)
        {
            List<QuestCondition> l = Conditions();
            if (index < 0 || index >= l.Count) throw new ModKitException("the quest has no condition " + (index + 1));
            return l[index];
        }

        /// <summary>
        /// Sets the type of condition index in every place that has it. The type must be one the shipped table uses
        /// (when the data set has the table), and the code must stay an id of the same table: npc to talk_in is
        /// taken, npc to item is not.
        /// </summary>
        public int SetConditionType(int index, string type)
        {
            type = (type ?? "").Trim();
            if (type.Length == 0) throw new ModKitException("the condition type is empty");
            QuestCondition c = Condition(index);
            List<string> known = Editor.ConditionTypes();
            if (known.Count > 0 && !known.Contains(type)) throw new ModKitException("no shipped quest has a condition of type \"" + type + "\"");
            string was = CodeTable(c.Type), now = CodeTable(type);
            if (was != now)
                throw new ModKitException("the code " + c.Code + " of this condition is " + (was != null ? "an id of the " + was + " table" : "free text") + "; type \"" + type + "\" takes " + (now != null ? "an id of the " + now + " table" : "free text"));
            return EachCondition(index, delegate(QuestNode row) { if (Editor.Attr(row, "type") != type) Editor.SetAttr(row, "type", type); });
        }

        /// <summary>
        /// Sets the code of condition index in every place that has it. A code that is an id of another table
        /// (CodeTable of the type) is refused.
        /// </summary>
        public int SetConditionCode(int index, string code)
        {
            code = (code ?? "").Trim();
            QuestCondition c = Condition(index);
            string table = CodeTable(c.Type);
            if (table != null) throw new ModKitException("the code of a \"" + c.Type + "\" condition is an id of the " + table + " table; it is not edited here");
            return EachCondition(index, delegate(QuestNode row) { if ((Editor.Attr(row, "code") ?? "") != code) Editor.SetAttr(row, "code", code); });
        }

        // ---- texts ----

        static void CheckText(string field)
        {
            if (Array.IndexOf(TextFields, field) < 0) throw new ModKitException("no quest text \"" + field + "\" (name, desc, manual, complete)");
        }

        public string Text(QuestPlace p, string field) { CheckText(field); return Editor.Attr(p.Root, field); }

        /// <summary>A text in the editor's language (else the first language that has a row).</summary>
        public string Text(string field)
        {
            CheckText(field);
            QuestPlace first = null;
            foreach (QuestPlace p in Places)
            {
                if (p.Kind != QuestPlace.Text) continue;
                if (first == null) first = p;
                if (string.Equals(p.Language, Editor.Language, StringComparison.OrdinalIgnoreCase)) { first = p; break; }
            }
            return first == null ? null : Editor.Attr(first.Root, field);
        }

        public string Name { get { return Text("name") ?? ""; } }

        /// <summary>The languages the quest has a string row in.</summary>
        public List<string> Languages()
        {
            var l = new List<string>();
            foreach (QuestPlace p in Places) if (p.Kind == QuestPlace.Text && !l.Contains(p.Language)) l.Add(p.Language);
            return l;
        }

        /// <summary>Sets a text in the string rows of one language (null: every language) on every target. Returns the number of rows.</summary>
        public int SetText(string language, string field, string value)
        {
            CheckText(field);
            if (value == null) throw new ArgumentNullException("value");
            int n = 0;
            foreach (QuestPlace p in Places)
            {
                if (p.Kind != QuestPlace.Text) continue;
                if (language != null && !string.Equals(p.Language, language, StringComparison.OrdinalIgnoreCase)) continue;
                if (Editor.Attr(p.Root, field) != value) Editor.SetAttr(p.Root, field, value);
                n++;
            }
            return n;
        }

        public int SetText(string field, string value) { return SetText(null, field, value); }
    }

    public sealed partial class QuestEditor
    {
        public const string IdSpace = "quest", TableEntry = "questdata/questdata.xml";
        public const string ClientXml = "client:Xml.m2d", GmsXml = "gms-client:Xml.m2d", ServerXml = "server:Xml.m2d";
        public const long MaxId = 99999999;

        public readonly DataSet Data;
        public readonly Mod Mod;
        /// <summary>The language the names of the list and the draft's Text come from first.</summary>
        public string Language = "en";
        /// <summary>The archive of the quest table (the kms-client's), or null.</summary>
        public readonly string TableXml;
        /// <summary>The archive whose quest files the mod names: the server's, else the gms-client's, or null.</summary>
        public readonly string FileXml;

        static readonly Regex FileRx = new Regex(@"^quest/(\d+)\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex StringRx = new Regex(@"^string/([^/]+)/questdescription[^/]*\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The server-only archive; its two quest script tables list quests by id.</summary>
        public const string ServerM2d = "server:Server.m2d";

        // A file that lists quests by id: rows of one element name under the root, the quest id in one attribute.
        sealed class ListSpec
        {
            internal readonly Regex Rx;
            internal readonly string Row, Key, What;
            internal ListSpec(string rx, string row, string key, string what)
            {
                Rx = new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); Row = row; Key = key; What = what;
            }
        }

        static readonly ListSpec[] Lists =
        {
            new ListSpec(@"^script/quest/questscript[^/]*\.xml$", "quest", "id", "talk script"),
            new ListSpec(@"^table/questtofieldtable\.xml$", "quest", "id", "fields of the quest"),
            new ListSpec(@"^string/[^/]+/specialquesttype\.xml$", "key", "id", "special type name"),
            new ListSpec(@"^table/server/questscriptcondition[^/]*\.xml$", "condition", "questID", "script condition"),
            new ListSpec(@"^table/server/questscriptfunction[^/]*\.xml$", "function", "questID", "script function"),
        };

        static ListSpec SpecOf(string entry)
        {
            entry = entry.Replace('\\', '/');
            foreach (ListSpec s in Lists) if (s.Rx.IsMatch(entry)) return s;
            return null;
        }

        sealed class ListDoc
        {
            internal string Name;
            internal ListSpec Spec;
            internal readonly Dictionary<long, List<XmlElem>> Rows = new Dictionary<long, List<XmlElem>>();
        }

        sealed class DocInfo { internal XmlDoc Doc; internal string Name; }
        sealed class StringDoc
        {
            internal string Name, Lang;
            internal XmlDoc Doc;
            internal readonly Dictionary<long, List<XmlElem>> Rows = new Dictionary<long, List<XmlElem>>();
        }

        public QuestEditor(DataSet data, Mod mod)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (mod == null) throw new ArgumentNullException("mod");
            Data = data; Mod = mod;
            if (data.Exists(ClientXml) && Doc(ClientXml, TableEntry) != null) TableXml = ClientXml;
            if (data.Exists(ServerXml) && FileIndex(ServerXml).Count > 0) FileXml = ServerXml;
            else if (data.Exists(GmsXml) && FileIndex(GmsXml).Count > 0) FileXml = GmsXml;
        }

        /// <summary>True when the data set has quest data at all.</summary>
        public bool HasData { get { return TableXml != null || FileXml != null; } }

        /// <summary>The archives the editor writes lines for.</summary>
        public List<string> Sides()
        {
            var l = new List<string>();
            if (TableXml != null) l.Add(TableXml);
            if (FileXml != null) l.Add(FileXml);
            return l;
        }

        // the archives with files that list quests: the editor's sides and the server-only archive
        List<string> ListArchives()
        {
            List<string> l = Sides();
            if (Data.Exists(ServerM2d)) l.Add(ServerM2d);
            return l;
        }

        // every file of an archive that lists quests by id, with its rows by quest id
        List<ListDoc> ListDocs(string archive)
        {
            string key = "questlists\n" + archive;
            object o;
            if (Data.Cache.TryGetValue(key, out o)) return (List<ListDoc>)o;
            var list = new List<ListDoc>();
            M2dArchive arch = Data.Open(archive);
            foreach (M2dEntry e in arch.Entries)
            {
                ListSpec spec = SpecOf(e.Name);
                if (spec == null) continue;
                XmlDoc doc = XmlDoc.Parse(arch.Read(e), archive + " " + e.Name);
                if (doc.Root == null) continue;
                var ld = new ListDoc { Name = e.Name, Spec = spec };
                foreach (XmlElem c in doc.Root.Children)
                {
                    long id;
                    if (c.Name != spec.Row || !TryId(c.Attr(spec.Key), out id)) continue;
                    List<XmlElem> l;
                    if (!ld.Rows.TryGetValue(id, out l)) ld.Rows[id] = l = new List<XmlElem>();
                    l.Add(c);
                }
                list.Add(ld);
            }
            list.Sort(delegate(ListDoc a, ListDoc b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            Data.Cache[key] = list;
            return list;
        }

        /// <summary>The condition types the shipped quest table uses, sorted; empty when the data set has no table.</summary>
        public List<string> ConditionTypes()
        {
            const string key = "questcondtypes";
            object o;
            if (Data.Cache.TryGetValue(key, out o)) return (List<string>)o;
            var l = new List<string>();
            foreach (List<XmlElem> rows in TableRows().Values)
                foreach (XmlElem row in rows)
                    foreach (XmlElem c in row.Children)
                    {
                        string t = c.Name == "condition" ? c.Attr("type") : null;
                        if (!string.IsNullOrEmpty(t) && !l.Contains(t)) l.Add(t);
                    }
            l.Sort(StringComparer.Ordinal);
            Data.Cache[key] = l;
            return l;
        }

        // the archives with quest files: the kms-client's copies and the per-id side
        List<string> FileArchives()
        {
            var l = new List<string>();
            if (TableXml != null && FileIndex(TableXml).Count > 0) l.Add(TableXml);
            if (FileXml != null) l.Add(FileXml);
            return l;
        }

        internal static string S(long id) { return id.ToString(CultureInfo.InvariantCulture); }

        internal static bool TryId(string s, out long id)
        {
            id = 0;
            return s != null && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out id);
        }

        /// <summary>A quest id as the file names write it: 8 digits.</summary>
        public static string Padded(long id) { return id.ToString("00000000", CultureInfo.InvariantCulture); }

        /// <summary>The per-id file of a new quest.</summary>
        public static string NewFileEntry(long id) { return "quest/" + Padded(id) + ".xml"; }

        // ---- the original data ----

        DocInfo Doc(string archive, string entry)
        {
            string key = "questdoc\n" + archive + "\n" + entry.ToLowerInvariant();
            object o;
            if (Data.Cache.TryGetValue(key, out o)) return (DocInfo)o;
            DocInfo info = null;
            if (Data.Exists(archive))
            {
                M2dArchive arch = Data.Open(archive);
                M2dEntry e = arch.Find(entry);
                if (e != null)
                {
                    XmlDoc doc = XmlDoc.Parse(arch.Read(e), archive + " " + e.Name);
                    if (doc.Root != null) info = new DocInfo { Doc = doc, Name = e.Name };
                }
            }
            Data.Cache[key] = info;
            return info;
        }

        Dictionary<long, string> FileIndex(string archive)
        {
            string key = "questfiles\n" + archive;
            object o;
            if (Data.Cache.TryGetValue(key, out o)) return (Dictionary<long, string>)o;
            var ix = new Dictionary<long, string>();
            foreach (M2dEntry e in Data.Open(archive).Entries)
            {
                Match m = FileRx.Match(e.Name.Replace('\\', '/'));
                long id;
                if (m.Success && TryId(m.Groups[1].Value, out id)) ix[id] = e.Name;
            }
            Data.Cache[key] = ix;
            return ix;
        }

        Dictionary<long, List<XmlElem>> TableRows()
        {
            const string key = "questtable";
            object o;
            if (Data.Cache.TryGetValue(key, out o)) return (Dictionary<long, List<XmlElem>>)o;
            var ix = new Dictionary<long, List<XmlElem>>();
            if (TableXml != null)
                foreach (XmlElem c in Doc(TableXml, TableEntry).Doc.Root.Children)
                {
                    long id;
                    if (c.Name != "quest" || !TryId(c.Attr("id"), out id)) continue;
                    List<XmlElem> l;
                    if (!ix.TryGetValue(id, out l)) ix[id] = l = new List<XmlElem>();
                    l.Add(c);
                }
            Data.Cache[key] = ix;
            return ix;
        }

        // every questdescription file of an archive, in every language, with its rows by quest id
        List<StringDoc> Strings(string archive)
        {
            string key = "queststrings\n" + archive;
            object o;
            if (Data.Cache.TryGetValue(key, out o)) return (List<StringDoc>)o;
            var list = new List<StringDoc>();
            M2dArchive arch = Data.Open(archive);
            foreach (M2dEntry e in arch.Entries)
            {
                Match m = StringRx.Match(e.Name.Replace('\\', '/'));
                if (!m.Success) continue;
                XmlDoc doc = XmlDoc.Parse(arch.Read(e), archive + " " + e.Name);
                if (doc.Root == null) continue;
                var sd = new StringDoc { Name = e.Name, Lang = m.Groups[1].Value, Doc = doc };
                foreach (XmlElem c in doc.Root.Children)
                {
                    long id;
                    if (c.Name != "quest" || !TryId(c.Attr("questID"), out id)) continue;
                    List<XmlElem> l;
                    if (!sd.Rows.TryGetValue(id, out l)) sd.Rows[id] = l = new List<XmlElem>();
                    l.Add(c);
                }
                list.Add(sd);
            }
            list.Sort(delegate(StringDoc a, StringDoc b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            Data.Cache[key] = list;
            return list;
        }

        /// <summary>The language folders of an archive that have a quest text file.</summary>
        public List<string> LanguagesIn(string archive)
        {
            var l = new List<string>();
            foreach (StringDoc d in Strings(archive)) if (!l.Contains(d.Lang)) l.Add(d.Lang);
            return l;
        }

        // ---- the mod's lines ----

        static bool SameEntry(string a, string b) { return string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase); }

        IEnumerable<EditOp> Edits(string archive, string entry)
        {
            foreach (ModOp op in Mod.Ops.ToArray())
            {
                var ed = op as EditOp;
                if (ed != null && ed.Archive == archive && SameEntry(ed.Entry, entry)) yield return ed;
            }
        }

        EditOp Edit(string archive, string entry)
        {
            foreach (EditOp ed in Edits(archive, entry)) return ed;
            var made = new EditOp { Archive = archive, Entry = entry };
            Mod.Ops.Add(made);
            return made;
        }

        void Drop(QuestPlace p, RowOp op)
        {
            foreach (EditOp ed in Edits(p.Archive, p.Entry))
                if (ed.Rows.Remove(op))
                {
                    if (ed.Rows.Count == 0) Mod.Ops.Remove(ed);
                    return;
                }
        }

        static XmlElem LiteralOf(RowOp r)
        {
            try
            {
                XmlDoc f = XmlDoc.ParseFragment(r.Xml.Trim(), "the new row");
                return f.Top.Count == 1 ? f.Root : null;
            }
            catch (ModKitException) { return null; }
        }

        static XmlElem Frag(QuestNode n)
        {
            XmlElem e = XmlDoc.ParseFragment(n.Add.Xml.Trim(), "the new row").Root;
            foreach (int i in n.Path) e = e.Children[i];
            return e;
        }

        RowOp FindOp(QuestNode n, RowOpKind kind, bool create)
        {
            string at = RowPath.Canonical(n.Orig);
            foreach (EditOp ed in Edits(n.Place.Archive, n.Place.Entry))
                foreach (RowOp r in ed.Rows)
                    if (r.Kind == kind && (r.At ?? "") == at) return r;
            if (!create) return null;
            // The files and the string rows of the targets are written each by its own line (the formats differ),
            // so a change says that it is for its side only.
            var op = new RowOp { Kind = kind, At = at, Sync = kind != RowOpKind.Change || n.Place.Kind == QuestPlace.Table };
            Edit(n.Place.Archive, n.Place.Entry).Rows.Add(op);
            return op;
        }

        static bool Plain(AttrOp a, string name) { return a.Kind != AttrOpKind.Text && string.IsNullOrEmpty(a.At) && a.Name == name; }

        internal string Attr(QuestNode n, string name)
        {
            if (n.Literal) return Frag(n).Attr(name);
            RowOp op = n.Add ?? FindOp(n, RowOpKind.Change, false);
            if (op != null)
                for (int i = op.Attrs.Count - 1; i >= 0; i--)
                    if (Plain(op.Attrs[i], name)) return op.Attrs[i].Kind == AttrOpKind.Set ? op.Attrs[i].Value : null;
            return n.Orig.Attr(name);
        }

        /// <summary>Sets an attribute of an element (null: takes it away).</summary>
        internal void SetAttr(QuestNode n, string name, string value)
        {
            if (n.Literal)
            {
                XmlElem e = Frag(n);
                var ed = new XmlEditor(e.Doc);
                if (value != null) ed.SetAttr(e, name, value);
                else if (e.FindAttr(name) != null) ed.RemoveAttr(e, name);
                else return;
                n.Add.Xml = ed.Apply();
                return;
            }
            RowOp op = n.Add ?? FindOp(n, RowOpKind.Change, true);
            op.Attrs.RemoveAll(delegate(AttrOp a) { return Plain(a, name); });
            string was = n.Orig.Attr(name);
            if (value != was)
                op.Attrs.Add(value != null ? new AttrOp { Kind = AttrOpKind.Set, Name = name, Value = value } : new AttrOp { Kind = AttrOpKind.Unset, Name = name });
            if (n.Add == null && op.Attrs.Count == 0) Drop(n.Place, op);
        }

        /// <summary>The child elements of a name (null: all), without the ones the mod deletes and with the ones it adds.</summary>
        internal List<QuestNode> Kids(QuestNode n, string name)
        {
            var l = new List<QuestNode>();
            if (n.Literal)
            {
                XmlElem e = Frag(n);
                for (int i = 0; i < e.Children.Count; i++)
                {
                    if (name != null && e.Children[i].Name != name) continue;
                    var path = new int[n.Path.Length + 1];
                    Array.Copy(n.Path, path, n.Path.Length);
                    path[n.Path.Length] = i;
                    l.Add(new QuestNode { Place = n.Place, Add = n.Add, Path = path });
                }
                return l;
            }
            if (n.Add != null) return l;
            foreach (XmlElem c in n.Orig.Children)
            {
                if (name != null && c.Name != name) continue;
                var k = new QuestNode { Place = n.Place, Orig = c };
                if (FindOp(k, RowOpKind.Delete, false) == null) l.Add(k);
            }
            string at = RowPath.Canonical(n.Orig);
            foreach (EditOp ed in Edits(n.Place.Archive, n.Place.Entry))
                foreach (RowOp r in ed.Rows)
                {
                    if (r.Kind != RowOpKind.Add || r.Xml == null || r.In != at) continue;
                    XmlElem lit = LiteralOf(r);
                    if (lit != null && (name == null || lit.Name == name)) l.Add(new QuestNode { Place = n.Place, Add = r });
                }
            return l;
        }

        /// <summary>True for an element the mod adds by a line of its own that has nothing but this one attribute.</summary>
        internal bool AddedOnlyFor(QuestNode n, string attr)
        {
            if (!n.Literal || n.Path.Length != 0) return false;
            XmlElem e = Frag(n);
            if (e.Children.Count > 0) return false;
            foreach (XmlAttr a in e.Attrs) if (a.Name != attr) return false;
            return true;
        }

        /// <summary>
        /// After a row inside an element the mod adds was removed: takes that element out too when it is left with
        /// no attribute and no child (it was added to hold the row).
        /// </summary>
        internal void RemoveIfEmptyAdd(QuestNode removed)
        {
            if (!removed.Literal || removed.Path.Length == 0) return;
            var path = new int[removed.Path.Length - 1];
            Array.Copy(removed.Path, path, path.Length);
            var parent = new QuestNode { Place = removed.Place, Add = removed.Add, Path = path };
            // the row of a new quest itself stays, whatever it holds
            if (path.Length == 0 && removed.Add.In == null) return;
            XmlElem e = Frag(parent);
            if (e.Children.Count == 0 && e.Attrs.Count == 0) Remove(parent);
        }

        /// <summary>Adds a child element (literal XML) as the last one.</summary>
        internal QuestNode AddKid(QuestNode n, string xml)
        {
            if (n.Literal)
            {
                XmlElem e = Frag(n);
                var ed = new XmlEditor(e.Doc);
                ed.Append(e, xml, "");
                n.Add.Xml = ed.Apply();
                var path = new int[n.Path.Length + 1];
                Array.Copy(n.Path, path, n.Path.Length);
                path[n.Path.Length] = e.Children.Count;
                return new QuestNode { Place = n.Place, Add = n.Add, Path = path };
            }
            if (n.Add != null) throw new ModKitException("a copied row takes no child elements");
            var add = new RowOp { Kind = RowOpKind.Add, In = RowPath.Canonical(n.Orig), Xml = xml };
            Edit(n.Place.Archive, n.Place.Entry).Rows.Add(add);
            return new QuestNode { Place = n.Place, Add = add };
        }

        /// <summary>Takes an element out: a shipped one is deleted by the mod, an added one leaves the mod.</summary>
        internal void Remove(QuestNode n)
        {
            if (n.Literal)
            {
                if (n.Path.Length == 0) { Drop(n.Place, n.Add); return; }
                XmlElem e = Frag(n);
                var ed = new XmlEditor(e.Doc);
                ed.Delete(e);
                n.Add.Xml = ed.Apply();
                return;
            }
            if (n.Add != null) { Drop(n.Place, n.Add); return; }
            RowOp ch = FindOp(n, RowOpKind.Change, false);
            if (ch != null) Drop(n.Place, ch);
            string at = RowPath.Canonical(n.Orig);
            foreach (EditOp ed in Edits(n.Place.Archive, n.Place.Entry))
                ed.Rows.RemoveAll(delegate(RowOp r) { return r.Kind == RowOpKind.Add && r.In == at; });
            FindOp(n, RowOpKind.Delete, true);
        }

        // ---- search ----

        // the literal <quest> rows the mod adds to the table
        List<RowOp> TableAdds(long id)
        {
            var l = new List<RowOp>();
            if (TableXml == null) return l;
            foreach (EditOp ed in Edits(TableXml, TableEntry))
                foreach (RowOp r in ed.Rows)
                {
                    if (r.Kind != RowOpKind.Add || r.Xml == null || r.In != null) continue;
                    XmlElem lit = LiteralOf(r);
                    long v;
                    if (lit != null && lit.Name == "quest" && TryId(lit.Attr("id"), out v) && (id < 0 || v == id)) l.Add(r);
                }
            return l;
        }

        EntryOp FileAdd(string archive, long id)
        {
            foreach (ModOp op in Mod.Ops)
            {
                var en = op as EntryOp;
                if (en != null && en.Archive == archive && !en.Delete && en.Copy != null && SameEntry(en.Name, NewFileEntry(id))) return en;
            }
            return null;
        }

        /// <summary>The quest ids the mod adds.</summary>
        public List<long> AddedIds()
        {
            var ids = new List<long>();
            foreach (RowOp r in TableAdds(-1))
            {
                long v;
                if (TryId(LiteralOf(r).Attr("id"), out v) && !ids.Contains(v)) ids.Add(v);
            }
            foreach (ModOp op in Mod.Ops)
            {
                var en = op as EntryOp;
                if (en == null || en.Delete || en.Copy == null || !FileArchives().Contains(en.Archive)) continue;
                Match m = FileRx.Match(en.Name.Replace('\\', '/'));
                long v;
                if (m.Success && TryId(m.Groups[1].Value, out v) && !ids.Contains(v)) ids.Add(v);
            }
            ids.Sort();
            return ids;
        }

        /// <summary>True when the shipped data has a quest of the id on any target.</summary>
        public bool Exists(long id)
        {
            if (TableRows().ContainsKey(id)) return true;
            foreach (string a in FileArchives()) if (FileIndex(a).ContainsKey(id)) return true;
            return false;
        }

        Dictionary<long, string> Names()
        {
            string key = "questnames\n" + Language;
            object o;
            if (Data.Cache.TryGetValue(key, out o)) return (Dictionary<long, string>)o;
            var names = new Dictionary<long, string>();
            foreach (bool any in new[] { false, true })
                foreach (string archive in Sides())
                    foreach (StringDoc d in Strings(archive))
                    {
                        if (!any && !string.Equals(d.Lang, Language, StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (KeyValuePair<long, List<XmlElem>> kv in d.Rows)
                            if (!names.ContainsKey(kv.Key)) names[kv.Key] = kv.Value[0].Attr("name") ?? "";
                    }
            Data.Cache[key] = names;
            return names;
        }

        /// <summary>
        /// The quests whose id or name contains text (empty: all), the mod's new ones first, then by id; at most max.
        /// </summary>
        public List<QuestHit> Find(string text, int max)
        {
            text = (text ?? "").Trim();
            Dictionary<long, string> names = Names();
            var hits = new List<QuestHit>();
            List<long> added = AddedIds();
            foreach (long id in added)
            {
                string name = "";
                try { name = Open(id).Name; } catch (ModKitException) { }
                hits.Add(new QuestHit { Id = id, Name = name, Added = true });
            }
            // a shipped quest whose string rows the mod changes is listed under the mod's name
            var renamed = new Dictionary<long, string>();
            foreach (string archive in Sides())
                foreach (StringDoc sd in Strings(archive))
                    foreach (EditOp ed in Edits(archive, sd.Name))
                        foreach (RowOp r in ed.Rows)
                        {
                            if (r.Kind != RowOpKind.Change || r.At == null) continue;
                            long id;
                            string err;
                            XmlElem row;
                            try { row = RowPath.Parse(r.At).TryResolve(sd.Doc.Root, out err); } catch (ModKitException) { continue; }
                            if (row == null || !TryId(row.Attr("questID"), out id) || renamed.ContainsKey(id)) continue;
                            try { renamed[id] = Open(id).Name; } catch (ModKitException) { }
                        }
            var ids = new List<long>(TableRows().Keys);
            foreach (string a in FileArchives()) foreach (long id in FileIndex(a).Keys) if (!TableRows().ContainsKey(id)) ids.Add(id);
            ids.Sort();
            long last = -1;
            foreach (long id in ids)
            {
                if (id == last || added.Contains(id)) continue;
                last = id;
                string name;
                if (!renamed.TryGetValue(id, out name)) names.TryGetValue(id, out name);
                hits.Add(new QuestHit { Id = id, Name = name ?? "" });
            }
            var l = new List<QuestHit>();
            foreach (QuestHit h in hits)
            {
                if (text.Length > 0 && S(h.Id).IndexOf(text, StringComparison.Ordinal) < 0 && h.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                l.Add(h);
                if (l.Count >= max) break;
            }
            return l;
        }

        // ---- open ----

        static string TargetName(string archive) { Profile p = Profile.OfArchive(archive); return p != null ? p.Name : DataSet.RootOf(archive); }

        static void QuestElems(XmlElem root, List<KeyValuePair<XmlElem, string>> into)
        {
            foreach (XmlElem c in root.Children)
            {
                if (c.Name == "quest") into.Add(new KeyValuePair<XmlElem, string>(c, ""));
                else if (c.Name == "environment")
                    foreach (XmlElem q in c.Children)
                        if (q.Name == "quest") into.Add(new KeyValuePair<XmlElem, string>(q, c.Attr("feature") ?? ""));
            }
        }

        /// <summary>The quest of an id, as the original data and the mod have it.</summary>
        public QuestDraft Open(long id)
        {
            var d = new QuestDraft(this, id);
            if (TableXml != null)
            {
                string name = Doc(TableXml, TableEntry).Name;
                List<XmlElem> rows;
                if (TableRows().TryGetValue(id, out rows))
                    foreach (XmlElem row in rows)
                    {
                        var p = new QuestPlace { Archive = TableXml, Entry = name, Kind = QuestPlace.Table, Feature = row.Attr("feature") ?? "" };
                        p.Root = new QuestNode { Place = p, Orig = row };
                        d.Places.Add(p);
                    }
                foreach (RowOp r in TableAdds(id))
                {
                    var p = new QuestPlace { Archive = TableXml, Entry = name, Kind = QuestPlace.Table, Feature = LiteralOf(r).Attr("feature") ?? "", IsNew = true };
                    p.Root = new QuestNode { Place = p, Add = r };
                    d.Places.Add(p);
                }
            }
            foreach (string archive in FileArchives())
            {
                string shipped, entry = null, from = null;
                bool isNew = false;
                if (FileIndex(archive).TryGetValue(id, out shipped)) entry = from = shipped;
                else
                {
                    EntryOp en = FileAdd(archive, id);
                    if (en != null) { entry = en.Name.Replace('\\', '/'); from = en.Copy; isNew = true; }
                }
                DocInfo doc = entry == null ? null : Doc(archive, from);
                if (doc == null) continue;
                var quests = new List<KeyValuePair<XmlElem, string>>();
                QuestElems(doc.Doc.Root, quests);
                foreach (KeyValuePair<XmlElem, string> q in quests)
                {
                    var p = new QuestPlace { Archive = archive, Entry = entry, Kind = QuestPlace.File, Feature = q.Value, IsNew = isNew };
                    p.Root = new QuestNode { Place = p, Orig = q.Key };
                    d.Places.Add(p);
                }
            }
            if (d.Places.Count == 0) throw new ModKitException("no quest " + id + " in the data or the mod");
            foreach (string archive in Sides())
                foreach (StringDoc sd in Strings(archive))
                {
                    List<XmlElem> rows;
                    if (sd.Rows.TryGetValue(id, out rows))
                        foreach (XmlElem row in rows)
                        {
                            var p = new QuestPlace { Archive = archive, Entry = sd.Name, Kind = QuestPlace.Text, Feature = row.Attr("feature") ?? "", Language = sd.Lang };
                            p.Root = new QuestNode { Place = p, Orig = row };
                            d.Places.Add(p);
                        }
                    foreach (EditOp ed in Edits(archive, sd.Name))
                        foreach (RowOp r in ed.Rows)
                        {
                            long v;
                            if (r.Kind != RowOpKind.Add || r.Copy == null || !TryId(KeyValue(r, "questID"), out v) || v != id) continue;
                            string err;
                            XmlElem src = RowPath.Parse(r.Copy).TryResolve(sd.Doc.Root, out err);
                            if (src == null) continue;
                            var p = new QuestPlace { Archive = archive, Entry = sd.Name, Kind = QuestPlace.Text, Feature = src.Attr("feature") ?? "", Language = sd.Lang, IsNew = true };
                            p.Root = new QuestNode { Place = p, Orig = src, Add = r };
                            d.Places.Add(p);
                        }
                }
            return d;
        }

        static string KeyValue(RowOp add, string key)
        {
            for (int i = add.Attrs.Count - 1; i >= 0; i--)
                if (add.Attrs[i].Kind == AttrOpKind.Set && Plain(add.Attrs[i], key)) return add.Attrs[i].Value;
            return null;
        }

        // ---- clone ----

        /// <summary>
        /// The other enabled mods (null: none known). The quest ids they reserve (&lt;ids space="quest"&gt;) are not
        /// offered by NextFreeId and not taken by Clone.
        /// </summary>
        public Func<IEnumerable<Mod>> Others;

        // the mod of the others that reserves the id, or null
        Mod ReservedBy(long id, List<Mod> others)
        {
            foreach (Mod m in others)
                foreach (IdRange r in m.Ids)
                    if (r.Space == IdSpace && id >= r.From && id <= r.To) return m;
            return null;
        }

        List<Mod> OtherMods()
        {
            var l = new List<Mod>();
            if (Others == null) return l;
            IEnumerable<Mod> mods = Others();
            if (mods != null) foreach (Mod m in mods) if (m != null && m != Mod && m.Id != Mod.Id) l.Add(m);
            return l;
        }

        /// <summary>The first id from the given one that no shipped quest and no quest of the mod has, and that no other enabled mod reserves.</summary>
        public long NextFreeId(long from)
        {
            List<long> added = AddedIds();
            List<Mod> others = OtherMods();
            for (long id = Math.Max(1, from); id <= MaxId; id++)
                if (!added.Contains(id) && !Exists(id) && ReservedBy(id, others) == null) return id;
            throw new ModKitException("no quest id is free from " + from);
        }

        static string PadLike(string was, long id)
        {
            string ids = S(id);
            was = was ?? "";
            return was.Length > ids.Length && was.StartsWith("0", StringComparison.Ordinal) ? ids.PadLeft(was.Length, '0') : ids;
        }

        /// <summary>
        /// A new quest that starts as a copy of a shipped one: its table row, its quest file on every target that has
        /// one (basic@questID set), and its string rows in every language, all under the new id. The id is reserved
        /// in the mod's &lt;ids space="quest"&gt;.
        /// </summary>
        public QuestDraft Clone(long sourceId, long newId) { return Clone(sourceId, newId, true); }

        /// <summary>
        /// Clone, and with listed also a copy of every row of the source in the files that list quests by id (its
        /// talk script, its fields, its special type name and the server's script conditions and functions).
        /// </summary>
        public QuestDraft Clone(long sourceId, long newId, bool listed)
        {
            if (newId <= 0 || newId > MaxId) throw new ModKitException("the new id must be from 1 to " + MaxId + " (the file name has 8 digits)");
            if (AddedIds().Contains(newId)) throw new ModKitException("the mod already adds a quest " + newId);
            if (Exists(newId)) throw new ModKitException("the id " + newId + " is already used in the data");
            Mod owner = ReservedBy(newId, OtherMods());
            if (owner != null) throw new ModKitException("the quest id " + newId + " is reserved by the enabled mod " + owner.Id);
            if (AddedIds().Contains(sourceId)) throw new ModKitException("quest " + sourceId + " is itself new in this mod; clone the shipped quest it was copied from");
            if (!Exists(sourceId)) throw new ModKitException("the data has no quest " + sourceId);
            List<XmlElem> rows;
            bool row = TableRows().TryGetValue(sourceId, out rows);
            if (TableXml != null && !row) throw new ModKitException("the " + TargetName(TableXml) + " table has no quest " + sourceId + " (" + TableEntry + ")");
            if (FileXml != null && !FileIndex(FileXml).ContainsKey(sourceId)) throw new ModKitException("the " + TargetName(FileXml) + " data has no file of quest " + sourceId);

            if (row)
            {
                string name = Doc(TableXml, TableEntry).Name;
                foreach (XmlElem r in rows)
                {
                    // the row at indent 0, so that the literal's own indent unit is the table's
                    XmlDoc f = XmlDoc.ParseFragment(new XmlEditor(r.Doc).Reindent(r.OuterText, r.Doc.IndentOf(r.Start), ""), "the quest row");
                    var ed = new XmlEditor(f);
                    ed.SetAttr(f.Root, "id", S(newId));
                    Edit(TableXml, name).Rows.Add(new RowOp { Kind = RowOpKind.Add, Xml = ed.Apply(), After = RowPath.Canonical(r) });
                }
            }
            var files = new List<string>();
            foreach (string archive in FileArchives())
            {
                string src;
                if (!FileIndex(archive).TryGetValue(sourceId, out src)) continue;
                Mod.Ops.Add(new EntryOp { Archive = archive, Name = NewFileEntry(newId), Copy = src });
                files.Add(archive);
            }
            foreach (string archive in Sides())
                foreach (StringDoc sd in Strings(archive))
                {
                    List<XmlElem> srows;
                    if (!sd.Rows.TryGetValue(sourceId, out srows)) continue;
                    foreach (XmlElem r in srows)
                    {
                        var add = new RowOp { Kind = RowOpKind.Add, Copy = RowPath.Canonical(r) };
                        add.Attrs.Add(new AttrOp { Kind = AttrOpKind.Set, Name = "questID", Value = PadLike(r.Attr("questID"), newId) });
                        Edit(archive, sd.Name).Rows.Add(add);
                    }
                }
            if (listed)
                foreach (string archive in ListArchives())
                    foreach (ListDoc ld in ListDocs(archive))
                    {
                        List<XmlElem> lrows;
                        if (!ld.Rows.TryGetValue(sourceId, out lrows)) continue;
                        foreach (XmlElem r in lrows)
                        {
                            var add = new RowOp { Kind = RowOpKind.Add, Copy = RowPath.Canonical(r) };
                            add.Attrs.Add(new AttrOp { Kind = AttrOpKind.Set, Name = ld.Spec.Key, Value = PadLike(r.Attr(ld.Spec.Key), newId) });
                            Edit(archive, ld.Name).Rows.Add(add);
                        }
                    }
            Reserve(newId, row, files);
            QuestDraft d = Open(newId);
            foreach (QuestPlace p in d.PlacesOf(QuestPlace.File))
                foreach (QuestNode b in Kids(p.Root, "basic"))
                    if (b.Orig.Attr("questID") != null) SetAttr(b, "questID", PadLike(b.Orig.Attr("questID"), newId));
            return d;
        }

        List<IdPlace> Places(bool row, List<string> files)
        {
            var l = new List<IdPlace>();
            if (row) l.Add(new IdPlace { Archive = TableXml, Entry = TableEntry, Rows = "quest", Key = "id" });
            foreach (string a in files) l.Add(new IdPlace { Archive = a, Entry = "quest/{id}.xml" });
            return l;
        }

        static bool SamePlaces(List<IdPlace> a, List<IdPlace> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Archive != b[i].Archive || !string.Equals(a[i].Entry, b[i].Entry, StringComparison.OrdinalIgnoreCase) || a[i].Rows != b[i].Rows) return false;
            return true;
        }

        void Reserve(long id, bool row, List<string> files)
        {
            foreach (IdRange r in Mod.Ids)
                if (r.Space == IdSpace && id >= r.From && id <= r.To) return;
            List<IdPlace> want = Places(row, files);
            foreach (IdRange r in Mod.Ids)
            {
                if (r.Space != IdSpace || !SamePlaces(r.Places, want)) continue;
                if (r.To + 1 == id) { r.To = id; return; }
                if (r.From - 1 == id) { r.From = id; return; }
            }
            var made = new IdRange { Space = IdSpace, From = id, To = id };
            made.Places.AddRange(want);
            Mod.Ids.Add(made);
        }

        /// <summary>
        /// Takes a new quest out of the mod again: its table row, its files with their edits, its string rows and
        /// its copies of the rows that list it (talk script, fields, special type name, script tables).
        /// </summary>
        public void RemoveClone(long id)
        {
            if (!AddedIds().Contains(id)) throw new ModKitException("the mod adds no quest " + id);
            if (TableXml != null)
                foreach (RowOp r in TableAdds(id))
                    foreach (EditOp ed in Edits(TableXml, TableEntry))
                        if (ed.Rows.Remove(r) && ed.Rows.Count == 0) Mod.Ops.Remove(ed);
            foreach (string archive in FileArchives())
            {
                EntryOp en = FileAdd(archive, id);
                if (en == null) continue;
                Mod.Ops.Remove(en);
                foreach (EditOp ed in Edits(archive, en.Name)) Mod.Ops.Remove(ed);
            }
            foreach (string archive in Sides())
                foreach (StringDoc sd in Strings(archive))
                    foreach (EditOp ed in Edits(archive, sd.Name))
                    {
                        ed.Rows.RemoveAll(delegate(RowOp r) { long v; return r.Kind == RowOpKind.Add && r.Copy != null && TryId(KeyValue(r, "questID"), out v) && v == id; });
                        if (ed.Rows.Count == 0) Mod.Ops.Remove(ed);
                    }
            ShareTalkTexts(id);
            foreach (KeyValuePair<EditOp, RowOp> kv in ListedOps(id))
            {
                kv.Key.Rows.Remove(kv.Value);
                if (kv.Key.Rows.Count == 0) Mod.Ops.Remove(kv.Key);
            }
            IdSpaces.Unreserve(Mod, IdSpace, id);
        }

        // the mod's copies of rows that list a quest of the id; read from the mod alone
        List<KeyValuePair<EditOp, RowOp>> ListedOps(long id)
        {
            var l = new List<KeyValuePair<EditOp, RowOp>>();
            foreach (ModOp op in Mod.Ops)
            {
                var ed = op as EditOp;
                ListSpec spec = ed == null ? null : SpecOf(ed.Entry);
                if (spec == null) continue;
                foreach (RowOp r in ed.Rows)
                {
                    long v;
                    if (r.Kind == RowOpKind.Add && r.Copy != null && TryId(KeyValue(r, spec.Key), out v) && v == id) l.Add(new KeyValuePair<EditOp, RowOp>(ed, r));
                }
            }
            return l;
        }

        /// <summary>
        /// The number of lines the mod holds on a shipped quest: changes, added and removed rows in its table rows,
        /// its files and its text rows. 0 for a quest the mod leaves alone and for a quest the mod adds. A clone
        /// copies the quest as shipped, so these lines are what a clone of the quest does not get.
        /// </summary>
        public int ModLines(long id)
        {
            int n = 0;
            foreach (QuestPlace p in Open(id).Places)
            {
                if (p.IsNew || p.Root.Orig == null) continue;
                string at = RowPath.Canonical(p.Root.Orig);
                foreach (EditOp ed in Edits(p.Archive, p.Entry))
                    foreach (RowOp r in ed.Rows)
                        if (Under(r.At, at) || (r.Kind == RowOpKind.Add && Under(r.In, at))) n++;
            }
            return n;
        }

        static bool Under(string path, string at)
        {
            if (path == null) return false;
            return at.Length == 0 || path == at || path.StartsWith(at + "/", StringComparison.Ordinal);
        }

        /// <summary>
        /// The rows the mod copies for a new quest in the files that list quests by id, one text line per file:
        /// archive, entry, what the row is and how many.
        /// </summary>
        public List<string> ListedRows(long id)
        {
            var l = new List<string>();
            EditOp last = null;
            int n = 0;
            foreach (KeyValuePair<EditOp, RowOp> kv in ListedOps(id))
            {
                if (kv.Key != last && last != null) { l.Add(ListedLine(last, id, n)); n = 0; }
                last = kv.Key; n++;
            }
            if (last != null) l.Add(ListedLine(last, id, n));
            return l;
        }

        static string ListedLine(EditOp ed, long id, int n)
        {
            ListSpec s = SpecOf(ed.Entry);
            return ed.Archive + "  " + ed.Entry.Replace('\\', '/') + "  <" + s.Row + " " + s.Key + "=\"" + id + "\"> " + (n == 1 ? "row" : n + " rows") + " (" + s.What + ")";
        }

        /// <summary>The places a new quest id is written to, per target, as text lines: for the plan view and the README.</summary>
        public List<string> Registration(long id)
        {
            var l = new List<string>();
            if (TableXml != null) l.Add(TableXml + "  " + TableEntry + "  <quest id=\"" + id + "\"> row");
            foreach (string a in FileArchives()) l.Add(a + "  " + NewFileEntry(id) + "  (basic questID=\"" + Padded(id) + "\")");
            foreach (string a in Sides())
            {
                List<string> langs = LanguagesIn(a);
                if (langs.Count > 0) l.Add(a + "  string/<" + string.Join(",", langs.ToArray()) + ">/questdescription*.xml  <quest questID=\"" + Padded(id) + "\"> row, where the source has one");
            }
            l.AddRange(ListedRows(id));
            int own = OwnTalkTextCount(id);
            if (own > 0)
                foreach (string a in Sides())
                {
                    List<string> langs = TalkLanguagesIn(a);
                    if (langs.Count > 0) l.Add(a + "  string/<" + string.Join(",", langs.ToArray()) + ">/scriptquest*.xml  <key id> rows of " + own + " talk text(s) of its own, where the source has one");
                }
            if (FileXml == ServerXml && Data.Exists(GmsXml))
                l.Add(GmsXml + "  the server's lines above, mirrored: the same files, rows and attributes");
            return l;
        }
    }
}
