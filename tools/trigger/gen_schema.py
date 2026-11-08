#!/usr/bin/env python3
"""Generate tools/trigger/schema.json from the local Maple2 server sources.

Usage: gen_schema.py --server <Maple2 checkout> [--shipped DIR] [-o tools/trigger/schema.json]

Sources read (nothing is hand-written except the PARSERS / REF_KIND tables below,
which describe the C# helper semantics and were checked against the code):
  Maple2.Server.Game/Trigger/Helpers/TriggerFunctionMapping.cs  name -> class + attribute keys + parser
  Maple2.Server.Game/Trigger/Helpers/Trigger.{Actions,Conditions}.cs  class -> ctor params -> context method
  Maple2.Server.Game/Trigger/TriggerContext*.cs  context method bodies (implemented vs stub)
  Maple2.Server.Game/Trigger/Helpers/TriggerEnums.cs, Maple2.Model/Enum/JobGroup.cs  enum values
  Maple2.File.Ingest/Utils/TriggerDefinitionOverride.cs  shipped name -> server name, arg renames,
                                                           required/defaults, splitters
  Maple2.File.Ingest/Utils/TriggerTranslate.cs  Korean name lookups

--shipped DIR (a directory holding trigger/<xblock>/<name>.xml, e.g. an `m2d extract` of Xml.m2d)
adds per-function usage counts from the shipped scripts (after ingest normalisation).
"""
import argparse, json, os, re, sys
from collections import Counter

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)


def read(p):
    with open(p, encoding='utf-8-sig') as f:
        return f.read()


def balanced(s, i, open_='(', close=')'):
    """s[i] == open_; return index of the matching close (skips string literals)."""
    depth = 0
    j = i
    n = len(s)
    while j < n:
        c = s[j]
        if c == '"':
            j += 1
            while j < n and s[j] != '"':
                if s[j] == '\\':
                    j += 1
                j += 1
        elif c == open_:
            depth += 1
        elif c == close:
            depth -= 1
            if depth == 0:
                return j
        j += 1
    raise ValueError('unbalanced at %d' % i)


def split_top(s, sep=','):
    out, depth, cur, instr = [], 0, [], False
    for c in s:
        if instr:
            cur.append(c)
            if c == '"':
                instr = False
            continue
        if c == '"':
            instr = True
        elif c in '([{':
            depth += 1
        elif c in ')]}':
            depth -= 1
        if c == sep and depth == 0:
            out.append(''.join(cur).strip())
            cur = []
        else:
            cur.append(c)
    if ''.join(cur).strip():
        out.append(''.join(cur).strip())
    return out


# Semantics of the TriggerFunctionMapping helpers (TriggerFunctionMapping.cs, bottom of file).
# type: schema type; on_bad: what the server does with a malformed non-empty value:
#   'zero'  -> silently uses the default (int.TryParse / float.TryParse / bool compare)
#   'throw' -> exception; TriggerCache logs "Failed to parse trigger" and the whole script is not loaded
#   'default' -> TryParse-style enum: falls back to the default
PARSERS = {
    'ParseInt': {'type': 'int', 'on_bad': 'zero'},
    'ParseFloat': {'type': 'float', 'on_bad': 'zero'},
    'ParseBool': {'type': 'bool', 'on_bad': 'zero'},
    'ParseIntArray': {'type': 'int_list', 'on_bad': 'throw'},
    'ParseVector3': {'type': 'vector3', 'on_bad': 'zero'},
    'ParseAlign': {'type': 'enum', 'enum': 'Align', 'on_bad': 'throw'},
    'ParseLocale': {'type': 'enum', 'enum': 'Locale', 'on_bad': 'throw'},
    'ParseFieldGame': {'type': 'enum', 'enum': 'FieldGame', 'on_bad': 'throw'},
    'ParseOperatorType': {'type': 'enum', 'enum': 'OperatorType', 'on_bad': 'throw'},
    'ParseCompareType': {'type': 'enum', 'enum': 'CompareType', 'on_bad': 'throw'},
    'ParseJobCode': {'type': 'enum', 'enum': 'JobCode', 'on_bad': 'default'},
    'ParseWeather': {'type': 'enum', 'enum': 'Weather', 'on_bad': 'default'},
    'ParseBannerType': {'type': 'banner', 'on_bad': 'default'},
}
EMPTY_DEFAULTS = {'ParseInt': '0', 'ParseFloat': '0', 'ParseBool': 'false', 'ParseIntArray': '[]',
                  'ParseVector3': '0,0,0'}

# Map-object kind of an argument, keyed by (server function, attribute) or by attribute alone.
# Derived from the TriggerContext bodies: Objects.Boxes/Meshes/... (TriggerCollection, keyed by
# TriggerObjectID), Field.TryGetPortal (PortalID), npc.SpawnPointId (SpawnPointID of
# SpawnPointNPC / MS2RegionSpawn), Field.Entities.EventItemSpawns (EventSpawnPointItem),
# Field.EnumerateInteract (interactID), Field.TryGetBreakable(int) (breakable TriggerObjectID).
OBJ_COLLECTIONS = {
    'Boxes': 'box', 'Meshes': 'mesh', 'Cameras': 'camera', 'Actors': 'actor', 'Agents': 'agent',
    'Cubes': 'cube', 'Effects': 'effect', 'Ladders': 'ladder', 'Ropes': 'rope', 'Sounds': 'sound',
}


def parse_enums(server):
    enums = {}
    for p in [os.path.join(server, 'Maple2.Server.Game/Trigger/Helpers/TriggerEnums.cs'),
              os.path.join(server, 'Maple2.Model/Enum/JobGroup.cs')]:
        s = read(p)
        for m in re.finditer(r'enum\s+(\w+)\s*(?::\s*\w+)?\s*\{([^}]*)\}', s):
            body = re.sub(r'//[^\n]*', '', m.group(2))
            vals = {}
            nxt = 0
            for part in body.split(','):
                part = part.strip()
                if not part:
                    continue
                if '=' in part:
                    k, v = [x.strip() for x in part.split('=', 1)]
                    try:
                        nxt = int(v, 0)
                    except ValueError:
                        nxt = None
                else:
                    k = part
                vals[k] = nxt
                nxt = nxt + 1 if isinstance(nxt, int) else None
            enums[m.group(1)] = vals
    return enums


def parse_parser_defaults(mapping_src):
    out = {}
    for m in re.finditer(r'private static (\w+) (Parse\w+)\(string\? (\w+)\)\s*\{\s*if \(string\.IsNullOrEmpty\(\3\)\) return ([^;]+);', mapping_src):
        out[m.group(2)] = m.group(4).split('.')[-1]
    return out


def parse_arg(expr):
    expr = expr.strip()
    m = re.fullmatch(r'(Parse\w+)\(attrs\?\["([^"]+)"\]\?\.Value\)', expr)
    if m:
        return {'key': m.group(2), 'parser': m.group(1)}
    m = re.fullmatch(r'attrs\?\["([^"]+)"\]\?\.Value \?\? (.+)', expr)
    if m:
        d = m.group(2)
        return {'key': m.group(1), 'parser': 'string', 'default': '' if d == 'string.Empty' else d}
    m = re.fullmatch(r'attrs\?\["([^"]+)"\]\?\.Value\.Split\(\'(.)\'\) \?\? \[\]', expr)
    if m:
        return {'key': m.group(1), 'parser': 'string_list', 'sep': m.group(2)}
    raise ValueError('unhandled mapping argument: ' + expr)


def parse_mapping(src):
    cond_at = src.index('ConditionMap =')
    out = {'action': {}, 'condition': {}}
    for m in re.finditer(r'\{\s*"([a-z0-9_]+)",\s*(attrs|_)\s*=>\s*new Trigger\.(\w+)\(', src):
        i = m.end() - 1
        j = balanced(src, i)
        inner = src[i + 1:j]
        args = [parse_arg(a) for a in split_top(inner)] if inner.strip() else []
        kind = 'condition' if m.start() > cond_at else 'action'
        line = src.count('\n', 0, m.start()) + 1
        out[kind][m.group(1)] = {'class': m.group(3), 'args': args, 'line': line}
    return out


def parse_classes(src):
    """class Name(params) : IAction|ICondition { ... context.Method(...) }"""
    out = {}
    for m in re.finditer(r'public class (\w+)(?:\(([^)]*)\))?\s*:\s*(IAction|ICondition|IGroupCondition)\s*\{', src):
        i = m.end() - 1
        j = balanced(src, i, '{', '}')
        body = src[i:j]
        params = []
        if m.group(2):
            for p in split_top(m.group(2)):
                t, n = p.rsplit(' ', 1)
                params.append({'name': n, 'cstype': t})
        cm = re.search(r'context\.(\w+)\(', body)
        nm = re.search(r'public string Name => "([^"]+)"', body)
        out[m.group(1)] = {'params': params, 'method': cm.group(1) if cm else None,
                           'name': nm.group(1) if nm else None, 'body': body}
    return out


LIVE_TOKENS = re.compile(r'\b(Field|Objects|Broadcast|owner|Events|skipState|StartTick|currentRandom|'
                         r'Session|Packet|\w+Packet|DateTime|Random|lastDebugKey)\b')


def parse_context(files):
    out = {}
    for p in files:
        src = read(p)
        for m in re.finditer(r'\n    (?:public|private|internal|protected)(?: static)? (?:[\w<>\[\]?, ]+?) (\w+)\(([^)]*)\)\s*\{', src):
            i = m.end() - 1
            j = balanced(src, i, '{', '}')
            body = src[i + 1:j]
            stripped = re.sub(r'(Error|Warn|Debug)Log\((?:[^;]|"[^"]*")*?\);', '', body, flags=re.S)
            live = bool(LIVE_TOKENS.search(stripped))
            # Calls to other context methods (helpers like UpdateMesh) count as live too.
            calls = set(re.findall(r'\b([A-Z]\w+)\(', stripped)) - {'Math', 'TimeSpan', 'List', 'Join'}
            logs = sorted(set(re.findall(r'\b(Error|Warn|Debug)Log\(', body)))
            objs = sorted(set(OBJ_COLLECTIONS[x] for x in re.findall(r'Objects\.(\w+)', body) if x in OBJ_COLLECTIONS))
            extra = []
            if 'TryGetPortal' in body:
                extra.append('portal')
            if 'SpawnPointId' in body:
                extra.append('spawn')
            if 'EventItemSpawns' in body:
                extra.append('item_spawn')
            if 'EnumerateInteract' in body:
                extra.append('interact')
            if 'TryGetBreakable' in body:
                extra.append('breakable')
            if 'Trigger.Skills' in body:
                extra.append('skill')
            params = []
            for prm in split_top(m.group(2)):
                prm = re.sub(r'^params ', '', prm.strip())
                prm = prm.split('=')[0].strip()
                if ' ' in prm:
                    t, n = prm.rsplit(' ', 1)
                    params.append({'name': n, 'cstype': t})
            out[m.group(1)] = {
                'file': os.path.basename(p), 'line': src.count('\n', 0, m.start()) + 2,
                'implemented': live, 'logs': logs, 'objects': objs + extra, 'params': params,
                'calls': sorted(calls), 'logger': 'logger.' in stripped,
            }
    # A method that only calls other context helpers is live when one of those is live.
    changed = True
    while changed:
        changed = False
        for k, v in out.items():
            if not v['implemented'] and any(out.get(c, {}).get('implemented') for c in v['calls'] if c != k):
                v['implemented'] = changed = True
    for v in out.values():
        if not v['implemented'] and v.get('logger'):
            v['log_only'] = True
        if v['implemented'] and 'Error' in v['logs']:
            v['partial'] = True
    return out


def to_snake(text):
    from normalize import to_snake_case
    return to_snake_case(text)


def parse_overrides(src):
    """Parse TriggerDefinitionOverride.cs into {action:{orig: ov}, condition:{orig: ov}}."""
    out = {'action': {}, 'condition': {}}

    def parse_tuples(s):
        return re.findall(r'\(\s*"([^"]*)"\s*,\s*(null|Required|"[^"]*")\s*\)', s)

    def parse_ov(name, body):
        ov = {'name': name}
        nm = re.search(r'Names = BuildNameOverride\(((?:[^()]|\([^()]*\))*)\)', body)
        ov['names'] = {}
        if nm:
            for a, b in re.findall(r'\(\s*"([^"]*)"\s*,\s*"([^"]*)"\s*\)', nm.group(1)):
                ov['names'][to_snake(a)] = to_snake(b)
        tm = re.search(r'Types = BuildTypeOverride\(((?:[^()]|\([^()]*\))*)\)', body)
        ov['types'] = None
        if tm:
            ov['types'] = {}
            for a, d in parse_tuples(tm.group(1)):
                ov['types'][to_snake(a)] = ('<required>' if d == 'Required' else None if d == 'null' else d.strip('"'))
        cm = re.search(r'Compare = BuildCompareOverride\(([^)]*)\)', body)
        if cm:
            parts = [x.strip().strip('"') for x in cm.group(1).split(',')]
            if parts[2:] == ['Required']:
                parts[2] = '<required>'
            ov['compare'] = {'field': to_snake(parts[0]), 'op': parts[1] if parts[1].startswith('<') else to_snake(parts[1]),
                             'default': parts[2] if len(parts) > 2 else 'Equal'}
        return ov

    for m in re.finditer(r'(Action|Condition)Override\["([^"]+)"\] = new TriggerDefinitionOverride\(([^)]*)\)\s*\{', src):
        kind = m.group(1).lower()
        i = m.end() - 1
        j = balanced(src, i, '{', '}')
        body = src[i + 1:j]
        name = m.group(3).split(',')[0].strip()
        name = '' if name in ('string.Empty', '""') else name.strip('"')
        sm = re.search(r'FunctionSplitter = "([^"]+)"', body)
        if sm:
            ov = {'name': name, 'splitter': sm.group(1), 'lookup': {}}
            for lm in re.finditer(r'\["([^"]*)"\] = new TriggerDefinitionOverride\("([^"]*)"[^)]*\)\s*\{', body):
                li = lm.end() - 1
                lj = balanced(body, li, '{', '}')
                ov['lookup'][lm.group(1)] = parse_ov(lm.group(2), body[li + 1:lj])
            # The outer Types: when the splitter attribute is missing, the ingest uses the first
            # value as the FunctionLookup key (null Types = NullReferenceException).
            outer = re.sub(r'FunctionLookup = new Dictionary.*', '', body, flags=re.S)
            tm = re.search(r'Types = BuildTypeOverride\(((?:[^()]|\([^()]*\))*)\)', outer)
            ov['types'] = None
            if tm:
                ov['types'] = {to_snake(a): (None if d == 'null' else d.strip('"')) for a, d in parse_tuples(tm.group(1))}
        else:
            ov = parse_ov(name, body)
        ov['line'] = src.count('\n', 0, m.start()) + 1
        out[kind][m.group(2)] = ov
    return out


def parse_translate(src):
    out = {}
    for which in ('ActionLookup', 'ConditionLookup'):
        m = re.search(which + r' = new\(\) \{(.*?)\};', src, re.S)
        out[which] = dict(re.findall(r'\{"([^"]+)", "([^"]+)"\}', m.group(1)))
    return out


def enabled_features(tables, locale='NA', env='Live'):
    """Maple2.File.Parser Filter.Load: a feature is kept when feature[locale] <= setting[env][locale]."""
    import xml.etree.ElementTree as ET
    st = ET.parse(os.path.join(tables, 'table', 'feature_setting.xml')).getroot()
    limit = int(next(s for s in st.iter('setting') if s.get('type') == env).get(locale))
    ft = ET.parse(os.path.join(tables, 'table', 'feature.xml')).getroot()
    return sorted(f.get('name') for f in ft.iter('feature') if int(f.get(locale)) <= limit)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    root = os.path.normpath(os.path.join(HERE, '..', '..'))
    ap.add_argument('--server', help='your Maple2 server checkout (default: MS2_SERVER_DIR)')
    ap.add_argument('--shipped', help='dir containing trigger/<xblock>/*.xml (m2d extract of Xml.m2d)')
    ap.add_argument('--tables', help='dir holding table/feature.xml and table/feature_setting.xml (Xml.m2d); '
                    'gives the features the ingest keeps (locale NA, env Live, as in Maple2.File.Ingest/Program.cs)')
    ap.add_argument('-o', '--out', default=os.path.join(HERE, 'schema.json'))
    a = ap.parse_args()
    if not a.server:
        sys.path.insert(0, os.path.join(HERE, '..'))
        import modtools_config as cfg
        a.server = str(cfg.require('MS2_SERVER_DIR', 'gen_schema.py'))
    sv = a.server
    game = os.path.join(sv, 'Maple2.Server.Game')
    mapping_src = read(os.path.join(game, 'Trigger/Helpers/TriggerFunctionMapping.cs'))
    mapping = parse_mapping(mapping_src)
    parser_defaults = parse_parser_defaults(mapping_src)
    classes = {}
    classes.update(parse_classes(read(os.path.join(game, 'Trigger/Helpers/Trigger.Actions.cs'))))
    classes.update(parse_classes(read(os.path.join(game, 'Trigger/Helpers/Trigger.Conditions.cs'))))
    tdir = os.path.join(game, 'Trigger')
    ctx = parse_context(sorted(os.path.join(tdir, f) for f in os.listdir(tdir) if f.startswith('TriggerContext')))
    enums = parse_enums(sv)
    ingest = os.path.join(sv, 'Maple2.File.Ingest/Utils')
    overrides = parse_overrides(read(os.path.join(ingest, 'TriggerDefinitionOverride.cs')))
    translate = parse_translate(read(os.path.join(ingest, 'TriggerTranslate.cs')))

    # reverse override index: server name -> [shipped names] and server arg -> [shipped arg names]
    rev = {'action': {}, 'condition': {}}
    for kind in ('action', 'condition'):
        for orig, ov in overrides[kind].items():
            subs = list(ov['lookup'].items()) if 'lookup' in ov else [(None, ov)]
            for sval, sub in subs:
                r = rev[kind].setdefault(sub['name'], {'shipped_names': [], 'arg_aliases': {}, 'required': set(),
                                                       'ingest_defaults': {}, 'compare': None, 'splitter': []})
                if orig not in r['shipped_names']:
                    r['shipped_names'].append(orig)
                if sval is not None:
                    r['splitter'].append({'shipped_name': orig, 'attr': ov['splitter'], 'value': sval})
                for old, new in sub['names'].items():
                    r['arg_aliases'].setdefault(new, [])
                    if old not in r['arg_aliases'][new]:
                        r['arg_aliases'][new].append(old)
                for argn, d in (sub.get('types') or {}).items():
                    if d == '<required>':
                        r['required'].add(argn)
                    elif d is not None:
                        r['ingest_defaults'][argn] = d
                if sub.get('compare'):
                    r['compare'] = sub['compare']

    schema = {'version': 1,
              'note': 'Argument names, types, enum values, and implementation status describe Maple2 (WIP); they are not verified against Nexon shipped behavior.',
              'generated_from': os.path.relpath(sv, root), 'enums': enums,
              'parsers': PARSERS, 'actions': {}, 'conditions': {},
              'ingest': {'overrides': overrides, 'translate': translate}}
    if a.tables:
        schema['ingest']['features_enabled'] = enabled_features(a.tables)
    problems = []
    for kind, key in (('action', 'actions'), ('condition', 'conditions')):
        for name, ent in sorted(mapping[kind].items()):
            cls = classes.get(ent['class'], {})
            params = cls.get('params', [])
            method = cls.get('method')
            c = ctx.get(method) if method else None
            r = rev[kind].get(name, {})
            args = []
            for idx, arg in enumerate(ent['args']):
                p = PARSERS.get(arg['parser'], {'type': 'string' if arg['parser'] == 'string' else 'string_list', 'on_bad': 'none'})
                d = {'name': arg['key'], 'type': p['type'], 'parser': arg['parser'], 'on_bad': p['on_bad']}
                if 'enum' in p:
                    d['enum'] = p['enum']
                    d['values'] = list(enums.get(p['enum'], {}).keys())
                if arg['parser'] in parser_defaults:
                    d['default'] = parser_defaults[arg['parser']]
                elif arg['parser'] in EMPTY_DEFAULTS:
                    d['default'] = EMPTY_DEFAULTS[arg['parser']]
                elif arg['parser'] == 'string':
                    d['default'] = arg.get('default', '')
                elif arg['parser'] == 'string_list':
                    d['default'] = '[]'
                if idx < len(params):
                    d['param'] = params[idx]['name']
                    d['cstype'] = params[idx]['cstype']
                if arg['key'] in r.get('required', ()):
                    d['required'] = True
                if arg['key'] in r.get('ingest_defaults', {}):
                    d['ingest_default'] = r['ingest_defaults'][arg['key']]
                if arg['key'] in r.get('arg_aliases', {}):
                    d['shipped_aliases'] = r['arg_aliases'][arg['key']]
                if arg['key'] != arg['key'].lower() or '_' not in arg['key'] and any(ch.isupper() for ch in arg['key']):
                    problems.append('%s %s: attribute key %r is not snake_case; the ingest renames every '
                                    'shipped attribute to snake_case, so this argument is never read from shipped data'
                                    % (kind, name, arg['key']))
                args.append(d)
            # object-kind of id arguments
            objs = c['objects'] if c else []
            for d in args:
                k = d['name']
                if d['type'] not in ('int', 'int_list'):
                    continue
                ref = None
                if re.fullmatch(r'(\w+_)?box_ids?', k):
                    ref = 'box'
                elif k in ('spawn_id', 'spawn_ids', 'spawn_point_id', 'spawn_point_ids', 'from_spawn_id', 'to_spawn_id'):
                    ref = 'item_spawn' if 'item_spawn' in objs else 'spawn'
                elif k in ('portal_id', 'target_portal_id') and name not in ('move_user', 'move_random_user', 'move_user_to_pos'):
                    ref = 'portal'
                elif k in ('interact_ids', 'interact_id') or (k == 'trigger_ids' and 'interact' in objs):
                    ref = 'interact'
                elif k in ('trigger_id', 'trigger_ids'):
                    cand = [o for o in objs if o in OBJ_COLLECTIONS.values() or o in ('breakable', 'skill', 'portal')]
                    if len(cand) == 1:
                        ref = cand[0]
                if ref:
                    d['ref'] = ref
            entry = {'source': 'maple2', 'class': ent['class'], 'mapping_line': ent['line'], 'args': args,
                     'method': method,
                     'status': ('log_only' if c and c.get('log_only') else 'partial' if c and c.get('partial') else
                                'implemented' if c and c['implemented'] else 'stub' if c else
                                ('builtin' if kind == 'condition' else 'unknown')),
                     'shipped_names': r.get('shipped_names', []),
                     }
            if c:
                entry['context'] = '%s:%d' % (c['file'], c['line'])
                entry['logs'] = c['logs']
            if kind == 'condition':
                entry['negatable'] = any(a['name'] == 'negate' for a in args)
                if r.get('compare'):
                    entry['compare'] = r['compare']
                if name in ('any_one', 'all_of'):
                    entry['group'] = True
            if r.get('splitter'):
                entry['splitter'] = r['splitter']
            schema[key][name] = entry
        # override targets that the runtime does not map
        for sname in sorted(rev[kind]):
            if sname and sname not in mapping[kind]:
                problems.append('%s %s: ingest override target (from %s) has no TriggerFunctionMapping entry'
                                % (kind, sname, ', '.join(rev[kind][sname]['shipped_names'])))
    schema['server_problems'] = problems

    if a.shipped:
        from normalize import Normalizer, load_shipped
        norm = Normalizer(schema)
        use = {'action': Counter(), 'condition': Counter()}
        files = {'action': Counter(), 'condition': Counter()}
        raw = {'action': Counter(), 'condition': Counter()}
        shipped_args = {'action': {}, 'condition': {}}
        nscripts = 0
        for path, rel in load_shipped(a.shipped):
            nscripts += 1
            try:
                doc = norm.load_file(path, rel, a.shipped)
            except Exception:
                continue
            seen = {'action': set(), 'condition': set()}
            for el in doc.iter():
                if el.tag in ('action', 'condition') and el.get('name') is not None:
                    use[el.tag][el.get('name')] += 1
                    seen[el.tag].add(el.get('name'))
                    raw[el.tag][el.get('{m}raw_name', el.get('name'))] += 1
                    name = el.get('name')
                    known = {arg['name'] for arg in schema[el.tag + 's'].get(name, {}).get('args', [])}
                    extra = shipped_args[el.tag].setdefault(name, Counter())
                    for attr in el.attrib:
                        if attr not in known and not attr.startswith('{') and attr not in ('name', 'feature'):
                            extra[attr] += 1
            for k in seen:
                for n in seen[k]:
                    files[k][n] += 1
        for kind, key in (('action', 'actions'), ('condition', 'conditions')):
            for name, entry in schema[key].items():
                entry['shipped_uses'] = use[kind].get(name, 0)
                entry['shipped_scripts'] = files[kind].get(name, 0)
                for arg, count in sorted(shipped_args[kind].get(name, {}).items()):
                    entry['args'].append({'name': arg, 'type': 'shipped-only', 'source': 'shipped',
                                          'shipped_uses': count})
            schema['shipped_unknown_' + key] = {n: c for n, c in sorted(use[kind].items()) if n not in schema[key]}
        schema['shipped_scripts'] = nscripts

    with open(a.out, 'w', encoding='utf-8') as f:
        json.dump(schema, f, indent=1, ensure_ascii=False, sort_keys=False)
        f.write('\n')
    na, nc = len(schema['actions']), len(schema['conditions'])
    st = Counter(e['status'] for e in list(schema['actions'].values()) + list(schema['conditions'].values()))
    print('%s: %d actions, %d conditions; status %s; %d server problems' % (a.out, na, nc, dict(st), len(problems)))


if __name__ == '__main__':
    main()
