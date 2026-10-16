"""Authored muscle definition maps for SF Body Morphs (0.9.9).

The muscle counterpart of tools/wrinkles/author.py: from her export (Presets tab -> Export) one pair of textures for her body atlas
("body" = the reika_body sheet, the same layout as the body wrinkles), blended by the plugin into the same normal-map copy as the
wrinkles and faded in per muscle by her pose:

  muscle_<layout>_detail.png  RGBA8 linear, NxN: R,G = slope of the muscle relief in tangent space (x along +u, y along +v, metres per
                              metre, *0.5/SLOPE_MAX + 0.5), B = groove depth 0..1 (the albedo's groove shade), A = coverage 0..1 (debug).
  muscle_<layout>_region.png  RGBA8 linear, MxM, point-fetched in the blend: R = first muscle index + 1 (0 = none), G = second muscle
                              index + 1 (0 = none), B = the first muscle's share 0..1 (the texel's activation = B x act[R] + (1-B) x
                              act[G]; the shader interpolates that between region texels), A = coverage.
  muscle_<layout>_height.png  (0.11.12) RGBA8 linear, MxM: the relief's signed height in 16 bits (R high, G low byte of
                              h / HEIGHT_MAX x 0.5 + 0.5), B = coverage; GpuContact's bake turns it into the tensed-muscle geometry.
  muscle_layouts.txt          the muscle table the plugin reads (index, name, area core|back|legs|arms, side L|R|C).

The relief is a height field on her bind-pose skin made of muscle bellies (smooth bumps) and the grooves between them (linea alba,
tendinous intersections, linea semilunaris, iliac V line, serratus digitations, costal margin, spinal groove, quad / hamstring /
gastrocnemius separations, deltoid insertion, triceps horseshoe ...). Every primitive is placed in a limb's own cylinder coordinates
(s = metres along the bone from its joint, phi = degrees around it: 0 front, +90 her outer side, 180 back) from her skeleton, so it lands
on any UV layout of the same body; the height and its analytic gradient are evaluated per texel in 3D (no finite differences across
UV seams). A texel belongs to the limb whose axis is closest (minus a nominal radius); a primitive only reaches the limbs it lists, so
an inner-thigh muscle never shows on the other thigh. Breasts and genitals carry nothing (exclusion spheres).

The shape of a fit body is the Characters pipeline's job (a converted VaM character's "Fitness" body slider = Bodybuilder Details /
Torso Fitness / Fitness Size morphs); this is the surface definition on top of whatever shape she has, so it works with or without one.
Reference look: a VaM preset with Torso Fitness 0.26, Body Tone 0.71, Back Definition 0.90, RibCageDefine 0.54 and a game-ported
torso / limbs normal map (linea alba, the upper abs' intersections, serratus lines, the iliac V, knee and calf detail).

Usage:  python tools/muscles/author.py [export dir] [--preview]
        (default: the newest export under <game>\\BepInEx\\config\\SfMorphs\\export; output into the shader bundle project)
Needs numpy only. --preview writes her colour map with the muscles drawn on and front / back / side renders of her bind-pose body lit
with the muscle relief (rest tone and full tension) into tools/muscles/preview (gitignored).
"""
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'pipeline'))
sys.path.insert(0, os.path.join(HERE, '..', 'wrinkles'))
import sfio  # noqa: E402
import author as wr  # noqa: E402  (the wrinkle author: PNG io, Surf, raster, dilate)

OUT = os.path.join(HERE, '..', 'shaders', 'SfMorphShaders', 'Assets', 'SfMorph', 'Muscles')
PREVIEW = os.path.join(HERE, 'preview')
GAME_EXPORT = r'E:\Bak\New folder\Misc\SF\VR\BepInEx\config\SfMorphs\export'
SLOPE_MAX = 0.6          # slope encoded in R,G: +-SLOPE_MAX -> 0..1 (muscles are gentler than creases: finer steps)
SHADE_DEPTH = 0.0015     # groove depth (m) that gives a full groove shade (B = 1)
BULGE_GAIN = 2.0         # muscle bellies: heights below are x this (the first preview read flat next to the grooves)
HEIGHT_MAX = 0.016       # 0.11.12: the height map's range, +- metres
LIMB_WIDTH = 1.6         # grooves on the limbs are wider than the torso's (thin ones read as drawn lines)

# ---------------------------------------------------------------- segments (her limbs as cylinders)


class Seg:
    def __init__(self, name, a, b, radius, side, ext=(0.0, 0.0)):
        self.name = name
        self.a = np.asarray(a, np.float64)
        self.b = np.asarray(b, np.float64)
        self.len = float(np.linalg.norm(self.b - self.a))
        self.axis = (self.b - self.a) / self.len
        self.radius = radius
        self.ext = ext                       # membership reaches this far beyond A / B (metres)
        self.frame_only = False              # 0.11.13: a coordinate frame for primitives only (texels stay members of the real segments)
        sx = -1.0 if side == 'L' else 1.0    # her left = -x; the torso uses her left as +phi
        f = np.array([0, 0, 1.0]) - self.axis[2] * self.axis
        self.front = f / np.linalg.norm(f)
        l = np.array([sx, 0, 0]) - np.dot([sx, 0, 0], self.axis) * self.axis - np.dot([sx, 0, 0], self.front) * self.front
        self.lat = l / np.linalg.norm(l)

    def coords(self, X):
        """-> s (metres along the axis from A), phi (radians, 0 front, +pi/2 lateral), r (distance from the axis), e_phi (unit, the
        direction of growing phi on the surface)."""
        d = X - self.a
        s = d @ self.axis
        rad = d - s[:, None] * self.axis
        r = np.linalg.norm(rad, axis=1)
        phi = np.arctan2(rad @ self.lat, rad @ self.front)
        e = np.cross(self.axis, rad)
        # e = axis x rad turns from front towards lat when lat = axis x front; else flip
        if np.dot(np.cross(self.axis, self.front), self.lat) < 0:
            e = -e
        e /= np.maximum(np.linalg.norm(e, axis=1), 1e-12)[:, None]
        return s, phi, r, e

    def member_dist(self, X):
        d = X - self.a
        s = np.clip(d @ self.axis, -self.ext[0], self.len + self.ext[1])
        c = self.a + s[:, None] * self.axis
        return np.linalg.norm(X - c, axis=1) - self.radius


# ---------------------------------------------------------------- primitives


class Bulge:
    """A muscle belly: h = height x exp(-(ds/a)^2 - (arc/b)^2), arc = r x dphi. Negative height = a hollow (a dimple, a fossa)."""

    def __init__(self, seg, s, phi, a, b, height_mm, segs=None):
        self.seg, self.s, self.phi, self.a, self.b, self.h = seg, s, math.radians(phi), a, b, height_mm * 0.001 * BULGE_GAIN
        self.segs = segs


class Groove:
    """The furrow between muscles along a polyline of (s, phi deg): h = -depth x taper x exp(-(d/w)^2), d = the distance in (s, arc)."""

    def __init__(self, seg, pts, depth_mm, width_mm, taper=(1, 1), segs=None):
        self.seg = seg
        self.p = np.array([(s, math.radians(f)) for s, f in pts], np.float64)
        self.d = depth_mm * 0.001
        self.w = width_mm * 0.001 * (1.0 if seg == 'torso' else LIMB_WIDTH)
        self.taper = taper
        self.segs = segs


class Belly:
    """0.11.13 a muscle as a mass, not a dot: a spindle along a centreline of (s, phi deg, half-width mm) points, with a rounded plateau
    across it (h = height x (1 - (d/w)^flat)^2 for d < w, d = the distance to the centreline in (s, arc)) and ends that round off
    (the centreline's ends are caps; the height eases to `end` of itself towards them). Bellies merge with their neighbours by a soft
    maximum (UNION_P), so two muscles side by side leave a soft valley where their edges meet instead of a drawn line."""

    def __init__(self, seg, pts, height_mm, flat=2.0, end=0.45, segs=None):
        self.seg = seg
        self.p = np.array([(s, math.radians(f)) for s, f, w in pts], np.float64)
        self.w = np.array([w * 0.001 for s, f, w in pts], np.float64)
        self.h = height_mm * 0.001
        self.flat, self.end = flat, end
        self.segs = segs


UNION_P = 3.0            # the soft maximum of overlapping bellies (higher = closer to the plain maximum)
BELLY_EPS = 0.0015       # metres: the step of the bellies' numeric gradient (in the limb's own s / arc plane: no UV seams)


def belly_eval(pr, si, ri_, pi, ds=0.0, da=0.0):
    """Height factor 0..1 of a Belly at texels (si, ri_, pi), the texel moved by ds along s and da along the arc."""
    q = pr.p
    best = np.full(len(si), np.inf)
    bu = np.zeros(len(si))
    bw = np.zeros(len(si))
    seglen = np.zeros(len(q))
    a0 = wrap(q[0, 1] - pi)
    pts = [(q[k, 0] - (si + ds), ri_ * (a0 + q[k, 1] - q[0, 1]) - da) for k in range(len(q))]
    for k in range(1, len(q)):
        seglen[k] = seglen[k - 1] + math.hypot(q[k, 0] - q[k - 1, 0], (q[k, 1] - q[k - 1, 1]) * 0.1)
    total = max(seglen[-1], 1e-9)
    for k in range(len(q) - 1):
        ax, ay = pts[k]
        bx, by = pts[k + 1]
        vx, vy = bx - ax, by - ay
        l2 = np.maximum(vx * vx + vy * vy, 1e-14)
        tt = np.clip(-(ax * vx + ay * vy) / l2, 0, 1)
        d = np.hypot(ax + tt * vx, ay + tt * vy)
        u = d < best
        best[u] = d[u]
        bu[u] = ((seglen[k] + tt * (seglen[k + 1] - seglen[k])) / total)[u]
        bw[u] = (pr.w[k] + tt * (pr.w[k + 1] - pr.w[k]))[u]
    x = np.clip(best / np.maximum(bw, 1e-6), 0, 1)
    across = (1 - x ** pr.flat) ** 2
    along = pr.end + (1 - pr.end) * np.sin(np.pi * np.clip(bu, 0, 1)) ** 0.6
    return across * along


def wrap(a):
    return (a + math.pi) % (2 * math.pi) - math.pi


# ---------------------------------------------------------------- her muscles


def muscles(sk, navel_s, log):
    """-> segments, regions [(name, area, side)], primitives per region index."""
    P = lambda b: np.asarray(sk[b]['pos'], np.float64)  # noqa: E731
    segs = {}
    root, neck = P('Root_M'), P('Neck_M')
    segs['torso'] = Seg('torso', root, neck, 0.10, 'L', ext=(0.08, 0.02))
    segs['neck'] = Seg('neck', neck, P('Head_M'), 0.05, 'L', ext=(0.0, 0.1))
    # 0.11.13: the back's frame: the torso axis runs along her lower back (2-4 cm under the skin at the waist), where a degree is a
    # millimetre; this one runs through the middle of her trunk, so the back muscles' angles mean the same width everywhere
    fr = segs['torso'].front
    segs['back'] = Seg('back', root + fr * 0.045, neck + fr * 0.03, 0.10, 'L')
    segs['back'].frame_only = True
    for sd in ('L', 'R'):
        segs['thigh' + sd] = Seg('thigh' + sd, P('Hip_' + sd), P('Knee_' + sd), 0.075, sd, ext=(0.03, 0.02))
        segs['shin' + sd] = Seg('shin' + sd, P('Knee_' + sd), P('Ankle_' + sd), 0.05, sd, ext=(0.02, 0.06))
        segs['upperarm' + sd] = Seg('upperarm' + sd, P('Shoulder_' + sd), P('Elbow_' + sd), 0.045, sd, ext=(0.03, 0.02))
        segs['forearm' + sd] = Seg('forearm' + sd, P('Elbow_' + sd), P('Wrist_' + sd), 0.035, sd, ext=(0.02, 0.0))
        w = P('Wrist_' + sd)
        segs['hand' + sd] = Seg('hand' + sd, w, w + segs['forearm' + sd].axis * 0.12, 0.03, sd)

    regions, prims = [], []

    def reg(name, area, side):
        regions.append((name, area, side))
        prims.append([])
        return len(regions) - 1

    nv = navel_s
    T = 'torso'
    B = 'back'      # the back's own frame (axis through the middle of her trunk)
    # 0.11.13 (user: the old sculpt was cartoonish: Gaussian dots and drawn lines): muscles are Belly masses along their fibres with
    # rounded plateaus that meet in soft valleys; the drawn grooves stay only where anatomy has a real line (linea alba, tendinous
    # intersections, spine, inguinal line, glute fold, the calf's lower edge) and are shallower and wider. Reference: a sculpted
    # athletic female (front / side / back): broad pecs over the breasts, abs as soft rounded blocks (the top pair smallest, the lower
    # abdomen one smooth mass), an oblique pad over the hip, serratus fingers under the arm; upper and mid traps, the scapula's
    # infraspinatus / teres masses, lats tapering to the waist, two thick erector columns in the lower back with a deep groove between;
    # vastus lateralis sweep and the inner knee teardrop; split hamstrings; calf heads (inner larger and lower) with a heart-shaped
    # lower edge into the Achilles, the soleus flaring below them; rounded three-head deltoids, triceps horseshoe, forearm masses.
    # ---- core: abs (one region, both sides), obliques, serratus, ribcage
    r_abs = reg('abs', 'core', 'C')
    # 0.11.17 (user: the abs were too uniform and symmetrical): real abdominals are not a grid. The two sides' tendinous intersections
    # sit at different heights (here the right ~6-8 mm higher), each one curves and slopes its own way, the linea alba wanders a little,
    # and the blocks differ in size and height: the middle pair the fullest, the top pair narrower and tilted up towards the ribs, each
    # block a pillow (narrower at its ends) rather than a rectangle. The lower abdomen is one smooth mass with a faint fourth line on one
    # side only.
    prims[r_abs].append(Groove(T, [(nv + 0.012, 0), (nv + 0.05, 0.8), (nv + 0.09, -0.6), (nv + 0.13, 1.0), (0.272, 0.3)], 0.65, 7.0))   # linea alba
    prims[r_abs].append(Groove(T, [(nv - 0.075, 0.4), (nv - 0.04, -0.3), (nv - 0.012, 0.2)], 0.25, 7.0))                                  # its faint lower part
    # per side: intersections (s inner, s middle, s outer above the navel, depth mm, middle phi offset deg), blocks (half-width mm, height mm)
    sides = {
        1: dict(cuts=((0.002, 0.004, 0.009, 0.22, 0.0), (0.055, 0.060, 0.069, 0.45, 0.5), (0.109, 0.116, 0.127, 0.5, -0.5)),
                top=0.150, blocks=((28, 2.5), (29, 2.8), (25, 2.1))),
        -1: dict(cuts=((0.009, 0.011, 0.013, 0.18, -0.6), (0.064, 0.067, 0.076, 0.42, 0.0), (0.117, 0.122, 0.135, 0.52, 0.8)),
                 top=0.157, blocks=((27, 2.3), (28, 2.6), (24, 2.3))),
    }
    for sgn in (1, -1):
        cfg = sides[sgn]
        cuts = cfg['cuts']
        for si_, sm_, so_, dep, dphi in cuts:
            prims[r_abs].append(Groove(T, [(nv + si_, 3 * sgn), (nv + sm_, (14 + dphi) * sgn), (nv + so_, 26 * sgn)], dep, 7.5, taper=(0, 1)))
        # the blocks between consecutive intersections (the last one up to the costal margin), pillow-shaped, the upper ones tilting outwards
        edges = [c[1] for c in cuts] + [cfg['top']]
        for k, (w, hgt) in enumerate(cfg['blocks']):
            lo, hi = nv + edges[k] + 0.004, nv + edges[k + 1] - 0.004
            tilt = 0.4 * k
            prims[r_abs].append(Belly(T, [(lo, (11.6 - 0.2 * k) * sgn, w - 7), ((lo + hi) / 2, (12.4 + tilt) * sgn, w), (hi, (12.6 + 2 * tilt) * sgn, w - 7 - k)],
                                      hgt, flat=2.5, end=0.7))
        prims[r_abs].append(Belly(T, [(nv - 0.075, 7 * sgn, 19), (nv - 0.035, 10 * sgn, 26), (nv - 0.006, 11.5 * sgn, 27)], 1.6 if sgn > 0 else 1.45, flat=2.6, end=0.6))  # lower abdomen
        prims[r_abs].append(Groove(T, [(nv - 0.07, 29 * sgn), (nv, 34 * sgn), (nv + 0.08, 36 * sgn), (0.265, 32 * sgn)], 0.45, 9.0))  # semilunaris
    prims[r_abs].append(Groove(T, [(nv - 0.036, 3), (nv - 0.033, 12), (nv - 0.028, 22)], 0.15, 7.0, taper=(0, 1)))   # a faint fourth line, her left only
    for sd, sgn in (('L', 1), ('R', -1)):
        r = reg('obliques_' + sd, 'core', sd)
        prims[r].append(Belly(T, [(0.015, 66 * sgn, 34), (0.07, 72 * sgn, 46), (0.15, 74 * sgn, 38), (0.21, 72 * sgn, 26)], 3.0, flat=1.7, end=0.45,
                              segs=(T, 'thighL', 'thighR')))  # flank pad
        prims[r].append(Belly(T, [(0.06, 42 * sgn, 13), (0.13, 46 * sgn, 17), (0.20, 50 * sgn, 13)], 1.6, flat=2.0))   # front sheet to the semilunaris
        prims[r].append(Groove(T, [(0.085, 54 * sgn), (0.03, 38 * sgn), (-0.045, 19 * sgn)], 0.9, 8.0))      # inguinal (V) line
        r = reg('serratus_' + sd, 'core', sd)
        for k in range(4):
            s0 = 0.205 + 0.026 * k
            prims[r].append(Belly(T, [(s0, 74 * sgn, 6), (s0 + 0.02, 98 * sgn, 8)], 1.5, flat=2.0, end=0.5))
    r_rib = reg('ribcage', 'core', 'C')
    for sgn in (1, -1):
        prims[r_rib].append(Groove(T, [(0.272, 2 * sgn), (0.245, 18 * sgn), (0.205, 38 * sgn), (0.175, 58 * sgn), (0.165, 72 * sgn)], 0.45, 10.0, taper=(0, 1)))
    prims[r_rib].append(Groove(T, [(0.27, 0), (0.39, 0)], 0.5, 7.0))                             # sternum
    # ---- back: erectors (one region), lats + scapula per side
    r_back = reg('back', 'back', 'C')
    prims[r_back].append(Groove(B, [(-0.01, 180), (0.47, 180)], 1.4, 7.0))                       # spinal groove
    for sgn in (1, -1):
        # the erector columns: thick in the lower back (2 cm either side of the spine), thinning up the thorax
        prims[r_back].append(Belly(B, [(-0.03, 180 - 15 * sgn, 20), (0.06, 180 - 19 * sgn, 26), (0.16, 180 - 20 * sgn, 25), (0.26, 180 - 16 * sgn, 19),
                                       (0.38, 180 - 12 * sgn, 13)], 4.2, flat=1.8, end=0.3))
        prims[r_back].append(Belly(B, [(0.0, 180 - 5 * sgn, 10), (0.05, 180 - 6 * sgn, 8)], -1.2, flat=2.0))       # the sacral diamond's hollow
        prims[r_back].append(Bulge(B, 0.05, 180 - 17 * sgn, 0.012, 0.014, -0.9))                    # dimples of Venus
    for sd, sgn in (('L', 1), ('R', -1)):
        ua = 'upperarm' + sd
        r = reg('lats_' + sd, 'back', sd)
        prims[r].append(Belly(B, [(0.40, 114 * sgn, 30), (0.32, 124 * sgn, 60), (0.23, 136 * sgn, 58), (0.14, 150 * sgn, 40), (0.08, 158 * sgn, 22)],
                              2.4, flat=1.6, end=0.35, segs=(B, ua)))
        prims[r].append(Belly(B, [(0.44, 128 * sgn, 22), (0.40, 122 * sgn, 26), (0.36, 117 * sgn, 16)], 2.4, flat=1.8, segs=(B, ua)))   # teres major
        prims[r].append(Belly(B, [(0.47, 148 * sgn, 26), (0.43, 141 * sgn, 34), (0.39, 146 * sgn, 22)], 2.2, flat=1.8, segs=(B, ua)))   # infraspinatus
        prims[r].append(Groove(B, [(0.48, 158 * sgn), (0.42, 156 * sgn), (0.36, 160 * sgn)], 0.45, 11.0))   # the scapula's inner edge
    # ---- legs
    for sd in ('L', 'R'):
        sgn = 1 if sd == 'L' else -1
        th, sh = 'thigh' + sd, 'shin' + sd
        both = (T, th)
        r = reg('glutes_' + sd, 'legs', sd)
        prims[r].append(Bulge(T, -0.06, 108 * sgn, 0.045, 0.035, -1.3, segs=both))               # the hollow at the side
        prims[r].append(Belly(T, [(0.06, 112 * sgn, 22), (0.02, 118 * sgn, 30), (-0.02, 125 * sgn, 22)], 2.6, flat=2.0, segs=both))   # gluteus medius
        prims[r].append(Belly(T, [(0.02, 158 * sgn, 30), (-0.04, 150 * sgn, 50), (-0.09, 140 * sgn, 38)], 3.2, flat=1.8, end=0.6, segs=both))   # maximus
        prims[r].append(Groove(th, [(0.15, 140), (0.172, 165), (0.178, 190), (0.165, 215)], 0.9, 8.0, segs=both))  # glute fold
        r = reg('quads_' + sd, 'legs', sd)
        prims[r].append(Belly(th, [(0.05, 12, 18), (0.18, 6, 26), (0.31, 3, 18), (0.36, 2, 10)], 3.4, flat=2.2))            # rectus femoris
        prims[r].append(Belly(th, [(0.05, 72, 26), (0.18, 64, 38), (0.30, 50, 30), (0.37, 36, 16)], 4.4, flat=2.0))          # vastus lateralis
        prims[r].append(Belly(th, [(0.24, -24, 18), (0.31, -32, 26), (0.37, -26, 18)], 4.6, flat=1.8, end=0.55, segs=(th, sh)))   # vastus medialis
        prims[r].append(Groove(th, [(0.10, 98), (0.36, 102)], 0.6, 8.0))                                                     # iliotibial band
        prims[r].append(Belly(th, [(0.385, 0, 14), (0.405, 0, 12)], -1.2, flat=2.0))                                          # above the kneecap
        r = reg('hams_' + sd, 'legs', sd)
        prims[r].append(Belly(th, [(0.07, 152, 24), (0.20, 146, 32), (0.33, 132, 16)], 3.6, flat=1.8, segs=(th, sh, T)))      # biceps femoris
        prims[r].append(Belly(th, [(0.07, 205, 24), (0.20, 210, 32), (0.33, 222, 16)], 3.6, flat=1.8, segs=(th, sh, T)))      # semitendinosus / membranosus
        prims[r].append(Groove(th, [(0.16, 178), (0.34, 180)], 0.6, 8.0))
        r = reg('adductors_' + sd, 'legs', sd)
        prims[r].append(Belly(th, [(0.02, -78, 26), (0.11, -92, 30), (0.22, -108, 18)], 3.0, flat=2.0))
        prims[r].append(Groove(th, [(0.03, 30), (0.15, -20), (0.28, -55), (0.36, -75)], 0.5, 8.0))   # sartorius
        r = reg('calves_' + sd, 'legs', sd)
        knee = (sh, th)   # the heads start above the knee joint: they reach the back of the knee (thigh texels)
        prims[r].append(Belly(sh, [(-0.01, -162, 20), (0.08, -150, 30), (0.165, -140, 20)], 6.0, flat=1.8, end=0.45, segs=knee))   # gastrocnemius, inner head
        prims[r].append(Belly(sh, [(-0.01, 160, 19), (0.07, 148, 26), (0.135, 140, 16)], 5.0, flat=1.8, end=0.45, segs=knee))      # outer head
        prims[r].append(Groove(sh, [(0.03, 180), (0.13, 180)], 0.6, 6.0))
        prims[r].append(Groove(sh, [(0.15, 120), (0.18, 160), (0.20, 185), (0.215, 212), (0.19, 245)], 0.7, 8.0))      # the heart-shaped lower edge
        prims[r].append(Belly(sh, [(0.15, 110, 13), (0.26, 116, 10)], 2.0, flat=2.0))                                    # soleus, outer
        prims[r].append(Belly(sh, [(0.16, -114, 13), (0.27, -122, 10)], 2.0, flat=2.0))                                  # soleus, inner
        prims[r].append(Belly(sh, [(0.04, 30, 14), (0.15, 28, 14), (0.26, 20, 8)], 2.2, flat=2.0))                       # tibialis anterior
        prims[r].append(Groove(sh, [(0.05, 12), (0.30, 5)], 0.4, 7.0))                                                    # the shin
    # ---- arms
    for sd in ('L', 'R'):
        ua, fa = 'upperarm' + sd, 'forearm' + sd
        r = reg('deltoids_' + sd, 'arms', sd)
        prims[r].append(Belly(ua, [(-0.015, 32, 18), (0.04, 42, 18), (0.095, 75, 9)], 3.6, flat=2.0, segs=(ua, T)))      # front head
        prims[r].append(Belly(ua, [(-0.015, 90, 22), (0.05, 90, 24), (0.105, 86, 9)], 4.2, flat=2.0, segs=(ua, T)))     # side head
        prims[r].append(Belly(ua, [(-0.015, 148, 18), (0.04, 138, 18), (0.095, 102, 9)], 3.2, flat=2.0, segs=(ua, T)))   # rear head
        r = reg('biceps_' + sd, 'arms', sd)
        prims[r].append(Belly(ua, [(0.06, 0, 15), (0.12, 2, 20), (0.17, 6, 12)], 4.0, flat=2.0))
        prims[r].append(Belly(ua, [(0.11, 55, 10), (0.17, 50, 9)], 1.6, flat=2.0))                         # brachialis, outer side
        r = reg('triceps_' + sd, 'arms', sd)
        prims[r].append(Belly(ua, [(0.03, 198, 17), (0.09, 200, 20), (0.14, 190, 11)], 3.6, flat=2.0))     # long head
        prims[r].append(Belly(ua, [(0.03, 138, 15), (0.08, 135, 18), (0.13, 150, 9)], 3.4, flat=2.0))      # lateral head
        prims[r].append(Groove(ua, [(0.06, 120), (0.135, 145), (0.15, 180), (0.135, 215), (0.07, 240)], 0.6, 7.0))   # horseshoe
        r = reg('forearms_' + sd, 'arms', sd)
        prims[r].append(Belly(fa, [(-0.01, 62, 14), (0.05, 55, 16), (0.12, 45, 9)], 3.2, flat=2.0))        # brachioradialis
        prims[r].append(Belly(fa, [(0.0, -45, 17), (0.07, -40, 17), (0.15, -30, 9)], 2.6, flat=2.0))       # flexors
        prims[r].append(Belly(fa, [(0.0, 140, 15), (0.08, 150, 14), (0.15, 160, 8)], 2.4, flat=2.0))       # extensors
    # ---- 0.11.13 new: pecs (with the arms) and traps (with the back)
    for sd, sgn in (('L', 1), ('R', -1)):
        r = reg('pecs_' + sd, 'arms', sd)
        prims[r].append(Belly(T, [(0.445, 5 * sgn, 22), (0.44, 32 * sgn, 30), (0.425, 58 * sgn, 24), (0.41, 76 * sgn, 12)], 3.2, flat=1.8, end=0.5,
                              segs=(T, 'upperarm' + sd, 'neck')))
        prims[r].append(Groove(T, [(0.47, 70 * sgn), (0.43, 80 * sgn), (0.39, 86 * sgn)], 0.5, 8.0))   # deltopectoral groove
    r_tr = reg('traps', 'back', 'C')
    for sgn in (1, -1):
        sides = (B, 'neck', 'upperarmL', 'upperarmR')
        prims[r_tr].append(Belly(B, [(0.555, 175 * sgn, 22), (0.52, 148 * sgn, 32), (0.485, 118 * sgn, 20)], 2.4, flat=1.6, end=0.4, segs=sides))   # upper
        prims[r_tr].append(Belly(B, [(0.51, 176 * sgn, 22), (0.44, 166 * sgn, 34), (0.36, 174 * sgn, 18)], 2.0, flat=1.6, end=0.4, segs=sides))   # middle
    n = sum(len(p) for p in prims)
    log('muscles: %d primitives in %d regions (navel at s %.3f m above Root_M)' % (n, len(regions), navel_s))
    return segs, regions, prims


def find_navel(surf, segs, log):
    """The navel: the deepest dip of her front midline between 0.05 and 0.20 m above Root_M (rays from the front)."""
    T = segs['torso']
    best, bs = None, 0.12
    zs = []
    for s in np.arange(0.04, 0.20, 0.002):
        o = T.a + T.axis * s + T.front * 0.4
        h = surf.ray(o, -T.front)
        zs.append((s, (h - T.a) @ T.front if h is not None else np.nan))
    zs = np.array(zs)
    ok = ~np.isnan(zs[:, 1])
    if ok.sum() > 10:
        z = zs[ok]
        # depth below the straight line through the neighbours 2 cm away
        dip = []
        for i in range(len(z)):
            j0 = np.argmin(np.abs(z[:, 0] - (z[i, 0] - 0.02)))
            j1 = np.argmin(np.abs(z[:, 0] - (z[i, 0] + 0.02)))
            dip.append(0.5 * (z[j0, 1] + z[j1, 1]) - z[i, 1])
        dip = np.array(dip)
        k = int(np.argmax(dip))
        if dip[k] > 0.002:
            best, bs = z[k, 0], dip[k]
    if best is None:
        log('navel: no clear dip on the midline; using s 0.12')
        return 0.12
    log('navel: s %.3f m above Root_M (y %.3f), %.1f mm deep' % (best, (T.a + T.axis * best)[1], bs * 1000))
    return float(best)


# ---------------------------------------------------------------- evaluation


def evaluate(surf, X, N, t, segs, regions, prims, excl, log):
    M = len(X)
    names = [k for k in segs.keys() if not segs[k].frame_only]
    md = np.stack([segs[k].member_dist(X) for k in names], 1)
    member = md.argmin(1)
    cache = {}

    def seg_coords(k):
        if k not in cache:
            cache[k] = segs[k].coords(X)
        return cache[k]

    keep = np.ones(M)
    for ec, er in excl:
        keep *= np.clip((np.linalg.norm(X - ec, axis=1) - er) / 0.015, 0, 1)
    G = np.zeros((M, 3))
    H = np.zeros(M)          # 0.11.12: the height itself (m, signed), for the geometry bake
    Sp = np.zeros(M)         # 0.11.13: the bellies' soft maximum: sum of h^P and of h^(P-1) x grad
    Gp = np.zeros((M, 3))
    shade = np.zeros(M)
    wreg = np.zeros((M, len(regions)), np.float32)
    for ri, plist in enumerate(prims):
        for pr in plist:
            sg = segs[pr.seg]
            allowed = [names.index('torso' if segs[x].frame_only else x) for x in (pr.segs or (pr.seg,))]
            s, phi, r, e = seg_coords(pr.seg)
            pooled = False
            if isinstance(pr, Belly):
                q = pr.p
                lo, hi = q[:, 0].min() - pr.w.max() - 0.01, q[:, 0].max() + pr.w.max() + 0.01
                m = (s > lo) & (s < hi) & np.isin(member, allowed)
                if not m.any():
                    log('  %s: a belly on %s reaches no texel' % (regions[ri][0], pr.seg))
                    continue
                ii = np.nonzero(m)[0]
                si, ri_, pi = s[ii], r[ii], phi[ii]
                f0 = belly_eval(pr, si, ri_, pi)
                nz = f0 > 0
                if not nz.any():
                    continue
                ii, si, ri_, pi, f0 = ii[nz], si[nz], ri_[nz], pi[nz], f0[nz]
                e2 = BELLY_EPS
                fs = (belly_eval(pr, si, ri_, pi, ds=e2) - belly_eval(pr, si, ri_, pi, ds=-e2)) / (2 * e2)
                fa = (belly_eval(pr, si, ri_, pi, da=e2) - belly_eval(pr, si, ri_, pi, da=-e2)) / (2 * e2)
                g = f0 * keep[ii]
                h = pr.h * g
                grad = (pr.h * keep[ii])[:, None] * (fs[:, None] * sg.axis[None] + fa[:, None] * e[ii])
                env = g * abs(pr.h) / 0.002
                pooled = pr.h > 0
            elif isinstance(pr, Bulge):
                ds = s - pr.s
                arc = r * wrap(phi - pr.phi)
                m = (np.abs(ds) < 3 * pr.a) & (np.abs(arc) < 3 * pr.b) & np.isin(member, allowed)
                if not m.any():
                    log('  %s: a bulge at s %.2f phi %.0f on %s reaches no texel' % (regions[ri][0], pr.s, math.degrees(pr.phi), pr.seg))
                    continue
                ii = np.nonzero(m)[0]
                g = np.exp(-(ds[ii] / pr.a) ** 2 - (arc[ii] / pr.b) ** 2) * keep[ii]
                h = pr.h * g
                grad = h[:, None] * (-2 * ds[ii, None] / pr.a ** 2 * sg.axis[None] - 2 * arc[ii, None] / pr.b ** 2 * e[ii])
                env = g * abs(pr.h) / 0.002
                pooled = pr.h > 0
                if pr.h < 0:
                    shade[ii] += -h / SHADE_DEPTH * 0.3
            else:
                q = pr.p
                lo, hi = q[:, 0].min() - 3 * pr.w - 0.01, q[:, 0].max() + 3 * pr.w + 0.01
                m = (s > lo) & (s < hi) & np.isin(member, allowed)
                if not m.any():
                    log('  %s: a groove on %s reaches no texel' % (regions[ri][0], pr.seg))
                    continue
                ii = np.nonzero(m)[0]
                si, ri_, pi = s[ii], r[ii], phi[ii]
                # the polyline in this texel's (s, arc) plane: arc of each point relative to the texel
                best = np.full(len(ii), np.inf)
                bvec = np.zeros((len(ii), 2))
                bpos = np.zeros(len(ii))
                seglen = np.concatenate([[0], np.cumsum(np.hypot(np.diff(q[:, 0]), np.diff(q[:, 1]) * 0.1))])
                total = max(seglen[-1], 1e-9)
                # the points' phi are continuous along the line (140, 190, 215 deg): unwrap from the first point only, so a line
                # around the back never jumps across the front of the limb
                a0 = wrap(q[0, 1] - pi)
                for k in range(len(q) - 1):
                    ax, ay = q[k, 0] - si, ri_ * (a0 + q[k, 1] - q[0, 1])
                    bx, by = q[k + 1, 0] - si, ri_ * (a0 + q[k + 1, 1] - q[0, 1])
                    vx, vy = bx - ax, by - ay
                    l2 = np.maximum(vx * vx + vy * vy, 1e-14)
                    tt = np.clip(-(ax * vx + ay * vy) / l2, 0, 1)
                    cx, cy = ax + tt * vx, ay + tt * vy          # closest point relative to the texel
                    d = np.hypot(cx, cy)
                    u = d < best
                    best[u] = d[u]
                    bvec[u, 0] = cx[u]
                    bvec[u, 1] = cy[u]
                    bpos[u] = (seglen[k] + tt[u] * (seglen[k + 1] - seglen[k])) / total
                tap = wr.taper_fn(bpos, pr.taper)
                g = np.exp(-(best / pr.w) ** 2) * tap * keep[ii]
                h = -pr.d * g
                # dh/dd = 2 d D g / w^2, along the direction from the line to the texel (= -bvec / d)
                dd = 2 * best * pr.d * g / pr.w ** 2
                inv = 1.0 / np.maximum(best, 1e-9)
                gs, ga = -bvec[:, 0] * inv * dd, -bvec[:, 1] * inv * dd
                grad = gs[:, None] * sg.axis[None] + ga[:, None] * e[ii]
                env = g * pr.d / 0.001
                shade[ii] += -h / SHADE_DEPTH
            nn = N[ii]
            grad = grad - (grad * nn).sum(1)[:, None] * nn
            if pooled:
                hp = np.maximum(h, 0)
                Sp[ii] += hp ** UNION_P
                Gp[ii] += (hp ** (UNION_P - 1))[:, None] * grad
            else:
                G[ii] += grad
                H[ii] += h
            wreg[ii, ri] += env.astype(np.float32)
    # the bellies' soft maximum on top of the grooves and hollows
    Hp = Sp ** (1.0 / UNION_P)
    on = Hp > 1e-7
    H[on] += Hp[on]
    G[on] += Gp[on] / (Hp[on] ** (UNION_P - 1))[:, None]
    # slope in the texel's tangent frame (tangent = d pos/du, bitangent = d pos/dv, normalised), as the wrinkle maps
    pu, pv = surf.pu[t], surf.pv[t]
    tu = pu / np.maximum(np.linalg.norm(pu, axis=1), 1e-12)[:, None]
    tv = pv / np.maximum(np.linalg.norm(pv, axis=1), 1e-12)[:, None]
    sx = (G * tu).sum(1)
    sy = (G * tv).sum(1)
    order = np.argsort(-wreg, axis=1)[:, :2]
    w1 = wreg[np.arange(M), order[:, 0]]
    w2 = wreg[np.arange(M), order[:, 1]]
    tot = wreg.sum(1)
    return dict(G=G, H=H, sx=sx, sy=sy, shade=np.clip(shade, 0, 1), id1=order[:, 0], id2=order[:, 1], w1=w1, w2=w2, tot=tot, member=member)


def build(name, surf, sk, n, nreg, log, preview_dir=None, colour=None):
    log('%s: %d triangles, rasterising %dx%d' % (name, len(surf.f), n, n))
    tri, bary = surf.raster(n)
    cov = tri >= 0
    idx = np.nonzero(cov)[0]
    t = tri[idx]
    X = (surf.v[surf.f[t]] * bary[idx][:, :, None]).sum(1)
    N = (surf.vn[surf.f[t]] * bary[idx][:, :, None]).sum(1)
    N /= np.maximum(np.linalg.norm(N, axis=1), 1e-12)[:, None]
    log('%s: %.1f%% of the texels on her skin' % (name, 100.0 * cov.mean()))
    P = lambda b: np.asarray(sk[b]['pos'], np.float64)  # noqa: E731
    segs0 = {'torso': Seg('torso', P('Root_M'), P('Neck_M'), 0.1, 'L')}
    nav = find_navel(surf, segs0, log)
    segs, regions, prims = muscles(sk, nav, log)
    excl = []
    for side in ('Left', 'Right'):
        c = P(side + '_LowerBreast')
        rr = max(np.linalg.norm(P(side + '_Breast_' + k) - c) for k in ('out', 'up', 'down', 'in')) + 0.02
        excl.append((c, rr))
    # genitals: a sphere below the pubis, between the thighs
    root = P('Root_M')
    excl.append((root + np.array([0, -0.08, 0.035]), 0.045))
    ev = evaluate(surf, X, N, t, segs, regions, prims, excl, log)
    sx, sy = ev['sx'], ev['sy']
    smax = np.abs(np.concatenate([sx, sy])).max()
    log('%s: slope max %.3f (encoded up to %.2f), groove shade max %.2f' % (name, smax, SLOPE_MAX, ev['shade'].max()))

    det = np.zeros((n * n, 4), np.float32)
    det[:, 0] = det[:, 1] = 0.5
    det[idx, 0] = 0.5 + 0.5 * np.clip(sx / SLOPE_MAX, -1, 1)
    det[idx, 1] = 0.5 + 0.5 * np.clip(sy / SLOPE_MAX, -1, 1)
    det[idx, 2] = ev['shade']
    det[idx, 3] = np.clip(ev['tot'], 0, 1)
    det = wr.dilate(det.reshape(n, n, 4), cov.reshape(n, n), 6)

    # 0.11.12 height map at nreg (the geometry: GpuContact's bake reads it per vertex and scales each muscle's height by its
    # activation): the signed height in 16 bits, R = high byte, G = low byte of (h / HEIGHT_MAX x 0.5 + 0.5) x 65535; 2x2 mean of the
    # full-res texels, dilated (the bake samples it bilinearly at the vertex UV), B = coverage
    hf = np.zeros((n * n, 1), np.float32)
    hf[idx, 0] = ev['H']
    hf = wr.dilate(hf.reshape(n, n, 1), cov.reshape(n, n), 8)
    st = n // nreg
    hr = hf.reshape(nreg, st, nreg, st).mean(axis=(1, 3))
    cr = cov.reshape(nreg, st, nreg, st).any(axis=(1, 3))
    q = np.clip((hr / HEIGHT_MAX * 0.5 + 0.5) * 65535 + 0.5, 0, 65535).astype(np.uint32)
    hgt = np.zeros((nreg, nreg, 4), np.uint8)
    hgt[:, :, 0] = (q >> 8).astype(np.uint8)
    hgt[:, :, 1] = (q & 255).astype(np.uint8)
    hgt[:, :, 2] = np.where(cr, 255, 0).astype(np.uint8)
    hgt[:, :, 3] = 255
    log('%s: height %.2f to %.2f mm (encoded +-%.0f mm)' % (name, ev['H'].min() * 1000, ev['H'].max() * 1000, HEIGHT_MAX * 1000))

    # region map at nreg: id pair + share, from the full-res texels (nearest), dilated the same way so the shader's bilinear
    # interpolation of the activation never pulls in "none" at an island edge
    has = ev['tot'] > 0.01
    full = np.zeros((n * n, 4), np.float32)
    full[idx, 0] = np.where(has, ev['id1'] + 1, 0)
    full[idx, 1] = np.where(has & (ev['w2'] > 0.02), ev['id2'] + 1, 0)
    share = ev['w1'] / np.maximum(ev['w1'] + ev['w2'], 1e-9)
    full[idx, 2] = np.where(has, np.where(full[idx, 1] > 0, share, 1.0), 0)
    full[idx, 3] = np.clip(ev['tot'], 0, 1)
    step = n // nreg
    sel = (np.arange(nreg) * step + step // 2)
    pick = (sel[:, None] * n + sel[None, :]).ravel()
    covc = cov.reshape(nreg, step, nreg, step).any(axis=(1, 3))
    reg = full[pick].reshape(nreg, nreg, 4)
    # ids must not be averaged: dilate by copying the neighbour (the wrinkle dilate averages; here take the first covered neighbour)
    rc = reg.copy()
    cv = covc.copy() & (reg[:, :, 0] > 0)
    for _ in range(3):
        grow = np.zeros_like(cv)
        for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0)):
            sh = np.roll(np.roll(cv, dy, 0), dx, 1)
            nb = np.roll(np.roll(rc, dy, 0), dx, 1)
            take = sh & ~cv & ~grow
            rc[take] = nb[take]
            rc[take, 3] = 0
            grow |= take
        cv |= grow
    out = np.zeros((nreg, nreg, 4), np.uint8)
    out[:, :, 0] = rc[:, :, 0].astype(np.uint8)
    out[:, :, 1] = rc[:, :, 1].astype(np.uint8)
    out[:, :, 2] = np.clip(rc[:, :, 2] * 255 + 0.5, 0, 255).astype(np.uint8)
    out[:, :, 3] = np.clip(rc[:, :, 3] * 255 + 0.5, 0, 255).astype(np.uint8)
    for k, (rn, ra, rs) in enumerate(regions):
        m1 = (ev['id1'] == k) & has
        log('  muscle %2d %-13s %-4s %s: %7d texels, height up to %.1f mm' % (k + 1, rn, ra, rs, m1.sum(), ev['H'][m1].max() * 1000 if m1.any() else 0))
    if preview_dir:
        previews(name, n, idx, X, N, ev, regions, colour, preview_dir, log)
    return det, out, hgt, regions


# ---------------------------------------------------------------- previews


HUE = np.array([[1, .25, .25], [.25, 1, .25], [.3, .45, 1], [1, 1, .25], [1, .25, 1], [.25, 1, 1], [1, .6, .2], [.6, .25, 1], [.6, 1, .5], [1, .7, .8]])


def previews(name, n, idx, X, N, ev, regions, colour, d, log):
    os.makedirs(d, exist_ok=True)
    img = np.full((n, n, 3), 110.0)
    if colour is not None:
        ys = (np.arange(n) * colour.shape[0] // n)
        xs = (np.arange(n) * colour.shape[1] // n)
        img = colour[ys][:, xs].astype(np.float64)
    flat = img.reshape(-1, 3).copy()
    # 1) regions tinted + relief shading on the colour map (UV layout)
    has = ev['tot'] > 0.01
    tint = HUE[ev['id1'] % len(HUE)]
    a = (0.35 * has * np.clip(ev['tot'], 0, 1))[:, None]
    shadeL = lambert(N - ev['G'] * 1.0, np.array([-0.35, 0.75, 0.55]))
    reg_img = flat.copy()
    reg_img[idx] = flat[idx] * (1 - a) + tint * 255 * a
    reg_img[idx] *= (0.55 + 0.45 * shadeL)[:, None] / 0.85
    wr.write_png(os.path.join(d, 'muscle_%s_layout.png' % name), np.clip(reg_img.reshape(n, n, 3), 0, 255).astype(np.uint8))
    # 2) her body from the front, the back and her left side, lit: plain, rest tone (0.3), full tension (1.0)
    base_col = flat[idx] / 255.0
    rows = []
    for vname, axis_x, axis_z in (('front', np.array([1, 0, 0.0]), np.array([0, 0, 1.0])), ('back', np.array([-1, 0, 0.0]), np.array([0, 0, -1.0])),
                                  ('side', np.array([0, 0, 1.0]), np.array([-1, 0, 0.0]))):
        L = 0.6 * axis_z + 0.7 * np.array([0, 1.0, 0]) - 0.35 * axis_x     # a key light above, beside and in front of the viewer
        row = []
        for label, k in (('plain', 0.0), ('rest 0.3', 0.3), ('full', 1.0)):
            Nn = N - ev['G'] * k
            sh = lambert(Nn, L)
            c = base_col * (0.3 + 0.75 * sh[:, None]) * (1 - 0.3 * k * ev['shade'])[:, None]
            row.append(splat(X, c, axis_x, axis_z))
        rows.append(np.concatenate(row, 1))
    wr.write_png(os.path.join(d, 'muscle_%s_render.png' % name), np.clip(np.concatenate(rows, 0) * 255, 0, 255).astype(np.uint8))
    # 0.11.13: the geometry (what the GPU bake makes of the height map at strong flex): clay, the texel points pushed out by H x k with
    # the sculpt's own normals; full body front / side / back and close-ups of the torso front, the back and the legs from behind
    k = 0.010 / max(ev['H'].max(), 1e-4)   # the highest belly at 10 mm
    Xg = X + N * (ev['H'] * k)[:, None]
    Ng = N - ev['G'] * k
    clay = np.array([0.62, 0.56, 0.5])
    views = (('front', np.array([1, 0, 0.0]), np.array([0, 0, 1.0])), ('side', np.array([0, 0, 1.0]), np.array([-1, 0, 0.0])),
             ('back', np.array([-1, 0, 0.0]), np.array([0, 0, -1.0])))

    def shade(ax, az):
        L = 0.55 * az + 0.8 * np.array([0, 1.0, 0]) - 0.45 * ax
        return clay[None] * (0.18 + 0.85 * lambert(Ng, L)[:, None])
    row = [splat_win(Xg, shade(ax, az), ax, az, (-0.45, 0.45), (0.0, 1.75), 1100) for _, ax, az in views]
    wr.write_png(os.path.join(d, 'muscle_%s_geo.png' % name), np.clip(np.concatenate(row, 1) * 255, 0, 255).astype(np.uint8))
    close = [splat_win(Xg, shade(views[0][1], views[0][2]), views[0][1], views[0][2], (-0.22, 0.22), (0.85, 1.45), 1100),
             splat_win(Xg, shade(views[2][1], views[2][2]), views[2][1], views[2][2], (-0.22, 0.22), (0.85, 1.45), 1100),
             splat_win(Xg, shade(views[2][1], views[2][2]), views[2][1], views[2][2], (-0.25, 0.25), (0.02, 0.95), 1100),
             splat_win(Xg, shade(views[1][1], views[1][2]), views[1][1], views[1][2], (-0.25, 0.25), (0.02, 0.95), 1100),
             splat_win(Xg, shade(views[0][1], views[0][2]), views[0][1], views[0][2], (-0.25, 0.25), (0.02, 0.95), 1100)]
    wr.write_png(os.path.join(d, 'muscle_%s_geo_close.png' % name), np.clip(np.concatenate(close, 1) * 255, 0, 255).astype(np.uint8))
    # the abdomen up close under a light from above and one from the side
    fa, fz = views[0][1], views[0][2]
    abs_ = []
    for Lx in (np.array([0.25, 0.6, 0.75]), np.array([0.8, 0.25, 0.55])):
        cl = clay[None] * (0.18 + 0.85 * lambert(Ng, Lx)[:, None])
        abs_.append(splat_win(Xg, cl, fa, fz, (-0.12, 0.12), (0.92, 1.24), 1000, fat=3))
    wr.write_png(os.path.join(d, 'muscle_%s_geo_abs.png' % name), np.clip(np.concatenate(abs_, 1) * 255, 0, 255).astype(np.uint8))
    log('preview: %s' % d)


def lambert(Nn, L):
    L = L / np.linalg.norm(L)
    nn = Nn / np.maximum(np.linalg.norm(Nn, axis=1), 1e-12)[:, None]
    return np.clip(nn @ L, 0, 1) * 0.8 + 0.05


def splat_win(X, c, ax, az, xr, yr, H, fat=0):
    """splat into a window: xr / yr = the view's x and y range (metres), H pixels high; fat = each point also covers its neighbours
    out to this many pixels (close-ups, where the texels are sparser than the pixels)."""
    ay = np.array([0, 1.0, 0])
    px, py, pz = X @ ax, X @ ay, X @ az
    s = H / (yr[1] - yr[0])
    W = int((xr[1] - xr[0]) * s)
    m = (px > xr[0]) & (px < xr[1]) & (py > yr[0]) & (py < yr[1])
    ix = ((px[m] - xr[0]) * s).astype(np.int64)
    iy = ((yr[1] - py[m]) * s).astype(np.int64)
    img = np.full((H, W, 3), 0.12)
    f = img.reshape(-1, 3)
    cm, zm = c[m], pz[m]
    lins, zs, idx = [], [], []
    for dy in range(-fat, fat + 1):
        for dx in range(-fat, fat + 1):
            lins.append(np.clip(iy + dy, 0, H - 1) * W + np.clip(ix + dx, 0, W - 1))
            zs.append(zm - 0.002 * (abs(dx) + abs(dy)))   # a point's own pixel wins over a neighbour's spill at the same depth
            idx.append(np.arange(len(zm)))
    lin, z, k = np.concatenate(lins), np.concatenate(zs), np.concatenate(idx)
    o = np.argsort(z)
    f[lin[o]] = cm[k[o]]
    return img


def splat(X, c, ax, az, H=900):
    """Orthographic view of the texel points (nearest to the viewer wins), y up."""
    ay = np.array([0, 1.0, 0])
    px = X @ ax
    py = X @ ay
    pz = X @ az
    y0, y1 = 0.0, 1.75
    s = H / (y1 - y0)
    W = int(1.2 * s)
    ix = np.clip(((px + 0.6) * s).astype(np.int64), 0, W - 1)
    iy = np.clip(((y1 - py) * s).astype(np.int64), 0, H - 1)
    img = np.full((H, W, 3), 0.12)
    zb = np.full(H * W, -np.inf)
    lin = iy * W + ix
    o = np.argsort(pz)
    zb[lin[o]] = pz[o]
    imgf = img.reshape(-1, 3)
    imgf[lin[o]] = c[o]
    return img


# ---------------------------------------------------------------- main


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    preview = '--preview' in sys.argv
    d = args[0] if args else sfio.newest_subdir(GAME_EXPORT)
    lines = []

    def log(m):
        print(m)
        lines.append(m)
    log('export: ' + d)
    meshes, sk, missing = sfio.read_game_export(d)
    by = {m.name.replace(' (SfSoft)', ''): m for m in meshes}
    parts = []
    for nm in ['reika_upperBody_normal', 'reika_lowerBody_normal', 'reika_neck', 'reika_foot']:
        m = by.get(nm)
        if m is None:
            continue
        for gi, g in enumerate(m.groups):
            if g == 'reika_body':
                parts.append((m.v, m.uv, m.faces[m.face_group == gi]))
    if not parts:
        log('body: none of her body meshes in the export')
        sys.exit(1)
    surf = wr.Surf(parts)
    col = None
    if preview:
        for f in os.listdir(d):
            if f.lower() == 'reika_body_colormap.png':
                col = wr.read_png_rgb(os.path.join(d, f))
    n, nreg = 2048, 1024
    det, reg, hgt, regions = build('body', surf, sk, n, nreg, log, PREVIEW if preview else None, col)
    os.makedirs(OUT, exist_ok=True)
    wr.write_png(os.path.join(OUT, 'muscle_body_detail.png'), wr.to_u8(det))
    wr.write_png(os.path.join(OUT, 'muscle_body_region.png'), reg)
    wr.write_png(os.path.join(OUT, 'muscle_body_height.png'), hgt)
    table = ['# SF Body Morphs muscle layouts (tools/muscles/author.py). layout <name> <detail size> <region size> <slope max> <height max m>; then',
             '# muscle <index from 1> <name> <area core|back|legs|arms> <side L|R|C>',
             '# export ' + os.path.basename(d),
             'layout body %d %d %.3f %.3f' % (n, nreg, SLOPE_MAX, HEIGHT_MAX)]
    for k, (rn, ra, rs) in enumerate(regions):
        table.append('muscle %d %s %s %s' % (k + 1, rn, ra, rs))
    with open(os.path.join(OUT, 'muscle_layouts.txt'), 'w', newline='\n') as f:
        f.write('\n'.join(table) + '\n')
    with open(os.path.join(HERE, 'author.log'), 'w', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')


if __name__ == '__main__':
    main()
