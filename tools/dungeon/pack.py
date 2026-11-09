#!/usr/bin/env python3
"""pack.py: repack the archives touched by newmap.py into COPIES (originals are only read).

    python3 tools/dungeon/pack.py <newmap outdir> [-o <packdir>]      (default <outdir>/packed)

Reads <outdir>/manifest.json and runs `m2d repack <orig> <outdir>/<area> <copy> --add` per area that has files:

  <packdir>/client/Data/Xml.m2d(.m2h)                      client-xml  (KMS client Xml.m2d)
  <packdir>/client/Data/Resource/Exported.m2d              exported    (shared by client and server)
  <packdir>/client/Data/Resource/PrecomputedTerrain.m2d    terrain
  <packdir>/client/Data/Resource/Image.m2d                 image
  <packdir>/client/Data/Resource/asset-web-metadata.m2d    awm
  <packdir>/client/Data/Resource/Model/Npc.m2d, Textures.m2d   npc-model, textures (boss.py "model_dir"; client only)
  <packdir>/server-data/Xml.m2d, Server.m2d                server-xml, server (a symlink to the original if unchanged)
  <packdir>/server-data/Resource/                          symlinks: repacked copies above, every other entry of
                                                           the original Resource dir (read-only use)

<packdir>/server-data is a complete MS2_DATA_FOLDER for Maple2.File.Ingest (see README.md).  To use the client
files, copy them over a client COPY's Data/ (never over your original install).
"""
import argparse, json, os, sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from newmap import m2d  # noqa: E402

TARGET = {
    "client-xml": "client/Data/Xml.m2d",
    "exported": "client/Data/Resource/Exported.m2d",
    "terrain": "client/Data/Resource/PrecomputedTerrain.m2d",
    "image": "client/Data/Resource/Image.m2d",
    "awm": "client/Data/Resource/asset-web-metadata.m2d",
    "npc-model": "client/Data/Resource/Model/Npc.m2d",
    "textures": "client/Data/Resource/Model/Textures.m2d",
    "server-xml": "server-data/Xml.m2d",
    "server": "server-data/Server.m2d",
}
RESOURCE_AREAS = {"exported": "Exported", "terrain": "PrecomputedTerrain", "image": "Image", "awm": "asset-web-metadata"}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("outdir")
    ap.add_argument("-o", "--pack", help="output dir (default <outdir>/packed)")
    a = ap.parse_args()
    out = Path(a.outdir)
    man = json.loads((out / "manifest.json").read_text())
    pack = Path(a.pack) if a.pack else out / "packed"
    origs = {Path(p).resolve() for p in man["archives"].values()}
    origs |= {p.with_suffix(".m2h") for p in origs}

    for area, rel in TARGET.items():
        dst = pack / rel
        if area not in man["archives"]:
            continue  # optional area (an extra archive newmap.py never opened)
        orig = Path(man["archives"][area])
        if dst.resolve() in origs or dst.with_suffix(".m2h").resolve() in origs:
            sys.exit(f"refusing to write over an original archive: {dst}")
        dst.parent.mkdir(parents=True, exist_ok=True)
        for f in (dst, dst.with_suffix(".m2h")):
            if f.is_symlink() or f.exists():
                f.unlink()
        if not man["written"].get(area):
            # nothing to add. The ingest still needs the file in MS2_DATA_FOLDER; a client copy keeps its own.
            if rel.startswith("server-data/"):
                for f, src in ((dst, orig), (dst.with_suffix(".m2h"), orig.with_suffix(".m2h"))):
                    f.symlink_to(src.resolve())
                print(f"{area}: unchanged, {dst} -> {orig}")
            continue
        print(f"{area}: {orig} + {len(man['written'][area])} files -> {dst}")
        print("  " + m2d("repack", orig, out / area, dst, "--add").strip().replace("\n", "\n  "))

    # server-data/Resource: per-entry symlinks into the original Resource dir, repacked copies preferred
    res_orig = Path(man["archives"]["exported"]).parent
    res = pack / "server-data/Resource"
    res.mkdir(parents=True, exist_ok=True)
    for e in res_orig.iterdir():
        link = res / e.name
        target = e.resolve()
        stem, suf = e.stem, e.suffix
        for area, s in RESOURCE_AREAS.items():
            if stem == s and suf in (".m2d", ".m2h") and man["written"].get(area):
                target = (pack / TARGET[area]).with_suffix(suf).resolve()
        if link.is_symlink() or link.exists():
            link.unlink()
        link.symlink_to(target)
    print(f"server-data: {pack / 'server-data'} (MS2_DATA_FOLDER for ingest)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
