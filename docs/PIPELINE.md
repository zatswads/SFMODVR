# Character pipeline: a VaM character's look on her

Personal use only (the base figure is DAZ's; VaM looks carry their creators' licences). Nothing converted
is committed or shared; only scripts live in this repo.

## Idea

Keep her (the game's mesh, rig, skinning, UVs, materials, clothes, physics, expressions, interior parts) and
transfer the **shape** and **skin textures** of a VaM character onto her:

1. **Her side** (done, SfMorph 0.5.0): Presets tab → "Export … for the character pipeline" writes her meshes in the
   game's vertex order with UVs, bone weights and the bind-pose skeleton (`BepInEx\config\SfMorphs\export\…`).
   Meshes the game shipped as GPU-only can't be read by a mod; the export and the log say which.
2. **VaM side** (to write): a VaM plugin (`tools/vam/SfExport.cs`, added to the Person atom) writes the character
   as VaM shows it, with every morph applied, in a neutral pose: body/face mesh (Genesis 2 Female topology), UVs,
   joint positions, the skin texture files, and a portrait thumbnail. Needs VaM's
   `VaM_Data\Managed\Assembly-CSharp.dll` to write it against VaM's real API.
3. **Fit** (to write, headless Blender, `tools/pipeline/`): pose-match the two (joints), landmark-guided non-rigid
   fit of her surface onto the VaM surface (the job Wrap3D does in VaM porting tutorials, done here with numpy so
   no Wrap licence is needed), save per-vertex offsets for each of her meshes **in her vertex order** (never change
   her vertex count or order: the same rule as "keep vertex order" in VaM porting), new joint positions, and
   VaM's skin textures baked onto her UV layout (Blender bake, selected to active).
4. **Load** (to write, SfMorph): each converted character is a card in a **Characters gallery** (thumbnail + name).
   Selecting one adds its shape as real blendshapes (runtime `Mesh.AddBlendShapeFrame` on copies of her meshes,
   a 0–100% slider, so the bone sliders still work on top), moves the joints, carries the shape over to her clothes
   (nearest body vertex) so they don't clip, and loads the baked textures into her materials.

## Face sources

Anything that ends up as a VaM character works: VaM looks, FaceGen (photos or AI character sheets → FaceGen →
Genesis 2 morph → VaM), or sculpts. Front + side character-sheet views are what FaceGen's photo fit uses.

## Where to run it

- **Local Claude Code session on the PC (recommended)**: VaM, the game and Blender are already there; nothing to upload.
  Needs Blender 4.2+ installed, Python, and this repo cloned. Prompt to start it:

  > Continue the SF Body Morphs character pipeline in this repo (read CLAUDE.md, docs/HANDOFF.md, docs/PIPELINE.md).
  > VaM is at `E:\Bak\New folder\Misc\virt\vam`, the game at `E:\Bak\New folder\Misc\SF\VR`, Blender at `<path>`.
  > Use the newest export in `BepInEx\config\SfMorphs\export` and the VaM export of `<character>`. Run the fit with
  > Blender in the background, check the result yourself (vertex counts/order unchanged, offsets sane, renders of
  > before/after), and only hand it to me when it passes. Commit scripts only, never game or VaM content.

- **Cloud session**: works too (Blender runs here as the `bpy` module); upload the two export folders as zips.
