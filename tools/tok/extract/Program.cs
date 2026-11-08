// tokgeom: offline ground-input extractor for PathEngine .tok building (PathEngine-free; see README.md).
// Reads a map's entities (an xblock) and the PhysX snapshots in its NIF models, and writes the input of
// iContentProcessing3D::buildGround as the client's BuildCP3DSnapshotFromPhysX would see it (format: ../FORMAT.md):
//   <out>.geom   triangle meshes: "v x y z" lines, then "f a b c excl" (excl 1 = ExcludeFromGroundResult)
//   <out>.solids convex solids:  "s n x0 y0 z0 ..." one per line
// Rules (checked by IoU against the shipped .tok, see README.md):
//   - entities with IMS2PathEngineTOK.doesMakeTOK, plus every PhysXWhitebox (it has no doesMakeTOK to filter on);
//   - PhysXWhitebox: a box of its dimension centred on its position -> convex solid;
//   - MapProperties cubes (GeneratePhysX) and NIF box shapes -> convex solids, z shrunk 74/75 about the box centre;
//   - NIF triangle meshes -> meshes, local z shrunk 74/75; PhysX group 8 (entity PropCollisionGroup) -> excluded;
//     each triangle written clockwise (v0, v2, v1): an unflipped closed mesh comes out hollow; meshes in entity order;
//   - NIF convex meshes -> solids (hull points);
//   - x/y written as integers: negative values moved by +1, then truncated; mesh z exact, solid z truncated.
// usage: tokgeom <ResourceDir> <xblock> <outPrefix> [options]
//        tokgeom <ResourceDir> --list <substring>     list xblock names in Exported.m2d
//   <xblock>         a loose .xblock file (Toolbench export) or a name in Exported.m2d (xblock/<name>.xblock)
//   --project <dir>  Toolbench project dir; its loose .nif files (matched by /model/... or /library/... path) win over the archives
//   --survey         print entity/shape statistics          --dump  write <out>.ents.tsv (one row per entity)
// experiments (default off): --all (ignore doesMakeTOK)  --noscale  --nonifbox  --convexmesh  --nomeshshrink  --nodyn
//   --heightrule (cubes under 140 tall -> excluded meshes)  --noflip  --nowhitebox  --niforder
// exit: 0 ok, 1 failure (xblock not found, project dir missing), 2 usage
using System.Globalization;
using System.Numerics;
using Maple2.File.Flat;
using Maple2.File.Flat.maplestory2library;
using Maple2.File.Flat.physxmodellibrary;
using Maple2.File.Flat.standardmodellibrary;
using Maple2.File.IO;
using Maple2.File.IO.Enum;
using Maple2.File.IO.Nif;
using Maple2.File.Parser;
using Maple2.File.Parser.Flat;
using Maple2.File.Parser.MapXBlock;
using Maple2.File.Parser.Tools;

if (args.Length == 3 && args[1] == "--list") {   // tokgeom <ResourceDir> --list <substring>: xblock names in Exported.m2d
    foreach (var f in new M2dReader(Path.Combine(args[0], "Exported.m2d")).Files)
        if (f.Name.StartsWith("xblock/") && f.Name.Contains(args[2], StringComparison.OrdinalIgnoreCase)) Console.WriteLine(Path.GetFileNameWithoutExtension(f.Name));
    return 0;
}
if (args.Length < 3) { Console.Error.WriteLine("usage: tokgeom <ResourceDir> <xblock|file.xblock> <outPrefix> [--project <dir>] [--survey] [--dump]\n       tokgeom <ResourceDir> --list <substring>"); return 2; }
string res = args[0], xblock = args[1], outp = args[2];
var opt = new HashSet<string>(args.Skip(3));
// --project <dir>: loose files of a Toolbench project; its NIFs take precedence over the client archives
int pi = Array.IndexOf(args, "--project");
string project = pi >= 3 && pi + 1 < args.Length ? args[pi + 1] : null;
if (pi >= 3 && project == null) { Console.Error.WriteLine("--project needs a directory"); return 2; }
if (project != null && !Directory.Exists(project)) { Console.Error.WriteLine($"project directory not found: {project}"); return 1; }
bool survey = opt.Contains("--survey"), all = opt.Contains("--all"), useScale = !opt.Contains("--noscale");
bool nifBox = !opt.Contains("--nonifbox"), convexAsSolid = !opt.Contains("--convexmesh"), meshShrink = !opt.Contains("--nomeshshrink");
bool skipDyn = opt.Contains("--nodyn");
const float Shrink = 74f / 75f;

var stats = new SortedDictionary<string, int>();
void Count(string k, int n = 1) { stats.TryGetValue(k, out int v); stats[k] = v + n; }

// the flat (entity type) library is in Exported.m2d, so it is needed even for a loose .xblock
if (!File.Exists(Path.Combine(res, "Exported.m2d"))) { Console.Error.WriteLine($"tokgeom: {Path.Combine(res, "Exported.m2d")} not found: <ResourceDir> is the client Data/Resource folder, and its Exported.m2d (the entity type library) is needed also for a loose .xblock"); return 1; }
var exported = new M2dReader(Path.Combine(res, "Exported.m2d"));
var parser = new XBlockParser(exported, new FlatTypeIndex(exported));

var verts = new List<Vector3>(); var faces = new List<(int a, int b, int c, int ex)>();
var solids = new List<Vector3[]>();
var needed = new Dictionary<uint, List<(Matrix4x4 m, int group, int ord)>>();   // NIF llid -> instances (transform, entity PropCollisionGroup, entity order)
// NIF meshes are collected per instance and written in entity order (the order the client adds the PhysX actors);
// the BSP ground result depends on face order where closed meshes touch.
var chunks = new List<(int ord, Vector3[] v, (int a, int b, int c, int ex)[] f)>();
bool nifOrder = opt.Contains("--niforder");
// PhysX triangles are counter-clockwise seen from outside; the ground builder wants them clockwise (an unflipped
// closed mesh comes out hollow), so each triangle is written as (v0, v2, v1).
bool noFlip = opt.Contains("--noflip");
bool noWhitebox = opt.Contains("--nowhitebox");

// 8 corners of an axis-aligned box [lo,hi] in local space, transformed.
Vector3[] Box(Vector3 lo, Vector3 hi, Matrix4x4 m) {
    var p = new Vector3[8]; int i = 0;
    foreach (float x in new[] { lo.X, hi.X }) foreach (float y in new[] { lo.Y, hi.Y }) foreach (float z in new[] { lo.Z, hi.Z })
        p[i++] = Vector3.Transform(new Vector3(x, y, z), m);
    return p;
}
// box as a convex solid with its local z extent shrunk about the centre (client copyBox)
void BoxSolid(Vector3 lo, Vector3 hi, Matrix4x4 m) {
    float zc = (lo.Z + hi.Z) / 2, hz = (hi.Z - lo.Z) / 2 * Shrink;
    solids.Add(Box(new Vector3(lo.X, lo.Y, zc - hz), new Vector3(hi.X, hi.Y, zc + hz), m));
}
// box as an excluded triangle mesh (12 triangles), z shrunk about the centre
void BoxMesh(Vector3 lo, Vector3 hi, Matrix4x4 m, int ex) {
    float zc = (lo.Z + hi.Z) / 2, hz = (hi.Z - lo.Z) / 2 * Shrink;
    var p = Box(new Vector3(lo.X, lo.Y, zc - hz), new Vector3(hi.X, hi.Y, zc + hz), m);
    int b = verts.Count; verts.AddRange(p);
    // corner index = xi*4 + yi*2 + zi
    foreach (var q in new[] { (0, 2, 3, 1), (4, 5, 7, 6), (0, 1, 5, 4), (2, 6, 7, 3), (0, 4, 6, 2), (1, 3, 7, 5) }) {
        faces.Add((b + q.Item1, b + q.Item2, b + q.Item3, ex)); faces.Add((b + q.Item1, b + q.Item3, b + q.Item4, ex));
    }
}

// Generated cubes: tall ones (>= 140 after the shrink, e.g. the default 150 cube) are PhysX boxes -> solids;
// short ones (~100, explicit GeneratePhysXDimension) are excluded triangle meshes (IoU 1.000 on 02000305_bf).
bool heightRule = opt.Contains("--heightrule");
void Cube(Vector3 lo, Vector3 hi, Matrix4x4 m, string what) {
    var c = Box(lo, hi, m); float h = c.Max(v => v.Z) - c.Min(v => v.Z);
    if (!heightRule || h * Shrink >= 140) { BoxSolid(lo, hi, m); Count($"out {what} solid"); }
    else { BoxMesh(lo, hi, m, 1); Count($"out {what} excluded mesh"); }
}

// right angles give cos = -4e-8, not 0; snap so axis-aligned props stay exact (the -x/-y truncation rule is sensitive)
static float Snap(float v) => MathF.Abs(v) < 1e-6f ? 0 : MathF.Abs(MathF.Abs(v) - 1) < 1e-6f ? MathF.Sign(v) : v;

Matrix4x4 EntityMatrix(IPlaceable p) {
    var r = new Vector3(p.Rotation.X, p.Rotation.Y, p.Rotation.Z) * (MathF.PI / 180f);
    // Z, then Y, then X (row vectors: Rz*Ry*Rx). Z-only props can't tell the orders apart;
    // multi-axis props (02020130_bf blackpillardeco <90,45,0>, <270,315,180>) match only this one.
    var m = Matrix4x4.CreateRotationZ(r.Z) * Matrix4x4.CreateRotationY(r.Y) * Matrix4x4.CreateRotationX(r.X);
    m = new Matrix4x4(Snap(m.M11), Snap(m.M12), Snap(m.M13), 0, Snap(m.M21), Snap(m.M22), Snap(m.M23), 0, Snap(m.M31), Snap(m.M32), Snap(m.M33), 0, 0, 0, 0, 1);
    if (useScale && p.Scale > 0 && MathF.Abs(p.Scale - 1f) > 1e-6f) m = Matrix4x4.CreateScale(p.Scale) * m;
    m.Translation = p.Position;
    return m;
}

int nEnt = 0;
StreamWriter dump = opt.Contains("--dump") ? new StreamWriter(outp + ".ents.tsv") : null;   // debug: every placeable entity
// <xblock> is a loose .xblock file (as Toolbench exports it) or the name of one in Exported.m2d (xblock/<name>.xblock)
bool parsed = false;
Action<Action<IEnumerable<IMapEntity>>> parseEntities = File.Exists(xblock)
    ? cb => { using var xr = System.Xml.XmlReader.Create(xblock); parser.ParseXml(xr, cb); }
    : cb => parser.ParseMap(Path.GetFileNameWithoutExtension(xblock), cb);
parseEntities(entities => {
    parsed = true;
    foreach (var e in entities) {
        if (dump != null && e is IPlaceable dp)
            dump.WriteLine(string.Join("\t", e.GetType().Name, e.EntityName, dp.Position.X, dp.Position.Y, dp.Position.Z, dp.Rotation, dp.Scale,
                e is IMS2PathEngineTOK tk ? tk.doesMakeTOK : (bool?)null, e is IMS2MapProperties mq ? $"{mq.GeneratePhysX}:{mq.GeneratePhysXDimension}:{mq.CubeType}:{string.Join(";", mq.ChildrenCube.Select(kv => kv.Key + "=" + kv.Value))}" : "",
                e is IPhysXProp q ? q.PropCollisionGroup : -1, e is IMesh mm ? mm.NifAsset : "", e.ModelName));
        // doesMakeTOK filters the props; PhysX whiteboxes have no such flag and are always in the PhysX scene
        bool tok = e is IMS2PathEngineTOK { doesMakeTOK: true } || (e is IPhysXWhitebox && e is not IMS2PathEngineTOK && !noWhitebox);
        if (survey) {
            string kind = e is IPhysXWhitebox ? "whitebox" : e is IPhysXShape ? "physxshape" : e is IPhysXTerrain ? "terrain" : e is IMS2MapProperties mp0 && mp0.GeneratePhysX ? "mapprop-cube" : e is IMesh m0 && !string.IsNullOrEmpty(m0.NifAsset) ? "mesh" : "other";
            Count($"entity {kind} tok={tok}");
            if (e is IPhysXProp pxp) Count($"entity {kind} propgroup={pxp.PropCollisionGroup} tok={tok}");
            if (e is IPlaceable pp && MathF.Abs(pp.Scale - 1f) > 1e-6f) Count($"entity scaled({kind}) tok={tok}");
        }
        if (!tok && !all) continue;
        if (e is not IPlaceable p) continue;
        if (skipDyn && e is IMS2PhysXProp { IsStatic: false }) { Count("skip dynamic prop"); continue; }
        nEnt++;
        var t = EntityMatrix(p);
        if (e is IPhysXWhitebox wb) {
            var d = wb.ShapeDimensions;
            if (survey) Console.Error.WriteLine($"  whitebox {e.EntityName} at {p.Position} dim {d}");
            Cube(-d / 2, d / 2, t, "whitebox");   // centred on its position (ellinia PhysXWhitebox0..2 tops at 5247)
        } else if (e is IMS2MapProperties mp && mp.GeneratePhysX) {
            var d = mp.GeneratePhysXDimension; if (d == Vector3.Zero) d = new(150, 150, 150);
            Cube(new Vector3(-d.X / 2, -d.Y / 2, 0), new Vector3(d.X / 2, d.Y / 2, d.Z), t, "mapprop");
        } else if (e is IPhysXShape ps) {
            Count($"physxshape {ps.ShapeType} not handled");
        }
        if (e is IMesh mesh && !string.IsNullOrEmpty(mesh.NifAsset) && mesh.NifAsset.StartsWith("urn:llid")) {
            uint llid = Convert.ToUInt32(mesh.NifAsset.Substring(mesh.NifAsset.LastIndexOf(':') + 1, 8), 16);
            if (!needed.TryGetValue(llid, out var l)) needed[llid] = l = new();
            l.Add((t, e is IPhysXProp px ? px.PropCollisionGroup : -1, nEnt));
        }
    }
});

if (!parsed) { Console.Error.WriteLine($"xblock not found: {xblock}"); return 1; }

int found = 0;
foreach (var item in NifSources(needed.Keys.ToHashSet())) {
    var mats = needed[item.llid];
    found++;
    var doc = item.document;
    try { doc.Parse(); } catch (Exception ex) { Console.Error.WriteLine($"nif {item.llid:x8}: {ex.Message}"); Count("nif parse error"); continue; }
    int nshape = 0;
    foreach (var prop in doc.PhysXProps ?? new()) {
        if (prop.Snapshot == null) { Count($"nif {item.llid:x8} null snapshot", mats.Count); continue; }
        var scale = Matrix4x4.CreateScale(prop.PhysXToWorldScale);
        foreach (var actor in prop.Snapshot.Actors)
        foreach (var shape in actor.ShapeDescriptions) {
            string sk = $"nifshape {shape.ShapeType} grp={shape.CollisionGroup}{(shape.Mesh != null ? " meshflags=" + shape.Mesh.Flags : "")}";
            Count(sk, mats.Count); nshape++;
            var pose = shape.LocalPose * actor.Poses[0] * scale;
            if (shape.ShapeType == NxShapeType.Box) {
                if (!nifBox) continue;
                var h = shape.BoxHalfExtents;
                foreach (var (t, _, _) in mats) BoxSolid(-h, h, pose * t);
                continue;
            }
            if (shape.Mesh == null) { Count($"nifshape {shape.ShapeType} skipped", mats.Count); continue; }
            PhysXMesh pm;
            try { pm = new PhysXMesh(shape.Mesh.MeshData); } catch (Exception pex) { Count($"physxmesh error {pex.GetType().Name}", mats.Count); continue; }
            bool convex = shape.ShapeType == NxShapeType.Convex || (shape.Mesh.Flags & PhysXMeshFlags.IsConvex) != 0;
            if (convex && convexAsSolid) {
                foreach (var (t, _, _) in mats) solids.Add(pm.Vertices.Select(v => Vector3.Transform(v, pose * t)).ToArray());
                continue;
            }
            foreach (var (t, grp, ord) in mats) {
                // the entity's PropCollisionGroup is the PhysX group of its shapes; group 8 = ExcludeFromGroundResult
                int ex = (grp >= 0 ? grp : shape.CollisionGroup) == 8 ? 1 : 0;
                Count($"out nif mesh group={(grp >= 0 ? grp : shape.CollisionGroup)}");
                var m = pose * t;
                chunks.Add((nifOrder ? chunks.Count : ord, pm.Vertices.Select(v => Vector3.Transform(meshShrink ? new Vector3(v.X, v.Y, v.Z * Shrink) : v, m)).ToArray(),
                            pm.Faces.Select(f => noFlip ? ((int)f.Vert0, (int)f.Vert1, (int)f.Vert2, ex) : ((int)f.Vert0, (int)f.Vert2, (int)f.Vert1, ex)).ToArray()));
            }
        }
    }
    if (nshape == 0) Count($"nif {item.llid:x8} no physx shapes", mats.Count);
}
dump?.Dispose();
foreach (var c in chunks.Select((c, i) => (c, i)).OrderBy(x => x.c.ord).ThenBy(x => x.i).Select(x => x.c)) {
    int b = verts.Count; verts.AddRange(c.v);
    foreach (var f in c.f) faces.Add((f.a + b, f.b + b, f.c + b, f.ex));
}
Console.Error.WriteLine($"{xblock}: entities={nEnt} nifs={found}/{needed.Count} verts={verts.Count} faces={faces.Count} solids={solids.Count}");
if (survey) foreach (var kv in stats) Console.Error.WriteLine($"  {kv.Value,7} {kv.Key}");

// NIF models by llid. An llid is LlidHash of the model's resource path ("/model/map/<...>.nif", lower case), as in
// Maple2.File.Parser's NifParser; only the needed files are read. Sources in order (first match wins):
//   1. --project <dir>: loose *.nif files; each is tried under its path below <dir> and from any "model/" or "library/"
//      directory in its path, so <dir>/Model/Map/foo/bar.nif and <dir>/foo/bar.nif under a model/map tree both resolve;
//   2. the client archives in <ResourceDir> (Library.m2d, Model/*.m2d).
IEnumerable<(uint llid, NifDocument document)> NifSources(HashSet<uint> want) {
    if (project != null) {
        foreach (var f in Directory.EnumerateFiles(project, "*.nif", SearchOption.AllDirectories)) {
            string rel = Path.GetRelativePath(project, f).Replace('\\', '/').ToLowerInvariant();
            var cands = new List<string> { "/" + rel };
            string full = "/" + Path.GetFullPath(f).Replace('\\', '/').ToLowerInvariant();
            foreach (var root in new[] { "/model/", "/library/" })
                for (int i = full.IndexOf(root); i >= 0; i = full.IndexOf(root, i + 1)) cands.Add(full.Substring(i));
            foreach (var c in cands) {
                uint id = LlidHash.Hash(c);
                if (!want.Remove(id)) continue;
                Count("nif from project");
                yield return (id, new NifDocument(c, File.ReadAllBytes(f)));
                break;
            }
        }
    }
    foreach (var (pre, file) in new[] { ("/library/", "Library.m2d"), ("/model/map/", "Model/Map.m2d"), ("/model/effect/", "Model/Effect.m2d"),
                 ("/model/tool/", "Model/Tool.m2d"), ("/model/item/", "Model/Item.m2d"), ("/model/npc/", "Model/Npc.m2d"), ("/model/path/", "Model/Path.m2d") }) {
        if (want.Count == 0) yield break;
        string path = Path.Combine(res, file);
        if (!File.Exists(path)) continue;
        using var r = new M2dReader(path);
        foreach (var entry in r.Files.Where(x => x.Name.EndsWith(".nif")).ToList()) {
            uint id = LlidHash.Hash(pre + entry.Name);
            if (!want.Remove(id)) continue;
            yield return (id, new NifDocument(pre + entry.Name, r.GetBytes(entry)));
        }
    }
    foreach (var id in want) Console.Error.WriteLine($"warning: model {id:x8} not found");
}

// x/y (and solid z) are written as the integers the builder uses: negative x/y move one unit toward zero (all 1042
// negative coordinates in the shipped toks agree), then C truncation.
static int Ix(float v) => (int)(v < 0 ? v + 1 : v);
var ci = CultureInfo.InvariantCulture;
string F(float v) => v.ToString("R", ci);
using (var w = new StreamWriter(outp + ".geom")) {
    foreach (var v in verts) w.WriteLine($"v {Ix(v.X)} {Ix(v.Y)} {F(v.Z)}");
    foreach (var f in faces) w.WriteLine($"f {f.a} {f.b} {f.c} {f.ex}");
}
using (var w = new StreamWriter(outp + ".solids")) {
    foreach (var s in solids) w.WriteLine($"s {s.Length} " + string.Join(" ", s.Select(v => $"{Ix(v.X)} {Ix(v.Y)} {(int)v.Z}")));
}
return 0;
