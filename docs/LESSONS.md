# SF Body Morphs — lessons (read before changing a system)

Hard-won rules from headset runs. Each entry: symptom -> cause -> the rule now. Add to it whenever a fix teaches something; never
regress an entry without saying why in the README.

## Skin marks (frozen: keep this behaviour)

Chain: `GpuContact` capsules -> `SoftDeform.compute` Depth (`_Foreign`) -> Compose (`_Marks`: x redness, y pressure, z lingering dent) /
`MarkDisc` / `MarksDecay` -> `SkinMarks.OnAfterImprint` UV raster (`SkinMarks.shader` pass 0, blur pass 4) -> albedo (pass 1 or
`WrinkleRaster` albedo pass) and normal (pass 2 or `WrinkleRaster` pass 1).

1. **Her thighs left a red patch on her belly in a squat (0.9.4).** Cause: every contact reddened, her own limbs too. Rule: only
   foreign capsules mark. Each capsule's mark weight is in `Capsule.c.y`. At 0 (her own limbs) it dents and adds a faint shade but
   never reddens or prints. Depth writes `_Foreign` from foreign capsules only. Keep the `ForeignMarks` marker kernel so an old bundle
   is detected.
2. **A resting hand slowly turned her red.** Rule: redness only past a knee. In Compose, `MarkKnee = 0.25` and
   `drive = saturate((fo.x - knee) / (1 - knee))`. The weight follows the hand's speed against `Root_M`: 0.4 at rest, up to 1.5
   moving (`GpuContact`). Gain is 0.35/s x boost (`Body` contact fill; was 0.6).
3. **Dark red smears on her groin in sex scenes (0.11.35).** Cause: his pelvis and thighs press deep and fast, so they scored like a
   slapping hand. Rule: `PartnerMark = 0.2` sits under the knee, so his body never reddens; its shade and print are scaled by
   `saturate(mw / 0.4)`. The later red, inside-out patches (0.11.40) were Delta Mush. Switch Skin marks off first to see which
   system is to blame.
4. **Slaps.** Reactions (tier >= 3) queue up to 4 world discs (~7 cm). `MarkDisc` adds redness past the knee on purpose: x `MarkBoost`,
   capped at 1.
5. **Prints and shade looked polygonal, "a footprint in snow" (0.11.20).** Cause: per-vertex values are linear per triangle. Rule:
   blur in UV before composing (pass 4, coverage-weighted Gaussian), sized in mm on her skin (`MarksSmooth` 6 mm). Print depth and
   fade are settings (`MarksPrint`, `MarksPrintTime`), never constants.
6. **Metres per UV.** Sheets differ ~3x (body ~1.27 m/UV, face ~0.37). `ContactBake` gives `sqrt(3D area / UV area)`; blur sigma,
   dent slope (`_MarksScale = 1/Mpu`) and crease shade all use it. No texel constants.
7. **UV seams.** Pass 0 rasterises with Blend Max (shared sheets and overlapping islands never erase each other) and writes coverage
   in A. `SampleMarks` normalises by coverage and dilates one texel into gutters. The mesh seams are stitched (`SeamBlend`, 0.9.7);
   without that, marks broke at the neck.
8. **Flip-Y.** Never hard-code it. All passes share one uv -> clip mapping. The pass 3 self-test reads a UV gradient back at
   v = 0.25 (0.75 = flipped). No raster until `_flipState == 2`.
9. **Albedo slot ownership (0.8.4).** Two copies fought over one slot. Rule: one albedo RT per material. While
   `Wrinkles.OwnsAlbedoSlot` / `OwnsNormalSlot`, SkinMarks keeps no copy: it hands its marks RT over (`MarksOf`, `AlbedoParams`,
   same maths) and calls `Wrinkles.Touch` after each raster. Every RT is made by `MakeRT` -> `Wrinkles.Own`, and `FindAlbedo` skips
   owned textures, so nobody adopts ours as a base. On Morph's skin toggle it hands the base back, waits 2.4 s (`RetakeAt`), then
   retakes. Restore a slot only while it still holds our RT.
10. **Idle restore.** None of our textures may be in a slot while no mark shows. Hold `max(10 s, 4.6 x MarkFade)`, then
    `ReleaseSheets("idle")`: slots restored, all RTs freed, Wrinkles Touched to drop our dent. Off = the same teardown. Exception =
    `GoDead` + restore; never throw in the render loop.
11. **Timing.** Redness fades as `exp(-dt / MarkFade)` (15 s). Raster every 2nd frame while hot (2.5 s), ~3/s while fading; skip
    sheets that are invisible and clean. With nothing pressing, only `MarksDecay` runs.
12. **Cost.** Each raster on a Wrinkles-owned sheet re-blends Wrinkles at full resolution with mips: the biggest steady GPU cost in
    contact (perf-0.9.9). Don't raise the rate.
13. **Dead sliders (0.9.4 -> 0.9.9).** `s.Redness` / `s.Ao` sat inside a trailing `//` comment, so the sliders did nothing. Rule: one
    assignment per line in the contact fill, and TESTING moves each slider to both ends ("Marks boost 1 -> 4").
14. **No face / neck marks (0.9.7, 0.11.7).** These meshes are GPU-only. They join as `Extra` v2 renderers and take their skinning
    space from a readable sibling (rootBone). Her own hands dent her face but never mark it.

## Cross-system lessons

- **Rule out stale builds first.** Check the version in the log (`grep "SF Body Morphs"`) and in the DLL (`strings -el`). Each
  shader feature needs a marker-kernel or pass check that warns and greys out its row.
- **The bundle must hold an AssetBundle object** (0.7.7: the game refused it). `com.unity.modules.assetbundle` in the bundle
  project's manifest; `build.ps1` checks.
- **Never adopt or export our own textures as hers.** An export once saved the wrinkle / mark RTs and our clones. Check
  `Wrinkles.Owns`.
- **Bones, blendshapes and scales only through the trackers;** pulses (ripple `m_Force`) restore only if still ours.
- **Measure from her bind pose** (body creases, muscles on her bones); face regions from their idle level.
- **Preview renders before release** (`author.py --preview`, clay renders) catch line placement.
- **Short lines across a fold read as stitches** (0.11.41). A belly line below the navel, rising to the sides, reads as a caesarean
  scar. The fold belongs at the navel, drooping to the flanks.
- **New body sources (avatar, 0.11.42):** systems on hidden meshes show nothing. Route the shown renderers in.
