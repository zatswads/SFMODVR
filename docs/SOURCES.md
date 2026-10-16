# Character sources beyond VaM

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
  project in tools/shaders already builds Unity 2022.3.9 bundles (the avatar replacement mode below would use that).
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

VRChat does not reshape a base body: it drives any humanoid avatar from the player's pose through Unity's Humanoid
retargeting. The same works here: build her a Unity `Avatar` at runtime (`AvatarBuilder.BuildHumanAvatar` from her
AdvancedSkeleton bones), read her pose each frame with `HumanPoseHandler.GetHumanPose`, and set it on an avatar loaded from
an asset bundle built in Unity 2022.3.9 (the bundle project in `tools/shaders` already builds bundles for this game), with
her renderers hidden. Gains: any avatar, its own shaders and materials as made. Costs: her soft zones, GPU contact, wrinkles,
muscles, skin marks, clothes and the game's face animation (bone-driven; avatars use blendshapes / visemes) do not carry
over without per-system work; the partner's hand IK targets her bones, which the avatar follows only as closely as the
proportions match. A separate project, after the adapter.

## Order

1. Adapter v1 for FBX / GLB / VRM with the Unity Humanoid, Mixamo, VRM, DAZ G3/G8/G9 and AdvancedSkeleton tables; the
   round-trip self-test (her own export through the adapter must give a near-zero morph).
2. `.unitypackage` / `.fbx.meta` humanoid mapping and Unity material slots (VRChat avatars as sold).
3. UABEA-based offline export of her (optional).
4. Avatar replacement mode (separate design).
