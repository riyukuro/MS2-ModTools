"""Model library index (built from the shipped .flat files by flatindex.py)."""
import json, os, re

DEFAULT_INDEX = os.path.join(os.path.dirname(__file__), '..', '..', '..', 'out', 'maps', 'analysis', 'flatindex.json')

COLOR_TYPES = ('Color', 'ColorA')


def _category(flatfile):
    """'flat/library/maplestory2library/x.flat' -> ('library','maplestory2library'); 'flat/presets/presets object/x.flat' -> ('presets','presets object')"""
    parts = flatfile.split('/')
    return parts[1], parts[2]


class ModelIndex:
    def __init__(self, path=None):
        path = path or DEFAULT_INDEX
        if not os.path.isfile(path):
            raise SystemExit(f'no model index at {os.path.normpath(path)}: build it with '
                             'tools/mapedit/flatindex.py <Exported dir> <out.json> (--index, or out/maps/analysis/flatindex.json)')
        self.models = json.load(open(path))['models']
        self._types = {}
        self._nameable = {}

    def get(self, name):
        return self.models.get(name)

    def prop_types(self, model):
        t = self._types.get(model)
        if t is None:
            m = self.models.get(model)
            t = {p['name']: p['type'] for p in m['props']} if m else None
            self._types[model] = t
        return t

    def is_nameable(self, model):
        n = self._nameable.get(model)
        if n is None:
            m = self.models.get(model)
            n = bool(m) and any(x[0] == 'Nameable' for x in m['mixins'])
            self._nameable[model] = n
        return n

    def category(self, model):
        m = self.models.get(model)
        return _category(m['file']) if m else None

    def top_mixin(self, model):
        """The model/preset this one is built on (first mixin listed in the flat file)."""
        m = self.models.get(model)
        return m['mixins'][0][0] if m and m['mixins'] else None
