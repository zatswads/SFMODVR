# SF Body Morphs — handoff

State at **0.11.27** (branch `main`). 0.11.27 (tools/wrinkles/author.py face: face_creases re-sculpted (Crease roll_toward / roll / breaks / mid, negative depth = a raised roll; Bunch masses), build_sum (per texel the strongest region, sums all of that region's primitives; logs each region's cut at its edge), 14 face regions incl. nose_L / _R, mouthcorner_L / _R, chin; previews rasterise the face mesh with a depth test (Surf.raster depth); WrinkleTest groups follow; body layout unchanged; built, checked offline only). 0.11.26 (controller haptics, Haptics.cs, off by default; game facts: OpenXR 1.8.2, the game requests the Touch Pro profile but VirtualDesktopXR 1.1.0 doesn't support XR_FB_touch_controller_pro, so over VD only the main motor; built, not run). 0.11.25 (penetration sound, Penetration.cs). 0.11.24 (Detail Op.Blend 'upperBuild' drives shoulderWidth / upperarms + armLength / forearms + forearmLength / neck + neckLength / ribcageWidth (Body.Effective: sizes x DriveAt^b, moves + DriveAt x b, clamped to the slider range); muscles re-authored: fuller deltoids / traps, traps' neck part, neck_L / neck_R (area back, MusclePose HeadFlex / HeadSide / HeadTurn of Head_M vs Chest_M), BakeMuscles no longer skips ClsNeck; the muscle layout is full (32 = Wrinkles.MaxRegions = MuscleFlex.Max); BendPose slots 9 / 10 waist roll from the trunk side bend (Root_M / Spine1_M / Neck_M vs her left in Root_M's frame), ContactBake weights roll + crease on the flank 9-28 cm above Root_M, SoftDeform BendAmt reads _BendAmt2[j - 8]; WrinkleTest (Debug tab) overrides the region activations per expression group; author.py --preview face_renders; built, checked offline only). 0.11.23 (ReactionsGame.Rub stroke mode: a rub clip under 1.5 s plays as overlapping shuffled one-shots on the rub source (_rubStrokes / _rubNext); user's Sounds rub / grab / pat replaced by VaM scene-package skin clips, survey + audition reels in that session's scratchpad foley2 / foley3; built, not run). 0.11.22 (Reactions.GrabButtonSound: Grip.Grab* edge while that hand touches a seg (TouchTL / TouchTR within 0.3 s) -> FoleyShot Grab; BuildRub 150-850 Hz, air 1/16 below 4 kHz; built, not run). 0.11.21 (headset fixes, built, not run in the game: Quality default Fidelity + continuous soft-point weights (4th-nearest subtracted); finger joints without projection, connectedMassScale <= 3:1; SkinMarks.shader pass 4 Blur (coverage-weighted Gaussian on the marks RT, [Physics] MarksSmooth mm), compute _PrintAmt / _DentFade from MarksPrint / MarksPrintTime; muscles: obliques their own flex group + [Muscles] Obliques (relief via MActSent, bulge via mm; MusclePose.SideBend lifts it on the bent side), FlexMm = 10 tanh(mm/10), WrinkleRaster muscle slope soft-limited at 0.9, MusclePose.Pregnancy (her pregnant* renderers) fades abs / obliques; author.py: relief fades 4-35 mm from the genital / nipple overlay meshes, adonis groove 1.9 mm, shade 0.45). 0.11.20 (ReactionsGame.SceneFill: while Camera_system.playableDirector plays a *_Climax / *_Creampie / *CondomAnim / *cleanAnim / *_Finish_ enter Timeline (its one voice clip goes to Sound_Function.voice_audioSource, silenced by the mute; no AudioSource.clip, so StandIn never fired), pack request / climax / invite / afterglow clips in turn on the moan source; [Voice] PackInScenes; built, not run). 0.11.19 (belly_eval clamps the cross-section exponent to >= 2 (below that the centreline is a sharp ridge); back re-authored along the fibres, erectors broad and fading, obliques 6.8 mm; built, checked offline only). 0.11.18 (author.py: adonis belt groove 2.4 mm + oblique pad 5.2 mm over it, lower rectus wide; back: rhomboids / lower traps in the traps region, teres minor, scapula creases, erector edge groove, deeper spine; traps tension from the arms pulled back; built, checked offline only). 0.11.17 (author.py abs: per-side intersection heights / curves / block sizes, wandering linea alba, domed blocks; Subdiv.Apply builds twice when MeanEdge > 10 mm (her lower body); built, checked offline only). 0.11.16 (author.py Belly primitive: spindle along a (s, phi, half-width) centreline, plateau (1 - x^flat)^2 across, ends eased, soft maximum UNION_P 3 of positive bellies, numeric gradient in the limb s / arc plane; frame-only segment `back` (axis through the trunk) for the back muscles; muscles reach across limb boundaries via segs; 30 muscles (+ pecs_L/R area arms, traps area back), tensions in MusclePose, MuscleFlex parts chest / scapula; clay previews muscle_body_geo*.png; built, checked offline only). 0.11.15 (Gloss: Morph.ApplyGloss on every material of hers with the Skin shader props; shader GBuffer = smoothness `_SmoothnessMap.r*_SmoothnessPower + _AddMap.r*_AddPower`, roughness 1-that; Gloss<=1 scales both, >1 white _AddMap + _AddPower lift to GlossTop 0.85 mean from MeanR blit readback; built, not run in the game). 0.11.14 (face-only characters: MorphSet.FaceOnly from character.txt 'faceonly' holds Body at 0 and the body skin off in Morph.Update, Panel greys Body / Body skin / Full character and a pick starts as Face only; sfpipe --face-only, set by run.ps1 for adapter sources (source.txt) unless -FullBody, bake skips body + foot; built, not run in the game). 0.11.13 (sculpted muscle geometry, built, checked offline only: author.py writes muscle_body_height (signed height, 16 bits R / G, +-HEIGHT_MAX 16 mm, layouts line has <height max>); MuscleMapFor decodes it and takes one peak per area (a per-muscle peak made the weight jump between muscles); BakeMuscles: belly = height / area peak (-0.8..1.25, one light smoothing pass) instead of the geodesic pillow (kept for an old bundle); area mm sliders 0-20. Offline check script: sculptcheck.py in that session's scratchpad). 0.11.12 (fixes from the 0.11.11 run, built, not run in the game: reika_foot joins GPU contact as a body skin mesh (GpuContact.SkinLimb, rank 1, muscles baked) and BakeMuscles tries every welded copy's UV / accepts dilated texels / fills lone holes; Grad takes the bent normals from a smoothed copy of the final field (_FieldS / _GradSmooth, Smooth passes into C / D after Compose, [Physics] ContactNormalSmooth); SoftSim strokes = a partnerBody capsule closing on her soft-point centroid then backing off (_cPeak / _cAway), SoftImpact.Thrust sized by [Physics] RippleThrustAmt; KeepMood = the clip's mean pose, never Jaw_M; [Voice] LipTiming replaces LipLead, ReactionsGame.OutputLatency (DSP buffers); mute on 5 sources incl. sexFacial_audioSource and dialogue ([Voice] MuteGameDialogue), muted game clips are not busy, StandIn plays a pack clip per game voice onset; Debug tab = Panel.Debug.cs). 0.11.11 (ReactionsFace.Seed rewritten: procedural mouth poses in the mouth frame (mm on lips / corners / cheeks, only the jaw turns), LipSync.RetireOldMap sets pre-0.11.11 maps aside, mouthClose clamped to jawOpen in LipFrame; built grab sound = slow squeeze; ReactionsFace.KeepMood: while the voice holds the mouth, the mood clip's mouth / cheek pose is kept per frame and LipStep blends it under the visemes by [Voice] LipMood; voice packs Pixie / Pixie Dominant / Southern Domme / Scottish Peach installed with curves, Maive removed; built, not run). 0.11.10 (Flex test: MuscleFlex.TestArea overrides the activations in GpuContact.FillMuscles, one area at full for 2 s each, logged by LogTest; built, not run). 0.11.9 (Ticklish = smile clip + Pain eyes / brows overlay + grief-brow offsets, ReactionsFace.AgonyStep; built, not run in the game). 0.11.8 (tensed muscles as geometry, built, not run in the game: ContactBake.BakeMuscles reads muscle_body_region (GPU blit readback, the textures are not CPU-readable) per body-sheet vertex -> (id, share) x2 + a belly weight (geodesic distance to the muscle edge, smoothstep over 2.5-4 cm, x (1 - 0.7 groove)); MuscleFlex (Muscles.cs) carries Wrinkles' faded activations to GpuContact (_MusAmp / _MusAct float4[8], marker kernel MuscleFlex), Depth meets the bulged surface and Compose subtracts the bulge under the dent; soft depth x (1 - Firmness x act) in Depth / Compose, HerBody.SkinAllowance and SoftSim zone limits; flex-only frames = Compose + Grad + Apply; MusclePose anatomy pass (hams, lats, adductors, supine leg lift); ribcage relief only). 0.11.7 (fixes from the 0.11.4 headset run, built, not run in the game: fixed-size resizable
panel with vertical tabs and a Skin tab; face / neck dents (space from a readable sibling), face / neck meshes shape her head / neck
colliders; dent smoothing in metres (SkinEdge); skin-sound volume + tests; Embody neutral hand from the straightened animation pose,
thumb opposition axis + limits, controller models hidden, off at start, view position sliders; the 0.9.9 lip sync merged (it was only
on a side branch) with auto-seeded poses and a probe; tools/a2f merged; thrust ripples; mute the game's voice with a pack.
Open from that run: "eye tracking no longer works" (unclear which; nothing in the log), muscles as geometry and per-region settings,
natural (not line-like) face wrinkles: both wait for the user's go-ahead). Decision 2026-10-16: DAZ characters go in as slider-fit presets (`run.ps1 -Daz` defaults to -Sliders); the full conversion is parked for them (`-Convert`), see docs/SOURCES.md. 0.11.6 (fit: head blend ends under her chin + rigid head anchors in the warp, mouth-corner landmarks, breasts out of the shoulder no-data spheres, --breast-prealign experimental; open: breast skin folds on a much flatter VaM breast, e.g. RKS Krista 9; comparison renders by %TEMP%\sfm_chk\cmp_render.py). 0.11.5 (first Genesis 9 character, RKS Krista 9, through -Daz: DAZ material list for textures, G9 eye / mouth meshes merged, Lips / Ears from geometry; open: G9 jaw / chin pulled in + face / neck seam step, chest folds; test output in %TEMP%\sfm_chk\krista). 0.11.4 (`run.ps1 -Daz <preset.duf>`: DAZ Studio exports the character via tools/sources/daz_export.dsa, then -Source; base-figure check first; launch tested, export waits for Genesis 9 Starter Essentials on the PC). 0.11.3 (ReactionsFace: Surprise built from lid / brow / jaw bones along her bind-pose up in the head frame, only Animator-written bones; a panel pick still wins) built, not yet run in the game. 0.11.2 (source adapter `tools/sources/humanoid.py` + `run.ps1 -Source`: any rigged humanoid -> an SfExport folder; groups `Role@material`; verified by a G2 FBX round trip, the G8/G9 / VRChat tables not yet run on a real file; see docs/SOURCES.md). 0.11.1 (pipeline by role, not by her default mesh names: `tools/pipeline/roles.py`;
Export writes every variant under her with an `active` column; sfpipe fits the worn variant of each role and lets the others
follow; bake targets / face-only per face material; `docs/SOURCES.md` = the design for non-VaM sources, not built). 0.11.0 (character import materials, built, pipeline run locally on Yin, not yet seen in
the game): the baked normal maps carried body shape (detail-only bake now), the albedo was ~2x too bright (gain to her body),
her export held our clones (Export now writes the game's meshes / her textures, `Morph.GameMesh`); `tools/pipeline/matcheck.py`
material check after every bake and `run.ps1 -Check / -Rebake`; slider fit `run.ps1 -Sliders` (`sliderfit.py`, a preset of her
Body sliders, no mesh swap). Yin re-baked on the user's PC (old textures in `morphs\Yin\_check\old_textures`); preset
`Lexi (fit)` written. Triage notes: import-materials-triage.md in the session notes. 0.10.0 (muscle definition: `tools/muscles/author.py` authors a relief per muscle on the body atlas
(bellies + grooves placed in each limb's cylinder coordinates from her skeleton, 27 muscles in areas core / back / legs / arms;
`muscle_body_detail` BC7 slope / groove depth / coverage + `muscle_body_region` RGBA32 two ids + share, `muscle_layouts.txt`);
WrinkleRaster pass 0 adds the muscle slope to the creases' before the whiteout, pass 2 the groove shade and the muscle debug views;
`Muscles.cs` = MuscleSettings, MuscleLayer, MusclePose (joint angles vs bind + gravity loads -> tension per muscle); Wrinkles keeps
the body sheet while [Muscles] On even with [Wrinkles] On false (no strain dispatch then), act = Fitness + (1 - Fitness) x
clamp01(Gain x tension) with attack / release, re-blend at 1/32 steps, <= 10 Hz; bundle rebuilt) built, not yet run in the game.
0.9.9 (`Perf` timing line every 10 s in log + panel: per-stage avg/max ms (Perf.cs, Stopwatch around each stage of Body.ApplyAll, PhysicsHands.FixedStep, GpuContact.Run, Wrinkles' hook, SkinMarks' raster, panel), PlayerLoop phase markers (physx = FixedUpdate.PhysicsFixedUpdate, wait = gap between frames), fps, fixed steps/frame, GPU ms from XRDisplaySubsystem.TryGetAppGPUTimeLastFrame by reflection else FrameTimingManager; small allocation / reflection fixes; skin-mark Redness/Ao settings restored (lost in a comment since 0.9.4)) built, not yet run in the game. 0.9.8 (wrinkles: neck lines moved to the body atlas (they had landed on her chin), face / neck regions measured from an idle baseline (slow minimum, 20 s rise), nasolabial shorter; bundle rebuilt) built, not yet run in the game. 0.9.7 (GPU contact: skin meshes stitched at their seams (BuildMaps: SkinRank upper 0 / other body 1 /
neck 2 / face 3, a later skin mesh maps to an earlier one, `_MapW` = 1 within 1.5 mm fading to 0 by 1 cm, ApplyMap blends own and parent
Grad; dispatch passes by parent Level); her neck and face skin added as `Extra` v2 renderers (zero mask, no v1 imprint) so marks reach
them, SkinMarks allows the face class; [Physics] RippleImpact = the wave share at the impact point (`_WaveImpact`); bundle marker kernel
SeamBlend) built, not yet run in the game. 0.9.6 0.9.6 (VoiceShift fixed: correlation-aligned marks, asymmetric windows to the neighbour marks = exact reconstruction at 1/1, synthesis anchored per voiced stretch, voicing hysteresis, stretched + normalised grains for pitch < 1; offline harness in the session scratchpad vstest/vst2) built, not yet run in the game. 0.9.5 (wrinkles tuned from the 0.9.1 headset log: face ranges 0.025-0.035 and lines x1.3, belly = one soft roll following the belly curve + a faint fold, crease shade scaled by depth; bundle rebuilt) built, not yet run in the game. 0.9.4 (ripple slower / longer / bigger with Physics-tab sliders; marks only from foreign capsules: Depth writes
`_Foreign` (weighted press, foreign depth) from capsule mark weights in `Capsule.c.y` (0 = her own limbs, foreign 0.4-1.5 by speed vs Root_M),
Compose reddens past a 0.25 knee; BendPose slot 8 now an outward belly roll; softer built rub + [Reactions] RubSound; bundle rebuilt with a
ForeignMarks marker kernel) built, not yet run in the game. 0.9.3 (VoiceShift rewritten as streaming TD-PSOLA: YIN pitch on a 4x decimated copy, pulse-snapped
analysis marks, one-pulse grains windowed in the input and resampled by the formant; checked offline on real lines, not in the game).
0.9.2 (voice-pack lines + state lines guard / afterglow / ouch / request / invite, audio-driven jaw from
the face survey's widest jaw opening, lazy pack loading; vamx-lines-to-game-banks.md in the session notes) built, not yet run in the game.
0.9.1 (skin wrinkles redesigned: authored crease maps per UV layout from `tools/wrinkles/author.py`,
faded in per region by the directional compression across the region's lines, measured on the GPU; procedural sine folds removed;
bundle rebuilt with the maps; [Wrinkles] SpacingFace/SpacingBody removed) built, not yet run in the game. 0.9.0 (skin foley from the user's WAV folders with built-in pat / grab / rub fallbacks, voice packs,
voice pitch / formant filter, climax face hook fix, face pools / voice-onset faces; research notes research-vsr-face.md and
survey-voice-packs.md in the session notes) built, not yet run in the game. 0.8.9 (fixes from the first headset run of 0.8.1-0.8.8: dent floor = raw contact depth in Compose
(bundle rebuilt; Depth now writes FieldD, wide ends in A, Compose writes B), ripple amplitude x3, [Physics] MarksBoost, [Hands] YieldLock,
collider inset / Embody hand stop = 0.6 x tissue depth (HerBody.InsetShare), face mood picks skip body-motion clips; triage in the session
notes triage-0.8.8-log.md) built, not yet run in the game. 0.8.8 ([Reactions] LinesGamePath switch for the lines' game path, first-use index
log; IL check: no deferred reader of dirtyTalkVoice / dirtyTalkPlayNum) built, not yet run in the game. 0.8.7 (face clips: a survey of her face Animator's clips by SampleAnimation with an exact
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
| `Perf.cs` | 0.9.9 timing: `Perf.Begin/End(stage)` (Stopwatch ticks summed per frame), PlayerLoop markers at the start/end of each top-level phase and around PhysicsFixedUpdate, the 10 s `Perf last 10 s` line (`Perf.Summary` is shown in the panel); `[General] PerfLog`. |
| `Plugin.cs` | Entry point, `ConfigEntry`s, hidden `Runner` (Update = key + scan, OnGUI = panel), `ApplyStep` = the apply step inserted in Unity's PlayerLoop before `PreLateUpdate.ScriptRunBehaviourLateUpdate` (re-checked every 2 s; LateUpdate fallback), unfocused hotkey via `GetAsyncKeyState`. |
| `GameRig.cs` | Reflection into Assembly-CSharp: the woman (`MovePlayerAndGirl.reika`) and her body-part bones from the game's own references. |
| `Body.cs` | `Detail` table (slider: group, Op Size/Stretch/Move/Length, Dir, bones by game part → exact rig names → keywords, range, Keep), `Character` (renderers, skin bones, slider bones, bind-pose axes/move directions, look), scan every 2 s (cached; rebuilt only when the renderer set changes), apply/restore trackers for scale and position, one `Character '...'` log block per session. |
| `Look.cs` | A look (bone factors, slider values keyed by `Detail.Id` in `Regions`, file token `region`), text format, `Store` (autosave/presets in `BepInEx\config\SfMorphs`). |
| `MeshData.cs` | A mesh's data in game vertex order: CPU if `isReadable`, else GPU readback (`Mesh.GetVertexBuffer/GetIndexBuffer` + `GetData`, decoded by `GetVertexAttributeFormat/Offset/Stream/Dimension`, submeshes from `GetSubMesh`). All her meshes are GPU-only. Basis for the export and the future morph loader (readable clones). Bind poses are readable (0.5.0 log: slider directions worked). |
| `Export.cs` | Presets tab export: per mesh `.obj` (bind space, game vertex order, `g` per submesh) + `.skin.txt` (bones, bind poses, 4 weights/vertex), `skeleton.txt`, `meshes.txt`. Skips meshes that aren't `isReadable`. 0.11.0: the game's mesh behind any clone (`MorphState.GameMesh`: soft zones / Fidelity / morph), her texture behind our render textures (`Wrinkles.BaseOf`), every texture of her skin materials and `materials.txt` (every shader property + keywords). |
| `Morph.cs` | Characters (0.6.0): `MorphSet` (a converted character folder), `MorphState` per character (readable clones from MeshData + `AddBlendShapeFrame`, follow blendshapes for clothes via `SkinRef` nearest skin vertex + push-out, joint offsets as local moves for Body's trackers with matching bind poses, DynamicBone-simulated bones excluded, baked textures with restore). |
| `SoftSim.cs` | Soft zones' per-frame spring sim (0.7.0): helpers' rest place from their bone, damped spring at 1/120 s substeps, contacts against `SoftCapsule`s (push out, velocity transfer), clamp (in ≤ limit, out ≤ 0.3, sideways ≤ 0.5), bulge to neighbours, writes S; debug markers. Runs at the end of `Body.ApplyAll` (`SoftStep`; 0.8.5: after Embody, so his hand capsules are at your controllers). 0.8.1: per-zone stiffness/depth/damping from `Tissue.Zone`, resistance `Tissue.Resist`, fresh fast contacts (approach >= 0.7 m/s) into `Impacts` for Ripple, "partnerBody" gated by `SoftZones.PartnerBody`. |
| `VrHands.cs` | `SoftCapsule` + who can touch her (0.7.0): the user's hands via `VR_HandController.controller_left/right`, partner rigs (humanoid or AdvancedSkeleton wrists/fingers, nested rigs counted once at wrist level), her thighs/forearms/hands; palms = three 1.5 cm capsules; one-time `VR rig survey`. Refreshed every 2 s. 0.7.7: every capsule has a `CapSource` (your hand, physics hand, partner rig, her); `UpdateLive` (once per frame) marks gated sources dead while none of their renderers is drawn by the XR camera (hidden partner, controller models replaced by physics hands); the soft points, GPU imprint and breast mirror skip dead sources. Mirrors `Body.Contacts()` into the game's breast DynamicBones (`BreastColliders`: off / main `upperbreast` chains / all), detaching hidden sources (`MirrorLive`). `ContactsFeed` = what each source feeds. 0.8.1: her upper arms ("upperarm") and calves ("calf") too, and the partner's pelvis + thighs ("partnerBody", same gated source as his hands; Overlap, HerBody and the breast mirror ignore these kinds); `BreastDbs` for Ripple. |
| `PhysicsHands.cs` | Physics hands (0.7.0): hand mesh cut from the player's body (or primitives), palm rigidbody following the controller in `Runner.FixedUpdate`, grip curl, two private layers. 0.7.8: the controller pose is sampled once per frame with its linear/angular velocity (`Track`, in HerBody.cs) and extrapolated to `Time.fixedTime` of each step (feed-forward + gap gain 25/s, force cap); a rig jump (the game moving the VR rig) shifts the hand with its offset; the drawn hand = controller ∘ the offset of the last two steps interpolated by (time - fixedTime)/fdt, refreshed in onBeforeRender and again after Embody's view fix (`PreRenderRefresh`). Her colliders: one kinematic body per `HerBody.Seg` (`SegBody`), a convex hull cooked from `Seg.HullPts` (<= 2 per frame, inset toward the axis, 16-gon reduced fallback) or the capsule/sphere; friction materials (`Friction`). |
| `HerBody.cs` | Her body segments (0.7.8), shared by the physics hands and the pushes: segment defs, fit from her body meshes (bind pose + character morph deltas), hull points (12x16 grid in the segment frame: origin at joint A, Y along A->B, roll from `RollRef` = A or the owner bone for sibling joints such as the pelvis), `SegPose`, contact impulses from the physics hands (`PhysF`, 50 ms filter) and the geometric push from other hands (`GeoPush`: hand model/partner capsules vs her skin capsules, 300 N/m after 1.5 cm of skin give, measured after Overlap so the push fades as the limb yields), `PushOn` merge. |
| `FingerFan.cs` | 0.8.0: a finger's curl sweep fixed to the palm (tip + last-capsule probes at 8 curl fractions, forward kinematics of the rest pose, built once): `ConformPhysics` (CheckSphere on her layer, enter/stay hysteresis) for the physics hands, `ConformAnalytic` (HerBody.SkinDepth + bisection) for Embody. Open-loop: depends only on the palm pose. |
| `PhysicsHands.Rig.cs` | Hands v2 (0.7.2): jointed finger rigidbodies (proximal + fused middle/distal, ConfigurableJoint slerp drives with capped force = compliance), curl targets, tip-cast settle, kinematic fallback; palm force-capped follow (`Fcap = lerp(60,12,resist)`) + PD torque. |
| `Haptics.cs` | 0.11.26 controller haptics: HerBody.Feed -> `Contact(hand, part, pen, pt, n, seg)` for YOUR hands' capsules (part by bone name / Embody finger index: grip, trigger = index, thumb; `WorldCap.Part`), Penetration's openings (own stroke detector), Grip's grab edge -> per actuator a continuous level + pulses; path probed every 2-5 s and logged: OpenXR Input System actions on `<QuestProTouchController>{hand}/haptic|hapticTrigger|hapticThumb` when the runtime enabled XR_FB_touch_controller_pro (Meta runtime), else `<XRController>{hand}/haptic` (VDXR: no Touch Pro profile, one motor), else XR InputDevice channel 0 ([Haptics] Path 1); <= 30 sends/s per channel as 0.1 s impulses, explicit stop. Debug tab tests. |
| `Grip.cs` | Controller grip 0..1 per hand (VR_HandController.selectValue_*, Animator param, XR), once per frame; used by physics hands and Embody. |
| `Subdiv.cs` | Fidelity: one level PN-triangle subdivision in place (same Mesh object, so MorphState's swap check holds), seams welded, weights/blendshapes interpolated, exact Undo. |
| `Overlap.cs` / `Ik.cs` | Secondary motion v2 (0.7.7): second-order springs on 16 of her bones' local rotations (parents first), per-part bounce (zeta, frequency), ripple kick from the parent's world angular acceleration, planted hands/feet (animated end effector still, or still relative to a nearby partner/her capsule) held by an analytic two-bone IK (`Ik.TwoBone`, Embody's solve) while the chain lags, push springs (0.7.8: arms, legs, spine, neck/head via segment -> bone shares; per-region inertia/clamp/torque cap; tanh knee to 0.8 x clamp) from `HerBody.PushOn` (works at Overlap 0), faded on contact, reset on cuts. |
| `GpuContact.cs` | GPU contact (0.7.3): loads `sfmorph_shaders` (tools/shaders, built with Unity 2022.3.9f1 by build.ps1), sets `vertexBufferTarget |= Raw` on her soft renderers, dispatches `SoftDeform.compute` Imprint (exact ray-capsule exit along the inward normal, <= limit x mask) on the CURRENT skinned buffer once per frame after skinning (PlayerLoop step after UpdateAllSkinnedMeshes, SRP beginContextRendering fallback); skinning space calibrated by AsyncGPUReadback vs CPU skinning; never writes the previous buffer (motion vectors stay right). 0.8.1 v2 chain per renderer (`ContactBake`): Depth (exact capsule depth vs the tissue cap, Press knee, self-capsule skip masks in `Capsule.c.x`) -> Dilate x N (skirt) -> Smooth x2 (narrow) -> x3 (wide) -> [MarkDisc] -> Compose (narrow - rim bulge - bend - waves; marks) -> Grad (per canonical, clean positions) -> Apply (positions + bent normals + tangents, reads only its canonical's Grad). Capsules culled per renderer (bounds + 15 cm); idle 0 dispatches; any v2 error -> v1 for all; old bundle (no v2 kernels) -> v1 + a warning. `AfterImprint` event for SkinMarks. Her own limbs / his body alone in reach: Depth runs as a probe counting pressed vertices (`_Hits`, AsyncGPUReadback), the full chain only while > 0 (30 frames hold). Overlays (non-body v2 meshes: areola, tattoo, vulva) map each vertex to the body canonical within 5 mm at Sync (`ContactBake.WorldP`, CPU skin) and finish with ApplyMap (the body's Grad of this frame), so they move with the skin under them; bodies dispatch first. |
| `ContactBake.cs` | 0.8.1: `ContactSettings` (the [Physics] contact values, filled by Body each frame) and the per-renderer CPU bake for GPU contact v2: weld (WrinkleBake.WeldKey, canonical = first copy), CSR adjacency over canonical ids, bone class per vertex (design table 1.3), tissue (hard, soft) from `Tissue`, bend slots/weights, UV + triangles + metres per UV for SkinMarks. Never throws (a failed bake keeps v1 Imprint for that renderer). |
| `Tissue.cs` | 0.8.1: the anatomy table shared by the GPU dent (per-class hard/soft depth and floor, shin/biceps floors, `Press` knee curve: exact to 0.6 cap then a smooth saturation), SoftSim (per-zone stiffness, depth, damping) and the hands' resistance (`Resist` = (dent/cap)^2). Approximate on purpose: look and feel, not measurement. |
| `BendPose.cs` | 0.8.1: elbow/knee/hip angles vs the bind pose -> 9 bulge amplitudes (biceps, thigh back, thigh top, calf, belly fold) for Compose. |
| `Ripple.cs` | 0.8.1: impacts (SoftSim's fresh fast contacts via `SoftZones.Impacts`, Reactions' slaps) -> <= 8 bone-attached damped rings uploaded to Compose; breast impacts pulse the game's breast DynamicBones' `m_Force` for 0.12 s (tracker, restored only if still ours). |
| `SkinMarks.cs` | 0.8.1: per skin material a marks RT rasterised in UV space from GpuContact's per-vertex `_Marks` (SkinMarks.shader), composed into an albedo copy (redness, pressed darkening, pale print) and a normal copy, or through Wrinkles' blend pass when Wrinkles owns the normal slot (`Wrinkles.Touch`, `ExternalBase`); own `_FlipY` self-test; idle = slots restored and RTs freed after max(10 s, 4.6 x MarkFade). 0.8.4: while `Wrinkles.OwnsAlbedoSlot` (its albedo copy of the material), no albedo copy here: WrinkleRaster pass 4 composes the marks (`AlbedoParams`, same maths as SkinMarks.shader pass 1); one albedo RT per material. |
| `Wrinkles.cs` / `WrinkleBake.cs` | Skin wrinkles (0.9.0; procedural folds 0.7.5-0.8.9 replaced). Authored layouts in the bundle (`wrinkle_layouts.txt` + per layout `wrinkle_<name>_detail` (BC7: slope xy, crease depth, region fade) and `wrinkle_<name>_region` (RGBA32, readable: region id+1 / 0 none / 255 off-island, weight, crease line direction in UV as a doubled angle)), made by `tools/wrinkles/author.py` from her export (creases = 3D polylines placed from her face/limb bones and ray cast onto her bind-pose skin; breast exclusion spheres; `--preview` draws them on her colour maps). Sheet face -> layout "face", any other skin sheet -> "body". CPU bake per renderer (`WrinkleBake`): coverage check (>= 90 % of its triangles on the layout's islands, else no wrinkles for it), the triangles under a region (region map at the UV centroid, weight = mean of centroid and corners), per region triangle DmInv of the neutral (bind + blendshapes + bone scale), the compression direction across the lines in (t, b), weight x area in fixed point. `Wrinkles.compute`: RegionClear + RegionStrain (directional stretch sqrt(d^T C d) -> compression, InterlockedAdd per region) per sheet each frame (every 4th when idle), readback of the 32 sums; CPU: comp = num/den, act = smoothstep((comp - Threshold) / region range) with attack/release, gated by the region's class (face/neck/body toggles); a 1/64 change -> blend. `WrinkleRaster.shader`: pass 0 blend (base normal + detail slope x act[region] x fade x Strength, whiteout, SkinMarks dent), pass 1 _FlipY self-test, pass 2 albedo copy (marks x crease shade = Cavity x depth x act x fade, debug tint: activation / regions). Normal RT = base size raised to the detail size (face 1024 -> 2048), body cap 2048. Peak-compression log per sheet every 10 s. `ShaderBundle.cs`/`RawTarget.cs` are shared ref counts. Albedo copy per sheet while [Wrinkles] Cavity > 0 or Debug on (size = base capped by Resolution, 4096 max); Morph's skin state (`_texOn/_texKey/_set` by reflection, fallback `NormalFor`) changing with our copy in the slot = hand the base back for Morph's 2 s pass; while a character skin is on, a foreign texture in the slot is taken over only after 2.4 s; a bundle without RegionStrain / 3 passes / layouts is refused. |
| `Muscles.cs` | 0.10.0 muscle definition, a layer of the wrinkle sheets: `MuscleLayer` (muscle_layouts.txt + maps), `MusclePose` (trunk flex / side / twist of Neck_M - Spine1_M in Root_M's frame, hip flex / abduction, knee, ankle plantar flexion vs the shin, shoulder elevation vs chest-down + swing, elbow, wrist; loads: chest facing down/up x hips bent (back / abs), hip-over-ankle drop (legs), chest down x hand below shoulder (arms)) -> tension per muscle by name; Wrinkles.UpdateMuscles fades it (Fitness, Gain, attack / release) and logs the peak tension + pose every 10 s. Maps from `tools/muscles/author.py` (imports the wrinkle author's helpers; `--preview` = layout + lit front / back / side renders). |
| `Embody.cs` | Embody (0.7.0): partner rig (Player/BadGuy), view snap/follow by moving the XR rig root (undone exactly), head hidden (scale 0.001, tracker), two-bone arm IK to the controllers with automatic hand frame, override detection in `Application.onBeforeRender`. |
| `SoftZones.cs` | Physics (0.6.3): per zone (glutes, thighs in/out, belly, breast front/side) a rest point under her skin, helper pairs (anchor = DynamicBone root with an excluded second child so it is never re-aimed, soft = simulated particle hanging along the skin) under each bone that weights the zone, weights split w → w(1-f) + w f, bind poses appended (follow `MorphState.BindVersion`); readable clones of the game's meshes when no character is on, MorphState's clones extended in place otherwise (`SoftZones.Original` keeps MorphState cloning the game's mesh); DynamicBoneCollider capsules on partner hands (humanoid Animator or bone names), her forearms/hands/thighs; never adds children to game DynamicBone chain bones with < 2 children. |
| `Reactions.cs` | Reactions (0.7.9): per HerBody segment a touch classifier (physics-hand contact ENTER speed per hand `HitVn`, hand-model/partner approach speed `GeoVn`, force, duration -> brush/caress/grope/soft/hard/too-hard slap, sensitivity scales the 1.2/2.3/4.5 m/s thresholds), 10 zones, excitement E (tiers) and pain P with decay, states Idle/Aroused/Startled/Guarded/Climax/Afterglow (auto or buttons), the director (voice chance + arbiter + tier gap, face, flinch via `Overlap.Kick`/`SoftZones.Kick`, slap sound, blush pump, gaze-away envelope, desire feed), test buttons, logs. |
| `ReactionsGame.cs` | The game bridge for reactions, all by reflection (audio types too: UnityEngine.AudioModule isn't referenced): FacialFunction (MoanFunction, Blink, anim, facialLayer, functionPause), LadyVoice_Function gates, Sound_Function sources, GameSetting/SexMode/SexMode_State_System (desire gauge), voice library banks (Moan by length, Exhale_f32/f64/f128), SFX clips, own 3D slap AudioSource on the game's SFX mixer group, blush (`_BlushPower`) and look-at weight trackers, and the dirty-talk guard (rebuilds dirtyTalkVoice/dirtyTalkPlayNum only when PlayRandomVoice would throw). 0.8.6 lines: `BuildLines` (DirtyTalk_Library[lang] reikaNipple/reikaPussy/reikaAnal/public_DT_Lv1..3 banks, Feeling_enter_0..3), `SpeakLine` (game path `LineViaGame`: snapshot of the dirty-talk fields, level on FacialFunction.dirtyTalk_reika_level / _player_level, DirtyTalkTypeSelect(type), one-entry shuffle at the clip's index, PlayDirtyTalk(true), restore; fallback `LineOwn` on dirtyTalk_audioSource), `Feeling` (moan source, "moan" trigger). The director's `LineBlock` / `TryLine` / `TryFeeling` / `PelvisCat` are in Reactions.cs. |
| `ReactionsFace.cs` | 0.8.7 face clips: survey of `FacialFunction.anim.runtimeAnimatorController.animationClips` (`SampleAnimation` on the Animator's object, 3 clips per frame; snapshot/restore of every transform's local pose + activeSelf, renderer enabled + property block; a sentinel pose before a sample tells which face bones a clip writes, position only if all 3 axes; score = mm + 0.2 deg per written bone vs the bind face in Head_M's frame), ranked log + `face-clips.txt`, moods (keywords, strength, `[Reactions] FaceMap`), 15 Hz tracks of the written face bones, `Step` (envelope, lerp/slerp from the Animator's pose; mouth group skipped while she speaks/moans, eyeballs and hair never), `Watch` (which clips the game's eye reaction plays). Body.ApplyAll calls `React.FaceStep` for her before `Morph.Update`/`Apply`. |
| `Foley.cs` | 0.9.0: `SkinFoley` (WAV reader: PCM 8/16/24/32 + float, extensible, downmix, silence trim; categories slap_soft/medium/hard, pat, grab, rub from `BepInEx\plugins\SfMorph\Sounds\<cat>`; built noise sounds for pat/grab/rub; shuffle bags) and `VoicePack` (`Sounds\voice\<pack>\<cat>`, 40 clips per category, nearest-category fallback). Clips by reflected `AudioClip.Create/SetData`. ReactionsGame: `FoleyShot` (slaps on the slap source, pat/grab on `SfReact_foley`), `Rub` (loop on `SfReact_rub`), `PackVoice`/`PackBreath` on the moan source (`_packClip`: no 1.8 s padding rule). |
| (0.9.1) | VoicePack lists files and reads clips on first use (LRU 64, `Busy1/2` = clips on the sources never evicted); `lines\<bank>_<lvl>` and state lines; ReactionsGame `PackLine`/`PackState`/`PlayPack`, `PackLevel` (GetOutputData RMS while a pack clip sounds); ReactionsFace `HasJaw`/`JawStep` (survey keeps the widest Jaw_M rotation vs bind, < 35 deg); Reactions `TryPackLine`, `TryStateLine`, jaw follower in `FaceStep`. Pack Anime built by the scratch script vamx_pack.py (0.11.11: Pixie, Pixie Dominant, Southern Domme, Scottish Peach by build_pack.py, see the session notes' voice-packs-0.11.11.md) (classifier by folder + filename). |
| `VoiceShift.cs` | 0.9.0: `OnAudioFilterRead` filter added to the GameObjects of her voice sources (moan, dirty talk, voice, dialogue; skipped if shared with the sfx source) by `ReactionsGame.VoiceFx`: 0.9.3 TD-PSOLA (`Psola`: YIN every 8 ms on a 4x decimated ring, analysis marks snapped to the strongest sample within T/6, grain = Hann over +-T of the input resampled by f, laid down every T/p; unvoiced 6 ms grains scaled by f; fixed latency 2 x (1.25 + 0.5) x Tmax(80 Hz)), mono in, each channel's energy share out, soft limit. Bypass at 1/1. The offline harness is in the session scratchpad (vstest). |
| `Panel.cs` | F8 window: character buttons, tabs Body (collapsible groups) / Bones / Presets / Characters / Physics (soft zones, motion, pushes) / VR (Embody, VR hands, contacts) / Reactions (`Panel.Reactions.cs`: toggles, states, voice guard) / Skin (wrinkles, muscles, marks) / Debug (`Panel.Debug.cs`, 0.11.12: timing, GPU check, markers, debug views, Flex test, every test button), filter + 80-row cap, greyed rows with reasons. |

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
  voice loop and the sex facial. Readers of `dirtyTalkVoice` / `dirtyTalkPlayNum`: only PlayRandomVoice, DirtyTalkNumber (one caller:
  PlayDirtyTalk, synchronous) and the uncalled `dirtyTalkVoiceListDebug`; the `DirtyTalkEyeFacialType` coroutine takes type/level/num as
  arguments and reads only `dirtyTalk_Facial_Library`; `RandomSequenceReset` is the only other writer of the counter. Feeling one-shots: `SexVoice_Source_Library.Feeling_enter_0..3` (single clips).

## Open items

- 0.11.0 to check in the headset (TESTING.md 0.11.0): Yin's re-baked skin (no dark / inside-out patches at nostrils, lips,
  ears, under the breasts; brightness close to hers), the face-only mode, the `Lexi (fit)` preset; a fresh export from the
  Presets tab with Physics on (the log line "Export: the game's own mesh written instead of our clone", vertex counts 19 469 /
  4 533 in meshes.txt, no RenderTexture in textures.txt), then `run.ps1 -All -Force` / `-Rebake` on the other characters.
  Unknown: what `_AttachMap_*` / `_AddMap` / `_NormalMap_2` do in Shader Graphs/Skin (makeup / areola / detail layers drawn over
  a character's albedo; the new export's materials.txt and maps show them). If a VaM skin still shows her lipstick or blush on
  top, an option to zero `_AttachColor_*` alpha / `_BlushPower` while a character skin is on is the next step.

- 0.9.1/0.9.5/0.9.8 to check in the headset (TESTING.md 0.9.8, 0.9.5, 0.9.1; 0.9.6 log: face/neck idle compression neck ~0.03, glabella 0.04-0.06 -> 0.9.8 idle baseline; 0.9.1 log: face peaks 0.03-0.05, belly 0.29, knee/elbow 0.2-0.25 = full, hipfold 0.05-0.08, armpit 0.010 = never active: check its placement / direction): the regions sit on the right skin (Debug = Regions), which regions actually activate
  with her expressions and bends (the 10 s peak-compression lines; the per-region ranges in `author.py` are first guesses: face 0.08-0.10,
  body 0.20-0.25 beyond Threshold), the elbow flexion side (both sides are regions; only the compressed one should show), line depth at
  Strength 1. The maps were authored on export Reika-20261016-023017 (face_a only); the 0.8.8 log also showed a face_c sheet: if its UVs
  differ it logs "not on the 'face' wrinkle layout" and needs its own job in author.py (export her with that face). CC4 route (4.2+, CC3+
  character, maps transferred to her UVs in Blender) kept as an optional replacement of the authored maps (same file names).
- Pose desync at the end of that run (her lying, the partner in cowgirl): nothing in the log; needs a repeat with Apply changes off.
- ~30 "her renderers changed" rebuilds per session (each re-bakes wrinkles, GPU contact, hulls; 6 frames with capsule fallbacks).
- Reactions: general-area lines resolved as handJob with the restored type anal (check DirtyTalkTypeSelect's type mapping). The climax
  hook and the second climax's missing feeling are fixed in 0.9.0.
- Sounds (0.9.0, user's PC only, personal use, never commit): Sounds\slap_* = BodyLanguage "Slaps" + the bqbq spank pack (Spank, spk_0/1),
  pat = BodyLanguage "ButtSlaps"; voice packs Rebecca / Amy from VAMMoan2 (VFedit). SallySoundFX has no audio (a visualizer). More voices
  (11 VAMMoan 2.1 voices, 20 vamX voices with spoken lines and pain clips) are extracted in the user's temp folder (survey-voice-packs.md).
  Not done: grab/rub recordings (built sounds now), the game's own sex moans with a pack, blink-off during eyes-closed face holds.

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

Avatar replacement (a third mode: the character's own mesh, rig and textures driven from her bones; design + the offline
check `tools/avatar/retarget_check.py`, the bundle builder `tools/avatar/build.ps1` (Unity project tools/avatar/SfAvatarBundle),
nothing in the plugin yet): `docs/SOURCES.md`, 'A third mode'.

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
