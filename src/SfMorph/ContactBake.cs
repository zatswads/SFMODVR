using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
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
        public uint[] AdjOff, AdjV;   // CSR over vertex ids (V + 1 offsets; rows filled for canonical ids only): unique canonical neighbours
        public Vector2[] Tissue;      // V: x = hard depth (m), y = soft depth (m) at softness 50 % (Tissue.cs: SoftMm x max(zone share, floor)); the cap is
                                      // (x + y x Tissue.SoftScale(softness)) x depth, computed in the kernel (softness and depth change at runtime)
        public uint[] Class;          // V: bone class id (see the table below); 0 = none
        public Vector4[] Bend;        // V: (j0, w0, j1, w1): BendPose slot 0..8 (-1 none), signed weight (+ outward bulge, - inward fold)
        public int BendCount;         // canonical vertices with a bend weight
        public Vector2[] Uv;          // V: UV0 (SkinMarks' raster); null when the mesh has none
        public int[][] SubTris;       // per submesh triangle index list
        public float AvgEdge;         // mean rest edge length (m)
        public float MetresPerUv;     // sqrt(sum 3D triangle area / sum UV triangle area); 0 without UVs
        public Vector3[] WorldP;      // V: where each vertex was at bake time (CPU skin with the bones then, no blendshapes; NaN = unskinned). GpuContact
                                      // maps overlay meshes (areola caps, tattoo, vulva rim) onto the body vertex under them with it
        public string Note = "";

        // ---- bone classes (design 1.3): id, side in the id's parity for the paired ones (even = left) ----
        internal const int ClsNone = 0, ClsPelvis = 1, ClsSpine = 2, ClsChest = 3, ClsNeck = 4, ClsHead = 5, ClsThigh = 6, ClsCalf = 8, ClsFoot = 10,
            ClsUpperArm = 12, ClsForearm = 14, ClsHand = 16, ClsButt = 18, ClsBreast = 20, ClsBelly = 22, ClsGenital = 23, ClsScapula = 24;
        const int ClassCount = 25;   // = Tissue.Classes

        static readonly string[] GenitalWords = { "vagin", "rectum", "anus", "uterus", "clit", "labia", "pussy", "genital", "ovar" };

        // 0 = her left, 1 = her right, -1 = none (AdvancedSkeleton "_L"/"_R" suffix, or a Left_/Right_ prefix)
        internal static int SideOf(string n)
        {
            if (string.IsNullOrEmpty(n)) return -1;
            if (n.EndsWith("_L", StringComparison.Ordinal) || n.StartsWith("Left_", StringComparison.Ordinal)) return 0;
            if (n.EndsWith("_R", StringComparison.Ordinal) || n.StartsWith("Right_", StringComparison.Ordinal)) return 1;
            return -1;
        }

        static bool Has(string n, string w) { return n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0; }

        // Class of a bone by its name; -1 = not recognised (the caller looks at the parent).
        internal static int ClassOfName(string n)
        {
            if (string.IsNullOrEmpty(n) || n.StartsWith(SoftZones.Prefix, StringComparison.Ordinal)) return -1;
            if (n == "Root_M" || n.StartsWith("Pelvis", StringComparison.Ordinal)) return ClsPelvis;
            if (n.StartsWith("Uterus_belly", StringComparison.Ordinal)) return ClsBelly;
            int sd = SideOf(n);
            if ((n.StartsWith("Left_", StringComparison.Ordinal) || n.StartsWith("Right_", StringComparison.Ordinal)) && (Has(n, "breast") || Has(n, "nipple")))
                return ClsBreast + sd;
            for (int i = 0; i < GenitalWords.Length; i++) if (Has(n, GenitalWords[i])) return ClsGenital;
            if (n.StartsWith("Spine", StringComparison.Ordinal)) return ClsSpine;
            if (n.StartsWith("Chest", StringComparison.Ordinal)) return ClsChest;
            if (n.StartsWith("Neck", StringComparison.Ordinal)) return ClsNeck;
            if (n.StartsWith("Head", StringComparison.Ordinal) || n.StartsWith("Jaw", StringComparison.Ordinal) || n.StartsWith("Eye", StringComparison.Ordinal)
                || n.StartsWith("Lip", StringComparison.Ordinal) || n.StartsWith("Cheek", StringComparison.Ordinal) || n.StartsWith("Tongue", StringComparison.Ordinal)
                || n.StartsWith("Teeth", StringComparison.Ordinal)) return ClsHead;
            if (sd < 0) return -1;
            if (n.StartsWith("Hip", StringComparison.Ordinal) || n.StartsWith("LegFrontSkin", StringComparison.Ordinal) || n.StartsWith("LegInsiteSkin", StringComparison.Ordinal)
                || n.StartsWith("LegOutsiteSkin", StringComparison.Ordinal)) return ClsThigh + sd;
            if (n.StartsWith("Knee", StringComparison.Ordinal)) return ClsCalf + sd;
            if (n.StartsWith("Ankle", StringComparison.Ordinal) || n.StartsWith("Toes", StringComparison.Ordinal)) return ClsFoot + sd;
            if (n.StartsWith("Shoulder", StringComparison.Ordinal)) return ClsUpperArm + sd;
            if (n.StartsWith("Elbow", StringComparison.Ordinal)) return ClsForearm + sd;
            if (n.StartsWith("Wrist", StringComparison.Ordinal) || Has(n, "finger") || n.StartsWith("Thumb", StringComparison.Ordinal)) return ClsHand + sd;
            if (n.StartsWith("Butt", StringComparison.Ordinal)) return ClsButt + sd;
            if (n.StartsWith("Scapula", StringComparison.Ordinal)) return ClsScapula;
            return -1;
        }

        // The bone's class, from its own name or the nearest recognised ancestor (our SfSoft helper bones hang under the bone they split).
        static int ClassOfBone(Transform t)
        {
            for (int d = 0; d < 10 && t != null; d++, t = t.parent)
            {
                int c = ClassOfName(t.name);
                if (c >= 0) return c;
            }
            return ClsNone;
        }

        // ---- the bake ----

        public static ContactBake Build(SkinnedMeshRenderer r, float[] zone, Character her, out string error)
        {
            error = null;
            try { return BuildInner(r, zone, her, out error); }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; return null; }
        }

        static ContactBake BuildInner(SkinnedMeshRenderer r, float[] zone, Character her, out string error)
        {
            error = null;
            var sw = Stopwatch.StartNew();
            if (r == null) { error = "no renderer"; return null; }
            Mesh m = r.sharedMesh;
            if (m == null) { error = "renderer has no mesh"; return null; }
            int V = m.vertexCount;
            if (V <= 0) { error = "mesh has no vertices"; return null; }

            Vector3[] P, N;
            Vector2[] uv;
            BoneWeight[] W;
            Matrix4x4[] bind;
            var subs = new List<int[]>();
            if (m.isReadable)
            {
                P = m.vertices; N = m.normals; uv = m.uv; W = m.boneWeights; bind = m.bindposes;
                for (int s = 0; s < m.subMeshCount; s++) subs.Add(m.GetTopology(s) == MeshTopology.Triangles ? m.GetTriangles(s) : new int[0]);
            }
            else
            {
                string e;
                MeshData d = WrinkleBake.CachedMeshData(m, out e);
                if (d == null) { error = e; return null; }
                P = d.Pos; N = d.Nrm; uv = d.Uv; W = d.Weights; bind = d.Bind; subs = d.Sub;
            }
            if (P == null || P.Length != V) { error = "vertex count mismatch (" + (P != null ? P.Length : 0) + " vs " + V + ")"; return null; }
            if (W == null || W.Length != V || bind == null || bind.Length == 0) { error = "no bone weights/bind poses"; return null; }
            if (N != null && N.Length != V) N = null;
            if (uv != null && uv.Length != V) uv = null;
            if (zone != null && zone.Length != V) { error = "zone length " + zone.Length + " != vertex count " + V; return null; }
            var bones = r.bones;
            int totalTris = 0;
            for (int s = 0; s < subs.Count; s++)
            {
                var tr = subs[s];
                if (tr == null || tr.Length % 3 != 0) { error = "submesh " + s + " has a broken index list"; return null; }
                for (int i = 0; i < tr.Length; i++) if (tr[i] < 0 || tr[i] >= V) { error = "triangle index " + tr[i] + " out of range (" + V + " vertices)"; return null; }
                totalTris += tr.Length / 3;
            }
            if (totalTris == 0) { error = "no triangles"; return null; }

            // 1. weld: the first vertex at each quantised rest position represents the group
            var canon = new uint[V];
            var seen = new Dictionary<ulong, int>(V);
            int groups = 0;
            for (int i = 0; i < V; i++)
            {
                ulong key = WrinkleBake.WeldKey(P[i]);
                int first;
                if (seen.TryGetValue(key, out first)) canon[i] = (uint)first;
                else { seen[key] = i; canon[i] = (uint)i; groups++; }
            }

            // 2. adjacency over canonical ids: directed edges of every triangle, rows sorted and made unique
            var cnt = new int[V + 1];
            for (int s = 0; s < subs.Count; s++)
            {
                var tr = subs[s];
                for (int i = 0; i < tr.Length; i += 3)
                {
                    uint a = canon[tr[i]], b = canon[tr[i + 1]], c = canon[tr[i + 2]];
                    if (a != b) { cnt[a + 1]++; cnt[b + 1]++; }
                    if (b != c) { cnt[b + 1]++; cnt[c + 1]++; }
                    if (c != a) { cnt[c + 1]++; cnt[a + 1]++; }
                }
            }
            for (int i = 0; i < V; i++) cnt[i + 1] += cnt[i];
            var raw = new uint[cnt[V]];
            var cur = new int[V];
            Array.Copy(cnt, cur, V);
            for (int s = 0; s < subs.Count; s++)
            {
                var tr = subs[s];
                for (int i = 0; i < tr.Length; i += 3)
                {
                    uint a = canon[tr[i]], b = canon[tr[i + 1]], c = canon[tr[i + 2]];
                    if (a != b) { raw[cur[a]++] = b; raw[cur[b]++] = a; }
                    if (b != c) { raw[cur[b]++] = c; raw[cur[c]++] = b; }
                    if (c != a) { raw[cur[c]++] = a; raw[cur[a]++] = c; }
                }
            }
            var adjOff = new uint[V + 1];
            var adjV = new uint[raw.Length];
            int outN = 0;
            for (int v = 0; v < V; v++)
            {
                adjOff[v] = (uint)outN;
                int lo = cnt[v], hi = cnt[v + 1];
                for (int i = lo + 1; i < hi; i++)   // insertion sort: rows are a handful of entries
                {
                    uint x = raw[i];
                    int j = i - 1;
                    while (j >= lo && raw[j] > x) { raw[j + 1] = raw[j]; j--; }
                    raw[j + 1] = x;
                }
                for (int i = lo; i < hi; i++) if (i == lo || raw[i] != raw[i - 1]) adjV[outN++] = raw[i];
            }
            adjOff[V] = (uint)outN;
            if (outN < adjV.Length) Array.Resize(ref adjV, outN);

            // 3. units: metres per mesh unit (the mesh's bind space may not be metres), edge length, UV density
            float mpu = MetresPerUnit(bones, bind, W);
            double edgeSum = 0.0;
            for (int v = 0; v < V; v++)
            {
                if (canon[v] != (uint)v) continue;
                for (uint k = adjOff[v]; k < adjOff[v + 1]; k++)
                {
                    uint u = adjV[k];
                    if (u > (uint)v) edgeSum += (P[v] - P[u]).magnitude;
                }
            }
            int edgeN = 0;
            for (int v = 0; v < V; v++) if (canon[v] == (uint)v) for (uint k = adjOff[v]; k < adjOff[v + 1]; k++) if (adjV[k] > (uint)v) edgeN++;
            float avgEdge = edgeN > 0 ? (float)(edgeSum / edgeN) * mpu : 0.007f;
            if (!(avgEdge > 1e-5f)) avgEdge = 0.007f;
            float mPerUv = 0f;
            if (uv != null)
            {
                double a3 = 0.0, a2 = 0.0;
                for (int s = 0; s < subs.Count; s++)
                {
                    var tr = subs[s];
                    for (int i = 0; i < tr.Length; i += 3)
                    {
                        int ia = tr[i], ib = tr[i + 1], ic = tr[i + 2];
                        a3 += 0.5 * Vector3.Cross(P[ib] - P[ia], P[ic] - P[ia]).magnitude;
                        Vector2 e1 = uv[ib] - uv[ia], e2 = uv[ic] - uv[ia];
                        a2 += 0.5 * Math.Abs(e1.x * e2.y - e1.y * e2.x);
                    }
                }
                if (a3 > 0.0 && a2 > 1e-12) mPerUv = (float)(Math.Sqrt(a3 / a2) * mpu);
            }

            // 4. her frame in this mesh's bind space (left / up / forward, root), for the bend weights and the shin / biceps floors
            var frame = Frame.Make(bones, bind, her);

            // 5. bone class and tissue per vertex: hard depth and soft depth, blended over the bone weights like the class itself
            var boneCls = new int[bones.Length];
            var clsSeen = new int[ClassCount];
            var bcache = new Dictionary<Transform, int>();
            for (int i = 0; i < bones.Length; i++)
            {
                var t = bones[i];
                int c = ClsNone;
                if (t != null && !bcache.TryGetValue(t, out c)) { c = ClassOfBone(t); bcache[t] = c; }
                boneCls[i] = c;
            }
            var cls = new uint[V];
            var tissue = new Vector2[V];
            for (int v = 0; v < V; v++)
            {
                var w = W[v];
                int b0 = w.boneIndex0, b1 = w.boneIndex1, b2 = w.boneIndex2, b3 = w.boneIndex3;
                float w0 = w.weight0, w1 = w.weight1, w2 = w.weight2, w3 = w.weight3;
                int c0 = Cls(boneCls, b0, w0), c1 = Cls(boneCls, b1, w1), c2 = Cls(boneCls, b2, w2), c3 = Cls(boneCls, b3, w3);
                float sum = 0f, hard = 0f, soft = 0f;
                float zs = zone != null ? Mathf.Clamp01(zone[v]) : 0f;
                bool fwdFace = frame.Ok && N != null && Vector3.Dot(N[v], frame.Fwd) > 0.3f;
                Accum(c0, w0, zs, fwdFace, ref sum, ref hard, ref soft);
                Accum(c1, w1, zs, fwdFace, ref sum, ref hard, ref soft);
                Accum(c2, w2, zs, fwdFace, ref sum, ref hard, ref soft);
                Accum(c3, w3, zs, fwdFace, ref sum, ref hard, ref soft);
                int best = ClsNone;
                float bestW = 0f;
                for (int k = 0; k < 4; k++)
                {
                    int ck = k == 0 ? c0 : k == 1 ? c1 : k == 2 ? c2 : c3;
                    if (ck < 0) continue;
                    float tot = (c0 == ck ? w0 : 0f) + (c1 == ck ? w1 : 0f) + (c2 == ck ? w2 : 0f) + (c3 == ck ? w3 : 0f);
                    if (tot > bestW) { bestW = tot; best = ck; }
                }
                cls[v] = (uint)best;
                tissue[v] = sum > 1e-4f ? new Vector2(hard / sum * 0.001f, soft / sum * 0.001f) : new Vector2(SfMorph.Tissue.HardMm[ClsNone] * 0.001f, 0f);
            }
            // welded copies share the group's largest tissue and the first copy's class
            var gx = new float[V];
            var gy = new float[V];
            for (int v = 0; v < V; v++)
            {
                int c = (int)canon[v];
                if (tissue[v].x > gx[c]) gx[c] = tissue[v].x;
                if (tissue[v].y > gy[c]) gy[c] = tissue[v].y;
            }
            for (int v = 0; v < V; v++)
            {
                int c = (int)canon[v];
                tissue[v] = new Vector2(gx[c], gy[c]);
                cls[v] = cls[c];
                clsSeen[cls[v]]++;
            }

            // 6. bend weights
            var bend = new Vector4[V];
            for (int v = 0; v < V; v++) bend[v] = new Vector4(-1f, 0f, -1f, 0f);
            string bendNote = "bend off (no " + (N == null ? "normals" : "character") + ")";
            int bendVerts = 0;
            if (N != null && her != null) bendVerts = BakeBend(frame, bind, P, N, cls, canon, mpu, bend, out bendNote);

            // 7. a CPU skin with the current bones (no blendshapes: the overlays carry the same "follow" deltas as the body under them, so leaving
            // them out on both keeps them matched): GpuContact's overlay -> body map
            var wp = new Vector3[V];
            var bm = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++) bm[i] = bones[i] != null && i < bind.Length ? bones[i].localToWorldMatrix * bind[i] : Matrix4x4.zero;
            for (int v = 0; v < V; v++)
            {
                var w = W[v];
                Vector3 acc = Vector3.zero;
                float sum = 0f;
                if (w.weight0 > 0f && w.boneIndex0 < bm.Length) { acc += bm[w.boneIndex0].MultiplyPoint3x4(P[v]) * w.weight0; sum += w.weight0; }
                if (w.weight1 > 0f && w.boneIndex1 < bm.Length) { acc += bm[w.boneIndex1].MultiplyPoint3x4(P[v]) * w.weight1; sum += w.weight1; }
                if (w.weight2 > 0f && w.boneIndex2 < bm.Length) { acc += bm[w.boneIndex2].MultiplyPoint3x4(P[v]) * w.weight2; sum += w.weight2; }
                if (w.weight3 > 0f && w.boneIndex3 < bm.Length) { acc += bm[w.boneIndex3].MultiplyPoint3x4(P[v]) * w.weight3; sum += w.weight3; }
                wp[v] = sum > 0.5f ? acc / sum : new Vector3(float.NaN, float.NaN, float.NaN);
            }

            var res = new ContactBake
            {
                R = r, Mesh = m, V = V, CanonCount = groups, Canon = canon, AdjOff = adjOff, AdjV = adjV, Tissue = tissue, Class = cls, Bend = bend,
                BendCount = bendVerts, Uv = uv, SubTris = subs.ToArray(), AvgEdge = avgEdge, MetresPerUv = mPerUv, WorldP = wp
            };
            sw.Stop();
            var sb = new StringBuilder();
            sb.Append("V ").Append(V).Append(" welded ").Append(V - groups).Append(", ").Append(totalTris).Append(" tris, adjacency ")
              .Append(groups > 0 ? ((double)outN / groups).ToString("0.0") : "0").Append("/vertex, edge ").Append((avgEdge * 1000f).ToString("0.0")).Append(" mm, ")
              .Append(mpu.ToString("0.####")).Append(" m/unit, classes");
            for (int c = 0; c < ClassCount; c++) if (clsSeen[c] > 0) sb.Append(' ').Append(ClassName(c)).Append(':').Append(clsSeen[c]);
            float tmin = float.MaxValue, tmax = 0f;
            for (int v = 0; v < V; v++) { float x = tissue[v].x + tissue[v].y; if (x < tmin) tmin = x; if (x > tmax) tmax = x; }
            sb.Append(", tissue ").Append((tmin * 1000f).ToString("0.#")).Append("-").Append((tmax * 1000f).ToString("0.#")).Append(" mm at softness 50 %, ").Append(frame.Note.Length > 0 ? frame.Note + ", " : "").Append(bendNote);
            if (mPerUv > 0f) sb.Append(", ").Append(mPerUv.ToString("0.###")).Append(" m/uv");
            sb.Append(", ").Append(sw.ElapsedMilliseconds).Append(" ms");
            res.Note = sb.ToString();
            return res;
        }

        // One bone influence's hard / soft depth (mm x weight): the class's HardMm, SoftMm x max(zone share, floor); a calf vertex facing forward sits
        // over the tibia (ShinFloor), an upper-arm one over the thin biceps layer (BicepsFloor).
        static void Accum(int cls, float w, float zone, bool fwdFace, ref float sum, ref float hard, ref float soft)
        {
            if (cls < 0) return;
            float fl = SfMorph.Tissue.Floor[cls];
            if (fwdFace)
            {
                if (cls == ClsCalf || cls == ClsCalf + 1) fl = SfMorph.Tissue.ShinFloor;
                else if (cls == ClsUpperArm || cls == ClsUpperArm + 1) fl = SfMorph.Tissue.BicepsFloor;
            }
            sum += w;
            hard += w * SfMorph.Tissue.HardMm[cls];
            soft += w * SfMorph.Tissue.SoftMm[cls] * Mathf.Max(zone, fl);
        }

        static int Cls(int[] boneCls, int bone, float w)
        {
            return w > 0f && bone >= 0 && bone < boneCls.Length ? boneCls[bone] : -1;
        }

        internal static string ClassName(int c)
        {
            switch (c)
            {
                case ClsPelvis: return "pelvis";
                case ClsSpine: return "spine";
                case ClsChest: return "chest";
                case ClsNeck: return "neck";
                case ClsHead: return "head";
                case ClsBelly: return "belly";
                case ClsGenital: return "genital";
                case ClsScapula: return "scapula";
            }
            string[] names = { "thigh", "calf", "foot", "upperarm", "forearm", "hand", "butt", "breast" };
            if (c >= ClsThigh && c < ClsBelly) return names[(c - ClsThigh) / 2] + ((c & 1) == 0 ? "_L" : "_R");
            return "none";
        }

        // The bind-space scale of the mesh: how many metres one mesh unit is, from the strongest bone's mesh-to-world matrix.
        static float MetresPerUnit(Transform[] bones, Matrix4x4[] bind, BoneWeight[] W)
        {
            var sum = new float[bones.Length];
            for (int v = 0; v < W.Length; v++)
            {
                var w = W[v];
                if (w.boneIndex0 < sum.Length && w.boneIndex0 >= 0) sum[w.boneIndex0] += w.weight0;
                if (w.boneIndex1 < sum.Length && w.boneIndex1 >= 0) sum[w.boneIndex1] += w.weight1;
            }
            int best = -1;
            for (int i = 0; i < bones.Length && i < bind.Length; i++) if (bones[i] != null && (best < 0 || sum[i] > sum[best])) best = i;
            if (best < 0) return 1f;
            Matrix4x4 mx = bones[best].localToWorldMatrix * bind[best];
            float sx = new Vector3(mx.m00, mx.m10, mx.m20).magnitude, sy = new Vector3(mx.m01, mx.m11, mx.m21).magnitude, sz = new Vector3(mx.m02, mx.m12, mx.m22).magnitude;
            float s = (sx + sy + sz) / 3f;
            return s > 1e-5f && s < 1000f ? s : 1f;
        }

        // ---- bend weights (design 2.2) ----

        static float G(float t, float c, float w) { float x = (t - c) / w; return Mathf.Exp(-x * x); }

        // Keeps the two largest |weight| (slot -1 = free); weights under 0.02 are dropped.
        static void Push(ref Vector4 b, int slot, float wt)
        {
            if (Mathf.Abs(wt) < 0.02f) return;
            if (b.x < 0f || Mathf.Abs(wt) > Mathf.Abs(b.y))
            {
                b.z = b.x; b.w = b.y;
                b.x = slot; b.y = wt;
            }
            else if (b.z < 0f || Mathf.Abs(wt) > Mathf.Abs(b.w)) { b.z = slot; b.w = wt; }
        }

        // Her frame in the mesh's own bind space (needs the hips and something above the root in this mesh's bone list: a foot or nipple mesh has
        // neither, so Ok is false there: no bend, no shin floor).
        struct Frame
        {
            public bool Ok;
            public Vector3 Left, Up, Fwd, RootP;
            public Func<string, Vector3?> Bp;   // a named bone's bind position in this mesh's space
            public string Note;

            public static Frame Make(Transform[] bones, Matrix4x4[] bind, Character her)
            {
                var f = new Frame { Note = "" };
                if (her == null) { f.Note = "no frame (no character)"; return f; }
                var idx = new Dictionary<Transform, int>();
                for (int i = 0; i < bones.Length; i++) if (bones[i] != null && !idx.ContainsKey(bones[i])) idx[bones[i]] = i;
                Func<string, Vector3?> bp = name =>
                {
                    Transform t;
                    int i;
                    if (!her.Bones.TryGetValue(name, out t) || t == null || !idx.TryGetValue(t, out i) || i >= bind.Length) return null;
                    return bind[i].inverse.MultiplyPoint3x4(Vector3.zero);
                };
                f.Bp = bp;
                Vector3? hl = bp("Hip_L"), hr = bp("Hip_R"), root = bp("Root_M");
                if (!hl.HasValue || !hr.HasValue) { f.Note = "no frame (no hips in this mesh)"; return f; }
                Vector3 rootP = root.HasValue ? root.Value : (hl.Value + hr.Value) * 0.5f;
                Vector3? upT = bp("Chest_M") ?? bp("Spine1_M") ?? bp("Neck_M") ?? bp("Head_M");
                if (!upT.HasValue || (upT.Value - rootP).sqrMagnitude < 1e-12f) { f.Note = "no frame (nothing above the pelvis in this mesh)"; return f; }
                Vector3 left = (hl.Value - hr.Value).normalized;
                Vector3 up = (upT.Value - rootP).normalized;
                Vector3 fwd = Vector3.Cross(up, left).normalized;
                up = Vector3.Cross(left, fwd).normalized;
                Vector3? toe = bp("Toes_L"), ankle = bp("Ankle_L");
                if (toe.HasValue && ankle.HasValue && Vector3.Dot(toe.Value - ankle.Value, fwd) < 0f) fwd = -fwd;
                f.Left = left; f.Up = up; f.Fwd = fwd; f.RootP = rootP; f.Ok = true;
                return f;
            }
        }

        // The slot weights per vertex from the rest pose in the mesh's own bind space. Returns the number of canonical vertices that got a weight.
        static int BakeBend(Frame fr, Matrix4x4[] bind, Vector3[] P, Vector3[] N, uint[] cls, uint[] canon, float mpu, Vector4[] bend, out string note)
        {
            if (!fr.Ok) { note = "no bend (" + (fr.Note.Length > 0 ? fr.Note : "no frame") + ")"; return 0; }
            Func<string, Vector3?> bp = fr.Bp;
            Vector3 fwd = fr.Fwd, up = fr.Up, rootP = fr.RootP;

            var sh = new Vector3?[2]; var el = new Vector3?[2]; var hip = new Vector3?[2]; var kn = new Vector3?[2]; var an = new Vector3?[2];
            for (int k = 0; k < 2; k++)
            {
                string s = k == 0 ? "_L" : "_R";
                sh[k] = bp("Shoulder" + s); el[k] = bp("Elbow" + s); hip[k] = bp("Hip" + s); kn[k] = bp("Knee" + s); an[k] = bp("Ankle" + s);
            }
            int V = P.Length, count = 0;
            var perSlot = new int[BendPose.Slots];
            for (int v = 0; v < V; v++)
            {
                int c = (int)cls[v];
                Vector3 p = P[v], n = N[v];
                float sF = Mathf.Max(0f, Vector3.Dot(n, fwd)), sB = Mathf.Max(0f, -Vector3.Dot(n, fwd));
                Vector4 b = new Vector4(-1f, 0f, -1f, 0f);
                if (c == ClsThigh || c == ClsThigh + 1)
                {
                    int k = c - ClsThigh;
                    float t;
                    if (hip[k].HasValue && kn[k].HasValue && Along(p, hip[k].Value, kn[k].Value, out t))
                    {
                        Push(ref b, 2 + k, G(t, 0.85f, 0.15f) * sB);
                        Push(ref b, 4 + k, G(t, 0.15f, 0.15f) * sF);
                    }
                }
                else if (c == ClsCalf || c == ClsCalf + 1)
                {
                    int k = c - ClsCalf;
                    float t;
                    if (kn[k].HasValue && an[k].HasValue && Along(p, kn[k].Value, an[k].Value, out t)) Push(ref b, 6 + k, G(t, 0.30f, 0.18f) * sB);
                }
                else if (c == ClsUpperArm || c == ClsUpperArm + 1)
                {
                    int k = c - ClsUpperArm;
                    float t;
                    if (sh[k].HasValue && el[k].HasValue && Along(p, sh[k].Value, el[k].Value, out t)) Push(ref b, k, G(t, 0.55f, 0.18f) * sF);
                }
                else if ((c == ClsPelvis || c == ClsBelly) && Vector3.Dot(n, fwd) > 0.3f)
                {
                    float y = Vector3.Dot(p - rootP, up) * mpu;
                    if (y >= 0f && y <= 0.12f) Push(ref b, 8, -G(y, 0.04f, 0.035f) * sF);
                }
                bend[v] = b;
                if (b.x >= 0f && canon[v] == (uint)v) { count++; perSlot[(int)b.x]++; if (b.z >= 0f) perSlot[(int)b.z]++; }
            }
            var sb = new StringBuilder("bend ").Append(count).Append(" verts [");
            for (int i = 0; i < perSlot.Length; i++) sb.Append(i > 0 ? " " : "").Append(perSlot[i]);
            note = sb.Append(']').ToString();
            return count;
        }

        // Position of p along a->b as 0..1 (unclamped); false for a degenerate bone.
        static bool Along(Vector3 p, Vector3 a, Vector3 b, out float t)
        {
            Vector3 ab = b - a;
            float l2 = ab.sqrMagnitude;
            t = 0f;
            if (l2 < 1e-12f) return false;
            t = Vector3.Dot(p - a, ab) / l2;
            return true;
        }
    }
}
