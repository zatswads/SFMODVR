"""Readers and writers for the two exports (SfMorph's game export, SfExport's VaM export).

Everything stays in Unity axes (left-handed, Y up, metres) and in the game's vertex order.
"""
import os
import numpy as np


class Mesh:
    """One of her meshes as SfMorph exported it (bind space, game vertex order)."""

    def __init__(self, name):
        self.name = name
        self.v = None          # (n,3) positions
        self.vn = None         # (n,3) normals
        self.uv = None         # (n,2)
        self.faces = None      # (m,3) vertex indices (triangles, all submeshes)
        self.face_group = None  # (m,) submesh index per face
        self.groups = []       # material name per submesh
        self.bones = []        # bone names (mesh bone index -> name)
        self.bind = None       # (b,4,4) bind poses (Unity bindposes: mesh -> bone)
        self.wi = None         # (n,4) bone indices into self.bones
        self.ww = None         # (n,4) weights


def _floats(parts):
    return [float(p) for p in parts]


def read_obj(path):
    """Minimal OBJ reader: positions, normals, uvs, faces with groups (fan-triangulated).
    Assumes v/vt/vn share indices when present (true for both exporters)."""
    v, vn, vt, faces, fgroup, groups = [], [], [], [], [], []
    g = -1
    with open(path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            if not line or line[0] == '#':
                continue
            t = line.split()
            if not t:
                continue
            k = t[0]
            if k == 'v':
                v.append(_floats(t[1:4]))
            elif k == 'vn':
                vn.append(_floats(t[1:4]))
            elif k == 'vt':
                vt.append(_floats(t[1:3]))
            elif k == 'g' or k == 'usemtl':
                name = t[1] if len(t) > 1 else 'g%d' % len(groups)
                if k == 'usemtl' and groups and groups[-1] == name:
                    continue
                groups.append(name)
                g = len(groups) - 1
            elif k == 'f':
                idx = [int(p.split('/')[0]) - 1 for p in t[1:]]
                for i in range(1, len(idx) - 1):
                    faces.append((idx[0], idx[i], idx[i + 1]))
                    fgroup.append(max(g, 0))
    if not groups:
        groups = ['default']
    return (np.array(v, np.float64), np.array(vn, np.float64) if vn else None,
            np.array(vt, np.float64) if vt else None, np.array(faces, np.int64).reshape(-1, 3),
            np.array(fgroup, np.int64), groups)


def read_skin(path, m):
    with open(path, 'r', encoding='utf-8', errors='replace') as f:
        lines = [l for l in f.read().splitlines() if l and not l.startswith('#')]
    i = 0
    nb = int(lines[i].split()[1]); i += 1
    m.bones, binds = [], []
    for _ in range(nb):
        t = lines[i].split(); i += 1
        m.bones.append(t[1])
        binds.append(np.array(_floats(t[2:18])).reshape(4, 4) if len(t) >= 18 else np.eye(4))
    m.bind = np.array(binds) if binds else np.zeros((0, 4, 4))
    nw = int(lines[i].split()[1]); i += 1
    w = np.array([_floats(l.split()) for l in lines[i:i + nw]]).reshape(-1, 8)
    m.wi = w[:, 0::2].astype(np.int64)
    m.ww = w[:, 1::2]
    s = m.ww.sum(1, keepdims=True)
    s[s <= 0] = 1
    m.ww = m.ww / s


def read_game_export(d):
    """Her export: meshes.txt index, per-mesh .obj + .skin.txt, skeleton.txt."""
    meshes, missing = [], []
    with open(os.path.join(d, 'meshes.txt'), encoding='utf-8') as f:
        rows = [l.split('\t') for l in f.read().splitlines() if l and not l.startswith('#')]
    for r in rows:
        name = r[0]
        obj = os.path.join(d, name + '.obj')
        skin = os.path.join(d, name + '.skin.txt')
        if not os.path.exists(obj):
            missing.append(name)
            continue
        m = Mesh(name)
        m.v, m.vn, m.uv, m.faces, m.face_group, m.groups = read_obj(obj)
        m.expected = int(r[2])
        m.materials = [x for x in r[4].split(';') if x] if len(r) > 4 else []
        if os.path.exists(skin):
            read_skin(skin, m)
        meshes.append(m)
    skel = {}
    sp = os.path.join(d, 'skeleton.txt')
    if os.path.exists(sp):
        with open(sp, encoding='utf-8') as f:
            for l in f.read().splitlines():
                if not l or l.startswith('#'):
                    continue
                t = l.split('\t')
                skel[t[0]] = dict(parent=t[1], pos=np.array(_floats(t[2].split())),
                                  rot=np.array(_floats(t[3].split())))
    return meshes, skel, missing


class Vam:
    pass


def read_vam_export(d):
    x = Vam()
    x.dir = d
    x.v, x.vn, x.uv, x.faces, x.face_group, x.groups = read_obj(os.path.join(d, 'body.obj'))
    x.joints = {}
    with open(os.path.join(d, 'joints.txt'), encoding='utf-8') as f:
        for l in f.read().splitlines():
            if not l or l.startswith('#'):
                continue
            t = l.split('\t')
            x.joints[t[0]] = dict(parent=t[1], pos=np.array(_floats(t[2].split())))
    x.materials = {}
    with open(os.path.join(d, 'materials.txt'), encoding='utf-8') as f:
        for l in f.read().splitlines():
            if not l or l.startswith('#'):
                continue
            t = l.split('\t')
            slots = dict(s.split('=', 1) for s in t[4:] if '=' in s)
            x.materials[t[1]] = dict(index=int(t[0]), region=t[3], slots=slots)
    x.info = {}
    ip = os.path.join(d, 'info.txt')
    if os.path.exists(ip):
        with open(ip, encoding='utf-8') as f:
            for l in f.read().splitlines():
                t = l.split('\t', 1)
                if len(t) == 2:
                    x.info[t[0]] = t[1]
    return x


def newest_subdir(d):
    subs = [os.path.join(d, s) for s in os.listdir(d) if os.path.isdir(os.path.join(d, s))]
    if not subs:
        raise SystemExit('no export folders in ' + d)
    return max(subs, key=os.path.getmtime)


def face_normals(v, f):
    n = np.cross(v[f[:, 1]] - v[f[:, 0]], v[f[:, 2]] - v[f[:, 0]])
    a = np.linalg.norm(n, axis=1)
    return n / np.maximum(a, 1e-20)[:, None], a * 0.5


def winding_sign(v, f, vn):
    """+1 if cross(b-a, c-a) points along the stored vertex normals, else -1."""
    if vn is None or len(vn) != len(v):
        return 1.0
    n, _ = face_normals(v, f)
    s = np.einsum('ij,ij->i', n, vn[f].mean(1))
    return 1.0 if np.median(s) >= 0 else -1.0


def vertex_normals(v, f, sign=1.0):
    n = np.cross(v[f[:, 1]] - v[f[:, 0]], v[f[:, 2]] - v[f[:, 0]]) * sign  # area weighted
    out = np.zeros_like(v)
    for k in range(3):
        np.add.at(out, f[:, k], n)
    l = np.linalg.norm(out, axis=1)
    out[l > 0] /= l[l > 0, None]
    return out


def write_morph(path, name, offsets, dnormals):
    """Per-vertex offsets in the game's vertex order, bind space, Unity axes.
    Text: header lines, then one 'dx dy dz nx ny nz' line per vertex (all vertices, in order)."""
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('# SfMorph morph: per-vertex position and normal deltas, bind space, game vertex order\n')
        f.write('mesh %s\n' % name)
        f.write('vertices %d\n' % len(offsets))
        for o, n in zip(offsets, dnormals):
            f.write('%.6g %.6g %.6g %.5g %.5g %.5g\n' % (o[0], o[1], o[2], n[0], n[1], n[2]))
