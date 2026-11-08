"""Reader/writer for MS2 runtime .xblock files (<game><entitySet><entity .../></entitySet></game>)."""
from lxml import etree
from .xmlio import NetWriter


class Entity:
    __slots__ = ('id', 'model', 'name', 'iterations', 'props')

    def __init__(self, id, model, name, iterations, props):
        self.id = id                # 32 hex chars ("N" format, lower case)
        self.model = model          # modelName
        self.name = name
        self.iterations = iterations  # int
        self.props = props          # ordered list of Prop

    def __repr__(self):
        return 'Entity(%s %s %r %d props=%d)' % (self.id, self.model, self.name, self.iterations, len(self.props))


class Prop:
    """One <property name=..> element. items = list of (index_or_None, value)."""
    __slots__ = ('name', 'items')

    def __init__(self, name, items):
        self.name = name
        self.items = items

    def __repr__(self):
        return 'Prop(%s, %r)' % (self.name, self.items)

    @property
    def scalar(self):
        return len(self.items) == 1 and self.items[0][0] is None


def read_xblock(path):
    """Yield Entity objects in file order (streaming)."""
    ctx = etree.iterparse(path, events=('end',), tag='entity', huge_tree=True)
    for _, e in ctx:
        props = []
        for p in e:
            items = [(s.get('index'), s.get('value')) for s in p]
            props.append(Prop(p.get('name'), items))
        yield Entity(e.get('id'), e.get('modelName'), e.get('name'), int(e.get('iterations')), props)
        e.clear()
        while e.getprevious() is not None:
            del e.getparent()[0]


DIALECTS = {
    'tab': dict(indent='\t', newline_attrs=False),          # compact, majority of MS2 files
    'toolbench': dict(indent='  ', newline_attrs=True),     # stock ContentService.XmlWriterSettings
}


def detect_dialect(path):
    with open(path, 'rb') as f:
        head = f.read(300)
    return 'toolbench' if b'<game>\r\n  <entitySet>' in head else 'tab'


def xblock_text(entities, dialect='tab'):
    """Serialise entities exactly like Toolbench's XmlEntityExporter + XmlWriter settings."""
    out = []
    w = NetWriter(out, **DIALECTS[dialect])
    w.start('game')
    w.start('entitySet')
    for e in entities:
        w.start('entity', [('id', e.id), ('modelName', e.model), ('name', e.name), ('iterations', str(e.iterations))])
        for p in e.props:
            w.start('property', [('name', p.name)])
            for idx, val in p.items:
                if idx is None:
                    w.empty('set', [('value', val)])
                else:
                    w.empty('set', [('index', idx), ('value', val)])
            w.end()
        w.end()
    w.end()
    w.end()
    return w.finish()


def write_xblock(entities, path, dialect='tab'):
    data = xblock_text(entities, dialect)
    with open(path, 'wb') as f:
        f.write(data.encode('utf-8'))


def ms2_prop_key(name):
    return name.lower()


def ms2_prop_order(names):
    """Property order of MS2's shipped xblocks (15,151 of 15,186 tria entities; the rest are unsorted leftovers):
    case-insensitive by name, the synthetic 'Name' property last.  (Stock Toolbench writes the model's flat order,
    i.e. MixinProperties order: own properties first, then the mixins', 'Name' last.)"""
    rest = sorted((n for n in names if n != 'Name'), key=ms2_prop_key)
    return rest + (['Name'] if 'Name' in names else [])


def reorder_props(entities, mode='ms2', original=None):
    """Reorder each entity's properties in place.  mode 'ms2' = ms2_prop_order; 'keep' = untouched.
    `original` {entity id: [property names]}: when an entity exists there with the same property-name set, its
    order wins (reproduces the shipped file exactly, including the unsorted leftovers)."""
    for e in entities:
        if mode == 'keep' and original is None:
            continue
        names = [p.name for p in e.props]
        o = original.get(e.id) if original else None
        if o is not None and sorted(o) == sorted(names):
            order = o
        elif mode == 'ms2':
            order = ms2_prop_order(names)
        else:
            continue
        pos = {}
        for i, n in enumerate(order):
            pos.setdefault(n, i)
        e.props.sort(key=lambda p: pos[p.name])
    return entities
