#!/usr/bin/env python3
"""Convert MS2 .xblock files into Toolbench WorldBuilder .block files.

  xblock2block.py in.xblock|dir  -o outdir [--index flatindex.json] [--layers none|category]
                 [--folders none|category] [--lib-root ..\\Library] [--model-ids ids.json] [--report r.json]
"""
import argparse, glob, json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from msmap.models import ModelIndex
from msmap.convert import xblock_to_block, Report
from msmap.block import write_block


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('input', nargs='+', help='.xblock file(s) or directories')
    ap.add_argument('-o', '--outdir', required=True)
    ap.add_argument('--index', help='flat model index json (tools/mapedit/flatindex.py)')
    ap.add_argument('--layers', default='none', choices=['none', 'category'])
    ap.add_argument('--folders', default='none', choices=['none', 'category'])
    ap.add_argument('--lib-root', default='..\\Library', help='relative path from the .block dir to the Library dir')
    ap.add_argument('--model-ids', default='auto',
                    help="'auto' (default): model-id = the GUID flat2model.py gives the .model file (known_guids.json, else uuid5); "
                         "'none': omit; or a json {modelName: guid}")
    ap.add_argument('--report')
    a = ap.parse_args(argv)
    index = ModelIndex(a.index)
    if a.model_ids == 'auto':
        from flat2model import GuidMap
        ids = GuidMap()
    elif a.model_ids == 'none':
        ids = None
    else:
        ids = json.load(open(a.model_ids))
    files = []
    for i in a.input:
        files += sorted(glob.glob(os.path.join(i, '*.xblock'))) if os.path.isdir(i) else [i]
    os.makedirs(a.outdir, exist_ok=True)
    rep = Report()
    for f in files:
        blk, r = xblock_to_block(f, index, layers=a.layers, folders=a.folders, lib_root=a.lib_root,
                                 model_ids=ids, report=rep)
        write_block(blk, os.path.join(a.outdir, blk.name + '.block'))
        print(f, '->', blk.name + '.block', file=sys.stderr)
    print(dict(rep.c))
    if a.report:
        json.dump({'counts': rep.c, 'samples': rep.samples}, open(a.report, 'w'), indent=1)


if __name__ == '__main__':
    main()
