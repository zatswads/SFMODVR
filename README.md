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

- **Body**: size of breasts, nipples, glutes, hips, waist, ribcage, thighs, calves, upper arms, forearms,
  hands, feet, neck, head, by scaling bones. Bones come from the game's own rig references where it has
  them (see 0.2.0), otherwise from bone names. Parts that should only thicken (thighs, arms, hips, ...)
  scale their children back, so a bigger thigh doesn't make a bigger calf and foot. A part with no bone
  is greyed out with the reason.
- **Morphs**: every blendshape on her meshes (body and clothes share a slider when the name matches).
  The value is *added* to the game's weight, so expressions and lip sync keep working underneath;
  -100..200 like VaM's over-range. Greyed out if her meshes have no blendshapes.
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
