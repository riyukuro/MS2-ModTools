#!/usr/bin/env python3
"""Check MS2 NIFs against the stock Gamebryo 3.2 NiStream format (NiStream.cpp LoadStream / NiNode::LoadBinary).

    nifcheck.py FILE.nif|DIR... [-v]

Parses the 30.2.0.3 header (metadata, RTTI table, object size table, fixed strings, groups), then re-reads every
NiNode with the stock NiObjectNET/NiAVObject/NiNode::LoadBinary layout and compares the bytes consumed with the
object's streamed size.  Prints class histogram, header versions and the NiNode size delta histogram.
"""
import collections
import os
import struct
import sys


class R:
    def __init__(s, b, o=0):
        s.b, s.o = b, o

    def skip_links(s):
        n = s.u("I")
        s.o += 4 * n

    def u(s, fmt):
        v = struct.unpack_from("<" + fmt, s.b, s.o)
        s.o += struct.calcsize("<" + fmt)
        return v if len(v) > 1 else v[0]


def parse(path):
    return parse_bytes(open(path, "rb").read())


def parse_bytes(b):
    nl = b.index(b"\n")
    r = R(b, nl + 1)
    hdr = b[:nl].decode("latin1")
    ver = r.u("I")
    endian = r.u("B") if ver >= 0x14000003 else 1
    user = r.u("I") if ver >= 0x0A000108 else 0
    nobj = r.u("I")
    if ver >= 0x14090001:                       # metadata block
        msize = r.u("I")
        r.o += msize
    nrtti = r.u("H")
    rtti = []
    for _ in range(nrtti):
        n = r.u("I")
        rtti.append(b[r.o:r.o + n].decode("latin1"))
        r.o += n
    types = [r.u("H") for _ in range(nobj)]
    sizes = [r.u("I") for _ in range(nobj)] if ver >= 0x14020005 else None
    str_off = r.o
    nstr, _maxlen = r.u("I"), r.u("I")
    strings = []
    for _ in range(nstr):
        n = r.u("I")
        strings.append(b[r.o:r.o + n].decode("latin1", "replace"))
        r.o += n
    str_end = r.o
    ngroups = r.u("I")
    r.o += 4 * ngroups
    objs = []
    for t, sz in zip(types, sizes):
        objs.append((rtti[t & 0x7FFF], bool(t & 0x8000), r.o, sz))
        r.o += sz
    return dict(hdr=hdr, ver=ver, endian=endian, user=user, objs=objs, strings=strings, str_off=str_off, str_end=str_end, b=b, end=r.o, total=len(b))


def rename_strings(b, mapping):
    """Return the NIF bytes with fixed strings renamed (e.g. MS2StandardMaterial -> NiStandardMaterial).  Objects
    reference fixed strings by index, so only the string table changes; object sizes stay valid."""
    n = parse_bytes(b)
    strings = [mapping.get(x, x) for x in n["strings"]]
    if strings == n["strings"]:
        return b
    enc = [x.encode("latin1") for x in strings]
    table = struct.pack("<II", len(enc), max([len(x) for x in enc] + [0]))
    table += b"".join(struct.pack("<I", len(x)) + x for x in enc)
    return b[:n["str_off"]] + table + b[n["str_end"]:]


def stock_ninode_size(b, off):
    r = R(b, off)
    r.u("I")                                    # name (fixed string index)
    r.skip_links()                     # extra data links
    r.u("I")                                    # controller link
    r.u("H")                                    # flags (unsigned short)
    r.o += 12 + 36 + 4                          # translate, rotate, scale
    r.skip_links()                     # properties
    r.u("I")                                    # collision object
    r.skip_links()                     # children
    r.skip_links()                     # effects
    return r.o - off


def main():
    files = []
    verbose = "-v" in sys.argv
    for a in sys.argv[1:]:
        if a == "-v":
            continue
        if os.path.isdir(a):
            for d, _, fs in os.walk(a):
                files += [os.path.join(d, f) for f in fs if f.lower().endswith(".nif")]
        else:
            files.append(a)
    hdrs, classes, deltas, ends, bad = (collections.Counter(), collections.Counter(), collections.Counter(),
                                        collections.Counter(), [])
    for f in sorted(files):
        try:
            n = parse(f)
        except Exception as e:                  # noqa: BLE001
            bad.append((f, repr(e)))
            continue
        hdrs[(n["hdr"], hex(n["ver"]), n["endian"], n["user"])] += 1
        ends[n["total"] - n["end"] == 8] += 1
        for cls, skip, off, sz in n["objs"]:
            classes[cls] += 1
            if cls == "NiNode":
                try:
                    d = sz - stock_ninode_size(n["b"], off)
                except struct.error:
                    d = "overrun"
                deltas[d] += 1
                if verbose and d != 0:
                    print(f, off, sz, d, n["b"][off:off + sz].hex())
    print(f"{len(files)} files; object table ends 8 bytes before EOF (1 top-level object): {dict(ends)}")
    print("headers (line, version, endian, user version):")
    for k, v in hdrs.most_common():
        print(f"  {v:6} {k}")
    print("NiNode streamed size - stock NiNode::LoadBinary size:", dict(deltas))
    print("classes:", ", ".join(f"{c}={n}" for c, n in classes.most_common()))
    for f, e in bad[:10]:
        print("PARSE FAIL", f, e)


if __name__ == "__main__":
    main()
