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

## 0.7.5 (skin wrinkles)

Install `dist/SfMorph.dll` + `dist/sfmorph_shaders` into `BepInEx\plugins`. Log: `Wrinkles: loaded ... kernels`, `_FlipY self-test`
(consistent or flipped once), per renderer classification (face/neck/body/not skin), `sheet '<mat>' base ... baked in .. ms`,
`memory:`, `skinned buffer ok`, scale check ~1.0, `first raster of sheet`, idle/awake lines. Headset: (1) VR tab → Skin wrinkles on,
Debug = Activation: red where her face folds in expressions, none at rest; (2) Debug off: smile lines/brow/eyes in the headset, look
for seams at UV borders (jaw, behind the ears) and shimmer with DLSS; (3) move a bone slider, press Recapture: no red at rest;
(4) Physics quality Fidelity on/off and switching characters: no stuck textures, wrinkles come back; (5) send log + screenshots.

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
