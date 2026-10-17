---
name: sfmorph-timing
description: Frame cost, frame pacing and stutter work on SF Body Morphs (src/SfMorph) and its sibling SF Neural Rendering VR (zatswads/UDLSS5VR). Use when the user reports low fps, hitches, judder, jiggle or motion that stutters, "smooth without the mod", or asks what something costs per frame, and before releasing any change to a per-frame simulation (soft zones, Overlap, physics hands, ripple, wrinkles, GPU contact).
---

# SfMorph timing: cost, pacing, stutter

Read `docs/HANDOFF.md` first (apply model, PlayerLoop order); this skill is the timing part of `sfmorph-dev`.

## What the plugin tells you (log signatures)

- `Perf last 10 s: N fps (frame avg / max), fixed steps/frame avg / max, GPU ...` then two lines: `ours ms avg/max` (each stage of
  Body.ApplyAll, PhysicsHands.FixedStep summed over steps, GpuContact.Run, Wrinkles' hook, SkinMarks' raster, panel) and
  `frame phases` (PlayerLoop: `fixed` incl. `physx`, `update`, `preLate` = our apply step + the game's, `postLate` = HDRP's render submit
  on the CPU, `wait` = main thread waiting for GPU / compositor). Code: `Perf.cs`, `[General] PerfLog`. Also at the top of the F8 panel.
- `Soft zones clock last 30 s: frames, substeps avg/max, long frames, cuts, clamp %, biggest per-frame jump mm` (SoftSim.ClockLog).
  Healthy: substeps ~ frame ms / 8.3 (6 at 23 fps, 3 at 41), cuts 0 outside scene changes, jump < ~15 mm.
- `Overlap: reset on <bone>: jumped ... / turned ..., frame N ms` (once per kind per session): a cut inside normal motion is a bug.
- `Character '...': ... animator '<name>' Normal|AnimatePhysics [root motion] <culling>`; the DynamicBone survey prints `rate=` and `mode=`.
  AnimatePhysics = her pose moves in 20 ms fixed steps (0-3 a frame) while our sims integrate the frame time.

## Triage recipe (cheap first)

1. `python tools/timing/perf_report.py "<LogOutput.log>"`: one table of every Perf window, the soft-zone clock windows, timing flags,
   and a verdict (ours / render submit / GPU-bound). Don't read the raw log yourself; if more is needed, give the log to a subagent
   (`model: "haiku"`, Explore) with a precise question and keep only its answer.
2. Known baseline (0.11.34 log, RTX 4090 laptop, Quest Pro over VD): steady ~30-42 fps, ours 1.2-2 ms, `postLate` 17-23 ms = HDRP's
   CPU render submit dominates; load spikes of 1-4 s are soft-zone / morph builds on character changes (expected, not per frame).
3. If the user says "smooth without the mod": compare windows with Apply off vs on, then the per-feature A/B below.

## A/B list for the headset (one change at a time, ~20 s each, same spot, still then touching)

Baseline -> Physics hands off -> soft zones Quality Fidelity->Balanced -> GPU contact off -> Wrinkles off -> Reactions off -> Embody off
-> Apply off (ours ~0: the rest is the game) -> SF Neural Rendering on/off (F6; to remove SfNr completely, pull SfNr.dll for one run)
-> SfNr frame generation off (optical-flow interpolation of uneven input judders on fast-moving flesh) -> Springiness 0 (no soft-zone
lag: if the stutter goes, it is the soft-zone clock).

## Rules that keep motion independent of the frame rate

- **Simulations integrate exactly the frame time.** `SimClock.Substeps(dt, step, max)` equal substeps of `dt / n` (Overlap and SoftSim
  do this). Never a fixed-step accumulator without output interpolation, never "behind: drop the rest" (`_acc = 0`): 0.11.47 SoftSim
  lost a quarter of every frame at 23 fps and alternated 2/3 steps at 41 fps: the jiggle stuttered.
- **Per-frame inputs are spread over the substeps** (a moving target): a lag / kick applied once before the substeps is a step input
  every frame whose size follows the frame time. Overlap slerps its target across substeps; SoftSim applies `Lag / n` per substep.
- **Limits inside the substeps, with the velocity into the limit removed**, not once per frame (a swing knocks against the wall).
- **Cut / teleport tests scale with dt:** `SimClock.Jumped(dist2, metres, speed, dt)`; a flat "20 cm in a frame" is ordinary dance
  motion at 23 fps or in a 100 ms frame.
- **Clamp a long frame** to `SimClock.MaxFrame` (0.1 s) and scale that frame's kick by `dt / dtRaw`.
- **Smoothing is dt-based:** `x += (t - x) * (1 - exp(-dt / tau))`, never `Lerp(x, t, 0.5f)` per frame (the lint flags it).
  Known exception kept on purpose: `HerBody.Track` (physics hands' per-frame sample, tuned at 23-40 fps; changing it changes the hands).
- **Fixed-step work** (Runner.FixedUpdate) only does what must happen per physics step; per-frame inputs are sampled once per frame.
- **Order:** body changes in `ApplyStep` (PreLateUpdate, before `ScriptRunBehaviourLateUpdate`: after Animator, before DynamicBone /
  FinalIK); GPU work in the post-skinning step or `beginContextRendering` with a `Time.frameCount` guard (never per camera / per eye).
- **No per-frame garbage:** no `Camera.allCameras`, `Object.name`, `MethodInfo.Invoke(new object[] ...)`, string keys / status strings
  rebuilt every frame, `GetComponent*` / `FindObjectsOfType` per frame. Cache, bind delegates, rebuild on change only.

## Checks before a release that touches a per-frame system

- `sh tools/timing/check.sh` (lint + offline clock harness; exit 1 = regression). The harness (`tools/timing/Program.cs`) runs the
  soft-zone spring model at 90/72/41/23 fps and uneven 23 / 41 fps against a 4 kHz reference; extend it when you add a simulation.
- The lint also runs on SfNr: `python tools/timing/lint_timing.py <UDLSS5VR>/src/SfNr`. A finding that is fine (load time, every few
  seconds, audio-rate) gets `timing-lint: ok <why>` on its line.
- TESTING.md entry with the log signatures to read; HANDOFF state line; README symptom -> cause -> fix.

## SfNr side (separate repo, separate commit)

SfNr changes no Unity timing and writes no bones; its pacing risks are GPU load near a VD / SteamVR frame-rate step, frame generation,
and main-thread garbage (fixed in 0.31.1: `CamList`, `Plugin.ConfigRev` gates for settings keys, per-eye note signatures, cached head
bones in People). Work in `C:\Users\rings\Claude\Projects\Modding\UDLSS5VR` (a git worktree of it for parallel work), branch
`claude/vam-dlss-unity-compat-ie6q8x`; build `dotnet build src/SfNr/SfNr.csproj -c Release -p:LangVersion=latest`.

## Spending tokens well

- Sweeps (find every per-frame allocation / reflection / accumulator, list hooks, scan a log) go to an Explore subagent with
  `model: "haiku"` or `"sonnet"` and a precise, bounded report ("file:line, how often, one-line fix, under 800 words"). Run SfMorph and
  SfNr sweeps in parallel. Keep design and the actual edits in the main session.
- Prefer the scripts (`perf_report.py`, `lint_timing.py`, the harness) to reading logs or code by hand.
