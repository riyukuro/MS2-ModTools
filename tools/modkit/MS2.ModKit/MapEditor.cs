// The entity edits of a map as the Toolbench Maps view makes them: the shipped map files (Resource/Exported.m2d) are
// read as they are, and every edit becomes a map op of the mod (MapOps.cs) with the shipped value as its expected old
// one, so that the planner makes it on every target that ships the same entity and warns where one does not.
//
//   Set      a property (one index of a list property) to a value: a <change> with <set old value>; the shipped value
//            again drops the set, a change without sets drops away.
//   Unset    the stored value: <unset old>, so the entity takes its model's default.
//   Remove   the entity: a <remove> in place of its change.
//   Restore  the entity: every op of it is dropped.
using System;
using System.Collections.Generic;
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
            M2dEntry f;
            if (!flats.TryGetValue(name, out f)) return null;
            try
            {
                FlatModel m = FlatIndex.ParseFlat(arch.Read(f), f.Name);
                return m.Name == name ? m : null;
            }
            catch (ModKitException) { return null; }
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
        /// entity lack is refused, and an index where the model's type is a plain value (or none where it is a list).
        /// </summary>
        public static void Set(Mod mod, string xblock, XEntity e, string name, string index, string value, FlatModel model = null)
        {
            if (string.IsNullOrEmpty(name)) throw new ModKitException("give the name of the property");
            if (name.Trim() != name) throw new ModKitException("the property name \"" + name + "\" has spaces at an end");
            if (index != null && index.Trim().Length == 0) throw new ModKitException("the index is empty; leave it out for a plain value");
            string id = IdOf(e);
            if (IsRemoved(mod, xblock, id)) throw new ModKitException("the mod removes entity " + id + "; restore it first");
            XProp shippedProp = e.Find(name);
            if (shippedProp != null && shippedProp.Items.Count > 0 && (index == null) != (shippedProp.Items[0].Index == null))
                throw new ModKitException("the property " + name + " is " + (index == null ? "a list; give an index" : "a plain value; leave the index out"));
            if (model != null && shippedProp == null)
            {
                FlatProp fp = model.Prop(name);
                if (fp == null) throw new ModKitException("the model " + model.Name + " has no property " + name);
                bool list = fp.Type != null && fp.Type.StartsWith("Assoc", StringComparison.Ordinal);
                if (list != (index != null))
                    throw new ModKitException("the property " + name + " is " + (list ? "a list (" + fp.Type + "); give an index" : "a plain value (" + fp.Type + "); leave the index out"));
            }
            string old = Shipped(e, name, index);
            MapOp m = MapOf(mod, xblock);
            MapEntityOp op = null;
            MapSet set = null;
            foreach (MapEntityOp c in ChangesOf(m, id))
            {
                if (op == null) op = c;
                MapSet f = c.Sets.Find(delegate (MapSet s) { return s.Name == name && s.Index == index; });
                if (f != null) { op = c; set = f; break; }
            }
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

        static void Tidy(Mod mod, MapOp m, MapEntityOp op)
        {
            if (m == null) return;
            if (op != null && op.Sets.Count == 0 && op.SetIterations == 0) m.Ops.Remove(op);
            if (m.Ops.Count == 0) mod.Maps.Remove(m);
        }
    }
}
