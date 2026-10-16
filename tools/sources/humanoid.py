"""Any rigged humanoid file -> an SfExport folder (what the VaM plugin writes), so the character pipeline takes it.

    blender -b --factory-startup -P humanoid.py -- --in <file.fbx|.glb|.gltf|.vrm|.blend> --out <folder> [--name Name]
            [--mesh <object name>] [--rig auto|g2|g8|g9|mixamo|unity|vrm|ue|cc|as] [--list]

Design: docs/SOURCES.md. Steps:
  1. import the file (FBX: DAZ Studio, Unity / VRChat avatars, Mixamo, Character Creator, Unreal; glTF / GLB; VRM with
     the VRM add-on; .blend), take the armature with the most bones and the largest mesh skinned to it (--mesh to pick);
  2. map its bones to a canonical humanoid set (tables below, picked by which names are present; DAZ Genesis 2/3/8/9,
     Mixamo, Unity Humanoid / VRChat, VRM, Unreal, Character Creator, AdvancedSkeleton) and write them as the Genesis 2
     joints tools/pipeline/rig.py reads (joints.txt);
  3. convert to the pipeline's frame (Unity: Y up, metres, the figure facing +Z with its left at -x);
  4. give every face a body role (the Genesis 2 group names the fit needs: Torso, Hips, Neck, Head, Face, Shoulders,
     Forearms, Hands, Legs, Feet; Lips, Ears, Sclera, InnerMouth, nails, eye parts by material name; Nipples from the chest
     geometry) by its nearest bone segment, and write it as the group 'Role@material' (the fit reads the role, the bake the
     material's textures);
  5. copy each material's base colour / normal map images (Principled BSDF) as tex_*.png and list them in materials.txt
     (_MainTex, _BumpMap); body.obj with one vertex per (position, UV) pair, normals; info.txt; source.txt (the report).
Only the joints and the skin surface are used downstream, so rig weights need not be good: an unrigged mesh can be rigged
quickly first (docs/SOURCES.md: ActionForge, Mixamo, AccuRIG, Rigify).
"""
import math
import os
import re
import shutil
import sys

import bpy
import numpy as np

ARGS = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []


def arg(name, default=None):
    return ARGS[ARGS.index(name) + 1] if name in ARGS else default


def log(s):
    print('[humanoid] ' + s, flush=True)


# ---- bone tables: canonical key -> candidate names ('%s' = side token of that rig) -------------------------------
CANON = ['hips', 'spine', 'chest', 'neck', 'head', 'eye', 'collar', 'upperarm', 'forearm', 'hand',
         'thumb1', 'thumb2', 'thumb3', 'index1', 'index2', 'index3', 'middle1', 'middle2', 'middle3',
         'ring1', 'ring2', 'ring3', 'pinky1', 'pinky2', 'pinky3', 'thigh', 'shin', 'foot', 'toe']
SIDED = set(CANON[5:])

# per rig: side tokens (left, right) and per key the candidate names (first present wins)
RIGS = {
    'g2': (('l', 'r'), {   # DAZ Genesis 2 (VaM)
        'hips': ['hip'], 'spine': ['abdomen2'], 'chest': ['chest'], 'neck': ['neck'], 'head': ['head'], 'eye': ['%sEye'],
        'collar': ['%sCollar'], 'upperarm': ['%sShldr'], 'forearm': ['%sForeArm'], 'hand': ['%sHand'],
        'thumb1': ['%sThumb1'], 'thumb2': ['%sThumb2'], 'thumb3': ['%sThumb3'], 'index1': ['%sIndex1'], 'index2': ['%sIndex2'],
        'index3': ['%sIndex3'], 'middle1': ['%sMid1'], 'middle2': ['%sMid2'], 'middle3': ['%sMid3'], 'ring1': ['%sRing1'],
        'ring2': ['%sRing2'], 'ring3': ['%sRing3'], 'pinky1': ['%sPinky1'], 'pinky2': ['%sPinky2'], 'pinky3': ['%sPinky3'],
        'thigh': ['%sThigh'], 'shin': ['%sShin'], 'foot': ['%sFoot'], 'toe': ['%sToe']}),
    'g8': (('l', 'r'), {   # DAZ Genesis 3 / 8 / 8.1 (bend / twist bones: the bend bone carries the joint)
        'hips': ['hip'], 'spine': ['abdomenUpper', 'abdomen2'], 'chest': ['chestUpper', 'chestLower', 'chest'],
        'neck': ['neckLower', 'neck'], 'head': ['head'], 'eye': ['%sEye'], 'collar': ['%sCollar'],
        'upperarm': ['%sShldrBend', '%sShldr'], 'forearm': ['%sForearmBend', '%sForeArm'], 'hand': ['%sHand'],
        'thumb1': ['%sThumb1'], 'thumb2': ['%sThumb2'], 'thumb3': ['%sThumb3'], 'index1': ['%sIndex1'], 'index2': ['%sIndex2'],
        'index3': ['%sIndex3'], 'middle1': ['%sMid1'], 'middle2': ['%sMid2'], 'middle3': ['%sMid3'], 'ring1': ['%sRing1'],
        'ring2': ['%sRing2'], 'ring3': ['%sRing3'], 'pinky1': ['%sPinky1'], 'pinky2': ['%sPinky2'], 'pinky3': ['%sPinky3'],
        'thigh': ['%sThighBend', '%sThigh'], 'shin': ['%sShin'], 'foot': ['%sFoot'], 'toe': ['%sToe']}),
    'g9': (('l_', 'r_'), {   # DAZ Genesis 9
        'hips': ['hip'], 'spine': ['spine2', 'spine1'], 'chest': ['spine4', 'spine3'], 'neck': ['neck1'], 'head': ['head'],
        'eye': ['%seye'], 'collar': ['%sshoulder'], 'upperarm': ['%supperarm'], 'forearm': ['%sforearm'], 'hand': ['%shand'],
        'thumb1': ['%sthumb1'], 'thumb2': ['%sthumb2'], 'thumb3': ['%sthumb3'], 'index1': ['%sindex1'], 'index2': ['%sindex2'],
        'index3': ['%sindex3'], 'middle1': ['%smid1'], 'middle2': ['%smid2'], 'middle3': ['%smid3'], 'ring1': ['%sring1'],
        'ring2': ['%sring2'], 'ring3': ['%sring3'], 'pinky1': ['%spinky1'], 'pinky2': ['%spinky2'], 'pinky3': ['%spinky3'],
        'thigh': ['%sthigh'], 'shin': ['%sshin'], 'foot': ['%sfoot'], 'toe': ['%stoes', '%stoe']}),
    'mixamo': (('Left', 'Right'), {
        'hips': ['Hips'], 'spine': ['Spine1', 'Spine'], 'chest': ['Spine2'], 'neck': ['Neck'], 'head': ['Head'], 'eye': ['%sEye'],
        'collar': ['%sShoulder'], 'upperarm': ['%sArm'], 'forearm': ['%sForeArm'], 'hand': ['%sHand'],
        'thumb1': ['%sHandThumb1'], 'thumb2': ['%sHandThumb2'], 'thumb3': ['%sHandThumb3'], 'index1': ['%sHandIndex1'],
        'index2': ['%sHandIndex2'], 'index3': ['%sHandIndex3'], 'middle1': ['%sHandMiddle1'], 'middle2': ['%sHandMiddle2'],
        'middle3': ['%sHandMiddle3'], 'ring1': ['%sHandRing1'], 'ring2': ['%sHandRing2'], 'ring3': ['%sHandRing3'],
        'pinky1': ['%sHandPinky1'], 'pinky2': ['%sHandPinky2'], 'pinky3': ['%sHandPinky3'],
        'thigh': ['%sUpLeg'], 'shin': ['%sLeg'], 'foot': ['%sFoot'], 'toe': ['%sToeBase']}),
    'unity': (('Left', 'Right'), {   # Unity Humanoid names (VRChat avatars, many FBX), also with '.L' / '_L' spellings below
        'hips': ['Hips'], 'spine': ['Spine'], 'chest': ['UpperChest', 'Chest'], 'neck': ['Neck'], 'head': ['Head'],
        'eye': ['%sEye', 'Eye_%s'], 'collar': ['%sShoulder', 'Shoulder_%s'], 'upperarm': ['%sUpperArm', 'Upper_arm_%s', 'UpperArm_%s'],
        'forearm': ['%sLowerArm', 'Lower_arm_%s', 'LowerArm_%s'], 'hand': ['%sHand', 'Hand_%s'],
        'thumb1': ['%sThumbProximal', 'Thumb1_%s'], 'thumb2': ['%sThumbIntermediate', 'Thumb2_%s'], 'thumb3': ['%sThumbDistal', 'Thumb3_%s'],
        'index1': ['%sIndexProximal', 'IndexFinger1_%s'], 'index2': ['%sIndexIntermediate', 'IndexFinger2_%s'], 'index3': ['%sIndexDistal', 'IndexFinger3_%s'],
        'middle1': ['%sMiddleProximal', 'MiddleFinger1_%s'], 'middle2': ['%sMiddleIntermediate', 'MiddleFinger2_%s'], 'middle3': ['%sMiddleDistal', 'MiddleFinger3_%s'],
        'ring1': ['%sRingProximal', 'RingFinger1_%s'], 'ring2': ['%sRingIntermediate', 'RingFinger2_%s'], 'ring3': ['%sRingDistal', 'RingFinger3_%s'],
        'pinky1': ['%sLittleProximal', 'LittleFinger1_%s'], 'pinky2': ['%sLittleIntermediate', 'LittleFinger2_%s'], 'pinky3': ['%sLittleDistal', 'LittleFinger3_%s'],
        'thigh': ['%sUpperLeg', 'Upper_leg_%s', 'UpperLeg_%s'], 'shin': ['%sLowerLeg', 'Lower_leg_%s', 'LowerLeg_%s'],
        'foot': ['%sFoot', 'Foot_%s'], 'toe': ['%sToes', 'Toe_%s', 'Toes_%s']}),
    'vrm': (('L', 'R'), {
        'hips': ['J_Bip_C_Hips'], 'spine': ['J_Bip_C_Spine'], 'chest': ['J_Bip_C_UpperChest', 'J_Bip_C_Chest'], 'neck': ['J_Bip_C_Neck'],
        'head': ['J_Bip_C_Head'], 'eye': ['J_Adj_%s_FaceEye'], 'collar': ['J_Bip_%s_Shoulder'], 'upperarm': ['J_Bip_%s_UpperArm'],
        'forearm': ['J_Bip_%s_LowerArm'], 'hand': ['J_Bip_%s_Hand'], 'thumb1': ['J_Bip_%s_Thumb1'], 'thumb2': ['J_Bip_%s_Thumb2'],
        'thumb3': ['J_Bip_%s_Thumb3'], 'index1': ['J_Bip_%s_Index1'], 'index2': ['J_Bip_%s_Index2'], 'index3': ['J_Bip_%s_Index3'],
        'middle1': ['J_Bip_%s_Middle1'], 'middle2': ['J_Bip_%s_Middle2'], 'middle3': ['J_Bip_%s_Middle3'], 'ring1': ['J_Bip_%s_Ring1'],
        'ring2': ['J_Bip_%s_Ring2'], 'ring3': ['J_Bip_%s_Ring3'], 'pinky1': ['J_Bip_%s_Little1'], 'pinky2': ['J_Bip_%s_Little2'],
        'pinky3': ['J_Bip_%s_Little3'], 'thigh': ['J_Bip_%s_UpperLeg'], 'shin': ['J_Bip_%s_LowerLeg'], 'foot': ['J_Bip_%s_Foot'],
        'toe': ['J_Bip_%s_ToeBase']}),
    'ue': (('_l', '_r'), {   # Unreal mannequin: side as a suffix
        'hips': ['pelvis'], 'spine': ['spine_02', 'spine_01'], 'chest': ['spine_05', 'spine_04', 'spine_03'], 'neck': ['neck_01'],
        'head': ['head'], 'eye': ['FACIAL%s_Eye', 'eye%s'], 'collar': ['clavicle%s'], 'upperarm': ['upperarm%s'], 'forearm': ['lowerarm%s'],
        'hand': ['hand%s'], 'thumb1': ['thumb_01%s'], 'thumb2': ['thumb_02%s'], 'thumb3': ['thumb_03%s'], 'index1': ['index_01%s'],
        'index2': ['index_02%s'], 'index3': ['index_03%s'], 'middle1': ['middle_01%s'], 'middle2': ['middle_02%s'],
        'middle3': ['middle_03%s'], 'ring1': ['ring_01%s'], 'ring2': ['ring_02%s'], 'ring3': ['ring_03%s'], 'pinky1': ['pinky_01%s'],
        'pinky2': ['pinky_02%s'], 'pinky3': ['pinky_03%s'], 'thigh': ['thigh%s'], 'shin': ['calf%s'], 'foot': ['foot%s'], 'toe': ['ball%s']}),
    'cc': (('L', 'R'), {   # Character Creator / ActorCore (CC_Base_ prefix stripped)
        'hips': ['Hip', 'Pelvis'], 'spine': ['Waist', 'Spine01'], 'chest': ['Spine02'], 'neck': ['NeckTwist01', 'Neck'], 'head': ['Head'],
        'eye': ['%s_Eye'], 'collar': ['%s_Clavicle'], 'upperarm': ['%s_Upperarm'], 'forearm': ['%s_Forearm'], 'hand': ['%s_Hand'],
        'thumb1': ['%s_Thumb1'], 'thumb2': ['%s_Thumb2'], 'thumb3': ['%s_Thumb3'], 'index1': ['%s_Index1'], 'index2': ['%s_Index2'],
        'index3': ['%s_Index3'], 'middle1': ['%s_Mid1'], 'middle2': ['%s_Mid2'], 'middle3': ['%s_Mid3'], 'ring1': ['%s_Ring1'],
        'ring2': ['%s_Ring2'], 'ring3': ['%s_Ring3'], 'pinky1': ['%s_Pinky1'], 'pinky2': ['%s_Pinky2'], 'pinky3': ['%s_Pinky3'],
        'thigh': ['%s_Thigh'], 'shin': ['%s_Calf'], 'foot': ['%s_Foot'], 'toe': ['%s_ToeBase']}),
    'as': (('_L', '_R'), {   # AdvancedSkeleton (the game's own rig: the round-trip self-test); side as a suffix
        'hips': ['Root_M'], 'spine': ['Spine1_M'], 'chest': ['Chest_M'], 'neck': ['Neck_M'], 'head': ['Head_M'], 'eye': ['Eye%s'],
        'collar': ['Scapula%s'], 'upperarm': ['Shoulder%s'], 'forearm': ['Elbow%s'], 'hand': ['Wrist%s'],
        'thumb1': ['ThumbFinger1%s'], 'thumb2': ['ThumbFinger2%s'], 'thumb3': ['ThumbFinger3%s'], 'index1': ['IndexFinger1%s'],
        'index2': ['IndexFinger2%s'], 'index3': ['IndexFinger3%s'], 'middle1': ['MiddleFinger1%s'], 'middle2': ['MiddleFinger2%s'],
        'middle3': ['MiddleFinger3%s'], 'ring1': ['RingFinger1%s'], 'ring2': ['RingFinger2%s'], 'ring3': ['RingFinger3%s'],
        'pinky1': ['PinkyFinger1%s'], 'pinky2': ['PinkyFinger2%s'], 'pinky3': ['PinkyFinger3%s'], 'thigh': ['Hip%s'],
        'shin': ['Knee%s'], 'foot': ['Ankle%s'], 'toe': ['Toes%s']}),
}
# canonical -> Genesis 2 joint names (what rig.py reads); sided keys get the l/r prefix
G2 = {'hips': 'hip', 'spine': 'abdomen2', 'chest': 'chest', 'neck': 'neck', 'head': 'head', 'eye': 'Eye', 'collar': 'Collar',
      'upperarm': 'Shldr', 'forearm': 'ForeArm', 'hand': 'Hand', 'thumb1': 'Thumb1', 'thumb2': 'Thumb2', 'thumb3': 'Thumb3',
      'index1': 'Index1', 'index2': 'Index2', 'index3': 'Index3', 'middle1': 'Mid1', 'middle2': 'Mid2', 'middle3': 'Mid3',
      'ring1': 'Ring1', 'ring2': 'Ring2', 'ring3': 'Ring3', 'pinky1': 'Pinky1', 'pinky2': 'Pinky2', 'pinky3': 'Pinky3',
      'thigh': 'Thigh', 'shin': 'Shin', 'foot': 'Foot', 'toe': 'Toe'}
G2_PARENT = {'hip': '-', 'abdomen2': 'hip', 'chest': 'abdomen2', 'neck': 'chest', 'head': 'neck', 'Eye': 'head', 'Collar': 'chest',
             'Shldr': 'Collar', 'ForeArm': 'Shldr', 'Hand': 'ForeArm', 'Thigh': 'hip', 'Shin': 'Thigh', 'Foot': 'Shin', 'Toe': 'Foot'}
PREFIXES = ('mixamorig:', 'mixamorig1:', 'mixamorig2:', 'CC_Base_', 'Armature|', 'Genesis8Female|', 'Genesis9|', 'DEF-', 'ORG-')

# material name patterns -> role (non-skin parts and skin features); everything else is skin, split by bones. Matched on
# the lower-cased name with camelCase / separators turned into spaces, as whole words ('ear' must not hit 'Forearms').
MAT_ROLES = [(r'^eye (left|right)( \d+)?$|\beye ?balls?\b', 'Sclera'),   # Genesis 9: one material per eyeball
             (r'\bfinger ?nails?\b', 'Fingernails'), (r'\btoe ?nails?\b', 'Toenails'), (r'\bsclera\b', 'Sclera'),
             (r'\biris(es)?\b', 'Irises'), (r'\bpupils?\b', 'Pupils'), (r'\bcornea\b', 'Cornea'),
             (r'\beye ?moisture\b|\btears?\b', 'Tear'), (r'\beye ?reflection\b', 'EyeReflection'), (r'\blacrimals?\b', 'Lacrimals'),
             (r'lash', 'Eyelashes'), (r'\bbrows?\b|eyebrow', 'Eyebrows'), (r'\bteeth\b|\btooth\b', 'Teeth'), (r'\btongue\b', 'Tongue'),
             (r'\bgums?\b', 'Gums'), (r'\bmouth\b|\binner ?mouth\b|\bmouth ?cavity\b', 'InnerMouth'), (r'\blips?\b', 'Lips'),
             (r'\bears?\b', 'Ears'), (r'\bnostrils?\b', 'Nostrils'), (r'genital|\bdefault ?mat\b', 'defaultMat'),
             (r'\bhidden\b', 'Hidden'), (r'\bhair\b', 'Hair'), (r'\bnails?\b', 'Nails')]
SKIN_HINTS = (r'eye ?socket',)   # skin anyway


def words(name):
    """'EyeMoisture.001' -> 'eye moisture 001' (camelCase and separators to spaces, lower case)."""
    n = re.sub(r'([a-z])([A-Z])', r'\1 \2', name)
    return re.sub(r'[^a-z0-9]+', ' ', n.lower()).strip()


def norm(n):
    for p in PREFIXES:
        if n.startswith(p):
            n = n[len(p):]
    return n.split('|')[-1].split(':')[-1]


def pick_rig(names, want='auto'):
    """(rig id, {canonical key or (key, side): bone name}) with the most keys found (case-insensitive)."""
    low = {norm(n).lower(): n for n in names}
    # also '.L' / '_L' / ' L' spellings of Unity-style names: LeftUpperLeg <- UpperLeg.L
    for n in list(low):
        m = re.match(r'^(.*?)[._ ]([lr])$', n)
        if m:
            low.setdefault(('left' if m.group(2) == 'l' else 'right') + m.group(1), low[n])
    best = (None, {}, -1)
    for rid, (sides, tab) in RIGS.items():
        if want != 'auto' and rid != want:
            continue
        found = {}
        for key, cands in tab.items():
            for si, s in enumerate(sides if key in SIDED else (None,)):
                for c in cands:
                    nm = (c % s) if s is not None else c
                    if nm.lower() in low:
                        found[(key, 'LR'[si]) if s is not None else key] = low[nm.lower()]
                        break
        score = len(found)
        if score > best[2]:
            best = (rid, found, score)
    return best[0], best[1]


def unity(p):
    """Blender world (Z up, right-handed) -> the pipeline's Unity frame (Y up, left-handed): (-x, z, -y)."""
    p = np.asarray(p, float)
    return np.stack([-p[..., 0], p[..., 2], -p[..., 1]], -1)


def main():
    src = arg('--in'); out = arg('--out'); want = arg('--rig', 'auto')
    if not src or not out:
        raise SystemExit('usage: blender -b -P humanoid.py -- --in <file> --out <folder> [--name N] [--mesh obj] [--rig id]')
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    ext = os.path.splitext(src)[1].lower()
    if ext == '.fbx':
        bpy.ops.import_scene.fbx(filepath=src, automatic_bone_orientation=False, ignore_leaf_bones=False)
    elif ext in ('.glb', '.gltf'):
        bpy.ops.import_scene.gltf(filepath=src)
    elif ext == '.vrm':
        if not hasattr(bpy.ops.import_scene, 'vrm'):
            raise SystemExit('VRM needs the "VRM format" add-on in Blender (or export the avatar as FBX / GLB)')
        bpy.ops.import_scene.vrm(filepath=src)
    elif ext == '.blend':
        with bpy.data.libraries.load(src) as (a, b):
            b.objects = a.objects
        for o in b.objects:
            if o is not None:
                bpy.context.scene.collection.objects.link(o)
    else:
        raise SystemExit('unsupported file type %s (FBX, GLB/GLTF, VRM, BLEND)' % ext)
    arms = [o for o in bpy.context.scene.objects if o.type == 'ARMATURE']
    if not arms:
        raise SystemExit('no armature in %s: rig it first (docs/SOURCES.md: quick rigging)' % src)
    arm = max(arms, key=lambda o: len(o.data.bones))
    meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH' and
              (o.parent == arm or any(m.type == 'ARMATURE' and m.object == arm for m in o.modifiers))]
    if '--list' in ARGS:
        log('armature %s: %d bones: %s' % (arm.name, len(arm.data.bones), ', '.join(b.name for b in arm.data.bones)))
        for o in meshes:
            log('mesh %s: %d vertices, materials %s' % (o.name, len(o.data.vertices), ', '.join(m.name for m in o.data.materials if m)))
        return
    want_mesh = arg('--mesh')
    body = next((o for o in meshes if o.name == want_mesh), None) if want_mesh else (max(meshes, key=lambda o: len(o.data.vertices)) if meshes else None)
    if body is None:
        raise SystemExit('no mesh skinned to %s%s' % (arm.name, (' named ' + want_mesh) if want_mesh else ''))
    rid, bones = pick_rig([b.name for b in arm.data.bones], want)
    req = ['hips', 'neck', 'head'] + [(k, s) for k in ('collar', 'upperarm', 'forearm', 'hand', 'thigh', 'shin', 'foot') for s in 'LR']
    miss = [k if isinstance(k, str) else '%s %s' % k for k in req if k not in bones]
    log('rig: %s (%d of %d joints found); armature %s, body mesh %s (%d vertices)' % (
        rid, len(bones), len(CANON) * 2 - 5, arm.name, body.name, len(body.data.vertices)))
    if miss:
        raise SystemExit('the %s table found no bone for %s: pass --rig, or map the rig (docs/SOURCES.md)' % (rid, ', '.join(miss)))

    # joints: bone heads in the rest pose, world space -> Unity frame
    mw = arm.matrix_world
    J = {}
    for k, bn in bones.items():
        b = arm.data.bones[bn]
        J[k] = unity(np.array(mw @ b.head_local))
    # the meshes in their rest shape (shape keys as set, no armature deformation), world space: the body, plus separate
    # eye / mouth meshes (Genesis 9 keeps eyes and mouth as their own figures): their eyeballs give the eye landmarks
    def mat_role_of(name):
        l = words(name)
        if any(re.search(h, l) for h in SKIN_HINTS):
            return None
        return next((rr for kw, rr in MAT_ROLES if re.search(kw, l)), None)
    PART_ROLES = {'Sclera', 'Irises', 'Pupils', 'Cornea', 'Tear', 'EyeReflection', 'Lacrimals', 'Teeth', 'Tongue', 'Gums', 'InnerMouth'}
    parts = [body] + [o for o in meshes if o is not body and o.data.materials and
                      all(m is not None and mat_role_of(m.name) in PART_ROLES for m in o.data.materials)]
    dg = bpy.context.evaluated_depsgraph_get()
    co_l, lv_l, uv_l, nr_l, tl_l, tm_l, matobjs = [], [], [], [], [], [], []
    nv0 = nl0 = 0
    for o in parts:
        for m in o.modifiers:
            if m.type == 'ARMATURE':
                m.show_viewport = False
        dg.update()
        me = o.evaluated_get(dg).to_mesh()
        me.calc_loop_triangles()
        ow = o.matrix_world
        n_ = len(me.vertices)
        c_ = np.empty(n_ * 3); me.vertices.foreach_get('co', c_)
        co_l.append((np.c_[c_.reshape(-1, 3), np.ones(n_)] @ np.array(ow).T)[:, :3])
        nl_ = len(me.loops)
        v_ = np.empty(nl_, np.int64); me.loops.foreach_get('vertex_index', v_); lv_l.append(v_ + nv0)
        uvl = me.uv_layers.active
        if uvl is None:
            raise SystemExit('%s has no UVs' % o.name)
        u_ = np.empty(nl_ * 2); uvl.data.foreach_get('uv', u_); uv_l.append(u_.reshape(-1, 2))
        r_ = np.empty(nl_ * 3)
        try:
            me.corner_normals.foreach_get('vector', r_)
        except AttributeError:
            me.loops.foreach_get('normal', r_)
        nr_l.append(r_.reshape(-1, 3) @ np.array(ow.to_3x3().inverted().transposed()).T)
        nt_ = len(me.loop_triangles)
        t_ = np.empty(nt_ * 3, np.int64); me.loop_triangles.foreach_get('loops', t_); tl_l.append(t_.reshape(-1, 3) + nl0)
        m_ = np.empty(nt_, np.int64); me.loop_triangles.foreach_get('material_index', m_)
        tm_l.append(m_ + len(matobjs))
        matobjs += list(o.data.materials) or [None]
        nv0 += n_; nl0 += nl_
    if len(parts) > 1:
        log('merged with the body: %s (eye / mouth parts)' % ', '.join(o.name for o in parts[1:]))
    co = np.concatenate(co_l); lv = np.concatenate(lv_l); luv = np.concatenate(uv_l); lnr = np.concatenate(nr_l)
    tl = np.concatenate(tl_l); tm = np.concatenate(tm_l)
    nv = len(co)
    mats = [m.name if m else 'material%d' % i for i, m in enumerate(matobjs)] or ['default']

    # height check / unit scale (the importers usually give metres; DAZ FBX in cm and some exports in mm do not)
    P = unity(co)
    h = float(P[:, 1].max() - P[:, 1].min())
    k = 1.0
    for f in (0.01, 0.001, 100.0, 0.1, 10.0):
        if not 1.2 <= h <= 2.3 and 1.2 <= h * f <= 2.3:
            k = f
            break
    if k != 1.0:
        log('height %.3f in file units: scaled x%g to metres' % (h, k))
        P *= k
        J = {a: p * k for a, p in J.items()}
    floor = P[:, 1].min()
    P[:, 1] -= floor
    J = {a: p - np.array([0, floor, 0]) for a, p in J.items()}
    # facing +Z with its left at -x (as VaM's and her exports); a figure facing -Z is turned round
    fwd = np.mean([J[('foot', s)][2] - J[('foot', s)][2] for s in 'LR'])
    toes = [s for s in 'LR' if ('toe', s) in J]
    fwd = np.mean([J[('toe', s)][2] - J[('foot', s)][2] for s in toes]) if toes else 0.0
    if fwd < 0:
        log('figure faced -Z: turned 180 deg about Y')
        R = np.diag([-1.0, 1.0, -1.0])
        P = P @ R.T; J = {a: p @ R.T for a, p in J.items()}   # (the normals are turned below, once in this frame)
    if J[('thigh', 'L')][0] > J[('thigh', 'R')][0]:
        log('WARN: its left side is at +x after facing +Z: the rig is mirrored or its sides are swapped')
    lnr = unity(lnr)
    if fwd < 0:
        lnr = lnr @ np.diag([-1.0, 1.0, -1.0]).T
    lnr /= np.maximum(np.linalg.norm(lnr, axis=1, keepdims=True), 1e-9)

    # eyes: the rig's eye bones, else an estimate (WARN): 62 % of the way from the head joint to the top of the head
    if ('eye', 'L') not in J or ('eye', 'R') not in J:
        top = P[:, 1].max()
        hy = J['head'][1] + 0.62 * (top - J['head'][1])
        sp = 0.032
        front = P[(np.abs(P[:, 1] - hy) < 0.01) & (np.abs(P[:, 0]) < 0.06)]
        z = (front[:, 2].max() - 0.02) if len(front) else J['head'][2] + 0.06
        J[('eye', 'L')] = np.array([-sp, hy, z]); J[('eye', 'R')] = np.array([sp, hy, z])
        log('WARN: no eye bones: eyes estimated at %.3f m (check the face fit)' % hy)

    # one vertex per (position, UV) pair, as SfExport writes
    key = np.c_[lv, np.round(luv * 1e6).astype(np.int64)]
    uk, inv = np.unique(key, axis=0, return_inverse=True)
    inv = inv.ravel()
    V = P[uk[:, 0]]
    UV = np.zeros((len(uk), 2)); UV[inv] = luv
    N = np.zeros((len(uk), 3)); np.add.at(N, inv, lnr)
    N /= np.maximum(np.linalg.norm(N, axis=1, keepdims=True), 1e-9)
    F = inv[tl]
    # the fit needs outward normals: on the trunk (between hips and neck, near the mid-line) they must point away from the
    # trunk's vertical axis; a file with inverted normals is corrected (the winding needs nothing: the fit reads it from vn)
    hy_, ny_ = 0.5 * (J[('thigh', 'L')][1] + J[('thigh', 'R')][1]), J['neck'][1]
    tr = (V[:, 1] > hy_ + 0.05) & (V[:, 1] < ny_ - 0.05) & (np.abs(V[:, 0]) < 0.12)
    if tr.sum() > 50:
        ax = np.array([0.0, 0.0, np.median(V[tr, 2])])
        out_dot = np.einsum('ij,ij->i', N[tr][:, [0, 2]], (V[tr] - ax)[:, [0, 2]])
        if np.median(out_dot) < 0:
            N = -N
            log('normals pointed inward: flipped')

    # roles: material keywords first, then the nearest bone segment for skin
    sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'pipeline'))
    import rig
    g2 = {}
    for kk, p in J.items():
        if isinstance(kk, tuple):
            g2[('l' if kk[1] == 'L' else 'r') + G2[kk[0]]] = p
        else:
            g2[G2[kk]] = p
    for need in ('abdomen2', 'chest'):   # a spine joint the rig lacks: between its neighbours
        if need not in g2:
            g2[need] = g2['hip'] + (g2['neck'] - g2['hip']) * (0.35 if need == 'abdomen2' else 0.7)
            log('WARN: no %s bone: placed between hip and neck' % need)
    names, _, vsegs, _ = rig.label_segments()
    cen = V[F].mean(1)
    lab = rig.labels(cen, vsegs, g2)
    mat_role = {}
    for i, mn in enumerate(mats):
        l = words(mn)
        r = None
        if not any(re.search(h, l) for h in SKIN_HINTS):
            r = next((rr for kw, rr in MAT_ROLES if re.search(kw, l)), None)
        mat_role[i] = r
    hip_y = 0.5 * (g2['lThigh'][1] + g2['rThigh'][1])
    waist_y = g2['abdomen2'][1]
    neck_y, head_y = g2['neck'][1], g2['head'][1]
    eye_z = 0.5 * (g2['lEye'][2] + g2['rEye'][2])
    role = np.empty(len(F), object)
    for fi in range(len(F)):
        r = mat_role.get(int(tm[fi]))
        c = cen[fi]
        ln = names[lab[fi]]
        if r == 'Nails':
            r = 'Toenails' if ln.startswith('foot') or c[1] < 0.3 else 'Fingernails'
        if r is None:
            if ln.startswith(('thigh', 'shin')):
                r = 'Legs'
            elif ln.startswith('foot'):
                r = 'Feet'
            elif ln.startswith('uparm'):
                r = 'Shoulders'
            elif ln.startswith('forearm'):
                r = 'Forearms'
            elif ln.startswith(('hand', 'f')):
                r = 'Hands'
            elif c[1] > head_y - 0.02 or (c[1] > neck_y + 0.4 * (head_y - neck_y) and c[2] > g2['neck'][2] + 0.04):   # the chin's underside is Face (as VaM's)
                r = 'Face' if c[2] > eye_z - 0.065 else 'Head'
            elif c[1] > neck_y - 0.01:
                r = 'Neck'
            elif c[1] < max(hip_y + 0.06, waist_y - 0.04):
                r = 'Hips'
            else:
                r = 'Torso'
        role[fi] = r
    # Lips and Ears from geometry when no material names them (Genesis 9: both are part of 'Head'). Lips: the face skin within
    # 9 mm of the mouth opening (the face vertices that coincide with the inner mouth's), in front of it; ears: the outermost
    # head skin at eye height behind the eyes.
    from mathutils.kdtree import KDTree

    def kd(points):
        t = KDTree(len(points))
        for i_, p_ in enumerate(points):
            t.insert(p_, i_)
        t.balance()
        return t
    eye_y = 0.5 * (g2['lEye'][1] + g2['rEye'][1])
    face_f = np.flatnonzero(role == 'Face')
    im = np.flatnonzero(role == 'InnerMouth')
    if not (role == 'Lips').any() and len(im) and len(face_f):
        t_in = kd(V[np.unique(F[im])])
        fv = np.unique(F[face_f])
        ring = np.array([V[v] for v in fv if t_in.find(V[v])[2] < 5e-4])
        ring = ring[ring[:, 1] < eye_y - 0.03] if len(ring) else ring   # the mouth, not the eye sockets
        if len(ring) >= 8:
            t_r = kd(ring)
            # no wider than the mouth itself (the opening's corners): the fit takes the mouth corners and lip label size from the Lips
            cx_, hw_ = 0.5 * (ring[:, 0].min() + ring[:, 0].max()), 0.5 * np.ptp(ring[:, 0])
            near = np.array([t_r.find(cen[f_])[2] < 0.009 and cen[f_][2] > ring[:, 2].min() - 0.004 and abs(cen[f_][0] - cx_) <= hw_
                             for f_ in face_f])
            role[face_f[near]] = 'Lips'
            log('lips from the mouth opening: %d triangles (%d ring vertices)' % (int(near.sum()), len(ring)))
    if not (role == 'Ears').any():
        hf = np.flatnonzero(np.isin(role, ['Face', 'Head']))
        band = hf[(np.abs(cen[hf, 1] - (eye_y - 0.01)) < 0.04) & (cen[hf, 2] < eye_z - 0.035)]
        if len(band):
            cx = float(np.median(cen[hf, 0]))
            dx = np.abs(cen[band, 0] - cx)
            ear = band[dx > 0.82 * dx.max()]
            role[ear] = 'Ears'
            log('ears from the head shape: %d triangles' % len(ear))
    # nipples: the most forward chest point per side, a 45 mm disc around it (VaM's Nipples group: the breast cap round the areola, p90 47 mm)
    chest = np.flatnonzero(role == 'Torso')
    ys = g2['chest'][1]
    for sgn in (-1, 1):
        sel = chest[(np.sign(cen[chest, 0]) == sgn) & (np.abs(cen[chest, 0]) > 0.04) & (cen[chest, 1] > ys - 0.2) & (cen[chest, 1] < ys + 0.1)]
        if len(sel):
            tip = cen[sel[np.argmax(cen[sel, 2])]]
            role[sel[np.linalg.norm(cen[sel] - tip, axis=1) < 0.045]] = 'Nipples'
    groups = []
    gidx = np.empty(len(F), np.int64)
    for fi in range(len(F)):
        g = '%s@%s' % (role[fi], re.sub(r'[^A-Za-z0-9_.-]+', '_', mats[int(tm[fi])]))
        if g not in groups:
            groups.append(g)
        gidx[fi] = groups.index(g)

    # textures per material: Principled BSDF base colour / normal map images
    os.makedirs(out, exist_ok=True)
    tex_of = {}
    written = {}
    for i, m in enumerate(matobjs):
        if m is None or not m.use_nodes:
            continue
        slots = {}
        for node in m.node_tree.nodes:
            if node.type != 'BSDF_PRINCIPLED':
                continue
            for sock, slot in (('Base Color', '_MainTex'), ('Normal', '_BumpMap')):
                s = node.inputs.get(sock)
                if s is None or not s.is_linked:
                    continue
                n = s.links[0].from_node
                if n.type == 'NORMAL_MAP':
                    s2 = n.inputs.get('Color')
                    n = s2.links[0].from_node if s2 is not None and s2.is_linked else None
                while n is not None and n.type not in ('TEX_IMAGE',) and n.inputs and any(x.is_linked for x in n.inputs):
                    n = next(x.links[0].from_node for x in n.inputs if x.is_linked)
                if n is not None and n.type == 'TEX_IMAGE' and n.image is not None:
                    img = n.image
                    if not img.has_data and not (img.filepath and os.path.isfile(bpy.path.abspath(img.filepath))):
                        continue   # an FBX image slot without a file (DAZ's PBR maps): the DAZ material list fills it below
                    if img.name not in written:
                        fn = "tex_%02d_%s.png" % (len(written), re.sub(r"^tex_\d+_", "", re.sub(r"[^A-Za-z0-9_.-]+", "_", os.path.splitext(img.name)[0])))
                        path = bpy.path.abspath(img.filepath) if img.filepath else ''
                        try:
                            if path and os.path.isfile(path) and path.lower().endswith('.png'):
                                shutil.copyfile(path, os.path.join(out, fn))
                            else:
                                img2 = img.copy(); img2.filepath_raw = os.path.join(out, fn); img2.file_format = 'PNG'; img2.save()
                                bpy.data.images.remove(img2)
                            written[img.name] = fn
                        except Exception as e:  # noqa: BLE001
                            if 'image data' not in str(e):   # (an FBX slot without its file: the DAZ material list fills it)
                                log('WARN: texture %s not written (%s)' % (img.name, e))
                            continue
                    slots[slot] = written[img.name]
        tex_of[i] = slots
    # DAZ: the FBX exporter leaves out the maps of DAZ's PBR shaders; tools/sources/daz_export.dsa lists them next to the
    # FBX ("<fbx>.materials.txt": surface, channel, image). Fill the slots the FBX left empty from it.
    side = src + '.materials.txt'
    if os.path.isfile(side):
        chans = {}
        with open(side, encoding='utf-8', errors='replace') as f:
            for l in f.read().splitlines():
                t = l.split('\t')
                if len(t) >= 3 and not l.startswith('#'):
                    chans.setdefault(t[0].strip(), {})[t[1].strip().lower()] = t[2].strip()
        want = {'_MainTex': ('base color', 'diffuse color', 'translucency color'), '_BumpMap': ('normal map',)}
        filled = 0
        for i, m in enumerate(matobjs):
            if m is None:
                continue
            ch = chans.get(re.sub(r'\.\d+$', '', m.name)) or chans.get(m.name)
            if not ch:
                continue
            slots = tex_of.setdefault(i, {})
            for slot, labels in want.items():
                if slot in slots:
                    continue
                p = next((ch[lb] for lb in labels if lb in ch and os.path.isfile(ch[lb])), None)
                if p is None:
                    continue
                if p not in written:
                    fn = 'tex_%02d_%s' % (len(written), re.sub(r'[^A-Za-z0-9_.-]+', '_', os.path.basename(p)))
                    shutil.copyfile(p, os.path.join(out, fn))
                    written[p] = fn
                slots[slot] = written[p]
                filled += 1
        log('DAZ material channels (%s): %d texture slots filled' % (os.path.basename(side), filled))

    # body.obj
    with open(os.path.join(out, 'body.obj'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('# %s via tools/sources/humanoid.py (%s rig); Unity axes (left-handed, Y up), metres\n' % (os.path.basename(src), rid))
        for p in V:
            f.write('v %.6f %.6f %.6f\n' % tuple(p))
        for n in N:
            f.write('vn %.6f %.6f %.6f\n' % tuple(n))
        for t in UV:
            f.write('vt %.6f %.6f\n' % tuple(t))
        for gi, g in enumerate(groups):
            f.write('g %s\n' % g)
            for t in F[gidx == gi]:
                # Unity's front faces are clockwise: the frame change mirrored the winding, so reverse it back
                a, b, c = t[0] + 1, t[2] + 1, t[1] + 1
                f.write('f %d/%d/%d %d/%d/%d %d/%d/%d\n' % (a, a, a, b, b, b, c, c, c))
    # joints.txt (rest = posed: the file's own pose)
    with open(os.path.join(out, 'joints.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('# name\tparent\tmorphed rest position\tposed position\n')
        for nm, p in g2.items():
            base = nm[1:] if nm[0] in 'lr' and nm[1:] in G2_PARENT else nm
            par = G2_PARENT.get(base, 'hip')
            if nm[0] in 'lr' and par in G2_PARENT and par not in ('hip', 'chest', 'head', '-') and nm[1:] in G2_PARENT:
                par = nm[0] + par
            if base.startswith(('Thumb', 'Index', 'Mid', 'Ring', 'Pinky')):
                k_ = int(base[-1]); par = nm[0] + (base[:-1] + str(k_ - 1) if k_ > 1 else 'Hand')
            s = '%.6f %.6f %.6f' % tuple(p)
            f.write('%s\t%s\t%s\t%s\n' % (nm, par, s, s))
    # materials.txt (one row per group: the textures of its material)
    with open(os.path.join(out, 'materials.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('# index\tname\tenabled\tregion\tslot=file ...\n')
        mat_of_group = {}
        for fi in range(len(F)):
            mat_of_group.setdefault(int(gidx[fi]), int(tm[fi]))
        for gi, g in enumerate(groups):
            r = g.split('@')[0]
            region = 'face' if r in ('Face', 'Lips', 'Ears', 'Nostrils') else 'torso' if r in ('Torso', 'Hips', 'Neck', 'Head', 'Nipples') else \
                'limbs' if r in ('Legs', 'Feet', 'Shoulders', 'Forearms', 'Hands', 'Fingernails', 'Toenails') else '-'
            sl = tex_of.get(mat_of_group.get(gi, -1), {})
            f.write('%d\t%s\t1\t%s%s\n' % (gi, g, region, ''.join('\t%s=%s' % kv for kv in sl.items())))
    name = arg('--name') or os.path.splitext(os.path.basename(src))[0]
    with open(os.path.join(out, 'info.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('character\t%s\natom\t-\nvertices\t%d (UV vertices; base %d)\nmaterials\t%d\nsource\t%s (%s rig)\ntextures\t%d\n' % (
            name, len(V), nv, len(groups), os.path.basename(src), rid, len(written)))
    roles_n = {}
    for r in role:
        roles_n[r] = roles_n.get(r, 0) + 1
    rep = ['source %s, rig %s, armature %s, body mesh %s' % (src, rid, arm.name, body.name),
           'height %.3f m, %d vertices (%d UV vertices), %d triangles, %d groups, %d textures' % (
               float(V[:, 1].max()), nv, len(V), len(F), len(groups), len(written)),
           'joints: ' + ', '.join('%s<-%s' % ((k if isinstance(k, str) else '%s %s' % k), v) for k, v in bones.items()),
           'roles (triangles): ' + ', '.join('%s %d' % kv for kv in sorted(roles_n.items())),
           'materials: ' + ', '.join('%s -> %s %s' % (mats[i], mat_role.get(i) or 'skin', tex_of.get(i, {})) for i in range(len(mats))),
           'other meshes skinned to the armature (left out: clothes, hair, lashes): ' + (', '.join(o.name for o in meshes if o != body) or '-')]
    with open(os.path.join(out, 'source.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(rep) + '\n')
    for l in rep:
        log(l)
    log('wrote %s' % out)


if __name__ == '__main__':
    main()
