#!/usr/bin/env python3
"""Validate flat -> .model/.preset reconstruction.

  validate_models.py sdk  [--appdir <APPDIR>] [--show N]
      For every SDK .emtproj: rebuild each of its .model/.preset from the shipped .flat files, write them, parse
      them back, and compare against the original editor files (fields recoverable vs. not), and against the
      shipped .flat (re-flatten of the regenerated sources).
  validate_models.py ms2  --root out/maps/toolbench_root     (after flat2model.py has written the sources)
      Parse every generated .model/.preset, re-flatten, compare with the shipped .flat.
"""
import argparse, collections, json, os, re, sys, tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import modtools_config as cfg  # noqa: E402
from lxml import etree
from msmap import model as M
from msmap import flatrecon as R


def canon_flat(fd):
    """flatten() dict or Flat -> comparable structure (ids and invocation order ignored)."""
    return fd


def flat_dict_from_flat(f):
    return dict(name=f.name, traits=sorted(f.traits), mixins=[m[0] for m in f.mixins],
                props=[(p.name, p.type, tuple(p.sets), tuple(sorted(p.traits)), p.source) for p in f.props],
                behaviors=[(b['name'], b['type'], b['source'], tuple(sorted(b['traits']))) for b in f.behaviors],
                extradata=[(n, t, tuple(sorted(e, key=lambda x: x[1]))) for n, t, e in f.extradata])


def flat_dict_from_models(models, name):
    fd = M.flatten(models, name)
    return dict(name=fd['name'], traits=sorted(fd['traits']), mixins=fd['mixins'],
                props=[(p.name, p.val.ftype, tuple(p.val.sets), tuple(p.traits), p.source) for p in fd['props']],
                behaviors=[(b[0], b[1], b[2], tuple(b[3])) for b in fd['behaviors']],
                extradata=[(n, t, tuple(e)) for n, t, e in fd['extradata']])


def diff_flat(a, b):
    """a: reference (shipped), b: regenerated -> list of difference tags"""
    d = []
    if a['traits'] != b['traits']:
        d.append('traits')
    if a['mixins'] != b['mixins']:
        d.append('mixins' if sorted(a['mixins']) != sorted(b['mixins']) else 'mixin-order')
    pa = {p[0]: p for p in a['props']}
    pb = {p[0]: p for p in b['props']}
    if [p[0] for p in a['props']] != [p[0] for p in b['props']]:
        d.append('prop-names' if set(pa) != set(pb) else 'prop-order')
    for n in pa:
        if n in pb:
            x, y = pa[n], pb[n]
            if x[1] != y[1]:
                d.append('prop-type')
            if x[2] != y[2]:
                d.append('prop-value')
            if x[3] != y[3]:
                d.append('prop-traits')
            if x[4] != y[4]:
                d.append('prop-source')
    ba = {x[0]: x for x in a['behaviors'] if x[1] != 'Virtual'}
    bb = {x[0]: x for x in b['behaviors'] if x[1] != 'Virtual'}
    if ba != bb:
        d.append('behaviors')
    va = {x[0] for x in a['behaviors'] if x[1] == 'Virtual'}
    vb = {x[0] for x in b['behaviors'] if x[1] == 'Virtual'}
    if va != vb:
        d.append('virtual-behaviors')
    if a['extradata'] != b['extradata']:
        d.append('extradata')
    return d


# ---------------------------------------------------------------------------------------------------------
def read_emtproj(path):
    t = open(path, encoding='utf-8-sig').read()
    files = re.findall(r'<File\s+path="([^"]+)"', t)
    refs = re.findall(r'<Project\s+path="([^"]+)"', t)
    m = re.search(r'FlatModelsOutput"\s+property-type="String">\s*<value>([^<]*)</value>', t)
    return files, refs, (m.group(1) if m else '')


def closure(path, seen=None):
    seen = seen if seen is not None else {}
    path = os.path.normpath(path)
    if path in seen:
        return seen
    files, refs, flatdir = read_emtproj(path)
    seen[path] = (files, flatdir)
    for r in refs:
        rp = os.path.normpath(os.path.join(os.path.dirname(path), r.replace('\\', '/')))
        if os.path.exists(rp):
            closure(rp, seen)
    return seen


def meta_loss(orig):
    """what the original file carries that a .flat cannot (counts)"""
    c = collections.Counter()
    if orig.desc:
        c['model-description'] += 1
    if orig.folder:
        c['folder'] += 1
    for t in orig.traits:
        if t in M.TOOL_TIME_MODEL_TRAITS:
            c['tooltime-trait:' + t] += 1
    for p in orig.props:
        if p.desc:
            c['property-description'] += 1
        for r in (p.restriction or []):
            c['restriction:' + r] += 1
        for t in p.traits:
            if t in M.TOOL_TIME_PROPERTY_TRAITS:
                c['tooltime-property-trait:' + t] += 1
    for b in orig.behaviors:
        if b.desc:
            c['behavior-description'] += 1
    return c


def sdk(a):
    appdir = a.appdir or str(cfg.require('MS2_GAMEBRYO_APPDIR', 'validate_models.py sdk'))
    projs = []
    for dp, dn, fn in os.walk(appdir):
        for f in fn:
            if f.endswith('.emtproj'):
                projs.append(os.path.join(dp, f))
    tot = collections.Counter()
    ex = collections.defaultdict(list)
    losses = collections.Counter()
    seen_pairs = set()
    for proj in sorted(projs):
        clo = closure(proj)
        # shipped flats visible from this project
        flats = {}
        for p, (files, flatdir) in clo.items():
            d = os.path.normpath(os.path.join(os.path.dirname(p), flatdir.replace('\\', '/')))
            if os.path.isdir(d):
                for fn in os.listdir(d):
                    if fn.endswith('.flat'):
                        f = R.parse_flat(os.path.join(d, fn))
                        flats.setdefault(f.name, f)
        orig = {}
        for p, (files, _) in clo.items():
            for fl in files:
                if fl.endswith(('.model', '.preset')):
                    fp = os.path.normpath(os.path.join(os.path.dirname(p), fl.replace('\\', '/')))
                    if os.path.exists(fp):
                        om = M.parse_model(fp)
                        orig.setdefault(om.name, om)
        own = []
        files, _ = clo[os.path.normpath(proj)]
        for fl in files:
            if fl.endswith(('.model', '.preset')):
                own.append(re.sub(r'\.(model|preset)$', '', fl.replace('\\', '/').split('/')[-1]))
        rec = R.Recon(flats)
        regen = {}
        for n in flats:
            if n in orig:
                kind = orig[n].kind
                try:
                    regen[n] = rec.build_preset(n) if kind == 'preset' else rec.build(n)
                except Exception as e:
                    ex['build-error'].append('%s: %r' % (n, e))
        # round trip the regenerated sources through the XML writer/parser
        for n, m in list(regen.items()):
            tmp = tempfile.NamedTemporaryFile(suffix='.' + m.kind, delete=False)
            tmp.write(('﻿' + M.model_text(m)[1:] if False else M.model_text(m)).encode('utf-8'))
            tmp.close()
            try:
                pm = M.parse_model(tmp.name)
                pm.name = n
                regen[n] = pm
            finally:
                os.unlink(tmp.name)
        for n in own:
            if n not in regen or (proj, n) in seen_pairs:
                if n not in regen:
                    tot['no-flat'] += 1
                continue
            seen_pairs.add((proj, n))
            tot['models'] += 1
            o = orig[n]
            g = regen[n]
            losses.update(meta_loss(o))
            # semantic: flatten original vs shipped flat; flatten regenerated vs shipped flat
            sf = flat_dict_from_flat(flats[n])
            try:
                d_o = diff_flat(sf, flat_dict_from_models(orig, n))
            except Exception as e:
                d_o = ['flatten-error']
            try:
                d_g = diff_flat(sf, flat_dict_from_models(regen, n))
            except Exception as e:
                d_g = ['flatten-error:%r' % e]
            for t in d_o:
                tot['orig-flatten-vs-shipped:' + t] += 1
            for t in d_g:
                tot['regen-flatten-vs-shipped:' + t] += 1
                ex['regen-flatten-vs-shipped:' + t].append('%s (%s)' % (n, os.path.basename(proj)))
            if not d_g:
                tot['regen-flatten==shipped'] += 1
            if not d_o:
                tot['orig-flatten==shipped'] += 1
            # source-level structural diff
            diffs = struct_diff(o, g)
            for t in diffs:
                tot['src:' + t] += 1
                ex['src:' + t].append('%s (%s)' % (n, os.path.basename(proj)))
            if not diffs:
                tot['src-identical-modulo-metadata'] += 1
    print(json.dumps(dict(totals=dict(sorted(tot.items())), unrecoverable_in_originals=dict(sorted(losses.items()))),
                     indent=1))
    for k, v in sorted(ex.items()):
        print(k, len(v), v[:a.show])


def vkey(v):
    return (v.ftype, tuple(v.sets))


def struct_diff(o, g):
    d = []
    if [n for _, n in o.mixins] != [n for _, n in g.mixins]:
        d.append('mixins' if sorted(n for _, n in o.mixins) != sorted(n for _, n in g.mixins) else 'mixin-order')
    if o.kind != g.kind:
        d.append('kind')
    ot = sorted(t for t in o.traits if t not in M.TOOL_TIME_MODEL_TRAITS)
    if ot != sorted(g.traits):
        d.append('traits')
    op = {p.name: p for p in o.props}
    gp = {p.name: p for p in g.props}
    if [p.name for p in o.props] != [p.name for p in g.props]:
        d.append('own-prop-names' if set(op) != set(gp) else 'own-prop-order')
    for n in op:
        if n in gp:
            a, b = op[n], gp[n]
            if vkey(a.val) != vkey(b.val):
                d.append('own-prop-value')
            if (a.val.mtype or None) != (b.val.mtype or None):
                d.append('own-prop-editor-type(%s->%s)' % (a.val.mtype, b.val.mtype))
            if (a.val.kind or 'List' if a.val.is_collection else None) != (b.val.kind or 'List' if b.val.is_collection else None):
                d.append('own-prop-kind(%s->%s)' % (a.val.kind, b.val.kind))
            if sorted(t for t in a.traits if t not in M.TOOL_TIME_PROPERTY_TRAITS) != sorted(b.traits):
                d.append('own-prop-traits')
    oo = {p.name: p for p in o.poverrides}
    go = {p.name: p for p in g.poverrides}
    if set(oo) != set(go):
        d.append('overrides-names')
    for n in oo:
        if n in go:
            if vkey(oo[n].val) != vkey(go[n].val):
                d.append('override-value')
            if sorted(oo[n].trait_overrides) != sorted(go[n].trait_overrides):
                d.append('override-traits')
    ob = {b.name: b for b in o.behaviors}
    gb = {b.name: b for b in g.behaviors}
    if set(ob) != set(gb):
        d.append('behaviors-names')
    else:
        for n in ob:
            if ob[n].target != gb[n].target or sorted(ob[n].traits) != sorted(gb[n].traits):
                d.append('behavior-def')
    obo = {b.name: b for b in o.boverrides}
    gbo = {b.name: b for b in g.boverrides}
    if set(obo) != set(gbo):
        d.append('behavior-overrides')
    if [(n, t, sorted(e, key=lambda x: x[1])) for n, t, e in o.extradata] != \
            [(n, t, sorted(e, key=lambda x: x[1])) for n, t, e in g.extradata]:
        d.append('extradata')
    return sorted(set(d))


def ms2(a):
    root = a.root
    flats = R.load_flats(os.path.join(a.exported, 'flat'), jobs=a.jobs)
    gen = {}
    for dp, dn, fn in os.walk(os.path.join(root, 'Library')):
        for f in fn:
            if f.endswith(('.model', '.preset')):
                m = M.parse_model(os.path.join(dp, f))
                gen[m.name] = m
    print('generated sources:', len(gen), 'flats:', len(flats))
    tot = collections.Counter()
    ex = collections.defaultdict(list)
    for n, f in flats.items():
        kind = f.cat[0]            # 'library' | 'presets'
        if n not in gen:
            tot[kind + ':no-source'] += 1
            ex[kind + ':no-source'].append(n)
            continue
        try:
            d = diff_flat(flat_dict_from_flat(f), flat_dict_from_models(gen, n))
        except Exception as e:
            d = ['flatten-error']
            ex[kind + ':flatten-error'].append('%s %r' % (n, e))
        # 'virtual-behaviors' is an artifact of M.flatten (it does not drop empty-invocation Virtual behaviors the
        # way the C# exporter does), not a source difference.
        d = [t for t in d if t != 'virtual-behaviors']
        tot[kind + ':checked'] += 1
        if not d:
            tot[kind + ':identical'] += 1
        for t in d:
            tot['%s:%s' % (kind, t)] += 1
            if len(ex['%s:%s' % (kind, t)]) < 10:
                ex['%s:%s' % (kind, t)].append(n)
        # a preset flat is a snapshot of base-model-at-export-time + its overrides.  When the base model changed later
        # (mixins/props added or removed) the preset flat is stale; this is a property of the shipped data, not of
        # the reconstruction.  What must hold even then is: every value the preset overrides is reproduced.
        if kind == 'presets' and d and all(t in ('mixins', 'mixin-order', 'prop-names', 'prop-type', 'extradata',
                                                  'behaviors') for t in d):
            tot['presets:stale-base-only'] += 1
        elif d:
            tot[kind + ':REAL-DIFF'] += 1
            ex[kind + ':REAL-DIFF'].append('%s %s' % (n, d))
    print(json.dumps(dict(sorted(tot.items())), indent=1))
    for k, v in ex.items():
        print(k, v[:a.show])


if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('mode', choices=['sdk', 'ms2'])
    ap.add_argument('--appdir', help='Gamebryo SDK APPDIR (default: MS2_GAMEBRYO_APPDIR)')
    ap.add_argument('--root', default='out/maps/toolbench_root')
    ap.add_argument('--exported', default='out/maps/extracted/Exported')
    ap.add_argument('--show', type=int, default=6)
    ap.add_argument('-j', '--jobs', type=int, default=3)
    a = ap.parse_args()
    (sdk if a.mode == 'sdk' else ms2)(a)
