#!/usr/bin/env python3
"""Index MS2 .flat entity-model files into a compact JSON (name -> mixins/properties).

Usage: flatindex.py <Exported dir containing flat/> <out.json> [-j N]
"""
import os, sys, json, re
from multiprocessing import Pool
from lxml import etree

def parse_flat(path):
    parser = etree.XMLParser(huge_tree=True, remove_blank_text=True)
    root = etree.parse(path, parser).getroot()
    d = {'name': root.get('name'), 'id': int(root.get('id')), 'file': None,
         'traits': [], 'mixins': [], 'props': [], 'behaviors': []}
    for c in root:
        t = c.tag
        if t == 'trait':
            d['traits'].append(c.get('value'))
        elif t == 'mixin':
            d['mixins'].append([c.get('name'), int(c.get('id')), c.get('type')])
        elif t == 'property':
            p = {'name': c.get('name'), 'type': c.get('type'), 'id': int(c.get('id')), 'source': c.get('source')}
            sets = [(s.get('index'), s.get('value')) for s in c if s.tag == 'set']
            if sets:
                p['default'] = sets[0][1] if (len(sets) == 1 and sets[0][0] is None) else sets
            d['props'].append(p)
        elif t == 'behavior':
            d['behaviors'].append([c.get('name'), int(c.get('id')), c.get('type'), c.get('source')])
    return d

def work(args):
    root, rel = args
    try:
        d = parse_flat(os.path.join(root, rel))
        d['file'] = rel
        return d
    except Exception as e:
        return {'file': rel, 'error': str(e)}

def main():
    root, out = sys.argv[1], sys.argv[2]
    j = int(sys.argv[sys.argv.index('-j') + 1]) if '-j' in sys.argv else 3
    files = []
    for dp, dn, fn in os.walk(os.path.join(root, 'flat')):
        for f in fn:
            if f.endswith('.flat'):
                files.append((root, os.path.relpath(os.path.join(dp, f), root).replace(os.sep, '/')))
    files.sort()
    with Pool(j) as p:
        res = p.map(work, files, chunksize=64)
    models = {}
    errs = []
    for d in res:
        if 'error' in d:
            errs.append(d); continue
        if d['name'] in models:
            errs.append({'file': d['file'], 'error': 'duplicate model name', 'other': models[d['name']]['file']})
        models[d['name']] = d
    os.makedirs(os.path.dirname(out) or '.', exist_ok=True)
    json.dump({'models': models, 'errors': errs}, open(out, 'w'))
    print(len(models), 'models,', len(errs), 'errors')

if __name__ == '__main__':
    main()
