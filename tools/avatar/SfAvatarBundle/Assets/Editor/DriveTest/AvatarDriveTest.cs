using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

// Runs the plugin's own src/SfMorph/Avatar.cs (copied in by tools/avatar/drive_test.ps1, compiled against Stubs.cs) on her export
// and a PoseRecorder recording, in the editor:
//   Unity.exe -batchmode -quit -nographics -projectPath <this project> -executeMethod AvatarDriveTest.Run
//             -export <her SfMorph export dir> -pose <recording.txt> -store <folder holding avatars\> -avatar <Name> -out <dir>
//             [-frames 4] [-handik 0] [-footik 0]
// Her skeleton is built from skeleton.txt (bind pose = the export's mesh space = her root's space) with one skinned renderer whose
// bind poses are that pose; each recorded frame sets her bones, then Avatar's frame step drives the avatar. Measured per frame:
// the angle between each avatar limb / finger segment and hers (the offline Python check gives 0.00 degrees), and the skin's edge
// stretch against the avatar's rest (a wrong rotation shows as torn or crushed triangles). <out>/frame_NN.obj: the avatar's skin
// baked in her root's space (Unity axes), <out>/frame_NN_her.txt: her joints, for tools/avatar/retarget_check.py --unity.
public static class AvatarDriveTest
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static string Arg(string name, string def = null)
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
        return def;
    }

    static void Fail(string s) { Debug.LogError("SfAvatarTest FAILED: " + s); EditorApplication.Exit(1); }

    static Vector3 V(string s) { var f = s.Split(' ').Select(x => float.Parse(x, Inv)).ToArray(); return new Vector3(f[0], f[1], f[2]); }
    static Quaternion Q(string s) { var f = s.Split(' ').Select(x => float.Parse(x, Inv)).ToArray(); return new Quaternion(f[0], f[1], f[2], f[3]); }

    // her limb / finger segments (her bone, her child) and the avatar's (G9 names; the test is for G9 avatars)
    static readonly string[][] Segs =
    {
        new[] { "Shoulder{S}", "Elbow{S}", "{s}upperarm", "{s}forearm" }, new[] { "Elbow{S}", "Wrist{S}", "{s}forearm", "{s}hand" },
        new[] { "Hip{S}", "Knee{S}", "{s}thigh", "{s}shin" }, new[] { "Knee{S}", "Ankle{S}", "{s}shin", "{s}foot" },
        new[] { "Ankle{S}", "Toes{S}", "{s}foot", "{s}toes" }, new[] { "Wrist{S}", "MiddleFinger1{S}", "{s}hand", "{s}mid1" },
        new[] { "IndexFinger1{S}", "IndexFinger2{S}", "{s}index1", "{s}index2" }, new[] { "IndexFinger2{S}", "IndexFinger3{S}", "{s}index2", "{s}index3" },
        new[] { "MiddleFinger1{S}", "MiddleFinger2{S}", "{s}mid1", "{s}mid2" }, new[] { "RingFinger1{S}", "RingFinger2{S}", "{s}ring1", "{s}ring2" },
        new[] { "PinkyFinger1{S}", "PinkyFinger2{S}", "{s}pinky1", "{s}pinky2" }, new[] { "ThumbFinger1{S}", "ThumbFinger2{S}", "{s}thumb1", "{s}thumb2" },
    };

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
        var rend = new GameObject("reika_upperBody_normal").AddComponent<SkinnedMeshRenderer>();
        rend.transform.SetParent(root, false);
        var bl = rows.Select(r => bones[r[0]]).ToArray();
        var mesh = new Mesh { vertices = new Vector3[3], triangles = new[] { 0, 1, 2 } };
        mesh.bindposes = bl.Select(b => b.worldToLocalMatrix * rend.transform.localToWorldMatrix).ToArray();
        rend.sharedMesh = mesh; rend.bones = bl;

        // the avatar, through the plugin's own code
        var cfg = new BepInEx.Configuration.ConfigFile();
        SfMorph.Avatar.Bind(cfg);
        SfMorph.Avatar.Pick.Value = avatar;
        SfMorph.Avatar.HandIk.Value = float.Parse(Arg("-handik", "0"), Inv);
        SfMorph.Avatar.FootIk.Value = float.Parse(Arg("-footik", "0"), Inv);
        SfMorph.Avatar.Rescan();
        var body = new SfMorph.Body();
        body.Found.Add(new SfMorph.Character { Root = root });
        SfMorph.Avatar.Tick(body);
        if (!SfMorph.Avatar.On) { Fail("the avatar did not load: " + SfMorph.Avatar.Status); return; }
        Debug.Log("SfAvatarTest: loaded: " + SfMorph.Avatar.Status);
        var step = typeof(SfMorph.Avatar).GetMethod("Step", BindingFlags.NonPublic | BindingFlags.Static);
        var go = GameObject.Find("SfAvatar " + avatar);
        var av = new Dictionary<string, Transform>();
        foreach (var t in go.GetComponentsInChildren<Transform>(true)) if (!av.ContainsKey(t.name)) av[t.name] = t;
        var rends = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var main = rends.OrderByDescending(r => r.sharedMesh.vertexCount).First();
        var restEdges = Edges(main);

        // the recording
        var lines = File.ReadAllLines(pose);
        var names = new List<string>();
        var frames = new List<KeyValuePair<string, float[]>>();
        string cur = null;
        foreach (var l in lines)
        {
            if (l.StartsWith("bone ")) { var p = l.Substring(5); names.Add(p.Substring(p.IndexOf(' ') + 1).Trim()); }
            else if (l.StartsWith("frame ")) cur = l.Substring(6);
            else if (l.StartsWith("M ") && cur != null) frames.Add(new KeyValuePair<string, float[]>(cur, l.Substring(2).Split(' ').Select(x => float.Parse(x, Inv)).ToArray()));
        }
        var pick = new List<int> { -1 };
        for (int i = 0; i < nFrames; i++) pick.Add(Mathf.RoundToInt(i * (frames.Count - 1) / (float)Mathf.Max(1, nFrames - 1)));
        var bindPose = byDepth.Select(r => new KeyValuePair<Transform, Matrix4x4>(bones[r[0]], Matrix4x4.TRS(V(r[2]), Q(r[3]), Vector3.one))).ToList();
        float worst = 0f; int fi = 0;
        var report = new StringBuilder();
        foreach (var f in pick)
        {
            // her pose: the bind pose, or the frame (bone -> her root's space, rows 0-2 of a 4x4)
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
            step.Invoke(null, null);
            // directions
            float mx = 0f; var seg = new List<string>();
            foreach (var s in Segs)
                foreach (var side in new[] { "L", "R" })
                {
                    string S(string n) => n.Replace("{S}", "_" + side).Replace("{s}", side == "L" ? "l_" : "r_");
                    if (!bones.TryGetValue(S(s[0]), out var h0) || !bones.TryGetValue(S(s[1]), out var h1) || !av.TryGetValue(S(s[2]), out var a0) || !av.TryGetValue(S(s[3]), out var a1)) continue;
                    float ang = Vector3.Angle(a1.position - a0.position, h1.position - h0.position);
                    mx = Mathf.Max(mx, ang);
                    seg.Add(S(s[2]) + " " + ang.ToString("0.00", Inv));
                }
            worst = Mathf.Max(worst, mx);
            // skin stretch against the rest
            var e = Edges(main);
            var ratio = e.Select((len, i) => restEdges[i] > 1e-5f ? len / restEdges[i] : 1f).OrderBy(x => x).ToArray();
            float p01 = ratio[(int)(ratio.Length * 0.001f)], p99 = ratio[(int)(ratio.Length * 0.999f)], lo = ratio[0], hi = ratio[ratio.Length - 1];
            string label = f < 0 ? "bind" : "recording frame " + frames[f].Key.Split(' ')[0];
            report.Append("frame ").Append(fi).Append(" (").Append(label).Append("): segments max ").Append(mx.ToString("0.00", Inv)).Append(" deg; skin edges x")
                  .Append(lo.ToString("0.00", Inv)).Append(" / x").Append(p01.ToString("0.00", Inv)).Append(" (0.1 %) .. x").Append(p99.ToString("0.00", Inv)).Append(" (99.9 %) / x")
                  .Append(hi.ToString("0.00", Inv)).Append("; ").Append(string.Join(", ", seg)).Append('\n');
            // the avatar's skin and her joints for the Blender view
            WriteObj(Path.Combine(outDir, "frame_" + fi.ToString("00") + ".obj"), rends, root);
            var hj = new StringBuilder();
            foreach (var r in rows) { var p = root.InverseTransformPoint(bones[r[0]].position); hj.Append(r[0]).Append(' ').Append(p.x.ToString(Inv)).Append(' ').Append(p.y.ToString(Inv)).Append(' ').Append(p.z.ToString(Inv)).Append('\n'); }
            File.WriteAllText(Path.Combine(outDir, "frame_" + fi.ToString("00") + "_her.txt"), "# " + label + "\n" + hj);
            fi++;
        }
        File.WriteAllText(Path.Combine(outDir, "drive_test.txt"), "# SfAvatarTest: src/SfMorph/Avatar.cs on " + Path.GetFileName(pose) + ", avatar " + avatar + "\n" + report);
        foreach (var l in report.ToString().Split('\n').Where(x => x.Length > 0)) Debug.Log("SfAvatarTest: " + (l.Length > 300 ? l.Substring(0, 300) + " ..." : l));
        Debug.Log("SfAvatarTest: worst segment angle " + worst.ToString("0.00", Inv) + " deg");
        body.Found.Clear();
        SfMorph.Avatar.Pick.Value = "";
        SfMorph.Avatar.Tick(body);
        Debug.Log("SfAvatarTest: unloaded: " + (SfMorph.Avatar.On ? "STILL ON" : "off") + ", her renderer shown again: " + !rend.forceRenderingOff);
        if (worst > 1f) { Fail("segments off by up to " + worst.ToString("0.00", Inv) + " deg"); return; }
        Debug.Log("SfAvatarTest OK");
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

    static void WriteObj(string path, SkinnedMeshRenderer[] rends, Transform root)
    {
        var sb = new StringBuilder();
        int off = 1;
        foreach (var r in rends)
        {
            var m = new Mesh();
            r.BakeMesh(m, true);
            var tr = Matrix4x4.TRS(r.transform.position, r.transform.rotation, Vector3.one);
            var v = m.vertices;
            sb.Append("o ").Append(r.name).Append('\n');
            foreach (var p in v) { var w = root.InverseTransformPoint(tr.MultiplyPoint3x4(p)); sb.Append("v ").Append(w.x.ToString(Inv)).Append(' ').Append(w.y.ToString(Inv)).Append(' ').Append(w.z.ToString(Inv)).Append('\n'); }
            for (int s = 0; s < r.sharedMesh.subMeshCount; s++)
            {
                var t = m.GetTriangles(s);
                for (int i = 0; i < t.Length; i += 3) sb.Append("f ").Append(t[i] + off).Append(' ').Append(t[i + 1] + off).Append(' ').Append(t[i + 2] + off).Append('\n');
            }
            off += v.Length;
            UnityEngine.Object.DestroyImmediate(m);
        }
        File.WriteAllText(path, sb.ToString());
    }
}
