#!/usr/bin/env python3
"""ms2nif.py: MapleStory 2 (Gamebryo 30.2.0.3) scene graph, NiMesh and KF helpers on top of nifxml.py.

Conventions found on shipped files (f_body.nif, NPC nifs):
- NiAVObject.Rotation: the 9 floats in file order are the rotation matrix row-major for column vectors,
  p_parent = R * (s * p) + T.  (nif.xml names them m11 m21 m31 ...; take them in file order.)
- NiSkinningMeshModifier.Bone Transforms = inverse(world(bone)) relative to the skin space (= the skeleton root);
  on shipped humanoids it equals the node rest pose to 1e-5, so bind pose = rest pose.
- MS2 is Z-up, 1 unit ~ 1 cm-ish (the female body is about 100 units tall).
- NiMesh streams: INDEX (UINT16), one interleaved vertex stream (TEXCOORD, POSITION_BP, NORMAL_BP, BINORMAL_BP,
  TANGENT_BP, BLENDINDICES UINT8x4, BLENDWEIGHT float3: the 4th weight is 1 - sum), BONE_PALETTE (UINT16, maps a
  blend index to an index in the modifier's Bones list).
"""
import numpy as np
from nifxml import Nif, Spec  # noqa: F401

DT = {0x05: 'u1', 0x06: 'u1', 0x07: 'u1', 0x08: 'u1', 0x01: 'i1', 0x02: 'i1', 0x03: 'i1', 0x04: 'i1',
      0x11: 'i2', 0x12: 'i2', 0x13: 'i2', 0x14: 'i2', 0x15: 'u2', 0x16: 'u2', 0x17: 'u2', 0x18: 'u2',
      0x21: 'i4', 0x22: 'i4', 0x23: 'i4', 0x24: 'i4', 0x25: 'u4', 0x26: 'u4', 0x27: 'u4', 0x28: 'u4',
      0x31: 'f2', 0x32: 'f2', 0x33: 'f2', 0x34: 'f2', 0x35: 'f4', 0x36: 'f4', 0x37: 'f4', 0x38: 'f4',
      0x0D: 'u1', 0x0E: 'u1', 0x0F: 'u1', 0x10: 'u1', 0x1D: 'u2', 0x1E: 'u2', 0x1F: 'u2', 0x20: 'u2'}


def comp_dtype(f):
    n, size, code = (f >> 16) & 0xff, (f >> 8) & 0xff, f & 0xff
    dt = np.dtype('<' + DT[code])
    assert dt.itemsize == size, hex(f)
    return dt, n


def mat3(r):
    v = list(r.values())
    return np.array(v, dtype=np.float64).reshape(3, 3)


def mat3_dict(m):
    keys = ['m11', 'm21', 'm31', 'm12', 'm22', 'm32', 'm13', 'm23', 'm33']
    return dict(zip(keys, [float(x) for x in np.asarray(m).reshape(9)]))


def vec3(d):
    return np.array([d['x'], d['y'], d['z']], dtype=np.float64)


def vec3_dict(v):
    return {'x': float(v[0]), 'y': float(v[1]), 'z': float(v[2])}


def trs_matrix(T, R, s):
    M = np.eye(4)
    M[:3, :3] = R * s
    M[:3, 3] = T
    return M


def xform_matrix(x):
    """NiTransform / NiAVObject (Translation, Rotation, Scale) -> 4x4."""
    return trs_matrix(vec3(x['Translation']), mat3(x['Rotation']), x['Scale'])


def quat_to_mat(w, x, y, z):
    n = (w * w + x * x + y * y + z * z) ** .5 or 1.0
    w, x, y, z = w / n, x / n, y / n, z / n
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)],
                     [2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)],
                     [2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)]])


def mat_to_quat(m):
    """3x3 rotation (column-vector convention) -> (w, x, y, z)."""
    t = m[0, 0] + m[1, 1] + m[2, 2]
    if t > 0:
        s = (t + 1.0) ** .5 * 2
        q = (0.25 * s, (m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s)
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = (1.0 + m[0, 0] - m[1, 1] - m[2, 2]) ** .5 * 2
        q = ((m[2, 1] - m[1, 2]) / s, 0.25 * s, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s)
    elif m[1, 1] > m[2, 2]:
        s = (1.0 + m[1, 1] - m[0, 0] - m[2, 2]) ** .5 * 2
        q = ((m[0, 2] - m[2, 0]) / s, (m[0, 1] + m[1, 0]) / s, 0.25 * s, (m[1, 2] + m[2, 1]) / s)
    else:
        s = (1.0 + m[2, 2] - m[0, 0] - m[1, 1]) ** .5 * 2
        q = ((m[1, 0] - m[0, 1]) / s, (m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, 0.25 * s)
    q = np.array(q)
    return q / np.linalg.norm(q)


class Scene:
    """Scene graph view of a loaded Nif."""

    def __init__(self, nif):
        self.nif = nif
        self.B = nif.blocks
        self.parent = {}
        for i, b in enumerate(self.B):
            for c in b.get('Children', []):
                if c >= 0:
                    self.parent[c] = i

    def name(self, i):
        return self.nif.name(self.B[i])

    def local(self, i):
        return xform_matrix(self.B[i])

    def world(self, i, stop=None):
        M = self.local(i)
        while i in self.parent and self.parent[i] != stop:
            i = self.parent[i]
            M = self.local(i) @ M
        return M

    def walk(self, i=None, depth=0):
        i = self.nif.roots[0] if i is None else i
        yield i, depth
        for c in self.B[i].get('Children', []):
            if c >= 0:
                yield from self.walk(c, depth + 1)

    def node_by_name(self):
        return {self.name(i): i for i, _ in self.walk() if self.B[i]['_type'] == 'NiNode'}


def mesh_streams(nif, mi):
    """Decode a NiMesh: returns dict semantic name (with index suffix if >0) -> numpy array, plus 'regions'."""
    B = nif.blocks
    m = B[mi]
    out = {}
    for ref in m['Datastreams']:
        ds = B[ref['Stream']]
        data = ds['Data']['Data']
        fmts = ds['Component Formats']
        sems = [(nif.str(c['Name']), c['Index']) for c in ref['Component Semantics']]
        fields = []
        for k, (f, (nm, idx)) in enumerate(zip(fmts, sems)):
            dt, n = comp_dtype(f)
            key = nm if idx == 0 else '%s%d' % (nm, idx)
            fields.append((key, dt, (n,) if n > 1 else ()))
        rec = np.dtype(fields)
        arr = np.frombuffer(data, dtype=rec)
        for key, _, _ in fields:
            out[key] = np.array(arr[key])
        out.setdefault('_regions', {})[fields[0][0]] = ds['Regions']
    return out


def skin_of(nif, mi):
    B = nif.blocks
    for r in B[mi]['Modifiers']:
        if r >= 0 and B[r]['_type'] == 'NiSkinningMeshModifier':
            return r
    return -1


# ---------------- KF evaluation ----------------

def bspline_eval(cps, nq, t01, dim):
    """Open uniform cubic B-spline, nq control points (rows of cps, dim wide), t in [0,1]."""
    deg = 3
    qmd = nq - deg
    t01 = min(max(t01, 0.0), 1.0)
    if t01 >= 1.0:
        T, imin = float(qmd), nq - 1 - deg
    else:
        T = qmd * t01
        imin = int(T)
    imax = imin + deg
    knot = []
    for i1 in range(imax + 1 - deg, imax + 1 + deg):
        knot.append(0.0 if i1 <= deg else (float(qmd) if i1 >= nq else float(i1 - deg)))
    val = [0.0] * (deg + 1)
    val[deg] = 1.0
    for row in range(deg - 1, -1, -1):
        k0, k1 = deg, row
        inv = 1.0 / (knot[k0] - knot[k1])
        c1 = (knot[k0] - T) * inv
        val[row] = c1 * val[row + 1]
        for col in range(row + 1, deg):
            c0 = (T - knot[k1]) * inv
            val[col] *= c0
            k0 += 1
            k1 += 1
            inv = 1.0 / (knot[k0] - knot[k1])
            c1 = (knot[k0] - T) * inv
            val[col] += c1 * val[col + 1]
        c0 = (T - knot[k1]) * inv
        val[deg] *= c0
    out = np.zeros(dim)
    for k in range(deg + 1):
        out += val[k] * cps[imin + k]
    return out


INVALID = 0xFFFF
FLT_MAX = 3.0e38


def _lerp_keys(keys, t, kind):
    """keys: list of (time, value np.array); kind 'quat' slerps."""
    if not keys:
        return None
    if t <= keys[0][0]:
        return keys[0][1]
    if t >= keys[-1][0]:
        return keys[-1][1]
    for a, b in zip(keys, keys[1:]):
        if a[0] <= t <= b[0]:
            u = (t - a[0]) / ((b[0] - a[0]) or 1.0)
            if kind == 'quat':
                q0, q1 = a[1], b[1]
                if np.dot(q0, q1) < 0:
                    q1 = -q1
                d = min(1.0, float(np.dot(q0, q1)))
                th = np.arccos(d)
                if th < 1e-6:
                    q = q0 * (1 - u) + q1 * u
                else:
                    q = (np.sin((1 - u) * th) * q0 + np.sin(u * th) * q1) / np.sin(th)
                return q / np.linalg.norm(q)
            if len(a) > 2:
                # quadratic (Hermite) keys stored as (t, v, Forward, Backward); the stream order is the
                # in-tangent then the out-tangent, so a span uses key0's Backward and key1's Forward.
                h00, h10, h01, h11 = 2*u**3 - 3*u**2 + 1, u**3 - 2*u**2 + u, -2*u**3 + 3*u**2, u**3 - u**2
                return h00 * a[1] + h10 * a[3] + h01 * b[1] + h11 * b[2]
            return a[1] * (1 - u) + b[1] * u
    return keys[-1][1]


def _axis_quat(axis, a):
    q = np.zeros(4)
    q[0] = np.cos(a / 2)
    q[1 + axis] = np.sin(a / 2)
    return q


def quat_mul(a, b):
    w1, x1, y1, z1 = a
    w2, x2, y2, z2 = b
    return np.array([w1 * w2 - x1 * x2 - y1 * y2 - z1 * z2, w1 * x2 + x1 * w2 + y1 * z2 - z1 * y2,
                     w1 * y2 - x1 * z2 + y1 * w2 + z1 * x2, w1 * z2 + x1 * y2 - y1 * x2 + z1 * w2])


def euler_xyz_quat(x, y, z):
    """Gamebryo XYZ Euler rotation keys: q = qZ * qY * qX (checked numerically against the engine's result)."""
    return quat_mul(quat_mul(_axis_quat(2, z), _axis_quat(1, y)), _axis_quat(0, x))


def _float_keys(grp):
    if not grp.get('Num Keys'):
        return None
    it = grp.get('Interpolation')
    assert it in (1, 2, 5), 'float key type %s' % it
    if it == 2:
        return [(k['Time'], np.array([k['Value']]), np.array([k['Forward']]), np.array([k['Backward']]))
                for k in grp['Keys']]
    return [(k['Time'], np.array([k['Value']])) for k in grp['Keys']]


def _euler_keys(groups):
    """Three float KeyGroups (X, Y, Z angles) -> a sampling function wrapped as a 'keys' object."""
    ks = [_float_keys(g) for g in groups]
    return EulerKeys(ks)


class EulerKeys(list):
    def __init__(self, ks):
        super().__init__([(0.0, None)])
        self.ks = ks

    def at(self, t):
        ang = [(_lerp_keys(k, t, 'v')[0] if k else 0.0) for k in self.ks]
        return euler_xyz_quat(*ang)


class Channel:
    def __init__(self, fn, const=False):
        self.fn, self.const = fn, const


def kf_channels(kf, nodes=None):
    """Return (sequence name, duration, {node name: {'T': Channel, 'R': Channel, 'S': Channel}}, text keys).
    R channels return quaternions (w, x, y, z)."""
    B = kf.blocks
    seq = next(b for b in B if b['_type'] == 'NiSequenceData')
    name = kf.str(seq['Name'])
    dur = seq['Duration']
    res = {}
    for ei in seq['Evaluators']:
        e = B[ei]
        node = kf.str(e['Node Name'])
        ct = e['Channel Types']
        ch = {}
        if e['_type'] == 'NiTransformEvaluator':
            v = e['Value']
            data = B[e['Data']] if e['Data'] >= 0 else None
            keysT = keysR = keysS = None
            if data is not None:
                if data['Num Rotation Keys']:
                    rt = data['Rotation Type']
                    if rt == 4:
                        keysR = _euler_keys(data['XYZ Rotations'])
                    else:
                        assert rt in (1, 5), 'rotation key type %d' % rt   # TBC (3) not handled
                        keysR = [(k['Time'], np.array([k['Value'][c] for c in 'wxyz']))
                                 for k in data['Quaternion Keys']]
                tg = data['Translations']
                if tg['Num Keys']:
                    assert tg['Interpolation'] in (1, 2, 5), tg['Interpolation']
                    if tg['Interpolation'] == 2:
                        keysT = [(k['Time'], vec3(k['Value']), vec3(k['Forward']), vec3(k['Backward']))
                                 for k in tg['Keys']]
                    else:
                        keysT = [(k['Time'], vec3(k['Value'])) for k in tg['Keys']]
                sg = data['Scales']
                if sg['Num Keys']:
                    assert sg['Interpolation'] in (1, 2, 5), sg['Interpolation']
                    if sg['Interpolation'] == 2:
                        keysS = [(k['Time'], np.array([k['Value']]), np.array([k['Forward']]),
                                  np.array([k['Backward']])) for k in sg['Keys']]
                    else:
                        keysS = [(k['Time'], np.array([k['Value']])) for k in sg['Keys']]
            if ct[0]:
                if keysT:
                    ch['T'] = Channel(lambda t, k=keysT: _lerp_keys(k, t, 'v'))
                elif v['Translation']['x'] < FLT_MAX:
                    ch['T'] = Channel(lambda t, c=vec3(v['Translation']): c, True)
            if ct[1]:
                if isinstance(keysR, EulerKeys):
                    ch['R'] = Channel(keysR.at)
                elif keysR:
                    ch['R'] = Channel(lambda t, k=keysR: _lerp_keys(k, t, 'quat'))
                elif v['Rotation']['w'] < FLT_MAX and v['Rotation']['w'] > -FLT_MAX:
                    ch['R'] = Channel(lambda t, c=np.array([v['Rotation'][c] for c in 'wxyz']): c, True)
            if ct[2]:
                if keysS:
                    ch['S'] = Channel(lambda t, k=keysS: _lerp_keys(k, t, 'v'))
                elif v['Scale'] < FLT_MAX:
                    ch['S'] = Channel(lambda t, c=np.array([v['Scale']]): c, True)
        elif e['_type'] in ('NiBSplineCompTransformEvaluator', 'NiBSplineTransformEvaluator'):
            comp = e['_type'] == 'NiBSplineCompTransformEvaluator'
            data = B[e['Data']] if e['Data'] >= 0 else None
            nq = B[e['Basis Data']]['Num Control Points'] if e['Basis Data'] >= 0 else 0
            t0, t1 = e['Start Time'], e['End Time']
            span = (t1 - t0) or 1.0
            x = e['Transform']

            def mk(handle, dim, off, half):
                if comp:
                    raw = np.array(data['Compact Control Points'][handle:handle + nq * dim], dtype=np.float64)
                    cps = (off + half * raw / 32767.0).reshape(nq, dim)
                else:
                    cps = np.array(data['Float Control Points'][handle:handle + nq * dim]).reshape(nq, dim)
                return lambda t: bspline_eval(cps, nq, (t - t0) / span, dim)
            if ct[0]:
                h = e['Translation Handle']
                if h != INVALID:
                    ch['T'] = Channel(mk(h, 3, e.get('Translation Offset'), e.get('Translation Half Range')))
                elif x['Translation']['x'] < FLT_MAX:
                    ch['T'] = Channel(lambda t, c=vec3(x['Translation']): c, True)
            if ct[1]:
                h = e['Rotation Handle']
                if h != INVALID:
                    f = mk(h, 4, e.get('Rotation Offset'), e.get('Rotation Half Range'))
                    ch['R'] = Channel(lambda t, f=f: (lambda q: q / np.linalg.norm(q))(f(t)))
                elif abs(x['Rotation']['w']) < FLT_MAX:
                    ch['R'] = Channel(lambda t, c=np.array([x['Rotation'][c] for c in 'wxyz']): c, True)
            if ct[2]:
                h = e['Scale Handle']
                if h != INVALID:
                    ch['S'] = Channel(mk(h, 1, e.get('Scale Offset'), e.get('Scale Half Range')))
                elif abs(x['Scale']) < FLT_MAX:
                    ch['S'] = Channel(lambda t, c=np.array([x['Scale']]): c, True)
        else:
            continue
        if ch:
            res[node] = ch
    tk = []
    if seq['Text Keys'] >= 0:
        for k in B[seq['Text Keys']]['Text Keys']:
            tk.append((k['Time'], kf.str(k['Value']['Index'])))
    return name, dur, res, tk
