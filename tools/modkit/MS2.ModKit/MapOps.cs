// Map ops of a mod: changes of a shipped map as a list of entity operations, never as a whole file.
//
//   <map xblock="90000001_ab" [id="90000001"]>
//     <add name="crate-1" model="Crate_A" entity-name="Crate_A_900">
//       <property name="Position" value="150, 300, 450" />
//       <property name="Tags"><set index="0" value="a" /><set index="1" value="b" /></property>
//     </add>
//     <remove id="(32 hex)" model="Crate_A" position="0, 0, 150" />
//     <change id="(32 hex)" model="Crate_A" position="0, 0, 150">
//       <set name="Position" old="0, 0, 150" value="0, 0, 300" />      old left out = the map stores no value
//       <set name="Tags" index="1" old="b" value="c" />
//       <unset name="Rotation" old="0, 0, 90" />
//     </change>
//     <spawn name="wolves" model="SpawnPoint_A" entity-name="Spawn_900" point="140000">
//       <property name="Position" value="150, 300, 450" />
//       <row tag="wolf" population="3" coolTime="20" />                 the server row of the spawn point, one or more
//     </spawn>
//     <portal name="east" model="Portal_A" entity-name="Portal_900" id="50000" to-map="90000002" to-portal="50000">
//       <property name="Position" value="150, 300, 450" />
//     </portal>
//   </map>
//
// name= of add, spawn and portal is the operation's own name; the id of the new entity is derived from the mod id,
// the xblock name and that name (MapValues.NewEntityId). How the ops are planned is in PlannerMaps.cs.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MS2.ModKit.MapEdit;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public enum MapOpKind { Add, Remove, Change, Spawn, Portal }

    /// <summary>One property change of a change op.</summary>
    public sealed class MapSet
    {
        public string Name;
        /// <summary>null: the plain value of the property.</summary>
        public string Index;
        /// <summary>The value expected in the map now; null = the map stores none.</summary>
        public string Old;
        /// <summary>The new value; null = the stored value is removed (the model's default counts again).</summary>
        public string Value;
        public int Line;
    }

    public sealed class MapEntityOp
    {
        public MapOpKind Kind;
        /// <summary>Add, Spawn, Portal: the op's own name inside the mod.</summary>
        public string Name;
        /// <summary>Remove, Change: the entity id (32 hex digits).</summary>
        public string Id;
        public string Model;
        /// <summary>Add, Spawn, Portal: the display name of the new entity.</summary>
        public string EntityName;
        /// <summary>Remove, Change: the position expected in the map now ("" = none stored).</summary>
        public string Position;
        public readonly List<XProp> Props = new List<XProp>();
        public readonly List<MapSet> Sets = new List<MapSet>();
        /// <summary>Spawn: the SpawnPointID. Portal: the PortalID.</summary>
        public long Number;
        public long ToMap, ToPortal;
        /// <summary>Spawn: the attributes of each server row (map and spawn point are added by the tool).</summary>
        public readonly List<List<KeyValuePair<string, string>>> Rows = new List<List<KeyValuePair<string, string>>>();
        /// <summary>Add, Spawn, Portal: the iterations attribute of the new entity.</summary>
        public int Iterations = 1;
        public int Line;
    }

    public sealed class MapOp
    {
        /// <summary>The map file name without folder and extension.</summary>
        public string XBlock;
        /// <summary>The map id (for the server rows of a spawn); -1 = the leading digits of XBlock.</summary>
        public long MapId = -1;
        public readonly List<MapEntityOp> Ops = new List<MapEntityOp>();
        public int Line;

        public long EffectiveMapId
        {
            get
            {
                if (MapId >= 0) return MapId;
                int n = 0;
                while (n < XBlock.Length && XBlock[n] >= '0' && XBlock[n] <= '9') n++;
                long v;
                return n > 0 && long.TryParse(XBlock.Substring(0, n), NumberStyles.None, CultureInfo.InvariantCulture, out v) ? v : -1;
            }
        }
    }

    public sealed partial class Mod
    {
        /// <summary>Map ops (MapOps.cs).</summary>
        public readonly List<MapOp> Maps = new List<MapOp>();

        sealed partial class Reader
        {
            static bool IsEntityId(string s)
            {
                if (s == null || s.Length != 32) return false;
                foreach (char c in s) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
                return true;
            }

            // the client reads these as 32-bit whole numbers
            long Num32(XmlElem e, string name)
            {
                long v = Num(e, name);
                if (v > int.MaxValue) throw Bad(e, name + "=\"" + e.Attr(name) + "\" is too large: the game reads it as a 32-bit number (at most " + int.MaxValue.ToString(CultureInfo.InvariantCulture) + ")");
                return v;
            }

            internal MapOp ReadMap(XmlElem e)
            {
                Only(e, "xblock", "id");
                var m = new MapOp { XBlock = Need(e, "xblock"), Line = e.Line };
                if (m.XBlock.IndexOfAny(new[] { '/', '\\', '.' }) >= 0) throw Bad(e, "xblock=\"" + m.XBlock + "\" is the map file name without folder and extension");
                if (e.Attr("id") != null) m.MapId = Num32(e, "id");
                var names = new Dictionary<string, bool>();
                foreach (XmlElem c in e.Children)
                {
                    var op = new MapEntityOp { Line = c.Line };
                    switch (c.Name)
                    {
                        case "add": Only(c, "name", "model", "entity-name", "iterations"); op.Kind = MapOpKind.Add; break;
                        case "spawn":
                            Only(c, "name", "model", "entity-name", "iterations", "point"); op.Kind = MapOpKind.Spawn; op.Number = Num32(c, "point");
                            break;
                        case "portal":
                            Only(c, "name", "model", "entity-name", "iterations", "id", "to-map", "to-portal"); op.Kind = MapOpKind.Portal;
                            op.Number = Num32(c, "id"); op.ToMap = Num32(c, "to-map"); op.ToPortal = Num32(c, "to-portal");
                            break;
                        case "remove": Only(c, "id", "model", "position"); op.Kind = MapOpKind.Remove; break;
                        case "change": Only(c, "id", "model", "position"); op.Kind = MapOpKind.Change; break;
                        default: throw Bad(c, "unknown element <" + c.Name + "> in <map> (known: add, remove, change, spawn, portal)");
                    }
                    op.Model = Need(c, "model");
                    if (op.Kind == MapOpKind.Remove || op.Kind == MapOpKind.Change)
                    {
                        op.Id = Need(c, "id").ToLowerInvariant();
                        if (!IsEntityId(op.Id)) throw Bad(c, "id=\"" + c.Attr("id") + "\" is not an entity id (32 hex digits)");
                        op.Position = c.Attr("position");
                        if (op.Position == null) throw Bad(c, "<" + c.Name + "> needs position=\"...\", the position the entity has now (\"\" when the map stores none)");
                        if (op.Kind == MapOpKind.Remove && c.Children.Count > 0) throw Bad(c, "<remove> has no child elements");
                        foreach (XmlElem s in c.Children)
                        {
                            var ms = new MapSet { Line = s.Line };
                            if (s.Name == "set") { Only(s, "name", "index", "old", "value"); ms.Value = s.Attr("value"); if (ms.Value == null) throw Bad(s, "<set> needs value=\"...\" (it may be empty)"); }
                            else if (s.Name == "unset") { Only(s, "name", "index", "old"); if (s.Attr("old") == null) throw Bad(s, "<unset> needs old=\"...\", the value the map stores now"); }
                            else throw Bad(s, "unknown element <" + s.Name + "> in <change> (known: set, unset)");
                            ms.Name = Need(s, "name"); ms.Index = s.Attr("index"); ms.Old = s.Attr("old");
                            op.Sets.Add(ms);
                        }
                        if (op.Kind == MapOpKind.Change && op.Sets.Count == 0) throw Bad(c, "<change> changes nothing");
                    }
                    else
                    {
                        op.Name = Need(c, "name");
                        if (names.ContainsKey(op.Name)) throw Bad(c, "the name \"" + op.Name + "\" is used twice in this <map>; the id of the new entity comes from it");
                        names[op.Name] = true;
                        op.EntityName = Need(c, "entity-name");
                        if (c.Attr("iterations") != null)
                        {
                            op.Iterations = (int)Num32(c, "iterations");
                            if (op.Iterations < 1) throw Bad(c, "iterations=\"" + c.Attr("iterations") + "\" counts from 1");
                        }
                        foreach (XmlElem p in c.Children)
                        {
                            if (p.Name == "row" && op.Kind == MapOpKind.Spawn)
                            {
                                if (p.Children.Count > 0) throw Bad(p, "<row> has no child elements");
                                var row = new List<KeyValuePair<string, string>>();
                                foreach (XmlAttr a in p.Attrs)
                                {
                                    if (a.Name == "mapCode" || a.Name == "spawnPointID") throw Bad(p, "<row> does not take " + a.Name + "; the tool writes it from the map and point=\"...\"");
                                    row.Add(new KeyValuePair<string, string>(a.Name, a.Value));
                                }
                                op.Rows.Add(row);
                                continue;
                            }
                            if (p.Name != "property") throw Bad(p, "unknown element <" + p.Name + "> in <" + c.Name + "> (known: property" + (op.Kind == MapOpKind.Spawn ? ", row" : "") + ")");
                            Only(p, "name", "value");
                            var xp = new XProp(Need(p, "name"));
                            if (op.Props.Exists(delegate (XProp q) { return q.Name == xp.Name; })) throw Bad(p, "the property " + xp.Name + " is given twice");
                            if (p.Attr("value") != null)
                            {
                                if (p.Children.Count > 0) throw Bad(p, "<property> takes value=\"...\" or <set> children, not both");
                                xp.Items.Add(new XSet(null, p.Attr("value")));
                            }
                            foreach (XmlElem s in p.Children)
                            {
                                if (s.Name != "set") throw Bad(s, "unknown element <" + s.Name + "> in <property> (known: set)");
                                Only(s, "index", "value");
                                if (s.Attr("value") == null) throw Bad(s, "<set> needs value=\"...\"");
                                xp.Items.Add(new XSet(s.Attr("index"), s.Attr("value")));
                            }
                            op.Props.Add(xp);
                        }
                        string own = op.Kind == MapOpKind.Spawn ? "SpawnPointID" : op.Kind == MapOpKind.Portal ? "PortalID, TargetFieldSN, TargetPortalID" : null;
                        if (own != null)
                            foreach (XProp q in op.Props)
                                if (Array.IndexOf(own.Split(new[] { ", " }, StringSplitOptions.None), q.Name) >= 0)
                                    throw Bad(c, "<" + c.Name + "> writes the property " + q.Name + " itself; leave it out");
                    }
                    m.Ops.Add(op);
                }
                if (m.Ops.Count == 0) throw Bad(e, "<map> changes nothing");
                return m;
            }
        }

        void WriteMaps(StringBuilder sb)
        {
            foreach (MapOp m in Maps)
            {
                sb.Append("  <map"); A(sb, "xblock", m.XBlock);
                if (m.MapId >= 0) A(sb, "id", m.MapId.ToString(CultureInfo.InvariantCulture));
                sb.Append(">\n");
                foreach (MapEntityOp op in m.Ops)
                {
                    string tag = op.Kind.ToString().ToLowerInvariant();
                    sb.Append("    <").Append(tag);
                    if (op.Kind == MapOpKind.Remove || op.Kind == MapOpKind.Change)
                    {
                        A(sb, "id", op.Id); A(sb, "model", op.Model); A(sb, "position", op.Position);
                        if (op.Sets.Count == 0) { sb.Append(" />\n"); continue; }
                        sb.Append(">\n");
                        foreach (MapSet s in op.Sets)
                        {
                            sb.Append("      <").Append(s.Value == null ? "unset" : "set");
                            A(sb, "name", s.Name); A(sb, "index", s.Index); A(sb, "old", s.Old); A(sb, "value", s.Value);
                            sb.Append(" />\n");
                        }
                    }
                    else
                    {
                        A(sb, "name", op.Name); A(sb, "model", op.Model); A(sb, "entity-name", op.EntityName);
                        if (op.Iterations != 1) A(sb, "iterations", op.Iterations.ToString(CultureInfo.InvariantCulture));
                        if (op.Kind == MapOpKind.Spawn) A(sb, "point", op.Number.ToString(CultureInfo.InvariantCulture));
                        if (op.Kind == MapOpKind.Portal)
                        {
                            A(sb, "id", op.Number.ToString(CultureInfo.InvariantCulture));
                            A(sb, "to-map", op.ToMap.ToString(CultureInfo.InvariantCulture));
                            A(sb, "to-portal", op.ToPortal.ToString(CultureInfo.InvariantCulture));
                        }
                        sb.Append(">\n");
                        foreach (XProp p in op.Props)
                        {
                            sb.Append("      <property"); A(sb, "name", p.Name);
                            if (p.Scalar) { A(sb, "value", p.Items[0].Value); sb.Append(" />\n"); continue; }
                            sb.Append(">\n");
                            foreach (XSet s in p.Items) { sb.Append("        <set"); A(sb, "index", s.Index); A(sb, "value", s.Value); sb.Append(" />\n"); }
                            sb.Append("      </property>\n");
                        }
                        foreach (List<KeyValuePair<string, string>> row in op.Rows)
                        {
                            sb.Append("      <row");
                            foreach (KeyValuePair<string, string> a in row) A(sb, a.Key, a.Value);
                            sb.Append(" />\n");
                        }
                    }
                    sb.Append("    </").Append(tag).Append(">\n");
                }
                sb.Append("  </map>\n");
            }
        }
    }
}
