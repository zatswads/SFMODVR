using System;
using System.Collections.Generic;
using UnityEngine;

namespace SfMorph
{
    // 0.8.0 contact visuals v2 settings, filled by Body from [Physics] every frame and handed to GpuContact, BendPose, Ripple and SkinMarks.
    internal class ContactSettings
    {
        public float Softness = 0.5f;
        public float Skirt = 0.015f, Bulge = 0.6f, Depth = 1f;
        public bool Normals = true, SelfContact = true, PartnerBody = true;
        public float BendAmount = 0.6f, BicepsMm = 8f, KneeMm = 8f, CalfMm = 6f, HipMm = 5f, BellyFoldMm = 5f;
        public float Ripple = 0.6f, RippleSpeed = 1f, RippleWavelength = 0.07f, RippleDamping = 0.3f;
        public bool Marks = true;
        public float Redness = 0.5f, MarkFade = 15f, MarkGain = 0.6f, Ao = 0.5f;
    }

    // 0.8.0: what GpuContact's v2 field chain needs per renderer, baked on the CPU once per soft-zones build (design 1.2): welded vertices,
    // their adjacency, a bone class and tissue caps per vertex, bend weights, UVs and triangles for SkinMarks. Build never throws:
    // null + error = GpuContact keeps the v1 Imprint for that renderer.
    internal sealed class ContactBake
    {
        public SkinnedMeshRenderer R;
        public Mesh Mesh;
        public int V;                 // vertex count of Mesh
        public int CanonCount;        // number of welded representatives (canonical ids are vertex indices: Canon[v] = the first copy's index)
        public uint[] Canon;          // V: welded representative
        public uint[] AdjOff, AdjV;   // CSR over vertex ids (rows filled for canonical ids only): unique canonical neighbours
        public Vector2[] Tissue;      // V: x = hard depth cap (m) from the bone class, y = zone share 0..1 (SoftZones.ZoneFor; 0 when null)
        public uint[] Class;          // V: bone class id (design table 1.3); 0 = none
        public Vector4[] Bend;        // V: (j0, w0, j1, w1): BendPose slot 0..8 (-1 none), signed weight (+ outward bulge, - inward fold)
        public Vector2[] Uv;          // V: UV0 (SkinMarks' raster); null when the mesh has none
        public int[][] SubTris;       // per submesh triangle index list
        public float AvgEdge;         // mean rest edge length (m)
        public float MetresPerUv;     // sqrt(sum 3D triangle area / sum UV triangle area)
        public string Note = "";

        public static ContactBake Build(SkinnedMeshRenderer r, float[] zone, Character her, out string error)
        {
            error = "not implemented";
            return null;
        }
    }
}
