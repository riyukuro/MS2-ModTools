// The editor of group entities (Groups.cs): one shop or one drop box at a time, for the plugin's "MS2 Shops and
// Drops" view and the command line. It shows a group as the mod leaves it, target by target, and turns edits into
// the row ops of the mod (it never stores a copy of the shipped rows).
//
// The view is built from the planner's own output: the mod's op of that group is planned alone, and the rows are
// read back from the entries the plan writes (or the shipped entries where it writes nothing). Each row is tied to
// the identity the ops use:
//   shops      sn (with feature and locale when variants share an sn)
//   drop boxes (group, item, n-th row of that pair), counted on the shipped box (or the box a clone copies, or the
//              <dropBox> of a new box). The planner keeps the order of the rows it keeps and puts new rows at the end
//              of their group, so the rows of a group are matched in order: the kept shipped rows first, then the
//              rows of the add ops.
// A row an add op made is edited inside that op's element; a shipped row through a <row> op.
using System;
using System.Collections.Generic;
using System.Globalization;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>One row of a group on one target, as the mod leaves it.</summary>
    public sealed class GroupRowView
    {
        /// <summary>Shops: sn, feature and locale of the row (the identity of the ops).</summary>
        public string Sn, Feature, Locale;
        /// <summary>Drop boxes: the group and item of the row's identity (shipped values, nested names); n counts from 1.</summary>
        public string Group, Item;
        public int N;
        /// <summary>The add op that made the row; null: a shipped row, a row of the cloned group or of a new box's &lt;dropBox&gt;.</summary>
        public GroupRowOp AddOp;
        /// <summary>The add op's element holds several rows (a whole group): the index of this row among its rows.</summary>
        public int AddIndex;
        /// <summary>The entry that holds the row (a flat target: the split).</summary>
        public string Entry;
        public int Line;
        /// <summary>The row's attributes after the mod, in the target's names.</summary>
        public readonly List<KeyValuePair<string, string>> Fields = new List<KeyValuePair<string, string>>();
        /// <summary>The row as shipped (or as the source has it); null for a row an add op made.</summary>
        public Dictionary<string, string> Before;

        public bool IsAdded { get { return AddOp != null; } }

        public string Get(string name)
        {
            foreach (KeyValuePair<string, string> kv in Fields) if (kv.Key == name) return kv.Value;
            return null;
        }

        public string GetBefore(string name)
        {
            string v;
            return Before != null && Before.TryGetValue(name, out v) ? v : null;
        }

        public bool IsChanged
        {
            get
            {
                if (Before == null) return false;
                if (Before.Count != Fields.Count) return true;
                foreach (KeyValuePair<string, string> kv in Fields) if (GetBefore(kv.Key) != kv.Value) return true;
                return false;
            }
        }

        public string Describe(GroupKind kind)
        {
            if (kind == GroupKind.Shop)
                return "sn " + (Sn ?? "?") + (Feature != null ? " feature \"" + Feature + "\"" : "") + (Locale != null ? " locale \"" + Locale + "\"" : "") + (IsAdded ? " (new)" : "");
            return "group " + Group + " item " + Item + (N > 1 ? " (" + GroupRowOp.Ord(N) + " row of that item)" : "") + (IsAdded ? " (new)" : "");
        }
    }

    /// <summary>A group on one target: header, group fields (nested drop boxes) and rows.</summary>
    public sealed class GroupSideView
    {
        public string Archive, Title;
        /// <summary>The entries the group is read from (shops: stock, header; nested boxes: the final file; flat: every split that holds a row).</summary>
        public readonly List<string> Entries = new List<string>();
        public bool Flat;
        /// <summary>Shops: the shop_game_info rows of the shop; nested boxes: the &lt;dropBox&gt; element. Empty on a flat target.</summary>
        public readonly List<List<KeyValuePair<string, string>>> Headers = new List<List<KeyValuePair<string, string>>>();
        /// <summary>Nested boxes: the attributes of each &lt;group&gt;, by dropGroupID, in file order.</summary>
        public readonly List<KeyValuePair<string, List<KeyValuePair<string, string>>>> Groups = new List<KeyValuePair<string, List<KeyValuePair<string, string>>>>();
        public readonly List<GroupRowView> Rows = new List<GroupRowView>();
        /// <summary>Shipped rows the mod removes here.</summary>
        public readonly List<GroupRowView> Removed = new List<GroupRowView>();
        /// <summary>False when the target has no such group after the mod.</summary>
        public bool Present;

        public string Target { get { return DataSet.RootOf(Archive); } }
    }

    public sealed class GroupView
    {
        public GroupKind Kind;
        public long Id;
        /// <summary>The mod's op of the group; null when the mod does not touch it.</summary>
        public GroupOp Op;
        public readonly List<GroupSideView> Sides = new List<GroupSideView>();
        /// <summary>The plan's errors, warnings and notes about the group, one line each with its target.</summary>
        public readonly List<string> Lines = new List<string>();

        public bool IsNew { get { return Op != null && (Op.New || Op.IsClone); } }
        public string Noun { get { return Kind == GroupKind.Shop ? "shop" : "drop box"; } }

        public GroupSideView Side(string archive)
        {
            foreach (GroupSideView s in Sides) if (s.Archive == archive) return s;
            return null;
        }
    }

    public sealed partial class GroupEditor
    {
        public readonly DataSet Data;
        public readonly Mod Mod;
        public FieldMaps Maps;
        readonly Dictionary<GroupKind, List<long>> shipped = new Dictionary<GroupKind, List<long>>();

        public GroupEditor(DataSet data, Mod mod)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (mod == null) throw new ArgumentNullException("mod");
            Data = data; Mod = mod;
        }

        public static string Space(GroupKind kind) { return kind == GroupKind.Shop ? IdSpaces.ShopSpace : IdSpaces.DropBoxSpace; }
        static string Inv(long v) { return v.ToString(CultureInfo.InvariantCulture); }

        // ---- the ids there are ----

        /// <summary>The shipped ids of a kind (shops: the server's shop_game_info; boxes: the server final, else the Korean client final, else the flat splits).</summary>
        public List<long> ShippedIds(GroupKind kind)
        {
            List<long> l;
            if (shipped.TryGetValue(kind, out l)) return l;
            var set = new SortedDictionary<long, bool>();
            if (kind == GroupKind.Shop)
            {
                Collect(Planner.ServerTables, Planner.ShopInfoEntry, "shop", "shopID", set);
                Collect(Planner.ServerTables, Planner.ShopStockEntry, "shop", "shopID", set);
            }
            else
            {
                Collect(Planner.ServerTables, Planner.ServerDropEntry, DropMap.BoxNested, "dropBoxID", set);
                Collect(Profile.KmsClient.ArchiveIn("x:Xml.m2d"), Planner.KmsDropEntry, DropMap.BoxNested, "dropBoxID", set);
                if (set.Count == 0)
                    foreach (Profile p in new[] { Profile.Server, Profile.GmsClient })
                    {
                        string a = p.ArchiveIn("x:Xml.m2d");
                        if (!Data.Has(p) || !Data.Exists(a)) continue;
                        foreach (string n in Planner.FlatSplits(Data.Open(a))) Collect(a, n, DropMap.RowFlat, "individualDropBoxID", set);
                    }
            }
            l = new List<long>(set.Keys);
            shipped[kind] = l;
            return l;
        }

        void Collect(string archive, string entry, string name, string key, SortedDictionary<long, bool> set)
        {
            XmlDoc d = Read(null, archive, entry);
            if (d == null || d.Root == null) return;
            foreach (XmlElem e in d.Root.Children)
            {
                long v;
                if (e.Name == name && long.TryParse(e.Attr(key) ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) set[v] = true;
            }
        }

        /// <summary>Ids whose text starts with the search text (empty: all); the mod's new and cloned groups first.</summary>
        public List<EntityHit> Find(GroupKind kind, string text, int max)
        {
            text = (text ?? "").Trim();
            var hits = new List<EntityHit>();
            var seen = new HashSet<long>();
            foreach (GroupOp g in Mod.Groups)
                if (g.Kind == kind && (g.New || g.IsClone) && Inv(g.Id).StartsWith(text, StringComparison.Ordinal) && seen.Add(g.Id))
                    hits.Add(new EntityHit { Id = g.Id, Added = true, Name = g.IsClone ? "copy of " + Inv(g.Clone) : "" });
            foreach (long id in ShippedIds(kind))
            {
                if (hits.Count >= max) break;
                if (!Inv(id).StartsWith(text, StringComparison.Ordinal) || !seen.Add(id)) continue;
                GroupOp g = Mod.FindGroup(kind, id);
                hits.Add(new EntityHit { Id = id, Name = g != null ? "changed" : "" });
            }
            return hits;
        }

        public bool Exists(GroupKind kind, long id)
        {
            GroupOp g = Mod.FindGroup(kind, id);
            if (g != null && (g.New || g.IsClone)) return true;
            return ShippedIds(kind).BinarySearch(id) >= 0;
        }

        // ---- the view ----

        static XmlDoc ReadBytes(byte[] b, string label) { return b == null ? null : XmlDoc.Parse(b, label); }

        /// <summary>An entry after the plan (what it writes), or as shipped; null when the archive or entry is not there.</summary>
        XmlDoc Read(Plan plan, string archive, string entry)
        {
            if (plan != null)
            {
                PlanArchive pa = plan.Find(archive);
                if (pa != null)
                {
                    foreach (string d in pa.Delete) if (string.Equals(d, entry, StringComparison.OrdinalIgnoreCase)) return null;
                    foreach (KeyValuePair<string, byte[]> kv in pa.Put)
                        if (string.Equals(kv.Key, entry, StringComparison.OrdinalIgnoreCase)) return ReadBytes(kv.Value, archive + " " + entry);
                }
            }
            if (!Data.Exists(archive)) return null;
            M2dArchive arch = Data.Open(archive);
            M2dEntry e = arch.Find(entry);
            return e == null ? null : ReadBytes(arch.Read(e), archive + " " + entry);
        }

        /// <summary>The plan of the mod's op of this group alone (null when the mod does not touch the group).</summary>
        public Plan PlanOf(GroupOp g)
        {
            if (g == null) return null;
            var m = new Mod { Id = Mod.Id, Name = Mod.Name, Version = Mod.Version };
            m.Groups.Add(g);
            foreach (IdRange r in Mod.Ids) m.Ids.Add(r);
            return Planner.Build(Data, new List<Mod> { m }, new PlanOptions { Maps = Maps, NoSync = true });
        }

        /// <summary>The group on every target that has a place for it, as the mod leaves it.</summary>
        public GroupView Open(GroupKind kind, long id)
        {
            GroupOp g = Mod.FindGroup(kind, id);
            if (g == null && !Exists(kind, id)) throw new ModKitException("there is no " + (kind == GroupKind.Shop ? "shop " : "drop box ") + Inv(id) + " in the data, and the mod does not add one");
            var v = new GroupView { Kind = kind, Id = id, Op = g };
            Plan plan = PlanOf(g);
            if (plan != null)
            {
                string tag = g + ": ";
                foreach (List<PlanMessage> list in new[] { plan.Errors, plan.Conflicts, plan.Warnings, plan.Notes })
                    foreach (PlanMessage m in list)
                    {
                        string where = m.Archive == null ? "" : DataSet.RootOf(m.Archive) + ": ";
                        string kindText = list == plan.Errors ? "error: " : list == plan.Conflicts ? m.ConflictTag : list == plan.Warnings ? "warning: " : "";
                        v.Lines.Add(where + kindText + (m.Text.StartsWith(tag, StringComparison.Ordinal) ? m.Text.Substring(tag.Length) : m.Text));
                    }
            }
            if (kind == GroupKind.Shop) OpenShop(v, plan);
            else OpenBox(v, plan);
            return v;
        }

        static List<KeyValuePair<string, string>> AttrList(XmlElem e)
        {
            var l = new List<KeyValuePair<string, string>>();
            foreach (XmlAttr a in e.Attrs) l.Add(new KeyValuePair<string, string>(a.Name, a.Value));
            return l;
        }

        static Dictionary<string, string> AttrMap(XmlElem e)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (XmlAttr a in e.Attrs) d[a.Name] = a.Value;
            return d;
        }

        static List<XmlElem> Kids(XmlElem root, string name, string key, string value)
        {
            var l = new List<XmlElem>();
            if (root == null) return l;
            foreach (XmlElem e in root.Children) if (e.Name == name && (key == null || e.Attr(key) == value)) l.Add(e);
            return l;
        }

        /// <summary>The add ops of the group, with the element text of each (a whole group for a group add).</summary>
        static List<GroupRowOp> Adds(GroupOp g, string group)
        {
            var l = new List<GroupRowOp>();
            if (g == null) return l;
            foreach (GroupRowOp r in g.Rows) if (r.Kind == GroupRowKind.Add && (group == null || r.Group == group)) l.Add(r);
            return l;
        }

        // ---- shops ----

        void OpenShop(GroupView v, Plan plan)
        {
            string a = Planner.ServerTables;
            if (!Data.Has(Profile.Server) || !Data.Exists(a)) return;
            string id = Inv(v.Id);
            XmlDoc stock = Read(plan, a, Planner.ShopStockEntry), info = Read(plan, a, Planner.ShopInfoEntry);
            XmlDoc stock0 = Read(null, a, Planner.ShopStockEntry);
            if (stock == null || info == null || stock0 == null) return;
            var side = new GroupSideView { Archive = a, Title = Profile.Server.Title };
            side.Entries.Add(Planner.ShopStockEntry); side.Entries.Add(Planner.ShopInfoEntry);
            v.Sides.Add(side);
            foreach (XmlElem h in Kids(info.Root, "shop", "shopID", id)) side.Headers.Add(AttrList(h));
            List<XmlElem> after = new List<XmlElem>();
            foreach (XmlElem s in Kids(stock.Root, "shop", "shopID", id)) foreach (XmlElem it in s.Children) if (it.Name == "item") after.Add(it);
            side.Present = side.Headers.Count > 0 || Kids(stock.Root, "shop", "shopID", id).Count > 0;

            // the rows the ops are keyed on: the shipped shop, the shop a clone copies, none for a new shop
            GroupOp g = v.Op;
            var before = new List<XmlElem>();
            if (g == null || !g.New)
                foreach (XmlElem s in Kids(stock0.Root, "shop", "shopID", g != null && g.IsClone ? Inv(g.Clone) : id))
                    foreach (XmlElem it in s.Children) if (it.Name == "item") before.Add(it);
            var snCount = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (XmlElem it in before) { string sn = it.Attr("sn") ?? ""; int c; snCount.TryGetValue(sn, out c); snCount[sn] = c + 1; }

            var used = new HashSet<XmlElem>();
            var matched = new Dictionary<XmlElem, XmlElem>();
            foreach (XmlElem it in after)
                foreach (XmlElem b in before)
                    if (!used.Contains(b) && b.Attr("sn") == it.Attr("sn") && (b.Attr("feature") ?? "") == (it.Attr("feature") ?? "") && (b.Attr("locale") ?? "") == (it.Attr("locale") ?? ""))
                    { used.Add(b); matched[it] = b; break; }
            List<GroupRowOp> adds = Adds(g, null);
            var addOf = new Dictionary<XmlElem, GroupRowOp>();
            var taken = new HashSet<GroupRowOp>();
            // an add with its own sn is found by it; the others take the remaining new rows in order
            foreach (GroupRowOp r in adds)
            {
                string sn = r.Sn ?? XmlDoc.ParseFragment(r.Xml, "the new stock row").Root.Attr("sn");
                if (sn == null) continue;
                foreach (XmlElem it in after)
                    if (!matched.ContainsKey(it) && !addOf.ContainsKey(it) && it.Attr("sn") == sn) { addOf[it] = r; taken.Add(r); break; }
            }
            int next = 0;
            foreach (XmlElem it in after)
            {
                if (matched.ContainsKey(it) || addOf.ContainsKey(it)) continue;
                while (next < adds.Count && taken.Contains(adds[next])) next++;
                if (next < adds.Count) { addOf[it] = adds[next]; taken.Add(adds[next]); next++; }
            }
            foreach (XmlElem it in after)
            {
                var row = new GroupRowView { Sn = it.Attr("sn"), Entry = Planner.ShopStockEntry, Line = it.Line };
                row.Fields.AddRange(AttrList(it));
                XmlElem b;
                if (matched.TryGetValue(it, out b))
                {
                    row.Before = AttrMap(b);
                    int c;
                    if (snCount.TryGetValue(row.Sn ?? "", out c) && c > 1) { row.Feature = b.Attr("feature") ?? ""; row.Locale = b.Attr("locale") ?? ""; }
                }
                else
                {
                    GroupRowOp r;
                    if (addOf.TryGetValue(it, out r)) row.AddOp = r;
                }
                side.Rows.Add(row);
            }
            foreach (XmlElem b in before)
                if (!used.Contains(b))
                {
                    var row = new GroupRowView { Sn = b.Attr("sn"), Entry = Planner.ShopStockEntry, Line = b.Line, Before = AttrMap(b) };
                    int c;
                    if (snCount.TryGetValue(row.Sn ?? "", out c) && c > 1) { row.Feature = b.Attr("feature") ?? ""; row.Locale = b.Attr("locale") ?? ""; }
                    row.Fields.AddRange(AttrList(b));
                    side.Removed.Add(row);
                }
        }

        // ---- drop boxes ----

        sealed class Src { internal string Group, Item; internal int N; internal XmlElem Row; internal Dictionary<string, string> Attrs; }

        /// <summary>The identity rows of a box in the nested model, per group in file order.</summary>
        static List<Src> NestedSrc(XmlElem box, List<KeyValuePair<string, List<KeyValuePair<string, string>>>> groups)
        {
            var l = new List<Src>();
            var count = new Dictionary<string, int>(StringComparer.Ordinal);
            if (box == null) return l;
            foreach (XmlElem grp in box.Children)
            {
                if (grp.Name != DropMap.GroupNested) continue;
                string gid = grp.Attr("dropGroupID") ?? "";
                if (groups != null) groups.Add(new KeyValuePair<string, List<KeyValuePair<string, string>>>(gid, AttrList(grp)));
                foreach (XmlElem r in grp.Children)
                {
                    if (r.Name != DropMap.RowNested) continue;
                    string item = r.Attr("itemID") ?? "";
                    string k = gid + "\n" + item;
                    int c; count.TryGetValue(k, out c); c++; count[k] = c;
                    l.Add(new Src { Group = gid, Item = item, N = c, Row = r, Attrs = AttrMap(r) });
                }
            }
            return l;
        }

        static List<Src> FlatSrc(List<KeyValuePair<string, XmlElem>> rows)
        {
            var l = new List<Src>();
            var count = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, XmlElem> kv in rows)
            {
                XmlElem r = kv.Value;
                string gid = r.Attr("dropGroup") ?? "", item = r.Attr("item") ?? "";
                string k = gid + "\n" + item;
                int c; count.TryGetValue(k, out c); c++; count[k] = c;
                l.Add(new Src { Group = gid, Item = item, N = c, Row = r, Attrs = AttrMap(r) });
            }
            return l;
        }

        /// <summary>The source rows the mod removes (a row remove, or a whole-group remove).</summary>
        static bool Removes(GroupOp g, Src s)
        {
            if (g == null) return false;
            foreach (GroupRowOp r in g.Rows)
                if (r.Kind == GroupRowKind.Remove && r.Group == s.Group && (r.Item == null || (r.Item == s.Item && r.N == s.N))) return true;
            return false;
        }

        /// <summary>The rows of an add op's element: one &lt;v&gt;, or the &lt;v&gt; rows of a whole &lt;group&gt;.</summary>
        static int AddRowCount(GroupRowOp r)
        {
            XmlElem e = XmlDoc.ParseFragment(r.Xml, "the new rows").Root;
            if (e.Name != DropMap.GroupNested) return 1;
            int n = 0;
            foreach (XmlElem c in e.Children) if (c.Name == DropMap.RowNested) n++;
            return n;
        }

        static string AddGroup(GroupRowOp r)
        {
            if (r.Group != null) return r.Group;
            XmlElem e = XmlDoc.ParseFragment(r.Xml, "the new rows").Root;
            return e.Attr("dropGroupID") ?? "";
        }

        void OpenBox(GroupView v, Plan plan)
        {
            GroupOp g = v.Op;
            string id = Inv(v.Id), srcId = g != null && g.IsClone ? Inv(g.Clone) : id;
            XmlElem model = null;
            if (g != null && g.New) model = XmlDoc.ParseFragment(g.Xml, "the new drop box").Root;

            // nested targets
            var nested = new List<KeyValuePair<Profile, string[]>>
            {
                new KeyValuePair<Profile, string[]>(Profile.Server, new[] { Planner.ServerTables, Planner.ServerDropEntry }),
                new KeyValuePair<Profile, string[]>(Profile.KmsClient, new[] { Profile.KmsClient.ArchiveIn("x:Xml.m2d"), Planner.KmsDropEntry }),
            };
            foreach (KeyValuePair<Profile, string[]> t in nested)
            {
                if (!Data.Has(t.Key)) continue;
                string a = t.Value[0], entry = t.Value[1];
                XmlDoc d = Read(plan, a, entry), d0 = Read(null, a, entry);
                if (d == null || d0 == null) continue;
                var side = new GroupSideView { Archive = a, Title = t.Key.Title + " (nested)" };
                side.Entries.Add(entry);
                v.Sides.Add(side);
                List<XmlElem> boxes = Kids(d.Root, DropMap.BoxNested, "dropBoxID", id);
                side.Present = boxes.Count > 0;
                XmlElem box = boxes.Count > 0 ? boxes[0] : null;
                if (box != null) side.Headers.Add(AttrList(box));
                List<XmlElem> srcBoxes = model != null ? new List<XmlElem> { model } : Kids(d0.Root, DropMap.BoxNested, "dropBoxID", srcId);
                List<Src> src = NestedSrc(srcBoxes.Count > 0 ? srcBoxes[0] : null, null);
                if (model != null) foreach (Src s in src) s.Attrs = null;   // a new box's rows are its own xml, not changes
                var after = new List<KeyValuePair<string, XmlElem>>();
                List<Src> afterRows = NestedSrc(box, side.Groups);
                foreach (Src s in afterRows) after.Add(new KeyValuePair<string, XmlElem>(s.Group, s.Row));
                Tie(side, g, src, after, entry, false);
            }

            // flat targets
            foreach (Profile p in new[] { Profile.Server, Profile.GmsClient })
            {
                string a = p.ArchiveIn("x:Xml.m2d");
                if (!Data.Has(p) || !Data.Exists(a)) continue;
                var side = new GroupSideView { Archive = a, Title = p.Title + " (flat)", Flat = true };
                var after = new List<KeyValuePair<string, XmlElem>>();
                var before = new List<KeyValuePair<string, XmlElem>>();
                foreach (string n in Planner.FlatSplits(Data.Open(a)))
                {
                    XmlDoc d = Read(plan, a, n), d0 = Read(null, a, n);
                    bool any = false;
                    if (d != null)
                        foreach (XmlElem r in Kids(d.Root, DropMap.RowFlat, "individualDropBoxID", id)) { after.Add(new KeyValuePair<string, XmlElem>(n, r)); any = true; }
                    if (d0 != null && model == null)
                        foreach (XmlElem r in Kids(d0.Root, DropMap.RowFlat, "individualDropBoxID", srcId)) before.Add(new KeyValuePair<string, XmlElem>(n, r));
                    if (any) side.Entries.Add(n);
                }
                side.Present = after.Count > 0;
                if (!side.Present && before.Count == 0 && g == null) continue;
                v.Sides.Add(side);
                List<Src> src;
                if (model != null)
                {
                    // a new box: its flat rows are the <v> rows of the model, group by group
                    src = NestedSrc(model, null);
                    foreach (Src s in src) s.Attrs = null;
                }
                else src = FlatSrc(before);
                Tie(side, g, src, after, null, true);
            }
        }

        /// <summary>
        /// Ties the rows of a box after the mod (in file order) to the source rows and the add ops. In each group the
        /// planner keeps the kept source rows in order and puts the added rows after them.
        /// after: (entry or group, row); for nested targets the key is the group id, for flat ones the split.
        /// </summary>
        static void Tie(GroupSideView side, GroupOp g, List<Src> src, List<KeyValuePair<string, XmlElem>> after, string nestedEntry, bool flat)
        {
            // the kept source rows and the added rows of each group, in order
            var kept = new Dictionary<string, Queue<Src>>(StringComparer.Ordinal);
            foreach (Src s in src)
            {
                if (Removes(g, s)) { side.Removed.Add(RemovedRow(s, flat, nestedEntry)); continue; }
                Queue<Src> q;
                if (!kept.TryGetValue(s.Group, out q)) { q = new Queue<Src>(); kept[s.Group] = q; }
                q.Enqueue(s);
            }
            var added = new Dictionary<string, Queue<KeyValuePair<GroupRowOp, int>>>(StringComparer.Ordinal);
            foreach (GroupRowOp r in Adds(g, null))
            {
                string gid = AddGroup(r);
                Queue<KeyValuePair<GroupRowOp, int>> q;
                if (!added.TryGetValue(gid, out q)) { q = new Queue<KeyValuePair<GroupRowOp, int>>(); added[gid] = q; }
                int n = AddRowCount(r);
                for (int i = 0; i < n; i++) q.Enqueue(new KeyValuePair<GroupRowOp, int>(r, i));
            }
            foreach (KeyValuePair<string, XmlElem> kv in after)
            {
                XmlElem e = kv.Value;
                string gid = flat ? (e.Attr("dropGroup") ?? "") : kv.Key;
                var row = new GroupRowView { Entry = flat ? kv.Key : nestedEntry, Line = e.Line, Group = gid };
                row.Fields.AddRange(AttrList(e));
                Queue<Src> q;
                Queue<KeyValuePair<GroupRowOp, int>> qa;
                if (kept.TryGetValue(gid, out q) && q.Count > 0)
                {
                    Src s = q.Dequeue();
                    row.Group = s.Group; row.Item = s.Item; row.N = s.N;
                    row.Before = s.Attrs;
                }
                else if (added.TryGetValue(gid, out qa) && qa.Count > 0)
                {
                    KeyValuePair<GroupRowOp, int> a = qa.Dequeue();
                    row.AddOp = a.Key; row.AddIndex = a.Value;
                    row.Item = e.Attr(flat ? "item" : "itemID");
                }
                else row.Item = e.Attr(flat ? "item" : "itemID");
                side.Rows.Add(row);
            }
        }

        // ---- edits (each one goes into the mod as an op; nothing else is stored) ----

        /// <summary>The mod's op of the group, made when missing (an edit of a shipped group).</summary>
        GroupOp Op(GroupKind kind, long id)
        {
            GroupOp g = Mod.FindGroup(kind, id);
            if (g != null) return g;
            if (!Exists(kind, id)) throw new ModKitException("there is no " + (kind == GroupKind.Shop ? "shop " : "drop box ") + Inv(id) + " to edit");
            g = new GroupOp { Kind = kind, Id = id };
            Mod.Groups.Add(g);
            return g;
        }

        /// <summary>An op that changes nothing any more leaves the mod (a shipped group only).</summary>
        void Tidy(GroupOp g)
        {
            for (int i = g.Rows.Count - 1; i >= 0; i--)
                if ((g.Rows[i].Kind == GroupRowKind.Set || g.Rows[i].Kind == GroupRowKind.GroupSet) && g.Rows[i].Attrs.Count == 0) g.Rows.RemoveAt(i);
            if (!g.New && !g.IsClone && g.Header.Count == 0 && g.Rows.Count == 0) Mod.Groups.Remove(g);
        }

        static void Put(List<AttrOp> ops, string name, string value, string shippedValue)
        {
            for (int i = ops.Count - 1; i >= 0; i--) if (ops[i].Name == name && ops[i].At == null) ops.RemoveAt(i);
            if (value == shippedValue) return;
            ops.Add(value == null ? new AttrOp { Kind = AttrOpKind.Unset, Name = name } : new AttrOp { Kind = AttrOpKind.Set, Name = name, Value = value });
        }

        /// <summary>A field of the header: a shop's shop_game_info row, a box's &lt;dropBox&gt;. value null: remove the attribute.</summary>
        public void SetHeader(GroupKind kind, long id, string name, string value)
        {
            GroupOp g = Op(kind, id);
            if (g.New && g.Xml != null)
            {
                g.Xml = EditElem(g.Xml, null, name, value);
                return;
            }
            Put(g.Header, name, value, ShippedHeader(g, name));
            Tidy(g);
        }

        string ShippedHeader(GroupOp g, string name)
        {
            string src = Inv(g.IsClone ? g.Clone : g.Id);
            XmlDoc d = g.Kind == GroupKind.Shop ? Read(null, Planner.ServerTables, Planner.ShopInfoEntry) : Read(null, Planner.ServerTables, Planner.ServerDropEntry);
            if (d == null) return null;
            List<XmlElem> l = g.Kind == GroupKind.Shop ? Kids(d.Root, "shop", "shopID", src) : Kids(d.Root, DropMap.BoxNested, "dropBoxID", src);
            if (l.Count == 0) return null;
            string v = l[0].Attr(name);
            foreach (XmlElem e in l) if (e.Attr(name) != v) return "\u0001";   // the variants differ: every value is a change
            return v;
        }

        /// <summary>Drop boxes: a field of a &lt;group&gt; (nested names).</summary>
        public void SetGroupField(long id, string group, string name, string value)
        {
            GroupOp g = Op(GroupKind.DropBox, id);
            foreach (GroupRowOp r in g.Rows)
                if (r.Kind == GroupRowKind.Add && r.Item == null && AddGroup(r) == group && r.Group == null)
                {
                    r.Xml = EditElem(r.Xml, null, name, value);
                    return;
                }
            GroupRowOp op = null;
            foreach (GroupRowOp r in g.Rows) if (r.Kind == GroupRowKind.GroupSet && r.Group == group) op = r;
            if (op == null) { op = new GroupRowOp { Kind = GroupRowKind.GroupSet, Group = group }; g.Rows.Add(op); }
            Put(op.Attrs, name, value, null);
            Tidy(g);
        }

        /// <summary>Sets an attribute of an element in mod text (one row of a whole-group add: index among its &lt;v&gt;).</summary>
        static string EditElem(string xml, int? index, string name, string value)
        {
            XmlDoc d = XmlDoc.ParseFragment(xml, "the mod's element");
            XmlElem e = d.Root;
            if (index.HasValue)
            {
                int i = 0; XmlElem hit = null;
                foreach (XmlElem c in e.Children) if (c.Name == DropMap.RowNested && i++ == index.Value) { hit = c; break; }
                if (hit == null) throw new ModKitException("the new group has no row " + (index.Value + 1));
                e = hit;
            }
            var ed = new XmlEditor(d);
            if (value == null) { if (e.FindAttr(name) != null) ed.RemoveAttr(e, name); }
            else ed.SetAttr(e, name, value);
            return ed.Apply();
        }

        /// <summary>
        /// A field of a row in the view. On a flat target the name is the flat one; it is mapped to the nested model
        /// the mod is written against (a field of the group or the box goes to the group or the header).
        /// value null: remove the attribute.
        /// </summary>
        public void SetRowField(GroupView v, GroupSideView side, GroupRowView row, string name, string value)
        {
            if (v.Kind == GroupKind.DropBox && side.Flat)
            {
                DropField f = DropMap.Default.ByFlat(name);
                if (f == null) throw new ModKitException("the flat field \"" + name + "\" has no counterpart in the nested model; edit it on a nested target or in the mod text");
                string nv = value == null ? null : f.ToNested(value);
                if (f.Level == 0) { SetHeader(v.Kind, v.Id, f.Nested, nv); return; }
                if (f.Level == 1) { SetGroupField(v.Id, row.Group, f.Nested, nv); return; }
                name = f.Nested; value = nv;
            }
            GroupOp g = Op(v.Kind, v.Id);
            if (row.AddOp != null)
            {
                XmlElem e = XmlDoc.ParseFragment(row.AddOp.Xml, "the new row").Root;
                row.AddOp.Xml = EditElem(row.AddOp.Xml, e.Name == DropMap.GroupNested ? (int?)row.AddIndex : null, name, value);
                return;
            }
            if (v.Kind == GroupKind.DropBox && g.New && g.Xml != null)
            {
                // a row of a new box's own <dropBox>: edit it there
                g.Xml = EditBoxRow(g.Xml, row, name, value);
                return;
            }
            GroupRowOp op = FindSet(g, row);
            if (op == null)
            {
                op = new GroupRowOp { Kind = GroupRowKind.Set };
                Key(v.Kind, row, op);
                g.Rows.Add(op);
            }
            string shippedValue = null;
            if (!side.Flat && row.Before != null) shippedValue = row.GetBefore(name);
            else if (row.Before != null)
            {
                DropField f = DropMap.Default.ByNested(2, name);
                string fv = f == null ? null : row.GetBefore(f.Flat);
                shippedValue = fv == null || f == null ? null : f.ToNested(fv);
            }
            Put(op.Attrs, name, value, shippedValue);
            Tidy(g);
        }

        /// <summary>Sets an attribute of a row of a new box's own &lt;dropBox&gt; (name null: deletes the row).</summary>
        static string EditBoxRow(string xml, GroupRowView row, string name, string value)
        {
            XmlDoc d = XmlDoc.ParseFragment(xml, "the new drop box");
            int n = 0;
            foreach (XmlElem grp in d.Root.Children)
            {
                if (grp.Name != DropMap.GroupNested || grp.Attr("dropGroupID") != row.Group) continue;
                foreach (XmlElem r in grp.Children)
                {
                    if (r.Name != DropMap.RowNested || r.Attr("itemID") != row.Item || ++n != row.N) continue;
                    var ed = new XmlEditor(d);
                    if (name == null) ed.Delete(r);
                    else if (value == null) { if (r.FindAttr(name) != null) ed.RemoveAttr(r, name); }
                    else ed.SetAttr(r, name, value);
                    return ed.Apply();
                }
            }
            throw new ModKitException("the new box has no row " + row.Describe(GroupKind.DropBox));
        }

        static void Key(GroupKind kind, GroupRowView row, GroupRowOp op)
        {
            if (kind == GroupKind.Shop) { op.Sn = row.Sn; op.Feature = row.Feature; op.Locale = row.Locale; }
            else { op.Group = row.Group; op.Item = row.Item; op.N = row.N < 1 ? 1 : row.N; }
        }

        static bool Same(GroupKind kind, GroupRowOp op, GroupRowView row)
        {
            if (kind == GroupKind.Shop) return op.Sn == row.Sn && op.Feature == row.Feature && op.Locale == row.Locale;
            return op.Group == row.Group && op.Item == row.Item && op.N == (row.N < 1 ? 1 : row.N);
        }

        static GroupRowOp FindSet(GroupOp g, GroupRowView row)
        {
            foreach (GroupRowOp r in g.Rows) if (r.Kind == GroupRowKind.Set && Same(g.Kind, r, row)) return r;
            return null;
        }

        /// <summary>
        /// Adds a row: a shop's &lt;item&gt; (sn null: the planner gives the next free one) or a drop box's &lt;v&gt; in a
        /// group. attrs: the row's attributes in nested names (drop boxes) or stock names (shops).
        /// </summary>
        public GroupRowOp AddRow(GroupKind kind, long id, string group, IList<KeyValuePair<string, string>> attrs)
        {
            GroupOp g = Op(kind, id);
            var sb = new System.Text.StringBuilder();
            sb.Append('<').Append(kind == GroupKind.Shop ? "item" : DropMap.RowNested);
            if (attrs != null)
                foreach (KeyValuePair<string, string> kv in attrs)
                    if (kv.Value != null) sb.Append(' ').Append(kv.Key).Append("=\"").Append(XmlDoc.EscapeAttr(kv.Value, '"')).Append('"');
            sb.Append(" />");
            var op = new GroupRowOp { Kind = GroupRowKind.Add, Xml = sb.ToString() };
            if (kind == GroupKind.DropBox)
            {
                if (string.IsNullOrEmpty(group)) throw new ModKitException("a new drop row needs its group (dropGroupID)");
                op.Group = group;
            }
            else if (attrs != null) foreach (KeyValuePair<string, string> kv in attrs) if (kv.Key == "sn") op.Sn = kv.Value;
            g.Rows.Add(op);
            return op;
        }

        /// <summary>Drop boxes: adds a whole new group with one row per attribute list.</summary>
        public GroupRowOp AddGroup(long id, string group, IList<KeyValuePair<string, string>> groupAttrs, IList<IList<KeyValuePair<string, string>>> rows)
        {
            GroupOp g = Op(GroupKind.DropBox, id);
            var sb = new System.Text.StringBuilder();
            sb.Append('<').Append(DropMap.GroupNested).Append(" dropGroupID=\"").Append(XmlDoc.EscapeAttr(group, '"')).Append('"');
            if (groupAttrs != null) foreach (KeyValuePair<string, string> kv in groupAttrs) if (kv.Key != "dropGroupID" && kv.Value != null) sb.Append(' ').Append(kv.Key).Append("=\"").Append(XmlDoc.EscapeAttr(kv.Value, '"')).Append('"');
            sb.Append('>');
            if (rows != null)
                foreach (IList<KeyValuePair<string, string>> r in rows)
                {
                    sb.Append('<').Append(DropMap.RowNested);
                    foreach (KeyValuePair<string, string> kv in r) if (kv.Value != null) sb.Append(' ').Append(kv.Key).Append("=\"").Append(XmlDoc.EscapeAttr(kv.Value, '"')).Append('"');
                    sb.Append(" />");
                }
            sb.Append("</").Append(DropMap.GroupNested).Append('>');
            var op = new GroupRowOp { Kind = GroupRowKind.Add, Xml = sb.ToString() };
            g.Rows.Add(op);
            return op;
        }

        /// <summary>Removes a row: an added row loses its add op; a shipped row gets a remove op (and its set op goes).</summary>
        public void RemoveRow(GroupView v, GroupRowView row)
        {
            GroupOp g = Op(v.Kind, v.Id);
            if (row.AddOp != null)
            {
                XmlElem e = XmlDoc.ParseFragment(row.AddOp.Xml, "the new row").Root;
                if (e.Name == DropMap.GroupNested)
                {
                    int count = 0;
                    foreach (XmlElem c in e.Children) if (c.Name == DropMap.RowNested) count++;
                    if (count > 1)
                    {
                        var d = XmlDoc.ParseFragment(row.AddOp.Xml, "the new group");
                        int i = 0;
                        foreach (XmlElem c in d.Root.Children)
                            if (c.Name == DropMap.RowNested && i++ == row.AddIndex) { var ed = new XmlEditor(d); ed.Delete(c); row.AddOp.Xml = ed.Apply(); return; }
                    }
                }
                g.Rows.Remove(row.AddOp);
                Tidy(g);
                return;
            }
            if (v.Kind == GroupKind.DropBox && g.New && g.Xml != null)
            {
                g.Xml = EditBoxRow(g.Xml, row, null, null);
                return;
            }
            for (int i = g.Rows.Count - 1; i >= 0; i--) if (g.Rows[i].Kind == GroupRowKind.Set && Same(v.Kind, g.Rows[i], row)) g.Rows.RemoveAt(i);
            var op = new GroupRowOp { Kind = GroupRowKind.Remove };
            Key(v.Kind, row, op);
            g.Rows.Add(op);
        }

        /// <summary>Takes back a remove op of a row (the row comes back as shipped).</summary>
        public void Restore(GroupView v, GroupRowView removed)
        {
            GroupOp g = Mod.FindGroup(v.Kind, v.Id);
            if (g == null) return;
            for (int i = g.Rows.Count - 1; i >= 0; i--) if (g.Rows[i].Kind == GroupRowKind.Remove && Same(v.Kind, g.Rows[i], removed)) g.Rows.RemoveAt(i);
            Tidy(g);
        }

        /// <summary>Drop boxes: removes a whole group.</summary>
        public void RemoveGroup(long id, string group)
        {
            GroupOp g = Op(GroupKind.DropBox, id);
            for (int i = g.Rows.Count - 1; i >= 0; i--)
                if (g.Rows[i].Group == group || (g.Rows[i].Kind == GroupRowKind.Add && g.Rows[i].Group == null && AddGroup(g.Rows[i]) == group)) g.Rows.RemoveAt(i);
            g.Rows.Add(new GroupRowOp { Kind = GroupRowKind.Remove, Group = group, Item = null });
        }

        /// <summary>A copy of a shipped group under a new id (0: the lowest free id of the reserved range).</summary>
        public long Clone(GroupKind kind, long source, long newId)
        {
            GroupOp own = Mod.FindGroup(kind, source);
            bool added = own != null && (own.New || own.IsClone);
            if (!added && ShippedIds(kind).BinarySearch(source) < 0) throw new ModKitException("there is no " + (kind == GroupKind.Shop ? "shop " : "drop box ") + Inv(source) + " to copy");
            newId = TakeId(kind, newId);
            // a group the mod adds is copied op for op (its source, xml and row ops); a shipped one becomes a clone op
            Mod.Groups.Add(added ? CopyOp(own, newId) : new GroupOp { Kind = kind, Id = newId, Clone = source });
            IdSpaces.Reserve(Mod, Space(kind), newId);
            return newId;
        }

        static GroupOp CopyOp(GroupOp g, long id)
        {
            var c = new GroupOp { Kind = g.Kind, Id = id, New = g.New, Clone = g.Clone, Split = g.Split, Xml = g.Xml, HeaderFeature = g.HeaderFeature, HeaderLocale = g.HeaderLocale };
            foreach (AttrOp a in g.Header) c.Header.Add(CopyAttr(a));
            foreach (GroupRowOp r in g.Rows)
            {
                var n = new GroupRowOp { Kind = r.Kind, Sn = r.Sn, Feature = r.Feature, Locale = r.Locale, Group = r.Group, Item = r.Item, N = r.N, Xml = r.Xml };
                foreach (AttrOp a in r.Attrs) n.Attrs.Add(CopyAttr(a));
                c.Rows.Add(n);
            }
            return c;
        }

        static AttrOp CopyAttr(AttrOp a) { return new AttrOp { Kind = a.Kind, Name = a.Name, Value = a.Value, At = a.At, Sync = a.Sync }; }

        /// <summary>A new group written from scratch: xml is the shop's header row or the box's &lt;dropBox&gt; (null: an empty one).</summary>
        public long New(GroupKind kind, long newId, string xml)
        {
            newId = TakeId(kind, newId);
            if (xml == null)
                xml = kind == GroupKind.Shop ? "<shop />" : "<" + DropMap.BoxNested + " />";
            Mod.Groups.Add(new GroupOp { Kind = kind, Id = newId, New = true, Xml = xml });
            IdSpaces.Reserve(Mod, Space(kind), newId);
            return newId;
        }

        long TakeId(GroupKind kind, long id)
        {
            if (id <= 0)
            {
                List<long> free = IdSpaces.NextFreeGroupIds(Data, new[] { Mod }, Space(kind), 1, null);
                if (free.Count == 0) throw new ModKitException("the reserved range for new " + (kind == GroupKind.Shop ? "shops" : "drop boxes") + " is full");
                return free[0];
            }
            if (Exists(kind, id)) throw new ModKitException("the id " + Inv(id) + " is taken");
            return id;
        }

        /// <summary>Takes the mod's op of a group out (a new or cloned group goes away, a shipped one is as shipped again).</summary>
        public void Discard(GroupKind kind, long id)
        {
            GroupOp g = Mod.FindGroup(kind, id);
            if (g == null) return;
            Mod.Groups.Remove(g);
            if (g.New || g.IsClone) IdSpaces.Unreserve(Mod, Space(kind), id);
        }

        static GroupRowView RemovedRow(Src s, bool flat, string entry)
        {
            var row = new GroupRowView { Group = s.Group, Item = s.Item, N = s.N, Line = s.Row.Line, Entry = entry, Before = s.Attrs };
            row.Fields.AddRange(AttrList(s.Row));
            return row;
        }
    }
}
