using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;

namespace SfMorph
{
    // One renderer's wrinkle data, baked on the CPU (0.7.4). This layout is the contract with Wrinkles.compute /
    // WrinkleRaster.shader (see design-0.7.4.md section 3). All 3D data is in the mesh's own (bind) space.
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

        internal WrinkleBake.Aux Aux;  // bake-private (patch seeds for Recapture); not part of the contract
    }

    // CPU bake of the wrinkle data (0.7.4, design section 2). Neutral pose = the renderer's bind pose with its active blendshapes
    // at their current weights and the body-slider bone scales (Body.ScaleOf); no animation. Nothing here throws: failures come
    // back as an error string.
    internal static class WrinkleBake
    {
        // Wrinkle fold spacing in metres, written into RestV (set from the config before Bake / Recapture; a Recapture refreshes it).
        public static float SpacingFace = 0.004f, SpacingBody = 0.008f;

        const double WeldQuantum = 1e-5;                // rest positions closer than this (per axis, quantised) are one vertex
        const float PatchRadiusFace = 0.03f, PatchRadiusBody = 0.035f;
        const float PatchNormalAgree = 0.7f;            // dot(normal, patch seed normal) must exceed this
        const float MaskEyelid = 0f, MaskLip = 0.5f;
        const int RtStride = 20, RvStride = 12;

        internal class Aux
        {
            public float[] Mask;          // V, region mask (weld-group minimum)
            public int[] PatchOf;         // V, patch id or -1 (vertices of no triangle)
            public int[] PatchSeed;       // canonical vertex id of each patch's seed
            public int[] PatchTri;        // first triangle adjacent to the seed
        }

        class Src
        {
            public Vector3[] P;
            public Vector2[] Uv;
            public BoneWeight[] W;
            public Matrix4x4[] Bind;
            public int[] Tri;
        }

        // GPU-only meshes are immutable: read them back once per Mesh. Readable ones (morph clones) can change in place: always re-read.
        static readonly Dictionary<Mesh, MeshData> GpuCache = new Dictionary<Mesh, MeshData>();

        // ---- public API ----

        // Full bake of renderer r (submesh sub) against the neutral pose (bind pose + active blendshapes + bone scale).
        public static BakeResult Bake(SkinnedMeshRenderer r, int sub, Character her, bool face, out string error)
        {
            error = null;
            try
            {
                var sw = Stopwatch.StartNew();
                if (r == null) { error = "no renderer"; return null; }
                var m = r.sharedMesh;
                if (m == null) { error = "renderer has no mesh"; return null; }
                var mats = r.sharedMaterials;
                if (sub < 0 || sub >= mats.Length || mats[sub] == null) { error = "no material in slot " + sub; return null; }
                int posOff, nrmOff;
                error = ReadLayout(m, out posOff, out nrmOff);
                if (error != null) return null;
                Src s = ReadSrc(m, sub, out error);
                if (s == null) return null;
                int V = s.P.Length, T = s.Tri.Length / 3;
                for (int i = 0; i < s.Tri.Length; i++)
                    if (s.Tri[i] < 0 || s.Tri[i] >= V) { error = "triangle index " + s.Tri[i] + " out of range (" + V + " vertices)"; return null; }

                string bsNote;
                Vector3[] N = Neutral(r, m, s, her, out bsNote);

                var b = new BakeResult
                {
                    R = r, Mesh = m, Mat = mats[sub], Submesh = sub, V = V, T = T, Tris = s.Tri, Uv = s.Uv,
                    PosOff = posOff, NrmOff = nrmOff, Face = face
                };

                // weld groups by quantised rest position
                var canon = new uint[V];
                var seen = new Dictionary<ulong, int>(V);
                int groups = 0;
                for (int i = 0; i < V; i++)
                {
                    ulong key = Quant(N[i]);
                    int first;
                    if (seen.TryGetValue(key, out first)) canon[i] = (uint)first;
                    else { seen[key] = i; canon[i] = (uint)i; groups++; }
                }
                b.Canon = canon;

                // CSR adjacency over canonical ids
                var off = new uint[V + 1];
                for (int t = 0; t < T; t++)
                {
                    uint c0 = canon[s.Tri[3 * t]], c1 = canon[s.Tri[3 * t + 1]], c2 = canon[s.Tri[3 * t + 2]];
                    off[c0 + 1]++;
                    if (c1 != c0) off[c1 + 1]++;
                    if (c2 != c0 && c2 != c1) off[c2 + 1]++;
                }
                for (int i = 0; i < V; i++) off[i + 1] += off[i];
                var adj = new uint[off[V]];
                var cur = new uint[V];
                Array.Copy(off, cur, V);
                for (int t = 0; t < T; t++)
                {
                    uint c0 = canon[s.Tri[3 * t]], c1 = canon[s.Tri[3 * t + 1]], c2 = canon[s.Tri[3 * t + 2]];
                    adj[cur[c0]++] = (uint)t;
                    if (c1 != c0) adj[cur[c1]++] = (uint)t;
                    if (c2 != c0 && c2 != c1) adj[cur[c2]++] = (uint)t;
                }
                b.AdjOff = off;
                b.AdjTri = adj;

                // per-triangle rest basis, inverse rest edge matrix, area, UV Jacobian
                b.RestTri = new float[T * RtStride];
                FillTriangles(b, N);
                Vector3[] nC = CanonNormals(b);

                // patches: BFS over canonical adjacency in rest space
                var aux = new Aux { Mask = RegionMask(r, s, canon, face) };
                BuildPatches(b, aux, N, nC, face ? PatchRadiusFace : PatchRadiusBody);
                b.Aux = aux;

                b.RestV = new float[V * RvStride];
                FillRestV(b, N, nC);

                sw.Stop();
                b.Note = "V " + V + " T " + T + ", " + aux.PatchSeed.Length + " patches, " + (V - groups) + " welded, " + sw.ElapsedMilliseconds + " ms"
                    + (bsNote.Length > 0 ? ", " + bsNote : "");
                return b;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }

        // Recapture only: rest frames and patch origins against the current neutral (same topology). Weld groups, adjacency, patch
        // membership and masks are kept; RestTri, RestV[0..8] (rest position, patch origin/ref dir) and spacing are rewritten in place.
        public static bool Recapture(BakeResult b, Character her, out string error)
        {
            error = null;
            try
            {
                if (b == null || b.Aux == null) { error = "no bake"; return false; }
                var r = b.R;
                if (r == null) { error = "renderer gone"; return false; }
                var m = r.sharedMesh;
                if (m == null || m != b.Mesh) { error = "mesh changed"; return false; }
                Src s = ReadSrc(m, b.Submesh, out error);
                if (s == null) return false;
                if (s.P.Length != b.V || s.Tri.Length != b.T * 3) { error = "topology changed (" + s.P.Length + "/" + (s.Tri.Length / 3) + " vs " + b.V + "/" + b.T + ")"; return false; }
                string bsNote;
                Vector3[] N = Neutral(r, m, s, her, out bsNote);
                FillTriangles(b, N);
                Vector3[] nC = CanonNormals(b);
                FillRestV(b, N, nC);
                return true;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        // ---- mesh data ----

        // Position and Normal Float32x3 on stream 0 (what the compute shader reads from the skinned buffer); null = fine, else the reason.
        static string ReadLayout(Mesh m, out int posOff, out int nrmOff)
        {
            posOff = nrmOff = 0;
            if (!m.HasVertexAttribute(VertexAttribute.Position) || !m.HasVertexAttribute(VertexAttribute.Normal)) return "mesh has no position/normal";
            string w = CheckAttr(m, VertexAttribute.Position) ?? CheckAttr(m, VertexAttribute.Normal);
            if (w != null) return w;
            posOff = m.GetVertexAttributeOffset(VertexAttribute.Position);
            nrmOff = m.GetVertexAttributeOffset(VertexAttribute.Normal);
            return null;
        }

        static string CheckAttr(Mesh m, VertexAttribute a)
        {
            if (m.GetVertexAttributeStream(a) != 0) return a + " not on stream 0";
            if (m.GetVertexAttributeFormat(a) != VertexAttributeFormat.Float32 || m.GetVertexAttributeDimension(a) != 3) return a + " is not Float32x3";
            return null;
        }

        static Src ReadSrc(Mesh m, int sub, out string error)
        {
            error = null;
            var s = new Src();
            if (m.isReadable)
            {
                if (sub >= m.subMeshCount) { error = "submesh " + sub + " out of range"; return null; }
                if (m.GetTopology(sub) != MeshTopology.Triangles) { error = "submesh " + sub + " is not triangles"; return null; }
                s.P = m.vertices; s.Uv = m.uv; s.W = m.boneWeights; s.Bind = m.bindposes; s.Tri = m.GetTriangles(sub);
            }
            else
            {
                MeshData d;
                if (!GpuCache.TryGetValue(m, out d) || d.Pos == null || d.Pos.Length != m.vertexCount)
                {
                    foreach (var k in new List<Mesh>(GpuCache.Keys)) if (k == null) GpuCache.Remove(k);
                    string e;
                    d = MeshData.Read(m, out e);
                    if (d == null) { error = "mesh read back failed: " + e; return null; }
                    GpuCache[m] = d;
                }
                if (sub >= d.Sub.Count) { error = "submesh " + sub + " out of range"; return null; }
                s.P = d.Pos; s.Uv = d.Uv; s.W = d.Weights; s.Bind = d.Bind; s.Tri = d.Sub[sub];
            }
            int V = s.P != null ? s.P.Length : 0;
            if (V == 0 || V != m.vertexCount) { error = "vertex count mismatch (" + V + " vs " + m.vertexCount + ")"; return null; }
            if (s.Uv == null || s.Uv.Length != V) { error = "no UV0"; return null; }
            if (s.W == null || s.W.Length != V || s.Bind == null || s.Bind.Length == 0) { error = "no bone weights/bind poses"; return null; }
            if (s.Tri == null || s.Tri.Length < 3 || s.Tri.Length % 3 != 0) { error = "submesh " + sub + " has no triangles"; return null; }
            return s;
        }

        // ---- neutral pose ----

        // N(v) = sum_b w_b * inv(bind_b) * S_b * bind_b * (v + sum_k delta_k(v) * weight_k), S_b = the bone's current scale relative to rest
        // (compounded along her chain). Joint offsets are ignored.
        static Vector3[] Neutral(SkinnedMeshRenderer r, Mesh m, Src s, Character her, out string note)
        {
            note = "";
            int V = s.P.Length;
            var pos = (Vector3[])s.P.Clone();

            // active blendshapes (piecewise linear between the frames, as Unity evaluates them)
            int bsActive = 0;
            string bsFail = null;
            int shapes = m.blendShapeCount;
            if (shapes > 0)
            {
                Vector3[] dv0 = null, dv1 = null;
                for (int k = 0; k < shapes; k++)
                {
                    float w = r.GetBlendShapeWeight(k);
                    if (Mathf.Abs(w) < 1e-3f) continue;
                    int fc = m.GetBlendShapeFrameCount(k);
                    if (fc <= 0) continue;
                    try
                    {
                        if (dv0 == null) { dv0 = new Vector3[V]; dv1 = new Vector3[V]; }
                        // frame i (weights ascending): below the first, scale it from 0; between two, lerp; above the last, extrapolate from it
                        int hi = 0;
                        while (hi < fc - 1 && w > m.GetBlendShapeFrameWeight(k, hi)) hi++;
                        float fwHi = m.GetBlendShapeFrameWeight(k, hi);
                        if (hi == 0 || fc == 1)
                        {
                            m.GetBlendShapeFrameVertices(k, hi, dv0, null, null);
                            float f = Mathf.Abs(fwHi) > 1e-4f ? w / fwHi : 0f;
                            for (int i = 0; i < V; i++) pos[i] += dv0[i] * f;
                        }
                        else
                        {
                            float fwLo = m.GetBlendShapeFrameWeight(k, hi - 1);
                            float f = Mathf.Abs(fwHi - fwLo) > 1e-4f ? (w - fwLo) / (fwHi - fwLo) : 1f;
                            m.GetBlendShapeFrameVertices(k, hi - 1, dv0, null, null);
                            m.GetBlendShapeFrameVertices(k, hi, dv1, null, null);
                            for (int i = 0; i < V; i++) pos[i] += Vector3.LerpUnclamped(dv0[i], dv1[i], f);
                        }
                        bsActive++;
                    }
                    catch (Exception e) { bsFail = e.GetType().Name; break; }
                }
            }
            if (bsFail != null) note = "blendshapes unreadable (" + bsFail + ")";
            else if (bsActive > 0) note = bsActive + " blendshapes";

            // bone scales
            var bones = r.bones;
            int nb = s.Bind.Length;
            var M = new Matrix4x4[nb];
            var has = new bool[nb];
            bool any = false;
            Transform root = her != null ? her.Root : null;
            for (int i = 0; i < nb; i++)
            {
                Transform t = i < bones.Length ? bones[i] : null;
                if (t == null) continue;
                Vector3 f = Body.ScaleOf(t, root);
                if (Mathf.Abs(f.x - 1f) < 1e-4f && Mathf.Abs(f.y - 1f) < 1e-4f && Mathf.Abs(f.z - 1f) < 1e-4f) continue;
                var bp = s.Bind[i];
                if (Mathf.Abs(bp.determinant) < 1e-12f) continue;
                M[i] = bp.inverse * Matrix4x4.Scale(f) * bp;
                has[i] = true; any = true;
            }
            if (!any) return pos;
            var N = new Vector3[V];
            for (int v = 0; v < V; v++)
            {
                var w = s.W[v];
                Vector3 p = pos[v], acc = Vector3.zero;
                float sum = 0f;
                Acc(ref acc, ref sum, M, has, w.boneIndex0, w.weight0, p);
                Acc(ref acc, ref sum, M, has, w.boneIndex1, w.weight1, p);
                Acc(ref acc, ref sum, M, has, w.boneIndex2, w.weight2, p);
                Acc(ref acc, ref sum, M, has, w.boneIndex3, w.weight3, p);
                N[v] = sum > 1e-6f ? acc / sum : p;
            }
            return N;
        }

        static void Acc(ref Vector3 acc, ref float sum, Matrix4x4[] M, bool[] has, int bone, float wt, Vector3 p)
        {
            if (wt <= 0f || bone < 0 || bone >= M.Length) return;
            acc += (has[bone] ? M[bone].MultiplyPoint3x4(p) : p) * wt;
            sum += wt;
        }

        // ---- weld ----

        static ulong Quant(Vector3 p)
        {
            return Q(p.x) | (Q(p.y) << 21) | (Q(p.z) << 42);
        }

        static ulong Q(float v)
        {
            long q = (long)Math.Floor(v / WeldQuantum + 0.5) + (1 << 20);
            if (q < 0) q = 0; else if (q > (1 << 21) - 1) q = (1 << 21) - 1;
            return (ulong)q;
        }

        // ---- per-triangle rest data ----

        // DmInv is row-major [[1/|e1|, -(e2.t)/(|e1| e2.b)], [0, 1/e2.b]] with Dm = [e1 e2] as columns in the (t,b) basis (e1 = (|e1|,0),
        // e2 = (e2.t, e2.b)); J = Duv * DmInv * [t; b]. Degenerate triangles are all zeros (area 0 = ignore).
        static void FillTriangles(BakeResult b, Vector3[] N)
        {
            var tri = b.Tris;
            var uv = b.Uv;
            var rt = b.RestTri;
            for (int t = 0; t < b.T; t++)
            {
                int o = t * RtStride;
                for (int k = 0; k < RtStride; k++) rt[o + k] = 0f;
                int ia = tri[3 * t], ib = tri[3 * t + 1], ic = tri[3 * t + 2];
                Vector3 p0 = N[ia], e1 = N[ib] - p0, e2 = N[ic] - p0;
                Vector3 n = Vector3.Cross(e1, e2);
                float len = n.magnitude, l1 = e1.magnitude;
                if (!(len > 2e-12f) || !(l1 > 1e-7f)) continue;
                Vector3 tv = e1 / l1, nn = n / len, bv = Vector3.Cross(nn, tv);
                float e2t = Vector3.Dot(e2, tv), e2b = Vector3.Dot(e2, bv);
                if (!(Mathf.Abs(e2b) > 1e-8f)) continue;
                float m00 = 1f / l1, m01 = -e2t / (l1 * e2b), m10 = 0f, m11 = 1f / e2b;
                Vector2 d1 = uv[ib] - uv[ia], d2 = uv[ic] - uv[ia];
                float a00 = d1.x * m00 + d2.x * m10, a01 = d1.x * m01 + d2.x * m11;
                float a10 = d1.y * m00 + d2.y * m10, a11 = d1.y * m01 + d2.y * m11;
                Vector3 ju = a00 * tv + a01 * bv, jv = a10 * tv + a11 * bv;
                if (float.IsNaN(ju.x + ju.y + ju.z + jv.x + jv.y + jv.z) || float.IsInfinity(ju.x + ju.y + ju.z + jv.x + jv.y + jv.z)) continue;
                rt[o] = m00; rt[o + 1] = m01; rt[o + 2] = m10; rt[o + 3] = m11;
                rt[o + 4] = 0.5f * len;
                rt[o + 5] = tv.x; rt[o + 6] = tv.y; rt[o + 7] = tv.z;
                rt[o + 8] = bv.x; rt[o + 9] = bv.y; rt[o + 10] = bv.z;
                rt[o + 11] = ju.x; rt[o + 12] = ju.y; rt[o + 13] = ju.z;
                rt[o + 14] = jv.x; rt[o + 15] = jv.y; rt[o + 16] = jv.z;
            }
        }

        // Area-weighted rest normal per canonical vertex (zero for vertices of no triangle and for non-canonical ids).
        static Vector3[] CanonNormals(BakeResult b)
        {
            var nC = new Vector3[b.V];
            var rt = b.RestTri;
            var off = b.AdjOff;
            var adj = b.AdjTri;
            for (int c = 0; c < b.V; c++)
            {
                Vector3 sum = Vector3.zero;
                for (uint i = off[c]; i < off[c + 1]; i++)
                {
                    int o = (int)adj[i] * RtStride;
                    float area = rt[o + 4];
                    if (area <= 0f) continue;
                    // t x b = the triangle normal
                    var tv = new Vector3(rt[o + 5], rt[o + 6], rt[o + 7]);
                    var bv = new Vector3(rt[o + 8], rt[o + 9], rt[o + 10]);
                    sum += Vector3.Cross(tv, bv) * area;
                }
                nC[c] = sum.sqrMagnitude > 1e-20f ? sum.normalized : Vector3.zero;
            }
            return nC;
        }

        // ---- region masks ----

        // Face sheet: 1, but vertices weighted > 0.5 to Eyelid* bones -> 0 (blinks would flicker) and to Lip* bones -> 0.5. Body sheets: 1.
        // The weld group takes the minimum; vertices of no triangle get 0.
        static float[] RegionMask(SkinnedMeshRenderer r, Src s, uint[] canon, bool face)
        {
            int V = s.P.Length;
            var mask = new float[V];
            for (int i = 0; i < V; i++) mask[i] = 1f;
            if (face)
            {
                var bones = r.bones;
                int nb = s.Bind.Length;
                var kind = new byte[nb];   // 1 = lid, 2 = lip
                bool any = false;
                for (int i = 0; i < nb && i < bones.Length; i++)
                {
                    if (bones[i] == null) continue;
                    string n = bones[i].name;
                    if (n.StartsWith("Eyelid", StringComparison.OrdinalIgnoreCase)) { kind[i] = 1; any = true; }
                    else if (n.StartsWith("Lip", StringComparison.OrdinalIgnoreCase)) { kind[i] = 2; any = true; }
                }
                if (any)
                    for (int v = 0; v < V; v++)
                    {
                        var w = s.W[v];
                        float lid = 0f, lip = 0f;
                        Kind(kind, w.boneIndex0, w.weight0, ref lid, ref lip);
                        Kind(kind, w.boneIndex1, w.weight1, ref lid, ref lip);
                        Kind(kind, w.boneIndex2, w.weight2, ref lid, ref lip);
                        Kind(kind, w.boneIndex3, w.weight3, ref lid, ref lip);
                        if (lid > 0.5f) mask[v] = MaskEyelid;
                        else if (lip > 0.5f) mask[v] = MaskLip;
                    }
            }
            var groupMin = new float[V];
            for (int i = 0; i < V; i++) groupMin[i] = 1f;
            for (int i = 0; i < V; i++) { int c = (int)canon[i]; if (mask[i] < groupMin[c]) groupMin[c] = mask[i]; }
            for (int i = 0; i < V; i++) mask[i] = groupMin[(int)canon[i]];
            return mask;
        }

        static void Kind(byte[] kind, int bone, float wt, ref float lid, ref float lip)
        {
            if (wt <= 0f || bone < 0 || bone >= kind.Length) return;
            if (kind[bone] == 1) lid += wt; else if (kind[bone] == 2) lip += wt;
        }

        // ---- patches ----

        // Greedy patches over canonical vertices (seed = lowest unassigned id): BFS through triangle neighbours, accepting a vertex within
        // the radius of the seed (rest space) whose normal agrees with the seed's (dot > 0.7). Every vertex of a triangle gets a patch.
        static void BuildPatches(BakeResult b, Aux aux, Vector3[] N, Vector3[] nC, float radius)
        {
            int V = b.V;
            var canon = b.Canon;
            var off = b.AdjOff;
            var adj = b.AdjTri;
            var tris = b.Tris;
            var patchOf = new int[V];
            for (int i = 0; i < V; i++) patchOf[i] = -1;
            var seeds = new List<int>();
            var seedTri = new List<int>();
            var q = new Queue<int>();
            float r2 = radius * radius;
            for (int c = 0; c < V; c++)
            {
                if (canon[c] != (uint)c || off[c + 1] == off[c] || patchOf[c] >= 0 || nC[c].sqrMagnitude < 0.5f) continue;
                int pid = seeds.Count;
                seeds.Add(c);
                seedTri.Add((int)adj[off[c]]);
                patchOf[c] = pid;
                q.Clear();
                q.Enqueue(c);
                Vector3 sp = N[c], sn = nC[c];
                while (q.Count > 0)
                {
                    int u = q.Dequeue();
                    for (uint i = off[u]; i < off[u + 1]; i++)
                    {
                        int tr = (int)adj[i];
                        for (int k = 0; k < 3; k++)
                        {
                            int w = (int)canon[tris[3 * tr + k]];
                            if (patchOf[w] >= 0) continue;
                            if ((N[w] - sp).sqrMagnitude > r2) continue;
                            if (Vector3.Dot(nC[w], sn) <= PatchNormalAgree) continue;
                            patchOf[w] = pid;
                            q.Enqueue(w);
                        }
                    }
                }
            }
            // canonical vertices whose normal was degenerate got no patch from a seed: make them their own patch so nothing is left out
            for (int c = 0; c < V; c++)
            {
                if (canon[c] != (uint)c || off[c + 1] == off[c] || patchOf[c] >= 0) continue;
                patchOf[c] = seeds.Count;
                seeds.Add(c);
                seedTri.Add((int)adj[off[c]]);
            }
            for (int i = 0; i < V; i++) if (canon[i] != (uint)i) patchOf[i] = patchOf[canon[i]];
            aux.PatchOf = patchOf;
            aux.PatchSeed = seeds.ToArray();
            aux.PatchTri = seedTri.ToArray();
        }

        // Rewrites RestV: rest position, patch origin (the seed's rest position) and reference direction (the seed triangle's tangent,
        // projected into the seed's tangent plane), mask, spacing. Vertices of no patch: origin = own position, ref dir 0, mask 0.
        static void FillRestV(BakeResult b, Vector3[] N, Vector3[] nC)
        {
            var aux = b.Aux;
            int P = aux.PatchSeed.Length;
            var origin = new Vector3[P];
            var refDir = new Vector3[P];
            var rt = b.RestTri;
            for (int p = 0; p < P; p++)
            {
                int seed = aux.PatchSeed[p];
                origin[p] = N[seed];
                int o = aux.PatchTri[p] * RtStride;
                Vector3 t = new Vector3(rt[o + 5], rt[o + 6], rt[o + 7]);
                Vector3 n = nC[seed];
                Vector3 d = t - Vector3.Dot(t, n) * n;
                if (d.sqrMagnitude < 1e-8f)
                {
                    d = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right);
                    if (d.sqrMagnitude < 1e-8f) d = Vector3.right;
                }
                refDir[p] = d.normalized;
            }
            float spacing = b.Face ? SpacingFace : SpacingBody;
            var rv = b.RestV;
            for (int i = 0; i < b.V; i++)
            {
                int o = i * RvStride;
                int p = aux.PatchOf[i];
                Vector3 pos = N[i];
                rv[o] = pos.x; rv[o + 1] = pos.y; rv[o + 2] = pos.z;
                Vector3 org = p >= 0 ? origin[p] : pos, rd = p >= 0 ? refDir[p] : Vector3.zero;
                rv[o + 3] = org.x; rv[o + 4] = org.y; rv[o + 5] = org.z;
                rv[o + 6] = rd.x; rv[o + 7] = rd.y; rv[o + 8] = rd.z;
                rv[o + 9] = p >= 0 ? aux.Mask[i] : 0f;
                rv[o + 10] = spacing;
                rv[o + 11] = 0f;
            }
        }
    }
}
