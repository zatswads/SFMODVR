# SFMODVR — SF Body Morphs

BepInEx 5 plugin (`src/SfMorph`, built to `dist/SfMorph.dll`) for the HDRP VR game Sex Formula: VaM-style
body-part sizes, blendshape sliders, bone scaling and presets for the game's woman. Sibling of the SF Neural
Rendering VR plugin in `zatswads/UDLSS5VR` (both load together; keep them independent).

- Start with `docs/HANDOFF.md` (state, architecture, game facts, rules). Test plan: `docs/TESTING.md`.
  Changelog: `README.md`. Workflow skill: `.claude/skills/sfmorph-dev/SKILL.md`.
- Build: `REFS=<reference DLL folder> sh build.sh` (game DLLs must be uploaded by the user; never commit them).
- Branch `main`; bump `Plugin.Version` + README entry per release, rebuild `dist/SfMorph.dll`; no model names in commits.
