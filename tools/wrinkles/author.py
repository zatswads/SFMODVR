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
Needs numpy only. --preview also writes her colour maps with the creases and regions drawn on, next to the outputs, as *_preview.png
(not part of the bundle: tools/wrinkles/preview/).
"""
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

    def raster(self, n):
        """Texel centres in UV -> (triangle id or -1, barycentric weights). Row 0 = v 1."""
        tri = np.full(n * n, -1, np.int64)
        bary = np.zeros((n * n, 3), np.float32)
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
                tri[pix] = t[ti]
                bary[pix] = np.stack([l0[ti, ci], l1[ti, ci], l2[ti, ci]], 1)
        return tri, bary


# ---------------------------------------------------------------- creases


class Crease:
    def __init__(self, pts, region, depth, width, taper=(1, 1), seed=0, ridge=0.25):
        self.p = np.asarray(pts, np.float64)
        self.region = region
        self.depth = depth
        self.width = width
        self.taper = taper            # (start, end): 1 = fade to 0 at that end, 0 = full depth to the end (a half of a symmetric line)
        self.seed = seed
        self.ridge = ridge            # height of the rounded skin beside the crease, as a share of its depth (a soft roll: ~0.6)


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


# ---------------------------------------------------------------- layout build


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
               ('smile / squint', ('crowsfeet_L', 'crowsfeet_R', 'undereye_L', 'undereye_R', 'nasolabial_L', 'nasolabial_R')),
               ('nose scrunch', ('nose', 'glabella')))


def face_renders(regions, colour, n, d, log, strength=1.0, cavity=0.3):
    X, N, G, cav, best, fade, idx = (LAST[k] for k in ('X', 'N', 'G', 'cav', 'best', 'fade', 'idx'))
    base = np.full((len(idx), 3), 0.75)
    if colour is not None:
        ys = (np.arange(n) * colour.shape[0] // n)
        xs = (np.arange(n) * colour.shape[1] // n)
        base = colour[ys][:, xs].reshape(-1, 3)[idx].astype(np.float64) / 255.0
    names = [r[0] for r in regions]
    lo, hi = X.min(0), X.max(0)
    xr = (lo[0] - 0.01, hi[0] + 0.01)
    yr = (lo[1] - 0.005, hi[1] + 0.005)
    rows = []
    for lname, L in (('above', np.array([0.0, 0.75, 0.66])), ('side', np.array([0.7, 0.35, 0.62]))):
        L = L / np.linalg.norm(L)
        row = []
        for ename, regs in EXPRESSIONS:
            act = np.array([1.0 if names[k] in regs else 0.0 for k in range(len(names))])
            a = act[best] * fade
            Nn = N - G * (a * strength)[:, None]
            Nn /= np.maximum(np.linalg.norm(Nn, axis=1), 1e-12)[:, None]
            sh = np.clip(Nn @ L, 0, 1) * 0.85 + 0.2
            c = base * sh[:, None] * (1 - cavity * np.clip(cav, 0, 1) * a)[:, None]
            row.append(splat_face(X, c, xr, yr, 700))
        rows.append(np.concatenate(row, 1))
    path = os.path.join(d, 'wrinkle_face_expressions.png')
    write_png(path, np.clip(np.concatenate(rows, 0) * 255, 0, 255).astype(np.uint8))
    log('face renders (columns: %s; rows: light from above, from her left): %s' % (', '.join(e[0] for e in EXPRESSIONS), path))


def splat_face(X, c, xr, yr, H):
    """Orthographic front view (looking at her face along -z), nearest point wins, each point covers 2 px around it."""
    s = H / (yr[1] - yr[0])
    W = int((xr[1] - xr[0]) * s)
    ix = ((xr[1] - X[:, 0]) * s).astype(np.int64)          # her left (-x) on the image's right, as seen facing her
    iy = ((yr[1] - X[:, 1]) * s).astype(np.int64)
    img = np.full((H, W, 3), 0.1)
    f = img.reshape(-1, 3)
    o = np.argsort(X[:, 2])
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            lin = np.clip(iy + dy, 0, H - 1) * W + np.clip(ix + dx, 0, W - 1)
            f[lin[o]] = c[o]
    return img


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
    """Face layout creases. Her left = -x, front = +z, up = +y (Unity axes, bind space)."""
    P = lambda b: sk[b]['pos']  # noqa: E731
    front = np.array([0, 0, 1.0])
    out = []

    def on_front(x, y, back=None):
        h = surf.ray(np.array([x, y, 0.4]), -front)
        return h

    def on_dir(p, nrm):
        h = surf.ray(p + nrm * 0.08, -nrm, near=0.15)
        return h

    def line_front(pts2):
        res = [on_front(x, y) for x, y in pts2]
        return [r for r in res if r is not None]

    brow_in, brow_out = P('Eyebrow_Left_Insite'), P('Eyebrow_Left_Outsite')
    brow_y = 0.5 * (brow_in[1] + brow_out[1])
    canth = P('Eyelid_Left_Outsite_skin')
    med = P('Eyelid_Left_Insite_skin')
    low1 = P('Eyelid_LeftLower_skin_1')
    raiser = P('Cheek_Left_Raiser')
    corner = P('Lip_Left_Inside')
    # forehead top: the highest front-facing skin straight above the brows before the hairline (her face mesh is capped by hair)
    top = brow_y + 0.045
    regions = []

    def reg(name, cls, rng):
        regions.append((name, cls, rng))
        return len(regions) - 1

    side_names = (('L', 1.0), ('R', -1.0))
    for sn, sgn in side_names:
        mx = lambda pts: [(p[0] * sgn, p[1]) for p in pts]  # noqa: E731
        sx = -1.0 * sgn         # her left = -x
        # forehead: 4 lines across the forehead, each a half from the midline to the temple (full depth at the midline)
        # 0.9.2: face ranges from the headset log (her bone-driven face peaks at 0.03-0.05 compression, 0.08-0.10 left it at 0-30 %)
        rf = reg('forehead_' + sn, 'face', 0.03)
        for i, f in enumerate((0.20, 0.42, 0.63, 0.82)):
            y = brow_y + 0.006 + f * (top - brow_y - 0.006)
            span = 0.052 - 0.010 * f
            pts = []
            for k in range(9):
                u = k / 8.0
                x = sx * u * span
                pts.append((x, y + 0.004 * math.sin(math.pi * u) - 0.006 * u * u + 0.001 * i * u))
            line = line_front(pts)
            if len(line) > 3:
                out.append(Crease(wobble(np.array(line), lambda q: np.tile(front, (len(q), 1)), 0.0006, 11 + i + 50 * (sgn > 0)),
                                  rf, 0.00045 - 0.00006 * i, 0.0009, taper=(0, 1), seed=i + 100 * (sgn > 0)))
        # crow's feet: 4 short lines fanning out of the outer eye corner, on the temple
        rc = reg('crowsfeet_' + sn, 'face', 0.025)
        c = np.array([canth[0] * (1 if sgn > 0 else -1), canth[1], canth[2]])
        lat = unit([sx * 0.85, 0.0, -0.5])
        nrm = unit([sx * 0.8, 0.05, 0.6])
        upv = np.array([0, 1.0, 0])
        for i, a in enumerate((-32, -12, 8, 28)):
            ar = math.radians(a)
            dv = unit(lat * math.cos(ar) + upv * math.sin(ar))
            p0 = c + lat * 0.006
            pts = [on_dir(p0 + dv * (0.0045 * k), nrm) for k in range(6)]
            pts = [p for p in pts if p is not None]
            if len(pts) > 3:
                out.append(Crease(np.array(pts), rc, 0.0004, 0.0008, seed=20 + i + 50 * (sgn > 0)))
        # under the eye: one soft arc below the lower lid (the lid bones' skin), when the cheek pushes up
        ru = reg('undereye_' + sn, 'face', 0.025)
        l1 = np.array([low1[0] * (1 if sgn > 0 else -1), low1[1], low1[2]])
        m1 = np.array([med[0] * (1 if sgn > 0 else -1), med[1], med[2]])
        pts2 = []
        for k in range(9):
            u = k / 8.0
            x = m1[0] + (c[0] - m1[0]) * u
            y = l1[1] - 0.006 - 0.003 * math.sin(math.pi * u) + 0.002 * u
            pts2.append((x, y))
        line = line_front(pts2)
        if len(line) > 3:
            out.append(Crease(np.array(line), ru, 0.00035, 0.0009, seed=40 + 50 * (sgn > 0)))
        line = line_front([(x, y - 0.0045) for x, y in pts2[2:]])
        if len(line) > 3:
            out.append(Crease(np.array(line), ru, 0.00025, 0.0008, seed=41 + 50 * (sgn > 0)))
        # nasolabial fold: from beside the nose wing down and out to beside the mouth corner
        rn = reg('nasolabial_' + sn, 'face', 0.03)
        mc = np.array([corner[0] * (1 if sgn > 0 else -1), corner[1], corner[2]])
        nose_wing = (sx * 0.0175, mc[1] + 0.024)
        end = (mc[0] + sx * 0.009, mc[1] + 0.002)       # ends beside the mouth corner, not below it
        pts2 = []
        for k in range(10):
            u = k / 9.0
            x = nose_wing[0] + (end[0] - nose_wing[0]) * u + sx * 0.004 * math.sin(math.pi * u)
            y = nose_wing[1] + (end[1] - nose_wing[1]) * u
            pts2.append((x, y))
        line = line_front(pts2)
        if len(line) > 3:
            out.append(Crease(np.array(line), rn, 0.0007, 0.0014, taper=(0, 1), seed=60 + 50 * (sgn > 0)))
    # glabella: two short vertical lines between the brows (frown), plus a faint horizontal one at the nose root
    rg = reg('glabella', 'face', 0.035)
    bi = P('Eyebrow_Left_Insite')
    for i, x in enumerate((-0.0055, 0.0055)):
        line = line_front([(x + 0.0008 * (k - 3) * (1 if x > 0 else -1) * 0.3, bi[1] - 0.004 + 0.0035 * k) for k in range(7)])
        if len(line) > 3:
            out.append(Crease(np.array(line), rg, 0.0005, 0.0008, seed=80 + i))
    line = line_front([(-0.008 + 0.002 * k, bi[1] - 0.011) for k in range(9)])
    if len(line) > 3:
        out.append(Crease(np.array(line), rg, 0.0003, 0.0009, seed=83))
    # bunny lines: short oblique lines on the sides of the nose bridge (nose wrinkle)
    rb = reg('nose', 'face', 0.03)
    for sgn in (1.0, -1.0):
        for i in range(2):
            x0, y0 = sgn * 0.006, med[1] - 0.008 - 0.004 * i
            line = line_front([(x0 + sgn * 0.0018 * k, y0 + 0.0016 * k) for k in range(6)])
            if len(line) > 3:
                out.append(Crease(np.array(line), rb, 0.00022, 0.0007, seed=90 + i + 5 * (sgn > 0)))
    # (the front of her neck is on the body atlas: see body_creases. 0.9.1-0.9.6 cast its lines from the front here, which put them on
    # her chin at those heights: two lines running down from under her mouth.)
    for c in out:
        c.depth *= 1.3        # 0.9.2: her skin's subsurface scattering softens the lines (headset: 'barely affects her face')
    log('face: %d creases in %d regions' % (len(out), len(regions)))
    return out, regions, {k: (0.010 if regions[k][1] != 'neck' else 0.015) for k in range(len(regions))}, []


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
        # hip fold (groin crease): from the front of the hip down and in towards the pubis, plus a fainter parallel one below
        rh = reg('hipfold_' + sn, 'body', 0.20)
        rr[rh] = 0.03
        for i, dy in enumerate((0.0, -0.012)):
            pts = []
            for k in range(11):
                u = k / 10.0
                x = sx * (0.105 - 0.075 * u)
                y = 0.905 - 0.075 * u + dy - 0.008 * math.sin(math.pi * u)
                h = surf.ray(np.array([x, y, 0.4]), np.array([0, 0, -1.0]))
                if h is not None:
                    pts.append(h)
            if len(pts) > 5:
                out.append(Crease(wobble(np.array(pts), lambda q: np.tile([0, 0, 1.0], (len(q), 1)), 0.0008, 260 + i),
                                  rh, 0.0009 - 0.0003 * i, 0.0018, seed=260 + i + (10 if side == 'R' else 0)))
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
    # front of the neck: two soft horizontal bands when she looks down / tucks her chin. Cast from inside the neck outwards (from the
    # front they would hit her chin first).
    rk = reg('neck', 'neck', 0.03)
    rr[rk] = 0.02
    for i, y in enumerate((1.392, 1.409)):
        ctr = np.array([0, y, -0.045])
        pts = []
        for k in range(13):
            a = math.radians(-45 + 90 * k / 12.0)
            h = surf.ray(ctr, unit([math.sin(a), 0, math.cos(a)]), near=0.15)
            if h is not None and np.linalg.norm(h - ctr) < 0.08:
                pts.append(h)
        if len(pts) > 5:
            out.append(Crease(wobble(np.array(pts), lambda q: unit_rows(q - ctr), 0.0006, 70 + i),
                              rk, 0.0006 - 0.0002 * i, 0.0016, seed=70 + i, ridge=0.45))
    # belly: when she squats or leans forward the belly folds into one soft roll that follows its curve (lowest at the middle, rising
    # to the sides), with a fainter one above; broad and shallow with a raised rounded edge. 0.9.1's two thin straight lines read as a
    # scar (headset, 0.9.1).
    rbel = reg('belly', 'body', 0.20)
    rr[rbel] = 0.04
    for i, (y0, dep, wid) in enumerate(((0.948, 0.0013, 0.0065), (0.972, 0.0005, 0.005))):
        pts = []
        for k in range(17):
            x = -0.095 + 0.19 * k / 16.0
            h = surf.ray(np.array([x, y0 + 0.018 * (x / 0.095) ** 2, 0.4]), np.array([0, 0, -1.0]))
            if h is not None:
                pts.append(h)
        if len(pts) > 6:
            out.append(Crease(wobble(np.array(pts), lambda q: np.tile([0, 0, 1.0], (len(q), 1)), 0.0008, 300 + i),
                              rbel, dep, wid, seed=300 + i, ridge=0.6))
    log('body: %d creases in %d regions' % (len(out), len(regions)))
    excl = []
    for side in ('Left', 'Right'):
        c = P(side + '_LowerBreast')
        r = max(np.linalg.norm(P(side + '_Breast_' + k) - c) for k in ('out', 'up', 'down', 'in')) + 0.03
        excl.append((c, r))
    return out, regions, rr, excl


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
        ('body', ['reika_upperBody_normal', 'reika_lowerBody_normal', 'reika_neck', 'reika_foot'], 'reika_body', body_creases, 2048, 1024),
    ]
    for name, mnames, mat, fn, n, nreg in jobs:
        pr = parts(mnames, mat)
        if not pr:
            log('%s: none of %s in the export; skipped' % (name, mnames))
            continue
        surf = Surf(pr)
        creases, regions, rad, excl = fn(surf, sk, log)
        rad = {k: rad.get(k, 0.01) for k in range(len(regions))}
        det, reg, cov, cav, idx, best, wbest = build(name, surf, creases, regions, n, nreg, rad, log, excl)
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
    with open(os.path.join(OUT, 'wrinkle_layouts.txt'), 'w', newline='\n') as f:
        f.write('\n'.join(table) + '\n')
    with open(os.path.join(HERE, 'author.log'), 'w', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')


if __name__ == '__main__':
    main()
