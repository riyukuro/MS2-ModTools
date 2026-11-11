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

        /// <summary>The models of the map file's shipped entities whose model has the property (most used first).</summary>
        public List<string> ModelsWith(string xblock, string prop)
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
                    if (fm != null && fm.Prop(prop) != null) count[e.Model] = 1;
                }
            var res = new List<string>(count.Keys);
            res.Sort(delegate (string a, string b) { int d = count[b].CompareTo(count[a]); return d != 0 ? d : string.CompareOrdinal(a, b); });
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
            var used = new HashSet<long>();
            List<XEntity> l = Shipped(xblock);
            if (l != null) foreach (XEntity e in l) used.Add(Scalar(e, portal ? "PortalID" : "SpawnPointID"));
            foreach (MapEntityOp op in NewOps(mod, xblock))
                if (op.Kind == (portal ? MapOpKind.Portal : MapOpKind.Spawn)) used.Add(op.Number);
            long from = portal ? IdSpaces.PortalFrom : IdSpaces.SpawnPointFrom, to = portal ? IdSpaces.PortalTo : IdSpaces.SpawnPointTo;
            for (long n = from; n <= to; n++) if (!used.Contains(n)) return n;
            throw new ModKitException("every " + (portal ? "PortalID" : "SpawnPointID") + " of the mod range (" + from + "-" + to + ") is used in " + xblock);
        }

        /// <summary>A new spawn point: a &lt;spawn&gt; op with the next free SpawnPointID; it has no server row yet.</summary>
        public MapEntityOp AddSpawn(Mod mod, long mapId, string xblock, string name, string model, string position)
        {
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

        MapEntityOp NewOp(Mod mod, long mapId, string xblock, MapOpKind kind, string name, string model, string needs, string position)
        {
            if (mod == null) throw new ModKitException("no mod is being edited");
            if (!Has(xblock)) throw new ModKitException(Exported + " has no map file " + xblock);
            if (string.IsNullOrEmpty(name) || name.Trim() != name) throw new ModKitException("give the new entity a name without spaces at an end");
            if (FindNew(mod, xblock, name) != null) throw new ModKitException("the mod already adds an entity named \"" + name + "\" to " + xblock + "; the id of the new entity comes from its name");
            if (string.IsNullOrEmpty(model)) throw new ModKitException("give the model of the new entity");
            FlatModel fm = Model(model);
            if (fm == null) throw new ModKitException("the model listing (flat/) has no model " + model);
            if (fm.Prop(needs) == null) throw new ModKitException("the model " + model + " has no property " + needs + "; pick a " + (kind == MapOpKind.Spawn ? "spawn point" : "portal") + " model");
            var op = new MapEntityOp { Kind = kind, Name = name, Model = model, EntityName = name };
            if (!string.IsNullOrEmpty(position)) op.Props.Add(new XProp("Position", position.Trim()));
            MapOp m = MapOf(mod, xblock);
            if (m == null) mod.Maps.Add(m = new MapOp { XBlock = Key(xblock) });
            if (mapId > 0 && m.EffectiveMapId != mapId) m.MapId = mapId;   // the server rows of a spawn need the map id
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
            foreach (KeyValuePair<string, string> kv in row) sb.Append(sb.Length > 0 ? " " : "").Append(kv.Key).Append('=').Append(kv.Value);
            return sb.ToString();
        }

        static List<KeyValuePair<string, string>> ParseRow(string s)
        {
            var row = new List<KeyValuePair<string, string>>();
            foreach (string part in s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) throw new ModKitException("a server row is key=value pairs split by spaces (as tag=... population=... coolTime=...); \"" + part + "\" is not one");
                string k = part.Substring(0, eq);
                if (k == "mapCode" || k == "spawnPointID") throw new ModKitException("a server row does not take " + k + "; the tool writes it from the map and the SpawnPointID");
                if (row.Exists(delegate (KeyValuePair<string, string> x) { return x.Key == k; })) throw new ModKitException("the server row gives " + k + " twice");
                row.Add(new KeyValuePair<string, string>(k, part.Substring(eq + 1)));
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
        /// </summary>
        public static void SetNew(Mod mod, string xblock, MapEntityOp op, string name, string index, string value)
        {
            if (op == null || !IsNew(op) || !NewOps(mod, xblock).Contains(op)) throw new ModKitException("the mod does not add that entity to " + xblock);
            if (string.IsNullOrEmpty(name)) throw new ModKitException("give the name of the property");
            if (name.Trim() != name) throw new ModKitException("the property name \"" + name + "\" has spaces at an end");
            if (Array.IndexOf(Own(op), name) >= 0)
            {
                if (index != null) throw new ModKitException("the property " + name + " is a plain value; leave the index out");
                if (value == null) throw new ModKitException("the " + (op.Kind == MapOpKind.Spawn ? "spawn point" : "portal") + " writes " + name + " itself; set another number, it cannot be unset");
                long n = Number(name, value);
                if (name == "TargetFieldSN") op.ToMap = n; else if (name == "TargetPortalID") op.ToPortal = n; else op.Number = n;
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
            if (p != null && p.Items.Count > 0 && (index == null) != (p.Items[0].Index == null))
                throw new ModKitException("the property " + name + " is " + (index == null ? "a list; give an index" : "a plain value; leave the index out"));
            if (value == null)
            {
                if (p == null) return;
                p.Items.RemoveAll(delegate (XSet s) { return s.Index == index; });
                if (p.Items.Count == 0) { op.Props.Remove(p); if (op.Order != null) op.Order.Remove(name); }
                return;
            }
            if (p == null)
            {
                op.Props.Add(p = new XProp(name));
                if (op.Order != null) op.Order.Add(name);
            }
            int at = p.Items.FindIndex(delegate (XSet s) { return s.Index == index; });
            if (at >= 0) p.Items[at] = new XSet(index, value); else p.Items.Add(new XSet(index, value));
        }

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
