# Procedural skin wrinkles: method notes (for a port to Sex Formula / HDRP)

Inspiration: RidgeRock's ProceduralWrinkles 0.3.8 VaM plugin (CC BY-SA). This note describes its method in our own
words so we can reimplement it; no code from the package is used or should be copied (share-alike would apply).

## How it works
- **Strain, per triangle:** a rest frame per triangle (neutral pose with the character's shape, no pose); the live
  triangle gives a 2x2 deformation gradient; compression = max(0, 1 - sqrt(lambda_min)) of the Cauchy-Green tensor,
  its eigenvector (back in 3D) is the compression direction.
- **Per vertex:** area-weighted mean compression, and a 2x2 tensor of strain-weighted directions in the vertex's rest
  tangent frame; its principal axis is the vertex's compression axis.
- **Activation:** smoothstep of (compression - threshold) over ~0.18, a selectivity gate, strength, a face mask;
  temporal smoothing (attack ~0.08 s, release ~0.25 s). Axis hysteresis: a crease keeps its axis; a new axis
  (> 30 deg, held 0.12 s) fades the old crease out and the new one in.
- **Folds:** rest-space vertex patches (BFS, ~3 cm face / 3.5 cm body, normals agreeing), one origin per patch;
  phase = dot(restPos - patchOrigin, axis) / spacing (4 mm face, 8 mm body); ridges run across the compression axis;
  profile 0.45*sin(2*pi*p)*exp(-8*sin^2(pi*p)), faded by fwidth(phase) against aliasing. Optional crease joining
  (splines through neighbouring patches with aligned axes) for continuous lines.
- **Rendering:** a UV-space mesh (3 corner sub-patches per triangle forming a partition of unity) rasterised into a
  half-float slope texture (1024-2048); a full-screen pass adds the slope as a detail normal onto the base normal map
  and writes the result into the skin's normal map slot (mips regenerated). Per-triangle visibility fades, camera and
  distance culling (both VR eyes), everything suspended when idle.
- **Settings:** per region (face, neck, torso, arms, hands, legs) enabled, strength, threshold (~0.025), spacing,
  skin scale, selectivity; global attack/release, sample rate, culling, a recapture-neutral button.

## Port plan (Unity 2022.3 HDRP, her 'Shader Graphs/Skin' material)
1. Bake once: per-triangle rest frames, vertex-triangle adjacency, rest normals/tangent frames, patch origins,
   weld groups (UV seams), skin-region masks. Neutral = her bind pose WITH the active character's blendshapes and
   bone-slider scaling (else they read as strain).
2. Compute per frame (or 30 Hz) on SkinnedMeshRenderer.GetVertexBuffer (vertexBufferTarget |= Raw): triangle strain
   -> vertex gather + principal axis -> temporal smoothing and axis hysteresis (persistent buffers).
3. Rasterise a static UV-space mesh whose vertex shader reads those buffers into a slope RT (~2048, half float).
4. Blend base normal (the baked character normal from Morph.cs, or her original) + slope into a new RT, assign to
   _NormalMap (keep _NormalMap_2, the tiling detail); re-blend only on change.
5. Per-region controls, both-eye culling, face first.
Risks: _NormalMap encoding/linear-ness and override path in the Shader Graph; vertex buffer layout/order; DLSS
shimmer from fast changes; ~0.3-1 ms GPU, ~22 MB per 2048 RT. Effort: ~1 week face prototype, 2-3 weeks full.
