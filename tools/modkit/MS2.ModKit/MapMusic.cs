// A map's music as a mod edit: the cue a map plays (the <bgm> element's id) and the one it plays in a boss fight
// (boss), on every side at once. The kms client keeps the element in its table/fielddata.xml row (fieldData[id=N],
// one per environment), the server and the global client in map/<8-digit id>.xml (one per environment too). The
// shipped data never gives a map's environments different music, so one pair of cues per map is edited and written
// into each environment's element.
//
// The edit is written the way a mod file has it: a <change> of the row with set attributes at environment/bgm, on
// the kms client and on the server (or on the global client when the data set has no server); the mirror plans the
// server's line for the global client too (PlannerMirror.cs). An environment with no <bgm> element gets one, as an
// <add> into it. Setting the shipped value back takes the edit out again. Plan, Apply and Revert are the usual ones.
//
// The cue is a number of the event project's cue list (Sound.cs); 0 is no music. Whether the number is in the
// list is the caller's check (the view warns), as the project file may not be there.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>One environment of one side of a map: where its &lt;bgm&gt; element is, and what it ships.</summary>
    public sealed class MusicPlace
    {
        public string Archive;      // client:Xml.m2d, server:Xml.m2d, gms-client:Xml.m2d
        public string Entry;        // table/fielddata.xml or map/<id>.xml
        public string Row;          // the row op's selector: fieldData[id=N], or "" for the file's root
        public string Env;          // the environment under the row: environment, environment[feature=X]
        public string Feature = "", Locale = "";
        public bool HasBgm;         // false: the environment has no <bgm> element (an edit adds one)
        public string Id = "", Boss = "";       // as shipped ("" = none)
        /// <summary>The kms client or the side the edit is written on (the global client's place is shown only).</summary>
        public bool Written;
    }

    public sealed class MapMusic
    {
        public const string BgmAttr = "id", BossAttr = "boss";
        public readonly DataSet Data;
        public readonly Mod Mod;
        XmlDoc fieldData;       // the kms client's table, read once

        public MapMusic(DataSet data, Mod mod) { Data = data; Mod = mod; }

        /// <summary>The archive the per-id edit is written on: the server, else the global client (null: neither).</summary>
        public string PerIdSide
        {
            get
            {
                if (Data.Exists(EntityEditor.ServerXml)) return EntityEditor.ServerXml;
                return Data.Exists(EntityEditor.GmsXml) ? EntityEditor.GmsXml : null;
            }
        }

        /// <summary>Every environment of the map on every side, as shipped (or as a file the mod replaces whole has it).</summary>
        public List<MusicPlace> Places(long map)
        {
            var l = new List<MusicPlace>();
            string perId = PerIdSide;
            if (Data.Exists(EntityEditor.ClientXml))
            {
                XmlDoc d = FieldData();
                string err;
                XmlElem row = d == null || d.Root == null ? null : RowPath.Parse("fieldData[id=" + Inv(map) + "]").TryResolve(d.Root, out err);
                if (row != null) Add(l, EntityEditor.ClientXml, FieldRows.Entry, row, true);
            }
            foreach (string archive in new[] { EntityEditor.ServerXml, EntityEditor.GmsXml })
            {
                if (!Data.Exists(archive)) continue;
                byte[] b = Current(archive, FieldRows.FileOf((int)map));
                if (b == null) continue;
                XmlDoc d = XmlDoc.Parse(b, archive + " " + FieldRows.FileOf((int)map));
                if (d.Root != null) Add(l, archive, FieldRows.FileOf((int)map), d.Root, archive == perId);
            }
            return l;
        }

        static void Add(List<MusicPlace> l, string archive, string entry, XmlElem row, bool written)
        {
            string rowAt = RowPath.Canonical(row);
            foreach (XmlElem env in row.Children)
            {
                if (env.Name != "environment") continue;
                string at = RowPath.Canonical(env);
                var p = new MusicPlace
                {
                    Archive = archive, Entry = entry, Row = rowAt, Written = written,
                    Env = rowAt.Length == 0 ? at : at.Substring(rowAt.Length + 1),
                    Feature = env.Attr("feature") ?? "", Locale = env.Attr("locale") ?? "",
                };
                foreach (XmlElem c in env.Children)
                {
                    if (c.Name != "bgm") continue;
                    p.HasBgm = true;
                    p.Id = (c.Attr(BgmAttr) ?? "").Trim();
                    p.Boss = (c.Attr(BossAttr) ?? "").Trim();
                    break;
                }
                l.Add(p);
            }
        }

        XmlDoc FieldData()
        {
            if (fieldData == null)
            {
                byte[] b = Current(EntityEditor.ClientXml, FieldRows.Entry);
                if (b != null) fieldData = XmlDoc.Parse(b, EntityEditor.ClientXml + " " + FieldRows.Entry);
            }
            return fieldData;
        }

        byte[] Current(string archive, string entry)
        {
            EntryOp op = new TriggerEditor(Data, Mod).OpOf(archive, entry);
            if (op != null) return new TriggerEditor(Data, Mod).ModBytes(op);
            M2dArchive a = Data.Open(archive);
            M2dEntry e = a.Find(entry);
            return e == null ? null : a.Read(e);
        }

        // ---- the maps the mod edits, for the cue lists ----

        static readonly System.Text.RegularExpressions.Regex RowId = new System.Text.RegularExpressions.Regex("^fieldData\\[id=([0-9]+)\\]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        static readonly System.Text.RegularExpressions.Regex MapFileId = new System.Text.RegularExpressions.Regex("^map/([0-9]+)\\.xml$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>The maps whose fielddata row or map file the mod edits (any line, not only music), in mod order.</summary>
        public List<long> EditedMaps()
        {
            var l = new List<long>();
            foreach (ModOp o in Mod.Ops)
            {
                var ed = o as EditOp;
                if (ed == null) continue;
                string entry = (ed.Entry ?? "").Replace('\\', '/');
                if (SameArchive(ed.Archive, EntityEditor.ClientXml) && string.Equals(entry, FieldRows.Entry, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (RowOp r in ed.Rows)
                    {
                        System.Text.RegularExpressions.Match m = RowId.Match(r.Kind == RowOpKind.Add ? r.In ?? "" : r.At ?? "");
                        if (m.Success) AddOnce(l, long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
                    }
                    continue;
                }
                if (!SameArchive(ed.Archive, EntityEditor.ServerXml) && !SameArchive(ed.Archive, EntityEditor.GmsXml)) continue;
                System.Text.RegularExpressions.Match f = MapFileId.Match(entry);
                if (f.Success) AddOnce(l, long.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture));
            }
            return l;
        }

        static void AddOnce(List<long> l, long v) { if (!l.Contains(v)) l.Add(v); }

        /// <summary>
        /// Puts the mod's music of the maps it edits into a list of bgm rows read from the shipped files (or the ones the
        /// mod replaces whole): their rows go and each side's rows as the mod leaves them come in. The global client
        /// shows the server's cues when the edit is mirrored. The param and value stay the shipped ones.
        /// </summary>
        public void Overlay(List<CueUse> uses)
        {
            string perId = PerIdSide;
            foreach (long map in EditedMaps()) Overlay(uses, map, perId);
        }

        /// <summary>Puts one map's music as the mod leaves it into the list (after an edit of that map alone).</summary>
        public void Overlay(List<CueUse> uses, long map) { Overlay(uses, map, PerIdSide); }

        void Overlay(List<CueUse> uses, long map, string perId)
        {
            List<MusicPlace> places = Places(map);
            if (places.Count == 0) return;
            List<CueUse> old = uses.FindAll(u => u.Map == map);
            uses.RemoveAll(u => u.Map == map);
            foreach (MusicPlace p in places)
            {
                MusicPlace from = Shown(p, places, perId);
                string env = SoundData.EnvLabel(p.Feature, p.Locale);
                foreach (bool boss in new[] { false, true })
                {
                    int cue;
                    if (!int.TryParse(Get(from, boss), NumberStyles.None, CultureInfo.InvariantCulture, out cue) || cue == 0) continue;
                    if (uses.Exists(u => u.Map == map && u.Boss == boss && u.Cue == cue && u.Side == p.Archive && u.Env == env)) continue;
                    CueUse was = old.Find(u => u.Boss == boss && u.Side == p.Archive && u.Env == env) ?? old.Find(u => u.Boss == boss && u.Side == p.Archive);
                    uses.Add(new CueUse { Cue = cue, Map = map, Boss = boss, Side = p.Archive, Env = env, Param = was != null ? was.Param : "", Value = was != null ? was.Value : "" });
                }
            }
        }

        /// <summary>The place whose values a place gets with the mod: itself, or for the global client's map file the
        /// server's environment of the same selector (the mirror carries the server's edit there).</summary>
        public static MusicPlace Shown(MusicPlace p, List<MusicPlace> places, string perId)
        {
            if (p.Written || p.Archive != EntityEditor.GmsXml || perId != EntityEditor.ServerXml) return p;
            return places.Find(x => x.Archive == perId && x.Env == p.Env) ?? places.Find(x => x.Archive == perId) ?? p;
        }

        // ---- the mod's edit ----

        /// <summary>The cue the place plays with the mod ("" = none): the mod's value, else the shipped one.</summary>
        public string Get(MusicPlace p, bool boss)
        {
            string attr = boss ? BossAttr : BgmAttr;
            if (p.HasBgm)
            {
                RowOp op = Change(p, false);
                if (op != null)
                    for (int i = op.Attrs.Count - 1; i >= 0; i--)
                        if (op.Attrs[i].Name == attr && op.Attrs[i].At == BgmAt(p))
                            return op.Attrs[i].Kind == AttrOpKind.Set ? op.Attrs[i].Value ?? "" : "";
                return boss ? p.Boss : p.Id;
            }
            RowOp add = Added(p, false);
            if (add == null) return "";
            XmlDoc d = XmlDoc.Parse(Encoding.UTF8.GetBytes(add.Xml), "mod bgm");
            return d.Root == null ? "" : (d.Root.Attr(attr) ?? "");
        }

        /// <summary>Whether the mod changes the map's music on any written place.</summary>
        public bool IsEdited(long map)
        {
            foreach (MusicPlace p in Places(map))
                if (p.Written && (Get(p, false) != p.Id || Get(p, true) != p.Boss)) return true;
            return false;
        }

        /// <summary>
        /// Sets the map's cue (boss: the boss fight's) on every written place: "" or null takes the attribute out (no
        /// music), the shipped value takes the mod's line out. Returns the places whose line changed.
        /// </summary>
        public int Set(long map, bool boss, string cue)
        {
            cue = (cue ?? "").Trim();
            int n;
            if (cue.Length > 0 && !int.TryParse(cue, NumberStyles.None, CultureInfo.InvariantCulture, out n))
                throw new ModKitException("a cue is a number of the event project's cue list, not \"" + cue + "\"");
            if (cue.Length > 0) cue = int.Parse(cue, NumberStyles.None, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            List<MusicPlace> places = Places(map);
            if (!places.Exists(p => p.Written)) throw new ModKitException("map " + Inv(map) + " has no row with an environment on the kms client or the server");
            int changed = 0;
            foreach (MusicPlace p in places)
            {
                if (!p.Written || Get(p, boss) == cue) continue;
                if (p.HasBgm) SetAttr(p, boss, cue);
                else SetAdded(p, boss, cue);
                changed++;
            }
            return changed;
        }

        /// <summary>Takes the mod's music lines of the map out. Returns the places that had one.</summary>
        public int Take(long map)
        {
            int n = 0;
            foreach (MusicPlace p in Places(map))
            {
                if (!p.Written) continue;
                bool had = false;
                if (p.HasBgm)
                {
                    RowOp op = Change(p, false);
                    if (op != null && op.Attrs.RemoveAll(a => a.At == BgmAt(p) && (a.Name == BgmAttr || a.Name == BossAttr)) > 0)
                    {
                        had = true;
                        if (op.Attrs.Count == 0) Drop(p, op);
                    }
                }
                else
                {
                    RowOp add = Added(p, false);
                    if (add != null) { Drop(p, add); had = true; }
                }
                if (had) n++;
            }
            return n;
        }

        void SetAttr(MusicPlace p, bool boss, string cue)
        {
            string attr = boss ? BossAttr : BgmAttr, at = BgmAt(p), shipped = boss ? p.Boss : p.Id;
            RowOp op = Change(p, false);
            if (op != null) op.Attrs.RemoveAll(a => a.Name == attr && a.At == at);
            if (cue == shipped)
            {
                if (op != null && op.Attrs.Count == 0) Drop(p, op);
                return;
            }
            if (op == null) op = Change(p, true);
            op.Attrs.Add(cue.Length == 0 ? new AttrOp { Kind = AttrOpKind.Unset, Name = attr, At = at }
                                         : new AttrOp { Kind = AttrOpKind.Set, Name = attr, Value = cue, At = at });
        }

        void SetAdded(MusicPlace p, bool boss, string cue)
        {
            RowOp add = Added(p, false);
            string id = boss ? Get(p, false) : cue, bossCue = boss ? cue : Get(p, true);
            if (id.Length == 0 && bossCue.Length == 0)
            {
                if (add != null) Drop(p, add);
                return;
            }
            var x = new StringBuilder("<bgm");
            if (id.Length > 0) x.Append(' ').Append(BgmAttr).Append("=\"").Append(id).Append('"');
            if (bossCue.Length > 0) x.Append(' ').Append(BossAttr).Append("=\"").Append(bossCue).Append('"');
            x.Append(" />");
            if (add == null)
            {
                add = new RowOp { Kind = RowOpKind.Add, In = InAt(p) };
                Edit(p, true).Rows.Add(add);
            }
            add.Xml = x.ToString();
        }

        static string BgmAt(MusicPlace p) { return p.Env + "/bgm"; }
        static string InAt(MusicPlace p) { return p.Row.Length == 0 ? p.Env : p.Row + "/" + p.Env; }

        EditOp Edit(MusicPlace p, bool create)
        {
            foreach (ModOp o in Mod.Ops)
            {
                var ed = o as EditOp;
                if (ed != null && SameArchive(ed.Archive, p.Archive) && string.Equals((ed.Entry ?? "").Replace('\\', '/'), p.Entry, StringComparison.OrdinalIgnoreCase)) return ed;
            }
            if (!create) return null;
            var n = new EditOp { Archive = p.Archive, Entry = p.Entry };
            Mod.Ops.Add(n);
            return n;
        }

        RowOp Change(MusicPlace p, bool create)
        {
            EditOp ed = Edit(p, create);
            if (ed == null) return null;
            foreach (RowOp r in ed.Rows)
                if (r.Kind == RowOpKind.Change && (r.At ?? "") == p.Row) return r;
            if (!create) return null;
            var op = new RowOp { Kind = RowOpKind.Change, At = p.Row };
            ed.Rows.Add(op);
            return op;
        }

        RowOp Added(MusicPlace p, bool create)
        {
            EditOp ed = Edit(p, create);
            if (ed == null) return null;
            string inAt = InAt(p);
            foreach (RowOp r in ed.Rows)
                if (r.Kind == RowOpKind.Add && r.In == inAt && r.Xml != null && r.Xml.TrimStart().StartsWith("<bgm", StringComparison.Ordinal)) return r;
            return null;
        }

        void Drop(MusicPlace p, RowOp op)
        {
            EditOp ed = Edit(p, false);
            if (ed == null || !ed.Rows.Remove(op)) return;
            if (ed.Rows.Count == 0) Mod.Ops.Remove(ed);
        }

        static bool SameArchive(string a, string b) { return string.Equals(DataSet.NormalizeRef(a), DataSet.NormalizeRef(b), StringComparison.OrdinalIgnoreCase); }

        static string Inv(long v) { return v.ToString(CultureInfo.InvariantCulture); }
    }
}
