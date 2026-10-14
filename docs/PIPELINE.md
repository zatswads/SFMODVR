# Character pipeline: a VaM character's look on her

Personal use only (the base figure is DAZ's; VaM looks carry their creators' licences). Nothing converted
is committed or shared; only scripts live in this repo.

## Idea

Keep her (the game's mesh, rig, skinning, UVs, materials, clothes, physics, expressions, interior parts) and
transfer the **shape** and **skin textures** of a VaM character onto her:

1. **Her side** (done, SfMorph 0.5.0): Presets tab → "Export … for the character pipeline" writes her meshes in the
   game's vertex order with UVs, bone weights and the bind-pose skeleton (`BepInEx\config\SfMorphs\export\…`).
   Meshes the game shipped as GPU-only can't be read by a mod; the export and the log say which.
2. **VaM side** (written, compile-checked against VaM's Assembly-CSharp, not yet run): `tools/vam/SfExport.cs`, a
   VaM plugin on the Person atom. "Export character for SF" writes to `VaM\Saves\PluginData\SfExport\<atom>-<time>\`:
   `body.obj` (Genesis 2 mesh, every morph applied, unposed rest frame, UV vertices + UVs, a group per material,
   from VaM's own morph snapshot `DAZCharacterRun.doSnap`; the status line reports stretched faces, 0 expected),
   `joints.txt` (DAZ bones: morphed rest position = same frame as body.obj, and posed position), `materials.txt` + `tex_*.png` (the textures
   VaM renders with, per material slot), `urls.txt` (the appearance's original texture files), `thumb.jpg`, `info.txt`.

   Running it (first character: MonsterShinkai.Callie.2 with `Custom\Atom\Person\Appearance\Preset_callie.vap`):
   1. Copy `tools/vam/SfExport.cs` to `VaM\Custom\Scripts\SfMod\SfExport.cs`.
   2. The `.var` must be where VaM loads packages (`AddonPackages`), as for normal play with that look.
   3. Add a Person, Appearance → Presets → load `Preset_callie`.
   4. Person → Plugins → Add Plugin → Select File → `Custom/Scripts/SfMod/SfExport.cs` → Open Custom UI →
      "Export character for SF". The status line and VaM's message log say where it wrote.
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
