"""Slider fit: a VaM character's relative body proportions as SF Body Morphs' own Body sliders (no mesh swap, her own skin).

    python sliderfit.py --game <her export dir> --vam <SfExport dir> --out <preset .txt> [--name Yin]

Runs in Blender's bundled Python (numpy) like sfpipe.py (run.ps1 -Sliders). The lower-risk alternative to the full
conversion: nothing of hers is replaced, the result is an ordinary preset (Presets tab > Load) that can be tuned by hand.

Both figures are measured in their own rest pose (VaM: T-pose, her: A-pose bind pose) with pose-independent measures
only, and only on landmarks both rigs place alike: knee, ankle, elbow, wrist, eyes and finger joints (rig.landmark_pairs),
plus skin landmarks (nipple tips, the widest hip, heel to toe). Her hip, spine, neck and shoulder joints sit elsewhere
than VaM's (rig.py), so they never give a measure: they only carry the slider the measure is put on. VaM is scaled to her
eye height: the fit moves proportions, not her height. Per slider (Body.cs semantics):
  Size      ratio of a limb's girth (median radius of a band across the limb), of hand / foot length, of head width x
            height, of breast / glute projection, of the widest hip width (pelvis).
  Length    the change of a landmark distance put on the bone that spans it; a Size slider with Keep on the same bone
            scales its length too, so that is divided out.
  Stretch   waist and under-bust width and depth ratios (waistWidth/Depth, ribcageWidth/Depth).
  Move (cm) nipple spacing (after the ribcage width).
Everything is clamped to the slider's range (clamps are noted). The preset carries the measurements as comments.
Not fitted: the face (her face bones reach little of a different face), shoulder and hip joint spacing (no landmark both
rigs agree on), breast lift.
"""
import argparse
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import sfio  # noqa: E402
import rig  # noqa: E402
import roles  # noqa: E402

VAM_SKIN = {'Legs', 'Nostrils', 'Lips', 'Face', 'Head', 'Hands', 'Shoulders', 'Neck', 'Hips', 'Torso', 'Nipples',
            'Forearms', 'Feet', 'Ears', 'defaultMat'}
# slider ranges (Body.cs Detail.All), in the order they are written
RANGE = {
    'thighs': (0.6, 1.6), 'thighLength': (0.85, 1.2), 'calves': (0.6, 1.6), 'calfLength': (0.85, 1.2), 'feet': (0.7, 1.4),
    'hips': (0.7, 1.5), 'glutes': (0.5, 2.0),
    'waistWidth': (0.7, 1.4), 'waistDepth': (0.7, 1.4), 'ribcageWidth': (0.8, 1.3), 'ribcageDepth': (0.8, 1.3),
    'torsoLength': (0.85, 1.2), 'breasts': (0.3, 2.5), 'breastSpacing': (-3.0, 3.0),
    'upperarms': (0.6, 1.6), 'armLength': (0.85, 1.2), 'forearms': (0.6, 1.6), 'forearmLength': (0.85, 1.2), 'hands': (0.7, 1.4),
    'neck': (0.7, 1.5), 'neckLength': (0.8, 1.3), 'head': (0.8, 1.25),
}
NEUTRAL_EPS = {'Move': 0.15, 'Ratio': 0.01}   # closer to neutral than this: left out

# joints: role -> (hers, VaM) per side ('%s' = L/R for her, l/r for VaM)
J = {
    'hip': ('Hip_%s', '%sThigh'), 'knee': ('Knee_%s', '%sShin'), 'ankle': ('Ankle_%s', '%sFoot'),
    'shoulder': ('Shoulder_%s', '%sShldr'), 'elbow': ('Elbow_%s', '%sForeArm'), 'wrist': ('Wrist_%s', '%sHand'),
    'mid3': ('MiddleFinger3_%s', '%sMid3'), 'eye': ('Eye_%s', '%sEye'),
}
JM = {'neck': ('Neck_M', 'neck'), 'head': ('Head_M', 'head'), 'chestj': ('Chest_M', 'chest'), 'spine1': ('Spine1_M', 'abdomen2')}
ARM_LABELS = ('uparm', 'forearm', 'hand', 'fIndex', 'fMiddle', 'fRing', 'fPinky', 'fThumb')


def log(s):
    print(s, flush=True)


class Fig:
    """One figure: skin points, part labels, joints by (role, side), nipple guesses."""

    def __init__(self, tag, pts, lab, names, joints, nipples):
        self.tag, self.p, self.lab, self.names, self.j, self.nip = tag, pts, lab, names, joints, nipples
        self.tip = nipples

    def jp(self, role, side=None):
        return self.j[(role, side)]

    def part(self, *names):
        return self.p[np.isin(self.lab, [self.names.index(n) for n in names if n in self.names])]

    def scale(self, s):
        self.p = self.p * s
        self.j = {k: p * s for k, p in self.j.items()}
        self.nip = self.nip * s


def load_her(gdir):
    meshes, skel, missing = sfio.read_game_export(gdir)
    for m in meshes:
        m.base = m.name.replace(' (SfSoft)', '').replace(' (SfMorph)', '')
    canon = rig.canonical_binds(meshes)
    bp = {b: np.linalg.inv(canon[b])[:3, 3] for b in canon}
    # her skin: the body / face variants she wears (roles.py; an export taken with physics on names them "<mesh> (SfSoft)")
    skin = roles.Roles([m.base for m in meshes], {m.base for m in meshes if getattr(m, 'active', True)}).skin()
    pts = [rig.apply(rig.to_canonical(m, canon), m.v) for m in meshes if m.base in skin]
    if not pts:
        raise SystemExit('her export %s has none of her skin meshes (upper / lower body, foot, neck, face)' % gdir)
    pts = np.concatenate(pts)
    joints = {}
    for role, (h, _) in J.items():
        for s in 'LR':
            if h % s in bp:
                joints[(role, s)] = bp[h % s]
    for role, (h, _) in JM.items():
        if h in bp:
            joints[(role, None)] = bp[h]
    nips = [bp[n] for n in ('Left_Nipple', 'Right_Nipple') if n in bp]
    names, hsegs, _, _ = rig.label_segments()
    return Fig('her', pts, rig.labels(pts, hsegs, bp), names, joints, np.array(nips))


def load_vam(vdir):
    v = sfio.read_vam_export(vdir)
    grole = lambda g: g.split('@', 1)[0]   # noqa: E731  ('Role@material' groups from tools/sources/humanoid.py)
    gi = [i for i, g in enumerate(v.groups) if grole(g) in VAM_SKIN]
    pts = v.v[np.unique(v.faces[np.isin(v.face_group, gi)])]
    vj = {k: j['pos'] for k, j in v.joints.items()}
    joints = {}
    for role, (_, vn) in J.items():
        for s, ls in (('L', 'l'), ('R', 'r')):
            if vn % ls in vj:
                joints[(role, s)] = vj[vn % ls]
    for role, (_, vn) in JM.items():
        if vn in vj:
            joints[(role, None)] = vj[vn]
    # nipples: the centre of VaM's 'Nipples' group per side (its lNipple / rNipple joints are not in the rest frame)
    nips = []
    ni = [i for i, g in enumerate(v.groups) if grole(g) == "Nipples"]
    if ni and ('hip', 'L') in joints and ('hip', 'R') in joints:
        nv = v.v[np.unique(v.faces[np.isin(v.face_group, ni)])]
        lpos = joints[('hip', 'L')][0] > joints[('hip', 'R')][0]
        for left in (True, False):
            sel = nv[(nv[:, 0] > 0) == (left == lpos)]
            if len(sel):
                nips.append(sel.mean(0))
    names, _, vsegs, _ = rig.label_segments()
    return Fig('VaM', pts, rig.labels(pts, vsegs, vj), names, joints, np.array(nips)), v


def forward(f):
    """+1 when the figure faces +z (its nipples are in front of the trunk's middle at their height), else -1."""
    y = f.nip[:, 1].mean()
    band = f.p[np.abs(f.p[:, 1] - y) < 0.01]
    return 1.0 if f.nip[:, 2].mean() > np.median(band[:, 2]) else -1.0


def girth(f, label, a, b, t0, t1):
    """Median radius of the points of `label` whose position along a->b is in [t0, t1], around the band's own centre
    in the plane across the axis (metres)."""
    pts = f.part(label)
    if len(pts) < 30:
        return np.nan
    u = b - a; L = np.linalg.norm(u); u /= L
    t = (pts - a) @ u / L
    band = pts[(t >= t0) & (t <= t1)]
    if len(band) < 20:
        return np.nan
    q = band - np.outer((band - a) @ u, u)
    return float(np.median(np.linalg.norm(q - q.mean(0), axis=1)))


def trunk_section(f, y, h=0.006):
    """Width (x extent), depth (z extent) and points of the trunk (no arms) in a horizontal band."""
    arm = [i for i, n in enumerate(f.names) if n.startswith(ARM_LABELS)]
    p = f.p[~np.isin(f.lab, arm)]
    p = p[np.abs(p[:, 1] - y) <= h]
    if len(p) < 10:
        return np.nan, np.nan, p
    return float(np.ptp(p[:, 0])), float(np.ptp(p[:, 2])), p


def mean_lr(fn):
    vals = [x for x in (fn('L'), fn('R')) if np.isfinite(x)]
    return float(np.mean(vals)) if vals else np.nan


def measure(f):
    """Every measure of one figure (metres)."""
    m = {}
    d = lambda r, s, r2: float(np.linalg.norm(f.jp(r, s) - f.jp(r2, s)))  # noqa: E731
    fw = forward(f)
    # nipple tips: the most forward skin point within 4 cm of each guess (the same landmark on both figures)
    tips = []
    for g in f.nip:
        near = f.p[np.linalg.norm(f.p - g, axis=1) < 0.04]
        tips.append(near[np.argmax(fw * near[:, 2])] if len(near) else g)
    f.tip = np.array(tips)
    knee_y = 0.5 * (f.jp('knee', 'L')[1] + f.jp('knee', 'R')[1])
    ankle_y = 0.5 * (f.jp('ankle', 'L')[1] + f.jp('ankle', 'R')[1])
    eye_y = 0.5 * (f.jp('eye', 'L')[1] + f.jp('eye', 'R')[1])
    nip_y = float(f.tip[:, 1].mean())
    hipy = 0.5 * (f.jp('hip', 'L')[1] + f.jp('hip', 'R')[1])   # only to place the search band
    ys = np.arange(hipy - 0.12, hipy + 0.08, 0.005)
    y_hip = float(ys[int(np.nanargmax([trunk_section(f, y)[0] for y in ys]))])
    m['eyeH'] = eye_y - ankle_y
    m['legUpper'] = y_hip - knee_y
    m['calfL'] = mean_lr(lambda s: d('knee', s, 'ankle'))
    m['torsoH'] = nip_y - y_hip
    m['upperH'] = eye_y - nip_y
    m['farmL'] = mean_lr(lambda s: d('elbow', s, 'wrist'))
    mid_x = 0.5 * (f.jp('wrist', 'L')[0] + f.jp('wrist', 'R')[0])
    # mid-line to elbow: sideways to the shoulder joint + shoulder joint to elbow (both rigs split this differently,
    # the sum is the same anatomy: shoulder breadth + upper arm)
    m['uarmReach'] = mean_lr(lambda s: abs(f.jp('shoulder', s)[0] - mid_x) + d('shoulder', s, 'elbow'))
    m['handL'] = mean_lr(lambda s: d('wrist', s, 'mid3'))

    def foot_len(s):   # heel to toe tip along the forward axis: the leg's skin below the ankle joint (labels split the heel)
        a = f.jp('ankle', s)
        p = f.part('foot' + s, 'shin' + s)
        p = p[(p[:, 1] < a[1]) & (np.abs(p[:, 0] - a[0]) < 0.08)]
        return float(np.ptp(p[:, 2])) if len(p) > 20 else np.nan
    m['footL'] = mean_lr(foot_len)
    m['hipW'], m['hipD'], pg = trunk_section(f, y_hip)
    y_ub = nip_y - 0.065 * m['eyeH'] / 1.45
    m['ubW'], m['ubD'], _ = trunk_section(f, y_ub)
    ys = np.arange(y_hip + 0.05, y_ub - 0.03, 0.005)
    y_w = float(ys[int(np.nanargmin([trunk_section(f, y)[0] for y in ys]))]) if len(ys) else 0.5 * (y_hip + y_ub)
    m['waistW'], m['waistD'], pw = trunk_section(f, y_w)
    m['thighR'] = mean_lr(lambda s: girth(f, 'thigh' + s, f.jp('hip', s), f.jp('knee', s), 0.30, 0.50))
    m['calfR'] = mean_lr(lambda s: girth(f, 'shin' + s, f.jp('knee', s), f.jp('ankle', s), 0.25, 0.40))
    m['uarmR'] = mean_lr(lambda s: girth(f, 'uparm' + s, f.jp('shoulder', s), f.jp('elbow', s), 0.45, 0.70))
    m['farmR'] = mean_lr(lambda s: girth(f, 'forearm' + s, f.jp('elbow', s), f.jp('wrist', s), 0.20, 0.40))
    m['neckR'] = girth(f, 'torso', f.jp('neck'), f.jp('head'), 0.35, 0.60)
    _, dn, _ = trunk_section(f, nip_y, 0.008)
    m['breastP'] = float(dn - m['ubD'])
    m['nipSp'] = float(abs(f.tip[0][0] - f.tip[1][0]))
    # the buttocks' fullest point is not at the widest hip on every figure (Genesis 9: below it): a 14 cm band round it
    _, _, pg = trunk_section(f, y_hip, 0.07)
    _, _, pw = trunk_section(f, y_w, 0.01)
    rear = lambda p: float((-fw * p[:, 2]).max())  # noqa: E731
    m['gluteP'] = rear(pg) - rear(pw) if len(pg) and len(pw) else np.nan
    hp = f.part('torso'); hp = hp[hp[:, 1] > eye_y - 0.03]
    band = hp[np.abs(hp[:, 1] - eye_y) < 0.01]
    m['headW'] = float(np.ptp(band[:, 0])) if len(band) > 10 else np.nan
    m['headH'] = float(hp[:, 1].max() - eye_y) if len(hp) else np.nan
    # her rig's own segments (what the Length sliders scale)
    m['thighBone'] = mean_lr(lambda s: d('hip', s, 'knee'))
    m['uarmBone'] = mean_lr(lambda s: d('shoulder', s, 'elbow'))
    m['neckBoneY'] = float(f.jp('head')[1] - f.jp('neck')[1])
    m['spineSegY'] = float(f.jp('chestj')[1] - f.jp('spine1')[1]) if ('chestj', None) in f.j and ('spine1', None) in f.j else np.nan
    return m


def fit(h, v):
    """Slider values from her measures h and VaM's v (already scaled). Returns ({slider: value}, notes)."""
    def r(k):
        return v[k] / h[k] if np.isfinite(v[k]) and np.isfinite(h[k]) and abs(h[k]) > 1e-6 else np.nan
    out, notes = {}, []

    def put(k, x, why):
        if not np.isfinite(x):
            notes.append('%s: not measured (%s)' % (k, why))
            return
        lo, hi = RANGE[k]
        c = float(np.clip(x, lo, hi))
        if abs(c - x) > 1e-4:
            notes.append('%s: %.3f clamped to %.3f (%s)' % (k, x, c, why))
        out[k] = c

    def grow(k, bone):   # a landmark distance's change put on a bone of length h[bone]
        return 1.0 + (v[k] - h[k]) / h[bone]

    put('thighs', r('thighR'), 'thigh girth')
    put('calves', r('calfR'), 'calf girth')
    put('upperarms', r('uarmR'), 'upper arm girth')
    put('forearms', r('farmR'), 'forearm girth')
    put('neck', r('neckR'), 'neck girth')
    put('thighLength', grow('legUpper', 'thighBone') / out.get('thighs', 1.0), 'widest hip to knee, / thigh size')
    put('calfLength', r('calfL') / out.get('calves', 1.0), 'knee to ankle, / calf size')
    put('forearmLength', r('farmL') / out.get('forearms', 1.0), 'elbow to wrist, / forearm size')
    put('armLength', grow('uarmReach', 'uarmBone') / out.get('upperarms', 1.0), 'mid-line to elbow, / upper arm size')
    if np.isfinite(h['spineSegY']) and h['spineSegY'] > 0.02:
        put('torsoLength', grow('torsoH', 'spineSegY'), 'widest hip to nipples, on the waist segment')
    # nipples to eyes spans the upper chest, the neck and the face: only the neck's share of it (her neck bone over the
    # distance) goes on the neck, the rest has no slider (noted)
    share = h['neckBoneY'] / h['upperH']
    put('neckLength', (1.0 + share * (v['upperH'] - h['upperH']) / h['neckBoneY']) / out.get('neck', 1.0),
        "nipples to eyes, the neck's share %.0f%%, / neck size" % (100 * share))
    if abs(v['upperH'] - h['upperH']) > 0.015:
        notes.append('nipples to eyes differs by %.0f mm; %.0f mm of it is above or below the neck (no slider: try Breasts > Lift '
                     'or Torso > Torso length by eye)' % (1000 * (v['upperH'] - h['upperH']), 1000 * (1 - share) * (v['upperH'] - h['upperH'])))
    put('waistWidth', r('waistW'), 'waist width'); put('waistDepth', r('waistD'), 'waist depth')
    put('ribcageWidth', r('ubW'), 'under-bust width'); put('ribcageDepth', r('ubD'), 'under-bust depth')
    put('hips', r('hipW'), 'widest hip width')
    rw = out.get('ribcageWidth', 1.0)
    put('breasts', r('breastP'), 'breast projection')
    put('breastSpacing', 100 * (v['nipSp'] - rw * h['nipSp']) / 2 / rw, 'nipple spacing after the ribcage width, per side')
    put('glutes', r('gluteP'), 'glute projection')
    put('hands', r('handL'), 'wrist to middle finger joint'); put('feet', r('footL'), 'heel to toe')
    put('head', np.sqrt(r('headW') * r('headH')), 'head width x height')
    for k in list(out):
        mv = RANGE[k][0] < 0
        if (mv and abs(out[k]) < NEUTRAL_EPS['Move']) or (not mv and abs(out[k] - 1) < NEUTRAL_EPS['Ratio']):
            del out[k]
    return out, notes


ROWS = [('eye height above the ankles', 'eyeH'), ('widest hip to knee', 'legUpper'), ('knee to ankle', 'calfL'),
        ('widest hip to nipples', 'torsoH'), ('nipples to eyes', 'upperH'), ('mid-line to elbow', 'uarmReach'),
        ('elbow to wrist', 'farmL'), ('wrist to middle finger joint', 'handL'), ('heel to toe', 'footL'),
        ('thigh radius', 'thighR'), ('calf radius', 'calfR'), ('upper arm radius', 'uarmR'), ('forearm radius', 'farmR'),
        ('neck radius', 'neckR'), ('widest hip width', 'hipW'), ('waist width', 'waistW'), ('waist depth', 'waistD'),
        ('under-bust width', 'ubW'), ('under-bust depth', 'ubD'), ('breast projection', 'breastP'),
        ('nipple spacing', 'nipSp'), ('glute projection', 'gluteP'), ('head width at the eyes', 'headW'),
        ('head top above the eyes', 'headH')]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--game', required=True)
    ap.add_argument('--vam', required=True)
    ap.add_argument('--out', required=True, help='preset file to write (SfMorphs\\presets\\<name> (fit).txt)')
    ap.add_argument('--name', default='')
    a = ap.parse_args()
    her = load_her(a.game)
    vam, vx = load_vam(a.vam)
    for f in (her, vam):
        miss = ['%s %s' % k for k in [(r, sd) for r in J for sd in 'LR'] + [(r, None) for r in ('neck', 'head')] if k not in f.j]
        if miss:
            raise SystemExit('%s skeleton lacks %s' % (f.tag, ', '.join(miss[:6])))
        if len(f.nip) != 2:
            raise SystemExit('%s: no nipples found (a female figure is needed)' % f.tag)
    h = measure(her)
    s = h['eyeH'] / measure(vam)['eyeH']
    vam.scale(s)
    v = measure(vam)
    sliders, notes = fit(h, v)
    name = a.name or vx.info.get('character', os.path.basename(os.path.normpath(a.vam)))
    lines = ['# SF Body Morphs look',
             '# slider fit of %s (VaM export %s) onto her (export %s), tools/pipeline/sliderfit.py' % (
                 name, os.path.basename(os.path.normpath(a.vam)), os.path.basename(os.path.normpath(a.game))),
             '# VaM scaled x%.4f to her eye height (proportions, not height)' % s,
             '# measure                          hers mm   VaM mm   ratio']
    for lbl, k in ROWS:
        lines.append('#   %-30s %7.1f  %7.1f   %s' % (lbl, 1000 * h[k], 1000 * v[k], '%.3f' % (v[k] / h[k]) if h[k] else '-'))
    lines += ['# note: ' + n for n in notes]
    order = [k for k in RANGE if k in sliders]
    lines += ['region\t%s\t%s' % (k, ('%.2f' % sliders[k]) if RANGE[k][0] < 0 else ('%.3f' % sliders[k])) for k in order]
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')
    for l in lines[1:]:
        log(l)
    log('wrote %s (%d sliders)' % (a.out, len(order)))


if __name__ == '__main__':
    main()
