#!/usr/bin/env python3
"""nif2gltf.py: MapleStory 2 NIF (30.2.0.3) + KF -> glTF 2.0 binary (.glb).

  nif2gltf.py in.nif out.glb [--kf a.kf ...] [--fps 30] [--tex DIR] [--no-mesh] [--zup]

- Nodes: the NiNode tree under the scene root, rest pose = NIF local transforms (= skin bind pose).
- One skin over every NiNode below the root; inverseBindMatrices = inverse(world bind).
- Skinned NiMesh (POSITION_BP/NORMAL_BP/TEXCOORD/BLENDINDICES/BLENDWEIGHT/BONE_PALETTE) -> skinned glTF mesh;
  rigid NiMesh (POSITION/NORMAL/TEXCOORD) -> mesh on its parent node.
- Each KF NiSequenceData -> one glTF animation sampled at --fps (B-spline and keyed evaluators).
- Axes: MS2 is Z-up; a wrapper root node rotates -90 deg about X so the glTF is Y-up (skip with --zup).
  Units are kept 1:1 (MS2 units; the female body is ~100 units tall).
- Textures: base map file names are taken from NiTexturingProperty; with --tex DIR the matching .dds is
  converted to PNG (PIL) and embedded.
"""
import argparse
import io
import json
import os
import struct
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from nifxml import Nif, Spec  # noqa: E402
import ms2nif as M  # noqa: E402


class GLB:
    def __init__(self):
        self.g = {'asset': {'version': '2.0', 'generator': 'MS2_decomp tools/nif/nif2gltf.py'},
                  'scenes': [{'nodes': []}], 'scene': 0, 'nodes': [], 'buffers': [], 'bufferViews': [],
                  'accessors': [], 'meshes': [], 'materials': [], 'textures': [], 'images': [],
                  'samplers': [], 'skins': [], 'animations': []}
        self.bin = bytearray()

    def view(self, data, target=None):
        while len(self.bin) % 4:
            self.bin.append(0)
        v = {'buffer': 0, 'byteOffset': len(self.bin), 'byteLength': len(data)}
        if target:
            v['target'] = target
        self.bin += data
        self.g['bufferViews'].append(v)
        return len(self.g['bufferViews']) - 1

    def acc(self, arr, kind, target=None, minmax=False, normalized=False):
        arr = np.ascontiguousarray(arr)
        ct = {np.dtype('float32'): 5126, np.dtype('uint16'): 5123, np.dtype('uint32'): 5125,
              np.dtype('uint8'): 5121}[arr.dtype]
        a = {'bufferView': self.view(arr.tobytes(), target), 'componentType': ct,
             'count': int(arr.shape[0]), 'type': kind}
        if normalized:
            a['normalized'] = True
        if minmax:
            flat = arr.reshape(arr.shape[0], -1)
            a['min'] = [float(x) for x in flat.min(0)]
            a['max'] = [float(x) for x in flat.max(0)]
        self.g['accessors'].append(a)
        return len(self.g['accessors']) - 1

    def node(self, **kw):
        self.g['nodes'].append(kw)
        return len(self.g['nodes']) - 1

    def write(self, path):
        g = {k: v for k, v in self.g.items() if v != []}
        while len(self.bin) % 4:
            self.bin.append(0)
        g['buffers'] = [{'byteLength': len(self.bin)}]
        js = json.dumps(g, separators=(',', ':')).encode()
        while len(js) % 4:
            js += b' '
        out = struct.pack('<III', 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(self.bin))
        out += struct.pack('<II', len(js), 0x4E4F534A) + js
        out += struct.pack('<II', len(self.bin), 0x004E4942) + bytes(self.bin)
        with open(path, 'wb') as f:
            f.write(out)


def trs_of(Mx):
    R = Mx[:3, :3]
    s = float(np.cbrt(np.linalg.det(R))) or 1.0
    q = M.mat_to_quat(R / s)   # w x y z
    return [float(x) for x in Mx[:3, 3]], [float(q[1]), float(q[2]), float(q[3]), float(q[0])], s


def textures_of(nif, mi):
    """Return {'base': name, 'normal': name, 'gloss': name, ...} for a mesh."""
    B = nif.blocks
    out = {}
    for p in B[mi].get('Properties', []):
        if p < 0 or B[p]['_type'] != 'NiTexturingProperty':
            continue
        b = B[p]
        for key, slot in (('base', 'Base Texture'), ('gloss', 'Gloss Texture'), ('normal', 'Normal Texture'),
                          ('glow', 'Glow Texture'), ('dark', 'Dark Texture'), ('detail', 'Detail Texture')):
            if b.get('Has ' + slot) and b[slot]['Source'] >= 0:
                out[key] = nif.str(B[b[slot]['Source']]['File Name']['Index'])
        for st in b.get('Shader Textures', []):
            if st.get('Has Map') and st['Map']['Source'] >= 0:
                out['shader%d' % st['Map ID']] = nif.str(B[st['Map']['Source']]['File Name']['Index'])
    return out


def material_flags(nif, mi):
    B = nif.blocks
    alpha = False
    for p in B[mi].get('Properties', []):
        if p >= 0 and B[p]['_type'] == 'NiAlphaProperty':
            alpha = True
    return alpha


def find_tex(texdir, name):
    if not texdir or not name:
        return None
    base = os.path.basename(name.replace('\\', '/'))
    for cand in (base, base.lower()):
        p = os.path.join(texdir, cand)
        if os.path.exists(p):
            return p
    low = base.lower()
    for f in os.listdir(texdir):
        if f.lower() == low:
            return os.path.join(texdir, f)
    return None


def convert(nif_path, out_path, kfs=(), fps=30.0, texdir=None, no_mesh=False, zup=False, spec=None):
    spec = spec or Spec()
    nif = Nif.load(spec, nif_path)
    sc = M.Scene(nif)
    B = nif.blocks
    root = nif.roots[0]
    glb = GLB()
    g = glb.g
    nmap = {}            # nif block index -> gltf node
    order = []

    def add(i):
        T, R, s = trs_of(sc.local(i))
        nd = glb.node(name=sc.name(i) or ('blk%d' % i), translation=T, rotation=R, scale=[s, s, s])
        nmap[i] = nd
        order.append(i)
        kids = []
        for c in B[i].get('Children', []):
            if c >= 0 and B[c]['_type'] == 'NiNode':
                kids.append(add(c))
        if kids:
            g['nodes'][nd]['children'] = kids
        return nd

    top = add(root)
    if zup:
        g['scenes'][0]['nodes'] = [top]
    else:
        wrap = glb.node(name='MS2_ZupToYup', rotation=[-0.7071067811865476, 0, 0, 0.7071067811865476],
                        children=[top])
        g['scenes'][0]['nodes'] = [wrap]

    joints = [i for i in order if i != root]
    jidx = {b: k for k, b in enumerate(joints)}
    skin = None
    if joints:
        ibm = np.array([np.linalg.inv(sc.world(i)).T.reshape(16) for i in joints], dtype=np.float32)
        g['skins'].append({'name': 'Bip01_skin', 'joints': [nmap[i] for i in joints], 'skeleton': nmap[root],
                           'inverseBindMatrices': glb.acc(ibm, 'MAT4')})
        skin = 0

    img_cache = {}

    def material_for(mi):
        tx = textures_of(nif, mi)
        mat = {'name': nif.name(B[mi]) + '_mat', 'pbrMetallicRoughness': {'metallicFactor': 0.0,
               'roughnessFactor': 0.8}, 'extras': {'ms2_textures': tx}}
        if material_flags(nif, mi):
            mat['alphaMode'] = 'MASK'
        p = find_tex(texdir, tx.get('base'))
        if p:
            if p not in img_cache:
                from PIL import Image
                im = Image.open(p)
                im.load()
                bio = io.BytesIO()
                im.convert('RGBA').save(bio, 'PNG')
                if not g['samplers']:
                    g['samplers'].append({'magFilter': 9729, 'minFilter': 9987})
                g['images'].append({'name': os.path.basename(p), 'mimeType': 'image/png',
                                    'bufferView': glb.view(bio.getvalue())})
                g['textures'].append({'sampler': 0, 'source': len(g['images']) - 1})
                img_cache[p] = len(g['textures']) - 1
            mat['pbrMetallicRoughness']['baseColorTexture'] = {'index': img_cache[p]}
        g['materials'].append(mat)
        return len(g['materials']) - 1

    if not no_mesh:
        for i, b in enumerate(B):
            if b['_type'] != 'NiMesh':
                continue
            if b.get('Flags', 0) & 1:      # APP_CULLED helper geometry (e.g. NPC 'Bone' boxes)
                continue
            assert b['Primitive Type'] == 0, 'only triangle lists'
            s = M.mesh_streams(nif, i)
            si = M.skin_of(nif, i)
            skinned = si >= 0 and 'BLENDINDICES' in s
            pos = s.get('POSITION_BP' if skinned else 'POSITION', s.get('POSITION', s.get('POSITION_BP')))
            nrm = s.get('NORMAL_BP' if skinned else 'NORMAL', s.get('NORMAL', s.get('NORMAL_BP')))
            attrs = {'POSITION': glb.acc(pos.astype(np.float32), 'VEC3', 34962, minmax=True)}
            if nrm is not None:
                n = nrm.astype(np.float32)
                ln = np.linalg.norm(n, axis=1, keepdims=True)
                ln[ln == 0] = 1
                attrs['NORMAL'] = glb.acc((n / ln).astype(np.float32), 'VEC3', 34962)
            if 'TEXCOORD' in s:
                attrs['TEXCOORD_0'] = glb.acc(s['TEXCOORD'].astype(np.float32), 'VEC2', 34962)
            if skinned:
                sk = B[si]
                pal = s.get('BONE_PALETTE', np.arange(len(sk['Bones']), dtype=np.uint16))
                bi = s['BLENDINDICES'].astype(np.int64)
                w3 = s['BLENDWEIGHT'].astype(np.float64)
                w = np.concatenate([w3, np.clip(1.0 - w3.sum(1, keepdims=True), 0, 1)], 1) if w3.shape[1] == 3 \
                    else w3
                bones = np.array([jidx[sk['Bones'][int(p)]] for p in pal], dtype=np.int64)
                J = bones[np.clip(bi, 0, len(bones) - 1)]
                w[bi >= len(bones)] = 0
                w[w < 1e-6] = 0
                J[w == 0] = 0
                w = w / w.sum(1, keepdims=True)
                attrs['JOINTS_0'] = glb.acc(J.astype(np.uint16), 'VEC4', 34962)
                attrs['WEIGHTS_0'] = glb.acc(w.astype(np.float32), 'VEC4', 34962)
                # skin space = skeleton root; check it is the scene root with identity
                assert sk['Skeleton Root'] == root, 'skeleton root is not the scene root'
            idx = s['INDEX'].astype(np.uint32 if s['INDEX'].max() > 65535 else np.uint16)
            prim = {'attributes': attrs, 'indices': glb.acc(idx, 'SCALAR', 34963), 'mode': 4,
                    'material': material_for(i)}
            g['meshes'].append({'name': nif.name(b), 'primitives': [prim]})
            mesh = len(g['meshes']) - 1
            if skinned:
                # a skinned mesh node ignores its own transform in glTF; parent it at the root
                nd = glb.node(name=nif.name(b), mesh=mesh, skin=skin)
                g['nodes'][nmap[root]].setdefault('children', []).append(nd)
            else:
                par = sc.parent.get(i, root)
                T, R, sc_ = trs_of(sc.local(i))
                nd = glb.node(name=nif.name(b), mesh=mesh, translation=T, rotation=R, scale=[sc_] * 3)
                g['nodes'][nmap.get(par, nmap[root])].setdefault('children', []).append(nd)

    byname = {sc.name(i): i for i in joints}
    for kfp in kfs:
        kf = Nif.load(spec, kfp)
        name, dur, chans, tks = M.kf_channels(kf)
        nfr = max(2, int(round(dur * fps)) + 1)
        times = np.linspace(0, dur, nfr).astype(np.float32)
        tacc = glb.acc(times.reshape(-1, 1), 'SCALAR', minmax=True)
        g['accessors'][tacc]['type'] = 'SCALAR'
        anim = {'name': name, 'samplers': [], 'channels': [], 'extras': {'text_keys': tks, 'kf': os.path.basename(kfp)}}
        for node, ch in chans.items():
            if node not in byname:
                continue
            nd = nmap[byname[node]]
            rest_T, rest_R, rest_s = trs_of(sc.local(byname[node]))
            for key, path in (('T', 'translation'), ('R', 'rotation'), ('S', 'scale')):
                if key not in ch:
                    continue
                vals = []
                prev = None
                for t in times:
                    v = np.asarray(ch[key].fn(float(t)), dtype=np.float64)
                    if key == 'R':
                        v = np.array([v[1], v[2], v[3], v[0]])
                        if prev is not None and np.dot(prev, v) < 0:
                            v = -v
                        prev = v
                    elif key == 'S':
                        v = np.repeat(v[:1], 3)
                    vals.append(v)
                arr = np.array(vals, dtype=np.float32)
                kind = 'VEC4' if key == 'R' else 'VEC3'
                anim['samplers'].append({'input': tacc, 'output': glb.acc(arr, kind), 'interpolation': 'LINEAR'})
                anim['channels'].append({'sampler': len(anim['samplers']) - 1, 'target': {'node': nd, 'path': path}})
        g['animations'].append(anim)
    glb.write(out_path)
    return glb


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('nif')
    ap.add_argument('out')
    ap.add_argument('--kf', nargs='*', default=[])
    ap.add_argument('--fps', type=float, default=30.0)
    ap.add_argument('--tex')
    ap.add_argument('--no-mesh', action='store_true')
    ap.add_argument('--zup', action='store_true')
    a = ap.parse_args()
    glb = convert(a.nif, a.out, a.kf, a.fps, a.tex, a.no_mesh, a.zup)
    g = glb.g
    print('%s: %d nodes, %d meshes, %d joints, %d animations, %d bytes bin' % (
        a.out, len(g['nodes']), len(g['meshes']), len(g['skins'][0]['joints']) if g['skins'] else 0,
        len(g['animations']), len(glb.bin)))


if __name__ == '__main__':
    main()
