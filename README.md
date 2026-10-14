# SFMODVR — SF Body Morphs

VaM-style body editing for the woman in **Sex Formula VR** (Unity 2022.3.9f1, HDRP, Mono). A BepInEx 5
plugin, `dist/SfMorph.dll`, independent of the SF Neural Rendering VR plugin
([UDLSS5VR](https://github.com/zatswads/UDLSS5VR)): own GUID (`sfmodvr.sfmorph`), config
(`BepInEx\config\sfmodvr.sfmorph.cfg`), key and panel, no Harmony patches, so both load side by side.

## Install

1. BepInEx 5 x64 in the game folder (already there if SfNr runs).
2. Copy `dist/SfMorph.dll` to `BepInEx\plugins\`.
3. In game, **F8** opens the panel in the desktop window (works while Virtual Desktop/SteamVR has focus).

## What it does

- **Body**: about 70 sliders in seven groups (Breasts, Glutes, Hips & legs, Torso, Arms, Head & neck, Face), built on
  her rig's bones (the game's meshes have no blendshapes): sizes, lift/spacing/projection moves in cm, limb and
  neck lengths, waist/ribcage width and depth, and the rig's shaping helpers (breast top/bottom/inner/outer,
  thigh front/inner/outer, glute fullness, belly; face: eyelids, eye corners, brows, cheeks, lips, mouth, jaw,
  chin). A slider whose bone is missing, or
  that would skew the bones under it, is greyed out with the reason.
- **Bones**: X/Y/Z scale of any skin bone.
- **Presets**: her changes save to `BepInEx\config\SfMorphs\characters\<name>.txt` as you edit and come
  back when she appears; presets (`...\presets\`) store a look by name to load later.
- **Apply changes** off shows the game's own look (A/B); every value it changed is given back.

Big changes to hips, thighs or arms move where bodies meet in the game's animations; head size
nudges the game's eyelid tracking a little.

## Build

```
REFS=<folder with reference DLLs> sh build.sh      # -> dist/SfMorph.dll
```

See `docs/HANDOFF.md` → Build for the DLL list. Windows: `dotnet build src/SfMorph -c Release -p:GameDir="..."`.

## Changelog

0.4.0:
- Face: a Face group (24 sliders) on her facial bones: upper/lower eyelids, outer eye corners (tilt), eye width,
  brow height / inner / outer / ridge, cheek size / height / fullness / width, lip size, upper and lower lip
  fullness, mouth width / corners / height, jaw size / width, chin projection / height; plus face width and
  length under Head & neck. They add to her expressions, blinks and lip sync rather than freezing them. The rig
  has no nose, ear or forehead bones, so those can't be shaped with bones (see the roadmap in HANDOFF).

0.3.0:
- Breast (and hair) physics jittered after changing a size. The changes were applied after the game's
  LateUpdate scripts, so DynamicBone simulated the unchanged body and was then resized under it: its
  particles and the bones disagreed every frame. The changes now apply in Unity's frame loop right after
  the animation and before the scripts' LateUpdate, so the physics and IK simulate the changed body. The
  log says `Body changes apply after the game's animation, before its physics and IK`.
- Detail sliders: the Body tab is now grouped (click a group to open it) with ~50 sliders using the rig's
  own helper bones (`Left_Breast_up/down/in/out`, `LegFrontSkin/LegInsiteSkin/LegOutsiteSkin`, `ButtSkin`,
  `Uterus_belly`, `Scapula`, cheeks, brows, lips, jaw) plus moves (cm), lengths and widths. Directions come
  from the rig's bind pose, so "up", "out" and "forward" stay the same however she's posed. Old saved
  looks keep their values (same ids for the original parts).
- Neck size now scales the whole neck (`Neck_M`), not only its upper helper bone.
- The Morphs tab is gone: none of her 36 meshes has a single blendshape (log), so it could never do anything.
- The log describes her once per session (with each slider's bones, their parents and child counts) instead
  of on every outfit change.

0.2.1:
- Moved to its own repository (SFMODVR). New plugin GUID `sfmodvr.sfmorph`, so its settings file is now
  `BepInEx\config\sfmodvr.sfmorph.cfg` (only the panel key, size and position lived in the old
  `udlss5vr.sfmorph.cfg`; delete it). Saved looks in `BepInEx\config\SfMorphs` are unchanged. If an older
  `SfMorph.dll` is still in `BepInEx\plugins`, replace it (don't keep both).

0.2.0:
- Finds the woman the way the game does: everything under `MovePlayerAndGirl.reika` (the game has
  exactly one). Name guessing is only a fallback now, so the player and the male partner are never listed.
- Body parts use the game's own bone references instead of bone names: FinalIK's BipedIK on
  `FacialFunction` (pelvis, spine, thighs, calves, feet, arms, hands, head; neck = the head's parent)
  and the breast physics chains on `LadyBreastPhysics_Function` (DynamicBone roots, else the breast-grab
  IK). The game's bones have no names anywhere in its code, so 0.1.0's name matching was a guess.
  Glutes and nipples still go by name. The panel says which way each part was found.
- Why this is safe with the game's systems: the game never scales bones or sets blendshapes in code
  (face types and pregnancy swap whole meshes, expressions and lip sync are Animator clips), and the breast
  physics (DynamicBone) and IK only move and rotate bones, so a scale on top survives and the physics follows it.

0.1.0 (in UDLSS5VR):
- First version. Women found by name (root object, bones, shapes); your own body (head at the camera)
  never listed. Each new character is logged once with its meshes, bones, shapes and which bones each
  body part matched.
