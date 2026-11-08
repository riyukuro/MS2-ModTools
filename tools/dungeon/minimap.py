#!/usr/bin/env python3
"""minimap.py: a minimap (Image.m2d map/minimap/<id>.dds) and its mapxblock <minimap> camera for a map.

    python3 tools/dungeon/minimap.py --xblock <x.xblock | name> --map-id <id> -o <out>.dds [--xml <out>.xml] [--png <p>]
    python3 tools/dungeon/minimap.py --geom <g>.geom --solids <g>.solids --map-id <id> -o <out>.dds ...

The picture is a flat stylised render of the map's collision geometry (tools/tok tokgeom's .geom + .solids: the
blocks, whiteboxes and NIF collision meshes the .tok is built from), seen through the same orthographic isometric
camera the shipped minimaps use. It is not Nexon's art; the walkable area and the map outline line up with it.

Shipped format (all 1658 map/minimap/*.dds in the KMS client Image.m2d): 512x512, DXT5 (alpha = 0 off the map),
10 mip levels, plain DDS header (no DX10). The client finds the picture and the world -> picture mapping in the
client Xml.m2d mapxblock/<xb>.xml (CFieldDataCli::LoadXBlock -> FUN_140a55df0):

    <minimap>
        <image name="./Data/Resource/Image/Map/Minimap/<id>.dds" left= right= top= bottom= icon= />  crop of the screen, 1024x1024
        <frustum left= right= top= bottom= near= far= ortho="true" />   ortho half-extents in world units
        <screen left="0" right="1920" top="0" bottom="1080" />          the virtual screen the frustum maps onto
        <camera position="x, y, z" rotation="90.000000, 45.000000, -45.000000" />   degrees (1310 of 1551 maps)
    </minimap>

MinimapViewport (in MS2Recomp, src/ms2/client/ms2/minimapviewport.cpp) projects a world point p: the axes are the columns of
Gamebryo's FromEulerAnglesXYZ (clockwise rotations, Rx*Ry*Rz) -> (depth, up, right), with up/right negated when
rotation.z != 0; sx = (right.(p-cam) + R)/(R-L), sy = (up.(p-cam) + T)/(T-B); screen px = sx*W - image.left,
(1-sy)*H - image.top; the 1024 crop is the 512 DDS at half scale. Without a <minimap> node the client shows none.

--xblock runs tokgeom (built from tools/tok/extract with `dotnet build` on first use, cached in <MS2_OUT>/tokgeom-bin or
--tokgeom-dir). --camera <mapxblock.xml> reuses an existing <minimap> camera instead of fitting one (for comparing
with a shipped picture). --faces <f> (a `tokbuild --faces` dump) outlines the walkable ground on top.
"""
import argparse, io, math, os, re, struct, subprocess, sys, shutil
from pathlib import Path

try:
    from PIL import Image, ImageDraw
except ImportError:
    raise SystemExit("minimap.py needs Pillow (python3 -c 'import PIL')")

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools"))
import modtools_config as cfg  # noqa: E402

SCREEN = (1920, 1080)
CROP = 1024           # screen pixels of the <image> crop
SIZE = 512            # DDS size
ROT = (90.0, 45.0, -45.0)
NEAR, FAR = 10.0, 100000.0
MARGIN = 0.06         # fraction of the crop left empty around the map
TALL = 300.0          # solids taller than two cubes (whitebox walls/fences, invisible in game) are not drawn


# ---------------------------------------------------------------- camera

def _mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]


def _rots(x, y, z):
    """Gamebryo NiMatrix3::FromEulerAnglesXYZ: Rx*Ry*Rz with clockwise (transposed) rotations."""
    cx, sx, cy, sy, cz, sz = math.cos(x), math.sin(x), math.cos(y), math.sin(y), math.cos(z), math.sin(z)
    rx = [[1, 0, 0], [0, cx, sx], [0, -sx, cx]]
    ry = [[cy, 0, -sy], [0, 1, 0], [sy, 0, cy]]
    rz = [[cz, sz, 0], [-sz, cz, 0], [0, 0, 1]]
    return _mul(rx, _mul(ry, rz))


def _unit(v):
    n = math.sqrt(sum(c * c for c in v)) or 1.0
    return [c / n for c in v]


def axes(rot):
    """(right, depth, up) like MinimapViewport::FUN_141486a20."""
    d = math.pi / 180.0
    x, y, z = rot
    if abs(z) < 1e-5:
        m = _rots(-x * d, -y * d, z * d)
        col = lambda j: [m[0][j], m[1][j], m[2][j]]
        return _unit(col(2)), _unit(col(0)), _unit(col(1))
    m = _rots(x * d, y * d, z * d)
    col = lambda j: [m[0][j], m[1][j], m[2][j]]
    return _unit([-c for c in col(2)]), _unit(col(0)), _unit([-c for c in col(1)])


def dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


class Camera:
    def __init__(self, pos, rot, frustum, screen, image):
        self.pos, self.rot, self.fr, self.sc, self.im = pos, rot, frustum, screen, image  # fr/sc/im: l r t b
        self.ax, self.ay, self.az = axes(rot)

    @classmethod
    def from_xml(cls, text):
        m = re.search(r"<minimap>.*?</minimap>", text, re.S)
        if not m:
            raise SystemExit("no <minimap> node")
        t = m.group(0)
        def rect(tag):
            e = re.search(r"<%s\s[^>]*>" % tag, t).group(0)
            return [float(re.search(r'\b%s="([^"]*)"' % k, e).group(1)) for k in ("left", "right", "top", "bottom")]
        vec = lambda a: [float(v) for v in re.search(r'\b%s="([^"]*)"' % a, t).group(1).split(",")]
        if 'ortho="true"' not in t:
            raise SystemExit("<minimap> frustum is not ortho; only ortho cameras are supported")
        return cls(vec("position"), vec("rotation"), rect("frustum"), rect("screen"), rect("image"))

    @classmethod
    def fit(cls, points, rot=ROT):
        """Ortho camera that frames the points' projection in the middle 1024x1024 of a 1920x1080 screen."""
        ax, ay, az = axes(rot)
        xs = [dot(ax, p) for p in points]
        ys = [dot(az, p) for p in points]
        ds = [dot(ay, p) for p in points]
        cx, cy = (min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2
        ext = max(max(xs) - min(xs), max(ys) - min(ys), 300.0) / (1 - 2 * MARGIN)
        upp = round(ext / CROP, 4)                       # world units per screen pixel
        hw, hh = round(upp * SCREEN[0] / 2, 2), round(upp * SCREEN[1] / 2, 2)
        back = (max(ds) - min(ds)) / 2 + 5000.0          # depth range well inside near..far
        dc = (min(ds) + max(ds)) / 2
        pos = [round(cx * ax[i] + cy * az[i] + (dc - back) * ay[i], 6) for i in range(3)]
        il, it = (SCREEN[0] - CROP) / 2, (SCREEN[1] - CROP) / 2
        return cls(pos, list(rot), [-hw, hw, hh, -hh], [0, SCREEN[0], 0, SCREEN[1]], [il, il + CROP, it, it + CROP])

    def projector(self, scale):
        """world point -> (pixel x, pixel y, depth) in a crop rendered at `scale` x screen pixels."""
        L, R, T, B = self.fr
        W, H = abs(self.sc[1] - self.sc[0]), abs(self.sc[3] - self.sc[2])
        il, it = self.im[0], self.im[2]
        ax, ay, az, c = self.ax, self.ay, self.az, self.pos
        ox, oy, od = dot(ax, c), dot(az, c), dot(ay, c)
        kx, ky = W / (R - L), H / (T - B)
        def P(p):
            sx = (p[0] * ax[0] + p[1] * ax[1] + p[2] * ax[2] - ox + R) * kx    # exe: X*0.5+0.5, X = 2u/(R-L)+(R+L)/(R-L)
            sy = (p[0] * az[0] + p[1] * az[1] + p[2] * az[2] - oy + T) * ky
            return ((sx - il) * scale, (H - sy - it) * scale, p[0] * ay[0] + p[1] * ay[1] + p[2] * ay[2] - od)
        return P

    def xml(self, map_id, nl="\r\n"):
        f = lambda v: ("%.2f" % v).rstrip("0").rstrip(".") if v != int(v) else str(int(v))
        r = lambda q: 'left="%s" right="%s" top="%s" bottom="%s"' % tuple(f(v) for v in q)
        lines = [
            "\t<minimap>",
            '\t\t<image name="./Data/Resource/Image/Map/Minimap/%08d.dds" %s icon=".\\Data\\Resource\\Image\\Map\\Icon\\" />'
            % (map_id, r(self.im)),
            '\t\t<frustum %s near="%s" far="%s" ortho="true" />' % (r(self.fr), f(NEAR), f(FAR)),
            '\t\t<screen %s />' % r(self.sc),
            '\t\t<camera position="%s" rotation="%s" />' % (", ".join("%.6f" % v for v in self.pos),
                                                           ", ".join("%.6f" % v for v in self.rot)),
            "\t</minimap>",
        ]
        return nl.join(lines) + nl


# ---------------------------------------------------------------- geometry input

def read_geom(path):
    verts, tris = [], []
    for line in open(path):
        a = line.split()
        if not a:
            continue
        if a[0] == "v":
            verts.append((float(a[1]), float(a[2]), float(a[3])))
        elif a[0] == "f":
            tris.append((int(a[1]), int(a[2]), int(a[3])))
    return [(verts[i], verts[j], verts[k]) for i, j, k in tris]


def read_solids(path):
    toks = open(path).read().split()
    out, i = [], 0
    while i < len(toks):
        if toks[i] != "s":
            raise SystemExit(f"{path}: expected 's' at token {i}")
        n = int(toks[i + 1])
        v = [float(t) for t in toks[i + 2:i + 2 + 3 * n]]
        out.append([tuple(v[k:k + 3]) for k in range(0, 3 * n, 3)])
        i += 2 + 3 * n
    return out


def read_faces(path):
    """tokbuild --faces: 't x y z x y z x y z n0 n1 n2' (n = -1: no neighbour across edge k)."""
    out = []
    for line in open(path):
        a = line.split()
        if a and a[0] == "t":
            v = [float(t) for t in a[1:10]]
            out.append(([tuple(v[k:k + 3]) for k in (0, 3, 6)], [int(t) for t in a[10:13]]))
    return out


# box corners as tokgeom writes them: index = 4*xi + 2*yi + zi
BOX_FACES = ((0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3))


def _sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def _hull2d(pts):
    pts = sorted(set(pts))
    if len(pts) < 3:
        return pts
    def half(seq):
        h = []
        for p in seq:
            while len(h) >= 2 and (h[-1][0] - h[-2][0]) * (p[1] - h[-2][1]) - (h[-1][1] - h[-2][1]) * (p[0] - h[-2][0]) <= 0:
                h.pop()
            h.append(p)
        return h
    lo, hi = half(pts), half(reversed(pts))
    return lo[:-1] + hi[:-1]


# ---------------------------------------------------------------- render

TOP_LO, TOP_HI = (104, 150, 112), (232, 222, 178)   # top faces: low (green) .. high (sand)
LIGHT = _unit((-0.45, -0.25, 0.86))
WALK_EDGE = (60, 52, 40, 255)


def _shade(n, z, zr):
    t = 0.0 if zr[1] <= zr[0] else min(1.0, max(0.0, (z - zr[0]) / (zr[1] - zr[0])))
    base = [TOP_LO[i] + (TOP_HI[i] - TOP_LO[i]) * t for i in range(3)]
    n = _unit(n)
    if n[2] < 0.5:                       # walls: darker, by which side faces the light
        k = 0.55 + 0.25 * max(0.0, dot(n, LIGHT) / max(1e-6, math.hypot(LIGHT[0], LIGHT[1])))
        base = [c * k for c in base]
    else:
        base = [c * (0.85 + 0.15 * max(0.0, dot(n, LIGHT))) for c in base]
    return tuple(int(min(255, c)) for c in base) + (255,)


def render(cam, tris, solids, faces=None, scale=1.0):
    """RGBA picture of the crop (CROP*scale square)."""
    P = cam.projector(scale)
    view = cam.ay
    zs = [p[2] for t in tris for p in t] + [p[2] for s in solids for p in s]
    if faces:
        zs += [p[2] for t, _ in faces for p in t]
    zs.sort()
    zr = (zs[len(zs) // 50], zs[-1 - len(zs) // 50]) if zs else (0, 1)
    items = []                           # (depth, [(poly, rgba)])
    for t in tris:
        n = _cross(_sub(t[1], t[0]), _sub(t[2], t[0]))
        if dot(n, n) < 1e-9:
            continue
        if dot(n, view) > 0:             # double-sided: face the camera
            n = (-n[0], -n[1], -n[2])
        pr = [P(p) for p in t]
        items.append((sum(q[2] for q in pr) / 3, [([(q[0], q[1]) for q in pr], _shade(n, max(p[2] for p in t), zr))]))
    for s in solids:
        pr = [P(p) for p in s]
        depth = sum(q[2] for q in pr) / len(pr)
        c = tuple(sum(p[i] for p in s) / len(s) for i in range(3))
        polys = []
        if len(s) == 8:
            for f in BOX_FACES:
                fc = tuple(sum(s[k][i] for k in f) / 4 for i in range(3))
                n = _cross(_sub(s[f[1]], s[f[0]]), _sub(s[f[2]], s[f[0]]))
                if dot(n, _sub(fc, c)) < 0:
                    n = (-n[0], -n[1], -n[2])
                if dot(n, n) < 1e-9 or dot(n, view) >= 0:
                    continue
                polys.append(([(pr[k][0], pr[k][1]) for k in f], _shade(n, max(s[k][2] for k in f), zr)))
        else:
            zt = max(p[2] for p in s)
            polys.append((_hull2d([(q[0], q[1]) for q in pr]), _shade((1, 1, 0), zt, zr)))
            top = [(q[0], q[1]) for p, q in zip(s, pr) if p[2] >= zt - 2]
            if len(top) >= 3:
                polys.append((_hull2d(top), _shade((0, 0, 1), zt, zr)))
        items.append((depth, polys))
    items.sort(key=lambda it: -it[0])    # far first
    n = int(round(CROP * scale))
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    for _, polys in items:
        for poly, col in polys:
            if len(poly) >= 3:
                edge = tuple(int(c * 0.72) for c in col[:3]) + (255,)
                d.polygon(poly, fill=col, outline=edge)
    if faces:                            # walkable outline: the edges with no neighbour
        w = max(1, int(round(2 * scale)))
        for t, nb in faces:
            pr = [P(p) for p in t]
            for k in range(3):
                if nb[k] == -1:
                    a, b = pr[k], pr[(k + 1) % 3]
                    d.line([(a[0], a[1]), (b[0], b[1])], fill=WALK_EDGE, width=w)
    return img


# ---------------------------------------------------------------- DDS

def _bc3(level):
    w, h = level.size
    if w % 4 or h % 4:                   # 2x2 / 1x1 mips: one block, edge-replicated
        pad = Image.new("RGBA", (max(4, w), max(4, h)))
        pad.paste(level.resize((max(4, w), max(4, h)), Image.NEAREST))
        level = pad
    b = io.BytesIO()
    level.save(b, "DDS", pixel_format="DXT5")
    data = b.getvalue()
    if data[:4] != b"DDS " or data[84:88] != b"DXT5":
        raise SystemExit("Pillow did not write a DXT5 DDS")
    return data[128:]


def write_dds(img, path):
    """512x512 DXT5 with the full mip chain, header laid out like the shipped minimaps."""
    levels = []
    lv = img
    while True:
        levels.append(_bc3(lv))
        if lv.size == (1, 1):
            break
        lv = lv.resize((max(1, lv.size[0] // 2), max(1, lv.size[1] // 2)), Image.LANCZOS)
    w, h = img.size
    hdr = bytearray(128)
    hdr[0:4] = b"DDS "
    struct.pack_into("<7I", hdr, 4, 124, 0x21007, h, w, 0, 0, len(levels))
    struct.pack_into("<2I4s", hdr, 76, 32, 4, b"DXT5")
    struct.pack_into("<I", hdr, 108, 0x401008)
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_bytes(bytes(hdr) + b"".join(levels))


# ---------------------------------------------------------------- tokgeom

def tokgeom_dll(cache):
    if not shutil.which("dotnet"):
        raise SystemExit("dotnet not found: tokgeom (tools/tok/extract) needs the .NET 10 SDK on PATH")
    dll = Path(cache) / "tokgeom.dll"
    src = REPO / "tools/tok/extract"
    newest = max(f.stat().st_mtime for f in (src / "Program.cs", src / "tokgeom.csproj"))
    if not dll.is_file() or dll.stat().st_mtime < newest:
        r = subprocess.run(["dotnet", "build", str(src), "-c", "Release", "-o", str(cache)], capture_output=True, text=True)
        if r.returncode != 0 or not dll.is_file():
            raise SystemExit(f"dotnet build tools/tok/extract failed:\n{r.stdout[-3000:]}{r.stderr[-2000:]}")
    return dll


def run_tokgeom(resource, xblock, out_stem, cache, project=None):
    cmd = ["dotnet", str(tokgeom_dll(cache)), str(resource), str(xblock), str(out_stem)]
    if project:
        cmd += ["--project", str(project)]
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0:
        raise SystemExit(f"tokgeom failed ({r.returncode}):\n{r.stdout[-2000:]}{r.stderr[-2000:]}")
    return Path(f"{out_stem}.geom"), Path(f"{out_stem}.solids")


def default_cache():
    return cfg.out_dir() / "tokgeom-bin"


# ---------------------------------------------------------------- main

def make(geom, solids, map_id, out, xml_out=None, png=None, camera_xml=None, faces=None, keep_tall=False):
    tris = read_geom(geom) if geom else []
    sol = read_solids(solids) if solids else []
    if not keep_tall:
        sol = [s for s in sol if max(p[2] for p in s) - min(p[2] for p in s) <= TALL]
    fc = read_faces(faces) if faces else None
    pts = [p for t in tris for p in t] + [p for s in sol for p in s]
    if not pts:
        raise SystemExit("no geometry: the .geom and .solids are empty")
    cam = Camera.from_xml(Path(camera_xml).read_text(encoding="utf-8-sig")) if camera_xml else Camera.fit(pts)
    crop = abs(cam.im[1] - cam.im[0])
    big = render(cam, tris, sol, fc, scale=2 * SIZE / crop)       # 2x supersampled
    img = big.resize((SIZE, SIZE), Image.LANCZOS)
    write_dds(img, out)
    if png:
        img.save(png)
    block = cam.xml(map_id)
    if xml_out:
        Path(xml_out).write_text(block, newline="")
    return block


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--map-id", type=int, required=True)
    ap.add_argument("-o", "--out", required=True, help="output .dds")
    ap.add_argument("--xml", help="write the mapxblock <minimap> node here")
    ap.add_argument("--png", help="also write the 512x512 picture as PNG")
    g = ap.add_argument_group("ground")
    g.add_argument("--xblock", help=".xblock file or Exported.m2d xblock name (runs tokgeom)")
    g.add_argument("--project", help="Toolbench project folder for tokgeom --project")
    g.add_argument("--resource", help="client Data/Resource for tokgeom (default: Resource under MS2_CLIENT_DATA)")
    g.add_argument("--tokgeom-dir", default=None, help="tokgeom build/cache dir")
    g.add_argument("--geom")
    g.add_argument("--solids")
    g.add_argument("--faces", help="tokbuild --faces dump: outline the walkable ground")
    ap.add_argument("--keep-tall", action="store_true", help="also draw solids taller than %d (whitebox walls)" % TALL)
    ap.add_argument("--camera", help="mapxblock xml whose <minimap> camera to use instead of fitting one")
    a = ap.parse_args()
    geom, solids = a.geom, a.solids
    if a.xblock:
        cache = Path(a.tokgeom_dir) if a.tokgeom_dir else default_cache()
        stem = Path(a.out).with_suffix("")
        resource = a.resource or cfg.require("MS2_CLIENT_DATA", "minimap.py --xblock without --resource") / "Resource"
        geom, solids = run_tokgeom(resource, a.xblock, f"{stem}.tokgeom", cache, a.project)
    if not geom and not solids:
        ap.error("give --xblock, or --geom/--solids")
    block = make(geom, solids, a.map_id, a.out, a.xml, a.png, a.camera, a.faces, a.keep_tall)
    if not a.xml:
        sys.stdout.write(block)
    return 0


if __name__ == "__main__":
    sys.exit(main())
