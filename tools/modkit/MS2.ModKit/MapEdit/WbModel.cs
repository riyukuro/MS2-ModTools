// The entity model files of the map editor (.model, .preset), rebuilt from the game's flattened model files (.flat).
// A .flat is one model with everything it inherits written out; the editor wants the sources: which models a model
// mixes in directly, which properties it declares itself and which inherited ones it overrides. This file holds the
// model as an object, its writer, and the rebuild. It gives the same bytes as tools/mapedit/flat2model.py.
using System;
using System.Collections.Generic;
using System.Globalization;
using MS2.ModKit.Xml;

namespace MS2.ModKit.MapEdit
{
    /// <summary>String.GetHashCode of the 32-bit .NET 2.0 runtime: the ids of models, properties and behaviors in a
    /// .flat are this hash of their name.</summary>
    public static class NetHash
    {
        public static uint Of(string s)
        {
            int n = s.Length;
            var ints = new uint[(n + 1) / 2 + 2];
            for (int i = 0; i < n; i++) ints[i / 2] |= (uint)s[i] << (16 * (i & 1));
            unchecked
            {
                int h1 = 352654597, h2 = 352654597;
                int k = 0, left = n;
                while (left > 2)
                {
                    h1 = (((h1 << 5) + h1) + (h1 >> 27)) ^ (int)ints[k];
                    h2 = (((h2 << 5) + h2) + (h2 >> 27)) ^ (int)ints[k + 1];
                    k += 2; left -= 4;
                }
                if (left > 0) h1 = (((h1 << 5) + h1) + (h1 >> 27)) ^ (int)ints[k];
                return (uint)(h1 + h2 * 1566083941);
            }
        }
    }

    /// <summary>A value as a .flat has it, plus the two things a .flat loses: the editor type (Rotator, Degree) and
    /// whether a collection is a List or a Map.</summary>
    public sealed class WbVal
    {
        public string FType;
        public List<XSet> Sets = new List<XSet>();
        public string MType, Kind;

        public bool IsCollection { get { return FType.StartsWith("Assoc", StringComparison.Ordinal); } }
        public string ItemType { get { return IsCollection ? FType.Substring(5) : FType; } }
    }

    public struct WbTraitOverride
    {
        public string Name; public bool Active;
        public WbTraitOverride(string name, bool active) { Name = name; Active = active; }
    }

    public sealed class WbProp
    {
        public string Name, Desc = "";
        public WbVal Val;
        public List<string> Traits = new List<string>();
        public List<WbTraitOverride> TraitOverrides = new List<WbTraitOverride>();   // overrides only
    }

    public sealed class WbBehavior
    {
        public string Name, Target, Desc = "";
        public List<string> Traits = new List<string>();
        public List<WbTraitOverride> TraitOverrides = new List<WbTraitOverride>();   // overrides only
    }

    public sealed class WbExtra
    {
        public string Name, Type;
        public List<string[]> Entries = new List<string[]>();      // type, key, value
    }

    public sealed class WbModel
    {
        public string Name, Id, Desc = "", Folder;
        public bool Preset;
        public readonly List<KeyValuePair<string, string>> Mixins = new List<KeyValuePair<string, string>>();    // id, name
        public readonly List<WbProp> Props = new List<WbProp>();
        public readonly List<WbProp> Overrides = new List<WbProp>();
        public readonly List<WbBehavior> Behaviors = new List<WbBehavior>();
        public readonly List<WbBehavior> BehaviorOverrides = new List<WbBehavior>();
        public List<string> Traits = new List<string>();
        public List<WbExtra> Extra = new List<WbExtra>();

        static string EditorName(string exportType) { return exportType == "AssetID" ? "Asset" : exportType; }

        internal static List<string> Split(string v)
        {
            var res = new List<string>();
            int s = -1;
            for (int i = 0; i <= v.Length; i++)
            {
                bool sep = i == v.Length || v[i] == ',' || char.IsWhiteSpace(v[i]);
                if (sep) { if (s >= 0) res.Add(v.Substring(s, i - s)); s = -1; }
                else if (s < 0) s = i;
            }
            return res;
        }

        static List<string> Padded(string v, int n)
        {
            List<string> t = Split(v);
            while (t.Count < n) t.Add("0");
            return t;
        }

        static int ColorByte(string tok)
        {
            int b = MapValues.ByteOfFloatText(tok);
            if (b >= 0) return b;
            double d;
            if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out d) || double.IsNaN(d))
                throw new ModKitException("a colour holds '" + tok + "', which is not a number");
            double r = Math.Round(d * 255.0);
            return r < 0 ? 0 : r > 255 ? 255 : (int)r;
        }

        static void Axes(NetXmlWriter w, string tag, string v, string[] axes, string inner)
        {
            List<string> t = Padded(v, axes.Length);
            w.Start(tag);
            for (int i = 0; i < axes.Length; i++)
            {
                w.Start(axes[i]);
                w.Empty(inner, "Value", t[i]);
                w.End();
            }
            w.End();
        }

        static readonly string[] Plain = { "Boolean", "SInt16", "UInt16", "SInt32", "UInt32", "SInt64", "UInt64", "Float32", "Float64", "String", "Degree" };

        static void WriteItem(NetXmlWriter w, string ftype, string v, string mtype)
        {
            string t = mtype ?? EditorName(ftype);
            if (Array.IndexOf(Plain, t) >= 0) w.Empty(t, "Value", v);
            else if (t == "Point3") Axes(w, "Point3", v, new[] { "X", "Y", "Z" }, "Float32");
            else if (t == "Point2") Axes(w, "Point2", v, new[] { "X", "Y" }, "Float32");
            else if (t == "Rotator") Axes(w, "Rotator", v, new[] { "Roll", "Pitch", "Yaw" }, "Degree");
            else if (t == "Color" || t == "ColorA")
            {
                int n = t == "Color" ? 3 : 4;
                List<string> toks = Padded(v, n);
                var a = new List<string>();
                string[] keys = { "R", "G", "B", "A" };
                for (int i = 0; i < n; i++) { a.Add(keys[i]); a.Add(ColorByte(toks[i]).ToString(CultureInfo.InvariantCulture)); }
                w.Empty(t, a.ToArray());
            }
            else if (t == "EntityRef")
            {
                w.Start("EntityRef");
                w.Leaf("Name", "");
                w.Leaf("Id", string.IsNullOrEmpty(v) ? MapValues.ZeroGuid : v);
                w.Leaf("TargetContainerHierarchy", "");
                w.End();
            }
            else if (t == "Asset")
            {
                w.Start("Asset", "Value", v);
                w.Leaf("LastKnownName", ""); w.Leaf("LastKnownPath", ""); w.Leaf("LastKnownTags", "");
                w.End();
            }
            else if (t == "AttachedNifAsset")
            {
                string[] parts = v.Split(';');
                var p = new string[5];
                for (int i = 0; i < 5; i++) p[i] = i < parts.Length ? parts[i] : "";
                w.Start("AttachedNifAsset");
                w.Start("AttachPoint"); w.Empty("String", "Value", p[0]); w.End();
                w.Start("NifAsset"); WriteItem(w, "AssetID", p[1], null); w.End();
                w.Start("Translation"); WriteItem(w, "Point3", p[2], null); w.End();
                w.Start("Rotation"); WriteItem(w, "Point3", p[3], "Rotator"); w.End();
                w.Start("Scale"); w.Empty("Float32", "Value", p[4].Length == 0 ? "1" : p[4]); w.End();
                w.End();
            }
            else w.Empty(t, "Value", v);
        }

        internal static bool Digits(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        static void WriteValue(NetXmlWriter w, WbVal val)
        {
            w.Start("Value");
            if (val.IsCollection)
            {
                string it = val.ItemType;
                string kind = val.Kind;
                if (kind == null)
                {
                    kind = "List";
                    foreach (XSet s in val.Sets) if (s.Index != null && !Digits(s.Index)) kind = "Map";
                }
                w.Start(kind, "ItemsDataType", val.MType ?? EditorName(it));
                w.Start("Items");
                foreach (XSet s in val.Sets)
                {
                    if (kind == "Map")
                    {
                        w.Start("Item", "Key", s.Index ?? "None");
                        WriteItem(w, it, s.Value, val.MType);
                        w.End();
                    }
                    else WriteItem(w, it, s.Value, val.MType);
                }
                w.End();
                w.End();
            }
            else WriteItem(w, val.FType, val.Sets.Count > 0 ? val.Sets[0].Value : "", val.MType);
            w.End();
        }

        static void WriteTraits(NetXmlWriter w, List<string> names)
        {
            var s = new List<string>(names);
            s.Sort(StringComparer.Ordinal);
            w.Start("Traits");
            foreach (string t in s) w.Empty("Trait", "Name", t);
            w.End();
        }

        static void WriteTraitOverrides(NetXmlWriter w, List<WbTraitOverride> list)
        {
            var s = new List<WbTraitOverride>(list);
            Stable.Sort(s, delegate (WbTraitOverride a, WbTraitOverride b)
            {
                int c = string.CompareOrdinal(a.Name, b.Name);
                return c != 0 ? c : a.Active.CompareTo(b.Active);
            });
            w.Start("TraitOverrides");
            foreach (WbTraitOverride t in s)
            {
                w.Start("TraitOverride", "IsActive", t.Active ? "True" : "False");
                w.Empty("Trait", "Name", t.Name);
                w.End();
            }
            w.End();
        }

        /// <summary>The file text (with its byte order mark as the first character).</summary>
        public string Text()
        {
            var w = new NetXmlWriter("  ", true);
            if (Folder != null && Folder.Length > 0) w.Start(Preset ? "EntityModelPreset" : "EntityModel", "version", "5", "ID", Id, "Description", Desc, "Folder", Folder);
            else w.Start(Preset ? "EntityModelPreset" : "EntityModel", "version", "5", "ID", Id, "Description", Desc);
            w.Start("Mixins");
            foreach (KeyValuePair<string, string> m in Mixins) w.Empty("Mixin", "SourceID", m.Key, "SourceName", m.Value);
            w.End();
            w.Start("Properties");
            foreach (WbProp p in Props)
            {
                w.Start("Property", "Name", p.Name, "Description", p.Desc);
                WriteTraits(w, p.Traits);
                WriteValue(w, p.Val);
                w.Start("Restriction");
                w.End();
                w.End();
            }
            w.End();
            w.Start("PropertyOverrides");
            var ov = new List<WbProp>(Overrides);
            Stable.Sort(ov, delegate (WbProp a, WbProp b) { return string.CompareOrdinal(a.Name, b.Name); });
            foreach (WbProp o in ov)
            {
                w.Start("PropertyOverride", "Name", o.Name, "Description", o.Desc);
                WriteTraitOverrides(w, o.TraitOverrides);
                WriteValue(w, o.Val);
                w.End();
            }
            w.End();
            w.Start("Behaviors");
            foreach (WbBehavior b in Behaviors)
            {
                w.Start("Behavior", "Name", b.Name, "Description", b.Desc, "Target", b.Target);
                WriteTraits(w, b.Traits);
                w.End();
            }
            w.End();
            w.Start("BehaviorOverrides");
            var bo = new List<WbBehavior>(BehaviorOverrides);
            Stable.Sort(bo, delegate (WbBehavior a, WbBehavior b) { return string.CompareOrdinal(a.Name, b.Name); });
            foreach (WbBehavior b in bo)
            {
                w.Start("BehaviorOverride", "Name", b.Name, "Description", b.Desc, "Target", b.Target);
                WriteTraitOverrides(w, b.TraitOverrides);
                w.End();
            }
            w.End();
            WriteTraits(w, Traits);
            w.Start("ExtraDataList");
            foreach (WbExtra x in Extra)
            {
                w.Start("ExtraData", "Name", x.Name, "Type", x.Type);
                var es = new List<string[]>(x.Entries);
                Stable.Sort(es, delegate (string[] a, string[] b) { return string.CompareOrdinal(a[1], b[1]); });
                foreach (string[] e in es) w.Empty("Entry", "Type", e[0], "Key", e[1], "Value", e[2]);
                w.End();
            }
            w.End();
            w.End();
            return w.Finish();
        }
    }

    public sealed class WbFlatProp
    {
        public string Type, Name;
        public List<string> Traits = new List<string>();
        public List<XSet> Sets = new List<XSet>();
    }

    public sealed class WbFlatBehavior
    {
        public string Name, Type, Source;
        public List<string> Traits = new List<string>();
    }

    /// <summary>One .flat with everything the rebuild needs. Kind is "library" or "presets", Cat the folder below it.</summary>
    public sealed class WbFlat
    {
        public string Name, Kind, Cat, File;
        public readonly List<string> Traits = new List<string>();
        public readonly List<string> Mixins = new List<string>();
        public readonly List<WbFlatProp> Props = new List<WbFlatProp>();
        public readonly List<WbFlatBehavior> Behaviors = new List<WbFlatBehavior>();
        public readonly List<WbExtra> Extra = new List<WbExtra>();
        Dictionary<string, WbFlatProp> byName;

        public bool IsPreset { get { return Kind == "presets"; } }

        public WbFlatProp Prop(string name)
        {
            if (byName == null)
            {
                byName = new Dictionary<string, WbFlatProp>(StringComparer.Ordinal);
                foreach (WbFlatProp p in Props) byName[p.Name ?? ""] = p;
            }
            WbFlatProp r;
            return byName.TryGetValue(name ?? "", out r) ? r : null;
        }

        /// <summary>file: "flat/library/NAME/x.flat" or "flat/presets/NAME/x.flat".</summary>
        public static WbFlat Parse(byte[] bytes, string file)
        {
            XmlElem root = XmlDoc.Parse(bytes, file).Root;
            var f = new WbFlat { Name = root.Attr("name"), File = file };
            string[] parts = file.Split('/');
            if (parts.Length >= 4) { f.Kind = parts[parts.Length - 3]; f.Cat = parts[parts.Length - 2]; }
            foreach (XmlElem c in root.Children)
            {
                switch (c.Name)
                {
                    case "trait": f.Traits.Add(c.Attr("value")); break;
                    case "mixin": f.Mixins.Add(c.Attr("name")); break;
                    case "property":
                        var p = new WbFlatProp { Type = c.Attr("type"), Name = c.Attr("name") };
                        foreach (XmlElem s in c.Children)
                        {
                            if (s.Name == "set") p.Sets.Add(new XSet(s.Attr("index"), s.Attr("value")));
                            else if (s.Name == "trait") p.Traits.Add(s.Attr("value"));
                        }
                        f.Props.Add(p);
                        break;
                    case "behavior":
                        var b = new WbFlatBehavior { Name = c.Attr("name"), Type = c.Attr("type"), Source = c.Attr("source") };
                        foreach (XmlElem s in c.Children) if (s.Name == "trait") b.Traits.Add(s.Attr("value"));
                        f.Behaviors.Add(b);
                        break;
                    case "extradata":
                        var x = new WbExtra { Name = c.Attr("name"), Type = c.Attr("type") };
                        foreach (XmlElem e in c.Children)
                            if (e.Name == "entry") x.Entries.Add(new[] { e.Attr("type"), e.Attr("key"), e.Attr("value") });
                        f.Extra.Add(x);
                        break;
                }
            }
            return f;
        }
    }

    /// <summary>All flats of a game by model name. Read order: library before presets, then folder name, then file
    /// name; of two flats with one model name the later one is used.</summary>
    public sealed class WbFlats
    {
        readonly Dictionary<string, WbFlat> byName = new Dictionary<string, WbFlat>(StringComparer.Ordinal);
        readonly List<string> names = new List<string>();

        public int Count { get { return names.Count; } }
        public IList<string> Names { get { return names.AsReadOnly(); } }
        public WbFlat Get(string name) { WbFlat f; return name != null && byName.TryGetValue(name, out f) ? f : null; }

        static int KindRank(string path) { return path.StartsWith("flat/library/", StringComparison.Ordinal) ? 0 : 1; }

        public static WbFlats FromFiles(IEnumerable<KeyValuePair<string, byte[]>> files)
        {
            var list = new List<KeyValuePair<string, byte[]>>();
            foreach (KeyValuePair<string, byte[]> f in files)
            {
                string[] p = f.Key.Split('/');
                if (p.Length == 4 && p[0] == "flat" && (p[1] == "library" || p[1] == "presets") && f.Key.EndsWith(".flat", StringComparison.Ordinal)) list.Add(f);
            }
            list.Sort(delegate (KeyValuePair<string, byte[]> a, KeyValuePair<string, byte[]> b)
            {
                int c = KindRank(a.Key).CompareTo(KindRank(b.Key));
                if (c != 0) return c;
                string[] pa = a.Key.Split('/'), pb = b.Key.Split('/');
                c = string.CompareOrdinal(pa[2], pb[2]);
                return c != 0 ? c : string.CompareOrdinal(pa[3], pb[3]);
            });
            var res = new WbFlats();
            foreach (KeyValuePair<string, byte[]> f in list)
            {
                WbFlat m = WbFlat.Parse(f.Value, f.Key);
                if (m.Name == null) throw new ModKitException(f.Key + ": the model has no name");
                if (!res.byName.ContainsKey(m.Name)) res.names.Add(m.Name);
                res.byName[m.Name] = m;
            }
            return res;
        }
    }

    /// <summary>The rebuild of model sources from flats.</summary>
    public sealed class WbRecon
    {
        static readonly string[] RotatorNames = { "Rotation", "ShapeRotation", "AttenuationVisualizerOrientation", "InitialOrientation" };
        static readonly string[] DegreeNames = { "OuterSpotAngle", "InnerSpotAngle", "FOV" };
        static readonly string[] MapNames = { "AttachedObjects", "NormalEventMap", "ImmediateEventMap", "CollisionBehaviors", "OnEnterBehaviors",
            "OnStayBehaviors", "OnLeaveBehaviors", "LoadedBlocks", "BlockData", "Categories" };

        readonly WbFlats flats;
        readonly IDictionary<string, string> known;
        readonly Dictionary<string, List<string>> direct = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        readonly Dictionary<string, Dictionary<string, bool>> defined = new Dictionary<string, Dictionary<string, bool>>(StringComparer.Ordinal);
        readonly Dictionary<string, string> ids = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly List<string> Warnings = new List<string>();

        /// <summary>known: model name to id, for the models whose real id is known; may be null.</summary>
        public WbRecon(WbFlats flats, IDictionary<string, string> known) { this.flats = flats; this.known = known; }

        /// <summary>The id of a model: the known one, else a fixed id made from the name.</summary>
        public string Id(string name)
        {
            string g;
            if (known != null && known.TryGetValue(name, out g)) return g;
            if (!ids.TryGetValue(name, out g)) ids[name] = g = MapValues.UrlUuid5("ms2-model:" + name);
            return g;
        }

        /// <summary>The mixins a model names itself: its flat lists all ancestors in walk order, so every name that
        /// an earlier direct mixin already brings is dropped.</summary>
        public List<string> DirectMixins(string name)
        {
            List<string> d;
            if (direct.TryGetValue(name, out d)) return d;
            d = new List<string>();
            var covered = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (string n in flats.Get(name).Mixins)
            {
                if (covered.ContainsKey(n)) continue;
                d.Add(n);
                covered[n] = true;
                WbFlat g = flats.Get(n);
                if (g != null) foreach (string m in g.Mixins) covered[m] = true;
            }
            direct[name] = d;
            return d;
        }

        void Walk(string n, Dictionary<string, bool> seen, List<string> res, Dictionary<string, bool> allowed)
        {
            if (seen.ContainsKey(n)) return;
            seen[n] = true;
            res.Add(n);
            if (allowed.ContainsKey(n) && flats.Get(n) != null) foreach (string d in DirectMixins(n)) Walk(d, seen, res, allowed);
        }

        static void Hint(string name, string ftype, out string mtype, out string kind)
        {
            mtype = null; kind = null;
            if (ftype == "Point3" && Array.IndexOf(RotatorNames, name) >= 0) mtype = "Rotator";
            else if (ftype == "Float32" && Array.IndexOf(DegreeNames, name) >= 0) mtype = "Degree";
            else if (ftype.StartsWith("Assoc", StringComparison.Ordinal) && Array.IndexOf(MapNames, name) >= 0) kind = "Map";
        }

        static bool IsIndex(string s, int n)
        {
            if (!WbModel.Digits(s)) return false;
            string t = s.TrimStart('0');
            return (t.Length == 0 ? "0" : t) == n.ToString(CultureInfo.InvariantCulture);
        }

        static WbVal Val(WbFlatProp p)
        {
            var v = new WbVal { FType = p.Type, Sets = new List<XSet>(p.Sets) };
            Hint(p.Name, p.Type, out v.MType, out v.Kind);
            if (v.IsCollection && v.Kind == null)
                for (int i = 0; i < p.Sets.Count; i++)
                    if (!IsIndex(p.Sets[i].Index, i)) { v.Kind = "Map"; break; }
            return v;
        }

        static bool SameSets(List<XSet> a, List<XSet> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i].Index != b[i].Index || a[i].Value != b[i].Value) return false;
            return true;
        }

        static bool SameTraits(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            var x = new List<string>(a); var y = new List<string>(b);
            x.Sort(StringComparer.Ordinal); y.Sort(StringComparer.Ordinal);
            for (int i = 0; i < x.Count; i++) if (x[i] != y[i]) return false;
            return true;
        }

        static List<WbTraitOverride> TraitChanges(List<string> now, List<string> before)
        {
            var res = new List<WbTraitOverride>();
            foreach (string t in now) if (!before.Contains(t)) res.Add(new WbTraitOverride(t, true));
            foreach (string t in before) if (!now.Contains(t)) res.Add(new WbTraitOverride(t, false));
            return res;
        }

        static List<WbExtra> CopyExtra(List<WbExtra> src)
        {
            var res = new List<WbExtra>();
            foreach (WbExtra x in src) res.Add(new WbExtra { Name = x.Name, Type = x.Type, Entries = new List<string[]>(x.Entries) });
            return res;
        }

        /// <summary>A library model: own properties are those no direct mixin has; an inherited property whose value,
        /// traits or type differ from the first direct mixin that has it is an override.</summary>
        public WbModel Build(string name)
        {
            WbFlat f = flats.Get(name);
            if (f == null) throw new ModKitException("no flat for model " + name);
            var m = new WbModel { Name = f.Name, Id = Id(f.Name), Traits = new List<string>(f.Traits) };
            List<string> dm = DirectMixins(name);
            foreach (string n in dm)
            {
                m.Mixins.Add(new KeyValuePair<string, string>(Id(n), n));
                if (flats.Get(n) == null) Warnings.Add(name + ": mixin " + n + " has no flat");
            }
            var allowed = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (string n in f.Mixins) allowed[n] = true;
            allowed[name] = true;
            var got = new List<string>();
            var seen = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (string d in dm) Walk(d, seen, got, allowed);
            bool same = got.Count == f.Mixins.Count;
            for (int i = 0; same && i < got.Count; i++) same = got[i] == f.Mixins[i];
            if (!same) Warnings.Add(name + ": mixin order differs after reduction");

            var inherited = new Dictionary<string, WbFlatProp>(StringComparer.Ordinal);
            foreach (string n in dm)
            {
                WbFlat g = flats.Get(n);
                if (g == null) continue;
                foreach (WbFlatProp p in g.Props) if (!inherited.ContainsKey(p.Name)) inherited[p.Name] = p;
            }
            foreach (WbFlatProp p in f.Props)
            {
                WbFlatProp ip;
                if (!inherited.TryGetValue(p.Name, out ip))
                    m.Props.Add(new WbProp { Name = p.Name, Val = Val(p), Traits = new List<string>(p.Traits) });
                else if (!SameSets(p.Sets, ip.Sets) || !SameTraits(p.Traits, ip.Traits) || p.Type != ip.Type)
                    m.Overrides.Add(new WbProp { Name = p.Name, Val = Val(p), TraitOverrides = TraitChanges(p.Traits, ip.Traits) });
            }
            // the model that declares a behavior is the ancestor whose flat names itself as the source
            var definer = new Dictionary<string, WbFlatBehavior>(StringComparer.Ordinal);
            foreach (string n in f.Mixins)
            {
                WbFlat g = flats.Get(n);
                if (g == null) continue;
                foreach (WbFlatBehavior b in g.Behaviors)
                    if (b.Source == n && !definer.ContainsKey(b.Name)) definer[b.Name] = b;
            }
            foreach (WbFlatBehavior b in f.Behaviors)
            {
                if (b.Type == "Virtual" || b.Source != name) continue;
                WbFlatBehavior ib;
                if (!definer.TryGetValue(b.Name, out ib)) m.Behaviors.Add(new WbBehavior { Name = b.Name, Target = b.Type, Traits = new List<string>(b.Traits) });
                else m.BehaviorOverrides.Add(new WbBehavior { Name = b.Name, Target = b.Type, TraitOverrides = TraitChanges(b.Traits, ib.Traits) });
            }
            m.Extra = CopyExtra(f.Extra);
            return m;
        }

        Dictionary<string, bool> Defined(string name, int depth)
        {
            Dictionary<string, bool> r;
            if (defined.TryGetValue(name, out r)) return r;
            if (depth > 256) throw new ModKitException("preset " + name + " is its own base");
            WbFlat f = flats.Get(name);
            if (f == null) r = new Dictionary<string, bool>();
            else if (f.IsPreset) r = f.Mixins.Count > 0 ? Defined(f.Mixins[0], depth + 1) : new Dictionary<string, bool>();
            else
            {
                r = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (WbFlatProp p in f.Props) r[p.Name] = true;
            }
            defined[name] = r;
            return r;
        }

        /// <summary>A preset: one mixin (the first of its flat) and the properties that differ from that model's flat.
        /// A property the base no longer declares is left out, with a warning.</summary>
        public WbModel BuildPreset(string name)
        {
            WbFlat f = flats.Get(name);
            if (f == null) throw new ModKitException("no flat for preset " + name);
            var m = new WbModel { Name = f.Name, Id = Id(f.Name), Preset = true, Traits = new List<string>(f.Traits) };
            string bas = null; WbFlat bf = null;
            if (f.Mixins.Count > 0)
            {
                bas = f.Mixins[0];
                m.Mixins.Add(new KeyValuePair<string, string>(Id(bas), bas));
                bf = flats.Get(bas);
            }
            if (bf == null) { Warnings.Add(name + ": preset base " + (bas ?? "None") + " has no flat"); return m; }
            Dictionary<string, bool> def = Defined(bas, 0);
            foreach (WbFlatProp p in f.Props)
            {
                WbFlatProp ip = bf.Prop(p.Name);
                if (ip == null || !def.ContainsKey(p.Name))
                {
                    Warnings.Add(name + ": preset property " + p.Name + " not in base model " + bas);
                    continue;
                }
                if (!SameSets(p.Sets, ip.Sets) || !SameTraits(p.Traits, ip.Traits))
                    m.Overrides.Add(new WbProp { Name = p.Name, Val = Val(p), TraitOverrides = TraitChanges(p.Traits, ip.Traits) });
            }
            return m;
        }
    }
}
