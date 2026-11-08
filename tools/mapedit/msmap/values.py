"""Property value conversions between the runtime (.xblock) and editable (.block) string forms.

Toolbench's TypeService stores/loads these as StringValue:
  Color    block "R, G, B" (bytes)       xblock "r/255, g/255, b/255"  (float32, .NET "G7")
  ColorA   block "R, G, B, A" (bytes)    xblock four floats
  EntityRef block "guid|name|hierarchy"  xblock "guid" ("" in block == Guid.Empty == 0000...)
Everything else is the same string in both files.
"""
import struct
import uuid

ZERO_GUID = '00000000-0000-0000-0000-000000000000'
COLOR_TYPES = ('Color', 'ColorA')


def _f32(x):
    return struct.unpack('f', struct.pack('f', x))[0]


def fmt_float32(x):
    """.NET Framework float.ToString() (== "G": 7 significant digits) for values in [0,1]."""
    if x == 0:
        return '0'
    s = '%.7g' % x
    if 'e' in s:  # not reachable for 1/255..1 but keep .NET-style exponent for safety
        m, e = s.split('e')
        s = '%sE%s%02d' % (m, e[0] if e[0] == '-' else '+', abs(int(e)))
    return s


_BYTE_TO_STR = [fmt_float32(_f32(b / 255.0)) for b in range(256)]
_STR_TO_BYTE = {s: b for b, s in enumerate(_BYTE_TO_STR)}


def color_xblock_to_block(value, n):
    """Return (block_value, exact). exact=False when the floats are not an exact byte/255 value."""
    toks = [t for t in value.replace(',', ' ').split() if t]
    if len(toks) != n:
        return value, False
    out = []
    exact = True
    for t in toks:
        b = _STR_TO_BYTE.get(t)
        if b is None:
            try:
                b = int(round(float(t) * 255.0))
            except ValueError:
                return value, False
            b = max(0, min(255, b))
            exact = False
        out.append(b)
    return ', '.join(str(b) for b in out), exact


def color_block_to_xblock(value, n):
    toks = [t for t in value.replace(',', ' ').split() if t]
    if len(toks) != n:
        return value
    return ', '.join(_BYTE_TO_STR[int(t)] for t in toks)


def entityref_xblock_to_block(guid_n, names_by_id):
    """guid as in xblock (dashed). names_by_id: {dashed_guid: entity name}. Returns (block_value, dangling)."""
    g = guid_n.strip().lower()
    if g == ZERO_GUID:
        return '', False
    name = names_by_id.get(g)
    if name is None:
        return g + '|<unresolved>|', True
    return g + '|' + name + '|', False


def entityref_block_to_xblock(value):
    if not value:
        return ZERO_GUID
    return value.split('|')[0]
