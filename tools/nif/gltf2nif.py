#!/usr/bin/env python3
"""gltf2nif.py: glTF 2.0 (.glb) skinned meshes -> MapleStory 2 NIF 30.2.0.3, spliced into a shipped template nif.

  gltf2nif.py in.glb template.nif out.nif [--mesh-template PA_Panty] [--tex-out DIR] [--tex-prefix NAME]
              [--keep-tex-names] [--scale 1.0] [--compare ref.nif]

The template supplies the skeleton (every NiNode, its controllers and extra data), the materials and the block
layout.  Every template NiMesh is removed; each glTF primitive becomes a clone of one template mesh "cluster"
(NiMesh, its NiDataStreams, NiSkinningMeshModifier, NiTexturingProperty + NiSourceTextures; the other properties
and extra data are shared): the template mesh with the same name as the glTF mesh, else --mesh-template.

Geometry: glTF joints are matched to template nodes by name.  Each vertex is moved into the template's bind space by
linear blend skinning with the template rest pose: v_nif = sum_j w_j * World_nif(j) * IBM_gltf(j) * v.  So any glTF
axis convention, unit scale or armature object transform drops out, and a mesh bound in another bind pose is
re-posed onto the template's (identical when the rig matches).  Unskinned glTF meshes are bound rigidly to their
nearest named ancestor joint.

Streams written per the template stream layout: INDEX (UINT16), TEXCOORD, POSITION_BP, NORMAL_BP, BINORMAL_BP (dP/dv),
TANGENT_BP (dP/du), BLENDINDICES (UINT8x4, into the palette), BLENDWEIGHT (3 floats, the 4th is 1 - sum),
BONE_PALETTE (identity into the modifier's bone list).  At most 4 influences per vertex (the largest 4, renormalized).

Textures: --tex-out writes the glTF base color image as an uncompressed 32-bit DDS (<prefix>_<n>_d.dds) and a flat
normal map, and points the cloned NiSourceTextures at them.  --keep-tex-names keeps the names nif2gltf stored in the
material extras (round-trip of a shipped model).  Options for --tex-out:
  --dds dxt5|dxt1   block-compressed DDS (full mip chain) instead of 32-bit; dxt5 keeps alpha
  --normal-tex      a mesh whose template has a Normal slot gets the material's glTF normalTexture
                    (<prefix>_<n>_n.dds; --normal-flip-g for OpenGL-convention maps) instead of the flat map
  --gloss-from-roughness S   the Gloss slot gets (1 - roughness) * S as a grey map (<prefix>_<n>_s.dds), from the
                    metallicRoughness texture's G channel (or roughnessFactor), instead of the base colour
"""
import argparse
import copy
import io
import json
import os
import struct
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from nifxml import Nif, Spec  # noqa: E402
import ms2nif as M  # noqa: E402


# ---------------- glTF reading ----------------

CT = {5120: 'i1', 5121: 'u1', 5122: 'i2', 5123: 'u2', 5125: 'u4', 5126: 'f4'}
NC = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'MAT4': 16}


class GLTF:
    def __init__(self, path):
        d = open(path, 'rb').read()
        if d[:4] == b'glTF':
            n, = struct.unpack_from('<I', d, 12)
            self.g = json.loads(d[20:20 + n])
            o = 20 + n
            self.bin = d[o + 8:o + 8 + struct.unpack_from('<I', d, o)[0]] if o < len(d) else b''
            self.dir = os.path.dirname(path)
        else:
            self.g = json.loads(d)
            self.dir = os.path.dirname(path)
            self.bin = None
        self.buffers = []
        for b in self.g.get('buffers', []):
            if 'uri' in b:
                self.buffers.append(open(os.path.join(self.dir, b['uri']), 'rb').read())
            else:
                self.buffers.append(self.bin)

    def view_bytes(self, vi):
        v = self.g['bufferViews'][vi]
        b = self.buffers[v['buffer']]
        o = v.get('byteOffset', 0)
        return b[o:o + v['byteLength']], v.get('byteStride')

    def acc(self, ai):
        a = self.g['accessors'][ai]
        dt = np.dtype('<' + CT[a['componentType']])
        nc = NC[a['type']]
        cnt = a['count']
        if 'bufferView' in a:
            raw, stride = self.view_bytes(a['bufferView'])
            off = a.get('byteOffset', 0)
            es = dt.itemsize * nc
            if stride and stride != es:
                arr = np.stack([np.frombuffer(raw, dt, nc, off + i * stride) for i in range(cnt)])
            else:
                arr = np.frombuffer(raw, dt, cnt * nc, off).reshape(cnt, nc)
        else:
            arr = np.zeros((cnt, nc), dt)
        arr = np.array(arr)
        if 'sparse' in a:
            sp = a['sparse']
            ir, _ = self.view_bytes(sp['indices']['bufferView'])
            idt = np.dtype('<' + CT[sp['indices']['componentType']])
            idx = np.frombuffer(ir, idt, sp['count'], sp['indices'].get('byteOffset', 0))
            vr, _ = self.view_bytes(sp['values']['bufferView'])
            vals = np.frombuffer(vr, dt, sp['count'] * nc, sp['values'].get('byteOffset', 0)).reshape(-1, nc)
            arr[idx] = vals
        if a.get('normalized') and dt.kind in 'iu':
            arr = arr.astype(np.float64) / float(np.iinfo(dt).max)
        return arr

    def image_bytes(self, ii):
        im = self.g['images'][ii]
        if 'bufferView' in im:
            return self.view_bytes(im['bufferView'])[0]
        return open(os.path.join(self.dir, im['uri']), 'rb').read()

    def local(self, ni):
        n = self.g['nodes'][ni]
        if 'matrix' in n:
            return np.array(n['matrix'], dtype=np.float64).reshape(4, 4).T
        t = n.get('translation', [0, 0, 0])
        x, y, z, w = n.get('rotation', [0, 0, 0, 1])
        s = n.get('scale', [1, 1, 1])
        m = np.eye(4)
        m[:3, :3] = M.quat_to_mat(w, x, y, z) @ np.diag(s)
        m[:3, 3] = t
        return m

    def worlds(self):
        par = {}
        for i, n in enumerate(self.g['nodes']):
            for c in n.get('children', []):
                par[c] = i
        W = {}

        def w(i):
            if i not in W:
                W[i] = (w(par[i]) if i in par else np.eye(4)) @ self.local(i)
            return W[i]
        for i in range(len(self.g['nodes'])):
            w(i)
        return W, par


# ---------------- DDS writing ----------------

DDS_FMT = 'argb'   # set by main(): argb (A8R8G8B8), dxt1 or dxt5


def _dxt_level(img, fmt):
    """One block-compressed mip level (Pillow's BC1/BC3 encoder); levels under 4x4 are padded to one block."""
    from PIL import Image
    if img.size[0] < 4 or img.size[1] < 4:
        pad = Image.new('RGBA', (max(4, img.size[0]), max(4, img.size[1])))
        pad.paste(img, (0, 0))
        img = pad
    bio = io.BytesIO()
    img.save(bio, 'DDS', pixel_format=fmt.upper())
    return bio.getvalue()[128:]


def write_dds(path, img, fmt=None):
    """DDS with a full mip chain (PIL image in, power-of-two size): A8R8G8B8, or DXT1/DXT5 (fmt or DDS_FMT)."""
    from PIL import Image
    fmt = fmt or DDS_FMT
    img = img.convert('RGBA')
    w, h = img.size
    p2 = lambda v: 1 << max(0, (v - 1).bit_length())  # noqa: E731
    if (w, h) != (p2(w), p2(h)):
        img = img.resize((p2(w), p2(h)), Image.LANCZOS)
        w, h = img.size
    if fmt in ('dxt1', 'dxt5'):
        mips, cur = [], img
        while True:
            mips.append(_dxt_level(cur, fmt))
            if cur.size == (1, 1):
                break
            cur = cur.resize((max(1, cur.size[0] // 2), max(1, cur.size[1] // 2)), Image.LANCZOS)
        lin = max(1, (w + 3) // 4) * max(1, (h + 3) // 4) * (8 if fmt == 'dxt1' else 16)
        hdr = struct.pack('<4sIIIIIII', b'DDS ', 124, 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000, h, w, lin, 0,
                          len(mips))
        hdr += b'\0' * 44
        hdr += struct.pack('<II4sIIIII', 32, 0x4, fmt.upper().encode(), 0, 0, 0, 0, 0)
        hdr += struct.pack('<IIIII', 0x1000 | 0x400000 | 0x8, 0, 0, 0, 0)
        with open(path, 'wb') as f:
            f.write(hdr + b''.join(mips))
        return
    mips = []
    cur = img
    while True:
        r, g, b, a = cur.split()
        mips.append(Image.merge('RGBA', (b, g, r, a)).tobytes())
        if cur.size == (1, 1):
            break
        cur = cur.resize((max(1, cur.size[0] // 2), max(1, cur.size[1] // 2)), Image.LANCZOS)
    hdr = struct.pack('<4sIIIIIII', b'DDS ', 124, 0x1 | 0x2 | 0x4 | 0x8 | 0x1000 | 0x20000, h, w, w * 4, 0,
                      len(mips))
    hdr += b'\0' * 44
    hdr += struct.pack('<IIIIIIII', 32, 0x41, 0, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000)
    hdr += struct.pack('<IIIII', 0x1000 | 0x400000 | 0x8, 0, 0, 0, 0)
    with open(path, 'wb') as f:
        f.write(hdr + b''.join(mips))


# ---------------- geometry ----------------

def tangents(P, N, UV, I):
    T = np.zeros_like(P)
    Bt = np.zeros_like(P)
    tri = I.reshape(-1, 3)
    p0, p1, p2 = P[tri[:, 0]], P[tri[:, 1]], P[tri[:, 2]]
    u0, u1, u2 = UV[tri[:, 0]], UV[tri[:, 1]], UV[tri[:, 2]]
    e1, e2 = p1 - p0, p2 - p0
    d1, d2 = u1 - u0, u2 - u0
    r = d1[:, 0] * d2[:, 1] - d2[:, 0] * d1[:, 1]
    ok = np.abs(r) > 1e-12
    r[~ok] = 1
    t = (e1 * d2[:, 1:2] - e2 * d1[:, 1:2]) / r[:, None]
    b = (e2 * d1[:, 0:1] - e1 * d2[:, 0:1]) / r[:, None]
    t[~ok] = 0
    b[~ok] = 0
    for k in range(3):
        np.add.at(T, tri[:, k], t)
        np.add.at(Bt, tri[:, k], b)

    def ortho(X, fallback):
        X = X - N * (N * X).sum(1, keepdims=True)
        ln = np.linalg.norm(X, axis=1, keepdims=True)
        bad = ln[:, 0] < 1e-9
        X[bad] = fallback[bad]
        ln[bad] = 1
        return X / ln
    # fallback frame for vertices without UV area
    a = np.where(np.abs(N[:, :1]) < 0.9, np.array([[1.0, 0, 0]]), np.array([[0, 1.0, 0]]))
    fb_t = np.cross(N, a)
    fb_t /= np.linalg.norm(fb_t, axis=1, keepdims=True) + 1e-30
    fb_b = np.cross(N, fb_t)
    return ortho(T, fb_t), ortho(Bt, fb_b)


def sphere(P):
    if len(P) == 0:
        return np.zeros(3), 0.0
    c = (P.min(0) + P.max(0)) / 2
    return c, float(np.linalg.norm(P - c, axis=1).max())


def top4(J, W):
    """J, W: (n, k) -> (n, 4) largest 4 weights, renormalized; duplicate joints merged."""
    n = J.shape[0]
    outJ = np.zeros((n, 4), np.int64)
    outW = np.zeros((n, 4))
    for v in range(n):
        acc = {}
        for j, w in zip(J[v], W[v]):
            if w > 0:
                acc[int(j)] = acc.get(int(j), 0.0) + float(w)
        items = sorted(acc.items(), key=lambda kv: -kv[1])[:4]
        if not items:
            items = [(0, 1.0)]
        s = sum(w for _, w in items)
        for k, (j, w) in enumerate(items):
            outJ[v, k] = j
            outW[v, k] = w / s
    return outJ, outW


# ---------------- NIF assembly ----------------

def cluster_of(nif, mi):
    """Blocks to clone for one template mesh: mesh, streams, modifiers, texturing property + its sources."""
    B = nif.blocks
    m = B[mi]
    own = [mi] + [d['Stream'] for d in m['Datastreams']] + [r for r in m['Modifiers'] if r >= 0]
    for p in m['Properties']:
        if p >= 0 and B[p]['_type'] == 'NiTexturingProperty':
            own.append(p)
            own += [s for s in nif.refs(B[p], ('Ref',)) if B[s]['_type'] == 'NiSourceTexture']
    return own


def clone_cluster(nif, mi):
    own = cluster_of(nif, mi)
    base = len(nif.blocks)
    mapping = {o: base + k for k, o in enumerate(own)}
    for o in own:
        nif.blocks.append(copy.deepcopy(nif.blocks[o]))
    from nifxml import RefWalker
    for k in range(len(own)):
        blk = nif.blocks[base + k]
        RefWalker(nif.spec, lambda kind, v: mapping.get(v, v) if kind == 'Ref' else v).struct(
            blk['_type'], None, {}, blk, blk['_type'])
    return mapping[mi]


def stream_payload(nif, ds, sems, arrays, count):
    """Build an interleaved stream payload following the template stream's component formats."""
    fields = []
    for f, (nm, idx) in zip(ds['Component Formats'], sems):
        dt, n = M.comp_dtype(f)
        key = nm if idx == 0 else '%s%d' % (nm, idx)
        fields.append((key, dt, (n,) if n > 1 else ()))
    rec = np.zeros(count, np.dtype(fields))
    for key, dt, shp in fields:
        if key not in arrays:
            raise SystemExit('template stream wants %s, which this mesh does not provide' % key)
        a = arrays[key]
        if shp:
            a = a[:, :shp[0]]
        rec[key] = a.astype(dt)
    data = rec.tobytes()
    ds['Data']['Data'] = data
    ds['Num Bytes'] = len(data)
    ds['Regions'] = [{'Start Index': 0, 'Num Indices': count}]
    ds['Num Regions'] = 1


def build(glb_path, tmpl_path, out_path, mesh_template='PA_Panty', tex_out=None, tex_prefix=None,
          keep_tex_names=False, spec=None, log=print, normal_tex=False, normal_flip_g=False, gloss_scale=None):
    spec = spec or Spec()
    gl = GLTF(glb_path)
    g = gl.g
    nif = Nif.load(spec, tmpl_path)
    B = nif.blocks
    sc = M.Scene(nif)
    root = nif.roots[0]
    names = sc.node_by_name()
    Wn = {i: sc.world(i) for i in names.values()}
    Wg, gpar = gl.worlds()
    tmpl_meshes = {nif.name(b): i for i, b in enumerate(B) if b['_type'] == 'NiMesh' and M.skin_of(nif, i) >= 0}
    if mesh_template not in tmpl_meshes:
        mesh_template = next(iter(tmpl_meshes))
    tex_prefix = tex_prefix or os.path.splitext(os.path.basename(out_path))[0]
    if tex_out:
        os.makedirs(tex_out, exist_ok=True)
    flat_n = None

    # glTF Z-up wrapper fallback for unnamed hierarchies: Y-up -> Z-up
    yup2zup = np.eye(4)
    yup2zup[:3, :3] = [[1, 0, 0], [0, 0, -1], [0, 1, 0]]

    work = []   # (name, P, N, UV, I, J(node idx), W, material index)
    for ni, node in enumerate(g['nodes']):
        if 'mesh' not in node:
            continue
        gm = g['meshes'][node['mesh']]
        skin = g['skins'][node['skin']] if 'skin' in node else None
        if skin is not None:
            jn = [g['nodes'][j].get('name') for j in skin['joints']]
            miss = [n for n in jn if n not in names]
            ibm = gl.acc(skin['inverseBindMatrices']).reshape(-1, 4, 4).transpose(0, 2, 1) \
                if 'inverseBindMatrices' in skin else np.array([np.eye(4)] * len(jn))
            # joints without a template node: fold their weight onto the nearest named ancestor
            jmap = []
            for k, j in enumerate(skin['joints']):
                a = j
                while g['nodes'][a].get('name') not in names and a in gpar:
                    a = gpar[a]
                nm = g['nodes'][a].get('name')
                if nm not in names:
                    raise SystemExit('joint %s has no template node (nor any ancestor)' % jn[k])
                jmap.append(names[nm])
            if miss:
                log('note: %d glTF joints not in the template, weights folded to ancestors: %s' % (
                    len(miss), ', '.join(miss[:12])))
            Xj = [Wn[jmap[k]] @ ibm[k] for k in range(len(jn))]
        else:
            a = ni
            while g['nodes'][a].get('name') not in names and a in gpar:
                a = gpar[a]
            nm = g['nodes'][a].get('name')
            if nm in names:
                Xr = Wn[names[nm]] @ np.linalg.inv(Wg[a]) @ Wg[ni]
                rid = names[nm]
            else:
                Xr = yup2zup @ Wg[ni]
                rid = next(i for i in names.values() if i != root)
        for pi, prim in enumerate(gm['primitives']):
            if prim.get('mode', 4) != 4:
                raise SystemExit('mesh %s: only triangle lists' % gm.get('name'))
            at = prim['attributes']
            P = gl.acc(at['POSITION']).astype(np.float64)
            nv = len(P)
            Nn = gl.acc(at['NORMAL']).astype(np.float64) if 'NORMAL' in at else None
            UV = gl.acc(at['TEXCOORD_0']).astype(np.float64) if 'TEXCOORD_0' in at else np.zeros((nv, 2))
            I = gl.acc(prim['indices']).reshape(-1).astype(np.int64) if 'indices' in prim else np.arange(nv)
            if skin is not None:
                Js, Ws = [], []
                k = 0
                while 'JOINTS_%d' % k in at:
                    Js.append(gl.acc(at['JOINTS_%d' % k]).astype(np.int64))
                    Ws.append(gl.acc(at['WEIGHTS_%d' % k]).astype(np.float64))
                    k += 1
                J, Wt = top4(np.concatenate(Js, 1), np.concatenate(Ws, 1))
                # bind-space transform per vertex: weighted sum of joint transforms
                X = np.zeros((nv, 4, 4))
                for c in range(4):
                    X += Wt[:, c, None, None] * np.array(Xj)[J[:, c]]
                Pn = np.einsum('vij,vj->vi', X[:, :3, :3], P) + X[:, :3, 3]
                if Nn is not None:
                    Nn = np.einsum('vij,vj->vi', np.linalg.inv(X[:, :3, :3]).transpose(0, 2, 1), Nn)
                Jn = np.array(jmap)[J]
            else:
                Pn = P @ Xr[:3, :3].T + Xr[:3, 3]
                if Nn is not None:
                    Nn = Nn @ np.linalg.inv(Xr[:3, :3])
                Jn = np.full((nv, 4), rid)
                Wt = np.zeros((nv, 4))
                Wt[:, 0] = 1
            if Nn is None:
                Nn = np.zeros_like(Pn)
                tri = I.reshape(-1, 3)
                fn = np.cross(Pn[tri[:, 1]] - Pn[tri[:, 0]], Pn[tri[:, 2]] - Pn[tri[:, 0]])
                for c in range(3):
                    np.add.at(Nn, tri[:, c], fn)
            Nn /= np.linalg.norm(Nn, axis=1, keepdims=True) + 1e-30
            if nv > 65535:
                raise SystemExit('mesh %s: %d vertices, UINT16 indices allow 65535; split it' % (gm.get('name'), nv))
            nm = gm.get('name') or node.get('name') or 'Mesh%d' % ni
            if len(gm['primitives']) > 1:
                nm = '%s_%d' % (nm, pi)
            work.append((nm, Pn, Nn, UV, I, Jn, Wt, prim.get('material')))

    # remove template meshes from the graph
    for b in B:
        if 'Children' in b:
            keep = [c for c in b['Children'] if c < 0 or B[c]['_type'] != 'NiMesh']
            b['Children'] = keep
            b['Num Children'] = len(keep)
    new_meshes = []
    for wi, (nm, P, N, UV, I, Jn, Wt, mat) in enumerate(work):
        src = tmpl_meshes.get(nm, tmpl_meshes[mesh_template])
        mi = clone_cluster(nif, src)
        m = nif.blocks[mi]
        m['Name'] = {'Index': nif.addstr(nm)}
        for k in ('Translation',):
            m[k] = {'x': 0.0, 'y': 0.0, 'z': 0.0}
        m['Rotation'] = M.mat3_dict(np.eye(3))
        m['Scale'] = 1.0
        # palette: bones actually used, in skeleton order
        used = sorted(set(int(x) for x, w in zip(Jn.reshape(-1), Wt.reshape(-1)) if w > 0),
                      key=lambda i: list(names.values()).index(i))
        pal = {b: k for k, b in enumerate(used)}
        bi = np.vectorize(lambda x: pal.get(int(x), 0))(Jn).astype(np.uint8)
        bi[Wt <= 0] = 0
        if len(used) > 255:
            raise SystemExit('mesh %s uses %d bones' % (nm, len(used)))
        T, Bn = tangents(P, N, UV, I)
        arrays = {'INDEX': I.astype(np.uint16), 'TEXCOORD': UV, 'POSITION_BP': P, 'NORMAL_BP': N,
                  'BINORMAL_BP': Bn, 'TANGENT_BP': T, 'BLENDINDICES': bi, 'BLENDWEIGHT': Wt[:, :3],
                  'BONE_PALETTE': np.arange(len(used), dtype=np.uint16), 'POSITION': P, 'NORMAL': N,
                  'BINORMAL': Bn, 'TANGENT': T}
        for ref in m['Datastreams']:
            ds = nif.blocks[ref['Stream']]
            sems = [(nif.str(c['Name']), c['Index']) for c in ref['Component Semantics']]
            first = sems[0][0]
            cnt = len(I) if first == 'INDEX' else (len(used) if first == 'BONE_PALETTE' else len(P))
            stream_payload(nif, ds, sems, arrays, cnt)
        sk = nif.blocks[next(r for r in m['Modifiers'] if r >= 0 and nif.blocks[r]['_type'] == 'NiSkinningMeshModifier')]
        sk['Skeleton Root'] = root
        # the template's value is the inverse of the template mesh's own transform; the clone's is identity
        sk['Skeleton Transform'] = {'Rotation': M.mat3_dict(np.eye(3)), 'Translation': M.vec3_dict(np.zeros(3)),
                                    'Scale': 1.0}
        sk['Bones'] = used
        sk['Num Bones'] = len(used)
        xfs, bounds = [], []
        for k, b in enumerate(used):
            X = np.linalg.inv(Wn[b])
            s = float(np.cbrt(np.linalg.det(X[:3, :3])))
            xfs.append({'Rotation': M.mat3_dict(X[:3, :3] / s), 'Translation': M.vec3_dict(X[:3, 3]), 'Scale': s})
            sel = ((bi == k) & (Wt > 0)).any(1)
            c, r = sphere(P[sel] @ X[:3, :3].T + X[:3, 3])
            bounds.append({'Center': M.vec3_dict(c), 'Radius': r})
        sk['Bone Transforms'] = xfs
        if 'Bone Bounds' in sk:
            sk['Bone Bounds'] = bounds
        c, r = sphere(P)
        m['Bounding Sphere'] = {'Center': M.vec3_dict(c), 'Radius': r}
        # textures
        gmat = g['materials'][mat] if mat is not None and 'materials' in g else {}
        tx = gmat.get('extras', {}).get('ms2_textures') if keep_tex_names else None
        if tx:
            texp = next(p for p in m['Properties'] if p >= 0 and nif.blocks[p]['_type'] == 'NiTexturingProperty')
            tp = nif.blocks[texp]
            for key, slot in (('base', 'Base Texture'), ('gloss', 'Gloss Texture'), ('normal', 'Normal Texture'),
                              ('glow', 'Glow Texture'), ('dark', 'Dark Texture'), ('detail', 'Detail Texture')):
                if tp.get('Has ' + slot) and key in tx:
                    nif.blocks[tp[slot]['Source']]['File Name'] = {'Index': nif.addstr(tx[key])}
        elif not keep_tex_names and tex_out is not None:
            texp = next(p for p in m['Properties'] if p >= 0 and nif.blocks[p]['_type'] == 'NiTexturingProperty')
            tp = nif.blocks[texp]
            from PIL import Image
            base_img = None
            pbr = gmat.get('pbrMetallicRoughness', {})
            if 'baseColorTexture' in pbr:
                tex = g['textures'][pbr['baseColorTexture']['index']]
                base_img = Image.open(io.BytesIO(gl.image_bytes(tex['source'])))
                base_img.load()
            else:
                col = tuple(int(255 * min(1, max(0, x))) for x in pbr.get('baseColorFactor', [0.8, 0.8, 0.8, 1]))
                base_img = Image.new('RGBA', (4, 4), col)
            dn = '%s_%d_d.dds' % (tex_prefix, wi)
            write_dds(os.path.join(tex_out, dn), base_img)
            if flat_n is None:
                flat_n = '%s_flat_n.dds' % tex_prefix
                write_dds(os.path.join(tex_out, flat_n), Image.new('RGBA', (4, 4), (128, 128, 255, 255)))
            nn, sn = flat_n, dn
            if normal_tex and tp.get('Has Normal Texture') and 'normalTexture' in gmat:
                ni = Image.open(io.BytesIO(gl.image_bytes(g['textures'][gmat['normalTexture']['index']]['source'])))
                ni = ni.convert('RGB')
                if normal_flip_g:
                    r_, g_, b_ = ni.split()
                    ni = Image.merge('RGB', (r_, g_.point(lambda v: 255 - v), b_))
                nn = '%s_%d_n.dds' % (tex_prefix, wi)
                write_dds(os.path.join(tex_out, nn), ni, 'dxt1' if DDS_FMT != 'argb' else None)
            if gloss_scale is not None and tp.get('Has Gloss Texture'):
                mr = pbr.get('metallicRoughnessTexture')
                rf = pbr.get('roughnessFactor', 1.0)
                if mr:
                    ri = Image.open(io.BytesIO(gl.image_bytes(g['textures'][mr['index']]['source']))).convert('RGB')
                    rough = np.asarray(ri, np.float32)[:, :, 1] / 255.0 * rf
                else:
                    rough = np.full((4, 4), rf, np.float32)
                gv = np.clip((1.0 - rough) * gloss_scale * 255.0, 0, 255).astype(np.uint8)
                sn = '%s_%d_s.dds' % (tex_prefix, wi)
                write_dds(os.path.join(tex_out, sn), Image.fromarray(gv, 'L').convert('RGB'),
                          'dxt1' if DDS_FMT != 'argb' else None)
            for slot, fname in (('Base Texture', dn), ('Normal Texture', nn), ('Gloss Texture', sn)):
                if tp.get('Has ' + slot):
                    nif.blocks[tp[slot]['Source']]['File Name'] = {'Index': nif.addstr(fname)}
            for slot in ('Glow Texture', 'Dark Texture', 'Detail Texture'):
                if tp.get('Has ' + slot):
                    nif.blocks[tp[slot]['Source']]['File Name'] = {'Index': nif.addstr(flat_n)}
        new_meshes.append(mi)
        log('mesh %-12s %5d verts %5d tris %3d bones  (template %s)' % (nm, len(P), len(I) // 3, len(used),
                                                                          nif.name(B[src])))
    rb = nif.blocks[root]
    rb['Children'] = rb['Children'] + new_meshes
    rb['Num Children'] = len(rb['Children'])
    n = nif.gc()
    # drop strings nobody references any more? (kept: harmless, the header table just lists them)
    if os.path.dirname(out_path):
        os.makedirs(os.path.dirname(out_path), exist_ok=True)
    nif.save(out_path)
    log('%s: %d blocks' % (out_path, n))
    return nif


def compare(a_path, b_path, spec=None, log=print):
    """Semantic comparison of two NIFs' skinned meshes by name (positions, normals, uv, weights per bone name)."""
    spec = spec or Spec()
    res = True
    na, nb = Nif.load(spec, a_path), Nif.load(spec, b_path)

    def meshes(n):
        out = {}
        for i, b in enumerate(n.blocks):
            if b['_type'] != 'NiMesh':
                continue
            s = M.mesh_streams(n, i)
            si = M.skin_of(n, i)
            P = s.get('POSITION_BP', s.get('POSITION'))
            Nn = s.get('NORMAL_BP', s.get('NORMAL'))
            wts = None
            if si >= 0:
                sk = n.blocks[si]
                pal = s['BONE_PALETTE']
                w3 = s['BLENDWEIGHT'].astype(np.float64)
                w = np.concatenate([w3, 1 - w3.sum(1, keepdims=True)], 1)
                bn = [n.name(n.blocks[sk['Bones'][int(p)]]) for p in pal]
                wts = [{bn[j]: round(float(x), 4) for j, x in zip(s['BLENDINDICES'][v], w[v]) if x > 1e-4}
                       for v in range(len(P))]
            out[n.name(b)] = (P.astype(np.float64), Nn.astype(np.float64), s['TEXCOORD'], s['INDEX'], wts)
        return out
    ma, mb = meshes(na), meshes(nb)
    for k in sorted(set(ma) | set(mb)):
        if k not in ma or k not in mb:
            log('%-10s only in %s' % (k, 'a' if k in ma else 'b'))
            res = False
            continue
        A, Bm = ma[k], mb[k]
        if len(A[0]) != len(Bm[0]) or not np.array_equal(A[3], Bm[3]):
            log('%-10s vertex/index layout differs (%d/%d verts)' % (k, len(A[0]), len(Bm[0])))
            res = False
            continue
        dp = np.abs(A[0] - Bm[0]).max()
        dn = np.abs(A[1] / (np.linalg.norm(A[1], axis=1, keepdims=True) + 1e-30) - Bm[1]).max()
        du = np.abs(A[2] - Bm[2]).max()
        dw = 0.0
        if A[4] is not None and Bm[4] is not None:
            for wa, wb in zip(A[4], Bm[4]):
                for j in set(wa) | set(wb):
                    dw = max(dw, abs(wa.get(j, 0) - wb.get(j, 0)))
        ok = dp < 1e-3 and dn < 1e-3 and du < 1e-6 and dw < 2e-3
        res &= ok
        log('%-10s %5d verts  max |dpos| %.2g  |dnormal| %.2g  |duv| %.2g  |dweight| %.2g  %s' % (
            k, len(A[0]), dp, dn, du, dw, 'SAME' if ok else 'DIFF'))
    return res


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('glb')
    ap.add_argument('template')
    ap.add_argument('out')
    ap.add_argument('--mesh-template', default='PA_Panty')
    ap.add_argument('--tex-out')
    ap.add_argument('--tex-prefix')
    ap.add_argument('--keep-tex-names', action='store_true')
    ap.add_argument('--dds', choices=('argb', 'dxt1', 'dxt5'), default='argb',
                    help='--tex-out DDS format (default argb = uncompressed 32-bit)')
    ap.add_argument('--normal-tex', action='store_true', help="use the glTF normalTexture in a template Normal slot")
    ap.add_argument('--normal-flip-g', action='store_true', help='invert the normal map green channel')
    ap.add_argument('--gloss-from-roughness', type=float, metavar='S',
                    help='Gloss slot = (1 - roughness) * S instead of the base colour')
    ap.add_argument('--compare', help='reference nif for a semantic mesh comparison')
    a = ap.parse_args()
    global DDS_FMT
    DDS_FMT = a.dds
    build(a.glb, a.template, a.out, a.mesh_template, a.tex_out, a.tex_prefix, a.keep_tex_names,
          normal_tex=a.normal_tex, normal_flip_g=a.normal_flip_g, gloss_scale=a.gloss_from_roughness)
    if a.compare:
        ok = compare(a.compare, a.out)
        print('semantic compare:', 'SAME' if ok else 'DIFF')
        sys.exit(0 if ok else 1)


if __name__ == '__main__':
    main()
