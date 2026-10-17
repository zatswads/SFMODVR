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
        public float BendAmount = 0.6f, BicepsMm = 8f, KneeMm = 8f, CalfMm = 6f, HipMm = 5f, BellyFoldMm = 3f, WaistRollMm = 10f;
        public float RippleThrustAmt = 1f, NormalSmooth = 0.008f;   // 0.11.12
        public float Ripple = 1f, RippleSpeed = 0.5f, RippleWavelength = 0.1f, RippleDamping = 0.6f, RippleImpact = 0.35f;
        // 0.11.8 tensed muscles as geometry ([Muscles] Flex, the per-area bulge in mm at full activation; Firmness is MuscleFlex.Firm)
        public float Flex = 1f, CoreMm = 3f, BackMm = 4f, LegsMm = 4f, ArmsMm = 4f;
        public float Obliques = 0.6f;   // 0.11.20: the obliques' share of CoreMm (and of their relief)
        public bool Marks = true;
        public float Redness = 0.5f, MarkFade = 15f, MarkGain = 0.6f, Ao = 0.5f, MarkBoost = 1f;
        // 0.11.20: the lingering print's depth share and time constant (s), and the marks' blur on her skin (m, sigma)
        public float Print = 1f, PrintTime = 0.8f, MarkSmooth = 0.006f;
        // 0.11.28 Delta Mush at her joints: on, strength 0..1, radius (m; read when a renderer is baked)
        public bool Mush = true;
        public float MushAmt = 1f, MushRadius = 0.02f;
        // 0.11.31 skin folds at deep bends (crease depth mm at a full bend, x FoldAmount) and chubby: soft fat per area (mm at its fullest
        // spot, x Chubby; FatArea order)
        public float FoldAmount = 1f, ElbowFoldMm = 4f, KneeFoldMm = 5f, HipFoldMm = 5f;
        public float Chubby = 0f;
        public readonly float[] FatMm = new float[FatAreas];
        public const int FatAreas = 8;   // belly, waist, back, glutes, thighs, arms, calves, face (cheeks, double chin, neck)
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
        public Vector4[] Bend;        // V: (j0, w0, j1, w1): BendPose slot 0..10 (-1 none), signed weight (+ outward bulge, - inward fold)
        public int BendCount;         // canonical vertices with a bend weight
        public Vector2[] Uv;          // V: UV0 (SkinMarks' raster); null when the mesh has none
        public int[][] SubTris;       // per submesh triangle index list
        public float AvgEdge;         // mean rest edge length (m)
        public float SkinEdge;        // 0.11.7: mean rest edge length away from the hands and feet (m): what the dent smoothing is sized by
        public float MetresPerUv;     // sqrt(sum 3D triangle area / sum UV triangle area); 0 without UVs
        public Vector4[] Muscle;      // 0.11.8 V: (id0, share0, id1, share1) muscle ids of MuscleFlex.Layer (-1 none) at the vertex UV, null = no muscle data
        public float[] Belly;         // 0.11.8 V: 0 at a muscle's edge / tendon, 1 mid-belly (geodesic falloff, less in its grooves)
        public int MuscleCount;       // canonical vertices with a muscle
        public Vector3[] WorldP;      // V: where each vertex was at bake time (CPU skin with the bones then, no blendshapes; NaN = unskinned). GpuContact
                                      // maps overlay meshes (areola caps, tattoo, vulva rim) onto the body vertex under them with it
        public MushData Mush;         // 0.11.28 Delta Mush at her joints (body skin meshes only; null = none)
        public Vector4[] Fold;        // 0.11.31 V: (fold slot 0..5 or -1, crease weight (inward), roll weight (outward, both sides of the crease), 0)
        public int FoldCount;         // canonical vertices with a fold
        public Vector4[] Fat;         // 0.11.31 V: (area, weight, area, weight) of the chubby areas (-1 none); weight 0..1 = share of the area's mm
        public int FatCount;
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

        // 0.11.34 (user: GPU resets ending in crashes or freezes since 17 Oct; their logs: an 11-17 s freeze of the main thread in GPU contact's
        // sync at every character / outfit change, 5-8 s per body mesh, each followed by nvlddmkm resets): the bake is split in two. Prepare (main
        // thread) reads everything that touches a Unity object (the mesh's arrays, bone names and transforms, her frames, the CPU skin's matrices,
        // which submeshes are on the body sheet, the Delta Mush rest shape); Job.Run does the arithmetic on a worker thread and touches no Unity
        // object. GpuContact starts the job and picks the result up on a later frame (Done), so the headset keeps getting frames meanwhile.
        internal sealed class Job
        {
            internal SkinnedMeshRenderer R;
            internal Mesh M;
            internal int V;
            internal Vector3[] P, N, MushRest;
            internal Vector2[] Uv;
            internal BoneWeight[] W;
            internal List<int[]> Subs;
            internal float[] Zone;
            internal int[] BoneCls;
            internal float Mpu, MushRadius;
            internal Frame Fr;
            internal HeadFrame Hf;
            internal bool HasHer;
            internal Matrix4x4[] SkinM;
            internal MuscleMap Mm;
            internal bool[] SubOnSheet;
            internal int MushShapes;
            internal long PrepMs;
            public volatile bool Done;
            public ContactBake Result;
            public string Error;

            public void Run()
            {
                try { string e; Result = Compute(this, out e); Error = e; }
                catch (Exception e) { Result = null; Error = e.GetType().Name + ": " + e.Message; }
                Done = true;
            }
        }

        // Main thread: the Unity side of the bake. null + error = no bake for this renderer.
        public static Job Prepare(SkinnedMeshRenderer r, float[] zone, Character her, MuscleMap mm, float mushRadius, out string error)
        {
            error = null;
            try { return PrepareInner(r, zone, her, mm, mushRadius, out error); }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; return null; }
        }

        static Job PrepareInner(SkinnedMeshRenderer r, float[] zone, Character her, MuscleMap mm, float mushRadius, out string error)
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
            var j = new Job { R = r, M = m, V = V, P = P, N = N, Uv = uv, W = W, Subs = subs, Zone = zone, HasHer = her != null, Mm = mm, MushRadius = mushRadius };
            // units: metres per mesh unit (the mesh's bind space may not be metres)
            j.Mpu = MetresPerUnit(bones, bind, W);
            // her frame in this mesh's bind space (left / up / forward, root) and her head's (the face / neck fat)
            j.Fr = Frame.Make(bones, bind, her);
            j.Hf = HeadFrame.Make(bones, bind, her);
            // the bone class of each bone (by name, or its nearest recognised ancestor)
            var boneCls = new int[bones.Length];
            var bcache = new Dictionary<Transform, int>();
            for (int i = 0; i < bones.Length; i++)
            {
                var t = bones[i];
                int c = ClsNone;
                if (t != null && !bcache.TryGetValue(t, out c)) { c = ClassOfBone(t); bcache[t] = c; }
                boneCls[i] = c;
            }
            j.BoneCls = boneCls;
            // the CPU skin's matrices with the current bones (the overlay -> body map)
            var bm = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++) bm[i] = bones[i] != null && i < bind.Length ? bones[i].localToWorldMatrix * bind[i] : Matrix4x4.zero;
            j.SkinM = bm;
            // muscles: the body-atlas submeshes (Wrinkles' class 3: her body / skin materials; the foot's own texture is not the body atlas)
            if (mm != null && uv != null)
            {
                var mats = r.sharedMaterials;
                j.SubOnSheet = new bool[subs.Count];
                for (int si = 0; si < subs.Count && si < mats.Length; si++)
                    j.SubOnSheet[si] = mats[si] != null && Wrinkles.Classify(mats[si], r) == 3 && (mats[si].name ?? "").IndexOf("foot", StringComparison.OrdinalIgnoreCase) < 0;
            }
            // 0.11.28 Delta Mush: the rest shape is her current one (the character and slider blendshapes, never the pose correctives)
            if (mushRadius > 0f && j.Fr.Bp != null)
            {
                var pm = (Vector3[])P.Clone();
                Vector3[] dv = null;
                j.MushShapes = HerBody.ApplyMorphs(r, pm, ref dv);
                j.MushRest = pm;
            }
            j.PrepMs = sw.ElapsedMilliseconds;
            return j;
        }

        // Worker thread: the arithmetic of the bake (no Unity object is touched here: only the Job's arrays and Unity's value types).
        static ContactBake Compute(Job jb, out string error)
        {
            error = null;
            var sw = Stopwatch.StartNew();
            var lap = new StringBuilder();
            long t0 = 0;
            Action<string> Lap = what => { long t = sw.ElapsedMilliseconds; lap.Append(lap.Length > 0 ? " " : "").Append(what).Append(' ').Append(t - t0); t0 = t; };
            int V = jb.V;
            Vector3[] P = jb.P, N = jb.N;
            Vector2[] uv = jb.Uv;
            BoneWeight[] W = jb.W;
            var subs = jb.Subs;
            float[] zone = jb.Zone;
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
            Lap("weld");

            // 3. units (metres per mesh unit from Prepare), edge length, UV density
            float mpu = jb.Mpu;
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

            // 4. her frame in this mesh's bind space (Prepare), for the bend weights and the shin / biceps floors
            var frame = jb.Fr;

            // 5. bone class and tissue per vertex: hard depth and soft depth, blended over the bone weights like the class itself
            var boneCls = jb.BoneCls;
            var clsSeen = new int[ClassCount];
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
            // 0.11.7: the edge length of the skin itself. The upper body's mean (2.4 mm with Fidelity) is set by its very dense fingers
            // (56k of 75k vertices); her back and chest are about twice that.
            double skinSum = 0.0; int skinN = 0;
            for (int v = 0; v < V; v++)
            {
                if (canon[v] != (uint)v) continue;
                uint cv = cls[v];
                if (cv == 10 || cv == 11 || cv == 16 || cv == 17) continue;   // feet, hands
                for (uint k = adjOff[v]; k < adjOff[v + 1]; k++)
                {
                    uint u = adjV[k];
                    if (u > (uint)v) { skinSum += (P[v] - P[u]).magnitude; skinN++; }
                }
            }
            float skinEdge = skinN > 0 ? (float)(skinSum / skinN) * mpu : avgEdge;
            if (!(skinEdge > 1e-5f)) skinEdge = avgEdge;
            Lap("tissue");

            // 6. bend weights
            var bend = new Vector4[V];
            for (int v = 0; v < V; v++) bend[v] = new Vector4(-1f, 0f, -1f, 0f);
            string bendNote = "bend off (no " + (N == null ? "normals" : "character") + ")";
            int bendVerts = 0;
            if (N != null && jb.HasHer) bendVerts = BakeBend(frame, P, N, cls, canon, mpu, bend, out bendNote);

            // 6b. 0.11.31 skin folds and the chubby areas
            var fold = new Vector4[V];
            var fat = new Vector4[V];
            for (int v = 0; v < V; v++) { fold[v] = new Vector4(-1f, 0f, 0f, 0f); fat[v] = new Vector4(-1f, 0f, -1f, 0f); }
            int foldVerts = 0, fatVerts = 0;
            string foldNote = "", fatNote = "";
            if (N != null && frame.Ok) foldVerts = BakeFolds(frame, P, N, cls, canon, mpu, fold, out foldNote);
            if (N != null) fatVerts = BakeFat(frame, jb.Hf, P, N, cls, canon, mpu, fat, out fatNote);
            Lap("shape");

            // 7. a CPU skin with the current bones (matrices from Prepare; no blendshapes: the overlays carry the same "follow" deltas as the body
            // under them, so leaving them out on both keeps them matched): GpuContact's overlay -> body map
            var wp = new Vector3[V];
            var bm = jb.SkinM;
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
            Lap("skin");

            // 8. 0.11.8 muscles (body sheet only): ids + shares at the vertex UV, the belly weight over the mesh
            Vector4[] mus = null;
            float[] belly = null;
            int musN = 0;
            string musNote = "";
            if (jb.Mm != null && uv != null) musN = BakeMuscles(jb.SubOnSheet, jb.Mm, P, uv, subs, canon, adjOff, adjV, cls, mpu, out mus, out belly, out musNote);
            Lap("muscles");

            // 9. 0.11.28 Delta Mush at the joints (body skin meshes): the rest shape is her current one (the character and slider blendshapes,
            // never the pose correctives), so the detail it puts back is hers. 0.11.34: only baked while [Physics] DeltaMush is on (GpuContact
            // passes radius 0 otherwise and bakes again when it is switched on)
            MushData mush = null;
            string mushNote = "";
            if (jb.MushRadius > 0f && jb.MushRest != null && frame.Bp != null)   // 0.11.29: not frame.Ok (that needs the hips: the upper body has none)
            {
                mush = MushBake.Build(jb.MushRest, N, canon, adjOff, adjV, cls, subs, mpu, skinEdge, frame.Bp, jb.MushRadius, out mushNote);
                if (jb.MushShapes > 0) mushNote += " (rest with " + jb.MushShapes + " shapes)";
                Lap("mush");
            }

            var res = new ContactBake
            {
                R = jb.R, Mesh = jb.M, V = V, CanonCount = groups, Canon = canon, AdjOff = adjOff, AdjV = adjV, Tissue = tissue, Class = cls, Bend = bend,
                BendCount = bendVerts, Uv = uv, SubTris = subs.ToArray(), AvgEdge = avgEdge, SkinEdge = skinEdge, MetresPerUv = mPerUv, WorldP = wp,
                Muscle = musN > 0 ? mus : null, Belly = musN > 0 ? belly : null, MuscleCount = musN, Mush = mush,
                Fold = fold, FoldCount = foldVerts, Fat = fat, FatCount = fatVerts
            };
            sw.Stop();
            var sb = new StringBuilder();
            sb.Append("V ").Append(V).Append(" welded ").Append(V - groups).Append(", ").Append(totalTris).Append(" tris, adjacency ")
              .Append(groups > 0 ? ((double)outN / groups).ToString("0.0") : "0").Append("/vertex, edge ").Append((avgEdge * 1000f).ToString("0.0")).Append(" mm (skin ").Append((skinEdge * 1000f).ToString("0.0")).Append(" mm), ")
              .Append(mpu.ToString("0.####")).Append(" m/unit, classes");
            for (int c = 0; c < ClassCount; c++) if (clsSeen[c] > 0) sb.Append(' ').Append(ClassName(c)).Append(':').Append(clsSeen[c]);
            float tmin = float.MaxValue, tmax = 0f;
            for (int v = 0; v < V; v++) { float x = tissue[v].x + tissue[v].y; if (x < tmin) tmin = x; if (x > tmax) tmax = x; }
            sb.Append(", tissue ").Append((tmin * 1000f).ToString("0.#")).Append("-").Append((tmax * 1000f).ToString("0.#")).Append(" mm at softness 50 %, ").Append(frame.Note.Length > 0 ? frame.Note + ", " : "").Append(bendNote);
            if (mPerUv > 0f) sb.Append(", ").Append(mPerUv.ToString("0.###")).Append(" m/uv");
            if (musNote.Length > 0) sb.Append(", ").Append(musNote);
            if (mushNote.Length > 0) sb.Append(", ").Append(mushNote);
            if (foldNote.Length > 0) sb.Append(", ").Append(foldNote);
            if (fatNote.Length > 0) sb.Append(", ").Append(fatNote);
            sb.Append(", ").Append(sw.ElapsedMilliseconds).Append(" ms on a worker thread (").Append(lap).Append("), ").Append(jb.PrepMs).Append(" ms on the main thread");
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
        internal struct Frame
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
                // 0.11.34: every named bone's bind position read here, on the main thread: Bp is called by the bake's worker thread, which must
                // not touch a Transform (not even as a dictionary key: Unity's equality checks the native object)
                var at = new Dictionary<string, Vector3>();
                foreach (var kv in her.Bones)
                {
                    int i;
                    if (kv.Value != null && idx.TryGetValue(kv.Value, out i) && i < bind.Length) at[kv.Key] = bind[i].inverse.MultiplyPoint3x4(Vector3.zero);
                }
                Func<string, Vector3?> bp = name =>
                {
                    Vector3 p;
                    if (name == null || !at.TryGetValue(name, out p)) return null;
                    return p;
                };
                f.Bp = bp;
                Vector3? hl = bp("Hip_L"), hr = bp("Hip_R"), root = bp("Root_M");
                // 0.11.31: her upper body has no hips (its biceps bulge and waist roll were never baked): her left from the shoulders there
                bool shoulders = false;
                if ((!hl.HasValue || !hr.HasValue) && root.HasValue && bp("Shoulder_L").HasValue && bp("Shoulder_R").HasValue) { hl = bp("Shoulder_L"); hr = bp("Shoulder_R"); shoulders = true; }
                if (!hl.HasValue || !hr.HasValue) { f.Note = "no frame (no hips in this mesh)"; return f; }
                Vector3 rootP = root.HasValue ? root.Value : (hl.Value + hr.Value) * 0.5f;
                if (shoulders) rootP = root.Value;
                Vector3? upT = bp("Chest_M") ?? bp("Spine1_M") ?? bp("Neck_M") ?? bp("Head_M");
                if (!upT.HasValue || (upT.Value - rootP).sqrMagnitude < 1e-12f) { f.Note = "no frame (nothing above the pelvis in this mesh)"; return f; }
                Vector3 left = (hl.Value - hr.Value).normalized;
                Vector3 up = (upT.Value - rootP).normalized;
                Vector3 fwd = Vector3.Cross(up, left).normalized;
                up = Vector3.Cross(left, fwd).normalized;
                Vector3? toe = bp("Toes_L"), ankle = bp("Ankle_L");
                Vector3? nip = bp("Left_Nipple") ?? bp("Left_LowerBreast"), chest = bp("Chest_M");
                if (toe.HasValue && ankle.HasValue) { if (Vector3.Dot(toe.Value - ankle.Value, fwd) < 0f) fwd = -fwd; }
                else if (nip.HasValue && chest.HasValue) { if (Vector3.Dot(nip.Value - chest.Value, fwd) < 0f) fwd = -fwd; }   // 0.11.31: the breasts are in front
                else
                {
                    // 0.11.35 (log: 'no frame (no toes or breasts ...)': 0.11.31 left her lower body without bends, folds and chubby): the leg's
                    // front skin helper lies in front of the hip, the butt skin helper behind it
                    Vector3? lf = bp("LegFrontSkin_L"), bs = bp("ButtSkin_L"), hip = bp("Hip_L");
                    if (lf.HasValue && hip.HasValue) { if (Vector3.Dot(lf.Value - hip.Value, fwd) < 0f) fwd = -fwd; }
                    else if (bs.HasValue && hip.HasValue) { if (Vector3.Dot(bs.Value - hip.Value, fwd) > 0f) fwd = -fwd; }
                    else f.Note = "front unchecked";
                }
                if (shoulders) f.Note = "frame from the shoulders";
                f.Left = left; f.Up = up; f.Fwd = fwd; f.RootP = rootP; f.Ok = true;
                return f;
            }
        }

        // The slot weights per vertex from the rest pose in the mesh's own bind space. Returns the number of canonical vertices that got a weight.
        static int BakeBend(Frame fr, Vector3[] P, Vector3[] N, uint[] cls, uint[] canon, float mpu, Vector4[] bend, out string note)
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
                    // 0.9.4 (user: a line across her lower belly in a squat, like a scar): 0.8.1 pulled a narrow band 4 cm above the hips in
                    // (sigma 3.5 cm) and the bent normals drew it as a groove. A flexed hip pushes the lower belly forward into a soft roll instead:
                    // a broad outward bulge centred 7 cm above the hips (sigma 5 cm), no groove; the hip crease itself is the thigh-top slot's edge.
                    float y = Vector3.Dot(p - rootP, up) * mpu;
                    if (y >= -0.02f && y <= 0.17f) Push(ref b, 8, G(y, 0.07f, 0.05f) * sF);
                }
                // 0.11.23 (user: the waist should bunch into a fold on the side she bends toward; reference: a skin roll above the hip): on her
                // flank between the iliac crest and the ribs (her narrowest waist is ~22 cm above Root_M, the hip widens below ~15 cm) a soft
                // roll (centre 19 cm, sigma 3 cm) with the crease under it (15 cm, sigma 1.2 cm, inward), strongest on the side, fading toward
                // the front and the back. Slots 9 / 10 = her left / right, driven by the trunk's side bend toward that side.
                if (c == ClsPelvis || c == ClsSpine || c == ClsBelly || c == ClsChest)
                {
                    float y = Vector3.Dot(p - rootP, up) * mpu;
                    if (y >= 0.09f && y <= 0.28f)
                    {
                        float lat = Vector3.Dot(n, fr.Left);
                        int k = lat >= 0f ? 0 : 1;
                        float side = Mathf.Abs(lat), x = Mathf.Clamp01((side - 0.15f) / 0.6f);
                        float wl = x * x * (3f - 2f * x);
                        if (wl > 0f) Push(ref b, 9 + k, (G(y, 0.19f, 0.03f) - 0.6f * G(y, 0.15f, 0.012f)) * wl);
                    }
                }
                bend[v] = b;
                if (b.x >= 0f && canon[v] == (uint)v) { count++; perSlot[(int)b.x]++; if (b.z >= 0f) perSlot[(int)b.z]++; }
            }
            var sb = new StringBuilder("bend ").Append(count).Append(" verts [");
            for (int i = 0; i < perSlot.Length; i++) sb.Append(i > 0 ? " " : "").Append(perSlot[i]);
            note = sb.Append(']').ToString();
            return count;
        }

        // ---- 0.11.31 skin folds ----

        // Where the skin of a deeply bent joint folds in: a crease line with the flesh rolling up on both sides, in the mesh's bind space.
        // Inner elbow (the front of the arm in her A-pose: the crease across the joint), back of the knee, the groin (the line from the front
        // of the hip down and in to the pubis, as the wrinkle maps draw it). Profile across the line (d, metres): crease g(d, 0, sc), rolls
        // g(d, +-off, sr) x R; faded around the limb by how much the skin faces the bend. Weights in (slot, crease, roll); BendPose drives them.
        static int BakeFolds(Frame fr, Vector3[] P, Vector3[] N, uint[] cls, uint[] canon, float mpu, Vector4[] fold, out string note)
        {
            Func<string, Vector3?> bp = fr.Bp;
            int V = P.Length, count = 0;
            var per = new int[BendPose.FoldSlots];
            for (int k = 0; k < 2; k++)
            {
                string sd = k == 0 ? "_L" : "_R";
                Vector3? sh = bp("Shoulder" + sd), el = bp("Elbow" + sd), wr = bp("Wrist" + sd), hip = bp("Hip" + sd), kn = bp("Knee" + sd), an = bp("Ankle" + sd);
                if (!an.HasValue && hip.HasValue && kn.HasValue) an = kn.Value + (kn.Value - hip.Value) * 0.95f;   // her lower body has no ankle bone: the shin ~ the thigh's length
                Vector3 lat = fr.Left * (k == 0 ? 1f : -1f);   // outward on this side
                for (int v = 0; v < V; v++)
                {
                    if (canon[v] != (uint)v) continue;
                    int c = (int)cls[v];
                    Vector3 p = P[v], n = N[v];
                    float best = 0f; Vector4 w = new Vector4(-1f, 0f, 0f, 0f);
                    // inner elbow
                    if (sh.HasValue && el.HasValue && wr.HasValue && (c == ClsUpperArm + k || c == ClsForearm + k))
                    {
                        Vector3 ax = (wr.Value - sh.Value).normalized, q = p - el.Value;
                        float d = Vector3.Dot(q, ax) * mpu, r = (q - ax * Vector3.Dot(q, ax)).magnitude * mpu;
                        float g = Sm(0.15f, 0.6f, Vector3.Dot(n, fr.Fwd)) * (1f - Sm(0.05f, 0.08f, r));
                        Profile(d, 0f, 0.006f, 0.015f, 0.009f, 0.45f, g, k, ref best, ref w);
                    }
                    // back of the knee (the crease sits a little below the joint centre)
                    if (hip.HasValue && kn.HasValue && an.HasValue && (c == ClsThigh + k || c == ClsCalf + k))
                    {
                        Vector3 ax = (an.Value - hip.Value).normalized, q = p - kn.Value;
                        float d = Vector3.Dot(q, ax) * mpu, r = (q - ax * Vector3.Dot(q, ax)).magnitude * mpu;
                        float g = Sm(0.15f, 0.6f, -Vector3.Dot(n, fr.Fwd)) * (1f - Sm(0.08f, 0.11f, r));
                        Profile(d, 0.004f, 0.007f, 0.017f, 0.010f, 0.5f, g, 2 + k, ref best, ref w);
                    }
                    // groin: a line in her front plane from just outside and above the hip joint down and in toward the pubis
                    if (hip.HasValue && (c == ClsThigh + k || c == ClsPelvis || c == ClsBelly))
                    {
                        Vector3 q = p - hip.Value;
                        float x = Vector3.Dot(q, lat) * mpu, y = Vector3.Dot(q, fr.Up) * mpu;
                        Vector2 a = new Vector2(0.007f, 0.030f), b = new Vector2(-0.068f, -0.045f), e = b - a;
                        float u = Vector2.Dot(new Vector2(x, y) - a, e) / e.sqrMagnitude;
                        Vector2 on = a + e * Mathf.Clamp01(u), nrm2 = new Vector2(-e.y, e.x).normalized;
                        float d = Vector2.Dot(new Vector2(x, y) - on, nrm2);
                        float along = 1f - Sm(0f, 0.25f, Mathf.Max(-u, u - 1f));
                        float g = Sm(0.2f, 0.6f, Vector3.Dot(n, fr.Fwd)) * along;
                        Profile(d, 0f, 0.006f, 0.016f, 0.010f, 0.5f, g, 4 + k, ref best, ref w);
                    }
                    if (best > 0.02f && best > Mathf.Abs(fold[v].y) + Mathf.Abs(fold[v].z)) { if (fold[v].x < 0f) count++; fold[v] = w; per[(int)w.x]++; }
                }
            }
            for (int v = 0; v < V; v++) if (canon[v] != (uint)v) fold[v] = fold[canon[v]];
            var sb = new StringBuilder("folds ").Append(count).Append(" verts [");
            for (int i = 0; i < per.Length; i++) sb.Append(i > 0 ? " " : "").Append(per[i]);
            note = sb.Append(']').ToString();
            return count;
        }

        static void Profile(float d, float c, float sc, float off, float sr, float roll, float g, int slot, ref float best, ref Vector4 w)
        {
            if (g <= 0f) return;
            float cr = G(d, c, sc) * g, rl = (G(d, c - off, sr) + G(d, c + off, sr)) * roll * g;
            if (cr + rl > best) { best = cr + rl; w = new Vector4(slot, cr, rl, 0f); }
        }

        static float Sm(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        // ---- 0.11.31 chubby: soft fat by area ----

        // Where she puts on fat, as soft deposits in the bind pose (her frame and bones, metres): the lower belly's pooch and a softer upper
        // belly; love handles over the hip bones; the bra-line bulge and the lower back; full glutes; saddlebags and the inner thighs; the backs
        // of the upper arms; the calves all round. Each area's weight peaks at 1 on its fullest spot (the area's mm there); the GPU pushes
        // the skin out by the two strongest areas. Hands, feet, head, breasts and genitals carry none.
        static int BakeFat(Frame fr, HeadFrame hf, Vector3[] P, Vector3[] N, uint[] cls, uint[] canon, float mpu, Vector4[] fat, out string note)
        {
            Func<string, Vector3?> bp = fr.Bp ?? (n => (Vector3?)null);
            int V = P.Length, count = 0;
            if (!fr.Ok && !hf.Ok) { note = ""; return 0; }
            const int A = ContactSettings.FatAreas;
            var per = new int[A];
            var hip = new Vector3?[2]; var kn = new Vector3?[2]; var an = new Vector3?[2]; var sh = new Vector3?[2]; var el = new Vector3?[2]; var wr = new Vector3?[2];
            for (int k = 0; k < 2; k++)
            {
                string sd = k == 0 ? "_L" : "_R";
                hip[k] = bp("Hip" + sd); kn[k] = bp("Knee" + sd); an[k] = bp("Ankle" + sd); sh[k] = bp("Shoulder" + sd); el[k] = bp("Elbow" + sd); wr[k] = bp("Wrist" + sd);
                if (!an[k].HasValue && hip[k].HasValue && kn[k].HasValue) an[k] = kn[k].Value + (kn[k].Value - hip[k].Value) * 0.95f;
            }
            var w = new float[A];
            for (int v = 0; v < V; v++)
            {
                if (canon[v] != (uint)v) continue;
                int c = (int)cls[v];
                Vector3 p = P[v], n = N[v];
                for (int a = 0; a < A; a++) w[a] = 0f;
                // the face and the neck (area 7): fuller cheeks, a double chin under the jaw, a softer neck
                if ((c == ClsHead || c == ClsNeck) && hf.Ok)
                {
                    Vector3 q = p - hf.Head;
                    float up = Vector3.Dot(n, hf.Up), fw = Vector3.Dot(n, hf.Fwd);
                    float wf = 0f;
                    if (hf.HasCheeks)
                        for (int k = 0; k < 2; k++)
                        {
                            Vector3 ck = (k == 0 ? hf.CheekL : hf.CheekR) + hf.Up * (0.006f / mpu);   // her cheek bones sit at mouth height, 5 cm out
                            wf = Mathf.Max(wf, G((p - ck).magnitude * mpu, 0f, 0.02f) * Sm(-0.2f, 0.4f, fw));
                        }
                    if (hf.HasLip)
                    {
                        // under the chin: skin facing down, more than 2 cm under the lower lip (not the underside of her nose, lips or lower lids),
                        // in front of the jaw's hinge, near her midline
                        float yl = Vector3.Dot(p - hf.LipLow, hf.Up) * mpu, z = Vector3.Dot(p - hf.Jaw, hf.Fwd) * mpu, xj = Vector3.Dot(p - hf.Jaw, hf.Left) * mpu;
                        wf = Mathf.Max(wf, Sm(-0.015f, -0.03f, yl) * Sm(0.01f, 0.04f, z) * Sm(0.2f, 0.7f, -up) * G(xj, 0f, 0.045f));
                    }
                    if (c == ClsNeck && hf.HasNeck)
                    {
                        // the neck: fuller all round, most at the front under the chin
                        float t = Vector3.Dot(p - hf.Neck, hf.Head - hf.Neck) / Mathf.Max(1e-8f, (hf.Head - hf.Neck).sqrMagnitude);
                        wf = Mathf.Max(wf, 0.35f + 0.55f * Sm(0.1f, 0.6f, fw) * Sm(0.3f, 0.9f, t));
                    }
                    if (wf >= 0.01f) { fat[v] = new Vector4(7f, Mathf.Min(1f, wf), -1f, 0f); count++; per[7]++; }
                    continue;
                }
                if (!fr.Ok) continue;
                if (c == ClsHand || c == ClsHand + 1 || c == ClsFoot || c == ClsFoot + 1 || c == ClsHead || c == ClsNeck || c == ClsGenital
                    || c == ClsBreast || c == ClsBreast + 1 || c == ClsNone) continue;
                float fF = Vector3.Dot(n, fr.Fwd), fL = Vector3.Dot(n, fr.Left);
                float y = Vector3.Dot(p - fr.RootP, fr.Up) * mpu, x = Vector3.Dot(p - fr.RootP, fr.Left) * mpu;
                bool trunk = c == ClsPelvis || c == ClsSpine || c == ClsChest || c == ClsBelly || c == ClsScapula || c == ClsButt || c == ClsButt + 1;
                if (trunk)
                {
                    // 0.11.35 (user: the body hardly changes): each area is a soft layer over its whole side of the torso with the deposits on it,
                    // so the trunk rounds out everywhere and the deposits lead (front / side / back weights overlap, so the layer is continuous)
                    float front = Sm(-0.1f, 0.5f, fF), back = Sm(-0.1f, 0.5f, -fF), flank = Sm(0.25f, 0.75f, Mathf.Abs(fL));
                    // belly: the pooch under the navel and a softer upper belly, on a layer from the pubis to under the breasts
                    float vB = Sm(-0.07f, 0.0f, y) * Sm(0.27f, 0.19f, y);
                    w[0] = front * Mathf.Max(0.4f * vB, Mathf.Max(G(y, 0.05f, 0.06f) * G(x, 0f, 0.11f), 0.6f * G(y, 0.15f, 0.05f) * G(x, 0f, 0.09f)));
                    // waist: love handles over the hip bones, a little toward the back, on a layer up the flanks
                    float vW = Sm(-0.02f, 0.05f, y) * Sm(0.32f, 0.24f, y);
                    w[1] = flank * Mathf.Max(0.4f * vW, G(y, 0.15f, 0.05f)) * (0.8f + 0.2f * Sm(-0.4f, 0.3f, -fF));
                    // back: the bra-line bulge under the shoulder blades and the lower back, on a layer up the back
                    float vK = Sm(-0.02f, 0.05f, y) * Sm(0.46f, 0.38f, y);
                    w[2] = back * Mathf.Max(0.35f * vK, Mathf.Max(G(y, 0.33f, 0.04f) * (0.6f + 0.4f * Sm(0.04f, 0.10f, Mathf.Abs(x))), 0.65f * G(y, 0.09f, 0.05f)));
                    // glutes: round them out (the butt helpers' area and the low back of the pelvis)
                    if (c == ClsButt || c == ClsButt + 1 || (c == ClsPelvis && y < 0.06f)) w[3] = back * (c == ClsPelvis ? Sm(0.06f, -0.04f, y) : 1f);
                }
                for (int k = 0; k < 2; k++)
                {
                    Vector3 lat = fr.Left * (k == 0 ? 1f : -1f);
                    float t;
                    // 0.11.35 (user: the arms' fat only inflated the upper arm): whole limbs. A layer along the limb, thicker high up, tapering
                    // to the wrist / toward the foot mesh's seam; the deposits on it; thigh and calf share the knee so it has no step
                    if ((c == ClsThigh + k || c == ClsButt + k || (c == ClsPelvis && Vector3.Dot(p - fr.RootP, lat) > 0f)) && hip[k].HasValue && kn[k].HasValue && Along(p, hip[k].Value, kn[k].Value, out t))
                    {
                        float out_ = Vector3.Dot(n, lat), inner = -out_;
                        // saddlebags (outer upper thigh) and the inner thigh on a layer down to the knee
                        float tw = Mathf.Max(G(t, 0.28f, 0.22f) * Sm(0.1f, 0.7f, out_), 0.85f * G(t, 0.25f, 0.25f) * Sm(0.1f, 0.7f, inner));
                        tw = Mathf.Max(tw, 0.55f * (1f - 0.4f * Sm(0.6f, 1.0f, t)));
                        if (c == ClsThigh + k)
                        {
                            float kk = 0.5f * Sm(0.85f, 1.0f, t);   // toward the knee: half of it the calves' area
                            w[4] = Mathf.Max(w[4], tw * (1f - kk));
                            w[6] = Mathf.Max(w[6], tw * kk);
                            // the fold under the butt fills a little with the glutes
                            w[3] = Mathf.Max(w[3], 0.6f * G(t, 0.05f, 0.08f) * Sm(0.1f, 0.6f, -fF));
                        }
                        else w[4] = Mathf.Max(w[4], tw * Sm(-0.05f, 0.05f, t));   // the pelvis / butt only where the thigh starts
                    }
                    if (c == ClsCalf + k && kn[k].HasValue && an[k].HasValue && Along(p, kn[k].Value, an[k].Value, out t))
                    {
                        // all round, more at the back; gone before the lower calf (her foot mesh carries it, without a frame of its own)
                        float cb = 0.5f * (0.7f + 0.3f * Sm(0f, 0.6f, -fF)) * Sm(0.65f, 0.4f, t);
                        float kc = 0.5f * Sm(0.12f, 0f, t);    // at the knee: half of it the thighs' area
                        w[6] = Mathf.Max(w[6], cb * (1f - kc));
                        w[4] = Mathf.Max(w[4], cb * kc);
                    }
                    // arms: upper arm and forearm as one (s 0 shoulder .. 1 elbow .. 2 wrist), the soft back of the upper arm on top
                    float s = -1f;
                    if (c == ClsUpperArm + k && sh[k].HasValue && el[k].HasValue && Along(p, sh[k].Value, el[k].Value, out t)) s = Mathf.Clamp(t, 0f, 1f);
                    else if (c == ClsForearm + k && el[k].HasValue && wr[k].HasValue && Along(p, el[k].Value, wr[k].Value, out t)) s = 1f + Mathf.Clamp(t, 0f, 1f);
                    if (s >= 0f)
                    {
                        float baseA = s < 1f ? Mathf.Lerp(0.75f, 0.6f, s) * Sm(0f, 0.2f, s) : 0.6f * (1f - Sm(0.35f, 0.9f, s - 1f));
                        float wing = 0.4f * G(s, 0.5f, 0.3f) * Sm(-0.2f, 0.6f, -fF + 0.3f * Vector3.Dot(n, lat));
                        w[5] = Mathf.Max(w[5], Mathf.Min(1f, baseA + wing));
                    }
                }
                int a0 = -1, a1 = -1;
                for (int a = 0; a < A; a++)
                {
                    if (w[a] < 0.01f) continue;
                    if (a0 < 0 || w[a] > w[a0]) { a1 = a0; a0 = a; }
                    else if (a1 < 0 || w[a] > w[a1]) a1 = a;
                }
                if (a0 < 0) continue;
                fat[v] = new Vector4(a0, w[a0], a1, a1 >= 0 ? w[a1] : 0f);
                count++; per[a0]++;
            }
            for (int v = 0; v < V; v++) if (canon[v] != (uint)v) fat[v] = fat[canon[v]];
            var sb = new StringBuilder("chubby ").Append(count).Append(" verts [");
            for (int i = 0; i < A; i++) sb.Append(i > 0 ? " " : "").Append(per[i]);
            note = sb.Append(']').ToString();
            return count;
        }

        // 0.11.31: her head's frame in this mesh's bind space (for the face / neck fat). The neck mesh has no left / right bones, so the frame
        // comes from her live bones (cheeks left / right, neck -> head up, the cheeks in front of the head joint), expressed in Head_M's own
        // space and carried into the mesh by Head_M's bind pose; positions from the mesh's own bind poses where it has the bone.
        internal struct HeadFrame
        {
            public bool Ok, HasCheeks, HasJaw, HasNeck, HasLip;
            public Vector3 Head, Neck, Jaw, CheekL, CheekR, LipLow, Up, Left, Fwd;

            public static HeadFrame Make(Transform[] bones, Matrix4x4[] bind, Character her)
            {
                var f = new HeadFrame();
                if (her == null) return f;
                Transform head, neck, cl, cr;
                if (!her.Bones.TryGetValue("Head_M", out head) || !her.Bones.TryGetValue("Neck_M", out neck) || head == null || neck == null) return f;
                if (!her.Bones.TryGetValue("Cheek_Left", out cl) || !her.Bones.TryGetValue("Cheek_Right", out cr) || cl == null || cr == null) return f;
                int ih = -1;
                for (int i = 0; i < bones.Length; i++) if (bones[i] == head) { ih = i; break; }
                if (ih < 0 || ih >= bind.Length) return f;
                Vector3 upW = (head.position - neck.position).normalized, leftW = cl.position - cr.position;
                leftW = (leftW - upW * Vector3.Dot(leftW, upW)).normalized;
                Vector3 fwdW = Vector3.Cross(upW, leftW);
                if (Vector3.Dot((cl.position + cr.position) * 0.5f - head.position, fwdW) < 0f) fwdW = -fwdW;
                Matrix4x4 toMesh = bind[ih].inverse;   // Head_M's local space -> this mesh's bind space
                Func<Vector3, Vector3> dir = w => toMesh.MultiplyVector(head.InverseTransformDirection(w)).normalized;
                f.Up = dir(upW); f.Left = dir(leftW); f.Fwd = dir(fwdW);
                f.Head = toMesh.MultiplyPoint3x4(Vector3.zero);
                Func<Transform, Vector3?> at = t =>
                {
                    for (int i = 0; i < bones.Length && i < bind.Length; i++) if (bones[i] == t) return bind[i].inverse.MultiplyPoint3x4(Vector3.zero);
                    return toMesh.MultiplyPoint3x4(head.InverseTransformPoint(t.position));   // not on this mesh: from the live pose, in the head's frame
                };
                f.Neck = at(neck).Value; f.HasNeck = true;
                f.CheekL = at(cl).Value; f.CheekR = at(cr).Value; f.HasCheeks = true;
                Transform jaw;
                if (her.Bones.TryGetValue("Jaw_M", out jaw) && jaw != null) { f.Jaw = at(jaw).Value; f.HasJaw = true; }
                Transform lip;
                if (f.HasJaw && her.Bones.TryGetValue("Lip_Lower_Insite", out lip) && lip != null) { f.LipLow = at(lip).Value; f.HasLip = true; }
                f.Ok = true;
                return f;
            }
        }

        // ---- 0.11.8 muscles as geometry ----

        // The muscle maps read back once (GpuContact.MuscleMapFor): the region ids / share / coverage and the detail's groove depth.
        internal sealed class MuscleMap
        {
            public MuscleLayer Layer;
            public Color32[] Region, Detail;
            public int RW, RH, DW, DH;
            // 0.11.12: the authored height (m) per texel and each muscle's peak (m, the highest texel it owns): the geometry is height / peak
            public float[] Height; public int HW, HH; public float[] Peak;
            public float HeightAt(Vector2 t)
            {
                float x = t.x * HW - 0.5f, y = t.y * HH - 0.5f;
                int x0 = Mathf.Clamp(Mathf.FloorToInt(x), 0, HW - 1), y0 = Mathf.Clamp(Mathf.FloorToInt(y), 0, HH - 1);
                int x1 = Mathf.Min(x0 + 1, HW - 1), y1 = Mathf.Min(y0 + 1, HH - 1);
                float fx = Mathf.Clamp01(x - x0), fy = Mathf.Clamp01(y - y0);
                float a = Mathf.Lerp(Height[y0 * HW + x0], Height[y0 * HW + x1], fx), b = Mathf.Lerp(Height[y1 * HW + x0], Height[y1 * HW + x1], fx);
                return Mathf.Lerp(a, b, fy);
            }
        }

        // How far a muscle's bulge ramps up from its edge (m) by area: an arm muscle is narrow, a thigh / the back broad.
        static float RampOf(string area)
        {
            return area == "arms" ? 0.025f : area == "legs" ? 0.04f : area == "back" ? 0.035f : 0.03f;
        }

        // Per canonical vertex of the body-atlas submeshes: the region texel under its UV (two ids, the first one's share), then the
        // belly weight: geodesic distance (Dijkstra over the welded adjacency, bind positions in metres) to the nearest vertex of another
        // muscle (or none), smoothstep over the area's ramp, x (1 - 0.7 groove) so the linea alba / spinal groove stay a valley, two
        // smoothing passes (the region is point-sampled at ~1 cm: ids step from texel to texel). A mesh's open border (the waist cut between
        // her upper and lower body) is not an edge: the bulge carries across it. Hands, feet, head, breasts and genitals carry none (0.11.23:
        // the neck does: the neck muscles and the traps' neck part; a neck vertex without a muscle texel under it stays at 0).
        static int BakeMuscles(bool[] sheet, MuscleMap mm, Vector3[] P, Vector2[] uv, List<int[]> subs, uint[] canon, uint[] adjOff, uint[] adjV,
                               uint[] cls, float mpu, out Vector4[] mus, out float[] belly, out string note)
        {
            int V = P.Length;
            mus = new Vector4[V];
            belly = new float[V];
            note = "";
            for (int v = 0; v < V; v++) mus[v] = new Vector4(-1f, 0f, -1f, 0f);
            var L = mm.Layer;
            if (L == null || mm.Region == null || mm.RW <= 0 || mm.RH <= 0) { note = "muscles: no map"; return 0; }
            // the body-atlas submeshes (Prepare: Wrinkles' class 3, her body / skin materials, not the foot's own texture)
            var onSheet = new bool[V];
            var uvOn = new bool[V];   // this copy's UV is on the body atlas
            int subsOn = 0;
            for (int si = 0; si < subs.Count && sheet != null && si < sheet.Length; si++)
            {
                if (!sheet[si]) continue;
                subsOn++;
                var tr = subs[si];
                for (int i = 0; i < tr.Length; i++) { onSheet[canon[tr[i]]] = true; uvOn[tr[i]] = true; }
            }
            if (subsOn == 0) { note = "muscles: not on the body sheet"; return 0; }
            // 1. the dominant muscle per canonical vertex (0 = none), its pair and share
            // 0.11.12 (user: seams in the bulge): every welded copy's UV is tried and the texel with the most coverage wins, and a texel the
            // author's dilation filled (an island's edge: coverage 0 but a copied id) counts. Before, a vertex on a UV seam often read only the
            // dilated rim, was dropped as "no muscle" and every UV seam drew a groove down the bulge (the leg sides, the calves).
            var dom = new int[V];
            var bestA = new int[V];
            var bestUv = new Vector2[V];   // 0.11.12: the UV the winning texel came from (the height is read there)
            for (int v = 0; v < V; v++) { bestA[v] = -1; bestUv[v] = uv[v]; }
            for (int v = 0; v < V; v++)
            {
                int cv = (int)canon[v];
                if (!onSheet[cv] || !uvOn[v]) continue;
                uint c = cls[cv];
                if (c == ClsHead || c == ClsHand || c == ClsHand + 1 || c == ClsFoot || c == ClsFoot + 1 || c == ClsBreast || c == ClsBreast + 1 || c == ClsGenital) continue;
                Vector2 t = uv[v];
                if (!(t.x >= 0f && t.x <= 1f && t.y >= 0f && t.y <= 1f)) continue;
                int px = Mathf.Clamp((int)(t.x * mm.RW), 0, mm.RW - 1), py = Mathf.Clamp((int)(t.y * mm.RH), 0, mm.RH - 1);
                var q = mm.Region[py * mm.RW + px];
                int i1 = q.r, i2 = q.g;
                if (i1 < 1 || i1 > L.Count || i1 > MuscleFlex.Max || q.a <= bestA[cv]) continue;
                bestA[cv] = q.a; bestUv[cv] = t;
                float sh = q.b / 255f;
                if (i2 < 1 || i2 > L.Count || i2 > MuscleFlex.Max) { i2 = 0; sh = 1f; }
                dom[cv] = sh >= 0.5f || i2 == 0 ? i1 : i2;
                mus[cv] = new Vector4(i1 - 1, sh, i2 > 0 ? i2 - 1 : -1f, i2 > 0 ? 1f - sh : 0f);
            }
            // a lone vertex without a muscle inside one (a missed texel): it takes the muscle most of its neighbours have
            for (int pass = 0; pass < 2; pass++)
                for (int v = 0; v < V; v++)
                {
                    if (dom[v] != 0 || canon[v] != (uint)v || !onSheet[v] || bestA[v] >= 0) continue;
                    uint c = cls[v];
                    if (c == ClsHead || c == ClsHand || c == ClsHand + 1 || c == ClsFoot || c == ClsFoot + 1 || c == ClsBreast || c == ClsBreast + 1 || c == ClsGenital) continue;
                    int n = 0, best = 0, bestN = 0;
                    for (uint k = adjOff[v]; k < adjOff[v + 1]; k++)
                    {
                        int d = dom[adjV[k]];
                        n++;
                        if (d == 0) continue;
                        int cnt = 0;
                        for (uint k2 = adjOff[v]; k2 < adjOff[v + 1]; k2++) if (dom[adjV[k2]] == d) cnt++;
                        if (cnt > bestN) { bestN = cnt; best = (int)adjV[k]; }
                    }
                    if (n > 0 && bestN * 3 >= n * 2) { dom[v] = dom[best]; mus[v] = mus[best]; bestA[v] = 0; }
                }
            // 0.11.12 (user: the muscles should approach a sculpted athletic body): with the authored height map the belly weight is the
            // sculpt itself, height / the muscle's peak: separate abdominal bellies with the linea alba and the tendinous lines between them,
            // the serratus fingers, the quad teardrop, the calf heads, grooves going in (negative) as the muscle tenses. The uniform
            // geodesic pillow below stays for a bundle without the map.
            if (mm.Height != null && mm.Peak != null)
            {
                int nh = 0;
                var bh = new float[V];
                for (int v = 0; v < V; v++)
                {
                    if (dom[v] == 0) continue;
                    nh++;
                    bh[v] = Mathf.Clamp(mm.HeightAt(bestUv[v]) / mm.Peak[dom[v] - 1], -0.8f, 1.25f);
                }
                if (nh == 0) { note = "muscles: none under her UVs"; return 0; }
                // one light pass (the map is ~1 cm per 5 texels: bilinear already; this only takes the texel stair off coarse meshes)
                var bs = new float[V];
                for (int v = 0; v < V; v++)
                {
                    if (dom[v] == 0) continue;
                    uint o0 = adjOff[v], o1 = adjOff[v + 1];
                    float sum = 0f; int n = 0;
                    for (uint k = o0; k < o1; k++) { int u = (int)adjV[k]; if (dom[u] != 0) { sum += bh[u]; n++; } }
                    bs[v] = n > 0 ? 0.6f * bh[v] + 0.4f * sum / n : bh[v];
                }
                return FinishMuscles(L, dom, mus, bs, canon, V, belly, true, out note);
            }
            // 2. geodesic distance to the nearest muscle edge (seeds: a muscle vertex next to another muscle or to none)
            var dist = new float[V];
            var heap = new MinHeap(4096);
            int musN = 0;
            for (int v = 0; v < V; v++)
            {
                dist[v] = float.MaxValue;
                if (dom[v] == 0) continue;
                musN++;
                bool edge = false;
                for (uint k = adjOff[v]; k < adjOff[v + 1] && !edge; k++) if (dom[adjV[k]] != dom[v]) edge = true;
                if (edge) { dist[v] = 0f; heap.Push(0f, v); }
            }
            if (musN == 0) { note = "muscles: none under her UVs"; return 0; }
            float d0;
            int u0;
            while (heap.Pop(out d0, out u0))
            {
                if (d0 > dist[u0]) continue;
                for (uint k = adjOff[u0]; k < adjOff[u0 + 1]; k++)
                {
                    int w = (int)adjV[k];
                    if (dom[w] != dom[u0]) continue;   // stays inside the muscle
                    float nd = d0 + (P[u0] - P[w]).magnitude * mpu;
                    if (nd < dist[w]) { dist[w] = nd; heap.Push(nd, w); }
                }
            }
            // 3. ramp, grooves, smoothing
            var b0 = new float[V];
            for (int v = 0; v < V; v++)
            {
                if (dom[v] == 0) continue;
                float ramp = RampOf(L.Area[dom[v] - 1]);
                float d = dist[v] == float.MaxValue ? ramp : dist[v];   // a muscle island with no edge on this mesh (cut by its border): full
                float x = Mathf.Clamp01(d / ramp);
                float g = 1f;
                if (mm.Detail != null && mm.DW > 0 && mm.DH > 0)
                {
                    Vector2 t = uv[v];
                    int px = Mathf.Clamp((int)(t.x * mm.DW), 0, mm.DW - 1), py = Mathf.Clamp((int)(t.y * mm.DH), 0, mm.DH - 1);
                    g = 1f - 0.7f * (mm.Detail[py * mm.DW + px].b / 255f);
                }
                b0[v] = x * x * (3f - 2f * x) * g;
            }
            var b1 = new float[V];
            for (int pass = 0; pass < 2; pass++)
            {
                for (int v = 0; v < V; v++)
                {
                    if (dom[v] == 0) { b1[v] = 0f; continue; }
                    uint o0 = adjOff[v], o1 = adjOff[v + 1];
                    float sum = 0f;
                    for (uint k = o0; k < o1; k++) sum += b0[adjV[k]];
                    b1[v] = o1 > o0 ? 0.5f * b0[v] + 0.5f * sum / (o1 - o0) : b0[v];
                }
                var tmp = b0; b0 = b1; b1 = tmp;
            }
            return FinishMuscles(L, dom, mus, b0, canon, V, belly, false, out note);
        }

        // Welded copies take their canonical's values; the bake note per area.
        static int FinishMuscles(MuscleLayer L, int[] dom, Vector4[] mus, float[] b, uint[] canon, int V, float[] belly, bool sculpt, out string note)
        {
            var perArea = new Dictionary<string, int>();
            int musN = 0;
            float lo = 0f, hi = 0f;
            for (int v = 0; v < V; v++)
            {
                int c = (int)canon[v];
                belly[v] = dom[c] != 0 ? (sculpt ? b[c] : Mathf.Clamp01(b[c])) : 0f;
                mus[v] = dom[c] != 0 ? mus[c] : new Vector4(-1f, 0f, -1f, 0f);
                if (c == v && dom[v] != 0)
                {
                    musN++;
                    string a = L.Area[dom[v] - 1]; int n; perArea.TryGetValue(a, out n); perArea[a] = n + 1;
                    lo = Mathf.Min(lo, belly[v]); hi = Mathf.Max(hi, belly[v]);
                }
            }
            var sb = new StringBuilder("muscles ").Append(musN).Append(" verts [");
            bool first = true;
            foreach (var kv in perArea) { sb.Append(first ? "" : " ").Append(kv.Key).Append(':').Append(kv.Value); first = false; }
            sb.Append(']');
            if (sculpt) sb.Append(" sculpted, belly ").Append(lo.ToString("0.00")).Append(" to ").Append(hi.ToString("0.00"));
            note = sb.ToString();
            return musN;
        }

        // A binary min-heap of (distance, vertex) for the geodesic belly distance (no PriorityQueue in this runtime).
        sealed class MinHeap
        {
            float[] _k; int[] _v; int _n;
            public MinHeap(int cap) { _k = new float[cap]; _v = new int[cap]; }
            public void Push(float k, int v)
            {
                if (_n == _k.Length) { Array.Resize(ref _k, _n * 2); Array.Resize(ref _v, _n * 2); }
                int i = _n++;
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (_k[p] <= k) break;
                    _k[i] = _k[p]; _v[i] = _v[p]; i = p;
                }
                _k[i] = k; _v[i] = v;
            }
            public bool Pop(out float k, out int v)
            {
                k = 0f; v = 0;
                if (_n == 0) return false;
                k = _k[0]; v = _v[0];
                float lk = _k[--_n]; int lv = _v[_n];
                int i = 0;
                while (true)
                {
                    int c = 2 * i + 1;
                    if (c >= _n) break;
                    if (c + 1 < _n && _k[c + 1] < _k[c]) c++;
                    if (_k[c] >= lk) break;
                    _k[i] = _k[c]; _v[i] = _v[c]; i = c;
                }
                _k[i] = lk; _v[i] = lv;
                return true;
            }
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
