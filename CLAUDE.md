# SFMODVR — SF Body Morphs

BepInEx 5 plugin (`src/SfMorph`, built to `dist/SfMorph.dll`) for the HDRP VR game Sex Formula: VaM-style
body-part sizes, blendshape sliders, bone scaling and presets for the game's woman. Sibling of the SF Neural
Rendering VR plugin in `zatswads/UDLSS5VR` (both load together; keep them independent).

- Start with `docs/HANDOFF.md` (state, architecture, game facts, rules). Test plan: `docs/TESTING.md`.
  Changelog: `README.md`. Workflow skill: `.claude/skills/sfmorph-dev/SKILL.md`.
- Build: `REFS=<reference DLL folder> sh build.sh` (game DLLs must be uploaded by the user; never commit them).
- Branch `main`; bump `Plugin.Version` + README entry per release, rebuild `dist/SfMorph.dll`; no model names in commits.
- Session titles: `MMDD HHMM <mark> [Tag] <topic> <ver>`, at most 44 characters (the sidebar cuts the rest), e.g.
  `1017 1612 🧪 [SfMorph] Lip sync, voices 11.45`.
  - `MMDD HHMM`: the user's local time (UK) of the session's latest activity.
  - mark (what the user should do): ⚙ working · ❓ waiting for the user's answer or decision · 🧪 built and installed, waiting for
    a headset test · ✅ done, nothing pending.
  - Tag: `[SfMorph]` the plugin, `[SfNr]` the Neural Rendering plugin, `[Pipeline]` character conversion tools (tools/pipeline,
    tools/sources).
  - topic: 2-4 words, at most 20 characters, the area not the whole story ("Muscle maps", "Lip sync, voices").
  - ver: the last `Plugin.Version` the session released, without the leading `0.` (`11.45`); none for `[SfNr]`.
  - Rename yourself (`set_session_title` with `self`): ⚙ when you start, and the right mark at every wrap-up (❓ when your
    wrap-up asks the user something, 🧪 after an untested release, ✅ otherwise). Sessions you start get the same format.
