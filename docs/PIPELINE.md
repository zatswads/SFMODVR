# Character pipeline: a VaM character's look on her

Personal use only (the base figure is DAZ's; VaM looks carry their creators' licences). Nothing converted
is committed or shared; only scripts live in this repo.

## Idea

Keep her (the game's mesh, rig, skinning, UVs, materials, clothes, physics, expressions, interior parts) and
transfer the **shape** and **skin textures** of a VaM character onto her:

1. **Her side** (SfMorph 0.5.1): Presets tab → "Export … for the character pipeline" writes her meshes in the
   game's vertex order with UVs, bone weights and the bind-pose skeleton (`BepInEx\config\SfMorphs\export\…`).
   All 36 of her meshes are GPU-only (0.5.0 exported nothing), so `MeshData` reads their vertex and index buffers
   back from the GPU and decodes them with the mesh's vertex layout; `meshes.txt` says CPU or GPU per mesh.
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
3. **Fit** (`tools/pipeline/`, done in 0.6.0). One command:

   ```
   powershell -ExecutionPolicy Bypass -File tools\pipelineun.ps1 -Name Yin -Game "<game>" -Vam "<VaM>" -Blender "<blender.exe>"
   ```

   Paths are remembered (`%LOCALAPPDATA%\SfMorph\pipeline.json`); later runs need only `-Name`. It takes the newest
   export on each side, installs scipy for Blender's Python once (into `%LOCALAPPDATA%\SfMorph\pydeps`, never into
   Blender), and writes `<game>\BepInEx\config\SfMorphs\morphs\<Name>\` in about a minute and a half:
   - `sfpipe.py` (Blender's Python, numpy/scipy): her meshes into one bind space (the right eye's iris/sclera are
     copies of the left eye placed by their own bind pose); the VaM figure scaled to her eye height; her skeleton
     posed into VaM's rest pose by matching limb directions (not the spine: the two rigs put spine joints in
     different places) and skinned with her weights; a thin-plate warp on anatomical joint centres, nipple tips and
     the mouth; a non-rigid ICP of her welded skin (upper/lower body, feet, neck, face) onto the VaM skin,
     restricted by body part (fingers can't grab fingers, upper lip can't grab lower lip), matches only along the
     normal, stiffer on fingers, toes and around the eyes, eyelids keep her own opening; spring relax on the trunk;
     unposed with the same blended skin matrices; folds untangled. Everything else follows: overlays (nipple discs,
     nails, decals) stay on the skin surface, lash strands move with their skin patch, eyes/teeth/tongue rigidly,
     the rest (mouth, hair, interior) smoothly. Output: `<mesh>.morph.txt` (position and normal deltas, her vertex
     order), `joints.txt`, `character.txt`, `thumb.jpg`, `_checkeport.txt`.
   - `blend.py` + `bake.py` (Blender, Cycles on the GPU): before/after renders in `_check\` and VaM's skin baked
     onto her UVs (selected to active, albedo as emission, tangent normals; VaM's DXT5nm normal maps unswizzled):
     `tex_reika_body_*`, `tex_reika_face_a_*`, `tex_reika_foot_*`.
   - Check `_checkeport.txt`: vertex counts, offsets, flipped triangles and stretch per mesh, fit residual both ways.

4. **Load** (SfMorph 0.6.0): Characters tab, see README.

Known limits: the fit runs in VaM's T-pose and is unposed with her linear skinning, so her shoulders in the
A-pose look a little narrower than VaM's; a few creases remain where her shape differs most (under a bigger
breast, the navel, between toes). Her eyelid shape stays hers.

## Face sources

Anything that ends up as a VaM character works: VaM looks, FaceGen (photos or AI character sheets → FaceGen →
Genesis 2 morph → VaM), or sculpts. Front + side character-sheet views are what FaceGen's photo fit uses.

## Where to run it

- **Local Claude Code session on the PC (recommended; the VaM export with textures is ~250 MB)**: VaM, the game and
  Blender are already there. Needs Claude Code, git, Blender 4.2+ (its Python gets numpy; add scipy with its pip) and
  this repo cloned. Prompt to start it:

  > Continue SF Body Morphs' character pipeline in this repo: read CLAUDE.md, docs/HANDOFF.md, docs/PIPELINE.md and
  > .claude/skills/sfmorph-dev/SKILL.md first. Paths: game `E:\Bak\New folder\Misc\SF\VR`, VaM
  > `E:\Bak\New folder\Misc\virt\vam`, Blender `<path to blender.exe>`. Inputs: the newest VaM export in
  > `<VaM>\Saves\PluginData\SfExport\`  and the newest game export in
  > `<game>\BepInEx\config\SfMorphs\export\` (SfMorph 0.5.1+; if its meshes.txt lists no meshes, stop and tell me).
  > 1) Write `tools/pipeline/` (Blender in background + numpy/scipy): read both exports; map her AdvancedSkeleton joints to
  > Genesis 2 bones; pose-match her bind pose to VaM's rest pose using her skin weights; landmark-guided non-rigid fit of
  > her skin meshes onto Callie's body.obj; write per-vertex offsets in her exact vertex order for each mesh plus joint
  > offsets; bake Callie's textures onto her UVs. 2) Verify it yourself before showing me: vertex counts and order
  > unchanged, no flipped or stretched triangles, offsets sane, before/after renders. 3) Then add SfMorph's runtime loader
  > and Characters gallery (readable clones of her meshes built from MeshData, AddBlendShapeFrame, a 0-100% slider,
  > clothes follow by nearest body vertex, baked textures). Build with `dotnet build src/SfMorph -c Release
  > -p:GameDir="<game>"`, bump the version, README entry, commit and push to main. Never commit game or VaM content.
  > Keep replies short and tell me exactly what to test in the headset.

- **Cloud session**: works too (Blender runs here as the `bpy` module); upload the two export folders as zips.
