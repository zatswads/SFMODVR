"""Offline pose replay: skin her meshes with a pose recorded in the game (SfMorph's PoseRecorder), once as the game's
original character and once as the converted one (morph deltas + moved joints with matching bind poses, exactly the
way Morph.cs does it at runtime), then measure what the conversion does in motion.

    python replay.py --game <SfMorph export dir> --char <morph dir> --pose <recording.txt> --out <dir>
                     [--face 1] [--body 1] [--jointw <body>] [--stride 1] [--max-frames 0] [--render-frames 4]
                     [--bake-frame 0] [--tol-mm 1] [--no-metrics]
    python replay.py --game <export dir> --synth <file.txt> [--synth-frames 8]     (makes a synthetic recording)

Writes <out>/replay_metrics.txt and <out>/replay.npz (render with: blender -b --factory-startup -P blend.py -- --replay <out>/replay.npz).

Skinning (per mesh, the mesh's own bind poses bp_k from its .skin.txt; M_b = bone -> character root space, from the file):
    original:  x = sum_k w_k M_b(k) bp_k v
    converted: x = sum_k w_k M'_b(k) bp'_k (v + body*D*(1-h) + face*D*h)
Joints (Morph.cs JointLocals / SetBind), parents first:
    eff_b = joints.txt offset of b, or its nearest ancestor's when b is 'fixed' (its parent is not a skin bone but a skin bone is above it)
    l_b   = bp_p[:3,:3] (eff_b - eff_p)            for a moved bone b with parent p
    M'_b  = M'_p T(l_b) inv(M_p) M_b               (unmoved: l_b = 0, the bone follows its parent's change)
    bp'_b = bp_b T(-ToMesh eff_b)
(DynamicBone-simulated bones, which the game keeps at their parent's offset, are not known offline.)
"""
import argparse
import os
import sys
import time

import numpy as np
from scipy.spatial import cKDTree

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import sfio  # noqa: E402
import rig  # noqa: E402
import posetest  # noqa: E402

SKIP_RENDER = ('uterus', 'rectum', 'Mouth', 'Teeth', 'tongue', 'Glow')
BODY_DEFAULT = ('reika_upperBody_normal', 'reika_lowerBody_normal', 'reika_foot', 'reika_neck', 'reika_face_a')
FACE_MESH = 'reika_face_a'
THIGH_PAIRS = (('thighL', 'thighR'), ('thighR', 'thighL'))
ARM_PAIRS = (('uparmL', 'torso'), ('uparmR', 'torso'))


def log(s):
    print('[replay] ' + s, flush=True)


def T(v):
    m = np.eye(4)
    m[:3, 3] = v
    return m


def clean(n):
    for s in (' (SfMorph)', ' (Instance)', ' (Clone)'):
        n = n.replace(s, '')
    return n.strip()


# ---------------------------------------------------------------------------------------------------------------
# pose file

class Pose:
    pass


def read_pose(path):
    """SfMorph pose recording v1 (see PoseRecorder.cs). Unknown keys are kept in .hdr and otherwise ignored."""
    P = Pose()
    P.hdr = {}
    P.bones = []
    P.frames = []
    P.r2r = np.eye(4)
    P.primary = (None, None)
    P.flags = {}
    P.bakefile = None
    nb = 0
    cur = None
    dropped = 0
    with open(path, 'r', encoding='utf-8', errors='replace') as f:
        for raw in f:
            line = raw.rstrip('\r\n')
            if not line.strip() or line.startswith('#'):
                continue
            key, _, rest = line.partition(' ')
            try:
                if key == 'frame':
                    t = rest.split()
                    cur = dict(index=int(float(t[0])), time=float(t[1]) if len(t) > 1 else 0.0, M=None, R=[])
                    P.frames.append(cur)
                elif cur is not None:
                    if key == 'M':
                        a = np.array(rest.split(), np.float64)
                        if len(P.bones) and len(a) == 12 * len(P.bones):
                            cur['M'] = a.reshape(-1, 3, 4)
                    elif key == 'R':
                        t = rest.split('\t')
                        if len(t) < 4:
                            t = rest.split()
                        cur['R'].append((clean(t[0]), clean(t[1]), int(float(t[2])) != 0, int(float(t[3]))))
                    elif key == 'end':
                        cur = None
                else:
                    if key == 'bones':
                        nb = int(rest.split()[0])
                        P.bones = ['-'] * nb
                    elif key == 'bone':
                        i, _, name = rest.partition(' ')
                        i = int(i)
                        if i >= len(P.bones):
                            P.bones += ['-'] * (i + 1 - len(P.bones))
                        P.bones[i] = name.strip()
                    elif key == 'primary':
                        t = rest.split('\t')
                        P.primary = (clean(t[0]), clean(t[1]) if len(t) > 1 else None)
                    elif key == 'primaryToRoot':
                        a = np.array(rest.split(), np.float64)
                        if len(a) >= 16:
                            P.r2r = a[:16].reshape(4, 4)
                    elif key == 'flags':
                        for kv in rest.split():
                            if '=' in kv:
                                k, v = kv.split('=', 1)
                                P.flags[k] = v
                    elif key == 'bake':
                        P.bakefile = rest.split(' (')[0].strip()
                        if P.bakefile in ('none', ''):
                            P.bakefile = None
                    elif key != 'frames':
                        P.hdr[key] = rest
            except (ValueError, IndexError):
                dropped += 1   # tolerant: a line that does not parse is skipped
    good = [fr for fr in P.frames if fr['M'] is not None]
    if len(good) != len(P.frames):
        log('WARN: %d frame(s) without a usable M line (wrong float count?) skipped' % (len(P.frames) - len(good)))
    P.frames = good
    if dropped:
        log('WARN: %d unparsable line(s) skipped' % dropped)
    return P


def write_pose(path, bones, frames, primary, r2r, bake_note, fps):
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('# SfMorph pose recording v1 (synthetic, made by replay.py --synth)\n')
        f.write('key Reika\nscene Synthetic\ndate synthetic\nfps %g\nseconds %g\nrootScale 1 1 1\n' % (fps, len(frames) / fps))
        f.write('flags apply=off converted=none softZones=off embody=off physicsHands=off\n')
        f.write('primary %s\t%s\n' % primary)
        f.write('primaryToRoot ' + ' '.join('%.7g' % x for x in r2r.ravel()) + '\n')
        f.write('bake %s\n' % (bake_note or 'none'))
        f.write('bones %d\n' % len(bones))
        for i, b in enumerate(bones):
            f.write('bone %d %s\n' % (i, b))
        f.write('frames %d\n' % len(frames))
        for i, (t, M, rends) in enumerate(frames):
            f.write('frame %d %.4f %.4f\n' % (i, t, t))
            f.write('M ' + ' '.join('%.7g' % x for x in M[:, :3, :].ravel()) + '\n')
            for r in rends:
                f.write('R %s\t%s\t%d\t%d\n' % r)
            f.write('end\n')


def read_bake(path):
    v, r2r, ls = [], None, np.ones(3)
    with open(path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            if line.startswith('v '):
                v.append([float(x) for x in line.split()[1:4]])
            elif line.startswith('#'):
                if 'rendererToRoot' in line:
                    a = np.array([float(x) for x in line.split(':', 1)[1].split()], np.float64)
                    if len(a) >= 16:
                        r2r = a[:16].reshape(4, 4)
                elif 'rendererLossyScale' in line:
                    ls = np.array([float(x) for x in line.split()[2:5]])
    return np.array(v, np.float64), r2r, ls


# ---------------------------------------------------------------------------------------------------------------
# character set-up: joints, morph, bind poses

class Setup:
    def __init__(self, game, char, body, face, jw):
        meshes, skel, _ = sfio.read_game_export(game)
        self.all_meshes = meshes
        self.meshes = [m for m in meshes if m.bind is not None and len(m.bones)]
        self.by = {m.name: m for m in self.meshes}
        self.canon = rig.canonical_binds(self.meshes)
        self.skin = set(self.canon)
        self.skel = skel
        self.body, self.face = body, face
        self.jw = body if jw is None else jw
        self.joints = {}
        jp = os.path.join(char, 'joints.txt')
        if os.path.exists(jp):
            with open(jp, encoding='utf-8') as f:
                for l in f.read().splitlines():
                    if l and not l.startswith('#'):
                        t = l.split('\t')
                        self.joints[t[0]] = np.array([float(x) for x in t[3].split()])
        self.skin_meshes = []
        cp = os.path.join(char, 'character.txt')
        if os.path.exists(cp):
            with open(cp, encoding='utf-8') as f:
                self.skin_meshes = [l.split('\t')[1].strip() for l in f.read().splitlines() if l.startswith('skin\t')]
        self.body_meshes = [n for n in (self.skin_meshes or BODY_DEFAULT) if n in self.by]
        # morph deltas and head weights per mesh
        self.D, self.H = {}, {}
        for m in self.meshes:
            p = os.path.join(char, m.name + '.morph.txt')
            if not os.path.exists(p):
                continue
            a = np.loadtxt(p, skiprows=3, ndmin=2)
            if len(a) != len(m.v):
                log('WARN: %s: morph has %d rows, mesh %d vertices; skipped' % (m.name, len(a), len(m.v)))
                continue
            self.D[m.name] = a[:, :3]
            self.H[m.name] = a[:, 6] if a.shape[1] > 6 else np.zeros(len(a))
        self._joints()
        self._tomesh()
        self.missing = set()
        self._base = {}

    def par(self, b):
        p = self.skel[b]['parent'] if b in self.skel else None
        return None if p in (None, '-', '') else p

    def ancestors(self, b):
        a, p, n = [], self.par(b), 0
        while p is not None and n < 256:
            a.append(p); p = self.par(p); n += 1
        return a

    def _joints(self):
        """Morph.cs JointLocals: effective offsets, local shifts of moved bones, the fixed (not moved) ones."""
        depth = {b: len(self.ancestors(b)) for b in self.skin}
        self.order = sorted(self.skin, key=lambda b: (depth[b], b))
        eff, fixed, self.anc = {}, set(), {}
        for b in self.order:
            anc = self.ancestors(b)
            sk = [a for a in anc if a in self.skin]
            self.anc[b] = sk[0] if sk else None
            p = self.par(b)
            if p is not None and p not in self.skin and sk:   # driven: parent is not a skin bone, a skin bone is above
                fixed.add(b)
            if b not in fixed and b in self.joints:
                eff[b] = self.joints[b] * self.jw
            else:
                eff[b] = next((eff[a] for a in anc if a in eff), np.zeros(3))
        self.eff, self.fixed = eff, fixed
        self.lcl, self.above = {}, {}
        for b in self.order:
            p = self.par(b)
            if b in fixed or p is None:
                continue
            dp = eff[p] if p in eff else next((eff[a] for a in self.ancestors(p) if a in eff), np.zeros(3))
            delta = eff[b] - dp
            if delta @ delta < 1e-10:
                continue
            if p in self.canon:
                self.lcl[b] = self.canon[p][:3, :3] @ delta
            else:
                self.above[b] = delta   # the static rig root above the skeleton: moved in the renderer's space
        # fallback pose of a bone missing in the recording: rigid on its nearest skin ancestor
        self.L = {b: self.canon[self.anc[b]] @ np.linalg.inv(self.canon[b]) for b in self.order if self.anc[b] is not None}
        log('joints: %d skin bones, %d moved (%s), %d fixed%s' % (
            len(self.skin), len(self.lcl) + len(self.above), ', '.join(sorted(self.lcl) + sorted(self.above)) or 'none', len(fixed),
            '' if self.joints else ' (no joints.txt)'))

    def _tomesh(self):
        """Rotation taking the common bind space to each mesh's own space (SfMorph's ToMesh), from the mesh's heaviest bone."""
        self.tomesh = {}
        for m in self.meshes:
            cnt = np.bincount(m.wi.ravel(), weights=m.ww.ravel(), minlength=len(m.bones))
            for k in np.argsort(-cnt):
                b = m.bones[k]
                if b in self.canon:
                    A = np.linalg.inv(self.canon[b]) @ m.bind[k]    # mesh -> common bind space
                    self.tomesh[m.name] = np.linalg.inv(A[:3, :3])
                    break
            else:
                self.tomesh[m.name] = np.eye(3)

    def solve(self, Mpose, r2r):
        """Per skin bone: original and converted bone->root matrices of one frame. Mpose: name -> 4x4 from the file."""
        Mo, Mc = {}, {}
        for b in self.order:
            a = self.anc[b]
            if b in Mpose:
                Mb = Mpose[b]
            else:
                self.missing.add(b)
                Mb = Mo[a] @ self.L[b] if a is not None else np.linalg.inv(self.canon[b])
            Mo[b] = Mb
            if a is None:
                Mc[b] = T(r2r[:3, :3] @ self.above[b]) @ Mb if b in self.above else Mb
            else:
                l = self.lcl.get(b)
                Mc[b] = Mc[a] @ (T(l) if l is not None else np.eye(4)) @ (np.linalg.inv(Mo[a]) @ Mb)
        return Mo, Mc

    def base_sd(self, key, ms):
        """Signed distance of the original bind-pose vertices of meshes ms to the original bind-pose face mesh (cached)."""
        if key not in self._base:
            # skinned with the bind pose (M_b = inv(common bind)): everything in the common bind space, also the right eye's copies
            Mo, _ = self.solve({bk: np.linalg.inv(self.canon[bk]) for bk in self.skin}, np.eye(4))
            f = self.by[FACE_MESH]
            self._base[key] = SurfaceDist(self.skin_orig(f, Mo), self.oriented[FACE_MESH])(
                np.concatenate([self.skin_orig(self.by[n], Mo) for n in ms]))
        return self._base[key]

    def bind_conv(self, m):
        bp = m.bind.copy()
        R = self.tomesh[m.name]
        for i, b in enumerate(m.bones):
            if b in self.eff and np.any(self.eff[b]):
                bp[i] = bp[i] @ T(-(R @ self.eff[b]))
        return bp

    def skin_orig(self, m, Mo):
        mats = np.array([Mo[b] @ m.bind[i] if b in Mo else np.eye(4) for i, b in enumerate(m.bones)])
        return rig.apply(rig.blend(m, mats), m.v)

    def skin_conv(self, m, Mc, bpc):
        mats = np.array([Mc[b] @ bpc[i] if b in Mc else np.eye(4) for i, b in enumerate(m.bones)])
        v = m.v
        if m.name in self.D:
            h = self.H[m.name][:, None]
            v = v + self.D[m.name] * (self.body * (1 - h) + self.face * h)
        return rig.apply(rig.blend(m, mats), v)


# ---------------------------------------------------------------------------------------------------------------
# geometry: signed distance to a triangle mesh

def _bary(p, a, b, c):
    """Barycentric coordinates (u,v,w) of the point on triangle abc closest to p (Ericson, vectorized)."""
    dot = lambda x, y: np.einsum('ij,ij->i', x, y)
    ab, ac, ap = b - a, c - a, p - a
    d1, d2 = dot(ab, ap), dot(ac, ap)
    bp = p - b
    d3, d4 = dot(ab, bp), dot(ac, bp)
    cp = p - c
    d5, d6 = dot(ab, cp), dot(ac, cp)
    vc, vb, va = d1 * d4 - d3 * d2, d5 * d2 - d1 * d6, d3 * d6 - d5 * d4
    den = va + vb + vc
    den = np.where(np.abs(den) < 1e-30, 1e-30, den)
    v, w = vb / den, vc / den
    s = lambda x: np.where(np.abs(x) < 1e-30, 1e-30, x)
    m = (va <= 0) & (d4 - d3 >= 0) & (d5 - d6 >= 0)                       # edge BC
    ww = (d4 - d3) / s((d4 - d3) + (d5 - d6))
    v, w = np.where(m, 1 - ww, v), np.where(m, ww, w)
    m = (vb <= 0) & (d2 >= 0) & (d6 <= 0)                                 # edge AC
    v, w = np.where(m, 0, v), np.where(m, d2 / s(d2 - d6), w)
    m = (d6 >= 0) & (d5 <= d6)                                            # vertex C
    v, w = np.where(m, 0, v), np.where(m, 1, w)
    m = (vc <= 0) & (d1 >= 0) & (d3 <= 0)                                 # edge AB
    v, w = np.where(m, d1 / s(d1 - d3), v), np.where(m, 0, w)
    m = (d3 >= 0) & (d4 <= d3)                                            # vertex B
    v, w = np.where(m, 1, v), np.where(m, 0, w)
    m = (d1 <= 0) & (d2 <= 0)                                             # vertex A
    v, w = np.where(m, 0, v), np.where(m, 0, w)
    return 1 - v - w, v, w


class SurfaceDist:
    """Signed distance (positive outside, along the vertex normals) from points to a triangle mesh whose
    faces are wound so that cross(b-a, c-a) points outward. Nearest triangle among the k nearest centroids."""

    def __init__(self, V, F, k=16):
        self.V, self.F = V, F
        self.A, self.B, self.C = V[F[:, 0]], V[F[:, 1]], V[F[:, 2]]
        self.vn = sfio.vertex_normals(V, F, 1.0)
        self.tree = cKDTree((self.A + self.B + self.C) / 3)
        self.k = min(k, len(F))

    def __call__(self, P, chunk=20000):
        out = np.empty(len(P))
        for s in range(0, len(P), chunk):
            out[s:s + chunk] = self._q(P[s:s + chunk])
        return out

    def _q(self, P):
        n, k = len(P), self.k
        _, idx = self.tree.query(P, k=k)
        idx = idx.reshape(n, k)
        t = idx.reshape(-1)
        p = np.repeat(P, k, axis=0)
        u, v, w = _bary(p, self.A[t], self.B[t], self.C[t])
        cp = u[:, None] * self.A[t] + v[:, None] * self.B[t] + w[:, None] * self.C[t]
        d = np.linalg.norm(p - cp, axis=1).reshape(n, k)
        j = d.argmin(1)
        r = np.arange(n)
        sel = idx[r, j]
        u, v, w = u.reshape(n, k)[r, j], v.reshape(n, k)[r, j], w.reshape(n, k)[r, j]
        c = cp.reshape(n, k, 3)[r, j]
        nrm = u[:, None] * self.vn[self.F[sel, 0]] + v[:, None] * self.vn[self.F[sel, 1]] + w[:, None] * self.vn[self.F[sel, 2]]
        sg = np.where(np.einsum('ij,ij->i', P - c, nrm) >= 0, 1.0, -1.0)
        return sg * d[r, j]


def oriented_faces(m):
    return m.faces if sfio.winding_sign(m.v, m.faces, m.vn) >= 0 else m.faces[:, ::-1]


# ---------------------------------------------------------------------------------------------------------------
# metrics

def metrics_frame(S, po, pc, pos, tol):
    """po / pc: mesh name -> posed vertices (original / converted). pos: joint name -> root-space position."""
    r = {}
    body = [n for n in S.body_meshes if n in po]
    if not body:
        return r
    cat_o = np.concatenate([po[n] for n in body])
    cat_c = np.concatenate([pc[n] for n in body])
    off = np.cumsum([0] + [len(po[n]) for n in body])
    F = np.concatenate([S.oriented[n] + off[i] for i, n in enumerate(body)])
    names, hsegs, _, _ = rig.label_segments()
    lab = rig.labels(cat_o, hsegs, pos)
    disp = np.linalg.norm(cat_c - cat_o, axis=1)
    r['p99'] = {names[i]: 1000 * float(np.percentile(disp[lab == i], 99)) for i in range(len(names)) if (lab == i).sum() >= 5}
    # teeth and eyeballs outside the face skin
    for key, pat in (('teeth', ('upperTeeth', 'lowerTeeth')), ('sclera', ('sclera',))):
        ms = [n for n in po if any(p in n for p in pat)]
        if not ms or FACE_MESH not in po:
            continue
        res = []
        d0 = S.base_sd(key, ms)
        for world in (po, pc):
            sd = SurfaceDist(world[FACE_MESH], S.oriented[FACE_MESH])
            d = sd(np.concatenate([world[n] for n in ms]))
            x = d - d0   # beyond where the same vertex sits in the original bind pose (the mouth is open: bind is already 'outside')
            res.append((int((d > tol).sum()), 1000 * float(d.max()), int((x > tol).sum()), 1000 * float(x.max())))
        r[key] = res
    # interpenetration proxy: vertices of a part inside the surface of another part
    for key, pairs in (('thigh', THIGH_PAIRS), ('arm', ARM_PAIRS)):
        res = []
        for world in (cat_o, cat_c):
            cnt = 0
            for a, b in pairs:
                ia, ib = names.index(a), names.index(b)
                tri = (lab[F] == ib).all(1)
                if not tri.any() or not (lab == ia).any():
                    continue
                sd = SurfaceDist(world, F[tri])
                cnt += int((sd(world[lab == ia]) < -tol).sum())
            res.append(cnt)
        r[key] = res
    return r


# ---------------------------------------------------------------------------------------------------------------
# synthetic recording

def fk_pose(order, parent, bind_pos, pose, alpha):
    R, P = {}, {}
    for b in order:
        p = parent.get(b)
        Rp = R.get(p, np.eye(3))
        P[b] = bind_pos[b] if p not in P else P[p] + Rp @ (bind_pos[b] - bind_pos[p])
        R[b] = (posetest.rot(np.array(pose[b]) * alpha) @ Rp) if b in pose else Rp
    M = {}
    for b in order:
        m = np.eye(4); m[:3, :3] = R[b]; m[:3, 3] = P[b] - R[b] @ bind_pos[b]
        M[b] = m
    return M


def make_synth(game, path, nframes, fps=10.0):
    meshes, skel, _ = sfio.read_game_export(game)
    meshes = [m for m in meshes if m.bind is not None and len(m.bones)]
    canon = rig.canonical_binds(meshes)
    bind_pos = {b: np.linalg.inv(canon[b])[:3, 3] for b in canon}
    def skin_parent(b):   # nearest skin bone above b (the rig has non-skin drivers in between; they follow their parent rigidly)
        p, n = skel[b]['parent'] if b in skel else None, 0
        while p is not None and p not in canon and n < 256:
            p = skel[p]['parent'] if p in skel else None; n += 1
        return p if p in canon else None
    parent = {b: skin_parent(b) for b in canon}
    # face-rig bones (eyelids, lips, cheeks...) hang under 'Facial', outside the skeleton: the game drives them from the head,
    # so the synthetic pose has them follow the nearest bone of the skeleton proper
    top = [b for b in canon if parent[b] is None]
    anchors = [b for b in canon if parent[b] is not None or b == 'Root_M']
    for b in top:
        if b != 'Root_M':
            parent[b] = min((a for a in anchors if a not in top or a == 'Root_M'),
                            key=lambda a: np.linalg.norm(bind_pos[a] - bind_pos[b]) if not (a == 'Root_M') else 9.0)
    log('synthetic: %d face-rig bones follow their nearest skeleton bone' % (len(top) - 1))
    order, seen = [], set()
    def visit(b):
        if b in seen:
            return
        if parent.get(b) in canon:
            visit(parent[b])
        seen.add(b); order.append(b)
    for b in sorted(canon):
        visit(b)
    pose = dict(posetest.POSE)
    pose['Jaw_M'] = (20, 0, 0)
    bones = sorted(canon)
    frames = []
    for i in range(nframes):
        a = i / max(nframes - 1, 1)
        Msk = fk_pose(order, parent, bind_pos, pose, a)
        M = np.array([Msk[b] @ np.linalg.inv(canon[b]) for b in bones])   # bone -> root, root = common bind space
        rends = [(m.name, m.name, 1, 0) for m in meshes]
        frames.append((i / fps, M, rends))
    # primary renderer: upper body, with a renderer-to-root transform that is not the identity, baked at frame 0
    prim = next((m for m in meshes if m.name == 'reika_upperBody_normal'), meshes[0])
    c, s = np.cos(np.radians(25)), np.sin(np.radians(25))
    r2r = np.array([[c, 0, s, 0.05], [0, 1, 0, 0.02], [-s, 0, c, -0.03], [0, 0, 0, 1.0]])
    Mo = {b: frames[0][1][i] for i, b in enumerate(bones)}
    mats = np.array([Mo[b] @ prim.bind[i] for i, b in enumerate(prim.bones)])
    x = rig.apply(rig.blend(prim, mats), prim.v)
    loc = (np.linalg.inv(r2r) @ np.c_[x, np.ones(len(x))].T).T[:, :3]
    bpath = os.path.splitext(path)[0] + '.bake.obj'
    with open(bpath, 'w', encoding='utf-8', newline='\n') as f:
        f.write('# synthetic bake of %s (vertices in the renderer local space)\n' % prim.name)
        f.write('# rendererToRoot (row-major 4x4, M[r,c]): ' + ' '.join('%.7g' % v for v in r2r.ravel()) + '\n')
        f.write('# rendererLossyScale 1 1 1\n')
        for v in loc:
            f.write('v %.7g %.7g %.7g\n' % tuple(v))
    write_pose(path, bones, frames, (prim.name, prim.name), r2r, '%s (%d vertices)' % (os.path.basename(bpath), len(loc)), fps)
    log('synthetic recording: %d frames, %d bones -> %s (+ %s)' % (nframes, len(bones), path, bpath))


# ---------------------------------------------------------------------------------------------------------------

def verify_bake(S, P, path, po_primary, pname):
    cands = []
    if P.bakefile:
        cands.append(os.path.join(os.path.dirname(os.path.abspath(path)), P.bakefile))
    cands += [os.path.splitext(path)[0] + '.bake.obj', path + '.bake.obj']
    bp = next((c for c in cands if os.path.exists(c)), None)
    if bp is None:
        log('bake: no .bake.obj next to the recording, verification skipped')
        return None
    b, r2r, ls = read_bake(bp)
    if r2r is None:
        r2r = P.r2r
    if len(b) != len(po_primary):
        log('bake: %s has %d vertices, %s %d; skipped' % (os.path.basename(bp), len(b), pname, len(po_primary)))
        return None
    h = lambda v: np.c_[v, np.ones(len(v))]
    R = lambda v: (r2r @ h(v).T).T[:, :3]
    Ri = np.linalg.inv(r2r)
    x = po_primary
    cand = {
        'R@bake vs skin (renderer->root)': R(b) - x,
        'R@(bake/scale) vs skin': R(b / ls) - x,
        'R@(bake*scale) vs skin': R(b * ls) - x,
        'bake vs R@skin (literal)': b - R(x),
        'bake vs inv(R)@skin': b - (Ri @ h(x).T).T[:, :3],
        'bake vs skin (no transform)': b - x,
    }
    rms = {k: 1000 * float(np.sqrt((d ** 2).sum(1).mean())) for k, d in cand.items()}
    lo = min(rms.values())
    best = next(k for k in rms if rms[k] <= lo + 1e-3)
    for k, v in rms.items():
        log('bake RMS %-34s %9.4f mm%s' % (k, v, '  <- best' if k == best else ''))
    log('bake verification (%s, frame 0): best %s = %.4f mm: %s' % (pname, best, rms[best], 'PASS' if rms[best] < 0.5 else 'WARN (> 0.5 mm)'))
    return best, rms[best], rms


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--game', required=True)
    ap.add_argument('--char')
    ap.add_argument('--pose')
    ap.add_argument('--out')
    ap.add_argument('--synth', help='write a synthetic recording (from the export bind pose) to this file and stop')
    ap.add_argument('--synth-frames', type=int, default=8)
    ap.add_argument('--face', type=float, default=1.0)
    ap.add_argument('--body', type=float, default=1.0)
    ap.add_argument('--jointw', type=float, default=None, help='weight of the joint moves (default: --body)')
    ap.add_argument('--stride', type=int, default=1)
    ap.add_argument('--max-frames', type=int, default=0)
    ap.add_argument('--render-frames', type=int, default=4)
    ap.add_argument('--bake-frame', type=int, default=0)
    ap.add_argument('--tol-mm', type=float, default=1.0, help='margin for teeth/eye outside the skin and for interpenetration')
    ap.add_argument('--no-metrics', action='store_true')
    a = ap.parse_args()
    if a.synth:
        make_synth(a.game, a.synth, a.synth_frames)
        return
    if not (a.char and a.pose and a.out):
        ap.error('--char, --pose and --out are required')
    t0 = time.time()
    os.makedirs(a.out, exist_ok=True)
    tol = a.tol_mm / 1000.0
    P = read_pose(a.pose)
    S = Setup(a.game, a.char, a.body, a.face, a.jointw)
    S.oriented = {m.name: oriented_faces(m) for m in S.meshes}
    log('pose: %d frames, %d bones, character %s, scene %s, fps %s, flags %s' % (
        len(P.frames), len(P.bones), P.hdr.get('key', '?'), P.hdr.get('scene', '?'), P.hdr.get('fps', '?'), P.flags))
    if P.flags.get('converted', 'none') != 'none' or P.flags.get('apply') == 'on':
        log('WARN: the recording was made with the conversion applied (converted=%s apply=%s): its bone matrices are the MOVED skeleton, '
            'not the original one this replay needs' % (P.flags.get('converted'), P.flags.get('apply')))
    unknown = [b for b in P.bones if b not in S.skin and b != '-']
    log('bones in the file not skinned by any mesh: %d; skin bones missing in the file: %d' % (len(unknown), len([b for b in S.skin if b not in P.bones])))
    # --- bind pose self-check: the converted skin must be the original plus the morph exactly
    Mb = {b: np.linalg.inv(S.canon[b]) for b in S.skin}
    Mo, Mc = S.solve(Mb, P.r2r)
    worst = 0.0
    for n in S.body_meshes:
        m = S.by[n]
        if n not in S.D:
            continue
        C = rig.to_canonical(m, S.canon)
        h = S.H[n][:, None]
        dd = np.einsum('nij,nj->ni', C[:, :3, :3], S.D[n] * (S.body * (1 - h) + S.face * h))
        err = np.linalg.norm(S.skin_conv(m, Mc, S.bind_conv(m)) - S.skin_orig(m, Mo) - dd, axis=1).max()
        worst = max(worst, err)
    log('bind-pose check (converted - original - D, %s): max %.5f mm %s' % (', '.join(S.body_meshes), worst * 1000, 'PASS' if worst < 5e-5 else 'WARN'))
    S.missing.clear()

    idx = list(range(0, len(P.frames), max(a.stride, 1)))
    if a.max_frames > 0:
        idx = idx[:a.max_frames]
    if not idx:
        raise SystemExit('no frames to replay')
    pick = sorted({idx[int(round(i))] for i in np.linspace(0, len(idx) - 1, min(a.render_frames, len(idx)))})
    bpc = {m.name: S.bind_conv(m) for m in S.meshes}
    names_r = [m.name for m in S.meshes if not any(s in m.name for s in SKIP_RENDER)]
    metric_names = set(S.body_meshes) | {m.name for m in S.meshes if any(p in m.name for p in ('Teeth', 'sclera'))}
    npz = {'frames': np.array(pick), 'times': np.array([P.frames[i]['time'] for i in pick])}
    rows, bake_res = [], None
    rpos = {}
    for fi in idx:
        fr = P.frames[fi]
        Mpose = {}
        for j, b in enumerate(P.bones):
            if b != '-' and b in S.skin:
                mm = np.eye(4); mm[:3, :] = fr['M'][j]
                Mpose[b] = mm
        Mo, Mc = S.solve(Mpose, P.r2r)
        enabled = {mn: en for _, mn, en, _ in fr['R']}
        po, pc = {}, {}
        for m in S.meshes:
            need = (not a.no_metrics and m.name in metric_names) or (fi in pick and m.name in names_r) or \
                   (fi == idx[min(a.bake_frame, len(idx) - 1)] and m.name == P.primary[1])
            if need:
                po[m.name] = S.skin_orig(m, Mo)
                pc[m.name] = S.skin_conv(m, Mc, bpc[m.name])
        if fi == idx[min(a.bake_frame, len(idx) - 1)] and P.primary[1] in po:
            bake_res = verify_bake(S, P, a.pose, po[P.primary[1]], P.primary[1])
        if fi in pick:
            k = pick.index(fi)
            for n in names_r:
                if n in po and enabled.get(n, True):
                    npz['%d|%s|orig' % (k, n)] = po[n].astype(np.float32)
                    npz['%d|%s|conv' % (k, n)] = pc[n].astype(np.float32)
                    npz['faces|' + n] = S.by[n].faces
        if not a.no_metrics:
            pos = {b: Mo[b][:3, 3] for b in Mo}
            r = metrics_frame(S, {n: po[n] for n in metric_names if n in po}, {n: pc[n] for n in metric_names if n in pc}, pos, tol)
            r['frame'], r['time'] = fr['index'], fr['time']
            rows.append(r)
        if len(idx) > 1 and (fi - idx[0]) % 10 == 0:
            log('frame %d / %d' % (fi, len(P.frames)))
    if S.missing:
        log('WARN: %d skin bone(s) missing in the recording (parent\'s change used): %s' % (len(S.missing), ', '.join(sorted(S.missing)[:12])))
    npz['names'] = np.array(sorted({k.split('|')[1] for k in npz if k.count('|') == 2}))
    np.savez_compressed(os.path.join(a.out, 'replay.npz'), **npz)

    # --- metrics file and summary
    lines = ['# replay metrics: %s | game %s | char %s | face %g body %g jointw %g | tol %g mm' % (
        os.path.basename(a.pose), os.path.basename(os.path.normpath(a.game)), os.path.basename(os.path.normpath(a.char)),
        a.face, a.body, S.jw, a.tol_mm)]
    if bake_res:
        lines.append('bake verification: best "%s" RMS %.4f mm (%s)' % (bake_res[0], bake_res[1], 'PASS' if bake_res[1] < 0.5 else 'WARN'))
    for r in rows:
        lines.append('frame %d t=%.3f' % (r['frame'], r['time']))
        lines.append('  p99_displacement_mm ' + ' '.join('%s=%.2f' % kv for kv in sorted(r.get('p99', {}).items())))
        for k, lab in (('teeth', 'teeth_outside_lips'), ('sclera', 'sclera_outside_lids')):
            if k in r:
                lines.append('  %s orig n=%d max=%.2fmm | conv n=%d max=%.2fmm' % (lab, r[k][0][0], r[k][0][1], r[k][1][0], r[k][1][1]))
                lines.append('  %s_beyond_orig_bind orig n=%d max=%.2fmm | conv n=%d max=%.2fmm' % (lab, r[k][0][2], r[k][0][3], r[k][1][2], r[k][1][3]))
        for k, lab in (('thigh', 'thigh_thigh_inside'), ('arm', 'uparm_torso_inside')):
            if k in r:
                lines.append('  %s orig n=%d | conv n=%d' % (lab, r[k][0], r[k][1]))
    with open(os.path.join(a.out, 'replay_metrics.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')

    summ = []
    if rows:
        def worst_of(key, val):
            r = max(rows, key=val)
            return r
        r = worst_of('p99', lambda r: max(r.get('p99', {'-': 0}).values()))
        part = max(r['p99'], key=r['p99'].get)
        summ.append('p99 displacement orig->conv: worst frame %d, %s %.1f mm' % (r['frame'], part, r['p99'][part]))
        for k, lab in (('teeth', 'teeth'), ('sclera', 'sclera')):
            if k in rows[0]:
                r = max(rows, key=lambda r: (r[k][1][2], r[k][1][3]))
                summ.append('%s beyond original bind depth (count/max mm), worst frame %d: orig %d/%.1f conv %d/%.1f  [raw outside skin: orig %d/%.1f conv %d/%.1f]' % (
                    lab, r['frame'], r[k][0][2], r[k][0][3], r[k][1][2], r[k][1][3], r[k][0][0], r[k][0][1], r[k][1][0], r[k][1][1]))
        for k, lab in (('thigh', 'thigh-thigh vertices inside'), ('arm', 'upper arm-torso vertices inside')):
            if k in rows[0]:
                r = max(rows, key=lambda r: r[k][1])
                summ.append('%s: worst frame %d  orig %d  conv %d' % (lab, r['frame'], r[k][0], r[k][1]))
    for s in summ:
        log('summary: ' + s)
    with open(os.path.join(a.out, 'replay_metrics.txt'), 'a', encoding='utf-8', newline='\n') as f:
        f.write('# summary (worst frame by converted value)\n' + '\n'.join('# ' + s for s in summ) + '\n')
    log('done in %.0f s; %s, %s' % (time.time() - t0, os.path.join(a.out, 'replay_metrics.txt'), os.path.join(a.out, 'replay.npz')))


if __name__ == '__main__':
    main()
