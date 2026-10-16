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

0.9.7 (skin seams stitched, marks on her neck and face, ripple impact dip):
- Pressing her stomach opened cut-outs in her skin. Cause: her skin is several meshes (upper body, lower body, neck, face) and each one
  worked out its dent alone; its skirt and smoothing only saw its own side of a seam, so a press across the seam (her stomach is where the
  upper and lower body meet) moved the two edges by different amounts and the seam opened. Now the lower body is stitched to the upper
  body, the neck to the body and the face to the neck: within 1.5 mm of the other mesh a vertex moves exactly like the vertex across the
  seam, fading to its own dent over 1 cm. Log: `GPU contact: skin seams stitched: ...`.
- Marks stopped at her neck and her head had none, so the neck seam showed. Her neck and face skin now join the contact chain: hands and
  the partner leave marks there too, the face sheet gets its own marks, and both get small dents limited by her anatomy (a few mm on
  bone). Her own hands on her face dent it a little, without marks.
- Ripple: the first dip under the hand was too spiky while the spreading rings had a good height. New Physics slider "impact dip"
  ([Physics] RippleImpact, default 35 %): the wave's depth at the impact point, rising to the full ring height one wavelength out.
- The face wrinkle lines under her mouth and the line around her neck are wrinkle work (authored maps); they went to that work with the
  references you sent.
- Needs the new `sfmorph_shaders` (rebuilt); with an older bundle the log warns and none of the three above apply.

0.9.6 (voice pitch / formant: crackle and hollowness fixed):
- 0.9.3's voice samples sounded crackled and hollow: its grain marks snapped to the loudest sample, which jumps between the peaks of a
  cycle, the new grain positions started at an arbitrary phase, and each grain was cut with a symmetric window, so neighbouring grains
  partly cancelled and clicked. Now each mark is placed where the waveform best matches the previous cycle (cross-correlation), the grain
  windows reach exactly to the neighbouring marks (they sum to 1: at pitch 1 / formant 1 the input comes back exactly, 135-145 dB),
  synthesis starts every voiced stretch on its own mark, breaths and hiss are copied in place, voicing has hysteresis (no on/off inside a
  vowel), and pitch below 1 stretches the grain windows to the new spacing (no buzz between pulses).

0.9.5 (wrinkles tuned from the first headset run of 0.9.1):
- Her face barely wrinkled: her bone-driven face compresses only 0.03-0.05 in an expression (log: glabella 0.047, nasolabial 0.029),
  but the face regions needed 0.08-0.10 beyond the threshold for full lines, so they stayed at 0-30 %. The face ranges are now
  0.025-0.035, and the face lines are 30 % deeper (her skin's subsurface scattering softens them).
- Squatting drew a thin line across her lower belly like a scar: the belly region was two thin straight creases. It is now one broad,
  shallow roll that follows the belly's curve (lowest in the middle, rising to the sides) with a raised rounded edge, and a fainter
  fold above; the crease shade of a fainter fold is weaker too. (The red band and the inward groove in that screenshot were skin marks
  from her own thighs and the bend groove, both fixed in 0.9.4.)
- Bundle rebuilt (maps); includes 0.9.4's shaders.

0.9.4 (ripple, marks only from hands, belly roll, rub sound):
- The ripple was better but still too subtle and gone too fast. Cause: a 1.5 m/s slap started an 8 mm wave that ran out at 1 m/s
  through a fixed 12 cm falloff, so it was over in about 0.15 s. Now it starts bigger (14 mm per m/s above 0.3 m/s, up to 35 mm;
  Ripple 0-200 %, default 100 %, never deeper than 1.5x her flesh there), travels at 0.5 m/s, is 10 cm crest to crest, reaches two
  wavelengths and fades to a third in 0.6 s. Physics tab: new sliders under "Ripple on impact" for speed, wavelength and duration
  (the [Physics] RippleSpeed / RippleWavelength / RippleDamping keys were config-only). Values still at the old defaults move to
  the new ones once (log: `Config migration: [Physics] ...`).
- Her thighs pressing her belly in a squat (and her arms on her sides) left an orange-red patch. Cause: the marks took their pressure
  from every contact, her own limbs included, and any resting press reddened over time. Now redness, the print a hand leaves and the
  pressed-flesh shade come only from hands and the partner: each of their capsules carries a mark weight from its speed relative to
  her pelvis (resting 0.4, a moving or slapping hand up to 1.5), and only a press past a knee reddens (a light resting hand: nothing; a
  firm squeeze: slowly; a moving press: fast). The build-up gain is lower (0.35 per second, was 0.6). Her own limbs still dent her and
  add a faint contact shade; they never redden or leave a print. Slaps still leave their mark.
- The line across her lower belly in a squat (like a scar) was partly ours: the 0.8.1 belly bend fold pulled a narrow band 4 cm above
  her hips inward and the bent normals shaded it as a groove, with the red self-contact patch under it. The belly now rounds forward
  in a broad soft roll centred 7 cm above her hips (BendBellyFoldMm default 3 mm, was 5). The authored belly wrinkle lines and the face
  wrinkles' sensitivity (her face's real compression is about a third of the face ranges) are with the wrinkles work, next release.
- The skin rub sounded like rubbing carpet. Cause: the built rub loop was 0.5-4 kHz noise with a random level jump every 10 ms, a rough
  crackle. Now a soft, breathy swish (180 Hz - 1.2 kHz with a faint airy sheen, a slow stroke-like swell, no grain), a little quieter.
  Reactions tab: "Rub sound" turns just the rub off ([Reactions] RubSound); slaps, pats and grabs keep sounding. Your own WAVs in
  Sounds
ub still come first.
- Needs the new `sfmorph_shaders` (rebuilt); with the old bundle the log warns and marks still come from every contact.

0.9.3 (voice pitch / formant redone):
- Voice pitch / formant sounded robotic and the formant slider barely did what it says: 0.9.0 shifted the linear-prediction residual
  with a fixed 25 ms delay-line window, which smeared her glottal pulses at every crossfade and blurred the envelope. Now TD-PSOLA:
  her pitch is tracked (YIN, 80-900 Hz), the voice is cut into one-pulse grains (one period either side of each pulse), and they are
  laid down at the new period (pitch) and resampled (formant); breaths and hiss keep their own timing. Checked offline on real lines
  and moans: pitch 1.20 moves 314 -> 378 Hz, formant alone leaves the pitch, formant 1.15 / 0.88 moves a known vowel's formants up 19 % /
  down 9 %, no clicks or added hiss. Latency while on about 45 ms (bypassed at 1 / 1).

0.9.2 (voice-pack lines, audio-driven jaw):
- Voice packs can now speak: `Sounds\voice\<pack>\lines\nipple_1..3|pussy_1..3|anal_1..3|public_1..3` (the game's line banks by level)
  and `invite|request|afterglow|guard|ouch`. With Lines on, a voiced touch sometimes speaks the area's line at her level (an area without
  lines: her general lines; tier 4: sometimes asking for more; tier 1-2 outside sex mode: sometimes inviting), on the moan source, so
  neither sex mode nor the game's dirty talk is needed. Guarded: a guard line when she turns guarded and sometimes when touched while
  guarded; afterglow: a line 1.5-3 s after the climax; a hard slap: 40 % an ouch line instead of the exertion. Two packs built from the
  vamX voices Maive and Anime (personal copies on the user's PC; lines sorted by their file names' transcript and arousal tags).
- A pack voice moved no lips (the game's lip sync is baked into its own clips): her jaw now follows the pack audio's loudness
  (AudioSource.GetOutputData, relative to a slow peak, open 30 ms / close 90 ms), up to 75 % of the widest jaw opening any of her face
  clips writes (found by the face survey, log `jaw: ...`). Needs Face clips on. The game's "moan" trigger is skipped for pack moans then.
- Voice packs read each clip on first use and keep the last 64 in memory (0.9.0 loaded up to 280 clips at once; a vamX pack has thousands).
0.9.1 (skin wrinkles redesigned: authored lines faded in per region):
- On her body the wrinkles were dark zebra stripes over her breasts, shoulders and brow at rest, and ridges over the whole torso; on her
  face they were barely visible. Cause: 0.7.5-0.8.8 synthesised a sine fold pattern wherever any skin compressed, along the compression
  direction. Her bind pose isn't her rest pose (idle arms, breast scaling), so the shoulders and breasts are always a little compressed and
  got folds; real skin only creases in a few places, and her face (bone-driven, coarse mesh) compresses smoothly, so the folds there
  stayed faint.
- Now, like games' expression wrinkle maps: authored crease maps on her own UV layouts (`tools/wrinkles/author.py` builds them from her
  Presets-tab export, placed from her face and limb bones): face = forehead lines, frown lines, crow's feet, under-eye, smile lines,
  nose, front of the neck; body = inside and back of the elbows, behind the knees, hip folds, armpits, belly. Each region's lines fade in
  (attack / release) by how much her skin compresses *across* them, measured per region on the GPU. A frown doesn't raise the forehead
  lines, and the stretched outside of a bent elbow stays smooth. Breasts, shoulders and the rest of her skin carry no region (her breasts
  are also an exclusion zone in the authoring), so they never wrinkle.
- The crease shade (Cavity) now darkens the authored line bottoms. Debug view: Activation (active lines red) or Regions (each region in
  its own colour). The log prints each sheet's peak compression per region every 10 s while something moves (for tuning Threshold).
  [Wrinkles] SpacingFace / SpacingBody are gone. Her face's wrinkle texture is now 2048 (the lines are authored at 2048).
- CC4's expression wrinkles (soupday's CC Unity tools) were considered. They need CC4 4.2+, a CC3+ character exported with wrinkles on,
  and a transfer of their maps to her face's UVs, and Reallusion's textures can't ship in this repo. CC4's runtime driver is its
  expression blendshapes, which her bone-driven face doesn't have. Maps made with CC4 (or by hand) can replace the authored ones: same
  file names, then rebuild the bundle.
- A face mesh on another UV layout (another face type) gets no wrinkles; the log says so (`its UVs are not on the 'face' wrinkle
  layout`). Export her with that face and run `tools/wrinkles/author.py` to add it.
- Needs the new `sfmorph_shaders` (rebuilt); an older bundle is refused with a log line.

0.9.0 (skin sounds, voice packs, voice pitch/formant, face reactions):
- The slap sound was too wet: it used the game's sex_fast / sex_normal clips (thrust sounds). Slaps now come from your own WAV files in
  `BepInEx\plugins\SfMorph\Sounds\slap_soft|slap_medium|slap_hard` by strength (soft slap: soft or medium by force; hard: medium or
  hard; too hard: hard), falling back to the next strength and then to the game's clips. New skin sounds: a pat on light taps (a hand
  landing at 0.45-1.2 m/s, below a soft slap; before, nothing), a grab when a hand closes on her (the grope's onset) and a skin rub loop
  while a hand slides over her (the touch point's speed in her segment's own frame, 4-40 cm/s; louder with a firmer hand). pat / grab / rub
  use your files in those folders, else built-in dry sounds. Toggle "Skin sounds" under Slap volume ([Reactions] Foley, on). Files are
  read once per game start (log `Skin foley: ...`); the plugin ships no audio.
- Voice packs: her reaction voice from WAV folders in `Sounds\voice\<pack>\breath_calm|breath_heavy|moan_low|moan_mid|moan_high|climax|exertion`
  ([Voice] Pack, chooser under Game voice). Moans by her excitement, exertion on hard slaps, climax moans, calm / heavy breaths; at most
  40 clips per category are loaded. While a pack is on, the game's lines and feeling sounds stay off (they are another voice); the game's
  own moans during sex are still the game's.
- Voice pitch and formant ([Voice] Pitch 0.75-1.4, Formant 0.8-1.25, sliders under Game voice): an audio filter on her voice sources
  (moan, dirty talk, voice loop, dialogue) changes pitch without changing speed (the lip sync stays) and the formants (the voice's size)
  separately; at 1 / 1 it is bypassed. Works with Reactions off too.
- Both climaxes in the 0.8.8 log played no climax face ("left to the game's own hook"): the game's eye reaction had once played the
  same clip (Feeling_enter_3), and 0.8.7 then left that clip to the game for the rest of the session. A clip is now left to the game only
  while the game is playing it at that moment, and never for the climax.
- The second climax had no feeling sound ("game face paused"): with our climax face on her, a paused game face no longer blocks the
  climax voice (dialogue, dirty talk and a sounding moan still do).
- Face reactions, ideas from a VaM scene's facial reactions: each pleasure level and the climax play from a small pool of clips of the same
  family (random, not the last two), pleasure and climax faces fade in 0.35 s and out 0.75 s (slaps stay quick), a pleasure face is
  followed by a random 2.5-5.5 s lockout, a face also starts when any moan of hers starts (the game's own too), and every climax moan
  renews the climax face.

0.8.9 (headset test of 0.8.8):
- A hard, pixelated seam around a finger's dent on her breast: the dent's two smoothing passes lowered it under a small contact (a
  fingertip) below the real penetration, so her skin stayed a few mm inside his finger and the depth intersection drew an aliased line.
  The raw contact depth (already pressed against the tissue cap) is now the floor of the smoothed dent; the soft edge around it is
  unchanged. Needs the new `sfmorph_shaders` (rebuilt).
- The ripple never showed: impacts made waves (the log has them, 2-5 mm), but the ring's falloff cut them to about 1 mm within 0.1 s.
  The wave now starts at 12 mm per m/s of impact speed from 0.4 m/s, capped at 25 mm (a 1.5 m/s slap at the default 60 % is ~8 mm).
- Skin marks too subtle: new "boost" slider under Marks ([Physics] MarksBoost, 1-4x): multiplies how fast marks build up, how red they
  get (saturates sooner), slap marks and the pressed darkening (capped).
- Limb yield: new toggle "Lock her hands and feet while a limb yields" ([Hands] YieldLock, off): while a hand pushes her arm or leg, her
  hand or foot stays where the animation puts it and the elbow / knee bends instead (before, a push on the forearm or calf freed the
  planted lock on purpose so the hand could move). Works with or without the planted lock.
- The physics hand still sank into her breasts and glutes: her colliders there were inset by the full tissue depth (2.4 cm inside the
  skin), but the GPU dent follows a press exactly only to 0.6 of its cap and then saturates, so the last centimetre showed the hand
  inside her. The collider inset (and Embody's hand stop) is now 0.6 of the tissue depth.
- Face reactions: Surprise picked the clip 'dance_P' (strongest eye movement, by strength only) and played it 21 times. Strength-only
  picks now skip clips named for body motions (dance, walk, idle, pose, sex positions).

0.8.8:
- Her lines (0.8.6) can now be kept off the game's dirty-talk path: Reactions tab, "Through the game's dirty talk" ([Reactions]
  LinesGamePath, on by default). Off, the clip plays on the game's dirty-talk source without her talking face or the subtitle, and the
  game's dirty-talk state is never touched. On, the first use logs the clip's index, the list sizes and the game's own shuffle that was
  put back, so a headset log shows the path is safe. Checked in the game's code: nothing reads the dirty-talk shuffle after the call
  returns (the face and the eye reaction get their numbers as arguments), so the game's later dirty talk and the voice guard see only
  its own state.

0.8.7:
- Her face barely reacted to touch: only the game's eye reaction, a blink or a wince, never the expressions her face Animator holds. Now
  a one-time survey (a few seconds after Reactions finds her) samples every clip of her face Animator's controller, measures how much each
  moves her face bones (eyelids, brows, mouth, cheeks) against her neutral face, logs the ranked list and saves it to
  `BepInEx\config\SfMorphs\face-clips.txt`. Each sample is undone in the same frame (every transform, active flag, renderer flag and
  property block under her Animator is put back), so nothing of it is ever drawn.
- Moods: Pleasure 1/2/3, Ticklish, Surprise, Pain, Climax get a clip each (by name, then by strength; speech and blink clips are left
  out). A caress or grope on her breasts, chest, hips, butt, thighs or neck shows pleasure by her excitement tier; on her arms, calves,
  belly or face a ticklish (caress) or surprised (grope) face; a soft slap surprise, a hard one pain; the climax its own face. The clip
  plays over the game's face on a face-only layer (her face bones only, eased in and out, Face strength slider), before the bone sliders,
  so they still apply; the eyeballs stay with the game's look-at, and the mouth stays the game's while she speaks or moans (its lip sync
  is in its clips). A clip the game's own eye reaction already plays is left to that hook (watched and logged when it fires).
- Reactions tab, Face clips: on/off, strength, the survey status, per mood the picked clip with < > to walk the ranked list (saved in
  `[Reactions] FaceMap`), Test, "Back to the automatic picks", and test buttons for the game's eye reactions Lv1/2/3 and its blink.

0.8.6:
- Her touch reactions only ever moaned, breathed or played the touch loops: the game's own recorded lines for the touched area were never
  used, although the game has them per voice language (the dirty-talk library's nipple, pussy and anal lines at three levels, its
  general lines, and the four feeling sounds). Now she speaks them (Reactions tab, "Speak lines for the touched area", on by default,
  with a Line chance slider, 50 %): breasts get nipple lines, between her legs pussy lines (front) or anal lines (back), butt, hips and
  thighs her general lines; the level follows her excitement (tier 1-2 Lv1, 3 Lv2, 4 Lv3; at tier 0 she still only moans). When her
  excitement rises a tier she makes the matching feeling sound (Feeling_enter_0..3), and the climax opens with the strongest one.
- The line goes through the game's own dirty-talk path (it builds the list for that area and level and plays our pick through
  PlayDirtyTalk), so her talking face and the subtitle come with it; every dirty-talk field that path changes (type, list, shuffle,
  level) is put back right after, so the game's own dirty talk and the 0.7.9 voice guard carry on as before. If that path can't be used,
  the clip plays on the game's dirty-talk source (no face) and the log says which path ran.
- Lines are stricter than moans: only in sex or foreplay mode with the game's dirty talk on, never over dialogue, a kiss, the game's
  feeling loops, its dirty talk or a moan still sounding, at most one per 8 s (scaled by Voice gap). Only the game's recorded clips are used.
- Test buttons: "Line for the zone" and "Feeling sound". Log: the clip counts per category and level once per voice language, the
  first 10 lines with clip name and path, and "line ..." / "feeling enter ..." in the event lines.

0.8.5:
- With Embody on, the soft zones dented her and pushed her breasts where the game's animation had his hand, not where you held it: the soft
  points read his hand capsules before Embody moved his arms to your controllers (the game's animation rewrites his arms every frame,
  Embody puts them back at the end of our step). The soft points now run after Embody, so his hand dents her under your controller,
  including where his hand stops at her skin. GPU contact read his hands after Embody already (it runs after skinning); her limbs yield
  to your controller since 0.8.3. A hidden partner still touches nothing. Only SfMorph.dll changed (the shader bundle is the 0.8.4 one).

0.8.4:
- Wrinkles on her face didn't show in the headset although the Activation debug view was red there: the folds were written only into her
  skin's normal map, and her skin shader's subsurface scattering blurs fine normal-map detail out of the diffuse light (the face most of
  all), so only a faint trace in the highlights was left. Now each fold also darkens her skin colour at the bottom of its crease (Crease
  shade, Wrinkles section, default 0.3; [Wrinkles] Cavity): the crease is found from the curvature of the fold height, so the broad ridges
  stay her own colour and only the narrow valleys get darker, a little warmer than plain shadow. Colour detail is not blurred by the
  scattering, so the folds read on her face. Works on the neck and body sheets too.
- One copy of her skin colour per material: with Body wrinkles on, hand prints and slap redness (Skin marks) are now drawn into the
  wrinkles' copy instead of a second one of their own (log: "Skin marks: ... albedo via Wrinkles"), so the two never take the slot from
  each other. The Characters tab skin toggle still works: the slot is handed back to it for its next pass when a skin switches (log:
  "albedo slot handed back"), then the shade goes on over the character's skin.
- [Wrinkles] Resolution 1 and 2 made 64-pixel wrinkle textures (the setting's 1/2 was taken as a pixel size): now 2048 and 1024 as described.
- Install both dist/SfMorph.dll and dist/sfmorph_shaders (new albedo pass; with the old bundle the Crease shade row is greyed and the log
  says so).

0.8.3:
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
- Physics → Secondary motion → Strength (1-4x) multiplies the trailing and swings. It scales what is shown, never the springs
  themselves, so it can't make them unstable; the shown lag is capped at Strength x the part's lag limit (at most 50 deg).
- Physics hands: the drawn hand is now exactly your controller in free air; the physics offset fades in only on contact (VR → "Drawn
  hand is exactly the controller in free air"). A game camera cut of ~0.4 m made the hand race across the gap (log: jitter up to
  35 cm); smaller rig jumps are now carried along, and a hand that lost its target in free air snaps back. A hand held deep in her
  no longer tumbles (the pull eases off with the distance it is blocked).
- Fingers close like VaM's DynamicLimbs: each finger's curl sweep is tested against her body from the palm (it can't feed back on
  itself), the finger stops where it touches and sinks in by the part's softness (breast/butt deep, shoulder/forearm barely), idle
  fingers follow the touching ones, low finger friction so tips slide. The hand rests in a relaxed curl (VR → Rest curl).
- Pushing her forearm or calf moves her hand or foot now (the planted lock lets go while that part yields; an upper-arm or thigh push still
  keeps the hand put). With physics hands off, Embody's hand stop, clasp and elbow swivel test her yielded skin, not the animation's.
- Grab moved to the trigger (VR → Grab with: Trigger / Grip / Either): the grip is the game's menu button. It closes the whole hand,
  physics hands and his hands alike; the old index-only pointing pose is gone.
- The hands sank into her before stopping (her soft colliders were inset a flat 2-4.5 cm, deeper than a thigh's flesh, so the hand
  showed inside her): the inset is now each part's tissue depth (butt/breast ~2.3 cm, belly ~1.8, thigh ~1.4, calf/upper arm ~0.9,
  forearm/neck ~0.4, head 0.3, scaled by Softness), the same depth Embody's hand may sink before it stops.
- Embody: his fingers rest in a neutral pose (from his bind pose) instead of the animation's; the grab button (VR -> Grab with, the trigger by default)
  closes the whole hand around what you hold (her shoulder, her breast) and it stops on her skin, deeper on soft parts. The view now faces the way his
  chest faces on a snap (his head is often turned in the animation, which put his arms off to one side of yours; VR → Face the view
  to), with View turn / tilt / roll sliders for what is left (a change re-snaps at once; tilt/roll never add up). With physics hands off his hand
  now stops at her skin (it passed straight through), and his elbow swings around her instead of his forearm passing through her.
  His wrist offset was rotated by the physics offset (up to 3.5 cm off when blocked): fixed. His animated hand no longer pushes her
  while you embody him (it pushed with the animation's hand, not yours).
- With the soft zones off, her colliders were still inset for soft skin (2-4.5 cm), so the hands sank into her: the inset now follows
  the soft zones.
0.8.2:
- Reactions "didn't do anything" (0.7.9/0.8.0 logs: 200 touches, her voice played 0 times). Causes and fixes:
  - Her voice was dropped outside the game's sex/foreplay mode, where most of the testing happened: now she voices there too with the game's
    own moan clips on her voice source.
  - The game's "dirty talk playing" flag never clears after a line, and its feeling-loop flag is on for long stretches of sex mode: both
    blocked her voice. Now only dirty talk actually sounding holds back a caress or grope voice; slaps always get through (as the game's
    own touch moan does), cutting a running moan when hard.
  - All of the game's moan clips are padded to 4 s, which kept the moan source "playing" and forced a 4 s pause between reactions: the
    tail now counts as silence after 1.8 s.
  - A swing over several body parts reacted to its first (often weakest) contact: now to the strongest one.
  - The slap sound was the game's hand-job stroking clip, 3D with a short range, and quieter for borderline hits: now the game's skin
    impact (thrust) clips, louder, audible at arm's length, and one continuous strength from soft to too hard.
  - Her face: slaps show the game's eye reactions plus the wince or a blink; caresses and gropes now show in her eyes too; her blush is
    scaled to the material's range; the game's own expression level follows her excitement (feed the game's desire is now on).
  - States flipped between startled and the others on every hit and spanking never raised her excitement: calmer now.
- The game's sound-effect shuffle (sex/hand-job sounds) threw the same IndexOutOfRange / NullReference as its dirty talk: the voice guard
  repairs it too.
- Reactions tab: a "last:" line says what each touch did or why it stayed quiet, and "Diagnose sound and face" logs the whole audio
  chain (listeners, mixer levels, sources), plays a test tone two ways and reports which face layers react: if you hear nothing,
  press it and send the log.

0.8.1:
- A hand pressed on her (lower belly especially) made jagged, hand-shaped cut-outs with sharp steps and went too deep: the GPU contact moved
  only the vertices inside a hand's capsule, each to the capsule's surface along its own normal, with nothing outside the rim (a wall at
  the rim sampled at ~7 mm vertex spacing), left the shading of the undented skin (the dent read as a hole) and capped the depth at 2-4.5 cm
  on every soft vertex on top of what the soft zones already pressed. Now the press is a smooth field over her skin: the depth inside the
  hand, then a skirt that falls off over ~15 mm around it whatever the mesh resolution (Dent edge), smoothed, with the pressed flesh rising
  in a low rim around the dent (Rim bulge), and the skin's shading tilted into the dent (bent normals), so it reads as pressed flesh.
- Anatomical flesh: how far each part gives follows her anatomy (about 23 mm on the glutes, 24 on the breasts, 18 on the lower belly,
  10-16 on the thighs, 9 on the calf but 4 over the shin, 7-10 on the upper arm, 2-4 over bone at the default 50 % Softness; Dent depth
  scales it all; approximate on purpose, for look and feel). A press follows the hand exactly at first, then stiffens smoothly toward
  that depth instead of stopping at a hard edge;
  the soft zones' springs are per part (breasts softest and the most jiggly, glutes deep and soft, belly medium, outer thigh firm), and a
  physics hand meets resistance that rises as the flesh compresses (a fingertip sinks into a glute, stops quickly on a shin).
- Her own body squashes where it touches: thighs together, a calf against the thigh when kneeling, an arm against her breast and side
  (her upper arms and calves are contacts now; a limb never dents the part it belongs to). The partner's pelvis and thighs press her too,
  not only his hands (Physics -> "The partner's body presses her too").
- Bend bulges: her biceps thicken as the elbow bends, the back of the thigh and the calf as the knee bends, the thigh tops when the hip
  flexes, and a shallow fold forms in the lower belly when she sits (Bend bulges; mm per part in the config file).
- Marks: a hand pressed on her glutes, thighs, belly or breasts leaves a print (pale for a moment after the hand lifts, then reddish) that
  fades over ~15 s (Marks fade), and slaps (Reactions on) leave redness at the spot, also on the 0.7.9 per-zone slaps; through Wrinkles'
  normal map when Body wrinkles are on. Nothing is changed in her textures while no mark is showing.
- Ripple: an impact (a slap, the partner's pelvis thrusting against her glutes or thighs) sends a damped ring through her flesh from the
  point of impact, and a breast impact also kicks the game's breast physics briefly (Ripple on impact).
- Overlay meshes lying on her skin (areola caps, tattoo, the vulva rim) move exactly with the skin under them in a dent (they used to get
  their own dent: the areola could z-fight or a tattoo float). Her own limbs only cost GPU time while they actually touch her.
- Wrinkles looked like stretch marks: each fold was a thin crease line on flat skin. Now compressed skin rolls into broad rounded ridges
  between narrow creases. On her face the folds stay subtle: her skin shader's subsurface scattering blurs fine normal-map detail out of
  the diffuse light (they show mostly in highlights); 0.8.4 adds a darker crease in the skin colour.
- Character shapes with body-slider morphs: the soft zones now take her rest shape from every non-corrective blendshape (the character and
  the body sliders) and rebuild when one of them moves.
- Install both dist/SfMorph.dll and dist/sfmorph_shaders (the shader bundle has new kernels; with the old bundle the GPU contact stays the
  0.7.9 one and the log says so).

0.8.0:
- Characters → Body sliders, like the ispinox Claire VaM scene: Fitness, Body Type, Waist, Hip, Breasts and Breast Under on a
  converted character, each 0-100 % from that character's own setting; plus Skin Tone (lighter/darker through the tint) and Gloss.
  The sliders stay greyed until the character has slider data: export the VaM character again with "Export morphs for SF" (the
  updated tools/vam/SfExport.cs) and re-run tools/pipeline/run.ps1.
- Joint correctives (VaM's pJCM morphs): the shoulder, collar, elbow, hip and knee bends get the VaM character's corrective shapes
  as her joints swing, measured from VaM's rest pose. "Joint correctives" toggle under the sliders.
- Clothes follow the sliders and correctives like they follow the base shape.

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
