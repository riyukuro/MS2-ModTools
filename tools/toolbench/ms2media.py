#!/usr/bin/env python3
"""Resolve the asset references (urn:llid) of MS2 maps to files in the Resource/*.m2d archives and extract them
into a Toolbench content root so the asset web resolves them (for WorldBuilder's viewport).

    ms2media.py ROOT BLOCK_OR_XBLOCK... [--awm DIR] [--media DIR] [--stock-materials]

Prereq: --awm DIR = asset-web-metadata.m2d extracted (`m2d extract Resource/asset-web-metadata.m2d DIR`, needs relpath.nt).

How MS2 assets are addressed (from Resource/asset-web-metadata.m2d, the original asset-web DB, and
Source/Tools/AssetController/LogicalIdTagger.cpp):
  * flats/xblocks reference assets as `urn:llid:XXXXXXXX-0000-0000-0000-000000000000`;
  * llid = FNV-1a 32 of the lower-cased relpath (relative to the content root = the client's Resource/ dir, e.g.
    "/model/map/cave/cube/ca_deform_brick_a01.nif"), minus class dirs kr/jp/cn/en (CaseSensitiveHashIds=False,
    PersistentIds=False) - checked for 82,893 of 83,183 llid.nt entries, the rest are locale variants.
  * So placing a file at ROOT/<relpath> is enough: Toolbench's asset controller re-scans ROOT and computes the same llid.
  * /Model/<Arch>/<rest> lives in Resource/Model/<Arch>.m2d as "<rest>" lower-cased.
Textures referenced by the NIFs (NiSourceTexture file names) are resolved by name against the same relpath table.

--stock-materials: MS2's NIFs name Nexon's code-defined materials (MS2StandardMaterial, MS2CharacterMaterial, ...),
which stock Toolbench does not have (the meshes then draw in the magenta error colour).  With this option the NIFs
placed in ROOT are rewritten copies whose fixed-string table maps every MS2*Material to NiStandardMaterial (a preview
approximation for the editor only; out/maps/media keeps the original bytes).  It also links every texture a NIF
names next to that NIF: NiSourceTexture resolves bare file names only via NiSearchPath (as given, cwd, NIF dir +
path, NIF dir, default path), while MS2 keeps them in /Model/<Arch>/Textures/... (the client resolves them itself).
"""
import argparse
import os
import re
import subprocess
import sys
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from nifcheck import parse_bytes, rename_strings  # noqa: E402

R = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(R, "tools"))
import modtools_config as cfg  # noqa: E402
TRIPLE = re.compile(r'<urn:uuid:([^>]+)> <[^>]+> "(.*)"\.')
LLID = re.compile(r"urn:llid:([0-9a-f]{8}-0000-0000-0000-000000000000)")
TEXNAME = re.compile(rb"[\x20-\x7e]{1,200}?\.dds", re.I)


def fnv1a(s):
    h = 0x811C9DC5
    for c in s.encode("utf-8"):
        h = ((h ^ c) * 0x01000193) & 0xFFFFFFFF
    return "%08x-0000-0000-0000-000000000000" % h


def load_nt(path):
    out = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            m = TRIPLE.match(line)
            if m:
                out[m.group(1)] = m.group(2)
    return out


def flat_index(root):
    idx = {}
    for d, _, files in os.walk(os.path.join(root, "Exported", "flat")):
        for f in files:
            if f.endswith(".flat"):
                idx.setdefault(f[:-5].lower(), os.path.join(d, f))
    return idx


def m2d_extract(arch, outdir, names):
    """names: m2d entry names (lower-case, '/'-separated). Uses `m2d extract` with prefixes in batches."""
    env = dict(os.environ, DOTNET_ROLL_FORWARD="Major")
    names = sorted(set(names))
    for i in range(0, len(names), 400):
        subprocess.run(["dotnet", str(cfg.ensure_m2d()), "extract", arch, outdir] + names[i:i + 400], check=True, env=env,
                       stdout=subprocess.DEVNULL)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("root")
    ap.add_argument("blocks", nargs="+")
    ap.add_argument("--awm", default=os.path.join(str(cfg.out_dir()), "maps", "media", "awm"))
    ap.add_argument("--media", default=os.path.join(str(cfg.out_dir()), "maps", "media"))
    ap.add_argument("--stock-materials", action="store_true")
    a = ap.parse_args()

    rel = load_nt(os.path.join(a.awm, "relpath.nt"))
    by_llid = {}
    by_name = defaultdict(list)          # texture file name (lower) -> relpaths
    for p in rel.values():
        by_llid.setdefault(fnv1a(re.sub(r"/(kr|jp|cn|en)/", "/", p.lower())), p)
        by_name[p.rsplit("/", 1)[-1].lower()].append(p)
    flats = flat_index(a.root)

    # 1. llids of the blocks + of every model/preset flat they instantiate (transitively via mixins)
    llids, todo, seen = set(), [], set()
    for b in a.blocks:
        s = open(b, encoding="utf-8-sig").read()
        llids |= set(LLID.findall(s))
        todo += [n.lower() for n in re.findall(r'(?:modelName|model-name)="([^"]+)"', s)]
    while todo:
        n = todo.pop()
        if n in seen or n not in flats:
            continue
        seen.add(n)
        s = open(flats[n], encoding="utf-8-sig").read()
        llids |= set(LLID.findall(s))
        todo += [m.lower() for m in re.findall(r'<mixin[^>]*name="([^"]+)"', s)]
    want = {}
    unresolved = []
    for l in sorted(llids):
        p = by_llid.get(l)
        (unresolved.append(l) if p is None else want.__setitem__(l, p))
    print(f"{len(seen)} flats, {len(llids)} llids, {len(want)} resolved, {len(unresolved)} unresolved")

    # 2. extract the referenced /Model/* files, grouped by archive
    def extract(relpaths):
        per = defaultdict(list)
        for p in relpaths:
            parts = p.split("/")
            if len(parts) > 3 and parts[1] == "Model":
                per[parts[2]].append("/".join(parts[3:]).lower())
        for arch, names in per.items():
            src = os.path.join(str(cfg.require("MS2_CLIENT_DATA", "ms2media.py")), "Resource", "Model", arch + ".m2d")
            out = os.path.join(a.media, "Model", arch)
            missing = [n for n in names if not os.path.exists(os.path.join(out, n))]
            if missing:
                m2d_extract(src, out, missing)
        return per

    per = extract(want.values())
    # 3. textures named inside the extracted NIFs
    tex = set()
    notex = set()
    nif_tex = defaultdict(set)              # nif relpath -> texture relpaths
    for p in want.values():
        if not p.lower().endswith(".nif"):
            continue
        f = os.path.join(a.media, "Model", p.split("/")[2], "/".join(p.split("/")[3:]).lower())
        if not os.path.exists(f):
            continue
        for t in TEXNAME.findall(open(f, "rb").read()):
            name = t.decode("ascii").replace("\\", "/").rsplit("/", 1)[-1].lower()
            cands = by_name.get(name)
            if cands:
                tex.update(cands)
                same = [c for c in cands if c.split("/")[:3] == p.split("/")[:3]]
                nif_tex[p].add((same or cands)[0])
            else:
                notex.add(name)
    extract(tex)
    print(f"{len(tex)} textures, {len(notex)} texture names not in the asset web")

    # 4. link into ROOT/<relpath> with the original casing (Wine is case-insensitive, the hash is lower-cased)
    n = conv = 0
    for p in sorted(set(want.values()) | tex):
        parts = p.split("/")
        if len(parts) <= 3 or parts[1] != "Model":
            continue
        src = os.path.join(a.media, "Model", parts[2], "/".join(parts[3:]).lower())
        dst = os.path.join(a.root, *parts[1:])
        if not os.path.exists(src):
            continue
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        if a.stock_materials and p.lower().endswith(".nif"):
            b = open(src, "rb").read()
            names = [x for x in parse_bytes(b)["strings"] if x.startswith("MS2") and x.endswith("Material")]
            if names:
                if os.path.lexists(dst):
                    os.unlink(dst)          # never write through a hard link into the media folder
                with open(dst, "wb") as f:
                    f.write(rename_strings(b, {x: "NiStandardMaterial" for x in names}))
                conv += 1
                continue
        if not os.path.lexists(dst):
            os.link(src, dst) if os.stat(src).st_dev == os.stat(os.path.dirname(dst)).st_dev else os.symlink(src, dst)
            n += 1
    near = 0
    if a.stock_materials:
        for p, ts in nif_tex.items():
            d = os.path.dirname(os.path.join(a.root, *p.split("/")[1:]))
            for t in ts:
                src = os.path.join(a.root, *t.split("/")[1:])
                dst = os.path.join(d, t.rsplit("/", 1)[-1])
                if os.path.exists(src) and not os.path.lexists(dst):
                    os.link(src, dst)
                    near += 1
    print(f"linked {n} files into {a.root}, {conv} NIFs written with stock materials, "
          f"{near} textures linked next to their NIFs")
    with open(os.path.join(a.root, "ms2media-unresolved.txt"), "w") as f:
        f.write("\n".join(unresolved + ["# textures:"] + sorted(notex)) + "\n")


if __name__ == "__main__":
    sys.exit(main())
