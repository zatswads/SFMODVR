using System;
using UnityEngine;

namespace SfMorph
{
    // One renderer's wrinkle data, baked on the CPU (0.7.4). This layout is the contract with Wrinkles.compute /
    // WrinkleRaster.shader (see design-0.7.4.md section 3). (Interface stub.)
    internal class BakeResult
    {
        public SkinnedMeshRenderer R;
        public Mesh Mesh;              // the mesh baked (the one the renderer showed)
        public Material Mat;           // the material whose normal slot this renderer feeds (its sheet)
        public int Submesh;            // which submesh/material slot of R
        public int V, T;               // vertex count, triangle count (of the baked submesh's triangles, indices into V)
        public int[] Tris;             // 3*T vertex indices
        public Vector2[] Uv;           // V (UV0)
        public uint[] Canon;           // V: canonical (welded) vertex id per vertex (a canonical vertex maps to itself)
        public uint[] AdjOff;          // V+1: CSR offsets over canonical ids into AdjTri (empty range for non-canonical ids)
        public uint[] AdjTri;          // triangles adjacent to each canonical vertex (across weld groups)
        public float[] RestTri;        // 20 floats per triangle: DmInv(2x2: 4), area(1), t(3), b(3), J(2x3 row-major: 6), pad(3)
        public float[] RestV;          // 12 floats per vertex: restPos(3), patchOrigin(3), patchRefDir(3), mask(1), spacing m(1), pad(1)
        public int PosOff, NrmOff;     // stream-0 byte offsets of Position/Normal in the mesh's layout
        public bool Face;              // face sheet (else neck/body)
        public string Note = "";       // one-line stats for the log
    }

    internal static class WrinkleBake
    {
        // Full bake of renderer r (submesh sub) against the neutral pose (bind pose + active blendshapes + bone scale).
        public static BakeResult Bake(SkinnedMeshRenderer r, int sub, Character her, bool face, out string error) { error = "stub"; return null; }
        // Recapture only: rest frames and patch origins against the current neutral (same topology).
        public static bool Recapture(BakeResult b, Character her, out string error) { error = "stub"; return false; }
    }
}
