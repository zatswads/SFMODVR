"""Material check: a converted character's skin textures against hers, before they go into the game.

    blender -b --factory-startup -P matcheck.py -- --out <morph dir> [--her <game export dir>]

Also run by blend.py right after the bake (run.ps1 -Check runs it alone on an existing character). Writes
_check/materials.txt (the numbers), _check/materials.png (contact sheet) and 'material check' rows in _check/summary.txt.

What it looks at, per baked material (tex_* lines of character.txt):
  albedo   linear luminance (median over skin texels) against her own albedo x _SkinColor: the game's skin shader takes the
           albedo as it is, so a VaM map at VaM's brightness comes out far too bright. With the character skin on, the
           plugin sets the tint white (the face-only variant keeps her tint, so that one is compared x tint). Also the
           share of clipped texels (a channel at 255).
  normal   the large-scale tilt (blurred map, angle from +Z): skin detail averages out flat (her own maps: p99 ~7 deg); a
           map that carries the shape of the body (fit residual, a different mesh) shades wrong on the morphed geometry.
  her layers  the game's skin shader draws her own layers on top of any albedo (_AttachMap_0..2 with _AttachColor_*,
           _BlushMap, _AddMap, _NormalMap_2): listed from her export's materials.txt, so it is known what stays hers.
Contact sheet: one row per material (body, each face, foot), columns: her albedo x tint | baked albedo | her normal |
baked normal | baked normal's large-scale tilt (black 0 deg -> red 15 -> yellow 30+).
"""
import os
import sys

import bpy
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bake  # noqa: E402  (shared helpers: srgb_to_lin, skin_mask, LUM, read_textures_txt, normal_layout)

N = 1024                 # analysis size
TILE = 320               # contact sheet tile
BLUR_PX = 12.0           # gaussian sigma at N for the large-scale tilt
ALBEDO_OK = (0.8, 1.25)  # luminance ratio baked / hers
TILT_P99_OK = 12.0       # deg
TILT_SHARE_OK = 0.02     # share of texels over 10 deg
CLIP_OK = 0.05           # share of skin texels with a channel at 255
LAYERS = ('_AttachMap_0', '_AttachMap_1', '_AttachMap_2', '_BlushMap', '_AddMap', '_NormalMap_2', '_SkinColorMaskMap', '_SmoothnessMap')


def log(s):
    print('[matcheck] ' + s, flush=True)


def load(path, n=N):
    """Stored values (0..1, no colour management) as float32 (n, n, 4), top row first."""
    im = bpy.data.images.load(path, check_existing=False)
    try:
        im.colorspace_settings.name = 'Non-Color'
        try:
            im.alpha_mode = 'CHANNEL_PACKED'
        except Exception:  # noqa: BLE001
            pass
        if tuple(im.size) != (n, n):
            im.scale(n, n)
        px = np.empty(n * n * 4, np.float32)
        im.pixels.foreach_get(px)
    finally:
        bpy.data.images.remove(im)
    return px.reshape(n, n, 4)[::-1].copy()


def blur(a, s):
    """Gaussian blur of a (n, n, c) array (scipy when it can be imported, else three box passes)."""
    try:
        try:
            from scipy.ndimage import gaussian_filter
        except ImportError:
            sys.path.append(os.path.join(os.environ.get('LOCALAPPDATA', ''), 'SfMorph', 'pydeps'))
            from scipy.ndimage import gaussian_filter
        return gaussian_filter(a, sigma=(s, s, 0), mode='nearest')
    except Exception:  # noqa: BLE001
        r = max(1, int(round(s * 0.9)))
        for _ in range(3):
            for ax in (0, 1):
                c = np.cumsum(np.pad(a, [(r + 1, r) if i == ax else (0, 0) for i in range(3)], mode='edge'), axis=ax)
                a = (np.take(c, range(2 * r + 1, c.shape[ax]), axis=ax) - np.take(c, range(0, c.shape[ax] - 2 * r - 1), axis=ax)) / (2 * r + 1)
        return a


def decode_normal(px):
    """(n, n, 4) stored values of any normal map layout -> unit XYZ."""
    lay = bake.normal_layout(px.reshape(-1, 4))
    if lay == 'dxt5nm':
        x = px[..., 3] * 2 - 1; y = px[..., 1] * 2 - 1
    else:
        x = px[..., 0] * 2 - 1; y = px[..., 1] * 2 - 1
    z = px[..., 2] * 2 - 1 if lay == 'rgb' else np.sqrt(np.clip(1 - x * x - y * y, 0, 1))
    n = np.stack([x, y, z], -1)
    return n / np.maximum(np.linalg.norm(n, axis=-1, keepdims=True), 1e-6), lay


def tilt(n):
    """Large-scale tilt in degrees per texel."""
    b = blur(n, BLUR_PX)
    b /= np.maximum(np.linalg.norm(b, axis=-1, keepdims=True), 1e-6)
    return np.degrees(np.arccos(np.clip(b[..., 2], -1, 1)))


def lum_median(lin):
    m = bake.skin_mask(lin.reshape(-1, 3))
    a = lin.reshape(-1, 3)[m]
    return (float(np.median(a @ bake.LUM)) if len(a) > 500 else float('nan')), m


def read_character(out):
    tex = {}
    p = os.path.join(out, 'character.txt')
    if os.path.isfile(p):
        with open(p, encoding='utf-8') as f:
            for l in f.read().splitlines():
                t = l.split('\t')
                if len(t) >= 4 and t[0] == 'texture':
                    tex[(t[1], t[2])] = os.path.join(out, t[3])
    return tex


def read_layers(her):
    """Her own shader layers per material from the game export's materials.txt: {mat: [text]}."""
    res = {}
    p = os.path.join(her, 'materials.txt') if her else ''
    if not p or not os.path.isfile(p):
        return res
    vals = {}
    with open(p, encoding='utf-8') as f:
        for l in f.read().splitlines():
            if l.startswith('#'):
                continue
            t = l.split('\t')
            if len(t) >= 5:
                vals[(t[0], t[2])] = t[4]
    for (mat, prop), v in vals.items():
        if prop in LAYERS and v != '-':
            extra = ''
            if prop.startswith('_AttachMap_'):
                k = prop[-1]
                extra = ' colour ' + vals.get((mat, '_AttachColor_' + k), '?') + ', fuzziness ' + vals.get((mat, '_AttachFuzziness_' + k), '?')
            res.setdefault(mat, []).append('%s = %s%s' % (prop, v.split(' ')[0], extra))
    return res


def heat(deg):
    t = np.clip(deg / 30.0, 0, 1)
    return np.stack([np.clip(t * 2, 0, 1), np.clip(t * 2 - 1, 0, 1), np.zeros_like(t)], -1)


def tile(a):
    """(N, N, 3) display values -> (TILE, TILE, 3) by block averaging."""
    k = N // TILE if N % TILE == 0 else None
    if k:
        return a.reshape(TILE, k, TILE, k, 3).mean((1, 3))
    idx = (np.arange(TILE) * N // TILE)
    return a[idx][:, idx]


def run(out, her, log=log, info=None):
    """Check the character in `out` against her export `her`. Returns summary rows; writes materials.txt / .png."""
    chk = os.path.join(out, '_check')
    os.makedirs(chk, exist_ok=True)
    tex = read_character(out)
    htex, htint = bake.read_textures_txt(her) if her else (None, None)
    htex = htex or {}; htint = htint or {}
    if info is None:
        info = {}
        p = os.path.join(chk, 'bake_info.txt')
        if os.path.isfile(p):
            with open(p, encoding='utf-8') as f:
                for l in f.read().splitlines():
                    if '\t' in l:
                        k, v = l.split('\t', 1); info[k] = v
    lines = ['# material check of %s against %s' % (out, her or '(no export of her: fixed reference values)')]
    for k, v in info.items():
        lines.append('bake: %s: %s' % (k, v))
    rows = []
    sheet = []
    alb_bad, nrm_bad, clip_bad = [], [], []
    # every baked material (body, each face variant, foot), body first
    mats = sorted({m for m, _ in tex}, key=lambda m: (m != 'reika_body', m))
    for mat in mats:
        tint = np.array(htint.get((mat, '_SkinColor'), (1.0, 1.0, 1.0)), np.float32)
        tlin = bake.srgb_to_lin(tint)
        her_alb = her_lin = None
        lh = bake.HER_LUM_FALLBACK
        if (mat, '_ColorMap') in htex and os.path.isfile(htex[(mat, '_ColorMap')]):
            her_lin = bake.srgb_to_lin(load(htex[(mat, '_ColorMap')])[..., :3]) * tlin
            lh = lum_median(her_lin)[0]
            her_alb = bake.lin_to_srgb(her_lin)
        row = [her_alb if her_alb is not None else np.zeros((N, N, 3), np.float32)]
        for kind in ('albedo', 'albedo_faceonly'):
            p = tex.get((mat, kind))
            if not p or not os.path.isfile(p):
                continue
            s = load(p)[..., :3]
            lin = bake.srgb_to_lin(s) * (tlin if kind.endswith('faceonly') else 1.0)
            lb, m = lum_median(lin)
            ratio = lb / max(lh, 1e-4)
            clip = float((s.reshape(-1, 3)[m] >= 254.5 / 255).any(-1).mean()) if m.any() else 0.0
            ok = ALBEDO_OK[0] <= ratio <= ALBEDO_OK[1]
            lines.append('%s %s: luminance %.3f vs hers %.3f -> x%.2f %s; mean sRGB %s; clipped %.1f%%' % (
                mat, kind, lb, lh, ratio, 'ok' if ok else 'OUT OF RANGE %.2f..%.2f' % ALBEDO_OK,
                np.round(s.reshape(-1, 3)[m].mean(0), 3) if m.any() else '-', 100 * clip))
            if not ok:
                alb_bad.append('%s %s x%.2f' % (mat.replace('reika_', ''), kind.replace('albedo', '').strip('_') or 'albedo', ratio))
            if clip > CLIP_OK:
                clip_bad.append('%s %.0f%%' % (mat.replace('reika_', ''), 100 * clip))
            if kind == 'albedo':
                row.append(s)
        if len(row) < 2:
            row.append(np.zeros((N, N, 3), np.float32))
        hn = None
        if (mat, '_NormalMap') in htex and os.path.isfile(htex[(mat, '_NormalMap')]):
            hn, lay = decode_normal(load(htex[(mat, '_NormalMap')]))
            ht = tilt(hn)
            lines.append('%s her normal (%s): large-scale tilt p99 %.1f deg, %.1f%% over 10 deg' % (mat, lay, np.percentile(ht, 99), 100 * (ht > 10).mean()))
        row.append(hn * 0.5 + 0.5 if hn is not None else np.zeros((N, N, 3), np.float32))
        bt = None
        for kind in ('normal', 'normal_faceonly'):
            p = tex.get((mat, kind))
            if not p or not os.path.isfile(p):
                continue
            px = load(p)
            n, lay = decode_normal(px)
            t = tilt(n)
            p99, share = float(np.percentile(t, 99)), float((t > 10).mean())
            ok = p99 <= TILT_P99_OK and share <= TILT_SHARE_OK
            lines.append('%s %s (%s): large-scale tilt median %.1f, p99 %.1f deg, %.1f%% over 10 deg -> %s' % (
                mat, kind, lay, np.median(t), p99, 100 * share, 'ok' if ok else 'CARRIES SHAPE (max p99 %.0f deg, %.0f%%)' % (TILT_P99_OK, 100 * TILT_SHARE_OK)))
            if not ok:
                nrm_bad.append('%s %s p99 %.0f deg' % (mat.replace('reika_', ''), kind.replace('normal', '').strip('_') or 'normal', p99))
            if kind == 'normal':
                row.append(n * 0.5 + 0.5)
                bt = t
        if len(row) < 4:
            row.append(np.zeros((N, N, 3), np.float32))
        row.append(heat(bt) if bt is not None else np.zeros((N, N, 3), np.float32))
        sheet.append(np.concatenate([tile(r) for r in row], axis=1))
    layers = read_layers(her)
    for mat in sorted(layers):
        for l in layers.get(mat, []):
            lines.append('%s her layer on top of any skin: %s' % (mat, l))
    if not tex:
        rows.append('WARN  material check: no baked textures listed in character.txt')
    else:
        rows.append(('PASS  ' if not alb_bad else 'WARN  ') + 'material check, albedo brightness: ' + (
            'within %.2f..%.2f of hers' % ALBEDO_OK if not alb_bad else ', '.join(alb_bad) + ' of hers (re-bake: run.ps1 -Name <name> -Rebake)'))
        rows.append(('PASS  ' if not nrm_bad else 'WARN  ') + 'material check, normal maps: ' + (
            'skin detail only (large-scale tilt p99 <= %.0f deg)' % TILT_P99_OK if not nrm_bad else ', '.join(nrm_bad) + ': the map carries body shape (re-bake: run.ps1 -Name <name> -Rebake)'))
        if clip_bad:
            rows.append('WARN  material check, clipped albedo: ' + ', '.join(clip_bad) + ' of skin texels at full white')
        if not her:
            rows.append('WARN  material check: no export of her given, compared with fixed values (export her from the Presets tab)')
    with open(os.path.join(chk, 'materials.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines + ['', 'summary:'] + rows + [
            '', 'materials.png: one row per material (body, face, foot); columns: her albedo x tint | baked albedo | her normal | '
            'baked normal | baked normal large-scale tilt (black 0 -> red 15 -> yellow 30+ deg)']) + '\n')
    if sheet:
        w = max(s.shape[1] for s in sheet)
        img = np.concatenate([np.pad(s, ((0, 4), (0, w - s.shape[1]), (0, 0))) for s in sheet], axis=0)
        h, w = img.shape[:2]
        im = bpy.data.images.new('matcheck', w, h, alpha=False, float_buffer=False)
        try:
            im.colorspace_settings.name = 'Non-Color'
            rgba = np.ones((h, w, 4), np.float32); rgba[..., :3] = np.clip(img[::-1], 0, 1)
            im.pixels.foreach_set(rgba.ravel())
            im.filepath_raw = os.path.join(chk, 'materials.png'); im.file_format = 'PNG'; im.save()
        finally:
            bpy.data.images.remove(im)
    for l in lines[1:]:
        log(l)
    return rows


def write_rows(out, rows):
    """Replace the material check rows of _check/summary.txt (standalone run) and refresh its header count."""
    path = os.path.join(out, '_check', 'summary.txt')
    lines = []
    if os.path.exists(path):
        with open(path, encoding='utf-8') as f:
            lines = [l for l in f.read().splitlines() if l]
    head = lines[0] if lines and lines[0].startswith('#') else '# ' + os.path.basename(os.path.normpath(out))
    body = [l for l in lines if not l.startswith('#') and 'material check' not in l] + rows
    npass = sum(l.startswith('PASS') for l in body)
    head = head.split(':')[0] + ': %d PASS, %d WARN' % (npass, len(body) - npass)
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join([head] + body) + '\n')


if __name__ == '__main__':
    args = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    a = lambda k, d=None: args[args.index(k) + 1] if k in args else d  # noqa: E731
    o = a('--out')
    h = a('--her', os.environ.get('SFP_GAME_EXPORT', ''))
    r = run(o, h)
    write_rows(o, r)
    for x in r:
        log('summary: ' + x)
