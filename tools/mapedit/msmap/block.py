"""Toolbench WorldBuilder .block / .layer documents (version 3).

Mirrors Emergent.Toolbench.WorldBuilder: BlockDocument/LayerDocument + Entity.ToXml, LocalEntityAspect,
LayerAspect, EntityPaletteAspect (model-references), EntityFolderAspect, SettingsAspect.
"""
import os
from lxml import etree
from .xmlio import NetWriter
from .xblock import Prop

DEFAULT_SETTINGS = [
    ('Emergent.WorldBuilder.CustomShaders.CustomShaderDirectory', 'String', ''),
    ('Emergent.WorldBuilder.ExportFolder', 'String', '.'),
    ('Emergent.WorldBuilder.Exporter', 'String', 'XmlBlockExporter'),
    ('Emergent.WorldBuilder.Grid.ScalePrecision', 'Float', '0.1'),
    ('Emergent.WorldBuilder.Grid.ScalePrecisionEnabled', 'Boolean', 'False'),
    ('Emergent.WorldBuilder.Grid.TranslationPrecision', 'Float', '1'),
    ('Emergent.WorldBuilder.Grid.TranslationPrecisionEnabled', 'Boolean', 'True'),
    ('Emergent.WorldBuilder.SnapPoints.SnapPointStickinessRadius', 'Float', '1'),
    ('Emergent.WorldBuilder.World.Scale', 'Float', '100'),
]
EXPORT_HINT = 'Emergent.Toolbench.WorldBuilder.PerContentLayerExportHint'


class BEntity:
    __slots__ = ('id', 'model', 'model_id', 'name', 'iterations', 'visible', 'locked', 'folder', 'tags', 'props')

    def __init__(self, id, model, name, iterations, props, model_id=None, visible=True, locked=False,
                 folder=None, tags=None):
        self.id = id                # dashed guid
        self.model = model
        self.model_id = model_id
        self.name = name
        self.iterations = iterations
        self.visible = visible
        self.locked = locked
        self.folder = folder
        self.tags = tags
        self.props = props


class Layer:
    def __init__(self, name, path=None, entities=None, export_hint=False):
        self.name = name
        self.path = path            # relative file name, e.g. 'Cubes.layer'
        self.entities = entities if entities is not None else []
        self.export_hint = export_hint
        self.layers = []


class Block:
    def __init__(self, name):
        self.name = name
        self.references = []        # relative paths of .emtproj files (backslashes like Toolbench)
        self.settings = list(DEFAULT_SETTINGS)
        self.layers = []
        self.entities = []
        self.folders = []           # (guid, name, parent_guid)


def _w_settings(w, settings):
    w.start('tool-settings', [('version', '1')])
    for sid, typ, val in settings:
        w.start('setting', [('id', sid), ('property-type', typ)])
        w.leaf('value', val)
        w.end()
    w.end()


def _w_entity(w, e):
    attrs = [('id', e.id), ('model-name', e.model)]
    if e.model_id:
        attrs.append(('model-id', e.model_id))
    attrs += [('name', e.name), ('iterations', str(e.iterations)),
              ('visible', 'true' if e.visible else 'false'), ('locked', 'true' if e.locked else 'false')]
    if e.folder:
        attrs.append(('folder', e.folder))
    if e.tags:
        attrs.append(('tags', e.tags))
    w.start('entity', attrs)
    w.start('properties')
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


def _w_entities(w, entities):
    w.start('entities', [('version', '1')])
    for e in entities:
        _w_entity(w, e)
    w.end()


def _w_layers(w, layers):
    w.start('layers', [('version', '1')])
    for l in layers:
        w.leaf('layer', l.path)
    w.end()


def layer_text(layer):
    out = []
    w = NetWriter(out, indent='  ', newline_attrs=True)
    w.start('layer', [('version', '3'), ('name', layer.name)])
    _w_settings(w, [(EXPORT_HINT, 'Boolean', 'True')] if layer.export_hint else [])
    _w_layers(w, layer.layers)
    _w_entities(w, layer.entities)
    w.end()
    return w.finish()


def block_text(block):
    out = []
    w = NetWriter(out, indent='  ', newline_attrs=True)
    w.start('block', [('version', '3'), ('name', block.name)])
    w.start('model-references', [('version', '1')])
    for r in block.references:
        w.leaf('reference', r)
    w.end()
    _w_settings(w, block.settings)
    _w_layers(w, block.layers)
    _w_entities(w, block.entities)
    w.empty('selections', [('version', '1')])
    w.start('entity-folders', [('version', '1')])
    for gid, name, parent in block.folders:
        w.empty('folder', [('id', gid), ('name', name), ('folder', parent)])
    w.end()
    w.end()
    return w.finish()


def _w_layer_tree(layer, dirpath):
    for sub in layer.layers:
        _w_layer_tree(sub, dirpath)
    with open(os.path.join(dirpath, layer.path), 'wb') as f:
        f.write(layer_text(layer).encode('utf-8'))


def write_block(block, path):
    """Write block (+ .layer files next to it)."""
    d = os.path.dirname(os.path.abspath(path))
    for l in block.layers:
        _w_layer_tree(l, d)
    with open(path, 'wb') as f:
        f.write(block_text(block).encode('utf-8'))


# ---------------------------------------------------------------- reading

def _read_entities(xentities):
    for e in xentities.iterchildren('entity'):
        props = []
        xp = e.find('properties')
        if xp is not None:
            for p in xp.iterchildren('property'):
                props.append(Prop(p.get('name'), [(s.get('index'), s.get('value')) for s in p.iterchildren('set')]))
        yield BEntity(e.get('id'), e.get('model-name'), e.get('name'), int(e.get('iterations', '0')), props,
                      model_id=e.get('model-id'), visible=e.get('visible', 'true') == 'true',
                      locked=e.get('locked', 'false') == 'true', folder=e.get('folder'), tags=e.get('tags'))


def _settings(root):
    res = {}
    ts = root.find('tool-settings')
    if ts is not None:
        for s in ts.iterchildren('setting'):
            v = s.find('value')
            res[s.get('id')] = (s.get('property-type'), v.text or '' if v is not None else '')
    return res


def _read_layer(path, dirpath):
    root = etree.parse(os.path.join(dirpath, path), etree.XMLParser(huge_tree=True)).getroot()
    l = Layer(root.get('name'), path)
    st = _settings(root)
    l.export_hint = st.get(EXPORT_HINT, (None, 'False'))[1].lower() == 'true'
    xe = root.find('entities')
    if xe is not None:
        l.entities = list(_read_entities(xe))
    xl = root.find('layers')
    if xl is not None:
        for x in xl.iterchildren('layer'):
            l.layers.append(_read_layer(x.text, dirpath))
    return l


def read_block(path):
    dirpath = os.path.dirname(os.path.abspath(path))
    root = etree.parse(path, etree.XMLParser(huge_tree=True)).getroot()
    b = Block(root.get('name'))
    b.references = [r.text for r in root.find('model-references').iterchildren('reference')]
    b.settings = [(k, t, v) for k, (t, v) in _settings(root).items()]
    xl = root.find('layers')
    if xl is not None:
        for x in xl.iterchildren('layer'):
            b.layers.append(_read_layer(x.text, dirpath))
    xe = root.find('entities')
    if xe is not None:
        b.entities = list(_read_entities(xe))
    xf = root.find('entity-folders')
    if xf is not None:
        b.folders = [(f.get('id'), f.get('name'), f.get('folder')) for f in xf.iterchildren('folder')]
    return b
