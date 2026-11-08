#!/usr/bin/env python3
"""Static check of a generated Library tree against the rules Toolbench enforces on load.

  lint_models.py --root out/maps/toolbench_root

Reports (these are the conditions behind Toolbench's load-time messages):
  * mixin SourceID that resolves to no model/preset, or whose SourceName differs from the target's name
  * PropertyOverride / BehaviorOverride on a name no ancestor defines ("was removed because it is no longer defined
    in a parent model")
  * duplicate model IDs / names
  * .diagram EntityModelID that resolves to no model ("Diagram '...' contains an unknown model")
"""
import argparse, collections, os, re, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from msmap import model as M


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--root', default='out/maps/toolbench_root')
    a = ap.parse_args()
    lib = os.path.join(a.root, 'Library')
    models, by_id, dup = {}, {}, collections.Counter()
    for dp, dn, fn in os.walk(lib):
        for f in fn:
            if f.lower().endswith(('.model', '.preset')):
                m = M.parse_model(os.path.join(dp, f))
                if m.guid in by_id:
                    dup[m.guid] += 1
                models[m.name] = m
                by_id[m.guid] = m
    print('models/presets:', len(models), 'duplicate ids:', sum(dup.values()))
    issues = collections.defaultdict(list)

    memo = {}

    def defined(m, seen=()):
        r = memo.get(m.guid)
        if r is None:
            props, behs = set(p.name for p in m.props), set(b.name for b in m.behaviors)
            for sid, sname in m.mixins:
                t = by_id.get(sid)
                if t is None or t.guid in seen:
                    continue
                p2, b2 = defined(t, seen + (m.guid,))
                props |= p2
                behs |= b2
            r = memo[m.guid] = (props, behs)
        return r

    for m in models.values():
        parent_props, parent_behs = set(), set()
        for sid, sname in m.mixins:
            t = by_id.get(sid)
            if t is None:
                issues['mixin-unresolved'].append('%s -> %s' % (m.name, sname))
                continue
            if t.name != sname:
                issues['mixin-name-mismatch'].append('%s: %s vs %s' % (m.name, sname, t.name))
            pp, bb = defined(t)
            parent_props |= pp
            parent_behs |= bb
        for o in m.poverrides:
            if o.name not in parent_props:
                issues['property-override-not-defined'].append('%s.%s' % (m.name, o.name))
        for o in m.boverrides:
            if o.name not in parent_behs:
                issues['behavior-override-not-defined'].append('%s.%s' % (m.name, o.name))
    ids = set(by_id)
    for dp, dn, fn in os.walk(lib):
        for f in fn:
            if f.lower().endswith('.diagram'):
                t = open(os.path.join(dp, f), encoding='utf-8-sig').read()
                for g in re.findall(r'EntityModelID="([^"]+)"', t):
                    if g not in ids:
                        issues['diagram-unknown-model'].append('%s %s' % (f, g))
    for k, v in sorted(issues.items()):
        print('%-32s %d  %s' % (k, len(v), v[:5]))
    if not issues:
        print('clean')
    return 1 if issues else 0


if __name__ == '__main__':
    sys.exit(main())
