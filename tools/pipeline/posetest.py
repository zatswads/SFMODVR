"""Pose test: her original vs the converted shape in a bent pose, skinned the way the game does it
(linear blend skinning, joints moved with matching bind poses). Writes <out>/_check/posetest.npz for blend.py --posetest.

    python posetest.py --game <SfMorph export dir> --out <morph dir>
"""
import argparse
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import sfio  # noqa: E402
import rig  # noqa: E402

POSE = {  # world-axis rotations (degrees) at each joint: the menu's raised leg, bent arm, a little twist
    'Hip_L': (-80, 0, 10), 'Knee_L': (95, 0, 0), 'Ankle_L': (30, 0, 0),
    'Hip_R': (8, 0, 0),
    'Spine1_M': (8, 10, 0), 'Chest_M': (5, 5, 0), 'Neck_M': (-5, -15, 0),
    'Shoulder_L': (0, 0, -25), 'Elbow_L': (0, 70, 0), 'Shoulder_R': (0, 0, 20), 'Elbow_R': (0, -40, 0),
}


def rot(deg):
    x, y, z = np.radians(deg)
    rx = np.array([[1, 0, 0], [0, np.cos(x), -np.sin(x)], [0, np.sin(x), np.cos(x)]])
    ry = np.array([[np.cos(y), 0, np.sin(y)], [0, 1, 0], [-np.sin(y), 0, np.cos(y)]])
    rz = np.array([[np.cos(z), -np.sin(z), 0], [np.sin(z), np.cos(z), 0], [0, 0, 1]])
    return rz @ ry @ rx


def fk(order, parent, bind_pos):
    R, P = {}, {}
    for b in order:
        p = parent.get(b)
        Rp = R.get(p, np.eye(3))
        P[b] = bind_pos[b] if p not in P else P[p] + Rp @ (bind_pos[b] - bind_pos[p])
        R[b] = (rot(POSE[b]) @ Rp) if b in POSE else Rp
    M = {}
    for b in order:
        T = np.eye(4); T[:3, :3] = R[b]; T[:3, 3] = P[b] - R[b] @ bind_pos[b]
        M[b] = T
    return M


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--game', required=True)
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    meshes, skel, _ = sfio.read_game_export(a.game)
    canon = rig.canonical_binds(meshes)
    bind_pos = {b: np.linalg.inv(canon[b])[:3, 3] for b in canon}
    parent = {b: skel[b]['parent'] for b in canon if b in skel}
    order, seen = [], set()
    def visit(b):
        if b in seen:
            return
        if parent.get(b) in canon:
            visit(parent[b])
        seen.add(b); order.append(b)
    for b in canon:
        visit(b)
    joints = {}
    with open(os.path.join(a.out, 'joints.txt'), encoding='utf-8') as f:
        for l in f.read().splitlines():
            if l and not l.startswith('#'):
                t = l.split('\t')
                joints[t[0]] = np.array([float(x) for x in t[3].split()])
    moved = {b: bind_pos[b] + joints.get(b, np.zeros(3)) for b in canon}
    M0 = fk(order, parent, bind_pos)       # her skeleton, posed
    M1 = fk(order, parent, moved)          # moved joints, same rotations
    out = {}
    for m in meshes:
        if any(s in m.name for s in ('uterus', 'rectum', 'Mouth', 'Teeth', 'tongue', 'Glow', 'iris', 'sclera', 'tears')):
            continue
        C = rig.to_canonical(m, canon)
        vc = rig.apply(C, m.v)
        d = np.loadtxt(os.path.join(a.out, m.name + '.morph.txt'), skiprows=3)[:, :3]
        dc = np.einsum('nij,nj->ni', C[:, :3, :3], d)
        def skin(M, v):
            # M already is posed bone x bind pose; with moved joints the bind pivot is the moved one (= bp * T(-offset))
            return rig.apply(rig.blend(m, np.array([M[b] if b in M else np.eye(4) for b in m.bones])), v)
        out[m.name + '|orig'] = skin(M0, vc)
        out[m.name + '|nojoints'] = skin(M0, vc + dc)
        out[m.name + '|joints'] = skin(M1, vc + dc)
        out[m.name + '|faces'] = m.faces
    np.savez_compressed(os.path.join(a.out, '_check', 'posetest.npz'), **out)
    print('posetest written')


if __name__ == '__main__':
    main()
