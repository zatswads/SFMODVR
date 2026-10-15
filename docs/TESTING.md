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
