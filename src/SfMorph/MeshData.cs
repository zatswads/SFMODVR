using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SfMorph
{
    // A mesh's data in the game's vertex order. The game ships its meshes GPU-only
    // (isReadable false: the 0.5.0 export got nothing), so for those the vertex and
    // index buffers are read back from the GPU (Mesh.GetVertexBuffer/GetIndexBuffer,
    // which Unity allows for any mesh) and decoded with the mesh's vertex layout.
    internal class MeshData
    {
        public Vector3[] Pos, Nrm;
        public Vector4[] Tan;
        public Vector2[] Uv;
        public List<Vector4>[] Uvs = new List<Vector4>[4];   // channels 1..3 as stored (index 0 unused), for faithful clones
        public Color[] Col;
        public BoneWeight[] Weights;
        public Matrix4x4[] Bind;
        public List<int[]> Sub = new List<int[]>();   // triangles per submesh (vertex indices)
        public string Source;

        public static MeshData Read(Mesh m, out string error)
        {
            error = null;
            var d = new MeshData();
            try { d.Bind = m.bindposes; } catch (Exception e) { d.Bind = new Matrix4x4[0]; error = "bind poses: " + e.Message; }
            if (m.isReadable)
            {
                d.Source = "CPU";
                d.Pos = m.vertices; d.Nrm = m.normals; d.Tan = m.tangents; d.Uv = m.uv; d.Weights = m.boneWeights;
                for (int c = 1; c < 4; c++) if (m.HasVertexAttribute(VertexAttribute.TexCoord0 + c)) { d.Uvs[c] = new List<Vector4>(); m.GetUVs(c, d.Uvs[c]); }
                if (m.HasVertexAttribute(VertexAttribute.Color)) d.Col = m.colors;
                for (int s = 0; s < m.subMeshCount; s++) d.Sub.Add(m.GetTopology(s) == MeshTopology.Triangles ? m.GetTriangles(s) : new int[0]);
                return d;
            }
            d.Source = "GPU";
            try { ReadGpu(m, d); }
            catch (Exception e) { error = (error != null ? error + "; " : "") + e.GetType().Name + ": " + e.Message; return null; }
            return d;
        }

        static void ReadGpu(Mesh m, MeshData d)
        {
            int n = m.vertexCount;
            var streams = new Dictionary<int, byte[]>();
            var strides = new Dictionary<int, int>();
            foreach (var a in m.GetVertexAttributes())
            {
                if (streams.ContainsKey(a.stream)) continue;
                using (var gb = m.GetVertexBuffer(a.stream))
                {
                    if (gb == null) throw new Exception("no GPU vertex buffer for stream " + a.stream);
                    var bytes = new byte[gb.count * gb.stride];
                    gb.GetData(bytes);
                    streams[a.stream] = bytes;
                    strides[a.stream] = gb.stride;
                }
            }
            Func<VertexAttribute, float[][]> read = attr =>
            {
                if (!m.HasVertexAttribute(attr)) return null;
                int st = m.GetVertexAttributeStream(attr), off = m.GetVertexAttributeOffset(attr), dim = m.GetVertexAttributeDimension(attr);
                var fmt = m.GetVertexAttributeFormat(attr);
                var b = streams[st]; int stride = strides[st];
                var r = new float[n][];
                for (int i = 0; i < n; i++)
                {
                    r[i] = new float[dim];
                    int p = i * stride + off;
                    for (int k = 0; k < dim; k++) r[i][k] = Value(b, ref p, fmt);
                }
                return r;
            };
            var pos = read(VertexAttribute.Position);
            if (pos == null) throw new Exception("mesh has no positions");
            d.Pos = new Vector3[n];
            for (int i = 0; i < n; i++) d.Pos[i] = new Vector3(pos[i][0], pos[i][1], pos[i][2]);
            var nrm = read(VertexAttribute.Normal);
            if (nrm != null) { d.Nrm = new Vector3[n]; for (int i = 0; i < n; i++) d.Nrm[i] = new Vector3(nrm[i][0], nrm[i][1], nrm[i][2]); }
            var tan = read(VertexAttribute.Tangent);
            if (tan != null) { d.Tan = new Vector4[n]; for (int i = 0; i < n; i++) d.Tan[i] = new Vector4(tan[i][0], tan[i][1], tan[i][2], tan[i].Length > 3 ? tan[i][3] : 1f); }
            var uv = read(VertexAttribute.TexCoord0);
            if (uv != null) { d.Uv = new Vector2[n]; for (int i = 0; i < n; i++) d.Uv[i] = new Vector2(uv[i][0], uv[i][1]); }
            for (int c = 1; c < 4; c++)
            {
                var ex = read(VertexAttribute.TexCoord0 + c);
                if (ex == null) continue;
                d.Uvs[c] = new List<Vector4>(n);
                for (int i = 0; i < n; i++) d.Uvs[c].Add(new Vector4(ex[i][0], ex[i].Length > 1 ? ex[i][1] : 0f, ex[i].Length > 2 ? ex[i][2] : 0f, ex[i].Length > 3 ? ex[i][3] : 0f));
            }
            var col = read(VertexAttribute.Color);
            if (col != null) { d.Col = new Color[n]; for (int i = 0; i < n; i++) d.Col[i] = new Color(col[i][0], col[i].Length > 1 ? col[i][1] : 0f, col[i].Length > 2 ? col[i][2] : 0f, col[i].Length > 3 ? col[i][3] : 1f); }
            var bw = read(VertexAttribute.BlendWeight);
            var bi = read(VertexAttribute.BlendIndices);
            if (bi != null)
            {
                d.Weights = new BoneWeight[n];
                for (int i = 0; i < n; i++)
                {
                    // Up to 4 influences, strongest first; one influence may come without a weight stream (weight 1).
                    var idx = bi[i]; var w = bw != null ? bw[i] : new[] { 1f };
                    var order = new List<int>();
                    for (int k = 0; k < idx.Length; k++) order.Add(k);
                    order.Sort((x, y) => (y < w.Length ? w[y] : 0f).CompareTo(x < w.Length ? w[x] : 0f));
                    var o = new BoneWeight();
                    for (int j = 0; j < 4 && j < order.Count; j++)
                    {
                        int k = order[j]; int bone = (int)idx[k]; float wt = k < w.Length ? w[k] : 0f;
                        if (j == 0) { o.boneIndex0 = bone; o.weight0 = wt; }
                        else if (j == 1) { o.boneIndex1 = bone; o.weight1 = wt; }
                        else if (j == 2) { o.boneIndex2 = bone; o.weight2 = wt; }
                        else { o.boneIndex3 = bone; o.weight3 = wt; }
                    }
                    d.Weights[i] = o;
                }
            }

            int[] indices;
            using (var ib = m.GetIndexBuffer())
            {
                if (ib == null) throw new Exception("no GPU index buffer");
                var bytes = new byte[ib.count * ib.stride];
                ib.GetData(bytes);
                bool wide = m.indexFormat == IndexFormat.UInt32;
                indices = new int[bytes.Length / (wide ? 4 : 2)];
                for (int i = 0; i < indices.Length; i++) indices[i] = wide ? BitConverter.ToInt32(bytes, i * 4) : BitConverter.ToUInt16(bytes, i * 2);
            }
            for (int s = 0; s < m.subMeshCount; s++)
            {
                var sm = m.GetSubMesh(s);
                if (sm.topology != MeshTopology.Triangles) { d.Sub.Add(new int[0]); continue; }
                var t = new int[sm.indexCount];
                for (int i = 0; i < sm.indexCount; i++) t[i] = indices[sm.indexStart + i] + sm.baseVertex;
                d.Sub.Add(t);
            }
        }

        static float Value(byte[] b, ref int p, VertexAttributeFormat f)
        {
            float v;
            switch (f)
            {
                case VertexAttributeFormat.Float32: v = BitConverter.ToSingle(b, p); p += 4; break;
                case VertexAttributeFormat.Float16: v = Mathf.HalfToFloat(BitConverter.ToUInt16(b, p)); p += 2; break;
                case VertexAttributeFormat.UNorm8: v = b[p] / 255f; p += 1; break;
                case VertexAttributeFormat.SNorm8: v = Mathf.Max((sbyte)b[p] / 127f, -1f); p += 1; break;
                case VertexAttributeFormat.UNorm16: v = BitConverter.ToUInt16(b, p) / 65535f; p += 2; break;
                case VertexAttributeFormat.SNorm16: v = Mathf.Max(BitConverter.ToInt16(b, p) / 32767f, -1f); p += 2; break;
                case VertexAttributeFormat.UInt8: v = b[p]; p += 1; break;
                case VertexAttributeFormat.SInt8: v = (sbyte)b[p]; p += 1; break;
                case VertexAttributeFormat.UInt16: v = BitConverter.ToUInt16(b, p); p += 2; break;
                case VertexAttributeFormat.SInt16: v = BitConverter.ToInt16(b, p); p += 2; break;
                case VertexAttributeFormat.UInt32: v = BitConverter.ToUInt32(b, p); p += 4; break;
                case VertexAttributeFormat.SInt32: v = BitConverter.ToInt32(b, p); p += 4; break;
                default: throw new Exception("vertex format " + f);
            }
            return v;
        }
    }
}
