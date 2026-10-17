# Character sources beyond VaM

**Update (2026-10-16 evening, user, 0.11.13):** DAZ characters (any non-VaM source) are converted **face-only**: the face
conversion looked right in the game (RKS Krista 9), the body did not. character.txt `faceonly`; the plugin holds Body at 0 %
and the body skin off; `run.ps1 -FullBody` still converts the body, `-Sliders` makes the slider-fit preset.

**Decision (2026-10-16, user):** DAZ characters go in as **slider-fit presets** (`run.ps1 -Daz <preset.duf>` writes
`presets\<Name> (fit).txt`; her own mesh and skin). The full conversion is **parked** for them (`-Convert` still runs it): on
RKS Krista 9 (Genesis 9) it fixed the face and neck but folded her breast skin under the breasts, unusable. The direction
noted for when it is picked up again: register each DAZ base mesh (Genesis 2, 9) onto her once, carefully, and carry each
character's morph across that fixed correspondence instead of fitting every character's shape from scratch.

**State (0.11.2):** the adapter is built: `tools/sources/humanoid.py`, `run.ps1 -Source <file>` (also with `-Sliders`).
Verified by a round trip: a VaM export packed as an FBX with its Genesis 2 (DAZ-named) skeleton and materials
(`tools/sources/selftest_fbx.py`) comes back with all 53 joints within 1.3 mm (the floor), the same vertices, outward normals,
the same winding, 81 % of faces in the same body role (the rest are boundary conventions: VaM's Hips reach down the thighs, the
elbow / neck splits) and an identical slider fit. The Genesis 3/8/9, Mixamo, Unity / VRChat, VRM, CC, UE and AdvancedSkeleton
tables are written but not yet run on a real file of that kind: the first DAZ Genesis 8/9 FBX the user gives is the next test
(`blender -b -P humanoid.py -- --in <file> --list` prints its bones and materials first).

**DAZ characters (0.11.3):** `run.ps1 -Daz <character preset .duf>` runs DAZ Studio with `tools/sources/daz_export.dsa`
(`DAZStudio.exe -scriptArg <duf> -scriptArg <fbx> <script>`: clear the scene, open the preset, base resolution, hide
everything but the figure, FBX with textures collected, `<fbx>.status.txt`, quit), then `-Source`. Before launching, run.ps1
checks that the preset's base figure (its first node's .dsf, e.g. `data/Daz 3D/Genesis 9/Base/Genesis9.dsf`) is in one of DAZ
Studio's content libraries (registry `HKCU\Software\DAZ\Studio6` ContentDirN), because a missing file stops DAZ Studio on a
modal dialog; it gives up after 10 minutes without a status file. Launch tested on the user's PC (DAZ Studio 6): the script
gets its arguments and opens the preset; the export itself waits for Genesis 9 Starter Essentials to be installed.
A character product folder (data / People / Runtime) is installed by copying it into a content library (`My Library`).

First real Genesis 9 run (RKS Krista 9, 0.11.5): rig g9 53/53 joints, fit residual 0.03 mm, eyes open (Krista's lids fitted),
material checks PASS. Open: jaw / chin pulled in (chin metric 21 mm) with a step at the face / neck seam, folds under the
breasts (54 flipped chest triangles; Lexi via the same adapter: 7). Candidates: the merged G9 mouth mesh in InnerMouth (the
mouth-ring metrics), the Face / Neck boundary rule on G9's jaw, the 45 mm nipple disc (tuned on Genesis 2) on G9's chest.

Tools looked at and not used: bluemoonfoundry/daz-mcp-server (an MCP server driving DAZ Studio through a C++ plugin with an
HTTP port on localhost:18811 and tools that run any DazScript and write any path: safe with caveats, but more than the export
needs; one auditable script does it), the old DazToBlender bridge (Blender 2.80-2.83, does not load in Blender 5.2; the FBX
path covers it), AutoRiggerPipeline.dsa (a DAZ wizard: OBJ import, rigging onto Genesis 9 by the Transfer Utility, unweighted
bone clean-up; no network or process calls; installed in `My Library/Scripts/AutoRigger` for clothing / unrigged meshes later).

Community routes worth knowing (VaM Hub, read 2026-10-16):
- *Daz Generations Converter* (Blender extension, Blender 4.4-5.1, download on Mega, contains executable code): converts a
  female shape between G2, G3, G8, G8.1 and G9 base meshes by barycentric placement, keeping vertex order (eyes and mouth
  moved but not morphed). An alternative for DAZ figures: G8/G9 shape -> a G2 morph -> the existing VaM path; it carries no
  textures (the adapter bakes the source's own textures instead).
- *Wrapping* (R3DS Wrap): paired landmark dots (each finger, toes, face, nipples) then a non-rigid wrap of a G2 base onto any
  model, eyes repaired after. The same idea as the fit here (landmark warp + non-rigid ICP); fingers and nipples need landmarks.
- *Wrapped model into DAZ then VaM*: Morph Loader Pro on G2F, Adjust Rigging to Shape, ERC Freeze, save as a morph .dsf.
- *Unity AssetBundles for VaM*: the Unity version and bundle workflow for VaM 1.x (Unity 2018); for this game the bundle
  project in tools/shaders already builds Unity 2022.3.9 bundles (the avatar replacement mode below uses a separate project of
  the same version).
- *Clothing into VaM* (video): download, scale, fit in Blender (shrinkwrap / lattice / proportional editing) to the G2 base,
  then DAZ and VaM. Here, clothes on her already follow a character's shape (MorphState follow); bringing new clothes in is a
  later step with the same fit.

The pipeline (`docs/PIPELINE.md`) reads one source format: an SfExport folder written by the VaM plugin, i.e. a Genesis 2
Female in VaM's vocabulary (`body.obj` with VaM material groups, `joints.txt` with Genesis 2 bone names, `materials.txt` +
textures). Everything after that is source independent in what it *does* (pose match by joint directions, landmark warp,
non-rigid fit, bake, material check, slider fit). So a new source needs only an **adapter** that writes the same folder.
On her side, 0.11.1 already handles every variant the game swaps (faces, nipples, nails, lashes, brows, bodies: `roles.py`).

## The adapter: any rigged humanoid -> an SfExport folder

`tools/sources/humanoid.py`, run in Blender (`blender -b -P humanoid.py -- --in <file> --out <SfExport dir>`). Groups are
written as `Role@material`: the fit (sfpipe `role()`, bake, slider fit) reads the role, the bake keys the textures by the full
name, so a role spread over several source materials keeps each material's own maps. VaM exports (no '@') are unchanged.

1. **Import** whatever Blender reads: FBX (VRChat / Unity avatars, Mixamo, DAZ Studio FBX export, Character Creator,
   ActorCore, Unreal mannequins), glTF / GLB, VRM (the VRM add-on), DAE, OBJ + a separate rig.
2. **Bone map** to Unity's `HumanBodyBones` (the standard VRChat avatars are built on), then to the Genesis 2 names
   `rig.py` expects. Tables per known rig, picked by which names are present:
   - Unity Humanoid / VRChat common (`Hips, Spine, Chest, UpperChest, Neck, Head, LeftUpperLeg, LeftLowerLeg, LeftFoot,
     LeftToes, LeftShoulder, LeftUpperArm, LeftLowerArm, LeftHand, LeftEye`, `.L/.R` and `_L/_R` spellings), and the
     avatar's own humanoid mapping from a `.fbx.meta` / `.unitypackage` when it is there (exact, no guessing);
   - Mixamo (`mixamorig:LeftUpLeg ...`), VRM (`J_Bip_L_UpperLeg ...`), DAZ Genesis 3/8/8.1/9 (`lThighBend/lThighTwist,
     lShin, lFoot, lShldrBend/lShldrTwist, lForearmBend, lHand, lEye`; twist bones folded into their bend bone), Character
     Creator / ActorCore (`CC_Base_L_Thigh ...`), Unreal mannequin (`thigh_l, calf_l ...`), Rigify (`DEF-thigh.L ...`),
     AdvancedSkeleton (her own: a round-trip self-test);
   - unknown rigs: a fuzzy match (side token + part keyword + chain order) that is shown for confirmation, never trusted
     silently.
   The joints written are the bone heads in the figure's rest pose (A or T: the fit poses her by joint directions).
3. **Body regions** instead of VaM's material names: each body face gets the region of its nearest bone segment (the
   same labels `rig.label_segments` uses), written as the Genesis 2 group names the fit needs (Torso, Hips, Neck, Head,
   Face, Shoulders, Forearms, Hands, Legs, Feet; Face = the head region in front of the ears). Lips, nostrils and nipples
   come from geometry (mouth opening boundary, nipple tips) or a VRChat viseme / VRM expression shape when present.
   Eyes: the eye bones (VRChat / VRM / DAZ have them), else the eye meshes' centres.
4. **Nude body only**: clothing meshes are listed and left out (by name, by being a separate mesh, or by not being skinned
   like skin); a figure whose clothes are merged into the body mesh is reported, not guessed.
5. **Textures from the materials**: Principled BSDF base colour / normal map / roughness images (glTF, FBX), or the
   Unity material slots of a `.unitypackage` (`_MainTex`, `_BumpMap`, Poiyomi / lilToon names), written as
   `tex_*.png` + `materials.txt` slots `_MainTex`, `_BumpMap`, `_GlossTex` (1 - roughness). The bake then does what it does
   for VaM: detail-only normals, brightness matched to her, the material check. Toon shaders (most VRChat avatars) have
   flat, pre-shaded albedo: the material check's brightness gate still applies, the look stays toon on her realistic skin
   shader (said in the summary).
6. `info.txt` (name, source format, rig table used), `thumb.jpg`, and a `_source_check.txt`: bones mapped / missing,
   regions found, meshes left out, scale (metres from the file's units, checked against a plausible height).

Then `run.ps1 -VamExport <that folder>` (or `-Sliders`) works unchanged.

## Unrigged meshes (a scan, a sculpt, a statue): quick rigging first

The adapter needs a skeleton. Routes that produce one in a known vocabulary:
- **ActionForge** (actionforge.app, browser): "Rig it" / finger detection, exports FBX / GLB on its Default (Quaternius),
  Mixamo, UE4/UE5 or Reallusion skeleton. Free CC0 content, but the tool's code is all rights reserved and has no API: a
  manual step the user does, then the adapter reads the Mixamo / UE table.
- Mixamo's auto-rigger (web, Mixamo skeleton), Reallusion AccuRIG (free, CC skeleton), Blender Rigify (meta-rig fitted by
  hand). The adapter does not need weights to be good: only the joints (landmarks) and the skin surface are used.

## UABEA / AssetsTools.NET

- **Her side (useful now)**: read the game's own assets offline: `Shader Graphs/Skin`'s material properties and keywords,
  every face / nipple / body variant mesh and its textures with their colour space, without the game running or the
  export button. A small .NET 8 tool on AssetsTools.NET (classdata.tpk from AssetRipper/Tpk) could write the same export
  folder as the Presets tab. Worth it when exports from the game keep being the fragile step.
- **Source side**: Unity asset bundles the user made or owns (their own avatar project's build, a purchased avatar's
  bundle) can be unpacked with AssetRipper / UABEA into FBX + textures, then go through the adapter. Extracting other
  people's uploaded VRChat avatars is against VRChat's terms and the creators' rights: not supported.

## A third mode: avatar replacement (the VRChat way)

**Design 2026-10-16; step 1 (offline retarget check) built and run on RKS Krista 9. Nothing built for the game yet.**

VRChat does not reshape a base body: it drives the avatar's own mesh, rig and materials from the player's pose. Here: load
a character's own figure (DAZ Genesis 9 / 8, VaM Genesis 2, later any rig `humanoid.py` knows) from an asset bundle, hide
her skin renderers, and every frame drive the figure's skeleton from her bones. Her skeleton keeps animating as now (the
game's Animator, FinalIK, the face Animator, our Overlap / Embody / Reactions): it becomes the *pose source* only.

### What failed before, and why this mode does not repeat it

| Earlier route | What went wrong | Avatar mode |
|---|---|---|
| Full conversion (fit her mesh to the character's shape: landmark warp + non-rigid fit) | Krista 9: skin folded under the breasts (54 flipped chest triangles), jaw / chin pulled in, a step at the face / neck seam | Her mesh is never deformed and nothing is fitted: the character's own mesh, as DAZ / VaM made it, is drawn |
| Baking the character's textures onto her UVs | Detail-only normals needed, albedo ~2x too bright, her Skin shader's blush / makeup layers (`_BlushPower`, `_AttachColor_*`, `_AddMap`) drawn over the baked skin | The character's own UVs and maps, nothing baked. If her Skin shader is used (route A below) those layers are zeroed explicitly and the brightness is checked against hers: the two things the bake got wrong |
| Matching rest poses by every joint direction (rig.py, early fit) | Spine / collar directions are rig conventions: matching them tipped her torso 21 degrees back and swung the shoulders 3 cm | Limbs are aligned by direction; the trunk, collars, eyes and jaw are not (only her *change* since bind is applied). The first check still aligned the collars in the coronal plane, as rig.py does: that dropped the avatar's clavicles 7 degrees (seen by the user in the renders), because her Scapula joint sits at the back of the ribcage and G9's collar starts at the sternum. Fixed: change only |
| One fit per character from scratch | Fragile per character (breast shapes, the G9 mouth mesh) | Nothing per character except the bone map, which is per rig family (g9 / g8 / g2 tables; g9 found 53 of 53 on Krista) |
| Face-only conversion (the current DAZ default) and slider-fit presets | Work, but the body is hers: her skin, her shape or the character's proportions on it | Kept as they are; avatar mode is a separate, third choice |

Unity Humanoid retargeting (`AvatarBuilder.BuildHumanAvatar` + `HumanPoseHandler`, the literal VRChat route) is **not** the
primary path: it needs both rigs in a T-pose to build the avatars (hers is an A-pose; she would have to be posed into one at
load), its muscle space clamps poses to the humanoid limits (the wide kneels and full knee flexion of sex poses), it carries
no twist bones beyond its own percentages and no toes, metacarpals or face bones, and it cannot be reproduced offline in
Blender. The direct world-rotation retarget below does all of that in ~70 matrix products per frame and is checked offline
by `tools/avatar/retarget_check.py`. An avatar that ships its own Humanoid `Avatar` (VRChat-style bundles) can still give
its bone map through `Animator.GetBoneTransform`, then go through the same retarget.

### Step 1, done: the offline retarget check

`blender -b --factory-startup -P tools/avatar/retarget_check.py -- --avatar <fbx> --game <her export dir> --out <dir>
[--pose <recording>] [--ik]` loads the character's FBX and her export (skeleton + skin), builds the map, poses the
character from her pose and renders both side by side (front + side per frame, `sheet.png`), with `retarget_check.txt` (per
frame: the angle between each mapped segment of the avatar and hers, end-effector distances, the twist split). Her pose is a
recording from the Characters tab (*Record 6 s of her animation*, `BepInEx\config\SfMorphs\poses\*.txt`) or, without
`--pose`, four test poses built on her bind skeleton (bind; raised leg; reach with the arm overhead (the shoulder girdle
taking a third of the elevation), a 75 degree forearm pronation, a 60 degree upper-arm twist, curled fingers, eyes and jaw;
a wide kneel). With renders, a first frame shows the avatar in its own rest pose beside her bind pose: the reference for
what the retarget changes.

Run 2026-10-16, RKS Krista 9 (G9 FBX from `daz_export.dsa`) on export Reika-20261016-175648, test poses:
- map: rig g9, 53 / 53 canonical bones; 30 direction-aligned limb / finger bones, 2 hands (two directions), 10 trunk /
  collar / eye / jaw bones (change only), 3 trunk bones spread (G9 spine1, spine3, neck2 share her bends),
  12 twist bones, 167 copies (DAZ's follower figures), 79 bones following their parent (metacarpals, toes, face rig).
- every mapped limb and finger segment points exactly where hers does in every frame (0.00 degrees); the avatar's bind
  frame matches its own rest at the collars and trunk; the trunk direction changes within 1.3 degrees of hers; the renders match pose
  for pose, including the pronated hand, the curled fingers, the head turn and the open jaw. The pose written to Blender's
  bones matches the computed one within 1 micrometre.
- proportions (avatar scaled x0.964 so its legs match hers): without IK her hands and the avatar's are 38-73 mm apart,
  feet 40-47 mm, head 28-43 mm. With `--ik` hands and feet land exactly on hers, at the cost of the elbows / knees bending
  differently by up to 25 degrees (Krista's arms are longer: in the bind pose her elbows have to bend).
- found on the way: DAZ's FBX holds one armature with each follower figure (eyes, mouth, lashes, tear, brows) as its own
  duplicate skeleton (`head.001` ...), the meshes bone-parented to their figure root *and* skinned to the duplicates (a
  double transform: the mouth and eyes floated off her in the first renders). The runtime must rebind them (below); the
  check script reparents them and poses the copies from the main bones.
- real recording (Room_Scene, 6 s at 10 fps, 254 bones; 4 frames rendered, the cameras follow her pelvis and facing): every
  limb and finger segment again 0.00 degrees from hers, the trunk within 2.3 degrees; the renders match pose for pose (hands
  on hips, arms out, bending forward with a shrug). Hands 39-63 mm from hers, feet 34-43 mm, head 30-40 mm.
- the recording shows **self-contact**: her fists rest on her own hips, Krista's hover beside hers (Krista's hips are
  narrower). IK to her wrists would not fix it (her hips are wider). Contacts with her own body need targets relative to the
  avatar's surface (below).

### The retarget (what the plugin would run every frame)

All rotations are world rotations in her root's space; `D_h = R_h(now) R_h(bind)^-1` is her bone's change since her bind
pose (from her SkinnedMeshRenderers' bind poses, as Export's skeleton.txt), so her bone axis conventions never matter.
1. **Aligned rest** (once at load): walk the avatar's skeleton from the root; each bone's rest is carried by its parent's
   alignment, then turned by the smallest rotation that makes its segment point along her matching segment in her bind
   pose: upper arm, forearm, thigh, shin, foot, fingers (k -> k+1; the last phalanx and the toes inherit), hand (the middle
   finger direction plus a roll from pinky1 -> index1). Trunk, collars, eyes, jaw: no turn.
   Her bind pose and G9's rest are both A-poses (upper arm 49 vs 47 degrees down), so the turns are small.
2. **Mapped bones**: `W = D_h A`. **Trunk bones between anchors** (G9 has spine1-4 and neck1-2 for her Spine1 / Chest /
   Neck): `W = D_lo (D_lo^-1 D_hi)^f A`, f by the place on the chain, so her bend is spread instead of kinked.
3. **Twist bones**: the upper arm / thigh bend bone keeps the swing of its change, its twist bones take 1/n .. 1 of the
   twist about the segment (G9: 0.5, 1; G8's single ShldrTwist / ThighTwist: 1; G2 has none); the forearm twist bones take
   k/(n+1) of the hand's twist about the forearm (pronation). Without this the skin twists at the shoulder / wrist only.
4. **Everything else** follows its parent at its rest offset (metacarpals, toes, the face rig until the face map, pectorals).
5. **Root**: her pelvis position plus `D_root` x (avatar hips - her pelvis, at bind). Scale: uniform, the avatar's leg
   length to hers (default; `height` and `none` too), so the feet meet the floor she stands on.
6. **IK** (weighted): two-bone IK of arms / legs to her wrists / ankles, pole at her elbow / knee; hand and foot keep
   `D_h A`. Weight 1 only where positions matter: her feet on the floor, her hands while planted (Overlap's planted lock) or
   held (Embody, a partner hand on her wrist); 0 otherwise, which keeps the natural bends (see the numbers above).
   **Self-contact** (her hand on her own hip, thigh, breast, face): the target is not her wrist but the same place on the
   avatar's body: her palm's nearest HerBody segment, the surface point (segment, height along it, angle around it) and the
   gap there, rebuilt on the avatar's refitted segment (the HerBody refit of v1); weight by that gap (full within ~3 cm).
7. **When**: in a PlayerLoop step right before `PostLateUpdate.UpdateAllSkinnedMeshes`, the point PoseRecorder samples
   (after the Animator, FinalIK, DynamicBone, the face Animator and our ApplyStep / Overlap / Embody): a recording is exactly
   the avatar's input, so the offline check covers the runtime maths.

### Her reference pose

The check renders her as a grey, eyeless, bald figure whose lower face droops and whose brows, neck, trapezius and
clavicles look wrong (the user's reading). That is her real geometry: skinned with the recorded pose, the check's mesh
lands on the game's own BakeMesh of the same frame (95 % of vertices within 0.00 mm, 2.95 mm at most where the soft-zone
helper bones are approximated). In the game her textures, eyes, lashes, hair and the plugin's PN smoothing (Fidelity) hide
it; in avatar mode none of her geometry is drawn.

What matters for the avatar is whether her bind pose is a fair zero for her bone changes. The recording says yes for the
face: over 60 frames her jaw never leaves its bind rotation (0.0 degrees), the lips sit 1-3 mm from bind (5 mm at most),
the lids move only to blink (up to 14 mm), the cheeks 0.7 mm. So the bind pose stays the reference for every bone. To
check again on a talking / moaning clip (lip sync and the game's sex faces drive the jaw); if the jaw then rests away from
bind, the face bones take her neutral (idle) face as their zero instead. The neck and head turn 6-18 degrees in this clip,
which is her looking around, not a constant offset.

### Shoulders and joint correctives

The clavicle follows her `Scapula` change, so the avatar's shoulder girdle rises only as far as hers does in the game's
animation. In the check a raised arm without scapula movement collapsed the avatar's shoulder (clavicle flat, a crease from
the neck to the armpit); with the girdle taking a third of the elevation (the scapulohumeral rhythm) it looks natural. Two
pieces, in this order:
1. **The figure's own correctives.** DAZ figures shape shoulders, elbows, hips and knees at large angles with joint
   corrective morphs, driven by bone angles; `daz_export.dsa` bakes the dialled shape and exports no morphs
   (`doMorphs false`, the FBX has 0 shape keys), so they are lost. Export the correctives as blend shapes (a morph rule for
   the corrective names; the exact G9 / G8 names from a test export's list) and drive each by its joint angle at runtime,
   the way converted characters already drive VaM's pJCM shapes (`JcmDef` in Morph.cs). This is DAZ's own fix for this
   mesh, so it comes before anything hand-made.
2. **Auto clavicle** (avatar only, if a recording shows it is needed): when her upper arm rises above ~30 degrees and her
   Scapula does not follow, give the avatar's collar a share of the elevation. The same idea as the helper-joint clavicle
   rigs the user pointed at (Paul Popov's Advanced Clavicle Rig, MIT, Maya: an IK clavicle plus scapula / trapezius /
   pectoral / latissimus helper joints on aim and point constraints, e.g. the pectoral joint half way between clavicle and
   chest). Helper joints of that kind need skin weights painted on the avatar (manual tools such as the Weight Paint Box
   Blender add-on, paid); worth it only for avatars that come without correctives (VRChat-style), once per base mesh.

### The bundle

- **Export** stays as now: DAZ via `daz_export.dsa` (FBX at base resolution with the follower figures and collected
  textures, `<fbx>.materials.txt` channel -> image). An option to keep the figure's hair and outfit nodes visible is the only
  change (v2). VaM Genesis 2: the SfExport folder has no skin weights (OBJ + joints); either `SfExport.cs` also writes the
  `DAZMergedMesh` weights (to look up in VaM's Assembly-CSharp), or the weights are transferred in Blender from a weighted
  Genesis 2 Female FBX exported once from DAZ Studio. Open.
- **Build** (step 2, built 2026-10-17): `powershell -File toolsvataruild.ps1 -Fbx <character.fbx> [-Name N] [-Out dir]
  [-MaxSize 4096] [-MapSize 2048]`, a separate Unity 2022.3.9 project `tools/avatar/SfAvatarBundle` (not the shader project:
  that one stays small and its compute bundle unaffected; Linear colour space like the game). build.ps1 copies the FBX,
  `<fbx>.materials.txt` and every texture it names plus `<name>.images` into `Assets/Avatars/<Name>` (git-ignored: never
  commit a character), then runs `AvatarBundle.Build` and loads the result back with `AvatarBundle.Verify`. Output
  `%LOCALAPPDATA%\SfMorphvatars\<Name>\` (`<name>` bundle + `avatar.txt`); nothing goes into the game.
  - Import: Generic rig with no avatar (rig type None drops the skinning), the Animator removed, **Read/Write on** (readable
    meshes: soft zones, GPU contact and the HerBody hulls need no GPU readback, unlike her meshes), blend shapes kept, no
    animation, 4 weights per vertex, file units (DAZ's centimetres come in as metres).
  - DAZ's follower figures (eyes, lashes, brows, tear, mouth) are rebound onto the main skeleton by bone name when the rest
    transforms agree (0.1 mm / 0.06 degrees); bones only a follower has (the G9 mouth's tongue01-05) are grafted onto the
    main skeleton under their nearest common ancestor; the duplicate skeletons and DAZ's stray 'Cube' are removed.
  - Textures per surface from DAZ's materials.txt (Base Color, Normal Map, Detail Normal Map, Specular Lobe 1 Roughness,
    Translucency Color, Cutout Opacity), else the FBX's own material slots (Unity hands those over as asset GUIDs): the eyes,
    lashes, brows, tear and mouth come from there. Albedo / translucency sRGB BC7, normals BC5, maps linear; albedo and
    normals up to 4096, the rest 2048. Generated: `<rough>_smooth` (1 - roughness, BC4, for her Skin shader's
    SmoothnessMap.r), `<rough>_mask` (HDRP mask map, A = smoothness), `<albedo>_<opacity>` (albedo with the cut-out in
    alpha: brows). Each surface's constant colour (black lashes, brown brows, the mouth's tint) goes into avatar.txt too.
  - Roles by surface name: skin, nails, eye, cornea, tear, lashes, brows, mouth, teeth.
  - The bundle carries **no shaders**: HDRP is not in the project, and shader variants built outside the game are the usual
    reason such bundles render pink or wrong. The prefab's materials are Unity's FBX defaults, named by surface: the plugin
    replaces them by name.
  - RKS Krista 9: 160.8 MB, 6 skinned meshes (body 27 087 vertices, 138 bones; eyes, lashes, brows, tear, mouth rebound,
    5 tongue bones grafted) on one skeleton of 143 bones, 18 surfaces, 29 textures, 1.683 m tall; loaded back: every mesh
    readable, every bone present, the skin's rest pose exact (bone x bind pose = the renderer's space within 0.00001).
- **Materials at load**, from the game's own loaded shaders (so keywords, stencils and SSS match the game's HDRP build).
  Route A: a copy of her body / face Skin material per avatar skin surface with the avatar's albedo / normal / roughness
  maps, every overlay layer zeroed (`_BlushPower`, `_AttachColor_*` alpha, `_AttachMap_*`, `_AddMap`, `_NormalMap_2`: the
  bake's clash) and tints neutral, plus a brightness gain from the albedo means (as matcheck.py does) shown as a slider.
  Route B: `HDRP/Lit` from `Shader.Find` with subsurface scattering and her skin's diffusion profile hash, only if the game
  has that shader loaded (logged). Eyes, cornea, tear: copies of her eye / tear materials; lashes, brows: alpha-clipped;
  mouth, teeth, nails: Lit or her mouth material. A panel switch A / B in the first headset run decides the default.
- **Load**: `AssetBundle.LoadFromFile`, instantiate beside her armature under her character root (not under her bones:
  `HightSetting` moves her root for heels and the avatar must follow it), on her layer; rebind each follower figure's
  renderer `bones[]` to the main skeleton bone of the same name (bind poses checked equal within 0.1 mm, else the copy is
  driven as in the check script); her skin, eyes, lashes, brows, hair and clothing renderers hidden with
  `forceRenderingOff` (the game's own `SetActive` swaps keep working underneath, nothing of hers is destroyed). Off =
  everything given back.

### Her systems: what carries over

| System | v1 (first in-game step) | Plan |
|---|---|---|
| Pose: animation, FinalIK, Overlap, planted lock, Embody, Reactions' flinch | **Carries over**: they move her bones, the avatar follows | - |
| Physics hands, VR hands, partner capsules | Carry over (partner side) | - |
| HerBody segments (hand colliders, pushes, yield) | **Needed in v1**: refit the hull points on the avatar's surface (avatar skin in its aligned rest, expressed in her segment frames), else your hands touch an invisible her | Same code with a mesh source |
| Body / Bones sliders, converted characters, slider presets | Off and greyed ("avatar mode: the avatar's own shape"): scaling her hidden bones would only bend the pose source | The Detail table on the avatar's canonical bones; DAZ morphs exported as blend shapes |
| Face: the game's face Animator, ReactionsFace clips, lip sync, voice jaw | Jaw and eyes carry over (shown offline); blink from her lid aperture | G9 has a bone face rig (lip corners, lips, cheeks, brows, lids, squint, nostrils): a face map from her face bones (moves in the head frame scaled by the eye distance), built like ReactionsFace.Seed's mouth frame; G8 / G2 faces are morphs: ARKit / viseme blend shapes when the export carries them (LipSync already produces ARKit weights) |
| Soft zones (SoftSim) | Off | The weight split on the avatar's readable mesh, zones anchored to mapped bones; G9's pectoral bones get a DynamicBone (the game's type, by reflection) for the breasts |
| GPU contact (dents, ripples) | Off | ContactBake on the avatar renderer: bone classes by canonical key instead of her names, Tissue tables unchanged, one body mesh (no seam stitching) |
| Skin marks | Off | With GPU contact; the albedo slot by material route |
| Wrinkles, muscles | Off | Authored per UV layout: `tools/wrinkles/author.py` / `tools/muscles/author.py` once per base figure (all G9 characters share the G9 UVs), maps in the avatar bundle; MusclePose keeps reading her bones |
| Clothes, hair | Her clothing and hair hidden (fitted to her shape and head): a bald, nude avatar unless the export includes hair | The figure's own DAZ hair / outfit (fitted by DAZ, follower figures rebound like the eyes), shown by her outfit groups' active state |
| Partner IK targets (his hands on her hips, breasts, wrists) | They target her bones; the avatar's surface differs by its proportions (offline: 30-70 mm at the extremities) | Weighted limb IK (above); a per-region surface offset for his hand targets later |
| Export, pose recorder, SF Neural Rendering VR | Unchanged (they read her / are independent) | - |

### Steps

1. Offline retarget check (done, above); the same on a real recording.
2. The bundle project and builder (`tools/avatar`), the materials manifest; a test bundle of Krista 9 (done 2026-10-17).
3. Plugin: `Avatar.cs` (load, hide, drive, give back) + the HerBody refit + a Characters tab *Avatars* list + `[Avatar]`
   config (scale mode, IK weights, material route), exclusive with converted characters. **Ask the user before building or
   installing anything into the game.**
4. G9 face map and blink; DAZ correctives exported and driven; material tuning from the first headset run.
5. Soft zones, GPU contact and marks on the avatar.
6. Wrinkle / muscle layouts per base figure; hair and outfits.

## Order

1. Adapter v1 for FBX / GLB / VRM with the Unity Humanoid, Mixamo, VRM, DAZ G3/G8/G9 and AdvancedSkeleton tables; the
   round-trip self-test (her own export through the adapter must give a near-zero morph).
2. `.unitypackage` / `.fbx.meta` humanoid mapping and Unity material slots (VRChat avatars as sold).
3. UABEA-based offline export of her (optional).
4. Avatar replacement mode (design and step 1 above).
