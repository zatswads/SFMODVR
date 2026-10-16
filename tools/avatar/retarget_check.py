"""Avatar replacement, step 1 (offline): drive a character's own skeleton from her pose and render the two side by side.
It proves the bone map and the retarget maths the plugin would run every frame (docs/SOURCES.md, 'A third mode: avatar
replacement'), on the character's own FBX (DAZ Genesis 9 / 8 / 2 from tools/sources/daz_export.dsa, or any rig
tools/sources/humanoid.py knows) before anything is built for the game.

    blender -b --factory-startup -P retarget_check.py -- --avatar <file.fbx> --game <SfMorph export dir> --out <dir>
            [--pose <PoseRecorder .txt>] [--frames 6] [--ik] [--scale legs|height|none] [--rig auto|g9|g8|g2|...]
            [--no-render]

Her pose: a recording from the Characters tab ('Record 6 s of her animation', BepInEx\\config\\SfMorphs\\poses\\*.txt),
or without --pose a set of test poses built on her exported bind skeleton (raised leg, reach with the arm overhead, a
forearm and an upper-arm twist, curled fingers, eyes and jaw, a wide kneel). Renders start with the avatar in its own rest.

The retarget, per frame (all in one world frame; D_h = her bone's world rotation now x its world rotation at bind):
  aligned rest  A_b: the avatar's rest pose turned, bone by bone from the root, so each limb segment points where hers
                points in her bind pose (upper arm, forearm, hand (two directions), fingers, thigh, shin, foot). The
                trunk, collars, eyes and jaw are NOT aligned: their directions are rig conventions (matching them tipped
                her torso 21 degrees in the old fit, tools/pipeline/rig.py; her Scapula joint sits at the back of the
                ribcage, G9's collar starts at the sternum: aligning it dropped the avatar's clavicles 7 degrees).
  mapped bone   W_b = D_h A_b                        (her world delta on the aligned rest: frames never compared)
  trunk between anchors (G9 spine1/3, neck2)   W_b = D_lo (D_lo^-1 D_hi)^f A_b   (her bend spread over the extra bones)
  twist bones   the bend bone keeps the swing, its twist bones take f = 1/n .. 1 of the twist about the segment
                (upper arm, thigh); forearm twist bones take k/(n+1) of the hand's twist about the forearm
  others        follow their parent at their rest offset (metacarpals, face rig, pectorals, the followers' copies)
  root          her pelvis position + D_root (avatar hip - her pelvis at bind), the avatar scaled by --scale
  --ik          two-bone IK of arms and legs to her wrists and ankles (pole: her elbow / knee), hands and feet keep D_h A

Writes <out>/retarget_check.txt (per frame: segment direction errors, end-effector distances, twist split) and,
unless --no-render, <out>/frame_NN.png (front + side, her left, the avatar right) and <out>/sheet.png.
"""
import math
import os
import sys

import bpy
import numpy as np
from mathutils import Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'pipeline'))
sys.path.insert(0, os.path.join(HERE, '..', 'sources'))
_deps = os.path.join(os.environ.get('LOCALAPPDATA', ''), 'SfMorph', 'pydeps')
if os.path.isdir(_deps):
    sys.path.append(_deps)
import sfio  # noqa: E402
import rig  # noqa: E402
import humanoid  # noqa: E402   (its main() runs only as a script)

ARGS = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []


def arg(name, default=None):
    return ARGS[ARGS.index(name) + 1] if name in ARGS else default


def log(s):
    print('[avatar] ' + s, flush=True)


# Unity (her export: Y up, left-handed, she faces +Z, her left at -X) -> Blender world (Z up): b = P u
P = np.array([[-1.0, 0, 0], [0, 0, -1.0], [0, 1.0, 0]])
BODY = ('reika_upperBody_normal', 'reika_lowerBody_normal', 'reika_foot', 'reika_neck', 'reika_face_a', 'reika_nipple_type_1')
OFF = 0.9   # metres between her and the avatar in the renders


# ---- rotations ------------------------------------------------------------------------------------------------
def orth(m):
    u, _, vt = np.linalg.svd(m)
    r = u @ vt
    if np.linalg.det(r) < 0:
        u[:, -1] *= -1
        r = u @ vt
    return r


def axang(axis, ang):
    a = np.asarray(axis, float)
    n = np.linalg.norm(a)
    if n < 1e-12 or abs(ang) < 1e-12:
        return np.eye(3)
    x, y, z = a / n
    c, s = math.cos(ang), math.sin(ang)
    C = 1 - c
    return np.array([[c + x * x * C, x * y * C - z * s, x * z * C + y * s],
                     [y * x * C + z * s, c + y * y * C, y * z * C - x * s],
                     [z * x * C - y * s, z * y * C + x * s, c + z * z * C]])


def minrot(a, b):
    """Smallest rotation taking direction a to direction b (proper, also for opposite directions)."""
    a = np.asarray(a, float); b = np.asarray(b, float)
    na, nb = np.linalg.norm(a), np.linalg.norm(b)
    if na < 1e-9 or nb < 1e-9:
        return np.eye(3)
    a = a / na; b = b / nb
    v = np.cross(a, b); c = float(np.clip(a @ b, -1, 1))
    if np.linalg.norm(v) < 1e-9:
        if c > 0:
            return np.eye(3)
        p = np.cross(a, [1, 0, 0] if abs(a[0]) < 0.9 else [0, 1, 0])
        return axang(p, math.pi)
    return axang(v, math.atan2(np.linalg.norm(v), c))


def to_quat(R):
    w = math.sqrt(max(0.0, 1 + R[0, 0] + R[1, 1] + R[2, 2])) / 2
    x = math.sqrt(max(0.0, 1 + R[0, 0] - R[1, 1] - R[2, 2])) / 2
    y = math.sqrt(max(0.0, 1 - R[0, 0] + R[1, 1] - R[2, 2])) / 2
    z = math.sqrt(max(0.0, 1 - R[0, 0] - R[1, 1] + R[2, 2])) / 2
    x = math.copysign(x, R[2, 1] - R[1, 2]); y = math.copysign(y, R[0, 2] - R[2, 0]); z = math.copysign(z, R[1, 0] - R[0, 1])
    return np.array([w, x, y, z])


def angle_of(R):
    return math.acos(float(np.clip((np.trace(R) - 1) / 2, -1, 1)))


def power(R, f):
    """R^f: the same axis, f times the angle."""
    q = to_quat(R)
    s = np.linalg.norm(q[1:])
    if s < 1e-12:
        return np.eye(3)
    ang = 2 * math.atan2(s, q[0])
    return axang(q[1:] / s, ang * f)


def twist_about(R, u):
    """Twist part of R about axis u (swing-twist: R = swing @ twist). Returns (twist matrix, signed angle)."""
    q = to_quat(R)
    u = np.asarray(u, float) / max(np.linalg.norm(u), 1e-12)
    p = (q[1:] @ u) * u
    t = np.array([q[0], *p])
    n = np.linalg.norm(t)
    if n < 1e-12:
        return np.eye(3), 0.0
    t /= n
    ang = 2 * math.atan2(t[1:] @ u, t[0])
    ang = (ang + math.pi) % (2 * math.pi) - math.pi
    return axang(u, ang), ang


def deg(a):
    return a * 180.0 / math.pi


def ang_between(a, b):
    na, nb = np.linalg.norm(a), np.linalg.norm(b)
    if na < 1e-9 or nb < 1e-9:
        return 0.0
    return deg(math.acos(float(np.clip(a @ b / (na * nb), -1, 1))))


# ---- her side: skeleton, poses, skinning ------------------------------------------------------------------------
class Her:
    pass


def load_her(game):
    H = Her()
    meshes, skel, _ = sfio.read_game_export(game)
    H.meshes = [m for m in meshes if m.bind is not None and len(m.bones)]
    H.canon = rig.canonical_binds(H.meshes)
    H.l2w = {b: np.linalg.inv(H.canon[b]) for b in H.canon}           # bone -> bind (root) space, Unity
    H.pos = {b: H.l2w[b][:3, 3] for b in H.canon}

    def skin_parent(b):   # nearest skin bone above b (as replay.make_synth)
        p, n = skel[b]['parent'] if b in skel else None, 0
        while p is not None and p not in H.canon and n < 256:
            p = skel[p]['parent'] if p in skel else None; n += 1
        return p if p in H.canon else None
    H.parent = {b: skin_parent(b) for b in H.canon}
    top = [b for b in H.canon if H.parent[b] is None and b != 'Root_M']
    anchors = [b for b in H.canon if H.parent[b] is not None and b not in top]
    for b in top:   # face-rig bones hang outside the skeleton: they follow the nearest skeleton bone (the head)
        H.parent[b] = min(anchors, key=lambda a: np.linalg.norm(H.pos[a] - H.pos[b]))
    H.order, seen = [], set()

    def visit(b):
        if b in seen:
            return
        if H.parent.get(b) in H.canon:
            visit(H.parent[b])
        seen.add(b); H.order.append(b)
    for b in sorted(H.canon):
        visit(b)
    return H


def her_fk(H, pose):
    """Test pose -> per bone skinning matrix S (bind root -> posed root, Unity). pose: bone -> list of ops applied to the
    bone's world rotation in order: ('e', (x, y, z)) world-axis degrees (tools/pipeline/posetest.rot), ('ax', deg, a, b)
    about the bone's current direction a -> b (bind positions carried by the bone's rotation so far)."""
    import posetest
    R, Pp = {}, {}
    for b in H.order:
        p = H.parent.get(b)
        Rp = R.get(p, np.eye(3))
        Pp[b] = H.pos[b] if p not in Pp else Pp[p] + Rp @ (H.pos[b] - H.pos[p])
        cur = Rp
        for op in pose.get(b, ()):
            if op[0] == 'e':
                cur = posetest.rot(np.array(op[1], float)) @ cur
            else:
                _, d, a, c = op
                if a in H.pos and c in H.pos:
                    cur = axang(cur @ (H.pos[c] - H.pos[a]), math.radians(d)) @ cur
        R[b] = cur
    S = {}
    for b in H.order:
        m = np.eye(4); m[:3, :3] = R[b]; m[:3, 3] = Pp[b] - R[b] @ H.pos[b]
        S[b] = m
    return S


def test_poses():
    import posetest
    curl = lambda s, d: {'%sFinger%d_%s' % (f, k, s): [('ax', d, 'PinkyFinger1_' + s, 'IndexFinger1_' + s)]
                         for f in ('Index', 'Middle', 'Ring', 'Pinky') for k in (1, 2, 3)}
    lat = ('Hip_R', 'Hip_L')   # her right -> left (Unity -x): about it, + swings a limb forward
    # arm overhead with the shoulder girdle taking about a third of the elevation (scapulohumeral rhythm)
    reach = {'Scapula_L': [('e', (0, 0, -28))], 'Shoulder_L': [('e', (0, 0, -67))], 'Elbow_L': [('ax', 50) + lat],
             'Wrist_L': [('ax', 75, 'Wrist_L', 'MiddleFinger1_L')],                      # forearm pronation, from the hand
             'Shoulder_R': [('e', (-75, 0, 0)), ('ax', 60, 'Shoulder_R', 'Elbow_R')],    # arm forward + upper-arm twist
             'Elbow_R': [('ax', 85) + lat], 'Wrist_R': [('e', (0, 0, 35))],
             'Neck_M': [('e', (0, 30, 0))], 'Head_M': [('e', (12, 10, 0))],
             'Eye_L': [('e', (0, 25, 0))], 'Eye_R': [('e', (0, 25, 0))], 'Jaw_M': [('e', (18, 0, 0))]}
    reach.update(curl('L', 55))
    for k in (1, 2):
        reach['ThumbFinger%d_L' % k] = [('ax', 30, 'ThumbFinger%d_L' % k, 'IndexFinger1_L')]
    kneel = {'Root_M': [('e', (20, 0, 0))],
             'Hip_L': [('e', (-105, 0, 0)), ('e', (0, 0, -30))], 'Knee_L': [('e', (120, 0, 0))], 'Ankle_L': [('e', (35, 0, 0))],
             'Hip_R': [('e', (-105, 0, 0)), ('e', (0, 0, 30))], 'Knee_R': [('e', (120, 0, 0))], 'Ankle_R': [('e', (35, 0, 0))],
             'Spine1_M': [('e', (15, 0, 8))], 'Chest_M': [('e', (10, -15, 0))], 'Neck_M': [('e', (-25, 0, 0))],
             'Shoulder_L': [('e', (-50, 0, 15))], 'Elbow_L': [('ax', 70) + lat],
             'Shoulder_R': [('e', (-30, 0, -10)), ('ax', -45, 'Shoulder_R', 'Elbow_R')], 'Elbow_R': [('ax', 30) + lat]}
    kneel.update(curl('R', 70))
    pt = {b: [('e', v)] for b, v in posetest.POSE.items()}
    pt['Jaw_M'] = [('e', (20, 0, 0))]
    return [('bind', {}), ('raised leg', pt), ('reach + twists', reach), ('wide kneel', kneel)]


def load_recording(H, path, nframes):
    import replay   # needs scipy (SfMorph pydeps)
    R = replay.read_pose(path)
    idx = np.unique(np.linspace(0, len(R.frames) - 1, max(1, min(nframes, len(R.frames)))).round().astype(int))
    out = []
    for i in idx:
        fr = R.frames[i]
        S = {}
        for j, b in enumerate(R.bones):
            if b in H.canon:
                m = np.eye(4); m[:3, :] = fr['M'][j]
                if np.abs(m[:3, :3]).sum() > 1e-6:
                    S[b] = m @ H.canon[b]
        out.append(('%s frame %d (%.2f s)' % (os.path.basename(path), fr['index'], fr['time']), S))
    log('recording %s: %d frames, %d bones, using %d' % (path, len(R.frames), len(R.bones), len(out)))
    return out


def her_world(H, S):
    """Blender world: D (rotation deltas) and joint positions for every bone in S (missing bones: their parent's)."""
    D, X = {}, {}
    for b in H.order:
        s = S.get(b)
        if s is None:
            p = H.parent.get(b)
            s = S.get(p, np.eye(4)) if p else np.eye(4)
            S[b] = s
        D[b] = P @ orth(s[:3, :3]) @ P.T
        X[b] = P @ (s[:3, :3] @ H.pos[b] + s[:3, 3])
    return D, X


# ---- the avatar ---------------------------------------------------------------------------------------------------
class Av:
    pass


def load_avatar(path, want):
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    ext = os.path.splitext(path)[1].lower()
    if ext == '.fbx':
        bpy.ops.import_scene.fbx(filepath=path, automatic_bone_orientation=False, ignore_leaf_bones=False)
    elif ext in ('.glb', '.gltf'):
        bpy.ops.import_scene.gltf(filepath=path)
    else:
        raise SystemExit('avatar: FBX or GLB/GLTF')
    arms = [o for o in bpy.context.scene.objects if o.type == 'ARMATURE']
    if not arms:
        raise SystemExit('no armature in ' + path)
    A = Av()
    A.arm = max(arms, key=lambda o: len(o.data.bones))
    A.arm.animation_data_clear()
    A.meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH' and
                (o.parent == A.arm or any(m.type == 'ARMATURE' and m.object == A.arm for m in o.modifiers))]
    names = [b.name for b in A.arm.data.bones]
    for o in A.meshes:   # DAZ's follower figures (eyes, mouth, lashes, brows) come bone-parented to their own figure
        if o.parent == A.arm and o.parent_type == 'BONE':   # root bone and without a modifier: skin them like the body
            mw = o.matrix_world.copy()
            o.parent_type = 'OBJECT'; o.parent_bone = ''
            o.matrix_world = mw
        if not any(m.type == 'ARMATURE' for m in o.modifiers):
            m = o.modifiers.new('Armature', 'ARMATURE'); m.object = A.arm
            vg = [g.name for g in o.vertex_groups]
            log('follower mesh %s: armature modifier added (%d groups, %d on main-skeleton names)' % (
                o.name, len(vg), sum(1 for g in vg if g in names and '.' not in g)))
    for o in bpy.context.scene.objects:   # stray objects (a 'Cube' in DAZ's FBX) out of the renders
        if o.type == 'MESH' and o not in A.meshes:
            o.hide_render = True
    A.rid, A.map = humanoid.pick_rig(names, want)
    log('avatar rig %s: %d canonical bones of %d (armature %s, %d bones, %d meshes)' % (
        A.rid, len(A.map), len(humanoid.CANON) * 2 - 5, A.arm.name, len(names), len(A.meshes)))
    return A


def avatar_rest(A):
    bpy.context.view_layer.update()
    mw = np.array(A.arm.matrix_world)
    A.robj = orth(mw[:3, :3])
    A.bones = list(A.arm.data.bones)
    A.byname = {b.name: b for b in A.bones}
    A.G, A.h = {}, {}
    for b in A.bones:
        ml = np.array(b.matrix_local)
        A.G[b.name] = orth(mw[:3, :3] @ ml[:3, :3])
        A.h[b.name] = (mw @ np.r_[ml[:3, 3], 1.0])[:3]
    A.parent = {b.name: (b.parent.name if b.parent else None) for b in A.bones}
    A.order = []

    def walk(b):
        A.order.append(b.name)
        for c in b.children:
            walk(c)
    for b in A.bones:
        if b.parent is None:
            walk(b)


# ---- the map -------------------------------------------------------------------------------------------------------
FINGERS = ('thumb', 'index', 'middle', 'ring', 'pinky')
LIMB_NEXT = {'upperarm': 'forearm', 'forearm': 'hand', 'thigh': 'shin', 'shin': 'foot', 'foot': 'toe'}
TRUNK = ('hips', 'spine', 'chest', 'neck', 'head')
JAW = {'her': 'Jaw_M', 'avatar': ('lowerjaw', 'lowerJaw', 'jaw', 'Jaw')}


def build_map(A, H):
    """role per avatar bone: dict(kind, her, ...). kinds: trunk / dir / frame / inherit (mapped), spread, twist, copy."""
    _, hm = humanoid.pick_rig(list(H.canon), 'as')
    role = {}

    def key_bone(m, key, s):
        return m.get((key, s) if key in humanoid.SIDED else key)
    for key in humanoid.CANON:
        for s in ('L', 'R') if key in humanoid.SIDED else (None,):
            ab, hb = key_bone(A.map, key, s), key_bone(hm, key, s)
            if not ab or not hb or hb not in H.canon:
                continue
            r = dict(her=hb, key=key, side=s)
            if key in TRUNK or key in ('eye', 'collar'):
                r['kind'] = 'trunk'   # change only: their directions are rig conventions
            elif key in LIMB_NEXT:
                r.update(kind='dir', to=(LIMB_NEXT[key], s))
            elif key == 'hand':
                r.update(kind='frame', to=('middle1', s), across=(('pinky1', s), ('index1', s)))
            elif key[:-1] in FINGERS:
                k = int(key[-1])
                r.update(kind='dir', to=(key[:-1] + str(k + 1), s)) if k < 3 else r.update(kind='inherit')
            else:
                r['kind'] = 'inherit'   # toe
            role[ab] = r
    ja = next((n for n in JAW['avatar'] if n in A.byname), None)
    if ja and JAW['her'] in H.canon:
        role[ja] = dict(kind='trunk', her=JAW['her'], key='jaw', side=None)
    # resolve 'to' / 'across' keys to bone pairs (avatar, her)
    for ab, r in role.items():
        for f in ('to',):
            if f in r:
                a2, h2 = key_bone(A.map, *r[f]), key_bone(hm, *r[f])
                r[f] = (a2, h2) if a2 and h2 in H.canon else None
        if 'across' in r:
            pr = [(key_bone(A.map, *x), key_bone(hm, *x)) for x in r['across']]
            r['across'] = pr if all(a and h in H.canon for a, h in pr) else None
    # trunk spread: unmapped bones on the avatar's hips -> head path share their neighbours' bend
    hips, head = A.map.get('hips'), A.map.get('head')
    if hips and head:
        path, b = [], A.byname[head]
        while b is not None:
            path.append(b.name)
            if b.name == hips:
                break
            b = b.parent
        path.reverse()
        anchors = [i for i, n in enumerate(path) if n in role]
        for i0, i1 in zip(anchors, anchors[1:]):
            for i in range(i0 + 1, i1):
                role[path[i]] = dict(kind='spread', lo=role[path[i0]]['her'], hi=role[path[i1]]['her'],
                                     f=(i - i0) / float(i1 - i0), key='trunk', side=None)
    # twist bones: children of a mapped upper arm / thigh (self twist) or forearm (the hand's twist)
    for key, mode in (('upperarm', 'self'), ('thigh', 'self'), ('forearm', 'hand')):
        for s in ('L', 'R'):
            bend = A.map.get((key, s))
            if not bend:
                continue
            tw = [c.name for c in A.byname[bend].children if 'twist' in c.name.lower() and c.name not in role]
            tw.sort(key=lambda n: np.linalg.norm(A.h[n] - A.h[bend]))
            n = len(tw)
            for k, t in enumerate(tw, 1):
                role[t] = dict(kind='twist', mode=mode, bend=bend, key=key, side=s,
                               f=(k / float(n)) if mode == 'self' else (k / float(n + 1)),
                               hand=A.map.get(('hand', s)))
            if tw:
                role[bend]['twisted'] = tw
    # copies: DAZ follower skeletons (head.003 ...) follow the main bone of the same name
    for n in A.byname:
        base = n.rsplit('.', 1)[0]
        if '.' in n and n.rsplit('.', 1)[1].isdigit() and base in A.byname and n not in role:
            role[n] = dict(kind='copy', of=base)
    return role


def align_rest(A, H, role):
    """A_b: the avatar's rest turned so its limb segments point where hers do in her bind pose."""
    hb = lambda b: P @ H.pos[b]
    Al = {}
    for b in A.order:
        p = A.parent[b]
        r = role.get(b, {})
        carry = (Al[p] @ A.G[p].T) if p else np.eye(3)
        K = carry @ A.G[b]
        C = np.eye(3)
        if r.get('kind') in ('dir', 'frame') and r.get('to'):
            a2, h2 = r['to']
            da = carry @ (A.h[a2] - A.h[b]); dh = hb(h2) - hb(r['her'])
            C = minrot(da, dh)
            if r['kind'] == 'frame' and r.get('across'):
                (aa, ha), (ab2, hb2) = r['across']
                xa = C @ carry @ (A.h[ab2] - A.h[aa]); xh = hb(hb2) - hb(ha)
                u = dh / np.linalg.norm(dh)
                xa = xa - (xa @ u) * u; xh = xh - (xh @ u) * u
                if np.linalg.norm(xa) > 1e-6 and np.linalg.norm(xh) > 1e-6:
                    C = minrot(xa, xh) @ C   # a roll about u (both are perpendicular to u)
        Al[b] = C @ K
    return Al


def pose_avatar(A, H, role, Al, D, X, root_off, ik=False):
    """World rotation W and joint position per avatar bone for her pose (D, X), plus the twist report."""
    W = {}
    fixed = {}
    for b, r in role.items():
        k = r['kind']
        if k in ('trunk', 'dir', 'frame', 'inherit'):
            fixed[b] = D[r['her']] @ Al[b]
        elif k == 'spread':
            lo, hi = D[r['lo']], D[r['hi']]
            fixed[b] = lo @ power(lo.T @ hi, r['f']) @ Al[b]
    hips = A.map['hips']
    her_root = role[hips]['her']
    root_pos = X[her_root] + D[her_root] @ root_off

    def resolve():
        Xa = {}
        for b in A.order:
            p = A.parent[b]
            r = role.get(b, {})
            if b in fixed:
                W[b] = fixed[b]
            elif r.get('kind') == 'copy':
                o = r['of']
                W[b] = W[o] @ A.G[o].T @ A.G[b]
            elif p:
                W[b] = W[p] @ Al[p].T @ Al[b]
            else:
                W[b] = Al[b]
            if r.get('kind') == 'copy':
                o = r['of']
                Xa[b] = Xa[o] + W[o] @ A.G[o].T @ (A.h[b] - A.h[o])
            elif p is None:
                Xa[b] = A.h[b]
            else:
                Xa[b] = Xa[p] + W[p] @ A.G[p].T @ (A.h[b] - A.h[p])
        # the root: move the whole tree so the hips land at root_pos
        d = root_pos - Xa[hips]
        for b in Xa:
            Xa[b] = Xa[b] + d
        return Xa
    Xa = resolve()
    if ik:
        for s in ('L', 'R'):
            for a, m, e in (('upperarm', 'forearm', 'hand'), ('thigh', 'shin', 'foot')):
                ba, bm, be = (A.map.get((x, s)) for x in (a, m, e))
                if not (ba and bm and be and ba in role and bm in role and be in role):
                    continue
                t = X[role[be]['her']]; pole = X[role[bm]['her']]
                pa, pm, pe = Xa[ba], Xa[bm], Xa[be]
                la, lb = np.linalg.norm(pm - pa), np.linalg.norm(pe - pm)
                tv = t - pa
                dist = float(np.clip(np.linalg.norm(tv), abs(la - lb) + 1e-4, la + lb - 1e-4))
                tn = tv / max(np.linalg.norm(tv), 1e-9)
                x = (la * la - lb * lb + dist * dist) / (2 * dist)
                hgt = math.sqrt(max(la * la - x * x, 0.0))
                pv = pole - pa; pv = pv - (pv @ tn) * tn
                if np.linalg.norm(pv) < 1e-6:
                    pv = (pm - pa) - ((pm - pa) @ tn) * tn
                pv /= max(np.linalg.norm(pv), 1e-9)
                mid = pa + tn * x + pv * hgt
                end = pa + tn * dist
                R1 = minrot(pm - pa, mid - pa)
                R2 = minrot(R1 @ (pe - pm), end - mid)
                fixed[ba] = R1 @ fixed[ba]
                fixed[bm] = R2 @ R1 @ fixed[bm]
        Xa = resolve()
    # twist: the bend bone keeps its swing, its twist bones share the twist
    rep = []
    for b, r in role.items():
        if 'twisted' not in r:
            continue
        p = A.parent[b]
        to = r['to'][0] if r.get('to') else None
        if not to:
            continue
        dA = Al[b] @ A.G[b].T @ (A.h[to] - A.h[b])   # the segment at the aligned rest, world
        if r['key'] in ('upperarm', 'thigh'):
            carry = W[p] @ Al[p].T
            delta = fixed[b] @ (carry @ Al[b]).T
            u = carry @ dA
            tw, ang = twist_about(delta, u)
            swing = delta @ tw.T
            fixed[b] = swing @ carry @ Al[b]
            for t in r['twisted']:
                fixed[t] = swing @ power(tw, role[t]['f']) @ carry @ Al[t]
            rep.append((b, deg(ang), [(t, deg(ang) * role[t]['f']) for t in r['twisted']]))
        else:   # forearm: the hand's twist about the forearm
            hnd = A.map.get(('hand', r['side']))
            if not hnd or hnd not in fixed:
                continue
            carry = fixed[b] @ Al[b].T
            delta = fixed[hnd] @ (carry @ Al[hnd]).T
            u = carry @ dA
            tw, ang = twist_about(delta, u)
            for t in r['twisted']:
                fixed[t] = power(tw, role[t]['f']) @ carry @ Al[t]
            rep.append((b + ' (hand)', deg(ang), [(t, deg(ang) * role[t]['f']) for t in r['twisted']]))
    Xa = resolve()
    return W, Xa, rep


def apply_pose(A, W, Xa):
    """Set pose bones so the armature shows (W, Xa) (world): matrix_basis from the armature-space matrices."""
    mwi = A.arm.matrix_world.inverted()
    pm = {}
    for b in A.order:
        m = np.eye(4)
        m[:3, :3] = A.robj.T @ W[b]
        m[:3, 3] = np.array(mwi @ Vector(Xa[b]))
        pm[b] = Matrix(m.tolist())
    for b in A.order:
        bone = A.byname[b]
        pb = A.arm.pose.bones[b]
        pb.rotation_mode = 'QUATERNION'
        if bone.parent is None:
            basis = bone.matrix_local.inverted() @ pm[b]
        else:
            rest = bone.parent.matrix_local.inverted() @ bone.matrix_local
            basis = rest.inverted() @ pm[bone.parent.name].inverted() @ pm[b]
        pb.matrix_basis = basis
    bpy.context.view_layer.update()


# ---- metrics --------------------------------------------------------------------------------------------------------
def segments(A, role):
    """(label, avatar from, avatar to, her from, her to) for every mapped limb / finger segment."""
    out = []
    for b, r in role.items():
        if r.get('to') and r['kind'] in ('dir', 'frame'):
            a2, h2 = r['to']
            out.append(('%s%s' % (r['key'], ' ' + r['side'] if r['side'] else ''), b, a2, r['her'], h2))
    return sorted(out)


def measure(A, H, role, X, Xa, X0, Xa0, segs):
    res = {}
    for lab, a1, a2, h1, h2 in segs:
        res[lab] = ang_between(Xa[a2] - Xa[a1], X[h2] - X[h1])
    # trunk: the change of the hips -> head direction from rest
    hips, head = A.map['hips'], A.map['head']
    hh, hd = role[hips]['her'], role[head]['her']
    res['trunk change'] = abs(ang_between(Xa[head] - Xa[hips], Xa0[head] - Xa0[hips]) - ang_between(X[hd] - X[hh], X0[hd] - X0[hh]))
    ends = {}
    for key in ('hand', 'foot', 'head', 'eye'):
        for s in ('L', 'R') if key in humanoid.SIDED else (None,):
            ab = A.map.get((key, s) if s else key)
            if ab in role:
                ends['%s%s' % (key, ' ' + s if s else '')] = 1000 * np.linalg.norm(Xa[ab] - X[role[ab]['her']])
    return res, ends


# ---- rendering ------------------------------------------------------------------------------------------------------
def her_objects(H):
    objs = []
    mat = bpy.data.materials.new('her'); mat.diffuse_color = (0.85, 0.62, 0.55, 1)
    for m in H.meshes:
        if m.name not in BODY or not m.active:
            continue
        me = bpy.data.meshes.new('her_' + m.name)
        me.from_pydata([tuple(v) for v in (P @ m.v.T).T], [], [tuple(f) for f in m.faces])
        me.materials.append(mat)
        o = bpy.data.objects.new('her_' + m.name, me)
        bpy.context.scene.collection.objects.link(o)
        objs.append((o, m))
    log('her meshes for the renders: %s' % ', '.join(m.name for _, m in objs))
    return objs


def skin_her(H, objs, S):
    for o, m in objs:
        mats = np.array([(S[b] @ H.l2w[b] @ m.bind[i]) if b in S else np.eye(4) for i, b in enumerate(m.bones)])
        x = rig.apply(rig.blend(m, mats), m.v)
        o.data.vertices.foreach_set('co', (P @ x.T).T.astype(np.float32).ravel())
        o.data.update()


def setup_render():
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'
    sc.display.shading.light = 'STUDIO'
    sc.display.shading.color_type = 'TEXTURE'
    sc.display.shading.show_cavity = True
    sc.render.resolution_x, sc.render.resolution_y = 900, 900
    sc.render.film_transparent = False
    sc.world = sc.world or bpy.data.worlds.new('w')
    cam = bpy.data.cameras.new('cam'); cam.type = 'ORTHO'; cam.ortho_scale = 2.3
    co = bpy.data.objects.new('cam', cam); sc.collection.objects.link(co); sc.camera = co
    return co


def render_views(A, cam, path_front, path_side):
    from mathutils import Euler
    base = A.arm.location.copy()
    A.arm.location = base + Vector((OFF, 0, 0))
    cam.location = (OFF / 2, -5, 0.85); cam.rotation_euler = Euler((math.radians(90), 0, 0))
    bpy.context.view_layer.update()
    bpy.context.scene.render.filepath = path_front
    bpy.ops.render.render(write_still=True)
    A.arm.location = base + Vector((0, OFF, 0))
    cam.location = (5, OFF / 2, 0.85); cam.rotation_euler = Euler((math.radians(90), 0, math.radians(90)))
    bpy.context.view_layer.update()
    bpy.context.scene.render.filepath = path_side
    bpy.ops.render.render(write_still=True)
    A.arm.location = base
    bpy.context.view_layer.update()


def sheet(paths, labels, out):
    imgs = [bpy.data.images.load(p) for p in paths]
    w, h = imgs[0].size
    rows = len(paths) // 2
    big = np.zeros((rows * h, 2 * w, 4), np.float32)
    for i, im in enumerate(imgs):
        px = np.array(im.pixels[:], np.float32).reshape(h, w, 4)
        r, c = i // 2, i % 2
        big[(rows - 1 - r) * h:(rows - r) * h, c * w:(c + 1) * w] = px
    s = bpy.data.images.new('sheet', 2 * w, rows * h, alpha=True)
    s.pixels = big.ravel()
    s.filepath_raw = out; s.file_format = 'PNG'; s.save()


# ---- main -------------------------------------------------------------------------------------------------------
def main():
    src, game, out = arg('--avatar'), arg('--game'), arg('--out')
    if not (src and game and out):
        raise SystemExit(__doc__)
    os.makedirs(out, exist_ok=True)
    H = load_her(game)
    log('her: %d skin bones, %d meshes (export %s)' % (len(H.canon), len(H.meshes), game))
    A = load_avatar(src, arg('--rig', 'auto'))
    avatar_rest(A)
    if 'hips' not in A.map or 'head' not in A.map:
        raise SystemExit('avatar: no hips / head in the %s table' % A.rid)
    # scale: the avatar's legs (thigh joint -> ankle) or height (head joint) to hers
    mode = arg('--scale', 'legs')
    k = 1.0
    if mode != 'none':
        def h_of(key):
            al = [A.h[A.map[(key, s)]][2] for s in 'LR' if (key, s) in A.map] if key in humanoid.SIDED else [A.h[A.map[key]][2]]
            return float(np.mean(al))
        hm = humanoid.pick_rig(list(H.canon), 'as')[1]
        hh = lambda key: float(np.mean([(P @ H.pos[hm[(key, s)]])[2] for s in 'LR'])) if key in humanoid.SIDED else float((P @ H.pos[hm[key]])[2])
        if mode == 'height':
            k = hh('head') / h_of('head')
        else:
            k = (hh('thigh') - hh('foot')) / (h_of('thigh') - h_of('foot'))
        A.arm.scale = A.arm.scale * k
        avatar_rest(A)
    log('avatar scaled x%.3f (%s): her head joint %.3f m, the avatar\'s %.3f m' % (
        k, mode, (P @ H.pos['Head_M'])[2] if 'Head_M' in H.pos else -1, A.h[A.map['head']][2]))
    role = build_map(A, H)
    kinds = {}
    for r in role.values():
        kinds[r['kind']] = kinds.get(r['kind'], 0) + 1
    log('map: ' + ', '.join('%s %d' % kv for kv in sorted(kinds.items())))
    Al = align_rest(A, H, role)
    her_root = role[A.map['hips']]['her']
    root_off = A.h[A.map['hips']] - P @ H.pos[her_root]

    if arg('--pose'):
        frames = load_recording(H, arg('--pose'), int(arg('--frames', '6')))
        frames.insert(0, ('bind', {}))
    else:
        frames = [(n, her_fk(H, p)) for n, p in test_poses()]
    S0 = her_fk(H, {})
    D0, X0 = her_world(H, dict(S0))
    _, Xa0, _ = pose_avatar(A, H, role, Al, D0, X0, root_off)
    segs = segments(A, role)

    render = '--no-render' not in ARGS
    if render:
        objs = her_objects(H)
        cam = setup_render()
    lines = ['# avatar retarget check: %s driven by her pose' % os.path.basename(src),
             '# avatar rig %s, scale x%.3f (%s), ik %s' % (A.rid, k, mode, 'on' if '--ik' in ARGS else 'off'),
             '# segment: angle (deg) between the avatar segment and hers; trunk change: |avatar - her| change of the hips -> head',
             '# direction from rest; ends: distance (mm) between the avatar joint and hers (proportions differ, so non-zero)', '']
    lines.append('map (avatar <- her): ' + ', '.join('%s<-%s' % (b, r['her']) for b, r in sorted(role.items()) if 'her' in r))
    lines.append('spread: ' + ', '.join('%s %s..%s f=%.2f' % (b, r['lo'], r['hi'], r['f']) for b, r in sorted(role.items()) if r['kind'] == 'spread'))
    lines.append('twist: ' + ', '.join('%s (%s, f=%.2f)' % (b, r['mode'], r['f']) for b, r in sorted(role.items()) if r['kind'] == 'twist'))
    unm = [b for b in A.order if b not in role]
    lines.append('follow their parent (%d): %s' % (len(unm), ', '.join(unm[:80]) + (' ...' if len(unm) > 80 else '')))
    lines.append('')
    worst = 0.0
    paths, labels = [], []
    if render:   # reference: the avatar in its own rest pose (no retarget) beside her bind pose
        skin_her(H, objs, dict(S0))
        for pb in A.arm.pose.bones:
            pb.matrix_basis = Matrix.Identity(4)
        bpy.context.view_layer.update()
        pf = os.path.join(out, 'rest_front.png'); ps = os.path.join(out, 'rest_side.png')
        render_views(A, cam, pf, ps)
        paths += [pf, ps]; labels.append('avatar rest')
        lines.append('frame rest: the avatar in its own rest pose (rendered only)')
    for i, (name, S) in enumerate(frames):
        if not S:
            S = dict(S0)
        D, X = her_world(H, S)
        for b in H.canon:   # bones missing from a recording: their bind skinning matrix
            S.setdefault(b, np.eye(4))
        W, Xa, rep = pose_avatar(A, H, role, Al, D, X, root_off, ik='--ik' in ARGS)
        seg, ends = measure(A, H, role, X, Xa, X0, Xa0, segs)
        mx = max(v for kk, v in seg.items())
        worst = max(worst, mx)
        lines.append('frame %d: %s' % (i, name))
        lines.append('  segments (deg): max %.2f, ' % mx + ', '.join('%s %.2f' % kv for kv in seg.items()))
        lines.append('  ends (mm): ' + ', '.join('%s %.0f' % kv for kv in ends.items()))
        for b, ang, tws in rep:
            lines.append('  twist %s %.1f deg -> %s' % (b, ang, ', '.join('%s %.1f' % t for t in tws)))
        if render:
            skin_her(H, objs, S)
            apply_pose(A, W, Xa)
            pf = os.path.join(out, 'frame_%02d_front.png' % i); ps = os.path.join(out, 'frame_%02d_side.png' % i)
            render_views(A, cam, pf, ps)
            paths += [pf, ps]; labels.append(name)
            log('rendered frame %d (%s)' % (i, name))
    lines.append('')
    lines.append('worst segment angle over all frames: %.2f deg' % worst)
    with open(os.path.join(out, 'retarget_check.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')
    if render and paths:
        sheet(paths, labels, os.path.join(out, 'sheet.png'))
    log('wrote %s (worst segment angle %.2f deg)' % (os.path.join(out, 'retarget_check.txt'), worst))


if __name__ == '__main__':
    main()
