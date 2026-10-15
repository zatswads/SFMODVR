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
