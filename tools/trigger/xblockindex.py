#!/usr/bin/env python3
"""Index the objects a trigger script can reference in a map xblock.

An xblock (Exported.m2d xblock/<name>.xblock) is a list of <entity modelName=...> with
<property name=...><set value=...>. The entity's class comes from its model's mixins
(Exported.m2d flat/**.flat: <model name><mixin name/>...). The kind of each object follows
Maple2.File.Ingest MapEntityMapper (switch order matters: the first matching case wins):

  MS2InteractObject                      -> interact   (interactID; Field.EnumerateInteract)
  Portal (incl. MS2TriggerPortal)        -> portal     (PortalID; Field.TryGetPortal)
  SpawnPointPC                           -> pc_spawn   (SpawnPointID)
  SpawnPointNPC / EventSpawnPointNPC     -> npc_spawn / event_npc_spawn (SpawnPointID)
  SpawnPointItem (EventSpawnPointItem)   -> item_spawn (SpawnPointID; Entities.EventItemSpawns)
  MS2RegionSpawnBase                     -> region_spawn (SpawnPointID)
  MS2TriggerObject: MS2TriggerActor/Agent/Block/Box/Camera/Cube/Effect/Ladder/Mesh/Rope/Skill/Sound
                                         -> actor/agent/block/box/... (TriggerObjectID; TriggerCollection)
  MS2TriggerModel                        -> trigger_model (TriggerModelID + XmlFilePath)
  MS2BreakableActor                      -> breakable  (TriggerBreakableID; Field.TryGetBreakable)

Usage:
  xblockindex.py build --exported <client Data>/Resource/Exported.m2d [--cache DIR]
  xblockindex.py show <xblock name | .xblock file> [--cache DIR]

`build` extracts the flats and all xblocks with tools/m2d (needs dotnet) into a temp dir under
the cache dir, writes <cache>/models.json and <cache>/xblocks.json, and deletes the extract.
`--from-dir DIR` indexes an existing extract (DIR/flat, DIR/xblock) instead.
"""
import argparse, json, os, re, shutil, subprocess, sys, tempfile
from concurrent.futures import ProcessPoolExecutor

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
sys.path.insert(0, os.path.join(ROOT, 'tools'))
import modtools_config as cfg  # noqa: E402
DEFAULT_CACHE = str(cfg.trigger_cache())

TRIGGER_SUB = ['MS2TriggerActor', 'MS2TriggerAgent', 'MS2TriggerBlock', 'MS2TriggerBox', 'MS2TriggerCamera',
               'MS2TriggerCube', 'MS2TriggerEffect', 'MS2TriggerLadder', 'MS2TriggerMesh', 'MS2TriggerPortal',
               'MS2TriggerRope', 'MS2TriggerSkill', 'MS2TriggerSound']
ID_PROPS = ('TriggerObjectID', 'SpawnPointID', 'PortalID', 'interactID', 'TriggerModelID', 'XmlFilePath',
            'TriggerBreakableID', 'IsVisible')


def classify(mixins):
    """Return the object kind for a set of mixin names (lower-case), following MapEntityMapper."""
    m = mixins
    if 'ms2interactobject' in m:
        return 'interact'
    if 'portal' in m:
        return 'portal'
    if 'spawnpoint' in m:
        if 'spawnpointpc' in m:
            return 'pc_spawn'
        if 'spawnpointnpc' in m:
            return 'event_npc_spawn' if 'eventspawnpointnpc' in m else 'npc_spawn'
        if 'eventspawnpointitem' in m or 'spawnpointitem' in m:
            return 'item_spawn'
        return 'spawn_other'
    if 'ms2regionspawnbase' in m:
        return 'region_spawn'
    if 'ms2triggerobject' in m:
        for sub in TRIGGER_SUB:
            if sub.lower() in m:
                return sub[len('MS2Trigger'):].lower()
        return 'trigger_object'
    if 'ms2regionskill' in m:
        return 'region_skill'
    if 'ms2triggermodel' in m:
        return 'trigger_model'
    if 'ms2breakableactor' in m:
        return 'breakable'
    return None


# xblocks and flat files come both on one line per element and pretty-printed (attribute per line)
ENTITY_RE = re.compile(r'<entity\s+id="([^"]*)"\s+modelName="([^"]*)"\s+name="([^"]*)"[^>]*?(/?)>')
PROP_RE = re.compile(r'<property\s[^>]*?name="([^"]+)"[^>]*>\s*<set\s+value="([^"]*)"')
MODEL_RE = re.compile(r'<model\s+name="([^"]+)"')
MIXIN_RE = re.compile(r'<mixin\s+name="([^"]+)"')


def read_text(p):
    with open(p, 'rb') as f:
        return f.read().decode('utf-8-sig', 'replace')


def scan_flat(path):
    s = read_text(path)
    mm = MODEL_RE.search(s)
    if not mm:
        return None
    props = {k: v for k, v in PROP_RE.findall(s) if k in ID_PROPS}
    return mm.group(1), MIXIN_RE.findall(s), props


def build_models(flat_dir):
    raw = {}
    paths = []
    for dp, dn, fn in os.walk(flat_dir):
        for f in fn:
            if f.endswith('.flat'):
                paths.append(os.path.join(dp, f))
    with ProcessPoolExecutor(max_workers=min(8, os.cpu_count() or 2)) as ex:
        for r in ex.map(scan_flat, paths, chunksize=256):
            if r:
                raw[r[0].lower()] = r
    models = {}

    def closure(name, seen):
        if name in seen:
            return set()
        seen.add(name)
        out = {name}
        r = raw.get(name)
        if r:
            for mx in r[1]:
                out |= closure(mx.lower(), seen)
        return out

    for key, (name, mixins, props) in raw.items():
        mx = closure(key, set())
        # defaults: nearest definition wins (the model itself, then its mixins in order)
        defaults = {}
        for src in [key] + [x.lower() for x in mixins]:
            for k, v in raw.get(src, (None, None, {}))[2].items():
                defaults.setdefault(k, v)
        models[key] = {'kind': classify(mx), 'defaults': defaults}
    return models


_MODELS = {}


def _init_models(models):
    _MODELS.clear()
    _MODELS.update(models)


def _scan_one(path):
    return scan_xblock((path, _MODELS))


def scan_xblock(args):
    path, models = args
    s = read_text(path)
    out = []
    pos = [m for m in ENTITY_RE.finditer(s)]
    for i, m in enumerate(pos):
        model = models.get(m.group(2).lower())
        kind = model['kind'] if model else None
        if model is None and m.group(2).lower() == 'ms2breakablenif':
            # no flat model in the export; the ingest has the BreakableNIF case commented out, so the
            # server has no such object. Indexed so the linter can say why an id is not found.
            model, kind = {'defaults': {}}, 'breakable_nif'
        if kind is None:
            continue
        end = pos[i + 1].start() if i + 1 < len(pos) else len(s)
        body = '' if m.group(4) else s[m.end():end]
        props = dict(model['defaults'])
        props.update({k: v for k, v in PROP_RE.findall(body) if k in ID_PROPS})
        out.append(entity_record(kind, m.group(3), props))
    return os.path.basename(path)[:-len('.xblock')].lower(), out


def as_int(v):
    try:
        return int(float(v))
    except (TypeError, ValueError):
        return None


def entity_record(kind, name, props):
    key = {'interact': 'interactID', 'portal': 'PortalID', 'trigger_model': 'TriggerModelID',
           'breakable': 'TriggerBreakableID', 'breakable_nif': 'TriggerBreakableID'}.get(kind)
    if key is None:
        key = 'SpawnPointID' if kind.endswith('spawn') or kind == 'spawn_other' else 'TriggerObjectID'
    rec = [kind, as_int(props.get(key)), name]
    if kind == 'trigger_model':
        rec.append(os.path.splitext(os.path.basename((props.get('XmlFilePath') or '').replace('\\', '/')))[0])
    return rec


def m2d(*args, out=None):
    env = dict(os.environ, DOTNET_ROLL_FORWARD='Major')
    return subprocess.run(['dotnet', str(cfg.ensure_m2d())] + list(args), check=True, env=env, stdout=out)


def build(a):
    os.makedirs(a.cache, exist_ok=True)
    tmp = None
    src = a.from_dir
    if not src:
        tmp = src = tempfile.mkdtemp(prefix='xbidx-', dir=a.cache)
        print('extracting flat/ and xblock/ from', a.exported, file=sys.stderr)
        m2d('extract', a.exported, src, 'flat/')
        m2d('extract', a.exported, src, 'xblock/')
    try:
        models = build_models(os.path.join(src, 'flat'))
        print('%d models' % len(models), file=sys.stderr)
        xdir = os.path.join(src, 'xblock')
        files = sorted(os.path.join(xdir, f) for f in os.listdir(xdir) if f.endswith('.xblock'))
        kinds = {k: v for k, v in models.items() if v['kind']}
        xb = {}
        with ProcessPoolExecutor(max_workers=a.jobs, initializer=_init_models, initargs=(kinds,)) as ex:
            for name, ents in ex.map(_scan_one, files, chunksize=8):
                xb[name] = ents
        with open(os.path.join(a.cache, 'models.json'), 'w') as f:
            json.dump(kinds, f, separators=(',', ':'))
        with open(os.path.join(a.cache, 'xblocks.json'), 'w', encoding='utf-8') as f:
            json.dump(xb, f, separators=(',', ':'), ensure_ascii=False)
        print('%d xblocks, %d objects -> %s' % (len(xb), sum(len(v) for v in xb.values()), a.cache), file=sys.stderr)
    finally:
        if tmp:
            shutil.rmtree(tmp, ignore_errors=True)


class XBlockIndex:
    """Objects of one xblock: kinds[kind][id] -> [entity names]; models: trigger model name -> ids."""

    def __init__(self, name, entities):
        self.name = name
        self.kinds = {}
        self.trigger_models = {}
        for rec in entities:
            kind, oid, ename = rec[0], rec[1], rec[2]
            self.kinds.setdefault(kind, {}).setdefault(oid, []).append(ename)
            if kind == 'trigger_model':
                self.trigger_models.setdefault(rec[3].lower(), []).append(oid)

    def kinds_of(self, oid):
        return sorted(k for k, ids in self.kinds.items() if oid in ids)

    def has(self, kind, oid):
        return oid in self.kinds.get(kind, {})


_cache = {}


def load_index(spec, cache=DEFAULT_CACHE):
    """spec: an xblock name ('02000006_ad'), an m2d entry ('xblock/02000006_ad.xblock') or a .xblock file."""
    if os.path.isfile(spec):
        mp = os.path.join(cache, 'models.json')
        if not os.path.exists(mp):
            raise SystemExit('need %s for a loose .xblock: run xblockindex.py build first' % mp)
        if mp not in _cache:
            with open(mp) as f:
                _cache[mp] = json.load(f)
        name, ents = scan_xblock((spec, _cache[mp]))
        return XBlockIndex(name, ents)
    name = os.path.basename(spec)
    if name.lower().endswith('.xblock'):
        name = name[:-len('.xblock')]
    xp = os.path.join(cache, 'xblocks.json')
    if xp not in _cache:
        if not os.path.exists(xp):
            raise SystemExit('no %s: run xblockindex.py build --exported <Exported.m2d> first' % xp)
        with open(xp, encoding='utf-8') as f:
            _cache[xp] = json.load(f)
    ents = _cache[xp].get(name.lower())
    if ents is None:
        return None
    return XBlockIndex(name.lower(), ents)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest='cmd', required=True)
    b = sub.add_parser('build')
    b.add_argument('--exported', help='Exported.m2d (default: <MS2_CLIENT_DATA>/Resource/Exported.m2d)')
    b.add_argument('--from-dir', help='existing extract holding flat/ and xblock/')
    b.add_argument('--cache', default=DEFAULT_CACHE)
    b.add_argument('-j', '--jobs', type=int, default=min(8, os.cpu_count() or 2))
    s = sub.add_parser('show')
    s.add_argument('xblock')
    s.add_argument('--cache', default=DEFAULT_CACHE)
    a = ap.parse_args()
    if a.cmd == 'build':
        if not a.exported:
            a.exported = str(cfg.require('MS2_CLIENT_DATA', 'xblockindex.py') / 'Resource' / 'Exported.m2d')
        build(a)
    else:
        ix = load_index(a.xblock, a.cache)
        if ix is None:
            raise SystemExit('unknown xblock ' + a.xblock)
        for k in sorted(ix.kinds):
            ids = sorted(i for i in ix.kinds[k] if i is not None)
            print('%-16s %4d  %s' % (k, len(ids), ','.join(map(str, ids))[:200]))
        for n, ids in sorted(ix.trigger_models.items()):
            print('trigger_model %s -> %s' % (n, ids))


if __name__ == '__main__':
    main()
