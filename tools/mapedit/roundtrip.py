#!/usr/bin/env python3
"""Round-trip test: xblock -> .block (on disk) -> xblock, compare with the original.

For every input .xblock reports
  * byte-identical            (output file == input file, same writer dialect)
  * semantic-identical        (independent lxml parse of both; entities keyed by id, attributes, property
                               name/index/value multiset per entity; ignores whitespace/formatting and
                               property order)
  * order-identical           (same, but property order must match too)
and aggregates lossy-field counters from the importer.

  roundtrip.py [files|dirs...] [-j 3] [--layers none|category] [--folders none|category] [--keep DIR] [--limit N]
"""
import argparse, collections, glob, json, os, shutil, sys, tempfile, time
from multiprocessing import Pool
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from lxml import etree
from msmap.models import ModelIndex
from msmap.convert import xblock_to_block, block_to_xblocks, Report
from msmap.block import write_block, read_block
from msmap.xblock import detect_dialect

_index = None


def semantic(path):
    """Independent parse: returns (list in file order of entity tuples) using a fully materialised tree."""
    root = etree.parse(path, etree.XMLParser(huge_tree=True)).getroot()
    assert root.tag == 'game' and len(root) == 1 and root[0].tag == 'entitySet', 'unexpected root structure'
    ents = []
    for e in root[0]:
        attrs = tuple(sorted(e.attrib.items()))
        props = []
        for p in e:
            assert p.tag == 'property'
            props.append((tuple(sorted(p.attrib.items())),
                          tuple(tuple(sorted(s.attrib.items())) for s in p)))
        ents.append((attrs, props))
    return ents


def compare(a, b):
    """-> (semantic_equal, order_equal)"""
    if len(a) != len(b):
        return False, False
    da = {dict(x[0])['id']: x for x in a}
    db = {dict(x[0])['id']: x for x in b}
    if da.keys() != db.keys():
        return False, False
    order_ok = [dict(x[0])['id'] for x in a] == [dict(x[0])['id'] for x in b]
    sem = True
    for k, x in da.items():
        y = db[k]
        if x[0] != y[0]:
            sem = False
            break
        if sorted(x[1]) != sorted(y[1]):
            sem = False
            break
        if x[1] != y[1]:
            order_ok = False
    return sem, sem and order_ok


def work(args):
    path, tmp, layers, folders, keep, indexpath = args
    global _index
    if _index is None:
        _index = ModelIndex(indexpath)
    name = os.path.splitext(os.path.basename(path))[0]
    t0 = time.time()
    d = os.path.join(tmp, name)
    os.makedirs(d, exist_ok=True)
    res = {'file': os.path.basename(path), 'ok_bytes': False, 'ok_sem': False, 'ok_order': False, 'error': None}
    try:
        dialect = detect_dialect(path)
        blk, rep = xblock_to_block(path, _index, layers=layers, folders=folders)
        bp = os.path.join(d, name + '.block')
        write_block(blk, bp)
        res['block_bytes'] = os.path.getsize(bp) + sum(os.path.getsize(os.path.join(d, f)) for f in os.listdir(d) if f.endswith('.layer'))
        del blk
        blk2 = read_block(bp)
        outd = os.path.join(d, 'out')
        os.makedirs(outd, exist_ok=True)
        written = block_to_xblocks(blk2, _index, d, outd, dialect)
        assert len(written) == 1, written
        res['ok_bytes'] = open(written[0], 'rb').read() == open(path, 'rb').read()
        sa, sb = semantic(path), semantic(written[0])
        sem, order = compare(sa, sb)
        res['ok_sem'], res['ok_order'] = sem, order
        if not sem:
            # tier 2: equal after removing a trailing Name==entity-name property that only the re-export has
            # (Toolbench adds Name for models mixing in Nameable; old xblocks predate that)
            def strip(ents, other):
                oth = {dict(x[0])['id']: x for x in other}
                out = []
                for a_, p_ in ents:
                    d = dict(a_)
                    nm = (('name', 'Name'),)
                    o = oth[d['id']]
                    keep = [q for q in p_ if not (q[0] == nm and q[1] == ((('value', d['name']),),)
                                                  and not any(r[0] == nm for r in o[1]))]
                    out.append((a_, keep))
                return out
            res['ok_sem_modulo_Name'] = compare(strip(sb, sa), sa)[0]
        res['counts'] = dict(rep.c)
        res['samples'] = {k: v for k, v in rep.samples.items()}
    except Exception as e:
        import traceback
        res['error'] = traceback.format_exc()
    res['secs'] = round(time.time() - t0, 2)
    if keep:
        os.makedirs(keep, exist_ok=True)
        shutil.copytree(d, os.path.join(keep, name), dirs_exist_ok=True)
    shutil.rmtree(d, ignore_errors=True)
    return res


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('input', nargs='*', default=['out/maps/extracted/Exported/xblock'])
    ap.add_argument('-j', type=int, default=3)
    ap.add_argument('--layers', default='none')
    ap.add_argument('--folders', default='none')
    ap.add_argument('--keep')
    ap.add_argument('--limit', type=int)
    ap.add_argument('--index')
    ap.add_argument('--every', type=int, default=1, help='test every Nth file')
    ap.add_argument('--tmp', default='out/maps/rt/tmp')
    ap.add_argument('--out', default='out/maps/rt/roundtrip.json')
    a = ap.parse_args()
    files = []
    for i in a.input:
        files += sorted(glob.glob(os.path.join(i, '*.xblock'))) if os.path.isdir(i) else [i]
    files = files[::a.every][:a.limit]
    os.makedirs(a.tmp, exist_ok=True)
    jobs = [(f, a.tmp, a.layers, a.folders, a.keep, a.index) for f in files]
    results = []
    t0 = time.time()
    with Pool(a.j) as p:
        for i, r in enumerate(p.imap_unordered(work, jobs, chunksize=1)):
            results.append(r)
            if r['error'] or not r['ok_sem']:
                print('FAIL', r['file'], (r['error'] or '')[-300:], flush=True)
            if (i + 1) % 100 == 0:
                print('%d/%d  %.0fs' % (i + 1, len(jobs), time.time() - t0), flush=True)
    n = len(results)
    tot = collections.Counter()
    samples = collections.defaultdict(list)
    files_with = collections.Counter()
    for r in results:
        for k, v in r.get('counts', {}).items():
            tot[k] += v
            files_with[k] += 1
        for k, v in r.get('samples', {}).items():
            if len(samples[k]) < 8:
                samples[k] += v[:2]
    summ = {
        'files': n,
        'errors': sum(1 for r in results if r['error']),
        'byte_identical': sum(r['ok_bytes'] for r in results),
        'semantic_identical': sum(r['ok_sem'] for r in results),
        'semantic_identical_modulo_added_Name': sum(bool(r['ok_sem'] or r.get('ok_sem_modulo_Name')) for r in results),
        'order_identical': sum(r['ok_order'] for r in results),
        'not_byte_identical': [r['file'] for r in results if not r['ok_bytes']][:50],
        'not_semantic_identical': [r['file'] for r in results if not r['ok_sem']],
        'importer_counters_total': tot,
        'importer_counters_files': files_with,
        'samples': samples,
        'block_bytes_total': sum(r.get('block_bytes', 0) for r in results),
        'seconds': round(time.time() - t0),
    }
    json.dump({'summary': summ, 'results': results}, open(a.out, 'w'), indent=1)
    print(json.dumps(summ, indent=1)[:6000])


if __name__ == '__main__':
    main()
