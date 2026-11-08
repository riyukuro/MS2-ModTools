#!/usr/bin/env python3
"""Write a minimal Toolbench game solution (.gsl, version 10) listing every *.emtproj and *.block under a root.

    mkgsl.py ROOT [NAME.gsl]

Paths in the solution are relative to ROOT (backslash-separated, as Toolbench writes them). Sets ContentRoot "." (asset web root = ROOT; ROOT also needs an asset-web-config/ dir, copy
APPDIR/Media/asset-web-config). Format copied from
Samples/GameDemos/MangledMetal/Data/MangledMetal.gsl (Hierarchy of <Item content-type=... parent=solution-id>).
Deterministic GUIDs (uuid5 over the relative path) so regenerating does not churn the file.
"""
import os
import sys
import uuid

NS = uuid.UUID("6f1b8c6e-9b1e-4c0f-9a59-6d5332000001")
TYPES = {
    ".emtproj": "Emergent.Toolbench.EntityModeling.EntityModelingProject",
    ".block": "Emergent.Toolbench.WorldBuilder.BlockDocument",
}


def main():
    root = os.path.abspath(sys.argv[1])
    name = sys.argv[2] if len(sys.argv) > 2 else os.path.basename(root) + ".gsl"
    sol_id = str(uuid.uuid5(NS, name))
    items = []
    for d, dirs, files in os.walk(root, followlinks=False):
        dirs.sort()
        for f in sorted(files):
            ext = os.path.splitext(f)[1].lower()
            if ext in TYPES:
                rel = os.path.relpath(os.path.join(d, f), root).replace("/", "\\")
                items.append((TYPES[ext], f, str(uuid.uuid5(NS, rel)), rel))
    out = ['﻿<?xml version="1.0" encoding="utf-8"?>', "<Solution", '  version="10">',
           "  <tool-settings", '    version="1">', "    <setting",
           '      id="Emergent.Toolbench.GameSolution.ContentRoot"', '      property-type="String">',
           "      <value>.</value>", "    </setting>", "  </tool-settings>",
           "  <Hierarchy", '    version="1">',
           "    <Item", '      content-type="Emergent.Toolbench.GameSolution.GameSolutionDocument"',
           f'      name="{name}"', f'      id="{sol_id}"', f'      filepath="{name}" />']
    for ctype, fname, iid, rel in items:
        out += ["    <Item", f'      content-type="{ctype}"', f'      name="{fname}"', f'      id="{iid}"',
                f'      parent="{sol_id}"', f'      filepath="{rel}" />']
    out += ["  </Hierarchy>", "</Solution>", ""]
    with open(os.path.join(root, name), "w", encoding="utf-8", newline="\r\n") as fh:
        fh.write("\n".join(out))
    print(f"{os.path.join(root, name)}: {len(items)} items")


if __name__ == "__main__":
    main()
