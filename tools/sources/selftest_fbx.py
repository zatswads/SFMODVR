"""Self-test input for humanoid.py: an SfExport folder (VaM, Genesis 2: DAZ bone names) packed as an FBX the way a DAZ /
Unity export looks (armature with the joints, the mesh parented with an Armature modifier, a material per VaM group with
its _MainTex / _BumpMap images), so humanoid.py's import, frame, bone map, roles and textures can be compared with the
original folder.

    blender -b --factory-startup -P selftest_fbx.py -- --vam <SfExport dir> --out <file.fbx>
"""
import os
import sys

import bpy
import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'pipeline'))
import sfio  # noqa: E402

ARGS = sys.argv[sys.argv.index('--') + 1:]
vdir = ARGS[ARGS.index('--vam') + 1]
out = ARGS[ARGS.index('--out') + 1]


def to_blender(p):
    """Unity frame (-x, z, -y of Blender) back to Blender: (x, y, z)_b = (-ux, -uz, uy)."""
    p = np.asarray(p, float)
    return np.stack([-p[..., 0], -p[..., 2], p[..., 1]], -1)


for o in list(bpy.data.objects):
    bpy.data.objects.remove(o, do_unlink=True)
vam = sfio.read_vam_export(vdir)
# armature: a bone per joint, head at the joint, tail towards its first child (or 3 cm up)
ad = bpy.data.armatures.new('Genesis2Female')
arm = bpy.data.objects.new('Genesis2Female', ad)
bpy.context.scene.collection.objects.link(arm)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode='EDIT')
kids = {}
for n, j in vam.joints.items():
    kids.setdefault(j['parent'], []).append(n)
eb = {}
for n, j in vam.joints.items():
    b = ad.edit_bones.new(n)
    h = to_blender(j['pos'])
    c = kids.get(n)
    t = to_blender(vam.joints[c[0]]['pos']) if c else h + np.array([0, 0, 0.03])
    if np.linalg.norm(t - h) < 1e-4:
        t = h + np.array([0, 0, 0.03])
    b.head = h; b.tail = t
    eb[n] = b
for n, j in vam.joints.items():
    if j['parent'] in eb:
        eb[n].parent = eb[j['parent']]
bpy.ops.object.mode_set(mode='OBJECT')
# mesh: VaM's body with a material per group (fan triangles already in sfio)
me = bpy.data.meshes.new('Genesis2Female_body')
V = to_blender(vam.v)
F = vam.faces[:, ::-1]   # the frame change mirrors: reverse the winding to keep the faces outward
me.vertices.add(len(V)); me.vertices.foreach_set('co', V.astype(np.float32).ravel())
me.loops.add(len(F) * 3); me.loops.foreach_set('vertex_index', F.astype(np.int32).ravel())
me.polygons.add(len(F)); me.polygons.foreach_set('loop_start', np.arange(0, len(F) * 3, 3, dtype=np.int32))
me.polygons.foreach_set('loop_total', np.full(len(F), 3, np.int32))
uv = me.uv_layers.new(name='UVMap'); uv.data.foreach_set('uv', vam.uv[F.ravel()].astype(np.float32).ravel())
me.update(calc_edges=True); me.validate()
for p in me.polygons:
    p.use_smooth = True
imgs = {}
for gi, g in enumerate(vam.groups):
    m = bpy.data.materials.new(g); m.use_nodes = True
    nt = m.node_tree; bsdf = nt.nodes.get('Principled BSDF')
    sl = vam.materials.get(g, {}).get('slots', {})
    for slot, sock in (('_MainTex', 'Base Color'), ('_BumpMap', 'Normal')):
        fn = sl.get(slot)
        if not fn or not os.path.isfile(os.path.join(vdir, fn)):
            continue
        key = fn
        if key not in imgs:
            imgs[key] = bpy.data.images.load(os.path.join(vdir, fn))
        tx = nt.nodes.new('ShaderNodeTexImage'); tx.image = imgs[key]
        if sock == 'Normal':
            tx.image.colorspace_settings.name = 'Non-Color'
            nm = nt.nodes.new('ShaderNodeNormalMap'); nt.links.new(tx.outputs['Color'], nm.inputs['Color'])
            nt.links.new(nm.outputs['Normal'], bsdf.inputs['Normal'])
        else:
            nt.links.new(tx.outputs['Color'], bsdf.inputs['Base Color'])
    me.materials.append(m)
me.polygons.foreach_set('material_index', vam.face_group.astype(np.int32))
body = bpy.data.objects.new('Genesis2Female_body', me)
bpy.context.scene.collection.objects.link(body)
body.parent = arm
mod = body.modifiers.new('Armature', 'ARMATURE'); mod.object = arm
bpy.ops.object.select_all(action='SELECT')
bpy.ops.export_scene.fbx(filepath=out, use_selection=True, path_mode='COPY', embed_textures=False, add_leaf_bones=False,
                         bake_anim=False)
print('[selftest] wrote %s: %d joints, %d vertices, %d groups' % (out, len(vam.joints), len(V), len(vam.groups)), flush=True)
