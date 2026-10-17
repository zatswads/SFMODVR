# Testing

Nothing here can run in a cloud session: the plugin only runs inside the game on the user's PC. The loop is
**build → user tests in the headset → user sends `LogOutput.log` + desktop-mirror screenshots** (panel visible).

## Before handing a build over

1. `REFS=... sh build.sh` builds with no errors and no new warnings.
2. `strings -el dist/SfMorph.dll | grep <version>` shows the new `Plugin.Version`; check the user's log line
   `SF Body Morphs <version>` before trusting a report (stale DLLs happen).
3. Re-read the diff for: per-frame `FindObjectsOfType`/`GetComponent*` (scan is every 2 s, cached), writes that
   don't go through the trackers (they'd accumulate or never be given back), anything touching her root transform.
4. README changelog entry; version bumped; committed and pushed.

## Log signatures

- `SF Body Morphs <version>: panel on F8, looks in ...`
- `Game rig: MovePlayerAndGirl found, FacialFunction, LadyBreastPhysics_Function` (anything missing = the game
  changed; parts fall back to names).
- `Body changes apply after the game's animation, before its physics and IK` (if instead "Couldn't join Unity's
  frame loop", physics may jitter).
- One `Character '<name>': female, ...` block per session, with `sliders:` (bones per slider with parent and child
  count, `OFF:` reasons) and `bones (parent):`.
- Red flag: `SF Body Morphs stopped after an error` (everything it changed is given back; the exception follows).

## Checklist

- [ ] F8 lists only her (not the player, not the male partner); status "the game's woman found".
- [ ] Each Body slider changes only that part; greyed parts say why. Thighs/arms don't grow the calves/hands.
- [ ] Breasts resize and still jiggle smoothly (no jitter), also when grabbed (DynamicBone + CCD IK); hair too.
- [ ] Each group's sliders: moves go the named way (up / out / forward) on both sides, in any pose; helper
      sliders (breast top/bottom/inner/outer, thigh front/inner/outer) visibly change the skin; greyed ones say why.
- [ ] Face: each slider visibly changes her face in the named way on both sides; blinking, expressions and lip sync
      still play on top; face width/length don't distort the eyes when she looks around.
- [ ] Lengths (thigh, calf, arms, neck, torso) lengthen without skewing; Waist/Ribcage width/depth don't shear when she bends.
- [ ] Changing face type / pregnancy stage / clothes in the game's menu keeps her changes on the new meshes.
- [ ] Apply changes off = exactly the game's look; on brings it back.
- [ ] Restart: her changes come back (file in `BepInEx\config\SfMorphs\characters`). Presets save and load.
- [ ] Presets tab → Export: log `Exported N meshes ... to ...`; note any "not readable by mods" meshes. Zip the folder for the pipeline.
- [ ] Characters tab (0.6.0): the card shows (thumbnail + name); picking it logs `Character '<name>' on 'Reika': 33 meshes
      morphed, N follow the skin, M joints, built in ... ms` (no "skipped"); her body and face take the shape; the Shape
      slider blends 0-100 %; None gives the original back exactly (meshes, joints, textures).
- [ ] With the shape: elbows/knees/fingers bend at the right place (Move joints on vs off); breasts and hair still
      jiggle and don't shift away from the body; blink, expressions and lip sync still work; eyes sit in the sockets.
- [ ] Clothes on (game's menu): they follow the new shape without skin poking through (log: they count as "follow").
- [ ] Skin textures on/off: VaM skin vs hers; the log's `Material 'reika_body' (...) textures:` line lists the slots.
- [ ] SfNr alongside still fine (AO, passthrough cut-out, NR follow the resized body).

## Soft zones (0.6.3)

Log: one `Physics survey:` block (the game's DynamicBones and colliders), then `Soft zones on 'Reika' (...)` with zones,
helper bones (`zone@bone(weight)`), meshes and collider counts, and `Soft zones: partner '...'` per partner rig (humanoid or
by bone names, hands and finger segments). In the headset: a hand pressed on her thigh and glute dents the skin and it springs
back; thighs pressed together flatten a little; she jiggles slightly when she moves (Springiness 0 = none); Physics off,
Apply off and picking/removing a character give her exact look back (no stray bones, no broken skinning).

## VR tab (0.7.0)

Log first: `VR rig survey:` (what the VR rig, Player and BadGuy are made of), `VrHands: your hands: ...` (must not be "none
found"), `Soft zones on 'Reika'` (zones, points, helpers), `Physics hands:` lines (layers, grip source, mesh extraction),
`Embody:` lines (rig root, partner, head axes, hand frame). In the headset:
- Soft zones (Physics on, Show soft points on): touching her glute/thigh/belly/breast with your controller turns markers red and
  the skin dents and springs back; the status line counts contacts.
- VR hands on: controller models gone, hands there instead, 1:1 with your controllers in the air, fingers curl with grip, the
  hand stops at her body after the skin gives (no passing through), no jitter while resting on her.
- Embody on (Follow): the view sits at his eyes and stays there through his animation; his head is not in the way; his hands
  follow your controllers (try Left, Right, Both); turning it off puts the view and his head back.

## 0.7.2

Log: `PhysicsHands: <side> finger rig: 11 rigidbodies ...` per hand (no "finger rig failed", no "limit sign swapped"),
`Grip:` lines, `palm: force cap avg ... resist max ...` every 10 s, `Soft zones on 'Reika' (..., quality Balanced)` with
~131 points / ~250 helpers, at Fidelity `Subdiv` lines per body mesh, `Overlap: driving 16 bones`, `Embody: ... fingers 5/5`.
Headset: (1) free air: hands 1:1, no lag/jitter; (2) palm into a glute slowly: the hand slows and stops, hand-shaped dent,
fingers fold and spring back; (3) grip in the air, then on her thigh: fingers settle on the skin; (4) Embody, physics hands off:
his fingers curl with grip, his arm trails a fast swing; both on: his hand stops where the physics hand stops; (5) Quality
Performance → Balanced → Fidelity: smoother silhouette and dents in Fidelity, note frame time; toggle a character in Fidelity
(no stuck mesh); (6) Overlap 0 → 50 %: elbows/knees trail and settle, no pops; 0 = the game's animation; (7) the VrHands log line
no longer lists SfPhys_*_visual as a partner.

## 0.7.3

Install: copy both `dist/SfMorph.dll` and `dist/sfmorph_shaders` to `BepInEx\plugins\`. Log: `GPU contact: supportsComputeShaders=True`,
`bundle loaded, kernels Smoke=0 Imprint=1`, per renderer `... skinned buffer ok`, `space rootBone (... mm)` (best < 20 mm, others
> 100 mm). Headset: (1) Physics section shows "GPU contact: on, N renderers, space rootBone"; (2) Smoke test (3 s): she inflates
~1 cm and returns, move your head/hands meanwhile with DLSS on: no ghost outline (nothing visible: CPU skinning or timing; flicker:
wrong buffer; explosion: layout); (3) with physics hands, press a thigh with one finger: a sharp finger-shaped dent inside the broad
dent, nothing elsewhere; toggle "Per-vertex contact on the GPU" to compare; (4) Gravity 0 → 100 %: breasts hang/flatten with her
pose, settle without jitter.

## 0.7.5 (skin wrinkles; procedural folds, replaced in 0.9.0)

Install `dist/SfMorph.dll` + `dist/sfmorph_shaders` into `BepInEx\plugins`. Log: `Wrinkles: loaded ... kernels`, `_FlipY self-test`
(consistent or flipped once), per renderer classification (face/neck/body/not skin), `sheet '<mat>' base ... baked in .. ms`,
`memory:`, `skinned buffer ok`, scale check ~1.0, `first raster of sheet`, idle/awake lines. Headset: (1) VR tab → Skin wrinkles on,
Debug = Activation: red where her face folds in expressions, none at rest; (2) Debug off: smile lines/brow/eyes in the headset, look
for seams at UV borders (jaw, behind the ears) and shimmer with DLSS; (3) move a bone slider, press Recapture: no red at rest;
(4) Physics quality Fidelity on/off and switching characters: no stuck textures, wrinkles come back; (5) send log + screenshots.


## 0.11.26 (controller haptics)

Install `dist/SfMorph.dll`. Startup log, even with haptics off: `Haptics: runtime VirtualDesktopXR 1.1.0, XR_FB_touch_controller_pro
off -> OpenXR one motor per controller (OculusTouchController / OculusTouchController)` over VD. Over Link it should say `... on ->
OpenXR localized (grip, trigger, thumb actuators; QuestProTouchController)`.

- Debug tab → Haptics: each button buzzes that controller for 0.5 s. Over VD, trigger / thumb play on the main motor. Over Link each
  should be felt in its own place: the grip in the handle, the trigger under your index finger, the thumb on the thumb rest. If a
  button is silent: [Haptics] Path 1 (the XR device path), restart, and report the `Haptics:` line.
- VR tab → Haptics on (needs soft zones or physics hands), Log 2:
  - Tap her lightly: a tick. Pat, then slap harder: stronger, sharper pulses (`... pulse 0.xx for NN ms: slap at N m/s`).
  - Rest a hand: barely anything. Slide it: a low buzz that grows with speed.
  - Grab her (grab button) while touching: a squeeze, then a light hold.
  - A finger in her: a buzz by depth and a pulse per stroke (`into her vagina`, `in-stroke ...`).
- Report:
  - whether the levels feel right (Touch and rub too strong / weak, slap too soft);
  - any buzz that never stops (the `buzz off` line missing);
  - the `last 10 s:` summary while touching;
  - whether a resting hand chatters (onset pulses while you don't move).

## 0.11.25 (penetration sound and reactions)

Install `dist/SfMorph.dll`; put the chosen squishes in `BepInEx\plugins\SfMorph\Sounds\penetration`. Log: `Skin foley: ...
penetration N files`, `Penetration: openings vagina 4 bones (+4 inside), anus .., mouth 8 bones (+1 inside)`.

- Debug tab → Test penetration sound: a squish at her vagina. Test penetration stroke: the sound plus her reaction (a
  `Reactions: penetration: vagina in-stroke by the test button ... E a -> b` line).
- Physics hands (or hand models, or embodied partner hands): push a finger into her vagina → a sound as it goes in, one per
  in / out stroke while you move it, none while it rests; faster = louder; pulling out = a softer one. Same for her anus and
  her mouth (quieter). A palm pressed between her legs: no penetration sound (the rub / grab as before).
- With PenetrationReacts on: her excitement rises with strokes, moans / pussy or anal lines come at the voice gaps; off: sound only.
- Report: whether entry is detected too early (outside the labia) or too late, the stroke rate, and the log lines.


## 0.11.24 (athletic upper body, waist roll, wrinkle test)

Install `dist/SfMorph.dll` **and** `dist/sfmorph_shaders` (new muscle maps, bend slots 9 / 10). Log: `Bend: 7 of 7 joints bound`,
`Muscles: ...` layout with 32 muscles; `Bend: first bulge waist roll L/R ... trunk side N deg` in a side bend.

- Body → Torso → Athletic upper body 0 → 1: broader shoulders, thicker upper arms / forearms / neck; elbows, wrists and her head
  stay where they were (no longer arms); hands still meet where the animation puts them. 1.25 for more.
- Skin tab, muscles on, Debug → Flex test: back = fuller traps and the new neck mass / neck muscles (no lumps, no cords, no seam
  where the neck meets her face); arms = rounder deltoid caps. In play: she turns her head → the opposite neck muscle shows.
- A side bend (a pose that leans her trunk sideways): a soft roll above the hip with a crease under it on the side she bends to,
  nothing on the stretched side; at rest nothing. Too much / little: Physics tab → waist roll in a side bend.
- Debug → Wrinkle test: watch her face for each step. Tell me which steps are visible at all and how strong; compare with
  tools/wrinkles/preview/wrinkle_face_expressions.png. Then send the log's `Wrinkles: sheet 'face' peak compression` lines from
  normal play (they say how far her expressions activate the lines).

## 0.11.23 (skin sound sets)

Install `dist/SfMorph.dll`. Log: `Skin foley: ... pat 7 files, grab 6 files, rub 9 files`.

- Slide a hand over her skin: a soft run of skin strokes (no single loop, no fabric rustle); quicker sliding is louder.
- Grab button on her / a firm grope: a hand-on-skin press. A light landing: a soft skin tap. Tell me if any category is too loud or quiet.

## 0.11.22 (grab button sound, softer rub)

Install `dist/SfMorph.dll`. Reactions on, Sound and Skin sounds on, physics hands on.

- Put a hand on her and press the grab button: a grab sound at your hand, and a `grab sound: your ... hand closed on ...` log line.
- Slide a hand over her skin: the rub should sound like skin (dull, soft), not fabric.

## 0.11.21 (headset fixes)

Install `dist/SfMorph.dll` and `dist/sfmorph_shaders`.

- Physics at defaults (Reset the Physics tab; Quality now Fidelity): her arm pressing her breast leaves no sharp fold. Also check Balanced.
- Physics hands (jointed fingers): push the open hand fingers-first into her breast, belly and thigh: the fingers stop at her skin
  (they may sink a few mm where she is soft) and the hand stops behind them. No "finger rig" warnings / kinematic fallback in the log.
- Press with a hand and lift it: the darkening and the print have round, soft edges (no triangles); the print fades in about 2 s.
  Skin tab: print, print fade, smoothing, press shade change it.
- Muscles at Strength 2, Flex 2, Core 10+: defined, not lumpy; Obliques 0 leaves the waist smooth while the abs still bulge; a side bend
  brings the oblique on that side back. Pregnancy setting on: the abs fade out (log "Muscles: pregnancy level N").
- Pelvis (any genital type in the game's customisation): no dark band or hard edge where the genital mesh meets her body.
## 0.11.20 (pack voice in the climax scenes)

Install `dist/SfMorph.dll`. A voice pack on (e.g. Southern Domme), Reactions on, Mute game voice on. In sex mode let her climax (or
pick the climax choice) and let the player finish (creampie):
1. Log `Reactions: voice: filling the game's climax scene 'Bedroom_Missionary_Climax' (18.7 s) with the pack`, then
   `voice: scene clip 1 at 0.3 s: request '...'`, `scene clip 2 ...: climax`, ... and `voice: scene over (it stopped), N pack clips played`.
2. In the headset: the scene has her pack voice throughout (a line, a climax sound, a line ...), no overlap with the game's voice,
   her lips move with the lines; nothing new starts in the last 1.5 s; the next sex loop's moans come back as before.
3. Creampie: request lines and high moans; clean / finish scenes: afterglow lines. `[Voice] PackInScenes = false`: silent again.

## 0.11.19 (natural back)

Install `dist/SfMorph.dll` and `dist/sfmorph_shaders`. Flex test, Back 10-16 mm: no rods or tubes along the spine or across the shoulder
blades; the shoulder blades read as broad rounded masses, the lower back smooth with a soft valley; the obliques bulge over the belt.

## 0.11.18 (adonis belt, athletic back)

Install `dist/SfMorph.dll` and `dist/sfmorph_shaders`.

- Flex test, Core 10-16 mm: a deep crease from each hip bone towards the pubis with the oblique pad bulging over it; the lower
  abdomen wide, not a V.
- Flex test, Back 10-16 mm, and a pose with her hands behind her back: the spine a deep valley between two columns, the shoulder
  blades' muscles and the diamond between them, lats spreading out from the armpits.

## 0.11.17 (irregular abs, lower body subdivided twice)

Install `dist/SfMorph.dll` and `dist/sfmorph_shaders`. Physics quality Fidelity. Log: `Fidelity subdivided reika_lowerBody_normal ...: 2 levels`
and `GPU contact: baked reika_lowerBody_normal: V ~70000 ... edge ~3.5 mm`.

- Flex test, Core 10-16 mm: the abs are uneven (rows staggered left / right, blocks of different sizes, domed), no grid.
- A hand on her glute / thigh: a round dent with smooth shading, no triangle facets. Watch the Timing line (Debug tab) for the GPU
  contact cost with the bigger lower body; tell me if the frame rate drops.

## 0.11.16 (naturalistic sculpt, pecs and traps)

Install `dist/SfMorph.dll` and `dist/sfmorph_shaders`. Log: `Muscles: layouts body (30 muscles, ...)`.

- Debug tab Flex test, all areas at 10-16 mm: soft abdominal blocks (no sharp chocolate bar), an oblique pad over each hip,
  broad erector columns and a deep lower-back groove, shoulder blades and traps, lats tapering to the waist, pecs over the
  breasts, calves with two heads; no straight cut-off edges at the knees, hips, neck or shoulders.
- Arms forward / crossed or weight on the hands: the pecs come up; arms raised or propping herself up: the traps.
- The relief alone (Flex 0, Strength 1): the same shapes as shading, softer than 0.11.13's lines.

## 0.11.13 (sculpted muscles)

Install `dist/SfMorph.dll` and `dist/sfmorph_shaders`. Log: `GPU contact: muscle maps read back (... sculpted height 1024x1024)` and per
body mesh `muscles N verts [...] sculpted, belly -0.xx to 1.xx`.

- Debug tab, Flex test with Core / Back / Arms / Legs at 8 mm, then 16-20 mm: separate abdominal bellies with grooves between them,
  serratus fingers on her sides, lats and shoulder blades, the quad teardrop above the knee, calf heads; no crinkles or steps where
  two muscles meet (sides of the waist, hip, armpit).
- Fitness 0.3 at rest: a little shape stays; a squat / plank / arms overhead brings the matching areas out.
- With the Skin tab Strength (relief) at 0 the shape alone should still read; tell me which areas need more or less.

## 0.11.12 (calf seam, smooth dents, stroke ripple, lip timing, voice mute, Debug tab)

Install `dist/SfMorph.dll` and `dist/sfmorph_shaders` (the bundle changed). Log: `SF Body Morphs 0.11.12`.

- Calves: log `GPU contact: baked reika_foot ... muscles N verts` and a seam line `reika_foot -> reika_lowerBody_normal`. Debug tab,
  Flex test (Legs mm up): the calf bulge runs down to the ankle with no step ~12 cm above it; no grooves along the sides of the legs.
- Dents: press her glute / thigh with a hand: round pressed-flesh shading, no triangle pattern. Physics tab **shading smoothness**
  0 vs 8 vs 16 mm to compare; tell me which looks right.
- Stroke ripple: doggy / missionary with Partner body on: `Ripple: stroke #n by Player on ...` lines at the stroke rate, a visible
  wave on her glutes / thighs each stroke. **stroke ripple** 0 % turns them off, 300 % is big.
- Lip sync (Southern Domme or Pixie, Lip timing 0): the jaw and lips open together; the log line `Lip sync: audio output ...`.
  If the mouth is early or late against the voice in the headset, move **Lip timing** and tell me the value.
- Mute on: in a sex scene no game moans, feeling sounds or dialogue voice; pack moans instead (`voice: pack stand-in` lines,
  the panel shows the last one). Off **...and its dialogue voice**: story dialogue is audible again.
- Debug tab: every test button works from there; the other tabs have none left.

## 0.11.11 (expression under lip sync, new voice packs)

Install `dist/SfMorph.dll`. Log: `SF Body Morphs 0.11.11`. Reactions, Face, Face clips and Lip sync on. Voice packs installed under
`plugins\SfMorph\Soundsoice` (Pixie, Pixie Dominant, Southern Domme, Scottish Peach), curves under `config\SfMorphs\lipsync\<pack>`.
1. Pick each pack in turn: log `Voice pack 'Pixie': breath_calm 7, ... ; lines: nipple 0/0/0, pussy 8/0/0, ...`; the Lip sync block
   says `Pack 'Pixie': N curves` (Pixie 191, Pixie Dominant 207, Southern Domme 321, Scottish Peach 278). No `Maive` in the chooser.
2. Test: a pack clip with lip sync. First time: log `lip sync: the mouth poses from an older version were set aside as ...arkit-map-0.11.9.txt`
   and `lip map built 30 shapes for a N mm mouth (jaw up to M deg) ...`; Lip sync block `Map: 30 of 31 shapes posed`. Then
   `Lip sync: curve for <pack>\<clip> (...)`: her lips shape the words (round on o / u, wide on ee, closed on m / b / p), the lower lip
   never pouts or curls out; lip probe lines show single-digit mm and under ~25 deg. Too small / too big: Lip strength.
3. Make her smile (a caress at mid excitement, or the face test with a happy mood) and let a line play over it: the smile corners
   and the cheeks stay while the lips move; the brows and eyes keep the mood. Slider **Keep her expression's mouth** 0 %: the
   mouth goes back to the 0.11.10 look (neutral mouth + visemes); 100 %: the full smile under the visemes (check that the lips
   still close on m / b / p).
3b. Grab her (close a hand on a breast / thigh) with `Sounds\grab` empty: a soft squeeze / skin sound, not a knock.
4. Hard slaps with a pack on: exertion clips (short "argh" / gasp) and sometimes an ouch line; the mouth grimaces with them.
5. Southern Domme: guarded lines ("don't touch anything without my permission", "you can look but no touching") when she is
   guarded; her lines are long (up to 13 s): check that she isn't interrupted mid-line.

## 0.11.10 (Flex test)

Install `dist/SfMorph.dll` (the 0.11.8 `sfmorph_shaders` stays). Log: `SF Body Morphs 0.11.10`. GPU contact on (Physics tab), any
scene with her animating. Skin tab, Muscles, **Flex test**:
1. The label counts down "now: arms / back / core / legs at full"; each area visibly rises in turn (side view of the upper arm,
   her back from behind, the belly in profile, the thighs and calves) and settles back after its 2 s. Stop test ends it at once.
2. Log, 4 lines + `Flex test done`: `Muscles: Flex test <area> at full for 2 s: bulge up to X mm (... x belly 0.9x) on N verts
   (<meshes>)`. Send the lines: N = 0 or a belly far below 1 means that area has no geometry to move.
3. A bulge in the wrong place (e.g. "arms" lifting her chest): the Muscles debug view "Muscles" shows the region; screenshot both.

## 0.11.9 (agonizing laugh for Ticklish)

Log: `Reactions face: moods: ... Ticklish = '<clip>' (...; laughing in agony: Pain's eyes / brows and grief brows over it)`.
Headset: Reactions tab, Face clips, Ticklish "Test" (or caress her side / belly): a wide smile with squeezed eyes and raised, knitted
inner brows (a pained, helpless laugh), not a happy smile; the face panel line names the clip "+ '<pain clip>' eyes/brows + grief
brows". If the eyes close too much or the brows look angry, say so (the shares are APainEyes / APainBrows / ABrowInMm in ReactionsFace.cs).

## 0.11.8 (muscles as shape, firmness)

Install `dist/SfMorph.dll` and `dist/sfmorph_shaders`. Log: `SF Body Morphs 0.11.8`; no `old sfmorph_shaders (no MuscleFlex)`
warning; `GPU contact: muscle maps read back (27 muscles, region 1024x1024, detail 2048x2048)`; `GPU contact: baked reika_body...:
..., muscles N verts [core:.. back:.. legs:.. arms:..]` for her body meshes (none for face / neck / overlays); the status line ends
`muscles N verts`. Skin tab: Muscles on, GPU contact on (Physics tab). Headset:
1. Flexed elbow: bend her elbow past 90 degrees (a pose or Embody's hands): the biceps belly rises (side view: the upper arm's
   front outline gets rounder), fading to nothing at the elbow crease and the shoulder; no step at the biceps / triceps border.
   Arms slider to 0 -> it flattens (the relief and the bend bulge stay).
2. Back relief: Fitness 1, arms overhead or a back arch: the spinal erectors stand up on both sides of the spine with the groove
   between them kept, the lats show along her sides. Back slider 0 / Flex 0 -> flat again.
3. Firmness: Firmness 0.5. Press a relaxed biceps (arm straight) with a palm, then the same arm with the elbow bent past 90: the
   flexed one dents visibly less and the physics hand stops sooner. Firmness 0 -> both the same.
4. A squat / kneeling pose: the quads and glutes rise; lying on her back with straight legs raised: the hamstrings do NOT bulge
   (passive stretch), the abs do a little.
5. No seam or step at the waist (her upper / lower body meshes) and at the neck; the areolas, tattoo and vulva rim stay on the skin.
6. Perf line (10 s in the log / panel): `gpuRun` and the GPU ms with Flex 1 vs Flex 0, standing idle: report both numbers.
7. If a muscle bulges in the wrong place, Muscles debug view "Muscles" shows which region the skin belongs to (the bulge follows
   those regions); send a screenshot.

## 0.11.7 (0.11.4 headset fixes, lip sync)

Install `dist/SfMorph.dll`. Log: `SF Body Morphs 0.11.7`; `GPU contact: renderer reika_face_c space rootBone taken from ...` (and
reika_neck, reika_face_a) instead of "cannot be calibrated"; `Her body: segments from N body meshes` with `head (...): <n> verts`
(not "0 verts ... (default)") and a neck hull; `GPU contact: baked ...: edge X mm (skin Y mm)`; `Embody: neutral finger pose
straightened from the animation's hand` (when no bind pose); `Lip sync: map: ...` or `Lip sync: no mouth poses yet: seeded N
shapes ...`. Headset:
1. Panel: drag an edge / corner to resize (also smaller); the title bar moves it; ticking options or the 10 s timing update no
   longer makes it jump; tabs in the left column; Skin tab = wrinkles, muscles, marks.
2. Press / slap her cheek, jaw and neck: a small dent and marks (lighter than the body). Hands stop on her face / throat, not a few
   cm before it.
3. Fidelity on, press her back / breast with a palm: the dent edge is round and soft, no capsule outline (compare Balanced).
4. Reactions tab: skin volume slider; Test pat / grab / rub are audible; a hand sliding on her plays the rub (`rub sound on`).
5. Embody: on after a game restart it starts off; on, hands Both: controller models hidden, his fingers rest open (not the
   animation's grip), the grab button closes them and the thumb closes across the palm without bending backwards; View
   forward / up / right move the view.
6. Lip sync: Reactions tab, voice pack Anime or Rebecca, "Test: a pack clip with lip sync": the log line lists switch on, face
   survey done, curves N > 0, mouth poses > 0; then `lip probe at ...: jawOpen 0.xx, mouthFunnel ... -> bones: Lip_... x mm, y deg`.
   Her lips move with the words, not just the jaw. If the lips look wrong, refine the poses in "Mouth poses for the lip sync".
7. Sex scene with Physics -> partner's body on: ripples on her glutes / thighs at each stroke (`Ripple: impact #... by Player`).
8. Voice pack on + Mute the game's own voice: the game's moans / dirty talk are silent, the pack still speaks.

## 0.11.3 (built Surprise face)

- [ ] Log `surprise: built from 16 face bones ...` and the `moods:` line shows `Surprise = built: wide eyes, raised brows`; no
      `left out` line (else those bones are not animated by her controller).
- [ ] Reactions -> face test Surprise, and soft slaps: eyes open wider, brows go up (inner most), mouth drops a little; never closes the
      eyes or frowns. Too strong / weak: the constants SUpMm / SBrowMm in ReactionsFace.cs.
## 0.11.1 (pipeline by role)

- [ ] Presets tab > Export: the message says "N of them variants she doesn't wear now"; meshes.txt lists reika_face_a and
      reika_face_c (and the other variants) with the active column 1 / 0.
- [ ] `run.ps1` on that export logs `her roles: face ..., fitted ...; variants that follow ...` and `bake targets: ...` with one
      line per face material; character.txt has texture lines for each face.
- [ ] In the game: a converted character, then the game's customisation switches her face (a / c): the character's face shape
      and skin stay on the new face (log `Character '...' on 'Reika': N meshes morphed`, no "skipped").

## 0.11.0 (character import materials, slider fit)

- [ ] Log `SF Body Morphs 0.11.0`. Characters tab > Yin (re-baked on the PC): no dark or inside-out patches at the nostrils,
      lips, eyelids, ears, under the breasts or on the fingers; skin brightness close to her own (not washed out / glowing);
      pores still visible up close. Face-only mode (face skin on, body skin off): no seam at the neck.
- [ ] If her lipstick, eye shadow or blush still sits on top of the VaM face: note it (her `_AttachMap_*` / `_BlushMap` layers;
      the next step is a switch for them).
- [ ] Presets tab > Export with Physics and Fidelity on: log `Export: the game's own mesh written instead of our clone for ...`;
      the new folder's meshes.txt has 19469 (upper body) / 4533 (lower body) vertices and no "(SfSoft)", textures.txt no
      RenderTexture, a materials.txt with every Shader Graphs/Skin property. Send that log: it has the full property lines.
- [ ] Presets tab > `Lexi (fit)` > Load (with no character picked): her body takes the VaM character's proportions with her own
      skin; nothing pokes or skews; compare with the Characters tab version.
- [ ] Pipeline on the PC: `run.ps1 -Name <name> -Check` prints the material rows; `-Rebake` re-bakes (other characters).

## 0.10.0 (muscle definition)

Copy `SfMorph.dll` + `sfmorph_shaders`. Log: `SF Body Morphs 0.10.0`, `Muscles: layouts body (27 muscles, 2048x2048)` (if it says
"none ... install the sfmorph_shaders", the bundle is old) and, once on, `Muscles: 13 of 13 joints`. Physics tab -> Muscle definition
on (Wrinkles may stay off):
1. At rest: faint tone only (Fitness 0.3). Fitness 0 -> 1 makes all of it stronger; Strength 0 removes the relief; Groove shade 0 / 1.
2. Debug Muscles: the colours sit on the right muscles (abs on her belly, not her ribs; erectors either side of the spine; calves on the
   back of the shin; biceps on the front of the upper arm, triceps behind). Debug Tension: green at rest, the worked muscles turn red.
3. Poses (watch `Muscles: peak tension over 10 s: ... (now: trunk flex .. hip flex .. knee ..)` every 10 s): she curls forward or sits
   leaning back -> abs; arches or bends over -> back; squats / cowgirl -> glutes, quads; on all fours -> back, lats, arms; tiptoes ->
   calves; arms overhead -> serratus, lats; bent elbows -> biceps. Wrong sign = a muscle that tenses in the opposite pose: send the
   line with her pose. Lying flat should stay near rest.
4. No seams on the body atlas islands (shoulders, hips, thigh fronts); the abs' lines don't run under her breasts.
5. Areas Core / Back / Legs / Arms each switch only theirs off. Wrinkles on together: creases and muscles both show.
6. Cost: the `Perf` line's wrinkles values with Muscles on vs off.

## 0.9.9 (timing line)

Copy `SfMorph.dll`. Log: `SF Body Morphs 0.9.9`, then every 10 s `Perf last 10 s: N fps ...` (also at the top of the panel). Read
the line after ~20 s in each state, one change at a time, same spot in the scene (hands still, then while touching her):
(1) baseline as you play; (2) Physics hands off; (3) soft zones Quality Fidelity -> Balanced (watch `simulate`, `gpuRun`, GPU ms);
(4) GPU contact off; (5) Wrinkles off; (6) Reactions off; (7) Embody off; (8) all of SF Body Morphs' Apply off (`applyAll` near 0:
the rest is the game); (9) SF Neural Rendering on vs off (F6). What to read: `ours` = our CPU ms per frame; `physx` = the game's physics
(incl. the physics hands' bodies) summed over the fixed steps; `postLate` = HDRP's render submit on the CPU; `wait` = the main thread
waiting for the GPU / compositor (large wait + GPU ms near the frame time = GPU bound). Send the log.

## 0.9.8 (face wrinkles)

Copy `SfMorph.dll` + `sfmorph_shaders`. Log: `SF Body Morphs 0.9.8`. With Face/Neck on (Body may be off): no lines under her mouth; no
ring around her neck at rest; Debug = Regions shows the neck patch on the front of her neck (not the chin). Expressions: the brow, smile
and eye lines come and go; held ~20 s they fade (intended). The `peak compression` lines show `idle 0.0xx` per face region. Start with
Strength 1 and crease shade 0.3-0.5.

## 0.9.7 (skin seams, neck / face marks, ripple impact dip)

- [ ] Log: `GPU contact: skin seams stitched: reika_lowerBody_normal -> reika_upperBody_normal N on the seam + M blended, reika_neck -> ...,
      reika_face_* -> ...`; renderer lines for reika_neck and reika_face_*; no `old sfmorph_shaders (no SeamBlend)` warning.
- [ ] Press her stomach across the waist (where the upper and lower body meet), the neck, the jaw line: no gaps or cut-outs, no step.
- [ ] Grope or slap her neck, cheek, shoulder-to-neck: marks on the neck and face, continuous across the neck seam. Her own hand on her
      face: a slight dent, no redness.
- [ ] Ripple: the first dip under the hand softer than before, the rings as high as in 0.9.6; "impact dip" 100 % = the 0.9.6 look.
- [ ] GPU cost: the `first v2 frame` line (more renderers now) and no frame drops while touching her face.

## 0.9.6 (voice filter fixed)

- [ ] Voice pitch 1.2 / 0.85 and formant 1.15 / 0.88 on moans and lines: no crackle, no hollow or phasey sound, no warble on long moans.

## 0.9.5 (wrinkles tuned)

Copy `SfMorph.dll` + `sfmorph_shaders`. Log: `SF Body Morphs 0.9.5`. Then the 0.9.1 list. Face: expressions should now bring the lines to
60-100 % (the `peak compression` lines: a region at 0.03-0.04 should show ~70-100 % in brackets). Squat / lean forward: one soft roll
across the lower belly following its curve, no thin scar-like line. Armpit: does it ever show (it never did in 0.9.1)?

## 0.9.4 (ripple, marks only from hands, belly roll, rub sound)

- [ ] Log: no `old sfmorph_shaders (no ForeignMarks)` warning; `Config migration: [Physics] Ripple 1, RippleSpeed 0.5, ...` once.
- [ ] Slap her glute or thigh (about 1.5 m/s): a ring clearly runs out from the hand and is visible for about half a second. Physics tab
      sliders speed / wavelength / duration change it live; Ripple 200 % is bigger still. Log `Ripple: impact ... wave A ~17 mm`.
- [ ] Squat / sit with her thighs against her belly, arms resting on her sides for a minute: dents, no red or orange patch, no pale print
      when she stands up.
- [ ] A hand resting lightly: no redness. A firm squeeze held: slow reddening. Rubbing or groping firmly: reddens within seconds. Slaps:
      the slap mark as before.
- [ ] Squatting: her lower belly rounds forward softly, no groove line (the wrinkle lines there are the wrinkles work's).
- [ ] Rub: a soft swish while a hand slides, not a crackle. Reactions tab: "Rub sound" off silences only the rub (slap, pat, grab
      still sound).
- [ ] Neck / shoulder grain: compare with Wrinkles off and Marks off (both hand her original textures back). If the grain stays, it is
      not from our copies (report a screenshot of both).

## 0.9.3 (voice pitch / formant redone)

- [ ] Voice pitch 1.2: higher, same speed, natural (no buzz or metallic ring); 0.85: lower, still natural. Long moans: no warble.
- [ ] Voice formant 1.15 alone: smaller / younger, same pitch; 0.88: fuller. Lip sync unchanged (~45 ms late at most).

## 0.9.2 (voice-pack lines, audio-driven jaw)

- [ ] Voice pack Anime (or Pixie): log `Voice pack 'Anime': ... lines: nipple .../.../..., pussy ..., ...`.
- [ ] Lines on: caressing her breasts / between her legs / thighs speaks fitting lines at her excitement; log `line nipple Lv2 '...' (pack)`.
- [ ] Turn her guarded (several hard slaps): a guard line ("not yet", "give me some space"); touching her while guarded sometimes too.
- [ ] Hard slaps: sometimes an ouch line instead of a gasp. After the climax: an afterglow line within ~3 s.
- [ ] Her jaw moves with the pack's voice (log `jaw: Jaw_M opens up to N deg ...` after the face survey); not with the game's own voice.
- [ ] No stutter when a new line plays (each is read on first use, a few ms).
## 0.9.1 (authored wrinkles)

Copy both `SfMorph.dll` and `sfmorph_shaders` (the maps are inside the bundle; an old bundle is refused: `Wrinkles: off: the shader bundle
is older than this DLL`). Log: `SF Body Morphs 0.9.1`, `Wrinkles: loaded ... authored layouts: face (11 regions: ...), body (11 regions:
...)`, `_FlipY self-test ... consistent`, per renderer `sheet '<mat>' base ...` with `N region triangles [forehead_L:.. ...]`, coverage
~100 % for her face and body meshes (a renderer on another UV layout: `has no wrinkles: its UVs are not on the ... layout`, expected for
the foot's own material), `skinned buffer ok`, scale check ~1.0, `first blend of sheet`.
- [ ] VR tab -> Skin wrinkles on, Face + Neck + Body on, Debug = Regions: coloured patches only on the forehead, between the brows, beside
      the eyes, under the eyes, along the smile lines, on the nose, front of the neck; inside/back of the elbows, behind the knees, hip
      folds, armpit hollows, lower belly. Nothing on her breasts or shoulders.
- [ ] Debug = Off, her at rest (idle): no lines on her body; face calm. Then expressions (reactions, her face clips) and poses that bend
      her elbows / knees / hips / lean forward: the matching lines fade in. Debug = Activation shows which (red) live.
- [ ] Every 10 s while something moves: `peak compression over 10 s (threshold ..): forehead_L 0.0xx (45%), ...`. Send these: they set
      the per-region ranges. Regions that never show at all in the log never compressed past half the threshold.
- [ ] Strength 0 -> 2 and Crease shade 0 -> 1: lines deeper / darker in the bottom; no seams at UV borders (jaw, behind the ears, arm seams).
- [ ] Characters tab skin toggle with wrinkles on: no stuck texture; Recapture after a body slider: no lines at rest.

## 0.9.0 (skin sounds, voice packs, voice pitch/formant, face reactions)

Copy `SfMorph.dll`; the sounds are in `BepInEx\plugins\SfMorph\Sounds` (copied on the user's PC; never in the repo).
- [ ] Log `Skin foley: slap_soft 11 files, slap_medium 9 files, slap_hard 12 files, pat 16 files, grab built, rub built` on the first sound.
- [ ] Slaps sound dry (hand on skin), stronger slaps louder / harder; the panel's slap test plays a hard one. Skin sounds off: the game's
      clips again.
- [ ] Light taps (below a soft slap) make a soft pat; a grope's onset a short grab; stroking her skin a quiet rub that follows the hand's
      speed and stops within ~0.3 s of stopping; no rub while the hand rests.
- [ ] Voice pack Rebecca / Amy (Game voice -> Voice pack): log `Voice pack 'Rebecca': breath_calm 28, breath_heavy 40/445, ...`; caresses
      breathe, gropes moan low / mid / high by excitement, hard slaps give an exertion, the climax its climax moans; no game lines while on.
- [ ] Voice pitch 1.2 / formant 1.0: higher voice, same speed and lip sync; formant 1.15 alone: smaller voice, same pitch. Log
      `voice pitch/formant filter on ...` lists her voice objects (none skipped). Listen for crackle on long moans.
- [ ] Climax: log `face Climax '...'` (no "left to the game's own hook"), the feeling or a climax moan also when the game face is paused.
- [ ] `mood pools:` line lists 2-4 clips per pleasure level; faces fade in softly, not on every touch (lockout), and also when she moans by
      herself.

## 0.8.9 (0.8.8 headset fixes)

Copy both `SfMorph.dll` and `sfmorph_shaders` (the dent floor is in the compute shader).
- [ ] A fingertip pressed into her breast: no hard jagged line where finger and skin meet (skin follows the finger up to the cap);
      the soft edge around the dent is as before; `GPU contact: v2 on` (no v1 fallback, no "old bundle" warning).
- [ ] A slap on her glutes / thigh: a visible ring travels out (log `Ripple: impact ... wave A` now ~5-20 mm). Too much: lower Ripple.
- [ ] Marks boost 1 -> 4: prints get red faster and deeper; slap marks stronger; pressed shading never goes black.
- [ ] Hands -> "Lock her hands and feet while a limb yields": push her forearm: the hand stays, the elbow bends; off: the hand moves.
- [ ] Physics hand on breast / glute: stops ~1 cm earlier (log segment insets now ~0.014 breast/butt at softness 0.5); no hand visibly
      inside her while the dent still follows.
- [ ] Face reactions: the `moods:` line no longer picks a dance/idle/pose clip for Surprise.
- Pose desync (her lying, him in cowgirl): not explained by the log. Repeat with F8 "Apply changes" off: if it still happens it is the
  game; if not, note the moment (Embody on? a mesh swap / "her renderers changed" just before?).

## 0.8.8 (lines' game-path switch)

Install `dist/SfMorph.dll`. Log: `SF Body Morphs 0.8.8`. (1) The first line logs `lines: first use of the game's path: ... at index i of a
list of n (first segment k); the game's own shuffle (playNum p, ...) restored right after; played: yes`: i < k; (2) over a few minutes of
lines and the game's own dirty talk: no IndexOutOfRange, the voice guard stays at "watching" (no repairs right after a line), her eyes
react with the line; (3) Reactions tab, "Through the game's dirty talk" off: lines still play (no face, no subtitle), status "lines (own
clips: the game's path is off)". Then the 0.8.7 and 0.8.6 lists.

## 0.8.7 (face clips)

Install `dist/SfMorph.dll` (bundle unchanged). Log: `SF Body Morphs 0.8.7`; with Reactions on, a few seconds in: `Reactions face: bound to
'...' (N transforms ...), M face bones`, `K clips in the controller`, `X of K clips move her face`, the ranked lines (5 per line), `ranked
list saved to ...face-clips.txt`, `moods: Pleasure 1 = '...' (by name / by strength only) ...`. Headset: (1) while the survey runs (status
"surveying ..."), no flicker, no pose jump, no stutter worth noting; (2) Reactions tab, Face clips: each mood Test shows a distinct
expression for about 2 s and eases back; < > changes the clip, Test again; the eye reaction Lv1/2/3 and Blink buttons work; (3) caress
her breasts at Aroused 1 / 3 / 4: pleasure faces getting stronger; stroke her arm: ticklish; hard slap: pain face; (4) while she moans or
speaks a line, the face clip changes her eyes and brows but her mouth keeps the game's lip sync; (5) Allow climax: the climax face; (6)
bone sliders on the face (eyelids, lips, brows) still apply during a face clip, nothing stays bent after it; (7) Face clips off: no
survey, faces as in 0.8.6. Send the log and `face-clips.txt` (the auto picks are a guess until the clip names are known).

## 0.8.6 (her lines for the touched area)

Install `dist/SfMorph.dll` (bundle unchanged). Log: `SF Body Morphs 0.8.6`, then `Reactions: lines for voice language N: nipple a/b/c,
pussy ..., anal ..., public ... (Lv1/Lv2/Lv3 ...); feeling enter 0 ... 3 ...; the game's path ... usable` and the bridge line's `lines yes`.
Headset, Reactions on, in foreplay or sex mode, the game's dirty talk on: (1) Reactions tab: status shows ", lines"; press Aroused 1, pick
Breast, "Line for the zone": she says a nipple line with her talking face and the subtitle (log `line 1: nipple Lv1 '...' via game`);
(2) Hips + "Line for the zone": a pussy line; Butt: a general line; Arm: "has no lines"; (3) Aroused 3 / Aroused 4: Lv2 / Lv3 lines;
(4) "Feeling sound": a feeling sound with her mouth moving; (5) real touch: caress / grope her breasts until the state reads Aroused 1:
a feeling sound at the rise, then now and then a nipple line instead of a moan, never two lines within 8 s, never over the game's own dirty
talk or a moan; hand between her legs from the front: pussy lines, from behind: anal lines (event lines say which); (6) after a line the
game's own dirty talk carries on with its usual lines (not stuck on nipple lines), no IndexOutOfRange spam, the voice guard line stays
quiet; (7) game's dirty talk off in its settings: status "lines idle", moans as before; (8) Allow climax, reach E 100 %: the climax opens with
the strongest feeling sound; (9) lines off: only moans/breaths as in 0.8.5. Send the log (lines with "line", "feeling", "voice guard").

## 0.8.5 (soft points read Embody's hands)

Install `dist/SfMorph.dll` (bundle unchanged). Log: `SF Body Morphs 0.8.5`. Headset: (1) Embody on, his hands on (both), physics hands
off, Physics on, Show soft points on: put his hand on her breast / belly / glute where the animation does NOT have it: the soft points
dent and her breast moves under your controller, nothing dents where the animation's hand would be; GPU contact dents in the same place;
(2) let go of the controller pose (move the hand away): the dent goes with it, no dent left at the animated hand; (3) Embody hands off
(the animation drives his arms): his animated hand dents her as before; (4) Embody on with the partner hidden by the game: no dents
from him ("is hidden: its N capsules don't touch her" in the log); (5) physics hands on + Embody: one dent under the physics hand, no
second one at the animated hand. Send the log + screenshots.

## 0.8.4 (crease shade)

Install `dist/SfMorph.dll` + `dist/sfmorph_shaders` (new albedo pass; the old bundle logs "the shader bundle has no albedo pass" and greys
Crease shade). Log: `Wrinkles: sheet 'reika_face_a' albedo copy: <prop> '<tex>' -> RT WxH, crease shade 0.30 (folds 4.0 mm apart, 0.xxx m
per UV)` (metres per UV plausible: roughly 0.2-0.5 for the face; "unknown" = no shade), `Morph's skin state found`, `memory: ... albedo
RTs`. Headset: (1) Wrinkles on, Debug off, a smile / frown / closed eyes: thin darker lines in the crease of each fold on her face (nose-lip
line, crow's feet, brow), none at rest; Crease shade 0 = the 0.8.1 look, 1 = clearly dark; (2) no dark lines along UV seams (jaw, behind
the ears, hairline) while she makes expressions; (3) Characters tab: face skin on/off with Wrinkles on: the character's face skin appears
within ~5 s and gets the shade; off gives her own skin back (log "albedo slot handed back"); switching characters the same; (4) Body
wrinkles on + a slap / hand press (Reactions, marks on): redness and the pale print still show on her body (log "albedo via Wrinkles"),
creases too; Body off: marks as in 0.8.1; (5) Wrinkles off: her skin colour exactly as without the mod; (6) fps unchanged in a still
pose, < 0.5 ms more while her face moves. Send the log + screenshots.

## 0.8.3 (limb yield, planted lock, hands, Embody hands)

Install `dist/SfMorph.dll` (+ `dist/sfmorph_shaders` unchanged). Log first: `Grip: left trigger from VR_HandController.activateValue_left`
(and right); `PhysicsHands: finger conform: fan 8 steps x 2 probes ...`; `PhysicsHands: drawn offset fade on ...`; `Her body: yield sources
...`; `Overlap: yield v3 ... driving N bones`; with Embody `Embody: neutral finger pose from the bind pose of '<renderer>' ...` (root match a
few mm, axis change small). Headset:
1. Free air, physics hands on, 30 s: the drawn hand is glued to the controller. 10 s line: drawn offset raw avg < 3 mm, shown avg 0.0;
   at the cap free air 0-5 %; flips < 10 %. A scene/POV cut: `rig shifts` counts, no hand racing across the room.
2. Palm on her thigh, hold 2 s, lift: the hand stops at the skin, the thigh gives a little and settles; on lift it returns without a
   bounce. `Her body: first yield on thigh_L ...`, `Overlap: first yield on Hip_L ...`, 10 s `settled N`.
3. Push her forearm sideways and hold, then pull back slowly: the forearm gives at the elbow first, the upper arm follows; held still it
   stays still (no Newton's cradle, no fulcrum); `contact starts` < 50 per 10 s while holding.
4. Push her forearm from the front toward her back: the whole arm swings at the shoulder, the elbow never bends backwards
   (`Overlap: elbow hinge: ...`).
5. Planted lock on, Legs 0 %, Spine 100 %: her hands resting on her body / on him and her feet on the bed stay put while her torso lags;
   push a leg: the knee bends, the foot stays. Spine 0 %: head and arms still swing a little on thrusts and turns.
6. Grip around her upper arm: fingers close until they touch and stop; squeeze fully: they sink a little, not to a fist; on her breast
   deeper. The grab button (trigger by default, VR → Grab with) closes the whole hand, thumb included; nothing pressed: a relaxed curl (Rest curl).
7. Embody on, physics hands off: his fingers rest neutral (not the animation's); his hand stops at her skin (`Embody: his left hand stops
   at her skin ...`); clasp her shoulder: stops on the skin, deeper on her breast; reach across her: the elbow swings around her
   (`Embody: his left elbow swivels ...`); push her forearm with his hand: it yields (`... by your left hand (Player)`).
8. Embody + physics hands on: his hand stops where the physics hand stops, fingers copy the conform, no double yield.
9. Reactions on, slap her thigh: a twitch that settles (no jerk, no ring); 10 s Overlap line `kicks N (max v)`.
10. Grab with Trigger: the trigger closes the whole hand (physics hands and his), the grip does nothing (the game's menu); Grip/Either.
11. Embody snap with "Face the view to: His chest": your arms line up with his; View turn/tilt/roll act at once and don't add up.
12. Strength 2x-4x: bigger trailing, still settles, no pops (planted hands/feet still held).
13. Palm on her thigh / butt / forearm: the hand stops about at the skin (thigh ~1.4 cm in, butt ~2.3, forearm ~0.4), not 3 cm inside.
## 0.8.1 (contact visuals v2)

Install `dist/SfMorph.dll` + `dist/sfmorph_shaders` (new kernels; the old bundle logs "bundle has no v2 kernels ... v1 imprint"). Log: per GPU
renderer `GPU contact: baked <name>: ...` (classes, bend joints, ms), `GPU contact: v2 on (...)`, `first v2 frame` (renderers, dispatches,
CPU ms) and the `Tissue: caps at softness ...` line; no "v1" fallback warnings; `Skin marks:` sheet lines (which albedo property on
reika_body, "normal via Wrinkles" with Body wrinkles on); `Ripple: impact #...`; `Bend:` first bulge per joint. Headset:
(1) physics hand flat on her lower belly: a soft dimple with a rounded rim, no steps or hand-shaped cut-out, depth ~1.5-2 cm at Softness 50 %,
the shading darkens into the dent; Dent edge 5 mm = the old hard edge; Shading toggle off = flat-lit dent; (2) fingertip on her shin and
forearm: a few mm only; on a glute it sinks deeper and the hand meets more resistance as it goes in; (3) standing at rest: no dents on the
inner thighs or her sides; thighs pressed together / kneeling (calf on thigh) / arm against her breast: both sides flatten, Her own limbs off
= the old interpenetration; (4) elbow bent ~90 deg: biceps visibly fuller; sitting: thigh tops thicker, a shallow lower-belly fold; Bend 0 =
none; (5) slap her butt (Reactions on): reddish mark at the spot fading over ~15 s; a firm 2 s press on a glute leaves a pale print, then
redness; Marks off clears them; idle ~70 s: "idle, slots restored" and the Characters tab skin toggle still works; (6) a hard slap or the
partner's pelvis thrusting: a ring travels ~15 cm through the flesh and dies within ~1 s, breasts kick a little on a breast slap; Ripple 0 =
none; (7) fps with everything on vs GPU contact off (expect < 1 ms at Balanced); Fidelity still playable; (8) press a breast / the tattoo: the
areola and tattoo stay on the skin (log: `overlays follow the body under them: ...`); (9) Wrinkles (Debug off): broad soft folds, not thin
lines. Send the log + screenshots.

## 0.8.2 (reactions fixes)

Reactions on, outside and inside sex/foreplay mode: slaps give a sound and a voice (log "-> moan" or "-> voice clip", the panel's "last:"
line says why when not); caresses show in her eyes now and then; the 10 s summary lists skip reasons. Press "Diagnose sound and face" once
and send the log block "Reactions diagnose": it says whether the test tone played (2D and through the game's sound-effect mixer), the
mixer levels, our source's state and which Animator layers changed after the "moan" trigger.

## 0.7.9 (reactions, voice guard)

Hands first: physics hands idle in free air 20 s: the 10 s line shows "at the cap" free air near 0 %, "follow v err avg" <= 0.15 m/s
with flips <= 10 %, spin flips <= 10 %, visual jitter avg <= 1 mm and <= 0.3 deg; the hands look steady. If flips stay high, try VR →
Jointed fingers off and send that log too. Embody/partner: his animated hands holding her arms no longer move them.
Log: `Reactions: FacialFunction ...` bridge line once (what was found/missing, voice banks, SFX clips, blush materials and base);
`Voice guard: ...` when the game's dirty-talk order is rebuilt, and SfNr's "Unity error repeating ... IndexOutOfRange" lines stop.
Headset (Reactions tab → on, a sex/foreplay scene): (1) medium spank on her butt: slap sound at the spot, hip/thigh swing and settle,
glute jiggle, moan with face; again within 0.3 s: nothing; (2) hard spank: louder, bigger swing, cheeks redden and fade over ~20 s,
pain rises; (3) several hard face slaps: guarded (status), less voice, bigger flinches; Gaze away on: she looks away a moment;
(4) palm resting on her breast: a caress every 1.5 s with an occasional breath, excitement creeps; a hard press: one grope;
(5) idle 15 s: excitement decays; outside sex mode: "voice idle (mode)", slaps still sound and flinch; (6) physics hands off, hand
models (Other hands push too on): the same by your hand model; Embody: "by Player"; (7) test buttons per zone/tier and the state
buttons; (8) Reactions off: blush and look-at back to the game's values; the voice guard still works.

## 0.7.8 (hand follow, hulls, pushes, tabs)

Install `dist/SfMorph.dll` (+ `dist/sfmorph_shaders` unchanged). Log: `Her body: segments from N body meshes` with hull points per
segment and a `hull frame check` line (warns if the hull and the skinned vertices disagree), `PhysicsHands: her hulls cooked: N ok`,
`PhysicsHands: friction ...`, no `space ambiguous` for the nipple meshes. Headset: (1) physics hands idle in free air 20 s: the 10 s
line shows "at the cap" free air near 0 %, steps/frame plausible, drawn-hand jitter small (mm, deg); hands no longer stutter;
(2) a game camera/POV cut: "rig shifts" counts, no `first contact ... 6 m/s`; (3) Show soft points: orange hulls follow her torso,
thighs, arms, also with a leg bent (pelvis hull doesn't roll); the palm stops at the skin on her sides; VR → Body colliders Capsules
shows the old sinking; (4) press her thigh/forearm/chest/head: it gives and springs back (`Overlap: thigh_L pushed by ...`); a hand
held in her settles short of the limit; (5) physics hands off, hand model or Embody partner hand into her forearm: `Her body: first
push on forearm_L by ...`; her own limbs and hidden partners never push; (6) a resting hand is carried along when she moves; no
judder when stroking (else lower Grip friction); (7) Physics tab / VR tab hold the right rows; Overlap 0 + Push 0 = the game's
animation; Physics off removes the breast colliders at once.

## 0.7.7 (contacts, Overlap v2, bundle)

Install `dist/SfMorph.dll` + `dist/sfmorph_shaders` into `BepInEx\plugins`. Log: no "not compatible with this newer version" error;
`GPU contact: ... bundle loaded`, `Wrinkles: loaded`; `VrHands: visibility against camera '...'`; `VrHands: Player is hidden: its N
capsules don't touch her` (when the partner isn't shown); `partner '[Character]': ... (N under Player/BadGuy skipped)`; `N hand
colliders ... on M breast DynamicBones [...]; skipped K [...]` (which chains are pushed); `Contacts feed: ...`; `PhysicsHands: her
colliders ... breast_L (...)`; `Overlap: v2 (ripple, planted lock, push) driving 16 bones`. Headset: (1) controllers idle away from
her: contacts 0, breast/areola identical to Physics off (compare with the toggle) at any Gravity; (2) Show soft points: hidden
partner capsules grey; your hand model's capsules turn red only when the mesh touches the skin; (3) Embody on, his hand on her:
contacts "by Player", breast moves, areola not pushed before touch; (4) physics hands: palm stops on the breast, fingers bend on
her, a brush on her forearm moves the elbow and it springs back; (5) Overlap: a hand resting on him stays put while the elbow
trails; after a fast torso turn the head follows a little later; Ripple 0 / Bounce sliders change the feel; Overlap 0 + Push 0 =
the game's animation; (6) Skin wrinkles on (Debug = Activation): red where her face folds.
