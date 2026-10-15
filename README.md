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
- **Characters**: a gallery of VaM characters converted for her (`tools/pipeline/run.ps1`, `docs/PIPELINE.md`). Picking
  one gives her that character's body and face shape as a real blendshape (0–100 % slider, the other sliders still
  work on top), moves her joints to match, makes her clothes follow, and puts the character's skin textures on her.
- **Character pipeline**: export her meshes for turning a VaM character into a morph for her (`docs/PIPELINE.md`).
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

0.8.0:
- Her arm went "crazy like a Newton's cradle" on the slightest push. The push was a force on springs that were far too light (an
  arm weighed like 1/6 of a real one, under-damped), and it was measured after the limb had already moved: the arm yielded, the
  push dropped, the arm sprang back into the hand, got pushed again, and the shoulder and elbow each swung on their own spring
  (the fulcrum look). Now her limbs YIELD instead: how deep a hand is in her limb (measured against the animation's pose, before
  anything moved it, and from where your controller is, not from the blocked physics hand) sets how far the limb gives; each joint
  eases there and back with critically damped motion, the forearm first and the upper arm a beat later (overlapping action), so it
  settles and never rings. Elbows and knees only bend the way they bend; a push that would bend them backwards swings the whole
  limb at the shoulder or hip. Slaps still kick the limb (with realistic arm/leg/head weights) and it settles.
- Planted hands and feet slid when her spine or arms trailed the animation, and her feet when a leg was pushed: the lock only ran for
  regions with secondary motion on and only for hands standing still in the world. Now every hand and foot our motion would move is
  held at the animation's place (relative to the world, her hips or the partner, whichever it rests on), and lets go only for a fast
  free swing.
- Spine at 0 % took the secondary motion of the head and arms with it: they only lagged in their own joint, and what looked like their
  motion was the spine's lag carried down. They now also swing by inertia when the body lurches (thrusts), not only when it turns.
- Physics hands: the drawn hand is now exactly your controller in free air; the physics offset fades in only on contact (VR → "Drawn
  hand is exactly the controller in free air"). A game camera cut of ~0.4 m made the hand race across the gap (log: jitter up to
  35 cm); smaller rig jumps are now carried along, and a hand that lost its target in free air snaps back. A hand held deep in her
  no longer tumbles (the pull eases off with the distance it is blocked).
- Fingers close like VaM's DynamicLimbs: each finger's curl sweep is tested against her body from the palm (it can't feed back on
  itself), the finger stops where it touches and sinks in by the part's softness (breast/butt deep, shoulder/forearm barely), idle
  fingers follow the touching ones, low finger friction so tips slide. Trigger curls the index finger, grip clasps the others, and
  the hand rests in a relaxed curl (VR → Rest curl).
- Embody: his fingers rest in a neutral pose (from his bind pose) instead of the animation's; trigger = index, grip = a clasp that
  closes around what you hold (her shoulder, her breast) and stops on her skin, deeper on soft parts. With physics hands off his hand
  now stops at her skin (it passed straight through), and his elbow swings around her instead of his forearm passing through her.
  His wrist offset was rotated by the physics offset (up to 3.5 cm off when blocked): fixed. His animated hand no longer pushes her
  while you embody him (it pushed with the animation's hand, not yours).
- With the soft zones off, her colliders were still inset for soft skin (2-4.5 cm), so the hands sank into her: the inset now follows
  the soft zones.

0.7.9:
- Physics hands still stuttered in 0.7.8 (the log: the pull at its limit in 87-99 % of steps in free air, the drawn hand jittering by
  ~8 mm and ~6 deg): the follow force and turning torque were sized for the whole hand (palm and fingers) but applied to the palm
  alone, so the palm got up to twice the intended push and 7-14 times the intended spin before its finger joints shared it, and both
  overshot every physics step. Now the hand gets a velocity change that removes only part of the error each step, applied to every
  body of the hand at once, so it cannot overshoot. The 10 s log line shows the follow error and how often it flips (should be rare).
- The partner's animated hands no longer push her limbs by default: his animations often hold her arms, and the 0.7.8 push moved
  them out of his grip (log: 35-40 N on her upper arms by Player). Physics → Pushes → "...and the partner's animated hands" turns it
  back on; the partner you embody still pushes (his hands are your controllers then).
- Reactions (new Reactions tab, off by default): she reacts to how she is touched, modelled on VaM's moan plugin and the
  Virtual Sweetheart scene. Each hand contact on a body part is a brush, caress (resting/stroking), grope (pressing hard) or a
  soft, hard or too-hard slap by how fast the hand arrives (1.2 / 2.3 / 4.5 m/s at 50 % sensitivity). Physics hands, your hand
  models and the partner's hands all count; her own limbs and hidden hands don't.
  - Responses, each with its own toggle: her voice through the game's own touch moan (or the plugin picks short gasps to long
    moans by intensity, with a little pitch variety; a breath for a caress), the moan face or a blink when the voice must stay
    quiet, a flinch (the limb swings and springs back, the skin jiggles), a slap sound at the spot (the game's sound-effect clips
    and volume), a blush on her cheeks that rises with excitement and after a slap and fades over ~20 s, and optionally she looks
    away after a hard slap or the game's own desire gauge rises.
  - Her voice never talks over the game's dialogue, kiss or scripted lines; during dirty talk only slaps get a moan (the game
    pauses its dirty talk for it); voice only in sex/foreplay mode.
  - States: excitement (tiers 1-4) rises with touch and fades after ten quiet seconds; pain from hard slaps makes her guarded (less
    voice, bigger flinches); a hard slap startles her; optional climax and afterglow. Buttons set a state; test buttons fire any
    reaction on any body part without touching her.
- The game's dirty-talk voice error (LadyVoice_Function.PlayRandomVoice IndexOutOfRange every frame, ~150-190 per 10 s in the
  logs, also restarting her dirty-talk face each frame) is repaired: when the game's play order no longer fits the current voice
  list it is rebuilt (Reactions tab → Game voice, on by default, independent of the reactions).

0.7.8:
- Physics hands stuttered and floated: the palm chased a controller pose that only changes once per rendered frame with a
  one-step "close the whole gap now" pull at the 50 Hz physics rate, so on every second step it braked to a stop and
  re-accelerated (the 0.7.6/0.7.7 logs show the 60 N pull limit hit in 88-100 % of steps with nothing touching). The
  game's own camera moves also left the hand racing at 6 m/s through her. Now the controller pose is sampled per frame
  with its velocity and extrapolated to each physics step (position and rotation), the hand keeps its offset when the game
  moves the VR rig, the drawn hand is interpolated between physics steps and refreshed right before rendering (after the
  headset's late tracking update and after Embody's view correction), and her colliders move smoothly between frames. The
  10 s log line now says how often the pull hits its limit in free air vs touching, physics steps per frame and the drawn
  hand's frame-to-frame jitter.
- Her colliders for the physics hands follow her shape: convex hulls from her skin (torso, pelvis, thighs, calves, arms,
  head) instead of 40th-percentile capsules, so the hand stops at the skin on the sides too; they follow her bone sliders
  and the character shape (VR → Body colliders: Convex hulls / Capsules).
- Hands grip her skin a little (VR → Grip friction): a resting hand is carried along when she moves.
- Pushes everywhere: hands push her arms, legs, head and spine aside and the pose springs back (Physics → Pushes); the
  push eases toward a limit instead of slamming into it. Not only the physics hands: your hand models and the partner's
  hands push too, by how deep they are in her ("Other hands push too"); her own limbs and hidden hands never push her.
- Panel: the old VR tab is now two tabs. Physics: soft zones, secondary motion, pushes, wrinkles. VR: Embody, VR hands,
  contacts.
- Fixes from the 0.7.7 headset log: the nipple meshes got no per-vertex contact (their skinning-space check was rejected
  as ambiguous on a small mesh); the 8 mm skin floor under a hand now applies to the body meshes only (no push-through at
  the nipple cap edge or clothes); breast colliders come off the moment Physics is switched off; an empty partner rig is
  no longer rebuilt and logged every 10 s; "Overlap: driving N bones" counts what it lists; a compensated wrinkle scale
  check is no longer a warning.

0.7.7:
- GPU contact and skin wrinkles actually load now: the shader bundle was built with Unity's AssetBundle module disabled in its
  project, so it had no AssetBundle object and the game refused it ("not compatible with this newer version of the Unity
  runtime"). Fixed in the bundle project; tools/shaders/build.ps1 now fails if that happens again. Install the new
  dist/sfmorph_shaders together with the DLL.
- Her breast/areola deformed with nothing touching her. Causes and fixes:
  - Hands nobody sees touched her: the hidden partner rigs (their animation often rests his hands on her breasts), a scene root
    listing the same hands twice, and controller models the physics hands replace. Now a hand counts only while it is drawn
    in the headset (Physics → "Hidden hands don't touch her"); nested rigs are counted once.
  - Contact began 2 cm before the skin (the soft points sit on the skin but the reach added 2 cm), and palms were 7 cm thick
    capsules. Now contact starts at the skin (3 mm) and a palm is three 3 cm thick capsules (VR hand models, partners,
    physics hands).
  - The hands pushed every breast DynamicBone of the game, nipple chains included, and with the physics hands on it was the
    hidden controller models that pushed. Now the hands you see push only the main breast chains (Physics → "Breast physics
    colliders": Off / Main chains / All); hidden hands are taken off them.
  - Gravity slid the 6 cm patch around the nipple (and pressed it in when she lies back); the game's breast physics already
    hangs the breast, so the breasts no longer get our patch sag (glutes, belly, thighs still do).
- Who touches her: the contact status names the hands ("contacts: 7 (breastFront_R 7) by your right hand 5, Player 2"), the
  debug markers show hidden hands' capsules grey, and the VR tab lists what each kind of contact feeds.
- Physics hands: breasts now have colliders (hands no longer sink into them), fingers give way while touching her, and your
  physics hands push her arms aside; the pose springs back (Hands → "Push her arms"). Skin outside the soft zones still gives
  8 mm under a hand (GPU), so fingers don't cut into her.
- Secondary motion v2: motion ripples down the chains (a fast torso turn swings head and arms a little later: Motion →
  Ripple), a bounce setting per body part, more head motion, and planted hands and feet stay where the animation puts them
  while the rest of the limb trails (Motion → Planted).

0.7.6:
- Breasts like the character's: her nipple cap is fitted onto the character's nipple and areola (nipple vertices to the
  nipple, disc to the skin, rim pinned), the skin around it fitted right up to the rim, her own fold no longer held
  stiff under the character's rounder underside, upper body and cap shaded as one surface. Nipple protrusion within
  0.5 mm, breast projection within 0.4 mm, no folded triangles on the chest.
- Face only without a neck seam: Presets → Export now also saves her original skin textures and tint
  (readback from the GPU, like her meshes); the pipeline then bakes a face-only face texture that fades into her own
  skin near the neck (25 mm band), with the character's overall tone shifted to hers and pre-compensated for the
  game's tint. It is used automatically when the face skin is on and the body skin off.
- Hooks for the procedural wrinkles (NormalFor; slots owned by the wrinkle pass are never overwritten or saved).
Export her again (Presets tab) and re-run tools/pipeline/run.ps1 to get the seam blend.

0.7.5:
- Procedural skin wrinkles (VR tab → Skin wrinkles, off by default): where her skin is compressed (smile lines, brow, around the
  eyes, neck; body optional), fine folds appear across the compression, computed on the GPU from her skinned mesh against her
  neutral shape (character shape and bone sliders included, so they don't read as wrinkles) and drawn into her skin's normal map
  (her mesh isn't changed: motion vectors and DLSS are unaffected). Folds fade in/out smoothly and keep their direction; eyelids
  are left out (no blink flicker). Strength, threshold, face/neck/body, a debug view (red where folds are active), Recapture.
  Needs dist/sfmorph_shaders next to the DLL. Method reimplemented from a description (no code from the VaM plugin that inspired it).

0.7.4:
- Breasts follow the character: her nipple mesh is a cap over a hole in her upper body; it is now fitted as a cap
  (harmonic from the fitted rim, snapped to the character's skin, her nipple relief kept) instead of being glued to
  half-fitted skin (it tore: edges stretched 22x). Breast projection matches the character within 1 mm.
- Face skin and body skin textures switch separately (Characters tab): facial likeness only = Body 0 %, Face 100 %,
  face skin on, body skin off.
- Sharper skin: 8K body texture (VaM's texel density), 4K face and feet, compressed on load (load time logged).
  The character's gloss is baked as a smoothness map but not applied yet (off until we know how the skin shader reads it).
- Pose check tools: the Characters tab records 6 s of her animation; tools/pipeline/replay.py replays it on her and the
  character offline with renders and metrics.
Re-run tools/pipeline/run.ps1 for existing characters.

0.7.3:
- Per-vertex contact on the GPU: a compute shader (dist/sfmorph_shaders, copy it next to SfMorph.dll) presses her skinned
  vertices out of your hands, fingers and the partner's hands right after Unity skins her and before anything renders, so the
  dent follows the exact shape at any mesh density (the soft points still give the broad dent and the jiggle). Her own limbs are
  left out. The skinning space is checked once per mesh with a GPU readback before the real deformer runs; "Smoke test (3 s)"
  inflates her by 1 cm and back to check the GPU path and DLSS motion vectors safely.
- Gravity (Physics, default 50 %): the soft zones settle toward the floor, depending on her pose: breasts hang when she stands,
  flatten on her back, drop when she bends over (breasts most, thighs least).

0.7.2:
- Hands felt rigid (0.7.0): the physics hand was velocity-driven (infinitely stiff) and the fingers only posed from grip. Now
  the palm follows the controller with a capped force and a PD torque (60 N in the air, down to 12 N as her skin compresses,
  so the hand slows and stops instead of hitting a wall), and the fingers are jointed rigidbodies (two per finger) with soft
  drives: they curl with grip, settle on her skin instead of passing into it, fold when pressed and spring back.
- Embody: his fingers curl with your grip (or copy the physics hand's fingers), and his arm trails fast moves a little.
- Contact: the dent now takes the shape of what presses (hand, fingers), the deeper the more the hand is held back; denser
  soft points (about 131 instead of 51). Physics → Quality: Performance (0.7.0 points), Balanced (dense, default), Fidelity
  (dense + her body mesh subdivided once with PN smoothing: rounder silhouette, finer dents; costs GPU skinning).
- Secondary motion (VR tab): her elbows, wrists, knees, head and spine trail fast animation a little and settle (damped
  springs per bone); fades out where a hand or her other leg touches her. Overlap 0 = exactly the game's animation.
- Our own hand meshes are no longer mistaken for a partner rig.
- Shader project for the next step (tools/shaders, Unity 2022.3.9f1 batch build → dist/sfmorph_shaders): a post-skinning
  compute deformer for per-vertex dents (not loaded by the plugin yet).

0.7.1:
- Characters keep her rig: the character's face goes on at its own proportions (eye spacing, nose, chin, ears,
  head width within about 1 mm), her body gets the character's surface on her own joint centres, so limb, finger
  and shoulder proportions stay hers and only the eyes and jaw move. Her shoulders/clavicles keep her shape (VaM
  shoulders come from a T-pose and stayed raised). Teeth, tongue and the mouth interior scale with the face (her
  teeth showed through the smaller lips). Eyeballs keep her size while the VaM export's eyes are closed.
- Face and Body sliders instead of one Shape slider (two blendshapes: body, face); old characters still load.
- The skin tint is set to white while the baked skin is on (it tinted the baked colour twice); her anime brow mesh
  is hidden while a character is on (the character's brows are in its face texture).
- Pipeline: any VaM female character (validation, PASS/WARN summary, run.ps1 -All). Re-run run.ps1 for older characters.

0.7.0:
- New **VR** tab (Physics, VR hands, Embody).
- Soft zones felt like "only jiggle" in 0.6.3: your own VR hands had no colliders (the hand finder matched UI objects named
  'HandJob_Panel'), and DynamicBone's pendulum turned a push into a sideways slide. Now:
  - Your hands are found through the game's own `VR_HandController` (UI objects are skipped), with partner hands and her own
    forearms, hands and thighs as before.
  - The zones are VaM-style soft points (3-7 per zone) under the skin, each a damped spring: a hand pushes the skin in by up to
    2-4.5 cm (Softness), it springs back, and nearby points bulge a little. Springiness lags them behind her own motion
    (secondary motion). Weights are still split, so untouched she deforms exactly as before.
  - "Show soft points" draws markers (green at rest, red touched) and the contact capsules; the panel shows live contacts.
- **VR hands**: physics-driven hands in place of the controller models: the game's own hands (or simple shapes), following
  your controllers 1:1 in free air, stopping at her body after the skin gives (her body as capsules sized from her skin),
  fingers curling with grip. They only collide with her (two unused physics layers) and press her soft zones.
- **Embody**: see through the partner's eyes. The game's own POV camera can end up elsewhere, so the view is ours: snap to
  his eyes, or follow them (position only; you turn your own head). His head can be hidden, and his left, right or both hands
  follow your controllers (two-bone IK on his arms). Everything is given back when turned off.
- Log: a one-time `VR rig survey` (VR rig, partner rigs, cameras, IK components) and `Physics survey` (layers, fixed step),
  so the next round doesn't need guesses.

0.6.3:
- Physics (Characters tab): soft zones, so hands and limbs press into her instead of clipping through. Her glutes,
  outer and inner thighs, belly and breasts (front and side) get helper bones under the skin, simulated by the game's own
  DynamicBone and colliding with capsules on the partner's hands and fingers, her forearms and hands, and her other thigh;
  the partner's hands also reach the game's own breast physics. Skin weights are split, not moved (weight w on a bone becomes
  w(1-f) on it and w f on a helper under it), so without contact she deforms exactly as before. **Softness** sets how far the
  skin gives, **Springiness** how much the zones lag and settle as she moves (secondary motion, natural by default).
  Works with or without a character shape; redone when the game or the shape swaps a mesh; turning it off gives every
  mesh, bone, weight and the game's DynamicBone lists back. The log has a one-time physics survey (the game's DynamicBones
  and colliders) and a `Soft zones on ...` line listing zones, helper bones, meshes and colliders.

0.6.2:
- Picking a character in the uniform scene looked badly wrong: dressed, the game swaps her upper body for
  `uniform_upperBody_normal` (and a uniform nipple mesh), which the loader treated as clothes following "the skin" — but
  only the skin meshes present in the scene, so her torso and arms followed her face, legs and feet (nails floated off
  the fingers). The pipeline now stores her whole fitted skin and all bind poses (`skin.txt`, `binds.txt`); body variants,
  clothes and anything unconverted follow that, whatever the scene uses, and get the skin's normal changes too.
  Re-run `tools/pipeline/run.ps1` for characters made with 0.6.0/0.6.1.
- Bones under the face rig, eyelid and thigh-skin drivers (no bind pose for their parent) were moved in their parent's
  animated frame; they now keep their ancestors' offset. Joint moves only use bind poses.
- Skin colour: the game's skin shader (`Shader Graphs/Skin`) keeps its colour in `_ColorMap`; the baked albedo goes there now
  (0.6.0 only set the normal map). Bake: VaM's eyelids (often caught mid-blink) no longer paint dark smudges on her open
  eyes; missed pixels get the character's own skin tone.
- No more full rebuild every few seconds: mesh data is cached, and each rebuild logs why (renderers added/removed, a mesh swapped).

0.6.0:
- Characters tab: converted VaM characters as cards (thumbnail + name). The shape goes onto readable copies of her
  meshes (built from the GPU readback, since the game's meshes are GPU-only) as a blendshape with a 0–100 % slider,
  swapped into her renderers and redone when the game swaps meshes. Her joints move with the shape (bind poses
  moved to match), so elbows and knees bend where the new limbs bend; bones the breast/hair physics simulates keep
  their parent's offset (the physics resets them every frame). Clothes and any mesh the pipeline didn't convert follow
  the nearest skin vertex and are pushed out where the new shape would poke through. The baked skin textures go into
  her skin materials (restored when you pick None or turn them off). Height stays hers (the VaM figure is scaled to her
  eye height), so scenes and the other character still line up.
- `tools/pipeline/`: one command (`run.ps1`) turns the newest VaM export and the newest export of her into a character:
  pose-match (her weights, VaM's rest pose), landmark warp, non-rigid fit of her skin onto the VaM body, offsets in her
  exact vertex order for all 33 meshes, joint offsets, texture bake on her UVs, and a check report with renders.
- VaM exporter: turns auto blink off for the snapshot (a blink caught mid-way gave half-closed eyes).

0.5.1:
- The 0.5.0 export wrote no meshes: the game ships all 36 of her meshes GPU-only (Read/Write off), and Unity refuses
  their vertices to scripts. The export now reads each mesh's vertex and index buffers back from the GPU and
  decodes them with the mesh's own vertex layout (positions, normals, tangents, UVs, bone weights, submeshes), so
  the data and vertex order are the game's. `meshes.txt` says how each mesh was read; failures are listed with the reason.

0.5.0:
- Character pipeline, step 1: Presets tab → "Export … for the character pipeline" writes her meshes in the game's
  vertex order (OBJ with UVs per submesh), bone weights and bind poses, and the bind-pose skeleton to
  `BepInEx\config\SfMorphs\export\`. This is the target a VaM character's shape is fitted onto (see
  `docs/PIPELINE.md`). The character log line now says how many of her meshes mods can read (needed for the export
  and for real morphs later).

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
