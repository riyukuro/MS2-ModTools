#!/usr/bin/env python3
"""mkxblock.py: generate a new map's xblock (tab dialect) from a small layout spec.

    python3 tools/dungeon/mkxblock.py <spec.json|spec.toml> -o <out.xblock>

The spec is a tools/dungeon/build.py spec with a "layout" key, or just the layout object.  build.py runs this itself
when the spec has a "layout" (see README, "New map from scratch").  All positions are grid cells [col, row]; a cell is
150 units, the floor is centred on the origin and a cube's origin is its bottom centre, so a cube at level L stands
on z = 150 * L and its top is z = 150 * (L + 1).

  floor      {model, cols, rows, level = 2}            one layer of cubes
  perimeter  {model, height}                           wall on the floor's outer ring (optional)
  blocks     [{model, at, size = [1, 1], height = 1, level = floor + 1}]    platforms, pillars; stacked upward from level
  pc_spawn   {at, facing = 0}                          stands on the top cube of that cell, as do the portal and the NPCs
  portal     {at, id, target_map, target_portal, facing = 0, enabled = false, visible = false}
  npcs       [{at, npc, count = 1, id, facing = 0}]    with an id: EventSpawnPointNPC, spawned by trigger SpawnMonster;
                                                       without: SpawnPointNPC, spawned when the field starts
  triggers   [{file, id}]                              one MS2TriggerModel per script (XmlFilePath = file)
  template   xblock name, default 99999911             source of the lights and the PhysX scenes; nothing else is used
  seed       any value: repeatable entity ids (default: fresh random ones)

Cube and portal models must be shipped presets (flat/presets/ in Exported.m2d); the case is taken from the preset.  NPC
ids must exist in the server Xml.m2d.  Sources are those of newmap.py, including the spec's "sources".
"""
import argparse, os, random, re, subprocess, sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent / "mapedit"))
from newmap import M2D_DLL, REPO, SpecError, dotnet_env, load_spec, m2d, resolve_sources  # noqa: E402
from msmap.xblock import Entity, Prop, read_xblock, write_xblock, xblock_text  # noqa: E402

CELL = 150
BOILERPLATE = ("MS2AmbientLight_Outside_", "MS2DirectionalLight_Outside_", "MS2PhysXScene", "MS2PhysXSceneForItem")
NPC_PROXY = "urn:llid:1e1c2257-0000-0000-0000-000000000000"   # EventSpawnPointNPC's editor proxy in shipped maps
MARGIN_XY, MARGIN_DOWN, MARGIN_UP = 1500, 600, 3000           # MS2Bounding around the layout


class Sources:
    def __init__(self, spec):
        src = resolve_sources(spec)
        self.exported = src["resource"] / "Exported.m2d"
        self.server_xml = src["server_xml"]
        self.names = m2d("list", self.exported).split("\n")
        self.flats = {}
        for n in self.names:
            n = n.strip()
            if n.lower().startswith("flat/presets/") and n.lower().endswith(".flat"):
                self.flats.setdefault(Path(n).stem.lower(), n)
        self._npcs = None

    def cat(self, name):
        r = subprocess.run(["dotnet", str(M2D_DLL), "cat", str(self.exported), name], capture_output=True, env=dotnet_env())
        if r.returncode:
            raise SpecError(f"cannot read {name} from {self.exported}")
        return r.stdout

    def has_npc(self, npc):
        if self._npcs is None:
            self._npcs = {Path(n.strip()).stem for n in m2d("list", self.server_xml).split("\n")
                          if n.strip().lower().startswith("npc/")}
        return f"{int(npc):08d}" in self._npcs


class Layout:
    def __init__(self, lay, src):
        self.lay, self.src = lay, src
        self.models = {}
        self.rng = random.Random(str(lay["seed"])) if "seed" in lay else random.SystemRandom()
        self.ents = []
        self.top = {}     # (col, row) -> highest cube level

    def need(self, obj, key, where):
        if key not in obj:
            raise SpecError(f"layout {where}: missing {key!r}")
        return obj[key]

    def preset(self, model, cube=True):
        """The preset's exact name, or an error: only models that exist in the shipped flats are written."""
        if model in self.models:
            return self.models[model]
        path = self.src.flats.get(model.lower())
        if path is None:
            raise SpecError(f"layout: model {model!r} is not a shipped preset (flat/presets/ in Exported.m2d)")
        if cube and "/presets cube/" not in path.lower():
            raise SpecError(f"layout: model {model!r} is not a cube preset ({path})")
        m = re.search(rb'<model name="([^"]+)"', self.src.cat(path))
        if not m:
            raise SpecError(f"layout: {path} has no model name")
        self.models[model] = m.group(1).decode()
        return self.models[model]

    def eid(self):
        return f"{self.rng.getrandbits(128):032x}"

    def add(self, model, name, props, iterations=1):
        self.ents.append(Entity(self.eid(), model, name, iterations,
                                [Prop(k, v if isinstance(v, list) else [(None, str(v))]) for k, v in props]))

    def pos(self, cell, where):
        c, r = cell
        if not (0 <= c < self.cols and 0 <= r < self.rows):
            raise SpecError(f"layout {where}: cell {cell} is outside the {self.cols} x {self.rows} floor")
        return (c - self.cols // 2) * CELL, (r - self.rows // 2) * CELL

    def cube(self, model, c, r, level):
        x, y = self.pos((c, r), f"cube {model}")
        z = level * CELL
        cx, cy = x // CELL, y // CELL
        self.add(model, f"{model}[{cx}, {cy}, {level}]", [("Position", f"{x}, {y}, {z}"), ("Rotation", "0, 0, 0")])
        self.top[(c, r)] = max(self.top.get((c, r), level), level)

    def surface(self, cell, where):
        x, y = self.pos(cell, where)
        return x, y, (self.top[tuple(cell)] + 1) * CELL

    def rotation(self, obj):
        return f"0, 0, {obj.get('facing', 0)}"

    def boilerplate(self, entities):
        got = set()
        for e in entities:
            if e.model in BOILERPLATE:
                got.add(e.model)
                self.ents.append(Entity(self.eid(), e.model, e.name, e.iterations, e.props))
        missing = set(BOILERPLATE) - got
        if missing:
            raise SpecError(f"layout template: no {sorted(missing)}")

    def build(self):
        lay = self.lay
        fl = self.need(lay, "floor", "")
        self.cols, self.rows = self.need(fl, "cols", "floor"), self.need(fl, "rows", "floor")
        self.level = fl.get("level", 2)
        fm = self.preset(self.need(fl, "model", "floor"))
        for r in range(self.rows):
            for c in range(self.cols):
                self.cube(fm, c, r, self.level)
        if "perimeter" in lay:
            pm = self.preset(self.need(lay["perimeter"], "model", "perimeter"))
            for r in range(self.rows):
                for c in range(self.cols):
                    if c in (0, self.cols - 1) or r in (0, self.rows - 1):
                        for h in range(self.need(lay["perimeter"], "height", "perimeter")):
                            self.cube(pm, c, r, self.level + 1 + h)
        for i, b in enumerate(lay.get("blocks", [])):
            bm = self.preset(self.need(b, "model", f"blocks[{i}]"))
            c0, r0 = self.need(b, "at", f"blocks[{i}]")
            w, d = b.get("size", [1, 1])
            base = b.get("level", self.level + 1)
            for c in range(c0, c0 + w):
                for r in range(r0, r0 + d):
                    for h in range(b.get("height", 1)):
                        self.cube(bm, c, r, base + h)

        pc = self.need(lay, "pc_spawn", "")
        x, y, z = self.surface(self.need(pc, "at", "pc_spawn"), "pc_spawn")
        self.add("SpawnPointPC", "SpawnPointPC0", [("Position", f"{x}, {y}, {z}"), ("Rotation", self.rotation(pc))])

        if "portal" in lay:
            self.portal(lay["portal"])
        seen = set()
        for i, n in enumerate(lay.get("npcs", [])):
            self.npc(n, i, seen)
        cx, cy = (self.cols // 2, self.rows // 2)
        for t in lay.get("triggers", []):
            tid, f = self.need(t, "id", "triggers"), self.need(t, "file", "triggers")
            x, y, z = self.surface((cx, cy), "triggers")
            self.add("MS2TriggerModel", f"MS2TriggerModel_{tid}",
                     [("XmlFilePath", f), ("TriggerModelID", tid), ("Position", f"{x}, {y}, {z}"),
                      ("Rotation", "0, 0, 0"), ("Scale", "1")])

        # the bounding box encloses the layout; only its Position is ever set in shipped files
        xs = [(c - self.cols // 2) * CELL for c in (0, self.cols - 1)]
        ys = [(r - self.rows // 2) * CELL for r in (0, self.rows - 1)]
        zmax = (max(self.top.values()) + 1) * CELL
        self.add("MS2Bounding", "MS2Bounding0",
                 [("Position", f"{xs[0] - MARGIN_XY}, {ys[1] + MARGIN_XY}, {self.level * CELL - MARGIN_DOWN}")])
        self.add("MS2Bounding", "MS2Bounding1",
                 [("Position", f"{xs[1] + MARGIN_XY}, {ys[0] - MARGIN_XY}, {zmax + MARGIN_UP}")])

        tpl = str(lay.get("template", "99999911"))
        name = f"xblock/{tpl}.xblock"
        tmp = Path(os.environ.get("TMPDIR", "/tmp")) / f"mkxblock-{os.getpid()}.xblock"
        tmp.write_bytes(self.src.cat(name))
        try:
            self.boilerplate(list(read_xblock(str(tmp))))
        finally:
            tmp.unlink()
        self.ents.sort(key=lambda e: e.id)  # as in shipped files
        return self.ents

    def portal(self, p):
        model = self.preset(p.get("model", "Portal_entrance"), cube=False)
        pid = self.need(p, "id", "portal")
        x, y, z = self.surface(self.need(p, "at", "portal"), "portal")
        props = [("TargetFieldSN", self.need(p, "target_map", "portal")),
                 ("TargetPortalID", self.need(p, "target_portal", "portal")), ("PortalID", pid)]
        if not p.get("enabled", False):
            props.append(("PortalEnable", "False"))
        if not p.get("visible", False):
            props.append(("IsVisible", "False"))
        props += [("Position", f"{x}, {y}, {z}"), ("Rotation", self.rotation(p))]
        self.add(model, f"Portal_{pid}", props)

    def npc(self, n, i, seen):
        where = f"npcs[{i}]"
        npc, count = self.need(n, "npc", where), n.get("count", 1)
        if not self.src.has_npc(npc):
            raise SpecError(f"layout {where}: npc {npc} is not in {self.src.server_xml}")
        x, y, z = self.surface(self.need(n, "at", where), where)
        npcs = ("NpcList", [(str(npc), str(count))])
        pos = ("Position", f"{x}, {y}, {z}")
        rot = ("Rotation", self.rotation(n))
        if "id" not in n:
            self.add("SpawnPointNPC", f"{npc}_{i}", [("NpcCount", count), npcs, pos, rot])
            return
        if n["id"] in seen:
            raise SpecError(f"layout {where}: spawn id {n['id']} used twice")
        seen.add(n["id"])
        self.add("EventSpawnPointNPC", f"EventSpawnPointNPC_{n['id']}",
                 [("SpawnPointID", n["id"]), ("NpcCount", count), npcs, ("ProxyNifAsset", NPC_PROXY), pos, rot])


def generate(spec, out):
    """Write the xblock of spec["layout"] (or of a bare layout); returns (entity count, summary)."""
    lay = spec.get("layout", spec)
    if "floor" not in lay:
        raise SpecError('layout: no "floor"')
    ents = Layout(lay, Sources(spec)).build()
    out = Path(out)
    out.parent.mkdir(parents=True, exist_ok=True)
    write_xblock(ents, str(out))
    back = list(read_xblock(str(out)))
    if xblock_text(back) != out.read_bytes().decode("utf-8"):
        raise SpecError(f"{out}: does not round-trip through read_xblock")
    kinds = {}
    for e in back:
        kinds[e.model] = kinds.get(e.model, 0) + 1
    return len(back), kinds


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("spec")
    ap.add_argument("-o", "--out", required=True)
    a = ap.parse_args()
    try:
        n, kinds = generate(load_spec(a.spec), a.out)
    except SpecError as e:
        print(f"error: {e}", file=sys.stderr)
        return 1
    print(f"{a.out}: {n} entities: " + ", ".join(f"{k} {v}" for k, v in sorted(kinds.items())))
    return 0


if __name__ == "__main__":
    sys.exit(main())
