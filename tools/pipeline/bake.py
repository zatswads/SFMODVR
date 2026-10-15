"""Texture bake (Blender/Cycles, selected to active): VaM's skin textures onto her UV layout.

Her fitted skin (posed into VaM's rest pose, lying on the VaM surface) is the bake target; the VaM body with its
own materials is the source. Albedo is baked as emission (exact colours, no lighting), normals in tangent space.
"""
import os

import bpy
import numpy as np

# her material -> (mesh, submesh material) pairs that carry it; uv offset for UVs outside 0..1
TARGETS = {
    'reika_body': ['reika_upperBody_normal', 'reika_lowerBody_normal', 'reika_foot', 'reika_neck',
                   'reika_nipple_type_1', 'reika_nail_normal', 'pussy_typeC_normal'],
    'reika_face_a': ['reika_face_a'],
    'reika_foot': ['reika_foot'],
}
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


def warn(log, msg):
    log('WARN: ' + msg)
    WARNS.append(msg)


def load_image(path, normal, log):
    """Load a VaM texture; None (with a WARN) when the file is missing or unreadable. Only _MainTex and _BumpMap are
    ever asked for: decal textures (_DecalTex) and the spec/gloss maps are ignored."""
    key = (path, normal)
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
        if normal:
            im = unswizzle(im, path, log)
    except Exception as e:  # noqa: BLE001
        BAD[os.path.basename(path)] = type(e).__name__
        log('  texture %s not usable (%s: %s)' % (os.path.basename(path), type(e).__name__, e))
        im = None
    _CACHE[key] = im
    return im


def unswizzle(im, path, log):
    im.colorspace_settings.name = 'Non-Color'
    w, h = im.size
    px = np.empty(w * h * 4, np.float32)
    im.pixels.foreach_get(px)
    px = px.reshape(-1, 4)
    s = px[::101]
    if s[:, 0].mean() > 0.95 and s[:, 2].mean() < 0.8:  # DXT5nm as read back from the GPU: X in alpha, Y in green
        x = px[:, 3] * 2 - 1; y = px[:, 1] * 2 - 1
        z = np.sqrt(np.clip(1 - x * x - y * y, 0, 1))
        px[:, 0] = px[:, 3]; px[:, 2] = z * 0.5 + 0.5; px[:, 3] = 1
        im.pixels.foreach_set(px.ravel())
        im.update()
        im.pack()  # keep the fixed pixels (otherwise Cycles reloads the file)
        log('  %s: unswizzled DXT5nm normal map' % os.path.basename(path))
    return im


def slot_image(vamdir, sl, slot, normal, log):
    fn = sl.get(slot)
    if not fn:
        return None
    return load_image(os.path.join(vamdir, fn), normal, log)


def skin_fill(vmats, vamdir, log, fill=(0.8, 0.62, 0.52, 1.0)):
    """Skin tone for pixels no ray reaches (inner folds): median of the first skin texture that loads (face, torso, ...)."""
    for g in ('Face', 'Torso', 'Head', 'Hips', 'Legs'):
        im = slot_image(vamdir, vmats.get(g, {}), '_MainTex', False, log)
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


def setup_cycles(log):
    sc = bpy.context.scene
    sc.render.engine = 'CYCLES'
    sc.cycles.samples = 1
    sc.cycles.use_denoising = False
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


def run(Z, names, out, vamdir, size, make_obj, u2b, clear, log):
    """Bake the textures. Returns (textures written, WARN messages). A texture that is missing or fails to load
    never stops the bake: that material falls back to the flat skin colour (_MainTex) or a flat normal (_BumpMap)."""
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
    albedo_mats, normal_mats = [], []
    have_normal = False
    flat, nobump = [], []
    for gi in keep:
        if not (vg == gi).any():
            continue
        g = groups[gi]
        sl = vmats.get(g, {})
        cimg = slot_image(vamdir, sl, '_MainTex', False, log)   # (_DecalTex and the spec/gloss maps are not used)
        nimg = slot_image(vamdir, sl, '_BumpMap', True, log)
        if cimg is None:
            flat.append(g)
            am = flat_emission('src_' + g, fill)
        else:
            am = emission_material('src_' + g, cimg)[0]
        have_normal |= nimg is not None
        if nimg is None and g in BUMP_EXPECTED:
            nobump.append(g)
        slot_of[gi] = len(albedo_mats)
        albedo_mats.append(am); normal_mats.append(normal_material('srcn_' + g, nimg))
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

    written = []
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
        res = size if mat != 'reika_foot' else size // 2
        for kind in ('albedo', 'normal'):
            if kind == 'normal' and not have_normal:
                continue
            tgt = None
            try:
                sel = [i for i, n in enumerate(used_meshes) if kind == 'albedo' or n not in NO_NORMAL]
                tgt = make_obj('tgt_' + mat, u2b(np.concatenate(vs)), np.concatenate([fs[i] for i in sel]), np.concatenate(uvs))
                img = bpy.data.images.new('%s_%s' % (mat, kind), res, res, alpha=False, float_buffer=False)
                img.generated_color = fill if kind == 'albedo' else (0.5, 0.5, 1.0, 1.0)
                if kind == 'normal':
                    img.colorspace_settings.name = 'Non-Color'
                tm = bpy.data.materials.new('tm_%s_%s' % (mat, kind))
                tm.use_nodes = True
                node = tm.node_tree.nodes.new('ShaderNodeTexImage'); node.image = img
                tm.node_tree.nodes.active = node
                tgt.data.materials.clear(); tgt.data.materials.append(tm)
                for i, (am, nm) in enumerate(zip(albedo_mats, normal_mats)):
                    src.data.materials[i] = am if kind == 'albedo' else nm
                bpy.ops.object.select_all(action='DESELECT')
                src.select_set(True); tgt.select_set(True)
                bpy.context.view_layer.objects.active = tgt
                sc = bpy.context.scene
                sc.render.bake.use_selected_to_active = True
                sc.render.bake.cage_extrusion = 0.012
                sc.render.bake.max_ray_distance = 0.05
                sc.render.bake.margin = 24
                sc.render.bake.margin_type = 'EXTEND'
                sc.render.bake.target = 'IMAGE_TEXTURES'
                if kind == 'albedo':
                    bpy.ops.object.bake(type='EMIT')
                    path = os.path.join(out, 'tex_%s_albedo.jpg' % mat)
                    sc.render.image_settings.file_format = 'JPEG'
                    sc.render.image_settings.quality = 93
                else:
                    sc.render.bake.normal_space = 'TANGENT'
                    bpy.ops.object.bake(type='NORMAL')
                    path = os.path.join(out, 'tex_%s_normal.png' % mat)
                    sc.render.image_settings.file_format = 'PNG'
                    sc.render.image_settings.color_depth = '8'
                sc.render.image_settings.color_mode = 'RGB'
                # pixels whose rays found no VaM skin come out pure black: give them the skin tone / a flat normal
                px = np.empty(res * res * 4, np.float32)
                img.pixels.foreach_get(px)
                px = px.reshape(-1, 4)
                miss = px[:, :3].max(1) < 0.004
                px[miss, :3] = fill[:3] if kind == 'albedo' else (0.5, 0.5, 1.0)
                img.pixels.foreach_set(px.ravel())
                log('  %s %s: %.2f%% of pixels had no hit (filled)' % (mat, kind, 100.0 * miss.mean()))
                img.save_render(path, scene=sc)
                written.append((mat, kind, os.path.basename(path)))
                log('baked %s %s (%dx%d) -> %s' % (mat, kind, res, res, path))
            except Exception as e:  # noqa: BLE001
                warn(log, 'bake of %s %s failed (%s: %s): her own texture stays' % (mat, kind, type(e).__name__, e))
            finally:
                if tgt is not None:
                    bpy.data.objects.remove(tgt, do_unlink=True)
    with open(os.path.join(out, 'character.txt'), 'a', encoding='utf-8', newline='\n') as f:
        for mat, kind, fn in written:
            f.write('texture\t%s\t%s\t%s\n' % (mat, kind, fn))
    return written, WARNS
