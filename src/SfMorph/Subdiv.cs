using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace SfMorph
{
    // One level of PN-triangle subdivision, in place on a readable skinned mesh (0.7.2 "Fidelity").
    //
    // Every triangle (a,b,c) becomes 4: (a,ab,ca) (ab,b,bc) (ca,bc,c) (ab,bc,ca)  (winding preserved). Original vertices keep
    // their index and position; the new edge midpoints are appended and displaced onto the PN-triangle (curved PN) edge curve.
    //
    // PN edge curve (Vlachos et al.): control points on edge i->j are  b_ij = (2*p_i + p_j - dot(p_j - p_i, n_i)*n_i) / 3,
    // the edge is the cubic Bezier  p_i, b_ij, b_ji, p_j. At t = 0.5 the Bezier weights are 1/8, 3/8, 3/8, 1/8, so
    //   mid = (p_i + p_j)/8 + 3/8*(b_ij + b_ji)
    // with b_ij + b_ji = p_i + p_j - (w_ij*n_i + w_ji*n_j)/3, w_ij = dot(p_j - p_i, n_i), w_ji = dot(p_i - p_j, n_j):
    //   mid = (p_i + p_j)/2 - (w_ij*n_i + w_ji*n_j)/8
    //       = (p_i + p_j)/2 + ( dot(d,n_j)*n_j - dot(d,n_i)*n_i ) / 8,   d = p_j - p_i
    // (symmetric in i<->j; on a unit sphere this lands at radius cos(x)*(1+sin(x)^2/2), x = half the arc angle, ~1 - x^4/4.)
    // The interior PN point b111 is not needed for edge midpoints, so the surface is C0 with the neighbours (same curve per edge).
    //
    // Seams: the displacement is computed from the welded (quantised 1e-5 position) representative position and the welded,
    // position-averaged normal, so duplicated seam vertices give bit-identical midpoints (no cracks). Every other attribute
    // (normal, tangent, uv0-7, colour, bone weights, blendshape deltas) is averaged per side from the two vertices of the
    // side's own index pair; midpoints are keyed by the sorted (i,j) index pair, so each side gets its own midpoint vertex.
    internal static class Subdiv
    {
        // Meshes currently subdivided by us (guards against a second level being applied before Restore).
        static readonly HashSet<int> Subdivided = new HashSet<int>();

        internal class Undo
        {
            Mesh m; SkinnedMeshRenderer r; Data orig; bool done;
            internal Undo(Mesh mesh, SkinnedMeshRenderer rend, Data original) { m = mesh; r = rend; orig = original; }

            // Writes the exact captured original back (Clear first so the vertex-count change is legal). Never throws.
            public void Restore()
            {
                if (done) return;
                done = true;
                try
                {
                    if (m == null) return;
                    int id = m.GetInstanceID();
                    Write(m, r, orig);
                    Subdivided.Remove(id);
                }
                catch (Exception e) { Plugin.Log.LogWarning("Subdiv.Restore failed: " + e.Message); }
                orig = null;
            }
        }

        // ---------------------------------------------------------------------------------------------------------------
        // data
        internal sealed class BsFrame { public float weight; public int[] idx; public Vector3[] dv, dn, dt; }   // sparse: only nonzero vertices
        internal sealed class BsShape { public string name; public BsFrame[] frames; }

        internal sealed class Data
        {
            public Vector3[] verts, nrm;
            public Vector4[] tan;
            public Vector4[][] uv = new Vector4[8][];
            public int[] uvDim = new int[8];
            public Color32[] col;
            public BoneWeight[] bw;
            public byte[] bpv; public BoneWeight1[] bw1;      // only when the original has > 4 influences per vertex (exact restore)
            public Matrix4x4[] bind;
            public IndexFormat fmt;
            public int[][] tris;
            public Bounds bounds;
            public BsShape[] shapes;
        }

        struct QKey : IEquatable<QKey>
        {
            public int x, y, z;
            public bool Equals(QKey o) { return x == o.x && y == o.y && z == o.z; }
            public override bool Equals(object o) { return o is QKey && Equals((QKey)o); }
            public override int GetHashCode() { unchecked { return (x * 73856093) ^ (y * 19349663) ^ (z * 83492791); } }
        }

        // new-vertex registry: sorted index pair -> new vertex index (appended after the original vertices)
        sealed class EdgeMap
        {
            readonly Dictionary<long, int> map;
            readonly int baseIdx;
            public readonly int[] A, B;
            public int Count;
            public EdgeMap(int maxEdges, int baseIndex)
            {
                map = new Dictionary<long, int>(maxEdges / 2 + 16);
                A = new int[maxEdges]; B = new int[maxEdges]; baseIdx = baseIndex;
            }
            public int Get(int a, int b)
            {
                long key = a < b ? (((long)a << 32) | (uint)b) : (((long)b << 32) | (uint)a);
                int v;
                if (map.TryGetValue(key, out v)) return v;
                v = baseIdx + Count;
                A[Count] = a; B[Count] = b; Count++;
                map.Add(key, v);
                return v;
            }
        }

        // ---------------------------------------------------------------------------------------------------------------
        public static Undo Apply(Mesh m, SkinnedMeshRenderer r, out string note)
        {
            note = "";
            try
            {
                if (m == null) { note = "no mesh"; return null; }
                if (!m.isReadable) { note = m.name + ": mesh not readable"; return null; }
                if (Subdivided.Contains(m.GetInstanceID())) { note = m.name + ": already subdivided"; return null; }
                var sw = System.Diagnostics.Stopwatch.StartNew();

                string why;
                Data o = Capture(m, out why);
                if (o == null) { note = m.name + ": " + why; return null; }

                int seams, frames;
                Data nw = Build(o, out seams, out frames);                      // pure computation, the mesh is untouched so far

                int n = o.verts.Length, tOld = 0, tNew = 0;
                for (int s = 0; s < o.tris.Length; s++) { tOld += o.tris[s].Length / 3; tNew += nw.tris[s].Length / 3; }

                var undo = new Undo(m, r, o);
                try { Write(m, r, nw); }
                catch (Exception e)
                {
                    string extra = "";
                    try { Write(m, r, o); } catch (Exception e2) { extra = " (restore also failed: " + e2.Message + ")"; }
                    note = m.name + ": write failed: " + e.Message + extra;
                    return null;
                }
                Subdivided.Add(m.GetInstanceID());
                sw.Stop();
                note = m.name + ": verts " + n + "->" + nw.verts.Length + ", tris " + tOld + "->" + tNew + ", seams welded " + seams +
                       ", blendshape frames " + frames + ", " + sw.ElapsedMilliseconds + " ms";
                return undo;
            }
            catch (Exception e)
            {
                note = (m != null ? m.name : "?") + ": subdivision failed: " + e.Message;
                return null;
            }
        }

        // ---------------------------------------------------------------------------------------------------------------
        // read everything needed to rebuild the mesh exactly. Does not modify the mesh. Returns null (+why) to refuse.
        static Data Capture(Mesh m, out string why)
        {
            why = "";
            int n = m.vertexCount;
            if (n < 3) { why = "no vertices"; return null; }
            var d = new Data();
            d.verts = m.vertices;
            d.bw = m.boneWeights;
            d.bind = m.bindposes;
            if (d.bw == null || d.bw.Length != n || d.bind == null || d.bind.Length == 0) { why = "mesh not skinned"; return null; }

            int sc = m.subMeshCount;
            if (sc < 1) { why = "no submeshes"; return null; }
            d.tris = new int[sc][];
            for (int s = 0; s < sc; s++)
            {
                var sd = m.GetSubMesh(s);
                if (sd.topology != MeshTopology.Triangles) { why = "submesh " + s + " is not Triangles"; return null; }
                if (sd.baseVertex != 0) { why = "submesh " + s + " has a base vertex"; return null; }
                d.tris[s] = m.GetTriangles(s);
            }
            d.fmt = m.indexFormat;
            d.bounds = m.bounds;

            var nrm = m.normals; d.nrm = (nrm != null && nrm.Length == n) ? nrm : null;
            var tan = m.tangents; d.tan = (tan != null && tan.Length == n) ? tan : null;
            var col = m.colors32; d.col = (col != null && col.Length == n) ? col : null;

            var tmp = new List<Vector4>(n);
            for (int c = 0; c < 8; c++)
            {
                var attr = (VertexAttribute)((int)VertexAttribute.TexCoord0 + c);
                if (!m.HasVertexAttribute(attr)) continue;
                int dim = m.GetVertexAttributeDimension(attr);
                tmp.Clear();
                m.GetUVs(c, tmp);
                if (tmp.Count != n) continue;
                d.uv[c] = tmp.ToArray();
                d.uvDim[c] = dim < 2 ? 2 : (dim > 4 ? 4 : dim);
            }

            // exact variable bone weights (only needed when some vertex has more than 4 influences)
            var bpv = m.GetBonesPerVertex();
            if (bpv.IsCreated && bpv.Length == n)
            {
                bool big = false;
                for (int i = 0; i < n; i++) if (bpv[i] > 4) { big = true; break; }
                if (big) { d.bpv = bpv.ToArray(); d.bw1 = m.GetAllBoneWeights().ToArray(); }
            }

            d.shapes = ReadShapes(m, n);
            return d;
        }

        static bool NZ(Vector3 v) { return v.x != 0f || v.y != 0f || v.z != 0f; }

        static BsShape[] ReadShapes(Mesh m, int n)
        {
            int sc = m.blendShapeCount;
            var res = new BsShape[sc];
            if (sc == 0) return res;
            var dv = new Vector3[n]; var dn = new Vector3[n]; var dt = new Vector3[n];
            var scratch = new int[n];
            for (int s = 0; s < sc; s++)
            {
                var sh = new BsShape { name = m.GetBlendShapeName(s) };
                int fc = m.GetBlendShapeFrameCount(s);
                sh.frames = new BsFrame[fc];
                for (int f = 0; f < fc; f++)
                {
                    var fr = new BsFrame { weight = m.GetBlendShapeFrameWeight(s, f) };
                    m.GetBlendShapeFrameVertices(s, f, dv, dn, dt);
                    int cnt = 0;
                    for (int i = 0; i < n; i++) if (NZ(dv[i]) || NZ(dn[i]) || NZ(dt[i])) scratch[cnt++] = i;
                    fr.idx = new int[cnt]; fr.dv = new Vector3[cnt]; fr.dn = new Vector3[cnt]; fr.dt = new Vector3[cnt];
                    for (int k = 0; k < cnt; k++)
                    {
                        int i = scratch[k];
                        fr.idx[k] = i; fr.dv[k] = dv[i]; fr.dn[k] = dn[i]; fr.dt[k] = dt[i];
                    }
                    sh.frames[f] = fr;
                }
                res[s] = sh;
            }
            return res;
        }

        // ---------------------------------------------------------------------------------------------------------------
        // pure computation of the subdivided mesh data
        static Data Build(Data o, out int seamsWelded, out int framesReadded)
        {
            int n = o.verts.Length;
            Vector3[] P = o.verts;

            // 1) weld by quantised position (1e-5): representative vertex per group, welded normal = normalised sum of members
            var rep = new int[n];
            var cells = new Dictionary<QKey, int>(n);
            seamsWelded = 0;
            for (int i = 0; i < n; i++)
            {
                var q = new QKey { x = Mathf.RoundToInt(P[i].x * 1e5f), y = Mathf.RoundToInt(P[i].y * 1e5f), z = Mathf.RoundToInt(P[i].z * 1e5f) };
                int r0;
                if (cells.TryGetValue(q, out r0)) { rep[i] = r0; seamsWelded++; }
                else { cells.Add(q, i); rep[i] = i; }
            }
            Vector3[] wn = new Vector3[n];                  // valid at representatives only
            if (o.nrm != null)
            {
                for (int i = 0; i < n; i++) wn[rep[i]] += o.nrm[i];
                for (int i = 0; i < n; i++) if (rep[i] == i) { float l = wn[i].magnitude; wn[i] = l > 1e-8f ? wn[i] / l : Vector3.zero; }
            }

            // 2) topology: 1-to-4 split per triangle, new vertices keyed by sorted index pair
            int idxTotal = 0;
            for (int s = 0; s < o.tris.Length; s++) idxTotal += o.tris[s].Length;
            var em = new EdgeMap(idxTotal, n);
            var nt = new int[o.tris.Length][];
            for (int s = 0; s < o.tris.Length; s++)
            {
                int[] src = o.tris[s];
                if (src.Length % 3 != 0) throw new InvalidOperationException("submesh " + s + " index count not a multiple of 3");
                var dst = new int[src.Length * 4];
                int w = 0;
                for (int t = 0; t < src.Length; t += 3)
                {
                    int a = src[t], b = src[t + 1], c = src[t + 2];
                    if ((uint)a >= (uint)n || (uint)b >= (uint)n || (uint)c >= (uint)n) throw new InvalidOperationException("triangle index out of range");
                    int ab = em.Get(a, b), bc = em.Get(b, c), ca = em.Get(c, a);
                    dst[w++] = a;  dst[w++] = ab; dst[w++] = ca;
                    dst[w++] = ab; dst[w++] = b;  dst[w++] = bc;
                    dst[w++] = ca; dst[w++] = bc; dst[w++] = c;
                    dst[w++] = ab; dst[w++] = bc; dst[w++] = ca;
                }
                nt[s] = dst;
            }
            int cnt = em.Count, n2 = n + cnt;
            int[] A = em.A, B = em.B;

            var d = new Data();
            d.tris = nt;
            d.bind = o.bind; d.bounds = o.bounds;
            d.fmt = n2 > 65535 ? IndexFormat.UInt32 : o.fmt;

            // 3) positions (PN edge midpoint, see header) and per-side attributes
            var V = new Vector3[n2];
            Array.Copy(P, V, n);
            Vector3[] N2 = null; Vector4[] T2 = null; Color32[] C2 = null;
            if (o.nrm != null) { N2 = new Vector3[n2]; Array.Copy(o.nrm, N2, n); }
            if (o.tan != null) { T2 = new Vector4[n2]; Array.Copy(o.tan, T2, n); }
            if (o.col != null) { C2 = new Color32[n2]; Array.Copy(o.col, C2, n); }
            var BW = new BoneWeight[n2];
            Array.Copy(o.bw, BW, n);
            d.uvDim = o.uvDim;
            for (int c = 0; c < 8; c++)
                if (o.uv[c] != null) { d.uv[c] = new Vector4[n2]; Array.Copy(o.uv[c], d.uv[c], n); }

            var tbi = new int[8]; var tbw = new float[8];
            for (int k = 0; k < cnt; k++)
            {
                int a = A[k], b = B[k], v = n + k;
                int ra = rep[a], rb = rep[b];
                Vector3 pa = P[ra], pb = P[rb], na = wn[ra], nb = wn[rb];
                Vector3 dd = pb - pa;
                // mid = (pa+pb)/2 + (dot(d,nb)*nb - dot(d,na)*na)/8
                V[v] = (pa + pb) * 0.5f + (nb * Vector3.Dot(dd, nb) - na * Vector3.Dot(dd, na)) * 0.125f;

                if (N2 != null)
                {
                    Vector3 s = o.nrm[a] + o.nrm[b];
                    float l = s.magnitude;
                    N2[v] = l > 1e-8f ? s / l : o.nrm[a];
                }
                if (T2 != null)
                {
                    Vector4 ta = o.tan[a], tb = o.tan[b];
                    Vector3 s = new Vector3(ta.x + tb.x, ta.y + tb.y, ta.z + tb.z);
                    float l = s.magnitude;
                    if (l > 1e-8f) s /= l; else s = new Vector3(ta.x, ta.y, ta.z);
                    T2[v] = new Vector4(s.x, s.y, s.z, ta.w);
                }
                if (C2 != null)
                {
                    Color32 ca = o.col[a], cb = o.col[b];
                    C2[v] = new Color32((byte)((ca.r + cb.r + 1) >> 1), (byte)((ca.g + cb.g + 1) >> 1), (byte)((ca.b + cb.b + 1) >> 1), (byte)((ca.a + cb.a + 1) >> 1));
                }
                for (int c = 0; c < 8; c++)
                    if (d.uv[c] != null) d.uv[c][v] = (o.uv[c][a] + o.uv[c][b]) * 0.5f;
                BW[v] = MergeBW(o.bw[a], o.bw[b], tbi, tbw);
            }
            d.verts = V; d.nrm = N2; d.tan = T2; d.col = C2; d.bw = BW;

            // 4) blendshapes: same names/order/frame weights, deltas (and normal/tangent deltas) linearly averaged on midpoints
            d.shapes = new BsShape[o.shapes.Length];
            framesReadded = 0;
            if (o.shapes.Length > 0)
            {
                var dv = new Vector3[n]; var dn = new Vector3[n]; var dt = new Vector3[n];
                var stamp = new int[n];
                int cur = 0;
                for (int s = 0; s < o.shapes.Length; s++)
                {
                    var os = o.shapes[s];
                    var ns = new BsShape { name = os.name, frames = new BsFrame[os.frames.Length] };
                    for (int f = 0; f < os.frames.Length; f++)
                    {
                        var of = os.frames[f];
                        cur++;
                        int oc = of.idx.Length;
                        for (int k = 0; k < oc; k++)
                        {
                            int i = of.idx[k];
                            stamp[i] = cur; dv[i] = of.dv[k]; dn[i] = of.dn[k]; dt[i] = of.dt[k];
                        }
                        int mc = 0;
                        for (int k = 0; k < cnt; k++) if (stamp[A[k]] == cur || stamp[B[k]] == cur) mc++;
                        var nf = new BsFrame { weight = of.weight, idx = new int[oc + mc], dv = new Vector3[oc + mc], dn = new Vector3[oc + mc], dt = new Vector3[oc + mc] };
                        Array.Copy(of.idx, nf.idx, oc); Array.Copy(of.dv, nf.dv, oc); Array.Copy(of.dn, nf.dn, oc); Array.Copy(of.dt, nf.dt, oc);
                        int w = oc;
                        for (int k = 0; k < cnt; k++)
                        {
                            int a = A[k], b = B[k];
                            bool sa = stamp[a] == cur, sb = stamp[b] == cur;
                            if (!sa && !sb) continue;
                            nf.idx[w] = n + k;
                            nf.dv[w] = ((sa ? dv[a] : Vector3.zero) + (sb ? dv[b] : Vector3.zero)) * 0.5f;
                            nf.dn[w] = ((sa ? dn[a] : Vector3.zero) + (sb ? dn[b] : Vector3.zero)) * 0.5f;
                            nf.dt[w] = ((sa ? dt[a] : Vector3.zero) + (sb ? dt[b] : Vector3.zero)) * 0.5f;
                            w++;
                        }
                        ns.frames[f] = nf;
                        framesReadded++;
                    }
                    d.shapes[s] = ns;
                }
            }
            return d;
        }

        // merge two skin weights: sum per bone (each at 0.5), keep the 4 largest, renormalise to 1
        static void Acc(int[] bi, float[] bw, ref int cnt, int bone, float w)
        {
            if (!(w > 0f)) return;
            for (int i = 0; i < cnt; i++) if (bi[i] == bone) { bw[i] += w; return; }
            bi[cnt] = bone; bw[cnt] = w; cnt++;
        }

        static BoneWeight MergeBW(BoneWeight a, BoneWeight b, int[] bi, float[] bw)
        {
            int cnt = 0;
            Acc(bi, bw, ref cnt, a.boneIndex0, a.weight0); Acc(bi, bw, ref cnt, a.boneIndex1, a.weight1);
            Acc(bi, bw, ref cnt, a.boneIndex2, a.weight2); Acc(bi, bw, ref cnt, a.boneIndex3, a.weight3);
            Acc(bi, bw, ref cnt, b.boneIndex0, b.weight0); Acc(bi, bw, ref cnt, b.boneIndex1, b.weight1);
            Acc(bi, bw, ref cnt, b.boneIndex2, b.weight2); Acc(bi, bw, ref cnt, b.boneIndex3, b.weight3);
            if (cnt == 0) return a;
            int top = cnt < 4 ? cnt : 4;
            for (int i = 0; i < top; i++)                       // partial selection sort, descending by weight
            {
                int best = i;
                for (int j = i + 1; j < cnt; j++) if (bw[j] > bw[best]) best = j;
                if (best != i)
                {
                    float tw = bw[i]; bw[i] = bw[best]; bw[best] = tw;
                    int tb = bi[i]; bi[i] = bi[best]; bi[best] = tb;
                }
            }
            float sum = 0f;
            for (int i = 0; i < top; i++) sum += bw[i];
            float inv = sum > 0f ? 1f / sum : 0f;
            var r = new BoneWeight();
            r.boneIndex0 = bi[0]; r.weight0 = bw[0] * inv;
            if (top > 1) { r.boneIndex1 = bi[1]; r.weight1 = bw[1] * inv; }
            if (top > 2) { r.boneIndex2 = bi[2]; r.weight2 = bw[2] * inv; }
            if (top > 3) { r.boneIndex3 = bi[3]; r.weight3 = bw[3] * inv; }
            return r;
        }

        // ---------------------------------------------------------------------------------------------------------------
        // Writes d into m (used both for the subdivided data and for Undo, so the order is identical):
        // save blendshape weights -> Clear (empties triangles/submeshes, so a vertex-count change is legal; also drops blendshapes,
        // bind poses and bone weights, rewritten below) -> index format -> vertices -> normals/tangents/uv/colours -> bone weights
        // -> bind poses -> subMeshCount + triangles -> bounds -> blendshape frames -> restore blendshape weights.
        static void Write(Mesh m, SkinnedMeshRenderer r, Data d)
        {
            float[] bsw = null;
            if (r != null && r.sharedMesh == m)
            {
                int bc = m.blendShapeCount;
                if (bc > 0) { bsw = new float[bc]; for (int i = 0; i < bc; i++) bsw[i] = r.GetBlendShapeWeight(i); }
            }

            m.Clear(true);
            m.ClearBlendShapes();
            m.indexFormat = d.fmt;
            m.vertices = d.verts;
            if (d.nrm != null) m.normals = d.nrm;
            if (d.tan != null) m.tangents = d.tan;
            int n = d.verts.Length;
            for (int c = 0; c < 8; c++)
            {
                Vector4[] u = d.uv[c];
                if (u == null) continue;
                int dim = d.uvDim[c];
                if (dim == 4) m.SetUVs(c, u);
                else if (dim == 3)
                {
                    var a = new Vector3[n];
                    for (int i = 0; i < n; i++) a[i] = new Vector3(u[i].x, u[i].y, u[i].z);
                    m.SetUVs(c, a);
                }
                else
                {
                    var a = new Vector2[n];
                    for (int i = 0; i < n; i++) a[i] = new Vector2(u[i].x, u[i].y);
                    m.SetUVs(c, a);
                }
            }
            if (d.col != null) m.colors32 = d.col;

            if (d.bpv != null && d.bw1 != null)
            {
                var na = new NativeArray<byte>(d.bpv, Allocator.Temp);
                var nb = new NativeArray<BoneWeight1>(d.bw1, Allocator.Temp);
                try { m.SetBoneWeights(na, nb); }
                finally { na.Dispose(); nb.Dispose(); }
            }
            else m.boneWeights = d.bw;
            m.bindposes = d.bind;

            m.subMeshCount = d.tris.Length;
            for (int s = 0; s < d.tris.Length; s++) m.SetTriangles(d.tris[s], s, true, 0);
            m.bounds = d.bounds;

            if (d.shapes != null && d.shapes.Length > 0)
            {
                var dv = new Vector3[n]; var dn = new Vector3[n]; var dt = new Vector3[n];
                for (int s = 0; s < d.shapes.Length; s++)
                {
                    var sh = d.shapes[s];
                    for (int f = 0; f < sh.frames.Length; f++)
                    {
                        var fr = sh.frames[f];
                        Array.Clear(dv, 0, n); Array.Clear(dn, 0, n); Array.Clear(dt, 0, n);
                        for (int k = 0; k < fr.idx.Length; k++)
                        {
                            int i = fr.idx[k];
                            dv[i] = fr.dv[k]; dn[i] = fr.dn[k]; dt[i] = fr.dt[k];
                        }
                        m.AddBlendShapeFrame(sh.name, fr.weight, dv, dn, dt);
                    }
                }
            }

            if (bsw != null)
            {
                int bc = Mathf.Min(bsw.Length, m.blendShapeCount);
                for (int i = 0; i < bc; i++) r.SetBlendShapeWeight(i, bsw[i]);
            }
        }
    }
}
