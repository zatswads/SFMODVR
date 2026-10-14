using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // Writes her meshes for the character pipeline (VaM look -> her body), in the game's
    // own vertex order, which every morph made for her must keep:
    //   <mesh>.obj        positions, normals, UVs, faces per submesh, in the mesh's bind space
    //   <mesh>.skin.txt   bone names, bind poses, 4 bone weights per vertex
    //   skeleton.txt      every skin bone: name, parent, bind position (mesh space) and rotation
    // to BepInEx\config\SfMorphs\export\<name>-<time>\. Unity needs a mesh to be readable for
    // this; the game may have shipped some as GPU-only, which the log lists.
    internal static class Export
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Run(Character c)
        {
            var dir = Path.Combine(Path.Combine(Store.Dir, "export"), Store.Safe(c.Key) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(dir);
            int written = 0;
            var unreadable = new List<string>();
            var bind = new Dictionary<Transform, Matrix4x4>();
            var seen = new HashSet<Mesh>();
            var index = new StringBuilder("# mesh\trenderer\tvertices\tsubmeshes\tmaterials\n");
            foreach (var r in c.Renderers)
            {
                if (r == null || r.sharedMesh == null || !seen.Add(r.sharedMesh)) continue;
                var m = r.sharedMesh;
                if (!m.isReadable) { unreadable.Add(m.name); continue; }
                var bones = r.bones;
                var bp = m.bindposes;
                for (int i = 0; i < bones.Length && i < bp.Length; i++) if (bones[i] != null && !bind.ContainsKey(bones[i])) bind[bones[i]] = bp[i];
                var name = Store.Safe(m.name);
                File.WriteAllText(Path.Combine(dir, name + ".obj"), Obj(m, r));
                File.WriteAllText(Path.Combine(dir, name + ".skin.txt"), Skin(m, bones, bp));
                var mats = new StringBuilder();
                foreach (var mat in r.sharedMaterials) mats.Append(mat != null ? mat.name : "-").Append(';');
                index.Append(name).Append('\t').Append(r.name).Append('\t').Append(m.vertexCount).Append('\t').Append(m.subMeshCount).Append('\t').Append(mats).Append('\n');
                written++;
            }
            File.WriteAllText(Path.Combine(dir, "meshes.txt"), index.ToString());
            File.WriteAllText(Path.Combine(dir, "skeleton.txt"), Skeleton(c, bind));
            var msg = "Exported " + written + " meshes of '" + c.Key + "' to " + dir + (unreadable.Count > 0 ? "; " + unreadable.Count + " not readable by mods: " + string.Join(", ", unreadable.ToArray()) : "");
            Plugin.Log.LogInfo(msg);
            return msg;
        }

        static string F(float v) { return v.ToString("0.######", Inv); }

        static string Obj(Mesh m, SkinnedMeshRenderer r)
        {
            var sb = new StringBuilder();
            sb.Append("# SfMorph export of ").Append(m.name).Append(" (renderer ").Append(r.name).Append("), bind space, Unity axes (left-handed, Y up)\n");
            var v = m.vertices; var n = m.normals; var uv = m.uv;
            foreach (var p in v) sb.Append("v ").Append(F(p.x)).Append(' ').Append(F(p.y)).Append(' ').Append(F(p.z)).Append('\n');
            foreach (var p in n) sb.Append("vn ").Append(F(p.x)).Append(' ').Append(F(p.y)).Append(' ').Append(F(p.z)).Append('\n');
            foreach (var p in uv) sb.Append("vt ").Append(F(p.x)).Append(' ').Append(F(p.y)).Append('\n');
            bool hasN = n.Length == v.Length, hasUv = uv.Length == v.Length;
            for (int s = 0; s < m.subMeshCount; s++)
            {
                if (m.GetTopology(s) != MeshTopology.Triangles) continue;
                var mat = s < r.sharedMaterials.Length && r.sharedMaterials[s] != null ? r.sharedMaterials[s].name : "submesh" + s;
                sb.Append("g ").Append(Store.Safe(mat).Replace(' ', '_')).Append('\n');
                var t = m.GetTriangles(s);
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    sb.Append('f');
                    for (int k = 0; k < 3; k++)
                    {
                        int x = t[i + k] + 1;
                        sb.Append(' ').Append(x);
                        if (hasUv || hasN) sb.Append('/').Append(hasUv ? x.ToString(Inv) : "");
                        if (hasN) sb.Append('/').Append(x);
                    }
                    sb.Append('\n');
                }
            }
            return sb.ToString();
        }

        static string Skin(Mesh m, Transform[] bones, Matrix4x4[] bp)
        {
            var sb = new StringBuilder("# bones: index name, then bind pose rows; weights: per vertex 4 x (bone index, weight)\n");
            sb.Append("bones ").Append(bones.Length).Append('\n');
            for (int i = 0; i < bones.Length; i++)
            {
                sb.Append(i).Append(' ').Append(bones[i] != null ? bones[i].name : "-");
                if (i < bp.Length) for (int k = 0; k < 16; k++) sb.Append(' ').Append(F(bp[i][k / 4, k % 4]));
                sb.Append('\n');
            }
            var w = m.boneWeights;
            sb.Append("weights ").Append(w.Length).Append('\n');
            foreach (var b in w)
                sb.Append(b.boneIndex0).Append(' ').Append(F(b.weight0)).Append(' ').Append(b.boneIndex1).Append(' ').Append(F(b.weight1)).Append(' ')
                  .Append(b.boneIndex2).Append(' ').Append(F(b.weight2)).Append(' ').Append(b.boneIndex3).Append(' ').Append(F(b.weight3)).Append('\n');
            return sb.ToString();
        }

        static string Skeleton(Character c, Dictionary<Transform, Matrix4x4> bind)
        {
            var sb = new StringBuilder("# name\tparent\tbind position (mesh space)\tbind rotation (quaternion xyzw)\n");
            foreach (var t in c.BoneList)
            {
                Matrix4x4 bp;
                if (t == null || !bind.TryGetValue(t, out bp)) continue;
                var w = bp.inverse;
                var pos = (Vector3)w.GetColumn(3);
                var rot = Quaternion.LookRotation(w.GetColumn(2), w.GetColumn(1));
                sb.Append(t.name).Append('\t').Append(t.parent != null ? t.parent.name : "-").Append('\t')
                  .Append(F(pos.x)).Append(' ').Append(F(pos.y)).Append(' ').Append(F(pos.z)).Append('\t')
                  .Append(F(rot.x)).Append(' ').Append(F(rot.y)).Append(' ').Append(F(rot.z)).Append(' ').Append(F(rot.w)).Append('\n');
            }
            return sb.ToString();
        }
    }
}
