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
    // to BepInEx\config\SfMorphs\export\<name>-<time>\. The game ships its meshes GPU-only, so
    // MeshData reads them back from the GPU (meshes.txt says which way each was read).
    internal static class Export
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Run(Character c)
        {
            var dir = Path.Combine(Path.Combine(Store.Dir, "export"), Store.Safe(c.Key) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(dir);
            int written = 0;
            var failed = new List<string>();
            var bind = new Dictionary<Transform, Matrix4x4>();
            var seen = new HashSet<Mesh>();
            var index = new StringBuilder("# mesh\trenderer\tvertices\tsubmeshes\tmaterials\tread from\n");
            foreach (var r in c.Renderers)
            {
                if (r == null || r.sharedMesh == null || !seen.Add(r.sharedMesh)) continue;
                var m = r.sharedMesh;
                string err;
                var d = MeshData.Read(m, out err);
                if (d == null) { failed.Add(m.name + " (" + err + ")"); continue; }
                if (err != null) Plugin.Log.LogWarning("Export " + m.name + ": " + err);
                var bones = r.bones;
                var bp = d.Bind;
                for (int i = 0; i < bones.Length && i < bp.Length; i++) if (bones[i] != null && !bind.ContainsKey(bones[i])) bind[bones[i]] = bp[i];
                var name = Store.Safe(m.name);
                File.WriteAllText(Path.Combine(dir, name + ".obj"), Obj(m, r, d));
                File.WriteAllText(Path.Combine(dir, name + ".skin.txt"), Skin(d, bones, bp));
                var mats = new StringBuilder();
                foreach (var mat in r.sharedMaterials) mats.Append(mat != null ? mat.name : "-").Append(';');
                index.Append(name).Append('\t').Append(r.name).Append('\t').Append(m.vertexCount).Append('\t').Append(m.subMeshCount).Append('\t').Append(mats).Append('\t').Append(d.Source).Append('\n');
                written++;
            }
            File.WriteAllText(Path.Combine(dir, "meshes.txt"), index.ToString());
            File.WriteAllText(Path.Combine(dir, "skeleton.txt"), Skeleton(c, bind));
            string texMsg;
            try { texMsg = Textures(c, dir); }
            catch (Exception e) { texMsg = "textures failed: " + e.Message; Plugin.Log.LogWarning("Export textures: " + e); }
            var msg = "Exported " + written + " meshes of '" + c.Key + "' to " + dir + (failed.Count > 0 ? "; " + failed.Count + " failed: " + string.Join(", ", failed.ToArray()) : "") + "; " + texMsg;
            Plugin.Log.LogInfo(msg);
            return msg;
        }

        static readonly string[] SkinMaterials = { "reika_face", "reika_body", "reika_foot" };
        static readonly string[] TexProps = { "_ColorMap", "_NormalMap" };
        static readonly string[] TintProps = { "_SkinColor", "_BaseColor", "_Color" };

        // Her ORIGINAL skin textures (not the converted character's, which MorphState swaps in) as PNGs, plus textures.txt:
        //   # material<TAB>property<TAB>file<TAB>texture format (graphics format)<TAB>size      one row per material x property
        //   # material<TAB>colour property<TAB>r g b a                                          her original tint colours
        // _ColorMap goes through an sRGB render texture (PNG holds the sRGB values), _NormalMap through a linear one.
        // One texture at a time (8K readbacks are big), temporaries destroyed at once.
        static string Textures(Character c, string dir)
        {
            var mats = new List<Material>();
            foreach (var r in c.Renderers)
            {
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || mats.Contains(m)) continue;
                    var n = m.name;
                    foreach (var k in SkinMaterials) if (n.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { mats.Add(m); break; }
                }
            }
            var rows = new StringBuilder("# material\tproperty\tfile\ttexture format (graphics format)\tsize\n");
            var cols = new StringBuilder("# material\tcolour property\tr g b a (her original value)\n");
            var files = new Dictionary<Texture, string>();
            int wrote = 0, skipped = 0;
            foreach (var m in mats)
            {
                var mname = m.name.Replace(" (Instance)", "").Trim();
                foreach (var prop in TexProps)
                {
                    if (!m.HasProperty(prop)) continue;
                    var tex = c.Morph.OriginalTexture(m, prop);
                    if (tex == null) { rows.Append(mname).Append('\t').Append(prop).Append("\t-\t-\t-\n"); skipped++; continue; }
                    string fmt = Describe(tex), size = tex.width + "x" + tex.height;
                    string file;
                    if (!files.TryGetValue(tex, out file))
                    {
                        file = Store.Safe(mname + "_" + prop.TrimStart('_')) + ".png";
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        string err = SavePng(tex, prop == "_ColorMap", Path.Combine(dir, file));
                        sw.Stop();
                        if (err != null) { Plugin.Log.LogWarning("Export texture " + file + " failed: " + err); file = "-"; skipped++; }
                        else { wrote++; Plugin.Log.LogInfo("Export texture " + file + " (" + tex.name + ", " + fmt + ", " + size + ") in " + (sw.Elapsed.TotalSeconds).ToString("0.0", Inv) + " s"); }
                        files[tex] = file;
                    }
                    rows.Append(mname).Append('\t').Append(prop).Append('\t').Append(file).Append('\t').Append(fmt).Append('\t').Append(size).Append('\n');
                }
                foreach (var pn in TintProps)
                {
                    if (!m.HasProperty(pn)) continue;
                    var col = c.Morph.OriginalColor(m, pn);
                    cols.Append(mname).Append('\t').Append(pn).Append('\t').Append(F(col.r)).Append(' ').Append(F(col.g)).Append(' ').Append(F(col.b)).Append(' ').Append(F(col.a)).Append('\n');
                }
            }
            File.WriteAllText(Path.Combine(dir, "textures.txt"), rows.ToString() + cols.ToString());
            return wrote + " skin textures written" + (skipped > 0 ? ", " + skipped + " skipped (see the log)" : "") + (mats.Count == 0 ? " (no reika_face/body/foot material found)" : "");
        }

        static string Describe(Texture t)
        {
            var t2 = t as Texture2D;
            return (t2 != null ? t2.format.ToString() : t.GetType().Name) + " (" + t.graphicsFormat + ")";
        }

        // Blit to a render texture of the right colour space, read it back and write the PNG; null on success.
        static string SavePng(Texture src, bool srgb, string path)
        {
            RenderTexture rt = null, prev = RenderTexture.active;
            Texture2D tmp = null;
            var t2 = src as Texture2D;
            try
            {
                if (t2 != null) { try { t2.requestedMipmapLevel = 0; } catch { } }   // streamed textures: ask for the full size
                rt = new RenderTexture(src.width, src.height, 0, RenderTextureFormat.ARGB32, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                tmp = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, !srgb);
                tmp.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                RenderTexture.active = prev;
                File.WriteAllBytes(path, tmp.EncodeToPNG());
                if (t2 != null && t2.loadedMipmapLevel > 0) Plugin.Log.LogWarning("Export texture " + Path.GetFileName(path) + ": only mip " + t2.loadedMipmapLevel + " was loaded (texture streaming); it may be lower resolution than " + src.width + "x" + src.height);
                return null;
            }
            catch (Exception e) { return e.Message; }
            finally
            {
                if (RenderTexture.active == rt) RenderTexture.active = prev;
                if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
                if (tmp != null) UnityEngine.Object.Destroy(tmp);
                if (t2 != null) { try { t2.ClearRequestedMipmapLevel(); } catch { } }
            }
        }

        static string F(float v) { return v.ToString("0.######", Inv); }

        static string Obj(Mesh m, SkinnedMeshRenderer r, MeshData d)
        {
            var sb = new StringBuilder();
            sb.Append("# SfMorph export of ").Append(m.name).Append(" (renderer ").Append(r.name).Append(", read from ").Append(d.Source).Append("), bind space, Unity axes (left-handed, Y up)\n");
            var v = d.Pos; var n = d.Nrm ?? new Vector3[0]; var uv = d.Uv ?? new Vector2[0];
            foreach (var p in v) sb.Append("v ").Append(F(p.x)).Append(' ').Append(F(p.y)).Append(' ').Append(F(p.z)).Append('\n');
            foreach (var p in n) sb.Append("vn ").Append(F(p.x)).Append(' ').Append(F(p.y)).Append(' ').Append(F(p.z)).Append('\n');
            foreach (var p in uv) sb.Append("vt ").Append(F(p.x)).Append(' ').Append(F(p.y)).Append('\n');
            bool hasN = n.Length == v.Length, hasUv = uv.Length == v.Length;
            for (int s = 0; s < d.Sub.Count; s++)
            {
                var t = d.Sub[s];
                if (t.Length == 0) continue;
                var mat = s < r.sharedMaterials.Length && r.sharedMaterials[s] != null ? r.sharedMaterials[s].name : "submesh" + s;
                sb.Append("g ").Append(Store.Safe(mat).Replace(' ', '_')).Append('\n');
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

        static string Skin(MeshData d, Transform[] bones, Matrix4x4[] bp)
        {
            var sb = new StringBuilder("# bones: index name, then bind pose rows; weights: per vertex 4 x (bone index, weight)\n");
            sb.Append("bones ").Append(bones.Length).Append('\n');
            for (int i = 0; i < bones.Length; i++)
            {
                sb.Append(i).Append(' ').Append(bones[i] != null ? bones[i].name : "-");
                if (i < bp.Length) for (int k = 0; k < 16; k++) sb.Append(' ').Append(F(bp[i][k / 4, k % 4]));
                sb.Append('\n');
            }
            var w = d.Weights ?? new BoneWeight[0];
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
