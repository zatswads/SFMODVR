# Character sources beyond VaM (design, not built yet)

The pipeline (`docs/PIPELINE.md`) reads one source format: an SfExport folder written by the VaM plugin, i.e. a Genesis 2
Female in VaM's vocabulary (`body.obj` with VaM material groups, `joints.txt` with Genesis 2 bone names, `materials.txt` +
textures). Everything after that is source independent in what it *does* (pose match by joint directions, landmark warp,
non-rigid fit, bake, material check, slider fit). So a new source needs only an **adapter** that writes the same folder.
On her side, 0.11.1 already handles every variant the game swaps (faces, nipples, nails, lashes, brows, bodies: `roles.py`).

## The adapter: any rigged humanoid -> an SfExport folder

`tools/sources/humanoid.py`, run in Blender (`blender -b -P humanoid.py -- --in <file> --out <SfExport dir>`):

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
