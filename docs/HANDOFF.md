# SF Body Morphs — handoff

State at **0.2.1** (branch `main`). Not yet run in the game. Read this first, then `docs/TESTING.md`.

## What this is

A BepInEx 5 (Mono) plugin, `dist/SfMorph.dll`, for **Sex Formula** VR (Unity 2022.3.9, HDRP 14, D3D11,
OpenXR). VaM-style editing of the game's woman: body-part sizes (bone scaling), the game's blendshapes as
additive sliders, per-bone scale, per-character autosave and named presets. F8 IMGUI panel in the desktop window.
It runs alongside SF Neural Rendering VR (`zatswads/UDLSS5VR`, `SfNr.dll`, F10/F6); they share nothing.

## The user and their setup

- Quest Pro over Virtual Desktop, RTX 4090 Laptop. Game: `E:\Bak\New folder\Misc\SF\VR\`.
  Plugin: `BepInEx\plugins\SfMorph.dll`. Config: `BepInEx\config\sfmodvr.sfmorph.cfg`.
  Looks: `BepInEx\config\SfMorphs\{characters,presets}\*.txt`. Log: `BepInEx\LogOutput.log`.
- They test in the headset and send the log + desktop-mirror screenshots. Concise replies, explain *why*,
  settings that can't apply are greyed out with the reason, remove options that don't earn their place.

## Working rules

- Develop and push on `main`; no PR unless asked.
- Every release: bump `Plugin.Version`, README changelog entry, rebuild `dist/SfMorph.dll`, commit, push.
- Commit trailer (no model names anywhere in the repo):
  ```
  Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
  Claude-Session: <session URL>
  ```

## Build

```
REFS=<folder with reference DLLs> sh build.sh      # -> dist/SfMorph.dll
```

`REFS` needs, from the game's `Sex Formula_Data\Managed`: `UnityEngine.dll`, `UnityEngine.CoreModule.dll`,
`UnityEngine.IMGUIModule.dll`, `UnityEngine.TextRenderingModule.dll`, `UnityEngine.InputLegacyModule.dll`;
and `BepInEx.dll` from BepInEx 5.4.23.2 `core`
(`https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip`, reachable
from a cloud session). A fresh cloud session doesn't have the game DLLs: ask the user to upload them (never
commit them). Toolchain: `apt-get update && apt-get install -y mono-mcs mono-devel ikdasm`.
`Assembly-CSharp.dll` is **not** referenced (read by reflection); ask for it only to investigate the game
(`ikdasm Assembly-CSharp.dll > ac.il`, then grep; hand the IL to a subagent, it's 225k lines).

## Architecture (`src/SfMorph/`)

| File | Role |
|---|---|
| `Plugin.cs` | Entry point, `ConfigEntry`s, hidden `Runner` object (`DefaultExecutionOrder(30000)`: Update = key + scan, LateUpdate = apply, OnGUI = panel), unfocused hotkey via `GetAsyncKeyState`. |
| `GameRig.cs` | Reflection into Assembly-CSharp: the woman (`MovePlayerAndGirl.reika`) and her body-part bones from the game's own references. |
| `Body.cs` | `Region` table (part → name keywords, range, Keep = scale children back), `Character` (renderers, skin bones, shape names, region bones, look), scan every 2 s (cached; rebuilt only when the renderer set changes), apply/restore trackers, one-shot `Character '...'` log block. |
| `Look.cs` | A look (shape offsets, bone factors, region factors), text format, `Store` (autosave/presets in `BepInEx\config\SfMorphs`). |
| `Panel.cs` | F8 window: character buttons, tabs Body / Morphs / Bones / Presets, filter + 80-row cap, greyed rows with reasons. |

### How changes are applied (keep this model)

Everything is relative to the game. Shape weight = game's weight + offset; bone scale = game's scale × factor.
A tracker keeps, per shape/bone, the base and the value we last wrote: if the current value differs from our
last write, the game changed it, so it becomes the new base; otherwise the base stays (no accumulation on
values nobody else writes). Anything no longer changed (slider reset, Apply off, error) is given back to its
base if it still holds our value. Region factors with Keep multiply the bone by f and its direct children by 1/f.

## Game facts (from Assembly-CSharp, 0.2.0 survey)

- One woman, "reika": `MovePlayerAndGirl.reika` (GameObject); also `.player`, `.badGuy`. No singletons; use
  `FindObjectOfType(type)`.
- The game has **no** blendshape code and no body `localScale` writes. Customisation (`LadyCustomiseFunction`)
  = mesh swaps (`faceType[]`, `nudeBody[pregnantNum]`, nipple/pussy/hair types via `SetActive`) + material
  params; event-driven (`DefaultSetting()`, `FaceType`, `PregnantFunction`), no Update. Saved by
  `Customise_Library` / `UserInterface_Library_Customise` with BinaryFormatter in `<dataPath>/SaveData/CustomPose`.
- Bones are never named in code. References: `FacialFunction.bipedIK.references` (FinalIK `BipedReferences`:
  pelvis, spine[], thighs, calves, feet, upper arms, forearms, hands, head), breasts:
  `LadyBreastPhysics_Function.left/right_breast_Physics.physicsBone[].m_Root` (DynamicBone) and
  `left/rightBreastCCDIK.solver.bones[]` (FinalIK CCDIK, weight 1 only while grabbed).
- Per-frame writers: DynamicBone (Update resets chain local pos/rot to init; LateUpdate writes world pos/rot from
  particle 1 on, never the root, never scale), FinalIK (pos/rot), `FacialFunction` (Animator layers/params for
  expressions, blink, lip sync baked in clips; eyelid helper uses hard-coded offsets → head scale skews it a bit).
  Voice-language change swaps the Animator controller (rebinds). ~70 Timelines (`PlayableDirector.Play`) may
  animate the body.
- `LadyCustomiseFunction.HightSetting` moves the woman's root for shoe heels: never scale/move her root.

## Open items

- 0.2.x untested in game. From the first log: `Game rig:` line, the `Character '...'` block (which bone each
  part got and whether it says "(game rig)"; do her body/face meshes have blendshapes at all?).
- Glutes and nipples still go by bone-name keywords; tune `Region.All` from the logged bone list.
- Ideas: hook the game's own save slots (`CustomPoseDate`) so a look follows the game's customise presets;
  translate/offset sliders (VaM "position" morphs) if bone scaling isn't enough.
