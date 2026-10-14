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
- One `Character '<name>': female, N skinned meshes, ...` block per character, with `regions:` (bone per part,
  `(game rig)` when from the game), `bones:` and `shapes:` lists.
- Red flag: `SF Body Morphs stopped after an error` (everything it changed is given back; the exception follows).

## Checklist

- [ ] F8 lists only her (not the player, not the male partner); status "the game's woman found (N body parts ...)".
- [ ] Each Body slider changes only that part; greyed parts say why. Thighs/arms don't grow the calves/hands.
- [ ] Breasts resize and still jiggle, also when grabbed (DynamicBone + CCD IK).
- [ ] Morphs (if any shapes): body shape moves the clothes with it; a face shape on top still animates.
- [ ] Changing face type / pregnancy stage / clothes in the game's menu keeps her changes on the new meshes.
- [ ] Apply changes off = exactly the game's look; on brings it back.
- [ ] Restart: her changes come back (file in `BepInEx\config\SfMorphs\characters`). Presets save and load.
- [ ] SfNr alongside still fine (AO, passthrough cut-out, NR follow the resized body).
