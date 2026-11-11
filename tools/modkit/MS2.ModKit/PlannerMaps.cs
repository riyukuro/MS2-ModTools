// Map ops (MapOps.cs) on the shipped maps: entries xblock/<name>.xblock of <root>:Resource/Exported.m2d.
//
// Every target that has that archive is planned by itself, against its own copy of the map: the same op can fit
// one target and be skipped with a warning on another. Only the touched lines of a map file change; every other
// byte of it stays (the edits go through XmlEditor, in the file's own writing style).
//
//   remove, change   name the entity by id and carry what the mod expects there now (model, position, the old value
//                    of each changed property). Something else there: a warning, the op is skipped on that target.
//   add, spawn,      the new entity gets an id derived from (mod id, xblock name, op name) and is put where the
//   portal           id-sorted order of the file wants it. An id that is already in the map is an error.
//   spawn            also adds its rows to table/mapspawntag.xml of server:Xml.m2d (what spawns at the point).
//   numbers          PortalID and SpawnPointID are per map. A number the map has already is an error (a conflict when
//                    another mod of the same plan brings it); a number outside the mod range is a warning.
//   two mods         may change the same map; the same entity touched by two mods is a conflict.
//
// A target whose archive is the same file as an earlier target's (the server's Resource folder is often a link to a
// client's) is written once, through the earlier target.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MS2.ModKit.M2d;
using MS2.ModKit.MapEdit;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        public const string MapArchivePath = "Resource/Exported.m2d";
        public const string SpawnTagArchiveName = "Xml.m2d", SpawnTagEntry = "table/mapspawntag.xml";
        public const string MapIngestNote = "the server does not read map files while it runs: after this apply, remove the stored navigation meshes of the changed maps and run the server's ingest again (this tool does neither)";

        sealed class MapEnt
        {
            internal XmlElem Elem;              // null for an entity a mod adds
            internal string Id, Model;
            internal long Portal = -1, Spawn = -1, ToMap = -1, ToPortal = -1;
            internal Mod AddedBy, Touched;
            internal bool Removed;
            internal XEntity New;
            internal MapEntityOp Op;
        }

        sealed class MapState
        {
            internal EntryState St;
            internal Profile Target;
            internal string ArchiveRef, XBlockName;
            internal long MapId;
            internal XDialect Dialect;
            internal XmlElem Set;
            internal FlatIndex Flat;
            internal readonly Dictionary<string, MapEnt> ById = new Dictionary<string, MapEnt>(StringComparer.Ordinal);
            internal readonly List<MapEnt> Shipped = new List<MapEnt>();
            internal readonly List<MapEnt> Added = new List<MapEnt>();
        }

        sealed class MapRun
        {
            internal readonly List<KeyValuePair<Profile, string>> Targets = new List<KeyValuePair<Profile, string>>();
            internal readonly Dictionary<string, FlatIndex> Flats = new Dictionary<string, FlatIndex>();
            internal readonly Dictionary<EntryState, MapState> States = new Dictionary<EntryState, MapState>();
            internal readonly List<MapState> Order = new List<MapState>();
            internal readonly Dictionary<string, Dictionary<long, bool>> Npcs = new Dictionary<string, Dictionary<long, bool>>();
            internal readonly Dictionary<string, Dictionary<long, bool>> MapIds = new Dictionary<string, Dictionary<long, bool>>();
            internal readonly Dictionary<string, MapTable> Tables = new Dictionary<string, MapTable>();
            internal readonly Dictionary<string, FeatureSet> Features = new Dictionary<string, FeatureSet>(StringComparer.Ordinal);
            internal readonly Dictionary<string, List<MapEnt>> Others = new Dictionary<string, List<MapEnt>>();
            internal readonly Dictionary<string, bool> Changed = new Dictionary<string, bool>();
        }

        /// <summary>The file a path leads to, links followed (net35: the full path only).</summary>
        internal static string RealPath(string path) { return RealPath(path, 0); }

        // A link's target may itself lie under a linked folder, so the target is resolved again from its root.
        static string RealPath(string path, int depth)
        {
            string full = Path.GetFullPath(path);
#if NET35
            return full;
#else
            if (depth > 16) return full;
            try
            {
                string root = Path.GetPathRoot(full);
                string cur = root;
                foreach (string part in full.Substring(root.Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
                {
                    cur = Path.Combine(cur, part);
                    FileSystemInfo fi = Directory.Exists(cur) ? (FileSystemInfo)new DirectoryInfo(cur) : new FileInfo(cur);
                    if (fi.LinkTarget == null) continue;
                    FileSystemInfo t = fi.ResolveLinkTarget(true);
                    if (t != null) cur = RealPath(t.FullName, depth + 1);
                }
                return cur;
            }
            catch (IOException) { return full; }
            catch (UnauthorizedAccessException) { return full; }
#endif
        }

        static string D(long v) { return v.ToString(CultureInfo.InvariantCulture); }

        static void DoMaps(Run run, IList<Mod> mods)
        {
            bool any = false;
            foreach (Mod m in mods) if (m.Maps.Count > 0) any = true;
            if (!any) return;
            Plan plan = run.Plan;
            var mr = new MapRun();
            var files = new Dictionary<string, Profile>(StringComparer.OrdinalIgnoreCase);
            foreach (Profile p in run.Data.Profiles)
            {
                string ar = p.Root + ":" + MapArchivePath;
                if (!run.Data.Exists(ar)) { plan.Note(p.Name + " has no " + MapArchivePath + ": the map lines are not applied there"); continue; }
                string real = RealPath(run.Data.Resolve(ar));
                Profile first;
                if (files.TryGetValue(real, out first))
                {
                    plan.Note(p.Name + " reads its maps from the same file as " + first.Name + " (its folder is a link): the map changes are written once, through " + first.Name);
                    continue;
                }
                files[real] = p;
                mr.Targets.Add(new KeyValuePair<Profile, string>(p, ar));
            }
            if (mr.Targets.Count == 0) plan.Note("no target has " + MapArchivePath + ": the map lines change nothing");

            // from the highest priority down: an entity or a number that two mods want stays with the first one
            for (int mi = mods.Count - 1; mi >= 0; mi--)
            {
                Mod m = mods[mi];
                foreach (MapOp mop in m.Maps)
                {
                    foreach (KeyValuePair<Profile, string> t in mr.Targets) PlanMap(run, mr, m, mop, t.Key, t.Value);
                    foreach (MapEntityOp op in mop.Ops)
                    {
                        if (op.Kind != MapOpKind.Spawn) continue;
                        // rows only for a place that was put into a map: a place skipped on every target gets none
                        bool placed = false;
                        foreach (MapState ms in mr.Order)
                            foreach (MapEnt e in ms.Added) if (e.Op == op) placed = true;
                        if (placed) SpawnRows(run, m, mop, op);
                        else if (op.Rows.Count > 0)
                            plan.Msg(plan.Warnings, m, null, null, "spawn point " + D(op.Number) + " of map " + mop.XBlock + " was put into no map file, so its spawn rows are not written either (mod.xml line " + op.Line + ")");
                    }
                }
            }

            foreach (MapState ms in mr.Order) FlushAdded(ms);
            foreach (MapState ms in mr.Order) PortalChecks(run, mr, ms);

            var names = new List<string>(mr.Changed.Keys);
            names.Sort(StringComparer.Ordinal);
            if (names.Count > 0) plan.Note("maps changed: " + string.Join(", ", names.ToArray()) + ". " + MapIngestNote);
        }

        // ---- reading a map ----

        static string Scalar(XmlElem entity, string prop)
        {
            foreach (XmlElem p in entity.Children)
                if (p.Name == "property" && p.Attr("name") == prop)
                    return p.Children.Count == 1 && p.Children[0].Attr("index") == null ? p.Children[0].Attr("value") : null;
            return null;
        }

        static long ModelNumber(FlatIndex flat, string model, string prop)
        {
            FlatModel fm = flat == null ? null : flat.Get(model);
            FlatProp fp = fm == null ? null : fm.Prop(prop);
            long v;
            if (fp != null && fp.DefaultIsScalar && long.TryParse(fp.Default[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return -1;
        }

        static long Number(string explicitValue, FlatIndex flat, string model, string prop)
        {
            long v;
            if (explicitValue != null) return long.TryParse(explicitValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1;
            return ModelNumber(flat, model, prop);
        }

        static void Numbers(MapEnt me, FlatIndex flat, string portal, string spawn, string toMap, string toPortal)
        {
            me.Portal = Number(portal, flat, me.Model, "PortalID");
            me.Spawn = Number(spawn, flat, me.Model, "SpawnPointID");
            if (me.Portal >= 0)
            {
                me.ToMap = Number(toMap, flat, me.Model, "TargetFieldSN");
                me.ToPortal = Number(toPortal, flat, me.Model, "TargetPortalID");
            }
        }

        static MapEnt ReadEnt(XmlElem e, FlatIndex flat)
        {
            var me = new MapEnt { Elem = e, Id = e.Attr("id"), Model = e.Attr("modelName") };
            Numbers(me, flat, Scalar(e, "PortalID"), Scalar(e, "SpawnPointID"), Scalar(e, "TargetFieldSN"), Scalar(e, "TargetPortalID"));
            return me;
        }

        static XmlElem EntitySet(XmlDoc doc)
        {
            if (doc.Root.Name != "game") return null;
            foreach (XmlElem c in doc.Root.Children) if (c.Name == "entitySet") return c;
            return null;
        }

        static FlatIndex Flat(Run run, MapRun mr, Mod mod, string ar, M2dArchive arch)
        {
            FlatIndex f;
            if (mr.Flats.TryGetValue(ar, out f)) return f;
            try { f = FlatIndex.FromArchive(arch); }
            catch (ModKitException x) { run.Plan.Msg(run.Plan.Warnings, mod, ar, null, "the model listing cannot be read (" + x.Message + "): models and properties are not checked"); f = null; }
            mr.Flats[ar] = f;
            return f;
        }

        static MapState OpenMap(Run run, MapRun mr, Mod mod, MapOp mop, Profile target, string ar)
        {
            Plan plan = run.Plan;
            M2dArchive arch = run.Open(mod, ar);
            if (arch == null) return null;
            string entry = "xblock/" + mop.XBlock + ".xblock";
            EntryState st = run.State(arch, ar, entry);
            if (st.DeletedBy != null && DeletedEntryStays(run, mod, st, "changes a map that " + Who(st.DeletedBy, mod) + " deletes")) return null;
            if (st.Base == null) { plan.Msg(plan.Warnings, mod, ar, entry, target.Name + " has no such map; its map lines are skipped there (mod.xml line " + mop.Line + ")"); return null; }
            Mod instead;
            if (st.BaseMod != mod && st.FileLeftOut.TryGetValue(mod, out instead))
            {
                plan.Conflict(mod, ar, st.Name, "changes its own map file, which is left out", Written(st, instead));
                return null;
            }
            if (st.BaseMod != null && st.BaseMod != mod)
            {
                // the whole file of the higher mod stays as it is. The map lines of the higher mod are made in the
                // shipped file where it can come back, and else (a map the other mod adds) in the other mod's file
                bool wins = run.Outranks(mod, st.BaseMod), back = st.Original != null && st.Doc == null;
                string text = "changes a map that mod " + st.BaseMod.Id + " " + (st.Existed ? "replaces" : "adds") + " as a whole file";
                if (wins && back) plan.EntryConflict(mod, ar, st.Name, text, mod, st.BaseMod);
                else plan.Conflict(mod, ar, st.Name, wins ? text + ": the change is made in that file" : text, wins ? mod : st.BaseMod, wins ? null : st.BaseMod);
                if (!wins) return null;
                if (back)
                {
                    DropEntryChange(plan, st);
                    st.FileLeftOut[st.BaseMod] = mod;
                    st.Base = st.Original; st.BaseMod = null;
                }
            }
            MapState ms;
            if (mr.States.TryGetValue(st, out ms)) return ms;
            if (!EnsureDoc(plan, mod, st)) return null;
            XmlElem set = EntitySet(st.Doc);
            if (set == null) { plan.Msg(plan.Errors, mod, ar, st.Name, "is not a map file (no <game><entitySet>)"); return null; }
            ms = new MapState { St = st, Target = target, ArchiveRef = ar, XBlockName = mop.XBlock, MapId = mop.EffectiveMapId, Dialect = XBlock.DetectDialect(st.Base), Set = set, Flat = Flat(run, mr, mod, ar, arch) };
            foreach (XmlElem e in set.Children)
            {
                if (e.Name != "entity") continue;
                MapEnt me = ReadEnt(e, ms.Flat);
                ms.Shipped.Add(me);
                if (me.Id != null) ms.ById[me.Id] = me;
            }
            mr.States[st] = ms;
            mr.Order.Add(ms);
            MapFileCheck(run, mr, mod, mop, ms);
            return ms;
        }

        // ---- the ops ----

        static string EntRow(string id) { return "entity[id=" + id + "]"; }

        static void PlanMap(Run run, MapRun mr, Mod mod, MapOp mop, Profile target, string ar)
        {
            Plan plan = run.Plan;
            MapState ms = OpenMap(run, mr, mod, mop, target, ar);
            if (ms == null) return;
            EntryState st = ms.St;
            foreach (MapEntityOp op in mop.Ops)
            {
                try
                {
                    if (op.Kind == MapOpKind.Remove || op.Kind == MapOpKind.Change) Existing(run, mr, mod, ms, op);
                    else AddEntity(run, mr, mod, mop, ms, op);
                }
                catch (ModKitException x) { plan.Msg(plan.Errors, mod, ar, st.Name, x.Message + " (mod.xml line " + op.Line + ")"); }
            }
        }

        static bool NumberFree(Run run, Mod mod, MapState ms, MapEnt self, bool portal, long n, int line)
        {
            Plan plan = run.Plan;
            string what = portal ? "PortalID" : "SpawnPointID";
            foreach (MapEnt o in AllEnts(ms))
            {
                if (o == self || o.Removed || (portal ? o.Portal : o.Spawn) != n) continue;
                if (o.AddedBy != null && o.AddedBy != mod)
                    plan.Conflict(mod, ms.ArchiveRef, ms.St.Name, what + " " + D(n) + " is also brought by mod " + o.AddedBy.Id + " (mod.xml line " + line + ")", o.AddedBy);
                else
                    plan.Msg(plan.Errors, mod, ms.ArchiveRef, ms.St.Name, what + " " + D(n) + " is already used in this map by entity " + o.Id + "; nothing is added (mod.xml line " + line + ")");
                return false;
            }
            long from = portal ? IdSpaces.PortalFrom : IdSpaces.SpawnPointFrom, to = portal ? IdSpaces.PortalTo : IdSpaces.SpawnPointTo;
            if (n < from || n > to)
                plan.Msg(plan.Warnings, mod, ms.ArchiveRef, ms.St.Name, what + " " + D(n) + " is outside the range kept for mods (" + D(from) + "-" + D(to) + "): a later game version or another mod may use the same number");
            return true;
        }

        static IEnumerable<MapEnt> AllEnts(MapState ms)
        {
            foreach (MapEnt e in ms.Shipped) yield return e;
            foreach (MapEnt e in ms.Added) yield return e;
        }

        static void AddEntity(Run run, MapRun mr, Mod mod, MapOp mop, MapState ms, MapEntityOp op)
        {
            Plan plan = run.Plan;
            EntryState st = ms.St;
            string id = op.NewId ?? MapValues.NewEntityId(mod.Id, mop.XBlock, op.Name);
            MapEnt old;
            if (ms.ById.TryGetValue(id, out old))
            {
                plan.Msg(plan.Errors, mod, ms.ArchiveRef, st.Name, "the new entity \"" + op.Name + "\" would get the id " + id + ", which is already in the map" + (old.AddedBy == null ? " (is the mod applied there already?)" : "") + " (mod.xml line " + op.Line + ")");
                return;
            }
            var props = new List<XProp>();
            foreach (XProp p in op.Props) props.Add(new XProp(p.Name, new List<XSet>(p.Items)));
            if (op.Kind == MapOpKind.Spawn) props.Add(new XProp("SpawnPointID", D(op.Number)));
            if (op.Kind == MapOpKind.Portal)
            {
                props.Add(new XProp("PortalID", D(op.Number)));
                props.Add(new XProp("TargetFieldSN", D(op.ToMap)));
                props.Add(new XProp("TargetPortalID", D(op.ToPortal)));
            }
            var xe = new XEntity { Id = id, Model = op.Model, Name = op.EntityName, Iterations = op.Iterations, Props = props };
            if (op.Order == null) OrderLikeShipped(ms, xe);
            else Stable.Sort(xe.Props, delegate (XProp x, XProp y) { return op.Order.IndexOf(x.Name).CompareTo(op.Order.IndexOf(y.Name)); });

            var me = new MapEnt { Id = id, Model = op.Model, AddedBy = mod, New = xe, Op = op };
            XProp pp = xe.Find("PortalID"), sp = xe.Find("SpawnPointID"), tm = xe.Find("TargetFieldSN"), tp = xe.Find("TargetPortalID");
            Numbers(me, ms.Flat, pp != null && pp.Scalar ? pp.Items[0].Value : null, sp != null && sp.Scalar ? sp.Items[0].Value : null,
                tm != null && tm.Scalar ? tm.Items[0].Value : null, tp != null && tp.Scalar ? tp.Items[0].Value : null);
            if (op.Kind != MapOpKind.Portal && pp == null) me.Portal = -1;      // a model default is not a number this op brings
            if (op.Kind != MapOpKind.Spawn && sp == null) me.Spawn = -1;
            if (me.Portal >= 0 && !NumberFree(run, mod, ms, me, true, me.Portal, op.Line)) return;
            if (me.Spawn >= 0 && !NumberFree(run, mod, ms, me, false, me.Spawn, op.Line)) return;

            ModelChecks(run, mr, mod, ms, op.Model, props, op.Kind, op.Line);
            ms.ById[id] = me;
            ms.Added.Add(me);
            mr.Changed[mop.XBlock] = true;
            plan.Changes.Add(new PlanChange { Kind = ChangeKind.AddRow, ModId = mod.Id, Archive = ms.ArchiveRef, Entry = st.Name, Row = EntRow(id), NewValue = "<entity modelName=\"" + op.Model + "\" name=\"" + op.EntityName + "\"> (" + props.Count + " properties)", OwnSides = true });
        }

        static void Existing(Run run, MapRun mr, Mod mod, MapState ms, MapEntityOp op)
        {
            Plan plan = run.Plan;
            EntryState st = ms.St;
            string verb = op.Kind == MapOpKind.Remove ? "removes" : "changes";
            MapEnt me;
            if (!ms.ById.TryGetValue(op.Id, out me))
            {
                plan.Msg(plan.Warnings, mod, ms.ArchiveRef, st.Name, "has no entity " + op.Id + "; the " + (op.Kind == MapOpKind.Remove ? "remove" : "change") + " is skipped on " + ms.Target.Name + " (mod.xml line " + op.Line + ")");
                return;
            }
            if (me.AddedBy != null)
            {
                string t = verb + " entity " + op.Id + " that " + Who(me.AddedBy, mod) + " adds";
                if (me.AddedBy == mod) plan.Msg(plan.Errors, mod, ms.ArchiveRef, st.Name, t); else plan.Conflict(mod, ms.ArchiveRef, st.Name, t, me.AddedBy);
                return;
            }
            if (me.Touched != null && (me.Touched != mod || me.Removed || op.Kind == MapOpKind.Remove))
            {
                string t = verb + " entity " + op.Id + " that " + Who(me.Touched, mod) + " " + (me.Removed ? "removes" : "changes");
                if (me.Touched == mod) plan.Msg(plan.Errors, mod, ms.ArchiveRef, st.Name, t); else plan.Conflict(mod, ms.ArchiveRef, st.Name, t, me.Touched);
                return;
            }
            string skip = " the " + (op.Kind == MapOpKind.Remove ? "remove" : "change") + " is skipped on " + ms.Target.Name + " (mod.xml line " + op.Line + ")";
            if (me.Model != op.Model)
            {
                plan.Msg(plan.Warnings, mod, ms.ArchiveRef, st.Name, "entity " + op.Id + " is a \"" + me.Model + "\" here, the mod expects \"" + op.Model + "\";" + skip);
                return;
            }
            string pos = Scalar(me.Elem, "Position") ?? "";
            if (pos != op.Position)
            {
                plan.Msg(plan.Warnings, mod, ms.ArchiveRef, st.Name, "entity " + op.Id + " stands at \"" + pos + "\" here, the mod expects \"" + op.Position + "\";" + skip);
                return;
            }
            if (op.Kind == MapOpKind.Remove)
            {
                st.Ed.DropEditsWithin(me.Elem);
                st.Ed.Delete(me.Elem);
                me.Removed = true; me.Touched = mod;
                mr.Changed[ms.XBlockName] = true;
                plan.Changes.Add(new PlanChange { Kind = ChangeKind.DeleteRow, ModId = mod.Id, Archive = ms.ArchiveRef, Entry = st.Name, Row = EntRow(op.Id), OldValue = "<entity modelName=\"" + me.Model + "\" name=\"" + me.Elem.Attr("name") + "\">", OwnSides = true });
                return;
            }

            // every expected old value first: a change is made as a whole or not at all
            var seen = new Dictionary<string, bool>();
            foreach (MapSet s in op.Sets)
            {
                string key = s.Name + "\n" + s.Index;
                if (seen.ContainsKey(key)) throw new ModKitException("the property " + s.Name + (s.Index != null ? " [" + s.Index + "]" : "") + " is changed twice in one <change>");
                seen[key] = true;
                XmlElem prop, item;
                FindItem(me.Elem, s, out prop, out item);
                string cur = item == null ? null : item.Attr("value");
                if (cur != s.Old)
                {
                    plan.Msg(plan.Warnings, mod, ms.ArchiveRef, st.Name, "entity " + op.Id + " property " + s.Name + (s.Index != null ? " [" + s.Index + "]" : "") + " is " + Show(cur).Replace("(removed)", "not stored") + " here, the mod expects " + Show(s.Old).Replace("(removed)", "no stored value") + ";" + skip);
                    return;
                }
                if (prop != null && s.Value != null && item == null && (s.Index == null) != (prop.Children.Count == 0 || prop.Children[0].Attr("index") == null) && prop.Children.Count > 0)
                    throw new ModKitException("the property " + s.Name + " of entity " + op.Id + " is " + (s.Index == null ? "a list here; give index=\"...\"" : "a plain value here; leave index out"));
            }
            long portal = me.Portal, spawn = me.Spawn;
            foreach (MapSet s in op.Sets)
            {
                if (s.Index != null || (s.Name != "PortalID" && s.Name != "SpawnPointID")) continue;
                bool isPortal = s.Name == "PortalID";
                long n = Number(s.Value, ms.Flat, me.Model, s.Name);
                if (n >= 0 && n != (isPortal ? me.Portal : me.Spawn) && !NumberFree(run, mod, ms, me, isPortal, n, op.Line)) return;
                if (isPortal) portal = n; else spawn = n;
            }

            string wasIt = me.Elem.Attr("iterations"), newIt = op.SetIterations.ToString(CultureInfo.InvariantCulture);
            if (op.SetIterations != 0 && wasIt != newIt)
            {
                st.Ed.SetAttr(me.Elem, "iterations", newIt);
                plan.Changes.Add(new PlanChange { Kind = ChangeKind.SetAttr, ModId = mod.Id, Archive = ms.ArchiveRef, Entry = st.Name, Row = EntRow(op.Id), Name = "iterations", OldValue = wasIt, NewValue = newIt, OwnSides = true });
            }
            var fresh = new List<XProp>();
            var check = new List<XProp>();
            foreach (MapSet s in op.Sets)
            {
                XmlElem prop, item;
                FindItem(me.Elem, s, out prop, out item);
                string row = EntRow(op.Id) + "/property[name=" + s.Name + "]" + (s.Index != null ? "/set[index=" + s.Index + "]" : "");
                if (s.Value == null)
                {
                    if (prop.Children.Count == 1) st.Ed.Delete(prop); else st.Ed.Delete(item);
                    plan.Changes.Add(new PlanChange { Kind = ChangeKind.RemoveAttr, ModId = mod.Id, Archive = ms.ArchiveRef, Entry = st.Name, Row = row, Name = "value", OldValue = s.Old, OwnSides = true });
                    continue;
                }
                check.Add(new XProp(s.Name, new List<XSet> { new XSet(s.Index, s.Value) }));
                if (item != null) st.Ed.SetAttr(item, "value", s.Value);
                else if (prop != null)
                {
                    string t = XBlock.PropText(new XProp(s.Name, new List<XSet> { new XSet(s.Index, s.Value) }), ms.Dialect);
                    int a = t.IndexOf("<set", StringComparison.Ordinal), b = t.LastIndexOf("/>", t.LastIndexOf("</property>", StringComparison.Ordinal), StringComparison.Ordinal) + 2;
                    st.Ed.Append(prop, t.Substring(a, b - a), Indent(ms, 4));
                }
                else
                {
                    XProp np = fresh.Find(delegate (XProp q) { return q.Name == s.Name; });
                    if (np == null) fresh.Add(np = new XProp(s.Name));
                    np.Items.Add(new XSet(s.Index, s.Value));
                }
                plan.Changes.Add(new PlanChange { Kind = ChangeKind.SetAttr, ModId = mod.Id, Archive = ms.ArchiveRef, Entry = st.Name, Row = row, Name = "value", OldValue = s.Old, NewValue = s.Value, OwnSides = true });
            }
            // new properties go where the shipped order (lower-cased name, "Name" last) wants them
            Stable.Sort(fresh, delegate (XProp x, XProp y) { return PropOrder(x.Name, y.Name); });
            foreach (XProp np in fresh)
            {
                XmlElem anchor = null;
                foreach (XmlElem p in me.Elem.Children)
                    if (p.Name == "property" && PropOrder(p.Attr("name") ?? "", np.Name) > 0) { anchor = p; break; }
                string text = XBlock.PropText(np, ms.Dialect);
                if (anchor != null) st.Ed.InsertBefore(anchor, text, Indent(ms, 3));
                else st.Ed.Append(me.Elem, text, Indent(ms, 3));
            }
            ModelChecks(run, mr, mod, ms, me.Model, check, MapOpKind.Change, op.Line);
            me.Touched = mod; me.Portal = portal; me.Spawn = spawn;
            foreach (MapSet s in op.Sets)
            {
                if (s.Index != null) continue;
                if (s.Name == "TargetFieldSN") me.ToMap = Number(s.Value, ms.Flat, me.Model, s.Name);
                if (s.Name == "TargetPortalID") me.ToPortal = Number(s.Value, ms.Flat, me.Model, s.Name);
            }
            mr.Changed[ms.XBlockName] = true;
        }

        // A new entity lists its properties the way the shipped entities of the same model in this file do: first
        // those of the shipped entity that shares the most properties with it, in that entity's order; each other
        // one next to its neighbour in the first shipped entity of the model that has both, else next to its
        // neighbour in the model's declared order. A file without such an entity gives the declared order alone.
        static void OrderLikeShipped(MapState ms, XEntity xe)
        {
            var names = new List<string>();
            foreach (XProp p in xe.Props) names.Add(p.Name);
            names = XBlock.ShippedOrder(names);        // a fixed starting order, so the result does not depend on the mod's
            var seqs = new List<List<string>>();
            List<string> best = null;
            int bestShared = 0;
            foreach (MapEnt o in ms.Shipped)
            {
                if (o.Model != xe.Model || o.Elem == null) continue;
                var seq = new List<string>();
                int shared = 0;
                foreach (XmlElem c in o.Elem.Children)
                {
                    if (c.Name != "property") continue;
                    string n = c.Attr("name") ?? "";
                    seq.Add(n);
                    if (names.Contains(n)) shared++;
                }
                seqs.Add(seq);
                if (shared > bestShared) { best = seq; bestShared = shared; }
            }
            var declared = new List<string>();
            FlatModel m = ms.Flat == null ? null : ms.Flat.Get(xe.Model);
            if (m != null) foreach (FlatProp fp in m.Props) declared.Add(fp.Name ?? "");
            seqs.Add(declared);

            var placed = new List<string>();
            if (best != null) foreach (string n in best) if (names.Contains(n) && !placed.Contains(n)) placed.Add(n);
            foreach (string n in names)
            {
                if (placed.Contains(n)) continue;
                bool done = false;
                foreach (List<string> seq in seqs)
                {
                    int at = seq.IndexOf(n);
                    if (at < 0) continue;
                    if (placed.Count == 0 && seq == declared) break;
                    for (int i = at - 1; i >= 0 && !done; i--)
                    {
                        int k = placed.IndexOf(seq[i]);
                        if (k >= 0) { placed.Insert(k + 1, n); done = true; }
                    }
                    for (int i = at + 1; i < seq.Count && !done; i++)
                    {
                        int k = placed.IndexOf(seq[i]);
                        if (k >= 0) { placed.Insert(k, n); done = true; }
                    }
                    if (done) break;
                }
                if (!done)
                {
                    // nothing to stand next to: in declared order among those placed this way, unknown names last
                    int dn = declared.IndexOf(n), k = placed.Count;
                    if (dn >= 0 && best == null)
                        for (k = 0; k < placed.Count; k++) { int d = declared.IndexOf(placed[k]); if (d < 0 || d > dn) break; }
                    placed.Insert(k, n);
                }
            }
            var pos = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < placed.Count; i++) pos[placed[i]] = i;
            Stable.Sort(xe.Props, delegate (XProp x, XProp y) { return pos[x.Name].CompareTo(pos[y.Name]); });
        }

        static int PropOrder(string a, string b)
        {
            if (a == b) return 0;
            if (a == "Name") return 1;
            if (b == "Name") return -1;
            return string.CompareOrdinal(a.ToLowerInvariant(), b.ToLowerInvariant());
        }

        static string Indent(MapState ms, int depth)
        {
            string unit = ms.Dialect == XDialect.Tab ? "\t" : "  ", s = "";
            for (int i = 0; i < depth; i++) s += unit;
            return s;
        }

        static void FindItem(XmlElem entity, MapSet s, out XmlElem prop, out XmlElem item)
        {
            prop = null; item = null;
            foreach (XmlElem p in entity.Children)
                if (p.Name == "property" && p.Attr("name") == s.Name) { prop = p; break; }
            if (prop == null) return;
            foreach (XmlElem c in prop.Children)
                if (c.Name == "set" && c.Attr("index") == s.Index) { item = c; break; }
        }

        // new entities: by id, each before the first shipped entity with a larger id (the files are sorted by id)
        static void FlushAdded(MapState ms)
        {
            if (ms.Added.Count == 0) return;
            var list = new List<MapEnt>(ms.Added);
            Stable.Sort(list, delegate (MapEnt a, MapEnt b) { return string.CompareOrdinal(a.Id, b.Id); });
            string ind = Indent(ms, 2);
            foreach (MapEnt me in list)
            {
                XmlElem anchor = null;
                foreach (MapEnt o in ms.Shipped)
                    if (o.Id != null && string.CompareOrdinal(o.Id, me.Id) > 0) { anchor = o.Elem; break; }
                string text = XBlock.EntityText(me.New, ms.Dialect);
                if (anchor != null) ms.St.Ed.InsertBefore(anchor, text, ind);
                else ms.St.Ed.Append(ms.Set, text, ind);
            }
        }

        // ---- the server rows of a spawn point ----

        static void SpawnRows(Run run, Mod mod, MapOp mop, MapEntityOp op)
        {
            Plan plan = run.Plan;
            string what = "spawn point " + D(op.Number) + " of map " + mop.XBlock;
            if (op.Rows.Count == 0)
            {
                plan.Msg(plan.Warnings, mod, null, null, what + " has no <row>: the server gets no line that says what spawns there, so nothing will (mod.xml line " + op.Line + ")");
                return;
            }
            long map = mop.EffectiveMapId;
            if (map < 0) { plan.Msg(plan.Errors, mod, null, null, what + ": the map id is not known; give <map id=\"...\"> (mod.xml line " + mop.Line + ")"); return; }
            string code = map.ToString("D8", CultureInfo.InvariantCulture), point = D(op.Number);

            // The table ships in the server's and in each client's Xml archive: every copy gets the rows, so they stay equal.
            bool server = false;
            foreach (Profile p in run.Data.Profiles)
            {
                string ar = p.Root + ":" + SpawnTagArchiveName;
                if (!run.Data.Exists(ar))
                {
                    if (p.IsServer) plan.Msg(plan.Warnings, mod, ar, SpawnTagEntry, what + ": the server has no such archive, the spawn rows are not written");
                    continue;
                }
                if (p.IsServer) server = true;
                SpawnRowsOn(run, mod, op, ar, p, what, code, point);
            }
            if (!server && !run.Data.Has(Profile.Server)) plan.Msg(plan.Notes, mod, null, null, what + ": the server is not a target of this data set, its spawn rows are not written");
        }

        static void SpawnRowsOn(Run run, Mod mod, MapEntityOp op, string ar, Profile p, string what, string code, string point)
        {
            Plan plan = run.Plan;
            M2dArchive arch = run.Open(mod, ar);
            if (arch == null) return;
            EntryState st = run.State(arch, ar, SpawnTagEntry);
            if (st.Base == null || st.DeletedBy != null)
            {
                plan.Msg(p.IsServer ? plan.Warnings : plan.Notes, mod, ar, SpawnTagEntry, what + ": this target has no such table, the spawn rows are not written there");
                return;
            }
            if (!EnsureDoc(plan, mod, st)) return;
            XmlElem last = null;
            foreach (XmlElem r in st.Doc.Root.Children)
            {
                if (r.Name != "region" || (r.Attr("mapCode") ?? "").TrimStart('0') != code.TrimStart('0') || DeletedBy(st, r) != null) continue;
                if (r.Attr("spawnPointID") == point)
                {
                    plan.Msg(plan.Errors, mod, ar, SpawnTagEntry, "this target already has a row for " + what + " (line " + r.Line + "); nothing is added (mod.xml line " + op.Line + ")");
                    return;
                }
                last = r;
            }
            // after the map's last row, or at the end of the table when the map has no row yet; rows added at one
            // place keep the order of the mod
            string after = last == null ? null : RowPath.Canonical(last);
            for (int k = 0; k < op.Rows.Count; k++)
            {
                List<KeyValuePair<string, string>> row = op.Rows[k];
                var sb = new System.Text.StringBuilder("<region mapCode=\"" + code + "\"");
                bool put = false;
                foreach (KeyValuePair<string, string> a in row)
                {
                    // the shipped rows carry feature and locale between the map and the point
                    if (!put && a.Key != "feature" && a.Key != "locale") { sb.Append(" spawnPointID=\"" + point + "\""); put = true; }
                    sb.Append(' ').Append(a.Key).Append("=\"").Append(XmlDoc.EscapeAttr(a.Value, '"')).Append('"');
                }
                if (!put) sb.Append(" spawnPointID=\"" + point + "\"");
                sb.Append(" />");
                var ro = new RowOp { Kind = RowOpKind.Add, Xml = sb.ToString(), FromIndent = "", After = after, Sync = false, Grouped = true, NoNewId = true, Line = op.Line };
                try { DoAdd(run, mod, st, ro); }
                catch (ModKitException ex) { plan.Msg(plan.Errors, mod, ar, SpawnTagEntry, ex.Message + " (mod.xml line " + op.Line + ")"); return; }
            }
        }

        // ---- checks (warnings) ----

        static bool IsNumber(string v, bool whole, bool signed)
        {
            v = v.Trim();
            if (whole)
            {
                long n;
                return long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n) && (signed || n >= 0);
            }
            double d;
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }

        static bool FitsType(string type, string v)
        {
            switch (type)
            {
                case "Boolean": return v == "True" || v == "False";
                case "SInt32": case "SInt16": return IsNumber(v, true, true);
                case "UInt32": case "UInt16": return IsNumber(v, true, false);
                case "Float32": case "Float64": return IsNumber(v, false, true);
                case "Point3": case "Point2":
                    string[] parts = v.Split(',');
                    if (parts.Length != (type == "Point3" ? 3 : 2)) return false;
                    foreach (string p in parts) if (!IsNumber(p, false, true)) return false;
                    return true;
                default: return true;
            }
        }

        static void ModelChecks(Run run, MapRun mr, Mod mod, MapState ms, string model, List<XProp> props, MapOpKind kind, int line)
        {
            Plan plan = run.Plan;
            string at = " (" + ms.Target.Name + ", mod.xml line " + line + ")";
            foreach (XProp p in props)
                if (p.Name == "NpcList")
                    foreach (XSet s in p.Items)
                    {
                        long id;
                        if (s.Index == null || !long.TryParse(s.Index, NumberStyles.None, CultureInfo.InvariantCulture, out id)) continue;
                        Dictionary<long, bool> npcs = NpcIds(run, mr, ms.Target);
                        if (npcs != null && !npcs.ContainsKey(id))
                            plan.Msg(plan.Warnings, mod, ms.ArchiveRef, ms.St.Name, "NPC " + s.Index + " of the NPC list is not in the NPC data of this target" + at);
                    }
            if (ms.Flat == null || ms.Flat.Count == 0) return;
            FlatModel fm = ms.Flat.Get(model);
            if (fm == null)
            {
                if (kind != MapOpKind.Change) plan.Msg(plan.Warnings, mod, ms.ArchiveRef, ms.St.Name, "the model \"" + model + "\" is not in the model listing of this target: the game may not show the new entity" + at);
                return;
            }
            foreach (XProp p in props)
            {
                FlatProp fp = fm.Prop(p.Name);
                if (fp == null) { plan.Msg(plan.Warnings, mod, ms.ArchiveRef, ms.St.Name, "the model \"" + model + "\" has no property " + p.Name + at); continue; }
                string type = fp.Type ?? "";
                bool assoc = type.StartsWith("Assoc", StringComparison.Ordinal);
                string item = assoc ? type.Substring(5) : type;
                foreach (XSet s in p.Items)
                {
                    if (assoc != (s.Index != null))
                        plan.Msg(plan.Warnings, mod, ms.ArchiveRef, ms.St.Name, "the property " + p.Name + " of \"" + model + "\" is " + (assoc ? "a list (" + type + "): its values need index=\"...\"" : "a plain value (" + type + "): it takes no index") + at);
                    else if (!FitsType(item, s.Value))
                        plan.Msg(plan.Warnings, mod, ms.ArchiveRef, ms.St.Name, "the value \"" + s.Value + "\" of the property " + p.Name + " is not a " + type + at);
                }
            }
        }

        static long FileNumber(string entry)
        {
            int slash = entry.LastIndexOf('/'), dot = entry.LastIndexOf('.');
            long id;
            if (dot > slash && long.TryParse(entry.Substring(slash + 1, dot - slash - 1), NumberStyles.None, CultureInfo.InvariantCulture, out id)) return id;
            return -1;
        }

        static void ScanIds(M2dArchive arch, M2dEntry e, string open, Dictionary<long, bool> into)
        {
            string text = System.Text.Encoding.UTF8.GetString(arch.Read(e));
            for (int i = text.IndexOf(open, StringComparison.Ordinal); i >= 0; i = text.IndexOf(open, i + open.Length, StringComparison.Ordinal))
            {
                int s = i + open.Length, k = s;
                while (k < text.Length && text[k] >= '0' && text[k] <= '9') k++;
                long id;
                if (k > s && k < text.Length && text[k] == '"' && long.TryParse(text.Substring(s, k - s), NumberStyles.None, CultureInfo.InvariantCulture, out id)) into[id] = true;
            }
        }

        // the ids of a target's Xml.m2d: one file per id (folder/.../<id>.xml), or rows of bundle files; null = not known
        static Dictionary<long, bool> IdsOf(Run run, Profile target, string perIdFolder, string bundlePrefix, string bundleOpen)
        {
            string ar = target.Root + ":Xml.m2d";
            if (!run.Data.Exists(ar)) return null;
            M2dArchive arch;
            try { arch = run.Data.Open(ar); } catch (ModKitException) { return null; }
            var ids = new Dictionary<long, bool>();
            bool found = false;
            foreach (M2dEntry e in arch.Entries)
            {
                string n = e.Name.ToLowerInvariant();
                if (!n.EndsWith(".xml", StringComparison.Ordinal)) continue;
                if (n.StartsWith(perIdFolder, StringComparison.Ordinal)) { long id = FileNumber(n); if (id >= 0) { ids[id] = true; found = true; } }
                else if (n.StartsWith(bundlePrefix, StringComparison.Ordinal)) { ScanIds(arch, e, bundleOpen, ids); found = true; }
            }
            foreach (EntryState st in run.Order)        // entries a mod of this plan adds
                if (st.Archive == ar && st.Base != null && st.Name.ToLowerInvariant().StartsWith(perIdFolder, StringComparison.Ordinal)) { long id = FileNumber(st.Name); if (id >= 0) ids[id] = true; }
            return found ? ids : null;
        }

        static Dictionary<long, bool> NpcIds(Run run, MapRun mr, Profile target)
        {
            Dictionary<long, bool> ids;
            if (!mr.Npcs.TryGetValue(target.Root, out ids)) mr.Npcs[target.Root] = ids = IdsOf(run, target, "npc/", "npcdata/", "<npc id=\"");
            return ids;
        }

        static Dictionary<long, bool> MapIdsOf(Run run, MapRun mr, Profile target)
        {
            Dictionary<long, bool> ids;
            if (!mr.MapIds.TryGetValue(target.Root, out ids)) mr.MapIds[target.Root] = ids = IdsOf(run, target, "map/", "table/fielddata.xml", "<fieldData id=\"");
            return ids;
        }

        // the portals of every map file of a map id on one target, after the plan
        static List<MapEnt> PortalsOf(Run run, MapRun mr, MapState from, long mapId)
        {
            string prefix = "xblock/" + mapId.ToString("D8", CultureInfo.InvariantCulture);
            var res = new List<MapEnt>();
            M2dArchive arch = run.Data.Open(from.ArchiveRef);
            foreach (M2dEntry e in arch.Entries)
            {
                string n = e.Name.ToLowerInvariant();
                if (!n.StartsWith(prefix, StringComparison.Ordinal) || !n.EndsWith(".xblock", StringComparison.Ordinal)) continue;
                char next = n[prefix.Length];
                if (next >= '0' && next <= '9') continue;
                MapState planned = null;
                foreach (MapState ms in mr.Order) if (ms.ArchiveRef == from.ArchiveRef && ms.St.Name.ToLowerInvariant() == n) planned = ms;
                if (planned != null) { foreach (MapEnt me in AllEnts(planned)) if (!me.Removed && me.Portal >= 0) res.Add(me); continue; }
                string key = from.ArchiveRef + "\n" + n;
                List<MapEnt> list;
                if (!mr.Others.TryGetValue(key, out list))
                {
                    list = new List<MapEnt>();
                    try
                    {
                        XmlElem set = EntitySet(XmlDoc.Parse(arch.Read(e), from.ArchiveRef + " " + e.Name));
                        if (set != null)
                            foreach (XmlElem x in set.Children)
                                if (x.Name == "entity") { MapEnt me = ReadEnt(x, from.Flat); if (me.Portal >= 0) list.Add(me); }
                    }
                    catch (ModKitException) { }
                    mr.Others[key] = list;
                }
                res.AddRange(list);
            }
            return res;
        }

        static void PortalChecks(Run run, MapRun mr, MapState ms)
        {
            Plan plan = run.Plan;
            foreach (MapEnt me in ms.Added)
            {
                if (me.Op.Kind != MapOpKind.Portal) continue;
                Mod mod = me.AddedBy;
                string what = "portal " + D(me.Portal) + " leads to portal " + D(me.ToPortal) + " of map " + D(me.ToMap);
                string at = " (" + ms.Target.Name + ", mod.xml line " + me.Op.Line + ")";
                Dictionary<long, bool> maps = MapIdsOf(run, mr, ms.Target);
                if (maps != null && !maps.ContainsKey(me.ToMap))
                {
                    plan.Msg(plan.Warnings, mod, ms.ArchiveRef, ms.St.Name, what + ", and this target's map table has no map " + D(me.ToMap) + at);
                    continue;
                }
                List<MapEnt> there = PortalsOf(run, mr, ms, me.ToMap);
                bool target = false, back = false;
                foreach (MapEnt o in there)
                {
                    if (o.Portal == me.ToPortal) target = true;
                    if (o.ToMap == ms.MapId) back = true;
                }
                if (!target) plan.Msg(plan.Warnings, mod, ms.ArchiveRef, ms.St.Name, what + ", and that map has no such portal after this plan" + at);
                if (!back && ms.MapId >= 0) plan.Msg(plan.Warnings, mod, ms.ArchiveRef, ms.St.Name, what + ", and no portal of that map leads back to map " + D(ms.MapId) + at);
            }
        }
    }
}
