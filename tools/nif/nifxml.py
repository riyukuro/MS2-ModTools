#!/usr/bin/env python3
"""nifxml.py: a small nif.xml-driven reader/writer for Gamebryo NIF/KF files, aimed at 30.2.0.3 (MapleStory 2).

The format description is niftools nif.xml (BSD, https://github.com/niftools/nifxml); this module interprets it at run
time, so every block type nif.xml knows is read and written the same way.  Nothing here comes from the Gamebryo SDK.

    spec = Spec('nif.xml')                     # or Spec(): MS2_NIFXML, $NIFXML or tools/nif/nif.xml
    nif = Nif.load(spec, 'x.nif')              # nif.blocks: list of (type_name_raw, dict)
    nif.save('y.nif')                          # writes header/blocks/footer from the dicts

Values: basics -> int/float, structs -> dict (field name -> value), arrays -> list, binary byte arrays -> bytes.
Block dicts also carry '_type' (the nif.xml niobject name).  NiFixedString/string/FilePath are header string indices;
use nif.str(i) / nif.addstr(s).
"""
import os, re, struct, sys, xml.etree.ElementTree as ET

V30_2_0_3 = 0x1E020003


def parse_ver(s):
    s = s.strip()
    if s.startswith('V'):
        s = s[1:].split('__')[0].replace('_', '.')
    p = [int(x) for x in s.split('.')] + [0, 0, 0]
    return (p[0] << 24) | (p[1] << 16) | (p[2] << 8) | p[3]


BASIC = {'uint64': 'Q', 'int64': 'q', 'ulittle32': 'I', 'uint': 'I', 'int': 'i', 'ushort': 'H', 'short': 'h',
         'char': 'B', 'sbyte': 'b', 'byte': 'B', 'normbyte': 'B', 'bool': 'B', 'BlockTypeIndex': 'H',
         'FileVersion': 'I', 'float': 'f', 'hfloat': 'e', 'Ptr': 'i', 'Ref': 'i', 'StringOffset': 'I',
         'NiFixedString': 'I'}

_OPS = re.compile(r'(\|\||&&|==|!=|<=|>=|<<|>>|[-+*/()<>!&|~])')



def nifxml_path():
    """niftools nif.xml (not shipped): the MS2_NIFXML setting (tools/modtools_config.py), $NIFXML, or tools/nif/nif.xml."""
    here = os.path.dirname(os.path.abspath(__file__))
    try:
        sys.path.insert(0, os.path.dirname(here))
        import modtools_config as cfg
        p = cfg.get('MS2_NIFXML')
    except ImportError:
        p = None
    p = p or os.environ.get('NIFXML') or os.path.join(here, 'nif.xml')
    if not os.path.isfile(p):
        raise SystemExit(f"nif.xml not found at {p}: download niftools' nif.xml (https://github.com/niftools/nifxml) "
                         "and set MS2_NIFXML (or $NIFXML) to it, or put it at tools/nif/nif.xml")
    return p

class Spec:
    def __init__(self, path=None, ver=V30_2_0_3, user=0, bsver=0):
        path = path or nifxml_path()
        root = ET.parse(path).getroot()
        self.ver, self.user, self.bsver = ver, user, bsver
        self.tokens = []                       # (token, replacement) in file order
        for tok in root.findall('token'):
            for e in tok:
                self.tokens.append((e.get('token'), e.get('string')))
        self.storage, self.structs, self.objs, self.enums = {}, {}, {}, {}
        for e in root:
            if e.tag in ('enum', 'bitfield', 'bitflags'):
                self.storage[e.get('name')] = e.get('storage')
                self.enums[e.get('name')] = {o.get('name'): int(o.get('value'), 0) for o in e.findall('option')
                                             if o.get('value')}
            elif e.tag == 'struct':
                self.structs[e.get('name')] = e
            elif e.tag == 'niobject':
                self.objs[e.get('name')] = e
        self._cexpr = {}
        self._fields = {}

    # ---- expressions ----
    def expand(self, s):
        for _ in range(4):
            for t, r in self.tokens:
                if t in s:
                    s = s.replace(t, r)
        return s

    def compile(self, s):
        c = self._cexpr.get(s)
        if c is not None:
            return c
        e = self.expand(s)
        out = []
        for part in _OPS.split(e):
            if part is None:
                continue
            q = part.strip()
            if not q:
                continue
            if _OPS.fullmatch(q):
                out.append({'&&': ' and ', '||': ' or ', '!': ' not '}.get(q, q))
            elif re.fullmatch(r'\d+\.\d+\.\d+\.\d+', q):
                out.append(str(parse_ver(q)))
            elif re.fullmatch(r'0x[0-9a-fA-F]+|\d+(\.\d*)?(e[-+]?\d+)?', q):
                out.append(q)
            elif q == 'INFINITY':
                out.append('float("inf")')
            else:
                out.append('_g(%r)' % q)
        c = compile(''.join(out) or 'True', s, 'eval')
        self._cexpr[s] = c
        return c

    def ev(self, s, ctx, args):
        def g(name):
            if name == 'Version':
                return self.ver
            if name == 'User Version':
                return self.user
            if name.startswith('BS Header'):
                return self.bsver
            if name.startswith('#ARG'):
                return args[name.strip('#')]
            cur = ctx
            for k in name.split('\\'):
                cur = cur.get(k, 0) if isinstance(cur, dict) else 0   # absent (cond false) fields read as 0
            return cur
        v = eval(self.compile(s), {'_g': g, 'float': float})
        return v

    # ---- field selection ----
    def isa(self, t, base):
        while t:
            if t == base:
                return True
            t = self.objs[t].get('inherit') if t in self.objs else None
        return False

    def fields_of(self, name):
        """Static field list of a struct or niobject (with inherited fields), version-filtered."""
        f = self._fields.get(name)
        if f is not None:
            return f
        if name in self.objs:
            e = self.objs[name]
            f = list(self.fields_of(e.get('inherit'))) if e.get('inherit') else []
        else:
            e = self.structs[name]
            f = []
        for fe in e.findall('field'):
            a = fe.attrib
            if a.get('abstract') == 'true':
                continue
            if 'since' in a and self.ver < parse_ver(a['since']):
                continue
            if 'until' in a and self.ver > parse_ver(a['until']):
                continue
            if 'vercond' in a and not self.ev(a['vercond'], {}, {}):
                continue
            f.append(a)
        self._fields[name] = f
        return f


class Reader:
    def __init__(self, spec, buf, pos=0):
        self.s, self.b, self.p = spec, buf, pos

    def basic(self, t, n=None):
        fmt = BASIC[t]
        if n is None:
            v, = struct.unpack_from('<' + fmt, self.b, self.p)
            self.p += struct.calcsize(fmt)
            return v
        v = list(struct.unpack_from('<%d%s' % (n, fmt), self.b, self.p))
        self.p += struct.calcsize(fmt) * n
        return v

    def value(self, t, tmpl, args, n=None, binary=False, btype=None):
        s = self.s
        if t == '#T#':
            t = tmpl
        if t in s.storage:
            t = s.storage[t]
        if t in BASIC:
            if binary and n is not None:
                v = self.b[self.p:self.p + n]
                self.p += n
                return bytes(v)
            return self.basic(t, n)
        if n is not None:
            return [self.value(t, tmpl, args, None, btype=btype) for _ in range(n)]
        return self.struct(t, tmpl, args, btype)

    def struct(self, name, tmpl, args, btype=None):
        s = self.s
        d = {}
        for a in s.fields_of(name):
            if 'onlyT' in a and not s.isa(btype, a['onlyT']):
                continue
            if 'excludeT' in a and s.isa(btype, a['excludeT']):
                continue
            if 'cond' in a and not s.ev(a['cond'], d, args):
                continue
            sub = {}
            for k, v in a.items():
                if k.startswith('arg'):
                    key = 'ARG' if k == 'arg' else 'ARG' + k[3:]
                    sub[key] = s.ev(v, d, args)
            t = a['type']
            if t == '#T#':
                t = tmpl
            ft = a.get('template')
            if ft == '#T#':
                ft = tmpl
            if 'length' in a:
                n = int(s.ev(a['length'], d, args))
                if 'width' in a:
                    w = a['width']
                    if t in BASIC or t in s.storage:
                        widths = [int(s.ev(w, d, args))] * n if not isinstance(d.get(w), list) else d[w]
                    else:
                        widths = [int(s.ev(w, d, args))] * n
                    v = [self.value(t, ft, sub, wi, a.get('binary') == 'true', btype) for wi in widths]
                else:
                    v = self.value(t, ft, sub, n, a.get('binary') == 'true', btype)
            else:
                v = self.value(t, ft, sub, None, False, btype)
            d[a['name']] = v
        return d


class Writer:
    def __init__(self, spec):
        self.s, self.out = spec, bytearray()

    def value(self, t, tmpl, args, v, n=None, binary=False, btype=None):
        s = self.s
        if t == '#T#':
            t = tmpl
        if t in s.storage:
            t = s.storage[t]
        if t in BASIC:
            fmt = BASIC[t]
            if n is None:
                self.out += struct.pack('<' + fmt, v)
            elif binary or isinstance(v, (bytes, bytearray)):
                assert len(v) == n, (t, len(v), n)
                self.out += v
            else:
                assert len(v) == n, (t, len(v), n)
                self.out += struct.pack('<%d%s' % (n, fmt), *v)
            return
        if n is not None:
            assert len(v) == n, (t, len(v), n)
            for x in v:
                self.struct(t, tmpl, args, x, btype)
            return
        self.struct(t, tmpl, args, v, btype)

    def struct(self, name, tmpl, args, d, btype=None):
        s = self.s
        for a in s.fields_of(name):
            if 'onlyT' in a and not s.isa(btype, a['onlyT']):
                continue
            if 'excludeT' in a and s.isa(btype, a['excludeT']):
                continue
            if 'cond' in a and not s.ev(a['cond'], d, args):
                continue
            sub = {}
            for k, v in a.items():
                if k.startswith('arg'):
                    key = 'ARG' if k == 'arg' else 'ARG' + k[3:]
                    sub[key] = s.ev(v, d, args)
            t = a['type']
            if t == '#T#':
                t = tmpl
            ft = a.get('template')
            if ft == '#T#':
                ft = tmpl
            v = d[a['name']]
            if 'length' in a:
                n = int(s.ev(a['length'], d, args))
                if 'width' in a:
                    for x in v:
                        self.value(t, ft, sub, x, len(x), a.get('binary') == 'true', btype)
                else:
                    self.value(t, ft, sub, v, n, a.get('binary') == 'true', btype)
            else:
                self.value(t, ft, sub, v, None, False, btype)


class RefWalker:
    """Visit every Ref/Ptr field of a decoded block (the same field walk as Writer).  fn(kind, value) -> new value,
    kind is 'Ref' or 'Ptr'; the block dict is updated in place."""

    def __init__(self, spec, fn):
        self.s, self.fn = spec, fn

    def value(self, t, tmpl, args, v, n=None, btype=None):
        s = self.s
        if t == '#T#':
            t = tmpl
        if t in ('Ref', 'Ptr'):
            if n is None:
                return self.fn(t, v)
            return [self.fn(t, x) for x in v]
        if t in s.storage or t in BASIC:
            return v
        if n is not None:
            for x in v:
                self.struct(t, tmpl, args, x, btype)
            return v
        self.struct(t, tmpl, args, v, btype)
        return v

    def struct(self, name, tmpl, args, d, btype=None):
        s = self.s
        for a in s.fields_of(name):
            if 'onlyT' in a and not s.isa(btype, a['onlyT']):
                continue
            if 'excludeT' in a and s.isa(btype, a['excludeT']):
                continue
            if 'cond' in a and not s.ev(a['cond'], d, args):
                continue
            sub = {}
            for k, v in a.items():
                if k.startswith('arg'):
                    key = 'ARG' if k == 'arg' else 'ARG' + k[3:]
                    sub[key] = s.ev(v, d, args)
            t = a['type']
            if t == '#T#':
                t = tmpl
            ft = a.get('template')
            if ft == '#T#':
                ft = tmpl
            v = d[a['name']]
            if 'length' in a:
                if 'width' in a:
                    d[a['name']] = [self.value(t, ft, sub, x, len(x), btype) for x in v]
                else:
                    d[a['name']] = self.value(t, ft, sub, v, len(v), btype)
            else:
                d[a['name']] = self.value(t, ft, sub, v, None, btype)


def _sized(b, o):
    n, = struct.unpack_from('<I', b, o)
    return b[o + 4:o + 4 + n], o + 4 + n


class Nif:
    """A whole NIF/KF file: header fields, block list, footer roots."""

    def __init__(self, spec):
        self.spec = spec
        self.header_line = b'Gamebryo File Format, Version 30.2.0.3'
        self.ver, self.endian, self.user = V30_2_0_3, 1, 0
        self.metadata = b''
        self.types, self.blocks, self.strings, self.groups, self.roots = [], [], [], [], [0]
        self.maxlen_hint = 0

    @staticmethod
    def base_type(raw):
        return raw.split(b'\x01')[0].decode()

    @classmethod
    def load(cls, spec, path_or_bytes, decode=True):
        b = path_or_bytes if isinstance(path_or_bytes, (bytes, bytearray)) else open(path_or_bytes, 'rb').read()
        n = cls(spec)
        nl = b.index(b'\n')
        n.header_line = b[:nl]
        o = nl + 1
        n.ver, = struct.unpack_from('<I', b, o); o += 4
        n.endian = b[o]; o += 1
        n.user, nb = struct.unpack_from('<II', b, o); o += 8
        n.metadata, o = _sized(b, o)
        nt, = struct.unpack_from('<H', b, o); o += 2
        for _ in range(nt):
            t, o = _sized(b, o)
            n.types.append(t)
        idx = struct.unpack_from('<%dH' % nb, b, o); o += 2 * nb
        sizes = struct.unpack_from('<%dI' % nb, b, o); o += 4 * nb
        ns, n.maxlen_hint = struct.unpack_from('<II', b, o); o += 8
        for _ in range(ns):
            s, o = _sized(b, o)
            n.strings.append(s)
        ng, = struct.unpack_from('<I', b, o); o += 4
        n.groups = list(struct.unpack_from('<%dI' % ng, b, o)); o += 4 * ng
        for i in range(nb):
            raw = n.types[idx[i] & 0x7fff]
            blk = {'_raw': raw, '_idx': idx[i]}
            if decode:
                r = Reader(spec, b, o)
                t = cls.base_type(raw)
                blk.update(r.struct(t, None, {}, t))
                blk['_type'] = t
                assert r.p == o + sizes[i], ('block %d %s: read %d of %d bytes' % (i, raw, r.p - o, sizes[i]))
            else:
                blk['_bytes'] = b[o:o + sizes[i]]
            n.blocks.append(blk)
            o += sizes[i]
        nr, = struct.unpack_from('<I', b, o); o += 4
        n.roots = list(struct.unpack_from('<%di' % nr, b, o)); o += 4 * nr
        assert o == len(b), (o, len(b))
        return n

    def str(self, i):
        return None if i in (0xFFFFFFFF, -1) else self.strings[i].decode('latin1')

    def addstr(self, s):
        e = s.encode('latin1')
        if e in self.strings:
            return self.strings.index(e)
        self.strings.append(e)
        return len(self.strings) - 1

    def block_bytes(self, blk):
        if '_bytes' in blk and '_type' not in blk:
            return blk['_bytes']
        w = Writer(self.spec)
        w.struct(blk['_type'], None, {}, blk, blk['_type'])
        return bytes(w.out)

    def tobytes(self):
        out = bytearray(self.header_line + b'\n')
        out += struct.pack('<IBII', self.ver, self.endian, self.user, len(self.blocks))
        out += struct.pack('<I', len(self.metadata)) + self.metadata
        # rebuild the type table from the blocks (keeps first-use order)
        types = []
        for blk in self.blocks:
            if blk['_raw'] not in types:
                types.append(blk['_raw'])
        if all(t in self.types for t in types) and len(types) == len(self.types):
            types = self.types               # keep the original order when the set is unchanged
        out += struct.pack('<H', len(types))
        for t in types:
            out += struct.pack('<I', len(t)) + t
        bodies = [self.block_bytes(b) for b in self.blocks]
        for blk in self.blocks:
            out += struct.pack('<H', types.index(blk['_raw']) | (blk.get('_idx', 0) & 0x8000))
        for body in bodies:
            out += struct.pack('<I', len(body))
        maxlen = max([len(s) for s in self.strings] + [0])
        out += struct.pack('<II', len(self.strings), maxlen)
        for s in self.strings:
            out += struct.pack('<I', len(s)) + s
        out += struct.pack('<I', len(self.groups)) + struct.pack('<%dI' % len(self.groups), *self.groups)
        for body in bodies:
            out += body
        out += struct.pack('<I', len(self.roots)) + struct.pack('<%di' % len(self.roots), *self.roots)
        return bytes(out)

    def save(self, path):
        with open(path, 'wb') as f:
            f.write(self.tobytes())

    # ---- helpers ----
    def name(self, blk):
        n = blk.get('Name')
        if isinstance(n, dict):
            n = n.get('Index')
        return self.str(n) if n is not None else None

    def refs(self, blk, kinds=('Ref', 'Ptr')):
        """All block indices a block points to (Ref and/or Ptr fields, -1 skipped)."""
        out = []

        def fn(k, v):
            if k in kinds and v >= 0:
                out.append(v)
            return v
        RefWalker(self.spec, fn).struct(blk['_type'], None, {}, blk, blk['_type'])
        return out

    def remap(self, mapping):
        """Rewrite every Ref/Ptr through mapping (old index -> new index or -1) and the roots."""
        def fn(k, v):
            return mapping.get(v, -1) if v >= 0 else v
        for blk in self.blocks:
            RefWalker(self.spec, fn).struct(blk['_type'], None, {}, blk, blk['_type'])
        self.roots = [mapping.get(r, -1) for r in self.roots]

    def gc(self):
        """Drop blocks not reachable from the roots through Ref fields (Ptrs do not keep blocks alive)."""
        seen, todo = set(), list(self.roots)
        while todo:
            i = todo.pop()
            if i < 0 or i in seen:
                continue
            seen.add(i)
            todo.extend(self.refs(self.blocks[i], ('Ref',)))
        keep = sorted(seen)
        mapping = {o: n for n, o in enumerate(keep)}
        self.blocks = [self.blocks[i] for i in keep]
        self.remap(mapping)
        return len(mapping)

    def by_name(self, name, t=None):
        for i, b in enumerate(self.blocks):
            if (t is None or b['_type'] == t) and self.name(b) == name:
                return i
        return -1


if __name__ == '__main__':
    import sys
    spec = Spec()
    for p in sys.argv[1:]:
        raw = open(p, 'rb').read()
        n = Nif.load(spec, raw)
        ok = n.tobytes() == raw
        print('%-60s blocks %4d  round-trip %s' % (os.path.basename(p), len(n.blocks), 'IDENTICAL' if ok else 'DIFF'))
