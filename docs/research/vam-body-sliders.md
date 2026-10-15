# VaM body-slider research: "2608 Claire Tentacle EXT" (ispinox)

Source: `ispinox.2608_Claire_Tentacle_EXT.2.var` (license PC = personal use). Read as untrusted data: unzipped with
`python -I` + zipfile into `...\Temp\claude\sfp\claire_pkg`, JSON parsed only, nothing executed. Numbers below were read
from the scene JSON and the packaged appearance preset; wording is my own. Purpose: find the DAZ Genesis 2 Female morphs
behind the Skin Tone / Breasts / Breast Under / Body Type / Fitness / Waist / Hip / Gloss sliders, for transfer to a game
character as blendshapes.

## 1. What drives the sliders

- The UI is NOT a Custom/Scripts plugin and NOT Timeline/LogicBricks. The package has no .cs/.cslist at all.
- It is one `Empty` atom named **"UI Morph Settings"** in the scene JSON
  (`Saves/scene/2026 08 2 Clair Tentacle/2608 Claire Tentacle EXT.json`) running the **VAMStory "Action" plugin**
  (`hazmhox.vamstory.41`, `vamstory_actions.cslist`). It builds a floating panel of 3 buttons, 8 "Slider Big" sliders
  (Act_3 .. Act_10) and 6 toggles. Each slider has a stored trigger ("Act_N-TriggerSlider") made of VaM trigger
  *transition actions*: each action says "receiver atom = Person, receiver storable = X, target = Y, start value, end value".
- Mapping is linear: slider 0 sets the target to its *start value*, slider 1 to its *end value*; in between it is a straight
  lerp (value = start + s * (end - start)). One slider can drive several targets, each with its own start/end (including
  reversed ranges, so a morph can fall as the slider rises).
- Every morph target lives on the `Person` atom, storable `geometry`, parameter name "morph: <Morph Name>".
- Slider defaults saved in the plugin (`Act_N-SliderDefaultValue`) match the morph values stored on the Person in the scene,
  so the sliders were applied at scene load.

### Slider to morph table (slider range 0..1; "->" = value at slider 0 to value at slider 1)

| Slider (Act) | Default s | Target (Person / storable) | Parameter | Range |
|---|---|---|---|---|
| Skin Tone (3) | 0.503 | `skin` | Diffuse Texture Offset | -0.05 -> +0.05 |
| Breasts (4) | 0.5 | `geometry` | Breast Zero | 0.4 -> 0 |
| | | | Breast Small | 0.7 -> 0 |
| | | | Breasts Small | 0.8 -> 0 |
| | | | Breast Large | 0 -> 0.5 |
| | | | Breasts Natural | 0 -> 0.8 |
| | | | Breasts TogetherApart | 0 -> 0.3 |
| | | | Breast Sag1 | 0 -> 0.546 |
| | | | Breasts Gone, Breasts Size, Breast Top Curve1/2, Breast Height, Breasts Diameter, Breast Under Smoother1/2/3, Breasts Shape 05/06/07 | pinned to 0 -> 0 (forces them off, no effect) |
| Breast Under (5) | 0 | `geometry` | Breast Height Lower | -0.2 -> +0.5 |
| Body Type (6) | 0 | `geometry` | Body Tone | 0 -> 1 |
| | | | (REN) Thin | 0 -> -0.5 |
| | | | Chubby 3 | 0 -> 0.4 |
| Fitness (7) | 0 | `geometry` | Bodybuilder Details | 0 -> 1 |
| | | | Torso Fitness | 0 -> 1 |
| | | | Fitness Size | 0 -> 1 |
| Waist (8) | 0.5 | `geometry` | Waist Width | -0.5 -> +0.5 |
| Hip (9) | 0.6 | `geometry` | Hip Size | 0 -> 0.7 |
| | | | Thighs Size | 0 -> 0.9 |
| | | | HipThighYoung | 0 -> 1 |
| Gloss (10) | 0.2 | `skin` | Specular Intensity | 1 -> 10 |

Notes on the table:
- "Breast Under" is mislabelled: it only moves Breast Height Lower (a lift/drop of the breast base). The real "Breast Under
  Smoother" morphs are held at zero by the Breasts slider.
- "Body Type" is a tone-and-leanness dial, not a muscle-size dial: Body Tone rises while a thin morph goes negative (so it
  leans out) and a chubby morph grows (adds soft mass). All three share one slider.
- "Fitness" is the muscle slider: three morphs move together at the same amplitude (0..1). Bodybuilder Details = muscle
  definition/size, Torso Fitness = torso-specific athletic shaping, Fitness Size = fitness-region volume (my reading of the
  names; not verified against the morph data).
- "Breasts" runs "small" morphs downward and "large/natural/sag" morphs upward, i.e. it blends from a small perky shape
  (slider 0) to a larger, more natural, slightly sagging shape (slider 1). Default 0.5 gives about Breast Zero 0.2, Breast
  Small 0.35, Breasts Small 0.4, Breast Large 0.25, Natural 0.4, TogetherApart 0.15, Sag1 0.27.
- Values at the scene default: Waist Width 0 (not stored), Hip Size 0.42, Thighs Size 0.54, HipThighYoung 0.6, Breast Height
  Lower -0.2, Body Tone / Thin / Chubby / Bodybuilder / Torso Fitness / Fitness Size all 0.
- The packaged *appearance preset* ("Reload Female Appearance" button, `Custom/Atom/Person/Appearance/Preset_2608 Claire
  Tentacle P1.vap`) holds a different, authored state, which back-solves to roughly: Body Type 0.71 (Body Tone 0.709, Thin
  -0.355, Chubby 3 0.284), Fitness 0.263 (Bodybuilder Details and Torso Fitness 0.263), Breasts 0.234, Waist 0.277, Hip 0.6.
  This is useful as a "fit athletic" example target set. (Fitness Size is absent from the preset.)
- Unrelated extra sliders in the scene: "UI Slider Pregnant" drives morph "Pregnant" 0..1; "UI Scale Slider Female" scales the
  whole Person 0.85..1.15; a jacket-sim stiffness slider exists. Not part of the body-slider panel.
- Toggles in the same panel (clothing/hair, not morphs): Necklace, Jacket, Bracelet, Holster (flip a material's Alpha Adjust
  between 0 and -1), and Pubic Small / Pubic Bush (swap hair-sim density/length settings).

## 2. Morph sources

- **No morph files are packed in this .var.** No .vmi/.vmb/.dsf and no `Custom/Atom/Person/Morphs` folder. Package contents:
  meta.json, 3 appearance/clothing/hair presets (+jpg), 4 spec-map jpgs, 22 mp3 voice/background sounds, 1 QR png, the scene
  json + jpg.
- All the slider morphs except Chubby 3 are referenced in the saved data by bare name (no `Package:/path.vmi` prefix), which
  in VaM means they are built-in Genesis 2 Female morphs (or loose files in the user's Custom folder), not packaged ones.
  Partly confirmed: the game's female morph bundle in the user's VaM install (`VaM_Data/StreamingAssets/f_mb`) contains
  internal names of the forms FBMBodyTone, FBMFitnessDetails, FBMBodybuilderDetails and BMWaistWidth, so Body Tone,
  Bodybuilder Details, Waist Width and the fitness family are built in. The other names (Torso Fitness, Fitness Size, Hip
  Size, Thighs Size, HipThighYoung, Breast Zero/Small/Large, Breasts Natural/Small, Breast Sag1, Breast Height Lower, Breasts
  TogetherApart, (REN) Thin) were not individually found (the bundle data is compressed, so a text search is incomplete).
  Treat them as built-in but verify in VaM's own Morphs UI (which shows each morph's region and file).
- The one non-built-in slider morph: **Chubby 3**, from external package `Azur.Chubby_3_Morph` (path
  `Custom/Atom/Person/Morphs/female/_Azur/1/Chubby 3.vmi`). It is a dependency, not packed here.
- Other morphs applied to this Person but NOT driven by any slider: Claire Redfield head/face morph (PixelPorts, value 1),
  AWW "Young" genitalia morph (1.38), many Azur detail morphs (belly, collarbone, elbow, hip/thigh folds, knee back,
  scapula, spine, armpit), Skynet BetterBends joint-correction morphs, MacGruber Life breathing morphs, plus built-in
  shoulder/forearm/wrist/ribcage/chest/navel/back detail values and expression/eyelid/finger morphs. A faithful clone of the
  character needs those; the sliders alone only change the morphs above. Static slider-adjacent values worth knowing: Forearms
  Size -0.5, Upper Arms Size -0.31, Shoulders Size -0.2, Shoulder Width (B) 0.43, Ribcage Size -0.23, Chest Height 0.24.
- Packed morph files: none, so no names/regions/sizes to list.

## 3. Skin Tone and Gloss

Both act on the Person's `skin` storable (the skin material), not on textures or colours:
- **Skin Tone** = skin param "Diffuse Texture Offset", range -0.05..+0.05 (default 0.0003). In VaM this is a small additive
  lightening/darkening of the diffuse (albedo) texture (my understanding of that parameter), not a hue change or texture swap.
  The skin colour itself is fixed at white with no tint (Skin Color h 0.116, s 0, v 1).
- **Gloss** = skin param "Specular Intensity", range 1..10 (default slider 0.2 gives 2.8).
- Fixed companion skin values, not moved by a slider: Specular Texture Offset -0.15, Gloss (skin param) 6, Gloss Texture
  Offset 0.5, Specular Fresnel 0.8, Diffuse Bumpiness 0.8, Specular Bumpiness 2.6, Specular Color white-ish (s 0), Subsurface
  Color pale red (s 0.25, v 1).
- Textures in use: Riddler "Skin 2 4K" diffuse and gloss maps (external), PixelPorts Claire normal map (external), and four
  specular maps packed in this var (`Textures/FemaleBase/SwFaceS / SwTorsoS / SwLimbsS / SwGenitalsS.jpg`, 0.28-1.66 MB each).
  No texture is swapped by any slider.
- For a game engine: tone is "albedo brightness offset +/-0.05", gloss is "specular multiplier 1..10 (default ~2.8)" plus a
  fixed gloss value; both are one-parameter material tweaks, not geometry.

## 4. Dependencies (meta.json, direct)

Timeline 291, Glance 23, SexyFluids 19, TittyMagic 70, ColliderEditor 38, OrificeDynamics 32 and 31, VAMMoan 23, SkinMicroDetail 8,
BetterBends 14, Freckles, PixelPorts Claire_Redfield_RE2_Remake 1 (needs AshAuryn Pose_Tools), Jackaroo New_Mouth_Female,
VL_13 Costume_O3_v2 (needs Stopper ClothingPluginManager 7), Skynet Eyes_Shadow_Reflection, StudioMF Perfect_eyelashes and
Primary_Specular, kemenate Female_Body_Hair, Theuf Leanahairstyle, AWWalker AWW_GenMorphResources, Azur Morphs_2026, MacGruber
Life 14 and PostMagic 4, Riddler Skin_2, VamLooks69 NipplesDecal, MonsterShinkai AnusDecals, BooMoon Lut_Pack, hazmhox VAMStory 41
and VAMOverlays 6 (needs MeshedVR PresetsPack), AcidBubbles Embody 59 and 61, ICannotDie LUT_Pack, Killer2u 118_LUTpack, Voithe
ActionsToggle 2, Stopper LightOptions 5 and CUAControllers 11, RainingMadness RE2InterrogationRoom (needs rernat
UnityAssetVamifier21), NoStage3 UnityAssetVamifier 20, Skynet Tentacle_Bundle_Demo_Scene_Final 3 (needs Timeline 287, CheesyFX
BodyLanguage 71 and Resources, Saking55 AutoBulger 7), Azur Chubby_3_Morph.

Only **hazmhox.vamstory.41** (the slider UI) and **Azur.Chubby_3_Morph** (one morph) matter for the body sliders; Azur.Morphs_2026
and Riddler.Skin_2 matter for the overall look. The rest are animation, fluids, voice, physics, props and post effects.

## 5. Practical takeaways for the game-character port

1. The "muscle / fitness / tonality" look = Genesis 2 Female built-ins **Body Tone**, **Bodybuilder Details**, **Torso Fitness**
   (plus **Fitness Size**); optionally pair Body Tone with **(REN) Thin** at -0.5x and **Chubby 3** at 0.4x as the scene does.
   A single 0..1 "Fitness" control that fans out to 3 morphs at equal weight is the scene's whole approach.
2. Waist and hip are linear fan-outs: Waist Width (-0.5..0.5) and Hip Size / Thighs Size / HipThighYoung (0..0.7 / 0..0.9 / 0..1).
3. Breast control mixes small and large morphs in opposite directions; to reproduce it, export Breast Zero, Breast Small,
   Breasts Small, Breast Large, Breasts Natural, Breasts TogetherApart, Breast Sag1 and Breast Height Lower.
4. Skin Tone and Gloss are material scalars, not morphs.
5. Nothing here is custom-authored geometry; the body morphs come from stock Genesis 2 Female data (only Chubby 3 needs the
   Azur .vmi).
