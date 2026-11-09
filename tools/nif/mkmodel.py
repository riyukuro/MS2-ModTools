#!/usr/bin/env python3
"""mkmodel.py: an NPC model folder (kfm + nif + kf + per-model xml + anikeytext row) for MapleStory 2.

  mkmodel.py --name 23009901_maid --nif our.nif --ref-dir <shipped npc model folder> --kf-dir <kf folder>
             [--map Seq=file.kf ...] [--key Seq:name=time ...] [--root "Scene Root"]
             [--anikeytext <client anikeytext.xml>] [--tex a.dds ...] -o OUTDIR

The reference folder (a shipped NPC: <ref>.kfm, <ref>.xml, *.kf) gives the sequence list, the sequence ids (KFM
event codes) and the transition table; the new model plays other kf files under those names: --map Seq=file.kf
(file in --kf-dir), and every reference sequence without a --map uses its own file name from --kf-dir if there is
one (e.g. the female idle_a.kf for Idle_A), else --default-kf.  Each kf is copied with its NiSequenceData renamed
to the sequence name.

Written to OUTDIR:
  <name>.kfm    binary KFM 30.2.0.3b: nif ".\\<name>.nif", root --root, the reference's sequences and transitions
  <name>.nif    copy of --nif
  <name>.xml    per-model text keys (the reference's, with key times clamped to the new durations)
  <Seq>.kf      one per sequence
  anikeytext.xml  the <kfm name="<name lower>"> row for Xml.m2d anikeytext.xml (ids from the reference row; "end" =
                the new kf's duration).  tools/dungeon/boss.py merges it into the client and server Xml.m2d.
                The server fires an NPC skill's attacks at the keys named by its <attack point="p0"> (and moves the
                caster between "move0" and "move1"); a reference row's keys belong to the reference's animation, so
                a sequence remapped to another kf usually needs --key Seq:p0=<hit time> (--key Seq:p1= deletes p1).
  *.dds         --tex files, copied (boss.py packs them into Textures.m2d)
"""
import argparse
import os
import re
import shutil
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from nifxml import Nif, Spec  # noqa: E402

KFM_MAGIC = b';Gamebryo KFM File Version 30.2.0.3b\n'


class KFM:
    """KFM 30.2.0.3b (binary).  Transitions are kept as raw bytes (dest id + type + type-specific data)."""

    def __init__(self, data):
        if not data.startswith(KFM_MAGIC):
            raise SystemExit('not a 30.2.0.3b KFM')
        self.d, self.o = data, len(KFM_MAGIC)
        self.endian = self._b()
        self.nif = self._s()
        self.root = self._s()
        self.defaults = self.d[self.o:self.o + 16]
        self.o += 16
        self.anims = []
        for _ in range(self._u()):
            eid, path, name = self._u(), self._s(), self._s()
            nt = self._u()
            t0 = self.o
            for _ in range(nt):
                self._u()
                if self._u() != 5:
                    self.o += 4  # duration
                    for _ in range(self._u()):
                        self._u()
                        self._s()
                    nc = self._u()  # (self.o += 8 * self._u() would drop _u's own advance)
                    self.o += 8 * nc
            self.anims.append({'id': eid, 'kf': path, 'name': name, 'nt': nt, 'trans': self.d[t0:self.o]})
        self.tail = self.d[self.o:]
        if self.tail != b'\0\0\0\0':
            raise SystemExit('KFM: unexpected trailer %r' % self.tail[:16])

    def _b(self):
        self.o += 1
        return self.d[self.o - 1]

    def _u(self):
        self.o += 4
        return struct.unpack_from('<I', self.d, self.o - 4)[0]

    def _s(self):
        n = self._u()
        self.o += n
        return self.d[self.o - n:self.o].decode('latin-1')

    def tobytes(self):
        S = lambda s: struct.pack('<I', len(s)) + s.encode('latin-1')  # noqa: E731
        out = [KFM_MAGIC, bytes([self.endian]), S(self.nif), S(self.root), self.defaults,
               struct.pack('<I', len(self.anims))]
        for a in self.anims:
            out += [struct.pack('<I', a['id']), S(a['kf']), S(a['name']), struct.pack('<I', a['nt']), a['trans']]
        return b''.join(out) + self.tail


def kf_copy(spec, src, dst, seq):
    """Copy a kf with its sequence renamed; returns the duration."""
    n = Nif.load(spec, src)
    sd = [b for b in n.blocks if b['_type'] == 'NiSequenceData']
    if len(sd) != 1:
        raise SystemExit('%s: %d NiSequenceData blocks' % (src, len(sd)))
    sd[0]['Name'] = n.addstr(seq) if not isinstance(sd[0]['Name'], dict) else {'Index': n.addstr(seq)}
    n.save(dst)
    return float(sd[0]['Duration'])


def ani_row(text, name):
    m = re.search(r'[^\S\n]*<kfm name="%s">.*?</kfm>[^\S\n]*\r?\n' % re.escape(name), text, re.S | re.I)
    return m.group(0) if m else None


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--name', required=True, help='model name (the npc <model kfm=...>)')
    ap.add_argument('--nif', required=True)
    ap.add_argument('--ref-dir', required=True)
    ap.add_argument('--kf-dir', required=True)
    ap.add_argument('--map', action='append', default=[], metavar='SEQ=FILE.kf')
    ap.add_argument('--key', action='append', default=[], metavar='SEQ:NAME=TIME',
                    help='set (or with an empty TIME delete) an anikeytext key, e.g. Attack_03_A:p0=0.55')
    ap.add_argument('--default-kf', default='idle_a.kf')
    ap.add_argument('--root', default='Scene Root', help='KFM root node name (f_body: "Scene Root")')
    ap.add_argument('--anikeytext', help='client anikeytext.xml holding the reference row')
    ap.add_argument('--tex', action='append', default=[])
    ap.add_argument('-o', '--out', required=True)
    a = ap.parse_args()
    spec = Spec()
    ref = [f for f in os.listdir(a.ref_dir) if f.lower().endswith('.kfm')]
    if len(ref) != 1:
        raise SystemExit('%s: want one .kfm, found %s' % (a.ref_dir, ref))
    ref_stem = ref[0][:-4]
    kfm = KFM(open(os.path.join(a.ref_dir, ref[0]), 'rb').read())
    kfs = {f.lower(): f for f in os.listdir(a.kf_dir) if f.lower().endswith('.kf')}
    keys = {}
    for k in a.key:
        m = re.match(r'([^:]+):([^=]+)=(.*)$', k)
        if not m:
            raise SystemExit('--key %s: want SEQ:NAME=TIME' % k)
        keys.setdefault(m.group(1).lower(), []).append((m.group(2), m.group(3).strip()))
    if keys and not a.anikeytext:
        raise SystemExit('--key needs --anikeytext')
    mp = {}
    for m in a.map:
        s, f = m.split('=', 1)
        mp[s.lower()] = f
    os.makedirs(a.out, exist_ok=True)
    shutil.copyfile(a.nif, os.path.join(a.out, a.name + '.nif'))
    kfm.nif = '.\\%s.nif' % a.name
    kfm.root = a.root
    dur = {}
    for an in kfm.anims:
        seq = an['name']
        src = mp.get(seq.lower()) or kfs.get(seq.lower() + '.kf') or a.default_kf
        src = kfs.get(src.lower(), src)
        dur[seq] = kf_copy(spec, os.path.join(a.kf_dir, src), os.path.join(a.out, seq + '.kf'), seq)
        an['kf'] = '.\\%s.kf' % seq
        print('%-16s id %3d  <- %-36s %.3f s' % (seq, an['id'], src, dur[seq]))
    open(os.path.join(a.out, a.name + '.kfm'), 'wb').write(kfm.tobytes())

    def clamp(m, seq):
        t = float(m.group(2))
        return '%s%.4f%s' % (m.group(1), min(t, dur.get(seq, t)), m.group(3))
    # per-model xml: the reference's, times clamped
    rx = os.path.join(a.ref_dir, ref_stem + '.xml')
    if os.path.exists(rx):
        t = open(rx, encoding='utf-8-sig').read()
        out = []
        for blk in re.split(r'(<[A-Za-z0-9_]+>.*?</[A-Za-z0-9_]+>)', t, flags=re.S):
            m = re.match(r'<([A-Za-z0-9_]+)>', blk)
            if m and m.group(1) in dur:
                blk = re.sub(r'(time=")([0-9.]+)(")', lambda q, s=m.group(1): clamp(q, s), blk)
            out.append(blk)
        open(os.path.join(a.out, a.name + '.xml'), 'w', encoding='utf-8-sig').write(''.join(out))
    # anikeytext row
    if a.anikeytext:
        row = ani_row(open(a.anikeytext, encoding='utf-8-sig').read(), ref_stem)
        if row is None:
            raise SystemExit('%s: no <kfm name="%s">' % (a.anikeytext, ref_stem))
        row = re.sub(r'<kfm name="[^"]*">', '<kfm name="%s">' % a.name.lower(), row, count=1)
        parts = re.split(r'(<seq [^>]*>.*?</seq>)', row, flags=re.S)
        for i, p in enumerate(parts):
            m = re.match(r'<seq id="\d+" name="([^"]+)">', p)
            if not m or m.group(1) not in dur:
                continue
            s = m.group(1)
            p = re.sub(r'(<key name="end" time=")([0-9.]+)(")', lambda q: '%s%.6f%s' % (q.group(1), dur[s], q.group(3)), p)
            p = re.sub(r'(<key name="(?!end")[^"]*" time=")([0-9.]+)(")',
                       lambda q, s=s: '%s%.6f%s' % (q.group(1), min(float(q.group(2)), dur[s]), q.group(3)), p)
            for name, tv in keys.pop(s.lower(), []):
                kre = r'[^\S\n]*<key name="%s" time="[^"]*" />[^\S\n]*\r?\n?' % re.escape(name)
                p = re.sub(kre, '', p)
                if tv:
                    if float(tv) > dur[s]:
                        raise SystemExit('--key %s:%s=%s: past the end of %s (%.3f s)' % (s, name, tv, s, dur[s]))
                    ind = re.search(r'\n([ \t]*)<key ', p)
                    p = re.sub(r'([ \t]*)</seq>', lambda q: '%s<key name="%s" time="%.6f" />\n%s</seq>' % (
                        ind.group(1) if ind else '\t\t\t', name, float(tv), q.group(1)), p, count=1)
            parts[i] = p
        if keys:
            raise SystemExit('--key: no sequence %s in the model' % ', '.join(sorted(keys)))
        open(os.path.join(a.out, 'anikeytext.xml'), 'w', encoding='utf-8').write(''.join(parts))
    for t in a.tex:
        shutil.copyfile(t, os.path.join(a.out, os.path.basename(t)))
    print('wrote', a.out)


if __name__ == '__main__':
    main()
