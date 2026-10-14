"""Her AdvancedSkeleton rig vs VaM's Genesis 2 bones: joint map, a common bind space, posing by
linear blend skinning with her own weights, and body-part labels for both figures."""
import numpy as np

SIDES = (('L', 'l'), ('R', 'r'))

# Bones whose rotation is matched to VaM's rest pose: her bone -> list of (her from, her to, vam from, vam to)
# vector pairs (one pair = minimal rotation, two = full frame). Unlisted bones inherit their parent's rotation.
def pose_pairs():
    # The trunk is not rotated: both stand upright, and VaM's spine/collar joints sit along the back while hers
    # run through the middle of the body, so their directions say more about rig conventions than posture
    # (matching them tipped her torso 21 degrees back). The surface fit takes care of posture.
    p = {}
    for s, v in SIDES:
        p['Hip_' + s] = [('Hip_' + s, 'Knee_' + s, v + 'Thigh', v + 'Shin')]
        p['Knee_' + s] = [('Knee_' + s, 'Ankle_' + s, v + 'Shin', v + 'Foot')]
        p['Ankle_' + s] = [('Ankle_' + s, 'Toes_' + s, v + 'Foot', v + 'Toe')]
        p['Shoulder_' + s] = [('Shoulder_' + s, 'Elbow_' + s, v + 'Shldr', v + 'ForeArm')]
        p['Elbow_' + s] = [('Elbow_' + s, 'Wrist_' + s, v + 'ForeArm', v + 'Hand')]
        p['Wrist_' + s] = [('Wrist_' + s, 'MiddleFinger1_' + s, v + 'Hand', v + 'Mid1'),
                           ('PinkyFinger1_' + s, 'IndexFinger1_' + s, v + 'Pinky1', v + 'Index1')]
        for hf, vf in (('Index', 'Index'), ('Middle', 'Mid'), ('Ring', 'Ring'), ('Pinky', 'Pinky'), ('Thumb', 'Thumb')):
            for k in (1, 2):
                p['%sFinger%d_%s' % (hf, k, s)] = [('%sFinger%d_%s' % (hf, k, s), '%sFinger%d_%s' % (hf, k + 1, s),
                                                     '%s%s%d' % (v, vf, k), '%s%s%d' % (v, vf, k + 1))]
    return p


# Anatomical joint centres both rigs agree on: landmarks for the proportion warp.
def landmark_pairs():
    l = [('Head_M', 'head')]
    for s, v in SIDES:
        l += [('Eye_' + s, v + 'Eye'), ('Knee_' + s, v + 'Shin'), ('Ankle_' + s, v + 'Foot'), ('Toes_' + s, v + 'Toe'),
              ('Elbow_' + s, v + 'ForeArm'), ('Wrist_' + s, v + 'Hand')]
        for hf, vf in (('Index', 'Index'), ('Middle', 'Mid'), ('Ring', 'Ring'), ('Pinky', 'Pinky'), ('Thumb', 'Thumb')):
            for k in (1, 2, 3):
                l.append(('%sFinger%d_%s' % (hf, k, s), '%s%s%d' % (v, vf, k)))
    return l


# Body-part labels: segments (from, to) in each rig; a point gets the label of its nearest segment.
# Correspondences may only cross between a label and its neighbours.
def label_segments():
    her, vam, names = [], [], []
    def add(name, hs, vs):
        names.append(name); her.append(hs); vam.append(vs)
    add('torso', [('Root_M', 'Spine1_M'), ('Spine1_M', 'Chest_M'), ('Chest_M', 'Neck_M'), ('Neck_M', 'Head_M'),
                  ('Head_M', '+Head_M'), ('Hip_L', 'Hip_R'), ('Scapula_L', 'Scapula_R')],
        [('hip', 'abdomen2'), ('abdomen2', 'chest'), ('chest', 'neck'), ('neck', 'head'), ('head', '+head'),
         ('lThigh', 'rThigh'), ('lCollar', 'rCollar')])
    for s, v in SIDES:
        add('thigh' + s, [('Hip_' + s, 'Knee_' + s)], [(v + 'Thigh', v + 'Shin')])
        add('shin' + s, [('Knee_' + s, 'Ankle_' + s)], [(v + 'Shin', v + 'Foot')])
        add('foot' + s, [('Ankle_' + s, 'Toes_' + s), ('Toes_' + s, '>Ankle_' + s)], [(v + 'Foot', v + 'Toe'), (v + 'Toe', '>' + v + 'Foot')])
        add('uparm' + s, [('Shoulder_' + s, 'Elbow_' + s)], [(v + 'Shldr', v + 'ForeArm')])
        add('forearm' + s, [('Elbow_' + s, 'Wrist_' + s)], [(v + 'ForeArm', v + 'Hand')])
        add('hand' + s, [('Wrist_' + s, 'IndexFinger1_' + s), ('Wrist_' + s, 'MiddleFinger1_' + s), ('Wrist_' + s, 'RingFinger1_' + s),
                         ('Wrist_' + s, 'PinkyFinger1_' + s), ('Wrist_' + s, 'ThumbFinger1_' + s)],
            [(v + 'Hand', v + 'Index1'), (v + 'Hand', v + 'Mid1'), (v + 'Hand', v + 'Ring1'), (v + 'Hand', v + 'Pinky1'), (v + 'Hand', v + 'Thumb1')])
        for hf, vf in (('Index', 'Index'), ('Middle', 'Mid'), ('Ring', 'Ring'), ('Pinky', 'Pinky'), ('Thumb', 'Thumb')):
            add('f' + hf + s, [('%sFinger1_%s' % (hf, s), '%sFinger2_%s' % (hf, s)), ('%sFinger2_%s' % (hf, s), '%sFinger3_%s' % (hf, s)),
                               ('%sFinger3_%s' % (hf, s), '>%sFinger2_%s' % (hf, s))],
                [('%s%s1' % (v, vf), '%s%s2' % (v, vf)), ('%s%s2' % (v, vf), '%s%s3' % (v, vf)), ('%s%s3' % (v, vf), '>%s%s2' % (v, vf))])
    nb = {n: {n} for n in names}
    def link(a, b):
        nb[a].add(b); nb[b].add(a)
    for s, _ in SIDES:
        link('torso', 'thigh' + s); link('thigh' + s, 'shin' + s); link('shin' + s, 'foot' + s)
        link('torso', 'uparm' + s); link('uparm' + s, 'forearm' + s); link('forearm' + s, 'hand' + s)
        for hf in ('Index', 'Middle', 'Ring', 'Pinky', 'Thumb'):
            link('hand' + s, 'f' + hf + s)
    return names, her, vam, nb


def _seg_point(name, pos, frm):
    """'X' = joint, '+X' = 0.12 m above X (top of head), '>X' = extend the segment frm->? away from X."""
    if name.startswith('+'):
        return pos[name[1:]] + np.array([0, 0.12, 0])
    if name.startswith('>'):
        a = pos[frm]; b = pos[name[1:]]
        return a + (a - b) * 0.7
    return pos[name]


def labels(points, segs, pos):
    """Nearest-segment label per point. segs: list per label of (from, to) names in pos."""
    best = np.full(len(points), np.inf)
    lab = np.zeros(len(points), np.int64)
    for li, sl in enumerate(segs):
        for frm, to in sl:
            if frm not in pos or (to.lstrip('+>') not in pos):
                continue
            a = pos[frm]; b = _seg_point(to, pos, frm)
            ab = b - a
            t = np.clip(((points - a) @ ab) / max(ab @ ab, 1e-12), 0, 1)
            d = np.linalg.norm(points - (a + t[:, None] * ab), axis=1)
            m = d < best
            best[m] = d[m]; lab[m] = li
    return lab


def canonical_binds(meshes):
    """Bind pose per bone name agreed by most skinned vertices (the right eye's iris/sclera are copies of the
    left eye placed only by their own Eye_R bind pose, so mesh spaces are not all the same)."""
    votes = {}
    for m in meshes:
        if m.bind is None or len(m.bones) == 0:
            continue
        cnt = np.bincount(m.wi.ravel(), weights=m.ww.ravel(), minlength=len(m.bones))
        for i, b in enumerate(m.bones):
            lst = votes.setdefault(b, [])
            for e in lst:
                if np.abs(e[0] - m.bind[i]).max() < 1e-4:
                    e[1] += 1; e[2] += cnt[i]
                    break
            else:
                lst.append([m.bind[i], 1, cnt[i]])
    # one vote per mesh, skinned weight breaks ties
    out = {b: max(l, key=lambda e: (e[1], e[2]))[0] for b, l in votes.items()}
    # a side bone with several candidates (Eye_R: 1 mesh at the right eye, 2 copies at the left) takes the one
    # closest to the mirror of its counterpart, when that counterpart is unambiguous
    for b, l in votes.items():
        if len(l) < 2 or b[-2:] not in ('_L', '_R'):
            continue
        o = b[:-2] + ('_R' if b.endswith('_L') else '_L')
        if o not in out or len(votes.get(o, [])) != 1:
            continue
        mp = np.linalg.inv(out[o])[:3, 3] * np.array([-1, 1, 1])
        out[b] = min(l, key=lambda e: np.linalg.norm(np.linalg.inv(e[0])[:3, 3] - mp))[0]
    return out


def blend(m, mats):
    """Per-vertex blended 4x4 of per-mesh-bone matrices mats (b,4,4) with the mesh's weights."""
    return np.einsum('nk,nkij->nij', m.ww, mats[m.wi])


def to_canonical(m, canon):
    """Per-vertex matrix taking this mesh's space to the common bind space."""
    mats = np.array([np.linalg.inv(canon[b]) @ m.bind[i] if b in canon else np.eye(4) for i, b in enumerate(m.bones)])
    return blend(m, mats)


def apply(M, v):
    return np.einsum('nij,nj->ni', M[:, :3, :3], v) + M[:, :3, 3]


def kabsch(src, dst, w=None):
    """Rotation R with R @ src_i ~ dst_i (unit vectors); one pair -> minimal rotation."""
    src = np.asarray(src, float); dst = np.asarray(dst, float)
    src = src / np.linalg.norm(src, axis=1, keepdims=True)
    dst = dst / np.linalg.norm(dst, axis=1, keepdims=True)
    if len(src) == 1:
        a, b = src[0], dst[0]
        v = np.cross(a, b); c = a @ b
        if np.linalg.norm(v) < 1e-9:
            return np.eye(3) if c > 0 else -np.eye(3)
        vx = np.array([[0, -v[2], v[1]], [v[2], 0, -v[0]], [-v[1], v[0], 0]])
        return np.eye(3) + vx + vx @ vx * (1 / (1 + c))
    w = np.ones(len(src)) if w is None else np.asarray(w, float)
    H = (src * w[:, None]).T @ dst
    U, S, Vt = np.linalg.svd(H)
    d = np.sign(np.linalg.det(Vt.T @ U.T))
    return Vt.T @ np.diag([1, 1, d]) @ U.T


def pose_skeleton(canon, skel, vam_pos, log=None):
    """FK from her bind pose into VaM's rest pose. Returns per-bone 4x4 skinning matrices in the common bind
    space (posed = M @ bind) and the posed joint positions."""
    pairs = pose_pairs()
    bind_pos = {b: np.linalg.inv(canon[b])[:3, 3] for b in canon}
    parent = {b: skel[b]['parent'] if b in skel else '-' for b in canon}
    order, seen = [], set()
    def visit(b):
        if b in seen:
            return
        p = parent.get(b, '-')
        if p in canon:
            visit(p)
        seen.add(b); order.append(b)
    for b in canon:
        visit(b)
    R, P = {}, {}
    for b in order:
        p = parent.get(b, '-')
        Rp = R.get(p, np.eye(3)); Pp = P.get(p)
        P[b] = bind_pos[b] if Pp is None else Pp + Rp @ (bind_pos[b] - bind_pos[p])
        R[b] = Rp
        if b in pairs:
            src, dst = [], []
            for hf, ht, vf, vt in pairs[b]:
                if hf in bind_pos and ht in bind_pos and vf in vam_pos and vt in vam_pos:
                    hv = Rp @ (bind_pos[ht] - bind_pos[hf]); vv = vam_pos[vt] - vam_pos[vf]
                    if np.linalg.norm(hv) > 1e-5 and np.linalg.norm(vv) > 1e-5:
                        src.append(hv); dst.append(vv)
                    elif log:
                        log('  pose: skipped %s->%s / %s->%s (zero length)' % (hf, ht, vf, vt))
            if src:
                R[b] = kabsch(src, dst) @ Rp
    M = {}
    for b in canon:
        T = np.eye(4)
        T[:3, :3] = R[b]
        T[:3, 3] = P[b] - R[b] @ bind_pos[b]
        M[b] = T
    return M, P, bind_pos
