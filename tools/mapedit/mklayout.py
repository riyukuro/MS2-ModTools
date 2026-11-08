#!/usr/bin/env python3
"""Assemble a Toolbench-style project root from the extracted MS2 archives.

  mklayout.py <root> [--extracted out/maps/extracted] [--maps 02000001_tw_tria gamemode ...] [--layers category] [--folders category]

  <root>/Library/<lib>/*.emtproj,*.diagram,*.preset,*.nif,*.lua   copied from Library.m2d (35 MB)
  <root>/Exported -> <extracted>/Exported                         symlink: flat/{library,presets}/*.flat + xblock/*.xblock
  <root>/Maps/<name>.block (+ .layer files)                       xblock2block output, ExportFolder = ..\\Exported\\xblock
  <root>/MISSING.txt                                              project files Toolbench would need that MS2 does not ship
"""
import argparse, os, re, shutil, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from msmap.models import ModelIndex
from msmap.convert import xblock_to_block, Report
from msmap.block import write_block
import flat2model


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('root')
    ap.add_argument('--extracted', default='out/maps/extracted')
    ap.add_argument('--maps', nargs='*', default=['gamemode'])
    ap.add_argument('--layers', default='none')
    ap.add_argument('--folders', default='none')
    ap.add_argument('--index')
    ap.add_argument('--no-models', action='store_true', help='do not (re)generate .model/.preset files with flat2model')
    ap.add_argument('-j', '--jobs', type=int, default=3)
    a = ap.parse_args()
    ex = os.path.abspath(a.extracted)
    root = os.path.abspath(a.root)
    os.makedirs(root, exist_ok=True)
    lib = os.path.join(root, 'Library')
    if not os.path.exists(lib):
        shutil.copytree(os.path.join(ex, 'Library'), lib)
    exp = os.path.join(root, 'Exported')
    if not os.path.lexists(exp):
        os.symlink(os.path.join(ex, 'Exported'), exp)
    if not a.no_models:
        stat, rec = flat2model.build(root, a.extracted, a.jobs)
        print(dict(stat))
    guids = flat2model.GuidMap()
    index = ModelIndex(a.index)
    maps = os.path.join(root, 'Maps')
    os.makedirs(maps, exist_ok=True)
    rep = Report()
    need = set()
    for m in a.maps:
        blk, _ = xblock_to_block(os.path.join(ex, 'Exported', 'xblock', m + '.xblock'), index, layers=a.layers,
                                 folders=a.folders, report=rep, export_folder='..\\Exported\\xblock', model_ids=guids)
        write_block(blk, os.path.join(maps, blk.name + '.block'))
        need.update(blk.references)
        print('wrote', blk.name)
    missing = []
    for ref in sorted(need):
        p = os.path.join(maps, ref.replace('\\', '/'))
        if not os.path.exists(flat2model.ci_path(os.path.normpath(p))):
            missing.append('project not shipped (must be synthesised): ' + ref)
    for emt in sorted(os.popen('find "%s" -name "*.emtproj"' % lib).read().split()):
        txt = open(emt, encoding='utf-8-sig').read()
        for f in re.findall(r'<File\s+path="([^"]+)"', txt):
            if not os.path.exists(flat2model.ci_path(os.path.join(os.path.dirname(emt), f.replace('\\', '/')))):
                missing.append('%s: %s' % (os.path.relpath(emt, root), f))
    open(os.path.join(root, 'MISSING.txt'), 'w').write('\n'.join(missing) + '\n')
    print(len(missing), 'missing project items -> MISSING.txt')


if __name__ == '__main__':
    main()
