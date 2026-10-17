using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// Runs the plugin's own src/SfMorph/Avatar.cs (and MeshData.cs; copied in by tools/avatar/drive_test.ps1, compiled against Stubs.cs)
// on her export and a PoseRecorder recording, in the editor:
//   Unity.exe -batchmode -quit -nographics -projectPath <this project> -executeMethod AvatarDriveTest.Run
//             -export <her SfMorph export dir> -pose <recording.txt> -store <folder holding avatars\> -avatar <Name> -out <dir> [-frames 4]
// Her skeleton comes from skeleton.txt, her body meshes (upperBody, lowerBody, foot, neck, face) from the export's .obj + .skin.txt,
// skinned the way the game does. Avatar.cs fits the avatar onto her skeleton; each recorded frame poses her bones and both are
// baked. Measured per frame: how far her body skin is from the avatar's (the avatar should move on her curves), the soles' height
// against hers (feet on her floor), the avatar skin's stretch against its own bind frame. <out>/frame_NN.obj: the avatar, and
// <out>/frame_NN_her.obj: her body, both in her root's space (Unity axes).
public static class AvatarDriveTest
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] HerMeshes = { "reika_upperBody_normal", "reika_lowerBody_normal", "reika_foot", "reika_neck", "reika_face_a", "pussy_typeC_normal", "pubicHair_typeD_normal" };

    static string Arg(string name, string def = null)
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
        return def;
    }

    static void Fail(string s) { Debug.LogError("SfAvatarTest FAILED: " + s); EditorApplication.Exit(1); }

    static float F(string s) { return float.Parse(s, Inv); }
    static Vector3 V(string s) { var f = s.Split(' ').Select(F).ToArray(); return new Vector3(f[0], f[1], f[2]); }
    static Quaternion Q(string s) { var f = s.Split(' ').Select(F).ToArray(); return new Quaternion(f[0], f[1], f[2], f[3]); }

    public static void Run()
    {
        try { RunInner(); }
        catch (Exception e) { Fail(e.ToString()); }
    }

    static void RunInner()
    {
        string export = Arg("-export"), pose = Arg("-pose"), store = Arg("-store"), avatar = Arg("-avatar"), outDir = Arg("-out", "DriveTest");
        int nFrames = int.Parse(Arg("-frames", "4"), Inv);
        Directory.CreateDirectory(outDir);
        SfMorph.Store.Dir = store;

        // her skeleton in its bind pose
        var root = new GameObject("reika").transform;
        var rows = File.ReadAllLines(Path.Combine(export, "skeleton.txt")).Where(l => l.Length > 0 && !l.StartsWith("#")).Select(l => l.Split('\t')).ToList();
        var bones = new Dictionary<string, Transform>();
        foreach (var r in rows) bones[r[0]] = new GameObject(r[0]).transform;
        foreach (var r in rows)
        {
            if (!bones.TryGetValue(r[1], out var p)) { p = new GameObject(r[1]).transform; p.SetParent(root, false); bones[r[1]] = p; }
            bones[r[0]].SetParent(p, false);
        }
        int Depth(Transform t) { int d = 0; for (var x = t; x != null; x = x.parent) d++; return d; }
        var byDepth = rows.OrderBy(r => Depth(bones[r[0]])).ToList();
        foreach (var r in byDepth) bones[r[0]].SetPositionAndRotation(V(r[2]), Q(r[3]));

        // her body meshes from the export, skinned to that skeleton
        var skinMat = new Material(Shader.Find("SfTest/Skin")) { name = "reika_body" };
        var her = new List<SkinnedMeshRenderer>();
        foreach (var name in HerMeshes)
        {
            Debug.Log("SfAvatarTest: reading " + name);
            string obj = Path.Combine(export, name + ".obj"), skin = Path.Combine(export, name + ".skin.txt");
            if (!File.Exists(obj) || !File.Exists(skin)) continue;
            var v = new List<Vector3>(); var tri = new List<int>();
            foreach (var l in File.ReadLines(obj))
            {
                if (l.StartsWith("v ")) v.Add(V(l.Substring(2).Trim()));
                else if (l.StartsWith("f "))
                {
                    var f = l.Substring(2).Trim().Split(' ').Select(x => int.Parse(x.Split('/')[0], Inv) - 1).ToArray();
                    for (int k = 1; k + 1 < f.Length; k++) { tri.Add(f[0]); tri.Add(f[k]); tri.Add(f[k + 1]); }
                }
            }
            var sl = File.ReadAllLines(skin).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
            int nb = int.Parse(sl[0].Split(' ')[1], Inv);
            var bt = new Transform[nb]; var bp = new Matrix4x4[nb];
            for (int b = 0; b < nb; b++)
            {
                var t = sl[1 + b].Split(' ');
                bones.TryGetValue(t[1], out bt[b]);
                if (t.Length < 18)
                {
                    // the plugin's soft-zone helpers (SfSoft_<zone>_<bone>) are listed without a bind pose: the bone they hang on
                    var host = bones.Keys.Where(k => t[1].EndsWith("_" + k, StringComparison.Ordinal)).OrderByDescending(k => k.Length).FirstOrDefault();
                    bt[b] = host != null ? bones[host] : root;
                    bp[b] = bt[b].worldToLocalMatrix;
                    continue;
                }
                var m = new Matrix4x4();
                for (int k = 0; k < 16; k++) m[k / 4, k % 4] = F(t[2 + k]);
                bp[b] = m;
            }
            int nw = int.Parse(sl[1 + nb].Split(' ')[1], Inv);
            var bw = new BoneWeight[nw];
            for (int i = 0; i < nw; i++)
            {
                var t = sl[2 + nb + i].Split(' ').Select(F).ToArray();
                bw[i] = new BoneWeight { boneIndex0 = (int)t[0], weight0 = t[1], boneIndex1 = (int)t[2], weight1 = t[3], boneIndex2 = (int)t[4], weight2 = t[5], boneIndex3 = (int)t[6], weight3 = t[7] };
            }
            for (int b = 0; b < nb; b++) if (bt[b] == null) bt[b] = root;   // a bone the skeleton file lacks: rigid
            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(v); mesh.SetTriangles(tri, 0); mesh.boneWeights = bw; mesh.bindposes = bp;
            mesh.RecalculateNormals();
            var go = new GameObject(name); go.transform.SetParent(root, false);
            var sr = go.AddComponent<SkinnedMeshRenderer>();
            sr.sharedMesh = mesh; sr.bones = bt; sr.sharedMaterials = new[] { skinMat };
            her.Add(sr);
        }
        if (her.Count == 0) { Fail("none of her body meshes in " + export); return; }
        Debug.Log("SfAvatarTest: her meshes " + string.Join(", ", her.Select(r => r.name + " " + r.sharedMesh.vertexCount)));

        Debug.Log("SfAvatarTest: her meshes built");
        // the avatar, through the plugin's own code
        var cfg = new BepInEx.Configuration.ConfigFile();
        SfMorph.Avatar.Bind(cfg);
        SfMorph.Avatar.Pick.Value = avatar;
        SfMorph.Avatar.Rescan();
        var body = new SfMorph.Body();
        body.Found.Add(new SfMorph.Character { Root = root });
        SfMorph.Avatar.Tick(body);
        if (!SfMorph.Avatar.On) { Fail("the avatar did not load: " + SfMorph.Avatar.Status); return; }
        Debug.Log("SfAvatarTest: loaded: " + SfMorph.Avatar.Status);
        var go2 = GameObject.Find("SfAvatar " + avatar);
        var rends = go2.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var main = rends.OrderByDescending(r => r.sharedMesh.vertexCount).First();
        int mainOff = 0; foreach (var r in rends) { if (r == main) break; mainOff += r.sharedMesh.vertexCount; }   // main's vertices in the baked list
        if (rends.Any(r => r.bones.Any(b => b == null))) { Fail("an avatar mesh has a missing bone"); return; }
        float[] restEdges = null, herRest = null;

        Debug.Log("SfAvatarTest: reading the recording");
        // the recording
        var names = new List<string>();
        var frames = new List<KeyValuePair<string, float[]>>();
        string cur = null;
        foreach (var l in File.ReadLines(pose))
        {
            if (l.StartsWith("bone ")) { var p = l.Substring(5); names.Add(p.Substring(p.IndexOf(' ') + 1).Trim()); }
            else if (l.StartsWith("frame ")) cur = l.Substring(6);
            else if (l.StartsWith("M ") && cur != null) frames.Add(new KeyValuePair<string, float[]>(cur, l.Substring(2).Split(' ').Select(F).ToArray()));
        }
        var pick = new List<int> { -1 };
        for (int i = 0; i < nFrames; i++) pick.Add(Mathf.RoundToInt(i * (frames.Count - 1) / (float)Mathf.Max(1, nFrames - 1)));
        var bindPose = byDepth.Select(r => new KeyValuePair<Transform, Matrix4x4>(bones[r[0]], Matrix4x4.TRS(V(r[2]), Q(r[3]), Vector3.one))).ToList();
        var report = new StringBuilder();
        int fi = 0; float worstGap = 0f, worstSole = 0f;
        var herFoot = her.FirstOrDefault(r => r.name == "reika_foot");
        foreach (var f in pick)
        {
            if (f < 0) foreach (var kv in bindPose) kv.Key.SetPositionAndRotation(kv.Value.GetColumn(3), kv.Value.rotation);
            else
            {
                var m = frames[f].Value;
                var at = new Dictionary<string, Matrix4x4>();
                for (int b = 0; b < names.Count; b++)
                {
                    var x = new Matrix4x4();
                    for (int rr = 0; rr < 3; rr++) for (int c = 0; c < 4; c++) x[rr, c] = m[b * 12 + rr * 4 + c];
                    x[3, 3] = 1f;
                    at[names[b]] = x;
                }
                foreach (var r in byDepth)
                    if (at.TryGetValue(r[0], out var x) && x.GetColumn(0).sqrMagnitude > 1e-8f)
                        bones[r[0]].SetPositionAndRotation(x.GetColumn(3), Quaternion.LookRotation(x.GetColumn(2), x.GetColumn(1)));
            }
            var av = Bake(rends, root); var hb = Bake(her.ToArray(), root);
            // on her curves: each of her body vertices (upper / lower body) to the nearest avatar vertex
            var grid = new Dictionary<long, List<Vector3>>();
            long Key(Vector3 p) { return ((long)Mathf.FloorToInt(p.x / 0.03f) + 100000 << 40) ^ ((long)Mathf.FloorToInt(p.y / 0.03f) + 100000 << 20) ^ ((long)Mathf.FloorToInt(p.z / 0.03f) + 100000); }
            foreach (var p in av.Skip(mainOff).Take(main.sharedMesh.vertexCount)) { var k = Key(p); if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<Vector3>(); l.Add(p); }
            var gaps = new List<float>();
            int herBodyVerts = her.Where(r => r.name.Contains("Body")).Sum(r => r.sharedMesh.vertexCount);
            foreach (var p in hb.Take(herBodyVerts))
            {
                float bd = 0.09f * 0.09f;
                int cx = Mathf.FloorToInt(p.x / 0.03f), cy = Mathf.FloorToInt(p.y / 0.03f), cz = Mathf.FloorToInt(p.z / 0.03f);
                for (int x = cx - 3; x <= cx + 3; x++) for (int y = cy - 3; y <= cy + 3; y++) for (int z = cz - 3; z <= cz + 3; z++)
                    { long k = ((long)x + 100000 << 40) ^ ((long)y + 100000 << 20) ^ ((long)z + 100000); if (grid.TryGetValue(k, out var l)) foreach (var q in l) { float d = (q - p).sqrMagnitude; if (d < bd) bd = d; } }
                gaps.Add(Mathf.Sqrt(bd));
            }
            gaps.Sort();
            float med = gaps[gaps.Count / 2], p95 = gaps[(int)(gaps.Count * 0.95f)];
            // her genital patch against the avatar: how far its vertices are from the avatar's skin (the rim must meet it)
            string patchNote = "";
            var pr = her.FirstOrDefault(r => r.name.StartsWith("pussy"));
            if (pr != null)
            {
                int off = 0; foreach (var r in her) { if (r == pr) break; off += r.sharedMesh.vertexCount; }
                var pd = new List<float>();
                for (int i = 0; i < pr.sharedMesh.vertexCount; i++)
                {
                    var p = hb[off + i]; float bd = 0.05f * 0.05f;
                    int cx = Mathf.FloorToInt(p.x / 0.03f), cy = Mathf.FloorToInt(p.y / 0.03f), cz = Mathf.FloorToInt(p.z / 0.03f);
                    for (int x = cx - 2; x <= cx + 2; x++) for (int y = cy - 2; y <= cy + 2; y++) for (int z = cz - 2; z <= cz + 2; z++)
                        { long k = ((long)x + 100000 << 40) ^ ((long)y + 100000 << 20) ^ ((long)z + 100000); if (grid.TryGetValue(k, out var l)) foreach (var q in l) { float d = (q - p).sqrMagnitude; if (d < bd) bd = d; } }
                    pd.Add(Mathf.Sqrt(bd));
                }
                pd.Sort();
                patchNote = "; her genital patch to the avatar's skin median " + (pd[pd.Count / 2] * 1000f).ToString("0.0", Inv) + " mm, 90 % " + (pd[(int)(pd.Count * 0.9f)] * 1000f).ToString("0.0", Inv) + " mm";
            }
            worstGap = Mathf.Max(worstGap, p95);
            // soles: lowest avatar vertex vs her foot mesh's lowest
            float avLow = av.Skip(mainOff).Take(main.sharedMesh.vertexCount).Min(p => p.y);
            float herLow = herFoot != null ? Bake(new[] { herFoot }, root).Min(p => p.y) : float.NaN;
            worstSole = Mathf.Max(worstSole, Mathf.Abs(avLow - herLow));
            // stretch against the avatar's bind frame
            var e = Edges(main);
            if (restEdges == null) restEdges = e;
            var ratio = e.Select((len, i) => restEdges[i] > 1e-5f ? len / restEdges[i] : 1f).OrderBy(x => x).ToArray();
            float p01 = ratio[(int)(ratio.Length * 0.001f)], p99 = ratio[(int)(ratio.Length * 0.999f)];
            // her own upper body against her bind frame: what plain skinning does to her
            var herUp = her.First(r => r.name.Contains("upperBody"));
            var he = Edges(herUp);
            if (herRest == null) herRest = he;
            var hr = he.Select((len, i) => herRest[i] > 1e-5f ? len / herRest[i] : 1f).OrderBy(x => x).ToArray();
            float h01 = hr[(int)(hr.Length * 0.001f)], h99 = hr[(int)(hr.Length * 0.999f)];
            // the avatar's most stretched edges: their vertices' bones (which of her bones pull them apart)
            if (f >= 0 && fi == 1)
            {
                var tri = main.sharedMesh.triangles; var bwv = main.sharedMesh.boneWeights; var bn = main.bones;
                var worstE = Enumerable.Range(0, e.Length).Where(i => restEdges[i] > 1e-5f).OrderByDescending(i => Mathf.Max(e[i] / restEdges[i], restEdges[i] / Mathf.Max(e[i], 1e-6f))).Take(6);
                foreach (var ei in worstE)
                {
                    int a = tri[ei], b = tri[(ei % 3 == 2) ? ei - 2 : ei + 1];
                    string W(int vi) { var w = bwv[vi]; return bn[w.boneIndex0].name + " " + w.weight0.ToString("0.00", Inv) + ", " + bn[w.boneIndex1].name + " " + w.weight1.ToString("0.00", Inv) + ", " + bn[w.boneIndex2].name + " " + w.weight2.ToString("0.00", Inv); }
                    Debug.Log("SfAvatarTest: stretched x" + (e[ei] / restEdges[ei]).ToString("0.0", Inv) + " at " + main.sharedMesh.vertices[a].ToString("F3") + ": [" + W(a) + "] -- [" + W(b) + "]");
                }
            }
            string label = f < 0 ? "bind" : "recording frame " + frames[f].Key.Split(' ')[0];
            report.Append("frame ").Append(fi).Append(" (").Append(label).Append("): her body to the avatar's skin median ").Append((med * 1000f).ToString("0.0", Inv))
                  .Append(" mm, 95 % ").Append((p95 * 1000f).ToString("0.0", Inv)).Append(" mm; lowest point avatar ").Append((avLow * 1000f).ToString("0", Inv))
                  .Append(" mm / her foot ").Append((herLow * 1000f).ToString("0", Inv)).Append(" mm; skin edges x").Append(p01.ToString("0.00", Inv))
                  .Append(" (0.1 %) .. x").Append(p99.ToString("0.00", Inv)).Append(" (99.9 %) of the bind frame (her upper body x").Append(h01.ToString("0.00", Inv)).Append(" .. x").Append(h99.ToString("0.00", Inv)).Append(")").Append(patchNote).Append("\n");
            WriteObj(Path.Combine(outDir, "frame_" + fi.ToString("00") + ".obj"), rends, av);
            WriteObj(Path.Combine(outDir, "frame_" + fi.ToString("00") + "_her.obj"), her.ToArray(), hb);
            fi++;
        }
        File.WriteAllText(Path.Combine(outDir, "drive_test.txt"), "# SfAvatarTest: src/SfMorph/Avatar.cs on " + Path.GetFileName(pose) + ", avatar " + avatar + "\n" + report);
        foreach (var l in report.ToString().Split('\n').Where(x => x.Length > 0)) Debug.Log("SfAvatarTest: " + l);
        body.Found.Clear();
        SfMorph.Avatar.Pick.Value = "";
        SfMorph.Avatar.Tick(body);
        bool shown = her.All(r => !r.forceRenderingOff);
        Debug.Log("SfAvatarTest: unloaded: " + (SfMorph.Avatar.On ? "STILL ON" : "off") + ", her renderers shown again: " + shown);
        if (!shown || SfMorph.Avatar.On) { Fail("not given back"); return; }
        Debug.Log("SfAvatarTest: worst 95 % gap " + (worstGap * 1000f).ToString("0.0", Inv) + " mm, worst sole difference " + (worstSole * 1000f).ToString("0.0", Inv) + " mm");
        Debug.Log("SfAvatarTest OK");
    }

    // every renderer baked into her root's space, vertices in renderer order
    static List<Vector3> Bake(SkinnedMeshRenderer[] rends, Transform root)
    {
        var all = new List<Vector3>();
        foreach (var r in rends)
        {
            var m = new Mesh();
            r.BakeMesh(m, true);
            var tr = Matrix4x4.TRS(r.transform.position, r.transform.rotation, Vector3.one);
            foreach (var p in m.vertices) all.Add(root.InverseTransformPoint(tr.MultiplyPoint3x4(p)));
            UnityEngine.Object.DestroyImmediate(m);
        }
        return all;
    }

    static float[] Edges(SkinnedMeshRenderer r)
    {
        var m = new Mesh();
        r.BakeMesh(m, true);
        var v = m.vertices; var t = r.sharedMesh.triangles;
        var e = new float[t.Length];
        for (int i = 0; i < t.Length; i += 3)
        {
            e[i] = (v[t[i]] - v[t[i + 1]]).magnitude; e[i + 1] = (v[t[i + 1]] - v[t[i + 2]]).magnitude; e[i + 2] = (v[t[i + 2]] - v[t[i]]).magnitude;
        }
        UnityEngine.Object.DestroyImmediate(m);
        return e;
    }

    static void WriteObj(string path, SkinnedMeshRenderer[] rends, List<Vector3> baked)
    {
        var sb = new StringBuilder();
        int off = 0;
        foreach (var r in rends)
        {
            int n = r.sharedMesh.vertexCount;
            sb.Append("o ").Append(r.name).Append('\n');
            for (int i = 0; i < n; i++) { var w = baked[off + i]; sb.Append("v ").Append(w.x.ToString(Inv)).Append(' ').Append(w.y.ToString(Inv)).Append(' ').Append(w.z.ToString(Inv)).Append('\n'); }
            for (int s = 0; s < r.sharedMesh.subMeshCount; s++)
            {
                var t = r.sharedMesh.GetTriangles(s);
                for (int i = 0; i < t.Length; i += 3) sb.Append("f ").Append(t[i] + off + 1).Append(' ').Append(t[i + 1] + off + 1).Append(' ').Append(t[i + 2] + off + 1).Append('\n');
            }
            off += n;
        }
        File.WriteAllText(path, sb.ToString());
    }
}
