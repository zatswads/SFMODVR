using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;

namespace SfMorph
{
    // An authored wrinkle layout (0.9.0, tools/wrinkles/author.py): the creases of one UV layout (her face sheet, her body atlas) and its
    // regions. Detail = the crease slope / depth / region fade (GPU only); Region = region id, measuring weight and crease direction per
    // texel (read here, point-fetched in the blend).
    internal class WrinkleLayout
    {
        public string Name;
        public Texture2D Detail, Region;
        public Color32[] Px;                 // Region's texels (row 0 = v 0)
        public int W, H;
        public readonly List<string> RegionName = new List<string>();
        public readonly List<string> RegionClass = new List<string>();   // face / neck / body
        public readonly List<float> Range = new List<float>();          // compression beyond the threshold for a full fade-in

        public int Count { get { return RegionName.Count; } }

        // texel of uv (nearest, clamped). 0.11.48: a UV in another UDIM tile (an avatar's sheets: Genesis 9's Body is tile 1, Legs 2,
        // Arms 3, each its own texture) reads this sheet's layout at its place inside the tile, as the material samples its texture
        public Color32 At(Vector2 uv)
        {
            uv = InTile(uv);
            int x = Mathf.Clamp((int)(uv.x * W), 0, W - 1), y = Mathf.Clamp((int)(uv.y * H), 0, H - 1);
            return Px[y * W + x];
        }

        public static Vector2 InTile(Vector2 uv)
        {
            if (uv.x < 0f || uv.x > 1f) uv.x -= Mathf.Floor(uv.x);
            if (uv.y < 0f || uv.y > 1f) uv.y -= Mathf.Floor(uv.y);
            return uv;
        }

        // Parses wrinkle_layouts.txt and finds each layout's two textures through load(name); bad lines are skipped with a note.
        public static List<WrinkleLayout> Parse(string text, Func<string, Texture2D> load, List<string> notes)
        {
            var res = new List<WrinkleLayout>();
            WrinkleLayout cur = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (t[0] == "layout" && t.Length >= 2)
                {
                    cur = new WrinkleLayout { Name = t[1] };
                    cur.Detail = load("wrinkle_" + cur.Name + "_detail");
                    cur.Region = load("wrinkle_" + cur.Name + "_region");
                    if (cur.Detail == null || cur.Region == null) { notes.Add(cur.Name + ": textures missing"); cur = null; continue; }
                    try { cur.Px = cur.Region.GetPixels32(); cur.W = cur.Region.width; cur.H = cur.Region.height; }
                    catch (Exception e) { notes.Add(cur.Name + ": region map unreadable (" + e.Message + ")"); cur = null; continue; }
                    res.Add(cur);
                }
                else if (t[0] == "region" && cur != null && t.Length >= 5)
                {
                    int idx;
                    float rg;
                    if (!int.TryParse(t[1], out idx) || idx != cur.Count + 1 || idx > Wrinkles.MaxRegions
                        || !float.TryParse(t[4], NumberStyles.Float, CultureInfo.InvariantCulture, out rg)) { notes.Add(cur.Name + ": bad line '" + line + "'"); continue; }
                    cur.RegionName.Add(t[2]);
                    cur.RegionClass.Add(t[3]);
                    cur.Range.Add(Mathf.Max(0.01f, rg));
                }
            }
            return res;
        }
    }

    // One renderer's wrinkle data (0.9.0), baked on the CPU: the triangles under an authored region with their neutral (rest) frame.
    // Contract with Wrinkles.compute: Tris = 3 vertex ids per region triangle; RegTri = 8 floats per region triangle: DmInv (m00 m01 m10
    // m11), the region's compression direction in the triangle's (t, b) rest basis (unit), weight (fixed point: region weight x rest area
    // x WeightPerM2), region index (0-based). All 3D data is in the mesh's own (bind) space.
    internal class BakeResult
    {
        public SkinnedMeshRenderer R;
        public Mesh Mesh;              // the mesh baked (the one the renderer showed)
        public Material Mat;           // the material whose slots this renderer feeds (its sheet)
        public int Submesh;            // which submesh/material slot of R
        public int V, T;               // vertex count, triangle count of the submesh
        public int RT;                 // region triangles
        public int[] Tris;             // 3*RT vertex indices
        public float[] RegTri;         // 8*RT
        public int[] SrcTri;           // RT: the submesh triangle each region triangle is
        public float[] DirUv;          // 2*RT: the crease line's direction in UV (cos a, sin a)
        public float[] Weight;         // RT: the region map's weight (0..1)
        public float[] RestP;          // 3 per vertex for the first min(V, 512) vertices (the skinned buffer's scale check)
        public int PosOff;             // stream-0 byte offset of Position
        public float Coverage;         // share of the submesh's triangles on the layout's UV islands
        public int[] PerRegion;        // region triangles per region
        public string Note = "";       // one-line stats for the log
    }

    // CPU bake of the wrinkle data. Neutral pose = the renderer's bind pose with its active blendshapes at their current weights and the
    // body-slider bone scales (Body.ScaleOf); no animation. Nothing here throws: failures come back as an error string.
    internal static class WrinkleBake
    {
        public const float WeightPerM2 = 1e8f;          // fixed-point units per square metre of region weight (a region < 40 dm^2 fits 32 bits)
        public const float MinCoverage = 0.9f;          // a mesh whose triangles mostly miss the layout's islands has another UV layout
        const double WeldQuantum = 1e-5;                // ContactBake's weld key: rest positions closer than this (per axis, quantised) are one vertex

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

        // Full bake of renderer r (submesh sub) against the neutral pose, on layout L. Null + error when the mesh isn't on the layout
        // (coverage) or can't be read.
        public static BakeResult Bake(SkinnedMeshRenderer r, int sub, Character her, WrinkleLayout L, out string error)
        {
            error = null;
            try
            {
                var sw = Stopwatch.StartNew();
                if (r == null) { error = "no renderer"; return null; }
                if (L == null) { error = "no wrinkle layout"; return null; }
                var m = r.sharedMesh;
                if (m == null) { error = "renderer has no mesh"; return null; }
                var mats = r.sharedMaterials;
                if (sub < 0 || sub >= mats.Length || mats[sub] == null) { error = "no material in slot " + sub; return null; }
                int posOff;
                error = ReadLayout(m, out posOff);
                if (error != null) return null;
                Src s = ReadSrc(m, sub, out error);
                if (s == null) return null;
                int V = s.P.Length, T = s.Tri.Length / 3;
                for (int i = 0; i < s.Tri.Length; i++)
                    if (s.Tri[i] < 0 || s.Tri[i] >= V) { error = "triangle index " + s.Tri[i] + " out of range (" + V + " vertices)"; return null; }

                // which triangles lie under a region: the region map at the UV centroid; the weight averaged over the centroid and the
                // corners (a corner in another region counts 0)
                var src = new List<int>();
                var reg = new List<int>();
                var wts = new List<float>();
                var dirs = new List<float>();
                var per = new int[L.Count];
                int onIsland = 0;
                for (int t = 0; t < T; t++)
                {
                    Vector2 a = s.Uv[s.Tri[3 * t]], b = s.Uv[s.Tri[3 * t + 1]], c = s.Uv[s.Tri[3 * t + 2]];
                    Color32 px = L.At((a + b + c) / 3f);
                    if (px.r != 255) onIsland++;
                    int id = px.r;
                    if (id < 1 || id > L.Count || px.g == 0) continue;
                    float w = px.g;
                    Color32 q;
                    q = L.At(a); if (q.r == id) w += q.g;
                    q = L.At(b); if (q.r == id) w += q.g;
                    q = L.At(c); if (q.r == id) w += q.g;
                    w /= 4f * 255f;
                    if (w < 0.01f) continue;
                    float c2 = px.b / 127.5f - 1f, s2 = px.a / 127.5f - 1f;
                    float ang = 0.5f * Mathf.Atan2(s2, c2);
                    src.Add(t); reg.Add(id - 1); wts.Add(w); dirs.Add(Mathf.Cos(ang)); dirs.Add(Mathf.Sin(ang));
                    per[id - 1]++;
                }
                float cov = T > 0 ? (float)onIsland / T : 0f;
                if (cov < MinCoverage)
                {
                    error = "its UVs are not on the '" + L.Name + "' wrinkle layout (" + (cov * 100f).ToString("0") + "% of its triangles on the layout's islands)";
                    return null;
                }

                string bsNote;
                Vector3[] N = Neutral(r, m, s, her, out bsNote);
                var res = new BakeResult
                {
                    R = r, Mesh = m, Mat = mats[sub], Submesh = sub, V = V, T = T, PosOff = posOff, Coverage = cov, PerRegion = per,
                    RT = src.Count, SrcTri = src.ToArray(), Weight = wts.ToArray(), DirUv = dirs.ToArray()
                };
                res.Tris = new int[3 * res.RT];
                res.RegTri = new float[8 * res.RT];
                for (int i = 0; i < res.RT; i++)
                {
                    for (int k = 0; k < 3; k++) res.Tris[3 * i + k] = s.Tri[3 * res.SrcTri[i] + k];
                    res.RegTri[8 * i + 7] = reg[i];
                }
                int bad = FillRegTri(res, N, s.Uv);
                FillRestP(res, N);
                sw.Stop();
                var regs = new System.Text.StringBuilder();
                for (int k = 0; k < per.Length; k++) if (per[k] > 0) regs.Append(regs.Length > 0 ? " " : "").Append(L.RegionName[k]).Append(':').Append(per[k]);
                res.Note = "V " + V + " T " + T + ", " + res.RT + " region triangles [" + regs + "]" + (bad > 0 ? " (" + bad + " degenerate)" : "")
                    + ", coverage " + (cov * 100f).ToString("0") + "%, " + sw.ElapsedMilliseconds + " ms" + (bsNote.Length > 0 ? ", " + bsNote : "");
                return res;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }

        // Recapture only: the rest frames against the current neutral (same topology and region triangles); RegTri is rewritten in place.
        public static bool Recapture(BakeResult b, Character her, out string error)
        {
            error = null;
            try
            {
                if (b == null) { error = "no bake"; return false; }
                var r = b.R;
                if (r == null) { error = "renderer gone"; return false; }
                var m = r.sharedMesh;
                if (m == null || m != b.Mesh) { error = "mesh changed"; return false; }
                Src s = ReadSrc(m, b.Submesh, out error);
                if (s == null) return false;
                if (s.P.Length != b.V || s.Tri.Length != b.T * 3) { error = "topology changed (" + s.P.Length + "/" + (s.Tri.Length / 3) + " vs " + b.V + "/" + b.T + ")"; return false; }
                string bsNote;
                Vector3[] N = Neutral(r, m, s, her, out bsNote);
                FillRegTri(b, N, s.Uv);
                FillRestP(b, N);
                return true;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        // ---- mesh data ----

        // Position Float32x3 on stream 0 (what the compute shader reads from the skinned buffer); null = fine, else the reason.
        static string ReadLayout(Mesh m, out int posOff)
        {
            posOff = 0;
            if (!m.HasVertexAttribute(VertexAttribute.Position)) return "mesh has no position";
            string w = CheckAttr(m, VertexAttribute.Position);
            if (w != null) return w;
            posOff = m.GetVertexAttributeOffset(VertexAttribute.Position);
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
                var layout = MorphState.ShapeLayout(m);   // 0.11.48: the neutral leaves the joint correctives out (they follow the pose)
                for (int k = 0; k < shapes; k++)
                {
                    if (Wrinkles.IsJcm(layout, k)) continue;
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

        // 0.8.0: ContactBake welds with the same quantised rest-position key and reads GPU-only meshes through the same cache.
        internal static ulong WeldKey(Vector3 p) { return Quant(p); }

        internal static MeshData CachedMeshData(Mesh m, out string error)
        {
            error = null;
            MeshData d;
            if (!GpuCache.TryGetValue(m, out d) || d.Pos == null || d.Pos.Length != m.vertexCount)
            {
                foreach (var k in new List<Mesh>(GpuCache.Keys)) if (k == null) GpuCache.Remove(k);
                d = MeshData.Read(m, out error);
                if (d == null) { error = "mesh read back failed: " + error; return null; }
                GpuCache[m] = d;
            }
            return d;
        }

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

        // Per region triangle: DmInv (row-major [[1/|e1|, -(e2.t)/(|e1| e2.b)], [0, 1/e2.b]], Dm = [e1 e2] in the (t, b) basis, t along
        // e1); the compression direction = across the crease lines (the line's UV direction mapped into 3D through d(rest)/d(u, v), turned
        // 90 degrees in the triangle's plane) in (t, b); weight = region weight x rest area x WeightPerM2. Degenerate triangles get weight
        // 0 (the kernel skips them). Keeps slot 7 (region index). Returns how many were degenerate.
        static int FillRegTri(BakeResult b, Vector3[] N, Vector2[] uv)
        {
            int bad = 0;
            var rt = b.RegTri;
            for (int i = 0; i < b.RT; i++)
            {
                int o = 8 * i;
                for (int k = 0; k < 7; k++) rt[o + k] = 0f;
                int ia = b.Tris[3 * i], ib = b.Tris[3 * i + 1], ic = b.Tris[3 * i + 2];
                Vector3 p0 = N[ia], e1 = N[ib] - p0, e2 = N[ic] - p0;
                Vector3 n = Vector3.Cross(e1, e2);
                float len = n.magnitude, l1 = e1.magnitude;
                if (!(len > 2e-12f) || !(l1 > 1e-7f)) { bad++; continue; }
                Vector3 tv = e1 / l1, nn = n / len, bv = Vector3.Cross(nn, tv);
                float e2t = Vector3.Dot(e2, tv), e2b = Vector3.Dot(e2, bv);
                if (!(Mathf.Abs(e2b) > 1e-8f)) { bad++; continue; }
                Vector2 d1 = uv[ib] - uv[ia], d2 = uv[ic] - uv[ia];
                float det = d1.x * d2.y - d2.x * d1.y;
                if (!(Mathf.Abs(det) > 1e-14f)) { bad++; continue; }
                Vector3 pu = (e1 * d2.y - e2 * d1.y) / det, pv = (e2 * d1.x - e1 * d2.x) / det;   // d(rest)/du, d(rest)/dv
                Vector3 line = pu * b.DirUv[2 * i] + pv * b.DirUv[2 * i + 1];
                line -= Vector3.Dot(line, nn) * nn;
                if (!(line.sqrMagnitude > 1e-20f)) { bad++; continue; }
                Vector3 across = Vector3.Cross(nn, line.normalized);
                float dt = Vector3.Dot(across, tv), db = Vector3.Dot(across, bv);
                float dl = Mathf.Sqrt(dt * dt + db * db);
                if (!(dl > 1e-6f)) { bad++; continue; }
                rt[o] = 1f / l1; rt[o + 1] = -e2t / (l1 * e2b); rt[o + 2] = 0f; rt[o + 3] = 1f / e2b;
                rt[o + 4] = dt / dl; rt[o + 5] = db / dl;
                rt[o + 6] = b.Weight[i] * 0.5f * len * WeightPerM2;
            }
            return bad;
        }

        static void FillRestP(BakeResult b, Vector3[] N)
        {
            int n = Math.Min(512, b.V);
            if (b.RestP == null || b.RestP.Length != 3 * n) b.RestP = new float[3 * n];
            for (int i = 0; i < n; i++) { b.RestP[3 * i] = N[i].x; b.RestP[3 * i + 1] = N[i].y; b.RestP[3 * i + 2] = N[i].z; }
        }
    }
}
