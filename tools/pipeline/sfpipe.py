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
HIDE = ('reika_eyeBrow_a',)  # hidden while a character is on (the baked face texture has its brows)
VAM_SKIN = {'Legs', 'Nostrils', 'Lips', 'Face', 'Toenails', 'Fingernails', 'Head', 'Hands', 'Shoulders', 'Neck',
            'Hips', 'Torso', 'Nipples', 'Forearms', 'Feet', 'Ears', 'defaultMat'}

LOG = []


def log(s):
    print(s, flush=True)
    LOG.append(s)


FACE_LM = ('nose', 'chin', 'eyeL', 'eyeR', 'mouth', 'earL', 'earR')


def slice_x_extent(v, f, y, zmin):
    """x extent of the mesh's cross-section at height y, in front of z = zmin (edge/plane intersections)."""
    pts = []
    for i, j in ((0, 1), (1, 2), (2, 0)):
        a, b = v[f[:, i]], v[f[:, j]]
        m = (a[:, 1] - y) * (b[:, 1] - y) < 0
        t = (y - a[m, 1]) / (b[m, 1] - a[m, 1])
        pts.append(a[m] + (b[m] - a[m]) * t[:, None])
    p = np.concatenate(pts)
    p = p[p[:, 2] > zmin]
    return p[:, 0].min(), p[:, 0].max()


def face_metrics(pts, eL, eR, mouth_pts=None, mouth=None, mesh=None):
    """Face measures from the head skin points pts (bind space, Unity axes: +z forward, +y up) and the eye centres.
    mouth: the mouth centre, or mouth_pts: the mouth-opening vertices (centre: mean x, median y, front z - 4 mm). Nose tip =
    most forward point between mouth and eye height, chin = lowest point of the front of the face (both averaged over
    the points within 2 mm of the extreme), head width = x extent of the cross-section at eye height in front of the ears (z > eye - 45 mm)."""
    em = 0.5 * (eL + eR)
    if mouth is None:
        # (median height: her boundary ring has a few stray vertices up at the nostrils)
        mouth = mouth_pts.mean(0); mouth[1] = np.median(mouth_pts[:, 1]); mouth[2] = mouth_pts[:, 2].max() - 0.004
        mrange = (mouth_pts[:, 1].min() - em[1], mouth_pts[:, 1].max() - em[1])
    mrange = locals().get('mrange', (np.nan, np.nan))
    mid = np.abs(pts[:, 0] - em[0]) < 0.03
    nm = mid & (pts[:, 1] < em[1]) & (pts[:, 1] > mouth[1])
    q = pts[nm]; nose = q[q[:, 2] > q[:, 2].max() - 0.002].mean(0)
    cm = mid & (pts[:, 1] < mouth[1]) & (pts[:, 2] > mouth[2] - 0.02)
    q = pts[cm]; chin = q[q[:, 1] < q[:, 1].min() + 0.002].mean(0)
    # lip front: the most forward point of the lips (between the nose and the chin, near the middle), a surface
    # feature that does not depend on how either mesh is cut open at the mouth
    lm_ = (np.abs(pts[:, 0] - em[0]) < 0.012) & (pts[:, 1] < nose[1] - 0.008) & (pts[:, 1] > chin[1] + 0.015)
    q = pts[lm_]; lip = q[q[:, 2] > q[:, 2].max() - 0.0015].mean(0)
    x0, x1 = slice_x_extent(mesh[0], mesh[1], em[1], em[2] - 0.045)
    # lateral extreme at eye height, ears included (her ear landmark)
    ew = np.abs(pts[:, 1] - em[1]) < 0.01
    lat = {sg: pts[ew & (np.sign(pts[:, 0]) == sg)][np.argmax(np.abs(pts[ew & (np.sign(pts[:, 0]) == sg)][:, 0]))] for sg in (-1, 1)}
    return dict(eL=eL, eR=eR, em=em, mouth=mouth, lip=lip, spacing=np.linalg.norm(eL - eR), eye_mouth=np.linalg.norm(em - mouth),
                eye_lip=np.linalg.norm(em - lip),
                nose=nose, chin=chin, width=x1 - x0, lat=lat, mrange=mrange)


def log_face_metrics(tag, m, log):
    log('  face metrics %-26s eye spacing %.1f mm, eye-mouth %.1f mm (lip front %.1f, opening y %.1f..%.1f), nose (%.1f %.1f %.1f) mm, chin (%.1f %.1f %.1f) mm, head width at eyes %.1f mm' % (
        (tag,) + (1000 * m['spacing'], 1000 * m['eye_mouth'], 1000 * m['eye_lip']) + tuple(1000 * np.array(m['mrange'])) + tuple(1000 * (m['nose'] - m['em'])) + tuple(1000 * (m['chin'] - m['em'])) + (1000 * m['width'],)))


def face_landmarks(m, pts, ears=None):
    """Landmarks for the face pre-alignment. Ear centres: the centroid of VaM's 'Ears' per side (ears), or, for her,
    the lateral extreme of the face at eye height."""
    d = dict(nose=m['nose'], chin=m['chin'], eyeL=m['eL'], eyeR=m['eR'], mouth=m['mouth'])
    for sg, k in ((-1, 'earL'), (1, 'earR')):
        d[k] = (ears[np.sign(ears[:, 0]) == sg].mean(0) if len(ears) > 2 else ears[0 if sg < 0 else 1]) if ears is not None else m['lat'][sg]
    return d


def body_metrics(sets, names, bind_pos, nip_y, log):
    """Cross-section width (x) and depth (z) at body landmarks for each of sets = {tag: (points, label per point)}:
    mid-thigh (y 0.65-0.70), widest hip, under-bust, waist and mid-upper-arm."""
    arm = tuple(n for n in names if n.startswith(('uparm', 'forearm', 'hand', 'fIndex', 'fMiddle', 'fRing', 'fPinky', 'fThumb')))
    def trunk(pts, lab):
        return pts[~np.isin(lab, [names.index(n) for n in arm])]
    def wd(p):
        return (p[:, 0].max() - p[:, 0].min(), p[:, 2].max() - p[:, 2].min()) if len(p) > 2 else (np.nan, np.nan)
    def band(p, y, h=0.005):
        return p[np.abs(p[:, 1] - y) <= h]
    def widest(p, y0, y1):
        ys = np.arange(y0, y1, 0.005)
        w = [wd(band(p, y))[0] for y in ys]
        return ys[int(np.nanargmax(w))]
    tr = {k: trunk(*v) for k, v in sets.items()}
    ref = next(iter(sets))
    y_hip = {k: widest(tr[k], 0.78, 1.0) for k in sets}
    y_ub = nip_y - 0.065
    ys = np.arange(y_hip[ref] + 0.05, y_ub - 0.03, 0.005)
    y_waist = ys[int(np.nanargmin([wd(band(tr[ref], y))[0] for y in ys]))]
    rows = []
    def row(label, fn):
        rows.append('  %-34s %s' % (label, '   '.join('%s %s' % (k, fn(k)) for k in sets)))
    fmt = lambda wdv: '%.0fx%.0f' % (1000 * wdv[0], 1000 * wdv[1])
    for sd in ('L', 'R'):
        def thigh(k):
            pts, lab = sets[k]
            p = pts[(lab == names.index('thigh' + sd)) & (pts[:, 1] >= 0.65) & (pts[:, 1] <= 0.70)]
            return fmt(wd(p))
        row('mid-thigh %s (y .65-.70)' % sd, thigh)
    row('widest hip (own y)', lambda k: '%s @%.3f' % (fmt(wd(band(tr[k], y_hip[k]))), y_hip[k]))
    row('under-bust (y %.3f)' % y_ub, lambda k: fmt(wd(band(tr[k], y_ub))))
    row('waist (y %.3f)' % y_waist, lambda k: fmt(wd(band(tr[k], y_waist))))
    for sd in ('L', 'R'):
        a0, a1 = bind_pos['Shoulder_' + sd], bind_pos['Elbow_' + sd]
        ax = (a1 - a0) / np.linalg.norm(a1 - a0); wv = np.cross(ax, [0, 0, 1.0]); wv /= np.linalg.norm(wv)
        def upper_arm(k):
            pts, lab = sets[k]
            p = pts[lab == names.index('uparm' + sd)]
            t = (p - a0) @ ax / np.linalg.norm(a1 - a0)
            p = p[(t > 0.45) & (t < 0.55)]
            if len(p) < 3:
                return 'n/a'
            return fmt((np.ptp(p @ wv), np.ptp(p[:, 2])))
        row('mid-upper-arm %s' % sd, upper_arm)
    log('  body cross-sections, width x depth in mm (%s):' % ' / '.join(sets))
    for r in rows:
        log(r)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--game', required=True)
    ap.add_argument('--vam', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--name', default=None)
    ap.add_argument('--joints', choices=('face', 'all', 'none'), default='face',
                    help="joints.txt offsets: face = only the eyes and the jaw (each capped to 15 mm, the rest stays hers so "
                         "her rig and animations are untouched); all = every bone (shell average of the fitted offsets); none")
    ap.add_argument('--proportions', choices=('hers', 'vam'), default='hers',
                    help="hers: the VaM character's surface and face on her own skeleton (limb, finger and shoulder "
                         "proportions stay hers, so her rig and animations fit); vam: also take VaM's proportions")
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
    fit_off = {}
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
        fit_off[m.name] = base
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
    eye_off, eye_ratio, fm_her, fm_vam = None, {}, None, None
    if a.proportions == 'vam':
        xfit, wmatch, dist = fit.nonrigid_icp(x0, wf, lambda x: sfio.vertex_normals(x, wf, hsign), surfaces, hlab, log, stiff=stiff, nodata=lids)
        # trunk only: the face's lips and lids and the fingers keep the vertex layout the fit gave them
        movable = (hlab == names.index('torso')) & (wp[:, 1] < P['Neck_M'][1])
        xfit = fit.relax_project(xfit, wf, x0, surfaces, hlab, lambda x: sfio.vertex_normals(x, wf, hsign), wmatch > 0, movable, log=log)
        wd = rig.apply(np.linalg.inv(wA), xfit) - wc   # back to her bind pose
        vam_out = vam_v; vam_eyes = np.array([vj['lEye'], vj['rEye']]); bindfit = False
    else:
        # Her proportions: the VaM body is brought onto HER skeleton instead. 1) Each VaM vertex borrows her skinning
        # (blended bone matrices of the nearest vertex of her warped, posed skin, same body part) and is unposed
        # into her A-pose bind pose: no T-pose fit pushed back through her rig (that raised the clavicles).
        # 2) A thin-plate warp moves VaM's joint centres, nipples and mouth onto hers: limb, finger and shoulder
        # proportions become hers. 3) Her bind-pose skin is fitted straight onto that: the offsets are surface
        # shape only, and the joints barely move.
        from scipy.spatial import cKDTree
        vlab_v = rig.labels(vam_v, vsegs, vj)
        vlab_v = lip_labels(vam_v, vlab_v, vam_mouth)
        Av = np.zeros((len(vam_v), 4, 4))
        for li, n in enumerate(names):
            selv = np.where(vlab_v == li)[0]
            if not len(selv):
                continue
            allowed = np.isin(hlab, [names.index(x) for x in nb[n]])
            hi = np.where(allowed)[0]
            if not len(hi):
                hi = np.arange(nw)
            d, k = cKDTree(x0[hi]).query(vam_v[selv], k=4)
            w = 1.0 / (d + 1e-4) ** 2; w /= w.sum(1, keepdims=True)
            Av[selv] = np.einsum('nk,nkij->nij', w, wA[hi][k])
        vam_u = rig.apply(np.linalg.inv(Av), vam_v)
        bsrc, bdst, bl = [], [], []
        for (h, v), sp in zip(lm, src):
            if h in M and v in vj and h != 'Head_M' and not h.startswith('Eye_'):
                bsrc.append(rig.apply(np.linalg.inv(M[h])[None], vj[v][None])[0]); bdst.append(bind_pos[h]); bl.append(h)
        # nipples and mouth: the same VaM points, unposed with their own vertex's skinning
        if nip is not None and 'Nipples' in vam.groups:
            for sgn in (-1, 1):
                ids = vn_i[np.sign(vam_v[vn_i, 0]) == sgn]
                hm = nip.vc[np.sign(nip.vc[:, 0]) == sgn]
                if len(ids) and len(hm):
                    j = ids[np.argmax(vam_v[ids, 2])]
                    bsrc.append(vam_u[j]); bdst.append(hm[np.argmax(hm[:, 2])]); bl.append('nipple')
        hm_b = face.vc[bv].mean(0); hm_b[2] = face.vc[bv][:, 2].max() - 0.004
        vm_b = rig.apply(np.linalg.inv(M['Head_M'])[None], vam_mouth[None])[0]
        bsrc = np.array(bsrc); bdst = np.array(bdst)
        dd = np.linalg.norm(bdst - bsrc, axis=1)
        log('her proportions: VaM unposed into her bind pose; %d landmarks (no head, eyes or mouth: the face stays VaM-like), '
            'mean %.1f mm, max %.1f mm (%s) before the warp' % (len(bl), 1000 * dd.mean(), 1000 * dd.max(), bl[int(np.argmax(dd))]))
        vam_b = fit.tps_warp(vam_u, bsrc, bdst, smoothing=1e-4)
        # Head decoupling: the head is not part of the proportion warp. VaM's head goes on her skeleton rigidly (eye
        # midpoints coincide), blended over the neck from the warped body (weight 0 at her Neck_M, 1 at her Head_M).
        eye_v = {}
        for e, v in (('Eye_L', 'lEye'), ('Eye_R', 'rEye')):
            eye_v[e] = rig.apply(np.linalg.inv(M[e])[None], vj[v][None])[0]
        eye_mid_b = 0.5 * (bind_pos['Eye_L'] + bind_pos['Eye_R'])
        t_eye = eye_mid_b - 0.5 * (eye_v['Eye_L'] + eye_v['Eye_R'])
        yN, yH = bind_pos['Neck_M'][1], bind_pos['Head_M'][1]
        head_names = [names.index(x) for x in ('torso', 'lipU', 'lipL')]
        w_head = np.where(np.isin(vlab_v, head_names), np.clip((vam_u[:, 1] + t_eye[1] - yN) / (yH - yN), 0, 1), 0.0)
        vam_b = vam_b * (1 - w_head)[:, None] + (vam_u + t_eye) * w_head[:, None]
        log('head decoupling: VaM head rigid at her eyes, offset %s mm (eye spacing VaM %.1f mm, hers %.1f mm); neck blend between y %.3f and %.3f, %d VaM vertices fully rigid' % (
            np.round(t_eye * 1000, 1), 1000 * np.linalg.norm(eye_v['Eye_L'] - eye_v['Eye_R']),
            1000 * np.linalg.norm(bind_pos['Eye_L'] - bind_pos['Eye_R']), yN, yH, (w_head >= 1).sum()))
        eye_c = {e: eye_v[e] + t_eye for e in eye_v}                      # VaM eye centres in her bind space
        eye_off = {e: eye_c[e] - bind_pos[e] for e in eye_c}
        vm_a = vm_b + t_eye
        # eyeball radii: VaM's 'Sclera' vertices around their eye (VaM's rest pose, her scale), hers: her sclera meshes
        scl = vam_v[np.unique(vam.faces[vam.face_group == vam.groups.index('Sclera')])]
        r_vam = np.mean([np.linalg.norm(scl[np.sign(scl[:, 0]) == sg] - vj[k], axis=1).mean() for sg, k in ((-1, 'lEye'), (1, 'rEye'))])
        r_her = {}
        for nm, e in (('reika_left_sclera', 'Eye_L'), ('reika_right_sclera', 'Eye_R')):
            ms = next((m for m in meshes if m.name == nm), None)
            if ms is not None:
                r_her[e] = np.linalg.norm(ms.vc - bind_pos[e], axis=1).mean()
        eye_ratio = {e: r_vam / r for e, r in r_her.items()}
        log('eyeball radius: VaM %.2f mm, hers %s mm (scale %s); eye offsets %s mm' % (
            1000 * r_vam, {e: round(float(1000 * r), 2) for e, r in r_her.items()}, {e: round(float(r), 3) for e, r in eye_ratio.items()},
            {e: np.round(o * 1000, 1).tolist() for e, o in eye_off.items()}))
        # face metrics source points: her face mesh (welded), VaM's head skin
        used = face.fit_idx
        face_w = widx[fit_off['reika_face_a'] + np.arange(len(used))]
        mouth_w = widx[fit_off['reika_face_a'] + np.searchsorted(used, bv)]
        vhead = np.unique(vam.faces[np.isin(vam.face_group, [vam.groups.index(g) for g in ('Face', 'Nostrils', 'Lips', 'Ears', 'Head')])])
        vears = np.unique(vam.faces[vam.face_group == vam.groups.index('Ears')])
        fm_her = face_metrics(wc[face_w], bind_pos['Eye_L'], bind_pos['Eye_R'], mouth_pts=wc[mouth_w], mesh=(wc, wf))
        # VaM's mouth opening: its lip vertices that touch the inner mouth (the same kind of ring as her boundary vertices)
        lip_i = np.unique(vam.faces[vam.face_group == vam.groups.index('Lips')])
        in_i = np.unique(vam.faces[vam.face_group == vam.groups.index('InnerMouth')])
        ring = lip_i[cKDTree(vam_b[in_i]).query(vam_b[lip_i])[0] < 0.0015]
        if len(ring) >= 10:
            vm_a = vam_b[ring].mean(0); vm_a[2] = vam_b[ring][:, 2].max() - 0.004
        log('VaM mouth opening: %d ring vertices, centre %s mm from the eye midpoint' % (len(ring), np.round(1000 * (vm_a - eye_mid_b), 1)))
        fm_vam = face_metrics(vam_b[vhead], eye_c['Eye_L'], eye_c['Eye_R'], mouth_pts=vam_b[ring] if len(ring) >= 10 else None,
                              mouth=None if len(ring) >= 10 else vm_a, mesh=(vam_b, vf))
        vfn_b, _ = sfio.face_normals(vam_b, vf)
        vfn_b *= vsign
        surfaces = {}
        for li, n in enumerate(names):
            mm = np.isin(vlab, [names.index(x) for x in nb[n]])
            surfaces[li] = fit.Surface(vam_b, vf[mm], vfn_b[mm]) if mm.any() else None
        stiff_b = np.ones(nw)
        stiff_b[np.isin(hlab, [i for i, n in enumerate(names) if n.startswith(('hand', 'fIndex', 'fMiddle', 'fRing', 'fPinky', 'fThumb', 'foot'))])] = 8.0
        for e in ('Eye_L', 'Eye_R'):   # a soft ring round the eyes (the lids are fitted now)
            r = np.linalg.norm(wc - bind_pos[e], axis=1)
            stiff_b = np.maximum(stiff_b, np.clip(3.0 * (1 - (r - 0.016) / 0.012), 1, 3))
        # her mouth's inside (the face mesh continues behind the lips): faces down like the underside of a chin
        # (inside, normals face the head's centre line; the chin's skin faces away from it)
        wn_b = sfio.vertex_normals(wc, wf, hsign)
        axis = np.c_[np.zeros(nw), wc[:, 1], np.full(nw, hm_b[2] - 0.035)]
        mouth_in = (np.linalg.norm(wc - hm_b, axis=1) < 0.07) & (np.einsum('ij,ij->i', wn_b, wc - axis) < 0) & (wc[:, 2] < hm_b[2])
        # and the whole bag behind the lips, down towards the throat (the chin's skin is lower or further forward)
        mouth_in |= ((wc[:, 2] < hm_b[2] - 0.012) & (np.abs(wc[:, 0] - hm_b[0]) < 0.035)
                     & (wc[:, 1] > hm_b[1] - 0.035) & (wc[:, 1] < hm_b[1] + 0.03) & (wc[:, 2] > hm_b[2] - 0.11))
        nodata = mouth_in.copy()
        # VaM exports can catch the lids mid-blink. Measure each eye's opening on the VaM skin (the largest
        # skin-free gap on a vertical line through the eye, in front of the eyeball); if it's mostly closed, her
        # own lids keep their opening (and the report says to re-export with blinking off), else VaM's lids are fitted.
        if 'Sclera' in vam.groups:
            scl = np.unique(vam.faces[vam.face_group == vam.groups.index('Sclera')])
            cen_f = vam_b[vf].mean(1)
            apertures = []
            for sgn in (-1, 1):
                sv = vam_b[scl][np.sign(vam_b[scl, 0]) == sgn]
                if not len(sv):
                    continue
                ec = sv.mean(0); er = np.linalg.norm(sv - ec, axis=1).mean()
                m = (np.abs(cen_f[:, 0] - ec[0]) < 0.002) & (cen_f[:, 2] > ec[2] + 0.5 * er) & (np.abs(cen_f[:, 1] - ec[1]) < 0.012)
                ys = np.sort(np.r_[cen_f[m, 1] - ec[1], -0.012, 0.012])
                gaps = np.diff(ys)
                apertures.append(float(gaps.max()) if len(gaps) else 0.0)
            ap = min(apertures) if apertures else 1.0
            closed = ap < 0.006
            log('VaM eye opening: %s mm -> %s' % (', '.join('%.1f' % (1000 * a) for a in apertures),
                'mostly closed (caught mid-blink?): her own lids keep their opening. Re-export from VaM with the SfExport '
                'plugin (it turns blinking off) to get the character\'s eye shape too' if closed else 'open: the lids are fitted'))
            if closed:
                for e in ('Eye_L', 'Eye_R'):
                    r = np.linalg.norm(wc - bind_pos[e], axis=1)
                    nodata |= r < 0.021
                    stiff_b = np.maximum(stiff_b, np.clip(12.0 * (1 - (r - 0.016) / 0.012), 1, 12))
        # VaM's shoulders come from a T-pose (trapezius bunched up with the arms raised) and don't come down with
        # the arms: her own clavicle/shoulder shape is held by extra stiffness (the data still pulls), VaM's takes
        # over smoothly around it
        n_sh = np.zeros(nw, bool)
        for sdn in ('Shoulder_L', 'Shoulder_R', 'Scapula_L', 'Scapula_R'):
            r = np.linalg.norm(wc - bind_pos[sdn], axis=1)
            n_sh |= r < 0.11
            stiff_b = np.maximum(stiff_b, np.clip(6.0 * (1 - (r - 0.11) / 0.06), 1, 6))
        log('%d mouth-interior vertices keep her shape (no data); %d shoulder vertices stiffer (up to 6, data on)' % (mouth_in.sum(), n_sh.sum()))
        # Face pre-alignment: her head (only) is warped onto VaM's face landmarks first, so the ICP starts with the
        # nose, chin, ears and mouth near their targets (its smoothness term anchors to the aligned shape).
        lm_her = face_landmarks(fm_her, wc[face_w], ears=None)
        # her ear landmark is the lateral extreme of the face at eye height, so VaM's is the lateral extreme of its
        # 'Ears' at eye height too (their centroid sits lower and further in: it squashed her ears by 3 cm)
        ve = vam_b[vears]; ve = ve[np.abs(ve[:, 1] - eye_mid_b[1]) < 0.01]
        ears_dst = np.array([ve[np.argmax(np.abs(ve[:, 0]) * (np.sign(ve[:, 0]) == sg))] for sg in (-1, 1)])
        lm_vam = face_landmarks(fm_vam, vam_b[vhead], ears=ears_dst)
        lsrc = np.array([lm_her[k] for k in FACE_LM]); ldst = np.array([lm_vam[k] for k in FACE_LM])
        log('face pre-alignment landmarks, her -> VaM (mm): ' + ', '.join('%s %.1f' % (k, 1000 * np.linalg.norm(lm_vam[k] - lm_her[k])) for k in FACE_LM))
        pre = fit.tps_warp(wc, lsrc, ldst, smoothing=1e-4)
        w_her = np.where(np.isin(hlab, head_names), np.clip((wc[:, 1] - yN) / (yH - yN), 0, 1), 0.0)
        x_start = wc + (pre - wc) * w_her[:, None]
        min_dot = np.where(np.linalg.norm(wc - eye_mid_b, axis=1) < 0.12, 0.2, 0.5)
        xfit, wmatch, dist = fit.nonrigid_icp(x_start, wf, lambda x: sfio.vertex_normals(x, wf, hsign), surfaces, hlab, log, stiff=stiff_b, nodata=nodata, min_dot=min_dot)
        movable = (hlab == names.index('torso')) & (wc[:, 1] < bind_pos['Neck_M'][1])
        xfit = fit.relax_project(xfit, wf, wc, surfaces, hlab, lambda x: sfio.vertex_normals(x, wf, hsign), wmatch > 0, movable, log=log)
        wd = xfit - wc
        vam_out = vam_b; vam_eyes = np.array([eye_c['Eye_L'], eye_c['Eye_R']]); bindfit = True
    log('fitted skin offsets in bind pose: mean %.1f mm, p99 %.1f mm, max %.1f mm' % (
        1000 * np.linalg.norm(wd, axis=1).mean(), 1000 * np.percentile(np.linalg.norm(wd, axis=1), 99), 1000 * np.linalg.norm(wd, axis=1).max()))

    wd, b0, b1 = fit.untangle(wc, wf, wd, iters=1000, rings=5)
    log('untangle fitted skin: %d flipped triangles -> %d' % (b0, b1))

    # normals of the fitted skin on the welded mesh (one normal per welded vertex: no seams at mesh borders)
    wn0 = sfio.vertex_normals(wc, wf, hsign); wn1 = sfio.vertex_normals(wc + wd, wf, hsign)

    # --- every mesh: offsets (fitted, rigid or following), normal deltas -------------------------------
    pos = 0
    joint_off = {b: np.zeros(3) for b in canon}
    if a.joints == 'all':
        for b in canon:
            joint_off[b] = fit.shell_average(wc, wd, bind_pos[b])
    if eye_off is None:   # vam proportions: the eye joints follow the fitted skin around them
        eye_off = {e: fit.shell_average(wc, wd, bind_pos[e], kmin=96, ratio=2.0) for e in ('Eye_L', 'Eye_R') if e in bind_pos}
    def cap(o, mx=0.015):
        n = np.linalg.norm(o)
        return o if n <= mx else o * (mx / n)
    if a.joints == 'all':
        for e in eye_off:
            joint_off[e] = eye_off[e]
    elif a.joints == 'face':
        for e in eye_off:
            joint_off[e] = cap(eye_off[e])
        if 'Jaw_M' in bind_pos:
            joint_off['Jaw_M'] = cap(fit.shell_average(wc, wd, bind_pos['Jaw_M']))
    log('joints (%s): %s' % (a.joints, ', '.join('%s %s mm' % (b, np.round(o * 1000, 1).tolist()) for b, o in sorted(joint_off.items()) if np.any(o)) or 'all zero'))
    if eye_ratio and eye_off:
        # her lids against the eyeball: the fitted lid vertices must stay outside it
        for e in ('Eye_L', 'Eye_R'):
            c = bind_pos[e] + eye_off[e]
            lid = np.linalg.norm(wc - bind_pos[e], axis=1) < 0.03
            dl = np.linalg.norm(wc[lid] + wd[lid] - c, axis=1)
            log('  %s: eyeball radius %.2f mm (VaM), nearest fitted lid vertex %.2f mm from the centre (%d vertices within 30 mm)' % (
                e, 1000 * eye_ratio[e] * r_her[e], 1000 * dl.min(), lid.sum()))
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
            if j in eye_off:   # eyeball: moves to VaM's eye centre and takes its size
                d[:] = eye_off[j] + (eye_ratio.get(j, 1.0) - 1.0) * (m.vc - bind_pos[j])
            else:
                d[:] = d.mean(0)
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
        if bindfit:                                          # bind pose (for bake/render)
            m.vp = m.vc
            m.vp1 = m.vc + d
        else:                                                # VaM's rest pose
            m.vp1 = rig.apply(m.A, m.vc + d)
            if len(m.fit_idx):
                m.vp1[m.fit_idx] = xfit[widx[pos - len(m.fit_idx):pos]]
        results.append(m)

    # --- verification ------------------------------------------------------------------------------------
    body = None
    if bindfit:
        fm_fit = face_metrics((wc + wd)[face_w], bind_pos['Eye_L'] + eye_off['Eye_L'], bind_pos['Eye_R'] + eye_off['Eye_R'],
                              mouth_pts=(wc + wd)[mouth_w], mesh=(wc + wd, wf))
        log('face metrics (head, eye midpoint at her eyes; positions relative to it):')
        log_face_metrics('Reika bind face', fm_her, log)
        log_face_metrics('VaM head (rigid, aligned)', fm_vam, log)
        log_face_metrics('fitted result', fm_fit, log)
        dif = lambda k: np.linalg.norm(fm_fit[k] - fm_vam[k]) if np.ndim(fm_fit[k]) else abs(fm_fit[k] - fm_vam[k])
        log('  fitted - VaM: eye spacing %.1f mm, eye-mouth %.1f mm (lip front %.1f), nose %.1f mm, chin %.1f mm, head width %.1f mm' % (
            1000 * dif('spacing'), 1000 * dif('eye_mouth'), 1000 * dif('eye_lip'), 1000 * dif('nose'), 1000 * dif('chin'), 1000 * dif('width')))
        nip_y = nip.vc[:, 1].mean() if nip is not None else bind_pos['Chest_M'][1]
        skin_vi = np.unique(vf.ravel())
        vlab_vb = vlab_v
        sets = {'Reika': (wc, hlab), 'fitted': (wc + wd, hlab), 'VaM-warped': (vam_out[skin_vi], vlab_vb[skin_vi])}
        body = lambda: body_metrics(sets, names, bind_pos, nip_y, log)
    rep = verify(results, wmatch, dist, log, body)
    # the other direction: how well her fitted skin covers the VaM skin, per VaM material
    kd1 = cKDTree(xfit); kd0 = cKDTree(wc if bindfit else x0)
    rows = []
    for gi in skin_g:
        vi = np.unique(vam.faces[vam.face_group == gi])
        d1, _ = kd1.query(vam_out[vi]); d0, _ = kd0.query(vam_out[vi])
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
    # Her whole fitted skin (welded) and every bone's bind pose in the common bind space: what clothes, outfit body
    # variants (uniform_upperBody_normal replaces her upper body when she's dressed) and any unconverted mesh follow
    # in the game, whichever of her meshes the scene uses.
    with open(os.path.join(a.out, 'skin.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('# her skin, welded, common bind space: position, normal, offset, normal delta\n')
        f.write('vertices %d\n' % nw)
        dn = wn1 - wn0
        for i in range(nw):
            f.write('%.6g %.6g %.6g %.4g %.4g %.4g %.6g %.6g %.6g %.4g %.4g %.4g\n' % (tuple(wc[i]) + tuple(wn0[i]) + tuple(wd[i]) + tuple(dn[i])))
    with open(os.path.join(a.out, 'binds.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('# bone\tbind pose (mesh -> bone), 16 values row by row, common bind space\n')
        for b in sorted(canon):
            f.write('%s\t%s\n' % (b, ' '.join('%.7g' % x for x in canon[b].ravel())))
    thumb = os.path.join(a.vam, 'thumb.jpg')
    if os.path.exists(thumb):
        shutil.copyfile(thumb, os.path.join(a.out, 'thumb.jpg'))
    with open(os.path.join(a.out, 'character.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('# SfMorph character (made by tools/pipeline)\n')
        f.write('name\t%s\n' % name)
        f.write('source\t%s\n' % vam.info.get('character', '?'))
        f.write('made\t%s\n' % time.strftime('%Y-%m-%d %H:%M'))
        f.write('scale\t%.5f\n' % s)
        f.write('proportions\t%s\n' % a.proportions)
        for m in results:
            f.write('mesh\t%s\t%d\n' % (m.name, len(m.v)))
        for n in FIT:  # her skin: clothes follow the nearest of these vertices in the game
            f.write('skin\t%s\n' % n)
        for n in HIDE:  # her anime brows: VaM characters carry their brows in the face texture
            f.write('hide\t%s\n' % n)
    np.savez_compressed(os.path.join(chk, 'stage.npz'),
                        vam_v=vam_out, vam_eyes=vam_eyes, vam_f=vam.faces, vam_g=vam.face_group, vam_uv=vam.uv, vam_vn=vam.vn * vsign,
                        vam_groups=np.array(vam.groups), names=np.array([m.name for m in results]),
                        dbg_wp=wp, dbg_x0=x0, dbg_xfit=xfit, dbg_wf=wf, dbg_lab=hlab, dbg_match=wmatch,
                        **{'%s|%s' % (m.name, k): getattr(m, k) for m in results
                           for k in ('vc', 'dc', 'vp', 'vp1', 'faces', 'face_group', 'uv')},
                        **{'%s|groups' % m.name: np.array(m.groups) for m in results})
    log('fit stage done in %.0f s' % (time.time() - t0))
    with open(os.path.join(chk, 'report.txt'), 'w', encoding='utf-8') as f:
        f.write('\n'.join(LOG) + '\n')
    return 0 if rep else 1


def verify(meshes, wmatch, dist, log, extra=None):
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
    if extra:
        extra()
    log('  result: %s' % ('OK' if ok else 'CHECK (see rows above)'))
    return ok


if __name__ == '__main__':
    sys.exit(main())
