"""Texture bake (Blender/Cycles, selected to active): VaM's skin textures onto her UV layout.

Her fitted skin (posed into VaM's rest pose, lying on the VaM surface) is the bake target; the VaM body with its
own materials is the source. Albedo is baked as emission (exact colours, no lighting), normals in tangent space,
smoothness as emission of VaM's _GlossTex (Non-Color). _SpecTex is skipped: the game shader has no slot for it.

Memory: every target is an 8-bit image (an 8K float image would be 1 GB). The no-hit fill after the bake runs on the
image saved as a raw BMP, memory-mapped and processed in row chunks, so no full-size float array is ever built.

Face-only variant (the game keeps her body skin and uses only the character's face skin): the face texture fades into
HER face texture near the neck seam so the seam does not show, and its tone shifts toward hers. Needs her original
textures, exported by the plugin into the game export folder (SFP_GAME_EXPORT, set by run.ps1).
"""
import os
import re
import time

import bpy
import numpy as np

# her material -> (mesh, submesh material) pairs that carry it; uv offset for UVs outside 0..1
TARGETS = {
    'reika_body': ['reika_upperBody_normal', 'reika_lowerBody_normal', 'reika_foot', 'reika_neck',
                   'reika_nipple_type_1', 'reika_nail_normal', 'pussy_typeC_normal'],
    'reika_face_a': ['reika_face_a'],
    'reika_foot': ['reika_foot'],
}
# texture size relative to the body's (--size): body 8192, face 4096, foot 4096
RES_DIV = {'reika_body': 1, 'reika_face_a': 2, 'reika_foot': 2}
MARGIN_MUL = {'reika_foot': 2}   # the foot was baked at half the size with the same 24 px margin
KINDS = ('albedo', 'normal', 'smooth')
# face-only variant
FACE_MAT = 'reika_face_a'
SEAM_NEIGHBOURS = ('reika_neck', 'reika_upperBody_normal')   # her meshes that meet the face at the neck seam
SEAM_TOL = 5e-5            # m (0.05 mm): a face vertex this close to a neck/body vertex is a seam vertex
FACE_BAND_MM = 25.0        # width of the fade: w = 0 at the seam (all hers) -> 1 this far inside (all VaM)
TONE_STRENGTH = 1.0        # 0 = no tone shift, 1 = full per-channel gain (mean_her / mean_vam in the band)
GAIN_RANGE = (0.6, 1.6)
BAND_LO, BAND_HI = 0.2, 0.8   # pixels with w in this range are compared for the tone shift
NO_EXPORT_MSG = 'export her again from the Presets tab to get the face-only seam blend'
JPEG_QUALITY = 95
CHUNK_ROWS = 256   # rows per step of the no-hit fill
# shells and canals that sit off the VaM surface: their rays hit at grazing angles, so they keep a flat normal
NO_NORMAL = {'reika_nail_normal', 'pussy_typeC_normal'}
VAM_SKIN = {'Legs', 'Nostrils', 'Lips', 'Face', 'Toenails', 'Fingernails', 'Head', 'Hands', 'Shoulders', 'Neck',
            'Hips', 'Torso', 'Nipples', 'Forearms', 'Feet', 'Ears', 'defaultMat'}


def read_materials(vamdir):
    mats = {}
    with open(os.path.join(vamdir, 'materials.txt'), encoding='utf-8') as f:
        for l in f.read().splitlines():
            if not l or l.startswith('#'):
                continue
            t = l.split('	')
            if len(t) > 1:
                mats[t[1]] = dict(s.split('=', 1) for s in t[4:] if '=' in s)
    return mats


_CACHE = {}
WARNS = []   # appended to _check/summary.txt by blend.py
BAD = {}     # unusable texture files: file name -> reason
# skin materials that should carry a normal map: a missing one is worth a WARN (Nostrils and the like never have one)
BUMP_EXPECTED = ('Face', 'Torso', 'Legs', 'Head', 'Hips', 'Neck', 'Hands', 'Feet', 'Forearms', 'Shoulders')
GLOSS_EXPECTED = BUMP_EXPECTED + ('Lips', 'defaultMat')   # same idea for _GlossTex


def warn(log, msg):
    log('WARN: ' + msg)
    WARNS.append(msg)


def load_image(path, kind, log):
    """Load a VaM texture (kind: 'color' | 'normal' | 'gloss'); None (with a log line, and a WARN later from BAD) when the
    file is missing or unreadable. Only _MainTex, _BumpMap and _GlossTex are asked for: decal textures (_DecalTex)
    and the spec map are ignored."""
    key = (path, kind)
    if key in _CACHE:
        return _CACHE[key]
    im = None
    try:
        if not os.path.isfile(path):
            raise FileNotFoundError(os.path.basename(path))
        im = bpy.data.images.load(path, check_existing=False)
        if im.size[0] == 0 or im.size[1] == 0:
            bpy.data.images.remove(im)
            im = None
            raise ValueError('texture has no pixels')
        if kind == 'normal':
            im = unswizzle(im, path, log)
        elif kind == 'gloss':
            im.colorspace_settings.name = 'Non-Color'
    except Exception as e:  # noqa: BLE001
        BAD[os.path.basename(path)] = type(e).__name__
        log('  texture %s not usable (%s: %s)' % (os.path.basename(path), type(e).__name__, e))
        im = None
    _CACHE[key] = im
    return im


def normal_layout(px):
    """Which layout a normal map's pixels (N x 4, 0..1) are in: 'dxt5nm' (X in alpha, Y in green, red 1),
    'bc5' (X in red, Y in green, blue ~0, alpha ~1) or 'rgb'."""
    s = px[::101]
    if s[:, 0].mean() > 0.95 and s[:, 2].mean() < 0.8:
        return 'dxt5nm'
    if s[:, 2].mean() < 0.1 and s[:, 3].mean() > 0.95:
        return 'bc5'
    return 'rgb'


def unswizzle(im, path, log):
    im.colorspace_settings.name = 'Non-Color'
    w, h = im.size
    px = np.empty(w * h * 4, np.float32)
    im.pixels.foreach_get(px)
    px = px.reshape(-1, 4)
    lay = normal_layout(px)
    if lay == 'dxt5nm':  # as read back from the GPU: X in alpha, Y in green
        x = px[:, 3] * 2 - 1; y = px[:, 1] * 2 - 1
        z = np.sqrt(np.clip(1 - x * x - y * y, 0, 1))
        px[:, 0] = px[:, 3]; px[:, 2] = z * 0.5 + 0.5; px[:, 3] = 1
    elif lay == 'bc5':  # X in red, Y in green, Z dropped: rebuild it
        x = px[:, 0] * 2 - 1; y = px[:, 1] * 2 - 1
        z = np.sqrt(np.clip(1 - x * x - y * y, 0, 1))
        px[:, 2] = z * 0.5 + 0.5; px[:, 3] = 1
    if lay != 'rgb':
        im.pixels.foreach_set(px.ravel())
        im.update()
        im.pack()  # keep the fixed pixels (otherwise Cycles reloads the file)
        log('  %s: unswizzled %s normal map' % (os.path.basename(path), 'DXT5nm' if lay == 'dxt5nm' else 'BC5'))
    return im


def slot_image(vamdir, sl, slot, kind, log):
    fn = sl.get(slot)
    if not fn:
        return None
    return load_image(os.path.join(vamdir, fn), kind, log)


def skin_fill(vmats, vamdir, log, fill=(0.8, 0.62, 0.52, 1.0)):
    """Skin tone for pixels no ray reaches (inner folds): median of the first skin texture that loads (face, torso, ...)."""
    for g in ('Face', 'Torso', 'Head', 'Hips', 'Legs'):
        im = slot_image(vamdir, vmats.get(g, {}), '_MainTex', 'color', log)
        if im is None:
            continue
        w, h = im.size
        px = np.empty(w * h * 4, np.float32); im.pixels.foreach_get(px)
        px = px.reshape(-1, 4)[::97]
        px = px[(px[:, 0] > 0.3) & (px[:, 0] < 0.98)]  # skin, not the black/white borders
        if len(px):
            fill = tuple(float(x) for x in np.median(px, axis=0)[:3]) + (1.0,)
            log('fill colour from %s: %s' % (g, np.round(fill[:3], 3)))
            return fill
    warn(log, 'no skin texture to take the fill colour from: using a default skin tone')
    return fill


def flat_emission(name, color):
    """A material without a texture (slot missing or unreadable): the flat skin colour."""
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    emi = nt.nodes.new('ShaderNodeEmission')
    emi.inputs['Color'].default_value = color
    nt.links.new(emi.outputs['Emission'], out.inputs['Surface'])
    return m


def emission_material(name, img):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    tex = nt.nodes.new('ShaderNodeTexImage'); tex.image = img
    emi = nt.nodes.new('ShaderNodeEmission')
    nt.links.new(tex.outputs['Color'], emi.inputs['Color'])
    nt.links.new(emi.outputs['Emission'], out.inputs['Surface'])
    return m, nt, out, emi


def normal_material(name, img):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    bsdf = nt.nodes.new('ShaderNodeBsdfDiffuse')
    nt.links.new(bsdf.outputs['BSDF'], out.inputs['Surface'])
    if img is not None:
        tex = nt.nodes.new('ShaderNodeTexImage'); tex.image = img
        nm = nt.nodes.new('ShaderNodeNormalMap'); nm.space = 'TANGENT'
        nt.links.new(tex.outputs['Color'], nm.inputs['Color'])
        nt.links.new(nm.outputs['Normal'], bsdf.inputs['Normal'])
    return m


def gloss_samples(im):
    """A sample of an image's red channel (a gloss map is grayscale), read without a full-size float copy when the
    image is large: a scaled temporary copy stands in for it."""
    w, h = im.size
    tmp = None
    if w * h > 4096 * 4096:
        k = int(np.ceil(np.sqrt(w * h / (4096.0 * 4096.0))))
        tmp = im.copy()
        tmp.scale(max(1, w // k), max(1, h // k))
        im = tmp
        w, h = im.size
    px = np.empty(w * h * 4, np.float32)
    im.pixels.foreach_get(px)
    r = px.reshape(-1, 4)[::97, 0].copy()
    if tmp is not None:
        bpy.data.images.remove(tmp)
    return r


def setup_cycles(log):
    sc = bpy.context.scene
    sc.render.engine = 'CYCLES'
    sc.cycles.samples = 1
    sc.cycles.use_denoising = False
    try:   # saved pixels are the baked values, not a display transform of them
        sc.view_settings.view_transform = 'Standard'
        sc.view_settings.look = 'None'
    except Exception:  # noqa: BLE001
        pass
    try:
        prefs = bpy.context.preferences.addons['cycles'].preferences
        for kind in ('OPTIX', 'CUDA'):
            try:
                prefs.compute_device_type = kind
                prefs.get_devices()
                devs = [d for d in prefs.devices if d.type == kind]
                if devs:
                    for d in prefs.devices:
                        d.use = d.type == kind
                    sc.cycles.device = 'GPU'
                    log('bake on GPU (%s: %s)' % (kind, ', '.join(d.name for d in devs)))
                    return
            except TypeError:
                continue
    except Exception as e:  # noqa: BLE001
        log('GPU setup failed (%s), baking on CPU' % e)
    sc.cycles.device = 'CPU'


def to_byte(x):
    return int(min(255, max(0, int(float(x) * 255.0 + 0.5))))


def fill_and_save(img, res, kind, fill_rgb, sentinel, path, sc, scratch, log):
    """Fill the pixels no ray reached and write the final file. Returns the share of such pixels.

    The baked 8-bit image is saved as a raw BMP and memory-mapped; the fill runs CHUNK_ROWS rows at a time, so even
    at 8192x8192 nothing larger than a few MB is ever held as a float array. The patched BMP is then loaded again
    (still 8-bit) and written as the final PNG/JPEG with the scene's output settings.
    albedo/normal: a no-hit pixel comes out black (all channels <= 1/255) -> fill colour.
    smooth: the target started as a sentinel colour that a hit can never leave (use_clear is off) -> median gloss,
            and every hit pixel gets R=G=B.
    """
    tmp = os.path.join(scratch, '_bake_%s.bmp' % os.path.splitext(os.path.basename(path))[0])
    img.filepath_raw = tmp
    img.file_format = 'BMP'
    img.save()
    colorspace = img.colorspace_settings.name
    with open(tmp, 'rb') as f:
        hdr = f.read(54)
    off = int.from_bytes(hdr[10:14], 'little')
    bw = int.from_bytes(hdr[18:22], 'little'); bh = int.from_bytes(hdr[22:26], 'little', signed=True)
    bpp = int.from_bytes(hdr[28:30], 'little')
    if hdr[:2] != b'BM' or bpp != 24 or bw != res or abs(bh) != res:
        raise ValueError('unexpected raw BMP layout (%dx%d, %d bpp)' % (bw, bh, bpp))
    stride = (res * 3 + 3) & ~3
    mm = np.memmap(tmp, dtype=np.uint8, mode='r+', offset=off, shape=(res, stride))
    fb = np.array([to_byte(c) for c in fill_rgb[::-1]], np.uint8)          # BGR, as the file stores it
    sb = np.array([to_byte(c) for c in sentinel[::-1]], np.uint8) if sentinel is not None else None
    n_miss = 0
    for r0 in range(0, res, CHUNK_ROWS):
        a = mm[r0:r0 + CHUNK_ROWS, :res * 3].reshape(-1, res, 3)
        if kind == 'smooth':
            miss = (a == sb).all(axis=2)
            gray = a[:, :, 2].copy()                                   # R of BGR
            a[:] = gray[:, :, None]
            a[miss] = fb
        else:
            miss = a.max(axis=2) <= 1
            a[miss] = fb
        n_miss += int(miss.sum())
    mm.flush()
    del mm, a, miss
    im2 = bpy.data.images.load(tmp, check_existing=False)
    im2.colorspace_settings.name = colorspace
    im2.save_render(path, scene=sc)
    bpy.data.images.remove(im2)
    os.remove(tmp)
    return n_miss / float(res * res)


# ---- face-only variant ---------------------------------------------------------------------------------------
def smoothstep(x):
    x = np.clip(x, 0.0, 1.0)
    return x * x * (3.0 - 2.0 * x)


def srgb_to_lin(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4).astype(np.float32)


def lin_to_srgb(c):
    c = np.clip(c, 0.0, 1.0)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * np.power(c, 1 / 2.4) - 0.055).astype(np.float32)


def kdtree():
    """scipy's cKDTree; Blender's Python does not always see run.ps1's PYTHONPATH, so look in the pipeline's private
    scipy folder (%LOCALAPPDATA%\SfMorph\pydeps) too."""
    try:
        from scipy.spatial import cKDTree
    except ImportError:
        import sys
        sys.path.append(os.path.join(os.environ.get('LOCALAPPDATA', ''), 'SfMorph', 'pydeps'))
        from scipy.spatial import cKDTree
    return cKDTree


def seam_weights(Z, log):
    """Per-vertex fade weight of her face mesh. Seam vertices = face vertices that coincide (< SEAM_TOL on the bind
    positions vc) with a neck / upper-body vertex; the eye, mouth and nostril holes do not touch those meshes, so they
    are never seam. w = smoothstep(distance to the nearest seam vertex / band). Returns (w, seam vertex indices)."""
    cKDTree = kdtree()
    vc = Z[FACE_MAT + '|vc'].astype(np.float64)
    seam = np.zeros(len(vc), bool)
    for n in SEAM_NEIGHBOURS:
        if n + '|vc' in Z.files:
            d, _ = cKDTree(Z[n + '|vc'].astype(np.float64)).query(vc)
            seam |= d < SEAM_TOL
    idx = np.nonzero(seam)[0]
    if len(idx) == 0:
        return None, idx
    d, _ = cKDTree(vc[idx]).query(vc)
    w = smoothstep(d / (FACE_BAND_MM * 1e-3))
    log('face-only: %d seam vertices, fade band %.0f mm (%d of %d vertices inside the band)' % (
        len(idx), FACE_BAND_MM, int(((w > 0) & (w < 1)).sum()), len(vc)))
    return w.astype(np.float32), idx


def read_textures_txt(gdir):
    """Her exported textures, found only through textures.txt in the game export folder:
        material <TAB> property <TAB> file <TAB> format <TAB> size        (file/format/size are '-' when missing)
        # material <TAB> colour property <TAB> r g b a                    (second block: her original tints)
    Returns ({(material, property): path}, {(material, property): (r, g, b)}); (None, None) without textures.txt."""
    path = os.path.join(gdir, 'textures.txt') if gdir else ''
    if not path or not os.path.isfile(path):
        return None, None
    tex, tint = {}, {}
    with open(path, encoding='utf-8') as f:
        for l in f.read().splitlines():
            if not l.strip() or l.startswith('#'):
                continue
            t = l.split('\t')
            if len(t) >= 5:   # texture row
                fn = t[2].strip()
                if fn and fn != '-':
                    tex[(t[0].strip(), t[1].strip())] = os.path.join(gdir, fn)
            elif len(t) >= 3:   # colour row
                nums = re.findall(r'[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?', t[2])
                if len(nums) >= 3:
                    tint[(t[0].strip(), t[1].strip())] = tuple(float(x) for x in nums[:3])
    return tex, tint


def face_material_key(tex, tint):
    """Her face material's name in textures.txt: reika_face_a, else any material containing 'reika_face'."""
    names = [m for m, _ in list(tex) + list(tint)]
    if FACE_MAT in names:
        return FACE_MAT
    for m in names:
        if 'reika_face' in m.lower():
            return m
    return None


def read_pixels(path, res, log):
    """A texture file as a float32 (res, res, 4) array of its stored values (0..1, no colour management), resampled to
    res when it has another size."""
    im = bpy.data.images.load(path, check_existing=False)
    try:
        im.colorspace_settings.name = 'Non-Color'
        try:
            im.alpha_mode = 'CHANNEL_PACKED'
        except Exception:  # noqa: BLE001
            pass
        w, h = im.size
        if w == 0 or h == 0:
            raise ValueError('texture has no pixels')
        if (w, h) != (res, res):
            log('  %s: %dx%d resampled to %dx%d' % (os.path.basename(path), w, h, res, res))
            im.scale(res, res)
        px = np.empty(res * res * 4, np.float32)
        im.pixels.foreach_get(px)
    finally:
        bpy.data.images.remove(im)
    return px.reshape(res, res, 4)


def write_image(arr, res, path, fmt, quality=None):
    """arr: float32 (res, res, 3) stored values; written as 8-bit RGB with no colour transform."""
    img = bpy.data.images.new('sfp_out', res, res, alpha=False, float_buffer=False)
    try:
        img.colorspace_settings.name = 'Non-Color'
        rgba = np.ones((res, res, 4), np.float32)
        rgba[:, :, :3] = arr
        img.pixels.foreach_set(rgba.ravel())
        img.update()
        img.filepath_raw = path
        img.file_format = fmt
        if quality is not None:
            img.save(filepath=path, quality=quality)
        else:
            img.save()
    finally:
        bpy.data.images.remove(img)


def bake_seam_mask(Z, w, make_obj, u2b, res, sc, scratch, out, log):
    """Bake the per-vertex fade weight w into her face UV space (colour attribute -> emission -> 8-bit Non-Color
    image, same no-hit fill and margin as the other maps). Returns the saved mask path."""
    n = FACE_MAT
    g = [str(x) for x in Z[n + '|groups']]
    f = Z[n + '|faces'][Z[n + '|face_group'] == g.index(FACE_MAT)]
    uv = Z[n + '|uv'].copy()
    uv -= np.floor(np.median(uv[np.unique(f)], axis=0))
    tgt = img = tm = None
    try:
        tgt = make_obj('tgt_seam', u2b(Z[n + '|vp1']), f, uv)
        ca = tgt.data.color_attributes.new('sfp_w', 'FLOAT_COLOR', 'POINT')
        col = np.zeros((len(w), 4), np.float32)
        col[:, 0] = col[:, 1] = col[:, 2] = w; col[:, 3] = 1.0
        ca.data.foreach_set('color', col.ravel())
        img = bpy.data.images.new('seam_mask', res, res, alpha=False, float_buffer=False)
        img.generated_color = (0.0, 0.0, 0.0, 1.0)
        img.colorspace_settings.name = 'Non-Color'
        tm = bpy.data.materials.new('tm_seam')
        tm.use_nodes = True
        nt = tm.node_tree
        for nd in list(nt.nodes):
            nt.nodes.remove(nd)
        outn = nt.nodes.new('ShaderNodeOutputMaterial')
        at = nt.nodes.new('ShaderNodeAttribute'); at.attribute_name = 'sfp_w'; at.attribute_type = 'GEOMETRY'
        emi = nt.nodes.new('ShaderNodeEmission')
        nt.links.new(at.outputs['Color'], emi.inputs['Color'])
        nt.links.new(emi.outputs['Emission'], outn.inputs['Surface'])
        node = nt.nodes.new('ShaderNodeTexImage'); node.image = img
        nt.nodes.active = node
        tgt.data.materials.clear(); tgt.data.materials.append(tm)
        bpy.ops.object.select_all(action='DESELECT')
        tgt.select_set(True)
        bpy.context.view_layer.objects.active = tgt
        sc.render.bake.use_selected_to_active = False
        sc.render.bake.margin = max(8, 24 * res // 4096)
        sc.render.bake.margin_type = 'EXTEND'
        sc.render.bake.target = 'IMAGE_TEXTURES'
        sc.render.bake.use_clear = True
        bpy.ops.object.bake(type='EMIT')
        sc.render.image_settings.file_format = 'PNG'
        sc.render.image_settings.color_depth = '8'
        sc.render.image_settings.color_mode = 'RGB'
        path = os.path.join(out, 'tex_%s_seam.png' % FACE_MAT)
        fill_and_save(img, res, 'seam', (0.0, 0.0, 0.0), None, path, sc, scratch, log)   # no-hit = all hers (w = 0)
        return path
    finally:
        if tgt is not None:
            bpy.data.objects.remove(tgt, do_unlink=True)
        if img is not None:
            bpy.data.images.remove(img)
        if tm is not None:
            bpy.data.materials.remove(tm)


def log_band_width(Z, w, idx, mask, res, log):
    """How wide the fade is in texture pixels: from the face triangles (UV area per 3D area) and measured on the mask
    (distance from the seam vertices' UVs to the first pixels with w > 0.97)."""
    n = FACE_MAT
    g = [str(x) for x in Z[n + '|groups']]
    f = Z[n + '|faces'][Z[n + '|face_group'] == g.index(FACE_MAT)]
    uv = Z[n + '|uv'].astype(np.float64)
    uv -= np.floor(np.median(uv[np.unique(f)], axis=0))
    vc = Z[n + '|vc'].astype(np.float64) * 1000.0   # mm
    wm = w[f].mean(1)
    sel = (wm > 0.05) & (wm < 0.95)
    if sel.any():
        t = f[sel]
        a3 = 0.5 * np.linalg.norm(np.cross(vc[t[:, 1]] - vc[t[:, 0]], vc[t[:, 2]] - vc[t[:, 0]]), axis=1).sum()
        e1 = uv[t[:, 1]] - uv[t[:, 0]]; e2 = uv[t[:, 2]] - uv[t[:, 0]]
        a2 = 0.5 * np.abs(e1[:, 0] * e2[:, 1] - e1[:, 1] * e2[:, 0]).sum() * res * res
        pxmm = float(np.sqrt(a2 / a3))
        log('face-only: band %.0f mm = about %.0f px (%.2f px/mm at the seam, %dx%d texture)' % (FACE_BAND_MM, FACE_BAND_MM * pxmm, pxmm, res, res))
    try:
        cKDTree = kdtree()
        ys, xs = np.nonzero(mask[::4, ::4] > 0.97)
        if len(xs):
            seam_px = uv[idx] * res
            d, _ = cKDTree(seam_px).query(np.stack([xs * 4.0, ys * 4.0], 1))
            log('face-only: on the baked mask w > 0.97 starts %.0f px from the nearest seam vertex' % d.min())
    except Exception as e:  # noqa: BLE001
        log('face-only: band width on the mask not measured (%s)' % e)


def face_only(Z, names, out, size, make_obj, u2b, log):
    """Build the face-only albedo/normal. Returns ([(mat, kind, file)], summary row)."""
    row = 'face-only seam blend: '
    res = size // RES_DIV[FACE_MAT]
    gdir = os.environ.get('SFP_GAME_EXPORT', '')
    tex, tints = read_textures_txt(gdir)
    fm = face_material_key(tex, tints) if tex is not None else None
    her_col = tex.get((fm, '_ColorMap')) if fm else None
    if not her_col or not os.path.isfile(her_col):
        log('WARN: her original face texture not found (%s): %s' % (
            'SFP_GAME_EXPORT not set' if not gdir else ('no textures.txt in ' + gdir if tex is None else 'no _ColorMap listed for the face material'),
            NO_EXPORT_MSG))
        return [], 'WARN  ' + row + 'her original textures are missing, ' + NO_EXPORT_MSG
    her_nrm = tex.get((fm, '_NormalMap'))
    vam_a = os.path.join(out, 'tex_%s_albedo.jpg' % FACE_MAT)
    vam_n = os.path.join(out, 'tex_%s_normal.png' % FACE_MAT)
    if not os.path.isfile(vam_a):
        return [], 'WARN  ' + row + 'the face albedo was not baked, so no face-only variant'
    if FACE_MAT + '|vc' not in Z.files or not any(n + '|vc' in Z.files for n in SEAM_NEIGHBOURS):
        return [], 'WARN  ' + row + 'her face / neck meshes are not in the stage data'
    w, idx = seam_weights(Z, log)
    if w is None:
        return [], 'WARN  ' + row + 'no face vertex coincides with the neck or body: no seam found'
    t0 = time.time()
    sc = bpy.context.scene
    scratch = os.path.join(out, '_check')
    mpath = bake_seam_mask(Z, w, make_obj, u2b, res, sc, scratch, out, log)
    mask = read_pixels(mpath, res, log)[:, :, 0].copy()
    log_band_width(Z, w, idx, mask, res, log)
    tint, tsrc = (1.0, 1.0, 1.0), 'white (no tint listed)'
    for p in ('_SkinColor', '_BaseColor', '_Color'):
        if (fm, p) in tints:
            tint, tsrc = tints[(fm, p)], p
            break
    t = np.maximum(np.array(tint, np.float32), 0.02)
    log('face-only: her face tint %s from %s' % (np.round(tint, 3), tsrc))
    written = []
    # --- albedo (linear space) ---
    her = srgb_to_lin(read_pixels(her_col, res, log)[:, :, :3])
    vam = srgb_to_lin(read_pixels(vam_a, res, log)[:, :, :3])
    band = (mask > BAND_LO) & (mask < BAND_HI)
    nb = int(band.sum())
    gain = np.ones(3, np.float32)
    if nb >= 200:
        mh = her[band].mean(0); mv = vam[band].mean(0)
        gain = np.clip(mh / np.maximum(mv, 1e-4), *GAIN_RANGE)
        gain = (gain ** TONE_STRENGTH).astype(np.float32)
        log('face-only: tone shift over %d band pixels: her mean %s, VaM mean %s -> gain %s' % (
            nb, np.round(mh, 3), np.round(mv, 3), np.round(gain, 3)))
    else:
        log('face-only: only %d band pixels, no tone shift' % nb)
    for r0 in range(0, res, 512):
        s = slice(r0, r0 + 512)
        m = mask[s][:, :, None]
        her[s] = np.clip(her[s] * (1 - m) + (vam[s] * gain / t) * m, 0.0, 1.0)
    del vam
    apath = os.path.join(out, 'tex_%s_albedo_faceonly.jpg' % FACE_MAT)
    write_image(lin_to_srgb(her), res, apath, 'JPEG', quality=JPEG_QUALITY)
    written.append((FACE_MAT, 'albedo_faceonly', os.path.basename(apath)))
    del her
    # --- normal ---
    note = ''
    if her_nrm and os.path.isfile(her_nrm) and os.path.isfile(vam_n):
        hp = read_pixels(her_nrm, res, log)
        lay = normal_layout(hp.reshape(-1, 4))
        if lay == 'dxt5nm':
            hx = hp[:, :, 3] * 2 - 1; hy = hp[:, :, 1] * 2 - 1
        else:
            hx = hp[:, :, 0] * 2 - 1; hy = hp[:, :, 1] * 2 - 1
        hz = hp[:, :, 2] * 2 - 1 if lay == 'rgb' else np.sqrt(np.clip(1 - hx * hx - hy * hy, 0, 1))
        del hp
        vp = read_pixels(vam_n, res, log)[:, :, :3] * 2 - 1
        out_n = np.empty((res, res, 3), np.float32)
        for r0 in range(0, res, 512):
            s = slice(r0, r0 + 512)
            m = mask[s]
            nx = hx[s] * (1 - m) + vp[s, :, 0] * m
            ny = hy[s] * (1 - m) + vp[s, :, 1] * m
            nz = hz[s] * (1 - m) + vp[s, :, 2] * m
            ln = np.maximum(np.sqrt(nx * nx + ny * ny + nz * nz), 1e-6)
            out_n[s, :, 0] = nx / ln * 0.5 + 0.5; out_n[s, :, 1] = ny / ln * 0.5 + 0.5; out_n[s, :, 2] = nz / ln * 0.5 + 0.5
        npath = os.path.join(out, 'tex_%s_normal_faceonly.png' % FACE_MAT)
        write_image(out_n, res, npath, 'PNG')
        written.append((FACE_MAT, 'normal_faceonly', os.path.basename(npath)))
        log('face-only: her normal map layout %s' % lay)
    else:
        note = ' (no normal variant: ' + ('her normal map missing' if not (her_nrm and os.path.isfile(her_nrm)) else 'no baked face normal') + ')'
        log('face-only:' + note)
    log('face-only seam blend done in %.1f s' % (time.time() - t0))
    return written, 'PASS  ' + row + '%d seam vertices, %.0f mm fade, gain %s, tint %s%s' % (
        len(idx), FACE_BAND_MM, '/'.join('%.2f' % x for x in gain), '/'.join('%.2f' % x for x in tint), note)


def add_summary_row(out, row):
    """Put the face-only row into _check/summary.txt (blend.py's own rows are added after it and keep it)."""
    path = os.path.join(out, '_check', 'summary.txt')
    lines = []
    if os.path.exists(path):
        with open(path, encoding='utf-8') as f:
            lines = [l for l in f.read().splitlines() if l and 'face-only seam blend' not in l]
    if not lines:
        return
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines + [row]) + '\n')


def run(Z, names, out, vamdir, size, make_obj, u2b, clear, log):
    """Bake the textures. Returns (textures written, WARN messages). A texture that is missing or fails to load
    never stops the bake: that material falls back to the flat skin colour (_MainTex), a flat normal (_BumpMap) or
    the median gloss (_GlossTex)."""
    clear()
    setup_cycles(log)
    vmats = read_materials(vamdir)
    groups = [str(g) for g in Z['vam_groups']]
    # --- source: VaM skin, one material slot per VaM material ---------------------------------------
    keep = [i for i, g in enumerate(groups) if g in VAM_SKIN]
    m = np.isin(Z['vam_g'], keep)
    # VaM's eyelids (often caught mid-blink) sit where her open eyes are: left out, her lids get the skin average.
    # The exclusion zone is relative to the VaM eyeball radius (15.3 mm on the character it was tuned on).
    if 'vam_eyes' in Z.files:
        er = float(Z['vam_eye_r']) if 'vam_eye_r' in Z.files and np.isfinite(float(Z['vam_eye_r'])) else 0.0153
        cen = Z['vam_v'][Z['vam_f']].mean(1)
        for e in Z['vam_eyes']:
            r = np.linalg.norm(cen - e, axis=1)
            m &= ~((r < 1.11 * er) & (cen[:, 2] > e[2] - 0.26 * er))
    if not m.any():
        warn(log, 'the VaM skin has no faces: no textures baked')
        return [], WARNS
    vf = Z['vam_f'][m][:, ::-1]
    vg = Z['vam_g'][m]
    src = make_obj('vam_src', u2b(Z['vam_v']), vf, Z['vam_uv'])
    fill = skin_fill(vmats, vamdir, log)
    slot_of = {}
    albedo_mats, normal_mats, gloss_mats = [], [], []
    have_normal = False
    flat, nobump = [], []
    gloss_imgs, no_gloss = {}, []
    used_groups = []
    for gi in keep:
        if not (vg == gi).any():
            continue
        g = groups[gi]
        sl = vmats.get(g, {})
        cimg = slot_image(vamdir, sl, '_MainTex', 'color', log)   # (_DecalTex is not used)
        nimg = slot_image(vamdir, sl, '_BumpMap', 'normal', log)
        gimg = slot_image(vamdir, sl, '_GlossTex', 'gloss', log)
        if cimg is None:
            flat.append(g)
            am = flat_emission('src_' + g, fill)
        else:
            am = emission_material('src_' + g, cimg)[0]
        have_normal |= nimg is not None
        if nimg is None and g in BUMP_EXPECTED:
            nobump.append(g)
        if gimg is None and g in GLOSS_EXPECTED:
            no_gloss.append(g)
        gloss_imgs[g] = gimg
        used_groups.append(g)
        slot_of[gi] = len(albedo_mats)
        albedo_mats.append(am); normal_mats.append(normal_material('srcn_' + g, nimg))
    # smoothness: emission of _GlossTex; a material without a usable one gets the median gloss of the set (0.5 if none)
    pool = [gloss_samples(im) for im in {id(im): im for im in gloss_imgs.values() if im is not None}.values()]
    have_gloss = bool(pool)
    gloss_fill = float(np.median(np.concatenate(pool))) if have_gloss else 0.5
    del pool
    for g in used_groups:
        gm = gloss_imgs[g]
        gloss_mats.append(emission_material('srcg_' + g, gm)[0] if gm is not None
                          else flat_emission('srcg_' + g, (gloss_fill, gloss_fill, gloss_fill, 1.0)))
    log('smoothness fill (median gloss): %.3f%s' % (gloss_fill, '' if have_gloss else ' (no gloss texture found)'))
    if any(sl.get('_SpecTex') for sl in (vmats.get(g, {}) for g in used_groups)):
        log('_SpecTex skipped: the game shader has no specular slot')
    for am in albedo_mats:
        src.data.materials.append(am)
    mi = np.array([slot_of.get(int(g), 0) for g in vg], np.int32)
    src.data.polygons.foreach_set('material_index', mi)
    if BAD:
        shown = ', '.join('%s (%s)' % (k, v) for k, v in list(BAD.items())[:3])
        warn(log, '%d VaM texture file(s) missing or unreadable: %s%s' % (len(BAD), shown, ' ...' if len(BAD) > 3 else ''))
    if flat:
        warn(log, 'no usable _MainTex on %s: baked as the flat skin colour' % ', '.join(sorted(flat)))
    if nobump:
        warn(log, 'no usable _BumpMap on %s: flat normals there' % ', '.join(sorted(nobump)))
    if not have_normal:
        warn(log, 'no usable _BumpMap on any skin material: no normal maps baked')
    if no_gloss:
        warn(log, 'no usable _GlossTex on %s: median gloss there' % ', '.join(sorted(no_gloss)))
    if not have_gloss:
        warn(log, 'no usable _GlossTex on any skin material: flat smoothness %.2f' % gloss_fill)

    written = []
    scratch = os.path.join(out, '_check')
    os.makedirs(scratch, exist_ok=True)
    for mat, meshes in TARGETS.items():
        vs, fs, uvs, used_meshes = [], [], [], []
        base = 0
        for n in meshes:
            if n not in names:
                continue
            g = [str(x) for x in Z[n + '|groups']]
            if mat not in g:
                continue
            fm = Z[n + '|face_group'] == g.index(mat)
            f = Z[n + '|faces'][fm]
            uv = Z[n + '|uv'].copy()
            used = np.unique(f)
            off = np.floor(np.median(uv[used], axis=0))  # the foot's own material lives at u 1..2
            uv -= off
            vs.append(Z[n + '|vp1']); uvs.append(uv); fs.append(f + base); used_meshes.append(n)
            base += len(Z[n + '|vp1'])
        if not fs:
            continue
        res = size // RES_DIV.get(mat, 1)
        for kind in KINDS:
            if kind == 'normal' and not have_normal:
                continue
            tgt = img = tm = None
            t0 = time.time()
            try:
                sel = [i for i, n in enumerate(used_meshes) if kind != 'normal' or n not in NO_NORMAL]
                tgt = make_obj('tgt_' + mat, u2b(np.concatenate(vs)), np.concatenate([fs[i] for i in sel]), np.concatenate(uvs))
                img = bpy.data.images.new('%s_%s' % (mat, kind), res, res, alpha=False, float_buffer=False)
                sentinel = None
                if kind == 'albedo':
                    img.generated_color = fill
                elif kind == 'normal':
                    img.generated_color = (0.5, 0.5, 1.0, 1.0)
                else:   # a colour no baked (gray) hit can have, so the pixels no ray reaches can be told from black gloss
                    gf = min(1.0, max(0.0, gloss_fill))
                    sentinel = (gf, gf, 0.0 if to_byte(gf) > 127 else 1.0)
                    img.generated_color = sentinel + (1.0,)
                if kind != 'albedo':
                    img.colorspace_settings.name = 'Non-Color'
                tm = bpy.data.materials.new('tm_%s_%s' % (mat, kind))
                tm.use_nodes = True
                node = tm.node_tree.nodes.new('ShaderNodeTexImage'); node.image = img
                tm.node_tree.nodes.active = node
                tgt.data.materials.clear(); tgt.data.materials.append(tm)
                for i, (am, nm, gm) in enumerate(zip(albedo_mats, normal_mats, gloss_mats)):
                    src.data.materials[i] = am if kind == 'albedo' else (nm if kind == 'normal' else gm)
                bpy.ops.object.select_all(action='DESELECT')
                src.select_set(True); tgt.select_set(True)
                bpy.context.view_layer.objects.active = tgt
                sc = bpy.context.scene
                sc.render.bake.use_selected_to_active = True
                sc.render.bake.cage_extrusion = 0.012
                sc.render.bake.max_ray_distance = 0.05
                sc.render.bake.margin = max(8, 24 * res // 4096) * MARGIN_MUL.get(mat, 1)   # the same width in UV space at every size
                sc.render.bake.margin_type = 'EXTEND'
                sc.render.bake.target = 'IMAGE_TEXTURES'
                sc.render.bake.use_clear = kind != 'smooth'
                if kind == 'albedo':
                    bpy.ops.object.bake(type='EMIT')
                    path = os.path.join(out, 'tex_%s_albedo.jpg' % mat)
                    sc.render.image_settings.file_format = 'JPEG'
                    sc.render.image_settings.quality = JPEG_QUALITY
                elif kind == 'normal':
                    sc.render.bake.normal_space = 'TANGENT'
                    bpy.ops.object.bake(type='NORMAL')
                    path = os.path.join(out, 'tex_%s_normal.png' % mat)
                    sc.render.image_settings.file_format = 'PNG'
                    sc.render.image_settings.color_depth = '8'
                else:
                    bpy.ops.object.bake(type='EMIT')
                    path = os.path.join(out, 'tex_%s_smooth.png' % mat)
                    sc.render.image_settings.file_format = 'PNG'
                    sc.render.image_settings.color_depth = '8'
                sc.render.image_settings.color_mode = 'RGB'
                # pixels whose rays found no VaM skin: give them the skin tone / a flat normal / the median gloss
                if kind == 'albedo':
                    frgb = fill[:3]
                elif kind == 'normal':
                    frgb = (0.5, 0.5, 1.0)
                else:
                    frgb = (gloss_fill,) * 3
                miss = fill_and_save(img, res, kind, frgb, sentinel, path, sc, scratch, log)
                log('  %s %s: %.2f%% of pixels had no hit (filled)' % (mat, kind, 100.0 * miss))
                written.append((mat, kind, os.path.basename(path)))
                log('baked %s %s (%dx%d) -> %s in %.1f s' % (mat, kind, res, res, path, time.time() - t0))
            except Exception as e:  # noqa: BLE001
                warn(log, 'bake of %s %s failed (%s: %s): her own texture stays' % (mat, kind, type(e).__name__, e))
            finally:
                if tgt is not None:
                    bpy.data.objects.remove(tgt, do_unlink=True)
                if img is not None:
                    bpy.data.images.remove(img)
                if tm is not None:
                    bpy.data.materials.remove(tm)
    extra = []   # face-only variant lines (not counted in `written`: blend.py expects the 9 main maps there)
    if FACE_MAT in names and any(m == FACE_MAT and k == 'albedo' for m, k, _ in written):
        try:
            extra, row = face_only(Z, names, out, size, make_obj, u2b, log)
        except Exception as e:  # noqa: BLE001
            extra, row = [], 'WARN  face-only seam blend: failed (%s: %s)' % (type(e).__name__, e)
            log('WARN: ' + row)
        add_summary_row(out, row)
    # character.txt: replace any texture lines from an earlier bake, then list this one's
    cpath = os.path.join(out, 'character.txt')
    keep_lines = []
    if os.path.exists(cpath):
        with open(cpath, encoding='utf-8') as f:
            keep_lines = [l for l in f.read().splitlines() if not l.startswith('texture\t')]
    with open(cpath, 'w', encoding='utf-8', newline='\n') as f:
        for l in keep_lines:
            f.write(l + '\n')
        for mat, kind, fn in written + extra:
            f.write('texture\t%s\t%s\t%s\n' % (mat, kind, fn))
    return written, WARNS
