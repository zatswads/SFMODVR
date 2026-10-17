# SFMODVR — SF Body Morphs

BepInEx 5 plugin (`src/SfMorph`, built to `dist/SfMorph.dll`) for the HDRP VR game Sex Formula: VaM-style
body-part sizes, blendshape sliders, bone scaling and presets for the game's woman. Sibling of the SF Neural
Rendering VR plugin in `zatswads/UDLSS5VR` (both load together; keep them independent).

- Start with `docs/HANDOFF.md` (state, architecture, game facts, rules). Test plan: `docs/TESTING.md`.
  Changelog: `README.md`. Workflow skill: `.claude/skills/sfmorph-dev/SKILL.md`.
- Build: `REFS=<reference DLL folder> sh build.sh` (game DLLs must be uploaded by the user; never commit them).
- Branch `main`; bump `Plugin.Version` + README entry per release, rebuild `dist/SfMorph.dll`; no model names in commits.
- Session titles: `YYMMDD_HHMM <version> <topic>`, e.g. `261017_1531 0.11.10 headset feedback: muscles, dents, ripple, voice`.
  Time = the session's latest activity in the user's local time (UK); version = the `Plugin.Version` the work is about (the run's
  version for headset feedback, else main's version at the time; SfNr work writes `SfNr`); topic = a few words, no "SfMorph".
  Rename yourself (`set_session_title` with `self`) when you start and again at each wrap-up / release so the time stays the latest;
  sessions you start get the same format.
