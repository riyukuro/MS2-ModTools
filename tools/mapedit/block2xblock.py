#!/usr/bin/env python3
"""Export Toolbench WorldBuilder .block files to runtime .xblock (re-implementation of XmlBlockExporter).

  block2xblock.py in.block... -o outdir [--index flatindex.json] [--dialect tab|toolbench] [--order block|ms2|toolbench]
"""
import argparse, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from msmap.models import ModelIndex
from msmap.convert import block_to_xblocks
from msmap.block import read_block


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('input', nargs='+')
    ap.add_argument('-o', '--outdir', required=True)
    ap.add_argument('--index')
    ap.add_argument('--dialect', default='tab', choices=['tab', 'toolbench'])
    ap.add_argument('--order', default='block', choices=['block', 'ms2', 'toolbench'],
                    help="property order per entity: block = as stored in the .block (default; round-trips MS2 xblocks), "
                         "ms2 = case-insensitive by name with Name last (MS2's shipped files), "
                         "toolbench = the model's flat order with Name last (what stock Toolbench exports)")
    ap.add_argument('--sort-properties', action='store_true', help=argparse.SUPPRESS)
    a = ap.parse_args(argv)
    index = ModelIndex(a.index)
    os.makedirs(a.outdir, exist_ok=True)
    for f in a.input:
        blk = read_block(f)
        for w in block_to_xblocks(blk, index, os.path.dirname(os.path.abspath(f)), a.outdir, a.dialect, 'ms2' if a.sort_properties else a.order):
            print(w)


if __name__ == '__main__':
    main()
