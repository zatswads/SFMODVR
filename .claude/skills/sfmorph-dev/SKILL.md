---
name: sfmorph-dev
description: Work on SF Body Morphs (src/SfMorph), the BepInEx plugin for Sex Formula VR that adds VaM-style body-part sizes, blendshape sliders, bone scaling and presets for the game's woman. Use for any bug report, log/screenshot triage, feature or build in this repo.
---

# SfMorph development

Read `docs/HANDOFF.md` (architecture, game facts, apply model) before changing code, and `docs/TESTING.md`
for what to verify and what to ask the user to test.

## Session title

`YYMMDD_HHMM <version> <topic>` (UK local time of the latest activity; the version the work is about; SfNr work writes `SfNr`), e.g.
`261017_1531 0.11.10 headset feedback: muscles, dents, ripple, voice`. Set it with `set_session_title` (`self`) at the start and again at
each wrap-up / release; give sessions you start the same format.

## Loop for every change

1. **Triage first, cheaply.** The user attaches `LogOutput.log` and desktop-mirror screenshots.
   - Version: `grep -n "SF Body Morphs" <log>`. A stale DLL is the most common "still broken".
   - Our lines: `grep -n "SF Body Morphs\|Game rig\|Character '" <log>`; errors: `grep -n "SfMorph\|Exception" <log>`.
   - The `Character '...'` block lists her meshes, bones and shapes: use it to tune `Region.All`.
   - Hand big reads (long logs, IL dumps) to a subagent and keep only its conclusion.
2. **Find the root cause.** Check first: is the value going through the trackers (game value vs our last write)?
   Did the game swap meshes (renderer set changed → `Build` with the previous look)? Is a game system
   (DynamicBone, FinalIK, Animator, Timeline) writing the same property after us?
3. **Implement** in the existing style: reflection for anything in Assembly-CSharp (no compile-time reference),
   comments that explain why, greyed controls with an `off: reason`.
4. **Build**: `REFS=<refs> sh build.sh`. Missing DLLs → ask the user (list in HANDOFF → Build; never commit them).
   Check `strings -el dist/SfMorph.dll | grep <version>`.
5. **Release**: bump `Plugin.Version`, README changelog (symptom → cause → fix), commit with the trailer, push `main`.
6. **Reply**: what was wrong and why, what changed, what to check, and say plainly it was built but not run in game.

## Face, lip sync and voice packs (lessons from 0.9.9-0.11.45)

- **Never copy a game clip's frame as a pose.** 0.9.9-0.11.10 seeded the ARKit mouth poses from her face clips: a fellatio frame
  for jawOpen, -50 deg on the lower lip for mouthLowerDown; the lip curled and pouted. Poses are built (`ReactionsFace.Seed`):
  millimetre moves in her mouth's frame, rotation only on the jaw and on the lips carried round the jaw's pivot.
- **Check the frame, don't assume it.** 0.11.11-0.11.38 took "up" from two inner-lip bones that coincide on a closed mouth; up
  pointed forward and every shape moved the wrong way. `Seed` now refuses to build unless her brows sit 40-80 mm above the mouth
  along up and opening the jaw takes the lower lip down; keep that check when changing it. Offline, the saved `arkit-map.txt`
  shows it: on bones with the same parent, mouthRollLower / Pucker / UpperUp must not point along jawOpen's lower-lip delta.
- **Read the curves before mapping a channel.** Measure each new ARKit channel over a pack's CSVs (per-clip minimum = baseline,
  p50 / p99) before driving bones with it. mouthClose is "lips together while the jaw is open": clamp it to jawOpen (0.11.11 / 0.11.12).
- **Limits stay on:** `LipStep` holds the non-jaw deltas of a bone to 15 mm / 30 deg (the jaw-follow part is outside the limit,
  or the lips leave the teeth); the lip probe logs `held back ...` when a pose is too strong.
- **Old saved maps:** a fix to the pose builder must retire maps saved before it (`LipSync.RetireOldMap`, keeps a backup) or add
  only the missing shapes (`Seed(onlyEmpty)`); otherwise the user keeps the broken poses.
- **Verify in the log after every change:** `lip map built N shapes ... (up from the model's up, brows N mm above the mouth)` and
  `lip probe` lines with single-digit mm, under ~25 deg, no `held back`.
- **Voice packs:** never delete user audio (move it to `_removed-voice-packs`); check `origin/main`'s version before copying a
  build into the game (an older worktree build downgrades it); pack paths can pass 260 chars, so bake from a short staging path.

## Don'ts

- Don't scale or move her root (`MovePlayerAndGirl.reika`): the game positions it (shoe height, scenes).
- Don't write blendshapes/scales outside the trackers (accumulation, nothing to give back).
- Don't add per-frame `FindObjectsOfType`/`GetComponent*`; the 2 s scan caches per character.
- Don't share code, config or keys with SfNr; both must load and unload independently.
- Don't put model names in commits, code or docs.
