# SF Body Morphs — handoff

State at **0.7.3** (branch `main`). 0.7.3 (GPU per-vertex contact, pose-dependent sag) built, not yet run in the game; 0.7.2 not yet reported on.

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
| `Plugin.cs` | Entry point, `ConfigEntry`s, hidden `Runner` (Update = key + scan, OnGUI = panel), `ApplyStep` = the apply step inserted in Unity's PlayerLoop before `PreLateUpdate.ScriptRunBehaviourLateUpdate` (re-checked every 2 s; LateUpdate fallback), unfocused hotkey via `GetAsyncKeyState`. |
| `GameRig.cs` | Reflection into Assembly-CSharp: the woman (`MovePlayerAndGirl.reika`) and her body-part bones from the game's own references. |
| `Body.cs` | `Detail` table (slider: group, Op Size/Stretch/Move/Length, Dir, bones by game part → exact rig names → keywords, range, Keep), `Character` (renderers, skin bones, slider bones, bind-pose axes/move directions, look), scan every 2 s (cached; rebuilt only when the renderer set changes), apply/restore trackers for scale and position, one `Character '...'` log block per session. |
| `Look.cs` | A look (bone factors, slider values keyed by `Detail.Id` in `Regions`, file token `region`), text format, `Store` (autosave/presets in `BepInEx\config\SfMorphs`). |
| `MeshData.cs` | A mesh's data in game vertex order: CPU if `isReadable`, else GPU readback (`Mesh.GetVertexBuffer/GetIndexBuffer` + `GetData`, decoded by `GetVertexAttributeFormat/Offset/Stream/Dimension`, submeshes from `GetSubMesh`). All her meshes are GPU-only. Basis for the export and the future morph loader (readable clones). Bind poses are readable (0.5.0 log: slider directions worked). |
| `Export.cs` | Presets tab export: per mesh `.obj` (bind space, game vertex order, `g` per submesh) + `.skin.txt` (bones, bind poses, 4 weights/vertex), `skeleton.txt`, `meshes.txt`. Skips meshes that aren't `isReadable`. |
| `Morph.cs` | Characters (0.6.0): `MorphSet` (a converted character folder), `MorphState` per character (readable clones from MeshData + `AddBlendShapeFrame`, follow blendshapes for clothes via `SkinRef` nearest skin vertex + push-out, joint offsets as local moves for Body's trackers with matching bind poses, DynamicBone-simulated bones excluded, baked textures with restore). |
| `SoftSim.cs` | Soft zones' per-frame spring sim (0.7.0): helpers' rest place from their bone, damped spring at 1/120 s substeps, contacts against `SoftCapsule`s (push out, velocity transfer), clamp (in ≤ limit, out ≤ 0.3, sideways ≤ 0.5), bulge to neighbours, writes S; debug markers. Runs in `Body.ApplyAll` after `Apply(c)`. |
| `VrHands.cs` | `SoftCapsule` + who can touch her (0.7.0): the user's hands via `VR_HandController.controller_left/right`, partner rigs (humanoid or AdvancedSkeleton wrists/fingers), her thighs/forearms/hands; mirrors capsules into the game's breast DynamicBones; one-time `VR rig survey`. Refreshed every 2 s. |
| `PhysicsHands.cs` | Physics hands (0.7.0): hand mesh cut from the player's body (or primitives), palm rigidbody following the controller by velocity in `Runner.FixedUpdate`, visual pose = controller ∘ physics offset (no added latency), grip curl, her body as kinematic capsules (inset so the soft zones give first), two private layers. |
| `PhysicsHands.Rig.cs` | Hands v2 (0.7.2): jointed finger rigidbodies (proximal + fused middle/distal, ConfigurableJoint slerp drives with capped force = compliance), curl targets, tip-cast settle, kinematic fallback; palm force-capped follow (`Fcap = lerp(60,12,resist)`) + PD torque. |
| `Grip.cs` | Controller grip 0..1 per hand (VR_HandController.selectValue_*, Animator param, XR), once per frame; used by physics hands and Embody. |
| `Subdiv.cs` | Fidelity: one level PN-triangle subdivision in place (same Mesh object, so MorphState's swap check holds), seams welded, weights/blendshapes interpolated, exact Undo. |
| `Overlap.cs` | Secondary motion: second-order spring follow of her limb/head/spine local rotations after the animation, faded on contact, reset on cuts. |
| `GpuContact.cs` | GPU contact (0.7.3): loads `sfmorph_shaders` (tools/shaders, built with Unity 2022.3.9f1 by build.ps1), sets `vertexBufferTarget |= Raw` on her soft renderers, dispatches `SoftDeform.compute` Imprint (exact ray-capsule exit along the inward normal, <= limit x mask) on the CURRENT skinned buffer once per frame after skinning (PlayerLoop step after UpdateAllSkinnedMeshes, SRP beginContextRendering fallback); skinning space calibrated by AsyncGPUReadback vs CPU skinning; never writes the previous buffer (motion vectors stay right). |
| `Embody.cs` | Embody (0.7.0): partner rig (Player/BadGuy), view snap/follow by moving the XR rig root (undone exactly), head hidden (scale 0.001, tracker), two-bone arm IK to the controllers with automatic hand frame, override detection in `Application.onBeforeRender`. |
| `SoftZones.cs` | Physics (0.6.3): per zone (glutes, thighs in/out, belly, breast front/side) a rest point under her skin, helper pairs (anchor = DynamicBone root with an excluded second child so it is never re-aimed, soft = simulated particle hanging along the skin) under each bone that weights the zone, weights split w → w(1-f) + w f, bind poses appended (follow `MorphState.BindVersion`); readable clones of the game's meshes when no character is on, MorphState's clones extended in place otherwise (`SoftZones.Original` keeps MorphState cloning the game's mesh); DynamicBoneCollider capsules on partner hands (humanoid Animator or bone names), her forearms/hands/thighs; never adds children to game DynamicBone chain bones with < 2 children. |
| `Panel.cs` | F8 window: character buttons, tabs Body (collapsible groups) / Bones / Presets / Characters, filter + 80-row cap, greyed rows with reasons. |

### How changes are applied (keep this model)

**When**: after animation, before LateUpdate scripts (PlayerLoop step). Applying after DynamicBone made the breast
physics jitter in 0.2.x. **What**: everything is relative to the game. Bone scale = game's scale × factor;
bone position = game's position × length factor + offset.
A tracker keeps, per shape/bone, the base and the value we last wrote: if the current value differs from our
last write, the game changed it, so it becomes the new base; otherwise the base stays (no accumulation on
values nobody else writes). Anything no longer changed (slider reset, Apply off, error) is given back to its
base if it still holds our value. Keep: the bone gets f, the part's own helpers below it (name starts with the
bone's stem: `HipPart1_L` under `Hip_L`) keep it, the next joint gets 1/f (axis-mapped). Only uniform scale is
safe on a bone whose children rotate (non-uniform skews them), so Stretch is allowed on leaves, or with Keep on
the spine where children barely rotate. Moves use bind-pose directions: character left = Hip_L - Hip_R, up =
pelvis → chest, forward = up × left (checked: toes in front of ankles), mapped into the parent's local space.

## Her rig (from the 0.2.1 log)

AdvancedSkeleton naming: `Root_M` (pelvis) › `Hip_L` › `HipPart1_L` › `HipPart2_L` › `Knee_L` › `Ankle_L` › `Toes_L`;
`Spine1_M` › `Chest_M` › `Neck_M` › `NeckPart1_M` › `Head_M`; `Scapula_L` › `Shoulder_L` (+Part1/2) › `Elbow_L` (+Part1/2) › `Wrist_L`.
Shaping helpers: `Left_UpperBreast` (DynamicBone root) / `Left_LowerBreast` / `Left_Breast_up|down|in|out` /
`Left_Nipple`; `Butt_L` › `ButtSkin_L`; `LegFrontSkin_L`, `LegInsiteSkin_L`, `LegOutsiteSkin_L`; `Uterus_belly`;
face: `Cheek_Left(_Raiser)`, `Eyebrow_*`, `Eyelid_*_skin_*`, `Lip_*`, `Jaw_M`, `Eye_L/R`; plus genital/rectum/uterus
chains and per-outfit `Panties_Skin_*`. **No mesh has blendshapes.** Exact parents of the helpers are logged by 0.3.0.

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

- 0.3.0 untested in game: jitter gone? which helper sliders visibly do something (helper bones may carry few
  skin weights); check the `sliders:` line for each slider's bones, parents and child counts.
- 0.4.0 face sliders untested: which visibly work, whether moves fight the expression animation.

## Character pipeline

See `docs/PIPELINE.md` (VaM look → her: her export 0.5.x; VaM exporter `tools/vam/SfExport.cs`; fit/bake
`tools/pipeline/run.ps1` and the runtime loader + Characters gallery in 0.6.0). Local toolchain on the user's PC:
.NET 8 SDK in `%LOCALAPPDATA%\Microsoft\dotnet` (for `dotnet build`), Blender 5.2, scipy in `%LOCALAPPDATA%\SfMorph\pydeps`. VaM facts (VaM Assembly-CSharp survey):
Person skin = `DAZMergedSkinV2`, mesh = `skin.dazMesh` (`DAZMergedMesh`, metres); morphed unposed UV vertices via
`DAZCharacterRun.doSnap` → `snappedMorphedUVVertices`, or `dazMesh.morphedUVVertices`; faces `dazMesh.UVPolyList`
(MeshPoly: materialNum, vertices, may be quads); bones `skin.root.dazBones`, `morphedWorldPosition` = rest frame of the
morphed mesh; textures `skin.GPUmaterials[i]` slots `_MainTex _SpecTex _GlossTex _BumpMap _DecalTex`; plugins write
with `FileManagerSecure` under `Saves/PluginData` (no prompt). To compile-check the plugin here: mcs against VaM's
Assembly-CSharp + Unity module refs + a stub UnityEngine.UI (Button.onClick).
User decisions: personal use only; no Appearance tab (DLSS 5 NR repaints materials anyway); face likeness from AI
character sheets via FaceGen or VaM. Not building selfie/photo-of-a-real-person features.

## Roadmap (agreed direction, in order)

1. **Bone sliders** (done, 0.3/0.4): limited to the bones the rig has. Face has no nose, ear or forehead bones.
2. **Appearance tab** (next, cheap): the woman's material parameters the game already uses
   (`LadyCustomiseFunction`: `_SkinColor`, `_BlushPower`, `_SmoothnessPower`, hair `_HairColor`, eye/lip colours on
   `ladyFaceMat[]`, `ladyBodyMat`, `ladyEyeMat`, hair mats) as continuous sliders and colour pickers, re-applied
   after the game's `DefaultSetting()` (mesh/material swaps). Log each material's float/colour properties first.
3. **Real morphs (runtime blendshapes)**, the VaM-quality path: copy each body/face mesh, `Mesh.AddBlendShapeFrame`
   with deltas, swap the copy into the renderer (re-do on the game's mesh swaps). Deltas from (a) procedural
   regions (push along normals with falloff around a point/bone weights: nose width/bridge, ear tips, forehead,
   belly, areola) and (b) shapes sculpted in Blender on the extracted game mesh, exported as a per-vertex delta file
   the mod loads (same vertex order). Clothes must follow: transfer each body morph to clothing meshes by nearest
   body vertex in bind space at load. Biggest win, most work; needs a mesh-extraction step on the user's PC.
- Also possible: hook the game's own customise save slots (`CustomPoseDate`) so a look follows the game's presets.
