#!/usr/bin/env python3
"""Python port of the server's trigger ingest normalisation (Maple2.File.Ingest TriggerMapper).

Shipped scripts (Xml.m2d trigger/<xblock>/<name>.xml) use the original names: CamelCase or Korean
action/condition names, CamelCase attributes, `!Name` negation and `<import>`. The ingest turns
them into the form the game server reads (TriggerStorage): snake_case names, renamed attributes,
merged imports. This module repeats those steps so tools can check a script the way the server
will see it.

Usage as a tool:  normalize.py <script.xml> [--root DIR]   prints the normalised XML.

Elements keep their source position in pseudo-attributes under the '{m}' namespace:
  {m}line      source line
  {m}file      source file (set on states merged from an <import>)
  {m}raw_name  the name before normalisation (actions, conditions, states)
  {m}raw_args  original attribute name for each renamed attribute: "new=old;..."
"""
import json, os, sys
import xml.etree.ElementTree as ET
from xml.parsers import expat

M = '{m}'
HERE = os.path.dirname(os.path.abspath(__file__))


def to_snake_case(text):
    """TriggerTranslate.ToSnakeCase."""
    text = text.replace(' ', '')
    for a, b in (('NPC', 'Npc'), ('NPc', 'Npc'), ('PVP', 'Pvp'), ('ID', 'Id'), ('PC', 'Pc'), ('UI', 'Ui'),
                 ('Setpc', 'SetPc'), ('UnSet', 'Unset'), ('Emotionloop', 'EmotionLoop')):
        text = text.replace(a, b)
    if not text:
        raise ValueError('empty name')  # C# text[0] throws IndexOutOfRange
    out = [text[0].lower()]
    for c in text[1:]:
        if c.isupper():
            out.append('_' + c.lower())
        else:
            out.append(c)
    return ''.join(out)


SUB_START = (('1st', 'First'), ('2nd', 'Second'), ('3rd', 'Third'), ('4th', 'Fourth'), ('5th', 'Fifth'),
             ('6th', 'Sixth'), ('7th', 'Seventh'))


def fix_class_name(name):
    """TriggerMapper.FixClassName (applied to state@name and transition@state)."""
    if name is None:
        return None
    if not name.strip():
        return 'State'
    reserved = {'None': 'StateNone', 'True': 'StateTrue', 'False': 'StateFalse', 'del': 'StateDelete'}
    if name in reserved:
        return reserved[name]
    name = name.replace('-', 'To').replace(' ', '_').replace('.', '_')
    for k, v in SUB_START:
        if name.startswith(k):
            name = name.replace(k, v)
            break
    prefix = ''
    while name and not name[0].isalpha():
        if name[0] != '_':
            prefix += name[0]
        name = name[1:]
    if not prefix:
        return name
    if not name:
        return 'State' + prefix
    return name + '_' + prefix if not name[-1].isalpha() else name + prefix


def translate(name, table):
    """TriggerMapper.Translate: split on '_' and ' ', look each piece up, join, snake-case."""
    import re
    return to_snake_case(''.join(table.get(p, p) for p in re.split(r'[_ ]', name)))


class ParseError(Exception):
    def __init__(self, msg, line=None):
        super().__init__(msg)
        self.line = line


def parse_xml(path):
    """Parse with expat, recording line numbers. Returns the root Element."""
    with open(path, 'rb') as f:
        data = f.read()
    return parse_xml_bytes(data)


def parse_xml_bytes(data):
    p = expat.ParserCreate()
    stack, root = [], [None]

    def start(tag, attrs):
        el = ET.Element(tag, attrs)
        el.set(M + 'line', str(p.CurrentLineNumber))
        if stack:
            stack[-1].append(el)
        else:
            root[0] = el
        stack.append(el)

    def end(tag):
        stack.pop()

    p.StartElementHandler = start
    p.EndElementHandler = end
    p.buffer_text = True
    try:
        p.Parse(data, True)
    except expat.ExpatError as e:
        raise ParseError(expat.ErrorString(e.code), e.lineno)
    return root[0]


def strip_meta(el):
    for e in el.iter():
        for k in [k for k in e.attrib if k.startswith(M)]:
            del e.attrib[k]
    return el


def import_pack_path(raw):
    """TriggerMapper.ExtractImportPaths for one path attribute; None if blank."""
    if raw is None or not raw.strip():
        return None
    path = raw.replace('\\', '/')
    if path.startswith('./'):
        path = path[2:]
    tp = path.replace('Data/Xml/Trigger/', 'trigger/')
    if not tp.startswith('trigger/') and '/' not in tp:
        tp = 'trigger/' + tp
    return tp


class PackIndex:
    """Pack names ('trigger/<folder>/<file>.xml') -> files under a root directory."""

    ORDER_FILE = 'pack-order.txt'

    def __init__(self, root):
        self.root = root
        self.names = []
        tdir = os.path.join(root, 'trigger')
        order = os.path.join(root, self.ORDER_FILE)
        if os.path.exists(order):
            # pack names in archive order (the order the ingest searches); see README
            with open(order, encoding='utf-8') as f:
                self.names = [l.strip() for l in f if l.strip().startswith('trigger/')]
        elif os.path.isdir(tdir):
            for folder in sorted(os.listdir(tdir)):
                fd = os.path.join(tdir, folder)
                if os.path.isdir(fd):
                    for fn in sorted(os.listdir(fd)):
                        self.names.append('trigger/%s/%s' % (folder, fn))
        self.exact = set(self.names)

    def resolve(self, pack_path):
        """LoadImportedStates lookup: exact ordinal match, else first entry that EndsWith
        '/'+filename or Equals, ignoring case, in archive order. The archive order comes from
        <root>/pack-order.txt when it exists (one pack name per line), else sorted names."""
        if pack_path in self.exact:
            return pack_path
        base = '/' + pack_path.rsplit('/', 1)[-1].lower()
        pl = pack_path.lower()
        for n in self.names:
            nl = n.lower()
            if nl.endswith(base) or nl == pl:
                return n
        return None

    def file(self, pack_name):
        return os.path.join(self.root, *pack_name.split('/'))


def splitter_new_name(attr, names):
    """Name the splitter attribute gets after Normalizer._rename_args."""
    try:
        sk = to_snake_case(attr)
    except ValueError:
        return attr
    return names.get(sk, sk)


class Normalizer:
    def __init__(self, schema=None, features=None):
        if schema is None:
            with open(os.path.join(HERE, 'schema.json'), encoding='utf-8') as f:
                schema = json.load(f)
        ing = schema['ingest']
        self.ov = ing['overrides']
        self.tr_action = ing['translate']['ActionLookup']
        self.tr_cond = ing['translate']['ConditionLookup']
        self.features = set(features if features is not None else ing.get('features_enabled', []))
        self._packs = {}

    def packs(self, root):
        if root not in self._packs:
            self._packs[root] = PackIndex(root)
        return self._packs[root]

    def load_file(self, path, rel=None, root=None, issues=None):
        """Parse, merge imports (when root is given) and normalise. issues gets (level, line, msg)."""
        doc = parse_xml(path)
        self.merge_imports(doc, root, issues)
        self.normalize(doc, issues)
        return doc

    def merge_imports(self, doc, root, issues=None):
        imports = [el for el in doc.iter('import')]
        states = {}
        for im in imports:
            pp = import_pack_path(im.get('path'))
            line = int(im.get(M + 'line', 0))
            if pp is None:
                continue
            if root is None:
                continue
            name = self.packs(root).resolve(pp)
            if name is None:
                if issues is not None:
                    issues.append(('error', line, 'import %r does not resolve (pack path %r); its states are '
                                   'silently left out' % (im.get('path'), pp)))
                continue
            try:
                idoc = parse_xml(self.packs(root).file(name))
            except ParseError as e:
                if issues is not None:
                    issues.append(('error', line, 'import %r: %s parse error line %s: %s' % (im.get('path'), name, e.line, e)))
                continue
            im.set(M + 'resolved', name)
            for st in idoc.iter('state'):
                sn = st.get('name')
                if sn:
                    st.set(M + 'file', name)
                    states[sn] = st  # later imports win
        existing = {st.get('name') for st in doc.iter('state')}
        for sn, st in states.items():
            if sn not in existing:
                doc.append(st)
        # remove <import> nodes
        for parent in list(doc.iter()):
            for ch in list(parent):
                if ch.tag == 'import':
                    parent.remove(ch)

    def _rename_args(self, el, names):
        renamed = []
        for k in [k for k in el.attrib if k != 'name' and not k.startswith(M)]:
            try:
                sk = to_snake_case(k)
            except ValueError:
                continue
            new = names.get(sk)
            if new is None:
                if k != sk:
                    new = sk
                else:
                    continue
            v = el.attrib.pop(k)
            el.set(new, v)  # last one wins if two map to the same name
            renamed.append('%s=%s' % (new, k))
        if renamed:
            el.set(M + 'raw_args', ';'.join(renamed))

    def normalize(self, doc, issues=None):
        # remove nodes with a disabled feature
        for parent in list(doc.iter()):
            for ch in list(parent):
                if ch.tag in ('state', 'action', 'condition'):
                    f = ch.get('feature')
                    if f is not None and f != '' and f not in self.features:
                        parent.remove(ch)
        for st in doc.iter('state'):
            if st.get('name') is not None:
                st.set(M + 'raw_name', st.get('name'))
                st.set('name', fix_class_name(st.get('name')))
        for tr in doc.iter('transition'):
            if tr.get('state') is not None:
                tr.set(M + 'raw_state', tr.get('state'))
                tr.set('state', fix_class_name(tr.get('state')))
        for el in doc.iter('action'):
            raw = el.get('name')
            if raw is None:
                continue
            el.set(M + 'raw_name', raw)
            try:
                name = translate(raw, self.tr_action)
            except ValueError:
                name = raw
            el.set('name', name)
            ov = self.ov['action'].get(name)
            if ov is None:
                continue
            if 'splitter' in ov:
                sp = el.get(ov['splitter'])
                # missing splitter attribute: the first value of the outer Types is the key
                key = sp if sp is not None else (next(iter(ov['types'].values())) if ov.get('types') else None)
                sub = ov['lookup'].get(key) if key is not None else None
                if sub is None:
                    if issues is not None:
                        issues.append(('error', int(el.get(M + 'line', 0)),
                                       'action %r: %s=%r has no ingest mapping (FunctionLookup keys %s); the ingest '
                                       'throws on this script' % (raw, ov['splitter'], sp, sorted(ov['lookup']))))
                    el.set(M + 'ingest_fail', '1')
                    continue
                el.set(M + 'splitter', splitter_new_name(ov['splitter'], sub['names']))
                ov = sub
                el.set('name', ov['name'])
            self._rename_args(el, ov['names'])
        for el in doc.iter('condition'):
            raw = el.get('name')
            if raw is None:
                continue
            el.set(M + 'raw_name', raw)
            if raw.startswith('!'):
                el.set('negate', 'true')
            try:
                name = translate(raw.lstrip('!'), self.tr_cond)
            except ValueError:
                name = raw
            el.set('name', name)
            ov = self.ov['condition'].get(name)
            if ov is None:
                continue
            if ov['name'] != name:
                el.set('name', ov['name'])
            self._rename_args(el, ov['names'])
        return doc


def load_shipped(root):
    """Yield (path, pack_name) for every trigger/<folder>/<file>.xml under root."""
    for name in PackIndex(root).names:
        yield os.path.join(root, *name.split('/')), name


def main():
    import argparse
    ap = argparse.ArgumentParser(description='Print a shipped trigger script in normalised (server) form.')
    ap.add_argument('script')
    ap.add_argument('--root', help='dir holding trigger/<folder>/*.xml, for <import> resolution')
    a = ap.parse_args()
    n = Normalizer()
    issues = []
    doc = n.load_file(a.script, root=a.root, issues=issues)
    for lv, ln, msg in issues:
        print('%s:%s: %s: %s' % (a.script, ln, lv, msg), file=sys.stderr)
    strip_meta(doc)
    ET.indent(doc)
    sys.stdout.write(ET.tostring(doc, encoding='unicode') + '\n')


if __name__ == '__main__':
    main()
