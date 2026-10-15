"""SF Body Morphs character pipeline, Blender stage: before/after renders and texture bake.

    blender -b --factory-startup -P blend.py -- --out <morph dir> --vam <SfExport dir> [--no-bake] [--size 4096]

Reads <out>/_check/stage.npz written by sfpipe.py. Renders go to <out>/_check/*.png; baked textures to
<out>/tex_<material>_albedo.png and _normal.png (her UV layout, for SfMorph's loader).
"""
import os
import sys
import time

import bpy
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

ARGS = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []


def arg(name, default=None):
    return ARGS[ARGS.index(name) + 1] if name in ARGS else default


OUT = arg('--out')
VAMDIR = arg('--vam')
SIZE = int(arg('--size', '4096'))
CHK = os.path.join(OUT, '_check')
Z = np.load(os.path.join(CHK, 'stage.npz'))
NAMES = [str(n) for n in Z['names']]

# Her skin materials baked from VaM's skin (her material -> which of her meshes' submeshes carry it)
BAKE = ['reika_body', 'reika_face_a']
VAM_SKIN = {'Legs', 'Nostrils', 'Lips', 'Face', 'Toenails', 'Fingernails', 'Head', 'Hands', 'Shoulders', 'Neck',
            'Hips', 'Torso', 'Nipples', 'Forearms', 'Feet', 'Ears', 'defaultMat'}
SKIP_RENDER = ('uterus', 'rectum', 'Mouth', 'Teeth', 'tongue', 'Glow')


def log(s):
    print('[blend] ' + s, flush=True)


def u2b(v, dx=0.0):
    """Unity (left-handed, Y up) -> Blender (right-handed, Z up): (x, y, z) -> (x, z, y), a reflection that also
    turns Unity's clockwise front faces into Blender's counter-clockwise ones."""
    o = np.empty_like(v)
    o[:, 0] = v[:, 0] - dx; o[:, 1] = v[:, 2]; o[:, 2] = v[:, 1]  # cameras face her: +dx = screen right
    return o


def make_obj(name, v, f, uv=None, coll=None):
    me = bpy.data.meshes.new(name)
    me.vertices.add(len(v))
    me.vertices.foreach_set('co', v.astype(np.float32).ravel())
    me.loops.add(len(f) * 3)
    me.loops.foreach_set('vertex_index', f.astype(np.int32).ravel())
    me.polygons.add(len(f))
    me.polygons.foreach_set('loop_start', np.arange(0, len(f) * 3, 3, dtype=np.int32))
    me.polygons.foreach_set('loop_total', np.full(len(f), 3, np.int32))
    if uv is not None:
        lay = me.uv_layers.new(name='UVMap')
        lay.data.foreach_set('uv', uv[f.ravel()].astype(np.float32).ravel())
    me.update(calc_edges=True)
    me.validate()
    for p in me.polygons:
        p.use_smooth = True
    ob = bpy.data.objects.new(name, me)
    (coll or bpy.context.scene.collection).objects.link(ob)
    return ob


def clear():
    for ob in list(bpy.data.objects):
        bpy.data.objects.remove(ob, do_unlink=True)


def her(key, dx, skip=SKIP_RENDER):
    obs = []
    for n in NAMES:
        if any(s in n for s in skip):
            continue
        if key == 'bind':
            v = Z[n + '|vc']
        elif key == 'morph':
            v = Z[n + '|vc'] + Z[n + '|dc']
        else:
            v = Z[n + '|' + key]
        obs.append(make_obj(n + '_' + key, u2b(v, dx), Z[n + '|faces']))
    return obs


def vam(dx):
    g = [i for i, n in enumerate(Z['vam_groups']) if str(n) in VAM_SKIN]
    m = np.isin(Z['vam_g'], g)
    return make_obj('vam', u2b(Z['vam_v'], dx), Z['vam_f'][m][:, ::-1])  # VaM's winding is the other way round


def camera(name, loc, rot, scale):
    cam = bpy.data.cameras.new(name)
    cam.type = 'ORTHO'
    cam.ortho_scale = scale
    ob = bpy.data.objects.new(name, cam)
    bpy.context.scene.collection.objects.link(ob)
    ob.location = loc
    ob.rotation_euler = rot
    return ob


def render(path, cams, res=(2400, 1200)):
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'
    sc.display.shading.light = 'STUDIO'
    sc.display.shading.color_type = 'SINGLE'
    sc.display.shading.single_color = (0.8, 0.68, 0.6)
    sc.display.shading.show_cavity = True
    sc.render.resolution_x, sc.render.resolution_y = res
    sc.render.film_transparent = False
    sc.world = sc.world or bpy.data.worlds.new('w')
    for cname, cam in cams.items():
        sc.camera = cam
        sc.render.filepath = path.replace('.png', '_%s.png' % cname)
        bpy.ops.render.render(write_still=True)
        log('render ' + sc.render.filepath)


def renders():
    import math
    # posed (VaM's rest pose): her posed | VaM scaled to her | her fitted
    clear()
    her('vp', -1.6); vam(0.0); her('vp1', 1.6)
    cams = {'front': camera('cf', (0, 6, 0.85), (math.radians(90), 0, math.radians(180)), 5.0)}
    render(os.path.join(CHK, 'posed.png'), cams, (2400, 1000))
    # side by side in profile: her posed | VaM | her fitted (shifted along the view's horizontal axis)
    clear()
    for ob in her('vp', 0) + [vam(0)] + her('vp1', 0):
        pass
    for ob in bpy.data.objects:
        k = -0.5 if ob.name.endswith('_vp') else (0.5 if ob.name.endswith('_vp1') else 0.0)
        ob.location.y += k
    cams = {'side': camera('cs', (6, 0, 1.0), (math.radians(90), 0, math.radians(90)), 1.6)}
    render(os.path.join(CHK, 'posed.png'), cams, (1600, 1600))
    # bind pose (how it looks in the game): before | after, front and side
    clear()
    her('bind', -0.5); her('morph', 0.5)
    cams = {'front': camera('cf', (0, 6, 0.85), (math.radians(90), 0, math.radians(180)), 2.4),
            'side': camera('cs', (6, 0, 0.85), (math.radians(90), 0, math.radians(90)), 2.4)}
    render(os.path.join(CHK, 'bind.png'), cams, (1600, 1600))
    # faces close up: VaM | before | after
    clear()
    sk = SKIP_RENDER + ('hair', 'lash', 'Brow', 'tears')
    vam(-0.25); her('bind', 0.0, skip=sk); her('morph', 0.25, skip=sk)
    cams = {'front': camera('cf', (0, 6, 1.53), (math.radians(90), 0, math.radians(180)), 0.8),
            'side': camera('cs', (6, 0, 1.53), (math.radians(90), 0, math.radians(90)), 0.8)}
    for ob in bpy.data.objects:  # side view: spread along the view's horizontal axis instead
        pass
    render(os.path.join(CHK, 'face.png'), {'front': cams['front']}, (2000, 800))
    for ob in bpy.data.objects:
        if ob.type == 'MESH':
            dx = ob.data.vertices[0].co.x if False else 0
    clear()
    vam(0); her('bind', 0, skip=sk); her('morph', 0, skip=sk)
    for ob in bpy.data.objects:
        ob.location.y += -0.25 if ob.name == 'vam' else (0.25 if ob.name.endswith('_morph') else 0.0)
    render(os.path.join(CHK, 'face.png'), {'side': camera('cs', (6, 0, 1.53), (math.radians(90), 0, math.radians(90)), 0.8)}, (2000, 800))


def closeups():
    import math
    # bind pose before | after, close up on the parts the verification flags most often
    for name, centre, scale, side in (('chest', (0.105, 1.2), 0.3, True), ('feet', (0.12, 0.06), 0.35, True),
                                      ('hand', (0.45, 0.93), 0.3, False)):
        clear()
        her('bind', 0.0); her('morph', 0.0)
        for ob in bpy.data.objects:
            if ob.name.endswith('_morph'):
                ob.location.y -= 0.4  # move 'after' out of the way along depth, then render two cameras
        x, y = centre
        cams = {'before': camera('cb', (-x, 6, y), (math.radians(90), 0, math.radians(180)), scale)}
        if side:
            cams['beforeside'] = camera('cbs', (6, 0, y), (math.radians(90), 0, math.radians(90)), scale)
        render(os.path.join(CHK, 'close_%s.png' % name), cams, (900, 900))
        for ob in bpy.data.objects:
            if ob.type == 'MESH':
                ob.hide_render = not ob.name.endswith('_morph')
                if ob.name.endswith('_morph'):
                    ob.location.y += 0.4
        cams = {'after': camera('ca', (-x, 6, y), (math.radians(90), 0, math.radians(180)), scale)}
        if side:
            cams['afterside'] = camera('cas', (6, 0, y), (math.radians(90), 0, math.radians(90)), scale)
        render(os.path.join(CHK, 'close_%s.png' % name), cams, (900, 900))


def posetest():
    import math
    P = np.load(os.path.join(CHK, 'posetest.npz'))
    names = sorted({k.split('|')[0] for k in P.files})
    clear()
    for key, dx in (('orig', -0.9), ('nojoints', 0.0), ('joints', 0.9)):
        for n in names:
            make_obj(n + '_' + key, u2b(P[n + '|' + key], dx), P[n + '|faces'])
    cams = {'front': camera('cf', (0, 6, 0.9), (math.radians(90), 0, math.radians(180)), 3.0),
            'quarter': camera('cq', (4.2, 4.2, 1.0), (math.radians(90), 0, math.radians(135)), 3.6)}
    render(os.path.join(CHK, 'posetest.png'), cams, (2400, 1000))


def debug():
    import math
    clear()
    f = Z['dbg_wf']
    vam(-0.25)
    make_obj('x0', u2b(Z['dbg_x0'], 0.0), f)
    make_obj('xfit', u2b(Z['dbg_xfit'], 0.25), f)
    render(os.path.join(CHK, 'debug.png'), {'front': camera('cf', (0, 6, 1.5), (math.radians(90), 0, math.radians(180)), 0.8)}, (2000, 800))


if __name__ == '__main__':
    t0 = time.time()
    if '--posetest' in ARGS:
        posetest(); sys.exit(0)
    if '--debug' in ARGS:
        debug(); closeups(); sys.exit(0)
    renders()
    if '--no-bake' not in ARGS:
        import bake
        bake.run(Z, NAMES, OUT, VAMDIR, SIZE, make_obj, u2b, clear, log)
    log('done in %.0f s' % (time.time() - t0))
