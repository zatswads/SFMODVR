# SF Body Morphs — handoff

State at **0.8.7** (branch `main`). 0.8.7 (face clips: a survey of her face Animator's clips by SampleAnimation with an exact
same-frame restore, mood picks, cached face-bone tracks played on a face-only layer before the bone sliders) built, not yet run in the
game. 0.8.6 (her reactions speak the game's recorded lines for the touched area by excitement level through
its own dirty-talk path, and its feeling sounds on a tier rise and at the climax) built, not yet run in the game. 0.8.5 (the soft points run after Embody, so the embodied partner's hand capsules dent her where
your controller holds his hand, not at his animation pose) built, not yet run in the game. 0.8.4 (crease shade: the wrinkles darken her albedo at the bottom of each fold, since the skin
shader's subsurface scattering blurs normal-map detail on the face; SkinMarks composes through Wrinkles' albedo copy) built, not yet run in
the game. 0.8.3 (hands: limb yield instead of push springs, planted lock for every chain, inertia ripple, drawn physics hand = controller in free air, finger conform fan, grab-button fingers, Embody neutral fingers, clasp, hand stop and elbow swivel) built, not run in the game. 0.7.9 ran once (log 2026-10-15 19:40): the follow fix works (free-air flips 0-2 %, at the cap 0 %). Also on main: 0.8.2 (reactions fixes) and 0.8.1 (contact visuals v2: smooth GPU dents with anatomical depth, bent normals and a rim bulge, self-contact and the partner's body, bend bulges, skin marks, ripple; design-0.8.0-contact-visuals.md in the session notes) built, not yet run in the game. 0.7.9 (reactions and states, Reactions tab, dirty-talk voice guard) and 0.7.8 (physics-hand follow fix,
hull colliders, pushes from any hand on limbs/spine/head, friction, Physics/VR tabs) built, not yet run in the game. 0.7.7 ran once (log 2026-10-15 14:51): GPU contact and wrinkles load and run; the
physics hands stuttered (fixed in 0.7.8). GPU contact (0.7.3) and wrinkles (0.7.5) never ran before 0.7.7: the bundle lacked its
AssetBundle object (the bundle project's Packages/manifest.json must list com.unity.modules.assetbundle; build.ps1 checks it).

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
| `SoftSim.cs` | Soft zones' per-frame spring sim (0.7.0): helpers' rest place from their bone, damped spring at 1/120 s substeps, contacts against `SoftCapsule`s (push out, velocity transfer), clamp (in ≤ limit, out ≤ 0.3, sideways ≤ 0.5), bulge to neighbours, writes S; debug markers. Runs at the end of `Body.ApplyAll` (`SoftStep`; 0.8.5: after Embody, so his hand capsules are at your controllers). 0.8.1: per-zone stiffness/depth/damping from `Tissue.Zone`, resistance `Tissue.Resist`, fresh fast contacts (approach >= 0.7 m/s) into `Impacts` for Ripple, "partnerBody" gated by `SoftZones.PartnerBody`. |
| `VrHands.cs` | `SoftCapsule` + who can touch her (0.7.0): the user's hands via `VR_HandController.controller_left/right`, partner rigs (humanoid or AdvancedSkeleton wrists/fingers, nested rigs counted once at wrist level), her thighs/forearms/hands; palms = three 1.5 cm capsules; one-time `VR rig survey`. Refreshed every 2 s. 0.7.7: every capsule has a `CapSource` (your hand, physics hand, partner rig, her); `UpdateLive` (once per frame) marks gated sources dead while none of their renderers is drawn by the XR camera (hidden partner, controller models replaced by physics hands); the soft points, GPU imprint and breast mirror skip dead sources. Mirrors `Body.Contacts()` into the game's breast DynamicBones (`BreastColliders`: off / main `upperbreast` chains / all), detaching hidden sources (`MirrorLive`). `ContactsFeed` = what each source feeds. 0.8.1: her upper arms ("upperarm") and calves ("calf") too, and the partner's pelvis + thighs ("partnerBody", same gated source as his hands; Overlap, HerBody and the breast mirror ignore these kinds); `BreastDbs` for Ripple. |
| `PhysicsHands.cs` | Physics hands (0.7.0): hand mesh cut from the player's body (or primitives), palm rigidbody following the controller in `Runner.FixedUpdate`, grip curl, two private layers. 0.7.8: the controller pose is sampled once per frame with its linear/angular velocity (`Track`, in HerBody.cs) and extrapolated to `Time.fixedTime` of each step (feed-forward + gap gain 25/s, force cap); a rig jump (the game moving the VR rig) shifts the hand with its offset; the drawn hand = controller ∘ the offset of the last two steps interpolated by (time - fixedTime)/fdt, refreshed in onBeforeRender and again after Embody's view fix (`PreRenderRefresh`). Her colliders: one kinematic body per `HerBody.Seg` (`SegBody`), a convex hull cooked from `Seg.HullPts` (<= 2 per frame, inset toward the axis, 16-gon reduced fallback) or the capsule/sphere; friction materials (`Friction`). |
| `HerBody.cs` | Her body segments (0.7.8), shared by the physics hands and the pushes: segment defs, fit from her body meshes (bind pose + character morph deltas), hull points (12x16 grid in the segment frame: origin at joint A, Y along A->B, roll from `RollRef` = A or the owner bone for sibling joints such as the pelvis), `SegPose`, contact impulses from the physics hands (`PhysF`, 50 ms filter) and the geometric push from other hands (`GeoPush`: hand model/partner capsules vs her skin capsules, 300 N/m after 1.5 cm of skin give, measured after Overlap so the push fades as the limb yields), `PushOn` merge. |
| `FingerFan.cs` | 0.8.0: a finger's curl sweep fixed to the palm (tip + last-capsule probes at 8 curl fractions, forward kinematics of the rest pose, built once): `ConformPhysics` (CheckSphere on her layer, enter/stay hysteresis) for the physics hands, `ConformAnalytic` (HerBody.SkinDepth + bisection) for Embody. Open-loop: depends only on the palm pose. |
| `PhysicsHands.Rig.cs` | Hands v2 (0.7.2): jointed finger rigidbodies (proximal + fused middle/distal, ConfigurableJoint slerp drives with capped force = compliance), curl targets, tip-cast settle, kinematic fallback; palm force-capped follow (`Fcap = lerp(60,12,resist)`) + PD torque. |
| `Grip.cs` | Controller grip 0..1 per hand (VR_HandController.selectValue_*, Animator param, XR), once per frame; used by physics hands and Embody. |
| `Subdiv.cs` | Fidelity: one level PN-triangle subdivision in place (same Mesh object, so MorphState's swap check holds), seams welded, weights/blendshapes interpolated, exact Undo. |
| `Overlap.cs` / `Ik.cs` | Secondary motion v2 (0.7.7): second-order springs on 16 of her bones' local rotations (parents first), per-part bounce (zeta, frequency), ripple kick from the parent's world angular acceleration, planted hands/feet (animated end effector still, or still relative to a nearby partner/her capsule) held by an analytic two-bone IK (`Ik.TwoBone`, Embody's solve) while the chain lags, push springs (0.7.8: arms, legs, spine, neck/head via segment -> bone shares; per-region inertia/clamp/torque cap; tanh knee to 0.8 x clamp) from `HerBody.PushOn` (works at Overlap 0), faded on contact, reset on cuts. |
| `GpuContact.cs` | GPU contact (0.7.3): loads `sfmorph_shaders` (tools/shaders, built with Unity 2022.3.9f1 by build.ps1), sets `vertexBufferTarget |= Raw` on her soft renderers, dispatches `SoftDeform.compute` Imprint (exact ray-capsule exit along the inward normal, <= limit x mask) on the CURRENT skinned buffer once per frame after skinning (PlayerLoop step after UpdateAllSkinnedMeshes, SRP beginContextRendering fallback); skinning space calibrated by AsyncGPUReadback vs CPU skinning; never writes the previous buffer (motion vectors stay right). 0.8.1 v2 chain per renderer (`ContactBake`): Depth (exact capsule depth vs the tissue cap, Press knee, self-capsule skip masks in `Capsule.c.x`) -> Dilate x N (skirt) -> Smooth x2 (narrow) -> x3 (wide) -> [MarkDisc] -> Compose (narrow - rim bulge - bend - waves; marks) -> Grad (per canonical, clean positions) -> Apply (positions + bent normals + tangents, reads only its canonical's Grad). Capsules culled per renderer (bounds + 15 cm); idle 0 dispatches; any v2 error -> v1 for all; old bundle (no v2 kernels) -> v1 + a warning. `AfterImprint` event for SkinMarks. Her own limbs / his body alone in reach: Depth runs as a probe counting pressed vertices (`_Hits`, AsyncGPUReadback), the full chain only while > 0 (30 frames hold). Overlays (non-body v2 meshes: areola, tattoo, vulva) map each vertex to the body canonical within 5 mm at Sync (`ContactBake.WorldP`, CPU skin) and finish with ApplyMap (the body's Grad of this frame), so they move with the skin under them; bodies dispatch first. |
| `ContactBake.cs` | 0.8.1: `ContactSettings` (the [Physics] contact values, filled by Body each frame) and the per-renderer CPU bake for GPU contact v2: weld (WrinkleBake.WeldKey, canonical = first copy), CSR adjacency over canonical ids, bone class per vertex (design table 1.3), tissue (hard, soft) from `Tissue`, bend slots/weights, UV + triangles + metres per UV for SkinMarks. Never throws (a failed bake keeps v1 Imprint for that renderer). |
| `Tissue.cs` | 0.8.1: the anatomy table shared by the GPU dent (per-class hard/soft depth and floor, shin/biceps floors, `Press` knee curve: exact to 0.6 cap then a smooth saturation), SoftSim (per-zone stiffness, depth, damping) and the hands' resistance (`Resist` = (dent/cap)^2). Approximate on purpose: look and feel, not measurement. |
| `BendPose.cs` | 0.8.1: elbow/knee/hip angles vs the bind pose -> 9 bulge amplitudes (biceps, thigh back, thigh top, calf, belly fold) for Compose. |
| `Ripple.cs` | 0.8.1: impacts (SoftSim's fresh fast contacts via `SoftZones.Impacts`, Reactions' slaps) -> <= 8 bone-attached damped rings uploaded to Compose; breast impacts pulse the game's breast DynamicBones' `m_Force` for 0.12 s (tracker, restored only if still ours). |
| `SkinMarks.cs` | 0.8.1: per skin material a marks RT rasterised in UV space from GpuContact's per-vertex `_Marks` (SkinMarks.shader), composed into an albedo copy (redness, pressed darkening, pale print) and a normal copy, or through Wrinkles' blend pass when Wrinkles owns the normal slot (`Wrinkles.Touch`, `ExternalBase`); own `_FlipY` self-test; idle = slots restored and RTs freed after max(10 s, 4.6 x MarkFade). 0.8.4: while `Wrinkles.OwnsAlbedoSlot` (its albedo copy of the material), no albedo copy here: WrinkleRaster pass 4 composes the marks (`AlbedoParams`, same maths as SkinMarks.shader pass 1); one albedo RT per material. |
| `Wrinkles.cs` / `WrinkleBake.cs` | Skin wrinkles (0.7.5): CPU bake per renderer (neutral = bind pose + blendshapes + bone scale, weld groups, CSR adjacency, rest frames, UV Jacobians, patches) → `Wrinkles.compute` (TriStrain from live skinned edges, VertexGather with attack/release + axis hysteresis) before GpuContact's Imprint (BeforeImprint / own PlayerLoop step) → `WrinkleRaster.shader` (UV-space slope raster, blend into a copy of her `_NormalMap`, debug tint, _FlipY self-test). `ShaderBundle.cs`/`RawTarget.cs` are shared ref counts. 0.8.4 albedo copy per sheet while [Wrinkles] Cavity > 0 or Debug on (pass 4: base x SkinMarks' marks x crease shade from the slope RT's divergence, scaled by spacing / (metres per UV of the bake x 2) + debug tint; size = base capped by Resolution, 4096 max); Morph's skin state (`_texOn/_texKey/_set` by reflection, fallback `NormalFor`) changing with our copy in the slot = hand the base back for Morph's 2 s pass; while a character skin is on, a foreign texture in the slot is taken over only after 2.4 s; old bundle (4 passes) = debug only. |
| `Embody.cs` | Embody (0.7.0): partner rig (Player/BadGuy), view snap/follow by moving the XR rig root (undone exactly), head hidden (scale 0.001, tracker), two-bone arm IK to the controllers with automatic hand frame, override detection in `Application.onBeforeRender`. |
| `SoftZones.cs` | Physics (0.6.3): per zone (glutes, thighs in/out, belly, breast front/side) a rest point under her skin, helper pairs (anchor = DynamicBone root with an excluded second child so it is never re-aimed, soft = simulated particle hanging along the skin) under each bone that weights the zone, weights split w → w(1-f) + w f, bind poses appended (follow `MorphState.BindVersion`); readable clones of the game's meshes when no character is on, MorphState's clones extended in place otherwise (`SoftZones.Original` keeps MorphState cloning the game's mesh); DynamicBoneCollider capsules on partner hands (humanoid Animator or bone names), her forearms/hands/thighs; never adds children to game DynamicBone chain bones with < 2 children. |
| `Reactions.cs` | Reactions (0.7.9): per HerBody segment a touch classifier (physics-hand contact ENTER speed per hand `HitVn`, hand-model/partner approach speed `GeoVn`, force, duration -> brush/caress/grope/soft/hard/too-hard slap, sensitivity scales the 1.2/2.3/4.5 m/s thresholds), 10 zones, excitement E (tiers) and pain P with decay, states Idle/Aroused/Startled/Guarded/Climax/Afterglow (auto or buttons), the director (voice chance + arbiter + tier gap, face, flinch via `Overlap.Kick`/`SoftZones.Kick`, slap sound, blush pump, gaze-away envelope, desire feed), test buttons, logs. |
| `ReactionsGame.cs` | The game bridge for reactions, all by reflection (audio types too: UnityEngine.AudioModule isn't referenced): FacialFunction (MoanFunction, Blink, anim, facialLayer, functionPause), LadyVoice_Function gates, Sound_Function sources, GameSetting/SexMode/SexMode_State_System (desire gauge), voice library banks (Moan by length, Exhale_f32/f64/f128), SFX clips, own 3D slap AudioSource on the game's SFX mixer group, blush (`_BlushPower`) and look-at weight trackers, and the dirty-talk guard (rebuilds dirtyTalkVoice/dirtyTalkPlayNum only when PlayRandomVoice would throw). 0.8.6 lines: `BuildLines` (DirtyTalk_Library[lang] reikaNipple/reikaPussy/reikaAnal/public_DT_Lv1..3 banks, Feeling_enter_0..3), `SpeakLine` (game path `LineViaGame`: snapshot of the dirty-talk fields, level on FacialFunction.dirtyTalk_reika_level / _player_level, DirtyTalkTypeSelect(type), one-entry shuffle at the clip's index, PlayDirtyTalk(true), restore; fallback `LineOwn` on dirtyTalk_audioSource), `Feeling` (moan source, "moan" trigger). The director's `LineBlock` / `TryLine` / `TryFeeling` / `PelvisCat` are in Reactions.cs. |
| `ReactionsFace.cs` | 0.8.7 face clips: survey of `FacialFunction.anim.runtimeAnimatorController.animationClips` (`SampleAnimation` on the Animator's object, 3 clips per frame; snapshot/restore of every transform's local pose + activeSelf, renderer enabled + property block; a sentinel pose before a sample tells which face bones a clip writes, position only if all 3 axes; score = mm + 0.2 deg per written bone vs the bind face in Head_M's frame), ranked log + `face-clips.txt`, moods (keywords, strength, `[Reactions] FaceMap`), 15 Hz tracks of the written face bones, `Step` (envelope, lerp/slerp from the Animator's pose; mouth group skipped while she speaks/moans, eyeballs and hair never), `Watch` (which clips the game's eye reaction plays). Body.ApplyAll calls `React.FaceStep` for her before `Morph.Update`/`Apply`. |
| `Panel.cs` | F8 window: character buttons, tabs Body (collapsible groups) / Bones / Presets / Characters / Physics (soft zones, motion, pushes, wrinkles) / VR (Embody, VR hands, contacts) / Reactions (`Panel.Reactions.cs`: toggles, states, tests, voice guard), filter + 80-row cap, greyed rows with reasons. |

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

## 0.8.3 hands notes (design: design-0.8.0.md incl. addendum 13, in the session notes)

- Frame order: Apply → **HerBody.Update (yield query, before Overlap; Overlap.Unwind first puts the animation's rotation back on bones the
  animation didn't rewrite)** → Overlap.Update (lag + inertia ripple + yield filters + planted lock) → PhysicsHands.Update → Reactions →
  Embody → SoftStep (0.8.5: Soft.Simulate, Ripple, GPU capsule list, marks, breast mirror gate; after Embody so the embodied partner's
  hand capsules are at your controllers. GPU contact reads the capsules at its dispatch after skinning, also after Embody).
- Yield (Overlap, replaces the 0.7.8 push springs): per segment the deepest penetration of any hand source into the ANIMATION-pose skin
  shape (`Seg.YieldPen/Pt/N/By`) → displacement `n * Dmax * tanh(G * pen / Dmax)` per region → rotation-vector targets by displacement
  shares along the chain (elbow/knee flexion-only hinges, the rest to shoulder/hip) → exact critically damped filter per bone (attack per
  bone, release 0.7x), clamped. Sources: hand models, partner capsules (PushByPartner), `PhysicsHands.FillGhost` (the hands at the
  CONTROLLER pose), `Embody.FillGhost` (his hands at the IK target; his animated capsules are excluded). PhysF/GeoF/Hit* stay for Reactions
  only; `Overlap.Kick` is a velocity impulse with realistic inertias.
- Physics hands: drawn offset gated (smoothstep 5-15 mm / 4-10 deg, 1 while touching, attack 40 ms, release 250 ms; lost target > 5-10 cm
  fades out); finger commands `lerp(rest curl, 1, grab)` for all five fingers (the grab button closes the whole hand: trigger by default, VR → Grab with); conform per rendered frame on the
  rigidbody pose, followers 0.5/0.75, give `HerBody.ClaspGive x (0.5 + Softness)`; finger material friction 0.15 Minimum.
- Embody: neutral finger base from the bindposes (world rotations written top-down), clasp by `FingerFan.ConformAnalytic`, hand stop
  (3 iterations vs `HerBody.CapsuleDepth` with skin allowance, 12 Hz smoothing) when physics hands are off, elbow swivel (candidate pole
  angles vs her skin, hysteresis).

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
- Dirty talk (IL, 0.8.6): `LadyVoice_Function.DirtyTalkTypeSelect(type)` sets `dirtyTalkType`, `dirtyTalkActive = true` and, by the type and
  `FacialFunction.dirtyTalk_reika_level` (reikaNipple/reikaPussy/reikaAnal and some sex types) or `dirtyTalk_player_level` (handJob, tittyFuck,
  ...), 1..3, builds `dirtyTalkVoiceList` = 4 concatenated arrays (`RandomDirtyTalk`: `dtClipLength_1..3`) and `dirtyTalkTypeList_string` /
  `dirtyTalkTypeList` / `dirtyTalkLevelList` (`DirtyTalkTypeAndLevel`); it writes nothing else. reika* Lv1 = the type's Lv1 + empty x3, Lv2 =
  Lv2 + inviteSex, Lv3 = Lv3 + inviteSex + requestSex; handJob = public_DT_LvN + handJob_DT_LvN (+ invites). `PlayDirtyTalk(true)` plays
  `list[dirtyTalkVoice[dirtyTalkPlayNum]]` (counter then decremented), `DirtyTalkNumber` maps the index to type/level/num by the segment
  lengths, and the face (`DirtyTalkFacialAnimation`, `DirtyTalkEyeFacialType`) and subtitle get those as arguments; it also stops the
  voice loop and the sex facial. Feeling one-shots: `SexVoice_Source_Library.Feeling_enter_0..3` (single clips).

## Open items

- 0.8.4 to check in the headset (TESTING.md 0.8.4): creases visible on her face, no seam lines, the metres-per-UV value in the albedo-copy
  log line plausible (it sets the crease scale), the Characters tab face/body skin toggle with Wrinkles on, marks through Wrinkles' copy.
- 0.8.3 to check in the headset (TESTING.md 0.8.3): hinge sign (a push from the front must not bend the elbow backwards: else flip `vh` in
  Overlap.AddYield), contact starts < 50 per 10 s while holding a limb (else lower Elbow/Knee attack), his bind pose is an open hand (log
  line root match / axis change), the swivel direction, Reactions KickNs vs the new inertias (owned by the reactions work).
- Follow-ups: Overlap's planted lock still tests the embodied partner's capsules at the animation pose (it runs before Embody; her hand
  planted on his arm follows his animated arm); his
  forearm/upper arm can still pass through her when the controller is behind her (swivel only); per-segment collider inset from Tissue.cs
  (contact-visuals) instead of the softness rule; hand-to-hand collisions (rejected for now).

- 0.8.1 to check in the headset (TESTING.md 0.8.1): no v1 fallback, dent smooth (no steps) and not deeper than the Tissue caps, no permanent
  self-contact dents standing at rest (thighs, arm on side), bend bulge sizes, marks albedo property found on "Shader Graphs/Skin" (log line per
  sheet), print/redness visible through Wrinkles, ripple size, GPU cost (first v2 frame line). Hands session may inset HerBody's hulls by Tissue.
- 0.7.8 to check in the headset: free-air "at the cap" near 0 % and low drawn-hand jitter (if not: Jointed fingers off to isolate the
  finger joints); hull frames line up with the markers in a flexed-leg pose (pelvis roll from Root_M); "cooked a reduced set" lines
  (16-gon fallback when PhysX's 255-polygon limit bites); stick-slip when stroking (Friction 0.5); partner hands left inside her by an
  animation now push her limbs by up to the knee angle (PushByTouch); the extrapolation lead (first-contact speeds plausible).
- GPU floor: 0.7.8 keeps it on body meshes only (C#). The shader-side normal guard (SoftDeform.compute Imprint: skip floor-only vertices
  when the capsule axis lies behind the skin) waits for the next bundle rebuild.
- 0.7.9 to check: the bridge line ("Reactions: FacialFunction ...") lists nothing important missing; slap tiers feel right at 50 %
  sensitivity (log speeds); flinch not a jerk (kick cap 0.08 x torque cap); blush range of _BlushPower (logged base) vs BlushGain;
  the voice guard stops the IndexOutOfRange spam. Deferred: per-zone redness marks, reaction_enter_Eye ids, breast DynamicBone
  force pulse, mood_num meaning (experimental steppers log the Animator's parameters). Design/research: design-0.7.9-reactions.md,
  research-vammoan.md, survey-game-reactions.md in the session notes.

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
