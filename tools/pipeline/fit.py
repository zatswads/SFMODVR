"""Landmark warp + non-rigid ICP of her (posed) skin onto the VaM surface. numpy/scipy only."""
import numpy as np
import scipy.sparse as sp
import scipy.sparse.linalg as spla
from scipy.spatial import cKDTree
from scipy.interpolate import RBFInterpolator


def weld(points, tol=2e-5):
    """Map coincident points (split normals/UV seams, mesh borders) to one index. Returns (index per point, n)."""
    pairs = cKDTree(points).query_pairs(tol, output_type='ndarray')
    n = len(points)
    if len(pairs) == 0:
        return np.arange(n), n
    g = sp.coo_matrix((np.ones(len(pairs)), (pairs[:, 0], pairs[:, 1])), shape=(n, n))
    k, lab = sp.csgraph.connected_components(g, directed=False)
    return lab, k


def uniform_laplacian(n, faces):
    e = np.concatenate([faces[:, [0, 1]], faces[:, [1, 2]], faces[:, [2, 0]]])
    e = e[e[:, 0] != e[:, 1]]
    A = sp.coo_matrix((np.ones(len(e) * 2), (np.r_[e[:, 0], e[:, 1]], np.r_[e[:, 1], e[:, 0]])), shape=(n, n)).tocsr()
    A.data[:] = 1
    deg = np.asarray(A.sum(1)).ravel()
    deg[deg == 0] = 1
    return sp.eye(n) - sp.diags(1 / deg) @ A


def tps_warp(points, src, dst, smoothing=1e-5):
    if len(src) < 4:
        return points.copy()
    f = RBFInterpolator(src, dst - src, kernel='thin_plate_spline', smoothing=smoothing)
    out = points.copy()
    for i in range(0, len(points), 50000):
        out[i:i + 50000] += f(points[i:i + 50000])
    return out


def closest_on_triangles(p, a, b, c):
    """Closest point to p on triangles (a,b,c), all (n,3). Ericson, Real-Time Collision Detection 5.1.5."""
    ab = b - a; ac = c - a; ap = p - a
    d1 = np.einsum('ij,ij->i', ab, ap); d2 = np.einsum('ij,ij->i', ac, ap)
    bp = p - b
    d3 = np.einsum('ij,ij->i', ab, bp); d4 = np.einsum('ij,ij->i', ac, bp)
    cp = p - c
    d5 = np.einsum('ij,ij->i', ab, cp); d6 = np.einsum('ij,ij->i', ac, cp)
    va = d3 * d6 - d5 * d4; vb = d5 * d2 - d1 * d6; vc = d1 * d4 - d3 * d2
    den = va + vb + vc
    den[np.abs(den) < 1e-30] = 1e-30
    v = vb / den; w = vc / den
    res = a + ab * v[:, None] + ac * w[:, None]
    # edge/vertex regions (overwrite in reverse priority)
    m = (va <= 0) & ((d4 - d3) >= 0) & ((d5 - d6) >= 0)   # BC
    t = (d4 - d3) / np.maximum((d4 - d3) + (d5 - d6), 1e-30)
    res[m] = (b + (c - b) * t[:, None])[m]
    m = (vb <= 0) & (d2 >= 0) & (d6 <= 0)                 # AC
    t = d2 / np.maximum(d2 - d6, 1e-30)
    res[m] = (a + ac * t[:, None])[m]
    m = (vc <= 0) & (d1 >= 0) & (d3 <= 0)                 # AB
    t = d1 / np.maximum(d1 - d3, 1e-30)
    res[m] = (a + ab * t[:, None])[m]
    m = (d6 >= 0) & (d5 <= d6); res[m] = c[m]
    m = (d3 >= 0) & (d4 <= d3); res[m] = b[m]
    m = (d1 <= 0) & (d2 <= 0); res[m] = a[m]
    return res


class Surface:
    """Triangle soup with nearest-point queries (candidate triangles from nearest centroids)."""

    def __init__(self, v, f, fn):
        self.v, self.f, self.fn = v, f, fn
        self.cent = v[f].mean(1)
        self.tree = cKDTree(self.cent)

    def nearest(self, p, k=8):
        k = min(k, len(self.f))
        _, idx = self.tree.query(p, k=k)
        idx = idx.reshape(len(p), k)
        best = np.full(len(p), np.inf); bp = np.zeros_like(p); bf = np.zeros(len(p), np.int64)
        for j in range(k):
            fi = idx[:, j]
            tri = self.f[fi]
            q = closest_on_triangles(p, self.v[tri[:, 0]], self.v[tri[:, 1]], self.v[tri[:, 2]])
            d = np.linalg.norm(q - p, axis=1)
            m = d < best
            best[m] = d[m]; bp[m] = q[m]; bf[m] = fi[m]
        return bp, best, bf


def _zheight(srf, p, k=16):
    """Surface height at the (x, y) of points p: z of the front-most triangle of srf that covers it (triangles seen from
    +z, barycentric in xy). Returns (z, found)."""
    k = min(k, len(srf.f))
    _, idx = srf.tree.query(p, k=k)
    idx = idx.reshape(len(p), k)
    zbest = np.full(len(p), -np.inf)
    for j in range(k):
        t = srf.f[idx[:, j]]
        a, b, c = srf.v[t[:, 0]], srf.v[t[:, 1]], srf.v[t[:, 2]]
        den = (b[:, 1] - c[:, 1]) * (a[:, 0] - c[:, 0]) + (c[:, 0] - b[:, 0]) * (a[:, 1] - c[:, 1])
        den = np.where(np.abs(den) < 1e-14, 1e-14, den)
        u = ((b[:, 1] - c[:, 1]) * (p[:, 0] - c[:, 0]) + (c[:, 0] - b[:, 0]) * (p[:, 1] - c[:, 1])) / den
        v = ((c[:, 1] - a[:, 1]) * (p[:, 0] - c[:, 0]) + (a[:, 0] - c[:, 0]) * (p[:, 1] - c[:, 1])) / den
        w = 1 - u - v
        inside = (u >= -1e-6) & (v >= -1e-6) & (w >= -1e-6)
        z = u * a[:, 2] + v * b[:, 2] + w * c[:, 2]
        better = inside & (z > zbest)
        zbest[better] = z[better]
    return zbest, np.isfinite(zbest)


def nonrigid_icp(x0, faces, normals_fn, surfaces, lab, log, stiff=None, nodata=None,
                 alphas=(40, 20, 10, 5, 2.5, 1.2, 0.6, 0.3, 0.15), inner=3,
                 dmax=(0.15, 0.10, 0.07, 0.05, 0.035, 0.025, 0.018, 0.013, 0.01), min_dot=0.5,
                 no_side=None, pin=None, pin_pos=None, pin_w=1e4):
    """x0: (n,3) start positions; surfaces[label] = Surface of allowed VaM triangles; lab: (n,) label per vertex.
    Minimises sum w|x - c|^2 + alpha |L (x - x0)|^2, re-finding closest points c each pass.
    min_dot: smallest normal agreement (vertex normal . surface normal) for a match, a scalar or one value per vertex
    (looser on the face, where features such as the nose and the lids are steep). The along-normal test applies
    only beyond 4 mm (no_side: per-vertex bool, True = no such exemption, a match must lie along the normal at any
    distance: the breast's underside, where a sideways match is how a fold latches on).
    pin, pin_pos: vertices held at pin_pos[pin] with data weight pin_w (e.g. a rim that must stay on the fitted skin)."""
    n = len(x0)
    L = uniform_laplacian(n, faces)
    LtL = (L.T @ (L if stiff is None else sp.diags(stiff) @ L)).tocsc()
    x = x0.copy()
    for stage, alpha in enumerate(alphas):
        dm = dmax[min(stage, len(dmax) - 1)]
        for it in range(inner):
            nrm = normals_fn(x)
            c = np.zeros_like(x); w = np.zeros(n); dist = np.full(n, np.inf)
            for li, srf in surfaces.items():
                sel = np.where(lab == li)[0]
                if len(sel) == 0 or srf is None:
                    continue
                q, d, fi = srf.nearest(x[sel])
                dot = np.einsum('ij,ij->i', nrm[sel], srf.fn[fi])
                # the match must lie along the vertex normal (in front or behind), not off to the side: sideways
                # matches are how inner lips and folds latch onto the wrong surface and crumple the face
                off = q - x[sel]
                along = np.abs(np.einsum('ij,ij->i', off, nrm[sel])) / np.maximum(d, 1e-9)
                md = min_dot[sel] if np.ndim(min_dot) else min_dot
                near = (d < 0.004) if no_side is None else ((d < 0.004) & ~no_side[sel])
                ok = (d < dm) & (dot > md) & ((along > 0.8) | near)
                c[sel] = q; dist[sel] = d
                w[sel] = np.where(ok, 1.0, 0.0)
            if nodata is not None:
                w[nodata] = 0
            matched = w > 0
            w_match = w.copy()   # (returned: pinned vertices are held, not matched to the surface)
            if pin is not None:
                w[pin] = pin_w; c[pin] = pin_pos[pin]
            A = (alpha * LtL + sp.diags(w)).tocsc()
            solve = spla.factorized(A)
            rhs0 = alpha * (LtL @ x0)
            xn = np.empty_like(x)
            for k in range(3):
                xn[:, k] = solve(rhs0[:, k] + w * c[:, k])
            step = np.linalg.norm(xn - x, axis=1).max()
            x = xn
        ok = matched
        log('  icp alpha %-5g dmax %.3f: %5.1f%% matched, residual mean %.2f mm, p95 %.2f mm, last step %.2f mm' % (
            alpha, dm, 100 * ok.mean(), 1000 * dist[ok].mean() if ok.any() else 0,
            1000 * np.percentile(dist[ok], 95) if ok.any() else 0, 1000 * step))
    return x, w_match, dist


def idw(src_pts, src_val, pts, k=8, exact=2e-5):
    tree = cKDTree(src_pts)
    k = min(k, len(src_pts))
    d, i = tree.query(pts, k=k)
    d = d.reshape(len(pts), k); i = i.reshape(len(pts), k)
    w = 1.0 / (d + 1e-4) ** 2
    w[d[:, 0] < exact] = 0
    w[d[:, 0] < exact, 0] = 1
    w /= w.sum(1, keepdims=True)
    return np.einsum('nk,nkj->nj', w, src_val[i])


def shell_average(src_pts, src_val, p, kmin=48, ratio=1.6):
    """Average of values on the surface shell around an interior point p (for joint offsets): all points
    within ratio x the nearest distance (at least kmin), weighted 1/d^2."""
    tree = cKDTree(src_pts)
    d0, _ = tree.query(p)
    idx = tree.query_ball_point(p, max(d0 * ratio, 1e-3))
    if len(idx) < kmin:
        _, idx = tree.query(p, k=min(kmin, len(src_pts)))
        idx = np.atleast_1d(idx)
    idx = np.asarray(idx)
    d = np.linalg.norm(src_pts[idx] - p, axis=1)
    w = 1 / (d + 1e-3) ** 2
    return (src_val[idx] * w[:, None]).sum(0) / w.sum()


def rotate_normals(n0, a, b):
    """Rotate n0 by the rotation taking unit a to unit b (per row)."""
    v = np.cross(a, b); c = np.einsum('ij,ij->i', a, b)
    s = np.linalg.norm(v, axis=1)
    k = np.zeros_like(v); m = s > 1e-9
    k[m] = v[m] / s[m, None]
    kxn = np.cross(k, n0); kdn = np.einsum('ij,ij->i', k, n0)
    out = n0 * c[:, None] + kxn * s[:, None] + k * kdn[:, None] * (1 - c)[:, None]
    l = np.linalg.norm(out, axis=1, keepdims=True)
    return out / np.maximum(l, 1e-12)


def smooth_follow(v, faces, d0, pinned, soft=0.02):
    """Displacement for a mesh that follows the fitted skin: as smooth as possible over its own topology
    (no folds in dense parts such as the mouth interior or lashes), exact where it touches the skin (pinned),
    softly tied to the nearest-skin values d0 everywhere else."""
    n = len(v)
    if len(faces) == 0:
        return d0
    L = uniform_laplacian(n, faces)
    w = np.where(pinned, 1e4, soft)
    A = (L.T @ L + sp.diags(w)).tocsc()
    solve = spla.factorized(A)
    out = np.empty_like(d0)
    for k in range(3):
        out[:, k] = solve(w * d0[:, k])
    return out


def components(n, faces):
    if len(faces) == 0:
        return np.arange(n)
    e = np.concatenate([faces[:, [0, 1]], faces[:, [1, 2]]])
    g = sp.coo_matrix((np.ones(len(e)), (e[:, 0], e[:, 1])), shape=(n, n))
    return sp.csgraph.connected_components(g, directed=False)[1]


def patch_rigid(v, faces, skin_pts, skin_d, k=12):
    """Overlay pieces (nipples, nails, lash strands): each connected piece moves rigidly with the skin patch
    under it (Kabsch of the nearest skin points, bind -> morphed), so it keeps its own shape."""
    comp = components(len(v), faces)
    tree = cKDTree(skin_pts)
    out = np.zeros_like(v)
    order = np.argsort(comp)
    bounds = np.flatnonzero(np.diff(comp[order])) + 1
    for idx in np.split(order, bounds):
        _, nb = tree.query(v[idx], k=k)
        nb = np.unique(nb)
        a = skin_pts[nb]; b = a + skin_d[nb]
        ca, cb = a.mean(0), b.mean(0)
        H = (a - ca).T @ (b - cb)
        U, S, Vt = np.linalg.svd(H)
        D = np.diag([1, 1, np.sign(np.linalg.det(Vt.T @ U.T))])
        R = Vt.T @ D @ U.T
        out[idx] = (v[idx] - ca) @ R.T + cb - v[idx]
    return out


def untangle(v, faces, d, iters=300, rings=3, fixed=None):
    """Relax offsets around flipped triangles (normal reversed between v and v + d) toward their neighbours'
    average until none are left; returns (d, flipped before, flipped after). fixed: vertices that keep their offsets."""
    if len(faces) == 0:
        return d, 0, 0
    n = len(v)
    e = np.concatenate([faces[:, [0, 1]], faces[:, [1, 2]], faces[:, [2, 0]]])
    A = sp.coo_matrix((np.ones(len(e) * 2), (np.r_[e[:, 0], e[:, 1]], np.r_[e[:, 1], e[:, 0]])), shape=(n, n)).tocsr()
    A.data[:] = 1
    deg = np.maximum(np.asarray(A.sum(1)).ravel(), 1)
    def bad(dd):
        a = np.cross(v[faces[:, 1]] - v[faces[:, 0]], v[faces[:, 2]] - v[faces[:, 0]])
        w = v + dd
        b = np.cross(w[faces[:, 1]] - w[faces[:, 0]], w[faces[:, 2]] - w[faces[:, 0]])
        return (np.einsum('ij,ij->i', a, b) < 0) & (np.linalg.norm(a, axis=1) > 1e-14)
    d = d.copy()
    first = int(bad(d).sum())
    for _ in range(iters):
        bf = bad(d)
        if not bf.any():
            break
        m = np.zeros(n, bool); m[faces[bf].ravel()] = True
        for _ in range(rings):
            m = m | (A @ m.astype(float) > 0)
        if fixed is not None:
            m &= ~fixed
        avg = (A @ d) / deg[:, None]
        d[m] = 0.5 * d[m] + 0.5 * avg[m]
    return d, first, int(bad(d).sum())


def relax_project(x, faces, x_rest, surfaces, lab, normals_fn, match, movable, iters=25, step=0.4, log=None):
    """Spring relaxation in the tangent plane (rest lengths = her own edge lengths before the fit), then the
    matched vertices are put back onto the VaM surface. Spreads out vertices that the closest-point fit bunched
    into ridges (her larger breast's underside squeezed into a smaller one) without leaving the surface."""
    e = np.concatenate([faces[:, [0, 1]], faces[:, [1, 2]], faces[:, [2, 0]]])
    e = np.unique(np.sort(e, axis=1), axis=0)
    r0 = np.linalg.norm(x_rest[e[:, 0]] - x_rest[e[:, 1]], axis=1)
    cur = np.linalg.norm(x[e[:, 0]] - x[e[:, 1]], axis=1)
    r0 *= np.median(cur / np.maximum(r0, 1e-9))   # overall size change is not strain
    n = len(x)
    cnt = np.bincount(e.ravel(), minlength=n).astype(float)
    x = x.copy()
    for it in range(iters):
        v = x[e[:, 1]] - x[e[:, 0]]
        l = np.linalg.norm(v, axis=1)
        f = ((l - r0) / np.maximum(l, 1e-9))[:, None] * v
        F = np.zeros_like(x)
        np.add.at(F, e[:, 0], f); np.add.at(F, e[:, 1], -f)
        F /= np.maximum(cnt, 1)[:, None]
        nrm = normals_fn(x)
        F -= np.einsum('ij,ij->i', F, nrm)[:, None] * nrm
        x[movable] += step * F[movable]
        for li, srf in surfaces.items():
            sel = np.where((lab == li) & match & movable)[0]
            if len(sel) and srf is not None:
                q, d, fi = srf.nearest(x[sel])
                ok = d < 0.01
                x[sel[ok]] = q[ok]
    if log:
        c2 = np.linalg.norm(x[e[:, 0]] - x[e[:, 1]], axis=1) / np.maximum(r0, 1e-9)
        log('  relax: edge strain p1/p99 %.2f/%.2f (before %.2f/%.2f)' % (
            np.percentile(c2, 1), np.percentile(c2, 99), np.percentile(cur / r0, 1), np.percentile(cur / r0, 99)))
    return x


def _bary(skin_v, skin_f, p, fi):
    """Barycentric coordinates (bu, bv, bw) of points p (on or near triangles fi)."""
    tri = skin_f[fi]
    a, b, c = skin_v[tri[:, 0]], skin_v[tri[:, 1]], skin_v[tri[:, 2]]
    v0, v1, v2 = b - a, c - a, p - a
    d00 = np.einsum('ij,ij->i', v0, v0); d01 = np.einsum('ij,ij->i', v0, v1); d11 = np.einsum('ij,ij->i', v1, v1)
    d20 = np.einsum('ij,ij->i', v2, v0); d21 = np.einsum('ij,ij->i', v2, v1)
    den = np.maximum(d00 * d11 - d01 * d01, 1e-30)
    bv = (d11 * d20 - d01 * d21) / den; bw = (d00 * d21 - d01 * d20) / den
    return 1 - bv - bw, bv, bw


def surface_attach(v, skin_v, skin_f, skin_d, sign=1.0, vn0=None, vn1=None):
    """Overlays lying on the skin (nails, tattoo/hair decals): each vertex keeps its spot on the skin (closest triangle,
    barycentric) and its height above it, along the skin normal there.
    vn0, vn1: the skin's vertex normals before and after the fit (one per skin vertex, already facing outwards): the
    frame uses their barycentric interpolation, so a bumpy or folded face-normal field cannot tear the overlay. Without
    them the triangle face normal (times sign) is used."""
    srf = Surface(skin_v, skin_f, np.zeros((len(skin_f), 3)))
    q, dist, fi = srf.nearest(v, k=12)
    tri = skin_f[fi]
    bu, bv, bw = _bary(skin_v, skin_f, q, fi)
    use_vn = vn0 is not None and vn1 is not None

    def frame(p, vn):
        A, B, C = p[tri[:, 0]], p[tri[:, 1]], p[tri[:, 2]]
        if use_vn:
            n = vn[tri[:, 0]] * bu[:, None] + vn[tri[:, 1]] * bv[:, None] + vn[tri[:, 2]] * bw[:, None]
        else:
            n = np.cross(B - A, C - A) * sign
        n = n / np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-20)
        return A * bu[:, None] + B * bv[:, None] + C * bw[:, None], n
    p0, n0 = frame(skin_v, vn0)
    p1, n1 = frame(skin_v + skin_d, vn1)
    h = np.einsum('ij,ij->i', v - p0, n0)
    tang = (v - p0) - h[:, None] * n0
    return p1 + tang + h[:, None] * n1 - v


def _vnormals(v, f):
    n = np.cross(v[f[:, 1]] - v[f[:, 0]], v[f[:, 2]] - v[f[:, 0]])
    out = np.zeros_like(v)
    for k in range(3):
        np.add.at(out, f[:, k], n)
    return out / np.maximum(np.linalg.norm(out, axis=1, keepdims=True), 1e-20)


def cap_attach(v, faces, skin_v, skin_d, target, scale=0.008, ramp=0.008, relief=1.0, reach=0.04, log=None):
    """Offsets for a cap that fills a hole in the skin (her nipple overlay: areola, nipple and the breast tip; the skin
    mesh has no vertices under it). Nearest-skin attachment cannot place such a cap: its vertices are up to 3 cm from the
    rim and every rim triangle gives a different frame (the overlay tears). Instead:
      1. the cap's smooth shape S (implicit cotangent smoothing at the given scale, border fixed) is moved by the harmonic
         interpolation of the fitted skin's offsets at the cap's border (the border vertices sit on skin vertices);
      2. S is put onto the target surface (VaM's own skin there: its height at the same x, y, within reach), blended in
         over ramp metres from the border;
      3. her relief (the cap minus S, along her normals: the nipple's bump) is added back along the new normals.
    skin_v, skin_d: fitted skin vertices and offsets (bind); target: fit.Surface of the VaM skin."""
    # work on the welded cap (UV seams split vertices; they must move together)
    widx, nw = weld(v)
    cnt = np.bincount(widx, minlength=nw).astype(float)
    vw = np.zeros((nw, 3)); np.add.at(vw, widx, v); vw /= cnt[:, None]
    fw = widx[faces]
    fw = fw[(fw[:, 0] != fw[:, 1]) & (fw[:, 1] != fw[:, 2]) & (fw[:, 2] != fw[:, 0])]
    d = _cap_attach(vw, fw, skin_v, skin_d, target, scale, ramp, relief, reach, log)[0]
    return d[widx] + (vw[widx] - v)


def _cot_laplacian(v, f):
    """Cotangent Laplacian (positive semi-definite, weights clamped at 1e-3 so slivers stay harmless) and the lumped
    vertex areas."""
    n = len(v)
    I, J, W = [], [], []
    area = np.zeros(n)
    for k in range(3):
        i, j, o = f[:, k], f[:, (k + 1) % 3], f[:, (k + 2) % 3]
        a, b = v[i] - v[o], v[j] - v[o]
        cr = np.linalg.norm(np.cross(a, b), axis=1)
        cot = np.einsum('ij,ij->i', a, b) / np.maximum(cr, 1e-18)
        w = 0.5 * np.maximum(cot, 1e-3)
        I += [i, j]; J += [j, i]; W += [w, w]
        if k == 0:
            tri = 0.5 * cr
            for c in range(3):
                np.add.at(area, f[:, c], tri / 3)
    I = np.concatenate(I); J = np.concatenate(J); W = np.concatenate(W)
    Wm = sp.coo_matrix((W, (I, J)), shape=(n, n)).tocsr()
    return (sp.diags(np.asarray(Wm.sum(1)).ravel()) - Wm).tocsr(), np.maximum(area, 1e-12)


def _cap_attach(v, faces, skin_v, skin_d, target, scale, ramp, relief, reach, log):
    n = len(v)
    # border: edges that belong to one triangle
    e = np.sort(np.concatenate([faces[:, [0, 1]], faces[:, [1, 2]], faces[:, [2, 0]]]), axis=1)
    ue, cnt = np.unique(e, axis=0, return_counts=True)
    border = np.zeros(n, bool); border[ue[cnt == 1].ravel()] = True
    Lc, Ma = _cot_laplacian(v, faces)
    Bi = np.flatnonzero(border); Ii = np.flatnonzero(~border)
    S = v.copy()
    if len(Ii):
        # implicit smoothing of the cap, border fixed: (M + t Lc) S = M v, t = scale^2 (scale: the size of the bumps that
        # count as relief)
        A = (sp.diags(Ma[Ii]) + scale ** 2 * Lc[Ii][:, Ii]).tocsc()
        rhs = Ma[Ii, None] * v[Ii] - scale ** 2 * (Lc[Ii][:, Bi] @ v[Bi])
        solve = spla.factorized(A)
        for k in range(3):
            S[Ii, k] = solve(rhs[:, k])
    # 1. displacement of the cap: harmonic (inverse edge length weights) between the fitted skin's offsets at its
    # border (the border vertices sit on skin vertices), so it cannot wiggle or overshoot the rim's own strain
    skin_tree = cKDTree(skin_v)
    Lap = Lc
    Bi = np.flatnonzero(border); Ii = np.flatnonzero(~border)
    D = np.zeros((n, 3))
    if len(Bi):
        dB = skin_d[skin_tree.query(v[Bi])[1]]
        D[Bi] = dB
        if len(Ii):
            A = (Lap[Ii][:, Ii] + 1e-9 * sp.eye(len(Ii))).tocsc()
            rhs = -(Lap[Ii][:, Bi] @ dB)
            solve = spla.factorized(A)
            for k in range(3):
                D[Ii, k] = solve(rhs[:, k])
    s1 = S + D
    # 2. onto the target surface (its height at the cap's own x, y: the cap faces forward), from the border inwards
    zt, found = _zheight(target, s1)
    dist = np.abs(zt - s1[:, 2])
    ok = found & (dist < reach)
    q = s1.copy(); q[ok, 2] = zt[ok]
    el = np.linalg.norm(v[faces[:, 0]] - v[faces[:, 1]], axis=1)
    g = sp.coo_matrix((np.r_[el, el], (np.r_[faces[:, 0], faces[:, 1]], np.r_[faces[:, 1], faces[:, 0]])), shape=(n, n)).tocsr()
    hop = sp.csgraph.dijkstra(g, directed=False, indices=np.flatnonzero(border), min_only=True) if border.any() else np.full(n, np.inf)
    w = np.clip(hop / ramp, 0, 1); w = w * w * (3 - 2 * w)
    w = np.where(ok, w, 0.0)
    base = s1 + (q - s1) * w[:, None]
    # 3. her relief along the normals
    b = np.einsum('ij,ij->i', v - S, _vnormals(v, faces))
    out = base + relief * b[:, None] * _vnormals(base, faces)
    if log:
        log('  cap: %d vertices, %d on the border, %d snapped to the VaM surface (mean move %.2f mm), relief %.2f..%.2f mm' % (
            n, border.sum(), (w > 0).sum(), 1000 * np.linalg.norm((q - s1)[w > 0], axis=1).mean() if (w > 0).any() else 0, 1000 * b.min(), 1000 * b.max()))
    return out - v, b, border


def cap_icp(v, faces, skin_v, skin_d, target, nip_srf, disc_srf, sign, relief=0.0, relief_min=0.001, scale=0.003, ramp=0.025,
            reach=0.04, alphas=(10, 5, 2.5, 1.2, 0.6, 0.3, 0.15), dmax=(0.02, 0.015, 0.01, 0.008, 0.006, 0.005, 0.004),
            min_dot=0.2, pin_w=1e4, relax=300, relax_step=0.8, log=None):
    """Nipple cap (her nipple overlay) fitted to VaM's skin by non-rigid ICP, starting from cap_attach's result.
    Her nipple vertices (more than relief_min above her own smooth disc, the v - S term of cap_attach) target VaM's
    'Nipples' faces (nip_srf) only, the areola/disc vertices VaM's Torso faces (disc_srf) only. The rim (the cap's
    border, on the fitted skin's hole boundary) is pinned to the fitted skin with data weight pin_w. Afterwards the
    cap is untangled with the rim excluded. sign: winding sign of the cap (cross(b-a, c-a) * sign points outwards).
    Returns offsets per cap vertex."""
    widx, nw = weld(v)
    cnt = np.bincount(widx, minlength=nw).astype(float)
    vw = np.zeros((nw, 3)); np.add.at(vw, widx, v); vw /= cnt[:, None]
    fw = widx[faces]
    fw = fw[(fw[:, 0] != fw[:, 1]) & (fw[:, 1] != fw[:, 2]) & (fw[:, 2] != fw[:, 0])]
    d0, b, border = _cap_attach(vw, fw, skin_v, skin_d, target, scale, ramp, relief, reach, log)
    x0 = vw + d0
    # rim: border vertices on the fitted skin; their fitted positions
    skin_tree = cKDTree(skin_v)
    ds, si = skin_tree.query(vw)
    rim = border & (ds < 2e-5)
    pin_pos = vw + skin_d[si]
    lab = np.where(b > relief_min, 0, 1)
    lab[rim] = 1
    log_ = log or (lambda s: None)
    log_('  cap ICP: %d nipple vertices (relief > %.1f mm) -> VaM Nipples, %d disc vertices -> VaM Torso, %d rim vertices pinned (of %d border)' % (
        (lab == 0).sum() - 0, 1000 * relief_min, (lab == 1).sum(), rim.sum(), border.sum()))
    nrm = lambda x: _vnormals(x, fw) * sign
    x, w, dist = nonrigid_icp(x0, fw, nrm, {0: nip_srf, 1: disc_srf}, lab, log_, nodata=None, alphas=alphas, dmax=dmax,
                              min_dot=min_dot, pin=rim, pin_pos=pin_pos, pin_w=pin_w)
    if relax:
        # even out the triangles (rest lengths: her own edge lengths) in the tangent plane, back onto the VaM faces
        pos = relax_project(x, fw, vw, {0: nip_srf, 1: disc_srf}, lab, nrm, np.ones(len(x), bool), ~rim, iters=relax, step=relax_step, log=log)
        x = pos
    d = x - vw
    d, b0, b1 = untangle(vw, fw, d, iters=300, rings=3, fixed=rim)
    log_('  cap untangle (rim fixed): %d flipped triangles -> %d' % (b0, b1))
    return d[widx] + (vw[widx] - v)


def bump_tip(v, faces, scale=0.005):
    """Index (into v) of the tip of the bump on a small patch that faces +z (her nipple cap on one side, VaM's 'Nipples'
    faces on one side): the vertex that rises most above the patch's own smooth shape (implicit cotangent smoothing at the
    given scale, border fixed), along the outward normal. The nipple, unlike the patch's front-most vertex (the breast's
    apex, which is not the nipple on a round breast)."""
    used = np.unique(faces)
    remap = -np.ones(len(v), np.int64); remap[used] = np.arange(len(used))
    vp = v[used]
    w, nw = weld(vp)
    cnt = np.bincount(w, minlength=nw).astype(float)
    vw = np.zeros((nw, 3)); np.add.at(vw, w, vp); vw /= cnt[:, None]
    fw = w[remap[faces]]
    fw = fw[(fw[:, 0] != fw[:, 1]) & (fw[:, 1] != fw[:, 2]) & (fw[:, 2] != fw[:, 0])]
    e = np.sort(np.concatenate([fw[:, [0, 1]], fw[:, [1, 2]], fw[:, [2, 0]]]), axis=1)
    ue, c = np.unique(e, axis=0, return_counts=True)
    border = np.zeros(nw, bool); border[ue[c == 1].ravel()] = True
    Lc, Ma = _cot_laplacian(vw, fw)
    Ii = np.flatnonzero(~border); Bi = np.flatnonzero(border)
    S = vw.copy()
    if len(Ii):
        A = (sp.diags(Ma[Ii]) + scale ** 2 * Lc[Ii][:, Ii]).tocsc()
        rhs = Ma[Ii, None] * vw[Ii] - scale ** 2 * (Lc[Ii][:, Bi] @ vw[Bi])
        solve = spla.factorized(A)
        for k in range(3):
            S[Ii, k] = solve(rhs[:, k])
    n = _vnormals(vw, fw)
    if n[:, 2].sum() < 0:
        n = -n
    b = np.einsum('ij,ij->i', vw - S, n)
    i = int(np.argmax(b))
    # back to an index of v: the member of the welded group
    return int(used[np.flatnonzero(w == i)[0]])
