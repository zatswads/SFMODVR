"""Authored skin wrinkle maps for SF Body Morphs (0.9.0).

Builds, from her export (Presets tab -> Export), one pair of textures per UV layout ("face" = reika_face_a's sheet, "body" =
the reika_body atlas):

  wrinkle_<layout>_detail.png  RGBA8 linear, NxN: R,G = crease slope in tangent space (x along +u, y along +v, metres per
                               metre, *0.5/SlopeMax + 0.5), B = crease depth 0..1 (the albedo's crease shade), A = region fade 0..1
                               (how much of the region's activation reaches this texel).
  wrinkle_<layout>_region.png  RGBA8 linear, MxM, point-sampled and read on the CPU: R = region index + 1 (0 = her skin but no
                               region, 255 = outside the layout's UV islands), G = measuring weight 0..1, B,A = the crease line's
                               direction in UV as a doubled angle (cos 2a, sin 2a)*0.5+0.5.
  wrinkle_layouts.txt          the region table the plugin reads (index, name, class face/neck/body, compression range).

The creases are 3D polylines on her bind-pose skin, placed from her face and limb bones (skeleton.txt) and ray cast onto the mesh;
each texel takes the dominant crease's height profile h = depth * taper * (-g(d/w) + 0.25 g((d - 1.9w)/1.2w)), g = exp(-x^2), with
the analytic gradient (no finite differences across UV seams). A region is the set of creases one skin movement makes (forehead
lines when her brows go up, the elbow's inner creases when it bends); the plugin measures the skin's compression across the creases
of each region (direction from the B,A channels) and fades the region's creases in by it. Breasts, shoulders and the rest of her
skin carry no region, so nothing wrinkles there.

Usage:  python tools/wrinkles/author.py [export dir] [--preview]
        (default: the newest export under <game>\\BepInEx\\config\\SfMorphs\\export; output into the shader bundle project)
        python tools/wrinkles/author.py --avatar=<drive_test export> [--figure=g9] [--preview]
        (0.11.48: an avatar base figure's sheets, Genesis 9's g9head / g9body / g9arms / g9legs, from the avatar mesh rebuilt on her
        skeleton that tools/avatar/drive_test.ps1 writes to <out>\\export; the other layouts in wrinkle_layouts.txt are kept)
Needs numpy only. --preview also writes her colour maps with the creases and regions drawn on, next to the outputs, as *_preview.png
(not part of the bundle: tools/wrinkles/preview/).
"""
import copy
import math
import os
import struct
import sys
import zlib

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'pipeline'))
import sfio  # noqa: E402

OUT = os.path.join(HERE, '..', 'shaders', 'SfMorphShaders', 'Assets', 'SfMorph', 'Wrinkles')
PREVIEW = os.path.join(HERE, 'preview')
GAME_EXPORT = r'E:\Bak\New folder\Misc\SF\VR\BepInEx\config\SfMorphs\export'
SLOPE_MAX = 1.5          # slope encoded in R,G: +-SLOPE_MAX -> 0..1

# ---------------------------------------------------------------- PNG


def write_png(path, img):
    """img: HxW or HxWxC uint8 (row 0 = top = v 1)."""
    if img.ndim == 2:
        img = img[:, :, None]
    h, w, c = img.shape
    ct = {1: 0, 2: 4, 3: 2, 4: 6}[c]
    raw = b''.join(b'\x00' + img[y].tobytes() for y in range(h))

    def chunk(t, d):
        return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n')
        f.write(chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, ct, 0, 0, 0)))
        f.write(chunk(b'IDAT', zlib.compress(raw, 6)))
        f.write(chunk(b'IEND', b''))


def read_png_rgb(path):
    """8-bit RGB/RGBA non-interlaced PNG -> HxWx3 uint8 (for the previews only)."""
    with open(path, 'rb') as f:
        data = f.read()
    pos, idat, w = 8, b'', 0
    while pos < len(data):
        n = struct.unpack('>I', data[pos:pos + 4])[0]
        t = data[pos + 4:pos + 8]
        d = data[pos + 8:pos + 8 + n]
        if t == b'IHDR':
            w, h, bd, ct = struct.unpack('>IIBB', d[:10])
            if bd != 8 or ct not in (2, 6):
                return None
            c = 3 if ct == 2 else 4
        elif t == b'IDAT':
            idat += d
        pos += 12 + n
    raw = zlib.decompress(idat)
    stride = w * c
    out = np.zeros((h, stride), np.uint8)
    prev = np.zeros(stride, np.int32)
    p = 0
    for y in range(h):
        ft = raw[p]
        line = np.frombuffer(raw, np.uint8, stride, p + 1).astype(np.int32)
        p += 1 + stride
        if ft == 0:
            cur = line
        elif ft == 2:
            cur = (line + prev) & 255
        else:
            cur = np.zeros(stride, np.int32)
            for x in range(stride):
                a = cur[x - c] if x >= c else 0
                b = prev[x]
                cc = prev[x - c] if x >= c else 0
                if ft == 1:
                    pr = a
                elif ft == 3:
                    pr = (a + b) >> 1
                else:
                    pa, pb, pc = abs(b - cc), abs(a - cc), abs(a + b - 2 * cc)
                    pr = a if pa <= pb and pa <= pc else (b if pb <= pc else cc)
                cur[x] = (line[x] + pr) & 255
        out[y] = cur
        prev = cur
    return out.reshape(h, w, c)[:, :, :3]

# ---------------------------------------------------------------- geometry


class Surf:
    """Her skin of one layout: triangles in bind space with UVs."""

    def __init__(self, parts):
        vs, fs, uvs, off = [], [], [], 0
        for v, uv, f in parts:
            vs.append(v)
            uvs.append(uv)
            fs.append(f + off)
            off += len(v)
        self.v = np.concatenate(vs)
        self.uv = np.concatenate(uvs)
        self.f = np.concatenate(fs)
        a, b, c = (self.v[self.f[:, k]] for k in range(3))
        n = np.cross(b - a, c - a)
        ln = np.linalg.norm(n, axis=1)
        self.area = 0.5 * ln
        self.fn = n / np.maximum(ln, 1e-20)[:, None]
        vn = np.zeros_like(self.v)
        for k in range(3):
            np.add.at(vn, self.f[:, k], n)
        self.vn = vn / np.maximum(np.linalg.norm(vn, axis=1), 1e-20)[:, None]
        # per triangle d(position)/du, d(position)/dv and the in-plane gradients of u and v (rows of J)
        e1, e2 = b - a, c - a
        uva, uvb, uvc = (self.uv[self.f[:, k]] for k in range(3))
        d1, d2 = uvb - uva, uvc - uva
        det = d1[:, 0] * d2[:, 1] - d2[:, 0] * d1[:, 1]
        ok = np.abs(det) > 1e-14
        det = np.where(ok, det, 1.0)
        self.pu = (e1 * d2[:, 1:2] - e2 * d1[:, 1:2]) / det[:, None]
        self.pv = (e2 * d1[:, 0:1] - e1 * d2[:, 0:1]) / det[:, None]
        self.pu[~ok] = 0
        self.pv[~ok] = 0
        # J = pinv([pu pv]): gu = gradient of u in the plane
        g00 = (self.pu * self.pu).sum(1)
        g01 = (self.pu * self.pv).sum(1)
        g11 = (self.pv * self.pv).sum(1)
        gd = g00 * g11 - g01 * g01
        gd = np.where(np.abs(gd) > 1e-30, gd, 1.0)
        self.gu = (self.pu * g11[:, None] - self.pv * g01[:, None]) / gd[:, None]
        self.gv = (self.pv * g00[:, None] - self.pu * g01[:, None]) / gd[:, None]
        self.tri_ok = ok

    def ray(self, o, d, near=None):
        """First hit of the ray o + t d (t > 0) on the surface; None if none. near: limit to triangles within this distance of o."""
        a, b, c = (self.v[self.f[:, k]] for k in range(3))
        sel = slice(None)
        if near is not None:
            ctr = (a + b + c) / 3
            m = np.linalg.norm(ctr - o, axis=1) < near
            a, b, c = a[m], b[m], c[m]
        e1, e2 = b - a, c - a
        p = np.cross(d, e2)
        det = (e1 * p).sum(1)
        ok = np.abs(det) > 1e-14
        inv = 1.0 / np.where(ok, det, 1.0)
        s = o - a
        u = (s * p).sum(1) * inv
        q = np.cross(s, e1)
        v = (q @ d) * inv
        t = (e2 * q).sum(1) * inv
        hit = ok & (u >= 0) & (v >= 0) & (u + v <= 1) & (t > 1e-6)
        if not hit.any():
            return None
        t = np.where(hit, t, np.inf)
        return o + d * t.min()

    def raster(self, n, depth=None):
        """Texel centres in UV -> (triangle id or -1, barycentric weights). Row 0 = v 1. depth (per vertex, 0.11.25 previews): where
        triangles overlap, the one with the highest interpolated depth wins (a z-buffer); None = the last one drawn."""
        tri = np.full(n * n, -1, np.int64)
        bary = np.zeros((n * n, 3), np.float32)
        zbuf = np.full(n * n, -np.inf) if depth is not None else None
        uv = self.uv[self.f] * n                     # (T,3,2) in texel units, x = u*n, y = v*n
        lo = np.floor(uv.min(1) - 0.5).astype(np.int64)
        hi = np.ceil(uv.max(1) - 0.5).astype(np.int64)
        size = np.maximum(hi[:, 0] - lo[:, 0], hi[:, 1] - lo[:, 1]) + 1
        bins = [2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096]
        start = 0
        for bsz in bins:
            ids = np.nonzero((size > start) & (size <= bsz) & self.tri_ok)[0]
            start = bsz
            if len(ids) == 0:
                continue
            chunk = max(1, 4_000_000 // (bsz * bsz))
            oy, ox = np.mgrid[0:bsz, 0:bsz]
            ox, oy = ox.ravel(), oy.ravel()
            for s0 in range(0, len(ids), chunk):
                t = ids[s0:s0 + chunk]
                px = lo[t, 0][:, None] + ox[None, :]
                py = lo[t, 1][:, None] + oy[None, :]
                cx, cy = px + 0.5, py + 0.5
                a, b, c = uv[t, 0], uv[t, 1], uv[t, 2]
                v0x, v0y = (b - a)[:, 0:1], (b - a)[:, 1:2]
                v1x, v1y = (c - a)[:, 0:1], (c - a)[:, 1:2]
                d = v0x * v1y - v1x * v0y
                d = np.where(np.abs(d) < 1e-12, 1e-12, d)
                wx, wy = cx - a[:, 0:1], cy - a[:, 1:2]
                l1 = (wx * v1y - v1x * wy) / d
                l2 = (v0x * wy - wx * v0y) / d
                l0 = 1 - l1 - l2
                inside = (l0 >= -1e-6) & (l1 >= -1e-6) & (l2 >= -1e-6) & (px >= 0) & (px < n) & (py >= 0) & (py < n)
                ti, ci = np.nonzero(inside)
                pix = (n - 1 - py[ti, ci]) * n + px[ti, ci]
                bb = np.stack([l0[ti, ci], l1[ti, ci], l2[ti, ci]], 1)
                if zbuf is not None:
                    z = (depth[self.f[t[ti]]] * bb).sum(1)
                    o = np.argsort(z)                  # nearest last: its write wins among duplicates, then against the buffer
                    pix, ti_o, bb, z = pix[o], t[ti][o], bb[o], z[o]
                    win = z > zbuf[pix]
                    tri[pix[win]] = ti_o[win]
                    bary[pix[win]] = bb[win]
                    zbuf[pix[win]] = z[win]
                    continue
                tri[pix] = t[ti]
                bary[pix] = bb
        return tri, bary


# ---------------------------------------------------------------- creases


class Crease:
    def __init__(self, pts, region, depth, width, taper=(1, 1), seed=0, ridge=0.25, roll_toward=None, roll=(1.0, 1.0), breaks=0.0, mid=None):
        self.p = np.asarray(pts, np.float64)
        self.region = region
        self.depth = depth            # metres; negative = a raised roll along the line instead of a furrow (0.11.25, face)
        self.width = width
        self.taper = taper            # (start, end): 1 = fade to 0 at that end, 0 = full depth to the end (a half of a symmetric line)
        self.seed = seed
        self.ridge = ridge            # height of the rounded skin beside the crease, as a share of its depth (a soft roll: ~0.6)
        # 0.11.25 (face only, the summing build): the roll on the side towards roll_toward (a 3D direction) x roll[0], the other side x
        # roll[1] (a fold whose skin bunches over one side); breaks 0..1 = how much of the line fades out in gaps; mid = (share, metres):
        # the depth at the midline (x = 0) as a share, back to full that far out (the forehead's two frontalis bellies)
        self.roll_toward = None if roll_toward is None else np.asarray(roll_toward, np.float64)
        self.roll = roll
        self.breaks = breaks
        self.mid = mid
        self.micro = True             # 0.11.47: False = add_micro leaves it alone (the groin's own branch folds)


class Bunch:
    """0.11.25 a soft mass the expression pushes up (positive height) or a dimple (negative): h = height x exp(-(u/r1)^2 - (v/r2)^2),
    u along `axis` (projected onto the skin), v across it, centred on a point of her skin."""

    def __init__(self, c, axis, r1, r2, height, region):
        self.c = np.asarray(c, np.float64)
        self.axis = np.asarray(axis, np.float64)
        self.r1, self.r2, self.h = r1, r2, height
        self.region = region


def resample(p, step):
    seg = np.linalg.norm(np.diff(p, axis=0), axis=1)
    s = np.concatenate([[0], np.cumsum(seg)])
    n = max(2, int(math.ceil(s[-1] / step)) + 1)
    t = np.linspace(0, s[-1], n)
    return np.stack([np.interp(t, s, p[:, k]) for k in range(3)], 1), t / max(s[-1], 1e-9)


def taper_fn(s, taper):
    a = np.ones_like(s)
    if taper[0]:
        a = a * np.clip(s / 0.25, 0, 1) ** 0.7
    if taper[1]:
        a = a * np.clip((1 - s) / 0.25, 0, 1) ** 0.7
    return a


def nearest_on_polyline(x, q):
    """x: (M,3) points, q: (K,3) polyline. -> distance (M,), closest point (M,3), segment index (M,), fraction along it (M,)."""
    best_d = np.full(len(x), np.inf)
    best_c = np.zeros_like(x)
    best_i = np.zeros(len(x), np.int64)
    best_f = np.zeros(len(x))
    a, b = q[:-1], q[1:]
    ab = b - a
    l2 = np.maximum((ab * ab).sum(1), 1e-20)
    for s0 in range(0, len(a), 64):
        aa, abb, ll = a[s0:s0 + 64], ab[s0:s0 + 64], l2[s0:s0 + 64]
        t = np.clip(((x[:, None, :] - aa[None]) * abb[None]).sum(2) / ll[None], 0, 1)
        c = aa[None] + t[:, :, None] * abb[None]
        d = np.linalg.norm(x[:, None, :] - c, axis=2)
        k = d.argmin(1)
        dk = d[np.arange(len(x)), k]
        m = dk < best_d
        best_d[m] = dk[m]
        best_c[m] = c[np.arange(len(x)), k][m]
        best_i[m] = s0 + k[m]
        best_f[m] = t[np.arange(len(x)), k][m]
    return best_d, best_c, best_i, best_f


def wobble(p, nrm, amp, seed):
    """Small lateral and depth irregularity along a line (skin creases are never straight)."""
    rng = np.random.default_rng(seed)
    q, s = resample(p, 0.0015)
    t = np.gradient(q, axis=0)
    t /= np.maximum(np.linalg.norm(t, axis=1), 1e-12)[:, None]
    side = np.cross(nrm(q), t)
    side /= np.maximum(np.linalg.norm(side, axis=1), 1e-12)[:, None]
    L = s * np.linalg.norm(np.diff(p, axis=0), axis=1).sum()
    off = np.zeros(len(q))
    for k in range(3):
        wl = rng.uniform(0.012, 0.04)
        off += amp * rng.uniform(0.3, 1.0) * np.sin(2 * math.pi * L / wl + rng.uniform(0, 6.28)) / (k + 1)
    return q + side * off[:, None]


# ---------------------------------------------------------------- micro lines (0.11.40)


class SkinNear:
    """Nearest-vertex lookups on her skin (a coarse grid over the vertices): snap a point onto the skin and take its normal."""

    def __init__(self, surf, cell=0.01):
        self.v, self.n, self.cell = surf.v, surf.vn, cell
        key = np.floor(self.v / cell).astype(np.int64)
        self.grid = {}
        for i, k in enumerate(map(tuple, key)):
            self.grid.setdefault(k, []).append(i)

    def near(self, p):
        k = np.floor(p / self.cell).astype(np.int64)
        ids = []
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for dz in (-1, 0, 1):
                    ids.extend(self.grid.get((k[0] + dx, k[1] + dy, k[2] + dz), ()))
        if not ids:
            return None
        ids = np.array(ids)
        return ids[np.argmin(np.linalg.norm(self.v[ids] - p, axis=1))]

    def snap(self, p):
        """p projected onto the tangent plane of its nearest vertex (good over the few millimetres to centimetres a micro line spans)."""
        i = self.near(p)
        if i is None:
            return None, None
        nrm = self.n[i]
        return p - np.dot(p - self.v[i], nrm) * nrm, nrm


def micro_lines(c, skin, rng, minw, feather=False, scale=1.0):
    """0.11.40 the fine lines a fold drags with it (user: 'micro lines from the main lines, where the skin bends the most'): real folds
    are never one clean groove. Around the middle of a line, where the skin bends most, the fold
      - echoes: a fainter, thinner line on each side of it, over part of its length, leaving the main line and rejoining it;
      - branches: short lines that fork off at a shallow angle towards the line's ends (where the folding fans out);
      - feathers (off by default): very short faint lines across the fold (the 0.11.40 previews: across a fold they read as stitches).
    Depth and width are shares of the main line's (width never under minw: one texel of the atlas plus a little), all with breaks so
    nothing reads as a drawn copy. Deterministic from the line's seed."""
    out = []
    if c.depth <= 0 or len(c.p) < 3:
        return out
    q, sarc = resample(c.p, 0.0008)
    L = np.linalg.norm(np.diff(q, axis=0), axis=1).sum()
    w0, d0 = c.width, c.depth * scale
    if L < 6 * w0:
        return out
    t = np.gradient(q, axis=0)
    t /= np.maximum(np.linalg.norm(t, axis=1), 1e-12)[:, None]
    nrm = np.zeros_like(q)
    for i in range(len(q)):
        j = skin.near(q[i])
        nrm[i] = skin.n[j] if j is not None else np.array([0, 0, 1.0])
    side = np.cross(nrm, t)
    side /= np.maximum(np.linalg.norm(side, axis=1), 1e-12)[:, None]

    def at(u):
        k = int(np.clip(round(u * (len(q) - 1)), 0, len(q) - 1))
        return q[k], t[k], side[k]

    def poly(pts):
        res = []
        for pnt in pts:
            sp_, _ = skin.snap(pnt)
            if sp_ is not None:
                res.append(sp_)
        return np.array(res)

    def width(f):
        return max(minw, w0 * f)

    seed = c.seed * 31 + 7
    # echoes: one on each side, over 35-65 % of the line around its middle, leaving it and rejoining (offset x sin)
    for sgn in (1.0, -1.0):
        span = rng.uniform(0.35, 0.65)
        u0 = 0.15 + rng.uniform(0, 1) * max(0.0, 0.85 - span - 0.15)
        off = sgn * rng.uniform(1.7, 2.6) * w0
        pts = []
        for k in range(14):
            v = k / 13.0
            u = u0 + span * v
            pnt, tt, sd = at(u)
            pts.append(pnt + sd * off * math.sin(math.pi * v) ** 0.7)
        e = poly(pts)
        if len(e) > 4:
            out.append(Crease(e, c.region, d0 * rng.uniform(0.2, 0.3), width(rng.uniform(0.4, 0.5)), taper=(1, 1), seed=seed, ridge=0.2,
                              breaks=0.45))
            seed += 1
    # branches: forks towards both ends, pointing away from the middle, off alternate sides
    nb = 2 if L < 25 * w0 else 3
    for b in range(nb):
        u = (rng.uniform(0.12, 0.32) if b % 2 == 0 else rng.uniform(0.68, 0.88)) if b < 2 else rng.uniform(0.42, 0.58)
        pnt, tt, sd = at(u)
        dirn = 1.0 if u >= 0.5 else -1.0
        sg = 1.0 if (b + c.seed) % 2 == 0 else -1.0
        ang = math.radians(rng.uniform(16, 34))
        ln = L * rng.uniform(0.09, 0.17)
        bend = rng.uniform(0.3, 0.8) * (1 if rng.uniform() < 0.5 else -1)   # branches curve away (straight ones read as scratches)
        pts = []
        for k in range(10):
            v = k / 9.0
            a = ang * (1 + bend * v)
            pts.append(pnt + (tt * dirn * math.cos(a) + sd * sg * math.sin(a)) * ln * v)
        e = poly(pts)
        if len(e) > 4:
            out.append(Crease(e, c.region, d0 * rng.uniform(0.18, 0.28), width(rng.uniform(0.5, 0.65)), taper=(0, 1), seed=seed, ridge=0.2,
                              breaks=0.2))
            seed += 1
    # feathers: short faint lines across the deepest third of a joint crease
    if feather:
        nf = int(np.clip(L / (6 * w0), 3, 7))
        for k in range(nf):
            u = 0.33 + 0.34 * (k + rng.uniform(0.2, 0.8)) / nf
            pnt, tt, sd = at(u)
            a = math.radians(rng.uniform(62, 85)) * (1 if rng.uniform() < 0.5 else -1)
            dvec = tt * math.cos(a) + sd * math.sin(a)
            ln = rng.uniform(2.2, 3.4) * w0
            ofs = rng.uniform(-0.3, 0.3) * ln
            e = poly([pnt + dvec * (ofs + ln * (k2 / 6.0 - 0.5)) for k2 in range(7)])
            if len(e) > 4:
                out.append(Crease(e, c.region, d0 * rng.uniform(0.12, 0.2), width(rng.uniform(0.35, 0.45)), taper=(1, 1), seed=seed, ridge=0.1))
                seed += 1
    return out


def add_micro(prims, surf, regions, minw, log, feather_regions=(), scale=1.0):
    """Micro lines for every fold of prims (not the bunches, not the raised rolls), in the fold's own region."""
    skin = SkinNear(surf)
    extra = []
    for c in prims:
        if not isinstance(c, Crease) or not c.micro:
            continue
        rng = np.random.default_rng(c.seed * 1009 + 17)
        extra.extend(micro_lines(c, skin, rng, minw, feather=regions[c.region][0].split('_')[0] in feather_regions, scale=scale))
    log('micro lines: %d around %d folds' % (len(extra), sum(isinstance(c, Crease) for c in prims)))
    return prims + extra


# ---------------------------------------------------------------- layout build


def line_noise(L, seed, k0=0, wl=(0.012, 0.035)):
    """A smooth irregular signal along a line's arc length L (metres), about -1..1: three sines of wl wavelengths."""
    rng = np.random.default_rng(seed * 7 + 3 + k0)
    out = np.zeros_like(L)
    for k in range(3):
        out += rng.uniform(0.5, 1.0) * np.sin(2 * math.pi * L / rng.uniform(*wl) + rng.uniform(0, 6.28)) / (1 + 0.6 * k)
    return out / 1.6


def eval_prim(pr, X, N, region_radius):
    """0.11.25 one primitive of the summing build -> (texel ids, height, 3D gradient, region weight, measuring direction, crease shade)
    or None when it reaches no texel."""
    rr = region_radius[pr.region]
    if isinstance(pr, Bunch):
        ext = 3.0 * max(pr.r1, pr.r2) + rr
        near = np.nonzero(np.linalg.norm(X - pr.c, axis=1) < ext)[0]
        if len(near) == 0:
            return None
        nc = N[near[np.argmin(np.linalg.norm(X[near] - pr.c, axis=1))]]
        a1 = pr.axis - np.dot(pr.axis, nc) * nc
        a1 /= max(np.linalg.norm(a1), 1e-12)
        a2 = np.cross(nc, a1)
        d = X[near] - pr.c
        u, v = d @ a1, d @ a2
        g = np.exp(-(u / pr.r1) ** 2 - (v / pr.r2) ** 2)
        h = pr.h * g
        grad = h[:, None] * (-2 * u[:, None] / pr.r1 ** 2 * a1[None] - 2 * v[:, None] / pr.r2 ** 2 * a2[None])
        nn = N[near]
        grad -= (grad * nn).sum(1)[:, None] * nn
        wr = np.exp(-((u / (pr.r1 + rr)) ** 2 + (v / (pr.r2 + rr)) ** 2))
        dirv = np.tile(a1, (len(near), 1))
        cav = np.maximum(-h, 0) / 0.0006 * 0.5          # a dimple shades a little
        return near, h, grad, wr, dirv, cav
    q, sarc = resample(pr.p, 0.0004)
    ctr = q.mean(0)
    rad = np.linalg.norm(q - ctr, axis=1).max() + max(rr, 4 * pr.width)
    near = np.nonzero(np.linalg.norm(X - ctr, axis=1) < rad)[0]
    if len(near) == 0:
        return None
    x = X[near]
    d, cp, si, fr = nearest_on_polyline(x, q)
    sp = sarc[si] + (sarc[np.minimum(si + 1, len(sarc) - 1)] - sarc[si]) * fr
    tan = q[np.minimum(si + 1, len(q) - 1)] - q[si]
    tan /= np.maximum(np.linalg.norm(tan, axis=1), 1e-12)[:, None]
    Ltot = np.linalg.norm(np.diff(q, axis=0), axis=1).sum()
    L = sp * Ltot
    n1 = line_noise(L, pr.seed)
    n2 = line_noise(L, pr.seed, 11, (0.02, 0.045))
    amp = taper_fn(sp, pr.taper) * np.clip(0.8 + 0.25 * n1, 0.4, 1.0)
    if pr.breaks > 0:
        # a line fades out and back in over ~1 cm where it breaks (a gap opening over a millimetre read as a cut)
        gx = np.clip((n2 + 1.1 - 1.6 * pr.breaks) / 0.9, 0, 1)
        amp = amp * (gx * gx * (3 - 2 * gx))
    if pr.mid is not None:
        mx = np.clip(np.abs(cp[:, 0]) / pr.mid[1], 0, 1)
        amp = amp * (pr.mid[0] + (1 - pr.mid[0]) * mx * mx * (3 - 2 * mx))
    w = pr.width * (0.88 + 0.2 * line_noise(L, pr.seed, 23, (0.015, 0.04)))
    g1 = np.exp(-(d / w) ** 2)
    r = (d - 1.9 * w) / (1.2 * w)
    g2 = np.exp(-r ** 2)
    rad_dir = x - cp
    rad_dir /= np.maximum(np.linalg.norm(rad_dir, axis=1), 1e-12)[:, None]
    ridge = np.full(len(near), pr.ridge)
    if pr.roll_toward is not None:
        ridge = pr.ridge * np.where(rad_dir @ pr.roll_toward > 0, pr.roll[0], pr.roll[1])
    h = pr.depth * amp * (-g1 + ridge * g2)
    dh = pr.depth * amp * (2 * d / (w * w) * g1 - ridge * 2 * r / (1.2 * w) * g2)
    grad = dh[:, None] * rad_dir
    nn = N[near]
    grad -= (grad * nn).sum(1)[:, None] * nn
    wr = np.exp(-(d / rr) ** 2) * np.clip(taper_fn(sp, pr.taper) * 1.5, 0, 1)
    cav = amp * g1 * max(pr.depth, 0) / 0.001
    return near, h, grad, wr, tan, cav


def build_sum(name, surf, prims, regions, n, nreg, region_radius, log, excl=()):
    """0.11.25 the face's build: every texel belongs to one region (the strongest weight, as before: the plugin fades a texel by one
    region's activation) and sums the heights and slopes of all that region's folds and bunches (a furrow's rolls overlap its
    neighbour's; a bunch sits between lines), instead of taking the single strongest line. A region's relief that would have shown on
    another region's texels is cut there: the log reports the largest cut per region, so regions can be spaced to keep it small."""
    log('%s: %d triangles, rasterising %dx%d' % (name, len(surf.f), n, n))
    tri, bary = surf.raster(n)
    cov = tri >= 0
    idx = np.nonzero(cov)[0]
    t = tri[idx]
    X = (surf.v[surf.f[t]] * bary[idx][:, :, None]).sum(1)
    N = (surf.vn[surf.f[t]] * bary[idx][:, :, None]).sum(1)
    N /= np.maximum(np.linalg.norm(N, axis=1), 1e-12)[:, None]
    log('%s: %.1f%% of the texels on her skin' % (name, 100.0 * cov.mean()))
    M, R = len(idx), len(regions)
    keep = np.ones(M)
    for ec, er in excl:
        keep *= np.clip((np.linalg.norm(X - ec, axis=1) - er) / 0.01, 0, 1)
    w_reg = np.zeros((M, R), np.float32)
    dir_reg = np.zeros((M, R, 3), np.float32)
    has_line = np.zeros((M, R), bool)
    evals = []
    for pi_, pr in enumerate(prims):
        e = eval_prim(pr, X, N, region_radius)
        if e is None:
            log('  %s primitive %d (%s): no texels near it' % (name, pi_, regions[pr.region][0]))
            continue
        near, h, grad, wr, dirv, cav = e
        wr = wr * keep[near]
        k = pr.region
        upd = wr > w_reg[near, k]
        w_reg[near[upd], k] = wr[upd]
        # the measuring direction comes from the region's folds; a bunch sets it only where no fold of the region reaches
        if isinstance(pr, Crease):
            setd = upd | (~has_line[near, k] & (wr > 0.01))
            dir_reg[near[setd], k] = dirv[setd]
            has_line[near[wr > 0.01], k] = True
        else:
            setd = upd & ~has_line[near, k]
            dir_reg[near[setd], k] = dirv[setd]
        evals.append((k, near, h, grad, cav))
    best = w_reg.argmax(1)
    wbest = w_reg[np.arange(M), best]
    fade = np.clip(wbest / 0.35, 0, 1)
    H = np.zeros(M)
    G = np.zeros((M, 3))
    cavs = np.zeros(M)
    cut = np.zeros(R)
    peak = np.zeros(R)
    for k, near, h, grad, cav in evals:
        own = best[near] == k
        hk = h * keep[near]
        on = near[own]
        H[on] += hk[own]
        G[on] += grad[own] * keep[on][:, None]
        cavs[on] = np.maximum(cavs[on], cav[own] * keep[on])
        peak[k] = max(peak[k], np.abs(hk).max())
        if (~own).any():
            cut[k] = max(cut[k], np.abs(hk[~own]).max())
    pu, pv = surf.pu[t], surf.pv[t]
    tu = pu / np.maximum(np.linalg.norm(pu, axis=1), 1e-12)[:, None]
    tv = pv / np.maximum(np.linalg.norm(pv, axis=1), 1e-12)[:, None]
    sx = (G * tu).sum(1)
    sy = (G * tv).sum(1)
    smax = np.abs(np.concatenate([sx, sy])).max() if M else 0
    log('%s: relief %.2f to %.2f mm, slope max %.2f (encoded up to %.1f)' % (name, H.min() * 1000, H.max() * 1000, smax, SLOPE_MAX))
    cav = np.clip(cavs * 0.8, 0, 1)
    det = np.zeros((n * n, 4), np.float32)
    det[:, 0] = det[:, 1] = 0.5
    det[idx, 0] = 0.5 + 0.5 * np.clip(sx / SLOPE_MAX, -1, 1)
    det[idx, 1] = 0.5 + 0.5 * np.clip(sy / SLOPE_MAX, -1, 1)
    det[idx, 2] = cav
    det[idx, 3] = fade
    det = dilate(det.reshape(n, n, 4), cov.reshape(n, n), 6)
    reg = region_map(surf, t, idx, n, nreg, cov, best, wbest, dir_reg, M)
    for k, (rn, rc, rg) in enumerate(regions):
        m = (best == k) & (wbest > 0.02)
        log('  region %2d %-16s %-4s range %.2f: %6d texels, relief up to %.2f mm, cut at its edge %.2f mm' % (k + 1, rn, rc, rg, m.sum(), peak[k] * 1000, cut[k] * 1000))
    LAST.update(X=X, N=N, G=G, cav=cav, best=best, fade=fade, idx=idx, surf=surf)
    return det, reg, cov.reshape(n, n), cav, idx, best, wbest


def region_map(surf, t, idx, n, nreg, cov, best, wbest, dir_reg, M):
    """The region map at nreg: point samples of the region id, its weight and the line direction (as build writes it)."""
    step = n // nreg
    reg = np.zeros((nreg, nreg, 4), np.uint8)
    full_id = np.full(n * n, 255, np.int64)
    full_id[idx] = np.where(wbest > 0.02, best + 1, 0)
    full_w = np.zeros(n * n)
    full_w[idx] = np.where(wbest > 0.02, wbest, 0)
    d3 = dir_reg[np.arange(M), best]
    du = (d3 * surf.gu[t]).sum(1)
    dv = (d3 * surf.gv[t]).sum(1)
    ang = np.arctan2(dv, du)
    full_c = np.zeros(n * n)
    full_s = np.zeros(n * n)
    full_c[idx] = np.cos(2 * ang)
    full_s[idx] = np.sin(2 * ang)
    sel = (np.arange(nreg) * step + step // 2)
    pick = (sel[:, None] * n + sel[None, :]).ravel()
    covc = cov.reshape(nreg, step, nreg, step).any(axis=(1, 3)).ravel()
    rid = full_id[pick]
    rid = np.where((rid == 255) & covc, 0, rid)
    reg[:, :, 0] = rid.reshape(nreg, nreg)
    reg[:, :, 1] = np.clip(full_w[pick] * 255 + 0.5, 0, 255).reshape(nreg, nreg)
    reg[:, :, 2] = np.clip((full_c[pick] * 0.5 + 0.5) * 255 + 0.5, 0, 255).reshape(nreg, nreg)
    reg[:, :, 3] = np.clip((full_s[pick] * 0.5 + 0.5) * 255 + 0.5, 0, 255).reshape(nreg, nreg)
    return reg


def build(name, surf, creases, regions, n, nreg, region_radius, log, excl=()):
    """Rasterise the layout and evaluate the creases. regions: list of (name, class, range)."""
    log('%s: %d triangles, rasterising %dx%d' % (name, len(surf.f), n, n))
    tri, bary = surf.raster(n)
    cov = tri >= 0
    idx = np.nonzero(cov)[0]
    t = tri[idx]
    X = (surf.v[surf.f[t]] * bary[idx][:, :, None]).sum(1)
    N = (surf.vn[surf.f[t]] * bary[idx][:, :, None]).sum(1)
    N /= np.maximum(np.linalg.norm(N, axis=1), 1e-12)[:, None]
    log('%s: %.1f%% of the texels on her skin' % (name, 100.0 * cov.mean()))

    M = len(idx)
    h_best = np.zeros(M)            # dominant crease height (signed, metres)
    g_best = np.zeros((M, 3))       # its 3D gradient
    cav = np.zeros(M)
    w_reg = np.zeros((M, len(regions)))
    dir_reg = np.zeros((M, len(regions), 3))

    deepest = {}
    for c in creases:
        deepest[c.region] = max(deepest.get(c.region, 0.0), c.depth)
    for ci, c in enumerate(creases):
        q, s = resample(c.p, 0.0004)
        ctr = q.mean(0)
        rad = np.linalg.norm(q - ctr, axis=1).max() + max(region_radius[c.region], 4 * c.width)
        near = np.nonzero(np.linalg.norm(X - ctr, axis=1) < rad)[0]
        if len(near) == 0:
            log('  crease %d (%s): no texels near it' % (ci, regions[c.region][0]))
            continue
        x = X[near]
        keep = np.ones(len(near))
        for ec, er in excl:          # no crease and no region inside an exclusion sphere (her breasts)
            keep *= np.clip((np.linalg.norm(x - ec, axis=1) - er) / 0.01, 0, 1)
        d, cp, si, fr = nearest_on_polyline(x, q)
        sp = s[si] + (s[np.minimum(si + 1, len(s) - 1)] - s[si]) * fr
        tan = q[np.minimum(si + 1, len(q) - 1)] - q[si]
        tan /= np.maximum(np.linalg.norm(tan, axis=1), 1e-12)[:, None]
        rng = np.random.default_rng(c.seed + 17)
        ph = rng.uniform(0, 6.28, 2)
        amp = keep * taper_fn(sp, c.taper) * (0.75 + 0.25 * np.sin(2 * math.pi * sp * 2.3 + ph[0]) * np.sin(2 * math.pi * sp * 0.9 + ph[1]))
        w = c.width
        g1 = np.exp(-(d / w) ** 2)
        r = (d - 1.9 * w) / (1.2 * w)
        g2 = np.exp(-r ** 2)
        h = c.depth * amp * (-g1 + c.ridge * g2)
        dh = c.depth * amp * (2 * d / (w * w) * g1 - c.ridge * 2 * r / (1.2 * w) * g2)    # dh/dd
        rad_dir = x - cp
        rad_dir /= np.maximum(np.linalg.norm(rad_dir, axis=1), 1e-12)[:, None]
        grad = dh[:, None] * rad_dir
        nn = N[near]
        grad -= (grad * nn).sum(1)[:, None] * nn            # into the tangent plane
        take = np.abs(h) > np.abs(h_best[near])
        tk = near[take]
        h_best[tk] = h[take]
        g_best[tk] = grad[take]
        cav[near] = np.maximum(cav[near], amp * g1 * (c.depth / deepest[c.region]))   # a fainter fold shades less
        # region weight: around the creases, out to the region's radius, faded with the crease's own taper (wider ends)
        rr = region_radius[c.region]
        wr = keep * np.exp(-(d / rr) ** 2) * np.clip(taper_fn(sp, c.taper) * 1.5, 0, 1)
        k = c.region
        upd = wr > w_reg[near, k]
        w_reg[near[upd], k] = wr[upd]
        dir_reg[near[upd], k] = tan[upd]

    # slope in the texel's tangent frame (tangent = d pos/du, bitangent = d pos/dv, normalised)
    pu, pv = surf.pu[t], surf.pv[t]
    tu = pu / np.maximum(np.linalg.norm(pu, axis=1), 1e-12)[:, None]
    tv = pv / np.maximum(np.linalg.norm(pv, axis=1), 1e-12)[:, None]
    sx = (g_best * tu).sum(1)
    sy = (g_best * tv).sum(1)

    # the region per texel and its fade: the strongest region; the fade is 1 inside, falling off at the region's edge
    best = w_reg.argmax(1)
    wbest = w_reg[np.arange(M), best]
    fade = np.clip(wbest / 0.35, 0, 1)

    det = np.zeros((n * n, 4), np.float32)
    det[:, 0] = det[:, 1] = 0.5
    det[idx, 0] = 0.5 + 0.5 * np.clip(sx / SLOPE_MAX, -1, 1)
    det[idx, 1] = 0.5 + 0.5 * np.clip(sy / SLOPE_MAX, -1, 1)
    det[idx, 2] = np.clip(cav, 0, 1)
    det[idx, 3] = fade
    det = dilate(det.reshape(n, n, 4), cov.reshape(n, n), 6)

    # region map at nreg: point samples of the same fields (nearest full-res texel)
    step = n // nreg
    reg = np.zeros((nreg, nreg, 4), np.uint8)
    full_id = np.full(n * n, 255, np.int64)
    full_id[idx] = np.where(wbest > 0.02, best + 1, 0)
    full_w = np.zeros(n * n)
    full_w[idx] = np.where(wbest > 0.02, wbest, 0)
    # crease direction in UV of the texel's region (3D tangent -> (du, dv) through the gradients of u and v)
    d3 = dir_reg[np.arange(M), best]
    du = (d3 * surf.gu[t]).sum(1)
    dv = (d3 * surf.gv[t]).sum(1)
    ang = np.arctan2(dv, du)
    full_c = np.zeros(n * n)
    full_s = np.zeros(n * n)
    full_c[idx] = np.cos(2 * ang)
    full_s[idx] = np.sin(2 * ang)
    sel = (np.arange(nreg) * step + step // 2)
    pick = (sel[:, None] * n + sel[None, :]).ravel()
    # a coarse texel is "covered" if any of its fine texels is (so the coverage test of a mesh on this layout is generous at seams)
    covc = cov.reshape(nreg, step, nreg, step).any(axis=(1, 3)).ravel()
    rid = full_id[pick]
    rid = np.where((rid == 255) & covc, 0, rid)
    reg[:, :, 0] = rid.reshape(nreg, nreg)
    reg[:, :, 1] = np.clip(full_w[pick] * 255 + 0.5, 0, 255).reshape(nreg, nreg)
    reg[:, :, 2] = np.clip((full_c[pick] * 0.5 + 0.5) * 255 + 0.5, 0, 255).reshape(nreg, nreg)
    reg[:, :, 3] = np.clip((full_s[pick] * 0.5 + 0.5) * 255 + 0.5, 0, 255).reshape(nreg, nreg)

    for k, (rn, rc, rg) in enumerate(regions):
        m = (best == k) & (wbest > 0.02)
        log('  region %2d %-16s %-4s range %.2f: %6d texels, %d with creases' % (k + 1, rn, rc, rg, m.sum(), (m & (cav > 0.2)).sum()))
    LAST.update(X=X, N=N, G=g_best, cav=cav, best=best, fade=fade, idx=idx)   # 0.11.23: for the lit previews
    return det, reg, cov.reshape(n, n), cav, idx, best, wbest


LAST = {}

# 0.11.23 (user: the face wrinkles are barely noticeable; show what they should look like with expressions): her face lit from the export
# with each expression's regions at full, as the plugin blends them at Strength 1 (normal = base - slope x act x fade) and Cavity 0.3
# (albedo x (1 - Cavity x depth x act x fade)); the same groups as the Debug tab's Wrinkle test.
EXPRESSIONS = (('neutral', ()), ('brows up', ('forehead_L', 'forehead_R')), ('frown', ('glabella',)),
               ('squint', ('crowsfeet_L', 'crowsfeet_R', 'undereye_L', 'undereye_R')),
               ('smile', ('crowsfeet_L', 'crowsfeet_R', 'undereye_L', 'undereye_R', 'nasolabial_L', 'nasolabial_R')),
               ('nose scrunch', ('nose_L', 'nose_R', 'glabella')), ('frown lips', ('chin', 'mouthcorner_L', 'mouthcorner_R')))


def face_renders(regions, colour, n, d, log, strength=1.0, cavity=0.3):
    """Her face rendered from the export: the mesh rasterised in an orthographic front view (front-facing triangles), each pixel's UV
    looks up the authored relief (as the plugin blends it: normal - slope x act x fade x Strength) and her colour map (x the crease
    shade, Cavity). Full faces per expression, then close-ups (neutral | expression) under a raking light."""
    surf = LAST['surf']
    X, G, cav, best, fade, idx = (LAST[k] for k in ('X', 'G', 'cav', 'best', 'fade', 'idx'))
    names = [r[0] for r in regions]
    GI = np.zeros((n * n, 3))
    GI[idx] = G
    CI = np.zeros(n * n)
    CI[idx] = cav
    col = np.full((n * n, 3), 0.75)
    if colour is not None:
        ys = (np.arange(n) * colour.shape[0] // n)
        xs = (np.arange(n) * colour.shape[1] // n)
        col = colour[ys][:, xs].reshape(-1, 3).astype(np.float64) / 255.0

    def act_img(regs):
        a = np.zeros(n * n)
        act = np.array([1.0 if names[k] in regs else 0.0 for k in range(len(names))])
        a[idx] = act[best] * fade
        return a

    keepf = surf.fn[:, 2] > 0.02                      # front-facing triangles only (no depth buffer needed for a front view)
    ff = surf.f[keepf]

    def view(cx, cy, h, px):
        uvs = np.stack([(h - (surf.v[:, 0] - cx)) / (2 * h), (surf.v[:, 1] - (cy - h)) / (2 * h)], 1)   # her left (-x) on the right
        tmp = Surf([(surf.v, uvs, ff)])
        tri, bary = tmp.raster(px, depth=surf.v[:, 2])
        ok = tri >= 0
        fv = ff[tri[ok]]
        b = bary[ok].astype(np.float64)
        uv = (surf.uv[fv] * b[:, :, None]).sum(1)
        nrm = (surf.vn[fv] * b[:, :, None]).sum(1)
        tx = np.clip((uv[:, 0] * n).astype(np.int64), 0, n - 1)
        ty = np.clip((uv[:, 1] * n).astype(np.int64), 0, n - 1)
        return ok, (n - 1 - ty) * n + tx, nrm

    def shade(vw, a, L, px):
        ok, tex, nrm = vw
        Nn = nrm - GI[tex] * (a[tex] * strength)[:, None]
        Nn /= np.maximum(np.linalg.norm(Nn, axis=1), 1e-12)[:, None]
        sh = np.clip(Nn @ L, 0, 1) * 0.9 + 0.15
        c = col[tex] * sh[:, None] * (1 - cavity * np.clip(CI[tex], 0, 1) * a[tex])[:, None]
        img = np.full((px * px, 3), 0.1)
        img[ok] = c
        return img.reshape(px, px, 3)

    La = np.array([0.0, 0.75, 0.66]); La /= np.linalg.norm(La)
    Lr = np.array([-0.45, 0.72, 0.5]); Lr /= np.linalg.norm(Lr)
    full = view(0.0, 1.53, 0.115, 560)
    row = [shade(full, act_img(regs), La, 560) for _, regs in EXPRESSIONS]
    write_png(os.path.join(d, 'wrinkle_face_expressions.png'), np.clip(np.concatenate(row, 1) * 255, 0, 255).astype(np.uint8))
    crops = {'brows up': (0.0, 1.588, 0.045), 'frown': (0.0, 1.548, 0.03), 'squint': (-0.045, 1.528, 0.035), 'smile': (-0.025, 1.495, 0.05),
             'nose scrunch': (0.0, 1.532, 0.028), 'frown lips': (0.0, 1.452, 0.038)}
    tiles = []
    for ename, regs in EXPRESSIONS[1:]:
        cx, cy, h = crops.get(ename, (0.0, 1.53, 0.05))
        vw = view(cx, cy, h, 600)                      # the crop centre in mesh x (< 0 = her left)
        tiles.append(np.concatenate([shade(vw, np.zeros(n * n), Lr, 600), np.full((600, 8, 3), 0.1), shade(vw, act_img(regs), Lr, 600)], 1))
    rows = [np.concatenate(tiles[i:i + 2], 1) if i + 1 < len(tiles) else np.pad(tiles[i], ((0, 0), (0, tiles[i].shape[1]), (0, 0)), constant_values=0.1)
            for i in range(0, len(tiles), 2)]
    rows = [np.pad(r, ((0, 10), (0, 0), (0, 0)), constant_values=0.1) for r in rows]
    write_png(os.path.join(d, 'wrinkle_face_closeups.png'), np.clip(np.concatenate(rows, 0) * 255, 0, 255).astype(np.uint8))
    log('face renders: %s (%s); close-ups (neutral | expression, light from above her left): wrinkle_face_closeups.png'
        % (os.path.join(d, 'wrinkle_face_expressions.png'), ', '.join(e[0] for e in EXPRESSIONS)))


def dilate(img, cov, it):
    """Spread the edge texels of the UV islands outwards (bilinear/mip bleed)."""
    img = img.copy()
    cov = cov.copy()
    for _ in range(it):
        acc = np.zeros_like(img)
        cnt = np.zeros(cov.shape)
        for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0)):
            sh = np.roll(np.roll(cov, dy, 0), dx, 1)
            acc += np.roll(np.roll(img, dy, 0), dx, 1) * sh[:, :, None]
            cnt += sh
        grow = (~cov) & (cnt > 0)
        img[grow] = acc[grow] / cnt[grow][:, None]
        cov = cov | grow
    return img


def to_u8(det):
    return np.clip(det * 255 + 0.5, 0, 255).astype(np.uint8)


# ---------------------------------------------------------------- her landmarks and creases


def unit(v):
    v = np.asarray(v, np.float64)
    return v / np.linalg.norm(v)


def face_creases(surf, sk, log):
    """0.11.25 face sculpt (user: the old maps were thin drawn lines, 'very poor'; references: MetaHuman-style per-expression wrinkle
    normal maps). Sculpted expression folds instead of lines: broad furrows with rolls of bunched skin between them, lines that vary in
    depth and width, break and overlap, and the soft masses an expression pushes up (the corrugator bunch between the brows, the lower
    lid's roll, the cheek mass over the nasolabial fold, the chin's dimpling). Grouped by expression like the references: brows up
    (forehead), frown / angry brows (glabella), squint (crow's feet, under the eyes), smile (nasolabial + cheek, with the eyes), nose
    scrunch (bunny lines), frown lips (chin, mouth corners). Her left = -x, front = +z, up = +y (Unity axes, bind space); the mm below
    are the sculpt at full activation and Strength 1."""
    P = lambda b: np.asarray(sk[b]['pos'], np.float64)  # noqa: E731
    front = np.array([0, 0, 1.0])
    upv = np.array([0, 1.0, 0])
    out = []
    regions = []
    rr = {}

    def reg(name, cls, rng, radius):
        regions.append((name, cls, rng))
        rr[len(regions) - 1] = radius
        return len(regions) - 1

    def on_front(x, y):
        return surf.ray(np.array([x, y, 0.4]), -front)

    def on_dir(p, nrm):
        return surf.ray(p + nrm * 0.08, -nrm, near=0.15)

    def line_front(pts2):
        res = [on_front(x, y) for x, y in pts2]
        return np.array([r for r in res if r is not None])

    def line_dir(pts3, nrm):
        res = [on_dir(np.asarray(p), nrm) for p in pts3]
        return np.array([r for r in res if r is not None])

    def frontal(q):
        return np.tile(front, (len(q), 1))

    def add(line, region, depth_mm, width_mm, seed, **kw):
        wob = kw.pop('wob', 0.0005)
        nf = kw.pop('nrm', frontal)
        if len(line) < 3:
            log('  face: a line of %s missed her skin' % regions[region][0])
            return
        out.append(Crease(wobble(line, nf, wob, seed) if wob > 0 else line, region, depth_mm * 0.001, width_mm * 0.001, seed=seed, **kw))

    def bunch(p, axis, r1_mm, r2_mm, h_mm, region, nrm=None):
        if p is None:
            log('  face: a bunch of %s missed her skin' % regions[region][0])
            return
        out.append(Bunch(p, axis, r1_mm * 0.001, r2_mm * 0.001, h_mm * 0.001, region))

    brow_in, brow_mid, brow_out = P('Eyebrow_Left_Insite'), P('Eyebrow_Left_Middle'), P('Eyebrow_Left_Outsite')
    brow_y = (brow_in[1] + brow_mid[1] + brow_out[1]) / 3
    canth, med = P('Eyelid_Left_Outsite_skin'), P('Eyelid_Left_Insite_skin')
    lowlid = [P('Eyelid_LeftLower_skin_%d' % k) for k in range(3)]
    raiser = P('Cheek_Left_Raiser')
    corner = P('Lip_Left_Inside')
    lower_lip = P('Lip_Lower_Insite')
    # the chin's most forward point on her midline (below the lower lip)
    chin = None
    for y in np.arange(lower_lip[1] - 0.012, lower_lip[1] - 0.05, -0.002):
        h = on_front(0.0, y)
        if h is not None and (chin is None or h[2] > chin[2]):
            chin = h
    rng0 = np.random.default_rng(7)

    # ---- frown / angry brows: glabella (one region, both sides)
    rg = reg('glabella', 'face', 0.035, 0.017)
    gy0, gy1 = brow_in[1] - 0.005, brow_in[1] + 0.013
    # (user: keep them feminine. The references are male / older faces: she is a young woman, so fewer, shallower, softer lines and
    # wider gentler folds; most of an expression is carried by the soft masses - the brow bunch, the lid roll, round cheeks - not lines)
    gy0, gy1 = brow_in[1] - 0.004, brow_in[1] + 0.0105
    for i, (x0, dep, sd) in enumerate(((-0.0058, 0.5, 1), (0.0052, 0.58, -1))):
        # the vertical '11' lines: short, soft, slightly curved and diverging upwards, one side a little deeper
        pts = [(x0 + sd * 0.0015 * (k / 6.0) ** 1.5 - sd * 0.0007 * math.sin(math.pi * k / 6.0), gy0 + (gy1 - gy0) * k / 6.0) for k in range(7)]
        add(line_front(pts), rg, dep, 1.4, 80 + i, ridge=0.5, roll_toward=np.array([-sd, 0, 0.0]), roll=(1.0, 0.5), breaks=0.0)
    for sgn in (1.0, -1.0):
        # corrugator bunch: the inner brow heads pulled together and down push up a soft mass above them
        bunch(on_front(sgn * -0.0125, brow_in[1] + 0.0045), unit([1, 0.35 * sgn, 0]), 9.0, 4.5, 0.55, rg)
    # procerus: one soft horizontal fold across the nose root with the skin bunched above it
    pts = [(-0.0065 + 0.013 * k / 8.0, brow_in[1] - 0.0095 + 0.001 * math.sin(math.pi * k / 8.0)) for k in range(9)]
    add(line_front(pts), rg, 0.22, 1.7, 88, ridge=0.55, roll_toward=upv, roll=(1.0, 0.3), breaks=0.0)
    bunch(on_front(0.0, brow_in[1] - 0.005), np.array([1.0, 0, 0]), 7.5, 3.4, 0.4, rg)

    for sn, sgn in (('L', 1.0), ('R', -1.0)):
        sx = -sgn                                   # her left = -x
        mir = lambda p, s=sgn: np.array([p[0] * s, p[1], p[2]])   # noqa: E731  (the left bone mirrored to this side)
        jit = 0.0003 * (1 if sn == 'L' else -1)     # the two sides are not mirror images
        seed0 = 0 if sn == 'L' else 500

        # ---- brows up: forehead furrows (frontalis). Five wavy furrows across the forehead, ~7 mm apart, with rolls of skin between them
        # (the profile's ridges overlap into a corrugation); each half runs from the midline (weaker there: the frontalis has two
        # bellies) to the temple, longer in the middle rows, the lowest dipping towards the brow's tail; depth and width wander along
        # the line and some lines break.
        # (feminine: three long soft lines, not five broken furrows)
        rf = reg('forehead_' + sn, 'face', 0.03, 0.011)
        for i in range(3):
            y = brow_y + 0.0165 + 0.0085 * i
            span = (0.047, 0.05, 0.042)[i]
            pts = []
            for k in range(13):
                u = k / 12.0
                pts.append((sx * u * span, y + jit * (i % 2 + 0.5) * u + 0.0018 * math.sin(math.pi * u) - 0.004 * u ** 2.2 - 0.0015 * u * (i == 0)
                            + 0.0006 * math.sin(5 * u + i + seed0)))
            add(line_front(pts), rf, (0.42, 0.48, 0.34)[i], 1.8, seed0 + 11 + i, taper=(0, 1), ridge=0.35, breaks=0.1, mid=(0.6, 0.012))
        # ---- squint: crow's feet. A fan of six lines out of the outer eye corner, curving around the orbit, different lengths, starting
        # 5-9 mm out from the corner, with puffy skin bunched between them and over the orbit's outer rim
        rc = reg('crowsfeet_' + sn, 'face', 0.025, 0.012)
        c = mir(canth)
        lat = unit([sx * 0.85, 0.0, -0.5])
        nrm = unit([sx * 0.8, 0.05, 0.6])
        # (feminine: four shorter, shallower lines; the puff of skin around them does most of the work)
        for i, (a, l0, ln, dep) in enumerate(((-38, 0.007, 0.016, 0.22), (-17, 0.006, 0.02, 0.32), (2, 0.0065, 0.019, 0.29), (21, 0.008, 0.014, 0.19))):
            pts = []
            for k in range(8):
                t = k / 7.0
                ar = math.radians(a + 6 * t * (1 if a < 0 else -1))    # the outer ends curve back around the orbit
                dv = unit(lat * math.cos(ar) + upv * math.sin(ar))
                pts.append(c + lat * l0 + dv * (ln * t))
            add(line_dir(pts, nrm), rc, dep, 1.5, seed0 + 20 + i, ridge=0.45, breaks=0.0, taper=(1, 1), nrm=lambda q, n=nrm: np.tile(n, (len(q), 1)))
        bunch(on_dir(c + lat * 0.011 + upv * 0.001, nrm), upv, 9.5, 6.5, 0.5, rc)
        # ---- squint: under the eye. The lower lid's roll of muscle (a raised band 3 mm under the lid), the crease at its lower edge, and
        # two fainter arcs lower down that fan out towards the outer corner
        ru = reg('undereye_' + sn, 'face', 0.025, 0.010)
        lid = sorted([mir(p) for p in lowlid], key=lambda p: abs(p[0]))
        xi, xo = mir(med)[0] + sx * 0.004, c[0]

        def arc(dy, bow, x0f=0.0, x1f=1.0, lid=lid, xi=xi, xo=xo):
            pts = []
            for k in range(11):
                u = x0f + (x1f - x0f) * k / 10.0
                x = xi + (xo - xi) * u
                ly = float(np.interp(abs(x), [abs(p[0]) for p in lid], [p[1] for p in lid]))
                pts.append((x, ly - dy - bow * math.sin(math.pi * u) + 0.0025 * u * u))
            return line_front(pts)
        # (feminine: the lid roll - the soft band of a young woman's smiling eyes - carries it; one soft crease under it, one faint arc)
        add(arc(0.0032, 0.0012), ru, -0.6, 2.0, seed0 + 40, ridge=0.0, wob=0.0003, breaks=0.0)        # the roll (a ridge: negative depth)
        add(arc(0.0064, 0.0016, 0.08, 0.95), ru, 0.36, 1.2, seed0 + 41, ridge=0.3, roll_toward=-upv, roll=(1.0, 0.0), breaks=0.0)
        add(arc(0.0100, 0.0016, 0.45, 1.0), ru, 0.11, 1.2, seed0 + 42, ridge=0.4, breaks=0.25)
        # ---- smile: the nasolabial fold and the cheek. A deep fold from beside the nose wing down and out to beside the mouth corner,
        # the cheek rolling over it on the outer side (the inner, lip side stays flat), and the cheek's apple pushed up and forward
        rn = reg('nasolabial_' + sn, 'face', 0.03, 0.015)
        mc = mir(corner)
        top = (sx * 0.0165, mc[1] + 0.026)
        end = (mc[0] + sx * 0.0095, mc[1] + 0.002)       # (feminine: it ends beside the corner)
        pts = [(top[0] + (end[0] - top[0]) * u + sx * 0.0045 * math.sin(math.pi * u), top[1] + (end[1] - top[1]) * u) for u in np.linspace(0, 1, 12)]
        add(line_front(pts), rn, 0.55, 2.3, seed0 + 60, taper=(1, 1), ridge=0.7, roll_toward=np.array([sx, 0.2, 0]), roll=(1.0, 0.1), breaks=0.0)
        cheek = [(x + sx * 0.0052, y + 0.0008) for x, y in pts[1:-1]]
        add(line_front(cheek), rn, -0.75, 4.0, seed0 + 61, taper=(1, 1), ridge=0.0, wob=0.0004, breaks=0.0)      # the cheek mass over the fold
        bunch(on_front(sx * 0.037, raiser[1] - 0.0085), unit([1, -0.25 * sgn, 0]), 14.0, 9.5, 1.0, rn)            # the cheek's apple
        # ---- nose scrunch: bunny lines, three oblique folds down the side of the nose bridge
        rb = reg('nose_' + sn, 'face', 0.03, 0.009)
        nn = unit([sx * 0.75, 0.15, 0.65])
        for i, (y0, ln, dep) in enumerate(((med[1] + 0.002, 0.011, 0.36), (med[1] - 0.0025, 0.009, 0.26))):
            p0 = np.array([sx * 0.0045, y0, 0.07])
            dv = unit([sx * 0.75, -0.55, -0.35])
            add(line_dir([p0 + dv * (ln * t) for t in np.linspace(0, 1, 7)], nn), rb, dep, 1.0, seed0 + 70 + i, ridge=0.5, breaks=0.0,
                nrm=lambda q, n=nn: np.tile(n, (len(q), 1)))
        # ---- frown lips: the mouth corner pulled down (depressor): a short marionette fold down and out, the modiolus bunched beside it
        rm = reg('mouthcorner_' + sn, 'face', 0.03, 0.009)
        pts = [(mc[0] + sx * (0.0035 + 0.0035 * u), mc[1] - 0.0055 - 0.013 * u) for u in np.linspace(0, 1, 8)]
        add(line_front(pts), rm, 0.28, 1.6, seed0 + 90, taper=(1, 1), ridge=0.6, roll_toward=np.array([sx, 0, 0]), roll=(1.0, 0.3), breaks=0.0)
        bunch(on_front(mc[0] + sx * 0.0065, mc[1] - 0.001), upv, 4.5, 5.5, 0.4, rm)

    # ---- frown lips: chin (mentalis). The lip-chin crease deepens, the chin pad bunches up and dimples ('orange peel')
    rch = reg('chin', 'face', 0.03, 0.010)
    if chin is not None:
        ys = lower_lip[1] - 0.0115
        pts = [(-0.012 + 0.024 * k / 10.0, ys - 0.0025 * (1 - ((k - 5) / 5.0) ** 2)) for k in range(11)]
        add(line_front(pts), rch, 0.4, 1.8, 95, taper=(1, 1), ridge=0.7, roll_toward=-upv, roll=(1.0, 0.25), breaks=0.0)
        bunch(on_front(0.0, chin[1] + 0.002), np.array([1.0, 0, 0]), 12.0, 8.0, 0.5, rch)
        for k in range(18):
            a, r = rng0.uniform(0, 2 * math.pi), math.sqrt(rng0.uniform(0, 1))
            p = on_front(r * 0.011 * math.cos(a), chin[1] + 0.001 + r * 0.008 * math.sin(a))
            bunch(p, np.array([1.0, 0, 0]), rng0.uniform(1.8, 2.8), rng0.uniform(1.6, 2.6), -rng0.uniform(0.06, 0.14), rch)
    else:
        log('face: no chin found on her midline; no chin region detail')
    n_line = sum(isinstance(c, Crease) for c in out)
    log('face: %d folds and %d bunches in %d regions' % (n_line, len(out) - n_line, len(regions)))
    return out, regions, rr, []


def unit_rows(v):
    v = np.asarray(v, np.float64)
    return v / np.maximum(np.linalg.norm(v, axis=1), 1e-12)[:, None]


def ring_crease(surf, centre, axis, front, arc_deg, along, depth, width, region, seed, radius=0.12):
    """A crease around a limb: the points of the limb's surface at `along` metres along `axis` from `centre`, over +-arc_deg about
    the `front` direction. Rays come from outside towards the axis."""
    axis = unit(axis)
    f = unit(front - np.dot(front, axis) * axis)
    s = np.cross(axis, f)
    pts = []
    for k in range(13):
        a = math.radians(-arc_deg + 2 * arc_deg * k / 12.0)
        d = f * math.cos(a) + s * math.sin(a)
        o = centre + axis * along + d * radius
        h = surf.ray(o, -d, near=radius + 0.1)
        if h is not None and np.linalg.norm(h - (centre + axis * along)) < radius * 0.9:
            pts.append(h)
    if len(pts) < 5:
        return None
    pts = np.array(pts)
    pts = wobble(pts, lambda q: unit_rows(q - (centre + axis * along)), 0.0006, seed)
    return Crease(pts, region, depth, width, seed=seed)


def body_creases(surf, sk, log):
    P = lambda b: sk[b]['pos']  # noqa: E731
    out, regions = [], []

    def reg(name, cls, rng):
        regions.append((name, cls, rng))
        return len(regions) - 1

    rr = {}
    for sn, side in (('L', 'L'), ('R', 'R')):
        sx = -1.0 if side == 'L' else 1.0
        # elbow: inner creases (2) and the loose skin on the back (3 short ones), separate regions (only the compressed side shows)
        el, sh, wr = P('Elbow_' + side), P('Shoulder_' + side), P('Wrist_' + side)
        ax = unit(wr - sh)
        fr = np.array([0, 0, 1.0])
        ri = reg('elbow_in_' + sn, 'body', 0.20)
        rr[ri] = 0.025
        for i, al in enumerate((-0.006, 0.004)):
            c = ring_crease(surf, el, ax, fr, 55, al, 0.0008, 0.0016, ri, 200 + i + (10 if side == 'R' else 0), radius=0.07)
            if c:
                out.append(c)
        rb = reg('elbow_back_' + sn, 'body', 0.20)
        rr[rb] = 0.025
        for i, al in enumerate((-0.008, 0.0, 0.008)):
            c = ring_crease(surf, el, ax, -fr, 40, al, 0.0004, 0.0011, rb, 220 + i + (10 if side == 'R' else 0), radius=0.07)
            if c:
                out.append(c)
        # knee: the crease at the back of the knee (2 lines)
        kn, hp, an = P('Knee_' + side), P('HipPart2_' + side), P('Ankle_' + side)
        lax = unit(an - hp)
        rk = reg('knee_back_' + sn, 'body', 0.20)
        rr[rk] = 0.035
        for i, al in enumerate((-0.004, 0.012)):
            c = ring_crease(surf, kn, lax, np.array([0, 0, -1.0]), 50, al, 0.0009, 0.0018, rk, 240 + i + (10 if side == 'R' else 0), radius=0.1)
            if c:
                out.append(c)
        # hip fold (groin / inguinal crease): from the front of the hip down and in towards the pubis. 0.11.47 (refs: seated with legs
        # apart the crease is a sharp, deep line with short folds fanning from it onto the compressed thigh): a narrow deep core with
        # the thigh's skin rolling over it, a fainter broken echo below, and 3-4 small folds branching off the core on the thigh side
        # (each leaves the crease at a shallow angle and curves away; no line crosses the crease). Per side its own shape and depths.
        rh = reg('hipfold_' + sn, 'body', 0.20)
        rr[rh] = 0.03
        so = 10 if side == 'R' else 0
        hr = np.random.default_rng(265 + so)
        thigh = unit([sx, -1.0, 0.0])                   # across the crease towards the thigh (down and out)

        def groin_xy(u, dy=0.0):
            return (sx * (0.105 - 0.075 * u), 0.905 - 0.075 * u + dy - 0.008 * math.sin(math.pi * u))

        def front_hit(x, y):
            return surf.ray(np.array([x, y, 0.4]), np.array([0, 0, -1.0]))

        #             dy      u0 .. u1      depth mm                width mm  ridge  breaks
        for i, (dy, u0, u1, dep, wid, rdg, brk) in enumerate(((0.0, 0.0, 1.0, 1.15 if side == 'L' else 1.05, 2.5, 0.6, 0.0),
                                                                (-0.016, 0.12, 0.8, 0.36 if side == 'L' else 0.3, 2.9, 0.4, 0.3))):
            pts = []
            for k in range(13):
                x, y = groin_xy(u0 + (u1 - u0) * k / 12.0, dy)
                h = front_hit(x, y)
                if h is not None:
                    pts.append(h)
            if len(pts) > 5:
                out.append(Crease(wobble(np.array(pts), lambda q: np.tile([0, 0, 1.0], (len(q), 1)), 0.0007, 260 + i + so),
                                  rh, dep * 0.001, wid * 0.001, seed=260 + i + so, ridge=rdg, roll_toward=thigh, roll=(1.0, 0.45),
                                  breaks=brk))
        nbr = 4 if side == 'L' else 3
        us = np.sort(hr.uniform(0.18, 0.86, nbr))
        for b, u in enumerate(us):
            x0, y0 = groin_xy(u)
            x1, y1 = groin_xy(min(u + 0.02, 1.0))
            tg = unit([x1 - x0, y1 - y0, 0.0])          # along the crease, towards the pubis
            nn = thigh - np.dot(thigh, tg) * tg
            nn = unit(nn)
            dirn = tg if hr.uniform() < 0.6 else -tg    # most fan towards the pubis
            ang = math.radians(hr.uniform(18, 30))
            bend = hr.uniform(0.15, 0.45) * (1 if hr.uniform() < 0.5 else -1)
            ln = hr.uniform(0.016, 0.026)
            p0 = np.array([x0, y0, 0.0]) + nn * 0.0035
            pts = []
            for k in range(9):
                v = k / 8.0
                a = ang * (1 + bend * v)
                q = p0 + (dirn * math.cos(a) + nn * math.sin(a)) * ln * v
                if np.dot(q - p0, nn) > 0.0065:         # stop short of the echo below (a fold crossing it read as a stitch)
                    break
                h = front_hit(q[0], q[1])
                if h is not None:
                    pts.append(h)
            if len(pts) > 4:
                c = Crease(np.array(pts), rh, (1.2 if side == 'L' else 1.1) * hr.uniform(0.25, 0.35) * 0.001, hr.uniform(0.0021, 0.0025),
                           taper=(0, 1), seed=340 + b + so, ridge=0.3)
                c.micro = False
                out.append(c)
        # armpit: creases in the hollow under the shoulder, running front to back, when the arm comes down. Cast from below and
        # outside (the armpit faces down and out in her bind pose); a ray from the front would land on the side of her breast.
        ra = reg('armpit_' + sn, 'body', 0.25)
        rr[ra] = 0.025
        shp, elb = P('Shoulder_' + side), P('Elbow_' + side)
        hol = shp + unit(elb - shp) * 0.03 + np.array([-sx * 0.035, -0.06, 0.0])
        nrm = unit([sx * 0.55, -0.83, 0.0])
        for i, dd in enumerate((-0.008, 0.0, 0.008)):
            pts = []
            for k in range(9):
                z = -0.035 + 0.06 * k / 8.0
                o = hol + np.array([sx * dd * 0.5, dd, z]) + nrm * 0.08
                h = surf.ray(o, -nrm, near=0.2)
                if h is not None and np.linalg.norm(h - (hol + np.array([0, 0, z]))) < 0.05:
                    pts.append(h)
            if len(pts) > 4:
                out.append(Crease(np.array(pts), ra, 0.0005, 0.0013, seed=280 + i + (10 if side == 'R' else 0)))
    # front of the neck: 2-3 soft horizontal creases across the whole front when she looks down / tucks her chin, uneven like a real
    # neck (headset 0.11.34: the 0.9.8 pair showed as two mirrored diagonal strokes): each line its own height, slight tilt and waves,
    # its own reach to either side, depth and width; not mirrored. Cast from inside the neck outwards (from the front they would hit
    # her chin first). Her front-of-neck UV patch is shared with the reika_neck piece under her chin (main(): the body layout is
    # evaluated on the upper body there, so the lines run through the middle instead of stopping at the patch's edge).
    rk = reg('neck', 'neck', 0.03)
    rr[rk] = 0.02
    #        height  depth mm width mm  reach left / right (deg)  tilt (m over the arc)  wave (m)  seed
    lines = ((1.3845, 0.45, 2.4, (-78, 70), 0.0012, 0.0011, 71),
             (1.3985, 0.55, 2.7, (-66, 84), -0.0009, 0.0013, 72),
             (1.4105, 0.3, 2.1, (-48, 58), 0.0007, 0.0008, 73))
    for y0, dep, wid, (a0, a1), tilt, wave, seed in lines:
        rng = np.random.default_rng(seed)
        ph = rng.uniform(0, 6.28, 3)
        ctr = np.array([0, y0, -0.045])
        pts = []
        for k in range(25):
            a = math.radians(a0 + (a1 - a0) * k / 24.0)
            u = a / math.radians(80)
            y = y0 + tilt * u + wave * (0.6 * math.sin(2.1 * a + ph[0]) + 0.4 * math.sin(4.7 * a + ph[1]))
            c2 = np.array([0, y, -0.045])
            h = surf.ray(c2, unit([math.sin(a), 0, math.cos(a)]), near=0.15)
            if h is not None and np.linalg.norm(h - c2) < 0.08:
                pts.append(h)
        if len(pts) > 8:
            out.append(Crease(wobble(np.array(pts), lambda q: unit_rows(q - ctr), 0.0005, seed + 10),
                              rk, dep * 0.001, wid * 0.001, seed=seed, ridge=0.35))
    # belly (0.11.40): when she sits, squats or curls forward her belly folds AT THE NAVEL, the skin bunching into a soft roll above
    # the fold; to the sides the fold droops towards her flanks (the waist folds down over the hip). 0.9.5's fold sat 2 cm below the
    # navel and rose to the sides: exactly a caesarean scar's place and smile shape (headset: still read as a scar). Now: the main fold
    # a few millimetres above the navel, sagging to the sides, broken in places, its roll above it; a fainter upper fold 3-4 cm higher,
    # shorter and off-centre; and the micro lines around both (main()). Not mirrored.
    rbel = reg('belly', 'body', 0.20)
    rr[rbel] = 0.045
    nav = None
    for y in np.arange(0.92, 1.03, 0.002):          # the navel: the deepest point of her front midline in that band
        h = surf.ray(np.array([0.0, y, 0.4]), np.array([0, 0, -1.0]))
        if h is not None and (nav is None or h[2] < nav[2]):
            nav = h
    ny = nav[1] if nav is not None else 0.97
    log('belly: navel at y %.3f' % ny)
    #       height above the navel, sag to the sides (m), reach left / right (m), depth mm, width mm, seed, breaks
    # (no breaks: on a fold this wide they cut it into a row of dashes, which read as stitches in the preview)
    folds = ((0.004, 0.022, (-0.105, 0.095), 1.0, 7.0, 300, 0.0),
             (0.037, 0.012, (-0.055, 0.075), 0.45, 5.0, 301, 0.0))
    for dy, sag, (x0, x1), dep, wid, seed, brk in folds:
        rng = np.random.default_rng(seed)
        ph = rng.uniform(0, 6.28, 2)
        pts = []
        for k in range(21):
            x = x0 + (x1 - x0) * k / 20.0
            xn = x / 0.1
            y = ny + dy - sag * xn * xn + 0.0015 * math.sin(3.1 * xn + ph[0]) + 0.004 * xn * 0.3
            h = surf.ray(np.array([x, y, 0.4]), np.array([0, 0, -1.0]))
            if h is not None:
                pts.append(h)
        if len(pts) > 8:
            out.append(Crease(wobble(np.array(pts), lambda q: np.tile([0, 0, 1.0], (len(q), 1)), 0.0009, seed + 5),
                              rbel, dep * 0.001, wid * 0.001, seed=seed, ridge=0.75, roll_toward=np.array([0, 1.0, 0]), roll=(1.0, 0.35),
                              breaks=brk))

    # 0.11.47 trunk folds from the reference images / videos. Points are cast from inside her trunk outwards (from outside, rays at
    # the waist meet her arms first): a = degrees from her front round to her side sx (90 = the side, 180 = the back).
    def around(samples, sxx, zc=0.0):
        pts = []
        for a, y in samples:
            ar = math.radians(a)
            c = np.array([0.0, y, zc])
            h = surf.ray(c, np.array([sxx * math.sin(ar), 0.0, math.cos(ar)]), near=0.25)
            if h is not None and np.linalg.norm(h - c) < 0.2:
                pts.append(h)
        return np.array(pts)

    def trunk_line(sxx, a0, a1, y0, drop, wave, seed, n=21, bow=0.0):
        """a line from angle a0 at height y0 to a1, dropping `drop` metres on the way, bowing up by `bow` in the middle, with a slow
        irregular wave"""
        ph = np.random.default_rng(seed).uniform(0, 6.28, 2)
        smp = []
        for k in range(n):
            u = k / (n - 1.0)
            smp.append((a0 + (a1 - a0) * u, y0 - drop * u + bow * math.sin(math.pi * u) + wave * (0.6 * math.sin(5.3 * u + ph[0]) + 0.4 * math.sin(11.0 * u + ph[1]))))
        pts = around(smp, sxx)
        if len(pts) < 8:
            return None
        return wobble(pts, lambda q: unit_rows(q * np.array([1.0, 0.0, 1.0])), 0.0006, seed + 5)

    up = np.array([0, 1.0, 0])
    # flank (refs: back / side, arched with a twist or a side bend): 3-5 stacked near-horizontal creases between the 12th rib and the
    # iliac crest, from the erector column round to just behind her side, sloping a little down towards the front, 15-25 mm apart,
    # the middle ones the deepest and longest, the skin rolling above each. Only the compressed side's region activates, so the
    # one-sidedness comes from the plugin's per-region compression. Per side its own count, heights, reach and depths.
    #       height at the back, back / front end (deg), drop to the front (m), depth mm, width mm, wave (m), seed
    flank = {'L': ((1.075, 150, 110, 0.004, 0.55, 3.2, 0.0012, 400),
                   (1.054, 160, 102, 0.007, 0.95, 3.8, 0.0015, 401),
                   (1.031, 163, 99, 0.008, 1.1, 4.0, 0.0013, 402),
                   (1.010, 154, 107, 0.006, 0.7, 3.4, 0.0011, 403)),
             'R': ((1.088, 144, 116, 0.003, 0.5, 2.8, 0.0010, 410),
                   (1.069, 155, 104, 0.005, 0.75, 3.4, 0.0013, 411),
                   (1.048, 162, 100, 0.006, 1.05, 3.9, 0.0016, 412),
                   (1.025, 158, 102, 0.009, 0.9, 3.7, 0.0012, 413),
                   (1.004, 149, 110, 0.005, 0.6, 3.0, 0.0010, 414))}
    # side waist (refs: a crunch / a side bend): 2-4 diagonal folds on the side of the waist running down and forward (~20-25 deg),
    # 10-15 mm apart, 50-80 mm long, a sharper core; in front of the flank creases (they do not meet: separate poses)
    #       height at the back end, back / front end (deg), drop (m), depth mm, width mm, wave (m), seed
    waist = {'L': ((1.090, 86, 57, 0.024, 0.55, 2.4, 0.0008, 420),
                   (1.076, 88, 52, 0.029, 0.85, 2.8, 0.0009, 421),
                   (1.061, 84, 55, 0.026, 0.7, 2.6, 0.0008, 422)),
             'R': ((1.094, 82, 63, 0.016, 0.4, 2.2, 0.0006, 430),
                   (1.082, 87, 55, 0.027, 0.8, 2.7, 0.0009, 431),
                   (1.068, 86, 52, 0.030, 0.85, 2.8, 0.0009, 432),
                   (1.054, 83, 59, 0.022, 0.55, 2.4, 0.0007, 433))}
    for side in ('L', 'R'):
        sxx = -1.0 if side == 'L' else 1.0
        rf = reg('flank_' + side, 'body', 0.12)
        rr[rf] = 0.025
        for y0, a0, a1, drop, dep, wid, wave, seed in flank[side]:
            p = trunk_line(sxx, a0, a1, y0, drop, wave, seed)
            if p is not None:
                c = Crease(p, rf, dep * 0.001, wid * 0.001, seed=seed, ridge=0.8, roll_toward=up, roll=(1.0, 0.25))
                c.micro = dep >= 0.9     # micro lines only around the deep middle folds (on all of them the flank read as cracked)
                out.append(c)
        rw = reg('sidewaist_' + side, 'body', 0.10)
        rr[rw] = 0.016
        for y0, a0, a1, drop, dep, wid, wave, seed in waist[side]:
            p = trunk_line(sxx, a0, a1, y0, drop, wave, seed, n=15, bow=0.004)     # bunched skin bows: straight ones read as scratches
            if p is not None:
                out.append(Crease(p, rw, dep * 0.001, wid * 0.001, seed=seed, ridge=0.75, roll_toward=up, roll=(1.0, 0.3)))
    # upper belly (ref: sitting slightly hunched): two transverse creases across the upper belly under the breasts, the upper one
    # arched up in the middle (along the costal margin), the lower one nearly straight with a slight dip, each with its roll on
    # the side facing the other so a paler raised band runs between them
    rub = reg('upperbelly', 'body', 0.15)
    rr[rub] = 0.025
    #        height above the navel, arch at the middle (m), left / right end (deg), depth mm, width mm, roll towards, seed
    for dy, arch, (a0, a1), dep, wid, rt, seed in ((0.124, 0.009, (-38, 34), 0.4, 3.4, -up, 440),
                                                    (0.070, -0.003, (-36, 42), 0.42, 3.6, up, 441)):
        ph = np.random.default_rng(seed).uniform(0, 6.28, 2)
        smp = []
        for k in range(23):
            a = a0 + (a1 - a0) * k / 22.0
            an = a / 45.0
            smp.append((a, ny + dy + arch * (1 - an * an) + 0.0012 * math.sin(4.0 * an + ph[0]) + 0.0015 * an))
        pts = around(smp, 1.0)                          # negative angles: her left
        if len(pts) > 12:
            c = Crease(wobble(pts, lambda q: np.tile([0, 0, 1.0], (len(q), 1)), 0.0006, seed + 5), rub, dep * 0.001, wid * 0.001,
                       seed=seed, ridge=0.7, roll_toward=rt, roll=(1.0, 0.3), breaks=0.12)
            c.micro = False      # two plain rolls (with echoes beside them the upper one read as a doubled, drawn line)
            out.append(c)
    log('body: %d creases in %d regions' % (len(out), len(regions)))
    excl = []
    for side in ('Left', 'Right'):
        c = P(side + '_LowerBreast')
        r = max(np.linalg.norm(P(side + '_Breast_' + k) - c) for k in ('out', 'up', 'down', 'in')) + 0.03
        excl.append((c, r))
    return out, regions, rr, excl


# ---------------------------------------------------------------- main



# ---------------------------------------------------------------- avatars (0.11.48)

# The UV sheets of an avatar base figure: (layout name, surface name in the avatar export, face sheet). Every character of a figure
# shares its UVs, so one set of layouts serves them all. Genesis 9 keeps its skin in UDIM tiles (Head 0, Body 1, Legs 2, Arms 3): each
# sheet is its own texture, so the tile offset comes off (frac) and the plugin does the same at runtime.
AVATAR_SHEETS = {
    'g9': [('g9head', 'Head', True), ('g9body', 'Body', False), ('g9arms', 'Arms', False), ('g9legs', 'Legs', False)],
}


def avatar_parts(m, surface):
    """The triangles of one surface of the avatar export with its UVs moved into 0..1 (the UDIM tile of its triangles taken off)."""
    for gi, g in enumerate(m.groups):
        if g != surface:
            continue
        f = m.faces[m.face_group == gi]
        if not len(f):
            return None
        tile = np.floor(np.median(m.uv[f.reshape(-1)], axis=0))
        uv = m.uv - tile[None, :]
        return [(m.v, uv, f)], tile
    return None


def shifted(prims, k0):
    """Copies of creases / bunches with their region index moved by k0 (the body's regions after the face's on a head sheet)."""
    out = []
    for p in prims:
        q = copy.copy(p)
        q.region = p.region + k0
        out.append(q)
    return out


def merge_table(path, new_blocks, header):
    """wrinkle_layouts.txt with the layouts authored now replaced or added and every other layout kept as it was."""
    blocks, order = {}, []
    if os.path.isfile(path):
        cur = None
        for l in open(path, encoding='utf-8').read().splitlines():
            if l.startswith('layout '):
                cur = l.split()[1]
                order.append(cur)
                blocks[cur] = [l]
            elif cur is not None and l.strip():
                blocks[cur].append(l)
    for name, lines_ in new_blocks:
        if name not in blocks:
            order.append(name)
        blocks[name] = lines_
    with open(path, 'w', newline='\n') as f:
        f.write('\n'.join(header + [l for name in order for l in blocks[name]]) + '\n')


def avatar_main(d, figure, preview, log):
    """Wrinkle layouts for an avatar base figure from tools/avatar/drive_test.ps1's export of the avatar mesh rebuilt onto her
    skeleton (her bind pose, her bones): the creases are placed in 3D exactly as on her (her face and limb bones, ray cast onto the
    avatar's skin: the body creases on the whole body, the face creases on the head), then each UV sheet is rasterised on its own."""
    meshes, sk, missing = sfio.read_game_export(d)
    if not meshes:
        log('avatar export %s: no mesh' % d)
        sys.exit(1)
    m = max(meshes, key=lambda x: len(x.v))
    sheets = AVATAR_SHEETS[figure]
    log('avatar %s: %s, %d vertices, surfaces %s' % (figure, m.name, len(m.v), ', '.join(m.groups)))
    parts, tiles = {}, {}
    for lname, surface, face in sheets:
        r = avatar_parts(m, surface)
        if r is None:
            log('%s: no surface %s in the export; skipped' % (lname, surface))
            continue
        parts[lname], tiles[lname] = r
        log('%s: surface %s, UV tile (%d, %d) taken off' % (lname, surface, tiles[lname][0], tiles[lname][1]))
    full = Surf([p for l in parts for p in parts[l]])
    bc, bregs, brad, bexcl = body_creases(full, sk, log)
    bc = add_micro(bc, full, bregs, 0.0011, log)
    blocks = []
    for lname, surface, face in sheets:
        if lname not in parts:
            continue
        surf = Surf(parts[lname])
        prims, regions, rad, excl = bc, list(bregs), {k: brad.get(k, 0.01) for k in range(len(bregs))}, list(bexcl)
        if face:
            fc, fregs, frad, fexcl = face_creases(surf, sk, log)
            fc = add_micro(fc, surf, fregs, 0.00045, log, scale=0.8)
            nf = len(fregs)
            prims = fc + shifted(bc, nf)
            regions = list(fregs) + list(bregs)
            rad = {k: frad.get(k, 0.01) for k in range(nf)}
            rad.update({k + nf: brad.get(k, 0.01) for k in range(len(bregs))})
            excl = list(fexcl) + list(bexcl)
        if len(regions) > 32:
            log('%s: %d regions, more than the plugin takes (32): skipped' % (lname, len(regions)))
            continue
        n, nreg = 2048, 1024
        det, reg, cov, cav, idx, best, wbest = build_sum(lname, surf, prims, regions, n, nreg, rad, log, excl)
        write_png(os.path.join(OUT, 'wrinkle_%s_detail.png' % lname), to_u8(det))
        write_png(os.path.join(OUT, 'wrinkle_%s_region.png' % lname), reg)
        bl = ['layout %s %d %d' % (lname, n, nreg)]
        for k, (rn, rc, rg) in enumerate(regions):
            bl.append('region %d %s %s %.3f' % (k + 1, rn, rc, rg))
        blocks.append((lname, bl))
        if preview:
            os.makedirs(PREVIEW, exist_ok=True)
            img = np.full((n * n, 3), 128.0)
            hue = np.array([[1, .2, .2], [.2, 1, .2], [.2, .4, 1], [1, 1, .2], [1, .2, 1], [.2, 1, 1], [1, .6, .2], [.6, .2, 1]])
            a = (0.35 * (wbest > 0.02))[:, None]
            img[idx] = img[idx] * (1 - a) + hue[best % len(hue)] * 255 * a
            img[idx] = img[idx] * (1 - 0.85 * np.clip(cav, 0, 1)[:, None])
            write_png(os.path.join(PREVIEW, 'wrinkle_%s_preview.png' % lname), np.clip(img.reshape(n, n, 3), 0, 255).astype(np.uint8))
            if face:
                face_renders(regions, None, n, PREVIEW, log)
    header = ['# SF Body Morphs wrinkle layouts (tools/wrinkles/author.py). layout <name> <detail size> <region size>; then',
              '# region <index from 1> <name> <class face|neck|body> <compression range>',
              '# face / body: her export; %s*: avatar export %s' % (figure, os.path.basename(os.path.normpath(d)))]
    merge_table(os.path.join(OUT, 'wrinkle_layouts.txt'), blocks, header)
    return blocks


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    preview = '--preview' in sys.argv
    d = args[0] if args else sfio.newest_subdir(GAME_EXPORT)
    lines = []

    def log(m):
        print(m)
        lines.append(m)
    # 0.11.48: --avatar=<export of the avatar mesh on her skeleton> [--figure=g9]: the avatar's UV sheets instead of hers
    av = [a[9:] for a in sys.argv[1:] if a.startswith('--avatar=')]
    if av:
        fig = ([a[9:] for a in sys.argv[1:] if a.startswith('--figure=')] or ['g9'])[0]
        log('avatar export: ' + av[0])
        avatar_main(av[0], fig, preview, log)
        with open(os.path.join(HERE, 'author_%s.log' % fig), 'w', newline='\n') as f:
            f.write('\n'.join(lines) + '\n')
        return
    log('export: ' + d)
    meshes, sk, missing = sfio.read_game_export(d)
    by = {}
    for m in meshes:
        by[m.name.replace(' (SfSoft)', '')] = m

    def parts(names, mat):
        res = []
        for nm in names:
            m = by.get(nm)
            if m is None:
                continue
            for gi, g in enumerate(m.groups):
                if g == mat:
                    res.append((m.v, m.uv, m.faces[m.face_group == gi]))
        return res

    os.makedirs(OUT, exist_ok=True)
    table = ['# SF Body Morphs wrinkle layouts (tools/wrinkles/author.py). layout <name> <detail size> <region size>; then',
             '# region <index from 1> <name> <class face|neck|body> <compression range>',
             '# export ' + os.path.basename(d)]
    jobs = [
        ('face', ['reika_face_a'], 'reika_face_a', face_creases, 2048, 1024),
        # 0.11.35: reika_neck (under her chin) shares its UVs with the upper body's front of the neck; evaluated on the upper body (the
        # visible throat) so the neck lines cross it. The neck renderer still finds its region in the region map at runtime.
        ('body', ['reika_upperBody_normal', 'reika_lowerBody_normal', 'reika_foot'], 'reika_body', body_creases, 2048, 1024),
    ]
    only = [a[7:] for a in sys.argv[1:] if a.startswith('--only=')]   # 0.11.25: --only=face while iterating (layouts.txt keeps the others)
    for name, mnames, mat, fn, n, nreg in jobs:
        if only and name not in only:
            continue
        pr = parts(mnames, mat)
        if not pr:
            log('%s: none of %s in the export; skipped' % (name, mnames))
            continue
        surf = Surf(pr)
        creases, regions, rad, excl = fn(surf, sk, log)
        rad = {k: rad.get(k, 0.01) for k in range(len(regions))}
        # 0.11.40 micro lines around every fold (face: down to 0.45 mm; body: 1.1 mm, about two texels of its atlas) and the body through
        # the summing build too (a fold and its micro lines add up instead of the strongest one winning per texel)
        if name == 'face':
            creases = add_micro(creases, surf, regions, 0.00045, log, scale=0.8)
        else:
            creases = add_micro(creases, surf, regions, 0.0011, log)   # no feathers: short lines across a fold read as stitches (preview)
        det, reg, cov, cav, idx, best, wbest = build_sum(name, surf, creases, regions, n, nreg, rad, log, excl)
        write_png(os.path.join(OUT, 'wrinkle_%s_detail.png' % name), to_u8(det))
        write_png(os.path.join(OUT, 'wrinkle_%s_region.png' % name), reg)
        table.append('layout %s %d %d' % (name, n, nreg))
        for k, (rn, rc, rg) in enumerate(regions):
            table.append('region %d %s %s %.3f' % (k + 1, rn, rc, rg))
        if preview:
            os.makedirs(PREVIEW, exist_ok=True)
            col = None
            for f in os.listdir(d):
                if f.lower() == (mat + '_ColorMap.png').lower():
                    col = read_png_rgb(os.path.join(d, f))
            img = np.full((n, n, 3), 128, np.float64)
            if col is not None:
                ys = (np.arange(n) * col.shape[0] // n)
                xs = (np.arange(n) * col.shape[1] // n)
                img = col[ys][:, xs].astype(np.float64)
            flat = img.reshape(-1, 3)
            hue = np.array([[1, .2, .2], [.2, 1, .2], [.2, .4, 1], [1, 1, .2], [1, .2, 1], [.2, 1, 1], [1, .6, .2], [.6, .2, 1]])
            reg_on = wbest > 0.02
            tint = hue[best % len(hue)]
            a = (0.35 * reg_on)[:, None]
            flat[idx] = flat[idx] * (1 - a) + tint * 255 * a
            dark = np.clip(cav, 0, 1)[:, None]
            flat[idx] = flat[idx] * (1 - 0.85 * dark)
            write_png(os.path.join(PREVIEW, 'wrinkle_%s_preview.png' % name), np.clip(flat.reshape(n, n, 3), 0, 255).astype(np.uint8))
            if name == 'face':
                face_renders(regions, col, n, PREVIEW, log)
    if only:
        return
    # 0.11.48: the avatar layouts (--avatar) stay in the table
    hdr = [l for l in table if l.startswith('#')]
    blocks = []
    for l in table:
        if l.startswith('layout '):
            blocks.append((l.split()[1], [l]))
        elif not l.startswith('#') and blocks:
            blocks[-1][1].append(l)
    merge_table(os.path.join(OUT, 'wrinkle_layouts.txt'), blocks, hdr)
    with open(os.path.join(HERE, 'author.log'), 'w', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')


if __name__ == '__main__':
    main()
