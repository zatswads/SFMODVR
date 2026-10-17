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

## Don'ts

- Don't scale or move her root (`MovePlayerAndGirl.reika`): the game positions it (shoe height, scenes).
- Don't write blendshapes/scales outside the trackers (accumulation, nothing to give back).
- Don't add per-frame `FindObjectsOfType`/`GetComponent*`; the 2 s scan caches per character.
- Don't share code, config or keys with SfNr; both must load and unload independently.
- Don't put model names in commits, code or docs.
