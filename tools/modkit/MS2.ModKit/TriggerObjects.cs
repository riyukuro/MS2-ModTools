// The objects a trigger script can name in its map's xblock (a port of tools/trigger/xblockindex.py, read on demand
// instead of from its cache): an xblock (Exported.m2d xblock/<name>.xblock) lists <entity modelName=...> with
// <property name=...><set value=...>; the entity's kind follows from its model's mixins (Exported.m2d flat/**.flat)
// as Maple2.File.Ingest MapEntityMapper sorts them (the first matching case wins):
//
//   MS2InteractObject                      -> interact   (interactID)
//   Portal (incl. MS2TriggerPortal)        -> portal     (PortalID)
//   SpawnPointPC                           -> pc_spawn   (SpawnPointID)
//   SpawnPointNPC / EventSpawnPointNPC     -> npc_spawn / event_npc_spawn (SpawnPointID)
//   SpawnPointItem (EventSpawnPointItem)   -> item_spawn (SpawnPointID)
//   MS2RegionSpawnBase                     -> region_spawn (SpawnPointID)
//   MS2TriggerObject: MS2TriggerActor/Agent/Block/Box/Camera/Cube/Effect/Ladder/Mesh/Rope/Skill/Sound
//                                          -> actor/agent/block/box/... (TriggerObjectID)
//   MS2TriggerModel                        -> trigger_model (TriggerModelID + XmlFilePath)
//   MS2BreakableActor                      -> breakable  (TriggerBreakableID)
//
// The lint (TriggerLint with TriggerLintOptions.Objects) checks the object ids of a script against them.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MS2.ModKit.M2d;
using MS2.ModKit.MapEdit;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>The objects of one xblock by kind and id, and the trigger models by the script they load.</summary>
    public sealed class TriggerObjects
    {
        /// <summary>The xblock's name, lower case.</summary>
        public readonly string Name;
        readonly Dictionary<string, Dictionary<long, List<string>>> kinds = new Dictionary<string, Dictionary<long, List<string>>>(StringComparer.Ordinal);
        readonly Dictionary<string, List<long>> models = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        public int Count { get; private set; }

        internal TriggerObjects(string name) { Name = name.ToLowerInvariant(); }

        internal void Add(string kind, long? id, string entity, string script)
        {
            Count++;
            Dictionary<long, List<string>> ids;
            if (!kinds.TryGetValue(kind, out ids)) kinds[kind] = ids = new Dictionary<long, List<string>>();
            if (id != null)
            {
                List<string> names;
                if (!ids.TryGetValue(id.Value, out names)) ids[id.Value] = names = new List<string>();
                names.Add(entity);
            }
            if (kind == "trigger_model")
            {
                List<long> l;
                string key = (script ?? "").ToLowerInvariant();
                if (!models.TryGetValue(key, out l)) models[key] = l = new List<long>();
                if (id != null) l.Add(id.Value);
            }
        }

        public bool Has(string kind, long id)
        {
            Dictionary<long, List<string>> ids;
            return kinds.TryGetValue(kind, out ids) && ids.ContainsKey(id);
        }

        /// <summary>The kinds that have an object with this id, sorted.</summary>
        public List<string> KindsOf(long id)
        {
            var l = new List<string>();
            foreach (KeyValuePair<string, Dictionary<long, List<string>>> k in kinds) if (k.Value.ContainsKey(id)) l.Add(k.Key);
            l.Sort(StringComparer.Ordinal);
            return l;
        }

        /// <summary>The kinds present, sorted, with their ids sorted.</summary>
        public SortedDictionary<string, List<long>> Ids()
        {
            var d = new SortedDictionary<string, List<long>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Dictionary<long, List<string>>> k in kinds)
            {
                var l = new List<long>(k.Value.Keys);
                l.Sort();
                d[k.Key] = l;
            }
            return d;
        }

        /// <summary>A TriggerModel loads trigger/&lt;xblock&gt;/&lt;script&gt;.xml (the name without folder and extension)?</summary>
        public bool LoadsScript(string script) { return models.ContainsKey((script ?? "").ToLowerInvariant()); }
    }

    /// <summary>
    /// The model kinds of one Exported archive (or extract) and its xblocks, read when asked for and kept.
    /// Safe for one thread at a time.
    /// </summary>
    public sealed class TriggerXBlocks
    {
        static readonly string[] TriggerSub = { "MS2TriggerActor", "MS2TriggerAgent", "MS2TriggerBlock", "MS2TriggerBox", "MS2TriggerCamera",
            "MS2TriggerCube", "MS2TriggerEffect", "MS2TriggerLadder", "MS2TriggerMesh", "MS2TriggerPortal", "MS2TriggerRope", "MS2TriggerSkill", "MS2TriggerSound" };
        static readonly HashSet<string> IdProps = new HashSet<string>(StringComparer.Ordinal)
            { "TriggerObjectID", "SpawnPointID", "PortalID", "interactID", "TriggerModelID", "XmlFilePath", "TriggerBreakableID", "IsVisible" };

        sealed class Kind { public string Name; public Dictionary<string, string> Defaults; }

        // model name (lower case) -> kind and default ids; only models that have a kind
        readonly Dictionary<string, Kind> kinds = new Dictionary<string, Kind>(StringComparer.Ordinal);
        readonly Func<string, byte[]> readXBlock;
        readonly Dictionary<string, TriggerObjects> cache = new Dictionary<string, TriggerObjects>(StringComparer.Ordinal);

        /// <summary>readXBlock: the bytes of xblock/&lt;name&gt;.xblock for a lower-case name, null when there is none.</summary>
        public TriggerXBlocks(FlatIndex flats, Func<string, byte[]> readXBlock)
        {
            this.readXBlock = readXBlock;
            var raw = new Dictionary<string, FlatModel>(StringComparer.Ordinal);
            foreach (string n in flats.Names) raw[n.ToLowerInvariant()] = flats.Get(n);
            foreach (KeyValuePair<string, FlatModel> m in raw)
            {
                var mix = new HashSet<string>(StringComparer.Ordinal);
                Closure(raw, m.Key, mix);
                string kind = Classify(mix);
                if (kind == null) continue;
                // defaults: the nearest definition wins (the model itself, then its own mixins in order)
                var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
                var srcs = new List<string> { m.Key };
                foreach (FlatMixin x in m.Value.Mixins) srcs.Add((x.Name ?? "").ToLowerInvariant());
                foreach (string s in srcs)
                {
                    FlatModel fm;
                    if (!raw.TryGetValue(s, out fm)) continue;
                    foreach (FlatProp p in fm.Props)
                        if (p.Name != null && IdProps.Contains(p.Name) && p.Default != null && p.Default.Count > 0 && p.Default[0].Index == null && !defaults.ContainsKey(p.Name))
                            defaults[p.Name] = p.Default[0].Value;
                }
                kinds[m.Key] = new Kind { Name = kind, Defaults = defaults };
            }
        }

        /// <summary>Models with a kind (the size of the index).</summary>
        public int ModelCount { get { return kinds.Count; } }

        /// <summary>An Exported archive: flat/**.flat and xblock/*.xblock.</summary>
        public static TriggerXBlocks OfArchive(M2dArchive exported)
        {
            return new TriggerXBlocks(FlatIndex.FromArchive(exported), delegate(string name)
            {
                M2dEntry e = exported.Find("xblock/" + name + ".xblock");
                return e == null ? null : exported.Read(e);
            });
        }

        /// <summary>An extract: a folder holding flat/ and xblock/.</summary>
        public static TriggerXBlocks OfFolder(string dir)
        {
            string xdir = Path.Combine(dir, "xblock");
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Directory.Exists(xdir))
                foreach (string f in Directory.GetFiles(xdir))
                    if (f.EndsWith(".xblock", StringComparison.OrdinalIgnoreCase))
                        files[Path.GetFileNameWithoutExtension(f).ToLowerInvariant()] = f;
            return new TriggerXBlocks(FlatIndex.FromDirectory(dir), delegate(string name)
            {
                string f;
                return files.TryGetValue(name, out f) ? File.ReadAllBytes(f) : null;
            });
        }

        /// <summary>An Exported archive (.m2d) or a folder holding flat/ and xblock/.</summary>
        public static TriggerXBlocks Load(string path)
        {
            if (Directory.Exists(path)) return OfFolder(path);
            if (!File.Exists(path)) throw new ModKitException("no Exported archive or extract at " + path);
            M2dArchive a = M2dArchive.Open(path);
            return OfArchive(a);
        }

        /// <summary>The objects of an xblock by name ("02000006_ad", "xblock/02000006_ad.xblock"); null when there is no such xblock.</summary>
        public TriggerObjects Get(string spec)
        {
            string name = spec.Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            if (slash >= 0) name = name.Substring(slash + 1);
            if (name.EndsWith(".xblock", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - ".xblock".Length);
            name = name.ToLowerInvariant();
            TriggerObjects o;
            if (cache.TryGetValue(name, out o)) return o;
            byte[] b = readXBlock(name);
            o = b == null ? null : Read(b, name);
            cache[name] = o;
            return o;
        }

        /// <summary>The objects of the xblock in these bytes (a loose file), named name.</summary>
        public TriggerObjects Read(byte[] bytes, string name)
        {
            var o = new TriggerObjects(name);
            Collect(XmlDoc.Parse(bytes, name + ".xblock").Root, o);
            return o;
        }

        void Collect(XmlElem e, TriggerObjects o)
        {
            if (e.Name != "entity") { foreach (XmlElem c in e.Children) Collect(c, o); return; }
            string model = (e.Attr("modelName") ?? "").ToLowerInvariant();
            Kind k;
            Dictionary<string, string> props;
            string kind;
            if (kinds.TryGetValue(model, out k)) { kind = k.Name; props = new Dictionary<string, string>(k.Defaults, StringComparer.Ordinal); }
            else if (model == "ms2breakablenif")
            {
                // no flat model in the export; the ingest has the BreakableNIF case commented out, so the server has no
                // such object. Kept so that the lint can say why an id is not found.
                kind = "breakable_nif";
                props = new Dictionary<string, string>(StringComparer.Ordinal);
            }
            else return;
            foreach (XmlElem p in e.Children)
            {
                string pn = p.Attr("name");
                if (pn == null || !IdProps.Contains(pn)) continue;
                foreach (XmlElem s in p.Children)
                {
                    if (s.Name == "set" && s.Attr("index") == null && s.Attr("value") != null) props[pn] = s.Attr("value");
                    break;
                }
            }
            string key;
            switch (kind)
            {
                case "interact": key = "interactID"; break;
                case "portal": key = "PortalID"; break;
                case "trigger_model": key = "TriggerModelID"; break;
                case "breakable": case "breakable_nif": key = "TriggerBreakableID"; break;
                default: key = kind.EndsWith("spawn", StringComparison.Ordinal) || kind == "spawn_other" ? "SpawnPointID" : "TriggerObjectID"; break;
            }
            string v, script = null;
            props.TryGetValue(key, out v);
            if (kind == "trigger_model")
            {
                string path;
                props.TryGetValue("XmlFilePath", out path);
                script = Path.GetFileNameWithoutExtension(Path.GetFileName((path ?? "").Replace('\\', '/')));
            }
            o.Add(kind, AsInt(v), e.Attr("name") ?? "", script);
        }

        // int(float(v)) of xblockindex.py
        static long? AsInt(string v)
        {
            double d;
            if (v == null || !double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return null;
            if (double.IsNaN(d) || double.IsInfinity(d) || Math.Abs(d) > 9e18) return null;
            return (long)Math.Truncate(d);
        }

        static void Closure(Dictionary<string, FlatModel> raw, string name, HashSet<string> seen)
        {
            if (!seen.Add(name)) return;
            FlatModel m;
            if (!raw.TryGetValue(name, out m)) return;
            foreach (FlatMixin x in m.Mixins) Closure(raw, (x.Name ?? "").ToLowerInvariant(), seen);
        }

        /// <summary>The object kind of a model whose mixins (lower case, the model itself included) are these.</summary>
        static string Classify(HashSet<string> m)
        {
            if (m.Contains("ms2interactobject")) return "interact";
            if (m.Contains("portal")) return "portal";
            if (m.Contains("spawnpoint"))
            {
                if (m.Contains("spawnpointpc")) return "pc_spawn";
                if (m.Contains("spawnpointnpc")) return m.Contains("eventspawnpointnpc") ? "event_npc_spawn" : "npc_spawn";
                if (m.Contains("eventspawnpointitem") || m.Contains("spawnpointitem")) return "item_spawn";
                return "spawn_other";
            }
            if (m.Contains("ms2regionspawnbase")) return "region_spawn";
            if (m.Contains("ms2triggerobject"))
            {
                foreach (string sub in TriggerSub) if (m.Contains(sub.ToLowerInvariant())) return sub.Substring("MS2Trigger".Length).ToLowerInvariant();
                return "trigger_object";
            }
            if (m.Contains("ms2regionskill")) return "region_skill";
            if (m.Contains("ms2triggermodel")) return "trigger_model";
            if (m.Contains("ms2breakableactor")) return "breakable";
            return null;
        }
    }
}
