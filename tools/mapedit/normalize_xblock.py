#!/usr/bin/env python3
"""Convert a Toolbench-exported .xblock into MS2's shipped dialect so repacked files diff minimally.

  normalize_xblock.py in.xblock... -o outdir [--original shipped.xblock | shipped_dir] [--order ms2|keep]
                      [--dialect tab|toolbench|auto]

Stock Toolbench (XmlEntityExporter) writes spaced multi-line XML (`toolbench` dialect) and, per entity, the properties
in MixinProperties order (the model's flat order, 'Name' last).  MS2's shipped xblocks use the compact tab dialect and
case-insensitively sorted properties ('Name' last).  This reads the exported file and rewrites it:
  * dialect    : tab (default; auto = same as --original when given)
  * properties : if --original has the entity (same id) with the same set of property names, its order is used,
                 else --order ms2 (case-insensitive sort, 'Name' last) or keep (as exported)
  * entities   : sorted by id (the exporter already does this)
With --original the result for an unedited map is byte-identical to the shipped file (verified on 02000001_tw_tria:
only the edited entity differs).
"""
import argparse, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from msmap.xblock import read_xblock, write_xblock, detect_dialect, reorder_props


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('input', nargs='+')
    ap.add_argument('-o', '--outdir', required=True)
    ap.add_argument('--original', help='shipped .xblock file, or a directory with <name>.xblock files')
    ap.add_argument('--order', default='ms2', choices=['ms2', 'keep'])
    ap.add_argument('--dialect', default='tab', choices=['tab', 'toolbench', 'auto'])
    a = ap.parse_args(argv)
    os.makedirs(a.outdir, exist_ok=True)
    for f in a.input:
        base = os.path.basename(f)
        orig = None
        if a.original:
            op = os.path.join(a.original, base) if os.path.isdir(a.original) else a.original
            if os.path.exists(op):
                orig = op
        ents = list(read_xblock(f))
        orig_order = {e.id: [p.name for p in e.props] for e in read_xblock(orig)} if orig else None
        reorder_props(ents, a.order, orig_order)
        ents.sort(key=lambda e: e.id)
        dialect = a.dialect
        if dialect == 'auto':
            dialect = detect_dialect(orig) if orig else 'tab'
        out = os.path.join(a.outdir, base)
        write_xblock(ents, out, dialect)
        print(out, len(ents), 'entities', 'order from original' if orig else 'order=' + a.order, dialect)


if __name__ == '__main__':
    main()
