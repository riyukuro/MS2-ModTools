"""xblock <-> block conversion (the importer and the Toolbench XmlBlockExporter re-implementation)."""
import collections
import os
import re
import uuid

from . import values as V
from .block import Block, BEntity, Layer, write_block, read_block
from .xblock import Entity, Prop, read_xblock, write_xblock, detect_dialect, ms2_prop_order

LIST_ENTITYREF = 'AssocEntityRef'
ENTITYREF = 'EntityRef'


def dashed(n):
    return str(uuid.UUID(n))


def undashed(g):
    return g.replace('-', '').lower()


def cat_label(cat):
    """('presets','presets object') -> 'Presets_object' (layer/folder/project label)."""
    kind, name = cat
    if kind == 'library':
        return name
    return re.sub(r'[^A-Za-z0-9_]+', '_', name.title()).strip('_')


# Library layout (relative to the Library root): lib dir -> emtproj file name
LIB_EMTPROJ = {
    'maplestory2library': 'maplestory2library/maplestory2.emtproj',
    'standardmodellibrary': 'standardmodellibrary/standardmodellibrary.emtproj',
    'physxmodellibrary': 'physxmodellibrary/physxmodellibrary.emtproj',
    'triggerslibrary': 'triggerslibrary/triggerslibrary.emtproj',
    'beastmodellibrary': 'beastmodellibrary/beastmodellibrary.emtproj',
    'gimodellibrary': 'gimodellibrary/gimodellibrary.emtproj',
    'tool': 'tool/tool.emtproj',
}


def project_for(cat, lib_root='..\\Library'):
    kind, name = cat
    if kind == 'library':
        return lib_root + '\\' + LIB_EMTPROJ[name].replace('/', '\\')
    return lib_root + '\\Presets\\' + cat_label(cat) + '.emtproj'


class Report:
    def __init__(self):
        self.c = collections.Counter()
        self.samples = collections.defaultdict(list)

    def note(self, key, sample=None, n=1):
        self.c[key] += n
        if sample is not None and len(self.samples[key]) < 5:
            self.samples[key].append(sample)

    def merge(self, other):
        self.c.update(other.c)
        for k, v in other.samples.items():
            for s in v:
                if len(self.samples[k]) < 5:
                    self.samples[k].append(s)


def xblock_to_block(xpath, index, name=None, layers='none', folders='none', lib_root='..\\Library',
                    model_ids=None, report=None, export_folder='.'):
    """Return a Block (in-memory) for the given .xblock. layers/folders in {'none','category'}."""
    rep = report or Report()
    ents = list(read_xblock(xpath))
    names_by_id = {dashed(e.id): e.name for e in ents}
    blk = Block(name or os.path.splitext(os.path.basename(xpath))[0])
    blk.settings = [(k, t, (export_folder if k == 'Emergent.WorldBuilder.ExportFolder' else v))
                    for k, t, v in blk.settings]
    used_cats = collections.OrderedDict()
    layer_by_cat = {}
    folder_by_cat = {}

    for e in ents:
        m = index.get(e.model)
        types = index.prop_types(e.model)
        cat = index.category(e.model)
        if m is None:
            rep.note('entity_model_missing_from_flats', e.model)
        else:
            used_cats.setdefault(cat, True)
        props = list(e.props)
        if m is not None and index.is_nameable(e.model) and not any(p.name == 'Name' for p in props):
            # stale xblock: exported before the model gained Nameable; Toolbench re-export would add Name
            rep.note('nameable_without_Name', (os.path.basename(xpath), e.id, e.model))
        # drop the Name property Toolbench synthesises for models mixing in "Nameable"
        if m is not None and index.is_nameable(e.model) and props and props[-1].name == 'Name' \
                and props[-1].scalar and props[-1].items[0][1] == e.name:
            props.pop()
            rep.note('synthetic_Name_dropped')
        out_props = []
        for p in props:
            t = types.get(p.name) if types else None
            if types is not None and t is None:
                rep.note('property_not_in_model', (e.model, p.name))
            items = p.items
            if t in V.COLOR_TYPES or t in (ENTITYREF, LIST_ENTITYREF):
                new = []
                for idx, val in items:
                    if t == 'Color' or t == 'ColorA':
                        nv, exact = V.color_xblock_to_block(val, 3 if t == 'Color' else 4)
                        if not exact:
                            rep.note('color_not_byte_exact', (xpath, e.id, p.name, val))
                    else:
                        nv, dang = V.entityref_xblock_to_block(val, names_by_id)
                        if dang:
                            rep.note('entityref_dangling', (xpath, e.id, p.name, val))
                    new.append((idx, nv))
                items = new
            out_props.append(Prop(p.name, items))
        be = BEntity(dashed(e.id), e.model, e.name, e.iterations, out_props,
                     model_id=(model_ids or {}).get(e.model))
        if folders == 'category' and cat:
            if cat not in folder_by_cat:
                folder_by_cat[cat] = str(uuid.uuid5(uuid.NAMESPACE_URL, 'ms2-folder:' + blk.name + ':' + cat_label(cat)))
                blk.folders.append((folder_by_cat[cat], cat_label(cat), V.ZERO_GUID))
            be.folder = folder_by_cat[cat]
        if layers == 'category' and cat:
            lay = layer_by_cat.get(cat)
            if lay is None:
                lay = layer_by_cat[cat] = Layer(cat_label(cat), '%s.%s.layer' % (blk.name, cat_label(cat)))
                blk.layers.append(lay)
            lay.entities.append(be)
        else:
            blk.entities.append(be)
    if None not in used_cats and any(index.get(e.model) is None for e in ents):
        pass
    blk.references = [project_for(c, lib_root) for c in used_cats]
    for l in blk.layers:
        l.entities.sort(key=lambda x: x.id)
    blk.entities.sort(key=lambda x: x.id)
    rep.note('entities', n=len(ents))
    return blk, rep


def _entity_to_xblock(be, index, model_ids_rev=None, drop_dangling=False):
    types = index.prop_types(be.model)
    props = []
    has_name = False
    for p in be.props:
        if p.name == 'Name':
            has_name = True
        t = types.get(p.name) if types else None
        items = p.items
        if t == 'Color' or t == 'ColorA':
            n = 3 if t == 'Color' else 4
            items = [(i, V.color_block_to_xblock(v, n)) for i, v in items]
        elif t in (ENTITYREF, LIST_ENTITYREF):
            items = [(i, V.entityref_block_to_xblock(v)) for i, v in items]
        props.append(Prop(p.name, items))
    if not has_name and index.is_nameable(be.model):
        props.append(Prop('Name', [(None, be.name)]))
    return Entity(undashed(be.id), be.model, be.name, be.iterations, props)


def _order_props(xe, index, order):
    """order: 'block' (as stored in the .block), 'ms2' (MS2's shipped xblocks: case-insensitive, Name last) or
    'toolbench' (what the stock exporter writes: the model's flat property order, Name last)."""
    if order == 'block':
        return
    names = [p.name for p in xe.props]
    if order == 'ms2':
        want = ms2_prop_order(names)
    elif order == 'toolbench':
        m = index.get(xe.model)
        pos = {p['name']: i for i, p in enumerate(m['props'])} if m else {}
        want = sorted((n for n in names if n != 'Name'), key=lambda n: pos.get(n, 1 << 30)) + \
            (['Name'] if 'Name' in names else [])
    else:
        raise ValueError(order)
    rank = {n: i for i, n in enumerate(want)}
    xe.props.sort(key=lambda p: rank[p.name])


def block_to_xblocks(block, index, dirpath, outdir, dialect='tab', order='block'):
    """Re-implementation of XmlBlockExporter.ProcessDocuments. Returns list of written files."""
    written = []

    def collect(layer_entities, layer_children, acc):
        for e in layer_entities:
            acc[e.id] = e
        for ch in layer_children:
            if ch.export_hint:
                emit(ch.name, ch.entities, ch.layers, ch.path)
            else:
                collect(ch.entities, ch.layers, acc)

    def emit(name, ents, children, path):
        acc = {}
        collect(ents, children, acc)
        out = []
        for e in acc.values():
            xe = _entity_to_xblock(e, index)
            _order_props(xe, index, order)
            out.append(xe)
        out.sort(key=lambda x: x.id)           # orderby PersistentID (hex order matches shipped files)
        fn = os.path.join(outdir, os.path.splitext(os.path.basename(path or name))[0] + '.xblock')
        write_xblock(out, fn, dialect)
        written.append(fn)

    emit(block.name, block.entities, block.layers, block.name)
    return written


def canonical_entities(ents, sort_props=True):
    """Canonical comparable form of an xblock entity list (order of entities by id; props by name if asked)."""
    res = []
    for e in ents:
        props = [(p.name, tuple(p.items)) for p in e.props]
        if sort_props:
            props.sort(key=lambda x: x[0])
        res.append((e.id, e.model, e.name, e.iterations, tuple(props)))
    res.sort(key=lambda x: x[0])
    return res
