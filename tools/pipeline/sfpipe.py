"""SF Body Morphs character pipeline, fit stage: a VaM character's shape onto her meshes.

    python sfpipe.py --game <SfMorph export dir> --vam <SfExport dir> --out <morph dir> [--name Yin]

Runs in Blender's bundled Python (numpy) with scipy on the path (see run.ps1). Writes, in her exact vertex
order, <mesh>.morph.txt for every exported mesh, joints.txt, character.txt, thumb.jpg, and _check/ (report,
stage.npz for the Blender bake/render stage).
"""
import argparse
import os
import shutil
import sys
import time

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import sfio  # noqa: E402
import rig  # noqa: E402
import fit  # noqa: E402

# Her skin surface that is fitted onto the VaM body (mesh -> submesh materials). Everything else (nails and
# nipples are shells over the skin, genitals have an inner canal, eyes, mouth, hair, decals, interior parts)
# follows the fitted skin.
FIT = {
    'reika_upperBody_normal': {'reika_body'},
    'reika_lowerBody_normal': {'reika_body'},
    'reika_foot': {'reika_body', 'reika_foot'},
    'reika_neck': {'reika_body'},
    'reika_face_a': {'reika_face_a'},
}
RIGID = {  # moved without deforming: mesh -> joint it is centred on (None = its own centroid)
    'reika_left_sclera': 'Eye_L', 'reika_left_iris': 'Eye_L', 'reika_leftEye_Glow': 'Eye_L',
    'reika_right_sclera': 'Eye_R', 'reika_right_iris': 'Eye_R', 'reika_rightEye_Glow': 'Eye_R',
    'reika_upperTeeth': None, 'reika_lowerTeeth': None, 'reika_tongue': None,
}
PATCH_RIGID = ('reika_eyelashes_a_1', 'reika_eyelashes_a_2')        # strands: move with the skin patch
ATTACH = ('reika_nipple_type_1', 'reika_nail_normal', 'lewdTattoos_normal', 'pubicHair_typeB_normal')  # lie on the skin
VAM_SKIN = {'Legs', 'Nostrils', 'Lips', 'Face', 'Toenails', 'Fingernails', 'Head', 'Hands', 'Shoulders', 'Neck',
            'Hips', 'Torso', 'Nipples', 'Forearms', 'Feet', 'Ears', 'defaultMat'}

LOG = []


def log(s):
    print(s, flush=True)
    LOG.append(s)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--game', required=True)
    ap.add_argument('--vam', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--name', default=None)
    a = ap.parse_args()
    t0 = time.time()
    chk = os.path.join(a.out, '_check')
    os.makedirs(chk, exist_ok=True)

    meshes, skel, missing = sfio.read_game_export(a.game)
    if not meshes:
        raise SystemExit('meshes.txt lists no meshes in ' + a.game)
    vam = sfio.read_vam_export(a.vam)
    name = a.name or vam.info.get('character', 'Character')
    log('character %s: VaM export %s, game export %s' % (name, a.vam, a.game))
    log('her meshes: %d (%d missing .obj: %s)' % (len(meshes), len(missing), ', '.join(missing) or '-'))
    for m in meshes:
        if len(m.v) != m.expected:
            raise SystemExit('%s: obj has %d vertices, meshes.txt says %d' % (m.name, len(m.v), m.expected))

    # --- common bind space -------------------------------------------------------------------------------
    canon = rig.canonical_binds(meshes)
    for m in meshes:
        m.C = rig.to_canonical(m, canon)
        m.vc = rig.apply(m.C, m.v)
        moved = np.linalg.norm(m.vc - m.v, axis=1).max()
        if moved > 1e-4:
            log('  %s: own mesh space differs from the common bind space by up to %.1f mm (placed via bind poses)' % (m.name, moved * 1000))
    bind_pos = {b: np.linalg.inv(canon[b])[:3, 3] for b in canon}

    # --- VaM scaled to her eye height (keeps her height, so animations and scenes still line up) ---------
    vj = {k: j['pos'] for k, j in vam.joints.items()}
    s = np.mean([bind_pos['Eye_L'][1], bind_pos['Eye_R'][1]]) / np.mean([vj['lEye'][1], vj['rEye'][1]])
    vam_v = vam.v * s
    vj = {k: p * s for k, p in vj.items()}
    log('scale VaM -> her: %.4f (eye height %.3f m -> %.3f m)' % (s, np.mean([vam.joints['lEye']['pos'][1], vam.joints['rEye']['pos'][1]]),
                                                                  bind_pos['Eye_L'][1]))

    # --- pose her into VaM's rest pose -------------------------------------------------------------------
    M, P, _ = rig.pose_skeleton(canon, skel, vj, log)
    for m in meshes:
        mats = np.array([M[b] if b in M else np.eye(4) for b in m.bones])
        m.A = rig.blend(m, mats)
        m.vp = rig.apply(m.A, m.vc)
    lm = [(h, v) for h, v in rig.landmark_pairs() if h in P and v in vj]
    for h, v in lm:
        if not h.startswith(('Index', 'Middle', 'Ring', 'Pinky', 'Thumb')):
            log('  posed %-12s vs %-8s: %s mm' % (h, v, np.round((vj[v] - P[h]) * 1000, 1)))
    # translation from the trunk's joint centres only (eyes, head, knees, ankles); fingers would dominate
    trunk = [(h, v) for h, v in lm if h.split('_')[0] in ('Eye', 'Head', 'Knee', 'Ankle')]
    t = np.median([P[h] - vj[v] for h, v in trunk], axis=0)
    t[1] = 0  # both stand on the floor
    vam_v = vam_v + t
    vj = {k: p + t for k, p in vj.items()}
    log('VaM offset to her: %s mm' % np.round(t * 1000, 1))

    # --- her fitted skin, welded across meshes -----------------------------------------------------------
    sel_v, sel_f = [], []
    base = 0
    for m in meshes:
        groups = FIT.get(m.name)
        m.fit_idx = np.zeros(0, np.int64)
        if not groups:
            continue
        gi = [i for i, g in enumerate(m.groups) if g in groups]
        fmask = np.isin(m.face_group, gi)
        used = np.unique(m.faces[fmask])
        m.fit_idx = used
        remap = -np.ones(len(m.v), np.int64); remap[used] = np.arange(len(used)) + base
        sel_v.append((m, used))
        sel_f.append(remap[m.faces[fmask]])
        base += len(used)
        log('  fit %s: %d of %d vertices (%s)' % (m.name, len(used), len(m.v), ', '.join(m.groups[i] for i in gi)))
    allc = np.concatenate([m.vc[u] for m, u in sel_v])
    allA = np.concatenate([m.A[u] for m, u in sel_v])
    allvn = np.concatenate([np.einsum('nij,nj->ni', m.C[u, :3, :3], m.vn[u]) for m, u in sel_v])
    widx, nw = fit.weld(allc)
    log('fitted skin: %d vertices, %d after welding seams' % (len(allc), nw))
    cnt = np.bincount(widx, minlength=nw).astype(float)
    wc = np.zeros((nw, 3)); np.add.at(wc, widx, allc); wc /= cnt[:, None]
    wA = np.zeros((nw, 4, 4)); np.add.at(wA, widx, allA); wA /= cnt[:, None, None]
    wf = widx[np.concatenate(sel_f)]
    wf = wf[(wf[:, 0] != wf[:, 1]) & (wf[:, 1] != wf[:, 2]) & (wf[:, 2] != wf[:, 0])]
    wp = rig.apply(wA, wc)

    # --- body-part labels and VaM target surfaces --------------------------------------------------------
    names, hsegs, vsegs, nb = rig.label_segments()
    hlab = rig.labels(wp, hsegs, P)
    # mouth: her mouth opening = boundary edges of the face mesh near her teeth; VaM's = its 'Lips' material.
    # Upper and lower lip become separate labels so the fit can't hang one lip on the other.
    face = next(m for m in meshes if m.name == 'reika_face_a')
    teeth = np.concatenate([m.vp for m in meshes if 'Teeth' in m.name])
    tc = teeth.mean(0)
    e = np.sort(np.concatenate([face.faces[:, [0, 1]], face.faces[:, [1, 2]], face.faces[:, [2, 0]]]), axis=1)
    ue, ec = np.unique(e, axis=0, return_counts=True)
    bv = np.unique(ue[ec == 1])
    bv = bv[np.linalg.norm(face.vp[bv] - tc, axis=1) < 0.035]
    her_mouth = face.vp[bv].mean(0)
    lips_v = vam_v[np.unique(vam.faces[vam.face_group == vam.groups.index('Lips')])]
    vam_mouth = np.array([lips_v[:, 0].mean(), 0.5 * (lips_v[:, 1].min() + lips_v[:, 1].max()), lips_v[:, 2].max() - 0.008])
    her_mouth[2] = face.vp[bv][:, 2].max() - 0.004
    log('mouth: hers %s, VaM %s (%d boundary vertices)' % (np.round(her_mouth, 3), np.round(vam_mouth, 3), len(bv)))
    names += ['lipU', 'lipL']
    nb['lipU'] = {'lipU', 'torso'}; nb['lipL'] = {'lipL', 'torso'}; nb['torso'] |= {'lipU', 'lipL'}
    def lip_labels(pts, lab, mc):
        r = np.linalg.norm((pts - mc) * np.array([1, 1.6, 1]), axis=1)
        near = (r < 0.03) & (lab == names.index('torso'))
        lab = lab.copy()
        lab[near & (pts[:, 1] >= mc[1])] = names.index('lipU')
        lab[near & (pts[:, 1] < mc[1])] = names.index('lipL')
        return lab
    vsign = sfio.winding_sign(vam.v, vam.faces, vam.vn)
    skin_g = [i for i, g in enumerate(vam.groups) if g in VAM_SKIN]
    vf = vam.faces[np.isin(vam.face_group, skin_g)]
    vfn, _ = sfio.face_normals(vam_v, vf)
    vfn *= vsign
    vlab = rig.labels(vam_v[vf].mean(1), vsegs, vj)
    hlab = lip_labels(wp, hlab, her_mouth)
    vlab = lip_labels(vam_v[vf].mean(1), vlab, vam_mouth)
    surfaces = {}
    for li, n in enumerate(names):
        allowed = [names.index(x) for x in nb[n]]
        m = np.isin(vlab, allowed)
        surfaces[li] = fit.Surface(vam_v, vf[m], vfn[m]) if m.any() else None
    log('labels: %s' % ', '.join('%s %d/%d' % (n, (hlab == i).sum(), (vlab == i).sum()) for i, n in enumerate(names) if (hlab == i).any()))

    # --- proportion warp on anatomical joint centres, then non-rigid ICP --------------------------------
    src = [P[h] for h, v in lm]; dst = [vj[v] for h, v in lm]
    # surface landmarks: nipple tips (her nipple mesh's front-most point per side, VaM's 'Nipples' material)
    nip = next((m for m in meshes if 'nipple' in m.name), None)
    if nip is not None and 'Nipples' in vam.groups:
        vn_i = np.unique(vam.faces[vam.face_group == vam.groups.index('Nipples')])
        for sgn in (-1, 1):
            hm = nip.vp[np.sign(nip.vp[:, 0]) == sgn]; vm = vam_v[vn_i][np.sign(vam_v[vn_i, 0]) == sgn]
            if len(hm) and len(vm):
                src.append(hm[np.argmax(hm[:, 2])]); dst.append(vm[np.argmax(vm[:, 2])])
                lm.append(('nipple' + ('L' if sgn < 0 else 'R'), 'Nipples'))
    src.append(her_mouth); dst.append(vam_mouth); lm.append(('mouth', 'Lips'))
    src = np.array(src); dst = np.array(dst)
    log('landmarks: %d joint centres, mean distance before warp %.1f mm, max %.1f mm (%s)' % (
        len(lm), 1000 * np.linalg.norm(dst - src, axis=1).mean(), 1000 * np.linalg.norm(dst - src, axis=1).max(),
        lm[int(np.argmax(np.linalg.norm(dst - src, axis=1)))][0]))
    x0 = fit.tps_warp(wp, src, dst)
    hsign = sfio.winding_sign(wc, wf, allvn[np.unique(widx, return_index=True)[1]])
    # stiffer where thin parts sit close together (fingers, toes) and around the eyes (VaM exports can catch the
    # lids mid-blink; her eyelids keep their own opening and follow the face around them)
    stiff = np.ones(nw)
    stiff[np.isin(hlab, [i for i, n in enumerate(names) if n.startswith(('hand', 'fIndex', 'fMiddle', 'fRing', 'fPinky', 'fThumb', 'foot'))])] = 8.0
    for e in ('Eye_L', 'Eye_R'):
        r = np.linalg.norm(wp - P[e], axis=1)
        stiff = np.maximum(stiff, np.clip(12.0 * (1 - (r - 0.016) / 0.012), 1, 12))
    lids = np.zeros(nw, bool)
    for e in ('Eye_L', 'Eye_R'):
        lids |= np.linalg.norm(x0 - dst[[i for i, (h, v) in enumerate(lm) if h == e][0]], axis=1) < 0.021
    log('stiffer fit on %d vertices (fingers, toes, eyelids); %d eyelid vertices keep her eye opening' % ((stiff > 1).sum(), lids.sum()))
    xfit, wmatch, dist = fit.nonrigid_icp(x0, wf, lambda x: sfio.vertex_normals(x, wf, hsign), surfaces, hlab, log, stiff=stiff, nodata=lids)

    # trunk only: the face's lips and lids and the fingers keep the vertex layout the fit gave them
    movable = (hlab == names.index('torso')) & (wp[:, 1] < P['Neck_M'][1])
    xfit = fit.relax_project(xfit, wf, x0, surfaces, hlab, lambda x: sfio.vertex_normals(x, wf, hsign), wmatch > 0, movable, log=log)

    # --- back to her bind pose ---------------------------------------------------------------------------
    wd = rig.apply(np.linalg.inv(wA), xfit) - wc
    log('fitted skin offsets in bind pose: mean %.1f mm, p99 %.1f mm, max %.1f mm' % (
        1000 * np.linalg.norm(wd, axis=1).mean(), 1000 * np.percentile(np.linalg.norm(wd, axis=1), 99), 1000 * np.linalg.norm(wd, axis=1).max()))

    wd, b0, b1 = fit.untangle(wc, wf, wd)
    log('untangle fitted skin: %d flipped triangles -> %d' % (b0, b1))

    # normals of the fitted skin on the welded mesh (one normal per welded vertex: no seams at mesh borders)
    wn0 = sfio.vertex_normals(wc, wf, hsign); wn1 = sfio.vertex_normals(wc + wd, wf, hsign)

    # --- every mesh: offsets (fitted, rigid or following), normal deltas -------------------------------
    pos = 0
    joint_off = {}
    for b in canon:
        joint_off[b] = fit.shell_average(wc, wd, bind_pos[b])
    for e in ('Eye_L', 'Eye_R'):
        if e in bind_pos:
            joint_off[e] = fit.shell_average(wc, wd, bind_pos[e], kmin=96, ratio=2.0)
    results = []
    from scipy.spatial import cKDTree
    skin_tree = cKDTree(wc)
    for m in meshes:
        d = fit.idw(wc, wd, m.vc, k=8)
        if m.name in ATTACH:
            d = fit.surface_attach(m.vc, wc, wf, wd, hsign)
        elif m.name in PATCH_RIGID:
            d = fit.patch_rigid(m.vc, m.faces, wc, wd)
        elif m.name not in RIGID and len(m.fit_idx) < len(m.v):
            near, _ = skin_tree.query(m.vc)
            pinned = near < 2e-5
            pinned[m.fit_idx] = True
            d = fit.smooth_follow(m.vc, m.faces, d, pinned)
        n_a = n_b = None
        if len(m.fit_idx):
            k = widx[pos:pos + len(m.fit_idx)]
            pos += len(m.fit_idx)
            d[m.fit_idx] = wd[k]
        if m.name in RIGID:
            j = RIGID[m.name]
            d[:] = joint_off[j] if j else d.mean(0)
        elif len(m.fit_idx) < len(m.v):
            free = np.ones(len(m.v), bool); free[m.fit_idx] = False
            d2, b0, b1 = fit.untangle(m.vc, m.faces, d)
            d[free] = d2[free]  # fitted vertices keep the welded skin's values (seams stay closed)
            if b0:
                log('  untangle %s: %d flipped triangles -> %d' % (m.name, b0, b1))
        # normal rotation from the shape change, applied to her own normals
        sgn = sfio.winding_sign(m.vc, m.faces, np.einsum('nij,nj->ni', m.C[:, :3, :3], m.vn))
        n_a = sfio.vertex_normals(m.vc, m.faces, sgn); n_b = sfio.vertex_normals(m.vc + d, m.faces, sgn)
        if len(m.fit_idx):
            k = widx[pos - len(m.fit_idx):pos]
            n_a[m.fit_idx] = wn0[k]; n_b[m.fit_idx] = wn1[k]
        vn_c = np.einsum('nij,nj->ni', m.C[:, :3, :3], m.vn)
        vn_c /= np.maximum(np.linalg.norm(vn_c, axis=1, keepdims=True), 1e-12)
        n_new = fit.rotate_normals(vn_c, n_a, n_b)
        Cinv = np.linalg.inv(m.C[:, :3, :3])
        m.d = np.einsum('nij,nj->ni', Cinv, d)                # back to the mesh's own space
        m.dn = np.einsum('nij,nj->ni', Cinv, n_new) - m.vn
        m.dc = d
        m.vp1 = rig.apply(m.A, m.vc + d)                     # posed, morphed (for bake/render)
        if len(m.fit_idx):
            m.vp1[m.fit_idx] = xfit[widx[pos - len(m.fit_idx):pos]]
        results.append(m)

    # --- verification ------------------------------------------------------------------------------------
    rep = verify(results, wmatch, dist, log)
    # the other direction: how well her fitted skin covers the VaM skin, per VaM material
    kd1 = cKDTree(xfit); kd0 = cKDTree(x0)
    rows = []
    for gi in skin_g:
        vi = np.unique(vam.faces[vam.face_group == gi])
        d1, _ = kd1.query(vam_v[vi]); d0, _ = kd0.query(vam_v[vi])
        rows.append('%s %.1f/%.1f (%.1f)' % (vam.groups[gi], 1000 * d1.mean(), 1000 * np.percentile(d1, 95), 1000 * d0.mean()))
    log('  VaM skin -> nearest fitted vertex, mean/p95 mm (before fit): ' + ', '.join(rows))

    # --- write -------------------------------------------------------------------------------------------
    for m in results:
        sfio.write_morph(os.path.join(a.out, m.name + '.morph.txt'), m.name, m.d, m.dn)
    with open(os.path.join(a.out, 'joints.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('# bone\tparent\tbind position\toffset (bind space, Unity axes, metres)\n')
        for b in sorted(joint_off):
            o = joint_off[b]; p = bind_pos[b]
            f.write('%s\t%s\t%.6f %.6f %.6f\t%.6f %.6f %.6f\n' % (b, skel[b]['parent'] if b in skel else '-', p[0], p[1], p[2], o[0], o[1], o[2]))
    thumb = os.path.join(a.vam, 'thumb.jpg')
    if os.path.exists(thumb):
        shutil.copyfile(thumb, os.path.join(a.out, 'thumb.jpg'))
    with open(os.path.join(a.out, 'character.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('# SfMorph character (made by tools/pipeline)\n')
        f.write('name\t%s\n' % name)
        f.write('source\t%s\n' % vam.info.get('character', '?'))
        f.write('made\t%s\n' % time.strftime('%Y-%m-%d %H:%M'))
        f.write('scale\t%.5f\n' % s)
        for m in results:
            f.write('mesh\t%s\t%d\n' % (m.name, len(m.v)))
        for n in FIT:  # her skin: clothes follow the nearest of these vertices in the game
            f.write('skin\t%s\n' % n)
    np.savez_compressed(os.path.join(chk, 'stage.npz'),
                        vam_v=vam_v, vam_f=vam.faces, vam_g=vam.face_group, vam_uv=vam.uv, vam_vn=vam.vn * vsign,
                        vam_groups=np.array(vam.groups), names=np.array([m.name for m in results]),
                        dbg_wp=wp, dbg_x0=x0, dbg_xfit=xfit, dbg_wf=wf, dbg_lab=hlab, dbg_match=wmatch,
                        **{'%s|%s' % (m.name, k): getattr(m, k) for m in results
                           for k in ('vc', 'dc', 'vp', 'vp1', 'faces', 'face_group', 'uv')},
                        **{'%s|groups' % m.name: np.array(m.groups) for m in results})
    log('fit stage done in %.0f s' % (time.time() - t0))
    with open(os.path.join(chk, 'report.txt'), 'w', encoding='utf-8') as f:
        f.write('\n'.join(LOG) + '\n')
    return 0 if rep else 1


def verify(meshes, wmatch, dist, log):
    """Vertex counts/order, flipped and stretched triangles, offset sizes, fit residual."""
    ok = True
    log('verification (bind pose, before -> after):')
    log('  %-28s %7s %8s %8s %8s %7s %9s %9s' % ('mesh', 'verts', 'off mean', 'off p99', 'off max', 'flipped', 'edge p0.1', 'edge p99.9'))
    for m in meshes:
        if len(m.d) != len(m.v) or len(m.d) != m.expected:
            log('  %s: COUNT MISMATCH' % m.name); ok = False
        f = m.faces
        n0, a0 = sfio.face_normals(m.vc, f); n1, a1 = sfio.face_normals(m.vc + m.dc, f)
        good = a0 > 1e-12
        flipped = int(((np.einsum('ij,ij->i', n0, n1) < 0) & good).sum())
        e = np.concatenate([f[:, [0, 1]], f[:, [1, 2]], f[:, [2, 0]]])
        l0 = np.linalg.norm(m.vc[e[:, 0]] - m.vc[e[:, 1]], axis=1)
        l1 = np.linalg.norm((m.vc + m.dc)[e[:, 0]] - (m.vc + m.dc)[e[:, 1]], axis=1)
        r = l1[l0 > 1e-7] / l0[l0 > 1e-7]
        o = np.linalg.norm(m.dc, axis=1)
        if flipped and len(m.v) > 3000:
            fc = m.vc[f[(np.einsum('ij,ij->i', n0, n1) < 0) & good]].mean(1)
            log('    %s flipped at (rounded, cm): %s' % (m.name, ' '.join('(%d %d %d)x%d' % (tuple(k) + (c,)) for k, c in
                zip(*np.unique(np.round(fc * 20) * 5, axis=0, return_counts=True)) if c >= 3)))
        m.stats = dict(flipped=flipped, emin=np.percentile(r, 0.1), emax=np.percentile(r, 99.9), omax=o.max())
        log('  %-28s %7d %6.1fmm %6.1fmm %6.1fmm %7d %9.2f %9.2f' % (m.name, len(m.v), 1000 * o.mean(), 1000 * np.percentile(o, 99),
                                                                    1000 * o.max(), flipped, m.stats['emin'], m.stats['emax']))
        if flipped > max(3, 0.001 * len(f)) or o.max() > 0.12:
            ok = False
    mm = wmatch > 0
    log('  fit residual (matched %.1f%%): mean %.2f mm, p95 %.2f mm, max %.1f mm' % (
        100 * mm.mean(), 1000 * dist[mm].mean(), 1000 * np.percentile(dist[mm], 95), 1000 * dist[mm].max()))
    log('  result: %s' % ('OK' if ok else 'CHECK (see rows above)'))
    return ok


if __name__ == '__main__':
    sys.exit(main())
