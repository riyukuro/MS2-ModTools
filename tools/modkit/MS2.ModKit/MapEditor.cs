// The entity edits of a map as the Toolbench Maps view makes them: the shipped map files (Resource/Exported.m2d) are
// read as they are, and every edit becomes a map op of the mod (MapOps.cs) with the shipped value as its expected old
// one, so that the planner makes it on every target that ships the same entity and warns where one does not.
//
//   Set      a property (one index of a list property) to a value: a <change> with <set old value>; the shipped value
//            again drops the set, a change without sets drops away.
//   Unset    the stored value: <unset old>, so the entity takes its model's default.
//   Remove   the entity: a <remove> in place of its change.
//   Restore  the entity: every op of it is dropped.
//
// New entities (AddSpawn, AddPortal): a <spawn> or <portal> op with the next free number of the mod range; SetNew
// edits its properties, its numbers (SpawnPointID; PortalID, TargetFieldSN, TargetPortalID) and the server rows of a
// spawn point (the item "(server row)", index 0, 1, ..., value "key=value key=value"); RemoveNew drops it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.MapEdit;

namespace MS2.ModKit
{
    /// <summary>One property item of an entity in the view: as shipped, as the mod has it, as the model has it.</summary>
    public sealed class MapPropRow
    {
        public string Name, Index;
        /// <summary>The shipped value; null when the map stores none.</summary>
        public string Shipped;
        /// <summary>The value with the mod's sets; null when the entity stores none then.</summary>
        public string Current;
        /// <summary>The model's default (scalar ones only); null when the model listing has none.</summary>
        public string Default;
        public string Type;
        /// <summary>The mod sets or unsets this item.</summary>
        public bool Edited;
    }

    public sealed class MapEditor
    {
        public readonly DataSet Data;
        /// <summary>The archive ref of the map files read ("client:Resource/Exported.m2d").</summary>
        public readonly string Exported;
        readonly M2dArchive arch;
        // the last map files read (a big one holds 15,000 entities): kept parsed, the oldest goes
        const int KeepMaps = 2;
        readonly List<KeyValuePair<string, List<XEntity>>> maps = new List<KeyValuePair<string, List<XEntity>>>();
        Dictionary<string, M2dEntry> flats;
        readonly Dictionary<string, FlatModel> models = new Dictionary<string, FlatModel>(StringComparer.Ordinal);
        // the listing models offered on a file without one (key: "prop|hint" or "mixin|hint"); the listing does not change
        readonly Dictionary<string, List<string>> listed = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        /// <summary>exported null: the first data root that has Resource/Exported.m2d (client, global client, server).</summary>
        public MapEditor(DataSet data, string exported)
        {
            Data = data;
            Exported = exported ?? Triggers.ExportedOf(data, null);
            if (Exported == null) throw new ModKitException("no data root has " + Planner.MapArchivePath);
            arch = data.Open(Exported);
        }

        /// <summary>The map files of a map as its row in the map table names them (with feature and locale).</summary>
        public static List<TriggerFolder> FilesOf(DataSet data, long mapId)
        {
            return Triggers.FoldersOf(data, null, mapId);
        }

        /// <summary>The name a mod keys a map file by: lower case, as the archive holds it.</summary>
        public static string Key(string xblock) { return xblock == null ? null : xblock.ToLowerInvariant(); }

        public bool Has(string xblock) { return arch.Find(Entry(xblock)) != null; }

        static string Entry(string xblock) { return "xblock/" + Key(xblock) + ".xblock"; }

        /// <summary>The shipped entities of a map file; null when the archive has none of that name.</summary>
        public List<XEntity> Shipped(string xblock)
        {
            for (int i = 0; i < maps.Count; i++)
                if (string.Equals(maps[i].Key, xblock, StringComparison.OrdinalIgnoreCase))
                {
                    KeyValuePair<string, List<XEntity>> kv = maps[i];
                    maps.RemoveAt(i);
                    maps.Add(kv);
                    return kv.Value;
                }
            M2dEntry e = arch.Find(Entry(xblock));
            List<XEntity> l = e == null ? null : XBlock.Read(arch.Read(e), Exported + " " + e.Name);
            maps.Add(new KeyValuePair<string, List<XEntity>>(xblock, l));
            if (maps.Count > KeepMaps) maps.RemoveAt(0);
            return l;
        }

        /// <summary>A shipped entity by id; null for none.</summary>
        public XEntity Find(string xblock, string id)
        {
            List<XEntity> l = Shipped(xblock);
            if (l != null) foreach (XEntity e in l) if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        /// <summary>A model of the listing (flat/**/&lt;model&gt;.flat) with the properties of its mixins; null for none.</summary>
        public FlatModel Model(string name)
        {
            if (name == null) return null;
            FlatModel m;
            if (models.TryGetValue(name, out m)) return m;
            models[name] = null;    // a mixin cycle ends here
            m = ReadModel(name);
            if (m != null)
            {
                var own = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (FlatProp p in m.Props) own[p.Name ?? ""] = true;
                foreach (FlatMixin x in m.Mixins)
                {
                    FlatModel b = Model(x.Name);
                    if (b == null) continue;
                    foreach (FlatProp p in b.Props) if (!own.ContainsKey(p.Name ?? "")) { own[p.Name ?? ""] = true; m.Props.Add(p); }
                }
            }
            models[name] = m;
            return m;
        }

        FlatModel ReadModel(string name)
        {
            ListFlats();
            M2dEntry f;
            if (!flats.TryGetValue(name, out f)) return null;
            FlatModel m = ParseFlat(f);
            return m != null && m.Name == name ? m : null;
        }

        FlatModel ParseFlat(M2dEntry f)
        {
            try { return FlatIndex.ParseFlat(arch.Read(f), f.Name); }
            catch (ModKitException) { return null; }
        }

        void ListFlats()
        {
            if (flats == null)
            {
                flats = new Dictionary<string, M2dEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (M2dEntry e in arch.Entries)
                {
                    string n = e.Name;
                    if (!n.StartsWith("flat/", StringComparison.OrdinalIgnoreCase) || !n.EndsWith(".flat", StringComparison.OrdinalIgnoreCase)) continue;
                    string file = n.Substring(n.LastIndexOf('/') + 1);
                    file = file.Substring(0, file.Length - 5);
                    if (!flats.ContainsKey(file)) flats[file] = e;
                }
            }
        }

        // ---- the mod's ops ----

        public static MapOp MapOf(Mod mod, string xblock)
        {
            foreach (MapOp m in mod.Maps) if (string.Equals(m.XBlock, xblock, StringComparison.OrdinalIgnoreCase)) return m;
            return null;
        }

        static MapEntityOp OpOf(MapOp m, string id, MapOpKind kind)
        {
            if (m != null)
                foreach (MapEntityOp op in m.Ops)
                    if (op.Kind == kind && string.Equals(op.Id, id, StringComparison.OrdinalIgnoreCase)) return op;
            return null;
        }

        /// <summary>Every change of an entity: the editor makes one, a hand-written mod may have more.</summary>
        static List<MapEntityOp> ChangesOf(MapOp m, string id)
        {
            var l = new List<MapEntityOp>();
            if (m != null)
                foreach (MapEntityOp op in m.Ops)
                    if (op.Kind == MapOpKind.Change && string.Equals(op.Id, id, StringComparison.OrdinalIgnoreCase)) l.Add(op);
            return l;
        }

        public static bool IsRemoved(Mod mod, string xblock, string id) { return OpOf(MapOf(mod, xblock), id, MapOpKind.Remove) != null; }

        public static bool IsChanged(Mod mod, string xblock, string id) { return OpOf(MapOf(mod, xblock), id, MapOpKind.Change) != null; }

        /// <summary>
        /// The shipped entities the mod edits on a map file, by lower-case id: Remove when it removes one, else Change.
        /// One pass over the ops, for a view that marks thousands of entities.
        /// </summary>
        public static Dictionary<string, MapOpKind> Edits(Mod mod, string xblock)
        {
            var d = new Dictionary<string, MapOpKind>(StringComparer.OrdinalIgnoreCase);
            MapOp m = mod == null ? null : MapOf(mod, xblock);
            if (m != null)
                foreach (MapEntityOp op in m.Ops)
                {
                    if (op.Id == null || (op.Kind != MapOpKind.Remove && op.Kind != MapOpKind.Change)) continue;
                    MapOpKind k;
                    if (op.Kind == MapOpKind.Remove || !d.TryGetValue(op.Id, out k)) d[op.Id] = op.Kind;
                }
            return d;
        }

        /// <summary>The ops the mod has on the map file that are not edits of a shipped entity (add, spawn, portal).</summary>
        public static int NewEntities(Mod mod, string xblock)
        {
            MapOp m = MapOf(mod, xblock);
            int n = 0;
            if (m != null) foreach (MapEntityOp op in m.Ops) if (op.Kind != MapOpKind.Remove && op.Kind != MapOpKind.Change) n++;
            return n;
        }

        static string Shipped(XEntity e, string name, string index)
        {
            XProp p = e.Find(name);
            if (p != null) foreach (XSet s in p.Items) if (s.Index == index) return s.Value;
            return null;
        }

        /// <summary>The entity's position as the map stores it; "" when it stores none.</summary>
        public static string Position(XEntity e)
        {
            XProp p = e.Find("Position");
            return p != null && p.Scalar ? p.Items[0].Value : "";
        }

        /// <summary>The property items of an entity: the shipped ones, the mod's new ones, then the model's others.</summary>
        public List<MapPropRow> Rows(Mod mod, string xblock, XEntity e)
        {
            var rows = new List<MapPropRow>();
            List<MapEntityOp> changes = ChangesOf(MapOf(mod, xblock), e.Id);
            FlatModel model = Model(e.Model);
            var named = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (XProp p in e.Props)
            {
                named[p.Name] = true;
                foreach (XSet s in p.Items) rows.Add(Row(model, p.Name, s.Index, s.Value));
            }
            foreach (MapEntityOp op in changes)
                foreach (MapSet s in op.Sets)
                {
                    MapPropRow r = rows.Find(delegate (MapPropRow x) { return x.Name == s.Name && x.Index == s.Index; });
                    if (r == null) { rows.Add(r = Row(model, s.Name, s.Index, null)); named[s.Name] = true; }
                    r.Current = s.Value;
                    r.Edited = true;
                }
            if (model != null)
                foreach (FlatProp p in model.Props)
                    if (p.Name != null && !named.ContainsKey(p.Name)) rows.Add(Row(model, p.Name, null, null));
            return rows;
        }

        static MapPropRow Row(FlatModel model, string name, string index, string shipped)
        {
            var r = new MapPropRow { Name = name, Index = index, Shipped = shipped, Current = shipped };
            FlatProp fp = model == null ? null : model.Prop(name);
            if (fp != null)
            {
                r.Type = fp.Type;
                if (index == null && fp.DefaultIsScalar) r.Default = fp.Default[0].Value;
            }
            return r;
        }

        /// <summary>The id of a shipped entity, refused when a mod could not keep it (mod.xml takes 32 hex digits).</summary>
        static string IdOf(XEntity e)
        {
            string id = e == null ? null : e.Id;
            bool ok = id != null && id.Length == 32;
            if (ok) foreach (char c in id) if (!Uri.IsHexDigit(c)) { ok = false; break; }
            if (!ok) throw new ModKitException("the entity " + (e == null ? "(none)" : (e.Name ?? "") + " has the id \"" + id + "\", not 32 hex digits") + "; a mod cannot name it");
            return id.ToLowerInvariant();
        }

        /// <summary>
        /// Sets one property item of a shipped entity (value null: unsets it). The shipped value again drops the set.
        /// Refused on an entity the mod removes. With the entity's model (Model(e.Model)) a property the model and the
        /// entity lack is refused, and an index where the model's type is a plain value (or none where it is a list);
        /// a set the mod already has is not checked again, so a hand-written or older one can still be taken out.
        /// </summary>
        public static void Set(Mod mod, string xblock, XEntity e, string name, string index, string value, FlatModel model = null)
        {
            if (string.IsNullOrEmpty(name)) throw new ModKitException("give the name of the property");
            if (name.Trim() != name) throw new ModKitException("the property name \"" + name + "\" has spaces at an end");
            if (index != null && index.Trim().Length == 0) throw new ModKitException("the index is empty; leave it out for a plain value");
            string id = IdOf(e);
            if (IsRemoved(mod, xblock, id)) throw new ModKitException("the mod removes entity " + id + "; restore it first");
            MapOp m = MapOf(mod, xblock);
            MapEntityOp op = null;
            MapSet set = null;
            foreach (MapEntityOp c in ChangesOf(m, id))
            {
                if (op == null) op = c;
                MapSet f = c.Sets.Find(delegate (MapSet s) { return s.Name == name && s.Index == index; });
                if (f != null) { op = c; set = f; break; }
            }
            // a set the mod already has is not checked again, so it can still be changed or taken out
            XProp shippedProp = e.Find(name);
            if (set == null && shippedProp != null && shippedProp.Items.Count > 0 && (index == null) != (shippedProp.Items[0].Index == null))
                throw new ModKitException("the property " + name + " is " + (index == null ? "a list; give an index" : "a plain value; leave the index out"));
            if (set == null && model != null && shippedProp == null)
            {
                FlatProp fp = model.Prop(name);
                if (fp == null) throw new ModKitException("the model " + model.Name + " has no property " + name);
                bool list = fp.Type != null && fp.Type.StartsWith("Assoc", StringComparison.Ordinal);
                if (list != (index != null))
                    throw new ModKitException("the property " + name + " is " + (list ? "a list (" + fp.Type + "); give an index" : "a plain value (" + fp.Type + "); leave the index out"));
            }
            string old = Shipped(e, name, index);
            if (value == old)
            {
                if (set != null) op.Sets.Remove(set);
                Tidy(mod, m, op);
                return;
            }
            if (m == null) mod.Maps.Add(m = new MapOp { XBlock = Key(xblock) });
            if (op == null) m.Ops.Add(op = new MapEntityOp { Kind = MapOpKind.Change, Id = id, Model = e.Model, Position = Position(e) });
            if (set == null) op.Sets.Add(set = new MapSet { Name = name, Index = index });
            set.Old = old;
            set.Value = value;
        }

        /// <summary>Removes a shipped entity: its changes give way to a remove.</summary>
        public static void Remove(Mod mod, string xblock, XEntity e)
        {
            string id = IdOf(e);
            if (IsRemoved(mod, xblock, id)) return;
            MapOp m = MapOf(mod, xblock);
            if (m == null) mod.Maps.Add(m = new MapOp { XBlock = Key(xblock) });
            m.Ops.RemoveAll(delegate (MapEntityOp op) { return op.Kind == MapOpKind.Change && string.Equals(op.Id, id, StringComparison.OrdinalIgnoreCase); });
            m.Ops.Add(new MapEntityOp { Kind = MapOpKind.Remove, Id = id, Model = e.Model, Position = Position(e) });
        }

        /// <summary>Drops every op of the mod on a shipped entity.</summary>
        public static void Restore(Mod mod, string xblock, string id)
        {
            MapOp m = MapOf(mod, xblock);
            if (m == null) return;
            m.Ops.RemoveAll(delegate (MapEntityOp op) { return (op.Kind == MapOpKind.Remove || op.Kind == MapOpKind.Change) && string.Equals(op.Id, id, StringComparison.OrdinalIgnoreCase); });
            Tidy(mod, m, null);
        }

        // ---- new entities: spawn points and portals ----

        /// <summary>The pseudo property of a new spawn point that holds its server rows (index 0, 1, ...).</summary>
        public const string ServerRow = "(server row)";

        static bool IsNew(MapEntityOp op) { return op.Kind == MapOpKind.Add || op.Kind == MapOpKind.Spawn || op.Kind == MapOpKind.Portal; }

        /// <summary>The ops of the mod that add an entity to the map file (add, spawn, portal), in mod order.</summary>
        public static List<MapEntityOp> NewOps(Mod mod, string xblock)
        {
            var l = new List<MapEntityOp>();
            MapOp m = mod == null ? null : MapOf(mod, xblock);
            if (m != null) foreach (MapEntityOp op in m.Ops) if (IsNew(op)) l.Add(op);
            return l;
        }

        public static MapEntityOp FindNew(Mod mod, string xblock, string name)
        {
            foreach (MapEntityOp op in NewOps(mod, xblock)) if (op.Name == name) return op;
            return null;
        }

        /// <summary>The position of a new entity ("" for none).</summary>
        public static string Position(MapEntityOp op)
        {
            XProp p = op.Props.Find(delegate (XProp x) { return x.Name == "Position"; });
            return p != null && p.Scalar ? p.Items[0].Value : "";
        }

        /// <summary>The models Add spawn point offers: those with SpawnPointID that are not an NPC or player spawn point.</summary>
        public List<string> SpawnModels(string xblock)
        {
            return ModelsWith(xblock, "SpawnPointID", "spawnpoint", IsNpcOrPcSpawn, "spawn");
        }

        bool IsNpcOrPcSpawn(string model) { return HasMixin(model, NpcSpawn.Mixin) || HasMixin(model, "SpawnPointPC"); }

        /// <summary>
        /// The models of the map file's shipped entities whose model has the property (most used first). When none has it and a
        /// hint is given: the models of the listing whose file name holds the hint and that have the property (at most 40).
        /// </summary>
        public List<string> ModelsWith(string xblock, string prop, string hint = null)
        {
            return ModelsWith(xblock, prop, hint, null, "");
        }

        /// <summary>As above, leaving out the models <paramref name="skip"/> is true for, on both paths and before the cap of 40
        /// (<paramref name="tag"/> keys the listing cache per skip).</summary>
        List<string> ModelsWith(string xblock, string prop, string hint, Predicate<string> skip, string tag)
        {
            var count = new Dictionary<string, int>(StringComparer.Ordinal);
            List<XEntity> l = Shipped(xblock);
            if (l != null)
                foreach (XEntity e in l)
                {
                    if (e.Model == null) continue;
                    int c;
                    if (count.TryGetValue(e.Model, out c)) { count[e.Model] = c + 1; continue; }
                    FlatModel fm = Model(e.Model);
                    if (fm != null && fm.Prop(prop) != null && (skip == null || !skip(e.Model))) count[e.Model] = 1;
                }
            var res = new List<string>(count.Keys);
            res.Sort(delegate (string a, string b) { int d = count[b].CompareTo(count[a]); return d != 0 ? d : string.CompareOrdinal(a, b); });
            if (res.Count > 0 || hint == null) return res;
            // no entity of the map file uses one: the models of the listing named like one (e.g. "portal") that have the property
            List<string> known;
            string key = "p:" + prop + "|" + hint + "|" + tag;
            if (listed.TryGetValue(key, out known)) return new List<string>(known);
            ListFlats();
            var names = new List<string>(flats.Keys);
            // names that start with the hint (Portal_...) before the others (Eff_portal_...)
            names.Sort(delegate (string a, string b)
            {
                bool sa = a.StartsWith(hint, StringComparison.OrdinalIgnoreCase), sb = b.StartsWith(hint, StringComparison.OrdinalIgnoreCase);
                return sa != sb ? (sa ? -1 : 1) : StringComparer.OrdinalIgnoreCase.Compare(a, b);
            });
            foreach (string n in names)
            {
                if (n.IndexOf(hint, StringComparison.OrdinalIgnoreCase) < 0) continue;
                FlatModel pm = ParseFlat(flats[n]);
                FlatModel fm = pm == null ? null : Model(pm.Name);     // with its mixins
                if (fm != null && fm.Prop(prop) != null && (skip == null || !skip(fm.Name))) res.Add(fm.Name);
                if (res.Count >= 40) break;
            }
            listed[key] = new List<string>(res);
            return res;
        }

        static long Scalar(XEntity e, string name)
        {
            XProp p = e.Find(name);
            long v;
            return p != null && p.Scalar && long.TryParse(p.Items[0].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v) ? v : -1;
        }

        /// <summary>The first number of the mod range (IdSpaces) that no shipped entity and no new one of the map file uses.</summary>
        public long NextNumber(Mod mod, string xblock, bool portal)
        {
            return NextNumber(mod, xblock, portal ? "PortalID" : "SpawnPointID");
        }

        /// <summary>
        /// The first number of the mod range of a per-map number (PortalID, SpawnPointID, TriggerObjectID) that no shipped
        /// entity and no new one of the map file uses: a spawn's or portal's own number, or the property of an added entity.
        /// </summary>
        public long NextNumber(Mod mod, string xblock, string prop)
        {
            long from, to;
            IdSpaces.MapNumberRange(prop, out from, out to);
            HashSet<long> used = UsedNumbers(mod, xblock, prop, null);
            for (long n = from; n <= to; n++) if (!used.Contains(n)) return n;
            throw new ModKitException("every " + prop + " of the mod range (" + from + "-" + to + ") is used in " + xblock);
        }

        /// <summary>
        /// The numbers (PortalID, SpawnPointID, TriggerObjectID) the map file has or the mod gives: shipped entities, the
        /// mod's new entities (a spawn's or portal's own number, the property of an &lt;add&gt;, hand-written ones too) and
        /// the sets of its &lt;change&gt; ops; the op <paramref name="except"/> is left out.
        /// </summary>
        HashSet<long> UsedNumbers(Mod mod, string xblock, string prop, MapEntityOp except)
        {
            // the game keys the numbers per object kind: for one entity's number only the same kind counts (as at Plan)
            string kind = except == null ? null : ObjectKind(except.Model);
            Predicate<string> other = delegate (string model)
            {
                string k = kind == null ? null : ObjectKind(model);
                return k != null && k != kind;
            };
            var used = new HashSet<long>();
            List<XEntity> l = Shipped(xblock);
            MapOp m = mod == null ? null : MapOf(mod, xblock);
            // a shipped entity the mod removes, or whose number a <change> sets or unsets, frees its shipped number (as at Plan)
            var freed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (m != null)
                foreach (MapEntityOp op in m.Ops)
                {
                    if (op.Id == null) continue;
                    if (op.Kind == MapOpKind.Remove) freed.Add(op.Id);
                    else if (op.Kind == MapOpKind.Change)
                        foreach (MapSet set in op.Sets) if (set.Name == prop && set.Index == null) freed.Add(op.Id);
                }
            if (l != null)
                foreach (XEntity e in l)
                {
                    if (e.Id != null && freed.Contains(e.Id)) continue;
                    long v = Scalar(e, prop);
                    if (v >= 0 && !other(e.Model)) used.Add(v);
                }
            if (m == null) return used;
            Dictionary<string, XEntity> byId = null;
            foreach (MapEntityOp op in m.Ops)
            {
                if (op == except) continue;
                string model = op.Model;
                if (!IsNew(op) && kind != null)
                {
                    // a <change> or <remove> of a shipped entity: the shipped one's model
                    if (byId == null)
                    {
                        byId = new Dictionary<string, XEntity>(StringComparer.OrdinalIgnoreCase);
                        if (l != null) foreach (XEntity e in l) if (e.Id != null) byId[e.Id] = e;
                    }
                    XEntity se;
                    if (op.Id != null && byId.TryGetValue(op.Id, out se)) model = se.Model;
                }
                if (other(model)) continue;
                long v;
                if (IsNew(op))
                {
                    if (Array.IndexOf(Own(op), prop) >= 0) used.Add(OwnValue(op, prop));
                    XProp p = op.Props.Find(delegate (XProp x) { return x.Name == prop; });
                    if (p != null && p.Scalar && long.TryParse(p.Items[0].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v)) used.Add(v);
                }
                else if (op.Kind == MapOpKind.Change)
                    foreach (MapSet set in op.Sets)
                        if (set.Name == prop && set.Index == null && long.TryParse(set.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v)) used.Add(v);
            }
            return used;
        }

        void RefuseUsed(Mod mod, string xblock, MapEntityOp op, string prop, long n)
        {
            if (UsedNumbers(mod, xblock, prop, op).Contains(n))
                throw new ModKitException(prop + " " + n.ToString(CultureInfo.InvariantCulture) + " is already used in " + xblock + " (by a shipped entity or another edit of the mod); pick a free one");
        }

        // ---- the other kinds of new entities: plain <add> ops ----

        /// <summary>A kind of entity the Maps view adds as a plain &lt;add&gt; op: its models have the mixin.</summary>
        public sealed class AddKind
        {
            /// <summary>What the user reads ("NPC spawn point").</summary>
            public string Label;
            /// <summary>Every model of the kind has this model among its mixins (or is it).</summary>
            public string Mixin;
            /// <summary>The per-map number the tool gives a new one (null for none).</summary>
            public string Number;
            /// <summary>Listing names that hold this are offered when no entity of the map file has a model of the kind.</summary>
            public string Hint;
        }

        /// <summary>An NPC spawn point: SpawnPointID from the spawn range, NpcList [npc id] = count and NpcCount.</summary>
        public static readonly AddKind NpcSpawn = new AddKind { Label = "NPC spawn point", Mixin = "SpawnPointNPC", Number = "SpawnPointID", Hint = "SpawnPointNPC" };
        /// <summary>An interact object (mesh or actor) with the interactID of its interact object row; the id is not unique.</summary>
        public static readonly AddKind Interact = new AddKind { Label = "interact object", Mixin = "MS2InteractObject", Hint = "Interact" };
        /// <summary>A trigger box: TriggerObjectID from the trigger object range, ShapeDimensions.</summary>
        public static readonly AddKind TriggerBox = new AddKind { Label = "trigger box", Mixin = "MS2TriggerBox", Number = "TriggerObjectID", Hint = "TriggerBox" };
        public static readonly AddKind[] AddKinds = { NpcSpawn, Interact, TriggerBox };

        /// <summary>Whether the model is the mixin or has it among its mixins, at any depth.</summary>
        public bool HasMixin(string model, string mixin)
        {
            if (model == null || mixin == null) return false;
            bool r;
            string key = model + "|" + mixin.ToLowerInvariant();
            if (!mixins.TryGetValue(key, out r)) mixins[key] = r = HasMixin(model, mixin, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return r;
        }

        readonly Dictionary<string, bool> mixins = new Dictionary<string, bool>(StringComparer.Ordinal);
        readonly Dictionary<string, string> kinds = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The object kind of a model (TriggerXBlocks.KindOf: npc_spawn, box, mesh, ...); null for none.</summary>
        public string ObjectKind(string model)
        {
            if (model == null) return null;
            string k;
            if (!kinds.TryGetValue(model, out k))
                kinds[model] = k = TriggerXBlocks.KindOf(delegate (string n) { return ReadModel(n) ?? ReadModelAnyCase(n); }, model);
            return k;
        }

        bool HasMixin(string model, string mixin, HashSet<string> seen)
        {
            if (model == null || !seen.Add(model)) return false;
            if (string.Equals(model, mixin, StringComparison.OrdinalIgnoreCase)) return true;
            FlatModel m = ReadModel(model) ?? ReadModelAnyCase(model);
            if (m == null) return false;
            foreach (FlatMixin x in m.Mixins) if (HasMixin(x.Name, mixin, seen)) return true;
            return false;
        }

        FlatModel ReadModelAnyCase(string name)
        {
            ListFlats();
            M2dEntry f;
            return flats.TryGetValue(name, out f) ? ParseFlat(f) : null;
        }

        /// <summary>What a new entity is, with its number: "spawn point 140000", "NPC spawn point 140001", "interact object", "entity".</summary>
        public string NewLabel(MapEntityOp op)
        {
            if (op.Kind == MapOpKind.Spawn) return "spawn point " + D(op.Number);
            if (op.Kind == MapOpKind.Portal) return "portal " + D(op.Number);
            AddKind k = KindOf(op);
            if (k == null) return "entity";
            XProp p = k.Number == null ? null : op.Props.Find(delegate (XProp x) { return x.Name == k.Number; });
            return k.Label + (p != null && p.Scalar ? " " + p.Items[0].Value : "");
        }

        /// <summary>The kind a new entity's model is of (null: none of AddKinds).</summary>
        public AddKind KindOf(MapEntityOp op)
        {
            if (op == null || op.Kind != MapOpKind.Add) return null;
            foreach (AddKind k in AddKinds) if (HasMixin(op.Model, k.Mixin)) return k;
            return null;
        }

        /// <summary>
        /// The models of the kind used by the map file's shipped entities (most used first); when none: the models of the
        /// listing whose file name holds the kind's hint (names that start with it first, at most 40).
        /// </summary>
        public List<string> ModelsOf(string xblock, AddKind kind)
        {
            var count = new Dictionary<string, int>(StringComparer.Ordinal);
            var not = new HashSet<string>(StringComparer.Ordinal);
            List<XEntity> l = Shipped(xblock);
            if (l != null)
                foreach (XEntity e in l)
                {
                    if (e.Model == null || not.Contains(e.Model)) continue;
                    int c;
                    if (count.TryGetValue(e.Model, out c)) { count[e.Model] = c + 1; continue; }
                    if (Model(e.Model) != null && HasMixin(e.Model, kind.Mixin)) count[e.Model] = 1; else not.Add(e.Model);
                }
            var res = new List<string>(count.Keys);
            res.Sort(delegate (string a, string b) { int d = count[b].CompareTo(count[a]); return d != 0 ? d : string.CompareOrdinal(a, b); });
            if (res.Count > 0) return res;
            List<string> known;
            string key = "m:" + kind.Mixin + "|" + kind.Hint;
            if (listed.TryGetValue(key, out known)) return new List<string>(known);
            ListFlats();
            var names = new List<string>(flats.Keys);
            names.Sort(delegate (string a, string b)
            {
                bool sa = a.StartsWith(kind.Hint, StringComparison.OrdinalIgnoreCase), sb = b.StartsWith(kind.Hint, StringComparison.OrdinalIgnoreCase);
                return sa != sb ? (sa ? -1 : 1) : StringComparer.OrdinalIgnoreCase.Compare(a, b);
            });
            foreach (string n in names)
            {
                if (n.IndexOf(kind.Hint, StringComparison.OrdinalIgnoreCase) < 0) continue;
                FlatModel pm = ParseFlat(flats[n]);
                if (pm == null || Model(pm.Name) == null || !HasMixin(pm.Name, kind.Mixin)) continue;
                res.Add(pm.Name);
                if (res.Count >= 40) break;
            }
            listed[key] = new List<string>(res);
            return res;
        }

        /// <summary>A new NPC spawn point: an &lt;add&gt; with the next free SpawnPointID, NpcList [npc id] = count and NpcCount = count.</summary>
        public MapEntityOp AddNpcSpawn(Mod mod, long mapId, string xblock, string name, string model, string position, long npcId, long count)
        {
            long id = Number("the NPC id", npcId), c = Number("NpcCount", count);
            if (id == 0) throw new ModKitException("give the id of the NPC that spawns there (a row of the NPC table)");
            if (c == 0) throw new ModKitException("give how many of the NPC spawn there (1 or more)");
            MapEntityOp op = NewOfKind(mod, mapId, xblock, NpcSpawn, name, model, position);
            op.Props.Add(new XProp("NpcList", new List<XSet> { new XSet(D(id), D(c)) }));
            op.Props.Add(new XProp("NpcCount", D(c)));
            MapOf(mod, xblock).Ops.Add(op);
            return op;
        }

        /// <summary>A new interact object: an &lt;add&gt; with the interactID of a row of the interact object table.</summary>
        public MapEntityOp AddInteract(Mod mod, long mapId, string xblock, string name, string model, string position, long interactId)
        {
            long id = Number("interactID", interactId);
            if (id == 0) throw new ModKitException("give the interactID of the object (a row of the interact object table)");
            MapEntityOp op = NewOfKind(mod, mapId, xblock, Interact, name, model, position);
            op.Props.Add(new XProp("interactID", D(id)));
            MapOf(mod, xblock).Ops.Add(op);
            return op;
        }

        /// <summary>A new trigger box: an &lt;add&gt; with the next free TriggerObjectID; ShapeDimensions "x, y, z" (null or "": the model's).</summary>
        public MapEntityOp AddTriggerBox(Mod mod, long mapId, string xblock, string name, string model, string position, string dimensions)
        {
            string dims = string.IsNullOrEmpty(dimensions) ? null : Dimensions(dimensions);
            MapEntityOp op = NewOfKind(mod, mapId, xblock, TriggerBox, name, model, position);
            if (dims != null) op.Props.Add(new XProp("ShapeDimensions", dims));
            MapOf(mod, xblock).Ops.Add(op);
            return op;
        }

        MapEntityOp NewOfKind(Mod mod, long mapId, string xblock, AddKind kind, string name, string model, string position)
        {
            long n = kind.Number == null ? -1 : NextNumber(mod, xblock, kind.Number);
            MapEntityOp op = NewOp(mod, mapId, xblock, MapOpKind.Add, name, model, null, position, kind);
            if (n >= 0) op.Props.Insert(0, new XProp(kind.Number, D(n)));
            return op;
        }

        static string D(long v) { return v.ToString(CultureInfo.InvariantCulture); }

        /// <summary>A position "x, y, z": three numbers, written without an exponent.</summary>
        static string Point3(string s)
        {
            string[] parts = s.Split(',');
            var o = new List<string>();
            foreach (string part in parts)
            {
                double v;
                if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) || !(Math.Abs(v) <= 10000000))
                { o = null; break; }
                o.Add(v.ToString("0.######", CultureInfo.InvariantCulture));
            }
            if (o == null || o.Count != 3) throw new ModKitException("Position takes three numbers split by commas (as 150, -300, 0), not \"" + s + "\"");
            return string.Join(", ", o.ToArray());
        }

        /// <summary>"x, y, z": three numbers above 0, written as the shipped maps write them.</summary>
        static string Dimensions(string s)
        {
            string[] parts = s.Split(',');
            var o = new List<string>();
            foreach (string part in parts)
            {
                double v;
                if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) || !(v >= 0.001) || !(v <= 1000000))
                { o = null; break; }
                o.Add(v.ToString("0.######", CultureInfo.InvariantCulture));      // never the exponent form
            }
            if (o == null || o.Count != 3) throw new ModKitException("ShapeDimensions takes three sizes from 0.001 to 1000000 split by commas (as 300, 300, 200), not \"" + s + "\"");
            return string.Join(", ", o.ToArray());
        }

        /// <summary>A new spawn point: a &lt;spawn&gt; op with the next free SpawnPointID; it has no server row yet.</summary>
        public MapEntityOp AddSpawn(Mod mod, long mapId, string xblock, string name, string model, string position)
        {
            if (model != null && IsNpcOrPcSpawn(model))
                throw new ModKitException("the model " + model + " is an NPC or player spawn point, which takes no spawn rows; use Add other... > NPC spawn point for an NPC");
            long n = NextNumber(mod, xblock, false);
            MapEntityOp op = NewOp(mod, mapId, xblock, MapOpKind.Spawn, name, model, "SpawnPointID", position);
            op.Number = n;
            MapOf(mod, xblock).Ops.Add(op);
            return op;
        }

        /// <summary>A new portal: a &lt;portal&gt; op with the next free PortalID that leads to a portal of a map.</summary>
        public MapEntityOp AddPortal(Mod mod, long mapId, string xblock, string name, string model, string position, long toMap, long toPortal)
        {
            long n = NextNumber(mod, xblock, true), tm = Number("TargetFieldSN", toMap), tp = Number("TargetPortalID", toPortal);
            MapEntityOp op = NewOp(mod, mapId, xblock, MapOpKind.Portal, name, model, "PortalID", position);
            op.Number = n;
            op.ToMap = tm;
            op.ToPortal = tp;
            MapOf(mod, xblock).Ops.Add(op);
            return op;
        }

        MapEntityOp NewOp(Mod mod, long mapId, string xblock, MapOpKind kind, string name, string model, string needs, string position, AddKind of = null)
        {
            if (mod == null) throw new ModKitException("no mod is being edited");
            if (!Has(xblock)) throw new ModKitException(Exported + " has no map file " + xblock);
            if (string.IsNullOrEmpty(name) || name.Trim() != name) throw new ModKitException("give the new entity a name without spaces at an end");
            if (FindNew(mod, xblock, name) != null) throw new ModKitException("the mod already adds an entity named \"" + name + "\" to " + xblock + "; the id of the new entity comes from its name");
            if (string.IsNullOrEmpty(model)) throw new ModKitException("give the model of the new entity");
            FlatModel fm = Model(model);
            if (fm == null) throw new ModKitException("the model listing (flat/) has no model " + model);
            if (of != null && !HasMixin(model, of.Mixin)) throw new ModKitException("the model " + model + " is not a " + of.Label + " model (it has no mixin " + of.Mixin + ")");
            if (needs != null && fm.Prop(needs) == null) throw new ModKitException("the model " + model + " has no property " + needs + "; pick a " + (kind == MapOpKind.Spawn ? "spawn point" : "portal") + " model");
            var op = new MapEntityOp { Kind = kind, Name = name, Model = model, EntityName = name };
            if (!string.IsNullOrEmpty(position)) op.Props.Add(new XProp("Position", Point3(position)));
            MapOp m = MapOf(mod, xblock);
            // the server rows of a spawn need the map id; a <map> that names one (or has spawns already) keeps it
            if (m != null && mapId > 0 && m.EffectiveMapId >= 0 && m.EffectiveMapId != mapId
                && (m.MapId >= 0 || m.Ops.Exists(delegate (MapEntityOp x) { return x.Kind == MapOpKind.Spawn; })))
                throw new ModKitException("the mod's <map> of " + xblock + " is for map " + m.EffectiveMapId.ToString(CultureInfo.InvariantCulture) + ", not " + mapId.ToString(CultureInfo.InvariantCulture)
                    + "; open the file through map " + m.EffectiveMapId.ToString(CultureInfo.InvariantCulture) + " (or change id= of the <map> in mod.xml)");
            if (m == null) mod.Maps.Add(m = new MapOp { XBlock = Key(xblock) });
            if (mapId > 0 && m.EffectiveMapId != mapId) m.MapId = mapId;
            return op;
        }

        static long Number(string what, long v)
        {
            if (v < 0 || v > int.MaxValue) throw new ModKitException(what + " " + v + " is not a number the game reads (0 to " + int.MaxValue + ")");
            return v;
        }

        static long Number(string what, string s)
        {
            long v;
            if (s == null || !long.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out v)) throw new ModKitException(what + " takes a whole number, not \"" + s + "\"");
            return Number(what, v);
        }

        /// <summary>The numbers a spawn or portal op writes itself, by property name.</summary>
        static string[] Own(MapEntityOp op)
        {
            return op.Kind == MapOpKind.Spawn ? new[] { "SpawnPointID" } : op.Kind == MapOpKind.Portal ? new[] { "PortalID", "TargetFieldSN", "TargetPortalID" } : new string[0];
        }

        static long OwnValue(MapEntityOp op, string name)
        {
            return name == "TargetFieldSN" ? op.ToMap : name == "TargetPortalID" ? op.ToPortal : op.Number;
        }

        static string RowText(List<KeyValuePair<string, string>> row)
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in row)
            {
                bool quote = kv.Value.Length == 0 || kv.Value.IndexOf(' ') >= 0 || kv.Value.IndexOf('\t') >= 0;
                sb.Append(sb.Length > 0 ? " " : "").Append(kv.Key).Append('=').Append(quote ? "\"" + kv.Value + "\"" : kv.Value);
            }
            return sb.ToString();
        }

        /// <summary>
        /// A server row typed as key=value pairs split by spaces; a value with spaces goes in double quotes (tag="a b"), and
        /// the quotes of a quoted value are not part of it. A key must be an XML attribute name: it is written as one.
        /// </summary>
        static List<KeyValuePair<string, string>> ParseRow(string s)
        {
            const string form = "a server row is key=value pairs split by spaces (as tag=wolf population=3 coolTime=20; a value with spaces in double quotes)";
            var row = new List<KeyValuePair<string, string>>();
            int i = 0;
            while (true)
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
                if (i >= s.Length) break;
                int start = i;
                while (i < s.Length && s[i] != '=' && s[i] != ' ' && s[i] != '\t') i++;
                if (i >= s.Length || s[i] != '=' || i == start) throw new ModKitException(form + "; \"" + s.Substring(start, i - start) + "\" is not one");
                string k = s.Substring(start, i - start), v;
                i++;
                if (i < s.Length && s[i] == '"')
                {
                    int end = s.IndexOf('"', i + 1);
                    if (end < 0) throw new ModKitException("the value of " + k + " has no closing double quote");
                    v = s.Substring(i + 1, end - i - 1);
                    i = end + 1;
                    if (i < s.Length && s[i] != ' ' && s[i] != '\t') throw new ModKitException("put a space after the closing double quote of the value of " + k);
                }
                else
                {
                    int vs = i;
                    while (i < s.Length && s[i] != ' ' && s[i] != '\t') i++;
                    v = s.Substring(vs, i - vs);
                    if (v.IndexOf('"') >= 0) throw new ModKitException("a double quote only goes around a whole value (" + k + "=\"...\"), not \"" + v + "\"");
                }
                if (!TableDraft.IsAttrName(k)) throw new ModKitException("\"" + k + "\" is not an XML attribute name (letters, digits, _ - . :, not first a digit); a server row key is written as one");
                if (k == "mapCode" || k == "spawnPointID") throw new ModKitException("a server row does not take " + k + "; the tool writes it from the map and the SpawnPointID");
                if (row.Exists(delegate (KeyValuePair<string, string> x) { return x.Key == k; })) throw new ModKitException("the server row gives " + k + " twice");
                row.Add(new KeyValuePair<string, string>(k, v));
            }
            if (row.Count == 0) throw new ModKitException("give the server row as key=value pairs, or Unset it to drop it");
            return row;
        }

        /// <summary>The property items of a new entity: its numbers, its properties, its server rows, then the model's others.</summary>
        public List<MapPropRow> RowsOfNew(MapEntityOp op)
        {
            var rows = new List<MapPropRow>();
            FlatModel model = Model(op.Model);
            var named = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (string n in Own(op))
            {
                MapPropRow r = Row(model, n, null, null);
                r.Current = OwnValue(op, n).ToString(CultureInfo.InvariantCulture);
                r.Edited = true;
                rows.Add(r);
                named[n] = true;
            }
            foreach (XProp p in op.Props)
            {
                named[p.Name] = true;
                foreach (XSet s in p.Items) { MapPropRow r = Row(model, p.Name, s.Index, null); r.Current = s.Value; r.Edited = true; rows.Add(r); }
            }
            if (op.Kind == MapOpKind.Spawn)
                for (int k = 0; k < op.Rows.Count; k++)
                    rows.Add(new MapPropRow { Name = ServerRow, Index = k.ToString(CultureInfo.InvariantCulture), Current = RowText(op.Rows[k]), Edited = true, Type = "server" });
            if (model != null)
                foreach (FlatProp p in model.Props)
                    if (p.Name != null && !named.ContainsKey(p.Name)) rows.Add(Row(model, p.Name, null, null));
            return rows;
        }

        /// <summary>
        /// Sets one property item of a new entity (value null: takes it out). The numbers of a spawn or portal are set,
        /// never unset; "(server row)" index k sets or drops a server row of a spawn point (k = the count adds one).
        /// A per-map number already used in the file is refused. A property the entity does not have yet is checked
        /// against its model as Set does (the name, and an index only on a list); one it has can always be changed or
        /// taken out, so a hand-written one is never stuck.
        /// </summary>
        public void SetNew(Mod mod, string xblock, MapEntityOp op, string name, string index, string value)
        {
            if (op == null || !IsNew(op) || !NewOps(mod, xblock).Contains(op)) throw new ModKitException("the mod does not add that entity to " + xblock);
            if (string.IsNullOrEmpty(name)) throw new ModKitException("give the name of the property");
            if (name.Trim() != name) throw new ModKitException("the property name \"" + name + "\" has spaces at an end");
            if (name.IndexOf(',') >= 0) throw new ModKitException("a property name has no comma: \"" + name + "\"");
            if (index != null && index.Trim().Length == 0) throw new ModKitException("the index is empty; leave it out for a plain value");
            if (Array.IndexOf(Own(op), name) >= 0)
            {
                if (index != null) throw new ModKitException("the property " + name + " is a plain value; leave the index out");
                if (value == null) throw new ModKitException("the " + (op.Kind == MapOpKind.Spawn ? "spawn point" : "portal") + " writes " + name + " itself; set another number, it cannot be unset");
                long n = Number(name, value);
                if (name == "TargetFieldSN") op.ToMap = n;
                else if (name == "TargetPortalID") op.ToPortal = n;
                else
                {
                    if (n != op.Number) RefuseUsed(mod, xblock, op, name, n);
                    op.Number = n;
                }
                return;
            }
            if (name == ServerRow)
            {
                if (op.Kind != MapOpKind.Spawn) throw new ModKitException("only a spawn point has server rows");
                int k;
                if (index == null || !int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out k) || k > op.Rows.Count)
                    throw new ModKitException("a server row takes the index of a row (0 to " + op.Rows.Count + "; " + op.Rows.Count + " adds one)");
                if (value == null) { if (k < op.Rows.Count) op.Rows.RemoveAt(k); return; }
                List<KeyValuePair<string, string>> row = ParseRow(value);
                if (k == op.Rows.Count) op.Rows.Add(row); else op.Rows[k] = row;
                return;
            }
            XProp p = op.Props.Find(delegate (XProp x) { return x.Name == name; });
            // a set is checked also when a hand-written <add> does not have the property yet; only the unset needs it
            if (op.Kind == MapOpKind.Add && (p != null || value != null) && Array.IndexOf(MapNumbers, name) >= 0)
            {
                // the per-map number of an added NPC spawn, trigger box, ...: another number, never none
                if (index != null) throw new ModKitException("the property " + name + " is a plain value; leave the index out");
                if (value == null) throw new ModKitException("the new entity needs its " + name + "; set another number, it cannot be unset");
                long n = Number(name, value);
                RefuseUsed(mod, xblock, op, name, n);
                value = D(n);
            }
            if (op.Kind == MapOpKind.Add && (p != null || value != null) && name == "interactID" && HasMixin(op.Model, Interact.Mixin))
            {
                // the interact object row of an added interact object: another row, never none
                if (index != null) throw new ModKitException("the property interactID is a plain value; leave the index out");
                if (value == null) throw new ModKitException("the new interact object needs its interactID; set another one, it cannot be unset");
                long n = Number(name, value);
                if (n == 0) throw new ModKitException("give the interactID of the object (a row of the interact object table)");
                value = D(n);
            }
            // the position of a new entity: three numbers, as the add buttons take it
            if (name == "Position" && index == null && value != null) value = Point3(value);
            // the size of an added trigger box: checked and written the way Add trigger box does
            if (op.Kind == MapOpKind.Add && name == "ShapeDimensions" && index == null && value != null && HasMixin(op.Model, TriggerBox.Mixin))
                value = Dimensions(value);
            if (p == null && value != null)
            {
                // a property the entity does not have yet: the model must have it, with an index only on a list
                FlatModel model = Model(op.Model);
                if (model != null)
                {
                    FlatProp fp = model.Prop(name);
                    if (fp == null) throw new ModKitException("the model " + model.Name + " has no property " + name);
                    bool list = fp.Type != null && fp.Type.StartsWith("Assoc", StringComparison.Ordinal);
                    if (list != (index != null))
                        throw new ModKitException("the property " + name + " is " + (list ? "a list (" + fp.Type + "); give an index" : "a plain value (" + fp.Type + "); leave the index out"));
                }
            }
            if (p != null && p.Items.Count > 0 && (index == null) != (p.Items[0].Index == null))
                throw new ModKitException("the property " + name + " is " + (index == null ? "a list; give an index" : "a plain value; leave the index out"));
            long oldSum = name == "NpcList" ? NpcListSum(op) : -1;
            if (value == null)
            {
                if (p == null) return;
                p.Items.RemoveAll(delegate (XSet s) { return s.Index == index; });
                if (p.Items.Count == 0) { op.Props.Remove(p); if (op.Order != null) op.Order.Remove(name); }
                if (name == "NpcList") NpcCountInStep(op, oldSum);
                return;
            }
            if (p == null)
            {
                op.Props.Add(p = new XProp(name));
                if (op.Order != null) op.Order.Add(name);
            }
            int at = p.Items.FindIndex(delegate (XSet s) { return s.Index == index; });
            if (at >= 0) p.Items[at] = new XSet(index, value); else p.Items.Add(new XSet(index, value));
            if (name == "NpcList") NpcCountInStep(op, oldSum);
        }

        /// <summary>
        /// NpcCount of an added NPC spawn point follows the sum of its NpcList counts: it is rewritten after an NpcList edit
        /// only when it was that sum before the edit (<paramref name="oldSum"/>), so a count set by hand (to spawn fewer
        /// than the list holds) stays. With the whole list unset the sum is 0.
        /// </summary>
        static void NpcCountInStep(MapEntityOp op, long oldSum)
        {
            int at = op.Props.FindIndex(delegate (XProp x) { return x.Name == "NpcCount"; });
            if (op.Kind != MapOpKind.Add || at < 0 || !op.Props[at].Scalar || oldSum < 0) return;
            long count;
            if (!long.TryParse(op.Props[at].Items[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out count) || count != oldSum) return;
            long sum = NpcListSum(op);
            if (sum < 0 || sum > int.MaxValue) return;
            op.Props[at] = new XProp("NpcCount", D(sum));
        }

        /// <summary>The sum of the NpcList counts (0 without a list; -1 when one is not a number).</summary>
        static long NpcListSum(MapEntityOp op)
        {
            XProp list = op.Props.Find(delegate (XProp x) { return x.Name == "NpcList"; });
            long sum = 0, v;
            if (list != null)
                foreach (XSet s in list.Items)
                {
                    if (!long.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out v)) return -1;
                    sum += v;
                }
            return sum;
        }

        /// <summary>The per-map numbers: an added entity that has one keeps it (it can be changed, not unset).</summary>
        static readonly string[] MapNumbers = { "PortalID", "SpawnPointID", "TriggerObjectID" };

        /// <summary>Drops a new entity the mod adds to the map file.</summary>
        public static void RemoveNew(Mod mod, string xblock, MapEntityOp op)
        {
            MapOp m = MapOf(mod, xblock);
            if (m == null || !m.Ops.Remove(op)) return;
            Tidy(mod, m, null);
        }

        static void Tidy(Mod mod, MapOp m, MapEntityOp op)
        {
            if (m == null) return;
            if (op != null && op.Sets.Count == 0 && op.SetIterations == 0) m.Ops.Remove(op);
            if (m.Ops.Count == 0) mod.Maps.Remove(m);
        }
    }
}
