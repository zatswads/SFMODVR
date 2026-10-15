"""SF Body Morphs character pipeline, fit stage: a VaM character's shape onto her meshes.

    python sfpipe.py --game <SfMorph export dir> --vam <SfExport dir> --out <morph dir> [--name Yin]

Runs in Blender's bundled Python (numpy) with scipy on the path (see run.ps1). Writes, in her exact vertex
order, <mesh>.morph.txt for every exported mesh, joints.txt, character.txt, thumb.jpg, and _check/ (report,
stage.npz for the Blender bake/render stage).
"""
import argparse
import os
import re
import shutil
import sys
import time
import traceback

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
# VaM skin groups the fit cannot do without (a missing one stops the run); the other VAM_SKIN groups are optional
# (the step that uses them is skipped with a WARN): Nostrils, Lips, Toenails, Fingernails, Nipples, Ears, defaultMat.
VAM_SKIN_REQUIRED = {'Legs', 'Face', 'Head', 'Hands', 'Shoulders', 'Neck', 'Hips', 'Torso', 'Forearms', 'Feet'}
VAM_FILES = ('body.obj', 'joints.txt', 'materials.txt')
# her bones that sfpipe.py reads directly (the rest of her rig is handled by rig.py, which skips what is missing)
HER_BONES = ('Eye_L', 'Eye_R', 'Head_M', 'Neck_M', 'Chest_M', 'Shoulder_L', 'Shoulder_R', 'Scapula_L', 'Scapula_R',
             'Elbow_L', 'Elbow_R')
FACE_JOINTS = {'Eye_L', 'Eye_R', 'Jaw_M'}   # the only joints an offset may be written for (--joints face)

LOG = []
WARNS = []     # up-front and skipped-step warnings, repeated as WARN lines in _check/summary.txt


class Fatal(Exception):
    """A problem that stops the run with one clear message (no traceback)."""


def die(msg):
    raise Fatal(msg)


def log(s):
    print(s, flush=True)
    LOG.append(s)


def warn(s):
    log('WARN: ' + s)
    WARNS.append(s)


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


def safe_metrics(label, fn):
    """Run a face-metrics measurement; an odd mesh (a window with no points in it) gives a WARN and None, not a crash."""
    try:
        return fn()
    except Exception as e:  # noqa: BLE001
        warn('%s metrics could not be measured (%s: %s)' % (label, type(e).__name__, e))
        return None


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


def breast_projection(sets, log):
    """Breast projection per set {tag: (points, nipple centres (2, 3), L then R)}: the largest z of the points within
    +-6 cm (x and y) of each nipple centre (the nipples' own overlay is not part of the points). Returns {tag: (zL, zR)}."""
    out = {}
    for tag, (pts, cen) in sets.items():
        z = []
        for c in cen:
            w = pts[(np.abs(pts[:, 0] - c[0]) < 0.06) & (np.abs(pts[:, 1] - c[1]) < 0.06)]
            z.append(float(w[:, 2].max()) if len(w) else np.nan)
        out[tag] = tuple(z)
    log('  breast projection, max z within +-6 cm of each nipple, L / R in mm: ' + '   '.join(
        '%s %.1f / %.1f' % (k, 1000 * v[0], 1000 * v[1]) for k, v in out.items()))
    return out


def male_reason(vam):
    """Why this export looks like a male Genesis 2 figure (or None): no 'Nipples' material, a 'Genitals' material
    (Genesis 2 Male's; the female figure's genitals are 'defaultMat'), or info.txt naming a male figure."""
    if 'Nipples' not in vam.groups:
        return "body.obj has no 'Nipples' material group"
    if 'Genitals' in vam.groups:
        return "body.obj has a 'Genitals' material group (Genesis 2 Male)"
    for k, v in vam.info.items():
        if k.strip().lower() in ('character', 'atom'):   # names the user chose: not evidence
            continue
        t = (k + ' ' + v).lower()
        if re.search(r'(?<![a-z])male(?![a-z])', t) or re.search(r'gender\W+m\b', t):
            return 'info.txt says male (%s = %s)' % (k, v)
    return None


def has_group(vam, g):
    """The VaM export has material group g with at least one face."""
    return g in vam.groups and bool((vam.face_group == vam.groups.index(g)).any())


def group_verts(vam, groups):
    """Vertex indices of the faces of the given material groups (those that exist, with faces)."""
    gi = [vam.groups.index(g) for g in groups if has_group(vam, g)]
    if not gi:
        return np.zeros(0, np.int64)
    return np.unique(vam.faces[np.isin(vam.face_group, gi)])


def validate_vam(vam, vamdir):
    """Up-front checks of the VaM export. Stops (Fatal) on a male figure or missing required parts; optional pieces
    that are missing are logged as WARN and their step is skipped. Returns the feature flags the fit branches on."""
    why = male_reason(vam)
    if why:
        die('This VaM export looks like a male figure (%s). The pipeline maps the female Genesis 2 figure onto Reika; '
            'export a female character.' % why)
    miss = [j for j in rig.vam_joint_names() if j not in vam.joints]
    if miss:
        die('joints.txt lacks %d joint(s) the pipeline uses: %s. Is this a Genesis 2 figure exported by the SfExport '
            'plugin?' % (len(miss), ', '.join(miss)))
    if len(vam.faces) == 0 or len(vam.v) < 1000:
        die('body.obj has no usable mesh (%d vertices, %d faces)' % (len(vam.v), len(vam.faces)))
    if vam.uv is None or vam.vn is None or len(vam.uv) != len(vam.v) or len(vam.vn) != len(vam.v):
        die('body.obj lacks UVs or normals per vertex: not an SfExport body.obj')
    gone = sorted(g for g in VAM_SKIN_REQUIRED if not has_group(vam, g))
    if gone:
        die('body.obj lacks the skin material group(s) %s: not a complete Genesis 2 export' % ', '.join(gone))
    feat = {}
    for g, what in (('Nipples', "nipple landmarks (her nipples are not matched to the character's)"),
                    ('Lips', 'mouth landmark (her mouth centre stays where it is; lip labels use her mouth position)'),
                    ('Ears', 'ear landmarks (the lateral extreme of the head at eye height is used instead)'),
                    ('Sclera', 'eyeball size and eye opening (eyeballs keep her size, eye opening not measured)'),
                    ('InnerMouth', 'mouth-opening ring (the lip centre is used for the mouth metrics)'),
                    ('Nostrils', 'nostril skin in the face metrics'),
                    ('Toenails', 'toenail skin'), ('Fingernails', 'fingernail skin')):
        feat[g] = has_group(vam, g)
        if not feat[g]:
            warn("VaM export has no '%s' faces: skipped %s" % (g, what))
    # (texture slots: a missing/unreadable _MainTex or _BumpMap is reported by the bake stage, which falls back to
    # a flat skin colour / flat normals)
    return feat


def check_game(meshes, canon, missing):
    got = {m.name for m in meshes}
    gone = sorted(n for n in FIT if n not in got)
    if gone:
        die('her export lacks the mesh(es) %s%s: export her again from SfMorph 0.5.1 or newer'
            % (', '.join(gone), ' (missing .obj: %s)' % ', '.join(missing) if missing else ''))
    gone = [b for b in HER_BONES if b not in canon]
    if gone:
        die('her skeleton/skin export lacks the bone(s) %s: export her again from SfMorph' % ', '.join(gone))


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
    chk = os.path.join(a.out, '_check')
    os.makedirs(chk, exist_ok=True)
    try:
        return run(a, chk)
    except Fatal as e:
        fail(chk, str(e))
    except Exception:  # noqa: BLE001
        fail(chk, 'internal error (see the traceback in _check/report.txt)', traceback.format_exc())
    return 2


def fail(chk, msg, detail=None):
    log('ERROR: ' + msg)
    if detail:
        log(detail)
    with open(os.path.join(chk, 'report.txt'), 'w', encoding='utf-8') as f:
        f.write('\n'.join(LOG) + '\n')
    with open(os.path.join(chk, 'summary.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('FAIL  %s\n' % msg)


def run(a, chk):
    t0 = time.time()
    for fn in VAM_FILES:
        if not os.path.isfile(os.path.join(a.vam, fn)):
            die('VaM export %s lacks %s (is it an SfExport folder? re-export from VaM)' % (a.vam, fn))
    meshes, skel, missing = sfio.read_game_export(a.game)
    if not meshes:
        die('meshes.txt lists no meshes in ' + a.game)
    try:
        vam = sfio.read_vam_export(a.vam)
    except (ValueError, IndexError, OSError) as e:
        die('cannot read the VaM export %s: %s' % (a.vam, e))
    name = a.name or vam.info.get('character', 'Character')
    log('character %s: VaM export %s, game export %s' % (name, a.vam, a.game))
    log('her meshes: %d (%d missing .obj: %s)' % (len(meshes), len(missing), ', '.join(missing) or '-'))
    for m in meshes:
        if len(m.v) != m.expected:
            die('%s: obj has %d vertices, meshes.txt says %d' % (m.name, len(m.v), m.expected))
    feat = validate_vam(vam, a.vam)

    # --- common bind space -------------------------------------------------------------------------------
    canon = rig.canonical_binds(meshes)
    for m in meshes:
        m.C = rig.to_canonical(m, canon)
        m.vc = rig.apply(m.C, m.v)
        moved = np.linalg.norm(m.vc - m.v, axis=1).max()
        if moved > 1e-4:
            log('  %s: own mesh space differs from the common bind space by up to %.1f mm (placed via bind poses)' % (m.name, moved * 1000))
    bind_pos = {b: np.linalg.inv(canon[b])[:3, 3] for b in canon}
    check_game(meshes, canon, missing)

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
    her_mouth[2] = face.vp[bv][:, 2].max() - 0.004
    lips_v = vam_v[group_verts(vam, ['Lips'])]
    lip_r = 0.03   # radius (y stretched 1.6x) round the mouth centre that is labelled upper/lower lip
    if feat['Lips']:
        vam_mouth = np.array([lips_v[:, 0].mean(), 0.5 * (lips_v[:, 1].min() + lips_v[:, 1].max()), lips_v[:, 2].max() - 0.008])
        # a wide VaM mouth (lips wider than hers, 30 mm radius) gets a proportionally wider lip label
        lip_r = max(0.03, 0.6 * float(np.ptp(lips_v[:, 0])))
    else:
        vam_mouth = her_mouth.copy()   # no VaM lips: her mouth stays where it is (no mouth landmark)
    log('mouth: hers %s, VaM %s (%d boundary vertices), lip label radius %.1f mm' % (np.round(her_mouth, 3), np.round(vam_mouth, 3), len(bv), 1000 * lip_r))
    names += ['lipU', 'lipL']
    nb['lipU'] = {'lipU', 'torso'}; nb['lipL'] = {'lipL', 'torso'}; nb['torso'] |= {'lipU', 'lipL'}
    def lip_labels(pts, lab, mc, rad=0.03):
        r = np.linalg.norm((pts - mc) * np.array([1, 1.6, 1]), axis=1)
        near = (r < rad) & (lab == names.index('torso'))
        lab = lab.copy()
        lab[near & (pts[:, 1] >= mc[1])] = names.index('lipU')
        lab[near & (pts[:, 1] < mc[1])] = names.index('lipL')
        return lab
    vsign = sfio.winding_sign(vam.v, vam.faces, vam.vn)
    skin_g = [i for i, g in enumerate(vam.groups) if g in VAM_SKIN and has_group(vam, g)]
    vf = vam.faces[np.isin(vam.face_group, skin_g)]
    vfn, _ = sfio.face_normals(vam_v, vf)
    vfn *= vsign
    # faces of VaM's 'Nipples' group: kept for measuring, but not an ICP target (her own nipple overlay replaces it; the
    # bump would half-pull the skin under the overlay)
    keep_t = (vam.face_group[np.isin(vam.face_group, skin_g)] != vam.groups.index('Nipples')) if feat['Nipples'] else np.ones(len(vf), bool)
    vlab = rig.labels(vam_v[vf].mean(1), vsegs, vj)
    hlab = lip_labels(wp, hlab, her_mouth)
    vlab = lip_labels(vam_v[vf].mean(1), vlab, vam_mouth, lip_r)
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
    use_nip = nip is not None and feat['Nipples']
    vn_i = group_verts(vam, ['Nipples'])
    if use_nip:
        for sgn in (-1, 1):
            hm = nip.vp[np.sign(nip.vp[:, 0]) == sgn]; vm = vam_v[vn_i][np.sign(vam_v[vn_i, 0]) == sgn]
            if len(hm) and len(vm):
                src.append(hm[np.argmax(hm[:, 2])]); dst.append(vm[np.argmax(vm[:, 2])])
                lm.append(('nipple' + ('L' if sgn < 0 else 'R'), 'Nipples'))
    if feat['Lips']:
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
    eye_open = None      # (apertures, closed) once measured on the VaM skin
    S = None             # mouth similarity (her proportions only): maps her mouth parts to the VaM-sized face
    lid_chk = []         # (eye, nearest fitted lid vertex to the eye centre, eyeball radius) in metres
    r_vam = None
    r_her = {}
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
        vlab_v = lip_labels(vam_v, vlab_v, vam_mouth, lip_r)
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
        if use_nip:
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
        for nm, e in (('reika_left_sclera', 'Eye_L'), ('reika_right_sclera', 'Eye_R')):
            ms = next((m for m in meshes if m.name == nm), None)
            if ms is not None:
                r_her[e] = np.linalg.norm(ms.vc - bind_pos[e], axis=1).mean()
        if feat['Sclera']:
            scl = vam_v[group_verts(vam, ['Sclera'])]
            rs = [np.linalg.norm(scl[np.sign(scl[:, 0]) == sg] - vj[k], axis=1).mean() for sg, k in ((-1, 'lEye'), (1, 'rEye'))
                  if (np.sign(scl[:, 0]) == sg).any()]
            r_vam = float(np.mean(rs)) if rs else None
        if r_vam is not None:
            # an eye-size morph can make the eyeballs very different from hers: keep her eyes within 0.75x..1.35x
            eye_ratio = {e: float(np.clip(r_vam / r, 0.75, 1.35)) for e, r in r_her.items()}
            if any(abs(r_vam / r - eye_ratio[e]) > 1e-9 for e, r in r_her.items()):
                warn("VaM eyeballs are much bigger/smaller than hers (radius %.2f mm vs %.2f mm): eyeball scale limited to %s"
                     % (1000 * r_vam, 1000 * np.mean(list(r_her.values())), {e: round(r, 3) for e, r in eye_ratio.items()}))
            log('eyeball radius: VaM %.2f mm, hers %s mm (scale %s); eye offsets %s mm' % (
                1000 * r_vam, {e: round(float(1000 * r), 2) for e, r in r_her.items()}, {e: round(float(r), 3) for e, r in eye_ratio.items()},
                {e: np.round(o * 1000, 1).tolist() for e, o in eye_off.items()}))
        else:
            log('eyeball radius: no VaM Sclera, her eyeballs keep their size; eye offsets %s mm' % (
                {e: np.round(o * 1000, 1).tolist() for e, o in eye_off.items()}))
        # face metrics source points: her face mesh (welded), VaM's head skin
        used = face.fit_idx
        face_w = widx[fit_off['reika_face_a'] + np.arange(len(used))]
        mouth_w = widx[fit_off['reika_face_a'] + np.searchsorted(used, bv)]
        vhead = group_verts(vam, ['Face', 'Nostrils', 'Lips', 'Ears', 'Head'])
        vears = group_verts(vam, ['Ears'])
        fm_her = safe_metrics('her face', lambda: face_metrics(wc[face_w], bind_pos['Eye_L'], bind_pos['Eye_R'], mouth_pts=wc[mouth_w], mesh=(wc, wf)))
        # VaM's mouth opening: its lip vertices that touch the inner mouth (the same kind of ring as her boundary vertices)
        ring = np.zeros(0, np.int64)
        if feat['Lips'] and feat['InnerMouth']:
            lip_i = group_verts(vam, ['Lips'])
            in_i = group_verts(vam, ['InnerMouth'])
            ring = lip_i[cKDTree(vam_b[in_i]).query(vam_b[lip_i])[0] < 0.0015]
        if len(ring) >= 10:
            vm_a = vam_b[ring].mean(0); vm_a[2] = vam_b[ring][:, 2].max() - 0.004
        log('VaM mouth opening: %d ring vertices, centre %s mm from the eye midpoint' % (len(ring), np.round(1000 * (vm_a - eye_mid_b), 1)))
        fm_vam = safe_metrics('VaM face', lambda: face_metrics(vam_b[vhead], eye_c['Eye_L'], eye_c['Eye_R'], mouth_pts=vam_b[ring] if len(ring) >= 10 else None,
                                                               mouth=None if len(ring) >= 10 else vm_a, mesh=(vam_b, vf)))
        vfn_b, _ = sfio.face_normals(vam_b, vf)
        vfn_b *= vsign
        surfaces = {}
        for li, n in enumerate(names):
            mm = np.isin(vlab, [names.index(x) for x in nb[n]]) & keep_t
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
        if feat['Sclera']:
            scl = group_verts(vam, ['Sclera'])
            cen_f = vam_b[vf].mean(1)
            apertures, ers = [], []
            for sgn in (-1, 1):
                sv = vam_b[scl][np.sign(vam_b[scl, 0]) == sgn]
                if not len(sv):
                    continue
                ec = sv.mean(0); er = np.linalg.norm(sv - ec, axis=1).mean()
                # window sizes are fractions of the VaM eyeball radius er (15.3 mm on the character this was tuned on:
                # 0.13 er = 2 mm wide line through the eye, 0.78 er = 12 mm up and down)
                band = 0.78 * er
                m = (np.abs(cen_f[:, 0] - ec[0]) < 0.13 * er) & (cen_f[:, 2] > ec[2] + 0.5 * er) & (np.abs(cen_f[:, 1] - ec[1]) < band)
                ys = np.sort(np.r_[cen_f[m, 1] - ec[1], -band, band])
                gaps = np.diff(ys)
                apertures.append(float(gaps.max()) if len(gaps) else 0.0)
                ers.append(er)
            ap_min = min(apertures) if apertures else 1.0
            closed = bool(apertures) and ap_min < 0.4 * float(np.mean(ers))     # 6 mm at er = 15.3 mm
            eye_open = (apertures, closed) if apertures else None
            log('VaM eye opening: %s mm -> %s' % (', '.join('%.1f' % (1000 * a) for a in apertures),
                'mostly closed (caught mid-blink?): her own lids keep their opening. Re-export from VaM with the SfExport '
                'plugin (it turns blinking off) to get the character\'s eye shape too' if closed else 'open: the lids are fitted'))
            if closed:
                # her own lids stay, so her eyeballs keep their size (the VaM-sized eyeball would push through them)
                if eye_ratio:
                    log('eyes closed on the VaM skin: eyeball scale %s -> 1.0 (her lids and eyeball stay matched)' % {e: round(r, 3) for e, r in eye_ratio.items()})
                eye_ratio = {e: 1.0 for e in ('Eye_L', 'Eye_R')}
                for e in ('Eye_L', 'Eye_R'):
                    r = np.linalg.norm(wc - bind_pos[e], axis=1)
                    nodata |= r < 0.021
                    stiff_b = np.maximum(stiff_b, np.clip(12.0 * (1 - (r - 0.016) / 0.012), 1, 12))
        # VaM's shoulders come from a T-pose (trapezius bunched up with the arms raised) and don't come down with
        # the arms: her own clavicle/shoulder shape is kept (no data within 12 cm of the shoulder and scapula bind
        # positions), with a stiffness ramp (10 at 12 cm down to 1 at 18 cm) so VaM's shape takes over smoothly around it
        n_sh = np.zeros(nw, bool)
        for sdn in ('Shoulder_L', 'Shoulder_R', 'Scapula_L', 'Scapula_R'):
            r = np.linalg.norm(wc - bind_pos[sdn], axis=1)
            n_sh |= r < 0.12
            stiff_b = np.maximum(stiff_b, np.clip(10.0 * (1 - (r - 0.12) / 0.06), 1, 10))
        nodata |= n_sh
        # her separate nipple overlay replaces VaM's nipple: no data within 15 mm of any overlay vertex (the ICP's
        # smoothness term interpolates the skin underneath from the rim)
        n_nip = np.zeros(nw, bool)
        nip_c = {}
        if nip is not None:
            n_nip = cKDTree(nip.vc).query(wc)[0] < 0.015
            nodata |= n_nip
            # inframammary fold: 55 mm below each nipple centre (bind pose) the fit is stiffer (4 at the centre, 1 at 45 mm)
            # so the fold keeps its line while the breast above it takes VaM's smaller volume
            for sgn in (-1, 1):
                hm = nip.vc[np.sign(nip.vc[:, 0]) == sgn]
                if not len(hm):
                    continue
                nip_c[sgn] = hm.mean(0)
                pf = wc[np.argmin(np.linalg.norm(wc - (nip_c[sgn] - np.array([0, 0.055, 0])), axis=1))]
                r = np.linalg.norm(wc - pf, axis=1)
                stiff_b = np.maximum(stiff_b, np.clip(1 + 3.0 * (1 - r / 0.045), 1, 4))
            log('%d skin vertices within 15 mm of her nipple overlay keep no VaM data; inframammary stiffness ramp (4 -> 1 over 45 mm) at 55 mm below each nipple; VaM Nipples faces %s as ICP target' % (
                n_nip.sum(), 'dropped' if feat['Nipples'] else 'absent'))
        log('%d mouth-interior vertices keep her shape (no data); %d shoulder vertices keep her shape (no data within 12 cm, stiffness ramp to 10 out to 18 cm)' % (mouth_in.sum(), n_sh.sum()))
        # Face pre-alignment: her head (only) is warped onto VaM's face landmarks first, so the ICP starts with the
        # nose, chin, ears and mouth near their targets (its smoothness term anchors to the aligned shape).
        if fm_her is not None and fm_vam is not None:
            lm_her = face_landmarks(fm_her, wc[face_w], ears=None)
            # her ear landmark is the lateral extreme of the face at eye height, so VaM's is the lateral extreme of its
            # 'Ears' at eye height too (their centroid sits lower and further in: it squashed her ears by 3 cm)
            ears_dst = None
            if len(vears):
                ve = vam_b[vears]; ve = ve[np.abs(ve[:, 1] - eye_mid_b[1]) < 0.01]
                if (ve[:, 0] < 0).any() and (ve[:, 0] > 0).any():
                    ears_dst = np.array([ve[np.argmax(np.abs(ve[:, 0]) * (np.sign(ve[:, 0]) == sg))] for sg in (-1, 1)])
                else:
                    warn('VaM Ears have no vertices at eye height on both sides: ear landmarks use the head width instead')
            lm_vam = face_landmarks(fm_vam, vam_b[vhead], ears=ears_dst)
            lsrc = np.array([lm_her[k] for k in FACE_LM]); ldst = np.array([lm_vam[k] for k in FACE_LM])
            log('face pre-alignment landmarks, her -> VaM (mm): ' + ', '.join('%s %.1f' % (k, 1000 * np.linalg.norm(lm_vam[k] - lm_her[k])) for k in FACE_LM))
            pre = fit.tps_warp(wc, lsrc, ldst, smoothing=1e-4)
            w_her = np.where(np.isin(hlab, head_names), np.clip((wc[:, 1] - yN) / (yH - yN), 0, 1), 0.0)
            x_start = wc + (pre - wc) * w_her[:, None]
        else:
            warn('face metrics failed: no face pre-alignment (the fit starts from her own face)')
            x_start = wc.copy()
        # Mouth similarity S: the face shrinks to the VaM character's, so her teeth, tongue and mouth interior shrink
        # with it (centre: her eye midpoint, no rotation), landing her lip ring on the pre-aligned lip ring
        # (the lips only: her boundary ring has a few stray vertices up at the nostrils, so the ring is cut to +-12 mm round its median height)
        ring_i = mouth_w[np.abs(wc[mouth_w][:, 1] - np.median(wc[mouth_w][:, 1])) < 0.012]
        ring_c = wc[ring_i].mean(0); ring_t = x_start[ring_i].mean(0)
        if fm_her is not None and fm_vam is not None:
            s_mouth = float(np.mean([fm_vam['spacing'] / fm_her['spacing'],
                                     np.linalg.norm(fm_vam['chin'] - fm_vam['em']) / np.linalg.norm(fm_her['chin'] - fm_her['em'])]))
        else:
            s_mouth = 1.0
        S_tr = ring_t - (eye_mid_b + s_mouth * (ring_c - eye_mid_b))
        S = lambda p: eye_mid_b + s_mouth * (np.asarray(p) - eye_mid_b) + S_tr
        x_start[mouth_in] = S(wc[mouth_in])
        log('mouth similarity S: scale %.4f about her eye midpoint (eye spacing %.4f, eye-chin %.4f), her lip ring centroid moves %.1f mm to the pre-aligned ring (%d lip-ring vertices of %d, %d interior vertices)' % (
            s_mouth, fm_vam['spacing'] / fm_her['spacing'] if fm_her is not None and fm_vam is not None else 1.0,
            np.linalg.norm(fm_vam['chin'] - fm_vam['em']) / np.linalg.norm(fm_her['chin'] - fm_her['em']) if fm_her is not None and fm_vam is not None else 1.0,
            1000 * np.linalg.norm(S(ring_c) - ring_c), len(ring_i), len(mouth_w), mouth_in.sum()))
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
    if S is not None:
        # mouth: what the generic relaxation cannot undo (tiny triangles at the lip corners folded by the lip fit) is
        # blended towards the mouth similarity S, which never folds, ring by ring until the mouth has no flipped triangle
        wd, f0, f1 = untangle_toward(wc, wf, wd, S(wc) - wc, np.linalg.norm(wc - hm_b, axis=1) < 0.06)
        log('untangle fitted skin, mouth (towards the similarity S): %d flipped triangles -> %d' % (f0, f1))

    # normals of the fitted skin on the welded mesh (one normal per welded vertex: no seams at mesh borders)
    wn0 = sfio.vertex_normals(wc, wf, hsign); wn1 = sfio.vertex_normals(wc + wd, wf, hsign)

    # head weight column: smoothstep of the bind-pose height between her Neck_M and Head_M (the ramp the head
    # alignment uses), written as the last value of every morph and skin line (the game's head-follow weight)
    def head_w(y):
        t = np.clip((np.asarray(y) - bind_pos['Neck_M'][1]) / (bind_pos['Head_M'][1] - bind_pos['Neck_M'][1]), 0, 1)
        return t * t * (3 - 2 * t)

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
        if S is not None and 'Jaw_M' in bind_pos:
            joint_off['Jaw_M'] = S(bind_pos['Jaw_M']) - bind_pos['Jaw_M']
    elif a.joints == 'face':
        for e in eye_off:
            joint_off[e] = cap(eye_off[e])
        if 'Jaw_M' in bind_pos:
            joint_off['Jaw_M'] = cap(fit.shell_average(wc, wd, bind_pos['Jaw_M'])) if S is None else S(bind_pos['Jaw_M']) - bind_pos['Jaw_M']
    log('joints (%s): %s' % (a.joints, ', '.join('%s %s mm' % (b, np.round(o * 1000, 1).tolist()) for b, o in sorted(joint_off.items()) if np.any(o)) or 'all zero'))
    if eye_ratio and eye_off:
        # her lids against the eyeball: the fitted lid vertices must stay outside it
        for e in ('Eye_L', 'Eye_R'):
            c = bind_pos[e] + eye_off[e]
            lid = np.linalg.norm(wc - bind_pos[e], axis=1) < 0.03
            dl = np.linalg.norm(wc[lid] + wd[lid] - c, axis=1)
            lid_chk.append((e, float(dl.min()), float(eye_ratio[e] * r_her[e])))
            log('  %s: eyeball radius %.2f mm, nearest fitted lid vertex %.2f mm from the centre (%d vertices within 30 mm)' % (
                e, 1000 * eye_ratio[e] * r_her[e], 1000 * dl.min(), lid.sum()))
    results = []
    from scipy.spatial import cKDTree
    skin_tree = cKDTree(wc)
    # her nipple overlay fills a hole in her skin mesh (areola, nipple and the breast tip): it follows the VaM skin there
    # (fit.cap_attach). Without VaM nipples (or in --proportions vam) it is attached like the other overlays.
    cap_target = fit.Surface(vam_out, vf, np.zeros((len(vf), 3))) if bindfit and nip is not None and use_nip else None
    for m in meshes:
        d = fit.idw(wc, wd, m.vc, k=8)
        capped = False
        if m.name in ATTACH:
            if m is nip and cap_target is not None:
                d = fit.cap_attach(m.vc, m.faces, wc, wd, cap_target, scale=0.003, ramp=0.025, log=log)
                capped = True   # (the generic untangle below only makes this cap worse)
            else:
                d = fit.surface_attach(m.vc, wc, wf, wd, hsign, vn0=wn0, vn1=wn1)
        elif m.name in PATCH_RIGID:
            d = fit.patch_rigid(m.vc, m.faces, wc, wd)
        elif m.name not in RIGID and len(m.fit_idx) < len(m.v):
            near, _ = skin_tree.query(m.vc)
            pinned = near < 2e-5
            pinned[m.fit_idx] = True
            if S is not None and m.name in ('reika_upperMouth', 'reika_lowerMouth'):
                d = np.where(pinned[:, None], d, S(m.vc) - m.vc)   # soft target: the shrunk mouth; the seams stay on the skin
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
            elif S is not None:
                d[:] = S(m.vc) - m.vc
            else:
                d[:] = d.mean(0)
        elif len(m.fit_idx) < len(m.v) and not capped:
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

    # upper-teeth front against the upper lip's inner surface (fitted face skin facing back into the mouth)
    tm = next((m for m in results if m.name == 'reika_upperTeeth'), None)
    if tm is not None and bindfit:
        tp = tm.vc + tm.dc
        cen = np.abs(tp[:, 0] - eye_mid_b[0]) < 0.008
        tf = tp[cen][np.argmax(tp[cen][:, 2])]
        fpos = (wc + wd)[face_w]; fn = wn1[face_w]
        win = (np.abs(fpos[:, 0] - tf[0]) < 0.010) & (np.abs(fpos[:, 1] - tf[1]) < 0.004) & (fn[:, 2] < -0.2) & (fpos[:, 2] > tf[2] - 0.02) & (fpos[:, 2] < tf[2] + 0.03)
        if win.any():
            log('upper teeth front z %.1f mm (y %.1f) vs upper lip inner surface z %.1f mm (%d vertices): clearance %.1f mm (positive = teeth behind the lip)' % (
                1000 * tf[2], 1000 * tf[1], 1000 * fpos[win, 2].mean(), win.sum(), 1000 * (fpos[win, 2].mean() - tf[2])))
        else:
            log('upper teeth front z %.1f mm: no inner-lip vertices found near it to measure the clearance' % (1000 * tf[2]))

    # --- verification ------------------------------------------------------------------------------------
    body = None
    breast_diff = []    # worst |fitted - VaM-warped| breast projection (m), once measured
    fm_diff = None      # face metric differences fitted - VaM (m), for the summary
    if bindfit:
        fm_fit = None
        if fm_her is not None and fm_vam is not None:
            fm_fit = safe_metrics('fitted face', lambda: face_metrics((wc + wd)[face_w], bind_pos['Eye_L'] + eye_off['Eye_L'], bind_pos['Eye_R'] + eye_off['Eye_R'],
                                                                     mouth_pts=(wc + wd)[mouth_w], mesh=(wc + wd, wf)))
        if fm_fit is not None:
            log('face metrics (head, eye midpoint at her eyes; positions relative to it):')
            log_face_metrics('Reika bind face', fm_her, log)
            log_face_metrics('VaM head (rigid, aligned)', fm_vam, log)
            log_face_metrics('fitted result', fm_fit, log)
            dif = lambda k: np.linalg.norm(fm_fit[k] - fm_vam[k]) if np.ndim(fm_fit[k]) else abs(fm_fit[k] - fm_vam[k])
            fm_diff = {k: float(dif(k)) for k in ('spacing', 'eye_mouth', 'eye_lip', 'nose', 'chin', 'width')}
            log('  fitted - VaM: eye spacing %.1f mm, eye-mouth %.1f mm (lip front %.1f), nose %.1f mm, chin %.1f mm, head width %.1f mm' % (
                1000 * dif('spacing'), 1000 * dif('eye_mouth'), 1000 * dif('eye_lip'), 1000 * dif('nose'), 1000 * dif('chin'), 1000 * dif('width')))
        nip_y = nip.vc[:, 1].mean() if nip is not None else bind_pos['Chest_M'][1]
        skin_vi = np.unique(vf.ravel())
        vlab_vb = vlab_v
        sets = {'Reika': (wc, hlab), 'fitted': (wc + wd, hlab), 'VaM-warped': (vam_out[skin_vi], vlab_vb[skin_vi])}
        # breast projection: her upperBody plus her nipple overlay (the cap that fills the hole in the upperBody under the
        # nipple) against the VaM skin, within +-6 cm of each nipple centre of the set; the upperBody alone is logged too
        ub = next((m for m in meshes if m.name == 'reika_upperBody_normal'), None)
        bp_sets = bp_body = None
        if ub is not None and nip is not None and len(nip_c) == 2:
            ub_w = np.unique(widx[fit_off[ub.name] + np.arange(len(ub.fit_idx))])
            sd = [np.sign(nip.vc[:, 0]) == sg for sg in (-1, 1)]
            c_her = np.array([nip.vc[k].mean(0) for k in sd])
            c_fit = np.array([(nip.vc + nip.dc)[k].mean(0) for k in sd])
            c_vam = np.array([vam_out[vn_i][np.sign(vam_out[vn_i, 0]) == sg].mean(0) for sg in (-1, 1)]) if use_nip else c_her
            bp_sets = {'Reika': (np.concatenate([wc[ub_w], nip.vc]), c_her),
                       'fitted': (np.concatenate([(wc + wd)[ub_w], nip.vc + nip.dc]), c_fit),
                       'VaM-warped': (vam_out[skin_vi], c_vam)}
            bp_body = {'Reika': (wc[ub_w], c_her), 'fitted': ((wc + wd)[ub_w], c_fit), 'VaM-warped': (vam_out[skin_vi], c_vam)}
        def body():
            try:
                body_metrics(sets, names, bind_pos, nip_y, log)
            except Exception as e:  # noqa: BLE001  (report only)
                log('  body cross-sections not measured (%s)' % e)
            try:
                if bp_sets is not None:
                    log('  breast projection, her upperBody alone (the hole under the nipple has no vertices):')
                    breast_projection(bp_body, log)
                    log('  breast projection, upperBody + nipple overlay:')
                    bp = breast_projection(bp_sets, log)
                    breast_diff.append(max(abs(bp['fitted'][i] - bp['VaM-warped'][i]) for i in (0, 1)))
                    log('  breast projection fitted - VaM-warped: L %.1f mm, R %.1f mm' % tuple(1000 * (bp['fitted'][i] - bp['VaM-warped'][i]) for i in (0, 1)))
            except Exception as e:  # noqa: BLE001  (report only)
                log('  breast projection not measured (%s)' % e)
    rep = verify(results, wmatch, dist, log, body)
    # the other direction: how well her fitted skin covers the VaM skin, per VaM material
    kd1 = cKDTree(xfit); kd0 = cKDTree(wc if bindfit else x0)
    rows = []
    for gi in skin_g:
        vi = np.unique(vam.faces[vam.face_group == gi])
        if not len(vi):
            continue
        d1, _ = kd1.query(vam_out[vi]); d0, _ = kd0.query(vam_out[vi])
        rows.append('%s %.1f/%.1f (%.1f)' % (vam.groups[gi], 1000 * d1.mean(), 1000 * np.percentile(d1, 95), 1000 * d0.mean()))
    log('  VaM skin -> nearest fitted vertex, mean/p95 mm (before fit): ' + ', '.join(rows))
    if bindfit and nip is not None and feat['Nipples']:
        # her skin under the nipple is her nipple overlay (the upperBody has a hole there): count its vertices too
        kd2 = cKDTree(np.concatenate([xfit, nip.vc + nip.dc]))
        d2, _ = kd2.query(vam_out[vn_i])
        d1v, _ = kd1.query(vam_out[vn_i])
        # (the nearest vertex of a 5 mm skin mesh is never closer than ~1.5 mm: the distance to the surface is the fair figure)
        srf_all = fit.Surface(np.concatenate([xfit, nip.vc + nip.dc]), np.concatenate([wf, nip.faces + len(xfit)]), np.zeros((len(wf) + len(nip.faces), 3)))
        ds = srf_all.nearest(vam_out[vn_i], k=16)[1]
        log('  VaM Nipples -> her fitted skin incl. her nipple overlay, mean/p95 mm: to the surface %.2f/%.2f, to the nearest vertex %.1f/%.1f '
            '(upperBody vertices alone, hole under the nipple: %.1f/%.1f)' % (
                1000 * ds.mean(), 1000 * np.percentile(ds, 95), 1000 * d2.mean(), 1000 * np.percentile(d2, 95), 1000 * d1v.mean(), 1000 * np.percentile(d1v, 95)))

    # --- write -------------------------------------------------------------------------------------------
    for m in results:
        sfio.write_morph(os.path.join(a.out, m.name + '.morph.txt'), m.name, m.d, m.dn, h=head_w(m.vc[:, 1]))
    with open(os.path.join(a.out, 'joints.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('# bone\tparent\tbind position\toffset (bind space, Unity axes, metres)\n')
        for b in sorted(joint_off):
            o = joint_off[b]; p = bind_pos[b]
            f.write('%s\t%s\t%.6f %.6f %.6f\t%.6f %.6f %.6f\n' % (b, skel[b]['parent'] if b in skel else '-', p[0], p[1], p[2], o[0], o[1], o[2]))
    # Her whole fitted skin (welded) and every bone's bind pose in the common bind space: what clothes, outfit body
    # variants (uniform_upperBody_normal replaces her upper body when she's dressed) and any unconverted mesh follow
    # in the game, whichever of her meshes the scene uses.
    with open(os.path.join(a.out, 'skin.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('# her skin, welded, common bind space: position, normal, offset, normal delta, head weight\n')
        f.write('vertices %d\n' % nw)
        dn = wn1 - wn0
        hw = head_w(wc[:, 1])
        for i in range(nw):
            f.write('%.6g %.6g %.6g %.4g %.4g %.4g %.6g %.6g %.6g %.4g %.4g %.4g %.4g\n' % (tuple(wc[i]) + tuple(wn0[i]) + tuple(wd[i]) + tuple(dn[i]) + (hw[i],)))
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
        f.write('vamexport\t%s\n' % os.path.basename(os.path.normpath(a.vam)))
        f.write('made\t%s\n' % time.strftime('%Y-%m-%d %H:%M'))
        f.write('scale\t%.5f\n' % s)
        f.write('proportions\t%s\n' % a.proportions)
        f.write('frames\tbody face\n')
        for m in results:
            f.write('mesh\t%s\t%d\n' % (m.name, len(m.v)))
        for n in FIT:  # her skin: clothes follow the nearest of these vertices in the game
            f.write('skin\t%s\n' % n)
        for n in HIDE:  # her anime brows: VaM characters carry their brows in the face texture
            f.write('hide\t%s\n' % n)
    np.savez_compressed(os.path.join(chk, 'stage.npz'),
                        vam_v=vam_out, vam_eyes=vam_eyes, vam_eye_r=np.float64(r_vam if r_vam else np.nan), vam_f=vam.faces, vam_g=vam.face_group, vam_uv=vam.uv, vam_vn=vam.vn * vsign,
                        vam_groups=np.array(vam.groups), names=np.array([m.name for m in results]),
                        dbg_wp=wp, dbg_x0=x0, dbg_xfit=xfit, dbg_wf=wf, dbg_lab=hlab, dbg_match=wmatch,
                        **{'%s|%s' % (m.name, k): getattr(m, k) for m in results
                           for k in ('vc', 'dc', 'vp', 'vp1', 'faces', 'face_group', 'uv')},
                        **{'%s|groups' % m.name: np.array(m.groups) for m in results})
    log('fit stage done in %.0f s' % (time.time() - t0))
    summarize(chk, name, results, wmatch, dist, eye_open, fm_diff, joint_off, a.proportions, lid_chk, breast_diff[0] if breast_diff else None)
    with open(os.path.join(chk, 'report.txt'), 'w', encoding='utf-8') as f:
        f.write('\n'.join(LOG) + '\n')
    return 0 if rep else 1


def untangle_toward(v, faces, d, target, region, rings=2, step=0.25, iters=60):
    """Remove flipped triangles (normal reversed between v and v + d) inside region by blending the offsets of the
    vertices round them (rings rings wide) towards target offsets, a little more each pass. Returns (d, flipped before,
    flipped after) counting the triangles that touch region."""
    n = len(v)
    reg = region[faces].any(1)
    e = np.concatenate([faces[:, [0, 1]], faces[:, [1, 2]], faces[:, [2, 0]]])
    import scipy.sparse as sp
    A = sp.coo_matrix((np.ones(len(e) * 2), (np.r_[e[:, 0], e[:, 1]], np.r_[e[:, 1], e[:, 0]])), shape=(n, n)).tocsr()

    def bad(dd):
        a = np.cross(v[faces[:, 1]] - v[faces[:, 0]], v[faces[:, 2]] - v[faces[:, 0]])
        w = v + dd
        b = np.cross(w[faces[:, 1]] - w[faces[:, 0]], w[faces[:, 2]] - w[faces[:, 0]])
        return (np.einsum('ij,ij->i', a, b) < 0) & (np.linalg.norm(a, axis=1) > 1e-14) & reg
    d0 = d.copy(); d = d.copy(); t = np.zeros(n)
    first = int(bad(d).sum())
    for _ in range(iters):
        bf = bad(d)
        if not bf.any():
            break
        m = np.zeros(n, bool); m[faces[bf].ravel()] = True
        for _ in range(rings):
            m = m | (A @ m.astype(float) > 0)
        t[m] = np.minimum(1.0, t[m] + step)
        d = d0 * (1 - t[:, None]) + target * t[:, None]
    return d, first, int(bad(d).sum())


def summarize(chk, name, results, wmatch, dist, eye_open, fm_diff, joint_off, proportions, lid_chk=(), breast_diff=None):
    """PASS/WARN lines for the things that matter, printed and written to _check/summary.txt (the Blender stage
    appends its texture line). Thresholds: eyes open, face metrics within 3 mm of VaM, fit residual mean < 0.5 mm,
    flipped triangles per main skin mesh < 0.5 %, largest offset < 6 cm, joint offsets only on the face."""
    rows = []

    def add(ok, text):
        rows.append(('PASS  ' if ok else 'WARN  ') + text)

    if eye_open is not None:
        ap, closed = eye_open
        shown = ', '.join('%.1f' % (1000 * x) for x in ap)
        if closed:
            add(False, "eye opening: VaM's eyes are closed or half closed (%s mm), so her own lids keep their opening; "
                       "re-export from VaM (the SfExport plugin turns blinking off)" % shown)
        else:
            add(True, "eye opening: open (%s mm), VaM's lids are fitted" % shown)
    else:
        add(False, 'eye opening: not measured (%s)' % ('--proportions vam' if proportions == 'vam' else 'no usable VaM Sclera'))
    if lid_chk:
        bad = [(e, d, r) for e, d, r in lid_chk if d < r - 0.001]
        add(not bad, 'lids vs eyeball: nearest fitted lid vertex to the eye centre %s mm, eyeball radius %s mm (need lid >= radius - 1 mm)' % (
            '/'.join('%.1f' % (1000 * d) for e, d, r in lid_chk), '/'.join('%.1f' % (1000 * r) for e, d, r in lid_chk)))
    if fm_diff is not None:
        bad = {k: v for k, v in fm_diff.items() if v > 0.003}
        worst = max(fm_diff, key=fm_diff.get)
        label = {'spacing': 'eye spacing', 'eye_mouth': 'eye-mouth', 'eye_lip': 'lip front', 'nose': 'nose', 'chin': 'chin', 'width': 'head width'}
        add(not bad, 'face metrics vs VaM (limit 3 mm): worst %s %.1f mm%s' % (
            label[worst], 1000 * fm_diff[worst], '' if not bad else '; over: ' + ', '.join('%s %.1f' % (label[k], 1000 * v) for k, v in bad.items())))
    else:
        add(False, 'face metrics vs VaM: not measured (%s)' % ('--proportions vam' if proportions == 'vam' else 'see WARN lines'))
    if breast_diff is not None:
        add(breast_diff < 0.003, 'breast projection vs VaM (limit 3 mm): worst %.1f mm' % (1000 * breast_diff))
    mm = wmatch > 0
    if mm.any():
        r = float(dist[mm].mean())
        add(r < 0.0005, 'fit residual mean %.2f mm (limit 0.5 mm), p95 %.2f mm, %.1f%% of the skin matched' % (
            1000 * r, 1000 * np.percentile(dist[mm], 95), 100 * mm.mean()))
    else:
        add(False, 'fit residual: nothing matched the VaM surface')
    skin = [m for m in results if m.name in FIT]
    over = ['%s %.2f%%' % (m.name, 100.0 * m.stats['flipped'] / max(len(m.faces), 1)) for m in skin
            if m.stats['flipped'] >= 0.005 * len(m.faces)]
    fl = max([100.0 * m.stats['flipped'] / max(len(m.faces), 1) for m in skin] or [0.0])
    add(not over, 'flipped triangles per main skin mesh: worst %.2f%% (limit 0.5%%)%s' % (fl, '; over: ' + ', '.join(over) if over else ''))
    big = max(results, key=lambda m: m.stats['omax'])
    add(big.stats['omax'] < 0.06, 'largest offset %.1f mm on %s (limit 60 mm)' % (1000 * big.stats['omax'], big.name))
    moved = sorted(b for b, o in joint_off.items() if np.any(o))
    other = [b for b in moved if b not in FACE_JOINTS]
    add(not other, 'joint offsets: %s' % ((', '.join(moved) or 'none') if not other else 'beyond the face: ' + ', '.join(other)))
    rows += ['WARN  ' + w for w in WARNS]
    npass = sum(r.startswith('PASS') for r in rows); nwarn = len(rows) - npass
    out = ['# %s: %d PASS, %d WARN' % (name, npass, nwarn)] + rows
    log('summary:')
    for r in out:
        log('  ' + r)
    with open(os.path.join(chk, 'summary.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(out) + '\n')


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
            fm = (np.einsum('ij,ij->i', n0, n1) < 0) & good
            fc = m.vc[f[fm]].mean(1)
            log('    %s flipped at (rounded, cm): %s' % (m.name, ' '.join('(%d %d %d)x%d' % (tuple(k) + (c,)) for k, c in
                zip(*np.unique(np.round(fc * 20) * 5, axis=0, return_counts=True)) if c >= 3)))
            # how many of them were twisted slivers in her own mesh (normal against the neighbours' average: hers vs fitted)
            def nb_dot(nn):
                sv = np.zeros((len(m.v), 3))
                for k in range(3):
                    np.add.at(sv, f[:, k], nn)
                nb = sv[f].sum(1) - 3 * nn
                return np.einsum('ij,ij->i', nn, nb) / np.maximum(np.linalg.norm(nb, axis=1), 1e-12)
            d0, d1 = nb_dot(n0)[fm], nb_dot(n1)[fm]
            log('    %s: of %d flipped, %d agreed with their neighbours in her mesh (a real fold) and %d were already twisted there '
                '(normal . neighbours < 0.8; after the fit %d of them agree with their neighbours, > 0.8)' % (
                    m.name, flipped, int((d0 >= 0.8).sum()), int((d0 < 0.8).sum()), int(((d0 < 0.8) & (d1 > 0.8)).sum())))
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
