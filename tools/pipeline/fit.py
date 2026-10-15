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


def nonrigid_icp(x0, faces, normals_fn, surfaces, lab, log, stiff=None, nodata=None,
                 alphas=(40, 20, 10, 5, 2.5, 1.2, 0.6, 0.3, 0.15), inner=3,
                 dmax=(0.15, 0.10, 0.07, 0.05, 0.035, 0.025, 0.018, 0.013, 0.01), min_dot=0.5):
    """x0: (n,3) start positions; surfaces[label] = Surface of allowed VaM triangles; lab: (n,) label per vertex.
    Minimises sum w|x - c|^2 + alpha |L (x - x0)|^2, re-finding closest points c each pass.
    min_dot: smallest normal agreement (vertex normal . surface normal) for a match, a scalar or one value per vertex
    (looser on the face, where features such as the nose and the lids are steep). The along-normal test applies
    only beyond 4 mm."""
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
                ok = (d < dm) & (dot > md) & ((along > 0.8) | (d < 0.004))
                c[sel] = q; dist[sel] = d
                w[sel] = np.where(ok, 1.0, 0.0)
            if nodata is not None:
                w[nodata] = 0
            A = (alpha * LtL + sp.diags(w)).tocsc()
            solve = spla.factorized(A)
            rhs0 = alpha * (LtL @ x0)
            xn = np.empty_like(x)
            for k in range(3):
                xn[:, k] = solve(rhs0[:, k] + w * c[:, k])
            step = np.linalg.norm(xn - x, axis=1).max()
            x = xn
        ok = w > 0
        log('  icp alpha %-5g dmax %.3f: %5.1f%% matched, residual mean %.2f mm, p95 %.2f mm, last step %.2f mm' % (
            alpha, dm, 100 * ok.mean(), 1000 * dist[ok].mean() if ok.any() else 0,
            1000 * np.percentile(dist[ok], 95) if ok.any() else 0, 1000 * step))
    return x, w, dist


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


def untangle(v, faces, d, iters=300, rings=3):
    """Relax offsets around flipped triangles (normal reversed between v and v + d) toward their neighbours'
    average until none are left; returns (d, flipped before, flipped after)."""
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


def surface_attach(v, skin_v, skin_f, skin_d, sign=1.0):
    """Overlays lying on the skin (areola disc, nails, tattoo/hair decals): each vertex keeps its spot on the
    skin (closest triangle, barycentric) and its height above it along the triangle normal."""
    srf = Surface(skin_v, skin_f, np.zeros((len(skin_f), 3)))
    q, dist, fi = srf.nearest(v, k=12)
    tri = skin_f[fi]
    a, b, c = skin_v[tri[:, 0]], skin_v[tri[:, 1]], skin_v[tri[:, 2]]
    v0, v1, v2 = b - a, c - a, q - a
    d00 = np.einsum('ij,ij->i', v0, v0); d01 = np.einsum('ij,ij->i', v0, v1); d11 = np.einsum('ij,ij->i', v1, v1)
    d20 = np.einsum('ij,ij->i', v2, v0); d21 = np.einsum('ij,ij->i', v2, v1)
    den = np.maximum(d00 * d11 - d01 * d01, 1e-30)
    bv = (d11 * d20 - d01 * d21) / den; bw = (d00 * d21 - d01 * d20) / den; bu = 1 - bv - bw
    def frame(p):
        A, B, C = p[tri[:, 0]], p[tri[:, 1]], p[tri[:, 2]]
        n = np.cross(B - A, C - A) * sign
        n /= np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-20)
        return A * bu[:, None] + B * bv[:, None] + C * bw[:, None], n
    p0, n0 = frame(skin_v)
    p1, n1 = frame(skin_v + skin_d)
    h = np.einsum('ij,ij->i', v - p0, n0)
    tang = (v - p0) - h[:, None] * n0
    return p1 + tang + h[:, None] * n1 - v
