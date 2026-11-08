#!/usr/bin/env python3
"""Lint trigger XML scripts against what the game server (Maple2.Server.Game TriggerStorage) does.

  lint.py <script.xml|dir>... [--xblock NAME|auto|xblock/x.xblock|file.xblock] [--root DIR]
          [--format auto|raw|server] [--allow FILE] [--strict] [--json] [--summary FILE] [-q]

Two script forms are read:
  raw     shipped form (Xml.m2d trigger/<xblock>/<name>.xml): CamelCase/Korean names, `!Name`,
          <import>. It is normalised with normalize.py the way the ingest does it, then checked.
  server  the ingested form TriggerStorage reads (snake_case names, no imports), e.g. a
          Maple2.Server.Game/DebugTriggers/<xblock>/<name>.xml file.
  auto    raw when a name has an upper-case or non-ASCII letter or a '!', or there is an <import>.

--xblock checks trigger object IDs against the map's entities (cache from xblockindex.py build).
'auto' takes the xblock name from the script's folder (trigger/<xblock>/<name>.xml and
DebugTriggers/<xblock>/<name>.xml both use it).

Each finding has a level and a code. errors: the server drops the script, a part of it, or the
ingest fails. warnings: the server reads the script but something is ignored, defaulted or
probably wrong. Findings matched by the allowlist (known-issues.json) are reported as 'allowed'
and do not count. Exit status: 1 if any error is left, 2 on bad usage, else 0.
"""
import argparse, fnmatch, json, os, re, sys
from collections import Counter

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import normalize  # noqa: E402
from normalize import M  # noqa: E402

INT_RE = re.compile(r'^\s*[+-]?\d+\s*$')
FLOAT_RE = re.compile(r'^\s*[+-]?(\d[\d,]*\.?\d*|\.\d+)([eE][+-]?\d+)?\s*$')
FLOAT_WORDS = {'nan', 'infinity', '-infinity', '+infinity', '∞', '-∞'}
SPAWN_KINDS = ('npc_spawn', 'event_npc_spawn', 'pc_spawn', 'region_spawn')
# ref (schema) -> xblock entity kinds the server lookup can find
REF_KINDS = {
    'box': ('box',), 'spawn': SPAWN_KINDS, 'item_spawn': ('item_spawn',), 'portal': ('portal',),
    'interact': ('interact',), 'breakable': ('breakable',),
}
# (kind, name, arg) -> (other arg, value): the arg is an object id only when the other arg has that value
COND_REF = {('action', 'set_dialogue', 'spawn_id'): ('type', '1')}
STRICT_REF = {('action', 'spawn_monster', 'spawn_ids'): ('event_npc_spawn',)}
RANGE_LIMIT = 100000


def dotnet_int(v):
    """int.TryParse (NumberStyles.Integer): whitespace, sign, digits, Int32 range."""
    if v is None or not INT_RE.match(v):
        return None
    n = int(v.strip())
    return n if -2 ** 31 <= n < 2 ** 31 else None


def dotnet_float(v):
    if v is None:
        return None
    s = v.strip()
    if s.lower() in FLOAT_WORDS:
        return 0.0
    if not FLOAT_RE.match(v):
        return None
    try:
        return float(s.replace(',', ''))
    except ValueError:
        return None


def int_array(v):
    """TriggerFunctionMapping.ParseIntArray. Returns (ids, error-or-None, dropped parts)."""
    if v is None or v == '':
        return [], None, []
    if v == 'all':
        return [-1], None, []

    def rng(a, b):
        if a > b:
            a, b = b, a
        if b - a > RANGE_LIMIT:
            return list(range(a, a + RANGE_LIMIT))
        return list(range(a, b + 1))

    if ',' in v:
        out, dropped = [], []
        for part in v.split(','):
            if '-' in part:
                rp = part.split('-')
                a = dotnet_int(rp[0]) if len(rp) == 2 else None
                b = dotnet_int(rp[1]) if len(rp) == 2 else None
                if a is not None and b is not None:
                    out += rng(a, b)
                else:
                    dropped.append(part)
            else:
                n = dotnet_int(part)
                if n is None:
                    dropped.append(part)
                else:
                    out.append(n)
        return out, None, dropped
    if '-' in v:
        rp = v.split('-')
        if len(rp) == 2:
            a, b = dotnet_int(rp[0]), dotnet_int(rp[1])
            if a is not None and b is not None:
                return rng(a, b), None, []
    n = dotnet_int(v)
    if n is None:
        return [], 'int.Parse(%r) throws' % v, []
    return [n], None, []


def enum_parse(v, names):
    """Enum.Parse(type, v, ignoreCase: true) succeeds?"""
    s = v.strip()
    if INT_RE.match(s):
        return True
    low = {k.lower() for k in names}
    parts = [p.strip().lower() for p in s.split(',')]
    return bool(s) and all(p in low for p in parts)


def is_raw(doc):
    if doc.find('.//import') is not None:
        return True
    for el in doc.iter():
        if el.tag in ('action', 'condition'):
            n = el.get('name') or ''
            if n.startswith('!') or any(c.isupper() or ord(c) > 127 for c in n):
                return True
    return False


def line_of(el):
    return int(el.get(M + 'line', 0) or 0)


def raw_name(el):
    return el.get(M + 'raw_name', el.get('name'))


class Linter:
    def __init__(self, schema, cache, xblock=None, root=None, fmt='auto'):
        self.schema = schema
        self.norm = normalize.Normalizer(schema)
        self.cache = cache
        self.xblock_opt = xblock
        self.root_opt = root
        self.fmt = fmt
        self.unknown_xblocks = set()
        self._targets = {}

    # ---- helpers -------------------------------------------------------------------------
    def guess_root(self, path):
        parts = os.path.abspath(path).split(os.sep)
        if len(parts) >= 3 and parts[-3].lower() == 'trigger':
            return os.sep.join(parts[:-3]) or os.sep
        return None

    def import_targets(self, root):
        """Pack names that some script under root imports (comments stripped)."""
        if root not in self._targets:
            pk = self.norm.packs(root)
            tg = {}
            for name in pk.names:
                try:
                    with open(pk.file(name), 'rb') as f:
                        text = f.read().decode('utf-8', 'replace')
                except OSError:
                    continue
                if '<import' not in text:
                    continue
                text = re.sub(r'<!--.*?-->', '', text, flags=re.S)
                for m in re.finditer(r'<import\b[^>]*\bpath="([^"]*)"', text):
                    pp = normalize.import_pack_path(m.group(1))
                    hit = pk.resolve(pp) if pp else None
                    if hit:
                        tg.setdefault(hit, []).append(name)
            self._targets[root] = tg
        return self._targets[root]

    def pack_name(self, path, root):
        rel = os.path.relpath(os.path.abspath(path), os.path.abspath(root)).replace(os.sep, '/')
        return rel

    def xblock_for(self, path):
        import xblockindex
        spec = self.xblock_opt
        if not spec:
            return None, None
        if spec == 'auto':
            spec = os.path.basename(os.path.dirname(os.path.abspath(path)))
        idx = xblockindex.load_index(spec, self.cache)
        return spec, idx

    # ---- one file ------------------------------------------------------------------------
    def lint_file(self, path):
        out = []

        def add(level, code, el_or_line, msg):
            ln = el_or_line if isinstance(el_or_line, int) else line_of(el_or_line)
            src = None if isinstance(el_or_line, int) else el_or_line.get(M + 'src')
            if code == 'maple2-gap' and not msg.startswith(('Maple2 server (WIP) currently', 'Maple2’s parser')):
                msg = 'Maple2 server (WIP) currently ' + msg[0].lower() + msg[1:]
            out.append({'path': path, 'line': ln, 'level': level, 'code': code,
                        'msg': msg + (' [in imported %s]' % src if src else '')})

        try:
            doc = normalize.parse_xml(path)
        except normalize.ParseError as e:
            add('error', 'parse', e.line or 0, 'XML parse error: %s' % e)
            return out, None
        except OSError as e:
            add('error', 'parse', 0, str(e))
            return out, None
        if doc is None:
            add('error', 'parse', 0, 'empty document')
            return out, None
        fmt = self.fmt if self.fmt != 'auto' else ('raw' if is_raw(doc) else 'server')
        issues = []
        self.importers = []
        if fmt == 'raw':
            root = self.root_opt or self.guess_root(path)
            if root:
                self.importers = self.import_targets(root).get(self.pack_name(path, root), [])
        if fmt == 'raw':
            root = self.root_opt or self.guess_root(path)
            if root is None and doc.find('.//import') is not None:
                add('warning', 'import-root', 0, 'cannot find the trigger/ root for <import>: pass --root')
            self.norm.merge_imports(doc, root, issues)
            for st in doc.iter('state'):
                if st.get(M + 'file'):
                    for el in st.iter():
                        el.set(M + 'src', st.get(M + 'file'))
            self.norm.normalize(doc, issues)
        elif doc.find('.//import') is not None:
            add('warning', 'import-ignored', doc.find('.//import'),
                'TriggerStorage does not read <import>; only the ingest merges imports')
        for lv, ln, msg in issues:
            add(lv, 'import' if 'import' in msg else 'ingest', ln, msg)

        if doc.tag != 'ms2':
            add('error', 'root', doc, 'root element is <%s>, the server needs <ms2>' % doc.tag)
            return out, fmt

        spec, idx = self.xblock_for(path) if self.xblock_opt else (None, None)
        if self.xblock_opt and idx is None:
            add('warning', 'xblock-missing', 0, 'xblock %r is not in the index; object IDs not checked' % spec)

        # --- structure + states
        states, order = {}, []
        for ch in doc:
            if ch.tag != 'state':
                add('warning', 'ignored-node', ch, '<%s> under <ms2> is ignored by the server' % ch.tag)
                continue
            n = ch.get('name')
            if not n:
                add('warning', 'state-noname', ch, 'state without a name is skipped')
                continue
            if n in states:
                add('error', 'state-dup', ch, 'duplicate state %r' % raw_name(ch))
                continue
            states[n] = ch
            order.append(n)
        if not order:
            add('warning', 'no-states', doc, 'script has no states')

        edges = {n: set() for n in order}
        for n in order:
            st = states[n]
            imported = bool(st.get(M + 'src'))
            for ch in st:
                if ch.tag == 'onEnter':
                    for c in ch:
                        if c.tag == 'action':
                            self.check_action(c, add, idx, states, edges[n], imported)
                        elif c.tag == 'transition':
                            self.check_transition(c, add, states, edges[n])
                        else:
                            add('warning', 'ignored-node', c, '<%s> in onEnter is ignored' % c.tag)
                elif ch.tag == 'onExit':
                    for c in ch:
                        if c.tag == 'action':
                            self.check_action(c, add, idx, states, edges[n], imported)
                        else:
                            add('warning', 'ignored-node', c, '<%s> in onExit is ignored' % c.tag)
                elif ch.tag == 'condition':
                    self.check_condition(ch, add, idx, states, edges[n], imported)
                else:
                    add('warning', 'ignored-node', ch, '<%s> in a state is ignored' % ch.tag)

        # --- reachability from the first state
        if order:
            seen, todo = {order[0]}, [order[0]]
            while todo:
                for t in edges[todo.pop()]:
                    if t in edges and t not in seen:
                        seen.add(t)
                        todo.append(t)
            for n in order:
                if n not in seen and not states[n].get(M + 'src'):
                    add('warning', 'unreachable', states[n], 'state %r is never entered from %r'
                        % (raw_name(states[n]), states[order[0]].get(M + 'raw_name', order[0])))

        # --- the script is used by a TriggerModel?
        if idx is not None:
            base = os.path.splitext(os.path.basename(path))[0].lower()
            if base not in idx.trigger_models:
                add('warning', 'no-trigger-model', 0, 'no TriggerModel in xblock %s names %s.xml; the script '
                    'is never loaded' % (idx.name, base))
        return out, fmt

    def check_transition(self, tr, add, states, edges):
        t = tr.get('state')
        if t is None:
            add('warning', 'transition-nostate', tr, 'transition without a state attribute')
            return
        edges.add(t)
        if t not in states:
            raw = tr.get(M + 'raw_state', t)
            if self.importers:
                add('warning', 'lib-transition', tr, 'transition to state %r, not in this file; it is imported by '
                    '%d script(s) (e.g. %s), which must define it' % (raw, len(self.importers), self.importers[0]))
                return
            add('error', 'bad-transition', tr, 'transition to state %r, which does not exist%s'
                % (raw, '' if raw == t else ' (normalised %r)' % t))
            add('warning', 'maple2-gap', tr, 'Maple2 server (WIP) currently stays in its current state when the target is missing; actions in a condition that holds run again every tick')

    def check_action(self, el, add, idx, states, edges, imported):
        if el.get(M + 'ingest_fail'):
            return
        name = el.get('name')
        if (raw_name(el) or '').lower() == 'dungeonvariable':
            add('warning', 'maple2-gap', el, 'DungeonVariable has no ingest mapping or server action; it does nothing')
            return
        e = self.schema['actions'].get(name) if name else None
        if e is None:
            add('error', 'unknown-action', el, 'unknown action %r%s; the server skips it'
                % (raw_name(el), '' if raw_name(el) == name else ' (normalised %r)' % name))
            return
        self.check_args(el, e, 'action', add, idx, imported)
        if name in ('set_skip', 'set_scene_skip'):
            for arg in ('state', 'action'):
                v = el.get(arg)
                if not v:
                    continue
                fixed = normalize.fix_class_name(v)
                if fixed != v:
                    add('warning', 'maple2-gap', el, '%s %s=%r is not normalised by the ingest; the server looks up that exact state name'
                        % (name, arg, v))
                if v in states:
                    edges.add(v)
                elif fixed in states:
                    edges.add(fixed)
                elif arg == 'state' and not imported:
                    add('warning', 'skip-state-missing', el, '%s state=%r does not exist' % (name, v))

    def check_condition(self, el, add, idx, states, edges, imported):
        name = el.get('name')
        e = self.schema['conditions'].get(name) if name else None
        if (raw_name(el) or '').lower() == 'dungeonvariable':
            add('warning', 'maple2-gap', el, 'DungeonVariable has no ingest mapping or server condition; it does nothing')
            if e is None:
                return
        if e is None:
            add('error', 'unknown-condition', el, 'unknown condition %r%s; the server skips it with its '
                'transition and actions' % (raw_name(el), '' if raw_name(el) == name else ' (normalised %r)' % name))
            return
        self.check_args(el, e, 'condition', add, idx, imported)
        if e.get('class') == 'NpcHp' and el.get('compare') is not None:
            add('warning', 'maple2-gap', el, 'CheckNpcHp compare is ignored; the server always uses higher')
        if e.get('class') == 'UserValue' and el.get('operator') is not None:
            add('warning', 'maple2-gap', el, 'UserValue operator is ignored; the server always uses the default comparison')
        if e.get('class') == 'NpcDamage' and (el.get('damageRate') is not None or el.get('damage_rate') is not None):
            add('warning', 'maple2-gap', el, 'npc_damage damageRate is never read by the server')
        if e.get('class') == 'WeddingHallState' and (el.get('hallState') is not None or el.get('hall_state') is not None):
            add('warning', 'maple2-gap', el, 'wedding_hall_state hallState is never read by the server')
        if e.get('group'):
            g = el.find('group')
            if g is None:
                add('error', 'group-missing', el, '%s without <group> is skipped by the server' % raw_name(el))
                return
            if not imported:
                add('warning', 'maple2-gap', el, '%s: the server never reads the inner conditions; it reads '
                    'transition/action inside <group> (%s)' % (raw_name(el), 'always true' if name == 'all_of'
                                                               else 'never true'))
            for c in g:
                if c.tag == 'condition':
                    ce = self.schema['conditions'].get(c.get('name') or '')
                    if ce is None:
                        add('error', 'unknown-condition', c, 'unknown condition %r in group' % raw_name(c))
                    else:
                        self.check_args(c, ce, 'condition', add, idx, imported)
                elif c.tag == 'transition':
                    self.check_transition(c, add, states, edges)
                elif c.tag == 'action':
                    self.check_action(c, add, idx, states, edges, imported)
            for c in el:
                if c.tag == 'transition':
                    # read neither by the server nor anyone else; count the edge so reachability
                    # shows what the script author meant
                    edges.add(c.get('state'))
                    if c.get('state') not in states:
                        self.check_transition(c, add, states, set())
                elif c.tag == 'action':
                    self.check_action(c, add, idx, states, edges, imported)
            return
        for c in el:
            if c.tag == 'transition':
                self.check_transition(c, add, states, edges)
            elif c.tag == 'action':
                self.check_action(c, add, idx, states, edges, imported)
            else:
                add('warning', 'ignored-node', c, '<%s> in condition %s is ignored' % (c.tag, raw_name(el)))

    def check_args(self, el, e, kind, add, idx, imported):
        args = {a['name']: a for a in e['args']}
        rawargs = dict(p.split('=', 1) for p in el.get(M + 'raw_args', '').split(';') if '=' in p)
        what = '%s %s' % (kind, raw_name(el))

        def an(k):
            return rawargs.get(k, k)

        for k, v in el.attrib.items():
            if k in ('name', 'feature') or k.startswith(M) or k == el.get(M + 'splitter'):
                continue  # the splitter attribute only picks the server action
            compare = e.get('compare') or {}
            if kind == 'condition' and compare.get('op') == k:
                # These comparison arguments are handled by server-specific checks below.
                continue
            if k == 'negate' and kind == 'condition':
                if not e.get('negatable') and not imported:
                    add('warning', 'maple2-gap', el, '%s: negation is ignored (the server condition has no '
                        'negate argument)' % what)
                continue
            a = args.get(k)
            if a is None:
                if not imported:
                    add('warning', 'maple2-gap', el, 'Maple2’s parser has no mapping for %s argument %r' % (what, an(k)))
                continue
            if imported:
                continue
            t = a['type']
            label = '%s %s=%r' % (what, an(k), v)
            if t == 'int' and v != '' and dotnet_int(v) is None:
                add('warning', 'maple2-gap', el, '%s: Maple2’s parser rejects this integer value; reads as 0' % label)
            elif t == 'float' and v != '' and dotnet_float(v) is None:
                add('warning', 'maple2-gap', el, '%s: Maple2’s parser rejects this number; reads as 0' % label)
            elif t == 'bool' and v.strip().lower() not in ('', '1', '0', 'true', 'false'):
                add('warning', 'maple2-gap', el, '%s: Maple2’s parser rejects this boolean; reads as false' % label)
            elif t == 'vector3' and v != '':
                p = v.split(',')
                if len(p) != 3 or any(dotnet_float(x) is None for x in p):
                    add('warning', 'maple2-gap', el, '%s: Maple2’s parser rejects this vector; reads as 0,0,0' % label)
            elif t == 'int_list':
                ids, err, dropped = int_array(v)
                if err:
                    add('warning', 'maple2-gap', el, '%s: Maple2’s parser rejects this value (%s)' % (label, err))
                elif dropped:
                    add('warning', 'int-list-dropped', el, '%s: parts %r are dropped' % (label, dropped))
            elif t == 'enum' and v != '':
                names = self.schema['enums'].get(a.get('enum'), {})
                if not enum_parse(v, names):
                    if a.get('on_bad') == 'throw':
                        add('warning', 'maple2-gap', el, '%s: Maple2’s parser rejects this enum value (Enum.Parse<%s> throws)'
                            % (label, a.get('enum')))
                    else:
                        add('warning', 'maple2-gap', el, '%s: Maple2’s parser rejects this %s; reads as %s' % (label, a.get('enum'),
                                                                                       a.get('default')))
            elif t == 'banner' and v not in ('', '1', '3', '4', '5', '6', '7'):
                add('warning', 'bad-banner', el, '%s: only 1,3,4,5,6,7 are mapped; reads as Text' % label)
        if not imported:
            for a in e['args']:
                if a.get('required') and el.get(a['name']) in (None, ''):
                    add('warning', 'missing-arg', el, '%s: required argument %r is missing; reads as %s'
                        % (what, a['name'], a.get('default')))
        if idx is not None:
            self.check_refs(el, e, kind, add, idx, args, an, what)

    def check_refs(self, el, e, kind, add, idx, args, an, what):
        for k, a in args.items():
            ref = a.get('ref')
            v = el.get(k)
            if not ref or v in (None, ''):
                continue
            cond = COND_REF.get((kind, el.get('name'), k))
            if cond and (el.get(cond[0]) or '').strip() != cond[1]:
                continue
            if a['type'] == 'int_list':
                ids = int_array(v)[0]
            else:
                n = dotnet_int(v)
                ids = [] if n is None else [n]
            kinds = STRICT_REF.get((kind, el.get('name'), k)) or REF_KINDS.get(ref, (ref,))
            missing, wrong = [], []
            for i in ids:
                if i <= 0:
                    continue
                if any(idx.has(kd, i) for kd in kinds):
                    continue
                other = idx.kinds_of(i)
                (wrong if other else missing).append((i, other))
            nif = [i for i, o in wrong if ref == 'breakable' and 'breakable_nif' in o]
            if nif:
                wrong = [(i, o) for i, o in wrong if i not in nif]
                add('warning', 'maple2-gap', el, '%s %s: id(s) %s are MS2BreakableNIF objects, which the '
                    'ingest does not export (case commented out in MapEntityMapper), so the server finds nothing'
                    % (what, an(k), compact(nif)))
            if missing:
                add('warning', 'ref-missing', el, '%s %s: %s id(s) %s not in xblock %s'
                    % (what, an(k), '/'.join(kinds), compact([i for i, _ in missing]), idx.name))
            if wrong:
                add('warning', 'ref-kind', el, '%s %s: id(s) %s are %s, not %s'
                    % (what, an(k), compact([i for i, _ in wrong]),
                       '/'.join(sorted({x for _, o in wrong for x in o})), '/'.join(kinds)))


def compact(ids):
    ids = sorted(set(ids))
    if len(ids) > 8:
        return ','.join(map(str, ids[:8])) + ',...(%d)' % len(ids)
    return ','.join(map(str, ids))


def load_allow(path):
    if not path or not os.path.exists(path):
        return []
    with open(path, encoding='utf-8') as f:
        data = json.load(f)
    return data.get('entries', [])


def allowed(f, allow, base):
    rel = os.path.relpath(f['path'], base) if base else f['path']
    rel = rel.replace(os.sep, '/')
    for a in allow:
        if a.get('code') and a['code'] != f['code']:
            continue
        if a.get('path') and not (fnmatch.fnmatch(rel, a['path']) or fnmatch.fnmatch(rel, '*/' + a['path'])):
            continue
        if a.get('match') and a['match'] not in f['msg']:
            continue
        if a.get('level') and a['level'] != f['level']:
            continue
        return a
    return None


def iter_paths(paths):
    for p in paths:
        if os.path.isdir(p):
            for dp, dn, fn in os.walk(p):
                dn.sort()
                for n in sorted(fn):
                    if n.lower().endswith('.xml'):
                        yield os.path.join(dp, n)
        else:
            yield p


def main():
    import xblockindex
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0],
                                 formatter_class=argparse.RawDescriptionHelpFormatter, epilog=__doc__)
    ap.add_argument('paths', nargs='+')
    ap.add_argument('--xblock', help="xblock name, 'auto' (script folder), xblock/x.xblock or a .xblock file")
    ap.add_argument('--root', help='dir holding trigger/<folder>/*.xml for <import>; default from the script path')
    ap.add_argument('--format', default='auto', choices=('auto', 'raw', 'server'))
    ap.add_argument('--cache', default=xblockindex.DEFAULT_CACHE, help='xblockindex cache dir')
    ap.add_argument('--schema', default=os.path.join(HERE, 'schema.json'))
    ap.add_argument('--allow', default=os.path.join(HERE, 'known-issues.json'), help="allowlist ('' for none)")
    ap.add_argument('--strict', action='store_true', help='fail on features Maple2 does not run correctly yet')
    ap.add_argument('--json', action='store_true', help='print findings as JSON lines')
    ap.add_argument('--summary', help='write a summary report to this file')
    ap.add_argument('-q', '--quiet', action='store_true', help='print errors only, plus the summary')
    a = ap.parse_args()
    with open(a.schema, encoding='utf-8') as f:
        schema = json.load(f)
    lt = Linter(schema, a.cache, a.xblock, a.root, a.format)
    allow = load_allow(a.allow)
    base = os.path.commonpath([os.path.abspath(p) for p in a.paths]) if a.paths else None
    if base and os.path.isfile(base):
        base = os.path.dirname(base)
    files = 0
    counts = Counter()
    by_code = Counter()
    allow_hits = Counter()
    files_with = Counter()
    fmts = Counter()
    for p in iter_paths(a.paths):
        files += 1
        found, fmt = lt.lint_file(p)
        if a.strict:
            for f in found:
                if f['code'] == 'maple2-gap' and f['level'] == 'warning':
                    f['level'] = 'error'
        fmts[fmt] += 1
        seen_levels = set()
        for f in found:
            hit = allowed(f, allow, base)
            if hit:
                f['level'] = 'allowed'
                allow_hits[hit.get('reason', '?')] += 1
            counts[f['level']] += 1
            by_code[(f['level'], f['code'])] += 1
            seen_levels.add(f['level'])
            if a.json:
                print(json.dumps(f, ensure_ascii=False))
            elif not a.quiet or f['level'] == 'error':
                print('%s:%d: %s: [%s] %s' % (f['path'], f['line'], f['level'], f['code'], f['msg']))
        for lv in seen_levels:
            files_with[lv] += 1
    lines = ['files: %d (%s)' % (files, ', '.join('%s %d' % (k, v) for k, v in sorted(fmts.items(), key=str))),
             'errors: %d in %d files' % (counts['error'], files_with['error']),
             'warnings: %d in %d files' % (counts['warning'], files_with['warning']),
             'allowed (known-issues): %d in %d files' % (counts['allowed'], files_with['allowed']), '',
             'by code:']
    for (lv, code), n in sorted(by_code.items(), key=lambda x: (x[0][0], -x[1])):
        lines.append('  %-8s %-20s %d' % (lv, code, n))
    if allow_hits:
        lines += ['', 'allowlist hits:'] + ['  %5d  %s' % (n, r) for r, n in allow_hits.most_common()]
    summary = '\n'.join(lines) + '\n'
    sys.stderr.write(summary)
    if a.summary:
        with open(a.summary, 'w', encoding='utf-8') as f:
            f.write(summary)
    return 1 if counts['error'] else 0


if __name__ == '__main__':
    sys.exit(main())
