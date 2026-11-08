"""Toolbench entity-modeling files (.model / .preset / .emtproj): object model, XML writer + parser, and
a re-implementation of EntityModelFlatView.ToXml (the "flatten" that produces the shipped .flat files).

Source of truth: Source/Tools/Toolbench/Plugins/Emergent.Toolbench.EntityModeling/{EntityModel,EntityModelPreset,
EntityModelProperty,PropertyOverride,EntityModelBehavior,BehaviorOverride,EntityModelMixin,ExtraData,EntityModelFlatView}.cs
and the TypeService/Assets value types.

Values are kept at the *flat* level: Val(ftype, sets, mtype, kind) where `ftype` is the exported type name used
by .flat ('Point3', 'AssetID', 'AssocString', ...), `sets` is [(index|None, string)] exactly as in <set>, and
`mtype`/`kind` carry the editor type information a .flat loses (Rotator/Degree, List/Map).
"""
import re
import struct

from lxml import etree

from .xmlio import NetWriter
from . import values as V

MODEL_VERSION = 5
TOOL_TIME_MODEL_TRAITS = ('Abstract', 'Global', 'PaletteHidden')
TOOL_TIME_PROPERTY_TRAITS = ('Summary', 'Hidden')
BUILTIN_TRAITS = ('BuiltinModel', 'ReplicaBuiltinModel')

# flat type  <-> model (editor) type
SCALARS = ('Boolean', 'SInt16', 'UInt16', 'SInt32', 'UInt32', 'SInt64', 'UInt64', 'Float32', 'Float64', 'String',
           'Point2', 'Point3', 'Matrix3', 'Color', 'ColorA', 'EntityRef', 'AttachedNifAsset', 'CustomType')
EXPORT_OF_EDITOR = {'Rotator': 'Point3', 'Degree': 'Float32', 'Asset': 'AssetID'}
EDITOR_OF_EXPORT = {'AssetID': 'Asset'}


def export_name(editor_type):
    return EXPORT_OF_EDITOR.get(editor_type, editor_type)


def editor_name(export_type):
    return EDITOR_OF_EXPORT.get(export_type, export_type)


class Val:
    __slots__ = ('ftype', 'sets', 'mtype', 'kind')

    def __init__(self, ftype, sets, mtype=None, kind=None):
        self.ftype = ftype            # exported type name, e.g. 'AssocString'
        self.sets = [tuple(s) for s in sets]
        self.mtype = mtype            # editor item type if it differs from editor_name(ftype): 'Rotator'|'Degree'
        self.kind = kind              # None | 'List' | 'Map'

    def key(self):
        return (self.ftype, tuple(self.sets))

    def __eq__(self, o):
        return isinstance(o, Val) and self.key() == o.key()

    def __repr__(self):
        return 'Val(%s,%r)' % (self.ftype, self.sets)

    @property
    def is_collection(self):
        return self.ftype.startswith('Assoc')

    @property
    def item_ftype(self):
        return self.ftype[5:] if self.is_collection else self.ftype


class Prop:
    def __init__(self, name, val, desc='', traits=(), restriction=None):
        self.name, self.val, self.desc, self.traits = name, val, desc, list(traits)
        self.restriction = restriction     # raw XML bytes of <Restriction> children (parser only)


class POverride:
    def __init__(self, name, val, trait_overrides=(), desc=''):
        self.name, self.val, self.desc = name, val, desc
        self.trait_overrides = list(trait_overrides)     # [(trait, active)]


class Behavior:
    def __init__(self, name, target, traits=(), desc=''):
        self.name, self.target, self.traits, self.desc = name, target, list(traits), desc


class BOverride:
    def __init__(self, name, target, trait_overrides=(), desc=''):
        self.name, self.target, self.desc = name, target, desc
        self.trait_overrides = list(trait_overrides)


class Model:
    def __init__(self, name, guid, kind='model'):
        self.name, self.guid, self.kind = name, guid, kind        # kind: 'model' | 'preset'
        self.desc = ''
        self.folder = None
        self.mixins = []          # [(guid, name)]
        self.props = []           # [Prop]
        self.poverrides = []      # [POverride]
        self.behaviors = []       # [Behavior]
        self.boverrides = []      # [BOverride]
        self.traits = []          # [name]
        self.extradata = []       # [(name, type, [(etype, key, value)])]
        self.path = None


# ---------------------------------------------------------------------------------------------------------
# value <-> XML
# ---------------------------------------------------------------------------------------------------------
def _split(v):
    return [t for t in re.split(r'[,\s]+', v.strip()) if t]


def _byte(tok):
    b = V._STR_TO_BYTE.get(tok)
    if b is None:
        b = max(0, min(255, int(round(float(tok) * 255.0))))
    return b


def _w_float(w, tag, v):
    w.empty(tag, [('Value', v)])


def _w_point(w, tag, v, n, axes):
    toks = _split(v)
    toks += ['0'] * (n - len(toks))
    w.start(tag)
    for ax, t in zip(axes, toks):
        w.start(ax)
        _w_float(w, 'Float32', t)
        w.end()
    w.end()


def _w_rotator(w, v):
    toks = _split(v)
    toks += ['0'] * (3 - len(toks))
    w.start('Rotator')
    for ax, t in zip(('Roll', 'Pitch', 'Yaw'), toks):
        w.start(ax)
        w.empty('Degree', [('Value', t)])
        w.end()
    w.end()


def write_item(w, ftype, v, mtype=None):
    """Write the typed XML element of one scalar value."""
    t = mtype or editor_name(ftype)
    if t in ('Boolean', 'SInt16', 'UInt16', 'SInt32', 'UInt32', 'SInt64', 'UInt64', 'Float32', 'Float64',
             'String', 'Degree'):
        w.empty(t, [('Value', v)])
    elif t == 'Point3':
        _w_point(w, 'Point3', v, 3, 'XYZ')
    elif t == 'Point2':
        _w_point(w, 'Point2', v, 2, 'XY')
    elif t == 'Rotator':
        _w_rotator(w, v)
    elif t in ('Color', 'ColorA'):
        n = 3 if t == 'Color' else 4
        toks = _split(v)
        toks += ['0'] * (n - len(toks))
        bs = [_byte(x) for x in toks]
        attrs = [('R', str(bs[0])), ('G', str(bs[1])), ('B', str(bs[2]))]
        if n == 4:
            attrs.append(('A', str(bs[3])))
        w.empty(t, attrs)
    elif t == 'EntityRef':
        w.start('EntityRef')
        w.leaf('Name', '')
        w.leaf('Id', v or V.ZERO_GUID)
        w.leaf('TargetContainerHierarchy', '')
        w.end()
    elif t == 'Asset':
        w.start('Asset', [('Value', v)])
        for k in ('LastKnownName', 'LastKnownPath', 'LastKnownTags'):
            w.leaf(k, '')
        w.end()
    elif t == 'AttachedNifAsset':
        p = (v.split(';') + [''] * 5)[:5]
        w.start('AttachedNifAsset')
        w.start('AttachPoint'); w.empty('String', [('Value', p[0])]); w.end()
        w.start('NifAsset', ); write_item(w, 'AssetID', p[1]); w.end()
        w.start('Translation'); write_item(w, 'Point3', p[2]); w.end()
        w.start('Rotation'); write_item(w, 'Point3', p[3], 'Rotator'); w.end()
        w.start('Scale'); w.empty('Float32', [('Value', p[4] or '1')]); w.end()
        w.end()
    else:
        w.empty(t, [('Value', v)])      # unknown/custom type: best effort


def write_value(w, val):
    """<Value> element body for a Val."""
    w.start('Value')
    if val.is_collection:
        it = val.item_ftype
        it_editor = val.mtype or editor_name(it)
        kind = val.kind or ('Map' if any(i is not None and not str(i).isdigit() for i, _ in val.sets) else 'List')
        w.start(kind, [('ItemsDataType', it_editor)])
        w.start('Items')
        for idx, v in val.sets:
            if kind == 'Map':
                w.start('Item', [('Key', str(idx))])
                write_item(w, it, v, val.mtype)
                w.end()
            else:
                write_item(w, it, v, val.mtype)
        w.end()
        w.end()
    else:
        write_item(w, val.ftype, val.sets[0][1] if val.sets else '', val.mtype)
    w.end()


def _read_item(e):
    """typed XML element -> (flat string, editor type)"""
    t = etree.QName(e).localname
    if t in ('Boolean', 'SInt16', 'UInt16', 'SInt32', 'UInt32', 'SInt64', 'UInt64', 'Float32', 'Float64',
             'String', 'Degree', 'Asset'):
        return e.get('Value', ''), t
    if t in ('Point3', 'Point2'):
        return ', '.join(c[0].get('Value') for c in e), t
    if t == 'Rotator':
        return ', '.join(c[0].get('Value') for c in e), t
    if t in ('Color', 'ColorA'):
        keys = 'RGB' if t == 'Color' else 'RGBA'
        return ', '.join(V._BYTE_TO_STR[int(e.get(k))] for k in keys), t
    if t == 'EntityRef':
        return (e.findtext('Id') or V.ZERO_GUID), t
    if t == 'AttachedNifAsset':
        parts = [_read_item(e.find(k)[0])[0] for k in ('AttachPoint', 'NifAsset', 'Translation', 'Rotation', 'Scale')]
        return ';'.join(parts), t
    return e.get('Value', ''), t


def read_value(vel):
    """<Value> element -> Val"""
    c = vel[0]
    t = etree.QName(c).localname
    if t in ('List', 'Map'):
        it = c.get('ItemsDataType')
        sets = []
        mt = None
        for i, item in enumerate(c.find('Items')):
            if t == 'Map':
                key = item.get('Key')
                s, _ = _read_item(item[0])
            else:
                key = str(i)
                s, _ = _read_item(item)
            sets.append((key, s))
        if it in ('Rotator', 'Degree'):
            mt = it
        return Val('Assoc' + export_name(it), sets, mt, t)
    s, et = _read_item(c)
    mt = et if et in ('Rotator', 'Degree') else None
    return Val(export_name(et), [(None, s)], mt)


# ---------------------------------------------------------------------------------------------------------
# .model / .preset writer
# ---------------------------------------------------------------------------------------------------------
def _traits(w, names):
    w.start('Traits')
    for t in sorted(names):
        w.empty('Trait', [('Name', t)])
    w.end()


def _trait_overrides(w, tovr):
    w.start('TraitOverrides')
    for t, act in sorted(tovr):
        w.start('TraitOverride', [('IsActive', 'True' if act else 'False')])
        w.empty('Trait', [('Name', t)])
        w.end()
    w.end()


def model_text(m):
    out = []
    w = NetWriter(out, indent='  ', newline_attrs=True)
    attrs = [('version', str(MODEL_VERSION)), ('ID', m.guid), ('Description', m.desc)]
    if m.folder:
        attrs.append(('Folder', m.folder))
    w.start('EntityModelPreset' if m.kind == 'preset' else 'EntityModel', attrs)
    w.start('Mixins')
    for g, n in m.mixins:
        w.empty('Mixin', [('SourceID', g), ('SourceName', n)])
    w.end()
    w.start('Properties')
    for p in m.props:
        w.start('Property', [('Name', p.name), ('Description', p.desc)])
        _traits(w, p.traits)
        write_value(w, p.val)
        w.start('Restriction')
        w.end()
        w.end()
    w.end()
    w.start('PropertyOverrides')
    for o in sorted(m.poverrides, key=lambda o: o.name):
        w.start('PropertyOverride', [('Name', o.name), ('Description', o.desc)])
        _trait_overrides(w, o.trait_overrides)
        write_value(w, o.val)
        w.end()
    w.end()
    w.start('Behaviors')
    for b in m.behaviors:
        w.start('Behavior', [('Name', b.name), ('Description', b.desc), ('Target', b.target)])
        _traits(w, b.traits)
        w.end()
    w.end()
    w.start('BehaviorOverrides')
    for b in sorted(m.boverrides, key=lambda o: o.name):
        w.start('BehaviorOverride', [('Name', b.name), ('Description', b.desc), ('Target', b.target)])
        _trait_overrides(w, b.trait_overrides)
        w.end()
    w.end()
    _traits(w, m.traits)
    w.start('ExtraDataList')
    for name, typ, entries in m.extradata:
        w.start('ExtraData', [('Name', name), ('Type', typ)])
        for et, k, v in sorted(entries, key=lambda e: e[1]):
            w.empty('Entry', [('Type', et), ('Key', k), ('Value', v)])
        w.end()
    w.end()
    w.end()
    return w.finish()


# ---------------------------------------------------------------------------------------------------------
# .model / .preset parser
# ---------------------------------------------------------------------------------------------------------
def _tr(e):
    return [t.get('Name') for t in e.find('Traits')] if e.find('Traits') is not None else []


def _tovr(e):
    return [(t[0].get('Name'), t.get('IsActive') in ('True', 'true')) for t in e.find('TraitOverrides')]


def parse_model(path):
    root = etree.parse(path).getroot()
    kind = 'preset' if etree.QName(root).localname == 'EntityModelPreset' else 'model'
    name = re.sub(r'\.(model|preset)$', '', path.replace('\\', '/').rsplit('/', 1)[-1], flags=re.I)
    m = Model(name, root.get('ID'), kind)
    m.desc = root.get('Description', '')
    m.folder = root.get('Folder')
    m.path = path
    m.mixins = [(x.get('SourceID'), x.get('SourceName')) for x in root.find('Mixins')]
    for p in root.find('Properties'):
        pr = Prop(p.get('Name'), read_value(p.find('Value')), p.get('Description', ''), _tr(p))
        r = p.find('Restriction')
        pr.restriction = [etree.QName(c).localname for c in r] if r is not None else []
        m.props.append(pr)
    for o in root.find('PropertyOverrides'):
        m.poverrides.append(POverride(o.get('Name'), read_value(o.find('Value')), _tovr(o), o.get('Description', '')))
    for b in root.find('Behaviors'):
        m.behaviors.append(Behavior(b.get('Name'), b.get('Target'), _tr(b), b.get('Description', '')))
    for b in root.find('BehaviorOverrides'):
        m.boverrides.append(BOverride(b.get('Name'), b.get('Target'), _tovr(b), b.get('Description', '')))
    m.traits = _tr(root)
    for x in root.find('ExtraDataList'):
        m.extradata.append((x.get('Name'), x.get('Type'), [(e.get('Type'), e.get('Key'), e.get('Value')) for e in x]))
    return m


# ---------------------------------------------------------------------------------------------------------
# flatten (EntityModelFlatView) -- operates on a {name: Model} dict
# ---------------------------------------------------------------------------------------------------------
class FlatProp:
    def __init__(self, name, val, traits, source):
        self.name, self.val, self.traits, self.source = name, val, sorted(traits), source


def flatten(models, name):
    """Return dict(name, traits, mixins[list of names], props[FlatProp], behaviors[(name,type,source,traits)],
    extradata) for models[name], mirroring EntityModelFlatView.ToXml."""
    X = models[name]
    # ParentModels (DFS preorder, unique)
    parents = []

    def obtain(m, depth=0):
        if depth > 256:
            raise RuntimeError('cycle')
        if m.name not in [p.name for p in parents]:
            parents.append(m)
        for g, n in m.mixins:
            s = models.get(n)
            if s is not None:
                obtain(s, depth + 1)

    obtain(X)
    props = {}
    order = []

    def collect(m, path):
        path = path + [m]
        builtin = any(t in BUILTIN_TRAITS for t in m.traits)
        for p in m.props:
            if p.name not in props:
                props[p.name] = [p, list(path), None]
                order.append(p.name)
            elif builtin:
                ex = props[p.name]
                src = ex[2] or (ex[1][-1] if any(t in BUILTIN_TRAITS for t in ex[1][-1].traits) else None)
                if src is None or src.name == m.name:
                    ex[2] = m
        for g, n in m.mixins:
            s = models.get(n)
            if s is not None:
                collect(s, path)

    collect(X, [])
    fprops = []
    for pn in order:
        p, hier, bsrc = props[pn]
        # value: first override walking from X down the hierarchy
        val = p.val
        for hm in hier:
            ov = next((o for o in hm.poverrides if o.name == pn), None)
            if ov is not None:
                val = ov.val
                break
        traits = list(p.traits)
        for hm in reversed(hier):
            ov = next((o for o in hm.poverrides if o.name == pn), None)
            if ov is not None:
                for t, act in ov.trait_overrides:
                    if act:
                        traits.append(t)
                    elif t in traits:
                        traits.remove(t)
        traits = [t for t in dict.fromkeys(traits) if t not in TOOL_TIME_PROPERTY_TRAITS]
        src = bsrc or (hier[-1] if any(t in BUILTIN_TRAITS for t in hier[-1].traits) else None)
        fprops.append(FlatProp(pn, val, traits, src.name if src else None))
    # behaviors
    behs = {}
    border = []

    def collectb(m, path):
        path = path + [m]
        for b in m.behaviors:
            if b.name not in behs:
                behs[b.name] = (b, list(path))
                border.append(b.name)
        for g, n in m.mixins:
            s = models.get(n)
            if s is not None:
                collectb(s, path)

    collectb(X, [])
    fbeh = []
    for bn in border:
        b, hier = behs[bn]
        provider = next((hm for hm in hier if any(o.name == bn for o in hm.boverrides)), hier[-1])
        traits = list(b.traits)
        target = b.target
        for hm in reversed(hier):
            ov = next((o for o in hm.boverrides if o.name == bn), None)
            if ov is not None:
                target = ov.target
                for t, act in ov.trait_overrides:
                    if act:
                        traits.append(t)
                    elif t in traits:
                        traits.remove(t)
        traits = sorted(dict.fromkeys(traits))
        extends = 'Extends' in traits
        rextends = not extends and 'ReverseExtends' in traits
        overrides = not (extends or rextends)
        if overrides or provider.name == X.name:
            fbeh.append((bn, target, provider.name, traits))
        else:
            fbeh.append((bn, 'Virtual', None, traits))     # (invocation order not modelled)
    return dict(name=X.name, traits=[t for t in X.traits if t not in TOOL_TIME_MODEL_TRAITS],
                mixins=[p.name for p in parents if p is not X], props=fprops, behaviors=fbeh,
                extradata=[(n, t, sorted(e, key=lambda x: x[1])) for n, t, e in X.extradata])
