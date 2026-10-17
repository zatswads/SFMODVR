using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // 0.11.28 Delta Mush at her joints (research-joint-correctives.md 4a; user: her joints pinch and flatten in deep bends). Linear skinning
    // averages the bone matrices at a joint, so the skin there collapses toward the bone (elbow inside, back of the knee, the hip / groin fold,
    // the armpit) and twists flat. Delta Mush (Mancewicz et al. 2014) as a post-pass on the skinned buffer: smooth the skinned positions K
    // times (which removes the collapse, and the detail with it), then put the rest-pose detail back: per vertex the offset from its smoothed
    // rest position, stored in a frame built from the smoothed surface (two fixed neighbours), so the detail turns with the smoothed skin.
    //
    // This bake (CPU, once per GpuContact bake of a body skin mesh): the joint weight per canonical vertex, the vertex lists the GPU passes
    // run on, and the rest data. The GPU runs the SAME smoothing on the same lists (MushGather: rings <= K+1 copied into both ping-pong buffers,
    // MushSmooth x K over rings <= K), so in the rest pose the reconstruction gives every core vertex back exactly (identity at rest); an error
    // from the frozen ring K+1 moves one ring inward per pass and never reaches ring 0, the weighted core.
    internal sealed class MushData
    {
        public int K;                 // smoothing passes (sized from the radius by the skin's edge length)
        public float Radius;          // the radius it was sized for (m)
        public uint[] Gather;         // canonical ids within K+1 rings of the core
        public uint[] Smooth;         // canonical ids within K rings
        public uint[] Core;           // canonical ids with a weight
        public uint[] All;            // every vertex id (welded copies too) whose canonical is in Core
        public Vector4[] Delta;       // V (canonical ids): xyz = rest offset from the smoothed rest position in the frame (t, b, n), metres; w = weight 0..1
        public Vector4[] Nrm;         // V: the rest normal in the same frame
        public uint[] Nb;             // 2V: the two frame neighbours of a core vertex
        public string Note = "";
    }

    internal static class MushBake
    {
        // The joints she bends and how far their collapse reaches (Gaussian sigma, m, around the joint centre in the bind pose). The wrists
        // and ankles are left out: the hands and feet are dense meshes (the passes would cover the fingers) and barely collapse.
        static readonly string[] Joints = { "Shoulder", "Elbow", "Hip", "Knee" };
        static readonly float[] Sigma = { 0.085f, 0.065f, 0.11f, 0.08f };
        const int MaxK = 48, MinK = 4;
        const float BoundaryFade = 0.025f;   // the weight fades to 0 toward an open mesh border (seams with the other skin meshes, the vulva hole)

        // P, N: bind positions / normals in mesh units (P with her current rest-shape blendshapes); cls: bone class per vertex; mpu: metres per unit;
        // edge: the skin's mean edge (m); bp: a bone's bind position in this mesh's space. null + note when nothing qualifies.
        public static MushData Build(Vector3[] P, Vector3[] N, uint[] canon, uint[] adjOff, uint[] adjV, uint[] cls, List<int[]> subs, float mpu, float edge,
                                     Func<string, Vector3?> bp, float radius, out string note)
        {
            int V = P.Length;
            if (N == null || N.Length != V || bp == null) { note = "delta mush: no normals / frame"; return null; }
            // 1. joint weight (canonical ids)
            var jp = new List<Vector3>(); var js = new List<float>();
            for (int j = 0; j < Joints.Length; j++)
                for (int k = 0; k < 2; k++)
                {
                    Vector3? p = bp(Joints[j] + (k == 0 ? "_L" : "_R"));
                    if (p.HasValue) { jp.Add(p.Value); js.Add(Sigma[j] / Mathf.Max(1e-6f, mpu)); }
                }
            if (jp.Count == 0) { note = "delta mush: no joints in this mesh"; return null; }
            var w = new float[V];
            int coreN = 0;
            for (int v = 0; v < V; v++)
            {
                if (canon[v] != (uint)v) continue;
                int c = (int)cls[v];
                if (c == ContactBake.ClsHand || c == ContactBake.ClsHand + 1 || c == ContactBake.ClsFoot || c == ContactBake.ClsFoot + 1 || c == ContactBake.ClsHead
                    || c == ContactBake.ClsNeck || c == ContactBake.ClsGenital || c == ContactBake.ClsBreast || c == ContactBake.ClsBreast + 1) continue;
                float g = 0f;
                for (int j = 0; j < jp.Count; j++) { float x = (P[v] - jp[j]).magnitude / js[j]; g = Mathf.Max(g, Mathf.Exp(-x * x)); }
                float wv = Smooth01(0.08f, 0.5f, g);   // a plateau around the joint, fading out by ~1.6 sigma
                w[v] = wv;
            }
            // 2. fade toward the mesh's open borders (edges of one triangle): there the next skin mesh carries on without our smoothing
            var useCount = new Dictionary<ulong, int>();
            foreach (var tr in subs)
                for (int i = 0; i + 2 < tr.Length; i += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        uint a = canon[tr[i + e]], b = canon[tr[i + (e + 1) % 3]];
                        if (a == b) continue;
                        ulong key = a < b ? ((ulong)a << 32) | b : ((ulong)b << 32) | a;
                        int n; useCount.TryGetValue(key, out n); useCount[key] = n + 1;
                    }
            var border = new List<uint>();
            var isBorder = new bool[V];
            foreach (var kv in useCount)
                if (kv.Value == 1)
                {
                    uint a = (uint)(kv.Key >> 32), b = (uint)(kv.Key & 0xffffffffu);
                    if (!isBorder[a]) { isBorder[a] = true; border.Add(a); }
                    if (!isBorder[b]) { isBorder[b] = true; border.Add(b); }
                }
            int fadeRings = Mathf.Max(1, Mathf.CeilToInt(BoundaryFade / Mathf.Max(1e-4f, edge)));
            var bd = Rings(V, border, adjOff, adjV, fadeRings);
            var core = new List<uint>();
            for (int v = 0; v < V; v++)
            {
                if (w[v] <= 0f) continue;
                if (bd[v] >= 0) w[v] *= Smooth01(0f, 1f, bd[v] / (float)fadeRings);
                if (w[v] > 0.01f) core.Add((uint)v); else w[v] = 0f;
            }
            if (core.Count == 0) { note = "delta mush: no joint skin in this mesh"; return null; }
            // 3. passes: a pass spreads ~ edge / sqrt(2), so K = 2 (radius / edge)^2, capped
            int K = Mathf.Clamp(Mathf.RoundToInt(2f * (radius / Mathf.Max(1e-4f, edge)) * (radius / Mathf.Max(1e-4f, edge))), MinK, MaxK);
            var ring = Rings(V, core, adjOff, adjV, K + 1);
            var gather = new List<uint>(); var smooth = new List<uint>();
            for (int v = 0; v < V; v++)
            {
                if (ring[v] < 0) continue;
                gather.Add((uint)v);
                if (ring[v] <= K) smooth.Add((uint)v);
            }
            // 4. the rest smoothing, exactly as the GPU does it (ping-pong, ring K+1 frozen); flat arrays: Vector3 operators made this take seconds
            var ax = new float[V]; var ay = new float[V]; var az = new float[V];
            var bx = new float[V]; var by = new float[V]; var bz = new float[V];
            foreach (uint v in gather) { ax[v] = bx[v] = P[v].x; ay[v] = by[v] = P[v].y; az[v] = bz[v] = P[v].z; }
            var sm = smooth.ToArray();
            float[] sx = ax, sy = ay, sz = az, dx = bx, dy = by, dz = bz;
            for (int pass = 0; pass < K; pass++)
            {
                for (int i = 0; i < sm.Length; i++)
                {
                    uint v = sm[i];
                    uint o0 = adjOff[v], o1 = adjOff[v + 1];
                    if (o1 <= o0) { dx[v] = sx[v]; dy[v] = sy[v]; dz[v] = sz[v]; continue; }
                    float tx = 0f, ty = 0f, tz = 0f;
                    for (uint k = o0; k < o1; k++) { uint u = adjV[k]; tx += sx[u]; ty += sy[u]; tz += sz[u]; }
                    float inv = 0.5f / (o1 - o0);
                    dx[v] = 0.5f * sx[v] + tx * inv; dy[v] = 0.5f * sy[v] + ty * inv; dz[v] = 0.5f * sz[v] + tz * inv;
                }
                var t1 = sx; sx = dx; dx = t1; t1 = sy; sy = dy; dy = t1; t1 = sz; sz = dz; dz = t1;
            }
            var S = new Vector3[V];   // after K passes
            foreach (uint v in gather) S[v] = new Vector3(sx[v], sy[v], sz[v]);
            // 5. frames: two neighbours spanning the tangent plane well, ordered so their cross product points out of her skin
            var delta = new Vector4[V]; var nrm = new Vector4[V]; var nb = new uint[2 * V];
            var coreOk = new List<uint>(core.Count);
            int weak = 0;
            foreach (uint v in core)
            {
                uint o0 = adjOff[v], o1 = adjOff[v + 1];
                Vector3 n0 = N[v].normalized;
                if (o1 - o0 < 2 || n0.sqrMagnitude < 0.5f) { weak++; continue; }
                Vector3 sv = S[v];
                int i0 = -1; float l0 = 0f;
                for (uint k = o0; k < o1; k++) { Vector3 e = S[adjV[k]] - sv; e -= n0 * Vector3.Dot(e, n0); float l = e.sqrMagnitude; if (l > l0) { l0 = l; i0 = (int)adjV[k]; } }
                if (i0 < 0 || l0 < 1e-16f) { weak++; continue; }
                Vector3 e0 = S[i0] - sv, e0p = (e0 - n0 * Vector3.Dot(e0, n0)).normalized;
                int i1 = -1; float best = 0f;
                for (uint k = o0; k < o1; k++)
                {
                    int u = (int)adjV[k];
                    if (u == i0) continue;
                    Vector3 e = S[u] - sv; e -= n0 * Vector3.Dot(e, n0);
                    if (e.sqrMagnitude < 1e-16f) continue;
                    float sn = Mathf.Abs(Vector3.Dot(Vector3.Cross(e0p, e.normalized), n0));
                    if (sn > best) { best = sn; i1 = u; }
                }
                if (i1 < 0 || best < 0.3f) { weak++; continue; }
                Vector3 fn, ft, fb;
                if (!Frame(S[i0] - sv, S[i1] - sv, n0, out fn, out ft, out fb)) { weak++; continue; }
                Vector3 d = (P[v] - sv) * mpu;
                delta[v] = new Vector4(Vector3.Dot(d, ft), Vector3.Dot(d, fb), Vector3.Dot(d, fn), w[v]);
                nrm[v] = new Vector4(Vector3.Dot(n0, ft), Vector3.Dot(n0, fb), Vector3.Dot(n0, fn), 0f);
                nb[2 * v] = (uint)i0; nb[2 * v + 1] = (uint)i1;
                coreOk.Add(v);
            }
            if (coreOk.Count == 0) { note = "delta mush: no usable frames"; return null; }
            var inCore = new bool[V];
            foreach (uint v in coreOk) inCore[v] = true;
            var all = new List<uint>();
            for (int v = 0; v < V; v++) if (inCore[canon[v]]) all.Add((uint)v);
            float wsum = 0f; foreach (uint v in coreOk) wsum += delta[v].w;
            var res = new MushData { K = K, Radius = radius, Gather = gather.ToArray(), Smooth = smooth.ToArray(), Core = coreOk.ToArray(), All = all.ToArray(), Delta = delta, Nrm = nrm, Nb = nb };
            note = res.Note = "delta mush " + coreOk.Count + " joint verts (mean weight " + (wsum / coreOk.Count).ToString("0.00") + ", " + jp.Count + " joints), " + K + " passes over "
                + smooth.Count + " verts (radius " + (radius * 1000f).ToString("0") + " mm at " + (edge * 1000f).ToString("0.0") + " mm edges"
                + (K == MaxK ? ", capped" : "") + "), " + border.Count + " border verts" + (weak > 0 ? ", " + weak + " without a frame" : "");
            return res;
        }

        // The smoothed surface's frame from the two neighbour edges, the same way the GPU's MushRecon builds it. 0.11.29: no handedness (her
        // skinning space is mirrored relative to the mesh, and a mirror flips a cross product): n along nref (the vertex normal: the rest normal
        // here, the skinned one on the GPU), t = e0 and b = e1 orthogonalised. Checked offline: exact at rest, under rotations and mirrors.
        static bool Frame(Vector3 e0, Vector3 e1, Vector3 nref, out Vector3 n, out Vector3 t, out Vector3 b)
        {
            // 0.11.39 (user: dark red inside-out smears with Round joints on, 0.11.30 too): at a vertex with few neighbours, a border or a
            // tight fold (her crotch, the vulva opening, the glute cleft) the smoothed neighbours can span a plane at right angles to her skin;
            // the cross product's normal was then perpendicular to the vertex normal and its sign a coin toss between this bake and the GPU, so
            // the detail came back mirrored with flipped normals (the GPU test harness showed n.N = 0 exactly there). The frame's normal is the
            // vertex normal itself now (her skinned normal on the GPU: always defined, mirror-proof); t and b are the neighbour edges in its
            // plane. Quality = how much of each edge lies in that plane: under 0.3 at rest the vertex is left out; the GPU fades it the same way.
            n = nref.normalized; t = b = Vector3.zero;
            if (n.sqrMagnitude < 0.5f) return false;
            t = e0 - n * Vector3.Dot(e0, n);
            b = e1 - n * Vector3.Dot(e1, n);
            if (t.sqrMagnitude < 0.09f * e0.sqrMagnitude || t.sqrMagnitude < 1e-20f) return false;
            t.Normalize();
            float bp = b.magnitude;
            b -= t * Vector3.Dot(b, t);
            if (b.sqrMagnitude < 0.09f * e1.sqrMagnitude || b.sqrMagnitude < 1e-20f || bp < 1e-10f) return false;
            b.Normalize();
            return true;
        }

        // Multi-source BFS over the canonical adjacency: rings from the sources (0 = a source), -1 beyond max.
        static int[] Rings(int V, List<uint> src, uint[] adjOff, uint[] adjV, int max)
        {
            var d = new int[V];
            for (int i = 0; i < V; i++) d[i] = -1;
            var q = new Queue<uint>();
            foreach (uint s in src) if (d[s] < 0) { d[s] = 0; q.Enqueue(s); }
            while (q.Count > 0)
            {
                uint v = q.Dequeue();
                if (d[v] >= max) continue;
                for (uint k = adjOff[v]; k < adjOff[v + 1]; k++)
                {
                    uint u = adjV[k];
                    if (d[u] >= 0) continue;
                    d[u] = d[v] + 1;
                    q.Enqueue(u);
                }
            }
            return d;
        }

        static float Smooth01(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }
    }
}
