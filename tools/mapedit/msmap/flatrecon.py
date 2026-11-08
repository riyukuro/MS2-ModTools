"""Reconstruct Toolbench .model / .preset sources from the exported .flat files.

A .flat (EntityModelFlatView.ToXml) is the fully flattened view of one model:
  * <mixin>s are ParentModels (DFS pre-order over the declared mixins, de-duplicated),
  * <property>s are the DFS pre-order of the model's own properties then its mixins' (first name wins),
    with values/traits after applying the nearest PropertyOverride,
  * <behavior>s likewise (provider = nearest BehaviorOverride, else definer; inherited Extends -> Virtual).
Inverting this gives (exactly, modulo editor-only metadata):
  direct mixins : greedy cover of the pre-order list (keeps order, drops names reachable from an earlier direct mixin)
  own properties: flat(X).props minus the union of flat(M).props over direct mixins M, same order
  overrides     : inherited property whose value/traits differ from the first direct mixin that has it
  behaviors     : source == X and not inherited -> defined; source == X and inherited -> BehaviorOverride
What a .flat cannot tell us (reported by validate_models.py): GUIDs, Description texts, Folder, restrictions
(range/enum/tag/model), tool-time traits (Abstract, Global, PaletteHidden, Summary, Hidden), List-vs-Map for empty
collections, Rotator-vs-Point3 and Degree-vs-Float32, the original Behaviors' Description, Entity-ref names,
Asset LastKnown* annotations, and which defaults were typed by a designer.
"""
import os
import uuid

from lxml import etree

from .model import (Model, Prop, POverride, Behavior, BOverride, Val, TOOL_TIME_MODEL_TRAITS, export_name,
                    editor_name)
from . import values as V

# Editor types the .flat export name hides.  Derived from the SDK models (validate_models.py reports the accuracy
# of the heuristics): property name -> editor type.
ROTATOR_NAMES = {'Rotation', 'ShapeRotation', 'AttenuationVisualizerOrientation', 'InitialOrientation'}
DEGREE_NAMES = {'OuterSpotAngle', 'InnerSpotAngle', 'FOV'}
MAP_NAMES = {'AttachedObjects', 'NormalEventMap', 'ImmediateEventMap', 'CollisionBehaviors', 'OnEnterBehaviors',
             'OnStayBehaviors', 'OnLeaveBehaviors', 'LoadedBlocks', 'BlockData', 'Categories'}


class FProp:
    __slots__ = ('type', 'name', 'id', 'source', 'traits', 'sets')

    def __init__(self, type, name, id, source, traits, sets):
        self.type, self.name, self.id, self.source, self.traits, self.sets = type, name, id, source, traits, sets

    def val(self):
        return Val(self.type, self.sets)


class Flat:
    def __init__(self):
        self.name = self.id = None
        self.traits = []
        self.mixins = []        # [(name, id, type)]
        self.props = []         # [FProp]
        self.behaviors = []     # [dict(name,id,type,source,traits,order)]
        self.extradata = []     # [(name, type, [(etype,key,value)])]
        self.path = None
        self.cat = None         # ('library', libdir) | ('presets', catdir)

    @property
    def pmap(self):
        m = self.__dict__.get('_pmap')
        if m is None:
            m = self.__dict__['_pmap'] = {p.name: p for p in self.props}
        return m


def parse_flat(path):
    r = etree.parse(path).getroot()
    f = Flat()
    f.path = path
    f.name = r.get('name')
    f.id = r.get('id')
    for c in r:
        t = c.tag
        if t == 'trait':
            f.traits.append(c.get('value'))
        elif t == 'mixin':
            f.mixins.append((c.get('name'), c.get('id'), c.get('type')))
        elif t == 'property':
            sets = [(s.get('index'), s.get('value')) for s in c.findall('set')]
            f.props.append(FProp(c.get('type'), c.get('name'), c.get('id'), c.get('source'),
                                 [x.get('value') for x in c.findall('trait')], sets))
        elif t == 'behavior':
            io = c.find('invocation-order')
            f.behaviors.append(dict(name=c.get('name'), id=c.get('id'), type=c.get('type'), source=c.get('source'),
                                    traits=[x.get('value') for x in c.findall('trait')],
                                    order=[m.get('name') for m in io] if io is not None else None))
        elif t == 'extradata':
            f.extradata.append((c.get('name'), c.get('type'),
                                [(e.get('type'), e.get('key'), e.get('value')) for e in c.findall('entry')]))
    return f


def load_flats(exported_flat_dir, jobs=1):
    """Return {model name: Flat} for flat/library/* and flat/presets/*. Directory names are the cat key."""
    items = []
    for kind in ('library', 'presets'):
        kd = os.path.join(exported_flat_dir, kind)
        if not os.path.isdir(kd):
            continue
        for cat in sorted(os.listdir(kd)):
            cd = os.path.join(kd, cat)
            for fn in sorted(os.listdir(cd)):
                if fn.endswith('.flat'):
                    items.append((os.path.join(cd, fn), (kind, cat)))
    if jobs > 1:
        import multiprocessing as mp
        with mp.get_context('fork').Pool(jobs) as pool:
            flats = pool.map(parse_flat, [p for p, _ in items], chunksize=200)
    else:
        flats = [parse_flat(p) for p, _ in items]
    out = {}
    for f, (_, cat) in zip(flats, items):
        f.cat = cat
        out[f.name] = f
    return out


# ---------------------------------------------------------------------------------------------------------
def model_guid(name, known=None):
    """Deterministic GUID of a model/preset (the real GUIDs are not recoverable; see known_guids.json)."""
    if known and name in known:
        return known[name]
    return str(uuid.uuid5(uuid.NAMESPACE_URL, 'ms2-model:' + name))


def direct_mixins(f, flats):
    """Greedy cover of f.mixins (pre-order list) -> list of direct mixin names (declaration order)."""
    covered = set()
    out = []
    for n, _, _ in f.mixins:
        if n in covered:
            continue
        out.append(n)
        covered.add(n)
        g = flats.get(n)
        if g is not None:
            covered.update(m[0] for m in g.mixins)
    return out


def preorder(name, flats, memo, direct):
    """Pre-order over direct mixins (what ParentModels would contain for `name`), excluding the model itself."""
    res = []
    seen = set()

    def visit(n):
        if n in seen:
            return
        seen.add(n)
        res.append(n)
        for d in direct.get(n, ()):
            visit(d)

    for d in direct.get(name, ()):
        visit(d)
    return res


def _editor_hint(name, ftype):
    """(mtype, kind) hints for a property of exported type `ftype` named `name`."""
    if ftype == 'Point3' and name in ROTATOR_NAMES:
        return 'Rotator', None
    if ftype == 'Float32' and name in DEGREE_NAMES:
        return 'Degree', None
    if ftype.startswith('Assoc') and name in MAP_NAMES:
        return None, 'Map'
    return None, None


def _val(p):
    mt, kind = _editor_hint(p.name, p.type)
    v = Val(p.type, p.sets, mt, kind)
    if p.type.startswith('Assoc') and kind is None:
        # a Map when the indices are not 0..n-1
        if any(i is None or not str(i).isdigit() or int(i) != n for n, (i, _) in enumerate(p.sets)):
            v.kind = 'Map'
    return v


class Recon:
    """Reconstructs Model objects from a {name: Flat} dict."""

    def __init__(self, flats, known_guids=None):
        self.flats = flats
        self.known = known_guids or {}
        self.direct = {}
        self.warn = []
        self.stats = {}

    def guid(self, name):
        return model_guid(name, self.known)

    def direct_of(self, name):
        d = self.direct.get(name)
        if d is None:
            d = self.direct[name] = direct_mixins(self.flats[name], self.flats)
        return d

    def build(self, name, kind='model'):
        f = self.flats[name]
        m = Model(f.name, self.guid(f.name), kind)
        m.traits = list(f.traits)
        dm = self.direct_of(name)
        m.mixins = [(self.guid(n), n) for n in dm]
        for n in dm:
            if n not in self.flats:
                self.warn.append('%s: mixin %s has no flat' % (name, n))
        # check the pre-order we would regenerate
        want = [x[0] for x in f.mixins]
        got = preorder(name, self.flats, None, {n: self.direct_of(n) for n in want + [name] if n in self.flats})
        if want != got:
            self.warn.append('%s: mixin order differs after reduction' % name)
        inherited = {}     # prop name -> first direct mixin's FProp
        for n in dm:
            g = self.flats.get(n)
            if g is None:
                continue
            for p in g.props:
                inherited.setdefault(p.name, p)
        for p in f.props:
            ip = inherited.get(p.name)
            if ip is None:
                if kind == 'preset':
                    self.warn.append('%s: preset property %s not in base model' % (name, p.name))
                    continue
                m.props.append(Prop(p.name, _val(p), '', [t for t in p.traits]))
            else:
                if p.sets != ip.sets or sorted(p.traits) != sorted(ip.traits) or p.type != ip.type:
                    tov = [(t, True) for t in p.traits if t not in ip.traits] + \
                          [(t, False) for t in ip.traits if t not in p.traits]
                    mt, kd = _editor_hint(p.name, p.type)
                    v = _val(p)
                    m.poverrides.append(POverride(p.name, v, tov))
        # behaviors: the definer is the ancestor flat whose behavior has source == that ancestor (a parent's own
        # flat may omit an inherited Virtual behavior whose invocation list is empty, so look at all ancestors)
        definer = {}
        for n, _, _ in f.mixins:
            g = self.flats.get(n)
            if g is None:
                continue
            for b in g.behaviors:
                if b['source'] == n and b['name'] not in definer:
                    definer[b['name']] = b
        for b in f.behaviors:
            if b['type'] == 'Virtual' or b['source'] != name:
                continue
            ib = definer.get(b['name'])
            if ib is None:
                m.behaviors.append(Behavior(b['name'], b['type'], b['traits']))
            else:
                tov = [(t, True) for t in b['traits'] if t not in ib['traits']] + \
                      [(t, False) for t in ib['traits'] if t not in b['traits']]
                m.boverrides.append(BOverride(b['name'], b['type'], tov))
        m.extradata = [(n, t, list(e)) for n, t, e in f.extradata]
        return m

    def defined_props(self, name):
        """Property names the *regenerated* source chain of `name` defines.  A library model's flat is consistent with
        its generated .model (validated); a preset defines nothing itself, so it has its base's.  Preset flats are
        snapshots of the base at export time, so a preset whose base is another preset may mention properties that
        the base preset's own (older) flat has but the real definition chain has lost -- Toolbench drops such
        overrides on load ("was removed because it is no longer defined in a parent model")."""
        d = self.__dict__.setdefault('_defined', {})
        r = d.get(name)
        if r is None:
            f = self.flats.get(name)
            if f is None:
                r = set()
            elif f.cat[0] == 'presets':
                r = self.defined_props(f.mixins[0][0]) if f.mixins else set()
            else:
                r = {p.name for p in f.props}
            d[name] = r
        return r

    def build_preset(self, name):
        """Presets: single mixin = first mixin of the flat; overrides vs. that model's flat."""
        f = self.flats[name]
        m = Model(f.name, self.guid(f.name), 'preset')
        m.traits = list(f.traits)
        if f.mixins:
            base = f.mixins[0][0]
            m.mixins = [(self.guid(base), base)]
            bf = self.flats.get(base)
        else:
            base, bf = None, None
        if bf is None:
            self.warn.append('%s: preset base %s has no flat' % (name, base))
            return m
        bp = bf.pmap
        defined = self.defined_props(base)
        for p in f.props:
            ip = bp.get(p.name)
            if ip is None or p.name not in defined:
                self.warn.append('%s: preset property %s not in base model %s' % (name, p.name, base))
                continue
            if p.sets != ip.sets or sorted(p.traits) != sorted(ip.traits):
                tov = [(t, True) for t in p.traits if t not in ip.traits] + \
                      [(t, False) for t in ip.traits if t not in p.traits]
                m.poverrides.append(POverride(p.name, _val(p), tov))
        for b in f.behaviors:
            pass
        return m
