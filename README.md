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
- **Avatars** (Characters tab): a character's own mesh and skin (a DAZ Genesis 9 / 8 / 2 figure built into an avatar bundle by
  `tools/avatar/build.ps1`) drawn instead of her, fitted onto her skeleton at load: her animation, IK, breast physics, hair and the
  Body / Bones sliders move it like her own body. Skin shader, brightness and her hair in the panel.
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

0.11.43 (avatar: her face rig on its face):
- The avatar's face moved only with her jaw and eyes, and its mouth opened too far on lines (user): its whole head hung on her head
  and jaw bones. Now its face skin, lashes and brows take her face rig's weights (lips, mouth corners, cheeks, brows, lids, jaw) from
  the matching place on her face, so the game's expressions, face clips and lip sync move its lips, brows and lids as they move hers,
  and the jaw opens the same amount. The matching place comes from a warp of the avatar's face landmarks onto hers (lip corners, the
  middle of each lip, inner and outer brows, eyes, head), used only for the lookup: the avatar's face keeps its own shape. Its eyes,
  teeth and mouth stay on her eye / jaw / head bones. Checked offline on a talking recording: the avatar's mouth opens and closes with
  hers.
- Not specific to one character: the landmarks come from per-rig face bone tables for DAZ Genesis 9, Genesis 3 / 8 and Genesis 2
  (VaM's figure); a rig without face bones keeps the jaw / eyes / head weights. Face surfaces are recognised by the DAZ names of each
  generation (Head; Face, Lips, Ears). VaM characters still need an exporter that writes their skin weights (docs/SOURCES.md).

0.11.42 (avatar: her skin shader and her skin systems):
- The avatar looked like a ghost and none of her muscles, chubby, dents, marks or the game's subsurface scattering showed on it (user):
  those systems ran on her hidden meshes, and its skin was an HDRP/Lit copy. Now the avatar's skin is a copy of her own skin material
  (the game's skin shader and scattering) by default (new setting [Avatar] SkinShader; the old Material value is not carried over), and
  the avatar's renderers are her body for the plugin's skin systems while her replaced renderers are left out: GPU contact (dents,
  ripples, Delta Mush at the joints, chubby, bend bulges and folds), skin marks, soft zones and her body colliders for the physics
  hands now work on the avatar's body (they go by her bones, which it is skinned to). Her face, breast and genital helper bones stay
  known from her hidden meshes. The game's genital patch follows the avatar's body dents.
- Not yet on the avatar: wrinkles and muscle relief (painted on her UV layout; they need one authored for the Genesis 9 layout, next),
  blush.

0.11.41 (natural wrinkles: micro lines, no scar on her belly):
- Her stomach line still looked like a caesarean scar: the belly fold sat 2 cm below her navel and curved up to the sides, which is
  exactly where and how that scar runs. It is now where a sitting or curled-forward belly really folds: at the navel, drooping
  towards her flanks, its depth varying along it, with the skin rolling over it from above; a fainter, shorter fold 3-4 cm higher.
- Every wrinkle line, face and body, was one clean groove. Real folds drag finer lines with them where the skin bends most, so each
  fold now has micro lines: a thinner, fainter echo on each side over part of its length (leaving the fold and rejoining it) and
  short curved branches forking off towards its ends, all with gaps and varying depth so nothing reads as a drawn copy. Short lines
  across a fold were tried and dropped (they read as stitches). Face micro lines down to 0.45 mm, body 1.1 mm (the body texture's
  resolution).
- The body's wrinkle maps now add up overlapping lines like the face's (a fold and its micro lines together), instead of the
  strongest line winning per texel. Shader bundle rebuilt.

0.11.40 (Round joints fixed for real):
- Turning Round joints (Delta Mush) on still left dark red, inside-out patches on her groin, belly and upper thighs (user). The
  0.11.35 change to his body's marks was not the main cause. The joint pass was: at vertices with few neighbours, at mesh borders
  and in tight folds (her crotch, the vulva opening, the glute cleft), the smoothed neighbours can span a plane at right angles to
  her skin. The frame built from them then had its normal side-on to the skin, and which way it flipped was a coin toss between the
  bake and the GPU, so the skin detail came back mirrored with flipped normals. Her subdivided lower body has many such spots.
- The frame's normal is now her skinned vertex normal, which is always defined and survives the mirrored space; the two other axes
  come from the smoothed neighbours, and a vertex whose frame is close to degenerate fades out instead of flipping.
- Checked with the plugin's own bake code and the real compute shader in Unity on her exported lower body:
  - At rest, under a rotation and under a mirror: every vertex comes back exactly (it moved up to 3.2 mm with 4 flipped normals
    before).
  - A 90-degree knee bend: half of plain skinning's inverted triangles are gone (102 -> 55), the same with or without a mirror.
- Shader bundle rebuilt: install dist/sfmorph_shaders too.

0.11.39 (the pack answers every game voice line; lower lip and teeth; fewer repeats):
- The pack now replaces her game voice wherever it has a fitting clip, whether or not "mute" was ticked (the switch is gone),
  and the game's own voice plays wherever the pack has nothing (a pack without lines like Rebecca / Amy, story scenes, a
  scene whose lines the pack lacks). Before, only moans were answered: the game's dirty talk went silent under the mute.
  - Dirty talk (every 3-10 s in sex): `reikaNipple_DT_..._LvN` -> a nipple line at level N, `reikaPussy` / `pussySex*` ->
    pussy, `reikaAnal` / `analSex` -> anal, the rest (public, hand / titty / foot jobs, massages) -> public, `invite*` ->
    invite, `request*` -> request; the game's dirty talk isn't stopped (its muted line runs on), so it doesn't fire the next
    line at once.
  - Her sex voice: moans by her desire and the stroke speed (fast one level up, slow one down), breaths for close-mouth /
    exhale (calm when slow), muffled breaths for licking / sucking, calm breaths for kissing; feeling sounds by their level.
  - Timeline scenes: the tease intros (good / normal -> invite lines, bad "not in the mood" -> guard lines), finish good /
    normal -> afterglow, bad -> guard, alongside 0.11.20's climax / creampie / condom / clean. Story scenes keep the game's
    voice. The log says which (`voice: story scene ...`, `... the pack has no guard lines, the game's own voice plays`).
- Lower lip and teeth (still wrong in 0.11.34): the mouth frame the poses are built in took "up" from the upper minus the
  lower inner-lip bone, which on her closed mouth points mostly forward; "roll the lower lip in" pulled it down off her
  teeth and "shrug" pushed it forward. Up is now the model's up from the bind poses (checked against the way the jaw opens),
  the drool chain's anchor bones (LiquidChain_lip_*) are no longer moved as lips, and maps from 0.11.11-0.11.38 are set aside
  as `arkit-map-<version>.txt` and rebuilt (log `lip map built 30 shapes ... (up from the model's up)`).
- The same few lines over and over: each pack folder now plays as a shuffled deck (every clip once before any repeats, no
  clip twice in a row across rounds) instead of a random pick that only avoided the last 6.

0.11.38 (avatar: her own genitals):
- The avatar's own crotch did not work with the game (user, headset): a translucent, untextured piece that did not match her genital
  customisation or move with the game's sex animations. Now the game's own genital meshes stay on: her pussy patch (vulva and anus,
  every type the customisation swaps), pubic hair and the internal meshes, bound to her bones as always, so the swaps and the
  animations work. The avatar's crotch is cut out where the patch sits and its skin around it (within 3 cm, fading out to 8 cm) is
  laid onto her body surface, so the patch's rim and the pubic hair sit on it as on her (checked offline: from below the patch shows
  exactly as on her own body, framed by the avatar's thighs). The patch is tinted to the avatar's skin: its own _SkinColor scaled by
  the avatar's skin colour around it over hers (a per-renderer property block: her shared body material, the game's skin colour
  setting and the brightness slider still apply).
- Her renderers the avatar does not replace stay drawn: clothes, toys, props, internal meshes (before, all of her was hidden); only
  her skin, nipples, face, eyes, lashes, brows, mouth and tears (and hair when Keep her hair is off) are hidden.

0.11.37 (chubby glutes move like the breasts; a broad upper body in the Muscles section):
- Chubby glutes now also change her glute physics, the way breast tissue behaves (user). More glute fat makes the glute soft zones
  softer (slower, wider jiggle), lets them give further under a hand or his body, and lets them keep wobbling longer. The belly and
  thigh zones follow their own fat the same way; slimmer (Chubby below 0) is firmer. The fat itself already rides on those soft zones
  and dents deeper. The Glutes slider is in the Chubby section of the Skin tab.
- Broad upper body in the Skin tab's Muscles section (user: no way to make her upper torso look broader there). It is the Body tab's
  Athletic upper body (one value, saved with her look), now broader (shoulders 1.4 -> 1.8 cm out, ribcage x1.05). With the muscles
  on, it also keeps her lats, deltoids, traps and upper chest filled out at rest (up to 70 % of their tensed shape) for a V-taper
  without flexing.

0.11.36 (neck lines across her whole neck, not two mirrored strokes):
- Her neck creases showed as two short, mirrored lines slanting down from under her jaw (headset 17 Oct). The front of her neck on
  the body texture is shared by two meshes: her throat and the small piece under her chin. The maps were made on the under-chin
  piece there, where the lines don't reach, so each line was blank in the middle and only its two side ends showed.
- The body maps are now made on her throat, and the neck has three soft creases running across the whole front: uneven, slightly
  wavy, each at its own height, depth and reach to either side (not mirrored), the middle one strongest, the top one short and faint.
  Same driver as before: they fade in when she looks down or tucks her chin. Shader bundle rebuilt.

0.11.35 (chubby on whole limbs and the torso; the lower body's bends back; no red smears from his body):
- Her lower body had no bend bulges, knee or groin folds and no chubby since 0.11.31 (log: "no frame (no toes or breasts to tell
  her front)"). Her toes are on the foot mesh, so 0.11.31's front check found nothing there. It now uses the leg's front skin helper
  (in front of the hip) or the butt skin helper (behind it). That also explains why Chubby hardly changed her body: the belly's
  lower half, the glutes and the thighs are on the lower body.
- Chubby covers whole limbs and the whole torso (user: it only inflated the upper arm and barely showed on the body).
  - Arms: the upper arm and the forearm as one, tapering to the wrist, softest at the back of the upper arm.
  - Thighs: down to the knee, sharing it with the calves so there is no step there.
  - Calves: fade out before her foot mesh starts.
  - Torso: belly, waist and back are each a soft layer over their side of the torso with the deposits on it.
  - Area defaults are a little fuller (belly 14, waist 12, back 8, glutes 14, thighs 14, arms 10, calves 7 mm); your saved values
    stay.
- Dark red smears across her groin and upper thighs in sex scenes (user's screenshot): his pelvis and thighs press deep into her
  there and move fast while he thrusts, so they reddened and printed a band like a slapping hand. His body now stays under the
  redness threshold and shades / prints at half strength; hands and slaps are unchanged.
- Shader bundle rebuilt: install dist/sfmorph_shaders too.

0.11.34 (GPU resets, crashes and freezes since 17 Oct):
- Symptom (user, three runs on 17 Oct with 0.11.30 / 0.11.33): the NVIDIA driver reset the GPU (System log nvlddmkm 153) and the game
  froze or crashed ("d3d11: failed to create buffer ... 0x887A0005", device removed).
- Cause found in the logs: at every character, outfit or mesh change GPU contact's bake froze the game's main thread for 11-17 s
  (5-8 s per body mesh; `gpuSync` 11-15 s in the Perf line). Each freeze lined up with one cluster of driver resets (four stalls,
  four clusters in the 03:03 run; both crashes followed one). The bake had grown with the Delta Mush data (0.11.28, baked even with
  DeltaMush off), the skin folds and chubby areas (0.11.31) and Fidelity's subdivided meshes.
- Fix: the bake's arithmetic runs on a worker thread; the main thread only reads the mesh and her bones (a few hundred ms). Until a
  renderer's bake is done it keeps the v1 imprint, then the seams and overlay maps are rebuilt. The Delta Mush data is baked only
  while [Physics] DeltaMush is on (switching it on bakes the body again). The bake line now gives its stages' times.
- Guards: the skinned buffer's count and stride are checked every frame (not only once): a buffer Unity recreated with another size
  is skipped and the renderer set up again. Before a chain is dispatched every buffer it indexes is checked against the vertex count
  (a mismatch skips it, logged once per reason); the shader checks every index it reads (neighbours, canonical ids, overlay maps,
  Delta Mush lists) before using it to address another buffer. Shader bundle rebuilt.
- Not run in the game yet.

0.11.33 (Debug tab tests for the new shape layers):
- Fold test: the inner elbows, the backs of the knees and the groin in turn at full depth for 2.5 s each, whatever her pose.
- Chubby test: each area alone at full (at least 10 mm) for 2 s, then all of them slimmer (-100 %), then all fuller (+100 %).
- Round joints A/B: Delta Mush on and off every second for 10 s, to compare a bent joint directly.
- Like the Flex test, they run over whatever she is doing, put your settings back when they end and log one line per step.

0.11.32 (avatar replacement on her skeleton):
- 0.11.29 drove the avatar's own skeleton from her pose (user, first headset run): the pose matched, but the body moved off her
  curves, the feet floated, drifted and one dipped through the floor, there was no breast jiggle and her hair did not stay on its
  head. Its own skeleton was the cause: its limb lengths and joints are not hers, and the game's physics, her hair and every system
  of the plugin hang on her skeleton.
- Now the avatar's mesh is rebuilt once at load onto her skeleton and the game skins it with her bones, like her own meshes. Posed
  into her bind pose, then warped smoothly so its shoulders, elbows, wrists, finger joints, hips, knees, ankles, toes, head and
  eyes sit on hers and its soles at her soles' height; torso, breasts, glutes, arms and legs take her own skin weights (blended from
  her nearest skin of the same body part: her breast chain the game's breast physics moves, her twist bones, her skin helpers);
  head, hands and feet go by the bone map, blended by the avatar's own weight shares at the neck, wrists and ankles. No per-frame
  work. Checked offline on two recordings of her (tools/avatar/drive_test.ps1): the avatar's skin within 3 mm of hers (median), soles
  within 3-7 mm of hers, skin stretch in the range of her own mesh.
- Size and hand / foot IK are gone from the panel (the avatar has her proportions and joints now). The HDRP/Lit material is never
  copied from one of the avatar's own materials again; the brightness slider only changes the colour.
- Still open: her soft zones, dents, marks, wrinkles and muscles act on her hidden meshes; the face moves only with her jaw and
  eyes; the cornea / tear overlays are left out; DAZ's joint correctives are not exported.

0.11.31 (skin folds, chubby, build triangle; the knee and waist bends work):
- Skin folds at deep bends: where a joint bends far, her skin now folds in as geometry. A soft crease runs across the joint with the
  flesh rolling up on both sides of it: the inner elbow (from ~60 deg, full at 140), the back of the knee (kneeling, a deep squat) and
  the groin (sitting, legs drawn up; the line from the front of the hip down to the pubis, where the wrinkle maps draw it). The
  shading follows the fold, and the wrinkle maps' fine lines sit on top. Physics tab: Skin folds (%) and the depth of each fold (mm).
- Chubby (Skin tab), the soft-fat counterpart of the muscle shape: fat where she would put it on, as geometry.
  - Belly: the pooch under the navel and a softer upper belly. Waist: love handles over the hip bones. Back: the bra-line bulge and the
    lower back. Glutes. Thighs: saddlebags and inner thighs. Arms: the backs of the upper arms. Calves. Face: fuller cheeks, a double
    chin, a softer neck.
  - One Chubby slider (-100..200 %) scales the areas, each with its own mm.
  - Fat hides the muscle bulge under it, dents deeper under a hand, ripples more, and makes the belly and waist rolls and the rolls
    beside the new folds fuller, so the soft creases read as soft, not stiff.
  - Below 0 she is slimmer in the same areas and the muscle shape shows more. Her clothes don't follow the fat.
- Build triangle (Skin tab, top): Skinny / Strength / Fat like a game's build screen, as a shortcut. Picking a point sets Chubby (Fat
  above 0, Skinny below 0) and the muscle tone, Flex and relief depth (Strength; the strength side turns the muscle relief on). Every
  slider stays free afterwards; the centre is her default look.
- Fixed: the knee bends never worked (log: "Bend: 4 of 7 joints bound; missing knee L/R ... no mesh has all three bones": her hip
  and knee are on the lower body, her ankle on the foot mesh). The joint is now measured across two meshes that share a bind pose
  (checked by a bone they both carry), so the thigh-back and calf bulges and the knee fold work.
- Fixed: the waist roll in a side bend never worked (trunk: "no hips in its mesh"); her left comes from the shoulders there now.
- Fixed: her upper body (biceps bulge, the waist roll's upper half, the shin / biceps tissue floors) had no frame ("no hips in this
  mesh"); it takes her left from the shoulders and her front from the breasts.
- Shader bundle rebuilt: install dist/sfmorph_shaders too (an old bundle: no folds or chubby, the rows say why).

0.11.30 (round joints, fixed):
- 0.11.28's Delta Mush tore her hips, groin and knees into purple, inside-out patches even standing still (user's screenshot). Her
  skinned body is mirrored relative to her mesh. The pass rebuilt each vertex's detail in a frame made with cross products, and a
  mirror flips a cross product, so the detail came back inside out with flipped normals. Her skin's subsurface scattering then shows
  through red-purple. The frame no longer has a handedness: it is oriented by the skinned normal and the second neighbour edge.
  Checked offline on her exported lower body: exact at rest, under rotations, mirrors, and when applied again on its own output.
- 0.11.28 only ran on her lower body. The upper body (shoulders, elbows) was skipped because its mesh has no hip bones; it is
  included now.
- Her lower body's bake took 5-9 s with 0.11.28 (a stall on every rebuild). The rest smoothing now runs on flat arrays.

0.11.29 (avatar replacement, first build):
- A third character mode (docs/SOURCES.md, 'A third mode: avatar replacement'). Instead of reshaping her mesh to a character
  (the full conversion folded under the breasts) or baking its skin onto her UVs (her blush and makeup layers drew over it), the
  character's own mesh, skeleton and textures are drawn in her place and driven from her bones every frame, as VRChat drives an
  avatar from the player.
- Characters tab, Avatars: pick an avatar bundle from `BepInEx\config\SfMorphs\avatars\<Name>` (built from the character's FBX by
  `tools/avatar/build.ps1`; nothing of the character is in the repository). Her renderers are hidden (forceRenderingOff; the
  game's own outfit swaps keep working under it); None gives everything back. Settings: Size (her leg length, her height, its
  own), Skin shader (the game's HDRP/Lit with subsurface scattering and her skin's diffusion profile, or a copy of her own skin
  shader with her makeup and blush layers off), Skin brightness, Keep her hair, Hands / Feet onto hers (two-bone IK, 0 = its own
  limb lengths).
- The retarget: each of her bones' change since her bind pose goes onto the avatar's matching bone, from a rest pose turned so
  every limb and finger segment points where hers does; the trunk, collars, eyes and jaw take only her change (their directions
  are rig conventions); the avatar's extra spine and neck bones share her bends; its twist bones share the upper-arm, thigh and
  forearm twist; it runs right before the game skins the meshes, after her animation, FinalIK, DynamicBone and our own motion.
  Checked offline with this code on a recording of her (Unity editor, `tools/avatar/drive_test.ps1`): every limb and finger
  segment within 0.03 degrees of hers.
- v1 limits: the cornea and tear overlays are left out (see-through surfaces, next step), lashes and brows are alpha-cut; her soft
  zones, dents, marks, wrinkles and muscles still run on her hidden body; your physics hands still collide with her body shape,
  which differs from the avatar's by its proportions; no face expressions beyond her jaw and eyes yet; DAZ's joint correctives are
  not exported, so deep bends fold like plain skinning (wrist, elbow point).

0.11.28 (round joints in deep bends: Delta Mush):
- Her elbows, knees, hips and shoulders pinched and flattened in deep bends (user). The game skins her with plain linear skinning:
  a vertex near a joint follows the average of the two bones' matrices, so in a deep bend the skin collapses toward the bone (the
  inner elbow and the back of the knee fold flat, the hip and groin lose their volume, the armpit caves in). Now a Delta Mush pass
  runs on her skinned body first in the GPU contact chain, every frame she is visible. The skin around each joint is smoothed,
  which removes the collapse and the detail with it. Then the rest pose's detail is put back in the frame of the smoothed surface,
  so it turns with the bend. In her rest pose it changes nothing.
- Only the joints are touched: a weight around each shoulder, elbow, hip and knee (about 6-11 cm). Hands, feet, head, neck,
  breasts and genitals are never mushed. The weight fades to zero toward the edges of each mesh, so the seams between her upper
  and lower body stay closed. Overlays lying on her skin (areolas, tattoo, the vulva rim) follow the body under them.
- The rest shape is her current one (character and body-slider shapes, never the pose correctives). The dents, bend bulges,
  ripples and muscle geometry are built on top of the corrected joint.
- It shapes the bend; it does not add creases or folds. Those still come from the wrinkle maps (crease shading) and the bend
  bulges / muscles (geometry).
- Physics tab under the contact rows: "Round joints in deep bends" (on by default) and "joint smoothing" (strength). Config
  [Physics] DeltaMush, DeltaMushAmount, DeltaMushRadius (mm; applies when her body is rebuilt). Shader bundle rebuilt: install
  dist/sfmorph_shaders too. With the old bundle it stays off and the panel says why.

0.11.27 (her face wrinkles re-sculpted):
- The face's wrinkle maps were thin drawn lines (0.4 mm deep, 1 mm wide, the single strongest line per texel): next to sculpted
  expression maps (the user's MetaHuman-style references) they read as scratches, and her skin's subsurface scattering blurs such
  thin detail away, so in the headset they were barely there. Re-sculpted as expression folds: broad soft furrows with rolls of
  bunched skin between them, lines that vary in depth and width and fade in and out, and the soft masses an expression pushes up
  (the brow bunch in a frown, the lower lid's roll in a smile, the cheek rolling over the smile fold, the chin's dimpling). The
  author now sums every fold and mass of a region per texel instead of keeping one line.
- Kept feminine (user): the references are male / older faces, so she gets fewer, shallower, wider lines - three soft forehead
  lines, two short frown lines, four soft crow's feet, one soft crease under the lid roll, a soft smile fold under round cheeks,
  barely-there chin dimples.
- Regions by expression: brows up (forehead), frown (glabella), squint (crow's feet, under the eyes), smile (+ nasolabial and cheek),
  nose scrunch (bunny lines, now per side), and new frown lips (chin, mouth corners): 14 face regions. The Debug tab's Wrinkle test
  follows (brows, frown, squint, smile, nose, frown lips, whole face, neck, body).
- `tools/wrinkles/author.py --preview` renders her face properly (the mesh rasterised with a depth test, the relief looked up per
  pixel): `wrinkle_face_expressions.png` and close-ups `wrinkle_face_closeups.png` (neutral | expression). `--only=face` for
  iterating. Body creases unchanged. Shader bundle rebuilt.

0.11.26 (controller haptics):
- The controllers now vibrate with what your hands touch on her. This is new and **off by default**: [Haptics] On, or VR tab → Haptics.
  It uses the contacts SF Body Morphs already measures: your hand models, the physics hands' fingers at the controller pose and the
  embodied partner's hands, each capsule tagged by finger (thumb, index, rest of the hand). The partner's animated hands play nothing.
  - A hand landing on her gives a short tick. From 0.45 m/s it is a pat pulse, and from 1.2 m/s a sharp slap pulse that grows to full at 3 m/s.
  - A resting hand gives a faint hold by how deep it presses, and sliding gives a low buzz by speed (full at 40 cm/s, the rub sound's curve).
  - The grab button closing on her gives a squeeze pulse, then a light hold while it stays closed.
  - A finger in her vagina, anus or mouth gives a buzz by depth and pulses going in and out and on each stroke (12 mm turns, at most 5 a second).
- Quest Pro actuators: the grip motor takes grabs, slaps and the palm, the trigger actuator your index fingertip and fingers in her,
  the thumb actuator your thumb.
  - The separate trigger and thumb actuators need Meta's runtime (Link / Air Link). There, the OpenXR extension
    XR_FB_touch_controller_pro is on and the game's Quest Pro controller profile binds them.
  - Virtual Desktop's OpenXR runtime (VDXR) has no Touch Pro profile and drops trigger / thumb outputs. The game's own log shows
    "XR_FB_touch_controller_pro not supported by runtime". Over VD, all three are mixed onto the main motor (the largest wins).
- The path is found at startup and logged once ("Haptics: runtime ... -> ..."):
  1. OpenXR Input System actions on the actuators (localized);
  2. else one OpenXR haptic action per controller;
  3. else `XR.InputDevice.SendHapticImpulse` channel 0 (set this one with [Haptics] Path 1).
  Everything is reached by reflection.
- Rate-limited: a buzz is re-sent at most 30 times a second as a 0.1 s impulse (so it dies on its own) and stopped when it falls to
  0. Onsets are at least 0.12 s apart per actuator.
- Sliders: Strength, Touch and rub, Pats and slaps, Grab, Penetration. Debug tab tests: per hand grip / trigger / thumb / slap, a rub
  buzz and strokes on the right trigger. [Haptics] Log 2 logs every pulse and buzz on / off.

0.11.25 (a penetration sound, and penetration excites her):
- A finger going into her vagina, anus or mouth had no sound of its own: the grab and the rub played (the user: the recorded squishes
  are too wet for a grab but suit this). New skin-sound category **penetration** (`Sounds\penetration\*.wav`, your files only; an empty
  folder is silent): it plays at the opening when a finger goes in (6 mm past the opening), comes out, and on each in / out stroke
  while it is in (the depth turning by 12 mm), louder with speed, at most 5 a second per opening, the way out a little softer.
- The detector (`Penetration.cs`) is fed from the same capsules as her body's push: your hand models, the physics hands' fingers at
  the controller pose (the PhysX fingers stop at her skin, the ghost goes on in), the embodied partner's hands, and partner hands
  when they push her. Each opening is a short tunnel from her bones: `Left/Right/Upper/Lower_Vaginal` toward the `*_inside` bones, the
  `Rectum_*` ring toward her pelvis root, the `Lip_*` bones toward her tongue. Only finger / thumb capsules (13 mm or thinner) count,
  so a palm pressed on her doesn't. While a finger is in, the grab button and the rub stay quiet there.
- **Penetration excites her** (on by default): going in and each in-stroke is a reaction like a grope on her hips (her face for the
  mouth): excitement (in: +0.03 vagina, +0.02 anus, +0.008 mouth; a stroke a third of that, more when faster), voice, the pussy /
  anal lines, face, the game's desire; a fast entry into her anus adds a little pain. A finger in her mouth speaks no lines.
- Reactions panel: Penetration sound toggle, penetration volume (times the skin volume), Penetration excites her; Debug tab: Test
  penetration sound, Test penetration stroke. Config `[Reactions] PenetrationSound`, `PenetrationVolume`, `PenetrationReacts`. Log:
  `Penetration: openings vagina 4 bones (+4 inside), anus N bones, mouth 8 bones (+1 inside)`, then a `Reactions: penetration:
  vagina in / in-stroke / out-stroke / out by ...` line per event (Log 1: entries, exits and the first 20 strokes of each entry).

0.11.24 (a more prominent upper body; the waist folds in a side bend; a wrinkle test):
- Body tab → Torso → **Athletic upper body** (0–1.25): one slider over the bone sliders for broader shoulders and thicker arms and
  neck. At 1: shoulders +1.4 cm a side, upper arms ×1.14, forearms ×1.08, neck ×1.14, ribcage width ×1.03. A limb's size was a
  uniform scale, so it also grew longer and moved the elbow / wrist / head; the build scales the length back by the inverse, so the
  limb gets thicker, not longer. It adds to the sliders' own values (a driven slider shows "with the build sliders: ...").
- Muscles (re-authored maps): fuller rounder deltoid caps (the side head the fullest, so the shoulders read broad from the front),
  slightly larger biceps / triceps, a fuller trap slope from the neck to the shoulder and a broad soft trap mass up the back of the
  neck (a first try as two columns read as rods, so it is one low mass), and new neck muscles per side (sternocleidomastoid, from
  behind the ear to the top of the sternum): they stand out as she turns her head away from that side, tilts it toward it, tips
  it forward or lifts it in a crunch. Her neck took no muscle geometry before (the bake skipped every neck vertex); it does now.
  The muscle layout is now full (32 of 32 regions).
- Side bend: her waist bunches into a soft roll above the hip with a crease under it on the side she bends toward (the user's
  reference photo), as part of the bend bulges: [Physics] BendWaistRollMm (10 mm at a full bend of ~28°, before Bend bulges; Physics
  tab "waist roll in a side bend"). Shader bundle rebuilt (the bend slots grew from 9 to 11).
- Debug tab → **Wrinkle test**: brows up, frown, smile / squint, nose scrunch, the whole face, the neck, then the body creases at
  full, 2.5 s each, whatever her face does. If the lines are faint in the test, the maps / Strength / Cavity are the issue; if they
  are clear in the test but faint in play, her expressions don't compress the skin enough (Threshold). `tools/wrinkles/author.py
  --preview` now also renders her face with each of those expressions at full (`tools/wrinkles/preview/wrinkle_face_expressions.png`).

0.11.23 (recorded skin sounds play as sets; the rub as a run of strokes):
- The rub played one clip on a loop, which only suits a long recorded bed or the built noise. Recorded rubs are short single
  strokes (0.3-1 s), so a rub made of them now plays as a shuffled run of one-shots that overlap a little, each at the current slide
  level and a slightly different pitch, like a game's footsteps (a first clip of 1.5 s or more still loops). Every category already
  picks from all its files without repeats, so a folder of similar clips gives the variation.
- On the user's PC (not in the repo, personal use): `Sounds\rub` = 9 skin-rub strokes, `Sounds\grab` = 6 hand-on-skin presses,
  `Sounds\pat` = 7 soft skin taps (the earlier pat files moved to `Sounds\_previous\pat`), all from VaM scene packages, rolled off
  above 3 kHz and levelled to -30 dBFS RMS (peaks -17 to -21 dBFS: the audition copies were too loud).

0.11.22 (grab sound on the grab button; a softer rub):
- The grab sound "didn't work": it only played at the onset of a classified grope (a press of 20 N or 1.5 cm held for 0.25 s), so
  closing your hand on her was silent. Now the grab button closing while that physics hand touches her plays it at the contact
  point (log `Reactions: grab sound: your left hand closed on ...`); the grope onset keeps its own, at most one per 0.5 s.
- The built rub sound (used while `Sounds
ub` is empty) still sounded like clothing: fabric is the bright rustle above 2.5 kHz.
  It is now a duller, softer skin band (150-850 Hz), the airy sheen at a sixteenth and below 4 kHz, with slower, deeper swells.
  Recorded skin sounds from the user's VaM folder are being surveyed; nothing is installed until they are picked.

0.11.21 (fixes from a headset run: breast physics, fingers, smooth marks, muscles, pelvis):
- The default Physics settings deformed her breasts oddly (a fold where her arm pressed one). Two causes. (1) The only soft-zone default
  that changes her shape is Quality: Balanced keeps the unsmoothed body mesh, Fidelity (the setup that looked right) subdivides and
  smooths it. Quality now defaults to Fidelity and Springiness to 0.45 (existing configs keep their values). (2) Each skin vertex hung
  on its 3 nearest soft points, and the third one's weight jumped from up to ~0.3 to nothing where the fourth took its place: with one
  point pushed 2-3 cm (her arm on her breast) the skin folded along that line. The weights are now continuous (the fourth's subtracted).
- Physics-hand fingers could pass straight through her. The finger joints used PhysX projection, which snaps a stretched joint back
  onto its anchor and ignores collisions: a finger pressed against her was pushed toward the palm by the contact and teleported
  forward into her on every step while the palm kept coming. Projection is off, and the joint sees the palm as at most 3x a finger's
  mass (the 12:1 ratio let it stretch), so the contact stops the finger and the palm behind it.
- The pressed-skin darkening and the print a hand leaves looked polygonal (a footprint in snow) and the print lasted too long with no
  setting. The marks are per-vertex values spread linearly over each triangle, so they followed her triangles. They are now blurred
  on her skin before anything uses them ([Physics] MarksSmooth, 6 mm). New: MarksPrint (how deep the print shows, 60 %) and
  MarksPrintTime (0.8 s to fade to a third; it was a fixed 1.5 s at full depth). The Skin tab also shows the press shade (MarksAo),
  which MarksBoost multiplies.
- At max settings the muscles looked like tumours, and raising the abs also inflated the obliques. The bulge was normalised by each
  area's highest belly, and the core's was the 6.8 mm oblique pad, so the abs reached only 40 % of Core mm. The obliques are now a group
  of their own with [Muscles] Obliques (60 % of Core, relief and bulge); a side bend still lets the oblique on that side bunch up at
  full strength. mm x Flex eases toward 10 mm, and the relief's slope eases toward ~0.9 so Strength 2 deepens definition instead of
  drawing domes. With her pregnancy setting on (her pregnant body meshes) the abs fade out, and the obliques from level 2.
- Dark seams around the pelvis. The adonis groove (2.8 mm) saturated the groove shade into a dark band down to the pubis, and every
  muscle's relief stopped hard at the separate genital / nipple meshes (the game's selectable variants, which carry no relief). The
  relief now fades out over 3.5 cm toward those meshes (all genital variants share one footprint), and the groove is 1.9 mm deep and
  shades at half strength. Maps re-authored, shader bundle rebuilt.

0.11.20 (the pack's voice in the game's climax scenes):
- With a voice pack and Mute game voice on, the unskippable climax scenes were silent. They (and the creampie, condom, clean and
  finish scenes) are Timelines: one pre-mixed 10-33 s clip of her lines and moans on a track bound to her voice source, which the
  mute silences; the stand-ins never answered it because a Timeline sets no clip on the source. Now, while such a scene's director
  plays (`Camera_system.playableDirector`), the pack fills it: climax = request lines and climax sounds in turn, creampie = request
  lines and high moans, condom = invite lines, clean / finish = afterglow lines and breaths, each 0.3-0.8 s after the last, with
  lip sync. `[Voice] PackInScenes` (on) switches it off.

0.11.19 (a natural back; stronger obliques):
- The back's erectors read as two lone tendons down the spine and the shoulder-blade muscles as rows of rods / tubes. Cause: a
  belly's cross-section (1 - x^k)^2 with k under 2 has no flat top - it peaks sharply on its centreline - and several muscles used
  k 1.4-1.8 to look "softer"; they became ridges. k is now at least 2 everywhere (softness comes from width). The lower back is two
  broad, low masses that fade up the thorax into a soft spinal valley (no column-edge groove); the upper back carries the shape:
  traps, the infraspinatus as a fan across the shoulder blade, teres major rolling up to the armpit, rhomboids running from the
  spine down and out, all along their fibres; the lower traps' pointed ridges are gone. Modelled on a lean athletic back.
- The external obliques are more prominent (a 6.8 mm pad over a 2.8 mm adonis belt). Shader bundle rebuilt.

0.11.18 (adonis belt, fuller waist, a lean athletic back):
- No adonis belt, and the lower abdomen narrowed into a V. The inguinal line is now a deep crease from each hip bone down to the
  pubis with the external oblique as a full pad rolling over it, so her waist bulges at the sides instead of tapering; the rectus
  stays nearly as wide down to the pubis (the semilunaris lines run almost parallel).
- The back, modelled on a lean athletic back under tension: a deeper spinal groove up to the neck, erector columns that stay
  strong through the thorax with their outer edges marked, rhomboids and lower traps (the diamond between the shoulder blades),
  teres minor and the creases that separate infraspinatus / teres / lat, fuller lats. Pulling the arms back (hands clasped behind
  her) now tenses the traps and rhomboids. `author.py --preview` writes `muscle_body_geo_back.png` too. Shader bundle rebuilt.

0.11.17 (irregular abs; a second subdivision level on her lower body):
- The abs were a symmetrical grid of equal blocks. Real abdominals are uneven: the two sides' tendinous intersections sit at
  different heights (here her right ~6-8 mm higher), each one curves and slopes its own way, the linea alba wanders a little, the
  blocks differ in size and height (the middle pair fullest, the top pair narrower and tilted up towards the ribs), and each block
  is a domed pillow meeting the linea alba, not a brick beside a flat strip. The lower abdomen stays one smooth mass with a faint
  fourth line on one side. `author.py --preview` also writes `muscle_body_geo_abs.png` (the abdomen up close, two lights).
- Dents on her lower body were still polygonal: her lower-body mesh (hips, glutes, thighs) has ~14 mm between vertices, 7 mm after
  Fidelity's one subdivision, while a hand's dent spans a few cm. Fidelity now splits a mesh twice when its mean edge is over 10 mm
  (her lower body: ~3.5 mm, about 70 000 vertices; the upper body, ~5 mm, keeps one level). Log: `Soft zones: Fidelity subdivided
  reika_lowerBody_normal ...: 2 levels (mean edge 14.x mm), verts 4533->...`. The GPU dent smoothing is sized in mm, so it follows.

0.11.16 (a naturalistic muscle sculpt; pecs and traps):
- The sculpt still read as cartoonish: every muscle was a few Gaussian dots and the definition was drawn lines, so a flexed
  abdomen became a chocolate bar of small sharp bumps, the back two thin rods beside the spine, and there were no pecs or traps.
  `tools/muscles/author.py` now builds muscles as masses along their fibres: a spindle along a curved centreline with a rounded
  plateau across it, its ends easing off, and overlapping bellies merged by a soft maximum, so neighbouring muscles meet in soft
  valleys; drawn grooves are kept only where anatomy has a real line (linea alba, tendinous intersections, spine, inguinal line,
  glute fold, the calf's lower edge), shallower and wider. Re-authored against an athletic female sculpt: soft abdominal blocks
  (the upper pairs smaller, the lower abdomen one smooth mass), an oblique pad over the hip, serratus fingers; broad erector
  columns either side of a deep lower-back groove, upper / middle traps, infraspinatus and teres on the shoulder blade, the lats as
  one sheet tapering to the waist; gluteus medius / maximus; rectus femoris, the vastus lateralis sweep and the inner-knee teardrop,
  split hamstrings, calf heads (the inner larger and lower) with a heart-shaped lower edge, soleus and tibialis; three-head deltoids,
  biceps, brachialis, triceps horseshoe and forearm masses. New muscles: pecs (Arms area: arm forward / across, weight on the hands)
  and traps (Back area: arms raised, propping her up, a held trunk).
- Back muscles were placed by angle around an axis that runs 2-4 cm under the skin of her lower back, where a degree is a
  millimetre (the rods); they now use their own frame through the middle of her trunk. Muscles near a joint reach across the limb
  boundary (calves to the back of the knee, hamstrings, traps to the neck and shoulders, deltoids and pecs to the trunk, the
  oblique pad to the hips), which removed straight cut-off edges there.
- `author.py --preview` writes clay renders of the geometry (`muscle_body_geo.png`, `muscle_body_geo_close.png`: torso front, back,
  legs from behind / side / front), used to tune this. Shader bundle rebuilt.

0.11.15 (Gloss works):
- The Characters tab's Gloss slider made no visible change (seen on a face-only character). It multiplied
  `_SmoothnessPower`, but her skin shader (read from its compiled GBuffer pass) computes smoothness as
  `_SmoothnessMap.r x _SmoothnessPower + _AddMap.r x _AddPower`, and her face's map is nearly black (mean 0.08), so x2 only
  took the face from 0.06 to 0.12 smoothness; a face-only character's body has no converted texture, so Gloss never reached it.
- Gloss now covers all her skin (face, body, feet, every material on her skin shader, converted or not): 0..1 scales both
  terms (0 fully matte, 1 the game's own), 1..2 keeps the game's map and adds an even lift (a white `_AddMap` x
  `_AddPower`) sized from each map's measured mean, so face and body both reach the same wet shine (mean smoothness 0.85)
  at 2. Everything comes back when the character is turned off; a value the game's customise menu writes is kept as the
  new original. The log gets one `Gloss <value>:` line per setting with the values written per material.

0.11.14 (face-only characters: the bad body combination can no longer be picked):
- A DAZ character converted for her showed a wrong body at Body 100 % and Krista's whole-body skin with the game's blush
  layers glaring on it when the body skin was on: the body conversion folds a body of quite another shape (Genesis 9) and
  is parked. Characters from other sources than VaM (DAZ, any -Source file) are now converted face-only: character.txt
  says `faceonly`, picking one sets Face only, the Body slider, the Body skin toggle and Full character are greyed out
  and held at 0 % / off whatever a preset says, and the bake skips the body and foot skin. `run.ps1 -Daz` makes this
  conversion by default (-Sliders: the slider-fit preset instead; -FullBody: the body too).

Pipeline, after 0.11.10 (skin bake fixes found on the Genesis 9 character's face; the plugin is unchanged):
- The adapter writes VaM's triangle winding. A file that came in the other way round (Krista's FBX) made the bake rebuild
  inverted source normals, so most of the normal detail was thrown away as "tilted" (face 96 %, body 64 %; now 1 %, 3 %).
- Faces get a second, tight bake pass (4 mm cage over the 12 mm one; the tight hit wins, its misses keep the wide one): the
  wide rays from her nose wings, eyelid rims, lip corners and ears landed on the character's nostril, lid and mouth insides
  (dark nostrils, dark eye rims, lip colour past the mouth corners). A VaM character's fuller lips still come from the wide pass.
- Her neck mesh (a cap inside the neck whose UVs lie on the throat) is baked before the upper body, so its rays' misses no
  longer paint a flat disc on her throat.
- Eyelids, when the character's eyes are open: her lid edges are landmarked onto the character's as seen from the front (both
  corners, upper and lower edge at 1/4, 1/2, 3/4 of the width) and her lids' inner edge onto the fitted eyeball, so the opening
  takes the character's shape (Krista: 8.8 mm tall like hers 8.6, was 10.4 and round). The lids keep the character's own skin
  (lash line, crease) instead of the flat skin average, and skin round the eye that faces the eyeball (a Genesis 9 face's eye
  socket, a flat never-seen texture island) is left out of the bake source: it was what her lid rays found (a pale shelf). Face
  texels no ray reaches take the nearest baked colour instead of the flat skin tone.
- Ears: the ear landmark is now the centroid of the whole ear (the head skin beyond 82 % of the half-width at ear height, the
  same rule on both heads; the adapter's ear labels are per side too). The outermost point at eye height it replaces fell on
  different parts of an asymmetric ear's rim (Krista: 11 mm apart), pulled one of her ears 16 mm down and crumpled it.
- Face variants whose material is not named after the mesh (reika_face_e: reika_faceE_mat) were skipped by the bake (no skin
  for that face); each face is now baked under its own material.
- Slider fit: limb girths are the band's outline (sqrt(area / pi)), not the median vertex distance, which read her calf 40 mm
  against an outline of 50 mm and gave every character 16-23 % bigger calves and, divided out, shorter shins (Krista's at the
  0.85 clamp). The neck girth is the narrowest neck outline between the eyes and the nipples, not a band between each rig's
  own neck and head joints (other heights per rig: Krista's neck came out 15 % thicker instead of 4 % thinner).

0.11.13 (sculpted muscles as geometry):
- The tensed-muscle shape (0.11.8) was one smooth pillow per muscle: a weight that rose from each muscle's edge to 1 over a few
  cm, so a fully flexed abdomen was one dome, not a six-pack, and nothing approached an athletic sculpt. `tools/muscles/author.py`
  already evaluated the sculpt (abdominal bellies and the tendinous lines between them, linea alba, serratus fingers, obliques, lats,
  erectors, glutes, the quad teardrop, hamstrings, calf heads, deltoid / biceps / triceps / forearm bellies) but only wrote its slope
  for the normal map. It now also writes the height itself (`muscle_body_height.png`, 16 bits over R / G, +-16 mm), and the GPU
  contact bake uses it per vertex: each vertex's shape = its sculpted height / the area's highest belly, times the activation, so
  the bellies rise and the grooves between them sink as she tenses. The area sliders (Skin tab: Core / Back / Arms / Legs) are the
  area's highest belly in mm, now up to 20 mm (15-20 mm with Flex 1-2 is a bodybuilder). The normal-map relief stays as the fine
  detail on top. A bundle without the height map falls back to the 0.11.8 pillow. Checked offline on her export (her body displaced
  at 8 mm and lit: separate abs, serratus, lats, scapulae, quads and knees); not seen in the game yet. Shader bundle rebuilt.

0.11.12 (fixes from the 0.11.11 headset run; a Debug tab):
- A seam / cut-off around her calves with the leg muscles up. Her lower calves and feet are their own mesh (`reika_foot`: a
  body-skin part and the foot material) and the GPU contact chain never took it in, so the calf bulge (and any dent) ended at the
  lower body's border about 12 cm above the ankle, at full height on one side and flat on the other. It now joins the chain as a
  body skin mesh, stitched to the lower body like the neck and face, with its own muscle bake. Also: a vertex sitting on a UV seam
  often read only the authored map's dilated rim (coverage 0) and was dropped as "no muscle", which drew a groove down every UV
  seam of the bulge; every welded copy's UV is now tried, the rim counts, and a lone vertex without a muscle inside one takes its
  neighbours'.
- Dents looked faceted and blotchy (her hand on her glute). Since 0.8.9 the dent field keeps the raw capsule depth as its floor
  (so the skin never stays inside a finger), and that floor steps from 0 to full within one edge; the bent normals came from that
  field, and on her lower body (7 mm edges even after Fidelity) the shading drew the triangles. The normals now come from a
  smoothed copy of the final field (positions keep the exact one), about 8 mm wide (Physics tab, **shading smoothness**,
  `[Physics] ContactNormalSmooth`, 0 = the old look). The muscle bulge and the ripples go through the same field and get the same
  smoothing. Shader bundle rebuilt.
- His strokes made no ripple. The log had 30 impacts in the whole run, his at 0.4 m/s giving 1 mm waves: his hips are fastest
  mid-stroke and nearly still when they reach her, so the speed measured at the touch was low and often under the 0.35 m/s gate.
  A stroke is now his pelvis / thigh closing on her (relative to her own motion) and backing off again: the first touch of each
  forward stroke fires once with that stroke's peak speed, 8 mm for a slow stroke up to 20 mm for a fast one, with its own
  slider (Physics tab, **stroke ripple**, `[Physics] RippleThrustAmt`). Log: `Ripple: stroke #n by Player ...`.
- Lip sync: the jaw ran ahead of the lips and they were out of step. With **Keep her expression's mouth** up, the mood clip (a
  moan / feeling loop) kept playing its own jaw and lips under the visemes at its own rhythm. The jaw is now the curve's alone and
  the other mouth / cheek bones take the mood clip's mean pose (the smile stays, its motion does not). `[Voice] LipLead` is replaced
  by **Lip timing** (`[Voice] LipTiming`, -300 to +300 ms, + = earlier, default 0): the old range could not move the mouth later
  and a maxed value put it about 0.3 s early; the game's audio output buffer delay is now added by the plugin (log
  `Lip sync: audio output ... the mouth waits N ms`). mouthClose is held to at most half of jawOpen (equal amounts shut her lips
  completely while the chin dropped).
- Mute the game's own voice: its sex-facial moans (Feeling_enter, on a source of their own) and its dialogue voice (it talks
  through it in scenes too; `[Voice] MuteGameDialogue`, on) were never muted. A muted game clip also kept counting as "she is
  speaking", which blocked the pack's moans and lines for most of a sex scene, so the new packs seemed to have no moans. Now each
  game moan / sex voice / feeling clip that starts while muted is answered by a pack clip of the same kind (breath, moan by her
  desire, feeling, climax; at most one per 1.5 s, never over her own pack voice). Log `voice: pack stand-in #n for the game's ...`.
  The pack folders were complete (all seven categories in each of the four new packs).
- Debug tab: timing, GPU check, soft-point markers, wrinkle / muscle debug views, Flex test and every Reactions test (reactions,
  skin sounds, eyes / blink, face moods, lip sync, diagnose, voice dump) moved there from their tabs.

0.11.11 (lip sync rebuilt: mouth poses, lower lip, expression under it; grab sound):
- Lip sync looked poor and pushed her lower lip out. The mouth poses that turn the Audio2Face weights into bone moves were
  "seeded" by copying whole frames of her face clips: jawOpen came from a fellatio clip, mouthLowerDown carried a -50 deg turn of
  the lower lip, mouthFunnel -22 deg, mouthClose +27 deg, and only 7 of the 31 mouth shapes got a pose (smile, pucker, press,
  roll, stretch did nothing). On top of that mouthClose (lips together while the jaw is open) was used even when Audio2Face gave
  more of it than of jawOpen (0.49 vs 0.16), which pushed the lower lip up past her neutral mouth into a pout. Now:
  - the poses are built in her mouth's own frame (left / up / forward from her neutral lip corners and lips, sized by her mouth
    width): millimetre moves of the lips, corners and cheeks for 30 shapes, a rotation only on the jaw (jawOpen = the survey's
    widest jaw opening, up to 25 deg, with the lower lips carried round the jaw's pivot), mouthClose = minus that on the lips;
  - mouthClose is clamped to jawOpen while playing;
  - a map saved by an older version is set aside as `arkit-map-<version>.txt` and rebuilt on the first lip-synced clip (log
    `lip sync: the mouth poses from an older version were set aside ...`, then `lip map built 30 shapes for a N mm mouth ...`);
  - the lip probe printed degrees x 57 (`Lip_Lower_Insite ... 1930 deg` was 34 deg).
  The editor's buttons are now Build empty shapes / Rebuild all / Rebuild this shape.
- The built grab sound (used while `Sounds\grab` is empty) sounded like a knock on wood: an 8 ms attack and a 60 ms decay of
  low noise. It is now a squeeze: a breathy skin band swelling in over ~50 ms, held while the fingers settle, released over ~150 ms,
  with a faint slide and a soft low press, no transient.
- Her expression while she speaks: while a voice-pack clip with a lip-sync curve played, her face clip's mood kept its eyes, brows and cheeks but lost its whole
  mouth (the clip skips the mouth bones while she speaks), and the lip-sync layer then pulled the mood's cheeks halfway back to
  neutral: a smiling or pained line was spoken with a blank mouth. Now the mood's mouth and cheek pose of that frame is kept
  under the lip-sync poses (Reactions tab, Lip sync: **Keep her expression's mouth**, `[Voice] LipMood`, 70 %), so the visemes
  go on top of the smile or grimace and the brows / eyes stay the mood's as before. The curves only drive the mouth (their eye
  and brow channels are not used), so the two layers do not fight over the upper face.
- Voice packs (user's PC, not in the repo): Pixie, Pixie Dominant, Southern Domme and Scottish Peach (vamX voices) with lip-sync
  curves for every clip; the Maive pack was removed.

0.11.10 (a test for the muscle shape):
- No way to see the muscle bulge without finding a pose that tenses each muscle. Skin tab, Muscles: **Flex test** puts arms,
  back, core and legs in turn at full activation for 2 s each (at least 3 mm, Flex at least 1) over whatever she is playing, the
  other areas at 0, then hands back to her pose's tension. It needs only GPU contact (works with the relief off). The log says per
  step how far the bellies go and on how many vertices of which meshes (`Muscles: Flex test arms at full for 2 s: bulge up to ...`).

0.11.9 (the tickle face is a helpless laugh):
- Ticklish now reads as an agonizing laugh instead of a plain smile: the smile / laugh clip keeps her mouth and cheeks, the Pain
  clip's squeezed eyes (85 %) and half of its knitted brows go over it, and her inner brows rise while the rest of the brows drop
  and the lids squeeze (built offsets on her lid / brow bones, like the built Surprise). "Shy" and "embarrass" clips no longer
  count as ticklish faces. A clip you pick for Ticklish in the panel gets the same treatment.

0.11.8 (tensed muscles as shape, firmer under a hand):
- Muscles were normal-map relief only: a flexed biceps or a back at full fitness looked painted on, the silhouette never changed.
  Each muscle now also pushes its belly out as geometry through the GPU contact chain (the inverse of a dent): the bake reads the
  muscle region map at every body vertex (two muscle ids + share) and gives it a belly weight, 0 at the muscle's edges and
  tendons, rising over 2.5 cm (arms) to 4 cm (legs) of geodesic distance, lower in the authored grooves (linea alba, spinal
  groove), so the abs and back keep their valley. Per frame the muscles' activations (the same ones the relief uses: Fitness
  plus the pose's tension) x the area's size in mm x Flex go to the compute shader; Compose adds the bulge under the dent and
  Grad / Apply bend the normals with it. The relief stays on top as the fine detail. Ribcage (bone showing, not a muscle) stays
  relief only. Needs GPU contact (Physics tab) and the shader bundle that comes with this DLL.
- A hand pressed a flexed muscle as deep as a relaxed one. The soft tissue depth under a muscle now shrinks with its activation
  (x (1 - Firmness x activation), Firmness 0.5): the GPU dent, the physics hands' stop (HerBody.SkinAllowance) and the soft
  zones' resistance (glutes, outer / inner thigh, belly) all read it. The hands meet the bulged surface, not the relaxed one.
- Pose -> tension, anatomy pass (the tension now moves geometry, so a stretched muscle must not read as working): hamstrings no
  longer tense for a straight leg lifted while lying (a passive stretch), they do for hip extension; lats overhead 0.7 -> 0.35
  (stretched; they work propping her up and pulling the arm back); adductors at a wide straddle only under her weight; abs brace
  when she lies on her back with her legs lifted.
- Panel, Skin tab, Muscles: Flex, Core / Back / Arms / Legs (mm), Firmness, each greyed with its reason (muscles off, GPU contact
  off, old bundle, no muscle on her baked meshes). Config [Muscles] Flex, CoreMm, BackMm, LegsMm, ArmsMm, Firmness.
- Cost: a frame with only muscle bulges runs Compose + Grad + Apply on her body meshes (3 dispatches each, no dilate / smooth
  passes), the same path as the bend bulges. With Fitness above 0 that runs every frame; Flex 0 removes it. Not measured in the
  game: compare the `Perf` line's `gpuRun` stage and GPU ms with Flex 1 and 0.

0.11.7 (fixes from the 0.11.4 headset run; the lip sync arrives):
- Panel jumped when an option changed or a debug line updated, and could not be resized: it was a layout window that grew and
  shrank with its content. Now a fixed-size window: any edge or corner resizes it, the title bar moves it, the content scrolls,
  the timing line folds away (closed by default). Tabs moved to a column on the left; a new **Skin** tab holds wrinkles, muscles
  and skin marks (out of Physics).
- No dents or marks on her face and neck: both meshes are GPU-only, so their skinning space could never be checked on the CPU and
  the dent stayed off for them ("cannot be calibrated (mesh is not readable)"). They now take the space her body found (rootBone).
  Her face and neck give a little more (head 1.5 + 3 mm, neck 3 mm soft), so they dent at a lighter press.
- Hands stopped centimetres off her face and throat: her head collider was a 9.5 cm default sphere about Head_M (her face mesh was
  never read for her body's shape). Her shown face and neck meshes now shape the head and neck colliders.
- Dents looked polygonal: the dent smoothing was a fixed number of vertex hops, sized for her own mesh (about 7 mm edges). With
  Fidelity's subdivision (edges halved) it blurred half as far and the dent kept the hand capsules' outline. The passes are now
  sized in metres (about 1.2 cm narrow, 2.4 cm wide) from the skin's own edge length (not the dense fingers').
- Skin sounds inaudible: pat, grab and rub were scaled by the slap volume (0.12 in the user's config; the rub also by 0.4). They
  have their own **skin volume** (default 80 %) and test buttons (pat, grab, rub); the rub's start is logged.
- Embody: his fingers kept the animation's pose: no skinned renderer carries his finger bones, so there was no bind pose to
  take the neutral hand from. The neutral hand is now the animation's hand straightened (each joint's curl taken out), so rest
  curl, grab and the clasp stop work again. The thumb curls across the palm toward the fingers (it curled toward the palm normal
  like a finger) and has its own joint limits (base 50, middle and tip 80 deg; the physics hand's thumb allowed 100-110, the
  embodied one up to 120). His hands hide the controller models while they follow them. Embody is off at every game start
  (`[Embody] OnAtStart`). View forward / up / right sliders move the view from his eyes (his head's axes, +-20 cm).
- Lip sync (the 0.9.9 Audio2Face player, never merged into 0.10-0.11.6, so 0.11.4 had only the loudness jaw): baked ARKit curves
  played in step with her voice clips through a mouth-bone map (`arkit-map.txt`, editor in the Reactions tab). With no map yet,
  the poses are seeded from her face clips on first use and saved. "Test: a pack clip with lip sync" logs every link (switch,
  face survey, curves found for the pack, posed shapes) and then 3 s of the top weights and the mouth bones moved. `tools/a2f`
  bakes the curves. Curves for the Rebecca and Anime packs were baked on the user's PC.
- The Physics tab's "Smoke test" (it sinks all of her skin 1 cm for 3 s on purpose, to check the GPU dents) is now "GPU check" with a line saying so.
- Ripple from his hips: his pelvis and thighs stay on her during sex, so the "fresh contact" rule only fired on the first touch.
  Every stroke (0.35 m/s or faster, at most one per 0.35 s) now sends a ripple (`[Physics] RippleThrust`, Physics tab).
- Voice packs: **Mute the game's own voice** while a pack is on (its moans and dirty talk at volume 0; the pack's clips on the same
  sources keep theirs; story dialogue untouched). The pack's lines are hooked into her states (guard, afterglow, ouch, request,
  invite lines played in the 0.11.4 log).

0.11.6 (fit fixes found on the Genesis 9 character; the plugin is unchanged):
- Jaw and chin: the rigid VaM head was blended into the body warp up to her Head_M (mouth height), so a large body warp sheared
  the lower face (Krista: jutting jaw, parted lips; chin 21 mm off). The blend now ends under her chin and the warp carries
  rigid head anchors (skull extremes, chin, eyes), so the neck takes the change smoothly (Krista chin 1.5 mm; Lexi 1.5 mm).
- Mouth: her mouth opening is landmarked by its corners too (onto the ends of the character's Lips), so a mouth of another
  width no longer folds her lips. The adapter's geometric Lips are no wider than the mouth opening.
- Shoulder no-data spheres keep off the breasts. `--breast-prealign` (off): an experimental ray warp of her breasts.
- Still open: on a breast of a quite different shape (Krista: flatter, lower) the fit folds her breast skin under the breasts;
  such a character is not usable as a conversion yet (its slider-fit preset is).
- `run.ps1 -Daz` now makes the slider-fit preset by default (DAZ characters: her own mesh and skin); `-Convert` runs the parked
  full conversion.

0.11.5 (first real DAZ Genesis 9 character through the pipeline; the plugin is unchanged):
- RKS Krista 9 (a Genesis 9 product) went DAZ Studio -> FBX -> adapter -> fit: all 53 joints from the Genesis 9 table, body fit
  residual 0.03 mm, eyes open and fitted, textures 9 of 9, both material checks PASS (albedo gain 0.70). Not good enough yet:
  her jaw / chin come out pulled in (chin 21 mm off) with a step at the face / neck seam, and the chest has folds under the
  breasts (54 flipped triangles); the Krista conversion is not installed, only the slider-fit preset `RKS Krista 9 (fit)`.
- DAZ's FBX export leaves out the PBR shaders' image maps: `daz_export.dsa` now lists every image-mapped channel of the figure
  (`<fbx>.materials.txt`) and the adapter fills Base Color / Normal Map from it.
- Genesis 9 keeps eyes and mouth as separate figures and has no Lips / Ears surfaces: the adapter merges eye / mouth meshes
  (their eyeballs become Sclera, so eyeball size and eye opening are measured), and derives Lips (face skin within 9 mm of the
  mouth opening) and Ears (outermost head skin at eye height) from the geometry when no material names them.
- Slider fit: glute projection measured over a 14 cm band (the buttocks' fullest point is below the widest hip on Genesis 9).
- run.ps1 -Daz: refuses to start while DAZ Studio is open (one instance only), and ends the instance it started when DAZ
  Studio lingers after the export (seen with DAZ Studio 6).

0.11.4 (DAZ characters through DAZ Studio; the plugin is unchanged):
- `run.ps1 -Daz <character preset .duf>`: DAZ Studio loads the character at base resolution and exports an FBX with its textures
  (`tools/sources/daz_export.dsa`), which goes on through `-Source`. run.ps1 first checks that the preset's base figure is
  installed (a missing Genesis 9 base otherwise leaves DAZ Studio on its missing-files dialog) and stops after 10 minutes
  without a result. `-Daz` / `-Source` also run without a usable export of her (they make the source folder; the conversion
  then asks for the export).

0.11.3 (Surprise face built from her face bones):
- The Surprise face was wrong (should be wide-open eyes and raised brows): her face Animator has no clip named for surprise and none
  that widens the eyes or raises the brows, so the pick by "most eye movement" played blink / sad / angry clips that close or narrow
  them. Surprise is now composed from her face bones (the ones the Face sliders move): upper lids up 2.6 mm, lower lids down 1 mm,
  brows up 4 mm, inner brows 5 mm, a fifth of her widest jaw opening (not while she speaks), along her up direction in the head frame
  taken from her bind pose (signed: it can't close the eyes); in 60 ms, held, out over 0.4 s. Only bones her Animator rewrites every
  frame are used (no accumulation; the log names any left out). A clip picked for Surprise in the panel still wins.

0.11.2 (other character sources: the plugin is unchanged, the pipeline takes any rigged humanoid):
- `run.ps1 -Source <file>` (`tools/sources/humanoid.py`): a DAZ Genesis 2/3/8/9 FBX, a Unity / VRChat avatar FBX, Mixamo, VRM,
  GLB, Character Creator or Unreal figure becomes the same folder the VaM plugin writes (Genesis 2 joints from a bone table
  picked by the names present, the body's faces given body roles by their nearest bone and material name, nipples from the
  chest shape, the materials' base colour / normal images, the pipeline's frame and units), then converts as usual or, with
  `-Sliders`, as a slider fit. A round trip through FBX of a VaM figure gives the same joints (1.3 mm), vertices and slider fit;
  the other rig tables wait for a real file of each kind. Unrigged meshes: rig them first (ActionForge, Mixamo, AccuRIG).
- The fit, bake and slider fit read groups as `Role@material`, so a body role spread over several source materials keeps each
  material's textures.

0.11.1 (the pipeline is no longer tied to one face and one set of her parts):
- The pipeline only knew her default variants by name (`reika_face_a`, `reika_nipple_type_1`, ...), while the game swaps her
  face, nipples, genitals, nails, lashes, brows and bodies by customisation: a character converted while she wore face_a had
  no face when the game put face_c on. Now the Presets-tab export writes every variant under her, worn or not (meshes.txt
  column `active`), and `tools/pipeline/roles.py` finds each role by pattern: the variant she wears (else the default) is
  fitted, the others follow the fitted skin, every face material gets its baked skin and face-only variant, renders show only
  what she wears. The material check, slider fit and bake targets follow the same roles.
- `docs/SOURCES.md`: the design for other sources (any rigged humanoid through one Blender adapter: Unity Humanoid / VRChat,
  Mixamo, VRM, DAZ Genesis 3/8/9, Character Creator, Unreal, Rigify; quick rigging of unrigged meshes; UABEA; a VRChat-style
  avatar replacement mode).

0.11.0 (character import: why the skin came out wrong, a material check, and a slider fit as the low-risk alternative):
- Imported skins looked wrong for three measured reasons (Yin, checked on the files): (1) the baked normal maps carried body
  shape, not skin detail: the selected-to-active bake records VaM's normal relative to hers, so every fit residual, faceting
  difference, open border and finger mis-hit became a tilt (large-scale tilt p99 22 deg on the body, 139 deg on the face:
  nostrils, lips, eyelids and ears shaded inside out; her own maps: 7 deg); (2) the albedo was about twice too bright in linear
  light (VaM's diffuse maps are near white, VaM's shader and material colours scale them; the game's skin shader takes the
  albedo as it is, and the tint is set white with a character skin on); (3) her newest export was taken with Physics / Fidelity
  on, so it held SfMorph's own subdivided clones (upper body 75 281 vertices instead of 19 469, names "(SfSoft)") and the
  wrinkle / mark render textures instead of her own.
- Bake: normals are detail only (each material is baked twice, with and without VaM's normal map; the rotation that takes the
  bare-geometry normal to +Z is applied to the detailed one, so shape differences cancel and VaM's pores stay; grazing hits fade
  to flat). Albedo brightness matched to her body (one gain for all materials from VaM's torso + limb skin x its diffuse colour
  against her body albedo x _SkinColor, VaM's colours kept; `run.ps1 -Brightness vam` keeps VaM's values).
- Material check before anything reaches the game (`tools/pipeline/matcheck.py`, run after every bake, or alone with
  `run.ps1 -Name <name> -Check`): albedo luminance against hers (0.8..1.25), clipped texels, the normal maps' large-scale tilt
  (p99 <= 12 deg), her own shader layers that stay on top of any skin (lipstick / blush / attach maps, from her export);
  `_check\materials.txt`, `_check\materials.png` (her albedo | baked | her normal | baked | tilt), PASS/WARN rows in
  `_check\summary.txt`. `run.ps1 -Name <name> -Rebake` bakes an existing character again without refitting (old textures
  kept in `_check\old_textures`).
- Export (Presets tab): writes the game's own meshes behind our clones (soft zones, Fidelity, the character morph) and her own
  textures behind the wrinkle / mark copies, every texture of her skin materials (attach / blush / mask maps too) and
  `materials.txt` with every property of them. `run.ps1` skips exports that hold clones. The VaM plugin (`tools/vam/SfExport.cs`)
  also writes `matprops.txt` (each material's diffuse colour and scalars; copy it to VaM again).
- Slider fit (`run.ps1 -Sliders`, `tools/pipeline/sliderfit.py`): no mesh swap, her own skin. The VaM character's relative
  proportions (girths, segment lengths between landmarks both rigs agree on, waist / under-bust / hip sections, breast and
  glute projection, nipple spacing, hands, feet, head; VaM scaled to her eye height) as her Body sliders, written as the preset
  "<Name> (fit)" with the measurements as comments; the face is not fitted.

0.10.0 (muscle definition that follows her pose):
- Her body had no muscle definition beyond the shape: a converted VaM character's Fitness slider (Characters tab) moves the shape, but
  the definition a fit VaM look gets from its normal maps (linea alba, the upper abs, serratus lines, the iliac V, back and calf
  detail) never reached her, and nothing reacted to what her body was doing.
- New: authored muscle maps on her body atlas (`tools/muscles/author.py`, 27 muscles: abs, obliques, serratus, ribcage; spine, lats,
  shoulder blades; glutes, quads, hamstrings, adductors, calves; deltoids, biceps, triceps, forearms), blended into the same
  normal-map / albedo copy as the wrinkles (one texture per sheet, one blend pass). Each muscle fades between a resting tone and full
  definition by her pose, like wrinkle maps by an expression: spine bent forward (or held leaning back with the hips bent) = abs, an
  arch or a bend over = back, a squat or hip / knee flexion under her weight = glutes and thighs, elbow flexion = biceps, tiptoes =
  calves, arms overhead = serratus and lats, propping herself up = arms. Physics tab -> Muscle definition: on, Fitness (resting
  tone), Tension gain, Strength, Groove shade, Core / Back / Legs / Arms, debug view (Tension: green relaxed to red tensed; Muscles:
  each in its own colour). Works with the wrinkles on or off. Cost: about 30 bone reads per frame on the CPU; the blend re-runs only
  when a muscle moves by 1/32, at most 10 times a second, and no strain is measured for it. Needs the new `sfmorph_shaders`.

0.9.9 (timing line, small per-frame savings):
- Nobody knew what the plugin costs per frame: the logs only hinted at the frame rate (about 23 fps from the hands' fixed steps per frame).
  New: every 10 s a `Perf last 10 s` line in the log and at the top of the panel: fps and frame time, fixed steps per frame, the GPU
  time of the frame when the VR runtime reports it, avg/max ms of each part of the plugin (apply step, her body, overlap, physics hands
  per frame and per fixed step, reactions, wrinkles, embody, soft zones, GPU contact dispatch, skin marks raster, panel), and the
  frame's phases (the game's and ours together: physics, update, late update, render submit, and the wait for the GPU / compositor).
  `[General] PerfLog` turns it off.
- Per-frame garbage and reflection removed without changing what you see: mesh names for hidden meshes (character conversions), bone
  names of her self-contact capsules, the skin marks' off status, the morph's missing-folder status, the elbow/knee hinge name, the
  voice-level read for the audio-driven jaw (a bound delegate instead of `MethodInfo.Invoke`), the wrinkles' baked-normal lookup
  (a direct call instead of reflection).
- Skin marks: the redness / shading sliders and the marks boost had no effect since 0.9.4 (two settings had slipped into a code
  comment). They work again; at the default values nothing changes.

0.9.8 (face wrinkles: no lines under her mouth, no ring around her neck at rest):
- Two lines ran down from under her mouth like marionette lines. Cause: they were the neck creases. They were placed by casting onto
  her face from the front, and at the neck's height the front of her face is her chin, so the neck bands landed on the chin. The front of
  her neck is on the body texture, not the face: the neck lines are now two soft bands there, placed from inside the neck outwards. The
  Neck toggle now works without Body.
- The neck line (72 % at rest) and the frown lines stayed on: her idle face animation keeps her neck at about 0.03 compression and between
  her brows at 0.04-0.06 with no expression. Face and neck regions now measure from their own idle level (a slow minimum: drops at
  once, rises over 20 s), so expressions show and her resting face stays smooth; a held expression fades after ~20 s. Body creases still
  measure from her bind pose (a bent elbow keeps its crease). The 10 s log lines print each face region's idle level.
- The smile lines end beside her mouth corners instead of running below them.
- Bundle rebuilt (maps), with 0.9.7's shaders.

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
