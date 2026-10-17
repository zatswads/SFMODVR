"""A VaM look (an SfExport folder: tools/vam/SfExport.cs) as a skinned FBX for tools/avatar/build.ps1 (avatar replacement mode).

    blender -b --factory-startup -P vam_avatar.py -- --vam <SfExport dir> --out <dir> [--name <Name>]

Writes <out>/<Name>.fbx (the Genesis 2 skeleton with DAZ's bone names, VaM's morphed mesh skinned to it, one material per VaM
surface), <out>/<Name>.fbx.materials.txt (DAZ Studio's format: surface, channel, image) and the images it names in
<out>/<Name>.images, plus <out>/<Name>.fbx.vam.txt (what was converted, warnings). build.ps1 takes that FBX unchanged.

- Weights: weights.txt (SfExport 2026-10-17 and later). VaM skins Genesis 2 with DAZ TriAx weights (a weight per rotation axis
  of each bone); a linear skin takes, per bone, the mean of its two bend axes (the twist axis, the last letter of VaM's
  rotation order, carries DAZ's twist gradient along the limb, which a linear skin without twist bones cannot use), 1 for
  the bone's fully weighted vertices, or the skin's general weights when it has them; normalised per vertex. Seam vertices
  copy their base vertex ('copy' lines). An export without weights.txt stops here (export the look again with SfExport).
- Frame: body.obj and the joints are in Unity's frame (metres, Y up, left-handed); Blender gets (-x, -z, y) and the FBX is
  written Y up with the space transform applied, so Unity gets VaM's numbers back with an identity root.
- Surfaces: VaM's material names are DAZ's Genesis 2 surface names; two are renamed so AvatarBundle's roles fit (InnerMouth
  -> Mouth, EyeReflection -> EyeMoisture); 'Hidden' and disabled surfaces are left out.
- Textures: _MainTex -> Base Color, _BumpMap -> Normal Map (VaM's DXT5nm-packed maps, R = 1 and x in alpha, unpacked to RGB),
  _GlossTex -> Specular Lobe 1 Roughness (1 - gloss), _AlphaTex -> Cutout Opacity.
"""
import os
import sys
import math

import bpy
import numpy as np

ARGS = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []


def arg(name, default=None):
    return ARGS[ARGS.index(name) + 1] if name in ARGS else default


vdir = arg('--vam')
out = arg('--out')
if not vdir or not out:
    raise SystemExit('usage: blender -b --factory-startup -P vam_avatar.py -- --vam <SfExport dir> --out <dir> [--name N]')


def info_name(d):
    p = os.path.join(d, 'info.txt')
    if os.path.exists(p):
        for l in open(p, encoding='utf-8'):
            t = l.rstrip('\n').split('\t', 1)
            if len(t) == 2 and t[0] == 'character' and t[1].strip():
                return t[1].strip()
    return os.path.basename(os.path.normpath(d))


name = arg('--name') or info_name(vdir)
safe = ''.join(c if c.isalnum() or c in ' -_.' else '_' for c in name).strip() or 'VaM look'
os.makedirs(out, exist_ok=True)
imgdir = os.path.join(out, safe + '.images')
os.makedirs(imgdir, exist_ok=True)
fbx = os.path.join(out, safe + '.fbx')
report = []


def say(s):
    report.append(s)
    print('[vam_avatar] ' + s, flush=True)


RENAME = {'InnerMouth': 'Mouth', 'EyeReflection': 'EyeMoisture'}
LEAVE_OUT = {'Hidden'}

# ---- read the export ------------------------------------------------------------------------------------------------------


def read_obj(path):
    v, vt, faces, fuv, fgroup, groups = [], [], [], [], [], []
    g = 0
    gi = {}
    with open(path, encoding='utf-8') as f:
        for l in f:
            if l.startswith('v '):
                v.append([float(x) for x in l.split()[1:4]])
            elif l.startswith('vt '):
                vt.append([float(x) for x in l.split()[1:3]])
            elif l.startswith('g '):
                n = l[2:].strip()
                if n not in gi:
                    gi[n] = len(groups); groups.append(n)
                g = gi[n]
            elif l.startswith('f '):
                ids, uvs = [], []
                for c in l.split()[1:]:
                    t = c.split('/')
                    ids.append(int(t[0]) - 1)
                    uvs.append(int(t[1]) - 1 if len(t) > 1 and t[1] else int(t[0]) - 1)
                faces.append(ids); fuv.append(uvs); fgroup.append(g)
    return np.array(v, float), np.array(vt, float) if vt else None, faces, fuv, fgroup, groups


def read_table(path):
    rows = []
    if os.path.exists(path):
        for l in open(path, encoding='utf-8'):
            if l.strip() and not l.startswith('#'):
                rows.append(l.rstrip('\n').split('\t'))
    return rows


V, VT, F, FUV, FG, GROUPS = read_obj(os.path.join(vdir, 'body.obj'))
nv = len(V)
say('%s: %d vertices, %d faces, %d surfaces' % (vdir, nv, len(F), len(GROUPS)))

# joints: skeleton.txt (rotation order, orientation) or joints.txt
joints = {}
for t in read_table(os.path.join(vdir, 'joints.txt')):
    joints[t[0]] = dict(parent=t[1], pos=np.array([float(x) for x in t[2].split()]))
order = {}
for t in read_table(os.path.join(vdir, 'skeleton.txt')):
    if t[0] in joints:
        order[t[0]] = t[2]
roots = [n for n, j in joints.items() if j['parent'] == '-' or j['parent'] not in joints]
main_root = 'hip' if 'hip' in joints else roots[0]


def descendants(n):
    kids = [k for k, j in joints.items() if j['parent'] == n]
    s = set(kids)
    for k in kids:
        s |= descendants(k)
    return s


# VaM gives the nipples no parent (its soft-body physics drive them): hang every stray root on its nearest joint
stray = [r for r in roots if r != main_root]
# VaM's nipple 'bones' are soft-body physics targets without a rest position (0 0 0): not part of the skeleton
for r in list(stray):
    if np.linalg.norm(joints[r]['pos']) < 1e-6 and not descendants(r):
        del joints[r]; stray.remove(r)
        say('bone %s has no rest position (a VaM physics target): left out' % r)
for r in stray:
    excl = set()
    for s2 in stray:
        excl |= descendants(s2) | {s2}
    best = min((k for k in joints if k not in excl), key=lambda k: np.linalg.norm(joints[k]['pos'] - joints[r]['pos']))
    joints[r]['parent'] = best
    say('bone %s had no parent: hung on %s (nearest joint)' % (r, best))

# materials
mats = {}
for t in read_table(os.path.join(vdir, 'materials.txt')):
    slots = dict(s.split('=', 1) for s in t[4:] if '=' in s)
    mats[t[1]] = dict(index=int(t[0]), enabled=t[2] == '1', slots=slots)

# ---- weights --------------------------------------------------------------------------------------------------------------
wpath = os.path.join(vdir, 'weights.txt')
if not os.path.exists(wpath):
    raise SystemExit('FAILED: %s has no weights.txt: export the look again with the current SfExport.cs (VaM, Person > '
                     'Plugins > SfExport > Export character for SF)' % vdir)
general = False
nodes = []          # [name, order, {i: (x, y, z)}, [full], {i: g}]
copies = []
with open(wpath, encoding='utf-8') as f:
    cur = None
    for l in f:
        if l.startswith('#') or not l.strip():
            continue
        t = l.split()
        k = t[0]
        if k == 'w':
            cur[2][int(t[1])] = (float(t[2]), float(t[3]), float(t[4]))
        elif k == 'full':
            cur[3].extend(int(x) for x in t[1:])
        elif k == 'g':
            cur[4][int(t[1])] = float(t[2])
        elif k == 'copy':
            copies.append((int(t[1]), int(t[2])))
        elif k == 'bone':
            cur = [t[1], t[2] if len(t) > 2 else 'XYZ', {}, [], {}]
            nodes.append(cur)
        elif k == 'general':
            general = t[1] == '1'
        elif k == 'vertices' and int(t[1]) != nv:
            raise SystemExit('FAILED: weights.txt is for %s vertices, body.obj has %d (not the same export?)' % (t[1], nv))

bone_names = [n[0] for n in nodes]
unknown = [b for b in bone_names if b not in joints]
W = np.zeros((nv, len(nodes)), np.float32)
use_general = general and any(n[4] for n in nodes)
if use_general:
    for bi, (bname, ro, tri, full, gen) in enumerate(nodes):
        for i, w in gen.items():
            if 0 <= i < nv:
                W[i, bi] += w
else:
    # VaM's TriAx weights are cumulative down the skeleton (DAZSkinV2.WalkBonesAndAccumulateWeights adds every child's weights into
    # its parent, and VaM skins the bones as a chain): the collar carries 1 along the whole arm, the chest 1 at the shoulder. A
    # linear skin needs each bone's own share: its weight minus its children's, per axis.
    C = np.zeros((len(nodes), nv, 3), np.float32)
    for bi, (bname, ro, tri, full, gen) in enumerate(nodes):
        if tri:
            ids = np.array([i for i in tri if 0 <= i < nv], np.int64)
            C[bi, ids] = np.array([tri[i] for i in ids], np.float32)
        fl = [i for i in full if 0 <= i < nv]
        C[bi, fl] = 1.0
    ni = {b: k for k, b in enumerate(bone_names)}
    for bi, (bname, ro, tri, full, gen) in enumerate(nodes):
        own = C[bi].copy()
        for k, j in joints.items():
            if j['parent'] == bname and k in ni:
                own -= C[ni[k]]
        own = np.clip(own, 0.0, 1.0)
        # VaM's rotation order lists DAZ's reversed (shoulder ZYX, shin XZY): the twist axis, along the bone, is the last letter
        # (checked on a VaM export: the forearm's X / the shin's Y weights are the lowest and run along the bone)
        twist = 'XYZ'.index(ro[-1]) if ro and ro[-1] in 'XYZ' else -1
        W[:, bi] = (own.sum(1) - own[:, twist]) / 2.0 if twist >= 0 else own.sum(1) / 3.0
    del C
for a, b in copies:
    if 0 <= a < nv and 0 <= b < nv:
        W[b] = W[a]
# bones the skeleton lacks: their weight goes to the nearest joint (none expected: VaM's nodes are its bones)
jn = list(joints)
JP = np.array([joints[n]['pos'] for n in jn])
for bi, b in enumerate(bone_names):
    if b in joints or not W[:, bi].any():
        continue
    idx = np.nonzero(W[:, bi])[0]
    c = V[idx].mean(0)
    tgt = jn[int(np.argmin(np.linalg.norm(JP - c, axis=1)))]
    say('weights of bone %s (not in the skeleton, %d vertices) moved to %s' % (b, len(idx), tgt))
    if tgt not in bone_names:
        bone_names.append(tgt); W = np.hstack([W, np.zeros((nv, 1), np.float32)])
    W[:, bone_names.index(tgt)] += W[:, bi]; W[:, bi] = 0
tot = W.sum(1)
none = np.nonzero(tot <= 1e-6)[0]
if len(none):
    # unweighted vertices take their nearest weighted vertex's weights
    ok = np.nonzero(tot > 1e-6)[0]
    from math import inf
    for i in none:
        j = ok[int(np.argmin(np.linalg.norm(V[ok] - V[i], axis=1)))]
        W[i] = W[j]
    say('%d unweighted vertices took their nearest weighted neighbour' % len(none))
W /= np.maximum(W.sum(1), 1e-9)[:, None]
say('weights: %d bones, %s; %d seam copies' % (len(nodes), 'general weights' if use_general else 'TriAx, cumulative -> own share per bone, mean of the bend axes', len(copies)))
# more than 4 bones per vertex: Unity keeps the strongest 4
over = int((np.count_nonzero(W > 1e-4, axis=1) > 4).sum())
if over:
    say('%d vertices have more than 4 bones (Unity keeps the strongest 4)' % over)

# ---- textures ---------------------------------------------------------------------------------------------------------------
done_img = {}


def img_load(fn):
    p = os.path.join(vdir, fn)
    if not os.path.isfile(p):
        return None
    im = bpy.data.images.load(p)
    im.colorspace_settings.name = 'Non-Color'
    return im


def short(fn, suffix=''):
    """tex_07_<long VaM texture path>.png -> tex_07<suffix>.png (SfExport numbers its textures; the long names hit Windows'
    260-character path limit once the avatar folders are added in front)"""
    stem = os.path.splitext(fn)[0]
    t = stem.split('_')
    base = '_'.join(t[:2]) if len(t) >= 2 and t[0] == 'tex' else stem[:40]
    return base + suffix + os.path.splitext(fn)[1].lower()


def save_px(src, px, name):
    w, h = src.size
    dst = bpy.data.images.new(os.path.splitext(name)[0], w, h, alpha=True, float_buffer=False)
    dst.colorspace_settings.name = 'Non-Color'
    dst.pixels.foreach_set(px.ravel())
    path = os.path.join(imgdir, os.path.splitext(name)[0] + '.png')
    dst.filepath_raw = path
    dst.file_format = 'PNG'
    dst.save()
    bpy.data.images.remove(dst)
    return path


def tex(fn, kind):
    """The image for a channel, converted where needed; its path in <out>/<Name>.images."""
    key = (fn, kind)
    if key in done_img:
        return done_img[key]
    path = None
    src_path = os.path.join(vdir, fn)
    if not os.path.isfile(src_path):
        done_img[key] = None
        return None
    if kind in ('albedo', 'opacity'):
        path = os.path.join(imgdir, short(fn))
        if not os.path.exists(path):
            import shutil
            shutil.copyfile(src_path, path)
    else:
        im = img_load(fn)
        w, h = im.size
        px = np.empty(w * h * 4, np.float32)
        im.pixels.foreach_get(px)
        px = px.reshape(-1, 4)
        if kind == 'normal':
            # Unity's DXT5nm packing (VaM's imported normal maps): R = 1, x in alpha, y in green
            if px[:, 0].mean() > 0.98 and px[:, 0].std() < 0.02 and px[:, 3].std() > 1e-3:
                x = px[:, 3] * 2 - 1; y = px[:, 1] * 2 - 1
                z = np.sqrt(np.clip(1 - x * x - y * y, 0, 1))
                px = np.stack([x * 0.5 + 0.5, y * 0.5 + 0.5, z * 0.5 + 0.5, np.ones_like(x)], 1)
                path = save_px(im, px, short(fn, '_rgb'))
                say('normal map %s: unpacked from DXT5nm' % fn)
            else:
                import shutil
                path = os.path.join(imgdir, short(fn))
                if not os.path.exists(path):
                    shutil.copyfile(src_path, path)
        elif kind == 'roughness':
            g = px[:, 0]
            r = 1 - g
            px = np.stack([r, r, r, np.ones_like(r)], 1)
            path = save_px(im, px, short(fn, '_rough'))
        bpy.data.images.remove(im)
    done_img[key] = path
    return path


# ---- build the scene -------------------------------------------------------------------------------------------------------
for o in list(bpy.data.objects):
    bpy.data.objects.remove(o, do_unlink=True)


def to_b(p):
    p = np.asarray(p, float)
    return np.stack([-p[..., 0], -p[..., 2], p[..., 1]], -1)


ad = bpy.data.armatures.new('Genesis2Female')
arm = bpy.data.objects.new('Genesis2Female', ad)
bpy.context.scene.collection.objects.link(arm)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode='EDIT')
kids = {}
for n, j in joints.items():
    kids.setdefault(j['parent'], []).append(n)
# the child a bone points at: the one along the chain (fingers, spine), else the farthest (the hand -> its carpals' middle)
PREFER = ('abdomen', 'chest', 'neck', 'head', 'Shldr', 'ForeArm', 'Hand', 'Shin', 'Foot', 'Toe', 'Mid1', 'Carpal1')
eb = {}
for n, j in joints.items():
    b = ad.edit_bones.new(n)
    h = to_b(j['pos'])
    c = kids.get(n, [])
    tgt = None
    for p in PREFER:
        for k in c:
            if k.endswith(p) or k == p:
                tgt = k; break
        if tgt:
            break
    if tgt is None and len(c) == 1:
        tgt = c[0]
    if tgt is not None and np.linalg.norm(to_b(joints[tgt]['pos']) - h) > 1e-3:
        t = to_b(joints[tgt]['pos'])
    else:
        pa = j['parent']
        d = h - to_b(joints[pa]['pos']) if pa in joints else np.array([0, 0, 1.0])
        d = d / max(np.linalg.norm(d), 1e-6)
        t = h + d * 0.03
    b.head = h; b.tail = t
    eb[n] = b
for n, j in joints.items():
    if j['parent'] in eb:
        eb[n].parent = eb[j['parent']]
bpy.ops.object.mode_set(mode='OBJECT')

# mesh: polygons as VaM has them (quads and triangles), surfaces left out where VaM hides them
keep = []
for fi, g in enumerate(FG):
    gname = GROUPS[g]
    m = mats.get(gname)
    if gname in LEAVE_OUT or (m is not None and not m['enabled']):
        continue
    keep.append(fi)
used = sorted({i for fi in keep for i in F[fi]})
remap = -np.ones(nv, np.int64); remap[used] = np.arange(len(used))
say('surfaces left out: %s' % (', '.join(sorted({GROUPS[FG[fi]] for fi in range(len(F))} - {GROUPS[FG[fi]] for fi in keep})) or 'none'))

me = bpy.data.meshes.new('Genesis2Female_body')
VB = to_b(V[used])
loops = []
loop_uv = []
starts = []
totals = []
for fi in keep:
    ids = F[fi][::-1]               # the frame change mirrors: reverse the winding to keep the faces outward
    uvs = FUV[fi][::-1]
    starts.append(len(loops)); totals.append(len(ids))
    loops.extend(remap[ids]); loop_uv.extend(uvs)
me.vertices.add(len(VB)); me.vertices.foreach_set('co', VB.astype(np.float32).ravel())
me.loops.add(len(loops)); me.loops.foreach_set('vertex_index', np.array(loops, np.int32))
me.polygons.add(len(keep))
me.polygons.foreach_set('loop_start', np.array(starts, np.int32)); me.polygons.foreach_set('loop_total', np.array(totals, np.int32))
if VT is not None:
    uvl = me.uv_layers.new(name='UVMap')
    uvl.data.foreach_set('uv', VT[np.array(loop_uv)].astype(np.float32).ravel())
me.update(calc_edges=True)
me.validate()
me.polygons.foreach_set('use_smooth', np.ones(len(keep), bool))

# materials, one per kept surface, with the textures as image nodes (for the FBX) and in materials.txt (for build.ps1)
surf_of_group = {}
mat_lines = ['# surface\tchannel\timage (tools/avatar/vam_avatar.py from %s)' % vdir]
kept_groups = []
for fi in keep:
    g = FG[fi]
    if g not in surf_of_group:
        surf_of_group[g] = len(kept_groups); kept_groups.append(g)
for g in kept_groups:
    vname = GROUPS[g]
    sname = RENAME.get(vname, vname)
    m = bpy.data.materials.new(sname)
    if not m.node_tree:
        m.use_nodes = True
    nt = m.node_tree; bsdf = nt.nodes.get('Principled BSDF')
    sl = mats.get(vname, {}).get('slots', {})
    for slot, kind, chan in (('_MainTex', 'albedo', 'Base Color'), ('_BumpMap', 'normal', 'Normal Map'),
                             ('_GlossTex', 'roughness', 'Specular Lobe 1 Roughness'), ('_AlphaTex', 'opacity', 'Cutout Opacity')):
        fn = sl.get(slot)
        if not fn:
            continue
        p = tex(fn, kind)
        if not p:
            say('surface %s: %s %s missing' % (vname, slot, fn))
            continue
        mat_lines.append('%s\t%s\t%s' % (sname, chan, p))
        if kind in ('albedo', 'normal'):
            tx = nt.nodes.new('ShaderNodeTexImage')
            tx.image = bpy.data.images.load(p, check_existing=True)
            if kind == 'normal':
                tx.image.colorspace_settings.name = 'Non-Color'
                nm = nt.nodes.new('ShaderNodeNormalMap'); nt.links.new(tx.outputs['Color'], nm.inputs['Color'])
                nt.links.new(nm.outputs['Normal'], bsdf.inputs['Normal'])
            else:
                nt.links.new(tx.outputs['Color'], bsdf.inputs['Base Color'])
    if vname == 'Eyelashes':
        bsdf.inputs['Base Color'].default_value = (0.02, 0.015, 0.012, 1)   # lashes: the constant colour AvatarBundle takes
    me.materials.append(m)
me.polygons.foreach_set('material_index', np.array([surf_of_group[FG[fi]] for fi in keep], np.int32))

body = bpy.data.objects.new('Genesis2Female_body', me)
bpy.context.scene.collection.objects.link(body)
body.parent = arm
mod = body.modifiers.new('Armature', 'ARMATURE'); mod.object = arm
# vertex groups
Wk = W[used]
for bi, b in enumerate(bone_names):
    if b not in joints:
        continue
    col = Wk[:, bi]
    nz = np.nonzero(col > 1e-4)[0]
    if not len(nz):
        continue
    vg = body.vertex_groups.new(name=b)
    for w in np.unique(np.round(col[nz], 4)):
        ids = nz[np.round(col[nz], 4) == w]
        vg.add(ids.tolist(), float(w), 'REPLACE')

# ---- check: a test bend skins sensibly (no vertex flies off) ----------------------------------------------------------------
dep = bpy.context.evaluated_depsgraph_get()
pb = arm.pose.bones
test = [('lForeArm', 70), ('rShin', -80), ('head', 30), ('lThigh', -60)]
for bn, ang in test:
    if bn in pb:
        pb[bn].rotation_mode = 'XYZ'; pb[bn].rotation_euler = (math.radians(ang), 0, 0)
bpy.context.view_layer.update()
ev = body.evaluated_get(bpy.context.evaluated_depsgraph_get())
pm = np.empty(len(ev.data.vertices) * 3, np.float32); ev.data.vertices.foreach_get('co', pm)
pm = pm.reshape(-1, 3)
# edge stretch under the test pose
ed = np.empty(len(me.edges) * 2, np.int32); me.edges.foreach_get('vertices', ed); ed = ed.reshape(-1, 2)
l0 = np.linalg.norm(VB[ed[:, 0]] - VB[ed[:, 1]], axis=1); l1 = np.linalg.norm(pm[ed[:, 0]] - pm[ed[:, 1]], axis=1)
ratio = l1 / np.maximum(l0, 1e-6)
say('test bend (elbow 70, knee 80, head 30, hip 60 deg): edge stretch x%.2f-%.2f (0.1/99.9 %%), max x%.2f, %d edges over x3'
    % (np.percentile(ratio, 0.1), np.percentile(ratio, 99.9), ratio.max(), int((ratio > 3).sum())))
for bn, _ in test:
    if bn in pb:
        pb[bn].rotation_euler = (0, 0, 0)
bpy.context.view_layer.update()

# ---- write -------------------------------------------------------------------------------------------------------------------
bpy.ops.object.select_all(action='DESELECT')
arm.select_set(True); body.select_set(True)
bpy.ops.export_scene.fbx(filepath=fbx, use_selection=True, path_mode='ABSOLUTE', embed_textures=False, add_leaf_bones=False,
                         bake_anim=False, axis_forward='-Z', axis_up='Y', bake_space_transform=True,
                         apply_scale_options='FBX_SCALE_UNITS', mesh_smooth_type='FACE', use_armature_deform_only=False,
                         primary_bone_axis='Y', secondary_bone_axis='X')
with open(fbx + '.materials.txt', 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(mat_lines) + '\n')
h = V[:, 1].max() - V[:, 1].min()
say('wrote %s: %d bones, %d vertices, %d surfaces, %d images, %.3f m tall' % (fbx, len(joints), len(VB), len(kept_groups),
                                                                             len({p for p in done_img.values() if p}), h))
with open(fbx + '.vam.txt', 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(report) + '\n')
