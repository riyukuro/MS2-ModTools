// The difference between a shipped map file and an edited one, as the map ops of a mod (MapOps.cs).
//
// An entity only in the shipped file becomes <remove>, one only in the edited file <add> (or <spawn>, <portal> when it
// carries those numbers) with its own id and property order, one in both with other values <change> with the shipped
// values as the expected old ones. Check then plans the ops on the shipped file and compares bytes: what the ops
// cannot say (another model, name or iterations of an entity, properties in another order, another layout) shows there.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MS2.ModKit.M2d;
using MS2.ModKit.MapEdit;

namespace MS2.ModKit
{
    public static class MapDiff
    {
        static bool Num32(XEntity e, string prop, out long v)
        {
            v = -1;
            XProp p = e.Find(prop);
            return p != null && p.Scalar && long.TryParse(p.Items[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out v) && v <= int.MaxValue
                && v.ToString(CultureInfo.InvariantCulture) == p.Items[0].Value;
        }

        static string Position(XEntity e)
        {
            XProp p = e.Find("Position");
            return p != null && p.Scalar ? p.Items[0].Value : "";
        }

        static bool Item(XProp p, string index, out string value)
        {
            value = null;
            if (p != null) foreach (XSet s in p.Items) if (s.Index == index) { value = s.Value; return true; }
            return false;
        }

        /// <summary>
        /// The ops that turn the shipped map into the edited one; null when the two hold the same entities and values.
        /// problems gets what the ops cannot say.
        /// </summary>
        public static MapOp Build(string xblock, byte[] shipped, byte[] edited, List<string> problems)
        {
            List<XEntity> a = XBlock.Read(shipped, xblock + " (shipped)"), b = XBlock.Read(edited, xblock + " (edited)");
            var inB = new Dictionary<string, XEntity>(StringComparer.Ordinal);
            foreach (XEntity e in b)
            {
                if (inB.ContainsKey(e.Id)) problems.Add("the edited map has the entity id " + e.Id + " twice");
                inB[e.Id] = e;
            }
            var inA = new Dictionary<string, XEntity>(StringComparer.Ordinal);
            var map = new MapOp { XBlock = xblock };
            foreach (XEntity e in a)
            {
                inA[e.Id] = e;
                XEntity n;
                if (!inB.TryGetValue(e.Id, out n))
                {
                    map.Ops.Add(new MapEntityOp { Kind = MapOpKind.Remove, Id = e.Id, Model = e.Model, Position = Position(e) });
                    continue;
                }
                if (n.Model != e.Model || n.Name != e.Name || n.Iterations != e.Iterations)
                {
                    problems.Add("entity " + e.Id + " has another model, name or iterations value (\"" + e.Model + "\" \"" + e.Name + "\" " + e.Iterations + " became \"" + n.Model + "\" \"" + n.Name + "\" " + n.Iterations + "); a mod cannot say that");
                    continue;
                }
                var op = new MapEntityOp { Kind = MapOpKind.Change, Id = e.Id, Model = e.Model, Position = Position(e) };
                foreach (XProp p in e.Props)
                {
                    XProp q = n.Find(p.Name);
                    foreach (XSet s in p.Items)
                    {
                        string now;
                        if (!Item(q, s.Index, out now)) op.Sets.Add(new MapSet { Name = p.Name, Index = s.Index, Old = s.Value });
                        else if (now != s.Value) op.Sets.Add(new MapSet { Name = p.Name, Index = s.Index, Old = s.Value, Value = now });
                    }
                }
                foreach (XProp q in n.Props)
                {
                    XProp p = e.Find(q.Name);
                    string was;
                    foreach (XSet t in q.Items)
                        if (!Item(p, t.Index, out was)) op.Sets.Add(new MapSet { Name = q.Name, Index = t.Index, Value = t.Value });
                }
                if (op.Sets.Count > 0) map.Ops.Add(op);
            }
            var names = new Dictionary<string, bool>();
            foreach (XEntity e in b)
            {
                if (inA.ContainsKey(e.Id)) continue;
                var op = new MapEntityOp { Kind = MapOpKind.Add, Model = e.Model, EntityName = e.Name, Iterations = e.Iterations, NewId = e.Id, Order = new List<string>() };
                op.Name = "new-" + e.Id.Substring(0, Math.Min(8, e.Id.Length));
                if (names.ContainsKey(op.Name)) op.Name = "new-" + e.Id;
                names[op.Name] = true;
                long point, portal, toMap, toPortal;
                if (Num32(e, "PortalID", out portal) && Num32(e, "TargetFieldSN", out toMap) && Num32(e, "TargetPortalID", out toPortal))
                {
                    op.Kind = MapOpKind.Portal; op.Number = portal; op.ToMap = toMap; op.ToPortal = toPortal;
                }
                else if (Num32(e, "SpawnPointID", out point)) { op.Kind = MapOpKind.Spawn; op.Number = point; }
                foreach (XProp p in e.Props)
                {
                    op.Order.Add(p.Name);
                    if (op.Kind == MapOpKind.Spawn && p.Name == "SpawnPointID") continue;
                    if (op.Kind == MapOpKind.Portal && (p.Name == "PortalID" || p.Name == "TargetFieldSN" || p.Name == "TargetPortalID")) continue;
                    op.Props.Add(new XProp(p.Name, new List<XSet>(p.Items)));
                }
                if (e.Iterations < 1) problems.Add("the new entity " + e.Id + " has iterations=\"" + e.Iterations + "\"; a mod cannot say that");
                foreach (string n in op.Order) if (n.IndexOf(',') >= 0) problems.Add("the new entity " + e.Id + " has a property name with a comma (" + n + "); a mod cannot say that");
                map.Ops.Add(op);
            }
            return map.Ops.Count == 0 ? null : map;
        }

        /// <summary>
        /// Plans the mod on a throw-away archive that holds only the shipped map and compares the planned file with the
        /// edited one. null = the same bytes; else what is wrong.
        /// </summary>
        public static string Check(Mod mod, string xblock, byte[] shipped, byte[] edited)
        {
            string dir = Path.Combine(Path.GetTempPath(), "modkit-mapdiff-" + Guid.NewGuid().ToString("N"));
            string entry = "xblock/" + xblock + ".xblock";
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "Resource"));
                M2dWriter.Create(Path.Combine(Path.Combine(dir, "Resource"), "Exported.m2d"), PackVersion.NS2F, M2dFlag.AesZlib,
                    new List<KeyValuePair<string, byte[]>> { new KeyValuePair<string, byte[]>(entry, shipped) });
                byte[] got = null;
                using (var data = new DataSet(dir, null))
                {
                    var only = new Mod { Id = mod.Id, Name = mod.Name, Version = mod.Version };
                    foreach (MapOp m in mod.Maps) if (m.XBlock == xblock) only.Maps.Add(m);
                    if (only.Maps.Count == 0) return Same(shipped, edited) ? null : "the mod has no ops for this map, and the two files differ";
                    Plan plan = Planner.Build(data, new[] { only }, null);
                    if (plan.HasErrors) return "the ops do not plan: " + plan.Errors[0];
                    foreach (PlanArchive pa in plan.Archives)
                        foreach (KeyValuePair<string, byte[]> kv in pa.Put)
                            if (string.Equals(kv.Key, entry, StringComparison.OrdinalIgnoreCase)) got = kv.Value;
                    foreach (PlanMessage w in plan.Warnings)
                        if (w.ToString().IndexOf("is skipped on", StringComparison.Ordinal) >= 0) return "an op is skipped: " + w;
                }
                if (got == null) return "the ops change nothing in the shipped map";
                if (Same(got, edited)) return null;
                return "the shipped map with the ops applied is not the edited map, byte for byte (first difference at byte " + FirstDiff(got, edited)
                    + "). The ops keep entities in id order and add a property to a shipped entity at its place by name; an edited map in another order or layout cannot be said as ops (modkit map-normalize --original puts an exported map in the shipped form)";
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        static bool Same(byte[] x, byte[] y) { return x.Length == y.Length && FirstDiff(x, y) == x.Length; }

        static int FirstDiff(byte[] x, byte[] y)
        {
            int n = Math.Min(x.Length, y.Length);
            for (int i = 0; i < n; i++) if (x[i] != y[i]) return i;
            return n;
        }
    }
}
